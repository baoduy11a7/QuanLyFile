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
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

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
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<RemoteFetchJob> _logger;

        public RemoteFetchJob(
            IInvoiceSourceProviderFactory providerFactory,
            IInvoiceImportService importService,
            IRemoteJobTracker jobTracker,
            ICredentialProtector credentialProtector,
            IAuditLogService auditLogService,
            ApplicationDbContext db,
            IServiceScopeFactory scopeFactory,
            ILogger<RemoteFetchJob> logger)
        {
            _providerFactory = providerFactory;
            _importService = importService;
            _jobTracker = jobTracker;
            _credentialProtector = credentialProtector;
            _auditLogService = auditLogService;
            _db = db;
            _scopeFactory = scopeFactory;
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

                // 4. Lọc hóa đơn trùng siêu tốc trực tiếp trong bộ nhớ (0 ms)
                int successCount = 0;
                int duplicateCount = 0;
                int failedCount = 0;
                int processed = 0;

                var existingInvoices = await _db.Invoices
                    .AsNoTracking()
                    .Where(i => i.TaxAccountId == taxAccountId)
                    .Select(i => new { i.InvoiceType, i.SellerTaxCode, i.InvoiceSymbol, i.InvoiceNumber })
                    .ToListAsync(ct);

                var existingSet = new HashSet<string>(
                    existingInvoices.Select(i => $"{i.InvoiceType}_{i.SellerTaxCode}_{i.InvoiceSymbol}_{i.InvoiceNumber}"),
                    StringComparer.OrdinalIgnoreCase);

                var toProcessInvoices = new List<RemoteInvoice>();
                foreach (var inv in remoteInvoices)
                {
                    var cleanSymbol = (inv.InvoiceSymbol ?? string.Empty).Trim();
                    var cleanNumber = (inv.InvoiceNumber ?? string.Empty).Trim().PadLeft(7, '0');
                    var sellerMst = (inv.SellerTaxCode ?? string.Empty).Trim();
                    var invoiceType = string.Equals(inv.InvoiceType, "BanRa", StringComparison.OrdinalIgnoreCase) ? "BanRa" : "MuaVao";
                    var key = $"{invoiceType}_{sellerMst}_{cleanSymbol}_{cleanNumber}";

                    if (existingSet.Contains(key))
                    {
                        duplicateCount++;
                    }
                    else
                    {
                        toProcessInvoices.Add(inv);
                        existingSet.Add(key);
                    }
                }

                if (duplicateCount > 0)
                {
                    _jobTracker.AddLog(jobId, $"ℹ️ Nhận diện {duplicateCount} hóa đơn đã có trên hệ thống (Bỏ qua chống trùng siêu tốc 0 giây).");
                    int initialPercent = total > 0 ? 25 + (int)((double)duplicateCount / total * 70) : 25;
                    _jobTracker.UpdateProgress(jobId, Math.Min(initialPercent, 90),
                        $"Đã nhận diện {duplicateCount} HĐ trùng. Đang xử lý {toProcessInvoices.Count} HĐ mới...",
                        totalFound: total, success: successCount, duplicate: duplicateCount, failed: failedCount);
                }

                // 5. Tải song song tối ưu với 4 luồng xử lý đồng thời
                if (toProcessInvoices.Any())
                {
                    using var semaphore = new SemaphoreSlim(4, 4);
                    var lockObj = new object();
                    var tasks = toProcessInvoices.Select(async inv =>
                    {
                        await semaphore.WaitAsync(ct);
                        try
                        {
                            ct.ThrowIfCancellationRequested();

                            byte[]? xmlBytes = null;
                            try
                            {
                                xmlBytes = await provider.DownloadXmlAsync(inv, loginResult.Token, ct);
                            }
                            catch (Exception exDl)
                            {
                                _logger.LogWarning("Lỗi khi tải XML cho HĐ {Symbol}-{Number}: {Msg}", inv.InvoiceSymbol, inv.InvoiceNumber, exDl.Message);
                            }

                            using var scope = _scopeFactory.CreateScope();
                            var scopedImport = scope.ServiceProvider.GetRequiredService<IInvoiceImportService>();
                            var scopedDb = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                            bool isImported = false;
                            if (xmlBytes != null && xmlBytes.Length > 0)
                            {
                                try
                                {
                                    using var xmlStream = new MemoryStream(xmlBytes);
                                    var fileName = $"Remote_{inv.InvoiceSymbol}_{inv.InvoiceNumber}.xml";
                                    var importResult = await scopedImport.ImportXmlAsync(xmlStream, fileName, taxAccountId, inv.InvoiceType, userId);

                                    if (importResult.SuccessCount > 0)
                                    {
                                        isImported = true;
                                        lock (lockObj)
                                        {
                                            successCount += importResult.SuccessCount;
                                            processed++;
                                            _jobTracker.AddLog(jobId, $" Đã nhập thành công HĐ {inv.InvoiceSymbol}-{inv.InvoiceNumber} ({inv.SellerName}) kèm chi tiết XML.");
                                        }
                                    }
                                }
                                catch (Exception exImp)
                                {
                                    _logger.LogWarning("Lỗi ImportXmlAsync cho HĐ {Symbol}-{Number}: {Msg}", inv.InvoiceSymbol, inv.InvoiceNumber, exImp.Message);
                                }
                            }

                            if (!isImported)
                            {
                                var directRes = await ImportDirectRemoteInvoiceWithDbAsync(scopedDb, inv, taxAccountId, userId);
                                lock (lockObj)
                                {
                                    processed++;
                                    if (directRes.IsSuccess)
                                    {
                                        successCount++;
                                        _jobTracker.AddLog(jobId, $" Đã nhập thành công HĐ {inv.InvoiceSymbol}-{inv.InvoiceNumber} ({inv.SellerName}) [Dữ liệu Cổng Thuế].");
                                    }
                                    else if (directRes.IsDuplicate)
                                    {
                                        duplicateCount++;
                                        _jobTracker.AddLog(jobId, $"ℹ️ HĐ {inv.InvoiceSymbol}-{inv.InvoiceNumber} đã có trên hệ thống.");
                                    }
                                    else
                                    {
                                        failedCount++;
                                        _jobTracker.AddLog(jobId, $"❌ Lỗi nhập HĐ {inv.InvoiceSymbol}-{inv.InvoiceNumber}: {directRes.ErrorMessage}");
                                    }
                                }
                            }

                            lock (lockObj)
                            {
                                int currentDone = processed + duplicateCount;
                                int percent = 25 + (int)((double)currentDone / total * 70);
                                if (percent > 95) percent = 95;
                                _jobTracker.UpdateProgress(jobId, percent, $"Đang xử lý {currentDone}/{total}: HĐ {inv.InvoiceSymbol}-{inv.InvoiceNumber}",
                                    totalFound: total, success: successCount, duplicate: duplicateCount, failed: failedCount);
                            }
                        }
                        catch (Exception exTask)
                        {
                            lock (lockObj)
                            {
                                failedCount++;
                                processed++;
                                _jobTracker.AddLog(jobId, $"❌ Lỗi xử lý HĐ {inv.InvoiceSymbol}-{inv.InvoiceNumber}: {exTask.Message}");
                            }
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    });

                    await Task.WhenAll(tasks);
                }

                // 6. Kết thúc thành công
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

        private async Task<(bool IsSuccess, bool IsDuplicate, string? ErrorMessage)> ImportDirectRemoteInvoiceWithDbAsync(
            ApplicationDbContext db,
            RemoteInvoice inv,
            int taxAccountId,
            string? userId)
        {
            try
            {
                var cleanSymbol = (inv.InvoiceSymbol ?? string.Empty).Trim();
                var cleanNumber = (inv.InvoiceNumber ?? string.Empty).Trim().PadLeft(7, '0');
                var sellerMst = (inv.SellerTaxCode ?? string.Empty).Trim();
                var invoiceType = string.Equals(inv.InvoiceType, "BanRa", StringComparison.OrdinalIgnoreCase) ? "BanRa" : "MuaVao";

                // 1. Kiểm tra tính idempotent (chống trùng lặp theo nghiệp vụ)
                var existing = await db.Invoices.FirstOrDefaultAsync(i =>
                    i.TaxAccountId == taxAccountId &&
                    i.InvoiceType == invoiceType &&
                    i.SellerTaxCode == sellerMst &&
                    i.InvoiceSymbol == cleanSymbol &&
                    i.InvoiceNumber == cleanNumber);

                if (existing != null)
                {
                    return (false, true, null);
                }

                // 2. Tạo entity Invoice trực tiếp từ metadata Cổng Thuế
                bool hasTaxCode = !string.IsNullOrWhiteSpace(inv.CqtCode);
                bool isCashRegister = cleanSymbol.Contains("M", StringComparison.OrdinalIgnoreCase);

                var status = inv.Status;
                if (status == "5") status = "Đã cấp mã";
                else if (string.IsNullOrWhiteSpace(status)) status = "Đã cấp mã";

                var invoice = new Invoice
                {
                    TaxAccountId = taxAccountId,
                    InvoiceType = invoiceType,
                    InvoiceSymbol = cleanSymbol,
                    InvoiceNumber = cleanNumber,
                    IssueDate = inv.IssueDate > DateTime.MinValue ? inv.IssueDate : DateTime.Now,
                    SellerTaxCode = sellerMst,
                    SellerName = !string.IsNullOrWhiteSpace(inv.SellerName) ? inv.SellerName.Trim() : "Không có tên",
                    SellerAddress = inv.SellerAddress ?? string.Empty,
                    BuyerTaxCode = (inv.BuyerTaxCode ?? string.Empty).Trim(),
                    BuyerName = !string.IsNullOrWhiteSpace(inv.BuyerName) ? inv.BuyerName.Trim() : string.Empty,
                    BuyerAddress = inv.BuyerAddress ?? string.Empty,
                    AmountBeforeTax = inv.AmountBeforeTax,
                    TaxAmount = inv.TaxAmount,
                    TotalAmount = inv.TotalAmount > 0 ? inv.TotalAmount : (inv.AmountBeforeTax + inv.TaxAmount),
                    TaxAuthorityCode = inv.CqtCode,
                    HasTaxCode = hasTaxCode,
                    IsCashRegister = isCashRegister,
                    Status = status,
                    SourceProvider = "GDT-Direct",
                    RiskLevel = "Normal",
                    ImportedAt = DateTime.Now,
                    ImportedByUserId = userId,
                    OriginalFileName = $"GDT_{cleanSymbol}_{cleanNumber}.xml"
                };

                // 3. Tạo dòng chi tiết hàng hóa đại diện
                var rate = inv.AmountBeforeTax > 0
                    ? Math.Round((inv.TaxAmount / inv.AmountBeforeTax) * 100, 0)
                    : 10;
                if (rate < 0) rate = 0;

                invoice.Details.Add(new InvoiceDetail
                {
                    LineNumber = 1,
                    ItemCode = "",
                    ItemName = $"Hàng hóa, dịch vụ theo HĐ {cleanSymbol}-{cleanNumber}",
                    Unit = "Lô/Gói",
                    Quantity = 1,
                    UnitPrice = inv.AmountBeforeTax,
                    AmountBeforeTax = inv.AmountBeforeTax,
                    TaxRate = rate,
                    TaxAmount = inv.TaxAmount,
                    TotalAmount = invoice.TotalAmount
                });

                // 4. Tạo file XML chuẩn hóa dự phòng lưu vào App_Data/RawFiles/{taxAccountId}
                try
                {
                    var xmlContent = GenerateStandardXmlForInvoice(invoice);
                    var storageRoot = Path.Combine(Directory.GetCurrentDirectory(), "App_Data", "RawFiles", taxAccountId.ToString());
                    Directory.CreateDirectory(storageRoot);
                    var filePath = Path.Combine(storageRoot, $"GDT_{cleanSymbol}_{cleanNumber}_{DateTime.Now:yyyyMMddHHmmssfff}.xml");
                    await File.WriteAllTextAsync(filePath, xmlContent, Encoding.UTF8);
                    invoice.RawXmlPath = filePath;
                }
                catch (Exception exXml)
                {
                    _logger.LogWarning(exXml, "Không thể lưu file XML dự phòng cho HĐ {Symbol}-{Number}", cleanSymbol, cleanNumber);
                }

                db.Invoices.Add(invoice);
                await db.SaveChangesAsync();

                return (true, false, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lưu trực tiếp hóa đơn {Symbol}-{Number} từ Cổng Thuế", inv.InvoiceSymbol, inv.InvoiceNumber);
                return (false, false, ex.Message);
            }
        }

        private string GenerateStandardXmlForInvoice(Invoice invoice)
        {
            var sb = new StringBuilder();
            var khms = !string.IsNullOrEmpty(invoice.InvoiceSymbol) && char.IsDigit(invoice.InvoiceSymbol[0])
                ? invoice.InvoiceSymbol.Substring(0, 1)
                : "1";
            var kh = !string.IsNullOrEmpty(invoice.InvoiceSymbol) && char.IsDigit(invoice.InvoiceSymbol[0]) && invoice.InvoiceSymbol.Length > 1
                ? invoice.InvoiceSymbol.Substring(1)
                : invoice.InvoiceSymbol;

            sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.AppendLine("<HDon xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">");
            sb.AppendLine("  <DLHDon>");
            sb.AppendLine("    <TTChung>");
            sb.AppendLine("      <PBan>2.0.0</PBan>");
            sb.AppendLine("      <THDon>Hóa đơn giá trị gia tăng</THDon>");
            sb.AppendLine($"      <KHMSHDon>{khms}</KHMSHDon>");
            sb.AppendLine($"      <KHHDon>{kh}</KHHDon>");
            sb.AppendLine($"      <SHDon>{invoice.InvoiceNumber}</SHDon>");
            sb.AppendLine($"      <NLap>{invoice.IssueDate:yyyy-MM-dd}</NLap>");
            sb.AppendLine("      <DVTTe>VND</DVTTe>");
            sb.AppendLine("      <TGia>1</TGia>");
            sb.AppendLine("      <HTTToan>TM/CK</HTTToan>");
            sb.AppendLine("    </TTChung>");
            sb.AppendLine("    <NDHDon>");
            sb.AppendLine("      <NBan>");
            sb.AppendLine($"        <Ten>{System.Security.SecurityElement.Escape(invoice.SellerName ?? "")}</Ten>");
            sb.AppendLine($"        <MST>{invoice.SellerTaxCode ?? ""}</MST>");
            sb.AppendLine($"        <DChi>{System.Security.SecurityElement.Escape(invoice.SellerAddress ?? "")}</DChi>");
            sb.AppendLine("      </NBan>");
            sb.AppendLine("      <NMua>");
            sb.AppendLine($"        <Ten>{System.Security.SecurityElement.Escape(invoice.BuyerName ?? "")}</Ten>");
            sb.AppendLine($"        <MST>{invoice.BuyerTaxCode ?? ""}</MST>");
            sb.AppendLine($"        <DChi>{System.Security.SecurityElement.Escape(invoice.BuyerAddress ?? "")}</DChi>");
            sb.AppendLine("      </NMua>");
            sb.AppendLine("      <DSHHDVu>");
            foreach (var d in invoice.Details)
            {
                sb.AppendLine("        <HHDVu>");
                sb.AppendLine($"          <STT>{d.LineNumber}</STT>");
                sb.AppendLine($"          <MHHDVu>{d.ItemCode ?? ""}</MHHDVu>");
                sb.AppendLine($"          <THHDVu>{System.Security.SecurityElement.Escape(d.ItemName ?? "")}</THHDVu>");
                sb.AppendLine($"          <DVTinh>{System.Security.SecurityElement.Escape(d.Unit ?? "")}</DVTinh>");
                sb.AppendLine($"          <SLuong>{d.Quantity}</SLuong>");
                sb.AppendLine($"          <DGia>{d.UnitPrice}</DGia>");
                sb.AppendLine($"          <ThTien>{d.AmountBeforeTax}</ThTien>");
                sb.AppendLine($"          <TSuat>{(d.TaxRate == -1 ? "KCT" : d.TaxRate + "%")}</TSuat>");
                sb.AppendLine($"          <TThue>{d.TaxAmount}</TThue>");
                sb.AppendLine("        </HHDVu>");
            }
            sb.AppendLine("      </DSHHDVu>");
            sb.AppendLine("      <TToan>");
            sb.AppendLine($"        <TgTCThue>{invoice.AmountBeforeTax}</TgTCThue>");
            sb.AppendLine($"        <TgTThue>{invoice.TaxAmount}</TgTThue>");
            sb.AppendLine($"        <TgTTTBSo>{invoice.TotalAmount}</TgTTTBSo>");
            sb.AppendLine("      </TToan>");
            sb.AppendLine("    </NDHDon>");
            sb.AppendLine("  </DLHDon>");
            if (!string.IsNullOrEmpty(invoice.TaxAuthorityCode))
            {
                sb.AppendLine($"  <MCCQT>{invoice.TaxAuthorityCode}</MCCQT>");
            }
            sb.AppendLine("  <DSCKS>");
            sb.AppendLine($"    <NNBS>{System.Security.SecurityElement.Escape(invoice.SellerName ?? "")}</NNBS>");
            sb.AppendLine($"    <NBS>{invoice.IssueDate:yyyy-MM-ddTHH:mm:ss}</NBS>");
            sb.AppendLine("  </DSCKS>");
            sb.AppendLine("</HDon>");
            return sb.ToString();
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
