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
        private readonly IInvoiceFileService _invoiceFileService;
        private readonly ILogger<ExportController> _logger;

        public ExportController(
            ApplicationDbContext db,
            ITaxAccountContext taxAccountContext,
            IExportService exportService,
            IAuditLogService auditLog,
            IEnumerable<IInvoiceSourceProvider> providers,
            ICredentialProtector protector,
            IInvoiceParserFactory parserFactory,
            IInvoiceFileService invoiceFileService,
            ILogger<ExportController> logger)
        {
            _db = db;
            _taxAccountContext = taxAccountContext;
            _exportService = exportService;
            _auditLog = auditLog;
            _providers = providers;
            _protector = protector;
            _parserFactory = parserFactory;
            _invoiceFileService = invoiceFileService;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> ExcelDetailed(InvoiceFilterViewModel filter)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            var taxAccount = await _taxAccountContext.GetCurrentTaxAccountAsync();
            if (!taxAccountId.HasValue) return BadRequest("Chưa chọn tài khoản thuế.");

            var query = _db.Invoices
                .Include(i => i.Details)
                .Where(i => i.TaxAccountId == taxAccountId.Value);

            if (filter.FromDate.HasValue)
                query = query.Where(i => i.IssueDate >= filter.FromDate.Value.Date);
            if (filter.ToDate.HasValue)
                query = query.Where(i => i.IssueDate <= filter.ToDate.Value.Date.AddDays(1).AddTicks(-1));

            var invoices = await query.ToListAsync();

            var fileBytes = await _exportService.ExportFullAccountingWorkbookAsync(invoices, taxAccount, filter.FromDate, filter.ToDate);

            await _auditLog.LogActionAsync(
                "Xuất Excel Chi tiết", 
                $"{invoices.Count} hóa đơn", 
                $"Tổng tiền: {invoices.Sum(i => i.TotalAmount):N0} đ", 
                taxAccountId.Value);

            string fileName = $"BangKeChiTiet_{taxAccount?.TaxCode}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
            return File(fileBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }

        [HttpGet]
        public async Task<IActionResult> ExcelSummary(InvoiceFilterViewModel filter)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            var taxAccount = await _taxAccountContext.GetCurrentTaxAccountAsync();
            if (!taxAccountId.HasValue) return BadRequest("Chưa chọn tài khoản thuế.");

            var query = _db.Invoices
                .Include(i => i.Details)
                .Where(i => i.TaxAccountId == taxAccountId.Value);

            if (filter.FromDate.HasValue)
                query = query.Where(i => i.IssueDate >= filter.FromDate.Value.Date);
            if (filter.ToDate.HasValue)
                query = query.Where(i => i.IssueDate <= filter.ToDate.Value.Date.AddDays(1).AddTicks(-1));

            var invoices = await query.ToListAsync();

            var fileBytes = await _exportService.ExportFullAccountingWorkbookAsync(invoices, taxAccount, filter.FromDate, filter.ToDate);

            await _auditLog.LogActionAsync(
                "Xuất Excel 7 Sheet Kế Toán", 
                $"{invoices.Count} hóa đơn", 
                $"Đầy đủ 7 sheet (HĐ Vào, HĐ Ra, Bảng kê 8%, Bảng kê bán ra, Bảng kê mua vào, Chi tiết bán ra, Chi tiết mua vào), Tổng tiền: {invoices.Sum(i => i.TotalAmount):N0} đ", 
                taxAccountId.Value);

            string fileName = $"BaoCao_HoaDon_{taxAccount?.TaxCode}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
            return File(fileBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }

        [HttpGet]
        public async Task<IActionResult> ExcelFullMuaVao(InvoiceFilterViewModel filter)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            var taxAccount = await _taxAccountContext.GetCurrentTaxAccountAsync();
            if (!taxAccountId.HasValue) return BadRequest("Chưa chọn tài khoản thuế.");

            var query = _db.Invoices
                .Include(i => i.Details)
                .Where(i => i.TaxAccountId == taxAccountId.Value && i.InvoiceType != "BanRa");

            if (filter.FromDate.HasValue)
                query = query.Where(i => i.IssueDate >= filter.FromDate.Value.Date);
            if (filter.ToDate.HasValue)
                query = query.Where(i => i.IssueDate <= filter.ToDate.Value.Date.AddDays(1).AddTicks(-1));

            var invoices = await query.OrderBy(i => i.IssueDate).ThenBy(i => i.InvoiceNumber).ToListAsync();

            var fileBytes = await _exportService.ExportFullDetailExcelAsync(invoices, taxAccount, false, filter.FromDate, filter.ToDate);

            await _auditLog.LogActionAsync(
                "Xuất Excel Chi tiết Mua vào Đầy đủ", 
                $"{invoices.Count} hóa đơn", 
                $"Báo cáo chi tiết mua vào đầy đủ 41 cột thông tin, Tổng tiền: {invoices.Sum(i => i.TotalAmount):N0} đ", 
                taxAccountId.Value);

            string fileName = $"BaoCao_ChiTiet_MuaVao_DayDu_{taxAccount?.TaxCode}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
            return File(fileBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }

        [HttpGet]
        public async Task<IActionResult> ExcelFullBanRa(InvoiceFilterViewModel filter)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            var taxAccount = await _taxAccountContext.GetCurrentTaxAccountAsync();
            if (!taxAccountId.HasValue) return BadRequest("Chưa chọn tài khoản thuế.");

            var query = _db.Invoices
                .Include(i => i.Details)
                .Where(i => i.TaxAccountId == taxAccountId.Value && i.InvoiceType == "BanRa");

            if (filter.FromDate.HasValue)
                query = query.Where(i => i.IssueDate >= filter.FromDate.Value.Date);
            if (filter.ToDate.HasValue)
                query = query.Where(i => i.IssueDate <= filter.ToDate.Value.Date.AddDays(1).AddTicks(-1));

            var invoices = await query.OrderBy(i => i.IssueDate).ThenBy(i => i.InvoiceNumber).ToListAsync();

            var fileBytes = await _exportService.ExportFullDetailExcelAsync(invoices, taxAccount, true, filter.FromDate, filter.ToDate);

            await _auditLog.LogActionAsync(
                "Xuất Excel Chi tiết Bán ra Đầy đủ", 
                $"{invoices.Count} hóa đơn", 
                $"Báo cáo chi tiết bán ra đầy đủ 41 cột thông tin, Tổng tiền: {invoices.Sum(i => i.TotalAmount):N0} đ", 
                taxAccountId.Value);

            string fileName = $"BaoCao_ChiTiet_BanRa_DayDu_{taxAccount?.TaxCode}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
            return File(fileBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }

        [HttpGet]
        public async Task<IActionResult> ExcelSmartPro(InvoiceFilterViewModel filter)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            var taxAccount = await _taxAccountContext.GetCurrentTaxAccountAsync();
            if (!taxAccountId.HasValue) return BadRequest("Chưa chọn tài khoản thuế.");

            var query = _db.Invoices
                .Include(i => i.Details)
                .Where(i => i.TaxAccountId == taxAccountId.Value);

            if (filter.FromDate.HasValue)
                query = query.Where(i => i.IssueDate >= filter.FromDate.Value.Date);
            if (filter.ToDate.HasValue)
                query = query.Where(i => i.IssueDate <= filter.ToDate.Value.Date.AddDays(1).AddTicks(-1));

            var invoices = await query.ToListAsync();

            string title = $"SỔ KẾ TOÁN SMART PRO (KTSC)";
            var fileBytes = await _exportService.ExportSmartProExcelAsync(invoices, taxAccount, title);

            await _auditLog.LogActionAsync(
                "Xuất Excel Smart Pro (KTSC)", 
                $"{invoices.Count} hóa đơn", 
                $"Tổng tiền: {invoices.Sum(i => i.TotalAmount):N0} đ", 
                taxAccountId.Value);

            string fileName = $"Smart_KTSC_{taxAccount?.TaxCode}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
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

            var zipBytes = await CreateOriginalPdfsZipAsync(invoices, taxAccountId.Value);

            await _auditLog.LogActionAsync(
                "Tải PDF Gốc Hàng Loạt (ZIP)", 
                $"{invoices.Count} hóa đơn", 
                $"Tải file nén ZIP chứa các file PDF hóa đơn gốc nhà cung cấp", 
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

            var zipBytes = await CreateOriginalPdfsZipAsync(invoices, taxAccountId.Value);

            await _auditLog.LogActionAsync(
                "Tải PDF Gốc Hàng Loạt (ZIP)", 
                $"{invoices.Count} hóa đơn", 
                $"Tải bộ lọc theo tiêu chí", 
                taxAccountId.Value);

            string fileName = $"HoaDon_PDF_Goc_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
            return File(zipBytes, "application/zip", fileName);
        }

        private async Task<byte[]> CreateOriginalPdfsZipAsync(List<Invoice> invoices, int taxAccountId)
        {
            using var zipMs = new MemoryStream();
            using (var archive = new System.IO.Compression.ZipArchive(zipMs, System.IO.Compression.ZipArchiveMode.Create, true))
            {
                var entryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var inv in invoices)
                {
                    try
                    {
                        var pdfResult = await _invoiceFileService.GetOrFetchOriginalPdfAsync(inv.Id, taxAccountId);
                        byte[]? pdfBytes = (pdfResult.Success && pdfResult.PdfBytes != null && pdfResult.PdfBytes.Length > 0)
                            ? pdfResult.PdfBytes
                            : null;

                        if (pdfBytes == null)
                        {
                            pdfBytes = await _exportService.GenerateInvoicePdfAsync(inv);
                        }

                        if (pdfBytes != null && pdfBytes.Length > 0)
                        {
                            var entryName = $"HD_{inv.InvoiceSymbol}_{inv.InvoiceNumber}_{inv.SellerTaxCode}.pdf";
                            if (entryNames.Contains(entryName))
                            {
                                entryName = $"HD_{inv.InvoiceSymbol}_{inv.InvoiceNumber}_{inv.SellerTaxCode}_{inv.Id}.pdf";
                            }
                            entryNames.Add(entryName);

                            var entry = archive.CreateEntry(entryName, System.IO.Compression.CompressionLevel.Optimal);
                            using var entryStream = entry.Open();
                            await entryStream.WriteAsync(pdfBytes, 0, pdfBytes.Length);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Lỗi đóng gói PDF cho HĐ {Symbol}-{Number}", inv.InvoiceSymbol, inv.InvoiceNumber);
                    }
                }
            }
            return zipMs.ToArray();
        }

        [HttpGet]
        public async Task<IActionResult> DownloadPdf(int id)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            if (!taxAccountId.HasValue)
            {
                return StatusCode(401, new
                {
                    success = false,
                    errorCode = "NO_TAX_ACCOUNT",
                    message = "Chưa chọn tài khoản thuế."
                });
            }

            var result = await _invoiceFileService.GetOrFetchOriginalPdfAsync(id, taxAccountId.Value);
            if (!result.Success || result.PdfBytes == null || result.PdfBytes.Length == 0)
            {
                int statusCode = result.HttpStatusCode > 0 ? result.HttpStatusCode : 404;
                return StatusCode(statusCode, new
                {
                    success = false,
                    errorCode = result.ErrorCode,
                    message = result.ErrorMessage,
                    endpoint = result.EndpointCalled,
                    gdtResponse = result.RawGdtResponseSnippet
                });
            }

            await _auditLog.LogActionAsync("Tải PDF hóa đơn gốc", result.FileName, $"Nguồn: {result.Source}", taxAccountId.Value);
            return File(result.PdfBytes, "application/pdf", result.FileName);
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
                    BuyerTaxCode = invoice.BuyerTaxCode ?? string.Empty
                };

                var dlResult = await provider.DownloadAllInvoiceFilesAsync(remoteInv, token);
                if (dlResult.Success && dlResult.Files.Any())
                {
                    await _invoiceFileService.SaveDownloadedFilesAsync(invoice.Id, invoice.TaxAccountId, dlResult);
                }

                var files = (XmlBytes: dlResult.XmlFile?.Content, PdfBytes: dlResult.PdfFile?.Content);
                bool updated = false;

                if (files.XmlBytes != null && files.XmlBytes.Length > 0)
                {
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
