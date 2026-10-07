using InvoiceManager.Data;
using InvoiceManager.Models.Entities;
using InvoiceManager.Models.ViewModels;
using InvoiceManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace InvoiceManager.Controllers
{
    [Authorize]
    public class ExportController : Controller
    {
        private readonly ApplicationDbContext _db;
        private readonly ITaxAccountContext _taxAccountContext;
        private readonly IExportService _exportService;
        private readonly IAuditLogService _auditLog;

        public ExportController(
            ApplicationDbContext db,
            ITaxAccountContext taxAccountContext,
            IExportService exportService,
            IAuditLogService auditLog)
        {
            _db = db;
            _taxAccountContext = taxAccountContext;
            _exportService = exportService;
            _auditLog = auditLog;
        }

        [HttpGet]
        public async Task<IActionResult> ExcelDetailed(InvoiceFilterViewModel filter)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            var taxAccount = await _taxAccountContext.GetCurrentTaxAccountAsync();
            if (!taxAccountId.HasValue) return BadRequest("Chưa chọn tài khoản thuế.");

            var invoices = await GetFilteredInvoicesQuery(filter, taxAccountId.Value).ToListAsync();

            string title = $"BẢNG KÊ CHI TIẾT HÀNG HÓA - HÓA ĐƠN {(filter.InvoiceType == "BanRa" ? "BÁN RA" : "MUA VÀO")}";
            var fileBytes = await _exportService.ExportDetailedExcelAsync(invoices, taxAccount, title);

            await _auditLog.LogActionAsync(
                "Xuất Excel Chi tiết", 
                $"{invoices.Count} hóa đơn", 
                $"Loại: {filter.InvoiceType}, Tổng tiền: {invoices.Sum(i => i.TotalAmount):N0} đ", 
                taxAccountId.Value);

            string fileName = $"BangKeChiTiet_{filter.InvoiceType}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
            return File(fileBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }

        [HttpGet]
        public async Task<IActionResult> ExcelSummary(InvoiceFilterViewModel filter)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            var taxAccount = await _taxAccountContext.GetCurrentTaxAccountAsync();
            if (!taxAccountId.HasValue) return BadRequest("Chưa chọn tài khoản thuế.");

            var invoices = await GetFilteredInvoicesQuery(filter, taxAccountId.Value).ToListAsync();

            string title = $"BẢNG KÊ TỔNG HỢP HÓA ĐƠN {(filter.InvoiceType == "BanRa" ? "BÁN RA" : "MUA VÀO")}";
            var fileBytes = await _exportService.ExportSummaryExcelAsync(invoices, taxAccount, title);

            await _auditLog.LogActionAsync(
                "Xuất Excel Tổng hợp", 
                $"{invoices.Count} hóa đơn", 
                $"Loại: {filter.InvoiceType}, Tổng tiền: {invoices.Sum(i => i.TotalAmount):N0} đ", 
                taxAccountId.Value);

            string fileName = $"BangKeTongHop_{filter.InvoiceType}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
            return File(fileBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BatchZip(InvoiceFilterViewModel filter, [FromForm] int[]? selectedIds)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            if (!taxAccountId.HasValue) return BadRequest("Chưa chọn tài khoản thuế.");

            List<Invoice> invoices;
            if (selectedIds != null && selectedIds.Length > 0)
            {
                invoices = await _db.Invoices
                    .Include(i => i.Details)
                    .Where(i => i.TaxAccountId == taxAccountId.Value && selectedIds.Contains(i.Id))
                    .ToListAsync();
            }
            else
            {
                invoices = await GetFilteredInvoicesQuery(filter, taxAccountId.Value).Take(200).ToListAsync();
            }

            if (!invoices.Any())
            {
                TempData["ErrorMessage"] = "Không có hóa đơn nào được chọn để xuất file ZIP.";
                return RedirectToAction("Index", "Invoice", filter);
            }

            var zipBytes = await _exportService.ExportInvoicesZipAsync(invoices);

            await _auditLog.LogActionAsync(
                "Xuất Bản thể hiện & XML (ZIP)", 
                $"{invoices.Count} hóa đơn", 
                $"Tải file nén chứa bản thể hiện & XML", 
                taxAccountId.Value);

            string fileName = $"HoaDon_HTML_XML_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
            return File(zipBytes, "application/zip", fileName);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BatchPdfZip(InvoiceFilterViewModel filter, [FromForm] int[]? selectedIds)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            if (!taxAccountId.HasValue) return BadRequest("Chưa chọn tài khoản thuế.");

            List<Invoice> invoices;
            if (selectedIds != null && selectedIds.Length > 0)
            {
                invoices = await _db.Invoices
                    .Include(i => i.Details)
                    .Where(i => i.TaxAccountId == taxAccountId.Value && selectedIds.Contains(i.Id))
                    .ToListAsync();
            }
            else
            {
                invoices = await GetFilteredInvoicesQuery(filter, taxAccountId.Value).Take(200).ToListAsync();
            }

            if (!invoices.Any())
            {
                TempData["ErrorMessage"] = "Không có hóa đơn nào để xuất file PDF gốc hàng loạt.";
                return RedirectToAction("Index", "Invoice", filter);
            }

            var zipBytes = await _exportService.ExportInvoicesPdfZipAsync(invoices);

            await _auditLog.LogActionAsync(
                "Tải PDF Gốc Hàng Loạt (ZIP)", 
                $"{invoices.Count} hóa đơn", 
                $"Tải file nén ZIP chứa các file PDF hóa đơn gốc", 
                taxAccountId.Value);

            string fileName = $"HoaDon_PDF_Goc_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
            return File(zipBytes, "application/zip", fileName);
        }

        [HttpGet]
        public async Task<IActionResult> BatchPdfZipGet(InvoiceFilterViewModel filter)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            if (!taxAccountId.HasValue) return BadRequest("Chưa chọn tài khoản thuế.");

            var invoices = await GetFilteredInvoicesQuery(filter, taxAccountId.Value).Take(200).ToListAsync();
            if (!invoices.Any())
            {
                TempData["ErrorMessage"] = "Không có hóa đơn nào phù hợp với bộ lọc để tải PDF.";
                return RedirectToAction("Index", "Invoice", filter);
            }

            var zipBytes = await _exportService.ExportInvoicesPdfZipAsync(invoices);
            await _auditLog.LogActionAsync(
                "Tải PDF Gốc Hàng Loạt (ZIP)", 
                $"{invoices.Count} hóa đơn", 
                $"Tải bộ lọc theo tiêu chí", 
                taxAccountId.Value);

            string fileName = $"HoaDon_PDF_Goc_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
            return File(zipBytes, "application/zip", fileName);
        }

        [HttpGet]
        public async Task<IActionResult> DownloadPdf(int id)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            var invoice = await _db.Invoices
                .Include(i => i.Details)
                .FirstOrDefaultAsync(i => i.Id == id && i.TaxAccountId == taxAccountId);

            if (invoice == null) return NotFound("Không tìm thấy hóa đơn.");

            var pdfBytes = await _exportService.GenerateInvoicePdfAsync(invoice);
            await _auditLog.LogActionAsync("Tải PDF hóa đơn", $"{invoice.InvoiceSymbol}-{invoice.InvoiceNumber}", null, taxAccountId);

            string fileName = $"HD_{invoice.InvoiceSymbol}_{invoice.InvoiceNumber}.pdf";
            return File(pdfBytes, "application/pdf", fileName);
        }

        private IQueryable<Invoice> GetFilteredInvoicesQuery(InvoiceFilterViewModel filter, int taxAccountId)
        {
            var query = _db.Invoices
                .Include(i => i.Details)
                .Where(i => i.TaxAccountId == taxAccountId);

            if (!string.IsNullOrEmpty(filter.InvoiceType))
                query = query.Where(i => i.InvoiceType == filter.InvoiceType);

            if (filter.FromDate.HasValue)
                query = query.Where(i => i.IssueDate >= filter.FromDate.Value.Date);

            if (filter.ToDate.HasValue)
                query = query.Where(i => i.IssueDate <= filter.ToDate.Value.Date.AddDays(1).AddTicks(-1));

            if (!string.IsNullOrWhiteSpace(filter.Keyword))
            {
                var kw = filter.Keyword.Trim();
                query = query.Where(i => i.InvoiceSymbol.Contains(kw) || i.InvoiceNumber.Contains(kw) || i.SellerTaxCode.Contains(kw) || i.SellerName.Contains(kw));
            }

            if (!string.IsNullOrWhiteSpace(filter.SellerTaxCode))
                query = query.Where(i => i.SellerTaxCode == filter.SellerTaxCode.Trim());

            if (!string.IsNullOrWhiteSpace(filter.SellerName))
            {
                var sn = filter.SellerName.Trim();
                query = query.Where(i => i.SellerName.Contains(sn));
            }

            if (!string.IsNullOrWhiteSpace(filter.InvoiceSymbol))
            {
                var sym = filter.InvoiceSymbol.Trim();
                query = query.Where(i => i.InvoiceSymbol.Contains(sym));
            }

            if (!string.IsNullOrWhiteSpace(filter.InvoiceNumber))
            {
                var num = filter.InvoiceNumber.Trim();
                query = query.Where(i => i.InvoiceNumber.Contains(num));
            }

            // Ký hiệu mẫu số hóa đơn (ký tự đầu tiên của ký hiệu mẫu số, ví dụ 1, 2, 3...)
            if (!string.IsNullOrWhiteSpace(filter.TemplateType))
            {
                var tt = filter.TemplateType.Trim();
                query = query.Where(i => i.InvoiceSymbol.StartsWith(tt));
            }

            // Trạng thái tải file
            if (!string.IsNullOrWhiteSpace(filter.FileDownloadStatus))
            {
                switch (filter.FileDownloadStatus)
                {
                    case "HasPdf":
                        query = query.Where(i => i.RawPdfPath != null && i.RawPdfPath != "");
                        break;
                    case "MissingPdf":
                        query = query.Where(i => i.RawPdfPath == null || i.RawPdfPath == "");
                        break;
                    case "HasXml":
                        query = query.Where(i => i.RawXmlPath != null && i.RawXmlPath != "");
                        break;
                    case "MissingXml":
                        query = query.Where(i => i.RawXmlPath == null || i.RawXmlPath == "");
                        break;
                    case "HasBoth":
                        query = query.Where(i => (i.RawPdfPath != null && i.RawPdfPath != "") && (i.RawXmlPath != null && i.RawXmlPath != ""));
                        break;
                }
            }

            // Kết quả kiểm tra
            if (!string.IsNullOrWhiteSpace(filter.ValidationResult))
            {
                switch (filter.ValidationResult)
                {
                    case "Normal":
                        query = query.Where(i => i.RiskLevel == "Normal");
                        break;
                    case "Warning":
                        query = query.Where(i => i.RiskLevel == "Warning" || i.RiskLevel == "HighRisk");
                        break;
                    case "HasTaxCode":
                        query = query.Where(i => i.HasTaxCode);
                        break;
                    case "NoTaxCode":
                        query = query.Where(i => !i.HasTaxCode);
                        break;
                    case "CashRegister":
                        query = query.Where(i => i.IsCashRegister);
                        break;
                }
            }

            if (filter.HasTaxCode.HasValue)
                query = query.Where(i => i.HasTaxCode == filter.HasTaxCode.Value);

            if (filter.IsCashRegister.HasValue)
                query = query.Where(i => i.IsCashRegister == filter.IsCashRegister.Value);

            if (!string.IsNullOrWhiteSpace(filter.Status))
                query = query.Where(i => i.Status == filter.Status);

            if (filter.MinAmount.HasValue)
                query = query.Where(i => i.TotalAmount >= filter.MinAmount.Value);

            if (filter.MaxAmount.HasValue)
                query = query.Where(i => i.TotalAmount <= filter.MaxAmount.Value);

            if (filter.IsReconciled.HasValue)
                query = query.Where(i => i.IsReconciled == filter.IsReconciled.Value);

            return query.OrderByDescending(i => i.IssueDate);
        }
    }
}
