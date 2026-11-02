using System;
using System.Collections.Generic;

namespace InvoiceManager.Models.ViewModels
{
    public class TaxDeclarationViewModel
    {
        public string TaxCode { get; set; } = string.Empty;
        public string CompanyName { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public string PeriodType { get; set; } = "Quarter"; // "Quarter" or "Month"
        public int PeriodValue { get; set; } = 1; // Quý 1..4 hoặc Tháng 1..12
        public int Year { get; set; } = DateTime.Now.Year;
        public DateTime FromDate { get; set; }
        public DateTime ToDate { get; set; }
        public bool IsFirstDeclaration { get; set; } = true;
        public int AdditionalCount { get; set; } = 0;

        public string PeriodDescription => PeriodType == "Quarter" ? $"Quý {PeriodValue}/{Year}" : $"Tháng {PeriodValue}/{Year}";

        // [21] Doanh thu HHDV không chịu thuế GTGT
        public decimal Target21_KCTRevenue { get; set; }

        // [22] Thuế GTGT còn được khấu trừ kỳ trước chuyển sang
        public decimal Target22_CarriedOverDeduction { get; set; }

        // [23] Giá trị HHDV mua vào chưa có thuế GTGT
        public decimal Target23_PurchaseAmountBeforeTax { get; set; }

        // [24] Thuế GTGT của HHDV mua vào
        public decimal Target24_PurchaseTaxAmount { get; set; }

        // [25] Tổng số thuế GTGT được khấu trừ kỳ này
        public decimal Target25_DeductibleTaxAmount { get; set; }

        // [26] HHDV bán ra không chịu thuế
        public decimal Target26_SalesAmount0 { get; set; }

        // [27] HHDV bán ra chịu thuế 0%
        public decimal Target27_SalesAmount5 { get; set; }

        // [28] Doanh thu bán ra chịu thuế 5%
        public decimal Target28_SalesTax5 { get; set; }

        // [29] Thuế GTGT bán ra chịu thuế 5%
        public decimal Target29_SalesAmount8And10 { get; set; }

        // [30] Thuế GTGT của HHDV bán ra chịu thuế 8% và 10%
        public decimal Target30_SalesTax8And10 { get; set; }

        // [31] HHDV bán ra không tính thuế
        public decimal Target31_SalesAmountNoTax { get; set; }

        // [32] Tổng doanh thu HHDV bán ra [32] = [26] + [27] + [29] + [31]
        public decimal Target32_TotalSalesAmount => Target26_SalesAmount0 + Target27_SalesAmount5 + Target29_SalesAmount8And10 + Target31_SalesAmountNoTax;

        // [33] Tổng thuế GTGT của HHDV bán ra [33] = [28] + [30]
        public decimal Target33_TotalSalesTax => Target28_SalesTax5 + Target30_SalesTax8And10;

        // [34] Thuế GTGT phát sinh trong kỳ [34] = [33]
        public decimal Target34_OutputTax => Target33_TotalSalesTax;

        // [35] Thuế GTGT khấu trừ trong kỳ [35] = [25]
        public decimal Target35_InputTax => Target25_DeductibleTaxAmount;

        // [36] Thuế GTGT phát sinh phải nộp trong kỳ [36] = Max(0, [34] - [35] - [22])
        public decimal Target36_TaxPayable => Math.Max(0, Target34_OutputTax - Target35_InputTax - Target22_CarriedOverDeduction);

        // [40] Thuế GTGT còn phải nộp vào NSNN [40] = [36]
        public decimal Target40_TaxToPay => Target36_TaxPayable;

        // [43] Thuế GTGT còn được khấu trừ chuyển kỳ sau [43] = Max(0, (Target35 + Target22) - Target34)
        public decimal Target43_CarriedForwardDeduction => Math.Max(0, (Target35_InputTax + Target22_CarriedOverDeduction) - Target34_OutputTax);

        // Alias Indicators cho Frontend JavaScript
        public decimal Indicator21_NoActivity => Target21_KCTRevenue;
        public decimal Indicator22_TaxCarriedForwardPreviousPeriod => Target22_CarriedOverDeduction;
        public decimal Indicator23_PurchaseTotalAmountBeforeTax => Target23_PurchaseAmountBeforeTax;
        public decimal Indicator24_PurchaseTotalTaxAmount => Target24_PurchaseTaxAmount;
        public decimal Indicator25_DeductiblePurchaseTaxAmount => Target25_DeductibleTaxAmount;
        public decimal Indicator26_RevenueNonTaxable => Target21_KCTRevenue;
        public decimal Indicator27_RevenueTax0Percent => Target26_SalesAmount0;
        public decimal Indicator28_RevenueTax5Percent => Target27_SalesAmount5;
        public decimal Indicator29_TaxAmount5Percent => Target28_SalesTax5;
        public decimal Indicator30_RevenueTax8Percent => Sales8Amount;
        public decimal Indicator31_TaxAmount8Percent => Sales8Tax;
        public decimal Indicator32_RevenueTax10Percent => Math.Max(0, Target29_SalesAmount8And10 - Sales8Amount);
        public decimal Indicator33_TaxAmount10Percent => Math.Max(0, Target30_SalesTax8And10 - Sales8Tax);
        public decimal Indicator34_TotalRevenue => Target32_TotalSalesAmount;
        public decimal Indicator35_TotalOutputTax => Target33_TotalSalesTax;
        public decimal Indicator36_VatIncurredPeriod => Math.Max(0, Target33_TotalSalesTax - Target25_DeductibleTaxAmount);
        public decimal Indicator40_VatPayable => Target40_TaxToPay;
        public decimal Indicator43_VatDeductibleNextPeriod => Target43_CarriedForwardDeduction;

        // Phụ lục giảm thuế 8%
        public decimal Sales8Amount { get; set; }
        public decimal Sales8Tax { get; set; }
        public decimal Sales8ReducedTax { get; set; }

        public decimal Purchase8Amount { get; set; }
        public decimal Purchase8Tax { get; set; }

        public int TotalPurchaseInvoices { get; set; }
        public int TotalSalesInvoices { get; set; }

        public List<TaxInvoiceItem> PurchaseInvoices { get; set; } = new();
        public List<TaxInvoiceItem> SoldInvoices { get; set; } = new();
        public List<Tax8ReductionItem> Tax8PercentReductionItems { get; set; } = new();
    }

    public class TaxInvoiceItem
    {
        public int Id { get; set; }
        public string InvoiceSymbol { get; set; } = string.Empty;
        public string InvoiceNumber { get; set; } = string.Empty;
        public string IssueDate { get; set; } = string.Empty;
        public string SellerName { get; set; } = string.Empty;
        public string SellerTaxCode { get; set; } = string.Empty;
        public string BuyerName { get; set; } = string.Empty;
        public string BuyerTaxCode { get; set; } = string.Empty;
        public string ItemName { get; set; } = string.Empty;
        public decimal AmountBeforeTax { get; set; }
        public string TaxRate { get; set; } = string.Empty;
        public decimal TaxAmount { get; set; }
        public decimal TotalAmount { get; set; }
        public string Note { get; set; } = string.Empty;
    }

    public class Tax8ReductionItem
    {
        public string ItemName { get; set; } = string.Empty;
        public string InvoiceSymbol { get; set; } = string.Empty;
        public string InvoiceNumber { get; set; } = string.Empty;
        public string IssueDate { get; set; } = string.Empty;
        public decimal AmountBeforeTax { get; set; }
        public decimal TaxAmount8Percent { get; set; }
        public decimal TaxReducedAmount2Percent { get; set; }
    }

    public class CalculateDeclarationRequest
    {
        public string PeriodType { get; set; } = "Quarter"; // "Quarter", "Month", or "Custom"
        public int? PeriodValue { get; set; }
        public int? Quarter { get; set; }
        public int? Month { get; set; }
        public int Year { get; set; } = DateTime.Now.Year;
        public DateTime? FromDate { get; set; }
        public DateTime? ToDate { get; set; }
        public decimal? CarriedOverTax { get; set; }
        public decimal? TaxCarriedForwardPreviousPeriod { get; set; }
        public bool Indicator21_NoActivity { get; set; }
    }
}
