using ClosedXML.Excel;
using InvoiceManager.Data;
using InvoiceManager.Models.Entities;
using InvoiceManager.Models.ViewModels;
using InvoiceManager.Services.TaxEngine;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InvoiceManager.Services
{
    public class TaxDeclarationService : ITaxDeclarationService
    {
        private readonly ApplicationDbContext _db;
        private readonly ITaxFormRegistry _registry;
        private readonly ITaxExpressionEvaluator _evaluator;

        public TaxDeclarationService(
            ApplicationDbContext db,
            ITaxFormRegistry? registry = null,
            ITaxExpressionEvaluator? evaluator = null)
        {
            _db = db;
            _registry = registry ?? new TaxFormRegistry();
            _evaluator = evaluator ?? new TaxExpressionEvaluator();
        }

        public ITaxFormRegistry GetRegistry() => _registry;

        /// <summary>
        /// Tính toán toàn bộ chỉ tiêu tờ khai thuế GTGT và các bảng kê kèm theo
        /// Tuân thủ quy định pháp luật và kiến trúc phiên bản động (Không hard-code)
        /// </summary>
        public async Task<TaxDeclarationViewModel> CalculateDeclarationAsync(
            int taxAccountId,
            string periodType,
            int periodValue,
            int year,
            DateTime? customFrom = null,
            DateTime? customTo = null,
            decimal carriedOverTax = 0,
            bool? isZeroActivity = null,
            bool autoFetchPriorPeriod = false)
        {
            var taxAccount = await _db.TaxAccounts.AsNoTracking().FirstOrDefaultAsync(t => t.Id == taxAccountId);

            // 1. Xác định kỳ tính thuế và hạn nộp (Luật Quản lý thuế 108/2025/QH15)
            DateTime fromDate;
            DateTime toDate;
            DateTime deadline;

            if (periodType.Equals("Quarter", StringComparison.OrdinalIgnoreCase))
            {
                int startMonth = ((periodValue - 1) * 3) + 1;
                fromDate = new DateTime(year, startMonth, 1);
                toDate = fromDate.AddMonths(3).AddDays(-1);

                // Khai theo quý: Chậm nhất là ngày cuối cùng của tháng đầu quý tiếp theo
                var nextQuarterFirstMonth = toDate.AddDays(1);
                deadline = new DateTime(nextQuarterFirstMonth.Year, nextQuarterFirstMonth.Month, DateTime.DaysInMonth(nextQuarterFirstMonth.Year, nextQuarterFirstMonth.Month));
            }
            else if (periodType.Equals("Month", StringComparison.OrdinalIgnoreCase))
            {
                fromDate = new DateTime(year, periodValue, 1);
                toDate = fromDate.AddMonths(1).AddDays(-1);

                // Khai theo tháng: Chậm nhất là ngày 20 của tháng tiếp theo
                var nextMonth = toDate.AddDays(1);
                deadline = new DateTime(nextMonth.Year, nextMonth.Month, 20);
            }
            else
            {
                fromDate = customFrom ?? new DateTime(year, 1, 1);
                toDate = customTo ?? new DateTime(year, 12, 31);
                deadline = new DateTime(year + 1, 1, 31);
            }

            // 2. TỰ ĐỘNG CHỌN PHIÊN BẢN BIỂU MẪU THEO KỲ TÍNH THUẾ (Mục 1.4 & Mục 8.3)
            var formVersion = _registry.ResolveVersionForPeriod(fromDate, toDate);
            decimal cashThreshold = _registry.GetParameterValue("CashPaymentThreshold", fromDate, 5000000m);
            decimal vatReductionRate = _registry.GetParameterValue("VatReductionRate", fromDate, 0.02m);

            var model = new TaxDeclarationViewModel
            {
                TaxCode = taxAccount?.TaxCode ?? "0317668491",
                CompanyName = taxAccount?.CompanyName ?? "CÔNG TY TNHH MÔI TRƯỜNG NDT",
                Address = taxAccount?.Address ?? "490/1 An Dương Vương, Phường An Lạc, TP. Hồ Chí Minh",
                PeriodType = periodType,
                PeriodValue = periodValue,
                Year = year,
                FromDate = fromDate,
                ToDate = toDate,
                SubmissionDeadline = deadline,
                IsOverdue = DateTime.Today > deadline,
                FormVersionCode = formVersion.Code,
                LegalDocRef = formVersion.LegalDocRef,
                LegalDocFullTitle = formVersion.LegalDocFullTitle,
                XmlTemplateCode = formVersion.XmlTemplateCode,
                XmlXsdVersion = formVersion.XmlXsdVersion,
                CashThresholdApplied = cashThreshold,
                Target22_CarriedOverDeduction = carriedOverTax
            };

            // Điền tooltip cho các chỉ tiêu từ metadata của version
            foreach (var f in formVersion.Fields)
            {
                string tt = !string.IsNullOrWhiteSpace(f.TooltipFormula) ? f.TooltipFormula : f.LegalBasis;
                model.IndicatorTooltips[f.Code] = tt;
            }

            // 3. Tự động lấy [22] từ [43] kỳ trước nếu được yêu cầu và chưa có số thuế chuyển sang
            if (autoFetchPriorPeriod && carriedOverTax == 0)
            {
                decimal prior43 = await GetPriorPeriodTarget43Async(taxAccountId, periodType, periodValue, year);
                if (prior43 > 0)
                {
                    model.Target22_CarriedOverDeduction = prior43;
                }
            }

            // 4. Lấy dữ liệu hóa đơn trong kỳ từ Database (AsNoTracking để tối ưu hiệu năng)
            var startOfDay = fromDate.Date;
            var endOfDay = toDate.Date.AddDays(1).AddTicks(-1);

            var invoices = await _db.Invoices
                .AsNoTracking()
                .Include(i => i.Details)
                .Where(i => i.TaxAccountId == taxAccountId && i.IssueDate >= startOfDay && i.IssueDate <= endOfDay)
                .ToListAsync();

            // Lọc bỏ hóa đơn bị thay thế, bị hủy
            var validInvoices = invoices.Where(i =>
                !i.Status.Contains("bị thay thế", StringComparison.OrdinalIgnoreCase) &&
                !i.Status.Contains("hủy", StringComparison.OrdinalIgnoreCase)).ToList();

            var muaVao = validInvoices.Where(i => i.InvoiceType != "BanRa").ToList();
            var banRa = validInvoices.Where(i => i.InvoiceType == "BanRa").ToList();

            model.TotalPurchaseInvoices = muaVao.Count;
            model.TotalSalesInvoices = banRa.Count;

            // 5. Kiểm tra chỉ tiêu [21] (Không phát sinh hoạt động)
            bool userCheckedNoActivity = isZeroActivity ?? false;
            if (userCheckedNoActivity)
            {
                if (validInvoices.Any())
                {
                    model.ValidationAlerts.Add("Cảnh báo: Có hóa đơn phát sinh trong kỳ (" + validInvoices.Count + " hóa đơn). Không thể chọn chỉ tiêu [21]!");
                }
                else
                {
                    model.Indicator21_NoActivity = true;
                    // Khi không phát sinh, các chỉ tiêu mua bán = 0, [43] = [22]
                    model.Target43_CarriedForwardDeduction = model.Target22_CarriedOverDeduction;
                    return model;
                }
            }

            // 6. Xử lý Mua vào & Kiểm tra điều kiện khấu trừ (Ngưỡng thanh toán không dùng tiền mặt)
            int cashWarnCount = 0;
            decimal purchaseTotalBeforeTax = 0;
            decimal purchaseTotalTax = 0;
            decimal purchaseDeductibleTax = 0;

            var purchaseItems = new List<TaxInvoiceItem>();

            foreach (var inv in muaVao.OrderBy(i => i.IssueDate).ThenBy(i => i.InvoiceNumber))
            {
                bool isCash = !string.IsNullOrEmpty(inv.PaymentMethod) &&
                    (inv.PaymentMethod.Contains("TM", StringComparison.OrdinalIgnoreCase) ||
                     inv.PaymentMethod.Contains("Tiền mặt", StringComparison.OrdinalIgnoreCase)) &&
                    !inv.PaymentMethod.Contains("CK", StringComparison.OrdinalIgnoreCase);

                bool isExceedThreshold = inv.TotalAmount >= cashThreshold;
                bool isWarning = isCash && isExceedThreshold;
                if (isWarning) cashWarnCount++;

                var details = inv.Details.OrderBy(d => d.LineNumber).ToList();
                if (!details.Any())
                {
                    decimal defRate = inv.AmountBeforeTax > 0 ? Math.Round((inv.TaxAmount / inv.AmountBeforeTax) * 100, 0) : 10;
                    details.Add(new InvoiceDetail
                    {
                        LineNumber = 1,
                        ItemName = $"Hàng hóa/dịch vụ theo HĐ số {inv.InvoiceNumber}",
                        AmountBeforeTax = inv.AmountBeforeTax,
                        TaxRate = defRate,
                        TaxAmount = inv.TaxAmount,
                        TotalAmount = inv.TotalAmount > 0 ? inv.TotalAmount : (inv.AmountBeforeTax + inv.TaxAmount)
                    });
                }

                purchaseTotalBeforeTax += inv.AmountBeforeTax;
                purchaseTotalTax += inv.TaxAmount;

                // Điều kiện khấu trừ: Nếu hóa đơn tiền mặt >= ngưỡng thì không đủ điều kiện khấu trừ
                if (!isWarning)
                {
                    purchaseDeductibleTax += inv.TaxAmount;
                }

                // Tách nhóm theo mức thuế suất để hiển thị bảng kê
                var groups = details.GroupBy(d => d.TaxRate).OrderBy(g => g.Key).ToList();
                foreach (var grp in groups)
                {
                    var firstItem = grp.First().ItemName;
                    var sumBefore = grp.Sum(d => d.AmountBeforeTax);
                    var sumTax = grp.Sum(d => d.TaxAmount > 0 ? d.TaxAmount : (grp.Key > 0 ? Math.Round(d.AmountBeforeTax * grp.Key / 100m, 0) : 0));
                    var sumTotal = sumBefore + sumTax;
                    string rateStr = grp.Key == -1 ? "KCT" : $"{grp.Key}%";

                    purchaseItems.Add(new TaxInvoiceItem
                    {
                        Id = inv.Id,
                        InvoiceSymbol = inv.InvoiceSymbol,
                        InvoiceNumber = inv.InvoiceNumber,
                        IssueDate = inv.IssueDate.ToString("dd/MM/yyyy"),
                        SellerName = inv.SellerName,
                        SellerTaxCode = inv.SellerTaxCode,
                        BuyerName = inv.BuyerName ?? "",
                        BuyerTaxCode = inv.BuyerTaxCode ?? "",
                        ItemName = firstItem,
                        AmountBeforeTax = sumBefore,
                        TaxRate = rateStr,
                        TaxAmount = sumTax,
                        TotalAmount = sumTotal,
                        PaymentMethod = inv.PaymentMethod ?? "TM/CK",
                        IsCashPaymentWarning = isWarning,
                        IsDeductible = !isWarning,
                        Note = isWarning ? $"[CẢNH BÁO] HĐ >= {cashThreshold:N0} đ thanh toán tiền mặt" : firstItem
                    });
                }

                // Kiểm tra phụ lục 8% mua vào
                var items8 = details.Where(d => Math.Round(d.TaxRate) == 8).ToList();
                if (items8.Any())
                {
                    model.Purchase8Amount += items8.Sum(d => d.AmountBeforeTax);
                    model.Purchase8Tax += items8.Sum(d => d.TaxAmount);
                }
                else if (inv.AmountBeforeTax > 0 && Math.Round(inv.TaxAmount / inv.AmountBeforeTax * 100) == 8)
                {
                    model.Purchase8Amount += inv.AmountBeforeTax;
                    model.Purchase8Tax += inv.TaxAmount;
                }
            }

            model.PurchaseInvoices = purchaseItems;
            model.CashThresholdWarningCount = cashWarnCount;
            if (cashWarnCount > 0)
            {
                model.ValidationAlerts.Add($"Có {cashWarnCount} hóa đơn mua vào từ {cashThreshold:N0} đ thanh toán tiền mặt không đủ điều kiện khấu trừ theo quy định pháp luật.");
            }

            // 7. Xử lý Bán ra theo các nhóm thuế suất & Phụ lục giảm thuế 8%
            decimal salesKct = 0;
            decimal sales0 = 0;
            decimal sales5Amount = 0;
            decimal sales5Tax = 0;
            decimal sales8Amount = 0;
            decimal sales8Tax = 0;
            decimal sales10Amount = 0;
            decimal sales10Tax = 0;
            decimal salesNoTax = 0;
            decimal salesNoDeclare = 0;

            var soldItems = new List<TaxInvoiceItem>();
            var redItems = new List<Tax8ReductionItem>();

            foreach (var inv in banRa.OrderBy(i => i.IssueDate).ThenBy(i => i.InvoiceNumber))
            {
                var details = inv.Details.OrderBy(d => d.LineNumber).ToList();
                if (!details.Any())
                {
                    decimal defRate = inv.AmountBeforeTax > 0 ? Math.Round((inv.TaxAmount / inv.AmountBeforeTax) * 100, 0) : 10;
                    details.Add(new InvoiceDetail
                    {
                        LineNumber = 1,
                        ItemName = $"Hàng hóa/dịch vụ theo HĐ số {inv.InvoiceNumber}",
                        AmountBeforeTax = inv.AmountBeforeTax,
                        TaxRate = defRate,
                        TaxAmount = inv.TaxAmount,
                        TotalAmount = inv.TotalAmount > 0 ? inv.TotalAmount : (inv.AmountBeforeTax + inv.TaxAmount)
                    });
                }

                foreach (var d in details)
                {
                    decimal rate = d.TaxRate;
                    if (rate == -1) // Không chịu thuế
                    {
                        salesKct += d.AmountBeforeTax;
                    }
                    else if (rate == 0) // Thuế suất 0%
                    {
                        sales0 += d.AmountBeforeTax;
                    }
                    else if (rate == 5) // Thuế suất 5%
                    {
                        sales5Amount += d.AmountBeforeTax;
                        sales5Tax += d.TaxAmount;
                    }
                    else if (rate == 8) // Thuế suất 8% (NQ 110)
                    {
                        sales8Amount += d.AmountBeforeTax;
                        sales8Tax += d.TaxAmount;

                        redItems.Add(new Tax8ReductionItem
                        {
                            ItemName = d.ItemName,
                            InvoiceSymbol = inv.InvoiceSymbol,
                            InvoiceNumber = inv.InvoiceNumber,
                            IssueDate = inv.IssueDate.ToString("dd/MM/yyyy"),
                            AmountBeforeTax = d.AmountBeforeTax,
                            TaxAmount8Percent = d.TaxAmount,
                            TaxReducedAmount2Percent = Math.Round(d.AmountBeforeTax * vatReductionRate, 0, MidpointRounding.AwayFromZero)
                        });
                    }
                    else if (rate >= 10) // Thuế suất 10%
                    {
                        sales10Amount += d.AmountBeforeTax;
                        sales10Tax += d.TaxAmount;
                    }
                    else if (rate == -2) // Không phải kê khai, nộp thuế (TT 89 mã [34a])
                    {
                        salesNoDeclare += d.AmountBeforeTax;
                    }
                    else
                    {
                        salesNoTax += d.AmountBeforeTax; // [32b] Khoản không tính thuế
                    }
                }

                var groups = details.GroupBy(d => d.TaxRate).OrderBy(g => g.Key).ToList();
                foreach (var grp in groups)
                {
                    var firstItem = grp.First().ItemName;
                    var sumBefore = grp.Sum(d => d.AmountBeforeTax);
                    var sumTax = grp.Sum(d => d.TaxAmount > 0 ? d.TaxAmount : (grp.Key > 0 ? Math.Round(d.AmountBeforeTax * grp.Key / 100m, 0) : 0));
                    var sumTotal = sumBefore + sumTax;
                    string rateStr = grp.Key == -1 ? "KCT" : $"{grp.Key}%";

                    soldItems.Add(new TaxInvoiceItem
                    {
                        Id = inv.Id,
                        InvoiceSymbol = inv.InvoiceSymbol,
                        InvoiceNumber = inv.InvoiceNumber,
                        IssueDate = inv.IssueDate.ToString("dd/MM/yyyy"),
                        SellerName = inv.SellerName,
                        SellerTaxCode = inv.SellerTaxCode,
                        BuyerName = inv.BuyerName ?? "",
                        BuyerTaxCode = inv.BuyerTaxCode ?? "",
                        ItemName = firstItem,
                        AmountBeforeTax = sumBefore,
                        TaxRate = rateStr,
                        TaxAmount = sumTax,
                        TotalAmount = sumTotal,
                        Note = firstItem
                    });
                }
            }

            model.SoldInvoices = soldItems;
            model.Tax8PercentReductionItems = redItems;
            model.Sales8Amount = sales8Amount;
            model.Sales8Tax = sales8Tax;
            model.Sales8ReducedTax = redItems.Sum(r => r.TaxReducedAmount2Percent);

            // Kiểm tra cảnh báo thời hạn NQ 110 (áp dụng đến 31/12/2026)
            if (sales8Amount > 0 && fromDate > new DateTime(2026, 12, 31))
            {
                model.IsOutOfNQ110Period = true;
                model.ValidationAlerts.Add("Cảnh báo: Kỳ kê khai nằm ngoài thời hạn hiệu lực của Nghị quyết 110 giảm thuế 8%!");
            }

            // 8. ĐƯA DỮ LIỆU ĐẦU VÀO VÀO ENGINE TÍNH TOÁN BIỂU THỨC (Không hard-code công thức!)
            var initialValues = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
            {
                ["[21]"] = 0m,
                ["[22]"] = model.Target22_CarriedOverDeduction,
                ["[23]"] = purchaseTotalBeforeTax,
                ["[23a]"] = 0m, // HHDV nhập khẩu
                ["[24]"] = purchaseTotalTax,
                ["[25]"] = purchaseDeductibleTax,
                ["[26]"] = salesKct,
                ["[27]"] = sales0,
                ["[28]"] = sales5Amount,
                ["[29]"] = sales5Tax,
                ["[30]"] = sales8Amount,
                ["[31]"] = sales8Tax,
                ["[32]"] = sales10Amount,
                ["[32a]"] = sales10Tax,
                ["[32b]"] = salesNoTax,
                ["[33]"] = sales10Tax, // Tạm thời
                ["[34a]"] = salesNoDeclare,
                ["[37]"] = 0m,
                ["[38]"] = 0m,
                ["[39a]"] = 0m,
                ["[40a]"] = 0m,
                ["[42]"] = 0m
            };

            // Thực thi tính toán qua TaxExpressionEvaluator theo đúng các công thức cấu hình trong formVersion
            var evaluated = _evaluator.EvaluateAllFields(formVersion.Fields, initialValues);

            // 9. Map kết quả từ Engine ra ViewModel
            model.Target23_PurchaseAmountBeforeTax = evaluated.GetValueOrDefault("[23]");
            model.Target23a_ImportPurchaseAmount = evaluated.GetValueOrDefault("[23a]");
            model.Target24_PurchaseTaxAmount = evaluated.GetValueOrDefault("[24]");
            model.Target25_DeductibleTaxAmount = evaluated.GetValueOrDefault("[25]");

            model.Target26_SalesAmount0 = evaluated.GetValueOrDefault("[26]");
            model.Target27_SalesAmount5 = evaluated.GetValueOrDefault("[27]");
            model.Target28_SalesTax5 = evaluated.GetValueOrDefault("[28]");
            model.Target29_SalesAmount8And10 = evaluated.GetValueOrDefault("[29]");
            model.Target30_SalesTax8And10 = evaluated.GetValueOrDefault("[30]");
            model.Target31_SalesAmountNoTax = evaluated.GetValueOrDefault("[31]");
            model.Target32_SalesAmount10 = evaluated.GetValueOrDefault("[32]");
            model.Target32a_SalesTax10 = evaluated.GetValueOrDefault("[32a]");
            model.Target32b_SalesAmountNoTax = evaluated.GetValueOrDefault("[32b]");
            model.Target33_TaxableSalesAmount = evaluated.GetValueOrDefault("[33]");
            model.Target34a_SalesAmountNoDeclare = evaluated.GetValueOrDefault("[34a]");
            model.Target34_TotalSalesAmount = evaluated.GetValueOrDefault("[34]");
            model.Target35_TotalSalesTax = evaluated.GetValueOrDefault("[35]");

            model.Target36_OutputTax = evaluated.GetValueOrDefault("[36]");
            model.Target40_TaxToPay = evaluated.GetValueOrDefault("[40]");
            model.Target41_RemainingDeductible = evaluated.GetValueOrDefault("[41]");
            model.Target43_CarriedForwardDeduction = evaluated.GetValueOrDefault("[43]");

            // 10. Chạy bộ kiểm tra TaxRules của phiên bản
            foreach (var rule in formVersion.Rules)
            {
                if (rule.RuleCode == "RULE_SETTLEMENT_BALANCE")
                {
                    if (model.Target40_TaxToPay > 0 && model.Target43_CarriedForwardDeduction > 0)
                    {
                        model.ValidationAlerts.Add($"[LỖI ĐỎ] {rule.Message} (Căn cứ: {rule.LegalBasis})");
                    }
                }
                else if (rule.RuleCode == "RULE_SALES_SUM_TT89")
                {
                    decimal expected34 = model.Target26_SalesAmount0 + model.Target33_TaxableSalesAmount + model.Target34a_SalesAmountNoDeclare;
                    if (Math.Abs(model.Target34_TotalSalesAmount - expected34) > 1)
                    {
                        model.ValidationAlerts.Add($"[LỖI ĐỎ] {rule.Message}");
                    }
                }
            }

            return model;
        }

        /// <summary>
        /// Lấy tự động số thuế [43] của kỳ liền trước để chuyển sang [22] kỳ này
        /// </summary>
        public async Task<decimal> GetPriorPeriodTarget43Async(int taxAccountId, string periodType, int periodValue, int year)
        {
            try
            {
                int priorPeriodVal;
                int priorYear = year;

                if (periodType.Equals("Quarter", StringComparison.OrdinalIgnoreCase))
                {
                    if (periodValue <= 1)
                    {
                        priorPeriodVal = 4;
                        priorYear = year - 1;
                    }
                    else
                    {
                        priorPeriodVal = periodValue - 1;
                    }
                }
                else
                {
                    if (periodValue <= 1)
                    {
                        priorPeriodVal = 12;
                        priorYear = year - 1;
                    }
                    else
                    {
                        priorPeriodVal = periodValue - 1;
                    }
                }

                // Tính toán sơ bộ kỳ trước để lấy [43] (autoFetchPriorPeriod = false để tránh đệ quy)
                var priorModel = await CalculateDeclarationAsync(taxAccountId, periodType, priorPeriodVal, priorYear, carriedOverTax: 0, isZeroActivity: null, autoFetchPriorPeriod: false);
                return priorModel.Target43_CarriedForwardDeduction;
            }
            catch
            {
                return 0m;
            }
        }

        /// <summary>
        /// Sinh XML tương thích 100% với XSD của Cơ quan thuế và phần mềm HTKK
        /// </summary>
        public string GenerateDeclarationXml(TaxDeclarationViewModel model)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine("<HSoThueDTu xmlns=\"http://kekhaithue.gdt.gov.vn/TKhaiThue\">");
            sb.AppendLine("  <HSoKhaiThue>");
            sb.AppendLine("    <TTinChung>");
            sb.AppendLine("      <TTinDVu>");
            sb.AppendLine("        <maDVu>HTKK</maDVu>");
            sb.AppendLine("        <tenDVu>Hỗ trợ kê khai thuế</tenDVu>");
            sb.AppendLine("        <pbanDVu>5.2.0</pbanDVu>");
            sb.AppendLine("      </TTinDVu>");
            sb.AppendLine("      <TTinTKhaiThue>");
            sb.AppendLine($"        <maTKhai>{model.XmlTemplateCode}</maTKhai>");
            sb.AppendLine("        <tenTKhai>Tờ khai thuế giá trị gia tăng (Mẫu 01/GTGT)</tenTKhai>");
            sb.AppendLine($"        <moTaBMau>{model.LegalDocFullTitle}</moTaBMau>");
            sb.AppendLine($"        <pbanTKhaiXML>{model.XmlXsdVersion}</pbanTKhaiXML>");
            sb.AppendLine($"        <loaiTKhai>{(model.IsFirstDeclaration ? "C" : "B")}</loaiTKhai>");
            sb.AppendLine($"        <soLan>{model.AdditionalCount}</soLan>");
            sb.AppendLine($"        <kyKKhai>{(model.PeriodType == "Quarter" ? "Q" + model.PeriodValue : "M" + model.PeriodValue.ToString("D2"))}/{model.Year}</kyKKhai>");
            sb.AppendLine($"        <kyKKhaiTuNgay>{model.FromDate:dd/MM/yyyy}</kyKKhaiTuNgay>");
            sb.AppendLine($"        <kyKKhaiDenNgay>{model.ToDate:dd/MM/yyyy}</kyKKhaiDenNgay>");
            sb.AppendLine($"        <ngayLapTKhai>{DateTime.Now:yyyy-MM-dd}</ngayLapTKhai>");
            sb.AppendLine("      </TTinTKhaiThue>");
            sb.AppendLine("      <NNT>");
            sb.AppendLine($"        <mst>{model.TaxCode}</mst>");
            sb.AppendLine($"        <tenNNT><![CDATA[{model.CompanyName}]]></tenNNT>");
            sb.AppendLine($"        <dchiNNT><![CDATA[{model.Address}]]></dchiNNT>");
            sb.AppendLine("      </NNT>");
            sb.AppendLine("    </TTinChung>");
            sb.AppendLine("    <CTieuTKhaiChinh>");
            sb.AppendLine($"      <ct21>{(model.Indicator21_NoActivity ? "1" : "0")}</ct21>");
            sb.AppendLine($"      <ct22>{(long)model.Target22_CarriedOverDeduction}</ct22>");
            sb.AppendLine($"      <ct23>{(long)model.Target23_PurchaseAmountBeforeTax}</ct23>");
            sb.AppendLine($"      <ct24>{(long)model.Target24_PurchaseTaxAmount}</ct24>");
            sb.AppendLine($"      <ct25>{(long)model.Target25_DeductibleTaxAmount}</ct25>");
            sb.AppendLine($"      <ct26>{(long)model.Target26_SalesAmount0}</ct26>");
            sb.AppendLine($"      <ct27>{(long)model.Target27_SalesAmount5}</ct27>");
            sb.AppendLine($"      <ct28>{(long)model.Target28_SalesTax5}</ct28>");
            sb.AppendLine($"      <ct29>{(long)model.Target29_SalesAmount8And10}</ct29>");
            sb.AppendLine($"      <ct30>{(long)model.Target30_SalesTax8And10}</ct30>");
            sb.AppendLine($"      <ct31>{(long)model.Target31_SalesAmountNoTax}</ct31>");
            sb.AppendLine($"      <ct32>{(long)model.Target32_SalesAmount10}</ct32>");
            sb.AppendLine($"      <ct33>{(long)model.Target32a_SalesTax10}</ct33>");
            sb.AppendLine($"      <ct34>{(long)model.Target34_TotalSalesAmount}</ct34>");
            sb.AppendLine($"      <ct35>{(long)model.Target35_TotalSalesTax}</ct35>");
            sb.AppendLine($"      <ct36>{(long)model.Target36_OutputTax}</ct36>");
            sb.AppendLine($"      <ct40>{(long)model.Target40_TaxToPay}</ct40>");
            sb.AppendLine($"      <ct43>{(long)model.Target43_CarriedForwardDeduction}</ct43>");
            sb.AppendLine("    </CTieuTKhaiChinh>");
            sb.AppendLine("  </HSoKhaiThue>");
            sb.AppendLine("</HSoThueDTu>");
            return sb.ToString();
        }

        /// <summary>
        /// Xuất file Excel đầy đủ 4 sheet chuẩn mực thẩm mỹ cao
        /// </summary>
        public async Task<byte[]> ExportDeclarationExcelAsync(TaxDeclarationViewModel model, int taxAccountId)
        {
            using var workbook = new XLWorkbook();
            string kyStr = model.PeriodType == "Quarter" ? $"Quý {model.PeriodValue} năm {model.Year}" : $"Tháng {model.PeriodValue} năm {model.Year}";

            // SHEET 1: TỜ KHAI 01/GTGT
            BuildToKhaiSheet(workbook, model, kyStr);

            // SHEET 2: BẢNG KÊ MUA VÀO (01-2/GTGT)
            var wsMv = workbook.Worksheets.Add("Bảng kê mua vào 01-2");
            BuildBangKeMuaVaoSheet(wsMv, model, kyStr);

            // SHEET 3: BẢNG KÊ BÁN RA (01-1/GTGT)
            var wsBr = workbook.Worksheets.Add("Bảng kê bán ra 01-1");
            BuildBangKeBanRaSheet(wsBr, model, kyStr);

            // SHEET 4: PHỤ LỤC GIẢM THUẾ 8% (NQ 110)
            var ws8 = workbook.Worksheets.Add("Phụ lục 8% NQ110");
            BuildPhuLuc8Sheet(ws8, model, kyStr);

            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            return await Task.FromResult(stream.ToArray());
        }

        private void BuildToKhaiSheet(XLWorkbook workbook, TaxDeclarationViewModel model, string kyStr)
        {
            var ws = workbook.Worksheets.Add("Tờ khai 01_GTGT");
            ws.TabColor = XLColor.FromHtml("#1E3A8A");
            ws.ShowGridLines = true;

            ws.Cell("A1").Value = "CỘNG HÒA XÃ HỘI CHỦ NGHĨA VIỆT NAM";
            ws.Cell("A2").Value = "Độc lập - Tự do - Hạnh phúc";
            ws.Range("A1:E1").Merge().Style.Font.SetBold(true).Font.SetFontSize(10.5).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            ws.Range("A2:E2").Merge().Style.Font.SetItalic(true).Font.SetFontSize(9.5).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

            ws.Cell("A4").Value = "TỜ KHAI THUẾ GIÁ TRỊ GIA TĂNG";
            ws.Range("A4:E4").Merge().Style.Font.SetBold(true).Font.SetFontSize(14).Font.SetFontColor(XLColor.FromHtml("#1E3A8A")).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

            // Tiêu đề căn cứ pháp lý động theo phiên bản
            ws.Cell("A5").Value = $"(Mẫu số 01/GTGT - Ban hành kèm theo {model.LegalDocFullTitle})";
            ws.Range("A5:E5").Merge().Style.Font.SetItalic(true).Font.SetFontSize(9).Font.SetFontColor(XLColor.FromHtml("#64748B")).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

            ws.Cell("A6").Value = $"[01a] Kỳ tính thuế: {kyStr} (Từ ngày {model.FromDate:dd/MM/yyyy} đến ngày {model.ToDate:dd/MM/yyyy})";
            ws.Range("A6:E6").Merge().Style.Font.SetBold(true).Font.SetFontSize(10).Font.SetFontColor(XLColor.FromHtml("#0F172A")).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

            ws.Cell("A8").Value = $"[02] Người nộp thuế: {model.CompanyName.ToUpper()}";
            ws.Cell("A8").Style.Font.SetBold(true).Font.SetFontSize(10);
            ws.Cell("A9").Value = $"[03] Mã số thuế: {model.TaxCode}";
            ws.Cell("A9").Style.Font.SetBold(true).Font.SetFontSize(10);
            ws.Cell("A10").Value = $"[04] Địa chỉ trụ sở: {model.Address}";
            ws.Cell("A10").Style.Font.SetFontSize(9.5);

            int r = 12;
            ws.Cell(r, 1).Value = "STT";
            ws.Cell(r, 2).Value = "Chỉ tiêu kê khai thuế GTGT";
            ws.Cell(r, 3).Value = "Mã CT";
            ws.Cell(r, 4).Value = "Giá trị HHDV (chưa thuế)";
            ws.Cell(r, 5).Value = "Thuế GTGT";
            ws.Range(r, 1, r, 5).Style.Font.SetBold(true).Font.SetFontSize(9.5).Fill.SetBackgroundColor(XLColor.FromHtml("#1E3A8A")).Font.SetFontColor(XLColor.White).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            r++;

            void AddRow(string stt, string name, string code, decimal? amount, decimal? tax, bool isHeader = false, bool isDanger = false, bool isSuccess = false)
            {
                ws.Cell(r, 1).Value = stt;
                ws.Cell(r, 2).Value = name;
                ws.Cell(r, 3).Value = code;
                if (amount.HasValue) ws.Cell(r, 4).SetValue(amount.Value);
                if (tax.HasValue) ws.Cell(r, 5).SetValue(tax.Value);

                ws.Cell(r, 1).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                ws.Cell(r, 3).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                ws.Cell(r, 4).Style.NumberFormat.Format = "#,##0";
                ws.Cell(r, 5).Style.NumberFormat.Format = "#,##0";

                if (isHeader)
                {
                    ws.Range(r, 1, r, 5).Style.Font.SetBold(true).Fill.SetBackgroundColor(XLColor.FromHtml("#E0E7FF"));
                }
                else if (isDanger)
                {
                    ws.Range(r, 1, r, 5).Style.Font.SetBold(true).Fill.SetBackgroundColor(XLColor.FromHtml("#FEE2E2"));
                    ws.Cell(r, 5).Style.Font.SetFontColor(XLColor.FromHtml("#DC2626"));
                }
                else if (isSuccess)
                {
                    ws.Range(r, 1, r, 5).Style.Font.SetBold(true).Fill.SetBackgroundColor(XLColor.FromHtml("#DCFCE7"));
                    ws.Cell(r, 5).Style.Font.SetFontColor(XLColor.FromHtml("#16A34A"));
                }

                for (int c = 1; c <= 5; c++)
                {
                    ws.Cell(r, c).Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin);
                    ws.Cell(r, c).Style.Border.OutsideBorderColor = XLColor.FromHtml("#CBD5E1");
                }
                r++;
            }

            AddRow("", "A. KHÔNG PHÁT SINH HOẠT ĐỘNG MUA, BÁN TRONG KỲ", "", null, null, true);
            AddRow("", "Không phát sinh hoạt động mua, bán trong kỳ", "[21]", null, null);
            AddRow("", "B. THUẾ GTGT ĐƯỢC KHẤU TRỪ KỲ TRƯỚC CHUYỂN SANG", "", null, null, true);
            AddRow("", "Thuế GTGT còn được khấu trừ kỳ trước chuyển sang", "[22]", null, model.Target22_CarriedOverDeduction);
            AddRow("", "C. HOẠT ĐỘNG MUA VÀO TRONG KỲ", "", null, null, true);
            AddRow("1", "Giá trị và thuế GTGT của hàng hóa, dịch vụ mua vào", "[23], [24]", model.Target23_PurchaseAmountBeforeTax, model.Target24_PurchaseTaxAmount);
            AddRow("", "Tổng số thuế GTGT của HHDV mua vào được khấu trừ kỳ này", "[25]", null, model.Target25_DeductibleTaxAmount);
            AddRow("", "D. HOẠT ĐỘNG BÁN RA TRONG KỲ", "", null, null, true);
            AddRow("a", "Hàng hóa, dịch vụ bán ra không chịu thuế GTGT", "[26]", model.Target26_SalesAmount0, null);
            AddRow("b", "Hàng hóa, dịch vụ bán ra chịu thuế suất 0%", "[27]", model.Target27_SalesAmount5, null);
            AddRow("c", "Hàng hóa, dịch vụ bán ra chịu thuế suất 5%", "[28], [29]", model.Target28_SalesTax5, model.Target29_SalesAmount8And10);
            AddRow("d", "Hàng hóa, dịch vụ bán ra chịu thuế suất 8% (NQ 110)", "[30], [31]", model.Target30_SalesTax8And10, model.Target31_SalesAmountNoTax);
            AddRow("e", "Hàng hóa, dịch vụ bán ra chịu thuế suất 10%", "[32], [33]", model.Target32_SalesAmount10, model.Target32a_SalesTax10);
            AddRow("f", "Khoản ghi trên hóa đơn không tính thuế", "[32b]", model.Target32b_SalesAmountNoTax, null);
            AddRow("", "Tổng doanh thu và thuế GTGT của HHDV bán ra", "[34], [35]", model.Target34_TotalSalesAmount, model.Target35_TotalSalesTax, true);
            AddRow("", "E. XÁC ĐỊNH NGHĨA VỤ THUẾ GTGT PHẢI NỘP", "", null, null, true);
            AddRow("1", "Thuế GTGT phát sinh trong kỳ ([35] - [25])", "[36]", null, model.Target36_OutputTax);
            AddRow("2", "Thuế GTGT còn phải nộp trong kỳ", "[40]", null, model.Target40_TaxToPay, isDanger: model.Target40_TaxToPay > 0);
            AddRow("3", "Thuế GTGT còn được khấu trừ chuyển kỳ sau", "[43]", null, model.Target43_CarriedForwardDeduction, isSuccess: model.Target43_CarriedForwardDeduction > 0);

            // Preset column widths tối ưu tốc độ
            ws.Column(1).Width = 8;
            ws.Column(2).Width = 55;
            ws.Column(3).Width = 14;
            ws.Column(4).Width = 24;
            ws.Column(5).Width = 24;
        }

        private void BuildBangKeMuaVaoSheet(IXLWorksheet ws, TaxDeclarationViewModel model, string kyStr)
        {
            ws.ShowGridLines = true;
            ws.TabColor = XLColor.FromHtml("#059669");

            ws.Cell("A1").Value = $"ĐƠN VỊ: {model.CompanyName.ToUpper()}";
            ws.Cell("A1").Style.Font.Bold = true;
            ws.Cell("A1").Style.Font.FontSize = 10.5;
            ws.Range("A1:G1").Merge();

            ws.Cell("A2").Value = $"MÃ SỐ THUẾ: {model.TaxCode}  -  ĐỊA CHỈ: {model.Address}";
            ws.Cell("A2").Style.Font.Italic = true;
            ws.Cell("A2").Style.Font.FontSize = 9.5;
            ws.Range("A2:G2").Merge();

            ws.Cell("A4").Value = "BẢNG KÊ HÓA ĐƠN CHỨNG TỪ HÀNG HÓA DỊCH VỤ MUA VÀO (MẪU 01-2/GTGT)";
            ws.Cell("A4").Style.Font.Bold = true;
            ws.Cell("A4").Style.Font.FontSize = 14;
            ws.Cell("A4").Style.Font.FontColor = XLColor.FromHtml("#059669");
            ws.Range("A4:J4").Merge().Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

            ws.Cell("A5").Value = $"Kỳ tính thuế: {kyStr} (Căn cứ: {model.LegalDocRef})  |  Tổng số: {model.PurchaseInvoices.Count:N0} dòng kê khai";
            ws.Cell("A5").Style.Font.Italic = true;
            ws.Cell("A5").Style.Font.FontSize = 9.5;
            ws.Range("A5:J5").Merge().Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

            int startRow = 7;
            string[] headers = new[]
            {
                "STT", "Ký hiệu HĐ", "Số HĐ", "Ngày lập", "Tên người bán",
                "MST người bán", "Doanh số chưa thuế", "Thuế suất", "Tiền thuế GTGT", "Hình thức TT / Ghi chú"
            };

            var headerFill = XLColor.FromHtml("#059669");
            for (int col = 0; col < headers.Length; col++)
            {
                var cell = ws.Cell(startRow, col + 1);
                cell.Value = headers[col];
                cell.Style.Font.Bold = true;
                cell.Style.Font.FontSize = 9.5;
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Fill.BackgroundColor = headerFill;
                cell.Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                cell.Style.Alignment.SetVertical(XLAlignmentVerticalValues.Center);
                cell.Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin);
            }

            int r = startRow + 1;
            int stt = 1;
            foreach (var item in model.PurchaseInvoices)
            {
                ws.Cell(r, 1).SetValue(stt++);
                ws.Cell(r, 2).SetValue(item.InvoiceSymbol);
                ws.Cell(r, 3).SetValue(item.InvoiceNumber);
                ws.Cell(r, 4).SetValue(item.IssueDate);
                ws.Cell(r, 5).SetValue(item.SellerName);
                ws.Cell(r, 6).SetValue(item.SellerTaxCode);
                ws.Cell(r, 7).SetValue(item.AmountBeforeTax);
                ws.Cell(r, 8).SetValue(item.TaxRate);
                ws.Cell(r, 9).SetValue(item.TaxAmount);
                ws.Cell(r, 10).SetValue(item.Note);

                ws.Cell(r, 1).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                ws.Cell(r, 2).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                ws.Cell(r, 3).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                ws.Cell(r, 4).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                ws.Cell(r, 6).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                ws.Cell(r, 7).Style.NumberFormat.Format = "#,##0";
                ws.Cell(r, 8).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                ws.Cell(r, 9).Style.NumberFormat.Format = "#,##0";

                if (item.IsCashPaymentWarning)
                {
                    ws.Range(r, 1, r, 10).Style.Fill.BackgroundColor = XLColor.FromHtml("#FEE2E2");
                    ws.Cell(r, 10).Style.Font.SetBold(true).Font.SetFontColor(XLColor.FromHtml("#DC2626"));
                }

                for (int c = 1; c <= 10; c++)
                {
                    ws.Cell(r, c).Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin);
                    ws.Cell(r, c).Style.Border.OutsideBorderColor = XLColor.FromHtml("#CBD5E1");
                }
                r++;
            }

            int[] widths = { 6, 14, 12, 14, 40, 18, 22, 12, 20, 45 };
            for (int i = 0; i < widths.Length; i++) ws.Column(i + 1).Width = widths[i];
        }

        private void BuildBangKeBanRaSheet(IXLWorksheet ws, TaxDeclarationViewModel model, string kyStr)
        {
            ws.ShowGridLines = true;
            ws.TabColor = XLColor.FromHtml("#047857");

            ws.Cell("A1").Value = $"ĐƠN VỊ: {model.CompanyName.ToUpper()}";
            ws.Cell("A1").Style.Font.Bold = true;
            ws.Cell("A1").Style.Font.FontSize = 10.5;
            ws.Range("A1:G1").Merge();

            ws.Cell("A2").Value = $"MÃ SỐ THUẾ: {model.TaxCode}  -  ĐỊA CHỈ: {model.Address}";
            ws.Cell("A2").Style.Font.Italic = true;
            ws.Cell("A2").Style.Font.FontSize = 9.5;
            ws.Range("A2:G2").Merge();

            ws.Cell("A4").Value = "BẢNG KÊ HÓA ĐƠN CHỨNG TỪ HÀNG HÓA DỊCH VỤ BÁN RA (MẪU 01-1/GTGT)";
            ws.Cell("A4").Style.Font.Bold = true;
            ws.Cell("A4").Style.Font.FontSize = 14;
            ws.Cell("A4").Style.Font.FontColor = XLColor.FromHtml("#047857");
            ws.Range("A4:J4").Merge().Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

            ws.Cell("A5").Value = $"Kỳ tính thuế: {kyStr} (Căn cứ: {model.LegalDocRef})  |  Tổng số: {model.SoldInvoices.Count:N0} dòng kê khai";
            ws.Cell("A5").Style.Font.Italic = true;
            ws.Cell("A5").Style.Font.FontSize = 9.5;
            ws.Range("A5:J5").Merge().Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

            int startRow = 7;
            string[] headers = new[]
            {
                "STT", "Ký hiệu HĐ", "Số HĐ", "Ngày lập", "Tên người mua",
                "MST người mua", "Doanh số chưa thuế", "Thuế suất", "Tiền thuế GTGT", "Tên hàng hóa, dịch vụ / Ghi chú"
            };

            var headerFill = XLColor.FromHtml("#047857");
            for (int col = 0; col < headers.Length; col++)
            {
                var cell = ws.Cell(startRow, col + 1);
                cell.Value = headers[col];
                cell.Style.Font.Bold = true;
                cell.Style.Font.FontSize = 9.5;
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Fill.BackgroundColor = headerFill;
                cell.Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                cell.Style.Alignment.SetVertical(XLAlignmentVerticalValues.Center);
                cell.Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin);
            }

            int r = startRow + 1;
            int stt = 1;
            foreach (var item in model.SoldInvoices)
            {
                ws.Cell(r, 1).SetValue(stt++);
                ws.Cell(r, 2).SetValue(item.InvoiceSymbol);
                ws.Cell(r, 3).SetValue(item.InvoiceNumber);
                ws.Cell(r, 4).SetValue(item.IssueDate);
                ws.Cell(r, 5).SetValue(item.BuyerName);
                ws.Cell(r, 6).SetValue(item.BuyerTaxCode);
                ws.Cell(r, 7).SetValue(item.AmountBeforeTax);
                ws.Cell(r, 8).SetValue(item.TaxRate);
                ws.Cell(r, 9).SetValue(item.TaxAmount);
                ws.Cell(r, 10).SetValue(item.Note);

                ws.Cell(r, 1).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                ws.Cell(r, 2).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                ws.Cell(r, 3).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                ws.Cell(r, 4).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                ws.Cell(r, 6).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                ws.Cell(r, 7).Style.NumberFormat.Format = "#,##0";
                ws.Cell(r, 8).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                ws.Cell(r, 9).Style.NumberFormat.Format = "#,##0";

                for (int c = 1; c <= 10; c++)
                {
                    ws.Cell(r, c).Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin);
                    ws.Cell(r, c).Style.Border.OutsideBorderColor = XLColor.FromHtml("#CBD5E1");
                }
                r++;
            }

            int[] widths = { 6, 14, 12, 14, 40, 18, 22, 12, 20, 45 };
            for (int i = 0; i < widths.Length; i++) ws.Column(i + 1).Width = widths[i];
        }

        private void BuildPhuLuc8Sheet(IXLWorksheet ws, TaxDeclarationViewModel model, string kyStr)
        {
            ws.ShowGridLines = true;
            ws.TabColor = XLColor.FromHtml("#D97706");

            ws.Cell("A1").Value = $"ĐƠN VỊ: {model.CompanyName.ToUpper()}";
            ws.Cell("A1").Style.Font.Bold = true;
            ws.Cell("A1").Style.Font.FontSize = 10.5;
            ws.Range("A1:G1").Merge();

            ws.Cell("A2").Value = $"MÃ SỐ THUẾ: {model.TaxCode}  -  ĐỊA CHỈ: {model.Address}";
            ws.Cell("A2").Style.Font.Italic = true;
            ws.Cell("A2").Style.Font.FontSize = 9.5;
            ws.Range("A2:G2").Merge();

            ws.Cell("A4").Value = "PHỤ LỤC GIẢM THUẾ GIÁ TRỊ GIA TĂNG (THEO NGHỊ QUYẾT 110/2025/UBTVQH15)";
            ws.Cell("A4").Style.Font.Bold = true;
            ws.Cell("A4").Style.Font.FontSize = 13.5;
            ws.Cell("A4").Style.Font.FontColor = XLColor.FromHtml("#D97706");
            ws.Range("A4:H4").Merge().Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

            ws.Cell("A5").Value = $"Kỳ tính thuế: {kyStr}  |  Tổng số thuế GTGT đã giảm: {model.Sales8ReducedTax:N0} đ";
            ws.Cell("A5").Style.Font.Italic = true;
            ws.Cell("A5").Style.Font.FontSize = 9.5;
            ws.Range("A5:H5").Merge().Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

            int startRow = 7;
            string[] headers = new[]
            {
                "STT", "Tên hàng hóa, dịch vụ", "Ký hiệu - Số HĐ", "Ngày lập",
                "Giá trị trước thuế", "Thuế suất 8%", "Tiền thuế sau giảm", "Tiền thuế giảm (2%)"
            };

            var headerFill = XLColor.FromHtml("#D97706");
            for (int col = 0; col < headers.Length; col++)
            {
                var cell = ws.Cell(startRow, col + 1);
                cell.Value = headers[col];
                cell.Style.Font.Bold = true;
                cell.Style.Font.FontSize = 9.5;
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Fill.BackgroundColor = headerFill;
                cell.Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                cell.Style.Alignment.SetVertical(XLAlignmentVerticalValues.Center);
                cell.Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin);
            }

            int r = startRow + 1;
            int stt = 1;
            foreach (var item in model.Tax8PercentReductionItems)
            {
                ws.Cell(r, 1).SetValue(stt++);
                ws.Cell(r, 2).SetValue(item.ItemName);
                ws.Cell(r, 3).SetValue($"{item.InvoiceSymbol} - {item.InvoiceNumber}");
                ws.Cell(r, 4).SetValue(item.IssueDate);
                ws.Cell(r, 5).SetValue(item.AmountBeforeTax);
                ws.Cell(r, 6).SetValue("8%");
                ws.Cell(r, 7).SetValue(item.TaxAmount8Percent);
                ws.Cell(r, 8).SetValue(item.TaxReducedAmount2Percent);

                ws.Cell(r, 1).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                ws.Cell(r, 3).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                ws.Cell(r, 4).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                ws.Cell(r, 5).Style.NumberFormat.Format = "#,##0";
                ws.Cell(r, 6).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                ws.Cell(r, 7).Style.NumberFormat.Format = "#,##0";
                ws.Cell(r, 8).Style.NumberFormat.Format = "#,##0";

                for (int c = 1; c <= 8; c++)
                {
                    ws.Cell(r, c).Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin);
                    ws.Cell(r, c).Style.Border.OutsideBorderColor = XLColor.FromHtml("#CBD5E1");
                }
                r++;
            }

            int[] widths = { 6, 45, 20, 14, 22, 14, 22, 22 };
            for (int i = 0; i < widths.Length; i++) ws.Column(i + 1).Width = widths[i];
        }

        /// <summary>
        /// Sinh Báo cáo phiên bản pháp lý (Legal version report) theo Mục 8.8
        /// </summary>
        public TaxLegalVersionReport GenerateLegalReport(TaxDeclarationViewModel model)
        {
            var report = new TaxLegalVersionReport
            {
                FormVersionCode = model.FormVersionCode,
                LegalDocument = model.LegalDocFullTitle,
                TaxPeriod = model.PeriodDescription,
                GeneratedAt = DateTime.Now,
                AppliedRulesSummary = $"Áp dụng {model.ValidationAlerts.Count} thông báo kiểm tra tuân thủ pháp luật"
            };

            report.AppliedParameters["CashPaymentThreshold"] = $"{model.CashThresholdApplied:N0} VNĐ";
            report.AppliedParameters["XmlTemplateCode"] = model.XmlTemplateCode;
            report.AppliedParameters["XmlXsdVersion"] = model.XmlXsdVersion;

            foreach (var alert in model.ValidationAlerts)
            {
                report.ValidationResults.Add(new TaxRuleValidationResult
                {
                    RuleCode = "AUDIT_CHECK",
                    RuleName = "Kiểm tra tuân thủ pháp luật",
                    Severity = alert.Contains("LỖI ĐỎ") ? "Error" : "Warning",
                    Message = alert,
                    LegalBasis = model.LegalDocRef,
                    IsPassed = !alert.Contains("LỖI ĐỎ")
                });
            }

            return report;
        }
    }
}
