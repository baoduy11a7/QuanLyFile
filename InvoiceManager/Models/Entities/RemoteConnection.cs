using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace InvoiceManager.Models.Entities
{
    public class RemoteConnection
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int TaxAccountId { get; set; }

        [ForeignKey("TaxAccountId")]
        public virtual TaxAccount TaxAccount { get; set; } = null!;

        [Required]
        [MaxLength(50)]
        public string ProviderName { get; set; } = "Gdt"; // "Gdt", "Misa", "Mock", etc.

        [Required]
        [MaxLength(100)]
        public string Username { get; set; } = string.Empty;

        /// <summary>
        /// Mật khẩu được mã hóa an toàn qua ASP.NET Core Data Protection (IDataProtector).
        /// Null nếu người dùng không chọn ghi nhớ.
        /// </summary>
        public string? EncryptedPassword { get; set; }

        public bool RememberPassword { get; set; } = false;

        public DateTime? LastSyncAt { get; set; }

        [MaxLength(50)]
        public string? LastSyncStatus { get; set; } // "Success", "Failed", "Partial"

        [MaxLength(500)]
        public string? LastSyncMessage { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
