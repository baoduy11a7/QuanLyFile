using System;

namespace InvoiceManager.Models.ViewModels
{
    /// <summary>
    /// Model cấu hình thông tin lấy hóa đơn gốc (FDI Accounting and Tax Pro)
    /// </summary>
    public class OriginalInvoiceConfigModel
    {
        public int TaxAccountId { get; set; }
        public string CompanyName { get; set; } = string.Empty;

        // A. Thông tin kết nối GOV
        public string ApiUrl { get; set; } = "https://hoadondientu.gdt.gov.vn";
        public string AuthMethod { get; set; } = "AutoOcrCaptcha"; // "AutoOcrCaptcha" | "BearerToken"
        public string Username { get; set; } = string.Empty;
        public string? Password { get; set; }
        public bool HasSavedPassword { get; set; }
        public string? LastConnectedAt { get; set; }
        public string ConnectionStatus { get; set; } = "Chưa kết nối";

        // B. Cấu hình phạm vi lấy hóa đơn
        public string? BuyerTaxCode { get; set; }
        public string? BuyerCccd { get; set; }
        public string DateRangeType { get; set; } = "CurrentMonth"; // "CurrentMonth", "CurrentQuarter", "AllYear", "Custom"
        public DateTime? FromDate { get; set; }
        public DateTime? ToDate { get; set; }
        public string InvoiceStatus { get; set; } = "All"; // "All", "New", "Replaced", "Adjusted", "Cancelled"
        public string InvoiceType { get; set; } = "Purchase"; // "Purchase" (Mua vào), "Sale" (Bán ra), "All"
        public bool OnlyUnDownloaded { get; set; } = true;
        public bool SkipExistingFiles { get; set; } = true;
        public bool AllowReDownload { get; set; } = true;

        // C. Cấu hình định dạng tệp hóa đơn gốc
        public bool DownloadXml { get; set; } = true;
        public bool DownloadPdf { get; set; } = true;
        public bool DownloadBoth { get; set; } = true;

        // D. Cấu hình lưu trữ hóa đơn
        public string StorageFolder { get; set; } = "HoaDonGoc";
        public bool AutoOrganizeByMst { get; set; } = true;
        public bool AutoOrganizeByYearMonth { get; set; } = true;
        public bool AutoOrganizeBySellerMst { get; set; } = false;
        public string FileNamePattern { get; set; } = "{KHHDon}_{SHDon}_{MSTBan}";
        public bool OverwriteExistingFiles { get; set; } = false;

        // E. Cấu hình tự động tải hóa đơn
        public bool AutoDownloadAfterSync { get; set; } = true;
        public bool AutoRetryFailed { get; set; } = true;
        public int MaxRetryCount { get; set; } = 3;
        public int RetryDelaySeconds { get; set; } = 3;
        public bool EnableAuditLog { get; set; } = true;
    }

    /// <summary>
    /// DTO kết quả từng dòng khi tải hàng loạt hóa đơn gốc
    /// </summary>
    public class BatchOriginalDownloadItemResult
    {
        public int InvoiceId { get; set; }
        public string InvoiceNumber { get; set; } = string.Empty;
        public string InvoiceSymbol { get; set; } = string.Empty;
        public string SellerTaxCode { get; set; } = string.Empty;
        public string SellerName { get; set; } = string.Empty;
        public string Status { get; set; } = "Chờ xử lý"; // "Thành công", "Đã tồn tại", "Thất bại", "Đang xử lý"
        public string Formats { get; set; } = "XML, PDF";
        public string? SavedPath { get; set; }
        public string? ErrorMessage { get; set; }
        public bool IsSuccess { get; set; }
        public bool IsSkipped { get; set; }
        public DateTime? ProcessedAt { get; set; }
    }

    /// <summary>
    /// DTO tổng hợp tiến trình tải hàng loạt hóa đơn gốc
    /// </summary>
    public class BatchOriginalDownloadSummaryResult
    {
        public int TotalCount { get; set; }
        public int SuccessCount { get; set; }
        public int ProcessingCount { get; set; }
        public int FailedCount { get; set; }
        public int SkippedCount { get; set; }
        public int ProgressPercent { get; set; }
        public bool IsCompleted { get; set; }
        public string? ZipDownloadUrl { get; set; }
        public System.Collections.Generic.List<BatchOriginalDownloadItemResult> Items { get; set; } = new();
    }
}
