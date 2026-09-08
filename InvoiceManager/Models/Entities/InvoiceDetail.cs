using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace InvoiceManager.Models.Entities
{
    public class InvoiceDetail
    {
        public int Id { get; set; }

        [Required]
        public int InvoiceId { get; set; }
        public Invoice? Invoice { get; set; }

        public int LineNumber { get; set; } = 1;

        [StringLength(50)]
        [Display(Name = "Mã hàng hóa/dịch vụ")]
        public string? ItemCode { get; set; }

        [Required]
        [StringLength(500)]
        [Display(Name = "Tên hàng hóa, dịch vụ")]
        public string ItemName { get; set; } = string.Empty;

        [StringLength(50)]
        [Display(Name = "Đơn vị tính")]
        public string? Unit { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        [Display(Name = "Số lượng")]
        public decimal Quantity { get; set; } = 1;

        [Column(TypeName = "decimal(18,4)")]
        [Display(Name = "Đơn giá")]
        public decimal UnitPrice { get; set; } = 0;

        [Column(TypeName = "decimal(18,2)")]
        [Display(Name = "Thành tiền chưa thuế")]
        public decimal AmountBeforeTax { get; set; } = 0;

        [Column(TypeName = "decimal(5,2)")]
        [Display(Name = "Thuế suất GTGT (%)")]
        public decimal TaxRate { get; set; } = 10; // 0, 5, 8, 10, -1 là KCT (Không chịu thuế)

        [Column(TypeName = "decimal(18,2)")]
        [Display(Name = "Tiền thuế GTGT")]
        public decimal TaxAmount { get; set; } = 0;

        [Column(TypeName = "decimal(18,2)")]
        [Display(Name = "Tổng cộng")]
        public decimal TotalAmount { get; set; } = 0;

        [StringLength(255)]
        public string? Notes { get; set; }
    }
}
