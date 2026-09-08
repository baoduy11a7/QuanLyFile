using System;
using System.ComponentModel.DataAnnotations;

namespace InvoiceManager.Models.Entities
{
    public class UserTaxAccount
    {
        public int Id { get; set; }

        [Required]
        public string UserId { get; set; } = string.Empty;
        public ApplicationUser? User { get; set; }

        [Required]
        public int TaxAccountId { get; set; }
        public TaxAccount? TaxAccount { get; set; }

        public bool IsDefault { get; set; } = false;

        public bool CanManage { get; set; } = true;

        public DateTime AssignedAt { get; set; } = DateTime.Now;
    }
}
