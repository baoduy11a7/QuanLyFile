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
    public class StandardTctParser : IInvoiceParser
    {
        public string ProviderName => "TCT-Standard";

        public bool CanParse(string xmlContent)
        {
            if (string.IsNullOrWhiteSpace(xmlContent)) return false;
            // Chuẩn hóa phát hiện XML hóa đơn điện tử theo TT78 / NĐ123
            return xmlContent.Contains("<HDon", StringComparison.OrdinalIgnoreCase) ||
                   xmlContent.Contains("<DLHDon", StringComparison.OrdinalIgnoreCase) ||
                   xmlContent.Contains("<TTChung", StringComparison.OrdinalIgnoreCase) ||
                   xmlContent.Contains("<DSHHDVu", StringComparison.OrdinalIgnoreCase) ||
                   xmlContent.Contains("<HHDVu", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<ParsedInvoiceResult> ParseAsync(Stream xmlStream, string fileName)
        {
            var result = new ParsedInvoiceResult
            {
                SourceProvider = "TCT-Standard"
            };

            try
            {
                using var reader = new StreamReader(xmlStream, Encoding.UTF8, true, 1024, leaveOpen: true);
                var content = await reader.ReadToEndAsync();
                
                if (string.IsNullOrWhiteSpace(content))
                {
                    result.Success = false;
                    result.ErrorMessage = "Nội dung tệp XML rỗng.";
                    return result;
                }

                // Xóa BOM hoặc các ký tự lạ ở đầu XML nếu có
                int xmlStart = content.IndexOf('<');
                if (xmlStart > 0)
                {
                    content = content.Substring(xmlStart);
                }

                XDocument doc = XDocument.Parse(content);
                var root = doc.Root;
                if (root == null)
                {
                    result.Success = false;
                    result.ErrorMessage = "Cấu trúc XML không hợp lệ.";
                    return result;
                }

                // 1. Thông tin chung
                var ttChung = FindElement(root, "TTChung");
                var ndHDon = FindElement(root, "NDHDon");

                var khmsHDon = GetElementValue(ttChung, "KHMSHDon")?.Trim() ?? ""; // Mẫu số: vd 1
                var khHDon = GetElementValue(ttChung, "KHHDon")?.Trim() ?? "";     // Ký hiệu: vd C26TAA hoặc 1C26TAA

                // Chuẩn hóa ký hiệu: nếu bắt đầu bằng chữ số (vd 1C26TAA), tách mẫu số riêng và ký hiệu riêng
                if (khHDon.Length >= 7 && char.IsDigit(khHDon[0]))
                {
                    if (string.IsNullOrEmpty(khmsHDon)) khmsHDon = khHDon.Substring(0, 1);
                    khHDon = khHDon.Substring(1);
                }

                result.InvoiceSymbol = khHDon;

                var rawNumber = GetElementValue(ttChung, "SHDon")?.Trim() ?? "";
                if (long.TryParse(rawNumber, out var num))
                {
                    result.InvoiceNumber = num.ToString("D7");
                }
                else
                {
                    result.InvoiceNumber = rawNumber.PadLeft(7, '0');
                }

                var nLapStr = GetElementValue(ttChung, "NLap");
                result.IssueDate = ParseVietnamInvoiceDate(nLapStr);

                // Mã Cơ quan thuế (CQT) cấp
                var mcqThue = GetElementValue(root, "MCCQT") ?? GetElementValue(ttChung, "MCCQT");
                if (string.IsNullOrWhiteSpace(mcqThue))
                {
                    var cqtNode = FindElement(root, "CQT");
                    if (cqtNode != null)
                    {
                        mcqThue = GetElementValue(cqtNode, "MCCQT");
                    }
                }
                result.TaxAuthorityCode = mcqThue;
                result.HasTaxCode = !string.IsNullOrWhiteSpace(mcqThue);

                // Loại hóa đơn máy tính tiền (Ký tự thứ 4 của KHHDon là 'M')
                result.IsCashRegister = khHDon.Length >= 4 && char.ToUpperInvariant(khHDon[3]) == 'M';

                // 2. Thông tin Người Bán (NBan)
                var nBan = FindElement(ndHDon, "NBan") ?? FindElement(root, "NBan");
                if (nBan != null)
                {
                    result.SellerTaxCode = GetElementValue(nBan, "MST");
                    result.SellerName = GetElementValue(nBan, "Ten");
                    result.SellerAddress = GetElementValue(nBan, "DChi");
                }

                // 3. Thông tin Người Mua (NMua)
                var nMua = FindElement(ndHDon, "NMua") ?? FindElement(root, "NMua");
                if (nMua != null)
                {
                    result.BuyerTaxCode = GetElementValue(nMua, "MST");
                    result.BuyerName = GetElementValue(nMua, "Ten");
                    result.BuyerAddress = GetElementValue(nMua, "DChi");
                }

                // 4. Danh sách hàng hóa, dịch vụ (DSHHDVu)
                var dsHHDVu = FindElement(ndHDon, "DSHHDVu");
                if (dsHHDVu != null)
                {
                    int stt = 1;
                    foreach (var hhdvu in dsHHDVu.Elements().Where(e => e.Name.LocalName.Equals("HHDVu", StringComparison.OrdinalIgnoreCase)))
                    {
                        var itemName = GetElementValue(hhdvu, "THHDVu", "THang", "TenHang", "TenHHDVu", "ItemName", "prodName", "ProdName", "name", "TTHHDVu")?.Trim() ?? string.Empty;
                        var unit = GetElementValue(hhdvu, "DVTinh", "DVT", "DonViTinh", "Unit", "unitName")?.Trim() ?? string.Empty;
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
                res.ValidationWarnings.Add("Số hóa đơn không được để trống.");

            if (string.IsNullOrWhiteSpace(res.SellerTaxCode))
                res.ValidationWarnings.Add("Mã số thuế bên bán không được để trống.");

            if (res.AmountBeforeTax < 0 || res.TotalAmount < 0)
                res.ValidationWarnings.Add("Hóa đơn có tổng tiền âm (Hóa đơn điều chỉnh/giảm).");
        }

        private XElement? FindElement(XElement? parent, string localName)
        {
            if (parent == null) return null;
            return parent.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));
        }

        private string GetElementValue(XElement? parent, params string[] localNames)
        {
            if (parent == null || localNames == null || localNames.Length == 0) return string.Empty;
            foreach (var name in localNames)
            {
                var el = parent.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (el != null && !string.IsNullOrWhiteSpace(el.Value))
                {
                    return el.Value.Trim();
                }
            }
            return string.Empty;
        }

        public static DateTime ParseVietnamInvoiceDate(string? dateStr, DateTime? fallback = null)
        {
            if (string.IsNullOrWhiteSpace(dateStr)) return fallback ?? DateTime.Today;
            var trimmed = dateStr.Trim();

            // 0. Milliseconds / Epoch timestamp (ví dụ Cổng thuế trả số millis)
            if (long.TryParse(trimmed, out var epochVal))
            {
                if (epochVal > 1000000000000L)
                    return DateTimeOffset.FromUnixTimeMilliseconds(epochVal).ToOffset(TimeSpan.FromHours(7)).DateTime.Date;
                if (epochVal > 1000000000L && epochVal < 3000000000L)
                    return DateTimeOffset.FromUnixTimeSeconds(epochVal).ToOffset(TimeSpan.FromHours(7)).DateTime.Date;
            }

            // 1. Kiểm tra ngày thuần túy không có giờ (yyyy-MM-dd, dd/MM/yyyy) tránh hoàn toàn bị lùi 1 ngày do lệch múi giờ
            if (DateTime.TryParseExact(trimmed, new[] { "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "yyyy/MM/dd", "dd-MM-yyyy", "d-M-yyyy" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var dOnly))
            {
                return dOnly.Date;
            }

            // 2. Nếu chuỗi có offset/giờ ISO (ví dụ 2026-09-08T17:00:00Z hoặc +00:00 hoặc không offset nhưng có giờ)
            if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
            {
                // Nếu giờ >= 17 và offset <= 0 (chuẩn UTC lưu ngày 00:00 của VN thành 17:00 ngày hôm trước)
                if (dto.Offset == TimeSpan.Zero || dto.Offset.TotalHours < 7)
                {
                    return dto.ToOffset(TimeSpan.FromHours(7)).DateTime.Date;
                }
                return dto.DateTime.Date;
            }

            // 3. Chuỗi dạng 2026-09-07 17:00:00 hoặc 07/09/2026 17:00:00
            if (DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dInv))
            {
                if (dInv.Hour >= 17)
                {
                    return dInv.AddHours(7).Date;
                }
                return dInv.Date;
            }

            if (DateTime.TryParse(trimmed, CultureInfo.GetCultureInfo("vi-VN"), DateTimeStyles.None, out var dVi))
            {
                if (dVi.Hour >= 17)
                {
                    return dVi.AddHours(7).Date;
                }
                return dVi.Date;
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
