using InvoiceManager.Models.Entities;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace InvoiceManager.Services
{
    public interface IExportService
    {
        Task<byte[]> ExportDetailedExcelAsync(List<Invoice> invoices, TaxAccount? taxAccount, string title);
        Task<byte[]> ExportSummaryExcelAsync(List<Invoice> invoices, TaxAccount? taxAccount, string title);
        Task<byte[]> ExportInvoicesZipAsync(List<Invoice> invoices);
        string GenerateInvoiceHtml(Invoice invoice);
    }
}
