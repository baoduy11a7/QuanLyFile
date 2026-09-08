using System;
using System.ComponentModel.DataAnnotations;

namespace InvoiceManager.Models.Entities
{
    public class AuditLog
    {
        public int Id { get; set; }

        public int? TaxAccountId { get; set; }
        public TaxAccount? TaxAccount { get; set; }

        [StringLength(450)]
        public string? UserId { get; set; }
        public ApplicationUser? User { get; set; }

        [StringLength(100)]
        [Display(Name = "Người thực hiện")]
        public string? UserName { get; set; }

        [Required]
        [StringLength(100)]
        [Display(Name = "Hành động")]
        public string Action { get; set; } = string.Empty; // "Xem hóa đơn", "Xem XML", "Xuất Excel", "Xuất PDF", "Import XML", "Xóa", "Đổi MST"

        [StringLength(255)]
        [Display(Name = "Đối tượng")]
        public string Target { get; set; } = string.Empty;

        [StringLength(2000)]
        [Display(Name = "Chi tiết thao tác")]
        public string? Details { get; set; }

        [StringLength(50)]
        public string? IpAddress { get; set; }

        [Display(Name = "Thời gian ghi nhận")]
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }
}
