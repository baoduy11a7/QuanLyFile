using System;
using System.ComponentModel.DataAnnotations;

namespace InvoiceManager.Models.Entities
{
    public class SyncLog
    {
        public int Id { get; set; }

        public int TaxAccountId { get; set; }
        public TaxAccount? TaxAccount { get; set; }

        public DateTime SyncedAt { get; set; } = DateTime.Now;

        [Display(Name = "Số HĐ mới tìm thấy")]
        public int NewInvoiceCount { get; set; } = 0;

        [Required]
        [StringLength(50)]
        [Display(Name = "Trạng thái")]
        public string Status { get; set; } = "Thành công"; // Thành công, Thất bại, Cảnh báo

        [StringLength(1000)]
        [Display(Name = "Thông báo / Lỗi")]
        public string? ErrorMessage { get; set; }

        [StringLength(100)]
        public string SyncType { get; set; } = "Thư mục tự động"; // Thư mục tự động, API, Thủ công
    }
}
