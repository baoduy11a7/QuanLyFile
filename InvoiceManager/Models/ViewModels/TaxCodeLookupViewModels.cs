namespace InvoiceManager.Models.ViewModels
{
    public class TaxCodeLookupResult
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public string? TaxCode { get; set; }
        public string? CompanyName { get; set; }
        public string? InternationalName { get; set; }
        public string? ShortName { get; set; }
        public string? Address { get; set; }
        public string? Status { get; set; }
        public string? Source { get; set; }
        public bool ExistsInSystem { get; set; }
        public int? ExistingAccountId { get; set; }
    }
}
