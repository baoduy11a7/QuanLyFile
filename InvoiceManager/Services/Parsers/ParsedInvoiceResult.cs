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
        public decimal DiscountRate { get; set; } = 0;
        public decimal DiscountAmount { get; set; } = 0;
        public decimal AmountBeforeTax { get; set; } = 0;
        public decimal TaxRate { get; set; } = 10;
        public decimal TaxAmount { get; set; } = 0;
        public decimal TotalAmount { get; set; } = 0;
        public decimal AmountBeforeTaxForeign { get; set; } = 0;
        public decimal TaxAmountForeign { get; set; } = 0;
        public decimal TotalAmountForeign { get; set; } = 0;
        public decimal AmountBeforeTaxVnd { get; set; } = 0;
        public decimal TaxAmountVnd { get; set; } = 0;
        public decimal TotalAmountVnd { get; set; } = 0;
    }

    public class ParsedInvoiceResult
    {
        public bool Success { get; set; } = true;
        public string? ErrorMessage { get; set; }
        public List<string> ValidationWarnings { get; set; } = new();

        public string TemplateCode { get; set; } = "1";
        public string InvoiceSymbol { get; set; } = string.Empty;
        public string InvoiceNumber { get; set; } = string.Empty;
        public DateTime IssueDate { get; set; } = DateTime.Now;
        public DateTime? SignedDate { get; set; }

        // Người bán
        public string SellerTaxCode { get; set; } = string.Empty;
        public string SellerName { get; set; } = string.Empty;
        public string? SellerAddress { get; set; }
        public string? SellerWebsite { get; set; }

        // Người mua
        public string? BuyerTaxCode { get; set; }
        public string? BuyerCitizenId { get; set; }
        public string? BuyerName { get; set; }
        public string? BuyerAddress { get; set; }

        // Tiền tệ & Tỉ giá
        public string Currency { get; set; } = "VND";
        public decimal ExchangeRate { get; set; } = 1;

        // Số tiền nguyên tệ
        public decimal AmountBeforeTax { get; set; }
        public decimal TaxAmount { get; set; }
        public decimal TotalAmount { get; set; }

        // Số tiền quy đổi VND
        public decimal AmountBeforeTaxVnd { get; set; } = 0;
        public decimal TaxAmountVnd { get; set; } = 0;
        public decimal TotalAmountVnd { get; set; } = 0;

        public decimal DiscountAmount { get; set; } = 0;
        public decimal FeeAmount { get; set; } = 0;

        // Hình thức, Tính chất, Kiểm tra
        public string PaymentMethod { get; set; } = "TM/CK";
        public string InvoiceProperty { get; set; } = "Hàng hoá/dịch vụ";
        public string VerificationResult { get; set; } = "Đã cấp mã hoá đơn";
        public string? VehicleNumber { get; set; }
        public string? ReplacedOrAdjustedNote { get; set; }
        public string? LookupCode { get; set; }
        public string? LookupUrl { get; set; }

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
