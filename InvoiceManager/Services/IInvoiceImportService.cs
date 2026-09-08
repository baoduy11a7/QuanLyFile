using InvoiceManager.Models.Entities;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace InvoiceManager.Services
{
    public class ImportBatchResult
    {
        public int TotalFiles { get; set; }
        public int SuccessCount { get; set; }
        public int SkippedDuplicateCount { get; set; }
        public int FailedCount { get; set; }
        public List<string> Messages { get; set; } = new();
        public List<Invoice> ImportedInvoices { get; set; } = new();
    }

    public interface IInvoiceImportService
    {
        Task<ImportBatchResult> ImportXmlAsync(Stream xmlStream, string fileName, int taxAccountId, string invoiceType, string? userId);
        Task<ImportBatchResult> ImportZipAsync(Stream zipStream, int taxAccountId, string invoiceType, string? userId);
    }
}
