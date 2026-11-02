using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace InvoiceManager.Models.ViewModels
{
    public class RemoteSyncRequestViewModel
    {
        [Required(ErrorMessage = "Vui lòng chọn cổng hóa đơn.")]
        public string ProviderName { get; set; } = "Gdt";

        [Required(ErrorMessage = "Mã số thuế không được để trống.")]
        public string TaxCode { get; set; } = string.Empty;

        [Required(ErrorMessage = "Vui lòng nhập tên đăng nhập cổng thuế.")]
        public string Username { get; set; } = string.Empty;

        [DataType(DataType.Password)]
        public string? Password { get; set; }

        public string? BearerToken { get; set; }

        public string? CaptchaKey { get; set; }

        public string? CaptchaCode { get; set; }

        [Required(ErrorMessage = "Vui lòng chọn loại hóa đơn cần lấy.")]
        public string InvoiceType { get; set; } = "MuaVao"; // "MuaVao", "BanRa", "All"

        [Required(ErrorMessage = "Vui lòng chọn ngày bắt đầu.")]
        [DataType(DataType.Date)]
        public DateTime FromDate { get; set; } = DateTime.Now.AddDays(-30);

        [Required(ErrorMessage = "Vui lòng chọn ngày kết thúc.")]
        [DataType(DataType.Date)]
        public DateTime ToDate { get; set; } = DateTime.Now;

        public bool RememberPassword { get; set; } = false;
    }

    public class RemoteJobStatusViewModel
    {
        public string JobId { get; set; } = string.Empty;
        public string Status { get; set; } = "Queued";
        public int Percent { get; set; } = 0;
        public string CurrentStage { get; set; } = string.Empty;
        public int TotalFound { get; set; } = 0;
        public int SuccessCount { get; set; } = 0;
        public int DuplicateCount { get; set; } = 0;
        public int FailedCount { get; set; } = 0;
        public int NewCount { get; set; } = 0;
        public int UpdatedCount { get; set; } = 0;
        public int FilesDownloadedCount { get; set; } = 0;
        public int FilesFailedCount { get; set; } = 0;
        public int MissingFilesCount { get; set; } = 0;
        public List<string> Errors { get; set; } = new();
        public string? ErrorMessage { get; set; }
        public List<string> Logs { get; set; } = new();
        public bool IsFinished { get; set; }
    }

    public class SyncWithOriginalFilesResultViewModel
    {
        public bool Success { get; set; }
        public string? JobId { get; set; }
        public string Message { get; set; } = string.Empty;
        public int TotalFound { get; set; }
        public int NewCount { get; set; }
        public int UpdatedCount { get; set; }
        public int FilesDownloadedCount { get; set; }
        public int FilesFailedCount { get; set; }
        public int MissingFilesCount { get; set; }
        public List<string> Errors { get; set; } = new();
    }

    public class SavedConnectionInfoViewModel
    {
        public string ProviderName { get; set; } = "Gdt";
        public string Username { get; set; } = string.Empty;
        public bool HasSavedPassword { get; set; }
        public DateTime? LastSyncAt { get; set; }
        public string? LastSyncStatus { get; set; }
        public string? LastSyncMessage { get; set; }
    }
}
