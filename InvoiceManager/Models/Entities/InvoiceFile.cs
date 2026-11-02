using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace InvoiceManager.Models.Entities
{
    public class InvoiceFile
    {
        public int Id { get; set; }

        [Required]
        public int InvoiceId { get; set; }
        public Invoice? Invoice { get; set; }

        [Required]
        public int TaxAccountId { get; set; }
        public TaxAccount? TaxAccount { get; set; }

        [Required]
        [StringLength(255)]
        [Display(Name = "Tên file gốc")]
        public string OriginalFileName { get; set; } = string.Empty;

        [Required]
        [StringLength(255)]
        [Display(Name = "Tên lưu trữ máy chủ")]
        public string StoredFileName { get; set; } = string.Empty;

        [Required]
        [StringLength(30)]
        [Display(Name = "Định dạng file")]
        public string FileExtension { get; set; } = string.Empty; // .xml, .pdf, .zip, etc.

        [Required]
        [StringLength(100)]
        [Display(Name = "MIME Content-Type")]
        public string ContentType { get; set; } = "application/octet-stream";

        [Display(Name = "Dung lượng (bytes)")]
        public long FileSizeBytes { get; set; }

        [Required]
        [StringLength(500)]
        [Display(Name = "Đường dẫn lưu trữ")]
        public string FilePath { get; set; } = string.Empty;

        [StringLength(100)]
        [Display(Name = "Nguồn tải")]
        public string Source { get; set; } = "GDT-ExportXml"; // GDT-ExportXml, GDT-ExportPdf, GDT-PackageZip, Upload

        [Display(Name = "Thời gian tải")]
        public DateTime DownloadedAt { get; set; } = DateTime.Now;

        [StringLength(64)]
        [Display(Name = "Mã SHA-256")]
        public string Sha256Hash { get; set; } = string.Empty;

        [StringLength(50)]
        [Display(Name = "Trạng thái file")]
        public string Status { get; set; } = "Valid"; // Valid, Corrupted, Pending
    }
}
