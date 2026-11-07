using InvoiceManager.Models.ViewModels;
using InvoiceManager.Services;
using InvoiceManager.Services.TaxEngine;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Text;
using System.Threading.Tasks;

namespace InvoiceManager.Controllers
{
    [Authorize]
    public class TaxDeclarationController : Controller
    {
        private readonly ITaxDeclarationService _taxDeclarationService;
        private readonly ITaxAccountContext _taxAccountContext;
        private readonly IAuditLogService _auditLog;

        public TaxDeclarationController(
            ITaxDeclarationService taxDeclarationService,
            ITaxAccountContext taxAccountContext,
            IAuditLogService auditLog)
        {
            _taxDeclarationService = taxDeclarationService;
            _taxAccountContext = taxAccountContext;
            _auditLog = auditLog;
        }

        [HttpPost]
        public async Task<IActionResult> Calculate([FromBody] CalculateDeclarationRequest req)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            if (!taxAccountId.HasValue) return BadRequest("Chưa chọn doanh nghiệp.");

            string pType = string.IsNullOrWhiteSpace(req?.PeriodType) ? "Quarter" : req.PeriodType;
            int pVal = 1;
            if (string.Equals(pType, "Month", StringComparison.OrdinalIgnoreCase))
            {
                pVal = req?.Month ?? req?.PeriodValue ?? DateTime.Today.Month;
                if (pVal < 1 || pVal > 12) pVal = DateTime.Today.Month;
            }
            else
            {
                pVal = req?.Quarter ?? req?.PeriodValue ?? ((DateTime.Today.Month - 1) / 3 + 1);
                if (pVal < 1 || pVal > 4) pVal = 4;
            }

            int year = (req?.Year > 0) ? req.Year : DateTime.Today.Year;
            decimal cTax = req?.CarriedOverTax ?? req?.TaxCarriedForwardPreviousPeriod ?? 0;

            var model = await _taxDeclarationService.CalculateDeclarationAsync(
                taxAccountId.Value,
                pType,
                pVal,
                year,
                req?.FromDate,
                req?.ToDate,
                cTax,
                req?.Indicator21_NoActivity);

            await _auditLog.LogActionAsync(
                "Tính toán Tờ khai thuế GTGT",
                $"Kỳ: {model.PeriodDescription}",
                $"Mẫu: {model.FormVersionCode} ({model.LegalDocRef}) | Thuế phải nộp: {model.Target40_TaxToPay:N0} đ | Khấu trừ: {model.Target43_CarriedForwardDeduction:N0} đ",
                taxAccountId.Value);

            return Json(new { success = true, data = model });
        }

        [HttpGet]
        public async Task<IActionResult> GetPriorPeriodTarget43(string periodType, int periodValue, int year)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            if (!taxAccountId.HasValue) return Json(new { success = false, target43 = 0 });

            decimal prior43 = await _taxDeclarationService.GetPriorPeriodTarget43Async(taxAccountId.Value, periodType, periodValue, year);
            return Json(new { success = true, target43 = prior43 });
        }

        [HttpGet]
        public IActionResult GetLegalCatalog()
        {
            var registry = _taxDeclarationService.GetRegistry();
            var versions = registry.GetAllVersions();
            return Json(new { success = true, versions });
        }

        [HttpGet]
        [HttpPost]
        public async Task<IActionResult> ExportXml(
            [FromQuery] string? periodType,
            [FromQuery] int? periodValue,
            [FromQuery] int? quarter,
            [FromQuery] int? month,
            [FromQuery] int? year,
            [FromQuery] decimal? taxCarriedForwardPreviousPeriod,
            [FromQuery] decimal? carriedOverTax,
            [FromBody] CalculateDeclarationRequest? bodyReq)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            var taxAccount = await _taxAccountContext.GetCurrentTaxAccountAsync();
            if (!taxAccountId.HasValue) return BadRequest("Chưa chọn doanh nghiệp.");

            string pType = bodyReq?.PeriodType ?? periodType ?? "Quarter";
            int pVal = 1;
            if (string.Equals(pType, "Month", StringComparison.OrdinalIgnoreCase))
            {
                pVal = bodyReq?.Month ?? month ?? bodyReq?.PeriodValue ?? periodValue ?? DateTime.Today.Month;
                if (pVal < 1 || pVal > 12) pVal = DateTime.Today.Month;
            }
            else
            {
                pVal = bodyReq?.Quarter ?? quarter ?? bodyReq?.PeriodValue ?? periodValue ?? 4;
                if (pVal < 1 || pVal > 4) pVal = 4;
            }

            int y = bodyReq?.Year ?? year ?? DateTime.Today.Year;
            decimal cTax = bodyReq?.CarriedOverTax ?? bodyReq?.TaxCarriedForwardPreviousPeriod ?? carriedOverTax ?? taxCarriedForwardPreviousPeriod ?? 0;

            var model = await _taxDeclarationService.CalculateDeclarationAsync(
                taxAccountId.Value,
                pType,
                pVal,
                y,
                bodyReq?.FromDate,
                bodyReq?.ToDate,
                cTax,
                bodyReq?.Indicator21_NoActivity);

            string xml = _taxDeclarationService.GenerateDeclarationXml(model);
            byte[] bytes = Encoding.UTF8.GetBytes(xml);

            string kyStr = pType == "Quarter" ? $"Q{pVal}_{y}" : $"T{pVal:D2}_{y}";
            string fileName = $"ToKhai_01GTGT_{taxAccount?.TaxCode}_{model.FormVersionCode}_{kyStr}.xml";

            await _auditLog.LogActionAsync(
                "Xuất XML Tờ khai thuế GTGT",
                fileName,
                $"Căn cứ: {model.LegalDocRef} | Kỳ: {kyStr} | Thuế phải nộp: {model.Target40_TaxToPay:N0} đ",
                taxAccountId.Value);

            return File(bytes, "application/xml", fileName);
        }

        [HttpGet]
        [HttpPost]
        public async Task<IActionResult> ExportExcel(
            [FromQuery] string? periodType,
            [FromQuery] int? periodValue,
            [FromQuery] int? quarter,
            [FromQuery] int? month,
            [FromQuery] int? year,
            [FromQuery] decimal? taxCarriedForwardPreviousPeriod,
            [FromQuery] decimal? carriedOverTax,
            [FromBody] CalculateDeclarationRequest? bodyReq)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            var taxAccount = await _taxAccountContext.GetCurrentTaxAccountAsync();
            if (!taxAccountId.HasValue) return BadRequest("Chưa chọn doanh nghiệp.");

            string pType = bodyReq?.PeriodType ?? periodType ?? "Quarter";
            int pVal = 1;
            if (string.Equals(pType, "Month", StringComparison.OrdinalIgnoreCase))
            {
                pVal = bodyReq?.Month ?? month ?? bodyReq?.PeriodValue ?? periodValue ?? DateTime.Today.Month;
                if (pVal < 1 || pVal > 12) pVal = DateTime.Today.Month;
            }
            else
            {
                pVal = bodyReq?.Quarter ?? quarter ?? bodyReq?.PeriodValue ?? periodValue ?? 4;
                if (pVal < 1 || pVal > 4) pVal = 4;
            }

            int y = bodyReq?.Year ?? year ?? DateTime.Today.Year;
            decimal cTax = bodyReq?.CarriedOverTax ?? bodyReq?.TaxCarriedForwardPreviousPeriod ?? carriedOverTax ?? taxCarriedForwardPreviousPeriod ?? 0;

            var model = await _taxDeclarationService.CalculateDeclarationAsync(
                taxAccountId.Value,
                pType,
                pVal,
                y,
                bodyReq?.FromDate,
                bodyReq?.ToDate,
                cTax,
                bodyReq?.Indicator21_NoActivity);

            byte[] bytes = await _taxDeclarationService.ExportDeclarationExcelAsync(model, taxAccountId.Value);

            string kyStr = pType == "Quarter" ? $"Q{pVal}_{y}" : $"T{pVal:D2}_{y}";
            string fileName = $"BangKe_ToKhai_01GTGT_{taxAccount?.TaxCode}_{model.FormVersionCode}_{kyStr}.xlsx";

            await _auditLog.LogActionAsync(
                "Xuất Excel Tờ khai thuế GTGT & Bảng kê",
                fileName,
                $"Căn cứ: {model.LegalDocRef} | Kỳ: {kyStr}",
                taxAccountId.Value);

            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }
    }
}
