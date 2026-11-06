using Hangfire;
using InvoiceManager.Data;
using InvoiceManager.Jobs;
using InvoiceManager.Jobs.JobTracker;
using InvoiceManager.Models.Entities;
using InvoiceManager.Models.ViewModels;
using InvoiceManager.Services;
using InvoiceManager.Services.Providers;
using InvoiceManager.Services.Providers.Gdt;
using InvoiceManager.Services.Providers.Models;
using InvoiceManager.Services.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

namespace InvoiceManager.Controllers
{
    [Authorize]
    public class ImportController : Controller
    {
        private readonly IInvoiceImportService _importService;
        private readonly ITaxAccountContext _taxAccountContext;
        private readonly IInvoiceSourceProviderFactory _providerFactory;
        private readonly IRemoteJobTracker _jobTracker;
        private readonly ICredentialProtector _credentialProtector;
        private readonly IAuditLogService _auditLogService;
        private readonly ITaxCodeLookupService _taxCodeLookupService;
        private readonly ApplicationDbContext _db;
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly ILogger<ImportController> _logger;

        public ImportController(
            IInvoiceImportService importService,
            ITaxAccountContext taxAccountContext,
            IInvoiceSourceProviderFactory providerFactory,
            IRemoteJobTracker jobTracker,
            ICredentialProtector credentialProtector,
            IAuditLogService auditLogService,
            ITaxCodeLookupService taxCodeLookupService,
            ApplicationDbContext db,
            IServiceScopeFactory serviceScopeFactory,
            ILogger<ImportController> logger)
        {
            _importService = importService;
            _taxAccountContext = taxAccountContext;
            _providerFactory = providerFactory;
            _jobTracker = jobTracker;
            _credentialProtector = credentialProtector;
            _auditLogService = auditLogService;
            _taxCodeLookupService = taxCodeLookupService;
            _db = db;
            _serviceScopeFactory = serviceScopeFactory;
            _logger = logger;
        }

        private void DispatchRemoteFetchJob(
            string jobId,
            int taxAccountId,
            string providerName,
            ProviderCredentials cred,
            FetchRequest req,
            bool rememberPassword,
            string? userId)
        {
            // Chạy ngay lập tức trên ASP.NET Core ThreadPool qua IServiceScopeFactory (0ms độ trễ)
            // Khắc phục hoàn toàn tình trạng bị treo 10% do Hangfire server timeout hoặc chậm queue
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _serviceScopeFactory.CreateScope();
                    var job = scope.ServiceProvider.GetRequiredService<RemoteFetchJob>();
                    await job.ExecuteAsync(jobId, taxAccountId, providerName, cred, req, rememberPassword, userId, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi thực thi tiến trình đồng bộ ngầm {JobId}", jobId);
                    _jobTracker.FailJob(jobId, $"Lỗi hệ thống khi chạy tiến trình ngầm: {ex.Message}");
                }
            });
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var taxAccount = await _taxAccountContext.GetCurrentTaxAccountAsync();
            ViewBag.TaxAccount = taxAccount;
            return View();
        }

        [HttpPost]
        [Authorize(Roles = "Admin,Accountant")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> QuickCrawlAndSelect([FromBody] QuickCrawlRequest model)
        {
            if (string.IsNullOrWhiteSpace(model?.TaxCode))
            {
                return Json(new { success = false, message = "Vui lòng nhập mã số thuế cần cào thông tin." });
            }

            var lookup = await _taxCodeLookupService.LookupAsync(model.TaxCode);
            if (!lookup.Success)
            {
                return Json(new { success = false, message = lookup.Message ?? "Không thể cào thông tin từ mã số thuế này." });
            }

            var cleanTaxCode = lookup.TaxCode!.Trim();
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
            {
                return Json(new { success = false, message = "Phiên làm việc hết hạn. Vui lòng đăng nhập lại." });
            }

            var userAcc = await _db.UserTaxAccounts
                .Include(u => u.TaxAccount)
                .FirstOrDefaultAsync(u => u.UserId == userId && u.TaxAccount != null && u.TaxAccount.TaxCode == cleanTaxCode);

            bool isNew = false;
            TaxAccount account;

            if (userAcc?.TaxAccount == null)
            {
                isNew = true;
                account = new TaxAccount
                {
                    TaxCode = cleanTaxCode,
                    CompanyName = lookup.CompanyName?.Trim() ?? "Doanh nghiệp mới",
                    Address = lookup.Address?.Trim() ?? "",
                    IsActive = true,
                    CreatedAt = DateTime.Now
                };
                _db.TaxAccounts.Add(account);
                await _db.SaveChangesAsync();

                _db.UserTaxAccounts.Add(new UserTaxAccount
                {
                    UserId = userId,
                    TaxAccountId = account.Id,
                    IsDefault = true,
                    CanManage = true,
                    AssignedAt = DateTime.Now
                });
                await _db.SaveChangesAsync();
            }
            else
            {
                account = userAcc.TaxAccount;
                account.IsActive = true;
                if (!string.IsNullOrWhiteSpace(lookup.CompanyName)) account.CompanyName = lookup.CompanyName.Trim();
                if (!string.IsNullOrWhiteSpace(lookup.Address)) account.Address = lookup.Address.Trim();
                await _db.SaveChangesAsync();
            }

            await _taxAccountContext.SetCurrentTaxAccountAsync(account.Id);

            await _auditLogService.LogActionAsync(
                isNew ? "Cào và thêm Doanh nghiệp mới (từ Import)" : "Cào và kích hoạt lại Doanh nghiệp (từ Import)",
                $"MST: {account.TaxCode}",
                account.CompanyName,
                account.Id);

            return Json(new
            {
                success = true,
                isNew = isNew,
                taxAccountId = account.Id,
                taxCode = account.TaxCode,
                companyName = account.CompanyName,
                address = account.Address,
                status = lookup.Status ?? "Đang hoạt động",
                message = isNew
                    ? $"Đã cào thông tin và thêm doanh nghiệp '{account.CompanyName}' (MST: {account.TaxCode}) vào hệ thống thành công!"
                    : $"Đã cào thông tin và kích hoạt lại doanh nghiệp '{account.CompanyName}' (MST: {account.TaxCode}) thành công!"
            });
        }

        #region File Upload (XML / ZIP / Excel)
        [HttpGet]
        public async Task<IActionResult> DownloadTemplate(string invoiceType = "MuaVao")
        {
            var bytes = await _importService.GenerateExcelTemplateAsync(invoiceType);
            string typeName = invoiceType == "BanRa" ? "BanRa" : "MuaVao";
            string fileName = $"Mau_Nhap_Hoa_Don_{typeName}_{DateTime.Now:yyyyMMdd}.xlsx";
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Upload(IFormFile? file, string invoiceType = "MuaVao")
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            if (!taxAccountId.HasValue)
            {
                return Json(new { success = false, message = "Chưa chọn tài khoản thuế." });
            }

            if (file == null || file.Length == 0)
            {
                return Json(new { success = false, message = "Vui lòng chọn file XML, ZIP hoặc Excel chứa hóa đơn điện tử." });
            }

            var extension = System.IO.Path.GetExtension(file.FileName).ToLowerInvariant();
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            using var stream = file.OpenReadStream();

            if (extension == ".xml")
            {
                var result = await _importService.ImportXmlAsync(stream, file.FileName, taxAccountId.Value, invoiceType, userId);
                return Json(new
                {
                    success = result.SuccessCount > 0,
                    total = result.TotalFiles,
                    successCount = result.SuccessCount,
                    skippedCount = result.SkippedDuplicateCount,
                    failedCount = result.FailedCount,
                    messages = result.Messages
                });
            }
            else if (extension == ".zip")
            {
                var result = await _importService.ImportZipAsync(stream, taxAccountId.Value, invoiceType, userId);
                return Json(new
                {
                    success = result.SuccessCount > 0,
                    total = result.TotalFiles,
                    successCount = result.SuccessCount,
                    skippedCount = result.SkippedDuplicateCount,
                    failedCount = result.FailedCount,
                    messages = result.Messages
                });
            }
            else if (extension == ".xlsx" || extension == ".xls")
            {
                var result = await _importService.ImportExcelAsync(stream, file.FileName, taxAccountId.Value, invoiceType, userId);
                return Json(new
                {
                    success = result.SuccessCount > 0,
                    total = result.TotalFiles,
                    successCount = result.SuccessCount,
                    skippedCount = result.SkippedDuplicateCount,
                    failedCount = result.FailedCount,
                    messages = result.Messages
                });
            }
            else
            {
                return Json(new { success = false, message = "Định dạng file không hỗ trợ. Vui lòng tải lên file .xml, .zip hoặc .xlsx (.xls)." });
            }
        }
        #endregion

        #region Remote Tax Portal Sync
        [HttpGet]
        [Authorize(Roles = "Admin,Accountant")]
        public async Task<IActionResult> GetCaptcha(string provider = "Gdt")
        {
            try
            {
                var prov = _providerFactory.GetProvider(provider);
                var captcha = await prov.GetCaptchaAsync();

                if (captcha == null)
                {
                    return Json(new { success = false, message = "Không thể lấy mã Captcha từ Cổng thuế. Vui lòng thử lại sau." });
                }

                return Json(new
                {
                    success = true,
                    key = captcha.Key,
                    imageBase64 = captcha.ImageBase64,
                    isSvg = captcha.IsSvg,
                    svgContent = captcha.SvgContent,
                    autoCode = captcha.AutoSolvedCode
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = $"Lỗi khi tải Captcha: {ex.Message}" });
            }
        }

        [HttpGet]
        [Authorize(Roles = "Admin,Accountant")]
        public async Task<IActionResult> GetSavedConnection(string provider = "Gdt")
        {
            var currentAccount = await _taxAccountContext.GetCurrentTaxAccountAsync();
            if (currentAccount == null)
            {
                return Json(new { success = false, message = "Chưa chọn tài khoản thuế." });
            }

            var conn = await _db.RemoteConnections
                .AsNoTracking()
                .Where(r => r.ProviderName == provider && (r.TaxAccountId == currentAccount.Id || r.Username == currentAccount.TaxCode))
                .OrderByDescending(r => !string.IsNullOrEmpty(r.EncryptedPassword))
                .ThenByDescending(r => r.TaxAccountId == currentAccount.Id)
                .FirstOrDefaultAsync();

            var activeToken = GdtPortalProvider.GetCachedToken(conn?.Username ?? currentAccount.TaxCode, currentAccount.Id);
            bool hasActiveToken = !string.IsNullOrWhiteSpace(activeToken) && !GdtPortalProvider.IsJwtExpired(activeToken);

            if (conn == null)
            {
                return Json(new
                {
                    success = true,
                    exists = false,
                    username = currentAccount.TaxCode,
                    rememberPassword = true,
                    hasSavedPassword = false,
                    hasActiveToken = hasActiveToken
                });
            }

            return Json(new
            {
                success = true,
                exists = true,
                username = !string.IsNullOrWhiteSpace(conn.Username) ? conn.Username : currentAccount.TaxCode,
                rememberPassword = true,
                hasSavedPassword = !string.IsNullOrEmpty(conn.EncryptedPassword),
                hasActiveToken = hasActiveToken,
                lastSyncAt = conn.LastSyncAt?.ToString("dd/MM/yyyy HH:mm"),
                lastSyncStatus = conn.LastSyncStatus,
                lastSyncMessage = conn.LastSyncMessage
            });
        }

        [HttpPost]
        [Authorize(Roles = "Admin,Accountant")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetRememberPassword([FromBody] SetRememberPasswordRequest request)
        {
            var currentAccount = await _taxAccountContext.GetCurrentTaxAccountAsync();
            if (currentAccount == null)
            {
                return Json(new { success = false, message = "Chưa chọn tài khoản thuế." });
            }

            var provider = string.IsNullOrWhiteSpace(request?.Provider) ? "Gdt" : request.Provider;
            var conn = await _db.RemoteConnections
                .FirstOrDefaultAsync(r => r.TaxAccountId == currentAccount.Id && r.ProviderName == provider);

            if (conn != null)
            {
                conn.RememberPassword = request!.Remember;
                if (!request.Remember)
                {
                    conn.EncryptedPassword = null;
                }
                conn.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
            }

            return Json(new { success = true, remember = request?.Remember ?? false });
        }

        [HttpPost("Import/StartRemoteFetch")]
        [HttpPost("/api/invoices/sync-with-original-files")]
        [Authorize(Roles = "Admin,Accountant")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> StartRemoteFetch([FromBody] RemoteSyncRequestViewModel model)
        {
            if (!ModelState.IsValid)
            {
                return Json(new { success = false, message = "Dữ liệu nhập vào chưa hợp lệ." });
            }

            var currentAccount = await _taxAccountContext.GetCurrentTaxAccountAsync();
            if (currentAccount == null)
            {
                return Json(new { success = false, message = "Phiên làm việc hết hạn hoặc chưa chọn doanh nghiệp." });
            }

            // Kiểm tra bảo mật Multi-Tenant: MST nhập vào phải khớp chính xác với TaxAccount đang chọn
            if (!string.Equals(model.TaxCode?.Trim(), currentAccount.TaxCode.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                await _auditLogService.LogActionAsync("RemoteSync_Denied", model.ProviderName, 
                    $"Cảnh báo: MST nhập ({model.TaxCode}) không khớp với doanh nghiệp hiện tại ({currentAccount.TaxCode}).", currentAccount.Id);
                return Json(new { success = false, message = "Mã số thuế không khớp với Doanh nghiệp đang được chọn trên hệ thống." });
            }

            var passwordToUse = model.Password;
            bool hasToken = !string.IsNullOrWhiteSpace(model.BearerToken);
            if (!hasToken)
            {
                var cachedToken = GdtPortalProvider.GetCachedToken(currentAccount.TaxCode, currentAccount.Id);
                if (!string.IsNullOrWhiteSpace(cachedToken) && !GdtPortalProvider.IsJwtExpired(cachedToken))
                {
                    model.BearerToken = cachedToken;
                    hasToken = true;
                }
            }

            if (!hasToken)
            {
                // Nếu mật khẩu để trống, kiểm tra xem có mật khẩu đã lưu không
                if (string.IsNullOrWhiteSpace(passwordToUse))
                {
                    var savedConn = await _db.RemoteConnections
                        .Where(r => r.ProviderName == model.ProviderName && (r.TaxAccountId == currentAccount.Id || r.Username == currentAccount.TaxCode))
                        .OrderByDescending(r => !string.IsNullOrEmpty(r.EncryptedPassword))
                        .FirstOrDefaultAsync();

                    if (savedConn != null && !string.IsNullOrEmpty(savedConn.EncryptedPassword))
                    {
                        passwordToUse = _credentialProtector.Unprotect(savedConn.EncryptedPassword) ?? string.Empty;
                    }
                }

                if (string.IsNullOrWhiteSpace(passwordToUse))
                {
                    return Json(new { success = false, message = "Vui lòng nhập mật khẩu Cổng thuế hoặc dán Bearer Token." });
                }
            }

            var cred = new ProviderCredentials
            {
                TaxCode = currentAccount.TaxCode,
                Username = !string.IsNullOrWhiteSpace(model.Username) ? model.Username.Trim() : currentAccount.TaxCode,
                Password = passwordToUse ?? string.Empty,
                CaptchaKey = model.CaptchaKey,
                CaptchaCode = model.CaptchaCode,
                BearerToken = model.BearerToken?.Trim()
            };

            var req = new FetchRequest
            {
                TaxCode = currentAccount.TaxCode,
                FromDate = model.FromDate,
                ToDate = model.ToDate,
                InvoiceType = model.InvoiceType
            };

            var jobId = Guid.NewGuid().ToString("N");
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            // Khởi tạo trạng thái job trong Tracker
            _jobTracker.CreateJob(jobId, out _);

            // Ghi Audit Log bắt đầu
            await _auditLogService.LogActionAsync("RemoteSync_Started", model.ProviderName,
                $"Khởi chạy lấy hóa đơn từ {model.FromDate:dd/MM/yyyy} đến {model.ToDate:dd/MM/yyyy} ({model.InvoiceType})", currentAccount.Id);

            // Thực thi tiến trình đồng bộ ngầm ngay lập tức
            DispatchRemoteFetchJob(
                jobId,
                currentAccount.Id,
                model.ProviderName,
                cred,
                req,
                model.RememberPassword,
                userId);

            return Json(new { success = true, jobId = jobId, message = "Đã khởi chạy tiến trình đồng bộ hóa đơn và tự động tải toàn bộ tệp gốc." });
        }

        [HttpGet]
        [Authorize(Roles = "Admin,Accountant")]
        public IActionResult RemoteStatus(string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                return Json(new { success = false, message = "Mã tiến trình không hợp lệ." });
            }

            var info = _jobTracker.GetProgress(jobId);
            if (info == null)
            {
                return Json(new { success = false, message = "Không tìm thấy thông tin tiến trình hoặc tiến trình đã quá hạn." });
            }

            return Json(new
            {
                success = true,
                jobId = info.JobId,
                status = info.Status,
                percent = info.Percent,
                stage = info.CurrentStage,
                totalFound = info.TotalFound,
                successCount = info.SuccessCount,
                duplicateCount = info.DuplicateCount,
                failedCount = info.FailedCount,
                newCount = info.NewCount,
                updatedCount = info.UpdatedCount,
                filesDownloadedCount = info.FilesDownloadedCount,
                filesFailedCount = info.FilesFailedCount,
                missingFilesCount = info.MissingFilesCount,
                errors = info.Errors,
                logs = info.Logs,
                isFinished = info.IsFinished,
                errorMessage = info.ErrorMessage
            });
        }

        [HttpPost]
        [Authorize(Roles = "Admin,Accountant")]
        [ValidateAntiForgeryToken]
        public IActionResult CancelRemoteJob([FromBody] CancelJobRequest req)
        {
            if (string.IsNullOrWhiteSpace(req?.JobId))
            {
                return Json(new { success = false, message = "Mã tiến trình không hợp lệ." });
            }

            _jobTracker.CancelJob(req.JobId);
            return Json(new { success = true, message = "Đã gửi lệnh hủy tiến trình thành công." });
        }

        #region Automatic One-Click Sync & Quick Download (No Captcha / Password Prompt)

        [HttpPost]
        [Authorize(Roles = "Admin,Accountant")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AutoSync([FromBody] AutoSyncRequest? request)
        {
            var currentAccount = await _taxAccountContext.GetCurrentTaxAccountAsync();
            if (currentAccount == null)
            {
                return Json(new { success = false, message = "Chưa chọn tài khoản doanh nghiệp làm việc." });
            }

            // 1. Kiểm tra Token còn hạn trong cache hoặc Password đã lưu
            var activeToken = GdtPortalProvider.GetCachedToken(currentAccount.TaxCode, currentAccount.Id);
            bool hasValidToken = !string.IsNullOrWhiteSpace(activeToken) && !GdtPortalProvider.IsJwtExpired(activeToken);

            string? plainPassword = null;
            var conn = await _db.RemoteConnections
                .Where(r => r.ProviderName == "Gdt" && (r.TaxAccountId == currentAccount.Id || r.Username == currentAccount.TaxCode))
                .OrderByDescending(r => !string.IsNullOrEmpty(r.EncryptedPassword))
                .FirstOrDefaultAsync();

            if (conn != null && !string.IsNullOrEmpty(conn.EncryptedPassword))
            {
                plainPassword = _credentialProtector.Unprotect(conn.EncryptedPassword);
            }

            if (!hasValidToken && string.IsNullOrEmpty(plainPassword))
            {
                return Json(new
                {
                    success = false,
                    needSetup = true,
                    taxCode = currentAccount.TaxCode,
                    companyName = currentAccount.CompanyName,
                    hasSavedPassword = false,
                    message = "Doanh nghiệp chưa cấu hình mật khẩu Cổng Thuế. Vui lòng thiết lập mật khẩu 1 lần để hệ thống tự động giải Captcha và đồng bộ vĩnh viễn."
                });
            }

            // 2. Thiết lập khoảng thời gian: Mặc định từ đầu quý hiện tại đến hôm nay, hoặc theo request
            var now = DateTime.Today;
            int quarter = (now.Month - 1) / 3 + 1;
            var defaultFrom = new DateTime(now.Year, (quarter - 1) * 3 + 1, 1);
            var fromDate = request?.GetResolvedFromDate(defaultFrom) ?? defaultFrom;
            var toDate = request?.GetResolvedToDate(now) ?? now;
            var invoiceType = string.IsNullOrWhiteSpace(request?.InvoiceType) ? "All" : request.InvoiceType;
            var categoryType = string.IsNullOrWhiteSpace(request?.CategoryType) ? "All" : request.CategoryType;

            var cred = new ProviderCredentials
            {
                TaxCode = currentAccount.TaxCode,
                Username = conn?.Username ?? currentAccount.TaxCode,
                Password = plainPassword ?? string.Empty,
                BearerToken = hasValidToken ? activeToken : null
            };

            var fetchReq = new FetchRequest
            {
                TaxCode = currentAccount.TaxCode,
                FromDate = fromDate,
                ToDate = toDate,
                InvoiceType = invoiceType,
                CategoryType = categoryType
            };

            var jobId = Guid.NewGuid().ToString("N");
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            _jobTracker.CreateJob(jobId, out _);

            await _auditLogService.LogActionAsync("AutoSync_Started", "Gdt",
                $"Tự động đồng bộ hóa đơn không cần nhập mật khẩu cho {currentAccount.CompanyName} ({currentAccount.TaxCode}) từ {fromDate:dd/MM/yyyy} đến {toDate:dd/MM/yyyy}.", currentAccount.Id);

            DispatchRemoteFetchJob(
                jobId,
                currentAccount.Id,
                "Gdt",
                cred,
                fetchReq,
                true,
                userId);

            return Json(new
            {
                success = true,
                jobId = jobId,
                companyName = currentAccount.CompanyName,
                taxCode = currentAccount.TaxCode,
                message = $"Đã tự động kết nối Cổng Tổng cục Thuế và khởi chạy đồng bộ cho {currentAccount.CompanyName}."
            });
        }

        [HttpGet]
        [Authorize(Roles = "Admin,Accountant")]
        public async Task<IActionResult> GetSyncHistory()
        {
            var currentAccount = await _taxAccountContext.GetCurrentTaxAccountAsync();
            if (currentAccount == null)
            {
                return Json(new { success = false, message = "Chưa chọn tài khoản doanh nghiệp làm việc." });
            }

            // Chỉ lấy nhật ký đồng bộ API Cổng Thuế GOV thực tế, loại bỏ log kiểm tra thư mục trống
            var logs = await _db.SyncLogs
                .AsNoTracking()
                .Where(s => s.TaxAccountId == currentAccount.Id && !s.SyncType.Contains("Hangfire") && !s.SyncType.Contains("Thư mục"))
                .OrderByDescending(s => s.SyncedAt)
                .Take(50)
                .ToListAsync();

            var result = logs.Select((l, idx) =>
            {
                string fromDate = "";
                string toDate = "";
                if (!string.IsNullOrEmpty(l.SyncType) && l.SyncType.Contains("-"))
                {
                    var parts = l.SyncType.Split('-');
                    if (parts.Length == 2)
                    {
                        fromDate = parts[0].Trim();
                        toDate = parts[1].Trim();
                    }
                }
                if (string.IsNullOrEmpty(fromDate))
                {
                    fromDate = l.SyncedAt.ToString("dd/MM/yyyy");
                    toDate = l.SyncedAt.ToString("dd/MM/yyyy");
                }

                int displayCount = l.NewInvoiceCount;
                if (displayCount == 0 && !string.IsNullOrEmpty(l.ErrorMessage))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(l.ErrorMessage, @"(\d+)\s+đã đủ file|Tìm thấy\s+(\d+)");
                    if (match.Success)
                    {
                        var numStr = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                        int.TryParse(numStr, out displayCount);
                    }
                }

                return new
                {
                    stt = idx + 1,
                    fromDate = fromDate,
                    toDate = toDate,
                    syncedCount = displayCount,
                    status = l.Status,
                    description = l.ErrorMessage ?? (l.Status == "Thành công" ? "Đồng bộ thành công từ Cổng Thuế GOV" : "Có lỗi phát sinh"),
                    syncedAt = l.SyncedAt.ToString("dd/MM/yyyy HH:mm")
                };
            }).ToList();

            return Json(new
            {
                success = true,
                companyName = currentAccount.CompanyName,
                taxCode = currentAccount.TaxCode,
                data = result
            });
        }

        [HttpPost]
        [Authorize(Roles = "Admin,Accountant")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteSyncData([FromBody] DeleteSyncDataRequest? request)
        {
            var currentAccount = await _taxAccountContext.GetCurrentTaxAccountAsync();
            if (currentAccount == null)
            {
                return Json(new { success = false, message = "Chưa chọn tài khoản doanh nghiệp làm việc." });
            }

            var fromDate = request?.GetResolvedFromDate(DateTime.MinValue) ?? DateTime.MinValue;
            var toDate = request?.GetResolvedToDate(DateTime.MaxValue) ?? DateTime.MaxValue;
            if (toDate < DateTime.MaxValue.Date)
            {
                toDate = toDate.Date.AddDays(1).AddTicks(-1);
            }

            var query = _db.Invoices
                .Include(i => i.Details)
                .Include(i => i.Files)
                .Where(i => i.TaxAccountId == currentAccount.Id && i.IssueDate >= fromDate && i.IssueDate <= toDate);

            if (!string.IsNullOrWhiteSpace(request?.InvoiceType) && request.InvoiceType != "All")
            {
                query = query.Where(i => i.InvoiceType == request.InvoiceType);
            }

            var invoicesToDelete = await query.ToListAsync();
            int deletedCount = invoicesToDelete.Count;

            foreach (var inv in invoicesToDelete)
            {
                if (!string.IsNullOrEmpty(inv.RawXmlPath) && System.IO.File.Exists(inv.RawXmlPath))
                {
                    try { System.IO.File.Delete(inv.RawXmlPath); } catch { }
                }
                if (!string.IsNullOrEmpty(inv.RawPdfPath) && System.IO.File.Exists(inv.RawPdfPath))
                {
                    try { System.IO.File.Delete(inv.RawPdfPath); } catch { }
                }
            }

            if (invoicesToDelete.Count > 0)
            {
                _db.Invoices.RemoveRange(invoicesToDelete);
            }

            // Xóa log đồng bộ tương ứng trong khoảng ngày
            var logsToDelete = await _db.SyncLogs
                .Where(s => s.TaxAccountId == currentAccount.Id && s.SyncedAt >= fromDate && s.SyncedAt <= toDate)
                .ToListAsync();
            if (logsToDelete.Count > 0)
            {
                _db.SyncLogs.RemoveRange(logsToDelete);
            }

            await _db.SaveChangesAsync();

            await _auditLogService.LogActionAsync("DeleteSyncData", "Gdt",
                $"Đã xóa {deletedCount} hóa đơn đã đồng bộ của {currentAccount.CompanyName} từ {fromDate:dd/MM/yyyy} đến {toDate:dd/MM/yyyy}.", currentAccount.Id);

            return Json(new
            {
                success = true,
                deletedCount = deletedCount,
                message = $"Đã xóa thành công {deletedCount} hóa đơn đã đồng bộ trong khoảng thời gian từ {fromDate:dd/MM/yyyy} đến {toDate:dd/MM/yyyy}."
            });
        }

        [HttpPost]
        [Authorize(Roles = "Admin,Accountant")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AutoQuickDownload([FromBody] AutoSyncRequest? request)
        {
            var currentAccount = await _taxAccountContext.GetCurrentTaxAccountAsync();
            if (currentAccount == null)
            {
                return Json(new { success = false, message = "Chưa chọn tài khoản doanh nghiệp làm việc." });
            }

            // Kiểm tra Token còn hạn trong cache hoặc Password đã lưu
            var activeToken = GdtPortalProvider.GetCachedToken(currentAccount.TaxCode, currentAccount.Id);
            bool hasValidToken = !string.IsNullOrWhiteSpace(activeToken) && !GdtPortalProvider.IsJwtExpired(activeToken);

            string? plainPassword = null;
            var conn = await _db.RemoteConnections
                .Where(r => r.ProviderName == "Gdt" && (r.TaxAccountId == currentAccount.Id || r.Username == currentAccount.TaxCode))
                .OrderByDescending(r => !string.IsNullOrEmpty(r.EncryptedPassword))
                .FirstOrDefaultAsync();

            if (conn != null && !string.IsNullOrEmpty(conn.EncryptedPassword))
            {
                plainPassword = _credentialProtector.Unprotect(conn.EncryptedPassword);
            }

            if (!hasValidToken && string.IsNullOrEmpty(plainPassword))
            {
                return Json(new
                {
                    success = false,
                    needSetup = true,
                    taxCode = currentAccount.TaxCode,
                    companyName = currentAccount.CompanyName,
                    hasSavedPassword = false,
                    message = "Doanh nghiệp chưa cấu hình mật khẩu Cổng Thuế. Vui lòng thiết lập mật khẩu 1 lần để hệ thống tự động giải Captcha và tải nhanh vĩnh viễn."
                });
            }

            // Tải nhanh: Lấy 30 ngày gần nhất
            var now = DateTime.Today;
            var fromDate = request?.GetResolvedFromDate(now.AddDays(-30)) ?? now.AddDays(-30);
            var toDate = request?.GetResolvedToDate(now) ?? now;
            var invoiceType = string.IsNullOrWhiteSpace(request?.InvoiceType) ? "All" : request.InvoiceType;

            var cred = new ProviderCredentials
            {
                TaxCode = currentAccount.TaxCode,
                Username = conn?.Username ?? currentAccount.TaxCode,
                Password = plainPassword ?? string.Empty,
                BearerToken = hasValidToken ? activeToken : null
            };

            var fetchReq = new FetchRequest
            {
                TaxCode = currentAccount.TaxCode,
                FromDate = fromDate,
                ToDate = toDate,
                InvoiceType = invoiceType
            };

            var jobId = Guid.NewGuid().ToString("N");
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            _jobTracker.CreateJob(jobId, out _);

            await _auditLogService.LogActionAsync("AutoQuickDownload_Started", "Gdt",
                $"Tải nhanh dữ liệu hóa đơn 30 ngày ({fromDate:dd/MM/yyyy} - {toDate:dd/MM/yyyy}) kèm tệp gốc cho {currentAccount.CompanyName}.", currentAccount.Id);

            DispatchRemoteFetchJob(
                jobId,
                currentAccount.Id,
                "Gdt",
                cred,
                fetchReq,
                true,
                userId);

            return Json(new
            {
                success = true,
                jobId = jobId,
                companyName = currentAccount.CompanyName,
                taxCode = currentAccount.TaxCode,
                message = $"Đang tự động tải nhanh hóa đơn và toàn bộ tệp gốc cho {currentAccount.CompanyName}."
            });
        }

        [HttpPost]
        [Authorize(Roles = "Admin,Accountant")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AutoMultiCompanySync()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
            {
                return Json(new { success = false, message = "Phiên làm việc hết hạn." });
            }

            var userAccounts = await _db.UserTaxAccounts
                .Include(u => u.TaxAccount)
                .Where(u => u.UserId == userId && u.TaxAccount != null)
                .Select(u => u.TaxAccount!)
                .ToListAsync();

            if (!userAccounts.Any())
            {
                return Json(new { success = false, message = "Không tìm thấy danh sách doanh nghiệp." });
            }

            int launchedCount = 0;
            var now = DateTime.Today;
            int quarter = (now.Month - 1) / 3 + 1;
            var defaultFrom = new DateTime(now.Year, (quarter - 1) * 3 + 1, 1);

            foreach (var acc in userAccounts)
            {
                var activeToken = GdtPortalProvider.GetCachedToken(acc.TaxCode, acc.Id);
                bool hasToken = !string.IsNullOrWhiteSpace(activeToken) && !GdtPortalProvider.IsJwtExpired(activeToken);

                string? plainPassword = null;
                var conn = await _db.RemoteConnections
                    .Where(r => r.ProviderName == "Gdt" && (r.TaxAccountId == acc.Id || r.Username == acc.TaxCode))
                    .OrderByDescending(r => !string.IsNullOrEmpty(r.EncryptedPassword))
                    .FirstOrDefaultAsync();

                if (conn != null && !string.IsNullOrEmpty(conn.EncryptedPassword))
                {
                    plainPassword = _credentialProtector.Unprotect(conn.EncryptedPassword);
                }

                if (!hasToken && string.IsNullOrEmpty(plainPassword))
                {
                    continue; // Bỏ qua cty chưa cấu hình
                }

                var cred = new ProviderCredentials
                {
                    TaxCode = acc.TaxCode,
                    Username = conn?.Username ?? acc.TaxCode,
                    Password = plainPassword ?? string.Empty,
                    BearerToken = hasToken ? activeToken : null
                };

                var fetchReq = new FetchRequest
                {
                    TaxCode = acc.TaxCode,
                    FromDate = defaultFrom,
                    ToDate = now,
                    InvoiceType = "All"
                };

                var jobId = Guid.NewGuid().ToString("N");
                _jobTracker.CreateJob(jobId, out _);

                DispatchRemoteFetchJob(
                    jobId,
                    acc.Id,
                    "Gdt",
                    cred,
                    fetchReq,
                    true,
                    userId);

                launchedCount++;
            }

            return Json(new
            {
                success = true,
                count = launchedCount,
                message = launchedCount > 0
                    ? $"Đã tự động khởi chạy đồng bộ ngầm cho {launchedCount}/{userAccounts.Count} doanh nghiệp có cấu hình Cổng Thuế."
                    : "Chưa có doanh nghiệp nào được lưu thông tin kết nối Cổng Thuế."
            });
        }

        [HttpPost]
        [Authorize(Roles = "Admin,Accountant")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveGovConfig([FromBody] SaveGovConfigRequest req)
        {
            var currentAccount = await _taxAccountContext.GetCurrentTaxAccountAsync();
            if (currentAccount == null)
            {
                return Json(new { success = false, message = "Chưa chọn tài khoản doanh nghiệp." });
            }

            if (string.IsNullOrWhiteSpace(req?.Password))
            {
                return Json(new { success = false, message = "Vui lòng nhập mật khẩu Cổng Thuế." });
            }

            var conn = await _db.RemoteConnections
                .FirstOrDefaultAsync(r => r.TaxAccountId == currentAccount.Id && r.ProviderName == "Gdt");

            if (conn == null)
            {
                conn = new RemoteConnection
                {
                    TaxAccountId = currentAccount.Id,
                    ProviderName = "Gdt",
                    Username = !string.IsNullOrWhiteSpace(req.Username) ? req.Username.Trim() : currentAccount.TaxCode,
                    RememberPassword = true,
                    CreatedAt = DateTime.UtcNow
                };
                _db.RemoteConnections.Add(conn);
            }

            conn.Username = !string.IsNullOrWhiteSpace(req.Username) ? req.Username.Trim() : currentAccount.TaxCode;
            conn.RememberPassword = true;
            conn.EncryptedPassword = _credentialProtector.Protect(req.Password.Trim());
            conn.UpdatedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync();

            await _auditLogService.LogActionAsync("SaveGovConfig", "Gdt",
                $"Đã lưu thông tin kết nối Cổng Tổng cục Thuế cho {currentAccount.CompanyName}.", currentAccount.Id);

            return Json(new { success = true, message = "Đã lưu thông tin kết nối thành công. Từ giờ hệ thống sẽ tự động đồng bộ hóa đơn!" });
        }

        #endregion
        #endregion
    }

    public class CancelJobRequest
    {
        public string JobId { get; set; } = string.Empty;
    }

    public class SetRememberPasswordRequest
    {
        public string Provider { get; set; } = "Gdt";
        public bool Remember { get; set; }
    }

    public class AutoSyncRequest
    {
        public string? FromDate { get; set; }
        public string? ToDate { get; set; }
        public string? InvoiceType { get; set; } = "All";
        public string? CategoryType { get; set; } = "All";

        public DateTime GetResolvedFromDate(DateTime fallback)
        {
            if (string.IsNullOrWhiteSpace(FromDate)) return fallback;
            if (DateTime.TryParseExact(FromDate.Trim(), new[] { "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy" }, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d))
                return d;
            if (DateTime.TryParse(FromDate, out var dAny))
                return dAny;
            return fallback;
        }

        public DateTime GetResolvedToDate(DateTime fallback)
        {
            if (string.IsNullOrWhiteSpace(ToDate)) return fallback;
            if (DateTime.TryParseExact(ToDate.Trim(), new[] { "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy" }, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d))
                return d;
            if (DateTime.TryParse(ToDate, out var dAny))
                return dAny;
            return fallback;
        }
    }

    public class DeleteSyncDataRequest
    {
        public string? FromDate { get; set; }
        public string? ToDate { get; set; }
        public string? InvoiceType { get; set; } = "All";

        public DateTime GetResolvedFromDate(DateTime fallback)
        {
            if (string.IsNullOrWhiteSpace(FromDate)) return fallback;
            if (DateTime.TryParseExact(FromDate.Trim(), new[] { "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy" }, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d))
                return d;
            if (DateTime.TryParse(FromDate, out var dAny))
                return dAny;
            return fallback;
        }

        public DateTime GetResolvedToDate(DateTime fallback)
        {
            if (string.IsNullOrWhiteSpace(ToDate)) return fallback;
            if (DateTime.TryParseExact(ToDate.Trim(), new[] { "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy" }, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d))
                return d;
            if (DateTime.TryParse(ToDate, out var dAny))
                return dAny;
            return fallback;
        }
    }

    public class SaveGovConfigRequest
    {
        public string? Username { get; set; }
        public string Password { get; set; } = string.Empty;
    }
}
