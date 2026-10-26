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

using InvoiceManager.Services.Providers;
using InvoiceManager.Services.Providers.Models;
using InvoiceManager.Services.Parsers;
using InvoiceManager.Services.Security;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Text;

namespace InvoiceManager.Controllers
{
    [Authorize]
    public class ExportController : Controller
    {
        private readonly ApplicationDbContext _db;
        private readonly ITaxAccountContext _taxAccountContext;
        private readonly IExportService _exportService;
        private readonly IAuditLogService _auditLog;
        private readonly IEnumerable<IInvoiceSourceProvider> _providers;
        private readonly ICredentialProtector _protector;
        private readonly IInvoiceParserFactory _parserFactory;
        private readonly ILogger<ExportController> _logger;

        public ExportController(
            ApplicationDbContext db,
            ITaxAccountContext taxAccountContext,
            IExportService exportService,
            IAuditLogService auditLog,
            IEnumerable<IInvoiceSourceProvider> providers,
            ICredentialProtector protector,
            IInvoiceParserFactory parserFactory,
            ILogger<ExportController> logger)
        {
            _db = db;
            _taxAccountContext = taxAccountContext;
            _exportService = exportService;
            _auditLog = auditLog;
            _providers = providers;
            _protector = protector;
            _parserFactory = parserFactory;
            _logger = logger;
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

        [HttpGet]
        public async Task<IActionResult> ExcelSmartPro(InvoiceFilterViewModel filter)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            var taxAccount = await _taxAccountContext.GetCurrentTaxAccountAsync();
            if (!taxAccountId.HasValue) return BadRequest("Chưa chọn tài khoản thuế.");

            var invoices = await GetFilteredInvoicesQuery(filter, taxAccountId.Value).ToListAsync();

            string title = $"SỔ KẾ TOÁN SMART PRO (KTSC) - HÓA ĐƠN {(filter.InvoiceType == "BanRa" ? "BÁN RA" : "MUA VÀO")}";
            var fileBytes = await _exportService.ExportSmartProExcelAsync(invoices, taxAccount, title);

            await _auditLog.LogActionAsync(
                "Xuất Excel Smart Pro (KTSC)", 
                $"{invoices.Count} hóa đơn", 
                $"Loại: {filter.InvoiceType}, Tổng tiền: {invoices.Sum(i => i.TotalAmount):N0} đ", 
                taxAccountId.Value);

            string fileName = $"Smart_KTSC_{filter.InvoiceType}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
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

            // Tự động tải bù file PDF gốc từ Cổng Thuế cho các hóa đơn chưa có file PDF trên máy
            var missingPdfs = invoices.Where(i => string.IsNullOrEmpty(i.RawPdfPath) || !System.IO.File.Exists(i.RawPdfPath)).Take(15).ToList();
            foreach (var inv in missingPdfs)
            {
                await TryFetchOriginalPdfFromGovAsync(inv);
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

            // Tự động tải bù file PDF gốc từ Cổng Thuế cho các hóa đơn chưa có file PDF trên máy
            var missingPdfs = invoices.Where(i => string.IsNullOrEmpty(i.RawPdfPath) || !System.IO.File.Exists(i.RawPdfPath)).Take(15).ToList();
            foreach (var inv in missingPdfs)
            {
                await TryFetchOriginalPdfFromGovAsync(inv);
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

            // Nếu chưa có file PDF gốc hoặc đang dùng dòng hàng tạm từ metadata, thử tải file gốc từ Cổng Thuế
            bool hasDummyDetails = invoice.Details.Any(d => d.ItemName.StartsWith("Hàng hóa, dịch vụ theo HĐ", StringComparison.OrdinalIgnoreCase) || d.Unit == "Lô/Gói");
            if (string.IsNullOrEmpty(invoice.RawPdfPath) || !System.IO.File.Exists(invoice.RawPdfPath) || hasDummyDetails)
            {
                await TryFetchOriginalPdfFromGovAsync(invoice);
            }

            // Xóa cache cũ cả trong thư mục thực thi lẫn thư mục gốc ứng dụng
            var cacheDirs = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "App_Data", "GeneratedPdfs"),
                Path.Combine(Directory.GetCurrentDirectory(), "App_Data", "GeneratedPdfs")
            };
            foreach (var dir in cacheDirs)
            {
                var cachedPdfPath = Path.Combine(dir, $"HD_{invoice.Id}_{invoice.InvoiceSymbol}_{invoice.InvoiceNumber}.pdf");
                try { if (System.IO.File.Exists(cachedPdfPath)) System.IO.File.Delete(cachedPdfPath); } catch { }
            }

            var pdfBytes = await _exportService.GenerateInvoicePdfAsync(invoice);
            await _auditLog.LogActionAsync("Tải PDF hóa đơn", $"{invoice.InvoiceSymbol}-{invoice.InvoiceNumber}", null, taxAccountId);

            string fileName = $"HD_{invoice.InvoiceSymbol}_{invoice.InvoiceNumber}.pdf";
            return File(pdfBytes, "application/pdf", fileName);
        }

        private async Task TryFetchOriginalPdfFromGovAsync(Invoice invoice)
        {
            if (invoice == null || invoice.TaxAccountId <= 0) return;

            var conn = await _db.RemoteConnections
                .FirstOrDefaultAsync(c => c.TaxAccountId == invoice.TaxAccountId && c.ProviderName == "Gdt");

            if (conn == null || string.IsNullOrEmpty(conn.EncryptedPassword)) return;

            try
            {
                var provider = _providers.FirstOrDefault(p => p.ProviderName == "Gdt");
                if (provider == null) return;

                var plainPassword = _protector.Unprotect(conn.EncryptedPassword);
                if (string.IsNullOrEmpty(plainPassword)) return;

                var loginRes = await provider.LoginAsync(new ProviderCredentials
                {
                    TaxCode = conn.Username,
                    Username = conn.Username,
                    Password = plainPassword
                });
                if (!loginRes.Success || string.IsNullOrEmpty(loginRes.Token)) return;
                string token = loginRes.Token;

                var remoteInv = new RemoteInvoice
                {
                    InvoiceSymbol = invoice.InvoiceSymbol,
                    InvoiceNumber = invoice.InvoiceNumber,
                    RawInvoiceNumber = invoice.InvoiceNumber,
                    SellerTaxCode = invoice.SellerTaxCode,
                    BuyerTaxCode = invoice.BuyerTaxCode
                };

                var files = await provider.DownloadInvoiceFilesAsync(remoteInv, token);
                var storageRoot = Path.Combine(Directory.GetCurrentDirectory(), "App_Data", "RawFiles", invoice.TaxAccountId.ToString(), DateTime.Now.Year.ToString(), DateTime.Now.Month.ToString("D2"));
                Directory.CreateDirectory(storageRoot);

                bool updated = false;
                if (files.PdfBytes != null && files.PdfBytes.Length > 0)
                {
                    var pdfPath = Path.Combine(storageRoot, $"Gov_{invoice.InvoiceSymbol}_{invoice.InvoiceNumber}_{Guid.NewGuid():N}.pdf");
                    await System.IO.File.WriteAllBytesAsync(pdfPath, files.PdfBytes);
                    invoice.RawPdfPath = pdfPath;
                    updated = true;
                }

                if (files.XmlBytes != null && files.XmlBytes.Length > 0)
                {
                    var xmlPath = Path.Combine(storageRoot, $"Gov_{invoice.InvoiceSymbol}_{invoice.InvoiceNumber}_{Guid.NewGuid():N}.xml");
                    await System.IO.File.WriteAllBytesAsync(xmlPath, files.XmlBytes);
                    invoice.RawXmlPath = xmlPath;
                    updated = true;

                    // Nếu hóa đơn chỉ có chi tiết tạm, cập nhật danh sách mặt hàng thật từ XML Cổng Thuế
                    try
                    {
                        using var xmlMs = new MemoryStream(files.XmlBytes);
                        var xmlString = Encoding.UTF8.GetString(files.XmlBytes);
                        var parser = _parserFactory.GetParser(xmlString);
                        var parsed = await parser.ParseAsync(xmlMs, $"Gov_{invoice.InvoiceSymbol}_{invoice.InvoiceNumber}.xml");
                        if (parsed.Success && parsed.Details.Any())
                        {
                            invoice.Details.Clear();
                            foreach (var d in parsed.Details)
                            {
                                invoice.Details.Add(new InvoiceDetail
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
                            if (!string.IsNullOrEmpty(parsed.SellerAddress)) invoice.SellerAddress = parsed.SellerAddress;
                            if (!string.IsNullOrEmpty(parsed.BuyerAddress)) invoice.BuyerAddress = parsed.BuyerAddress;
                            if (!string.IsNullOrEmpty(parsed.TaxAuthorityCode)) invoice.TaxAuthorityCode = parsed.TaxAuthorityCode;
                            invoice.HasTaxCode = parsed.HasTaxCode;
                            updated = true;
                        }
                    }
                    catch (Exception exParse)
                    {
                        _logger.LogWarning(exParse, "Không thể parse chi tiết XML Cổng Thuế cho HĐ {Symbol}-{Number}", invoice.InvoiceSymbol, invoice.InvoiceNumber);
                    }
                }

                if (updated)
                {
                    await _db.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Không thể tải trực tiếp file gốc từ Cổng Thuế cho HĐ {Symbol}-{Number}", invoice.InvoiceSymbol, invoice.InvoiceNumber);
            }
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
