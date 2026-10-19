using System;

namespace InvoiceManager.Models.ViewModels
{
    public class TaxAccountListItemViewModel
    {
        public int Id { get; set; }
        public string TaxCode { get; set; } = string.Empty;
        public string CompanyName { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public string? Email { get; set; }
        public string? PhoneNumber { get; set; }
        public string? Representative { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; }
        public string AccountingSoftware { get; set; } = "SMART PRO";
        public int PurchaseInvoiceCount { get; set; }
        public int SoldInvoiceCount { get; set; }
        public int TotalInvoiceCount => PurchaseInvoiceCount + SoldInvoiceCount;
        public bool IsCurrent { get; set; }
    }
}
