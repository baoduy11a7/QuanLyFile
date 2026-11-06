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

        [StringLength(20)]
        [Display(Name = "Ký hiệu mẫu số")]
        public string TemplateCode { get; set; } = "1"; // vd: 1, 2...

        [Required]
        [StringLength(20)]
        [Display(Name = "Số hóa đơn")]
        public string InvoiceNumber { get; set; } = string.Empty; // vd: 00000123

        [Required]
        [Display(Name = "Ngày lập hóa đơn")]
        public DateTime IssueDate { get; set; }

        [Display(Name = "Ngày người bán ký số")]
        public DateTime? SignedDate { get; set; }

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

        [StringLength(500)]
        [Display(Name = "Website người bán")]
        public string? SellerWebsite { get; set; }

        // Người mua
        [StringLength(20)]
        [Display(Name = "MST Người mua")]
        public string? BuyerTaxCode { get; set; }

        [StringLength(50)]
        [Display(Name = "CCCD/CMND Người mua")]
        public string? BuyerCitizenId { get; set; }

        [StringLength(255)]
        [Display(Name = "Tên Người mua")]
        public string? BuyerName { get; set; }

        [StringLength(500)]
        [Display(Name = "Địa chỉ Người mua")]
        public string? BuyerAddress { get; set; }

        // Tiền tệ & Tỉ giá
        [StringLength(20)]
        [Display(Name = "Đơn vị tiền tệ")]
        public string Currency { get; set; } = "VND";

        [Column(TypeName = "decimal(18,4)")]
        [Display(Name = "Tỉ giá")]
        public decimal ExchangeRate { get; set; } = 1;

        // Số tiền nguyên tệ
        [Column(TypeName = "decimal(18,2)")]
        [Display(Name = "Tổng tiền chưa thuế (nguyên tệ)")]
        public decimal AmountBeforeTax { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        [Display(Name = "Tổng tiền thuế (nguyên tệ)")]
        public decimal TaxAmount { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        [Display(Name = "Tổng tiền thanh toán (nguyên tệ)")]
        public decimal TotalAmount { get; set; }

        // Số tiền quy đổi VND
        [Column(TypeName = "decimal(18,2)")]
        [Display(Name = "Tiền chưa thuế (VND)")]
        public decimal AmountBeforeTaxVnd { get; set; } = 0;

        [Column(TypeName = "decimal(18,2)")]
        [Display(Name = "Tiền thuế (VND)")]
        public decimal TaxAmountVnd { get; set; } = 0;

        [Column(TypeName = "decimal(18,2)")]
        [Display(Name = "Tổng tiền thanh toán (VND)")]
        public decimal TotalAmountVnd { get; set; } = 0;

        [Column(TypeName = "decimal(18,2)")]
        [Display(Name = "Tổng tiền chiết khấu thương mại")]
        public decimal DiscountAmount { get; set; } = 0;

        [Column(TypeName = "decimal(18,2)")]
        [Display(Name = "Tổng tiền phí")]
        public decimal FeeAmount { get; set; } = 0;

        // Hình thức, Tính chất, Kiểm tra
        [StringLength(100)]
        [Display(Name = "Hình thức thanh toán")]
        public string? PaymentMethod { get; set; } = "TM/CK";

        [StringLength(100)]
        [Display(Name = "Tính chất")]
        public string? InvoiceProperty { get; set; } = "Hàng hoá/dịch vụ";

        [StringLength(200)]
        [Display(Name = "Kết quả kiểm tra hóa đơn")]
        public string? VerificationResult { get; set; } = "Đã cấp mã hoá đơn";

        [StringLength(50)]
        [Display(Name = "Biển số xe")]
        public string? VehicleNumber { get; set; }

        [StringLength(500)]
        [Display(Name = "Ghi chú hóa đơn bị thay thế, điều chỉnh")]
        public string? ReplacedOrAdjustedNote { get; set; }

        [StringLength(100)]
        [Display(Name = "Mã tra cứu hóa đơn gốc")]
        public string? LookupCode { get; set; }

        [StringLength(1000)]
        [Display(Name = "Url tra cứu hóa đơn gốc")]
        public string? LookupUrl { get; set; }

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

        // Danh sách tệp gốc (XML, PDF, ZIP, tệp đính kèm)
        public ICollection<InvoiceFile> Files { get; set; } = new List<InvoiceFile>();
    }
}
