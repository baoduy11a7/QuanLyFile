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

        public InvoiceController(
            ApplicationDbContext db,
            ITaxAccountContext taxAccountContext,
            IAuditLogService auditLog,
            IExportService exportService,
            ISyncService syncService)
        {
            _db = db;
            _taxAccountContext = taxAccountContext;
            _auditLog = auditLog;
            _exportService = exportService;
            _syncService = syncService;
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

            // Query gốc được cô lập chặt chẽ theo TaxAccountId của phiên làm việc
            var query = _db.Invoices
                .Include(i => i.Details)
                .Where(i => i.TaxAccountId == taxAccountId.Value);

            // 1. Lọc theo Loại hóa đơn (Mua vào / Bán ra)
            if (!string.IsNullOrEmpty(filter.InvoiceType))
            {
                query = query.Where(i => i.InvoiceType == filter.InvoiceType);
            }

            // 2. Lọc theo Ngày lập
            if (filter.FromDate.HasValue)
            {
                var from = filter.FromDate.Value.Date;
                query = query.Where(i => i.IssueDate >= from);
            }
            if (filter.ToDate.HasValue)
            {
                var to = filter.ToDate.Value.Date.AddDays(1).AddTicks(-1);
                query = query.Where(i => i.IssueDate <= to);
            }

            // 3. Tìm kiếm từ khóa (Ký hiệu, Số HĐ, MST người bán, Tên người bán)
            if (!string.IsNullOrWhiteSpace(filter.Keyword))
            {
                var kw = filter.Keyword.Trim();
                query = query.Where(i => i.InvoiceSymbol.Contains(kw) 
                                      || i.InvoiceNumber.Contains(kw) 
                                      || i.SellerTaxCode.Contains(kw) 
                                      || i.SellerName.Contains(kw));
            }

            // 4. Lọc theo MST người bán cụ thể
            if (!string.IsNullOrWhiteSpace(filter.SellerTaxCode))
            {
                var stc = filter.SellerTaxCode.Trim();
                query = query.Where(i => i.SellerTaxCode == stc);
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

            // Tính toán tổng hợp số liệu trực tiếp trên tập dữ liệu đã lọc (Khớp chính xác thanh Card phía trên ảnh tham khảo)
            var summary = new InvoiceSummaryViewModel
            {
                TotalCount = await query.CountAsync(),
                WithTaxCodeCount = await query.CountAsync(i => i.HasTaxCode && !i.IsCashRegister),
                WithoutTaxCodeCount = await query.CountAsync(i => !i.HasTaxCode && !i.IsCashRegister),
                CashRegisterCount = await query.CountAsync(i => i.IsCashRegister),
                TotalAmountBeforeTax = await query.SumAsync(i => (decimal?)i.AmountBeforeTax) ?? 0,
                TotalTaxAmount = await query.SumAsync(i => (decimal?)i.TaxAmount) ?? 0,
                TotalAmount = await query.SumAsync(i => (decimal?)i.TotalAmount) ?? 0
            };

            // Lấy thời điểm đồng bộ gần nhất
            var lastSync = await _db.SyncLogs
                .Where(s => s.TaxAccountId == taxAccountId.Value)
                .OrderByDescending(s => s.SyncedAt)
                .Select(s => (DateTime?)s.SyncedAt)
                .FirstOrDefaultAsync();
            summary.LastSyncedAt = lastSync;

            // Sắp xếp
            query = filter.SortBy switch
            {
                "DateAsc" => query.OrderBy(i => i.IssueDate),
                "AmountDesc" => query.OrderByDescending(i => i.TotalAmount),
                "AmountAsc" => query.OrderBy(i => i.TotalAmount),
                "NumberAsc" => query.OrderBy(i => i.InvoiceNumber),
                _ => query.OrderByDescending(i => i.IssueDate)
            };

            // Phân trang
            int totalItems = summary.TotalCount;
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
                .FirstOrDefaultAsync(i => i.Id == id && i.TaxAccountId == taxAccountId);

            if (invoice == null) return NotFound("Không tìm thấy hóa đơn.");

            await _auditLog.LogActionAsync("Xem bản thể hiện HTML", $"{invoice.InvoiceSymbol}-{invoice.InvoiceNumber}", null, taxAccountId);
            var html = _exportService.GenerateInvoiceHtml(invoice);
            return Content(html, "text/html; charset=utf-8");
        }

        [HttpGet]
        public async Task<IActionResult> ViewRawXml(int id)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            var invoice = await _db.Invoices
                .Include(i => i.Details)
                .FirstOrDefaultAsync(i => i.Id == id && i.TaxAccountId == taxAccountId);

            if (invoice == null) return NotFound("Không tìm thấy hóa đơn.");

            await _auditLog.LogActionAsync("Xem XML gốc", $"{invoice.InvoiceSymbol}-{invoice.InvoiceNumber}", null, taxAccountId);

            string xmlContent = "";
            if (!string.IsNullOrEmpty(invoice.RawXmlPath) && System.IO.File.Exists(invoice.RawXmlPath))
            {
                xmlContent = await System.IO.File.ReadAllTextAsync(invoice.RawXmlPath, Encoding.UTF8);
            }
            else
            {
                // Tái tạo nội dung XML chuẩn Nghị định 123 nếu hóa đơn tạo từ seed
                xmlContent = GenerateStandardXmlForInvoice(invoice);
            }

            return Content(xmlContent, "application/xml; charset=utf-8");
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
            if (!string.IsNullOrEmpty(filter.QuickDateRange))
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
                    case "custom":
                        // Giữ nguyên FromDate & ToDate người dùng nhập
                        break;
                    default:
                        filter.FromDate = new DateTime(now.Year, now.Month, 1);
                        filter.ToDate = filter.FromDate.Value.AddMonths(1).AddDays(-1);
                        break;
                }
            }
            else if (!filter.FromDate.HasValue && !filter.ToDate.HasValue)
            {
                // Mặc định tháng hiện tại
                filter.QuickDateRange = "thisMonth";
                filter.FromDate = new DateTime(now.Year, now.Month, 1);
                filter.ToDate = filter.FromDate.Value.AddMonths(1).AddDays(-1);
            }
        }

        private string GenerateStandardXmlForInvoice(Invoice invoice)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.AppendLine("<HDon xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">");
            sb.AppendLine("  <DLHDon>");
            sb.AppendLine("    <TTChung>");
            sb.AppendLine($"      <PBan>2.0.0</PBan>");
            sb.AppendLine($"      <THDon>Hóa đơn giá trị gia tăng</THDon>");
            sb.AppendLine($"      <KHMSHDon>{invoice.InvoiceSymbol.Substring(0, 1)}</KHMSHDon>");
            sb.AppendLine($"      <KHHDon>{invoice.InvoiceSymbol.Substring(1)}</KHHDon>");
            sb.AppendLine($"      <SHDon>{invoice.InvoiceNumber}</SHDon>");
            sb.AppendLine($"      <NLap>{invoice.IssueDate:yyyy-MM-dd}</NLap>");
            sb.AppendLine($"      <DVTTe>VND</DVTTe>");
            sb.AppendLine($"      <TGia>1</TGia>");
            sb.AppendLine($"      <HTTToan>TM/CK</HTTToan>");
            sb.AppendLine("    </TTChung>");
            sb.AppendLine("    <NDHDon>");
            sb.AppendLine("      <NBan>");
            sb.AppendLine($"        <Ten>{System.Security.SecurityElement.Escape(invoice.SellerName)}</Ten>");
            sb.AppendLine($"        <MST>{invoice.SellerTaxCode}</MST>");
            sb.AppendLine($"        <DChi>{System.Security.SecurityElement.Escape(invoice.SellerAddress ?? "")}</DChi>");
            sb.AppendLine("      </NBan>");
            sb.AppendLine("      <NMua>");
            sb.AppendLine($"        <Ten>{System.Security.SecurityElement.Escape(invoice.BuyerName ?? "")}</Ten>");
            sb.AppendLine($"        <MST>{invoice.BuyerTaxCode ?? ""}</MST>");
            sb.AppendLine($"        <DChi>{System.Security.SecurityElement.Escape(invoice.BuyerAddress ?? "")}</DChi>");
            sb.AppendLine("      </NMua>");
            sb.AppendLine("      <DSHHDVu>");
            foreach (var d in invoice.Details)
            {
                sb.AppendLine("        <HHDVu>");
                sb.AppendLine($"          <STT>{d.LineNumber}</STT>");
                sb.AppendLine($"          <MHHDVu>{d.ItemCode ?? ""}</MHHDVu>");
                sb.AppendLine($"          <THHDVu>{System.Security.SecurityElement.Escape(d.ItemName)}</THHDVu>");
                sb.AppendLine($"          <DVTinh>{System.Security.SecurityElement.Escape(d.Unit ?? "")}</DVTinh>");
                sb.AppendLine($"          <SLuong>{d.Quantity}</SLuong>");
                sb.AppendLine($"          <DGia>{d.UnitPrice}</DGia>");
                sb.AppendLine($"          <ThTien>{d.AmountBeforeTax}</ThTien>");
                sb.AppendLine($"          <TSuat>{(d.TaxRate == -1 ? "KCT" : d.TaxRate + "%")}</TSuat>");
                sb.AppendLine($"          <TThue>{d.TaxAmount}</TThue>");
                sb.AppendLine("        </HHDVu>");
            }
            sb.AppendLine("      </DSHHDVu>");
            sb.AppendLine("      <TToan>");
            sb.AppendLine($"        <TgTCThue>{invoice.AmountBeforeTax}</TgTCThue>");
            sb.AppendLine($"        <TgTThue>{invoice.TaxAmount}</TgTThue>");
            sb.AppendLine($"        <TgTTTBSo>{invoice.TotalAmount}</TgTTTBSo>");
            sb.AppendLine("      </TToan>");
            sb.AppendLine("    </NDHDon>");
            sb.AppendLine("  </DLHDon>");
            if (!string.IsNullOrEmpty(invoice.TaxAuthorityCode))
            {
                sb.AppendLine($"  <MCCQT>{invoice.TaxAuthorityCode}</MCCQT>");
            }
            sb.AppendLine("  <DSCKS>");
            sb.AppendLine($"    <NNBS>{System.Security.SecurityElement.Escape(invoice.SellerName)}</NNBS>");
            sb.AppendLine($"    <NBS>{invoice.IssueDate:yyyy-MM-ddTHH:mm:ss}</NBS>");
            sb.AppendLine("  </DSCKS>");
            sb.AppendLine("</HDon>");
            return sb.ToString();
        }
    }
}
