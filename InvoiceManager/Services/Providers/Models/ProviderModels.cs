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
        public string InvoiceSymbol { get; set; } = string.Empty; // Ký hiệu (e.g. 1C25TKT)
        public string InvoiceNumber { get; set; } = string.Empty; // Số HĐ (e.g. 00000501)
        public DateTime IssueDate { get; set; }
        public string SellerTaxCode { get; set; } = string.Empty;
        public string SellerName { get; set; } = string.Empty;
        public string BuyerTaxCode { get; set; } = string.Empty;
        public string BuyerName { get; set; } = string.Empty;
        public decimal TotalAmount { get; set; }
        public decimal AmountBeforeTax { get; set; }
        public decimal TaxAmount { get; set; }
        public string InvoiceType { get; set; } = "MuaVao"; // "MuaVao" hoặc "BanRa"
        public string? CqtCode { get; set; } // Mã cơ quan thuế cấp
        public string? Status { get; set; }
    }
}
