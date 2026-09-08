using InvoiceManager.Models.Entities;
using System;
using System.Collections.Generic;

namespace InvoiceManager.Models.ViewModels
{
    public class DashboardViewModel
    {
        public TaxAccount? CurrentTaxAccount { get; set; }
        public string Period { get; set; } = "thisYear"; // thisMonth, thisQuarter, thisYear, all
        public DateTime? FromDate { get; set; }
        public DateTime? ToDate { get; set; }

        // 1. Mua vào
        public int TotalInvoicesPurchase { get; set; }
        public decimal TotalAmountPurchase { get; set; }
        public decimal TotalTaxAmountPurchase { get; set; }

        // 2. Bán ra
        public int TotalInvoicesSale { get; set; }
        public decimal TotalAmountSale { get; set; }
        public decimal TotalTaxAmountSale { get; set; }

        // 3. Cân đối thuế GTGT
        public decimal NetVatPayable { get; set; } // Thuế đầu ra - Thuế đầu vào (nếu > 0 là phải nộp, < 0 là còn được khấu trừ)
        public int ReconciledCount { get; set; }
        public int UnreconciledCount { get; set; }
        public double ReconciledPercent { get; set; }

        // 4. Biểu đồ tháng
        public List<string> MonthlyLabels { get; set; } = new();
        public List<decimal> MonthlyPurchaseAmounts { get; set; } = new();
        public List<decimal> MonthlySaleAmounts { get; set; } = new();
        public List<decimal> MonthlyVatPurchase { get; set; } = new();

        // 5. Cơ cấu phân loại
        public int CountWithTaxCode { get; set; }
        public int CountWithoutTaxCode { get; set; }
        public int CountCashRegister { get; set; }
        public int CountAdjustedOrReplaced { get; set; }
        public int CountCancelled { get; set; }

        // 6. Danh sách quản lý & tra cứu nhanh
        public List<Invoice> UnreconciledInvoices { get; set; } = new();
        public List<Invoice> HighValueInvoices { get; set; } = new();
        public List<Invoice> RiskyOrCancelledInvoices { get; set; } = new();
        public List<SupplierSummaryDto> TopSuppliers { get; set; } = new();
    }

    public class SupplierSummaryDto
    {
        public string TaxCode { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int InvoiceCount { get; set; }
        public decimal TotalAmount { get; set; }
        public decimal TotalTaxAmount { get; set; }
        public DateTime LastInvoiceDate { get; set; }
    }

    public class QuickSearchResultDto
    {
        public int Id { get; set; }
        public string InvoiceSymbol { get; set; } = string.Empty;
        public string InvoiceNumber { get; set; } = string.Empty;
        public string IssueDate { get; set; } = string.Empty;
        public string InvoiceType { get; set; } = string.Empty;
        public string SellerTaxCode { get; set; } = string.Empty;
        public string SellerName { get; set; } = string.Empty;
        public decimal TotalAmount { get; set; }
        public string Status { get; set; } = string.Empty;
        public bool HasTaxCode { get; set; }
        public bool IsReconciled { get; set; }
        public string? ReconciledRefNo { get; set; }
    }
}
