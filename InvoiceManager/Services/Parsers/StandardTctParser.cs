using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace InvoiceManager.Services.Parsers
{
    /// <summary>
    /// Parser chuẩn kỹ thuật thành phần dữ liệu hóa đơn điện tử theo Quyết định 1450/QĐ-TCT và Nghị định 123/2020/NĐ-CP
    /// </summary>
    public class StandardTctParser : IInvoiceParser
    {
        public string ProviderName => "TCT-Standard";

        public bool CanParse(string xmlContent)
        {
            if (string.IsNullOrWhiteSpace(xmlContent)) return false;
            return xmlContent.Contains("<HDon") || xmlContent.Contains("<DLHDon") || xmlContent.Contains("<TTChung");
        }

        public async Task<ParsedInvoiceResult> ParseAsync(Stream xmlStream, string fileName)
        {
            var result = new ParsedInvoiceResult { SourceProvider = ProviderName };

            try
            {
                using var reader = new StreamReader(xmlStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                var rawXml = await reader.ReadToEndAsync();
                result.RawXmlContent = rawXml;

                if (string.IsNullOrWhiteSpace(rawXml))
                {
                    result.Success = false;
                    result.ErrorMessage = "Nội dung file XML rỗng.";
                    return result;
                }

                // Tự động chuẩn hóa các ký tự & tự do trong XML (EntityName parse error)
                var sanitizedXml = System.Text.RegularExpressions.Regex.Replace(
                    rawXml,
                    @"&(?!(amp|lt|gt|quot|apos|#\d+|#x[0-9a-fA-F]+);)",
                    "&amp;");

                var doc = XDocument.Parse(sanitizedXml);
                var root = doc.Root;
                if (root == null)
                {
                    result.Success = false;
                    result.ErrorMessage = "Không tìm thấy thẻ gốc XML.";
                    return result;
                }

                // Loại bỏ namespace tạm thời để truy vấn thẻ linh hoạt không phụ thuộc namespace prefix
                var dlHDon = FindElement(root, "DLHDon") ?? root;
                var ttChung = FindElement(dlHDon, "TTChung") ?? FindElement(root, "TTChung");
                var ndHDon = FindElement(dlHDon, "NDHDon") ?? FindElement(root, "NDHDon") ?? root;

                if (ttChung == null)
                {
                    result.Success = false;
                    result.ErrorMessage = "Cấu trúc XML không đúng định dạng HĐĐT: Thiếu thẻ thông tin chung <TTChung>.";
                    return result;
                }

                // 1. Thông tin chung
                var khmsHDon = GetElementValue(ttChung, "KHMSHDon")?.Trim() ?? ""; // Mẫu số: vd 1
                var khHDon = GetElementValue(ttChung, "KHHDon")?.Trim() ?? "";     // Ký hiệu: vd C26TAA hoặc 1C26TAA
                
                // Chuẩn hóa ký hiệu hóa đơn thống nhất toàn hệ thống (6 ký tự, ví dụ C26TAA, K26TYY)
                var cleanSymbol = !string.IsNullOrEmpty(khHDon) ? khHDon : khmsHDon;
                if (cleanSymbol.Length >= 7 && char.IsDigit(cleanSymbol[0]))
                {
                    cleanSymbol = cleanSymbol.Substring(1);
                }
                result.InvoiceSymbol = cleanSymbol.Trim();

                // Chuẩn hóa số hóa đơn 7 chữ số (ví dụ 0015483)
                var rawNumber = GetElementValue(ttChung, "SHDon")?.Trim() ?? "";
                if (long.TryParse(rawNumber, out var numVal))
                {
                    result.InvoiceNumber = numVal.ToString("D7");
                }
                else
                {
                    result.InvoiceNumber = rawNumber.PadLeft(7, '0');
                }

                var nLapStr = GetElementValue(ttChung, "NLap");
                result.IssueDate = ParseVietnamInvoiceDate(nLapStr);

                // Mã CQT
                var mcqThue = GetElementValue(root, "MCCQT") ?? GetElementValue(ttChung, "MCCQT");
                if (!string.IsNullOrEmpty(mcqThue))
                {
                    result.HasTaxCode = true;
                    result.TaxAuthorityCode = mcqThue;
                }
                else
                {
                    // Kiểm tra ký hiệu: nếu ký hiệu bắt đầu bằng 1 là có mã, 2 là không mã theo NĐ 123
                    result.HasTaxCode = result.InvoiceSymbol.StartsWith("1");
                }

                // Hóa đơn từ máy tính tiền (ký hiệu thường có chữ M, vd 1C25M...)
                result.IsCashRegister = result.InvoiceSymbol.Contains("M");

                // 2. Thông tin người bán
                var nBan = FindElement(ndHDon, "NBan");
                if (nBan != null)
                {
                    result.SellerTaxCode = GetElementValue(nBan, "MST");
                    result.SellerName = GetElementValue(nBan, "Ten");
                    result.SellerAddress = GetElementValue(nBan, "DChi");
                }

                // 3. Thông tin người mua
                var nMua = FindElement(ndHDon, "NMua");
                if (nMua != null)
                {
                    result.BuyerTaxCode = GetElementValue(nMua, "MST");
                    result.BuyerName = GetElementValue(nMua, "Ten");
                    result.BuyerAddress = GetElementValue(nMua, "DChi");
                }

                // 4. Bảng kê hàng hóa dịch vụ
                var dsHHDVu = FindElement(ndHDon, "DSHHDVu");
                if (dsHHDVu != null)
                {
                    int stt = 1;
                    foreach (var hhdvu in dsHHDVu.Elements().Where(e => e.Name.LocalName == "HHDVu"))
                    {
                        var itemName = GetElementValue(hhdvu, "THHDVu")?.Trim() ?? string.Empty;
                        var unit = GetElementValue(hhdvu, "DVTinh")?.Trim() ?? string.Empty;
                        var taxRate = ParseTaxRate(GetElementValue(hhdvu, "TSuat"));
                        var amountBeforeTax = ParseDecimal(GetElementValue(hhdvu, "ThTien"), 0);
                        var taxAmount = ParseDecimal(GetElementValue(hhdvu, "TThue"), 0);

                        // Nếu XML chuẩn TCT không lưu TThue ở từng dòng HHDVu mà chỉ có thuế suất, tính tiền thuế dòng
                        if (taxAmount == 0 && taxRate > 0 && amountBeforeTax > 0)
                        {
                            taxAmount = Math.Round(amountBeforeTax * taxRate / 100m, 0);
                        }

                        var line = new ParsedInvoiceDetail
                        {
                            LineNumber = int.TryParse(GetElementValue(hhdvu, "STT"), out var n) ? n : stt++,
                            ItemCode = GetElementValue(hhdvu, "MHHDVu")?.Trim(),
                            ItemName = itemName,
                            Unit = unit,
                            Quantity = ParseDecimal(GetElementValue(hhdvu, "SLuong"), 1),
                            UnitPrice = ParseDecimal(GetElementValue(hhdvu, "DGia"), 0),
                            AmountBeforeTax = amountBeforeTax,
                            TaxRate = taxRate,
                            TaxAmount = taxAmount,
                            TotalAmount = amountBeforeTax + taxAmount
                        };

                        result.Details.Add(line);
                    }
                }

                // 5. Tổng thanh toán
                var tToan = FindElement(ndHDon, "TToan") ?? FindElement(root, "TToan");
                if (tToan != null)
                {
                    result.AmountBeforeTax = ParseDecimal(GetElementValue(tToan, "TgTCThue"), 0);
                    result.TaxAmount = ParseDecimal(GetElementValue(tToan, "TgTThue"), 0);
                    result.TotalAmount = ParseDecimal(GetElementValue(tToan, "TgTTTBSo"), 0);
                }

                // Nếu thẻ tổng chưa có, tự tính từ chi tiết
                if (result.TotalAmount == 0 && result.Details.Any())
                {
                    result.AmountBeforeTax = result.Details.Sum(d => d.AmountBeforeTax);
                    result.TaxAmount = result.Details.Sum(d => d.TaxAmount);
                    result.TotalAmount = result.AmountBeforeTax + result.TaxAmount;
                }

                // Validation quy tắc nghiệp vụ hóa đơn
                ValidateInvoiceData(result);

                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Lỗi khi phân tích file XML: {ex.Message}";
            }

            return result;
        }

        private void ValidateInvoiceData(ParsedInvoiceResult res)
        {
            if (string.IsNullOrWhiteSpace(res.InvoiceSymbol))
                res.ValidationWarnings.Add("Ký hiệu hóa đơn trống.");

            if (string.IsNullOrWhiteSpace(res.InvoiceNumber))
                res.ValidationWarnings.Add("Số hóa đơn trống.");

            if (string.IsNullOrWhiteSpace(res.SellerTaxCode))
                res.ValidationWarnings.Add("Mã số thuế người bán trống.");

            // Kiểm tra tính đúng đắn số học
            if (res.Details.Any())
            {
                var sumLineAmount = res.Details.Sum(d => d.AmountBeforeTax);
                if (Math.Abs(sumLineAmount - res.AmountBeforeTax) > 10)
                {
                    res.ValidationWarnings.Add($"Cảnh báo: Tổng tiền dòng hàng ({sumLineAmount:N0}đ) lệch với tổng tiền trước thuế ghi nhận ({res.AmountBeforeTax:N0}đ).");
                }
            }
        }

        private XElement? FindElement(XElement? parent, string localName)
        {
            if (parent == null) return null;
            return parent.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));
        }

        private string GetElementValue(XElement? parent, string localName)
        {
            if (parent == null) return string.Empty;
            var el = parent.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));
            return el?.Value?.Trim() ?? string.Empty;
        }

        public static DateTime ParseVietnamInvoiceDate(string? dateStr, DateTime? fallback = null)
        {
            if (string.IsNullOrWhiteSpace(dateStr)) return fallback ?? DateTime.Today;
            var trimmed = dateStr.Trim();

            // 1. Kiểm tra ngày thuần túy không có giờ (yyyy-MM-dd, dd/MM/yyyy) tránh hoàn toàn bị lùi 1 ngày do lệch múi giờ
            if (DateTime.TryParseExact(trimmed, new[] { "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "yyyy/MM/dd", "dd-MM-yyyy", "d-M-yyyy" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var dOnly))
            {
                return dOnly.Date;
            }

            // 2. Nếu chuỗi có offset/giờ ISO (ví dụ 2026-09-08T17:00:00Z hoặc +00:00)
            // Phải chuyển đổi chuẩn xác sang múi giờ Việt Nam (+7) trước khi lấy Date
            if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
            {
                return dto.ToOffset(TimeSpan.FromHours(7)).DateTime.Date;
            }

            // 3. Culture vi-VN
            if (DateTime.TryParse(trimmed, CultureInfo.GetCultureInfo("vi-VN"), DateTimeStyles.None, out var dVi))
            {
                return dVi.Date;
            }

            if (DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dInv))
            {
                return dInv.Date;
            }

            return fallback ?? DateTime.Today;
        }

        private decimal ParseDecimal(string value, decimal defaultValue)
        {
            if (string.IsNullOrWhiteSpace(value)) return defaultValue;
            value = value.Replace(" ", "").Replace(",", ".");
            return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var res) ? res : defaultValue;
        }

        private decimal ParseTaxRate(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 10;
            var clean = value.Replace("%", "").Trim();
            if (clean.Equals("KCT", StringComparison.OrdinalIgnoreCase) || clean.Contains("chịu")) return -1;
            return decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out var rate) ? rate : 10;
        }
    }
}
