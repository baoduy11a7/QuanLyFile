namespace InvoiceManager.Services.Providers.Gdt
{
    public class GdtPortalOptions
    {
        public const string SectionName = "InvoiceProviders:Gdt";

        public string BaseUrl { get; set; } = "https://hoadondientu.gdt.gov.vn";
        public string CaptchaEndpoint { get; set; } = "/api/captcha";
        public string LoginEndpoint { get; set; } = "/api/security-taxpayer/authenticate";
        public string PurchaseQueryEndpoint { get; set; } = "/api/query/invoices/purchase";
        public string SoldQueryEndpoint { get; set; } = "/api/query/invoices/sold";
        public string ExportXmlEndpoint { get; set; } = "/api/query/invoices/export-xml";
        public int RateLimitDelayMs { get; set; } = 800;
        public int TimeoutSeconds { get; set; } = 30;
        public int PageSize { get; set; } = 50;
        public string UserAgent { get; set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    }
}
