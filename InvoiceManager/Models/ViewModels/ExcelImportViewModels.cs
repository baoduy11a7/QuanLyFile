using System;
using System.Collections.Generic;

namespace InvoiceManager.Models.ViewModels
{
    public class ExcelImportRowDto
    {
        public int RowIndex { get; set; }
        public string InvoiceSymbol { get; set; } = string.Empty;
        public string InvoiceNumber { get; set; } = string.Empty;
        public string IssueDateStr { get; set; } = string.Empty;
        public DateTime? IssueDate { get; set; }
        public string SellerTaxCode { get; set; } = string.Empty;
        public string SellerName { get; set; } = string.Empty;
        public string? SellerAddress { get; set; }
        public string? BuyerTaxCode { get; set; }
        public string? BuyerName { get; set; }
        public decimal AmountBeforeTax { get; set; }
        public decimal TaxAmount { get; set; }
        public decimal TotalAmount { get; set; }
        public string InvoiceType { get; set; } = "MuaVao";
        public string? LookupCode { get; set; }
        public string? Notes { get; set; }

        public bool IsValid { get; set; } = true;
        public List<string> ErrorMessages { get; set; } = new();
        public List<string> WarningMessages { get; set; } = new();
    }

    public class ExcelImportPreviewResult
    {
        public bool Success { get; set; } = true;
        public string? ErrorMessage { get; set; }
        public int TotalRows { get; set; }
        public int ValidCount { get; set; }
        public int ErrorCount { get; set; }
        public List<ExcelImportRowDto> Rows { get; set; } = new();
    }

    public class ExcelImportConfirmModel
    {
        public List<ExcelImportRowDto> Rows { get; set; } = new();
        public string? DefaultInvoiceType { get; set; }
    }

    public class ExcelImportSaveResult
    {
        public bool Success { get; set; } = true;
        public int TotalProcessed { get; set; }
        public int SuccessCount { get; set; }
        public int FailedCount { get; set; }
        public List<string> Messages { get; set; } = new();
    }
}
