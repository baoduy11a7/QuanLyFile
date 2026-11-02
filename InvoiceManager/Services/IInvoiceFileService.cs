using InvoiceManager.Models.Entities;
using InvoiceManager.Services.Providers.Models;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace InvoiceManager.Services
{
    public interface IInvoiceFileService
    {
        Task<List<InvoiceFile>> SaveDownloadedFilesAsync(int invoiceId, int taxAccountId, DownloadInvoiceResult downloadResult, CancellationToken ct = default);
        Task<InvoiceFile> SaveSingleFileAsync(int invoiceId, int taxAccountId, string originalFileName, string fileExtension, string contentType, byte[] content, string source, CancellationToken ct = default);
        Task<(Stream Stream, string ContentType, string DownloadFileName)?> GetFileForDownloadAsync(int fileId, int taxAccountId, CancellationToken ct = default);
        Task<(byte[] ZipBytes, string ZipFileName)?> DownloadAllFilesAsZipAsync(int invoiceId, int taxAccountId, CancellationToken ct = default);
        Task<List<InvoiceFile>> GetInvoiceFilesAsync(int invoiceId, int taxAccountId, CancellationToken ct = default);
        Task<(bool Success, string Message, int FilesCount)> SyncInvoiceFilesFromGdtAsync(int invoiceId, int taxAccountId, string? token = null, CancellationToken ct = default);
        Task<OriginalPdfFetchResult> GetOrFetchOriginalPdfAsync(int invoiceId, int taxAccountId, string? token = null, CancellationToken ct = default);
    }
}
