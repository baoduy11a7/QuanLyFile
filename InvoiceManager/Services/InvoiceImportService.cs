using InvoiceManager.Data;
using InvoiceManager.Models.Entities;
using InvoiceManager.Services.Parsers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InvoiceManager.Services
{
    public class InvoiceImportService : IInvoiceImportService
    {
        private readonly ApplicationDbContext _db;
        private readonly IInvoiceParserFactory _parserFactory;
        private readonly IAuditLogService _auditLog;
        private readonly ILogger<InvoiceImportService> _logger;
        private readonly string _storageRoot;

        public InvoiceImportService(
            ApplicationDbContext db,
            IInvoiceParserFactory parserFactory,
            IAuditLogService auditLog,
            IConfiguration config,
            ILogger<InvoiceImportService> logger)
        {
            _db = db;
            _parserFactory = parserFactory;
            _auditLog = auditLog;
            _logger = logger;
            _storageRoot = config.GetValue<string>("InvoiceSettings:StorageRootPath") ?? "App_Data/RawFiles";
        }

        public async Task<ImportBatchResult> ImportXmlAsync(Stream xmlStream, string fileName, int taxAccountId, string invoiceType, string? userId)
        {
            var batchResult = new ImportBatchResult { TotalFiles = 1 };

            try
            {
                // Đọc toàn bộ nội dung file vào memory stream để vừa lưu vừa parse
                using var ms = new MemoryStream();
                await xmlStream.CopyToAsync(ms);
                ms.Position = 0;

                using var reader = new StreamReader(ms, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                var xmlText = await reader.ReadToEndAsync();
                ms.Position = 0;

                var parser = _parserFactory.GetParser(xmlText);
                var parseResult = await parser.ParseAsync(ms, fileName);

                if (!parseResult.Success)
                {
                    batchResult.FailedCount++;
                    batchResult.Messages.Add($"File '{fileName}': {parseResult.ErrorMessage}");
                    return batchResult;
                }

                // 1. Lưu file XML gốc vào thư mục an toàn
                var savedFilePath = await SaveRawFileAsync(ms, taxAccountId, fileName);

                // 2. Kiểm tra tính idempotent (tránh trùng lặp theo khóa nghiệp vụ)
                var existing = await _db.Invoices
                    .FirstOrDefaultAsync(i => i.TaxAccountId == taxAccountId 
                                           && i.InvoiceType == invoiceType 
                                           && i.SellerTaxCode == parseResult.SellerTaxCode 
                                           && i.InvoiceSymbol == parseResult.InvoiceSymbol 
                                           && i.InvoiceNumber == parseResult.InvoiceNumber);

                if (existing != null)
                {
                    batchResult.SkippedDuplicateCount++;
                    batchResult.Messages.Add($"Hóa đơn {parseResult.InvoiceSymbol}-{parseResult.InvoiceNumber} của MST {parseResult.SellerTaxCode} đã tồn tại trên hệ thống. Đã bỏ qua để tránh trùng lặp.");
                    return batchResult;
                }

                // 3. Tạo Entity Invoice
                var invoice = new Invoice
                {
                    TaxAccountId = taxAccountId,
                    InvoiceSymbol = parseResult.InvoiceSymbol,
                    InvoiceNumber = parseResult.InvoiceNumber,
                    IssueDate = parseResult.IssueDate,
                    SellerTaxCode = parseResult.SellerTaxCode,
                    SellerName = parseResult.SellerName,
                    SellerAddress = parseResult.SellerAddress,
                    BuyerTaxCode = parseResult.BuyerTaxCode,
                    BuyerName = parseResult.BuyerName,
                    BuyerAddress = parseResult.BuyerAddress,
                    AmountBeforeTax = parseResult.AmountBeforeTax,
                    TaxAmount = parseResult.TaxAmount,
                    TotalAmount = parseResult.TotalAmount,
                    InvoiceType = invoiceType,
                    HasTaxCode = parseResult.HasTaxCode,
                    IsCashRegister = parseResult.IsCashRegister,
                    TaxAuthorityCode = parseResult.TaxAuthorityCode,
                    Status = parseResult.Status,
                    SourceProvider = parseResult.SourceProvider,
                    RawXmlPath = savedFilePath,
                    OriginalFileName = fileName,
                    ImportedAt = DateTime.Now,
                    ImportedByUserId = userId,
                    RiskLevel = parseResult.ValidationWarnings.Any() ? "Warning" : "Normal",
                    RiskReason = parseResult.ValidationWarnings.Any() ? string.Join("; ", parseResult.ValidationWarnings) : null
                };

                foreach (var d in parseResult.Details)
                {
                    invoice.Details.Add(new InvoiceDetail
                    {
                        LineNumber = d.LineNumber,
                        ItemCode = d.ItemCode,
                        ItemName = d.ItemName,
                        Unit = d.Unit,
                        Quantity = d.Quantity,
                        UnitPrice = d.UnitPrice,
                        AmountBeforeTax = d.AmountBeforeTax,
                        TaxRate = d.TaxRate,
                        TaxAmount = d.TaxAmount,
                        TotalAmount = d.TotalAmount
                    });
                }

                _db.Invoices.Add(invoice);
                await _db.SaveChangesAsync();

                await _auditLog.LogActionAsync(
                    "Import XML", 
                    $"HĐ {invoice.InvoiceSymbol}-{invoice.InvoiceNumber}", 
                    $"MST: {invoice.SellerTaxCode}, Tổng tiền: {invoice.TotalAmount:N0} đ", 
                    taxAccountId);

                batchResult.SuccessCount++;
                batchResult.ImportedInvoices.Add(invoice);
                batchResult.Messages.Add($"Thành công: Import hóa đơn {invoice.InvoiceSymbol}-{invoice.InvoiceNumber} ({invoice.SellerName}).");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi import XML file {FileName}", fileName);
                batchResult.FailedCount++;
                batchResult.Messages.Add($"File '{fileName}' gặp lỗi: {ex.Message}");
            }

            return batchResult;
        }

        public async Task<ImportBatchResult> ImportZipAsync(Stream zipStream, int taxAccountId, string invoiceType, string? userId)
        {
            var overallResult = new ImportBatchResult();

            try
            {
                using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
                var xmlEntries = archive.Entries
                    .Where(e => e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                overallResult.TotalFiles = xmlEntries.Count;

                if (!xmlEntries.Any())
                {
                    overallResult.Messages.Add("File ZIP không chứa bất kỳ file .xml hóa đơn nào.");
                    return overallResult;
                }

                foreach (var entry in xmlEntries)
                {
                    using var entryStream = entry.Open();
                    var singleResult = await ImportXmlAsync(entryStream, entry.Name, taxAccountId, invoiceType, userId);

                    overallResult.SuccessCount += singleResult.SuccessCount;
                    overallResult.SkippedDuplicateCount += singleResult.SkippedDuplicateCount;
                    overallResult.FailedCount += singleResult.FailedCount;
                    overallResult.Messages.AddRange(singleResult.Messages);
                    overallResult.ImportedInvoices.AddRange(singleResult.ImportedInvoices);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi giải nén và import ZIP");
                overallResult.FailedCount++;
                overallResult.Messages.Add($"Lỗi khi xử lý file ZIP: {ex.Message}");
            }

            return overallResult;
        }

        private async Task<string> SaveRawFileAsync(Stream stream, int taxAccountId, string originalFileName)
        {
            var now = DateTime.Now;
            var directory = Path.Combine(_storageRoot, taxAccountId.ToString(), now.Year.ToString(), now.Month.ToString("D2"));
            Directory.CreateDirectory(directory);

            var safeFileName = $"{Guid.NewGuid():N}_{Path.GetFileName(originalFileName)}";
            var fullPath = Path.Combine(directory, safeFileName);

            stream.Position = 0;
            using var fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write);
            await stream.CopyToAsync(fileStream);

            return fullPath;
        }
    }
}
