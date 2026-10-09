using InvoiceManager.Data;
using InvoiceManager.Jobs.JobTracker;
using InvoiceManager.Models.Entities;
using InvoiceManager.Services;
using InvoiceManager.Services.Providers;
using InvoiceManager.Services.Providers.Models;
using InvoiceManager.Services.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace InvoiceManager.Jobs
{
    public class RemoteFetchJob
    {
        private readonly IInvoiceSourceProviderFactory _providerFactory;
        private readonly IInvoiceImportService _importService;
        private readonly IRemoteJobTracker _jobTracker;
        private readonly ICredentialProtector _credentialProtector;
        private readonly IAuditLogService _auditLogService;
        private readonly ApplicationDbContext _db;
        private readonly ILogger<RemoteFetchJob> _logger;

        public RemoteFetchJob(
            IInvoiceSourceProviderFactory providerFactory,
            IInvoiceImportService importService,
            IRemoteJobTracker jobTracker,
            ICredentialProtector credentialProtector,
            IAuditLogService auditLogService,
            ApplicationDbContext db,
            ILogger<RemoteFetchJob> logger)
        {
            _providerFactory = providerFactory;
            _importService = importService;
            _jobTracker = jobTracker;
            _credentialProtector = credentialProtector;
            _auditLogService = auditLogService;
            _db = db;
            _logger = logger;
        }

        public async Task ExecuteAsync(
            string jobId,
            int taxAccountId,
            string providerName,
            ProviderCredentials cred,
            FetchRequest req,
            bool rememberPassword,
            string? userId,
            CancellationToken ct = default)
        {
            try
            {
                _jobTracker.UpdateProgress(jobId, 10, $"Đang kết nối cổng {providerName}...");
                _jobTracker.AddLog(jobId, $"Bắt đầu xác thực tài khoản MST: {cred.TaxCode} trên cổng {providerName}...");

                var provider = _providerFactory.GetProvider(providerName);

                // 1. Đăng nhập cổng thuế
                var loginResult = await provider.LoginAsync(cred, ct);
                if (!loginResult.Success || string.IsNullOrEmpty(loginResult.Token))
                {
                    var errMsg = loginResult.ErrorMessage ?? "Đăng nhập Cổng thuế thất bại không rõ nguyên nhân.";
                    _jobTracker.FailJob(jobId, errMsg);
                    await RecordConnectionStatusAsync(taxAccountId, providerName, cred.Username, false, errMsg, rememberPassword ? cred.Password : null);
                    await _auditLogService.LogActionAsync("RemoteSync_LoginFailed", providerName, $"Đăng nhập thất bại: {errMsg}", taxAccountId);
                    return;
                }

                _jobTracker.AddLog(jobId, "Đăng nhập Cổng thuế thành công. Đang lưu thông tin phiên làm việc...");
                _jobTracker.UpdateProgress(jobId, 25, "Đang quét danh sách hóa đơn theo khoảng ngày...");

                // 2. Lưu / Cập nhật cấu hình RemoteConnection
                await RecordConnectionStatusAsync(taxAccountId, providerName, cred.Username, true, "Đang đồng bộ...", rememberPassword ? cred.Password : null);

                // 3. Quét danh sách hóa đơn
                _jobTracker.AddLog(jobId, $"Bắt đầu quét hóa đơn từ ngày {req.FromDate:dd/MM/yyyy} đến {req.ToDate:dd/MM/yyyy} (Loại: {req.InvoiceType})...");

                var remoteInvoices = new List<RemoteInvoice>();
                await foreach (var inv in provider.FetchInvoicesAsync(req, loginResult.Token, ct))
                {
                    remoteInvoices.Add(inv);
                }

                int total = remoteInvoices.Count;
                _jobTracker.AddLog(jobId, $"Tìm thấy tổng cộng {total} hóa đơn trên Cổng thuế.");

                if (total == 0)
                {
                    _jobTracker.UpdateProgress(jobId, 100, "Không có hóa đơn nào trong khoảng thời gian đã chọn.", totalFound: 0, success: 0, duplicate: 0, failed: 0);
                    _jobTracker.CompleteJob(jobId, "Không có hóa đơn mới nào được tìm thấy.");
                    await RecordConnectionStatusAsync(taxAccountId, providerName, cred.Username, true, "Hoàn tất: 0 hóa đơn mới.", rememberPassword ? cred.Password : null);
                    await _auditLogService.LogActionAsync("RemoteSync_Complete", providerName, "Không tìm thấy hóa đơn nào trong khoảng ngày đã chọn.", taxAccountId);
                    return;
                }

                // 4. Tải từng hóa đơn XML và đẩy vào Pipeline Import
                int successCount = 0;
                int duplicateCount = 0;
                int failedCount = 0;
                int processed = 0;

                foreach (var inv in remoteInvoices)
                {
                    ct.ThrowIfCancellationRequested();
                    processed++;

                    int percent = 25 + (int)((double)processed / total * 70);
                    _jobTracker.UpdateProgress(jobId, percent, $"Đang xử lý {processed}/{total}: HĐ {inv.InvoiceSymbol}-{inv.InvoiceNumber}",
                        totalFound: total, success: successCount, duplicate: duplicateCount, failed: failedCount);

                    try
                    {
                        var xmlBytes = await provider.DownloadXmlAsync(inv, loginResult.Token, ct);
                        if (xmlBytes == null || xmlBytes.Length == 0)
                        {
                            failedCount++;
                            _jobTracker.AddLog(jobId, $"⚠️ Không thể tải file XML của HĐ {inv.InvoiceSymbol}-{inv.InvoiceNumber}.");
                            continue;
                        }

                        using var xmlStream = new MemoryStream(xmlBytes);
                        var fileName = $"Remote_{inv.InvoiceSymbol}_{inv.InvoiceNumber}.xml";

                        var importResult = await _importService.ImportXmlAsync(xmlStream, fileName, taxAccountId, inv.InvoiceType, userId);

                        if (importResult.SuccessCount > 0)
                        {
                            successCount += importResult.SuccessCount;
                            _jobTracker.AddLog(jobId, $" Đã nhập thành công HĐ {inv.InvoiceSymbol}-{inv.InvoiceNumber} ({inv.SellerName}).");
                        }
                        else if (importResult.SkippedDuplicateCount > 0)
                        {
                            duplicateCount += importResult.SkippedDuplicateCount;
                            _jobTracker.AddLog(jobId, $"ℹ️ HĐ {inv.InvoiceSymbol}-{inv.InvoiceNumber} đã có trên hệ thống (Bỏ qua chống trùng).");
                        }
                        else
                        {
                            failedCount += Math.Max(1, importResult.FailedCount);
                            var msg = string.Join("; ", importResult.Messages);
                            _jobTracker.AddLog(jobId, $"❌ Lỗi nhập HĐ {inv.InvoiceSymbol}-{inv.InvoiceNumber}: {msg}");
                        }
                    }
                    catch (Exception ex)
                    {
                        failedCount++;
                        _logger.LogError(ex, "Lỗi khi tải hoặc import HĐ {Symbol}-{Number}", inv.InvoiceSymbol, inv.InvoiceNumber);
                        _jobTracker.AddLog(jobId, $"❌ Lỗi xử lý HĐ {inv.InvoiceSymbol}-{inv.InvoiceNumber}: {ex.Message}");
                    }
                }

                // 5. Kết thúc thành công
                var summaryMsg = $"Hoàn tất: {successCount} mới, {duplicateCount} trùng lặp, {failedCount} lỗi.";
                _jobTracker.UpdateProgress(jobId, 100, summaryMsg, totalFound: total, success: successCount, duplicate: duplicateCount, failed: failedCount);
                _jobTracker.CompleteJob(jobId, summaryMsg);

                await RecordConnectionStatusAsync(taxAccountId, providerName, cred.Username, true, summaryMsg, rememberPassword ? cred.Password : null);
                await _auditLogService.LogActionAsync("RemoteSync_Complete", providerName, summaryMsg, taxAccountId);
            }
            catch (OperationCanceledException)
            {
                _jobTracker.CancelJob(jobId);
                await _auditLogService.LogActionAsync("RemoteSync_Cancelled", providerName, "Người dùng đã chủ động hủy tiến trình.", taxAccountId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi nghiêm trọng trong Background Job RemoteFetchJob");
                _jobTracker.FailJob(jobId, $"Lỗi hệ thống: {ex.Message}");
                await _auditLogService.LogActionAsync("RemoteSync_Error", providerName, $"Lỗi: {ex.Message}", taxAccountId);
            }
        }

        private async Task RecordConnectionStatusAsync(
            int taxAccountId,
            string providerName,
            string username,
            bool isSuccess,
            string message,
            string? clearPasswordToSave = null)
        {
            try
            {
                var conn = await _db.RemoteConnections
                    .FirstOrDefaultAsync(r => r.TaxAccountId == taxAccountId && r.ProviderName == providerName);

                if (conn == null)
                {
                    conn = new RemoteConnection
                    {
                        TaxAccountId = taxAccountId,
                        ProviderName = providerName,
                        Username = username,
                        CreatedAt = DateTime.UtcNow
                    };
                    _db.RemoteConnections.Add(conn);
                }

                conn.Username = username;
                conn.LastSyncAt = DateTime.UtcNow;
                conn.LastSyncStatus = isSuccess ? "Success" : "Failed";
                conn.LastSyncMessage = message;
                conn.UpdatedAt = DateTime.UtcNow;

                if (clearPasswordToSave != null)
                {
                    conn.RememberPassword = true;
                    conn.EncryptedPassword = _credentialProtector.Protect(clearPasswordToSave);
                }
                else if (clearPasswordToSave == null && !conn.RememberPassword)
                {
                    conn.EncryptedPassword = null;
                }

                await _db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Không thể cập nhật trạng thái RemoteConnection vào database.");
            }
        }
    }
}
