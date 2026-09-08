using System;
using System.ComponentModel.DataAnnotations;

namespace InvoiceManager.Models.Entities
{
    public class FeatureRequest
    {
        public int Id { get; set; }

        public int TaxAccountId { get; set; }
        public TaxAccount? TaxAccount { get; set; }

        [Required]
        [StringLength(50)]
        public string RequestType { get; set; } = "Báo lỗi"; // "Báo lỗi", "Yêu cầu tính năng", "Hỗ trợ nghiệp vụ"

        [Required]
        [StringLength(255)]
        public string Title { get; set; } = string.Empty;

        [Required]
        [StringLength(2000)]
        public string Description { get; set; } = string.Empty;

        [StringLength(50)]
        public string Status { get; set; } = "Chờ tiếp nhận"; // "Chờ tiếp nhận", "Đang xử lý", "Đã xử lý"

        [StringLength(100)]
        public string? ContactName { get; set; }

        [StringLength(100)]
        public string? ContactPhone { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}
