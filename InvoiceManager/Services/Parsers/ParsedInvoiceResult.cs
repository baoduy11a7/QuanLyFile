using System;
using System.Collections.Generic;

namespace InvoiceManager.Services.Parsers
{
    public class ParsedInvoiceDetail
    {
        public int LineNumber { get; set; } = 1;
        public string? ItemCode { get; set; }
        public string ItemName { get; set; } = string.Empty;
        public string? Unit { get; set; }
        public decimal Quantity { get; set; } = 1;
        public decimal UnitPrice { get; set; } = 0;
        public decimal AmountBeforeTax { get; set; } = 0;
        public decimal TaxRate { get; set; } = 10;
        public decimal TaxAmount { get; set; } = 0;
        public decimal TotalAmount { get; set; } = 0;
    }

    public class ParsedInvoiceResult
    {
        public bool Success { get; set; } = true;
        public string? ErrorMessage { get; set; }
        public List<string> ValidationWarnings { get; set; } = new();

        public string InvoiceSymbol { get; set; } = string.Empty;
        public string InvoiceNumber { get; set; } = string.Empty;
        public DateTime IssueDate { get; set; } = DateTime.Now;

        // Người bán
        public string SellerTaxCode { get; set; } = string.Empty;
        public string SellerName { get; set; } = string.Empty;
        public string? SellerAddress { get; set; }

        // Người mua
        public string? BuyerTaxCode { get; set; }
        public string? BuyerName { get; set; }
        public string? BuyerAddress { get; set; }

        // Số tiền
        public decimal AmountBeforeTax { get; set; }
        public decimal TaxAmount { get; set; }
        public decimal TotalAmount { get; set; }

        // Thuộc tính thuế
        public bool HasTaxCode { get; set; } = true;
        public bool IsCashRegister { get; set; } = false;
        public string? TaxAuthorityCode { get; set; }
        public string Status { get; set; } = "Hóa đơn mới";
        public string SourceProvider { get; set; } = "TCT";

        // Dòng hàng hóa
        public List<ParsedInvoiceDetail> Details { get; set; } = new();

        // XML gốc
        public string? RawXmlContent { get; set; }
    }
}
