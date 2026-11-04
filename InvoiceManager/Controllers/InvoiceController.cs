using InvoiceManager.Data;
using InvoiceManager.Models.Entities;
using InvoiceManager.Models.ViewModels;
using InvoiceManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InvoiceManager.Controllers
{
    [Authorize]
    public class InvoiceController : Controller
    {
        private readonly ApplicationDbContext _db;
        private readonly ITaxAccountContext _taxAccountContext;
        private readonly IAuditLogService _auditLog;
        private readonly IExportService _exportService;
        private readonly ISyncService _syncService;
        private readonly IInvoiceFileService _invoiceFileService;
        private readonly IOriginalInvoiceConfigService? _origConfigService;

        public InvoiceController(
            ApplicationDbContext db,
            ITaxAccountContext taxAccountContext,
            IAuditLogService auditLog,
            IExportService exportService,
            ISyncService syncService,
            IInvoiceFileService invoiceFileService,
            IOriginalInvoiceConfigService? origConfigService = null)
        {
            _db = db;
            _taxAccountContext = taxAccountContext;
            _auditLog = auditLog;
            _exportService = exportService;
            _syncService = syncService;
            _invoiceFileService = invoiceFileService;
            _origConfigService = origConfigService;
        }

        public async Task<IActionResult> Index(InvoiceFilterViewModel filter)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            var currentTaxAccount = await _taxAccountContext.GetCurrentTaxAccountAsync();
            var accessibleAccounts = await _taxAccountContext.GetAccessibleTaxAccountsAsync();

            if (!taxAccountId.HasValue)
            {
                return RedirectToAction("Index", "TaxAccount");
            }

            // Thiết lập khoảng ngày mặc định nếu chưa chọn
            SetupDateRange(filter);

            // Query cơ sở theo TaxAccountId, InvoiceType, Ngày và Keyword để tính số liệu tổng hợp ổn định cho toàn bộ kỳ lọc
            var baseQuery = _db.Invoices
                .Where(i => i.TaxAccountId == taxAccountId.Value);

            // 1. Lọc theo Loại hóa đơn (Mua vào / Bán ra)
            if (!string.IsNullOrEmpty(filter.InvoiceType))
            {
                baseQuery = baseQuery.Where(i => i.InvoiceType == filter.InvoiceType);
            }

            // 2. Lọc theo Ngày lập
            if (filter.FromDate.HasValue)
            {
                var from = filter.FromDate.Value.Date;
                baseQuery = baseQuery.Where(i => i.IssueDate >= from);
            }
            if (filter.ToDate.HasValue)
            {
                var to = filter.ToDate.Value.Date.AddDays(1).AddTicks(-1);
                baseQuery = baseQuery.Where(i => i.IssueDate <= to);
            }

            // 3. Tìm kiếm từ khóa (Ký hiệu, Số HĐ, MST người bán, Tên người bán)
            if (!string.IsNullOrWhiteSpace(filter.Keyword))
            {
                var kw = filter.Keyword.Trim();
                baseQuery = baseQuery.Where(i => i.InvoiceSymbol.Contains(kw) 
                                      || i.InvoiceNumber.Contains(kw) 
                                      || i.SellerTaxCode.Contains(kw) 
                                      || i.SellerName.Contains(kw));
            }

            // Tính toán tổng hợp số liệu trực tiếp trên tập dữ liệu của kỳ lọc (Tương thích 100% cả SQL Server và SQLite)
            var stats = await baseQuery
                .Select(i => new
                {
                    i.AmountBeforeTax,
                    i.TaxAmount,
                    i.TotalAmount,
                    i.HasTaxCode,
                    i.IsCashRegister
                })
                .ToListAsync();

            var summary = new InvoiceSummaryViewModel
            {
                TotalCount = stats.Count,
                WithTaxCodeCount = stats.Count(i => i.HasTaxCode && !i.IsCashRegister),
                WithoutTaxCodeCount = stats.Count(i => !i.HasTaxCode && !i.IsCashRegister),
                CashRegisterCount = stats.Count(i => i.IsCashRegister),
                TotalAmountBeforeTax = stats.Sum(i => i.AmountBeforeTax),
                TotalTaxAmount = stats.Sum(i => i.TaxAmount),
                TotalAmount = stats.Sum(i => i.TotalAmount)
            };

            // Query chi tiết áp dụng thêm các tiêu chí lọc danh sách (CQT, MTT, Trạng thái, Bộ lọc chuyên sâu...)
            IQueryable<Invoice> query = baseQuery.Include(i => i.Details).Include(i => i.Files);

            // 4. Lọc theo MST người bán cụ thể
            if (!string.IsNullOrWhiteSpace(filter.SellerTaxCode))
            {
                var stc = filter.SellerTaxCode.Trim();
                query = query.Where(i => i.SellerTaxCode == stc);
            }

            // Lọc theo Tên người bán
            if (!string.IsNullOrWhiteSpace(filter.SellerName))
            {
                var sn = filter.SellerName.Trim();
                query = query.Where(i => i.SellerName.Contains(sn));
            }

            // Lọc theo Ký hiệu hóa đơn
            if (!string.IsNullOrWhiteSpace(filter.InvoiceSymbol))
            {
                var sym = filter.InvoiceSymbol.Trim();
                query = query.Where(i => i.InvoiceSymbol.Contains(sym));
            }

            // Lọc theo Số hóa đơn
            if (!string.IsNullOrWhiteSpace(filter.InvoiceNumber))
            {
                var num = filter.InvoiceNumber.Trim();
                query = query.Where(i => i.InvoiceNumber.Contains(num));
            }

            // Ký hiệu mẫu số hóa đơn (ký tự đầu tiên của ký hiệu mẫu số: 1, 2, 3...)
            if (!string.IsNullOrWhiteSpace(filter.TemplateType))
            {
                var tt = filter.TemplateType.Trim();
                query = query.Where(i => i.InvoiceSymbol.StartsWith(tt));
            }

            // Lọc theo Trạng thái tải file (PDF, XML)
            if (!string.IsNullOrWhiteSpace(filter.FileDownloadStatus))
            {
                switch (filter.FileDownloadStatus)
                {
                    case "HasPdf":
                        query = query.Where(i => (i.RawPdfPath != null && i.RawPdfPath != "") || i.Files.Any(f => f.FileExtension == ".pdf"));
                        break;
                    case "MissingPdf":
                        query = query.Where(i => (i.RawPdfPath == null || i.RawPdfPath == "") && !i.Files.Any(f => f.FileExtension == ".pdf"));
                        break;
                    case "HasXml":
                        query = query.Where(i => (i.RawXmlPath != null && i.RawXmlPath != "") || i.Files.Any(f => f.FileExtension == ".xml"));
                        break;
                    case "MissingXml":
                        query = query.Where(i => (i.RawXmlPath == null || i.RawXmlPath == "") && !i.Files.Any(f => f.FileExtension == ".xml"));
                        break;
                    case "HasBoth":
                        query = query.Where(i => ((i.RawPdfPath != null && i.RawPdfPath != "") || i.Files.Any(f => f.FileExtension == ".pdf"))
                                              && ((i.RawXmlPath != null && i.RawXmlPath != "") || i.Files.Any(f => f.FileExtension == ".xml")));
                        break;
                }
            }

            // Lọc theo Kết quả kiểm tra
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

            // 5. Lọc theo Có mã / Không mã CQT
            if (filter.HasTaxCode.HasValue)
            {
                query = query.Where(i => i.HasTaxCode == filter.HasTaxCode.Value);
            }

            // 6. Lọc theo Máy tính tiền
            if (filter.IsCashRegister.HasValue)
            {
                query = query.Where(i => i.IsCashRegister == filter.IsCashRegister.Value);
            }

            // 7. Lọc theo Trạng thái
            if (!string.IsNullOrWhiteSpace(filter.Status))
            {
                query = query.Where(i => i.Status == filter.Status);
            }

            // 8. Lọc theo Khoảng tiền
            if (filter.MinAmount.HasValue)
            {
                query = query.Where(i => i.TotalAmount >= filter.MinAmount.Value);
            }
            if (filter.MaxAmount.HasValue)
            {
                query = query.Where(i => i.TotalAmount <= filter.MaxAmount.Value);
            }

            // 9. Lọc theo Đối soát ghi sổ
            if (filter.IsReconciled.HasValue)
            {
                query = query.Where(i => i.IsReconciled == filter.IsReconciled.Value);
            }

            // Lấy thời điểm đồng bộ gần nhất
            var lastSync = await _db.SyncLogs
                .Where(s => s.TaxAccountId == taxAccountId.Value)
                .OrderByDescending(s => s.SyncedAt)
                .Select(s => (DateTime?)s.SyncedAt)
                .FirstOrDefaultAsync();
            summary.LastSyncedAt = lastSync;

            // Sắp xếp linh hoạt theo ngày hoặc theo các chỉ số tài chính (Chưa thuế, Tiền thuế GTGT, Tổng thanh toán)
            // Lưu ý: SQLite không hỗ trợ ORDER BY trực tiếp trên kiểu decimal, cần cast sang double
            if (_db.Database.IsSqlite())
            {
                query = filter.SortBy switch
                {
                    "DateAsc" => query.OrderBy(i => i.IssueDate),
                    "AmountBeforeTaxDesc" => query.OrderByDescending(i => (double)i.AmountBeforeTax),
                    "AmountBeforeTaxAsc" => query.OrderBy(i => (double)i.AmountBeforeTax),
                    "TaxAmountDesc" => query.OrderByDescending(i => (double)i.TaxAmount),
                    "TaxAmountAsc" => query.OrderBy(i => (double)i.TaxAmount),
                    "TotalAmountDesc" or "AmountDesc" => query.OrderByDescending(i => (double)i.TotalAmount),
                    "TotalAmountAsc" or "AmountAsc" => query.OrderBy(i => (double)i.TotalAmount),
                    "NumberAsc" => query.OrderBy(i => i.InvoiceNumber),
                    "NumberDesc" => query.OrderByDescending(i => i.InvoiceNumber),
                    _ => query.OrderByDescending(i => i.IssueDate)
                };
            }
            else
            {
                query = filter.SortBy switch
                {
                    "DateAsc" => query.OrderBy(i => i.IssueDate),
                    "AmountBeforeTaxDesc" => query.OrderByDescending(i => i.AmountBeforeTax),
                    "AmountBeforeTaxAsc" => query.OrderBy(i => i.AmountBeforeTax),
                    "TaxAmountDesc" => query.OrderByDescending(i => i.TaxAmount),
                    "TaxAmountAsc" => query.OrderBy(i => i.TaxAmount),
                    "TotalAmountDesc" or "AmountDesc" => query.OrderByDescending(i => i.TotalAmount),
                    "TotalAmountAsc" or "AmountAsc" => query.OrderBy(i => i.TotalAmount),
                    "NumberAsc" => query.OrderBy(i => i.InvoiceNumber),
                    "NumberDesc" => query.OrderByDescending(i => i.InvoiceNumber),
                    _ => query.OrderByDescending(i => i.IssueDate)
                };
            }

            // Phân trang trên tập dữ liệu đã áp dụng bộ lọc nhánh
            int totalItems = await query.CountAsync();
            int page = filter.Page > 0 ? filter.Page : 1;
            int pageSize = filter.PageSize > 0 ? filter.PageSize : 15;
            int totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);

            var invoices = await query
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            var viewModel = new InvoiceIndexViewModel
            {
                Filter = filter,
                Summary = summary,
                Invoices = invoices,
                TotalItems = totalItems,
                TotalPages = totalPages,
                CurrentPage = page,
                CurrentTaxAccount = currentTaxAccount,
                AccessibleTaxAccounts = accessibleAccounts
            };

            return View(viewModel);
        }

        [HttpGet]
        public async Task<IActionResult> Detail(int id)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            var invoice = await _db.Invoices
                .Include(i => i.Details)
                .Include(i => i.Files)
                .Include(i => i.Files)
                .FirstOrDefaultAsync(i => i.Id == id && i.TaxAccountId == taxAccountId);

            if (invoice == null) return NotFound("Không tìm thấy hóa đơn hoặc bạn không có quyền truy cập.");

            await _auditLog.LogActionAsync("Xem chi tiết HĐ", $"{invoice.InvoiceSymbol}-{invoice.InvoiceNumber}", $"Số tiền: {invoice.TotalAmount:N0} đ", taxAccountId);
            return PartialView("_InvoiceDetailPartial", invoice);
        }

        [HttpGet]
        public async Task<IActionResult> ViewHtml(int id)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            var invoice = await _db.Invoices
                .Include(i => i.Details)
                .Include(i => i.Files)
                .FirstOrDefaultAsync(i => i.Id == id && i.TaxAccountId == taxAccountId);

            if (invoice == null) return NotFound("Không tìm thấy hóa đơn.");

            await _auditLog.LogActionAsync("Xem bản thể hiện HTML", $"{invoice.InvoiceSymbol}-{invoice.InvoiceNumber}", null, taxAccountId);
            // Ưu tiên bản thể hiện HTML gốc từ gói CQT / Nhà cung cấp
            var officialHtmlFile = invoice.Files
                .FirstOrDefault(f => string.Equals(f.FileExtension, ".html", StringComparison.OrdinalIgnoreCase)
                                     && !string.IsNullOrWhiteSpace(f.FilePath)
                                     && System.IO.File.Exists(f.FilePath));
            if (officialHtmlFile != null)
            {
                var htmlContent = await System.IO.File.ReadAllTextAsync(officialHtmlFile.FilePath, Encoding.UTF8);
                return Content(htmlContent, "text/html; charset=utf-8");
            }

            var html = _exportService.GenerateInvoiceHtml(invoice);
            return Content(html, "text/html; charset=utf-8");
        }

        [HttpGet]
        public async Task<IActionResult> ViewRawXml(int id)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            var invoice = await _db.Invoices
                .Include(i => i.Files)
                .FirstOrDefaultAsync(i => i.Id == id && i.TaxAccountId == taxAccountId);

            if (invoice == null) return NotFound("Không tìm thấy hóa đơn.");

            var xmlFile = invoice.Files.FirstOrDefault(f => f.FileExtension == ".xml" && System.IO.File.Exists(f.FilePath));
            string xmlContent = "";
            if (xmlFile != null)
            {
                xmlContent = await System.IO.File.ReadAllTextAsync(xmlFile.FilePath, Encoding.UTF8);
            }
            else if (!string.IsNullOrEmpty(invoice.RawXmlPath) && System.IO.File.Exists(invoice.RawXmlPath))
            {
                xmlContent = await System.IO.File.ReadAllTextAsync(invoice.RawXmlPath, Encoding.UTF8);
            }
            else
            {
                return Content("<?xml version=\"1.0\" encoding=\"utf-8\"?><Notice>Hóa đơn này chưa có tệp XML gốc từ Tổng cục Thuế. Vui lòng bấm 'Lấy tệp từ Cổng Thuế' trong chi tiết hóa đơn để hệ thống tải bản gốc.</Notice>", "application/xml; charset=utf-8");
            }

            await _auditLog.LogActionAsync("Xem XML gốc", $"{invoice.InvoiceSymbol}-{invoice.InvoiceNumber}", null, taxAccountId);
            return Content(xmlContent, "application/xml; charset=utf-8");
        }

        [HttpGet]
        public async Task<IActionResult> DownloadRawXml(int id)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            var invoice = await _db.Invoices
                .Include(i => i.Files)
                .FirstOrDefaultAsync(i => i.Id == id && i.TaxAccountId == taxAccountId);

            if (invoice == null) return NotFound("Không tìm thấy hóa đơn.");

            var xmlFile = invoice.Files.FirstOrDefault(f => f.FileExtension == ".xml" && System.IO.File.Exists(f.FilePath));
            if (xmlFile != null)
            {
                var bytes = await System.IO.File.ReadAllBytesAsync(xmlFile.FilePath);
                await _auditLog.LogActionAsync("Tải XML gốc", $"{invoice.InvoiceSymbol}-{invoice.InvoiceNumber}", null, taxAccountId);
                return File(bytes, "application/xml", xmlFile.OriginalFileName);
            }

            if (!string.IsNullOrEmpty(invoice.RawXmlPath) && System.IO.File.Exists(invoice.RawXmlPath))
            {
                var bytes = await System.IO.File.ReadAllBytesAsync(invoice.RawXmlPath);
                await _auditLog.LogActionAsync("Tải XML gốc", $"{invoice.InvoiceSymbol}-{invoice.InvoiceNumber}", null, taxAccountId);
                string fileName = $"HD_{invoice.InvoiceSymbol}_{invoice.InvoiceNumber}.xml";
                return File(bytes, "application/xml", fileName);
            }

            return NotFound("Hóa đơn chưa có tệp XML gốc từ Tổng cục Thuế. Vui lòng bấm 'Lấy tệp từ Cổng Thuế' trong chi tiết hóa đơn để tải về.");
        }

        [HttpGet]
        public async Task<IActionResult> DownloadFile(int id)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            if (!taxAccountId.HasValue) return Unauthorized();

            var result = await _invoiceFileService.GetFileForDownloadAsync(id, taxAccountId.Value);
            if (result == null) return NotFound("Tệp không tồn tại hoặc bạn không có quyền truy cập.");

            await _auditLog.LogActionAsync("Tải tệp gốc", result.Value.DownloadFileName, null, taxAccountId.Value);
            return File(result.Value.Stream, result.Value.ContentType, result.Value.DownloadFileName);
        }

        [HttpGet]
        public async Task<IActionResult> ViewFile(int id)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            if (!taxAccountId.HasValue) return Unauthorized();

            var result = await _invoiceFileService.GetFileForDownloadAsync(id, taxAccountId.Value);
            if (result == null) return NotFound("Tệp không tồn tại hoặc bạn không có quyền truy cập.");

            await _auditLog.LogActionAsync("Xem tệp gốc", result.Value.DownloadFileName, null, taxAccountId.Value);
            Response.Headers.ContentDisposition = "inline; filename=\"" + Uri.EscapeDataString(result.Value.DownloadFileName) + "\"";
            return File(result.Value.Stream, result.Value.ContentType);
        }

        [HttpGet]
        public async Task<IActionResult> DownloadAllFiles(int invoiceId)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            if (!taxAccountId.HasValue) return Unauthorized();

            var result = await _invoiceFileService.DownloadAllFilesAsZipAsync(invoiceId, taxAccountId.Value);
            if (result == null) return NotFound("Hóa đơn này chưa có tệp gốc nào được lưu trên hệ thống.");

            await _auditLog.LogActionAsync("Tải toàn bộ tệp gốc (ZIP)", result.Value.ZipFileName, null, taxAccountId.Value);
            return File(result.Value.ZipBytes, "application/zip", result.Value.ZipFileName);
        }

        [HttpPost]
        public async Task<IActionResult> SyncInvoiceFiles(int invoiceId)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            if (!taxAccountId.HasValue) return Json(new { success = false, message = "Phiên làm việc hết hạn." });

            var result = await _invoiceFileService.SyncInvoiceFilesFromGdtAsync(invoiceId, taxAccountId.Value);
            return Json(new { success = result.Success, message = result.Message, count = result.FilesCount });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadOriginalPdf(int id, IFormFile pdfFile)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            if (!taxAccountId.HasValue) return Json(new { success = false, message = "Chưa chọn tài khoản thuế." });
            var invoice = await _db.Invoices.FirstOrDefaultAsync(i => i.Id == id && i.TaxAccountId == taxAccountId.Value);
            if (invoice == null) return Json(new { success = false, message = "Không tìm thấy hóa đơn." });

            if (pdfFile == null || pdfFile.Length == 0)
            {
                return Json(new { success = false, message = "Vui lòng chọn file PDF." });
            }

            var ext = Path.GetExtension(pdfFile.FileName).ToLowerInvariant();
            if (ext != ".pdf")
            {
                return Json(new { success = false, message = "Chỉ chấp nhận file định dạng .pdf." });
            }

            using var ms = new MemoryStream();
            await pdfFile.CopyToAsync(ms);
            var bytes = ms.ToArray();

            await _invoiceFileService.SaveSingleFileAsync(
                id,
                taxAccountId.Value,
                pdfFile.FileName,
                ".pdf",
                "application/pdf",
                bytes,
                "UploadOriginal");

            await _auditLog.LogActionAsync("Đính kèm PDF gốc", $"{invoice.InvoiceSymbol}-{invoice.InvoiceNumber}", $"Lưu file {pdfFile.FileName}", taxAccountId);

            return Json(new { success = true, message = "Đã lưu trữ file PDF gốc thành công." });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleReconciliation(int id, string? refNo)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            var invoice = await _db.Invoices.FirstOrDefaultAsync(i => i.Id == id && i.TaxAccountId == taxAccountId);
            if (invoice == null) return Json(new { success = false, message = "Không tìm thấy hóa đơn." });

            invoice.IsReconciled = !invoice.IsReconciled;
            if (invoice.IsReconciled)
            {
                invoice.ReconciledRefNo = string.IsNullOrWhiteSpace(refNo) ? $"PC-{DateTime.Now:yyyyMMdd}-{invoice.Id}" : refNo.Trim();
                invoice.ReconciledAt = DateTime.Now;
            }
            else
            {
                invoice.ReconciledRefNo = null;
                invoice.ReconciledAt = null;
            }

            await _db.SaveChangesAsync();
            await _auditLog.LogActionAsync("Đối soát kế toán", $"{invoice.InvoiceSymbol}-{invoice.InvoiceNumber}", $"Trạng thái: {(invoice.IsReconciled ? "Đã vào sổ (" + invoice.ReconciledRefNo + ")" : "Hủy vào sổ")}", taxAccountId);

            return Json(new { success = true, isReconciled = invoice.IsReconciled, refNo = invoice.ReconciledRefNo });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SyncNow()
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            if (!taxAccountId.HasValue) return Json(new { success = false, message = "Chưa chọn tài khoản thuế." });

            var log = await _syncService.SyncInvoicesForTaxAccountAsync(taxAccountId.Value, "Thủ công (Nút Đồng bộ)");
            await _auditLog.LogActionAsync("Đồng bộ hóa đơn", $"MST ID {taxAccountId.Value}", log.ErrorMessage, taxAccountId.Value);

            TempData["SuccessMessage"] = $"Đồng bộ hoàn tất: {log.ErrorMessage}";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            if (!taxAccountId.HasValue) return Json(new { success = false, message = "Chưa chọn tài khoản thuế." });

            var invoice = await _db.Invoices
                .Include(i => i.Details)
                .Include(i => i.Files)
                .FirstOrDefaultAsync(i => i.Id == id && i.TaxAccountId == taxAccountId.Value);

            if (invoice == null) return Json(new { success = false, message = "Không tìm thấy hóa đơn cần xóa hoặc bạn không có quyền." });

            if (!string.IsNullOrEmpty(invoice.RawXmlPath) && System.IO.File.Exists(invoice.RawXmlPath))
            {
                try { System.IO.File.Delete(invoice.RawXmlPath); } catch { }
            }
            if (!string.IsNullOrEmpty(invoice.RawPdfPath) && System.IO.File.Exists(invoice.RawPdfPath))
            {
                try { System.IO.File.Delete(invoice.RawPdfPath); } catch { }
            }

            _db.Invoices.Remove(invoice);
            await _db.SaveChangesAsync();

            await _auditLog.LogActionAsync("Xóa hóa đơn", $"{invoice.InvoiceSymbol}-{invoice.InvoiceNumber}", $"MST: {invoice.SellerTaxCode}, Tiền: {invoice.TotalAmount:N0} đ, Nguồn: {invoice.SourceProvider}", taxAccountId.Value);

            return Json(new { success = true, message = $"Đã xóa thành công hóa đơn {invoice.InvoiceSymbol}-{invoice.InvoiceNumber}." });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteBatch([FromBody] int[] ids)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            if (!taxAccountId.HasValue) return Json(new { success = false, message = "Chưa chọn tài khoản thuế." });

            if (ids == null || ids.Length == 0) return Json(new { success = false, message = "Chưa chọn hóa đơn nào để xóa." });

            var invoices = await _db.Invoices
                .Include(i => i.Details)
                .Include(i => i.Files)
                .Where(i => ids.Contains(i.Id) && i.TaxAccountId == taxAccountId.Value)
                .ToListAsync();

            if (!invoices.Any()) return Json(new { success = false, message = "Không tìm thấy hóa đơn hợp lệ để xóa." });

            foreach (var invoice in invoices)
            {
                if (!string.IsNullOrEmpty(invoice.RawXmlPath) && System.IO.File.Exists(invoice.RawXmlPath))
                {
                    try { System.IO.File.Delete(invoice.RawXmlPath); } catch { }
                }
                if (!string.IsNullOrEmpty(invoice.RawPdfPath) && System.IO.File.Exists(invoice.RawPdfPath))
                {
                    try { System.IO.File.Delete(invoice.RawPdfPath); } catch { }
                }
            }

            _db.Invoices.RemoveRange(invoices);
            await _db.SaveChangesAsync();

            await _auditLog.LogActionAsync("Xóa hóa đơn hàng loạt", $"{invoices.Count} hóa đơn", $"Đã xóa {invoices.Count} hóa đơn được chọn", taxAccountId.Value);

            return Json(new { success = true, message = $"Đã xóa thành công {invoices.Count} hóa đơn đã chọn." });
        }

        private void SetupDateRange(InvoiceFilterViewModel filter)
        {
            var now = DateTime.Now;

            // 1. ƯU TIÊN HÀNG ĐẦU: Nếu người dùng đã chọn Từ ngày hoặc Đến ngày (Custom Date Range)
            if (filter.FromDate.HasValue || filter.ToDate.HasValue)
            {
                // Kiểm tra xem khoảng ngày người dùng chọn có khớp với mốc nhanh nào không để tô màu nút
                if (filter.FromDate.HasValue && filter.ToDate.HasValue)
                {
                    var f = filter.FromDate.Value.Date;
                    var t = filter.ToDate.Value.Date;

                    if (f == now.Date && t == now.Date)
                    {
                        filter.QuickDateRange = "today";
                    }
                    else if (f == new DateTime(now.Year, now.Month, 1) && t == new DateTime(now.Year, now.Month, 1).AddMonths(1).AddDays(-1))
                    {
                        filter.QuickDateRange = "thisMonth";
                    }
                    else if (f == new DateTime(now.AddMonths(-1).Year, now.AddMonths(-1).Month, 1) && t == new DateTime(now.Year, now.Month, 1).AddDays(-1))
                    {
                        filter.QuickDateRange = "lastMonth";
                    }
                    else if (f == new DateTime(now.Year, ((now.Month - 1) / 3) * 3 + 1, 1) && t == new DateTime(now.Year, ((now.Month - 1) / 3) * 3 + 1, 1).AddMonths(3).AddDays(-1))
                    {
                        filter.QuickDateRange = "thisQuarter";
                    }
                    else if (f == new DateTime(now.Year, 1, 1) && t == new DateTime(now.Year, 12, 31))
                    {
                        filter.QuickDateRange = "thisYear";
                    }
                    else
                    {
                        filter.QuickDateRange = "custom";
                    }
                }
                else
                {
                    filter.QuickDateRange = "custom";
                }

                // Tuyệt đối giữ nguyên khoảng ngày người dùng đã chọn, không ghi đè!
                return;
            }

            // 2. Nếu người dùng chọn mốc thời gian nhanh (Hôm nay, Tháng này, Tháng trước, Quý này, Năm nay)
            if (!string.IsNullOrEmpty(filter.QuickDateRange) && filter.QuickDateRange != "custom")
            {
                switch (filter.QuickDateRange)
                {
                    case "today":
                        filter.FromDate = now.Date;
                        filter.ToDate = now.Date;
                        break;
                    case "thisMonth":
                        filter.FromDate = new DateTime(now.Year, now.Month, 1);
                        filter.ToDate = filter.FromDate.Value.AddMonths(1).AddDays(-1);
                        break;
                    case "lastMonth":
                        var prevMonth = now.AddMonths(-1);
                        filter.FromDate = new DateTime(prevMonth.Year, prevMonth.Month, 1);
                        filter.ToDate = filter.FromDate.Value.AddMonths(1).AddDays(-1);
                        break;
                    case "thisQuarter":
                        int quarter = (now.Month - 1) / 3 + 1;
                        filter.FromDate = new DateTime(now.Year, (quarter - 1) * 3 + 1, 1);
                        filter.ToDate = filter.FromDate.Value.AddMonths(3).AddDays(-1);
                        break;
                    case "thisYear":
                        filter.FromDate = new DateTime(now.Year, 1, 1);
                        filter.ToDate = new DateTime(now.Year, 12, 31);
                        break;
                    default:
                        filter.QuickDateRange = "thisMonth";
                        filter.FromDate = new DateTime(now.Year, now.Month, 1);
                        filter.ToDate = filter.FromDate.Value.AddMonths(1).AddDays(-1);
                        break;
                }
            }
            else
            {
                // Mặc định tháng hiện tại khi mới truy cập trang
                filter.QuickDateRange = "thisMonth";
                filter.FromDate = new DateTime(now.Year, now.Month, 1);
                filter.ToDate = filter.FromDate.Value.AddMonths(1).AddDays(-1);
            }
        }

        [HttpGet("/api/invoices/{id:int}/original-pdf")]
        public async Task<IActionResult> GetOriginalPdf(int id, System.Threading.CancellationToken ct = default)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            if (!taxAccountId.HasValue)
            {
                return StatusCode(401, new
                {
                    success = false,
                    errorCode = "NO_TAX_ACCOUNT",
                    message = "Chưa chọn tài khoản thuế làm việc hoặc phiên đăng nhập đã hết hạn."
                });
            }

            var result = await _invoiceFileService.GetOrFetchOriginalPdfAsync(id, taxAccountId.Value, null, ct);
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

            await _auditLog.LogActionAsync(
                "Xem PDF gốc",
                result.FileName,
                $"Nguồn: {result.Source}, Dung lượng: {result.PdfBytes.Length:N0} bytes",
                taxAccountId.Value);

            Response.Headers["X-Original-Pdf-Source"] = result.Source;
            if (!string.IsNullOrWhiteSpace(result.Sha256Hash))
            {
                Response.Headers["X-Original-Pdf-Sha256"] = result.Sha256Hash;
            }
            Response.Headers.ContentDisposition = "inline; filename=\"" + Uri.EscapeDataString(result.FileName) + "\"";
            return File(result.PdfBytes, "application/pdf");
        }

        [HttpGet("/api/invoices/{id:int}/original-pdf/download")]
        public async Task<IActionResult> DownloadOriginalPdf(int id, System.Threading.CancellationToken ct = default)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            if (!taxAccountId.HasValue)
            {
                return StatusCode(401, new
                {
                    success = false,
                    errorCode = "NO_TAX_ACCOUNT",
                    message = "Chưa chọn tài khoản thuế làm việc hoặc phiên đăng nhập đã hết hạn."
                });
            }

            var result = await _invoiceFileService.GetOrFetchOriginalPdfAsync(id, taxAccountId.Value, null, ct);
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

            await _auditLog.LogActionAsync(
                "Tải PDF gốc",
                result.FileName,
                $"Nguồn: {result.Source}, Dung lượng: {result.PdfBytes.Length:N0} bytes",
                taxAccountId.Value);

            Response.Headers["X-Original-Pdf-Source"] = result.Source;
            if (!string.IsNullOrWhiteSpace(result.Sha256Hash))
            {
                Response.Headers["X-Original-Pdf-Sha256"] = result.Sha256Hash;
            }
            return File(result.PdfBytes, "application/pdf", result.FileName);
        }

        [HttpGet]
        public Task<IActionResult> ViewInvoicePdf(int id, System.Threading.CancellationToken ct = default)
        {
            return GetOriginalPdf(id, ct);
        }
    
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ProcessSingleOriginalDownload(
            int invoiceId,
            bool forceReDownload = false,
            System.Threading.CancellationToken ct = default)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            if (!taxAccountId.HasValue)
            {
                return Json(new BatchOriginalDownloadItemResult
                {
                    InvoiceId = invoiceId,
                    Status = "Thất bại",
                    ErrorMessage = "Chưa chọn tài khoản thuế hoặc phiên đăng nhập đã hết hạn.",
                    IsSuccess = false
                });
            }

            var config = (_origConfigService != null)
                ? await _origConfigService.GetConfigAsync(taxAccountId.Value, ct)
                : new OriginalInvoiceConfigModel();

            var inv = await _db.Invoices
                .Include(i => i.TaxAccount)
                .Include(i => i.Files)
                .FirstOrDefaultAsync(i => i.Id == invoiceId && i.TaxAccountId == taxAccountId.Value, ct);

            if (inv == null)
            {
                return Json(new BatchOriginalDownloadItemResult
                {
                    InvoiceId = invoiceId,
                    Status = "Thất bại",
                    ErrorMessage = "Không tìm thấy hóa đơn trong hệ thống.",
                    IsSuccess = false
                });
            }

            var itemResult = new BatchOriginalDownloadItemResult
            {
                InvoiceId = inv.Id,
                InvoiceNumber = inv.InvoiceNumber,
                InvoiceSymbol = inv.InvoiceSymbol,
                SellerTaxCode = inv.SellerTaxCode,
                SellerName = inv.SellerName,
                ProcessedAt = DateTime.Now
            };

            // 1. Kiểm tra nếu đã tồn tại file hợp lệ và cấu hình bỏ qua nếu đã có
            var existingPdf = inv.Files.FirstOrDefault(f => string.Equals(f.FileExtension, ".pdf", StringComparison.OrdinalIgnoreCase) && f.Status == "Valid" && System.IO.File.Exists(f.FilePath));
            var existingXml = inv.Files.FirstOrDefault(f => string.Equals(f.FileExtension, ".xml", StringComparison.OrdinalIgnoreCase) && f.Status == "Valid" && System.IO.File.Exists(f.FilePath));
            if (existingXml == null && !string.IsNullOrEmpty(inv.RawXmlPath) && System.IO.File.Exists(inv.RawXmlPath))
            {
                existingXml = new InvoiceFile { FilePath = inv.RawXmlPath };
            }

            if (config.SkipExistingFiles && !forceReDownload && (existingPdf != null || existingXml != null))
            {
                var formatsList = new System.Collections.Generic.List<string>();
                if (existingXml != null) formatsList.Add("XML");
                if (existingPdf != null) formatsList.Add("PDF");

                itemResult.IsSuccess = true;
                itemResult.IsSkipped = true;
                itemResult.Status = "Đã tồn tại";
                itemResult.Formats = string.Join(", ", formatsList);
                itemResult.SavedPath = existingPdf?.FilePath ?? existingXml?.FilePath;
                return Json(itemResult);
            }

            // 2. Tải hóa đơn gốc theo cấu hình
            bool needPdf = config.DownloadPdf || config.DownloadBoth;
            bool needXml = config.DownloadXml || config.DownloadBoth;

            string? savedPdfPath = null;
            string? savedXmlPath = null;
            var retrievedFormats = new System.Collections.Generic.List<string>();
            string? errorMsg = null;

            // 2a. Tải PDF gốc (Cam kết giữ nguyên bản gốc từ Tổng cục Thuế / Nhà cung cấp, không dựng PDF giả)
            if (needPdf)
            {
                try
                {
                    var pdfRes = await _invoiceFileService.GetOrFetchOriginalPdfAsync(inv.Id, taxAccountId.Value, null, ct);
                    if (pdfRes.Success && pdfRes.PdfBytes != null && pdfRes.PdfBytes.Length > 0)
                    {
                        if (_origConfigService != null)
                        {
                            savedPdfPath = _origConfigService.BuildResolvedStoragePath(
                                config,
                                inv.TaxAccount?.TaxCode ?? "",
                                inv.IssueDate,
                                inv.SellerTaxCode,
                                inv.InvoiceSymbol,
                                inv.InvoiceNumber,
                                ".pdf");

                            if (!System.IO.File.Exists(savedPdfPath) || config.OverwriteExistingFiles || forceReDownload)
                            {
                                await System.IO.File.WriteAllBytesAsync(savedPdfPath, pdfRes.PdfBytes, ct);
                            }
                        }
                        retrievedFormats.Add("PDF");
                    }
                    else
                    {
                        errorMsg = pdfRes.ErrorMessage ?? "Không thể tải PDF gốc từ Cổng Thuế hoặc Nhà cung cấp.";
                    }
                }
                catch (Exception exPdf)
                {
                    errorMsg = $"Lỗi tải PDF: {exPdf.Message}";
                }
            }

            // 2b. Tải/Sao lưu XML gốc
            if (needXml)
            {
                try
                {
                    byte[]? xmlBytes = null;
                    if (existingXml != null && System.IO.File.Exists(existingXml.FilePath))
                    {
                        xmlBytes = await System.IO.File.ReadAllBytesAsync(existingXml.FilePath, ct);
                    }
                    else if (!string.IsNullOrEmpty(inv.RawXmlPath) && System.IO.File.Exists(inv.RawXmlPath))
                    {
                        xmlBytes = await System.IO.File.ReadAllBytesAsync(inv.RawXmlPath, ct);
                    }

                    if (xmlBytes != null && xmlBytes.Length > 0)
                    {
                        if (_origConfigService != null)
                        {
                            savedXmlPath = _origConfigService.BuildResolvedStoragePath(
                                config,
                                inv.TaxAccount?.TaxCode ?? "",
                                inv.IssueDate,
                                inv.SellerTaxCode,
                                inv.InvoiceSymbol,
                                inv.InvoiceNumber,
                                ".xml");

                            if (!System.IO.File.Exists(savedXmlPath) || config.OverwriteExistingFiles || forceReDownload)
                            {
                                await System.IO.File.WriteAllBytesAsync(savedXmlPath, xmlBytes, ct);
                            }
                        }
                        retrievedFormats.Add("XML");
                    }
                }
                catch { }
            }

            if (retrievedFormats.Any())
            {
                itemResult.IsSuccess = true;
                itemResult.IsSkipped = false;
                itemResult.Status = "Thành công";
                itemResult.Formats = string.Join(", ", retrievedFormats);
                itemResult.SavedPath = savedPdfPath ?? savedXmlPath ?? "Lưu trữ hệ thống";

                if (config.EnableAuditLog)
                {
                    await _auditLog.LogActionAsync(
                        "Tải hóa đơn gốc thành công",
                        $"HĐ {inv.InvoiceSymbol}-{inv.InvoiceNumber}",
                        $"Định dạng: {itemResult.Formats}, Lưu tại: {itemResult.SavedPath}",
                        taxAccountId.Value);
                }
                return Json(itemResult);
            }

            itemResult.IsSuccess = false;
            itemResult.Status = "Thất bại";
            itemResult.ErrorMessage = errorMsg ?? "Không lấy được tệp gốc chính thức từ Tổng cục Thuế.";
            return Json(itemResult);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ExportBatchOriginalZip(
            [FromForm] System.Collections.Generic.List<int> invoiceIds,
            System.Threading.CancellationToken ct = default)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            if (!taxAccountId.HasValue || invoiceIds == null || !invoiceIds.Any())
            {
                return BadRequest("Không có hóa đơn nào được chọn.");
            }

            var invoices = await _db.Invoices
                .Include(i => i.TaxAccount)
                .Include(i => i.Files)
                .Where(i => invoiceIds.Contains(i.Id) && i.TaxAccountId == taxAccountId.Value)
                .ToListAsync(ct);

            using var zipMs = new System.IO.MemoryStream();
            using (var archive = new System.IO.Compression.ZipArchive(zipMs, System.IO.Compression.ZipArchiveMode.Create, true))
            {
                var entryNames = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

                foreach (var inv in invoices)
                {
                    // 1. Thêm PDF gốc nếu có
                    var pdfFile = inv.Files.FirstOrDefault(f => string.Equals(f.FileExtension, ".pdf", System.StringComparison.OrdinalIgnoreCase) && f.Status == "Valid" && System.IO.File.Exists(f.FilePath));
                    byte[]? pdfBytes = null;
                    if (pdfFile != null)
                    {
                        try { pdfBytes = await System.IO.File.ReadAllBytesAsync(pdfFile.FilePath, ct); } catch { }
                    }
                    if (pdfBytes == null)
                    {
                        var pdfRes = await _invoiceFileService.GetOrFetchOriginalPdfAsync(inv.Id, taxAccountId.Value, null, ct);
                        if (pdfRes.Success && pdfRes.PdfBytes != null) pdfBytes = pdfRes.PdfBytes;
                    }

                    if (pdfBytes != null && pdfBytes.Length > 0)
                    {
                        var entryName = $"{inv.TaxAccount?.TaxCode ?? "MST"}/{inv.IssueDate.Year}/{inv.IssueDate.Month:D2}/HD_{inv.InvoiceSymbol}_{inv.InvoiceNumber}_{inv.SellerTaxCode}.pdf";
                        if (entryNames.Add(entryName))
                        {
                            var entry = archive.CreateEntry(entryName, System.IO.Compression.CompressionLevel.Optimal);
                            using var s = entry.Open();
                            await s.WriteAsync(pdfBytes, 0, pdfBytes.Length, ct);
                        }
                    }

                    // 2. Thêm XML gốc nếu có
                    var xmlFile = inv.Files.FirstOrDefault(f => string.Equals(f.FileExtension, ".xml", System.StringComparison.OrdinalIgnoreCase) && f.Status == "Valid" && System.IO.File.Exists(f.FilePath));
                    string? xmlPath = xmlFile?.FilePath ?? (System.IO.File.Exists(inv.RawXmlPath) ? inv.RawXmlPath : null);
                    if (!string.IsNullOrEmpty(xmlPath) && System.IO.File.Exists(xmlPath))
                    {
                        try
                        {
                            var xmlBytes = await System.IO.File.ReadAllBytesAsync(xmlPath, ct);
                            if (xmlBytes != null && xmlBytes.Length > 0)
                            {
                                var entryName = $"{inv.TaxAccount?.TaxCode ?? "MST"}/{inv.IssueDate.Year}/{inv.IssueDate.Month:D2}/HD_{inv.InvoiceSymbol}_{inv.InvoiceNumber}_{inv.SellerTaxCode}.xml";
                                if (entryNames.Add(entryName))
                                {
                                    var entry = archive.CreateEntry(entryName, System.IO.Compression.CompressionLevel.Optimal);
                                    using var s = entry.Open();
                                    await s.WriteAsync(xmlBytes, 0, xmlBytes.Length, ct);
                                }
                            }
                        }
                        catch { }
                    }
                }
            }

            var zipBytes = zipMs.ToArray();
            var fileName = $"HoaDonGoc_{DateTime.Now:yyyyMMdd_HHmmss}.zip";

            await _auditLog.LogActionAsync(
                "Tải gói ZIP hóa đơn gốc",
                fileName,
                $"Đóng gói {invoices.Count} hóa đơn vào tệp nén ZIP dung lượng {zipBytes.Length:N0} bytes",
                taxAccountId.Value);

            return File(zipBytes, "application/zip", fileName);
        }
    }
}