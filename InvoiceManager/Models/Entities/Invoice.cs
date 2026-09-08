using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace InvoiceManager.Models.Entities
{
    public class Invoice
    {
        public int Id { get; set; }

        [Required]
        public int TaxAccountId { get; set; }
        public TaxAccount? TaxAccount { get; set; }

        [Required]
        [StringLength(20)]
        [Display(Name = "Ký hiệu mẫu số/ký hiệu")]
        public string InvoiceSymbol { get; set; } = string.Empty; // vd: 1C25TXX, 2C25TKT

        [Required]
        [StringLength(20)]
        [Display(Name = "Số hóa đơn")]
        public string InvoiceNumber { get; set; } = string.Empty; // vd: 00000123

        [Required]
        [Display(Name = "Ngày lập hóa đơn")]
        public DateTime IssueDate { get; set; }

        // Người bán
        [Required]
        [StringLength(20)]
        [Display(Name = "MST Người bán")]
        public string SellerTaxCode { get; set; } = string.Empty;

        [Required]
        [StringLength(255)]
        [Display(Name = "Tên Người bán")]
        public string SellerName { get; set; } = string.Empty;

        [StringLength(500)]
        [Display(Name = "Địa chỉ Người bán")]
        public string? SellerAddress { get; set; }

        // Người mua
        [StringLength(20)]
        [Display(Name = "MST Người mua")]
        public string? BuyerTaxCode { get; set; }

        [StringLength(255)]
        [Display(Name = "Tên Người mua")]
        public string? BuyerName { get; set; }

        [StringLength(500)]
        [Display(Name = "Địa chỉ Người mua")]
        public string? BuyerAddress { get; set; }

        // Số tiền
        [Column(TypeName = "decimal(18,2)")]
        [Display(Name = "Tổng tiền chưa thuế")]
        public decimal AmountBeforeTax { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        [Display(Name = "Tổng tiền thuế GTGT")]
        public decimal TaxAmount { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        [Display(Name = "Tổng tiền thanh toán")]
        public decimal TotalAmount { get; set; }

        // Phân loại hóa đơn
        [Required]
        [StringLength(20)]
        [Display(Name = "Loại hóa đơn")]
        public string InvoiceType { get; set; } = "MuaVao"; // "MuaVao" hoặc "BanRa"

        [Display(Name = "Có mã cơ quan thuế")]
        public bool HasTaxCode { get; set; } = true;

        [Display(Name = "HĐ từ máy tính tiền")]
        public bool IsCashRegister { get; set; } = false;

        [StringLength(100)]
        [Display(Name = "Mã của Cơ quan Thuế")]
        public string? TaxAuthorityCode { get; set; }

        [Required]
        [StringLength(50)]
        [Display(Name = "Trạng thái hóa đơn")]
        public string Status { get; set; } = "Hóa đơn mới"; // Hóa đơn mới, Đã thay thế, Đã điều chỉnh, Đã bị hủy

        [StringLength(50)]
        [Display(Name = "Nhà cung cấp giải pháp")]
        public string SourceProvider { get; set; } = "TCT"; // MISA, Viettel, VNPT, BKAV, TCT, Khác

        // Lưu trữ nguyên bản
        [StringLength(500)]
        public string? RawXmlPath { get; set; }

        [StringLength(500)]
        public string? RawPdfPath { get; set; }

        [StringLength(255)]
        public string? OriginalFileName { get; set; }

        [Display(Name = "Thời gian tải lên")]
        public DateTime ImportedAt { get; set; } = DateTime.Now;

        [StringLength(100)]
        public string? ImportedByUserId { get; set; }

        // Nghiệp vụ kế toán & đối soát
        [Display(Name = "Đã vào sổ kế toán")]
        public bool IsReconciled { get; set; } = false;

        [StringLength(100)]
        [Display(Name = "Số chứng từ nội bộ")]
        public string? ReconciledRefNo { get; set; }

        public DateTime? ReconciledAt { get; set; }

        // Cảnh báo rủi ro
        [StringLength(30)]
        public string RiskLevel { get; set; } = "Normal"; // Normal, Warning, HighRisk

        [StringLength(500)]
        public string? RiskReason { get; set; }

        [StringLength(1000)]
        [Display(Name = "Ghi chú")]
        public string? Notes { get; set; }

        // Chi tiết
        public ICollection<InvoiceDetail> Details { get; set; } = new List<InvoiceDetail>();
    }
}
