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
        public DateTime SubmissionDeadline { get; set; }
        public bool IsOverdue { get; set; }
        public bool IsFirstDeclaration { get; set; } = true;
        public int AdditionalCount { get; set; } = 0;

        // Phiên bản biểu mẫu pháp lý áp dụng
        public string FormVersionCode { get; set; } = "01_GTGT_TT89";
        public string LegalDocRef { get; set; } = "Thông tư 89/2026/TT-BTC";
        public string LegalDocFullTitle { get; set; } = "Thông tư số 89/2026/TT-BTC của Bộ Tài chính";
        public string XmlTemplateCode { get; set; } = "01_GTGT_TT89";
        public string XmlXsdVersion { get; set; } = "2.1.0";

        public string PeriodDescription => PeriodType == "Quarter" ? $"Quý {PeriodValue}/{Year}" : $"Tháng {PeriodValue}/{Year}";

        // [21] Không phát sinh hoạt động mua, bán trong kỳ
        public bool Indicator21_NoActivity { get; set; }
        public decimal Target21_KCTRevenue { get; set; }

        // [22] Thuế GTGT còn được khấu trừ kỳ trước chuyển sang
        public decimal Target22_CarriedOverDeduction { get; set; }

        // [23] Giá trị HHDV mua vào chưa có thuế GTGT
        public decimal Target23_PurchaseAmountBeforeTax { get; set; }

        // [23a] HHDV mua vào nhập khẩu (Mẫu TT 89)
        public decimal Target23a_ImportPurchaseAmount { get; set; }

        // [24] Thuế GTGT của HHDV mua vào
        public decimal Target24_PurchaseTaxAmount { get; set; }

        // [25] Tổng số thuế GTGT của HHDV mua vào được khấu trừ kỳ này
        public decimal Target25_DeductibleTaxAmount { get; set; }

        // [26] HHDV bán ra không chịu thuế
        public decimal Target26_SalesAmount0 { get; set; }

        // [27] HHDV bán ra chịu thuế 0%
        public decimal Target27_SalesAmount5 { get; set; }

        // [28] Doanh thu bán ra chịu thuế 5%
        public decimal Target28_SalesTax5 { get; set; }

        // [29] Thuế GTGT bán ra chịu thuế 5%
        public decimal Target29_SalesAmount8And10 { get; set; }

        // [30] HHDV bán ra chịu thuế 8% (NQ 110)
        public decimal Target30_SalesTax8And10 { get; set; }

        // [31] Thuế GTGT của HHDV bán ra chịu thuế 8%
        public decimal Target31_SalesAmountNoTax { get; set; }

        // [32] HHDV bán ra chịu thuế 10%
        public decimal Target32_SalesAmount10 { get; set; }

        // [32a] Thuế GTGT của HHDV bán ra chịu thuế 10%
        public decimal Target32a_SalesTax10 { get; set; }

        // [32b] Khoản ghi trên HĐ không tính vào giá tính thuế (Điểm mới TT 89)
        public decimal Target32b_SalesAmountNoTax { get; set; }

        // [33] Tổng doanh thu HHDV bán ra chịu thuế
        public decimal Target33_TaxableSalesAmount { get; set; }

        // [34a] HHDV không phải kê khai, nộp thuế GTGT (Điểm mới TT 89)
        public decimal Target34a_SalesAmountNoDeclare { get; set; }

        // [34] Tổng doanh thu HHDV bán ra
        public decimal Target34_TotalSalesAmount { get; set; }

        // [35] Tổng thuế GTGT của HHDV bán ra
        public decimal Target35_TotalSalesTax { get; set; }

        // [36] Thuế GTGT phát sinh trong kỳ
        public decimal Target36_OutputTax { get; set; }

        // [37] Điều chỉnh giảm khấu trừ các kỳ trước
        public decimal Target37_AdjustReduceDeduction { get; set; }

        // [38] Điều chỉnh tăng khấu trừ các kỳ trước
        public decimal Target38_AdjustIncreaseDeduction { get; set; }

        // [39a] Thuế GTGT nhận bàn giao được khấu trừ
        public decimal Target39a_HandoverDeduction { get; set; }

        // [40a] Thuế GTGT nộp thừa kỳ trước chuyển sang
        public decimal Target40a_OverpaidTaxPrevious { get; set; }

        // [40] Thuế GTGT còn phải nộp vào NSNN
        public decimal Target40_TaxToPay { get; set; }

        // [41] Thuế GTGT chưa khấu trừ hết kỳ này
        public decimal Target41_RemainingDeductible { get; set; }

        // [42] Thuế GTGT đề nghị hoàn
        public decimal Target42_RefundRequest { get; set; }

        // [43] Thuế GTGT còn được khấu trừ chuyển kỳ sau
        public decimal Target43_CarriedForwardDeduction { get; set; }

        // Tương thích ngược với các biến Javascript trên giao diện
        public decimal Target32_TotalSalesAmount => Target34_TotalSalesAmount;
        public decimal Target33_TotalSalesTax => Target35_TotalSalesTax;
        public decimal Target35_InputTax => Target25_DeductibleTaxAmount;
        public decimal Target36_TaxPayable => Target40_TaxToPay;

        public decimal Indicator22_TaxCarriedForwardPreviousPeriod => Target22_CarriedOverDeduction;
        public decimal Indicator23_PurchaseTotalAmountBeforeTax => Target23_PurchaseAmountBeforeTax;
        public decimal Indicator24_PurchaseTotalTaxAmount => Target24_PurchaseTaxAmount;
        public decimal Indicator25_DeductiblePurchaseTaxAmount => Target25_DeductibleTaxAmount;
        public decimal Indicator26_RevenueNonTaxable => Target26_SalesAmount0;
        public decimal Indicator27_RevenueTax0Percent => Target27_SalesAmount5;
        public decimal Indicator28_RevenueTax5Percent => Target28_SalesTax5;
        public decimal Indicator29_TaxAmount5Percent => Target29_SalesAmount8And10;
        public decimal Indicator30_RevenueTax8Percent => Sales8Amount;
        public decimal Indicator31_TaxAmount8Percent => Sales8Tax;
        public decimal Indicator32_RevenueTax10Percent => Target32_SalesAmount10;
        public decimal Indicator33_TaxAmount10Percent => Target32a_SalesTax10;
        public decimal Indicator34_TotalRevenue => Target34_TotalSalesAmount;
        public decimal Indicator35_TotalOutputTax => Target35_TotalSalesTax;
        public decimal Indicator36_VatIncurredPeriod => Target36_OutputTax;
        public decimal Indicator40_VatPayable => Target40_TaxToPay;
        public decimal Indicator43_VatDeductibleNextPeriod => Target43_CarriedForwardDeduction;

        // Phụ lục giảm thuế 8% theo Nghị quyết 110
        public decimal Sales8Amount { get; set; }
        public decimal Sales8Tax { get; set; }
        public decimal Sales8ReducedTax { get; set; }
        public decimal Purchase8Amount { get; set; }
        public decimal Purchase8Tax { get; set; }
        public bool IsOutOfNQ110Period { get; set; }

        public int TotalPurchaseInvoices { get; set; }
        public int TotalSalesInvoices { get; set; }

        // Cảnh báo & Danh sách kiểm tra
        public int CashThresholdWarningCount { get; set; }
        public decimal CashThresholdApplied { get; set; }
        public List<string> ValidationAlerts { get; set; } = new();
        public Dictionary<string, string> IndicatorTooltips { get; set; } = new();

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
        public string PaymentMethod { get; set; } = string.Empty;
        public bool IsCashPaymentWarning { get; set; }
        public bool IsDeductible { get; set; } = true;
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
        public bool? Indicator21_NoActivity { get; set; }
    }
}
