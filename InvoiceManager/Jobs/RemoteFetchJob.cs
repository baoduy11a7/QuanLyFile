using InvoiceManager.Data;
using InvoiceManager.Jobs.JobTracker;
using InvoiceManager.Models.Entities;
using InvoiceManager.Services;
using InvoiceManager.Services.Parsers;
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

                // Ưu tiên nạp token hợp lệ đang lưu trong cache nếu cred chưa có BearerToken
                if (string.IsNullOrWhiteSpace(cred.BearerToken))
                {
                    var cached = InvoiceManager.Services.Providers.Gdt.GdtPortalProvider.GetCachedToken(cred.Username ?? cred.TaxCode, taxAccountId);
                    if (!string.IsNullOrWhiteSpace(cached) && !InvoiceManager.Services.Providers.Gdt.GdtPortalProvider.IsJwtExpired(cached))
                    {
                        cred.BearerToken = cached;
                    }
                }

                if (string.IsNullOrWhiteSpace(cred.BearerToken) && string.IsNullOrWhiteSpace(cred.CaptchaCode) && string.IsNullOrWhiteSpace(cred.Password))
                {
                    var errMsg = "Doanh nghiệp chưa có mật khẩu hoặc phiên kết nối Cổng Thuế. Vui lòng thiết lập kết nối để tiếp tục.";
                    _jobTracker.FailJob(jobId, errMsg);
                    await RecordConnectionStatusAsync(taxAccountId, providerName, cred.Username, false, errMsg, rememberPassword ? cred.Password : null, rememberPassword);
                    await _auditLogService.LogActionAsync("RemoteSync_LoginFailed", providerName, errMsg, taxAccountId);
                    return;
                }

                // 1. Đăng nhập cổng thuế
                var loginResult = await provider.LoginAsync(cred, ct);
                if (!loginResult.Success || string.IsNullOrEmpty(loginResult.Token))
                {
                    var errMsg = loginResult.ErrorMessage ?? "Đăng nhập Cổng thuế thất bại không rõ nguyên nhân.";
                    _jobTracker.FailJob(jobId, errMsg);
                    await RecordConnectionStatusAsync(taxAccountId, providerName, cred.Username, false, errMsg, rememberPassword ? cred.Password : null, rememberPassword);
                    await _auditLogService.LogActionAsync("RemoteSync_LoginFailed", providerName, $"Đăng nhập thất bại: {errMsg}", taxAccountId);
                    return;
                }

                _jobTracker.AddLog(jobId, "Đăng nhập Cổng thuế thành công. Đang lưu thông tin phiên làm việc...");
                _jobTracker.UpdateProgress(jobId, 25, "Đang quét danh sách hóa đơn theo khoảng ngày...");

                // Lưu JWT Token hoạt động vào kho lưu trữ để chức năng Xem/Tải PDF gốc dùng ngay không cần đồng bộ lại
                InvoiceManager.Services.Providers.Gdt.GdtPortalProvider.CacheToken(cred.Username, loginResult.Token, taxAccountId);

                // 2. Lưu / Cập nhật cấu hình RemoteConnection
                await RecordConnectionStatusAsync(taxAccountId, providerName, cred.Username, true, "Đang đồng bộ...", cred.Password, rememberPassword);

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
                    await RecordConnectionStatusAsync(taxAccountId, providerName, cred.Username, true, "Hoàn tất: 0 hóa đơn mới.", cred.Password, rememberPassword);
                    await _auditLogService.LogActionAsync("RemoteSync_Complete", providerName, "Không tìm thấy hóa đơn nào trong khoảng ngày đã chọn.", taxAccountId);
                    return;
                }

                // 4. Lọc hóa đơn trùng siêu tốc trực tiếp trong bộ nhớ (0 ms)
                // 4. Phân loại hóa đơn: Mới hoàn toàn, Đã có nhưng thiếu file gốc, hoặc Đã đủ file
                int successCount = 0;
                int duplicateCount = 0;
                int failedCount = 0;
                int newCount = 0;
                int updatedCount = 0;
                int filesDownloadedCount = 0;
                int filesFailedCount = 0;
                int missingFilesCount = 0;
                int processed = 0;

                var existingInvoicesRaw = await _db.Invoices
                    .AsNoTracking()
                    .Include(i => i.Files)
                    .Where(i => i.TaxAccountId == taxAccountId)
                    .Select(i => new
                    {
                        i.Id,
                        i.InvoiceType,
                        i.SellerTaxCode,
                        i.InvoiceSymbol,
                        i.InvoiceNumber,
                        i.RawPdfPath,
                        i.RawXmlPath,
                        ValidFiles = i.Files
                            .Where(f => f.Status == "Valid" && !f.Source.StartsWith("Mock"))
                            .Select(f => new { f.FileExtension, f.FilePath })
                            .ToList()
                    })
                    .ToListAsync(ct);

                var existingInvoices = existingInvoicesRaw.Select(i => new
                {
                    i.Id,
                    i.InvoiceType,
                    i.SellerTaxCode,
                    i.InvoiceSymbol,
                    i.InvoiceNumber,
                    HasPdf = (!string.IsNullOrWhiteSpace(i.RawPdfPath) && File.Exists(i.RawPdfPath))
                             || i.ValidFiles.Any(f => string.Equals(f.FileExtension, ".pdf", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(f.FilePath) && File.Exists(f.FilePath)),
                    HasXml = (!string.IsNullOrWhiteSpace(i.RawXmlPath) && File.Exists(i.RawXmlPath))
                             || i.ValidFiles.Any(f => string.Equals(f.FileExtension, ".xml", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(f.FilePath) && File.Exists(f.FilePath)),
                    FilesCount = i.ValidFiles.Count(f => !string.IsNullOrWhiteSpace(f.FilePath) && File.Exists(f.FilePath))
                }).ToList();

                var existingDict = existingInvoices
                    .GroupBy(i => $"{i.InvoiceType}_{i.SellerTaxCode}_{i.InvoiceSymbol}_{i.InvoiceNumber}", StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.HasPdf).ThenByDescending(x => x.FilesCount).First(), StringComparer.OrdinalIgnoreCase);

                var toProcessInvoices = new List<(RemoteInvoice Invoice, bool IsExisting, int ExistingId)>();
                var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var inv in remoteInvoices)
                {
                    var cleanSymbol = (inv.InvoiceSymbol ?? string.Empty).Trim();
                    var cleanNumber = (inv.InvoiceNumber ?? string.Empty).Trim().PadLeft(7, '0');
                    var sellerMst = (inv.SellerTaxCode ?? string.Empty).Trim();
                    var invoiceType = string.Equals(inv.InvoiceType, "BanRa", StringComparison.OrdinalIgnoreCase) ? "BanRa" : "MuaVao";
                    var key = $"{invoiceType}_{sellerMst}_{cleanSymbol}_{cleanNumber}";

                    if (!seenKeys.Add(key))
                    {
                        continue;
                    }

                    if (existingDict.TryGetValue(key, out var existingItem))
                    {
                        if (existingItem.FilesCount > 0 && existingItem.HasXml && existingItem.HasPdf)
                        {
                            duplicateCount++;
                        }
                        else
                        {
                            // Hóa đơn đã tồn tại nhưng chưa có file gốc hoặc thiếu file -> cần tải bổ sung file gốc
                            toProcessInvoices.Add((inv, true, existingItem.Id));
                        }
                    }
                    else
                    {
                        // Hóa đơn mới hoàn toàn
                        toProcessInvoices.Add((inv, false, 0));
                    }
                }

                if (duplicateCount > 0)
                {
                    _jobTracker.AddLog(jobId, $"ℹ️ Nhận diện {duplicateCount} hóa đơn đã có đầy đủ tệp gốc trên hệ thống (Bỏ qua chống trùng tải).");
                    int initialPercent = total > 0 ? 25 + (int)((double)duplicateCount / total * 70) : 25;
                    _jobTracker.UpdateProgress(jobId, Math.Min(initialPercent, 90),
                        $"Đã nhận diện {duplicateCount} HĐ đã đủ tệp. Đang đồng bộ {toProcessInvoices.Count} HĐ cần xử lý...",
                        totalFound: total, success: successCount, duplicate: duplicateCount, failed: failedCount);
                }

                // 5. Tải song song tối ưu với giới hạn đồng thời để tránh quá tải Cổng Thuế
                if (toProcessInvoices.Any())
                {
                    using var semaphore = new SemaphoreSlim(2, 2);
                    var lockObj = new object();
                    var tasks = toProcessInvoices.Select(async item =>
                    {
                        await semaphore.WaitAsync(ct);
                        var inv = item.Invoice;
                        var isExisting = item.IsExisting;
                        var existingId = item.ExistingId;

                        try
                        {
                            ct.ThrowIfCancellationRequested();

                            DownloadInvoiceResult? dlResult = null;
                            try
                            {
                                dlResult = await provider.DownloadAllInvoiceFilesAsync(inv, loginResult.Token, ct);
                            }
                            catch (Exception exDl)
                            {
                                _logger.LogWarning("Lỗi khi tải tệp cho HĐ {Symbol}-{Number}: {Msg}", inv.InvoiceSymbol, inv.InvoiceNumber, exDl.Message);
                                lock (lockObj)
                                {
                                    filesFailedCount++;
                                    _jobTracker.AddError(jobId, $"HĐ {inv.InvoiceSymbol}-{inv.InvoiceNumber}: {exDl.Message}");
                                }
                            }

                            int downloadedForThisInv = dlResult?.Files?.Count ?? 0;
                            lock (lockObj)
                            {
                                if (downloadedForThisInv > 0)
                                {
                                    filesDownloadedCount += downloadedForThisInv;
                                }
                                else
                                {
                                    missingFilesCount++;
                                }
                            }

                            using var scope = _scopeFactory.CreateScope();
                            var scopedImport = scope.ServiceProvider.GetRequiredService<IInvoiceImportService>();
                            var scopedDb = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                            var scopedFileService = scope.ServiceProvider.GetRequiredService<IInvoiceFileService>();

                            if (isExisting)
                            {
                                // Cập nhật hóa đơn đã có và lưu file gốc
                                if (dlResult != null && dlResult.Files.Any())
                                {
                                    await scopedFileService.SaveDownloadedFilesAsync(existingId, taxAccountId, dlResult, ct);
                                }

                                // Cập nhật chi tiết mặt hàng thật và ngày lập từ XML nếu có
                                if (dlResult?.XmlFile?.Content != null && dlResult.XmlFile.Content.Length > 0)
                                {
                                    try
                                    {
                                        var existingEntity = await scopedDb.Invoices
                                            .Include(i => i.Details)
                                            .FirstOrDefaultAsync(i => i.Id == existingId, ct);

                                        if (existingEntity != null)
                                        {
                                            var xmlStr = Encoding.UTF8.GetString(dlResult.XmlFile.Content);
                                            var parserFactory = scope.ServiceProvider.GetRequiredService<IInvoiceParserFactory>();
                                            var parser = parserFactory.GetParser(xmlStr);
                                            using var xms = new MemoryStream(dlResult.XmlFile.Content);
                                            var parsed = await parser.ParseAsync(xms, dlResult.XmlFile.FileName);
                                            if (parsed.Success)
                                            {
                                                if (parsed.IssueDate > DateTime.MinValue) existingEntity.IssueDate = parsed.IssueDate;
                                                if (!string.IsNullOrEmpty(parsed.SellerAddress)) existingEntity.SellerAddress = parsed.SellerAddress;
                                                if (!string.IsNullOrEmpty(parsed.BuyerAddress)) existingEntity.BuyerAddress = parsed.BuyerAddress;
                                                if (!string.IsNullOrEmpty(parsed.TaxAuthorityCode)) existingEntity.TaxAuthorityCode = parsed.TaxAuthorityCode;
                                                existingEntity.HasTaxCode = parsed.HasTaxCode;

                                                if (parsed.Details.Any())
                                                {
                                                    bool needsDetailUpdate = !existingEntity.Details.Any() ||
                                                        existingEntity.Details.All(d => d.ItemName.StartsWith("Hàng hóa, dịch vụ theo HĐ") || d.Unit == "Lô/Gói" || d.Unit == "Lần") ||
                                                        existingEntity.SourceProvider == "GDT-Direct";

                                                    if (needsDetailUpdate)
                                                    {
                                                        existingEntity.Details.Clear();
                                                        foreach (var d in parsed.Details)
                                                        {
                                                            existingEntity.Details.Add(new InvoiceDetail
                                                            {
                                                                LineNumber = d.LineNumber,
                                                                ItemCode = d.ItemCode,
                                                                ItemName = d.ItemName,
                                                                Unit = d.Unit,
                                                                Quantity = d.Quantity,
                                                                UnitPrice = d.UnitPrice,
                                                                AmountBeforeTax = d.AmountBeforeTax,
                                                                TaxRate = d.TaxRate,
                                                                TaxAmount = d.TaxAmount,
                                                                TotalAmount = d.TotalAmount
                                                            });
                                                        }
                                                        existingEntity.SourceProvider = "TCT-Standard";
                                                    }
                                                }
                                                await scopedDb.SaveChangesAsync(ct);
                                            }
                                        }
                                    }
                                    catch (Exception exUpd)
                                    {
                                        _logger.LogWarning(exUpd, "Không thể cập nhật chi tiết XML cho HĐ Id {Id}", existingId);
                                    }
                                }

                                // Đảm bảo luôn có tệp PDF hợp lệ sẵn sàng trên đĩa để xem ngay không cần đăng nhập lại
                                if (dlResult?.PdfFile == null)
                                {
                                    try
                                    {
                                        await scopedFileService.GetOrFetchOriginalPdfAsync(existingId, taxAccountId, loginResult.Token, ct);
                                    }
                                    catch (Exception exEnsurePdf)
                                    {
                                        _logger.LogWarning(exEnsurePdf, "Không thể đảm bảo cache PDF cho HĐ đã có {Symbol}-{Number}", inv.InvoiceSymbol, inv.InvoiceNumber);
                                    }
                                }

                                lock (lockObj)
                                {
                                    updatedCount++;
                                    successCount++;
                                    processed++;
                                    string fSummary = downloadedForThisInv > 0 ? $" [Đã lấy {downloadedForThisInv} tệp gốc]" : " [Đã khởi tạo PDF lưu trữ]";
                                    _jobTracker.AddLog(jobId, $"ℹ️ Cập nhật HĐ {inv.InvoiceSymbol}-{inv.InvoiceNumber} ({inv.SellerName}){fSummary}.");
                                }
                            }
                            else
                            {
                                // Nhập mới hóa đơn
                                byte[]? xmlBytes = dlResult?.XmlFile?.Content;
                                byte[]? pdfBytes = dlResult?.PdfFile?.Content;
                                bool isImported = false;

                                if (xmlBytes != null && xmlBytes.Length > 0)
                                {
                                    try
                                    {
                                        using var xmlStream = new MemoryStream(xmlBytes);
                                        var fileName = $"Remote_{inv.InvoiceSymbol}_{inv.InvoiceNumber}.xml";
                                        var importResult = await scopedImport.ImportXmlAsync(xmlStream, fileName, taxAccountId, inv.InvoiceType, userId, null, pdfBytes);
                                        if (importResult.SuccessCount > 0 || importResult.SkippedDuplicateCount > 0)
                                        {
                                            isImported = true;
                                        }
                                    }
                                    catch (Exception exImp)
                                    {
                                        _logger.LogWarning("Lỗi ImportXmlAsync cho HĐ {Symbol}-{Number}: {Msg}", inv.InvoiceSymbol, inv.InvoiceNumber, exImp.Message);
                                    }
                                }

                                if (!isImported)
                                {
                                    var directRes = await ImportDirectRemoteInvoiceWithDbAsync(scopedDb, inv, taxAccountId, userId, xmlBytes, pdfBytes);
                                    if (!directRes.IsSuccess && !directRes.IsDuplicate)
                                    {
                                        lock (lockObj)
                                        {
                                            failedCount++;
                                            processed++;
                                            _jobTracker.AddLog(jobId, $"❌ Lỗi lưu HĐ {inv.InvoiceSymbol}-{inv.InvoiceNumber}: {directRes.ErrorMessage}");
                                            _jobTracker.AddError(jobId, $"HĐ {inv.InvoiceSymbol}-{inv.InvoiceNumber}: {directRes.ErrorMessage}");
                                        }
                                    }
                                }

                                // Lưu toàn bộ tệp gốc vào bảng InvoiceFiles
                                var cleanSymbol = (inv.InvoiceSymbol ?? string.Empty).Trim();
                                var cleanNum = (inv.InvoiceNumber ?? string.Empty).Trim().PadLeft(7, '0');
                                var targetInvId = await scopedDb.Invoices
                                    .Where(i => i.TaxAccountId == taxAccountId && i.InvoiceSymbol == cleanSymbol && i.InvoiceNumber == cleanNum && i.SellerTaxCode == inv.SellerTaxCode)
                                    .Select(i => i.Id)
                                    .FirstOrDefaultAsync(ct);

                                if (targetInvId > 0)
                                {
                                    if (dlResult != null && dlResult.Files.Any())
                                    {
                                        try
                                        {
                                            await scopedFileService.SaveDownloadedFilesAsync(targetInvId, taxAccountId, dlResult, ct);
                                        }
                                        catch (Exception exSaveFiles)
                                        {
                                            _logger.LogWarning(exSaveFiles, "Lỗi khi lưu các tệp gốc cho HĐ {Symbol}-{Number}", inv.InvoiceSymbol, inv.InvoiceNumber);
                                        }
                                    }

                                    // Đảm bảo hóa đơn vừa lấy vào hệ thống luôn có ngay tệp PDF hợp lệ trên đĩa
                                    if (dlResult?.PdfFile == null)
                                    {
                                        try
                                        {
                                            await scopedFileService.GetOrFetchOriginalPdfAsync(targetInvId, taxAccountId, loginResult.Token, ct);
                                        }
                                        catch (Exception exEnsurePdf)
                                        {
                                            _logger.LogWarning(exEnsurePdf, "Không thể đảm bảo cache PDF cho HĐ mới {Symbol}-{Number}", inv.InvoiceSymbol, inv.InvoiceNumber);
                                        }
                                    }
                                }

                                lock (lockObj)
                                {
                                    newCount++;
                                    successCount++;
                                    processed++;
                                    string fSummary = downloadedForThisInv > 0 ? $" [Đã lưu {downloadedForThisInv} tệp gốc]" : " [Đã khởi tạo PDF lưu trữ]";
                                    _jobTracker.AddLog(jobId, $" Đã nhập thành công HĐ {inv.InvoiceSymbol}-{inv.InvoiceNumber} ({inv.SellerName}){fSummary}.");
                                }
                            }

                            lock (lockObj)
                            {
                                int currentDone = processed + duplicateCount;
                                int percent = 25 + (int)((double)currentDone / total * 70);
                                if (percent > 95) percent = 95;
                                _jobTracker.UpdateStats(jobId, newCount, updatedCount, filesDownloadedCount, filesFailedCount, missingFilesCount);
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
                                _jobTracker.AddError(jobId, $"HĐ {inv.InvoiceSymbol}-{inv.InvoiceNumber}: {exTask.Message}");
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
                var summaryMsg = $"Hoàn tất: {newCount} mới, {updatedCount} cập nhật, {duplicateCount} đã đủ file. Tải thành công {filesDownloadedCount} tệp gốc ({missingFilesCount} HĐ không có tệp trên Cổng Thuế), {failedCount} lỗi.";
                _jobTracker.UpdateStats(jobId, newCount, updatedCount, filesDownloadedCount, filesFailedCount, missingFilesCount);
                _jobTracker.UpdateProgress(jobId, 100, summaryMsg, totalFound: total, success: successCount, duplicate: duplicateCount, failed: failedCount);
                _jobTracker.CompleteJob(jobId, summaryMsg);

                await RecordConnectionStatusAsync(taxAccountId, providerName, cred.Username, true, summaryMsg, cred.Password, rememberPassword);
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
            string? userId,
            byte[]? xmlBytes = null,
            byte[]? pdfBytes = null)
        {
            try
            {
                var cleanSymbol = (inv.InvoiceSymbol ?? string.Empty).Trim();
                if (cleanSymbol.Length >= 7 && char.IsDigit(cleanSymbol[0]))
                {
                    cleanSymbol = cleanSymbol.Substring(1);
                }

                var cleanNumber = (inv.InvoiceNumber ?? string.Empty).Trim().PadLeft(7, '0');
                var rawNum = cleanNumber.TrimStart('0');
                if (string.IsNullOrEmpty(rawNum)) rawNum = "0";

                var sellerMst = (inv.SellerTaxCode ?? string.Empty).Trim();
                var invoiceType = string.Equals(inv.InvoiceType, "BanRa", StringComparison.OrdinalIgnoreCase) ? "BanRa" : "MuaVao";

                // 1. Kiểm tra tính idempotent (chống trùng lặp theo nghiệp vụ bao gồm cả dạng 6 ký tự và 7 ký tự)
                var existing = await db.Invoices
                    .Include(i => i.Details)
                    .FirstOrDefaultAsync(i =>
                        i.TaxAccountId == taxAccountId &&
                        i.InvoiceType == invoiceType &&
                        i.SellerTaxCode == sellerMst &&
                        (i.InvoiceSymbol == cleanSymbol || i.InvoiceSymbol == "1" + cleanSymbol) &&
                        (i.InvoiceNumber == cleanNumber || i.InvoiceNumber == rawNum));

                if (existing != null)
                {
                    // Chuẩn hóa lại ký hiệu và số hóa đơn của bản ghi đã có nếu trước đó lưu lệch
                    existing.InvoiceSymbol = cleanSymbol;
                    existing.InvoiceNumber = cleanNumber;

                    // Nếu đã tồn tại nhưng chưa có file PDF gốc, cập nhật ngay
                    if (string.IsNullOrEmpty(existing.RawPdfPath) && pdfBytes != null && pdfBytes.Length > 0)
                    {
                        try
                        {
                            var storageRoot = Path.Combine(Directory.GetCurrentDirectory(), "App_Data", "RawFiles", taxAccountId.ToString(), DateTime.Now.Year.ToString(), DateTime.Now.Month.ToString("D2"));
                            Directory.CreateDirectory(storageRoot);
                            var pdfPath = Path.Combine(storageRoot, $"Gov_{cleanSymbol}_{cleanNumber}_{Guid.NewGuid():N}.pdf");
                            await File.WriteAllBytesAsync(pdfPath, pdfBytes);
                            existing.RawPdfPath = pdfPath;
                            await db.SaveChangesAsync();
                        }
                        catch { }
                    }
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

                // 3. Tạo dòng chi tiết hàng hóa đại diện nếu chưa có chi tiết
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

                // 4. Lưu file XML/PDF gốc thực tế nếu có (TUYỆT ĐỐI không tự tạo XML giả để thay thế file gốc)
                try
                {
                    var storageRoot = Path.Combine(Directory.GetCurrentDirectory(), "App_Data", "RawFiles", taxAccountId.ToString(), DateTime.Now.Year.ToString(), DateTime.Now.Month.ToString("D2"));
                    Directory.CreateDirectory(storageRoot);

                    if (xmlBytes != null && xmlBytes.Length > 0)
                    {
                        var filePath = Path.Combine(storageRoot, $"GDT_{cleanSymbol}_{cleanNumber}_{Guid.NewGuid():N}.xml");
                        await File.WriteAllBytesAsync(filePath, xmlBytes);
                        invoice.RawXmlPath = filePath;
                    }

                    if (pdfBytes != null && pdfBytes.Length > 0)
                    {
                        var pdfPath = Path.Combine(storageRoot, $"Gov_{cleanSymbol}_{cleanNumber}_{Guid.NewGuid():N}.pdf");
                        await File.WriteAllBytesAsync(pdfPath, pdfBytes);
                        invoice.RawPdfPath = pdfPath;
                    }
                }
                catch (Exception exFile)
                {
                    _logger.LogWarning(exFile, "Không thể lưu file XML/PDF gốc cho HĐ {Symbol}-{Number}", cleanSymbol, cleanNumber);
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



        private async Task RecordConnectionStatusAsync(
            int taxAccountId,
            string providerName,
            string username,
            bool isSuccess,
            string message,
            string? clearPasswordToSave = null,
            bool rememberPassword = false)
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

                if (!string.IsNullOrEmpty(clearPasswordToSave) && (rememberPassword || string.IsNullOrEmpty(conn.EncryptedPassword)))
                {
                    conn.RememberPassword = rememberPassword;
                    conn.EncryptedPassword = _credentialProtector.Protect(clearPasswordToSave);
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
