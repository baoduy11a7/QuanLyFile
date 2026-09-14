using ClosedXML.Excel;
using InvoiceManager.Data;
using InvoiceManager.Models.Entities;
using InvoiceManager.Models.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace InvoiceManager.Services
{
    public class ExcelImportService : IExcelImportService
    {
        private readonly ApplicationDbContext _db;
        private readonly IAuditLogService _auditLog;
        private readonly ILogger<ExcelImportService> _logger;

        public ExcelImportService(
            ApplicationDbContext db,
            IAuditLogService auditLog,
            ILogger<ExcelImportService> logger)
        {
            _db = db;
            _auditLog = auditLog;
            _logger = logger;
        }

        public Task<byte[]> GenerateTemplateAsync()
        {
            using var workbook = new XLWorkbook();

            // Sheet 1: Mẫu nhập dữ liệu
            var wsData = workbook.Worksheets.Add("MauNhapHoaDon");
            wsData.ShowGridLines = true;

            string[] headers = new[]
            {
                "Ký hiệu", "Số HĐ", "Ngày lập", "MST Người bán", "Tên Người bán", 
                "Địa chỉ Người bán", "MST Người mua", "Tên Người mua", 
                "Chưa thuế", "Tiền thuế", "Thanh toán", "Loại hóa đơn", 
                "Mã tra cứu", "Ghi chú"
            };

            for (int col = 0; col < headers.Length; col++)
            {
                var cell = wsData.Cell(1, col + 1);
                cell.Value = headers[col];
                cell.Style.Font.Bold = true;
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E40AF");
                cell.Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                cell.Style.Alignment.SetVertical(XLAlignmentVerticalValues.Center);
                cell.Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin);
                cell.Style.Border.OutsideBorderColor = XLColor.FromHtml("#93C5FD");
            }
            wsData.Row(1).Height = 28;

            // Dữ liệu mẫu (2 dòng minh họa)
            object[][] sampleRows = new object[][]
            {
                new object[] { "1C25TXX", "00000123", "15/01/2026", "0101234567", "CÔNG TY TNHH VẬT TƯ THIẾT BỊ HÀ NỘI", "Số 12 Phố Huế, Q. Hai Bà Trưng, Hà Nội", "0314567890", "CÔNG TY CỔ PHẦN CÔNG NGHỆ MINH ANH", 10000000, 1000000, 11000000, "MuaVao", "ABC123XYZ", "Hóa đơn mua vật tư dự án" },
                new object[] { "2C25TKT", "00000456", "20/01/2026", "0309876543-001", "CHI NHÁNH CÔNG TY CỔ PHẦN DỊCH VỤ SÀI GÒN", "Quận 1, TP. Hồ Chí Minh", "0314567890", "CÔNG TY CỔ PHẦN CÔNG NGHỆ MINH ANH", 5500000, 440000, 5940000, "MuaVao", "", "Hóa đơn tiếp khách" }
            };

            for (int r = 0; r < sampleRows.Length; r++)
            {
                for (int c = 0; c < sampleRows[r].Length; c++)
                {
                    var cell = wsData.Cell(r + 2, c + 1);
                    var val = sampleRows[r][c];
                    if (val is decimal || val is int)
                    {
                        cell.Value = Convert.ToDecimal(val);
                        cell.Style.NumberFormat.Format = "#,##0";
                    }
                    else
                    {
                        cell.Value = val.ToString();
                    }
                    cell.Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin);
                    cell.Style.Border.OutsideBorderColor = XLColor.FromHtml("#E5E7EB");
                }
            }

            wsData.Columns().AdjustToContents();
            wsData.Column(5).Width = 35;
            wsData.Column(6).Width = 35;

            // Sheet 2: Hướng dẫn định dạng dữ liệu
            var wsGuide = workbook.Worksheets.Add("HuongDanSuDung");
            wsGuide.ShowGridLines = true;

            wsGuide.Cell("A1").Value = "HƯỚNG DẪN ĐỊNH DẠNG DỮ LIỆU NHẬP HÓA ĐƠN EXCEL";
            wsGuide.Cell("A1").Style.Font.Bold = true;
            wsGuide.Cell("A1").Style.Font.FontSize = 14;
            wsGuide.Cell("A1").Style.Font.FontColor = XLColor.FromHtml("#1E3A8A");

            string[][] guideContent = new string[][]
            {
                new string[] { "Cột", "Quy định định dạng", "Bắt buộc?", "Ví dụ minh họa" },
                new string[] { "Ký hiệu", "Ký hiệu mẫu số và ký hiệu hóa đơn theo quy định (thường gồm 6 hoặc 7 ký tự).", "Bắt buộc", "1C25TXX, 2C25TKT, 1C25MXX" },
                new string[] { "Số HĐ", "Số hóa đơn gồm tối đa 8 chữ số. Có thể chứa số 0 đầu dòng.", "Bắt buộc", "00000123, 456" },
                new string[] { "Ngày lập", "Định dạng ngày dd/MM/yyyy hoặc yyyy-MM-dd.", "Bắt buộc", "15/01/2026" },
                new string[] { "MST Người bán", "Mã số thuế 10 số (doanh nghiệp) hoặc 13 số/10 số kèm -001 (chi nhánh).", "Bắt buộc", "0101234567 hoặc 0309876543-001" },
                new string[] { "Tên Người bán", "Tên đầy đủ của đơn vị bán hàng theo đăng ký thuế.", "Bắt buộc", "CÔNG TY TNHH ABC" },
                new string[] { "Chưa thuế", "Số tiền trước thuế GTGT. Không dùng dấu phẩy phân cách phần ngàn nếu nhập text.", "Bắt buộc", "10000000" },
                new string[] { "Tiền thuế", "Tiền thuế GTGT. Có thể bằng 0 nếu không chịu thuế hoặc thuế suất 0%.", "Bắt buộc", "1000000" },
                new string[] { "Thanh toán", "Tổng tiền thanh toán = Chưa thuế + Tiền thuế.", "Bắt buộc", "11000000" },
                new string[] { "Loại hóa đơn", "Nhập 'MuaVao' (cho HĐ mua hàng/chi phí) hoặc 'BanRa' (cho HĐ bán hàng/doanh thu). Mặc định là MuaVao.", "Tùy chọn", "MuaVao hoặc BanRa" },
                new string[] { "Mã tra cứu", "Mã bí mật hoặc mã tra cứu hóa đơn điện tử do bên bán cung cấp (nếu có).", "Tùy chọn", "A1B2C3D4" },
                new string[] { "Lưu ý quan trọng", "Dữ liệu nhập từ Excel sẽ được đánh dấu nguồn 'ManualExcel' trong hệ thống để phân biệt với hóa đơn XML gốc điện tử.", "Thông tin", "Phục vụ đối soát kế toán & lưu trữ" }
            };

            for (int r = 0; r < guideContent.Length; r++)
            {
                for (int c = 0; c < guideContent[r].Length; c++)
                {
                    var cell = wsGuide.Cell(r + 3, c + 1);
                    cell.Value = guideContent[r][c];
                    if (r == 0)
                    {
                        cell.Style.Font.Bold = true;
                        cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#F3F4F6");
                    }
                    cell.Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin);
                    cell.Style.Border.OutsideBorderColor = XLColor.FromHtml("#D1D5DB");
                }
            }
            wsGuide.Columns().AdjustToContents();

            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            return Task.FromResult(stream.ToArray());
        }

        public async Task<ExcelImportPreviewResult> PreviewExcelAsync(Stream stream, int taxAccountId, string defaultInvoiceType)
        {
            var result = new ExcelImportPreviewResult();

            try
            {
                using var workbook = new XLWorkbook(stream);
                var worksheet = workbook.Worksheets.FirstOrDefault();
                if (worksheet == null)
                {
                    result.Success = false;
                    result.ErrorMessage = "File Excel không chứa bất kỳ sheet dữ liệu nào.";
                    return result;
                }

                // 1. Tìm dòng header (header-based mapping)
                int headerRowIndex = -1;
                Dictionary<string, int> columnMap = new(StringComparer.OrdinalIgnoreCase);

                for (int r = 1; r <= Math.Min(worksheet.LastRowUsed()?.RowNumber() ?? 10, 10); r++)
                {
                    var row = worksheet.Row(r);
                    var map = BuildColumnMapping(row);
                    if (map.ContainsKey("InvoiceSymbol") || (map.ContainsKey("InvoiceNumber") && map.ContainsKey("SellerTaxCode")))
                    {
                        headerRowIndex = r;
                        columnMap = map;
                        break;
                    }
                }

                if (headerRowIndex == -1)
                {
                    result.Success = false;
                    result.ErrorMessage = "Không nhận diện được dòng tiêu đề cột hợp lệ trong file Excel. Vui lòng sử dụng file mẫu chuẩn của hệ thống.";
                    return result;
                }

                // Lấy danh sách hóa đơn hiện có trong DB để check duplicate
                var existingInvoices = await _db.Invoices
                    .Where(i => i.TaxAccountId == taxAccountId)
                    .Select(i => new { i.InvoiceType, i.SellerTaxCode, i.InvoiceSymbol, i.InvoiceNumber })
                    .ToListAsync();

                var existingKeys = new HashSet<string>(
                    existingInvoices.Select(i => BuildInvoiceKey(i.InvoiceType, i.SellerTaxCode, i.InvoiceSymbol, i.InvoiceNumber)),
                    StringComparer.OrdinalIgnoreCase);

                var fileInternalKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                int lastRow = worksheet.LastRowUsed()?.RowNumber() ?? headerRowIndex;
                int dataRowCount = 0;

                for (int r = headerRowIndex + 1; r <= lastRow; r++)
                {
                    var row = worksheet.Row(r);
                    if (row.IsEmpty()) continue;

                    var form = GetCellValue(row, columnMap, "InvoiceForm").Trim();
                    var symbol = GetCellValue(row, columnMap, "InvoiceSymbol").Trim();
                    if (!string.IsNullOrWhiteSpace(form) && !string.IsNullOrWhiteSpace(symbol) && !symbol.StartsWith(form))
                    {
                        symbol = $"{form}{symbol}";
                    }

                    var number = GetCellValue(row, columnMap, "InvoiceNumber");
                    var sellerTaxCode = GetCellValue(row, columnMap, "SellerTaxCode");

                    // Bỏ qua nếu các cột khóa chính đều trống
                    if (string.IsNullOrWhiteSpace(symbol) && string.IsNullOrWhiteSpace(number) && string.IsNullOrWhiteSpace(sellerTaxCode))
                        continue;

                    dataRowCount++;
                    var item = new ExcelImportRowDto
                    {
                        RowIndex = r,
                        InvoiceSymbol = symbol.Trim().ToUpperInvariant(),
                        InvoiceNumber = NormalizeInvoiceNumber(number),
                        IssueDateStr = GetCellValue(row, columnMap, "IssueDate"),
                        SellerTaxCode = NormalizeTaxCode(sellerTaxCode),
                        SellerName = GetCellValue(row, columnMap, "SellerName").Trim(),
                        SellerAddress = GetCellValue(row, columnMap, "SellerAddress").Trim(),
                        BuyerTaxCode = NormalizeTaxCode(GetCellValue(row, columnMap, "BuyerTaxCode")),
                        BuyerName = GetCellValue(row, columnMap, "BuyerName").Trim(),
                        InvoiceType = NormalizeInvoiceType(GetCellValue(row, columnMap, "InvoiceType"), defaultInvoiceType),
                        LookupCode = GetCellValue(row, columnMap, "LookupCode").Trim(),
                        Notes = GetCellValue(row, columnMap, "Notes").Trim()
                    };

                    // Đọc số tiền
                    item.AmountBeforeTax = ParseDecimalValue(GetCellRawValue(row, columnMap, "AmountBeforeTax"));
                    item.TaxAmount = ParseDecimalValue(GetCellRawValue(row, columnMap, "TaxAmount"));
                    item.TotalAmount = ParseDecimalValue(GetCellRawValue(row, columnMap, "TotalAmount"));

                    // Nếu TotalAmount = 0 nhưng có AmountBeforeTax
                    if (item.TotalAmount == 0 && (item.AmountBeforeTax > 0 || item.TaxAmount > 0))
                    {
                        item.TotalAmount = item.AmountBeforeTax + item.TaxAmount;
                    }

                    // Parse & Validate Ngày lập
                    ValidateAndParseDate(row, columnMap, item);

                    // Validate các trường nghiệp vụ
                    ValidateBusinessRules(item, existingKeys, fileInternalKeys);

                    result.Rows.Add(item);
                }

                result.TotalRows = result.Rows.Count;
                result.ValidCount = result.Rows.Count(r => r.IsValid);
                result.ErrorCount = result.Rows.Count(r => !r.IsValid);

                if (result.TotalRows == 0)
                {
                    result.Success = false;
                    result.ErrorMessage = "File Excel không chứa dòng dữ liệu hóa đơn nào sau dòng tiêu đề.";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi phân tích preview file Excel");
                result.Success = false;
                result.ErrorMessage = $"Lỗi khi đọc file Excel: {ex.Message}";
            }

            return result;
        }

        public async Task<ExcelImportSaveResult> SaveImportAsync(List<ExcelImportRowDto> validRows, int taxAccountId, string? userId)
        {
            var saveResult = new ExcelImportSaveResult { TotalProcessed = validRows.Count };

            if (!validRows.Any())
            {
                saveResult.Success = false;
                saveResult.Messages.Add("Không có dòng hợp lệ nào để lưu vào hệ thống.");
                return saveResult;
            }

            // Chia theo batch 100 dòng / transaction
            const int batchSize = 100;
            int batchNumber = 0;

            for (int i = 0; i < validRows.Count; i += batchSize)
            {
                batchNumber++;
                var batch = validRows.Skip(i).Take(batchSize).ToList();

                using var transaction = await _db.Database.BeginTransactionAsync();
                try
                {
                    foreach (var row in batch)
                    {
                        // Kiểm tra lại lần cuối xem có bị trùng trong DB không
                        var exists = await _db.Invoices.AnyAsync(inv =>
                            inv.TaxAccountId == taxAccountId
                            && inv.InvoiceType == row.InvoiceType
                            && inv.SellerTaxCode == row.SellerTaxCode
                            && inv.InvoiceSymbol == row.InvoiceSymbol
                            && inv.InvoiceNumber == row.InvoiceNumber);

                        if (exists)
                        {
                            saveResult.FailedCount++;
                            saveResult.Messages.Add($"Dòng {row.RowIndex}: Hóa đơn {row.InvoiceSymbol}-{row.InvoiceNumber} của MST {row.SellerTaxCode} đã tồn tại trong CSDL. Bỏ qua.");
                            continue;
                        }

                        var invoice = new Invoice
                        {
                            TaxAccountId = taxAccountId,
                            InvoiceSymbol = row.InvoiceSymbol,
                            InvoiceNumber = row.InvoiceNumber,
                            IssueDate = row.IssueDate ?? DateTime.Now,
                            SellerTaxCode = row.SellerTaxCode,
                            SellerName = row.SellerName,
                            SellerAddress = row.SellerAddress,
                            BuyerTaxCode = row.BuyerTaxCode,
                            BuyerName = row.BuyerName,
                            AmountBeforeTax = row.AmountBeforeTax,
                            TaxAmount = row.TaxAmount,
                            TotalAmount = row.TotalAmount,
                            InvoiceType = row.InvoiceType,
                            HasTaxCode = row.InvoiceSymbol.StartsWith("1"),
                            IsCashRegister = row.InvoiceSymbol.Contains("M", StringComparison.OrdinalIgnoreCase),
                            Status = "Hóa đơn mới",
                            SourceProvider = "ManualExcel",
                            OriginalFileName = "Import_Excel",
                            ImportedAt = DateTime.Now,
                            ImportedByUserId = userId,
                            Notes = string.IsNullOrWhiteSpace(row.Notes) ? "Nhập từ file Excel" : row.Notes,
                            RiskLevel = "Normal"
                        };

                        // Tạo dòng chi tiết mặc định
                        invoice.Details.Add(new InvoiceDetail
                        {
                            LineNumber = 1,
                            ItemName = "Hàng hóa/dịch vụ theo hóa đơn nhập Excel",
                            Quantity = 1,
                            UnitPrice = row.AmountBeforeTax,
                            AmountBeforeTax = row.AmountBeforeTax,
                            TaxRate = row.AmountBeforeTax > 0 ? Math.Round((row.TaxAmount / row.AmountBeforeTax) * 100, 0) : 0,
                            TaxAmount = row.TaxAmount,
                            TotalAmount = row.TotalAmount
                        });

                        _db.Invoices.Add(invoice);
                        saveResult.SuccessCount++;
                    }

                    await _db.SaveChangesAsync();
                    await transaction.CommitAsync();
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    _logger.LogError(ex, "Lỗi khi lưu batch {BatchNumber} gồm {Count} dòng Excel", batchNumber, batch.Count);
                    saveResult.FailedCount += batch.Count;
                    saveResult.Messages.Add($"Lỗi khi lưu lô dòng từ {i + 1} đến {Math.Min(i + batchSize, validRows.Count)}: {ex.Message}");
                }
            }

            // Ghi AuditLog
            await _auditLog.LogActionAsync(
                "Import Excel",
                $"{saveResult.SuccessCount}/{saveResult.TotalProcessed} hóa đơn thành công",
                $"Thành công: {saveResult.SuccessCount}, Lỗi: {saveResult.FailedCount}",
                taxAccountId);

            saveResult.Success = saveResult.SuccessCount > 0;
            saveResult.Messages.Insert(0, $"Đã lưu thành công {saveResult.SuccessCount}/{saveResult.TotalProcessed} hóa đơn từ file Excel.");

            return saveResult;
        }

        #region Helper Validation & Mapping Methods

        private Dictionary<string, int> BuildColumnMapping(IXLRow row)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int lastCol = row.LastCellUsed()?.Address.ColumnNumber ?? 25;

            for (int col = 1; col <= lastCol; col++)
            {
                var text = row.Cell(col).GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(text)) continue;

                // Chuẩn hóa: loại bỏ dấu tiếng Việt, khoảng trắng, xuống dòng Alt+Enter (\r, \n), dấu phẩy, gạch ngang...
                var normalized = RemoveVietnameseDiacritics(text)
                    .ToLowerInvariant()
                    .Replace("\r", "")
                    .Replace("\n", "")
                    .Replace("\t", "")
                    .Replace(" ", "")
                    .Replace("_", "")
                    .Replace("-", "")
                    .Replace(",", "")
                    .Replace(".", "");

                // 1. Ký hiệu & Mẫu số
                if (normalized.Contains("khmau") || (normalized.Contains("mauso") && !normalized.Contains("kyhieu")))
                {
                    map["InvoiceForm"] = col;
                }
                else if (normalized.Contains("kyhieu") || normalized.Contains("khhdon"))
                {
                    map["InvoiceSymbol"] = col;
                }
                // 2. Số hóa đơn: khớp cả 'so', 'sohd', 'shdon', 'sohoadon'
                else if (normalized == "so" || normalized == "shd" || normalized.StartsWith("sohd") || normalized.Contains("sohoadon") || normalized == "sohoadon")
                {
                    map["InvoiceNumber"] = col;
                }
                // 3. Ngày lập: khớp cả 'ngay', 'ngayhd', 'ngaylap', 'ngaythangnam'
                else if (normalized == "ngay" || normalized == "ngayhd" || normalized.StartsWith("ngay") || normalized.Contains("ngaylap") || normalized.Contains("ngayhoadon") || normalized.Contains("ngaythang"))
                {
                    map["IssueDate"] = col;
                }
                // 4. MST Người bán
                else if (normalized.Contains("mstnguoiban") || normalized.Contains("mstban") || (normalized.Contains("mst") && !normalized.Contains("mua") && !map.ContainsKey("SellerTaxCode")))
                {
                    map["SellerTaxCode"] = col;
                }
                // 5. Địa chỉ Người bán (Ưu tiên kiểm tra trước Tên người bán)
                else if (normalized.Contains("diachinguoiban") || normalized.Contains("diachiban") || normalized.Contains("dchiban"))
                {
                    map["SellerAddress"] = col;
                }
                // 6. Tên Người bán: loại trừ 'diachi', 'dchi', 'mst'
                else if (!normalized.Contains("diachi") && !normalized.Contains("dchi") && !normalized.Contains("mst")
                         && (normalized.Contains("nguoiban") || normalized.Contains("tenban") || normalized.Contains("donviban") || normalized == "nguoiban"))
                {
                    map["SellerName"] = col;
                }
                // 7. MST Người mua
                else if (normalized.Contains("mstnguoimua") || normalized.Contains("mstmua"))
                {
                    map["BuyerTaxCode"] = col;
                }
                // 8. Địa chỉ Người mua (Ưu tiên kiểm tra trước Tên người mua)
                else if (normalized.Contains("diachinguoimua") || normalized.Contains("diachimua") || normalized.Contains("dchimua"))
                {
                    map["BuyerAddress"] = col;
                }
                // 9. Tên Người mua: loại trừ 'diachi', 'dchi', 'mst'
                else if (!normalized.Contains("diachi") && !normalized.Contains("dchi") && !normalized.Contains("mst")
                         && (normalized.Contains("nguoimua") || normalized.Contains("tenmua") || normalized.Contains("donvimua") || normalized == "nguoimua"))
                {
                    map["BuyerName"] = col;
                }
                // 9. Tiền chưa thuế: khớp 'tienchuathue', 'chuathue', 'doanhsomuachuacothue', 'tienhang', 'doanhso'
                else if (normalized.Contains("chuathue") || normalized.Contains("tientruocthue") || normalized.Contains("tienhang") || normalized.Contains("doanhso"))
                {
                    map["AmountBeforeTax"] = col;
                }
                // 10. Tiền thuế GTGT: khớp 'tienthue', 'thuegtgt', 'thue'
                else if (normalized.Contains("tienthue") || normalized.Contains("thuegtgt") || normalized == "thue")
                {
                    map["TaxAmount"] = col;
                }
                // 11. Tổng thanh toán
                else if (normalized.Contains("thanhtoan") || normalized.Contains("tongcong") || normalized.Contains("tongtien"))
                {
                    map["TotalAmount"] = col;
                }
                // 12. Loại hóa đơn
                else if (normalized.Contains("loaihd") || normalized.Contains("loaihoadon"))
                {
                    map["InvoiceType"] = col;
                }
                // 13. Mã tra cứu
                else if (normalized.Contains("matracuu") || normalized.Contains("lookupcode"))
                {
                    map["LookupCode"] = col;
                }
                // 14. Ghi chú / Mặt hàng
                else if (normalized.Contains("ghichu") || normalized.Contains("notes") || normalized.Contains("mathang"))
                {
                    map["Notes"] = col;
                }
            }

            return map;
        }

        private void ValidateAndParseDate(IXLRow row, Dictionary<string, int> columnMap, ExcelImportRowDto item)
        {
            if (columnMap.TryGetValue("IssueDate", out int col))
            {
                var cell = row.Cell(col);
                if (cell.DataType == XLDataType.DateTime)
                {
                    item.IssueDate = cell.GetDateTime();
                    item.IssueDateStr = item.IssueDate.Value.ToString("dd/MM/yyyy");
                    return;
                }
                else if (cell.DataType == XLDataType.Number)
                {
                    // Trường hợp ngày lưu dạng serial number của Excel
                    try
                    {
                        var serial = cell.GetDouble();
                        if (serial > 30000 && serial < 60000)
                        {
                            item.IssueDate = DateTime.FromOADate(serial);
                            item.IssueDateStr = item.IssueDate.Value.ToString("dd/MM/yyyy");
                            return;
                        }
                    }
                    catch { }
                }
            }

            if (string.IsNullOrWhiteSpace(item.IssueDateStr))
            {
                item.ErrorMessages.Add("Ngày lập không được để trống.");
                item.IsValid = false;
                return;
            }

            string[] formats = new[] { 
                "d/M/yyyy", "dd/MM/yyyy", "yyyy-MM-dd", "d-M-yyyy", "dd-MM-yyyy",
                "M/d/yyyy", "MM/dd/yyyy", "yyyy/MM/dd", "d/M/yy", "M/d/yy" 
            };
            if (DateTime.TryParseExact(item.IssueDateStr.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
            {
                item.IssueDate = parsedDate;
            }
            else if (DateTime.TryParse(item.IssueDateStr.Trim(), CultureInfo.GetCultureInfo("vi-VN"), DateTimeStyles.None, out parsedDate))
            {
                item.IssueDate = parsedDate;
            }
            else if (DateTime.TryParse(item.IssueDateStr.Trim(), out parsedDate))
            {
                item.IssueDate = parsedDate;
            }
            else
            {
                item.ErrorMessages.Add($"Ngày lập '{item.IssueDateStr}' không đúng định dạng (dd/MM/yyyy).");
                item.IsValid = false;
            }
        }

        private void ValidateBusinessRules(ExcelImportRowDto item, HashSet<string> existingKeys, HashSet<string> fileInternalKeys)
        {
            // 1. Ký hiệu
            if (string.IsNullOrWhiteSpace(item.InvoiceSymbol))
            {
                item.ErrorMessages.Add("Ký hiệu hóa đơn trống.");
                item.IsValid = false;
            }
            else if (item.InvoiceSymbol.Length > 20)
            {
                item.ErrorMessages.Add("Ký hiệu hóa đơn không được vượt quá 20 ký tự.");
                item.IsValid = false;
            }

            // 2. Số HĐ
            if (string.IsNullOrWhiteSpace(item.InvoiceNumber))
            {
                item.ErrorMessages.Add("Số hóa đơn trống.");
                item.IsValid = false;
            }
            else if (item.InvoiceNumber.Length > 20)
            {
                item.ErrorMessages.Add("Số hóa đơn không được vượt quá 20 ký tự.");
                item.IsValid = false;
            }

            // 3. MST Người bán
            if (string.IsNullOrWhiteSpace(item.SellerTaxCode))
            {
                item.ErrorMessages.Add("Mã số thuế người bán trống.");
                item.IsValid = false;
            }
            else if (!IsValidVietnameseTaxCode(item.SellerTaxCode))
            {
                item.ErrorMessages.Add($"Mã số thuế người bán '{item.SellerTaxCode}' không đúng định dạng (10 số hoặc 13 số theo quy định).");
                item.IsValid = false;
            }

            // 4. Tên người bán
            if (string.IsNullOrWhiteSpace(item.SellerName))
            {
                item.ErrorMessages.Add("Tên người bán trống.");
                item.IsValid = false;
            }

            // 5. Số tiền hợp lệ
            if (item.AmountBeforeTax < 0)
            {
                item.ErrorMessages.Add("Tiền chưa thuế không được âm.");
                item.IsValid = false;
            }
            if (item.TaxAmount < 0)
            {
                item.ErrorMessages.Add("Tiền thuế GTGT không được âm.");
                item.IsValid = false;
            }
            if (item.TotalAmount < 0)
            {
                item.ErrorMessages.Add("Tổng tiền thanh toán không được âm.");
                item.IsValid = false;
            }

            // Cảnh báo số học (chỉ khi có cả tổng tiền và tiền trước thuế > 0)
            if (item.TotalAmount > 0 && item.AmountBeforeTax > 0)
            {
                decimal expectedTotal = item.AmountBeforeTax + item.TaxAmount;
                if (Math.Abs(item.TotalAmount - expectedTotal) > 10)
                {
                    item.WarningMessages.Add($"Tổng thanh toán ({item.TotalAmount:N0} đ) lệch so với Tiền chưa thuế + Thuế ({expectedTotal:N0} đ).");
                }
            }

            // 6. Kiểm tra trùng lặp khóa nghiệp vụ
            var key = BuildInvoiceKey(item.InvoiceType, item.SellerTaxCode, item.InvoiceSymbol, item.InvoiceNumber);

            if (fileInternalKeys.Contains(key))
            {
                item.ErrorMessages.Add($"Trùng lặp với một dòng khác trong cùng file Excel (MST: {item.SellerTaxCode}, Ký hiệu: {item.InvoiceSymbol}, Số: {item.InvoiceNumber}).");
                item.IsValid = false;
            }
            else
            {
                fileInternalKeys.Add(key);
            }

            if (existingKeys.Contains(key))
            {
                item.ErrorMessages.Add($"Hóa đơn {item.InvoiceSymbol}-{item.InvoiceNumber} của MST {item.SellerTaxCode} đã tồn tại trên hệ thống.");
                item.IsValid = false;
            }
        }

        private static bool IsValidVietnameseTaxCode(string taxCode)
        {
            if (string.IsNullOrWhiteSpace(taxCode)) return false;
            taxCode = taxCode.Trim().Replace(" ", "");
            // Chấp nhận: 10 chữ số (doanh nghiệp), 10 chữ số gạch ngang 3 số (chi nhánh), 12 chữ số (CCCD/Hộ kinh doanh theo TT 105/2020), 13 hoặc 14 chữ số liền
            return Regex.IsMatch(taxCode, @"^(\d{10}|\d{10}-\d{3}|\d{12}|\d{13}|\d{14})$");
        }

        private static string NormalizeInvoiceNumber(string number)
        {
            if (string.IsNullOrWhiteSpace(number)) return string.Empty;
            number = number.Trim();
            // Nếu là số nguyên và độ dài < 8, giữ nguyên định dạng hoặc pad 0 nếu cần
            if (long.TryParse(number, out var num) && number.Length <= 8)
            {
                return number.PadLeft(Math.Max(number.Length, 7), '0');
            }
            return number;
        }

        private static string NormalizeTaxCode(string? taxCode)
        {
            if (string.IsNullOrWhiteSpace(taxCode)) return string.Empty;
            return taxCode.Trim().Replace(" ", "");
        }

        private static string NormalizeInvoiceType(string? rawType, string defaultType)
        {
            if (string.IsNullOrWhiteSpace(rawType)) return defaultType;
            var clean = rawType.Trim().ToLowerInvariant();
            if (clean.Contains("ban") || clean.Contains("ra") || clean == "banra")
                return "BanRa";
            return "MuaVao";
        }

        private static string BuildInvoiceKey(string invoiceType, string sellerTaxCode, string symbol, string number)
        {
            return $"{invoiceType}_{sellerTaxCode}_{symbol}_{number}".ToUpperInvariant();
        }

        private string GetCellValue(IXLRow row, Dictionary<string, int> map, string key)
        {
            if (map.TryGetValue(key, out int col))
            {
                return row.Cell(col).GetString()?.Trim() ?? string.Empty;
            }
            return string.Empty;
        }

        private object? GetCellRawValue(IXLRow row, Dictionary<string, int> map, string key)
        {
            if (map.TryGetValue(key, out int col))
            {
                var cell = row.Cell(col);
                if (cell.DataType == XLDataType.Number) return cell.GetDouble();
                return cell.GetString()?.Trim();
            }
            return null;
        }

        private decimal ParseDecimalValue(object? val)
        {
            if (val == null) return 0;
            if (val is double d) return Convert.ToDecimal(d);
            if (val is decimal dec) return dec;
            if (val is int i) return i;

            var str = val.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(str)) return 0;

            str = str.Replace(" ", "").Replace(",", ".");
            if (decimal.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out var result))
            {
                return result;
            }
            return 0;
        }

        private static string RemoveVietnameseDiacritics(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var normalizedString = text.Normalize(NormalizationForm.FormD);
            var stringBuilder = new StringBuilder(normalizedString.Length);

            foreach (var c in normalizedString)
            {
                var unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(c);
                if (unicodeCategory != UnicodeCategory.NonSpacingMark)
                {
                    stringBuilder.Append(c);
                }
            }

            return stringBuilder.ToString().Normalize(NormalizationForm.FormC).Replace('đ', 'd').Replace('Đ', 'D');
        }

        #endregion
    }
}
