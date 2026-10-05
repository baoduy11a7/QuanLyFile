using Hangfire;
using InvoiceManager.Data;
using InvoiceManager.Jobs;
using InvoiceManager.Jobs.JobTracker;
using InvoiceManager.Models.Entities;
using InvoiceManager.Models.ViewModels;
using InvoiceManager.Services;
using InvoiceManager.Services.Providers;
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
        private readonly ApplicationDbContext _db;

        public ImportController(
            IInvoiceImportService importService,
            ITaxAccountContext taxAccountContext,
            IInvoiceSourceProviderFactory providerFactory,
            IRemoteJobTracker jobTracker,
            ICredentialProtector credentialProtector,
            IAuditLogService auditLogService,
            ApplicationDbContext db)
        {
            _importService = importService;
            _taxAccountContext = taxAccountContext;
            _providerFactory = providerFactory;
            _jobTracker = jobTracker;
            _credentialProtector = credentialProtector;
            _auditLogService = auditLogService;
            _db = db;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var taxAccount = await _taxAccountContext.GetCurrentTaxAccountAsync();
            if (taxAccount == null) return RedirectToAction("Index", "TaxAccount");

            ViewBag.TaxAccount = taxAccount;
            return View();
        }

        #region File Upload (XML / ZIP)
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
                return Json(new { success = false, message = "Vui lòng chọn file XML hoặc ZIP chứa hóa đơn điện tử." });
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
            else
            {
                return Json(new { success = false, message = "Định dạng file không hỗ trợ. Vui lòng chỉ tải lên file .xml hoặc .zip." });
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
                    svgContent = captcha.SvgContent
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
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            if (!taxAccountId.HasValue)
            {
                return Json(new { success = false, message = "Chưa chọn tài khoản thuế." });
            }

            var conn = await _db.RemoteConnections
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.TaxAccountId == taxAccountId.Value && r.ProviderName == provider);

            if (conn == null)
            {
                return Json(new { success = true, exists = false });
            }

            return Json(new
            {
                success = true,
                exists = true,
                username = conn.Username,
                hasSavedPassword = conn.RememberPassword && !string.IsNullOrEmpty(conn.EncryptedPassword),
                lastSyncAt = conn.LastSyncAt?.ToString("dd/MM/yyyy HH:mm"),
                lastSyncStatus = conn.LastSyncStatus,
                lastSyncMessage = conn.LastSyncMessage
            });
        }

        [HttpPost]
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

            // Nếu mật khẩu để trống, kiểm tra xem có mật khẩu đã lưu không
            if (string.IsNullOrWhiteSpace(passwordToUse))
            {
                var savedConn = await _db.RemoteConnections
                    .FirstOrDefaultAsync(r => r.TaxAccountId == currentAccount.Id && r.ProviderName == model.ProviderName);

                if (savedConn != null && savedConn.RememberPassword && !string.IsNullOrEmpty(savedConn.EncryptedPassword))
                {
                    passwordToUse = _credentialProtector.Unprotect(savedConn.EncryptedPassword) ?? string.Empty;
                }
            }

            if (string.IsNullOrWhiteSpace(passwordToUse))
            {
                return Json(new { success = false, message = "Vui lòng nhập mật khẩu Cổng thuế." });
            }

            var cred = new ProviderCredentials
            {
                TaxCode = currentAccount.TaxCode,
                Username = model.Username.Trim(),
                Password = passwordToUse,
                CaptchaKey = model.CaptchaKey,
                CaptchaCode = model.CaptchaCode
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

            // Đẩy vào Hangfire chạy ngầm
            BackgroundJob.Enqueue<RemoteFetchJob>(job => job.ExecuteAsync(
                jobId,
                currentAccount.Id,
                model.ProviderName,
                cred,
                req,
                model.RememberPassword,
                userId,
                CancellationToken.None));

            return Json(new { success = true, jobId = jobId, message = "Đã khởi chạy tiến trình lấy hóa đơn chạy nền." });
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
        #endregion
    }

    public class CancelJobRequest
    {
        public string JobId { get; set; } = string.Empty;
    }
}
