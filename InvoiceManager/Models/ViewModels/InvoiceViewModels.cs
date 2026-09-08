using InvoiceManager.Models.Entities;
using System;
using System.Collections.Generic;

namespace InvoiceManager.Models.ViewModels
{
    public class InvoiceFilterViewModel
    {
        public string InvoiceType { get; set; } = "MuaVao"; // MuaVao / BanRa
        public DateTime? FromDate { get; set; }
        public DateTime? ToDate { get; set; }
        public string QuickDateRange { get; set; } = "thisMonth"; // today, thisMonth, lastMonth, thisQuarter, thisYear, custom
        public string? Keyword { get; set; }
        public string? SellerTaxCode { get; set; }
        public bool? HasTaxCode { get; set; }
        public bool? IsCashRegister { get; set; }
        public string? Status { get; set; }
        public decimal? MinAmount { get; set; }
        public decimal? MaxAmount { get; set; }
        public bool? IsReconciled { get; set; }
        public string SortBy { get; set; } = "DateDesc";
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 15;
    }

    public class InvoiceSummaryViewModel
    {
        public int TotalCount { get; set; }
        public int WithTaxCodeCount { get; set; }
        public int WithoutTaxCodeCount { get; set; }
        public int CashRegisterCount { get; set; }
        public DateTime? LastSyncedAt { get; set; }
        public decimal TotalAmountBeforeTax { get; set; }
        public decimal TotalTaxAmount { get; set; }
        public decimal TotalAmount { get; set; }
    }

    public class InvoiceIndexViewModel
    {
        public InvoiceFilterViewModel Filter { get; set; } = new();
        public InvoiceSummaryViewModel Summary { get; set; } = new();
        public List<Invoice> Invoices { get; set; } = new();
        public int TotalItems { get; set; }
        public int TotalPages { get; set; }
        public int CurrentPage { get; set; }
        public TaxAccount? CurrentTaxAccount { get; set; }
        public List<TaxAccount> AccessibleTaxAccounts { get; set; } = new();
    }
}
