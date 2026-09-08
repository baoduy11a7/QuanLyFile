using InvoiceManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Threading.Tasks;

namespace InvoiceManager.Controllers
{
    [Authorize]
    public class ImportController : Controller
    {
        private readonly IInvoiceImportService _importService;
        private readonly ITaxAccountContext _taxAccountContext;

        public ImportController(
            IInvoiceImportService importService,
            ITaxAccountContext taxAccountContext)
        {
            _importService = importService;
            _taxAccountContext = taxAccountContext;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var taxAccount = await _taxAccountContext.GetCurrentTaxAccountAsync();
            if (taxAccount == null) return RedirectToAction("Index", "TaxAccount");

            ViewBag.TaxAccount = taxAccount;
            return View();
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
    }
}
