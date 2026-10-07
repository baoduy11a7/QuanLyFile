using InvoiceManager.Data;
using InvoiceManager.Models.Entities;
using InvoiceManager.Services.Parsers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using ClosedXML.Excel;
using System.Globalization;
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

        public async Task<ImportBatchResult> ImportExcelAsync(Stream excelStream, string fileName, int taxAccountId, string invoiceType, string? userId)
        {
            var batchResult = new ImportBatchResult();

            try
            {
                using var ms = new MemoryStream();
                await excelStream.CopyToAsync(ms);
                ms.Position = 0;

                // 1. Lưu file Excel gốc phục vụ lưu trữ và đối soát
                var savedFilePath = await SaveRawFileAsync(ms, taxAccountId, fileName);
                ms.Position = 0;

                using var workbook = new XLWorkbook(ms);
                var worksheet = workbook.Worksheets.FirstOrDefault();
                if (worksheet == null)
                {
                    batchResult.FailedCount++;
                    batchResult.Messages.Add($"File '{fileName}' không chứa bất kỳ bảng tính (worksheet) nào.");
                    return batchResult;
                }

                // 2. Tìm dòng tiêu đề (Header row)
                int headerRowIndex = -1;
                var colMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                int lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 0;
                int searchLimit = Math.Min(30, lastRow);

                for (int r = 1; r <= searchLimit; r++)
                {
                    var tempMap = MatchHeaderColumns(worksheet.Row(r));
                    // Cần nhận diện được ít nhất 2 cột trọng yếu
                    int score = 0;
                    if (tempMap.ContainsKey("Symbol") || tempMap.ContainsKey("Number")) score++;
                    if (tempMap.ContainsKey("SellerTaxCode") || tempMap.ContainsKey("SellerName")) score++;
                    if (tempMap.ContainsKey("TotalAmount") || tempMap.ContainsKey("AmountBeforeTax")) score++;
                    if (tempMap.ContainsKey("Date")) score++;

                    if (score >= 2)
                    {
                        headerRowIndex = r;
                        colMap = tempMap;
                        break;
                    }
                }

                if (headerRowIndex == -1)
                {
                    batchResult.FailedCount++;
                    batchResult.Messages.Add($"File '{fileName}' không nhận diện được các cột tiêu đề hợp lệ (Cần có: Ký hiệu/Số HĐ, MST Người bán, Ngày lập hoặc Số tiền). Bạn có thể tải file mẫu để kiểm tra cấu trúc.");
                    return batchResult;
                }

                // 3. Đọc các dòng dữ liệu và nhóm theo hóa đơn
                var parsedInvoices = new List<ExcelInvoiceIntermediate>();
                ExcelInvoiceIntermediate? currentInvoice = null;
                int consecutiveEmptyRows = 0;

                for (int r = headerRowIndex + 1; r <= lastRow; r++)
                {
                    var row = worksheet.Row(r);
                    if (row.IsEmpty())
                    {
                        consecutiveEmptyRows++;
                        if (consecutiveEmptyRows >= 5) break;
                        continue;
                    }
                    consecutiveEmptyRows = 0;

                    // Kiểm tra nếu là dòng tổng cộng / footer
                    string firstCellText = GetCellValueAsString(row.Cell(1)).ToLowerInvariant();
                    string secondCellText = GetCellValueAsString(row.Cell(2)).ToLowerInvariant();
                    if (firstCellText.Contains("tổng cộng") || firstCellText.Contains("cộng tiền") || 
                        secondCellText.Contains("tổng cộng") || secondCellText.Contains("cộng tiền"))
                    {
                        continue;
                    }

                    string symbol = GetColumnValue(row, colMap, "Symbol");
                    string number = GetColumnValue(row, colMap, "Number");
                    string sellerTaxCode = GetColumnValue(row, colMap, "SellerTaxCode");

                    // Làm sạch số hóa đơn
                    if (!string.IsNullOrWhiteSpace(number))
                    {
                        number = number.Trim();
                        if (long.TryParse(number, out long numVal) && number.Length < 8)
                        {
                            number = numVal.ToString("D8");
                        }
                    }

                    // Kiểm tra dòng bắt đầu hóa đơn mới hay là dòng chi tiết tiếp theo
                    bool isNewInvoice = !string.IsNullOrWhiteSpace(number) &&
                        (currentInvoice == null || 
                         currentInvoice.InvoiceNumber != number || 
                         (!string.IsNullOrWhiteSpace(symbol) && currentInvoice.InvoiceSymbol != symbol) ||
                         (!string.IsNullOrWhiteSpace(sellerTaxCode) && currentInvoice.SellerTaxCode != sellerTaxCode));

                    if (isNewInvoice)
                    {
                        currentInvoice = new ExcelInvoiceIntermediate
                        {
                            InvoiceSymbol = string.IsNullOrWhiteSpace(symbol) ? "1C25TXX" : symbol.Trim().ToUpperInvariant(),
                            InvoiceNumber = number,
                            IssueDate = ParseDateTime(GetColumnCell(row, colMap, "Date")) ?? DateTime.Today,
                            SellerTaxCode = sellerTaxCode.Replace(" ", "").Trim(),
                            SellerName = GetColumnValue(row, colMap, "SellerName"),
                            SellerAddress = GetColumnValue(row, colMap, "SellerAddress"),
                            BuyerTaxCode = GetColumnValue(row, colMap, "BuyerTaxCode"),
                            BuyerName = GetColumnValue(row, colMap, "BuyerName"),
                            BuyerAddress = GetColumnValue(row, colMap, "BuyerAddress"),
                            TaxAuthorityCode = GetColumnValue(row, colMap, "TaxAuthorityCode"),
                            Status = GetColumnValue(row, colMap, "Status"),
                            HeaderAmountBeforeTax = ParseDecimal(GetColumnCell(row, colMap, "AmountBeforeTax")),
                            HeaderTaxAmount = ParseDecimal(GetColumnCell(row, colMap, "TaxAmount")),
                            HeaderTotalAmount = ParseDecimal(GetColumnCell(row, colMap, "TotalAmount"))
                        };
                        parsedInvoices.Add(currentInvoice);
                    }

                    if (currentInvoice != null)
                    {
                        // Đọc dòng hàng chi tiết
                        string itemName = GetColumnValue(row, colMap, "ItemName");
                        decimal detailAmountBeforeTax = ParseDecimal(GetColumnCell(row, colMap, "AmountBeforeTax"));
                        decimal detailTaxAmount = ParseDecimal(GetColumnCell(row, colMap, "TaxAmount"));
                        decimal detailTotalAmount = ParseDecimal(GetColumnCell(row, colMap, "TotalAmount"));
                        decimal detailTaxRate = ParseDecimal(GetColumnCell(row, colMap, "TaxRate"));
                        decimal detailQuantity = ParseDecimal(GetColumnCell(row, colMap, "Quantity"));
                        decimal detailUnitPrice = ParseDecimal(GetColumnCell(row, colMap, "UnitPrice"));
                        string unit = GetColumnValue(row, colMap, "Unit");

                        if (detailQuantity == 0 && detailUnitPrice == 0 && detailAmountBeforeTax > 0)
                        {
                            detailQuantity = 1;
                            detailUnitPrice = detailAmountBeforeTax;
                        }

                        if (detailTotalAmount == 0 && detailAmountBeforeTax > 0)
                        {
                            detailTotalAmount = detailAmountBeforeTax + detailTaxAmount;
                        }

                        if (string.IsNullOrWhiteSpace(itemName))
                        {
                            itemName = "Chi tiết hóa đơn";
                        }

                        currentInvoice.Details.Add(new InvoiceDetail
                        {
                            LineNumber = currentInvoice.Details.Count + 1,
                            ItemName = itemName,
                            Unit = unit,
                            Quantity = detailQuantity > 0 ? detailQuantity : 1,
                            UnitPrice = detailUnitPrice > 0 ? detailUnitPrice : detailAmountBeforeTax,
                            AmountBeforeTax = detailAmountBeforeTax,
                            TaxRate = detailTaxRate,
                            TaxAmount = detailTaxAmount,
                            TotalAmount = detailTotalAmount
                        });
                    }
                }

                batchResult.TotalFiles = parsedInvoices.Count;
                if (parsedInvoices.Count == 0)
                {
                    batchResult.FailedCount++;
                    batchResult.Messages.Add($"File '{fileName}' không tìm thấy bất kỳ dòng hóa đơn nào có dữ liệu hợp lệ.");
                    return batchResult;
                }

                // 4. Lưu từng hóa đơn vào CSDL và kiểm tra trùng lặp (Idempotent)
                foreach (var item in parsedInvoices)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(item.InvoiceNumber))
                        {
                            batchResult.FailedCount++;
                            batchResult.Messages.Add($"Bỏ qua dòng dữ liệu do thiếu Số hóa đơn.");
                            continue;
                        }

                        if (string.IsNullOrWhiteSpace(item.SellerTaxCode))
                        {
                            item.SellerTaxCode = "KHONG_XAC_DINH";
                        }

                        if (string.IsNullOrWhiteSpace(item.SellerName))
                        {
                            item.SellerName = $"Đơn vị bán MST {item.SellerTaxCode}";
                        }

                        // Tính toán tổng tiền nếu cần
                        decimal totalBeforeTax = item.HeaderAmountBeforeTax;
                        decimal totalTax = item.HeaderTaxAmount;
                        decimal grandTotal = item.HeaderTotalAmount;

                        if (grandTotal == 0 && item.Details.Any())
                        {
                            totalBeforeTax = item.Details.Sum(d => d.AmountBeforeTax);
                            totalTax = item.Details.Sum(d => d.TaxAmount);
                            grandTotal = item.Details.Sum(d => d.TotalAmount);
                        }

                        if (grandTotal == 0 && totalBeforeTax > 0)
                        {
                            grandTotal = totalBeforeTax + totalTax;
                        }

                        // Kiểm tra trùng lặp
                        var existing = await _db.Invoices
                            .FirstOrDefaultAsync(i => i.TaxAccountId == taxAccountId
                                                   && i.InvoiceType == invoiceType
                                                   && i.SellerTaxCode == item.SellerTaxCode
                                                   && i.InvoiceSymbol == item.InvoiceSymbol
                                                   && i.InvoiceNumber == item.InvoiceNumber);

                        if (existing != null)
                        {
                            batchResult.SkippedDuplicateCount++;
                            batchResult.Messages.Add($"Hóa đơn {item.InvoiceSymbol}-{item.InvoiceNumber} của MST {item.SellerTaxCode} đã tồn tại trên hệ thống. Đã bỏ qua.");
                            continue;
                        }

                        var invoice = new Invoice
                        {
                            TaxAccountId = taxAccountId,
                            InvoiceSymbol = item.InvoiceSymbol,
                            InvoiceNumber = item.InvoiceNumber,
                            IssueDate = item.IssueDate,
                            SellerTaxCode = item.SellerTaxCode,
                            SellerName = item.SellerName,
                            SellerAddress = item.SellerAddress,
                            BuyerTaxCode = item.BuyerTaxCode,
                            BuyerName = item.BuyerName,
                            BuyerAddress = item.BuyerAddress,
                            AmountBeforeTax = totalBeforeTax,
                            TaxAmount = totalTax,
                            TotalAmount = grandTotal,
                            InvoiceType = invoiceType,
                            HasTaxCode = !string.IsNullOrWhiteSpace(item.TaxAuthorityCode),
                            TaxAuthorityCode = item.TaxAuthorityCode,
                            Status = string.IsNullOrWhiteSpace(item.Status) ? "Hóa đơn mới" : item.Status,
                            SourceProvider = "Excel",
                            RawXmlPath = savedFilePath,
                            OriginalFileName = fileName,
                            ImportedAt = DateTime.Now,
                            ImportedByUserId = userId,
                            RiskLevel = "Normal"
                        };

                        if (!item.Details.Any())
                        {
                            invoice.Details.Add(new InvoiceDetail
                            {
                                LineNumber = 1,
                                ItemName = "Tổng hợp theo hóa đơn",
                                Quantity = 1,
                                UnitPrice = totalBeforeTax,
                                AmountBeforeTax = totalBeforeTax,
                                TaxRate = totalBeforeTax > 0 ? Math.Round((totalTax / totalBeforeTax) * 100, 0) : 0,
                                TaxAmount = totalTax,
                                TotalAmount = grandTotal
                            });
                        }
                        else
                        {
                            foreach (var d in item.Details)
                            {
                                invoice.Details.Add(d);
                            }
                        }

                        _db.Invoices.Add(invoice);
                        await _db.SaveChangesAsync();

                        batchResult.SuccessCount++;
                        batchResult.ImportedInvoices.Add(invoice);
                        batchResult.Messages.Add($"Thành công: Import hóa đơn {invoice.InvoiceSymbol}-{invoice.InvoiceNumber} ({invoice.SellerName}) - {invoice.TotalAmount:N0} đ.");
                    }
                    catch (Exception rowEx)
                    {
                        _logger.LogError(rowEx, "Lỗi import dòng hóa đơn {Symbol}-{Number}", item.InvoiceSymbol, item.InvoiceNumber);
                        batchResult.FailedCount++;
                        batchResult.Messages.Add($"Lỗi HĐ {item.InvoiceSymbol}-{item.InvoiceNumber}: {rowEx.Message}");
                    }
                }

                await _auditLog.LogActionAsync(
                    "Import Excel", 
                    fileName, 
                    $"Nhập thành công {batchResult.SuccessCount} HĐ, Bỏ qua {batchResult.SkippedDuplicateCount} HĐ trùng, Lỗi {batchResult.FailedCount} HĐ", 
                    taxAccountId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi phân tích và import file Excel {FileName}", fileName);
                batchResult.FailedCount++;
                batchResult.Messages.Add($"Lỗi khi xử lý file Excel '{fileName}': {ex.Message}");
            }

            return batchResult;
        }

        public Task<byte[]> GenerateExcelTemplateAsync(string invoiceType)
        {
            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add("Bảng kê hóa đơn");
            ws.ShowGridLines = true;

            string typeTitle = invoiceType == "BanRa" ? "BÁN RA (DOANH THU)" : "MUA VÀO (CHI PHÍ)";

            // 1. Title & Note
            ws.Cell("A1").Value = $"MẪU NHẬP DỮ LIỆU HÓA ĐƠN ĐIỆN TỬ {typeTitle}";
            ws.Cell("A1").Style.Font.Bold = true;
            ws.Cell("A1").Style.Font.FontSize = 14;
            ws.Cell("A1").Style.Font.FontColor = XLColor.FromHtml("#1E3A8A");

            ws.Cell("A2").Value = "Lưu ý: Giữ nguyên dòng tiêu đề tại dòng 4. Các dòng có cùng (Ký hiệu HĐ + Số hóa đơn + MST Người bán) sẽ tự động được gộp thành 1 hóa đơn nhiều dòng hàng.";
            ws.Cell("A2").Style.Font.Italic = true;
            ws.Cell("A2").Style.Font.FontColor = XLColor.FromHtml("#B45309");

            // 2. Table Headers
            int startRow = 4;
            string[] headers = new[]
            {
                "STT", "Ký hiệu HĐ", "Số hóa đơn", "Ngày lập (dd/MM/yyyy)", 
                "MST Người bán", "Tên người bán", "Địa chỉ người bán", 
                "MST Người mua", "Tên người mua", 
                "Tên hàng hóa / Dịch vụ", "ĐVT", "Số lượng", "Đơn giá (VND)", 
                "Tiền chưa thuế (VND)", "Thuế suất (%)", "Tiền thuế GTGT (VND)", "Tổng cộng (VND)", 
                "Mã CQT", "Trạng thái HĐ"
            };

            for (int col = 0; col < headers.Length; col++)
            {
                var cell = ws.Cell(startRow, col + 1);
                cell.Value = headers[col];
                cell.Style.Font.Bold = true;
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E40AF");
                cell.Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                cell.Style.Alignment.SetVertical(XLAlignmentVerticalValues.Center);
                cell.Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin);
                cell.Style.Border.OutsideBorderColor = XLColor.FromHtml("#93C5FD");
            }
            ws.Row(startRow).Height = 28;

            // 3. Sample Rows
            var sampleData = new[]
            {
                new {
                    STT = 1, Symbol = "1C24TXX", Number = "00000001", Date = "15/10/2024",
                    SellerTax = "0101234567", SellerName = "Công ty CP Công Nghệ Thông Tin Ánh Dương", SellerAddr = "Số 12 phố Trần Phú, Ba Đình, Hà Nội",
                    BuyerTax = "0317748323", BuyerName = "Công ty TNHH Dịch Vụ Tài Chính Alpha",
                    Item = "Bản quyền phần mềm kế toán doanh nghiệp Cloud", Unit = "Gói", Qty = 1m, Price = 10000000m,
                    Amount = 10000000m, Rate = 10m, Tax = 1000000m, Total = 11000000m,
                    Cqt = "", Status = "Hóa đơn mới"
                },
                new {
                    STT = 2, Symbol = "1C24TXX", Number = "00000002", Date = "16/10/2024",
                    SellerTax = "0309876543", SellerName = "Công ty TNHH Văn Phòng Phẩm Toàn Cầu", SellerAddr = "45 Nguyễn Huệ, Phường Bến Nghé, Quận 1, TP.HCM",
                    BuyerTax = "0317748323", BuyerName = "Công ty TNHH Dịch Vụ Tài Chính Alpha",
                    Item = "Giấy in Double A A4 70gsm", Unit = "Ram", Qty = 20m, Price = 65000m,
                    Amount = 1300000m, Rate = 8m, Tax = 104000m, Total = 1404000m,
                    Cqt = "TCT0987654321", Status = "Hóa đơn mới"
                },
                new {
                    STT = 2, Symbol = "1C24TXX", Number = "00000002", Date = "16/10/2024",
                    SellerTax = "0309876543", SellerName = "Công ty TNHH Văn Phòng Phẩm Toàn Cầu", SellerAddr = "45 Nguyễn Huệ, Phường Bến Nghé, Quận 1, TP.HCM",
                    BuyerTax = "0317748323", BuyerName = "Công ty TNHH Dịch Vụ Tài Chính Alpha",
                    Item = "Bút bi gel nước Thiên Long 0.5mm", Unit = "Hộp", Qty = 5m, Price = 50000m,
                    Amount = 250000m, Rate = 8m, Tax = 20000m, Total = 270000m,
                    Cqt = "TCT0987654321", Status = "Hóa đơn mới"
                }
            };

            int curr = startRow + 1;
            foreach (var s in sampleData)
            {
                ws.Cell(curr, 1).Value = s.STT;
                ws.Cell(curr, 2).Value = s.Symbol;
                ws.Cell(curr, 3).Value = s.Number;
                ws.Cell(curr, 4).Value = s.Date;
                ws.Cell(curr, 5).Value = s.SellerTax;
                ws.Cell(curr, 6).Value = s.SellerName;
                ws.Cell(curr, 7).Value = s.SellerAddr;
                ws.Cell(curr, 8).Value = s.BuyerTax;
                ws.Cell(curr, 9).Value = s.BuyerName;
                ws.Cell(curr, 10).Value = s.Item;
                ws.Cell(curr, 11).Value = s.Unit;
                ws.Cell(curr, 12).Value = s.Qty;
                ws.Cell(curr, 13).Value = s.Price;
                ws.Cell(curr, 14).Value = s.Amount;
                ws.Cell(curr, 15).Value = s.Rate;
                ws.Cell(curr, 16).Value = s.Tax;
                ws.Cell(curr, 17).Value = s.Total;
                ws.Cell(curr, 18).Value = s.Cqt;
                ws.Cell(curr, 19).Value = s.Status;

                ws.Cell(curr, 1).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                ws.Cell(curr, 2).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                ws.Cell(curr, 3).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                ws.Cell(curr, 4).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                ws.Cell(curr, 5).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                ws.Cell(curr, 8).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                ws.Cell(curr, 11).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

                ws.Cell(curr, 12).Style.NumberFormat.Format = "#,##0.##";
                ws.Cell(curr, 13).Style.NumberFormat.Format = "#,##0";
                ws.Cell(curr, 14).Style.NumberFormat.Format = "#,##0";
                ws.Cell(curr, 15).Style.NumberFormat.Format = "0.##";
                ws.Cell(curr, 16).Style.NumberFormat.Format = "#,##0";
                ws.Cell(curr, 17).Style.NumberFormat.Format = "#,##0";

                for (int c = 1; c <= headers.Length; c++)
                {
                    ws.Cell(curr, c).Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin);
                    ws.Cell(curr, c).Style.Border.OutsideBorderColor = XLColor.FromHtml("#E5E7EB");
                }
                curr++;
            }

            ws.Columns().AdjustToContents();
            ws.Column(1).Width = 8;
            ws.Column(6).Width = Math.Max(35, ws.Column(6).Width);
            ws.Column(10).Width = Math.Max(35, ws.Column(10).Width);

            // Sheet 2: Hướng dẫn
            var guideWs = workbook.Worksheets.Add("Hướng dẫn");
            guideWs.Cell("A1").Value = "HƯỚNG DẪN ĐIỀN DỮ LIỆU FILE MẪU EXCEL";
            guideWs.Cell("A1").Style.Font.Bold = true;
            guideWs.Cell("A1").Style.Font.FontSize = 13;

            guideWs.Cell("A3").Value = "1. Ký hiệu HĐ: Định dạng chuẩn như 1C24TXX, 2C24TKT, C24TAA...";
            guideWs.Cell("A4").Value = "2. Số hóa đơn: Nhập dãy số (ví dụ: 1, 123 hoặc 00000123). Hệ thống sẽ tự chuẩn hóa.";
            guideWs.Cell("A5").Value = "3. Ngày lập: Định dạng dd/MM/yyyy (ví dụ: 25/10/2024).";
            guideWs.Cell("A6").Value = "4. MST Người bán & Tên người bán: Thông tin bắt buộc để đối soát.";
            guideWs.Cell("A7").Value = "5. Thuế suất: Điền số nguyên hoặc thập phân (ví dụ: 0, 5, 8, 10).";
            guideWs.Cell("A8").Value = "6. Hóa đơn nhiều dòng hàng: Giữ nguyên Ký hiệu HĐ, Số hóa đơn và MST Người bán cho các dòng hàng thuộc cùng 1 hóa đơn.";
            guideWs.Cell("A9").Value = "7. Hỗ trợ import bảng kê Cổng thuế / MISA: Bạn cũng có thể tải trực tiếp file Excel bảng kê từ hoadondientu.gdt.gov.vn hoặc phần mềm kế toán để nhập.";

            guideWs.Columns().AdjustToContents();

            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            return Task.FromResult(stream.ToArray());
        }

        #region Excel Parsing Helpers
        private class ExcelInvoiceIntermediate
        {
            public string InvoiceSymbol { get; set; } = string.Empty;
            public string InvoiceNumber { get; set; } = string.Empty;
            public DateTime IssueDate { get; set; } = DateTime.Today;
            public string SellerTaxCode { get; set; } = string.Empty;
            public string SellerName { get; set; } = string.Empty;
            public string? SellerAddress { get; set; }
            public string? BuyerTaxCode { get; set; }
            public string? BuyerName { get; set; }
            public string? BuyerAddress { get; set; }
            public string? TaxAuthorityCode { get; set; }
            public string? Status { get; set; }
            public decimal HeaderAmountBeforeTax { get; set; }
            public decimal HeaderTaxAmount { get; set; }
            public decimal HeaderTotalAmount { get; set; }
            public List<InvoiceDetail> Details { get; set; } = new();
        }

        private static Dictionary<string, int> MatchHeaderColumns(IXLRow row)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int lastCol = row.LastCellUsed()?.Address.ColumnNumber ?? 0;

            for (int col = 1; col <= lastCol; col++)
            {
                var text = row.Cell(col).GetString()?.Trim().ToLowerInvariant() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text)) continue;

                // Chuẩn hóa loại bỏ một số ký tự
                text = text.Replace("\n", " ").Replace("\r", " ").Trim();

                if (!map.ContainsKey("Symbol") && (text.Contains("ký hiệu") || text.Contains("ky hieu") || text.Contains("mẫu số") || text.Contains("mau so") || text == "symbol" || text == "khhd"))
                {
                    map["Symbol"] = col;
                }
                else if (!map.ContainsKey("Number") && (text.Contains("số hóa đơn") || text.Contains("so hoa don") || text.Contains("số hđ") || text.Contains("so hd") || text == "shd" || text == "invoicenumber" || text == "số hoá đơn"))
                {
                    map["Number"] = col;
                }
                else if (!map.ContainsKey("Date") && (text.Contains("ngày lập") || text.Contains("ngay lap") || text.Contains("ngày hóa đơn") || text.Contains("ngay hoa don") || text.Contains("ngày hđ") || text.Contains("ngay hd") || text == "date" || text == "ngày phát hành"))
                {
                    map["Date"] = col;
                }
                else if (!map.ContainsKey("SellerTaxCode") && (text.Contains("mst người bán") || text.Contains("mst nguoi ban") || text.Contains("mã số thuế người bán") || text.Contains("mst bên bán") || text.Contains("mst bán") || text.Contains("mst ban") || text == "sellertaxcode"))
                {
                    map["SellerTaxCode"] = col;
                }
                else if (!map.ContainsKey("SellerName") && (text.Contains("tên người bán") || text.Contains("ten nguoi ban") || text.Contains("đơn vị bán") || text.Contains("don vi ban") || text.Contains("tên đơn vị bán") || text.Contains("người bán") || text == "sellername"))
                {
                    map["SellerName"] = col;
                }
                else if (!map.ContainsKey("SellerAddress") && (text.Contains("địa chỉ người bán") || text.Contains("dia chi nguoi ban") || text.Contains("địa chỉ bán") || text == "selleraddress"))
                {
                    map["SellerAddress"] = col;
                }
                else if (!map.ContainsKey("BuyerTaxCode") && (text.Contains("mst người mua") || text.Contains("mst nguoi mua") || text.Contains("mã số thuế người mua") || text.Contains("mst bên mua") || text.Contains("mst mua") || text == "buyertaxcode"))
                {
                    map["BuyerTaxCode"] = col;
                }
                else if (!map.ContainsKey("BuyerName") && (text.Contains("tên người mua") || text.Contains("ten nguoi mua") || text.Contains("đơn vị mua") || text.Contains("tên đơn vị mua") || text.Contains("người mua") || text == "buyername"))
                {
                    map["BuyerName"] = col;
                }
                else if (!map.ContainsKey("BuyerAddress") && (text.Contains("địa chỉ người mua") || text.Contains("dia chi nguoi mua") || text.Contains("địa chỉ mua") || text == "buyeraddress"))
                {
                    map["BuyerAddress"] = col;
                }
                else if (!map.ContainsKey("ItemName") && (text.Contains("tên hàng hóa") || text.Contains("ten hang hoa") || text.Contains("hàng hóa") || text.Contains("mặt hàng") || text.Contains("nội dung") || text.Contains("diễn giải") || text == "itemname"))
                {
                    map["ItemName"] = col;
                }
                else if (!map.ContainsKey("Unit") && (text.Contains("đơn vị tính") || text.Contains("don vi tinh") || text == "đvt" || text == "dvt" || text == "unit"))
                {
                    map["Unit"] = col;
                }
                else if (!map.ContainsKey("Quantity") && (text.Contains("số lượng") || text.Contains("so luong") || text == "sl" || text == "quantity"))
                {
                    map["Quantity"] = col;
                }
                else if (!map.ContainsKey("UnitPrice") && (text.Contains("đơn giá") || text.Contains("don gia") || text == "unitprice"))
                {
                    map["UnitPrice"] = col;
                }
                else if (!map.ContainsKey("AmountBeforeTax") && (text.Contains("tiền chưa thuế") || text.Contains("tien chua thue") || text.Contains("tổng tiền chưa thuế") || text.Contains("thành tiền chưa thuế") || text.Contains("chưa thuế") || text == "amountbeforetax"))
                {
                    map["AmountBeforeTax"] = col;
                }
                else if (!map.ContainsKey("TaxRate") && (text.Contains("thuế suất") || text.Contains("thue suat") || text == "taxrate"))
                {
                    map["TaxRate"] = col;
                }
                else if (!map.ContainsKey("TaxAmount") && (text.Contains("tiền thuế") || text.Contains("tien thue") || text.Contains("tiền thuế gtgt") || text.Contains("thuế gtgt") || text == "taxamount"))
                {
                    map["TaxAmount"] = col;
                }
                else if (!map.ContainsKey("TotalAmount") && (text.Contains("tổng cộng") || text.Contains("tong cong") || text.Contains("tổng tiền") || text.Contains("tong tien") || text.Contains("tổng thanh toán") || text.Contains("tong thanh toan") || text.Contains("tiền thanh toán") || text == "totalamount"))
                {
                    map["TotalAmount"] = col;
                }
                else if (!map.ContainsKey("TaxAuthorityCode") && (text.Contains("mã cqt") || text.Contains("ma cqt") || text.Contains("mã cơ quan thuế") || text == "taxauthoritycode"))
                {
                    map["TaxAuthorityCode"] = col;
                }
                else if (!map.ContainsKey("Status") && (text.Contains("trạng thái") || text.Contains("trang thai") || text == "status"))
                {
                    map["Status"] = col;
                }
            }

            return map;
        }

        private static IXLCell? GetColumnCell(IXLRow row, Dictionary<string, int> colMap, string key)
        {
            if (colMap.TryGetValue(key, out int colIdx))
            {
                return row.Cell(colIdx);
            }
            return null;
        }

        private static string GetColumnValue(IXLRow row, Dictionary<string, int> colMap, string key)
        {
            var cell = GetColumnCell(row, colMap, key);
            return GetCellValueAsString(cell);
        }

        private static string GetCellValueAsString(IXLCell? cell)
        {
            if (cell == null || cell.IsEmpty()) return string.Empty;
            return cell.GetString()?.Trim() ?? cell.Value.ToString()?.Trim() ?? string.Empty;
        }

        private static decimal ParseDecimal(IXLCell? cell)
        {
            if (cell == null || cell.IsEmpty()) return 0m;
            try
            {
                if (cell.DataType == XLDataType.Number)
                {
                    return (decimal)cell.GetDouble();
                }
            }
            catch { }

            var text = cell.GetString()?.Trim() ?? cell.Value.ToString()?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text)) return 0m;

            text = text.Replace("VND", "", StringComparison.OrdinalIgnoreCase)
                       .Replace("VNĐ", "", StringComparison.OrdinalIgnoreCase)
                       .Replace("đ", "", StringComparison.OrdinalIgnoreCase)
                       .Replace(" ", "").Trim();

            if (text.Contains('.') && text.Contains(','))
            {
                if (text.LastIndexOf(',') > text.LastIndexOf('.'))
                {
                    text = text.Replace(".", "").Replace(',', '.');
                }
                else
                {
                    text = text.Replace(",", "");
                }
            }
            else if (text.Contains(','))
            {
                int commaIndex = text.LastIndexOf(',');
                if (text.Length - commaIndex - 1 == 3 && !text.Substring(0, commaIndex).Contains(','))
                {
                    text = text.Replace(",", "");
                }
                else
                {
                    text = text.Replace(',', '.');
                }
            }
            else if (text.Contains('.'))
            {
                int dotIndex = text.LastIndexOf('.');
                if (text.Length - dotIndex - 1 == 3 && !text.Substring(0, dotIndex).Contains('.'))
                {
                    text = text.Replace(".", "");
                }
            }

            if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var result))
            {
                return result;
            }
            return 0m;
        }

        private static DateTime? ParseDateTime(IXLCell? cell)
        {
            if (cell == null || cell.IsEmpty()) return null;
            try
            {
                if (cell.DataType == XLDataType.DateTime)
                {
                    return cell.GetDateTime();
                }
                if (cell.DataType == XLDataType.Number)
                {
                    double num = cell.GetDouble();
                    if (num > 30000 && num < 70000)
                    {
                        return DateTime.FromOADate(num);
                    }
                }
            }
            catch { }

            var str = cell.GetString()?.Trim() ?? cell.Value.ToString()?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(str)) return null;

            string[] formats = new[]
            {
                "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy",
                "yyyy-MM-dd", "yyyy/MM/dd", "dd/MM/yyyy HH:mm:ss", "yyyy-MM-dd HH:mm:ss"
            };

            if (DateTime.TryParseExact(str, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            {
                return dt;
            }

            if (DateTime.TryParse(str, new CultureInfo("vi-VN"), DateTimeStyles.None, out dt))
            {
                return dt;
            }

            if (DateTime.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
            {
                return dt;
            }

            return null;
        }
        #endregion

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
