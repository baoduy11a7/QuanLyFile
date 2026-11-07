using InvoiceManager.Data;
using InvoiceManager.Models.Entities;
using InvoiceManager.Services.Parsers;
using InvoiceManager.Services.Providers;
using InvoiceManager.Services.Providers.Gdt;
using InvoiceManager.Services.Providers.Models;
using InvoiceManager.Services.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace InvoiceManager.Services
{
    public class InvoiceFileService : IInvoiceFileService
    {
        private readonly ApplicationDbContext _db;
        private readonly IInvoiceSourceProvider _provider;
        private readonly IAuditLogService _auditLog;
        private readonly ICredentialProtector _protector;
        private readonly ILogger<InvoiceFileService> _logger;
        private readonly IExportService? _exportService;
        private readonly IInvoiceParserFactory? _parserFactory;
        private readonly string _storageRoot;

        public InvoiceFileService(
            ApplicationDbContext db,
            IInvoiceSourceProvider provider,
            IAuditLogService auditLog,
            ICredentialProtector protector,
            IConfiguration config,
            ILogger<InvoiceFileService> logger,
            IExportService? exportService = null,
            IInvoiceParserFactory? parserFactory = null)
        {
            _db = db;
            _provider = provider;
            _auditLog = auditLog;
            _protector = protector;
            _logger = logger;
            _exportService = exportService;
            _parserFactory = parserFactory;
            _storageRoot = config.GetValue<string>("InvoiceSettings:StorageRootPath") ?? "App_Data/RawFiles";
        }

        public async Task<List<InvoiceFile>> SaveDownloadedFilesAsync(
            int invoiceId,
            int taxAccountId,
            DownloadInvoiceResult downloadResult,
            CancellationToken ct = default)
        {
            var savedFiles = new List<InvoiceFile>();
            if (downloadResult == null || !downloadResult.Files.Any()) return savedFiles;

            var invoice = await _db.Invoices
                .Include(i => i.Files)
                .FirstOrDefaultAsync(i => i.Id == invoiceId && i.TaxAccountId == taxAccountId, ct);

            if (invoice == null)
            {
                _logger.LogWarning("Không tìm thấy hóa đơn Id {InvoiceId} cho TaxAccountId {TaxAccountId}", invoiceId, taxAccountId);
                return savedFiles;
            }

            var now = DateTime.Now;
            var directory = Path.Combine(_storageRoot, taxAccountId.ToString(), now.Year.ToString(), now.Month.ToString("D2"));
            Directory.CreateDirectory(directory);

            foreach (var dlFile in downloadResult.Files)
            {
                ct.ThrowIfCancellationRequested();
                if (dlFile.Content == null || dlFile.Content.Length == 0) continue;

                var hash = !string.IsNullOrEmpty(dlFile.Sha256Hash) ? dlFile.Sha256Hash : ComputeSha256(dlFile.Content);

                // Kiểm tra xem file này đã lưu cho hóa đơn chưa (dựa theo hash hoặc tên file gốc)
                var existing = invoice.Files.FirstOrDefault(f => f.Sha256Hash == hash);
                if (existing != null && File.Exists(existing.FilePath))
                {
                    savedFiles.Add(existing);
                    continue;
                }

                var cleanExt = (!string.IsNullOrEmpty(dlFile.FileExtension) ? dlFile.FileExtension : Path.GetExtension(dlFile.FileName)).ToLowerInvariant();
                var safeOriginalName = Path.GetFileName(dlFile.FileName);
                if (string.IsNullOrWhiteSpace(safeOriginalName))
                {
                    safeOriginalName = $"Invoice_{invoice.InvoiceSymbol}_{invoice.InvoiceNumber}{cleanExt}";
                }

                var storedFileName = $"{Guid.NewGuid():N}_{safeOriginalName}";
                var fullPath = Path.Combine(directory, storedFileName);

                await File.WriteAllBytesAsync(fullPath, dlFile.Content, ct);

                var fileEntity = new InvoiceFile
                {
                    InvoiceId = invoiceId,
                    TaxAccountId = taxAccountId,
                    OriginalFileName = safeOriginalName,
                    StoredFileName = storedFileName,
                    FileExtension = cleanExt,
                    ContentType = !string.IsNullOrWhiteSpace(dlFile.ContentType) ? dlFile.ContentType : "application/octet-stream",
                    FileSizeBytes = dlFile.Content.Length,
                    FilePath = fullPath,
                    Source = dlFile.Source,
                    DownloadedAt = DateTime.Now,
                    Sha256Hash = hash,
                    Status = "Valid"
                };

                _db.InvoiceFiles.Add(fileEntity);
                invoice.Files.Add(fileEntity);
                savedFiles.Add(fileEntity);

                // Cập nhật đường dẫn cho các trường kế thừa (ưu tiên ghi đè nếu là PDF gốc trực tiếp từ nhà cung cấp)
                if (cleanExt == ".xml" && string.IsNullOrEmpty(invoice.RawXmlPath))
                {
                    invoice.RawXmlPath = fullPath;
                }
                else if (cleanExt == ".pdf" && (string.IsNullOrEmpty(invoice.RawPdfPath) ||
                         string.Equals(dlFile.Source, "Supplier-DirectPdf", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(dlFile.Source, "UploadOriginal", StringComparison.OrdinalIgnoreCase)))
                {
                    invoice.RawPdfPath = fullPath;
                }
            }

            await _db.SaveChangesAsync(ct);
            return savedFiles;
        }

        public async Task<InvoiceFile> SaveSingleFileAsync(
            int invoiceId,
            int taxAccountId,
            string originalFileName,
            string fileExtension,
            string contentType,
            byte[] content,
            string source,
            CancellationToken ct = default)
        {
            var invoice = await _db.Invoices
                .Include(i => i.Files)
                .FirstOrDefaultAsync(i => i.Id == invoiceId && i.TaxAccountId == taxAccountId, ct);

            if (invoice == null)
            {
                throw new InvalidOperationException($"Không tìm thấy hóa đơn ID {invoiceId} thuộc tài khoản thuế ID {taxAccountId}");
            }

            var hash = ComputeSha256(content);
            var existing = invoice.Files.FirstOrDefault(f => f.Sha256Hash == hash);
            if (existing != null && File.Exists(existing.FilePath))
            {
                if (string.Equals(existing.FileExtension, ".pdf", StringComparison.OrdinalIgnoreCase) &&
                    (string.Equals(source, "Supplier-DirectPdf", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(source, "UploadOriginal", StringComparison.OrdinalIgnoreCase)) &&
                    !string.Equals(invoice.RawPdfPath, existing.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    invoice.RawPdfPath = existing.FilePath;
                    await _db.SaveChangesAsync(ct);
                }
                return existing;
            }

            var now = DateTime.Now;
            var directory = Path.Combine(_storageRoot, taxAccountId.ToString(), now.Year.ToString(), now.Month.ToString("D2"));
            Directory.CreateDirectory(directory);

            var safeOriginalName = Path.GetFileName(originalFileName);
            var cleanExt = (!string.IsNullOrEmpty(fileExtension) ? fileExtension : Path.GetExtension(safeOriginalName)).ToLowerInvariant();
            var storedFileName = $"{Guid.NewGuid():N}_{safeOriginalName}";
            var fullPath = Path.Combine(directory, storedFileName);

            await File.WriteAllBytesAsync(fullPath, content, ct);

            var fileEntity = new InvoiceFile
            {
                InvoiceId = invoiceId,
                TaxAccountId = taxAccountId,
                OriginalFileName = safeOriginalName,
                StoredFileName = storedFileName,
                FileExtension = cleanExt,
                ContentType = contentType,
                FileSizeBytes = content.Length,
                FilePath = fullPath,
                Source = source,
                DownloadedAt = DateTime.Now,
                Sha256Hash = hash,
                Status = "Valid"
            };

            _db.InvoiceFiles.Add(fileEntity);
            invoice.Files.Add(fileEntity);

            if (cleanExt == ".xml" && string.IsNullOrEmpty(invoice.RawXmlPath))
            {
                invoice.RawXmlPath = fullPath;
            }
            else if (cleanExt == ".pdf" && (string.IsNullOrEmpty(invoice.RawPdfPath) ||
                     string.Equals(source, "Supplier-DirectPdf", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(source, "UploadOriginal", StringComparison.OrdinalIgnoreCase)))
            {
                invoice.RawPdfPath = fullPath;
            }

            await _db.SaveChangesAsync(ct);
            return fileEntity;
        }

        public async Task<(Stream Stream, string ContentType, string DownloadFileName)?> GetFileForDownloadAsync(
            int fileId,
            int taxAccountId,
            CancellationToken ct = default)
        {
            var file = await _db.InvoiceFiles
                .AsNoTracking()
                .FirstOrDefaultAsync(f => f.Id == fileId && f.TaxAccountId == taxAccountId, ct);

            if (file == null || string.IsNullOrEmpty(file.FilePath) || !File.Exists(file.FilePath))
            {
                return null;
            }

            var stream = new FileStream(file.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return (stream, file.ContentType, file.OriginalFileName);
        }

        public async Task<(byte[] ZipBytes, string ZipFileName)?> DownloadAllFilesAsZipAsync(
            int invoiceId,
            int taxAccountId,
            CancellationToken ct = default)
        {
            var invoice = await _db.Invoices
                .Include(i => i.Files)
                .AsNoTracking()
                .FirstOrDefaultAsync(i => i.Id == invoiceId && i.TaxAccountId == taxAccountId, ct);

            if (invoice == null || !invoice.Files.Any())
            {
                return null;
            }

            using var zipMs = new MemoryStream();
            using (var archive = new ZipArchive(zipMs, ZipArchiveMode.Create, true))
            {
                var addedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var f in invoice.Files)
                {
                    if (string.IsNullOrEmpty(f.FilePath) || !File.Exists(f.FilePath)) continue;

                    var entryName = f.OriginalFileName;
                    if (addedNames.Contains(entryName))
                    {
                        var ext = Path.GetExtension(entryName);
                        var baseName = Path.GetFileNameWithoutExtension(entryName);
                        entryName = $"{baseName}_{f.Id}{ext}";
                    }
                    addedNames.Add(entryName);

                    var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                    using var entryStream = entry.Open();
                    using var fileStream = File.OpenRead(f.FilePath);
                    await fileStream.CopyToAsync(entryStream, ct);
                }
            }

            if (zipMs.Length == 0) return null;

            var zipName = $"HD_{invoice.InvoiceSymbol}_{invoice.InvoiceNumber}_TatCaTepGoc.zip";
            return (zipMs.ToArray(), zipName);
        }

        public async Task<List<InvoiceFile>> GetInvoiceFilesAsync(int invoiceId, int taxAccountId, CancellationToken ct = default)
        {
            return await _db.InvoiceFiles
                .AsNoTracking()
                .Where(f => f.InvoiceId == invoiceId && f.TaxAccountId == taxAccountId)
                .OrderByDescending(f => f.DownloadedAt)
                .ToListAsync(ct);
        }

        public async Task<(bool Success, string Message, int FilesCount)> SyncInvoiceFilesFromGdtAsync(
            int invoiceId,
            int taxAccountId,
            string? token = null,
            CancellationToken ct = default)
        {
            var invoice = await _db.Invoices
                .Include(i => i.TaxAccount)
                .Include(i => i.Files)
                .FirstOrDefaultAsync(i => i.Id == invoiceId && i.TaxAccountId == taxAccountId, ct);

            if (invoice == null)
            {
                return (false, "Không tìm thấy hóa đơn trên hệ thống.", 0);
            }

            token = await ResolveActiveGdtTokenAsync(taxAccountId, invoice.TaxAccount?.TaxCode, token, ct);

            if (string.IsNullOrWhiteSpace(token))
            {
                return (false, "Chưa có phiên kết nối Cổng Thuế hoặc Token đã hết hạn. Vui lòng đăng nhập Cổng Thuế tại mục Đồng bộ Hóa đơn.", 0);
            }

            // 2. Tạo đối tượng RemoteInvoice để truy vấn tải file
            var remoteInv = new RemoteInvoice
            {
                InvoiceSymbol = invoice.InvoiceSymbol,
                InvoiceNumber = invoice.InvoiceNumber,
                RawInvoiceNumber = long.TryParse(invoice.InvoiceNumber, out var n) ? n.ToString() : invoice.InvoiceNumber,
                SellerTaxCode = invoice.SellerTaxCode,
                BuyerTaxCode = invoice.BuyerTaxCode ?? invoice.TaxAccount?.TaxCode ?? string.Empty,
                InvoiceType = invoice.InvoiceType,
                IssueDate = invoice.IssueDate
            };

            try
            {
                var dlResult = await _provider.DownloadAllInvoiceFilesAsync(remoteInv, token, ct);
                if (!dlResult.Success || !dlResult.Files.Any())
                {
                    return (false, "Cổng Thuế không trả về tệp hợp lệ cho hóa đơn này (Có thể bên phát hành chưa ký số tải file lên hoặc token hết hạn).", 0);
                }

                var saved = await SaveDownloadedFilesAsync(invoiceId, taxAccountId, dlResult, ct);
                await _auditLog.LogActionAsync(
                    "Tải tệp gốc Cổng Thuế", 
                    $"{invoice.InvoiceSymbol}-{invoice.InvoiceNumber} ({saved.Count} tệp)", 
                    null, 
                    taxAccountId);

                return (true, $"Đã tải và lưu thành công {saved.Count} tệp gốc từ Tổng cục Thuế.", saved.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi đồng bộ tệp hóa đơn {Symbol}-{Number} từ Cổng Thuế", invoice.InvoiceSymbol, invoice.InvoiceNumber);
                return (false, $"Lỗi kết nối Cổng Thuế: {ex.Message}", 0);
            }
        }

        public async Task<OriginalPdfFetchResult> GetOrFetchOriginalPdfAsync(
            int invoiceId,
            int taxAccountId,
            string? token = null,
            CancellationToken ct = default)
        {
            var invoice = await _db.Invoices
                .Include(i => i.TaxAccount)
                .Include(i => i.Files)
                .Include(i => i.Details)
                .FirstOrDefaultAsync(i => i.Id == invoiceId && i.TaxAccountId == taxAccountId, ct);

            if (invoice == null)
            {
                return new OriginalPdfFetchResult
                {
                    Success = false,
                    ErrorCode = "INVOICE_NOT_FOUND",
                    ErrorMessage = "Không tìm thấy hóa đơn trên hệ thống hoặc bạn không có quyền truy cập.",
                    HttpStatusCode = 404
                };
            }

            // 1. Ưu tiên kiểm tra nếu đã có tệp PDF gốc trực tiếp từ Nhà cung cấp (Supplier-DirectPdf / UploadOriginal / GDT-ZipEntry) trong hệ thống
            var directSupplierPdfFile = invoice.Files
                .Where(f => string.Equals(f.FileExtension, ".pdf", StringComparison.OrdinalIgnoreCase)
                            && f.Status == "Valid"
                            && (string.Equals(f.Source, "Supplier-DirectPdf", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(f.Source, "UploadOriginal", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(f.Source, "GDT-ZipEntry", StringComparison.OrdinalIgnoreCase))
                            && !string.IsNullOrWhiteSpace(f.FilePath)
                            && File.Exists(f.FilePath))
                .OrderByDescending(f => f.DownloadedAt)
                .FirstOrDefault();

            if (directSupplierPdfFile != null)
            {
                try
                {
                    var directBytes = await File.ReadAllBytesAsync(directSupplierPdfFile.FilePath, ct);
                    if (GdtPortalProvider.IsValidPdfSignature(directBytes) && directBytes.Length > 3500)
                    {
                        return new OriginalPdfFetchResult
                        {
                            Success = true,
                            PdfBytes = directBytes,
                            FileName = !string.IsNullOrWhiteSpace(directSupplierPdfFile.OriginalFileName)
                                ? directSupplierPdfFile.OriginalFileName
                                : $"HD_{invoice.InvoiceSymbol}_{invoice.InvoiceNumber}_GocNCC.pdf",
                            ContentType = "application/pdf",
                            Sha256Hash = !string.IsNullOrWhiteSpace(directSupplierPdfFile.Sha256Hash)
                                ? directSupplierPdfFile.Sha256Hash
                                : ComputeSha256(directBytes),
                            Source = directSupplierPdfFile.Source,
                            SavedFilePath = directSupplierPdfFile.FilePath,
                            HttpStatusCode = 200
                        };
                    }
                }
                catch { }
            }

            var siblingInvoices = await _db.Invoices
                .Include(i => i.Files)
                .Where(i => i.Id != invoice.Id &&
                            i.SellerTaxCode == invoice.SellerTaxCode &&
                            i.InvoiceSymbol == invoice.InvoiceSymbol &&
                            i.InvoiceNumber == invoice.InvoiceNumber)
                .ToListAsync(ct);

            // 1b. Nếu chưa có PDF gốc trực tiếp từ Nhà cung cấp, kiểm tra ngay file XML của hóa đơn
            // để gọi API trực tiếp của Nhà cung cấp (Viettel SInvoice, MISA meInvoice, Vin-HOADON, TS24...) lấy đúng bản thể hiện gốc có logo
            var xmlCandidates = new List<string>();
            if (!string.IsNullOrEmpty(invoice.RawXmlPath) && File.Exists(invoice.RawXmlPath))
            {
                xmlCandidates.Add(invoice.RawXmlPath);
            }
            foreach (var f in invoice.Files.Where(f => string.Equals(f.FileExtension, ".xml", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(f.FilePath) && File.Exists(f.FilePath)))
            {
                if (!xmlCandidates.Contains(f.FilePath, StringComparer.OrdinalIgnoreCase)) xmlCandidates.Add(f.FilePath);
            }
            foreach (var sib in siblingInvoices)
            {
                if (!string.IsNullOrEmpty(sib.RawXmlPath) && File.Exists(sib.RawXmlPath) && !xmlCandidates.Contains(sib.RawXmlPath, StringComparer.OrdinalIgnoreCase))
                {
                    xmlCandidates.Add(sib.RawXmlPath);
                }
                foreach (var sf in sib.Files.Where(f => string.Equals(f.FileExtension, ".xml", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(f.FilePath) && File.Exists(f.FilePath)))
                {
                    if (!xmlCandidates.Contains(sf.FilePath, StringComparer.OrdinalIgnoreCase)) xmlCandidates.Add(sf.FilePath);
                }
            }

            foreach (var xmlPath in xmlCandidates)
            {
                try
                {
                    var xBytes = await File.ReadAllBytesAsync(xmlPath, ct);
                    var vPdf = await GdtPortalProvider.TryDownloadVendorPdfFromXmlAsync(xBytes, ct);
                    if (vPdf != null && GdtPortalProvider.IsValidPdfSignature(vPdf) && vPdf.Length > 3500)
                    {
                        var savedVPdf = await SaveSingleFileAsync(
                            invoiceId,
                            taxAccountId,
                            $"HD_{invoice.InvoiceSymbol}_{invoice.InvoiceNumber}_GocNCC.pdf",
                            ".pdf",
                            "application/pdf",
                            vPdf,
                            "Supplier-DirectPdf",
                            ct);

                        return new OriginalPdfFetchResult
                        {
                            Success = true,
                            PdfBytes = vPdf,
                            FileName = savedVPdf.OriginalFileName,
                            ContentType = "application/pdf",
                            Sha256Hash = savedVPdf.Sha256Hash,
                            Source = savedVPdf.Source,
                            SavedFilePath = savedVPdf.FilePath,
                            HttpStatusCode = 200
                        };
                    }
                }
                catch { }
            }

            // 1c. Dự phòng: Nếu nhà cung cấp không có API tải PDF trực tiếp không cần CAPTCHA, sử dụng tệp PDF đã lưu trong Cache (ví dụ GDT-OfficialPackagePdf)
            var cachedPdfFile = invoice.Files
                .Where(f => string.Equals(f.FileExtension, ".pdf", StringComparison.OrdinalIgnoreCase)
                            && f.Status == "Valid"
                            && !f.Source.StartsWith("Mock", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(f.Source, "GDT-StoredInvoicePdf", StringComparison.OrdinalIgnoreCase)
                            && !f.OriginalFileName.StartsWith("Mock_", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.DownloadedAt)
                .FirstOrDefault();

            var candidatePaths = new List<(string Path, InvoiceFile? Entity)>();
            if (cachedPdfFile != null && !string.IsNullOrWhiteSpace(cachedPdfFile.FilePath))
            {
                candidatePaths.Add((cachedPdfFile.FilePath, cachedPdfFile));
            }
            if (!string.IsNullOrWhiteSpace(invoice.RawPdfPath) &&
                !invoice.RawPdfPath.Contains("Mock_", StringComparison.OrdinalIgnoreCase) &&
                candidatePaths.All(c => !string.Equals(c.Path, invoice.RawPdfPath, StringComparison.OrdinalIgnoreCase)))
            {
                candidatePaths.Add((invoice.RawPdfPath, null));
            }

            foreach (var candidate in candidatePaths)
            {
                if (!File.Exists(candidate.Path)) continue;

                try
                {
                    var cachedBytes = await File.ReadAllBytesAsync(candidate.Path, ct);
                    if (GdtPortalProvider.IsValidPdfSignature(cachedBytes))
                    {
                        var sha256 = candidate.Entity?.Sha256Hash;
                        if (string.IsNullOrWhiteSpace(sha256))
                        {
                            sha256 = ComputeSha256(cachedBytes);
                        }

                        var fileName = candidate.Entity?.OriginalFileName;
                        if (string.IsNullOrWhiteSpace(fileName))
                        {
                            fileName = $"HD_{invoice.InvoiceSymbol}_{invoice.InvoiceNumber}.pdf";
                        }

                        return new OriginalPdfFetchResult
                        {
                            Success = true,
                            PdfBytes = cachedBytes,
                            FileName = fileName,
                            ContentType = "application/pdf",
                            Sha256Hash = sha256,
                            Source = "Cache",
                            SavedFilePath = candidate.Path,
                            HttpStatusCode = 200
                        };
                    }
                    else
                    {
                        _logger.LogWarning("File PDF đã lưu tại {Path} cho hóa đơn {Id} không có chữ ký %PDF- hợp lệ. Sẽ gọi lại API Tổng cục Thuế.", candidate.Path, invoiceId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Không thể đọc file PDF cache tại {Path} cho hóa đơn {Id}", candidate.Path, invoiceId);
                }
            }

            // 1d. Kiểm tra nếu có bản ghi cùng hóa đơn (cùng SellerTaxCode, InvoiceSymbol, InvoiceNumber) đã có sẵn tệp PDF trên đĩa
            foreach (var sib in siblingInvoices)
            {
                var sibPdfPaths = sib.Files
                    .Where(f => string.Equals(f.FileExtension, ".pdf", StringComparison.OrdinalIgnoreCase) &&
                                f.Status == "Valid" &&
                                !f.Source.StartsWith("Mock", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(f.Source, "GDT-StoredInvoicePdf", StringComparison.OrdinalIgnoreCase) &&
                                !string.IsNullOrWhiteSpace(f.FilePath))
                    .Select(f => f.FilePath)
                    .ToList();

                if (!string.IsNullOrWhiteSpace(sib.RawPdfPath) && !sib.RawPdfPath.Contains("Mock_", StringComparison.OrdinalIgnoreCase))
                {
                    sibPdfPaths.Add(sib.RawPdfPath);
                }

                foreach (var sibPath in sibPdfPaths.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!File.Exists(sibPath)) continue;
                    try
                    {
                        var sibBytes = await File.ReadAllBytesAsync(sibPath, ct);
                        if (GdtPortalProvider.IsValidPdfSignature(sibBytes))
                        {
                            var savedCopy = await SaveSingleFileAsync(
                                invoiceId,
                                taxAccountId,
                                $"GDT_{invoice.InvoiceSymbol}_{invoice.InvoiceNumber}.pdf",
                                ".pdf",
                                "application/pdf",
                                sibBytes,
                                "GDT-OfficialPackagePdf",
                                ct);

                            return new OriginalPdfFetchResult
                            {
                                Success = true,
                                PdfBytes = sibBytes,
                                FileName = savedCopy.OriginalFileName,
                                ContentType = "application/pdf",
                                Sha256Hash = savedCopy.Sha256Hash,
                                Source = "Cache",
                                SavedFilePath = savedCopy.FilePath,
                                HttpStatusCode = 200
                            };
                        }
                    }
                    catch { }
                }
            }

            // 1c. Nếu đã lưu gói ZIP gốc từ Tổng cục Thuế (GDT-PackageZip) trong hệ thống thì kết xuất PDF từ gói bản thể hiện gốc của GDT và lưu cache
            var cachedGdtZip = invoice.Files
                .Concat(siblingInvoices.SelectMany(s => s.Files))
                .Where(f => string.Equals(f.FileExtension, ".zip", StringComparison.OrdinalIgnoreCase)
                            && f.Status == "Valid"
                            && string.Equals(f.Source, "GDT-PackageZip", StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrWhiteSpace(f.FilePath)
                            && File.Exists(f.FilePath))
                .OrderByDescending(f => f.DownloadedAt)
                .FirstOrDefault();

            if (cachedGdtZip != null)
            {
                try
                {
                    var zipBytes = await File.ReadAllBytesAsync(cachedGdtZip.FilePath, ct);
                    var renderedPdf = await GdtPortalProvider.TryRenderOfficialGdtZipPackageToPdfAsync(zipBytes, ct);
                    if (renderedPdf != null && GdtPortalProvider.IsValidPdfSignature(renderedPdf))
                    {
                        var savedFromZip = await SaveSingleFileAsync(
                            invoiceId,
                            taxAccountId,
                            $"GDT_{invoice.InvoiceSymbol}_{invoice.InvoiceNumber}.pdf",
                            ".pdf",
                            "application/pdf",
                            renderedPdf,
                            "GDT-OfficialPackagePdf",
                            ct);

                        return new OriginalPdfFetchResult
                        {
                            Success = true,
                            PdfBytes = renderedPdf,
                            FileName = savedFromZip.OriginalFileName,
                            ContentType = "application/pdf",
                            Sha256Hash = savedFromZip.Sha256Hash,
                            Source = savedFromZip.Source,
                            SavedFilePath = savedFromZip.FilePath,
                            HttpStatusCode = 200
                        };
                    }
                }
                catch (Exception exZipPdf)
                {
                    _logger.LogWarning(exZipPdf, "Lỗi khi kết xuất PDF từ gói ZIP GDT đã lưu cho hóa đơn {Id}", invoiceId);
                }
            }

            // 2. Chưa có file PDF gốc hợp lệ -> Lấy token của tài khoản thuế hiện tại và gọi trực tiếp API Tổng cục Thuế
            var activeToken = await ResolveActiveGdtTokenAsync(taxAccountId, invoice.TaxAccount?.TaxCode, token, ct);
            if (string.IsNullOrWhiteSpace(activeToken))
            {
                return new OriginalPdfFetchResult
                {
                    Success = false,
                    ErrorCode = "NO_GDT_TOKEN",
                    ErrorMessage = "Chưa đăng nhập tài khoản thuế hoặc phiên kết nối Cổng Thuế đã hết hạn. Vui lòng đăng nhập Cổng Thuế tại mục Đồng bộ Hóa đơn.",
                    HttpStatusCode = 401
                };
            }

            var remoteInv = new RemoteInvoice
            {
                InvoiceSymbol = invoice.InvoiceSymbol,
                InvoiceNumber = invoice.InvoiceNumber,
                RawInvoiceNumber = long.TryParse(invoice.InvoiceNumber, out var num) ? num.ToString() : invoice.InvoiceNumber,
                SellerTaxCode = invoice.SellerTaxCode,
                BuyerTaxCode = invoice.BuyerTaxCode ?? invoice.TaxAccount?.TaxCode ?? string.Empty,
                InvoiceType = invoice.InvoiceType,
                IssueDate = invoice.IssueDate,
                TemplateCode = "1"
            };

            var fetchResult = await _provider.FetchOriginalPdfAsync(remoteInv, activeToken, ct);

            // Nếu gói dữ liệu trả kèm từ GDT có các tệp gốc hợp lệ (.xml, .zip), lưu lại để phục vụ tra cứu/đối chiếu
            if (fetchResult.CompanionPackage != null && fetchResult.CompanionPackage.Success && fetchResult.CompanionPackage.Files.Any())
            {
                try
                {
                    await SaveDownloadedFilesAsync(invoiceId, taxAccountId, fetchResult.CompanionPackage, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Lỗi khi lưu gói tệp đính kèm (.xml/.zip) từ GDT cho hóa đơn {Id}", invoiceId);
                }
            }

            if (!fetchResult.Success || fetchResult.PdfBytes == null || fetchResult.PdfBytes.Length == 0)
            {
                return fetchResult;
            }

            // 3. Kiểm tra nghiêm ngặt chữ ký %PDF- trước khi lưu và trả về
            if (!GdtPortalProvider.IsValidPdfSignature(fetchResult.PdfBytes))
            {
                return new OriginalPdfFetchResult
                {
                    Success = false,
                    ErrorCode = "INVALID_PDF_SIGNATURE",
                    ErrorMessage = "Dữ liệu phản hồi từ Tổng cục Thuế không phải tệp PDF hợp lệ (thiếu chữ ký %PDF-).",
                    EndpointCalled = fetchResult.EndpointCalled,
                    HttpStatusCode = 422
                };
            }

            var pdfFileName = !string.IsNullOrWhiteSpace(fetchResult.FileName)
                ? fetchResult.FileName
                : $"HD_{invoice.InvoiceSymbol}_{invoice.InvoiceNumber}.pdf";

            var savedEntity = await SaveSingleFileAsync(
                invoiceId,
                taxAccountId,
                pdfFileName,
                ".pdf",
                "application/pdf",
                fetchResult.PdfBytes,
                !string.IsNullOrWhiteSpace(fetchResult.Source) ? fetchResult.Source : "GDT-Direct",
                ct);

            fetchResult.SavedFilePath = savedEntity.FilePath;
            fetchResult.Sha256Hash = savedEntity.Sha256Hash;
            fetchResult.FileName = savedEntity.OriginalFileName;

            await _auditLog.LogActionAsync(
                "Tải PDF gốc từ Tổng cục Thuế",
                $"{invoice.InvoiceSymbol}-{invoice.InvoiceNumber} ({savedEntity.FileSizeBytes} bytes, SHA256: {savedEntity.Sha256Hash[..12]}...)",
                null,
                taxAccountId);

            return fetchResult;
        }

        private async Task<string?> ResolveActiveGdtTokenAsync(
            int taxAccountId,
            string? taxCode,
            string? explicitToken,
            CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(explicitToken))
            {
                return explicitToken.Trim();
            }

            // Ưu tiên 1: Lấy token đang hoạt động trong bộ nhớ / file cache của GdtPortalProvider
            var cachedToken = GdtPortalProvider.GetCachedToken(taxCode, taxAccountId);
            if (!string.IsNullOrWhiteSpace(cachedToken))
            {
                return cachedToken;
            }

            // Ưu tiên 2: Tìm thông tin kết nối đã lưu của tài khoản thuế hiện tại (hoặc cùng MST)
            var connection = await _db.RemoteConnections
                .Where(r => r.ProviderName == "Gdt" &&
                            (r.TaxAccountId == taxAccountId || (!string.IsNullOrEmpty(taxCode) && r.Username == taxCode)))
                .OrderByDescending(r => r.TaxAccountId == taxAccountId)
                .ThenByDescending(r => !string.IsNullOrEmpty(r.EncryptedPassword))
                .FirstOrDefaultAsync(ct);

            var lookupTaxCode = connection?.Username ?? taxCode;
            if (!string.IsNullOrWhiteSpace(lookupTaxCode))
            {
                var tokenByLookup = GdtPortalProvider.GetCachedToken(lookupTaxCode, taxAccountId);
                if (!string.IsNullOrWhiteSpace(tokenByLookup))
                {
                    return tokenByLookup;
                }
            }

            if (connection != null && !string.IsNullOrEmpty(connection.EncryptedPassword))
            {
                var plainPass = _protector.Unprotect(connection.EncryptedPassword);
                if (!string.IsNullOrEmpty(plainPass))
                {
                    var loginRes = await _provider.LoginAsync(new ProviderCredentials
                    {
                        TaxCode = connection.Username,
                        Username = connection.Username,
                        Password = plainPass
                    }, ct);

                    if (loginRes.Success && !string.IsNullOrEmpty(loginRes.Token))
                    {
                        GdtPortalProvider.CacheToken(connection.Username, loginRes.Token, taxAccountId);
                        return loginRes.Token;
                    }
                }
            }

            return null;
        }

        private static string ComputeSha256(byte[] data)
        {
            if (data == null || data.Length == 0) return string.Empty;
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(data);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}
