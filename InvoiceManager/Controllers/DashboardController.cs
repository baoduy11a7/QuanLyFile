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
    public class DashboardController : Controller
    {
        private readonly ApplicationDbContext _db;
        private readonly ITaxAccountContext _taxAccountContext;
        private readonly IAuditLogService _auditLog;

        public DashboardController(
            ApplicationDbContext db, 
            ITaxAccountContext taxAccountContext,
            IAuditLogService auditLog)
        {
            _db = db;
            _taxAccountContext = taxAccountContext;
            _auditLog = auditLog;
        }

        [HttpGet]
        public async Task<IActionResult> Index(string period = "thisYear", DateTime? fromDate = null, DateTime? toDate = null)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            var taxAccount = await _taxAccountContext.GetCurrentTaxAccountAsync();
            if (!taxAccountId.HasValue) return RedirectToAction("Index", "TaxAccount");

            var now = DateTime.Now;
            DateTime startPeriod;
            DateTime endPeriod;

            // Xử lý khoảng thời gian
            switch (period)
            {
                case "thisMonth":
                    startPeriod = new DateTime(now.Year, now.Month, 1);
                    endPeriod = startPeriod.AddMonths(1).AddTicks(-1);
                    break;
                case "lastMonth":
                    var prev = now.AddMonths(-1);
                    startPeriod = new DateTime(prev.Year, prev.Month, 1);
                    endPeriod = startPeriod.AddMonths(1).AddTicks(-1);
                    break;
                case "thisQuarter":
                    int q = (now.Month - 1) / 3 + 1;
                    startPeriod = new DateTime(now.Year, (q - 1) * 3 + 1, 1);
                    endPeriod = startPeriod.AddMonths(3).AddTicks(-1);
                    break;
                case "custom":
                    startPeriod = fromDate ?? new DateTime(now.Year, 1, 1);
                    endPeriod = toDate.HasValue ? toDate.Value.Date.AddDays(1).AddTicks(-1) : now;
                    break;
                case "thisYear":
                default:
                    period = "thisYear";
                    startPeriod = new DateTime(now.Year, 1, 1);
                    endPeriod = new DateTime(now.Year, 12, 31, 23, 59, 59);
                    break;
            }

            var queryInvoices = _db.Invoices
                .Include(i => i.Details)
                .Where(i => i.TaxAccountId == taxAccountId.Value && i.IssueDate >= startPeriod && i.IssueDate <= endPeriod);

            var allInvoices = await queryInvoices.ToListAsync();

            var purchases = allInvoices.Where(i => i.InvoiceType == "MuaVao").ToList();
            var sales = allInvoices.Where(i => i.InvoiceType == "BanRa").ToList();

            var vm = new DashboardViewModel
            {
                CurrentTaxAccount = taxAccount,
                Period = period,
                FromDate = startPeriod,
                ToDate = endPeriod,

                // 1. Mua vào
                TotalInvoicesPurchase = purchases.Count,
                TotalAmountPurchase = purchases.Sum(i => i.TotalAmount),
                TotalTaxAmountPurchase = purchases.Where(i => i.Status != "Đã bị hủy").Sum(i => i.TaxAmount),

                // 2. Bán ra
                TotalInvoicesSale = sales.Count,
                TotalAmountSale = sales.Sum(i => i.TotalAmount),
                TotalTaxAmountSale = sales.Where(i => i.Status != "Đã bị hủy").Sum(i => i.TaxAmount),

                // 3. Đối soát & Cân đối thuế
                ReconciledCount = purchases.Count(i => i.IsReconciled),
                UnreconciledCount = purchases.Count(i => !i.IsReconciled),

                // 4. Phân loại CQT
                CountWithTaxCode = purchases.Count(i => i.HasTaxCode && !i.IsCashRegister),
                CountWithoutTaxCode = purchases.Count(i => !i.HasTaxCode && !i.IsCashRegister),
                CountCashRegister = purchases.Count(i => i.IsCashRegister),
                CountAdjustedOrReplaced = purchases.Count(i => i.Status == "Đã điều chỉnh" || i.Status == "Đã thay thế"),
                CountCancelled = purchases.Count(i => i.Status == "Đã bị hủy")
            };

            // Cân đối thuế GTGT: Thuế đầu ra - Thuế đầu vào
            vm.NetVatPayable = vm.TotalTaxAmountSale - vm.TotalTaxAmountPurchase;
            vm.ReconciledPercent = vm.TotalInvoicesPurchase > 0 
                ? Math.Round((double)vm.ReconciledCount / vm.TotalInvoicesPurchase * 100, 1) 
                : 0;

            // 5. Biểu đồ 12 tháng gần nhất (không phụ thuộc vào filter period để xem xu hướng dài hạn)
            var allYearInvoices = await _db.Invoices
                .Where(i => i.TaxAccountId == taxAccountId.Value && i.IssueDate >= now.AddMonths(-11).Date)
                .ToListAsync();

            for (int m = 11; m >= 0; m--)
            {
                var target = now.AddMonths(-m);
                var label = $"T{target.Month}/{target.Year.ToString().Substring(2)}";
                vm.MonthlyLabels.Add(label);

                var mPurchases = allYearInvoices.Where(i => i.InvoiceType == "MuaVao" && i.IssueDate.Year == target.Year && i.IssueDate.Month == target.Month).ToList();
                var mSales = allYearInvoices.Where(i => i.InvoiceType == "BanRa" && i.IssueDate.Year == target.Year && i.IssueDate.Month == target.Month).ToList();

                vm.MonthlyPurchaseAmounts.Add(mPurchases.Sum(i => i.TotalAmount));
                vm.MonthlySaleAmounts.Add(mSales.Sum(i => i.TotalAmount));
                vm.MonthlyVatPurchase.Add(mPurchases.Sum(i => i.TaxAmount));
            }

            // 6. Top nhà cung cấp
            vm.TopSuppliers = purchases
                .GroupBy(i => new { i.SellerTaxCode, i.SellerName })
                .Select(g => new SupplierSummaryDto
                {
                    TaxCode = g.Key.SellerTaxCode,
                    Name = g.Key.SellerName,
                    InvoiceCount = g.Count(),
                    TotalAmount = g.Sum(i => i.TotalAmount),
                    TotalTaxAmount = g.Sum(i => i.TaxAmount),
                    LastInvoiceDate = g.Max(i => i.IssueDate)
                })
                .OrderByDescending(x => x.TotalAmount)
                .Take(6)
                .ToList();

            // 7. Danh sách Hóa đơn chưa vào sổ kế toán
            vm.UnreconciledInvoices = purchases
                .Where(i => !i.IsReconciled && i.Status != "Đã bị hủy")
                .OrderByDescending(i => i.IssueDate)
                .Take(8)
                .ToList();

            // 8. Hóa đơn giá trị lớn nhất trong kỳ
            vm.HighValueInvoices = purchases
                .OrderByDescending(i => i.TotalAmount)
                .Take(8)
                .ToList();

            // 9. Hóa đơn có cảnh báo rủi ro / Bị hủy / Điều chỉnh
            vm.RiskyOrCancelledInvoices = purchases
                .Where(i => i.RiskLevel == "Warning" || i.Status == "Đã bị hủy" || i.Status == "Đã điều chỉnh" || i.Status == "Đã thay thế")
                .OrderByDescending(i => i.IssueDate)
                .Take(8)
                .ToList();

            await _auditLog.LogActionAsync("Xem Dashboard tài chính", $"Kỳ: {period}", $"Mua vào: {vm.TotalInvoicesPurchase} HĐ, Bán ra: {vm.TotalInvoicesSale} HĐ", taxAccountId.Value);

            return View(vm);
        }

        [HttpGet]
        public async Task<IActionResult> QuickSearch(string term)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            if (!taxAccountId.HasValue || string.IsNullOrWhiteSpace(term))
            {
                return Json(new List<QuickSearchResultDto>());
            }

            var cleanTerm = term.Trim();
            var results = await _db.Invoices
                .Where(i => i.TaxAccountId == taxAccountId.Value &&
                           (i.InvoiceNumber.Contains(cleanTerm) ||
                            i.InvoiceSymbol.Contains(cleanTerm) ||
                            i.SellerTaxCode.Contains(cleanTerm) ||
                            i.SellerName.Contains(cleanTerm) ||
                            (i.TaxAuthorityCode != null && i.TaxAuthorityCode.Contains(cleanTerm))))
                .OrderByDescending(i => i.IssueDate)
                .Take(15)
                .Select(i => new QuickSearchResultDto
                {
                    Id = i.Id,
                    InvoiceSymbol = i.InvoiceSymbol,
                    InvoiceNumber = i.InvoiceNumber,
                    IssueDate = i.IssueDate.ToString("dd/MM/yyyy"),
                    InvoiceType = i.InvoiceType == "MuaVao" ? "Mua vào" : "Bán ra",
                    SellerTaxCode = i.SellerTaxCode,
                    SellerName = i.SellerName,
                    TotalAmount = i.TotalAmount,
                    Status = i.Status,
                    HasTaxCode = i.HasTaxCode,
                    IsReconciled = i.IsReconciled,
                    ReconciledRefNo = i.ReconciledRefNo
                })
                .ToListAsync();

            return Json(results);
        }
    }
}
