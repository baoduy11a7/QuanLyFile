using InvoiceManager.Models.ViewModels;
using System.IO;
using System.Threading.Tasks;

namespace InvoiceManager.Services
{
    public interface IExcelImportService
    {
        /// <summary>
        /// Sinh file Excel mẫu 2 sheets: Danh sách hóa đơn + Hướng dẫn định dạng dữ liệu
        /// </summary>
        Task<byte[]> GenerateTemplateAsync();

        /// <summary>
        /// Đọc file Excel người dùng tải lên, map theo tên cột (header-based) và validate chi tiết từng dòng
        /// </summary>
        Task<ExcelImportPreviewResult> PreviewExcelAsync(Stream stream, int taxAccountId, string defaultInvoiceType);

        /// <summary>
        /// Lưu các dòng hợp lệ vào CSDL theo từng batch nhỏ (100 dòng/transaction), gán SourceProvider="ManualExcel" và ghi AuditLog
        /// </summary>
        Task<ExcelImportSaveResult> SaveImportAsync(List<ExcelImportRowDto> validRows, int taxAccountId, string? userId);
    }
}
