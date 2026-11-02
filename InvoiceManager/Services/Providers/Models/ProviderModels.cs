using System;

namespace InvoiceManager.Services.Providers.Models
{
    public class ProviderCredentials
    {
        public string TaxCode { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string? CaptchaKey { get; set; }
        public string? CaptchaCode { get; set; }
        public string? BearerToken { get; set; }
    }

    public class CaptchaChallenge
    {
        public string Key { get; set; } = string.Empty;
        /// <summary>
        /// Chuỗi Base64 Data URL (e.g. data:image/png;base64,...) hoặc SVG text
        /// </summary>
        public string ImageBase64 { get; set; } = string.Empty;
        public bool IsSvg { get; set; } = false;
        public string? SvgContent { get; set; }
        public string? AutoSolvedCode { get; set; }
    }

    public class LoginResult
    {
        public bool Success { get; set; }
        public string? Token { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public string? ErrorMessage { get; set; }
        public bool RequiresCaptcha { get; set; } = false;

        public static LoginResult Ok(string token, DateTime? expiresAt = null) =>
            new() { Success = true, Token = token, ExpiresAt = expiresAt };

        public static LoginResult Fail(string error, bool requiresCaptcha = true) =>
            new() { Success = false, ErrorMessage = error, RequiresCaptcha = requiresCaptcha };
    }

    public class FetchRequest
    {
        public string TaxCode { get; set; } = string.Empty;
        public DateTime FromDate { get; set; }
        public DateTime ToDate { get; set; }
        /// <summary>
        /// "MuaVao", "BanRa", hoặc "All"
        /// </summary>
        public string InvoiceType { get; set; } = "MuaVao";
    }

    public class RemoteInvoice
    {
        public string ProviderKey { get; set; } = string.Empty; // Id hoặc khóa tải của cổng thuế
        public string TemplateCode { get; set; } = "1"; // Ký hiệu mẫu số hóa đơn (khmshdon: 1, 2, 3...)
        public string InvoiceSymbol { get; set; } = string.Empty; // Ký hiệu (e.g. 1C25TKT hoặc C26TUL)
        public string InvoiceNumber { get; set; } = string.Empty; // Số HĐ chuẩn hóa (e.g. 00000501)
        public string RawInvoiceNumber { get; set; } = string.Empty; // Số HĐ gốc từ API (e.g. 501)
        public DateTime IssueDate { get; set; }
        public string SellerTaxCode { get; set; } = string.Empty;
        public string SellerName { get; set; } = string.Empty;
        public string? SellerAddress { get; set; }
        public string BuyerTaxCode { get; set; } = string.Empty;
        public string BuyerName { get; set; } = string.Empty;
        public string? BuyerAddress { get; set; }
        public decimal TotalAmount { get; set; }
        public decimal AmountBeforeTax { get; set; }
        public decimal TaxAmount { get; set; }
        public string InvoiceType { get; set; } = "MuaVao"; // "MuaVao" hoặc "BanRa"
        public string? CqtCode { get; set; } // Mã cơ quan thuế cấp
        public string? Status { get; set; }
    }

    public class OriginalPdfFetchResult
    {
        public bool Success { get; set; }
        public byte[]? PdfBytes { get; set; }
        public string FileName { get; set; } = string.Empty;
        public string ContentType { get; set; } = "application/pdf";
        public string? Sha256Hash { get; set; }
        public string Source { get; set; } = "GDT-ExportPdf";
        public string? SavedFilePath { get; set; }
        public int HttpStatusCode { get; set; } = 200;
        public string ErrorCode { get; set; } = string.Empty;
        public string? ErrorMessage { get; set; }
        public string? VerifiedEndpoint { get; set; }
        public string? EndpointCalled
        {
            get => VerifiedEndpoint;
            set => VerifiedEndpoint = value;
        }
        public string? RawGdtResponseSnippet { get; set; }
        public DownloadInvoiceResult? CompanionPackage { get; set; }
    }

    public class DownloadedInvoiceFile
    {
        public string FileName { get; set; } = string.Empty;
        public string FileExtension { get; set; } = string.Empty; // .xml, .pdf, .zip, etc.
        public string ContentType { get; set; } = "application/octet-stream";
        public byte[] Content { get; set; } = Array.Empty<byte>();
        public string Source { get; set; } = "GDT-ExportXml"; // GDT-ExportXml, GDT-ExportPdf, GDT-PackageZip, GDT-ZipEntry
        public string Sha256Hash { get; set; } = string.Empty;
        public long FileSizeBytes => Content?.LongLength ?? 0;
    }

    public class DownloadInvoiceResult
    {
        public bool Success => Files.Count > 0;
        public string? ErrorMessage { get; set; }
        public System.Collections.Generic.List<DownloadedInvoiceFile> Files { get; set; } = new();

        public DownloadedInvoiceFile? XmlFile => Files.Find(f => string.Equals(f.FileExtension, ".xml", StringComparison.OrdinalIgnoreCase));
        public DownloadedInvoiceFile? PdfFile => Files.Find(f => string.Equals(f.FileExtension, ".pdf", StringComparison.OrdinalIgnoreCase));
        public DownloadedInvoiceFile? ZipFile => Files.Find(f => string.Equals(f.FileExtension, ".zip", StringComparison.OrdinalIgnoreCase));
    }
}
