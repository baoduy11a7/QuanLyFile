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
        private readonly IExcelImportService _excelImportService;
        private readonly ITaxAccountContext _taxAccountContext;

        public ImportController(
            IInvoiceImportService importService,
            IExcelImportService excelImportService,
            ITaxAccountContext taxAccountContext)
        {
            _importService = importService;
            _excelImportService = excelImportService;
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

        [HttpGet]
        public async Task<IActionResult> DownloadTemplate()
        {
            var templateBytes = await _excelImportService.GenerateTemplateAsync();
            string fileName = $"Mau_Nhap_Hoa_Don_Dien_Tu_{DateTime.Now:yyyyMMdd}.xlsx";
            return File(templateBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PreviewExcel(IFormFile? file, string defaultInvoiceType = "MuaVao")
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            if (!taxAccountId.HasValue)
            {
                return Json(new { success = false, message = "Chưa chọn tài khoản thuế." });
            }

            if (file == null || file.Length == 0)
            {
                return Json(new { success = false, message = "Vui lòng chọn file Excel (.xlsx, .xls) để nhập dữ liệu." });
            }

            var extension = System.IO.Path.GetExtension(file.FileName).ToLowerInvariant();
            if (extension != ".xlsx" && extension != ".xls")
            {
                return Json(new { success = false, message = "Định dạng file không hỗ trợ. Vui lòng chọn file .xlsx hoặc .xls." });
            }

            using var stream = file.OpenReadStream();
            var result = await _excelImportService.PreviewExcelAsync(stream, taxAccountId.Value, defaultInvoiceType);

            return Json(result);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ConfirmImport([FromBody] Models.ViewModels.ExcelImportConfirmModel model)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            if (!taxAccountId.HasValue)
            {
                return Json(new { success = false, message = "Chưa chọn tài khoản thuế." });
            }

            if (model == null || model.Rows == null || !model.Rows.Any())
            {
                return Json(new { success = false, message = "Không có dữ liệu hợp lệ để lưu." });
            }

            var validRows = model.Rows.Where(r => r.IsValid).ToList();
            if (!validRows.Any())
            {
                return Json(new { success = false, message = "Không có dòng hợp lệ nào để lưu." });
            }

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var result = await _excelImportService.SaveImportAsync(validRows, taxAccountId.Value, userId);

            return Json(result);
        }
    }
}
