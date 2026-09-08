using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace InvoiceManager.Models.Entities
{
    public class TaxAccount
    {
        public int Id { get; set; }

        [Required]
        [StringLength(20)]
        [Display(Name = "Mã số thuế")]
        public string TaxCode { get; set; } = string.Empty;

        [Required]
        [StringLength(255)]
        [Display(Name = "Tên công ty / Đơn vị")]
        public string CompanyName { get; set; } = string.Empty;

        [StringLength(500)]
        [Display(Name = "Địa chỉ đăng ký")]
        public string Address { get; set; } = string.Empty;

        [StringLength(100)]
        [EmailAddress]
        [Display(Name = "Email nhận hóa đơn")]
        public string? Email { get; set; }

        [StringLength(20)]
        [Display(Name = "Số điện thoại")]
        public string? PhoneNumber { get; set; }

        [StringLength(100)]
        [Display(Name = "Người đại diện")]
        public string? Representative { get; set; }

        [Display(Name = "Trạng thái hoạt động")]
        public bool IsActive { get; set; } = true;

        [Display(Name = "Ngày tạo")]
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        // Navigation
        public ICollection<Invoice> Invoices { get; set; } = new List<Invoice>();
        public ICollection<UserTaxAccount> UserTaxAccounts { get; set; } = new List<UserTaxAccount>();
        public ICollection<SyncLog> SyncLogs { get; set; } = new List<SyncLog>();
    }
}
