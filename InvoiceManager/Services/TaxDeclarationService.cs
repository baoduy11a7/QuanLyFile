using ClosedXML.Excel;
using InvoiceManager.Data;
using InvoiceManager.Models.Entities;
using InvoiceManager.Models.ViewModels;
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

        public TaxDeclarationService(ApplicationDbContext db)
        {
            _db = db;
        }

        public async Task<TaxDeclarationViewModel> CalculateDeclarationAsync(
            int taxAccountId,
            string periodType,
            int periodValue,
            int year,
            DateTime? customFrom = null,
            DateTime? customTo = null,
            decimal carriedOverTax = 0)
        {
            var taxAccount = await _db.TaxAccounts.FindAsync(taxAccountId);

            DateTime fromDate;
            DateTime toDate;

            if (periodType.Equals("Quarter", StringComparison.OrdinalIgnoreCase))
            {
                int startMonth = ((periodValue - 1) * 3) + 1;
                fromDate = new DateTime(year, startMonth, 1);
                toDate = fromDate.AddMonths(3).AddDays(-1);
            }
            else if (periodType.Equals("Month", StringComparison.OrdinalIgnoreCase))
            {
                fromDate = new DateTime(year, periodValue, 1);
                toDate = fromDate.AddMonths(1).AddDays(-1);
            }
            else
            {
                fromDate = customFrom ?? new DateTime(year, 1, 1);
                toDate = customTo ?? new DateTime(year, 12, 31);
            }

            var startOfDay = fromDate.Date;
            var endOfDay = toDate.Date.AddDays(1).AddTicks(-1);

            var invoices = await _db.Invoices
                .Include(i => i.Details)
                .Where(i => i.TaxAccountId == taxAccountId && i.IssueDate >= startOfDay && i.IssueDate <= endOfDay)
                .ToListAsync();

            var muaVao = invoices.Where(i => i.InvoiceType != "BanRa").ToList();
            var banRa = invoices.Where(i => i.InvoiceType == "BanRa").ToList();

            var model = new TaxDeclarationViewModel
            {
                TaxCode = taxAccount?.TaxCode ?? "",
                CompanyName = taxAccount?.CompanyName ?? "",
                Address = taxAccount?.Address ?? "",
                PeriodType = periodType,
                PeriodValue = periodValue,
                Year = year,
                FromDate = fromDate,
                ToDate = toDate,
                Target22_CarriedOverDeduction = carriedOverTax,
                TotalPurchaseInvoices = muaVao.Count,
                TotalSalesInvoices = banRa.Count
            };

            // 1. Tính toán Mua vào
            model.Target23_PurchaseAmountBeforeTax = muaVao.Sum(i => i.AmountBeforeTax);
            model.Target24_PurchaseTaxAmount = muaVao.Sum(i => i.TaxAmount);
            model.Target25_DeductibleTaxAmount = model.Target24_PurchaseTaxAmount;

            // 2. Tính toán Bán ra
            foreach (var inv in banRa)
            {
                var details = inv.Details != null && inv.Details.Any()
                    ? inv.Details.ToList()
                    : new List<InvoiceDetail>
                    {
                        new InvoiceDetail
                        {
                            AmountBeforeTax = inv.AmountBeforeTax,
                            TaxAmount = inv.TaxAmount,
                            TaxRate = inv.AmountBeforeTax > 0 ? Math.Round(inv.TaxAmount / inv.AmountBeforeTax * 100) : 0
                        }
                    };

                foreach (var d in details)
                {
                    decimal rate = d.TaxRate;
                    if (rate == -1) // Không chịu thuế
                    {
                        model.Target21_KCTRevenue += d.AmountBeforeTax;
                    }
                    else if (rate == 0) // Thuế suất 0%
                    {
                        model.Target26_SalesAmount0 += d.AmountBeforeTax;
                    }
                    else if (rate == 5) // Thuế suất 5%
                    {
                        model.Target27_SalesAmount5 += d.AmountBeforeTax;
                        model.Target28_SalesTax5 += d.TaxAmount;
                    }
                    else if (rate == 8) // Thuế suất 8%
                    {
                        model.Target29_SalesAmount8And10 += d.AmountBeforeTax;
                        model.Target30_SalesTax8And10 += d.TaxAmount;

                        model.Sales8Amount += d.AmountBeforeTax;
                        model.Sales8Tax += d.TaxAmount;
                        model.Sales8ReducedTax += Math.Round(d.AmountBeforeTax * 0.02m, 0);
                    }
                    else if (rate >= 10) // Thuế suất 10%
                    {
                        model.Target29_SalesAmount8And10 += d.AmountBeforeTax;
                        model.Target30_SalesTax8And10 += d.TaxAmount;
                    }
                    else
                    {
                        // Khác hoặc không tính thuế
                        model.Target31_SalesAmountNoTax += d.AmountBeforeTax;
                    }
                }
            }

            // Phụ lục 8% mua vào
            foreach (var inv in muaVao)
            {
                var details = inv.Details != null && inv.Details.Any()
                    ? inv.Details.ToList()
                    : new List<InvoiceDetail>();

                var items8 = details.Where(d => Math.Round(d.TaxRate) == 8).ToList();
                if (!items8.Any() && inv.AmountBeforeTax > 0 && Math.Round(inv.TaxAmount / inv.AmountBeforeTax * 100) == 8)
                {
                    model.Purchase8Amount += inv.AmountBeforeTax;
                    model.Purchase8Tax += inv.TaxAmount;
                }
                else
                {
                    model.Purchase8Amount += items8.Sum(d => d.AmountBeforeTax);
                    model.Purchase8Tax += items8.Sum(d => d.TaxAmount);
                }
            }

            // 3. Danh sách hóa đơn Bảng kê Mua vào: Nhóm theo từng loại thuế suất trong hóa đơn (tách dòng nếu có nhiều mức thuế)
            var purchaseItems = new List<TaxInvoiceItem>();
            foreach (var m in muaVao.OrderBy(i => i.IssueDate).ThenBy(i => i.InvoiceNumber))
            {
                var details = m.Details.OrderBy(d => d.LineNumber).ToList();
                if (!details.Any())
                {
                    decimal defRate = m.AmountBeforeTax > 0 ? Math.Round((m.TaxAmount / m.AmountBeforeTax) * 100, 0) : 10;
                    details.Add(new InvoiceDetail
                    {
                        LineNumber = 1,
                        ItemName = $"Hàng hóa/dịch vụ theo HĐ số {m.InvoiceNumber}",
                        AmountBeforeTax = m.AmountBeforeTax,
                        TaxRate = defRate,
                        TaxAmount = m.TaxAmount,
                        TotalAmount = m.TotalAmount > 0 ? m.TotalAmount : (m.AmountBeforeTax + m.TaxAmount)
                    });
                }

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
                        Id = m.Id,
                        InvoiceSymbol = m.InvoiceSymbol,
                        InvoiceNumber = m.InvoiceNumber,
                        IssueDate = m.IssueDate.ToString("dd/MM/yyyy"),
                        SellerName = m.SellerName,
                        SellerTaxCode = m.SellerTaxCode,
                        BuyerName = m.BuyerName ?? "",
                        BuyerTaxCode = m.BuyerTaxCode ?? "",
                        ItemName = firstItem,
                        AmountBeforeTax = sumBefore,
                        TaxRate = rateStr,
                        TaxAmount = sumTax,
                        TotalAmount = sumTotal,
                        Note = firstItem
                    });
                }
            }
            model.PurchaseInvoices = purchaseItems;

            // 4. Danh sách hóa đơn Bảng kê Bán ra: Nhóm theo từng loại thuế suất trong hóa đơn (tách dòng nếu có nhiều mức thuế)
            var soldItems = new List<TaxInvoiceItem>();
            foreach (var b in banRa.OrderBy(i => i.IssueDate).ThenBy(i => i.InvoiceNumber))
            {
                var details = b.Details.OrderBy(d => d.LineNumber).ToList();
                if (!details.Any())
                {
                    decimal defRate = b.AmountBeforeTax > 0 ? Math.Round((b.TaxAmount / b.AmountBeforeTax) * 100, 0) : 10;
                    details.Add(new InvoiceDetail
                    {
                        LineNumber = 1,
                        ItemName = $"Hàng hóa/dịch vụ theo HĐ số {b.InvoiceNumber}",
                        AmountBeforeTax = b.AmountBeforeTax,
                        TaxRate = defRate,
                        TaxAmount = b.TaxAmount,
                        TotalAmount = b.TotalAmount > 0 ? b.TotalAmount : (b.AmountBeforeTax + b.TaxAmount)
                    });
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
                        Id = b.Id,
                        InvoiceSymbol = b.InvoiceSymbol,
                        InvoiceNumber = b.InvoiceNumber,
                        IssueDate = b.IssueDate.ToString("dd/MM/yyyy"),
                        SellerName = b.SellerName,
                        SellerTaxCode = b.SellerTaxCode,
                        BuyerName = b.BuyerName ?? "",
                        BuyerTaxCode = b.BuyerTaxCode ?? "",
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

            // 5. Phụ lục giảm thuế 8% theo NQ 110
            var redItems = new List<Tax8ReductionItem>();
            foreach (var inv in banRa)
            {
                var details8 = inv.Details?.Where(d => Math.Round(d.TaxRate) == 8).ToList();
                if (details8 != null && details8.Any())
                {
                    foreach (var d in details8)
                    {
                        redItems.Add(new Tax8ReductionItem
                        {
                            ItemName = d.ItemName,
                            InvoiceSymbol = inv.InvoiceSymbol,
                            InvoiceNumber = inv.InvoiceNumber,
                            IssueDate = inv.IssueDate.ToString("dd/MM/yyyy"),
                            AmountBeforeTax = d.AmountBeforeTax,
                            TaxAmount8Percent = d.TaxAmount,
                            TaxReducedAmount2Percent = Math.Round(d.AmountBeforeTax * 0.02m, 0)
                        });
                    }
                }
                else if (inv.AmountBeforeTax > 0 && Math.Round(inv.TaxAmount / inv.AmountBeforeTax * 100) == 8)
                {
                    redItems.Add(new Tax8ReductionItem
                    {
                        ItemName = inv.Details?.FirstOrDefault()?.ItemName ?? "Hàng hóa dịch vụ giảm thuế 8%",
                        InvoiceSymbol = inv.InvoiceSymbol,
                        InvoiceNumber = inv.InvoiceNumber,
                        IssueDate = inv.IssueDate.ToString("dd/MM/yyyy"),
                        AmountBeforeTax = inv.AmountBeforeTax,
                        TaxAmount8Percent = inv.TaxAmount,
                        TaxReducedAmount2Percent = Math.Round(inv.AmountBeforeTax * 0.02m, 0)
                    });
                }
            }
            model.Tax8PercentReductionItems = redItems;

            return model;
        }

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
            sb.AppendLine("        <maTKhai>01_GTGT_TT80</maTKhai>");
            sb.AppendLine("        <tenTKhai>Tờ khai thuế giá trị gia tăng (Mẫu 01/GTGT)</tenTKhai>");
            sb.AppendLine("        <moTaBMau>Thông tư số 80/2021/TT-BTC ngày 29/9/2021 của Bộ Tài chính</moTaBMau>");
            sb.AppendLine("        <pbanTKhaiXML>2.0.0</pbanTKhaiXML>");
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
            sb.AppendLine($"      <ct21>{(long)model.Target21_KCTRevenue}</ct21>");
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
            sb.AppendLine($"      <ct32>{(long)model.Target32_TotalSalesAmount}</ct32>");
            sb.AppendLine($"      <ct33>{(long)model.Target33_TotalSalesTax}</ct33>");
            sb.AppendLine($"      <ct34>{(long)model.Target34_OutputTax}</ct34>");
            sb.AppendLine($"      <ct35>{(long)model.Target35_InputTax}</ct35>");
            sb.AppendLine($"      <ct36>{(long)model.Target36_TaxPayable}</ct36>");
            sb.AppendLine($"      <ct40>{(long)model.Target40_TaxToPay}</ct40>");
            sb.AppendLine($"      <ct43>{(long)model.Target43_CarriedForwardDeduction}</ct43>");
            sb.AppendLine("    </CTieuTKhaiChinh>");
            sb.AppendLine("  </HSoKhaiThue>");
            sb.AppendLine("</HSoThueDTu>");
            return sb.ToString();
        }

        public async Task<byte[]> ExportDeclarationExcelAsync(TaxDeclarationViewModel model, int taxAccountId)
        {
            using var workbook = new XLWorkbook();
            string kyStr = model.PeriodType == "Quarter" ? $"Quý {model.PeriodValue} năm {model.Year}" : $"Tháng {model.PeriodValue} năm {model.Year}";

            // ==========================================
            // SHEET 1: TỜ KHAI 01/GTGT
            // ==========================================
            var ws = workbook.Worksheets.Add("Tờ khai 01_GTGT");
            ws.TabColor = XLColor.FromHtml("#1E3A8A");
            ws.ShowGridLines = true;

            ws.Cell("A1").Value = "CỘNG HÒA XÃ HỘI CHỦ NGHĨA VIỆT NAM";
            ws.Cell("A2").Value = "Độc lập - Tự do - Hạnh phúc";
            ws.Range("A1:E1").Merge().Style.Font.SetBold(true).Font.SetFontSize(10.5).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            ws.Range("A2:E2").Merge().Style.Font.SetItalic(true).Font.SetFontSize(9.5).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            ws.Row(1).Height = 20;
            ws.Row(2).Height = 18;

            ws.Cell("A4").Value = "TỜ KHAI THUẾ GIÁ TRỊ GIA TĂNG";
            ws.Range("A4:E4").Merge().Style.Font.SetBold(true).Font.SetFontSize(14).Font.SetFontColor(XLColor.FromHtml("#1E3A8A")).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            ws.Row(4).Height = 26;

            ws.Cell("A5").Value = "(Mẫu số 01/GTGT - Ban hành kèm theo Thông tư số 80/2021/TT-BTC của Bộ Tài chính)";
            ws.Range("A5:E5").Merge().Style.Font.SetItalic(true).Font.SetFontSize(9).Font.SetFontColor(XLColor.FromHtml("#64748B")).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            ws.Row(5).Height = 18;

            ws.Cell("A6").Value = $"[01a] Kỳ tính thuế: {kyStr} (Từ ngày {model.FromDate:dd/MM/yyyy} đến ngày {model.ToDate:dd/MM/yyyy})";
            ws.Range("A6:E6").Merge().Style.Font.SetBold(true).Font.SetFontSize(10).Font.SetFontColor(XLColor.FromHtml("#0F172A")).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            ws.Row(6).Height = 22;

            ws.Cell("A8").Value = $"[02] Người nộp thuế: {model.CompanyName.ToUpper()}";
            ws.Cell("A8").Style.Font.SetBold(true).Font.SetFontSize(10).Font.SetFontColor(XLColor.FromHtml("#0F172A"));
            ws.Cell("A9").Value = $"[03] Mã số thuế: {model.TaxCode}";
            ws.Cell("A9").Style.Font.SetBold(true).Font.SetFontSize(10).Font.SetFontColor(XLColor.FromHtml("#0F172A"));
            ws.Cell("A10").Value = $"[04] Địa chỉ trụ sở: {model.Address}";
            ws.Cell("A10").Style.Font.SetFontSize(9.5).Font.SetFontColor(XLColor.FromHtml("#334155"));
            ws.Row(8).Height = 20;
            ws.Row(9).Height = 20;
            ws.Row(10).Height = 20;

            int r = 12;
            ws.Cell(r, 1).Value = "STT";
            ws.Cell(r, 2).Value = "Chỉ tiêu kê khai thuế GTGT";
            ws.Cell(r, 3).Value = "Mã CT";
            ws.Cell(r, 4).Value = "Giá trị HHDV (chưa thuế)";
            ws.Cell(r, 5).Value = "Thuế GTGT";
            ws.Range(r, 1, r, 5).Style.Font.SetBold(true).Font.SetFontSize(9.5).Fill.SetBackgroundColor(XLColor.FromHtml("#1E3A8A")).Font.SetFontColor(XLColor.White).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center).Alignment.SetVertical(XLAlignmentVerticalValues.Center);
            ws.Row(r).Height = 28;
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
                ws.Row(r).Height = 23;

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
                else
                {
                    ws.Range(r, 1, r, 5).Style.Font.SetFontSize(9.5).Font.SetFontColor(XLColor.FromHtml("#0F172A"));
                    if (r % 2 == 1) ws.Range(r, 1, r, 5).Style.Fill.SetBackgroundColor(XLColor.FromHtml("#F8FAFC"));
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
            AddRow("a", "Hàng hóa, dịch vụ bán ra không chịu thuế GTGT", "[26]", model.Target21_KCTRevenue, null);
            AddRow("b", "Hàng hóa, dịch vụ bán ra chịu thuế suất 0%", "[27]", model.Target26_SalesAmount0, null);
            AddRow("c", "Hàng hóa, dịch vụ bán ra chịu thuế suất 5%", "[28], [29]", model.Target27_SalesAmount5, model.Target28_SalesTax5);
            AddRow("d", "Hàng hóa, dịch vụ bán ra chịu thuế suất 8% (NQ 110/2023/QH15)", "[30], [31]", model.Target29_SalesAmount8And10, model.Target30_SalesTax8And10);
            AddRow("e", "Hàng hóa, dịch vụ bán ra chịu thuế suất 10%", "[32], [33]", model.Target29_SalesAmount8And10, model.Target30_SalesTax8And10);
            AddRow("f", "Hàng hóa, dịch vụ bán ra không tính thuế", "[31]", model.Target31_SalesAmountNoTax, null);
            AddRow("", "Tổng doanh thu và thuế GTGT của HHDV bán ra", "[34], [35]", model.Target32_TotalSalesAmount, model.Target33_TotalSalesTax, true);
            AddRow("", "E. XÁC ĐỊNH NGHĨA VỤ THUẾ GTGT PHẢI NỘP", "", null, null, true);
            AddRow("3", "Thuế GTGT phát sinh trong kỳ ([35] - [25])", "[36]", null, model.Target36_TaxPayable);
            AddRow("4", "Thuế GTGT còn phải nộp trong kỳ (Nếu [36] - [22] > 0)", "[40]", null, model.Target40_TaxToPay, false, true, false);
            AddRow("5", "Thuế GTGT còn được khấu trừ chuyển kỳ sau (Nếu [36] - [22] < 0)", "[43]", null, model.Target43_CarriedForwardDeduction, false, false, true);

            ws.Column(1).Width = 7;
            ws.Column(2).Width = 55;
            ws.Column(3).Width = 14;
            ws.Column(4).Width = 24;
            ws.Column(5).Width = 24;

            // Sheet 2: Bảng kê mua vào
            var wsMv = workbook.Worksheets.Add("Bảng kê mua vào");
            BuildBangKeMuaVaoSheet(wsMv, model, kyStr);

            // Sheet 3: Bảng kê bán ra
            var wsBr = workbook.Worksheets.Add("Bảng kê bán ra");
            BuildBangKeBanRaSheet(wsBr, model, kyStr);

            // Sheet 4: Phụ lục 8% (NQ 110)
            var ws8 = workbook.Worksheets.Add("Phụ lục 8% NQ110");
            BuildPhuLuc8Sheet(ws8, model, kyStr);

            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            return stream.ToArray();
        }

        private void BuildBangKeMuaVaoSheet(IXLWorksheet ws, TaxDeclarationViewModel model, string kyStr)
        {
            ws.ShowGridLines = true;
            ws.TabColor = XLColor.FromHtml("#059669");

            // 1. Thông tin công ty
            ws.Cell("A1").Value = $"ĐƠN VỊ: {model.CompanyName.ToUpper()}";
            ws.Cell("A1").Style.Font.Bold = true;
            ws.Cell("A1").Style.Font.FontSize = 10.5;
            ws.Cell("A1").Style.Font.FontColor = XLColor.FromHtml("#0F172A");
            ws.Range("A1:G1").Merge();
            ws.Row(1).Height = 22;

            ws.Cell("A2").Value = $"MÃ SỐ THUẾ: {model.TaxCode}  -  ĐỊA CHỈ: {model.Address}";
            ws.Cell("A2").Style.Font.Italic = true;
            ws.Cell("A2").Style.Font.FontSize = 9.5;
            ws.Cell("A2").Style.Font.FontColor = XLColor.FromHtml("#475569");
            ws.Range("A2:G2").Merge();
            ws.Row(2).Height = 20;

            ws.Row(3).Height = 10;

            // 2. Tiêu đề bảng kê
            ws.Cell("A4").Value = "BẢNG KÊ HÓA ĐƠN CHỨNG TỪ HÀNG HÓA DỊCH VỤ MUA VÀO";
            ws.Cell("A4").Style.Font.Bold = true;
            ws.Cell("A4").Style.Font.FontSize = 14;
            ws.Cell("A4").Style.Font.FontColor = XLColor.FromHtml("#059669");
            ws.Range("A4:G4").Merge().Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            ws.Row(4).Height = 28;

            ws.Cell("A5").Value = $"Kỳ tính thuế: {kyStr} (Từ ngày {model.FromDate:dd/MM/yyyy} đến ngày {model.ToDate:dd/MM/yyyy})  |  Tổng số: {model.PurchaseInvoices.Count:N0} dòng kê khai";
            ws.Cell("A5").Style.Font.Italic = true;
            ws.Cell("A5").Style.Font.FontSize = 9.5;
            ws.Cell("A5").Style.Font.FontColor = XLColor.FromHtml("#475569");
            ws.Range("A5:G5").Merge().Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            ws.Row(5).Height = 20;

            ws.Row(6).Height = 10;

            // 3. Tiêu đề cột
            int startRow = 7;
            string[] headers = new[]
            {
                "STT", "Ký hiệu HĐ", "Số HĐ", "Ngày lập", "Tên người bán",
                "MST người bán", "Doanh số chưa thuế", "Thuế suất", "Tiền thuế GTGT", "Tên hàng hóa, dịch vụ / Ghi chú"
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
                cell.Style.Border.OutsideBorderColor = XLColor.FromHtml("#047857");
            }
            ws.Row(startRow).Height = 30;

            int r = startRow + 1;
            int stt = 1;
            foreach (var item in model.PurchaseInvoices)
            {
                string soHd = long.TryParse(item.InvoiceNumber, out var sn) ? sn.ToString() : item.InvoiceNumber;
                string cleanRate = (item.TaxRate ?? "").Replace(".00%", "%").Trim();

                ws.Cell(r, 1).SetValue(stt++);
                ws.Cell(r, 2).SetValue(item.InvoiceSymbol);
                ws.Cell(r, 3).SetValue(soHd);
                ws.Cell(r, 4).SetValue(item.IssueDate);
                ws.Cell(r, 5).SetValue(item.SellerName);
                ws.Cell(r, 6).SetValue(item.SellerTaxCode);
                ws.Cell(r, 7).SetValue(item.AmountBeforeTax);
                ws.Cell(r, 8).SetValue(cleanRate);
                ws.Cell(r, 9).SetValue(item.TaxAmount);
                ws.Cell(r, 10).SetValue(item.ItemName);

                ws.Cell(r, 1).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                ws.Cell(r, 2).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                ws.Cell(r, 3).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                ws.Cell(r, 4).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                ws.Cell(r, 6).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                ws.Cell(r, 8).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

                ws.Cell(r, 1).Style.Font.SetBold(true).Font.SetFontColor(XLColor.FromHtml("#64748B"));
                ws.Cell(r, 6).Style.NumberFormat.Format = "@";
                ws.Cell(r, 7).Style.NumberFormat.Format = "#,##0";
                ws.Cell(r, 9).Style.NumberFormat.Format = "#,##0";
                ws.Cell(r, 9).Style.Font.SetBold(true);

                ws.Row(r).Height = 24;
                if (r % 2 == 1)
                {
                    ws.Range(r, 1, r, 10).Style.Fill.BackgroundColor = XLColor.FromHtml("#F8FAFC");
                }

                for (int c = 1; c <= 10; c++)
                {
                    ws.Cell(r, c).Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin);
                    ws.Cell(r, c).Style.Border.OutsideBorderColor = XLColor.FromHtml("#CBD5E1");
                }
                r++;
            }

            int endRow = r - 1;
            ws.Cell(r, 1).Value = "TỔNG CỘNG:";
            ws.Range(r, 1, r, 6).Merge().Style.Font.SetBold(true).Font.SetFontSize(10).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Right);
            ws.Cell(r, 7).FormulaA1 = endRow >= startRow + 1 ? $"SUM(G{startRow + 1}:G{endRow})" : "0";
            ws.Cell(r, 9).FormulaA1 = endRow >= startRow + 1 ? $"SUM(I{startRow + 1}:I{endRow})" : "0";
            ws.Cell(r, 7).Style.Font.Bold = true;
            ws.Cell(r, 9).Style.Font.Bold = true;
            ws.Cell(r, 7).Style.Font.FontSize = 10;
            ws.Cell(r, 9).Style.Font.FontSize = 10;
            ws.Cell(r, 7).Style.NumberFormat.Format = "#,##0";
            ws.Cell(r, 9).Style.NumberFormat.Format = "#,##0";
            ws.Range(r, 1, r, 10).Style.Fill.BackgroundColor = XLColor.FromHtml("#FEF3C7");
            ws.Range(r, 1, r, 10).Style.Border.SetTopBorder(XLBorderStyleValues.Thin);
            ws.Range(r, 1, r, 10).Style.Border.SetBottomBorder(XLBorderStyleValues.Double);
            ws.Range(r, 1, r, 10).Style.Border.TopBorderColor = XLColor.FromHtml("#94A3B8");
            ws.Range(r, 1, r, 10).Style.Border.BottomBorderColor = XLColor.FromHtml("#334155");
            ws.Row(r).Height = 28;

            // Chữ ký cuối bảng
            r += 2;
            ws.Cell(r, 7).Value = "Ngày ..... tháng ..... năm 2026";
            ws.Range(r, 7, r, 10).Merge().Style.Font.SetItalic(true).Font.SetFontSize(9.5).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            r++;
            ws.Cell(r, 2).Value = "NGƯỜI LẬP BIỂU";
            ws.Range(r, 2, r, 4).Merge().Style.Font.SetBold(true).Font.SetFontSize(9.5).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            ws.Cell(r, 7).Value = "NGƯỜI ĐẠI DIỆN THEO PHÁP LUẬT";
            ws.Range(r, 7, r, 10).Merge().Style.Font.SetBold(true).Font.SetFontSize(9.5).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            r++;
            ws.Cell(r, 2).Value = "(Ký, ghi rõ họ tên)";
            ws.Range(r, 2, r, 4).Merge().Style.Font.SetItalic(true).Font.SetFontSize(8.5).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            ws.Cell(r, 7).Value = "(Ký, đóng dấu, ghi rõ họ tên)";
            ws.Range(r, 7, r, 10).Merge().Style.Font.SetItalic(true).Font.SetFontSize(8.5).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

            ws.Column(1).Width = 7;
            ws.Column(2).Width = 14;
            ws.Column(3).Width = 12;
            ws.Column(4).Width = 14;
            ws.Column(5).Width = 42;
            ws.Column(6).Width = 18;
            ws.Column(7).Width = 22;
            ws.Column(8).Width = 13;
            ws.Column(9).Width = 20;
            ws.Column(10).Width = 45;
        }

        private void BuildBangKeBanRaSheet(IXLWorksheet ws, TaxDeclarationViewModel model, string kyStr)
        {
            ws.ShowGridLines = true;
            ws.TabColor = XLColor.FromHtml("#047857");

            // 1. Thông tin công ty
            ws.Cell("A1").Value = $"ĐƠN VỊ: {model.CompanyName.ToUpper()}";
            ws.Cell("A1").Style.Font.Bold = true;
            ws.Cell("A1").Style.Font.FontSize = 10.5;
            ws.Cell("A1").Style.Font.FontColor = XLColor.FromHtml("#0F172A");
            ws.Range("A1:G1").Merge();
            ws.Row(1).Height = 22;

            ws.Cell("A2").Value = $"MÃ SỐ THUẾ: {model.TaxCode}  -  ĐỊA CHỈ: {model.Address}";
            ws.Cell("A2").Style.Font.Italic = true;
            ws.Cell("A2").Style.Font.FontSize = 9.5;
            ws.Cell("A2").Style.Font.FontColor = XLColor.FromHtml("#475569");
            ws.Range("A2:G2").Merge();
            ws.Row(2).Height = 20;

            ws.Row(3).Height = 10;

            // 2. Tiêu đề bảng kê
            ws.Cell("A4").Value = "BẢNG KÊ HÓA ĐƠN CHỨNG TỪ HÀNG HÓA DỊCH VỤ BÁN RA";
            ws.Cell("A4").Style.Font.Bold = true;
            ws.Cell("A4").Style.Font.FontSize = 14;
            ws.Cell("A4").Style.Font.FontColor = XLColor.FromHtml("#047857");
            ws.Range("A4:G4").Merge().Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            ws.Row(4).Height = 28;

            ws.Cell("A5").Value = $"Kỳ tính thuế: {kyStr} (Từ ngày {model.FromDate:dd/MM/yyyy} đến ngày {model.ToDate:dd/MM/yyyy})  |  Tổng số: {model.SoldInvoices.Count:N0} dòng kê khai";
            ws.Cell("A5").Style.Font.Italic = true;
            ws.Cell("A5").Style.Font.FontSize = 9.5;
            ws.Cell("A5").Style.Font.FontColor = XLColor.FromHtml("#475569");
            ws.Range("A5:G5").Merge().Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            ws.Row(5).Height = 20;

            ws.Row(6).Height = 10;

            // 3. Tiêu đề cột
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
                cell.Style.Border.OutsideBorderColor = XLColor.FromHtml("#064E3B");
            }
            ws.Row(startRow).Height = 30;

            int r = startRow + 1;
            int stt = 1;
            foreach (var item in model.SoldInvoices)
            {
                string soHd = long.TryParse(item.InvoiceNumber, out var sn) ? sn.ToString() : item.InvoiceNumber;
                string cleanRate = (item.TaxRate ?? "").Replace(".00%", "%").Trim();

                ws.Cell(r, 1).SetValue(stt++);
                ws.Cell(r, 2).SetValue(item.InvoiceSymbol);
                ws.Cell(r, 3).SetValue(soHd);
                ws.Cell(r, 4).SetValue(item.IssueDate);
                ws.Cell(r, 5).SetValue(item.BuyerName);
                ws.Cell(r, 6).SetValue(item.BuyerTaxCode);
                ws.Cell(r, 7).SetValue(item.AmountBeforeTax);
                ws.Cell(r, 8).SetValue(cleanRate);
                ws.Cell(r, 9).SetValue(item.TaxAmount);
                ws.Cell(r, 10).SetValue(item.ItemName);

                ws.Cell(r, 1).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                ws.Cell(r, 2).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                ws.Cell(r, 3).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                ws.Cell(r, 4).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                ws.Cell(r, 6).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                ws.Cell(r, 8).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

                ws.Cell(r, 1).Style.Font.SetBold(true).Font.SetFontColor(XLColor.FromHtml("#64748B"));
                ws.Cell(r, 6).Style.NumberFormat.Format = "@";
                ws.Cell(r, 7).Style.NumberFormat.Format = "#,##0";
                ws.Cell(r, 9).Style.NumberFormat.Format = "#,##0";
                ws.Cell(r, 9).Style.Font.SetBold(true);

                ws.Row(r).Height = 24;
                if (r % 2 == 1)
                {
                    ws.Range(r, 1, r, 10).Style.Fill.BackgroundColor = XLColor.FromHtml("#F8FAFC");
                }

                for (int c = 1; c <= 10; c++)
                {
                    ws.Cell(r, c).Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin);
                    ws.Cell(r, c).Style.Border.OutsideBorderColor = XLColor.FromHtml("#CBD5E1");
                }
                r++;
            }

            int endRow = r - 1;
            ws.Cell(r, 1).Value = "TỔNG CỘNG:";
            ws.Range(r, 1, r, 6).Merge().Style.Font.SetBold(true).Font.SetFontSize(10).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Right);
            ws.Cell(r, 7).FormulaA1 = endRow >= startRow + 1 ? $"SUM(G{startRow + 1}:G{endRow})" : "0";
            ws.Cell(r, 9).FormulaA1 = endRow >= startRow + 1 ? $"SUM(I{startRow + 1}:I{endRow})" : "0";
            ws.Cell(r, 7).Style.Font.Bold = true;
            ws.Cell(r, 9).Style.Font.Bold = true;
            ws.Cell(r, 7).Style.Font.FontSize = 10;
            ws.Cell(r, 9).Style.Font.FontSize = 10;
            ws.Cell(r, 7).Style.NumberFormat.Format = "#,##0";
            ws.Cell(r, 9).Style.NumberFormat.Format = "#,##0";
            ws.Range(r, 1, r, 10).Style.Fill.BackgroundColor = XLColor.FromHtml("#FEF3C7");
            ws.Range(r, 1, r, 10).Style.Border.SetTopBorder(XLBorderStyleValues.Thin);
            ws.Range(r, 1, r, 10).Style.Border.SetBottomBorder(XLBorderStyleValues.Double);
            ws.Range(r, 1, r, 10).Style.Border.TopBorderColor = XLColor.FromHtml("#94A3B8");
            ws.Range(r, 1, r, 10).Style.Border.BottomBorderColor = XLColor.FromHtml("#334155");
            ws.Row(r).Height = 28;

            // Chữ ký cuối bảng
            r += 2;
            ws.Cell(r, 7).Value = "Ngày ..... tháng ..... năm 2026";
            ws.Range(r, 7, r, 10).Merge().Style.Font.SetItalic(true).Font.SetFontSize(9.5).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            r++;
            ws.Cell(r, 2).Value = "NGƯỜI LẬP BIỂU";
            ws.Range(r, 2, r, 4).Merge().Style.Font.SetBold(true).Font.SetFontSize(9.5).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            ws.Cell(r, 7).Value = "NGƯỜI ĐẠI DIỆN THEO PHÁP LUẬT";
            ws.Range(r, 7, r, 10).Merge().Style.Font.SetBold(true).Font.SetFontSize(9.5).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            r++;
            ws.Cell(r, 2).Value = "(Ký, ghi rõ họ tên)";
            ws.Range(r, 2, r, 4).Merge().Style.Font.SetItalic(true).Font.SetFontSize(8.5).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            ws.Cell(r, 7).Value = "(Ký, đóng dấu, ghi rõ họ tên)";
            ws.Range(r, 7, r, 10).Merge().Style.Font.SetItalic(true).Font.SetFontSize(8.5).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

            ws.Column(1).Width = 7;
            ws.Column(2).Width = 14;
            ws.Column(3).Width = 12;
            ws.Column(4).Width = 14;
            ws.Column(5).Width = 42;
            ws.Column(6).Width = 18;
            ws.Column(7).Width = 22;
            ws.Column(8).Width = 13;
            ws.Column(9).Width = 20;
            ws.Column(10).Width = 45;
        }

        private void BuildPhuLuc8Sheet(IXLWorksheet ws, TaxDeclarationViewModel model, string kyStr)
        {
            ws.ShowGridLines = true;
            ws.TabColor = XLColor.FromHtml("#D97706");

            // 1. Thông tin công ty
            ws.Cell("A1").Value = $"ĐƠN VỊ: {model.CompanyName.ToUpper()}";
            ws.Cell("A1").Style.Font.Bold = true;
            ws.Cell("A1").Style.Font.FontSize = 10.5;
            ws.Cell("A1").Style.Font.FontColor = XLColor.FromHtml("#0F172A");
            ws.Range("A1:E1").Merge();
            ws.Row(1).Height = 22;

            ws.Cell("A2").Value = $"MÃ SỐ THUẾ: {model.TaxCode}  -  ĐỊA CHỈ: {model.Address}";
            ws.Cell("A2").Style.Font.Italic = true;
            ws.Cell("A2").Style.Font.FontSize = 9.5;
            ws.Cell("A2").Style.Font.FontColor = XLColor.FromHtml("#475569");
            ws.Range("A2:E2").Merge();
            ws.Row(2).Height = 20;

            ws.Row(3).Height = 10;

            // 2. Tiêu đề
            ws.Cell("A4").Value = "PHỤ LỤC GIẢM THUẾ GIÁ TRỊ GIA TĂNG (NGHỊ QUYẾT SỐ 110/2023/QH15)";
            ws.Cell("A4").Style.Font.Bold = true;
            ws.Cell("A4").Style.Font.FontSize = 14;
            ws.Cell("A4").Style.Font.FontColor = XLColor.FromHtml("#B45309");
            ws.Range("A4:G4").Merge().Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            ws.Row(4).Height = 28;

            ws.Cell("A5").Value = $"Kỳ tính thuế: {kyStr} (Từ ngày {model.FromDate:dd/MM/yyyy} đến ngày {model.ToDate:dd/MM/yyyy})  |  Tổng số: {model.Tax8PercentReductionItems.Count:N0} mặt hàng";
            ws.Cell("A5").Style.Font.Italic = true;
            ws.Cell("A5").Style.Font.FontSize = 9.5;
            ws.Cell("A5").Style.Font.FontColor = XLColor.FromHtml("#475569");
            ws.Range("A5:G5").Merge().Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            ws.Row(5).Height = 20;

            ws.Row(6).Height = 10;

            // 3. Tiêu đề cột
            int startRow = 7;
            string[] headers = new[]
            {
                "STT", "Tên hàng hóa, dịch vụ", "Ký hiệu - Số HĐ", "Ngày lập",
                "Giá trị trước thuế (VNĐ)", "Thuế suất", "Tiền thuế sau giảm (8%)", "Tiền thuế được giảm (2%)"
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
                cell.Style.Border.OutsideBorderColor = XLColor.FromHtml("#B45309");
            }
            ws.Row(startRow).Height = 30;

            int r = startRow + 1;
            int stt = 1;
            foreach (var item in model.Tax8PercentReductionItems)
            {
                string kyHieuSo = !string.IsNullOrEmpty(item.InvoiceSymbol) 
                    ? $"{item.InvoiceSymbol} - {item.InvoiceNumber}" 
                    : item.InvoiceNumber;

                ws.Cell(r, 1).SetValue(stt++);
                ws.Cell(r, 2).SetValue(item.ItemName);
                ws.Cell(r, 3).SetValue(kyHieuSo);
                ws.Cell(r, 4).SetValue(item.IssueDate);
                ws.Cell(r, 5).SetValue(item.AmountBeforeTax);
                ws.Cell(r, 6).SetValue("8%");
                ws.Cell(r, 7).SetValue(item.TaxAmount8Percent);
                ws.Cell(r, 8).SetValue(item.TaxReducedAmount2Percent);

                ws.Cell(r, 1).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                ws.Cell(r, 3).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                ws.Cell(r, 4).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                ws.Cell(r, 6).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

                ws.Cell(r, 1).Style.Font.SetBold(true).Font.SetFontColor(XLColor.FromHtml("#64748B"));
                ws.Cell(r, 5).Style.NumberFormat.Format = "#,##0";
                ws.Cell(r, 7).Style.NumberFormat.Format = "#,##0";
                ws.Cell(r, 8).Style.NumberFormat.Format = "#,##0";
                ws.Cell(r, 7).Style.Font.SetBold(true);

                ws.Row(r).Height = 24;
                if (r % 2 == 1)
                {
                    ws.Range(r, 1, r, 8).Style.Fill.BackgroundColor = XLColor.FromHtml("#F8FAFC");
                }

                for (int c = 1; c <= 8; c++)
                {
                    ws.Cell(r, c).Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin);
                    ws.Cell(r, c).Style.Border.OutsideBorderColor = XLColor.FromHtml("#CBD5E1");
                }
                r++;
            }

            int endRow = r - 1;
            ws.Cell(r, 1).Value = "TỔNG CỘNG:";
            ws.Range(r, 1, r, 4).Merge().Style.Font.SetBold(true).Font.SetFontSize(10).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Right);
            ws.Cell(r, 5).FormulaA1 = endRow >= startRow + 1 ? $"SUM(E{startRow + 1}:E{endRow})" : "0";
            ws.Cell(r, 7).FormulaA1 = endRow >= startRow + 1 ? $"SUM(G{startRow + 1}:G{endRow})" : "0";
            ws.Cell(r, 8).FormulaA1 = endRow >= startRow + 1 ? $"SUM(H{startRow + 1}:H{endRow})" : "0";
            ws.Cell(r, 5).Style.Font.Bold = true;
            ws.Cell(r, 7).Style.Font.Bold = true;
            ws.Cell(r, 8).Style.Font.Bold = true;
            ws.Cell(r, 5).Style.Font.FontSize = 10;
            ws.Cell(r, 7).Style.Font.FontSize = 10;
            ws.Cell(r, 8).Style.Font.FontSize = 10;
            ws.Cell(r, 5).Style.NumberFormat.Format = "#,##0";
            ws.Cell(r, 7).Style.NumberFormat.Format = "#,##0";
            ws.Cell(r, 8).Style.NumberFormat.Format = "#,##0";
            ws.Range(r, 1, r, 8).Style.Fill.BackgroundColor = XLColor.FromHtml("#FEF3C7");
            ws.Range(r, 1, r, 8).Style.Border.SetTopBorder(XLBorderStyleValues.Thin);
            ws.Range(r, 1, r, 8).Style.Border.SetBottomBorder(XLBorderStyleValues.Double);
            ws.Range(r, 1, r, 8).Style.Border.TopBorderColor = XLColor.FromHtml("#94A3B8");
            ws.Range(r, 1, r, 8).Style.Border.BottomBorderColor = XLColor.FromHtml("#334155");
            ws.Row(r).Height = 28;

            // Chữ ký cuối bảng
            r += 2;
            ws.Cell(r, 5).Value = "Ngày ..... tháng ..... năm 2026";
            ws.Range(r, 5, r, 8).Merge().Style.Font.SetItalic(true).Font.SetFontSize(9.5).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            r++;
            ws.Cell(r, 2).Value = "NGƯỜI LẬP BIỂU";
            ws.Range(r, 2, r, 3).Merge().Style.Font.SetBold(true).Font.SetFontSize(9.5).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            ws.Cell(r, 5).Value = "NGƯỜI ĐẠI DIỆN THEO PHÁP LUẬT";
            ws.Range(r, 5, r, 8).Merge().Style.Font.SetBold(true).Font.SetFontSize(9.5).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            r++;
            ws.Cell(r, 2).Value = "(Ký, ghi rõ họ tên)";
            ws.Range(r, 2, r, 3).Merge().Style.Font.SetItalic(true).Font.SetFontSize(8.5).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            ws.Cell(r, 5).Value = "(Ký, đóng dấu, ghi rõ họ tên)";
            ws.Range(r, 5, r, 8).Merge().Style.Font.SetItalic(true).Font.SetFontSize(8.5).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

            ws.Column(1).Width = 7;
            ws.Column(2).Width = 45;
            ws.Column(3).Width = 18;
            ws.Column(4).Width = 14;
            ws.Column(5).Width = 22;
            ws.Column(6).Width = 13;
            ws.Column(7).Width = 20;
            ws.Column(8).Width = 20;
        }
    }
}
