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

                result.TemplateCode = !string.IsNullOrEmpty(khmsHDon) ? khmsHDon : "1";
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

                // Tiền tệ, Tỉ giá, Hình thức thanh toán, Tính chất
                var dvtTe = GetElementValue(ttChung, "DVTTe")?.Trim();
                result.Currency = !string.IsNullOrEmpty(dvtTe) ? dvtTe : "VND";

                var tGia = ParseDecimal(GetElementValue(ttChung, "TGia"), 1);
                result.ExchangeRate = tGia > 0 ? tGia : 1;

                var htTToan = GetElementValue(ttChung, "HTTToan")?.Trim();
                result.PaymentMethod = !string.IsNullOrEmpty(htTToan) ? htTToan : "TM/CK";

                var tChat = GetElementValue(ttChung, "TChat", "TCHDon")?.Trim();
                result.InvoiceProperty = !string.IsNullOrEmpty(tChat) ? tChat : "Hàng hoá/dịch vụ";

                // Thông tin khác (TTKhac): Mã tra cứu, Biển số xe, Ghi chú thay thế điều chỉnh
                var ttKhac = FindElement(ttChung, "TTKhac") ?? FindElement(root, "TTKhac");
                if (ttKhac != null)
                {
                    foreach (var ttin in ttKhac.Elements().Where(e => e.Name.LocalName.Equals("TTin", StringComparison.OrdinalIgnoreCase)))
                    {
                        var ttruong = GetElementValue(ttin, "TTruong")?.Trim() ?? "";
                        var dlieu = GetElementValue(ttin, "DLieu")?.Trim() ?? "";

                        if (string.IsNullOrEmpty(dlieu)) continue;

                        if (ttruong.Contains("Mã số bí mật", StringComparison.OrdinalIgnoreCase) ||
                            ttruong.Contains("Mã tra cứu", StringComparison.OrdinalIgnoreCase) ||
                            ttruong.Contains("Mã bảo mật", StringComparison.OrdinalIgnoreCase) ||
                            ttruong.Contains("Key tra cứu", StringComparison.OrdinalIgnoreCase))
                        {
                            result.LookupCode = dlieu;
                        }
                        else if (ttruong.Contains("Biển số xe", StringComparison.OrdinalIgnoreCase) ||
                                 ttruong.Contains("BSX", StringComparison.OrdinalIgnoreCase) ||
                                 ttruong.Contains("Số xe", StringComparison.OrdinalIgnoreCase))
                        {
                            result.VehicleNumber = dlieu;
                        }
                        else if (ttruong.Contains("Ghi chú", StringComparison.OrdinalIgnoreCase) ||
                                 ttruong.Contains("Lý do", StringComparison.OrdinalIgnoreCase))
                        {
                            result.ReplacedOrAdjustedNote = dlieu;
                        }
                        else if (ttruong.Contains("Website", StringComparison.OrdinalIgnoreCase) ||
                                 ttruong.Contains("Tra cứu", StringComparison.OrdinalIgnoreCase))
                        {
                            if (dlieu.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                            {
                                result.LookupUrl = dlieu;
                            }
                        }
                    }
                }

                // Ngày ký số của người bán
                try
                {
                    var signingTimeNode = root.Descendants()
                        .FirstOrDefault(e => e.Name.LocalName.Equals("SigningTime", StringComparison.OrdinalIgnoreCase));
                    if (signingTimeNode != null && DateTime.TryParse(signingTimeNode.Value, out var st))
                    {
                        result.SignedDate = st;
                    }
                    else
                    {
                        result.SignedDate = result.IssueDate;
                    }
                }
                catch
                {
                    result.SignedDate = result.IssueDate;
                }

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
                result.VerificationResult = !string.IsNullOrWhiteSpace(mcqThue) ? "Đã cấp mã hoá đơn" : "Hóa đơn không mã";

                // Loại hóa đơn máy tính tiền (Ký tự thứ 4 của KHHDon là 'M')
                result.IsCashRegister = khHDon.Length >= 4 && char.ToUpperInvariant(khHDon[3]) == 'M';

                // 2. Thông tin Người Bán (NBan)
                var nBan = FindElement(ndHDon, "NBan") ?? FindElement(root, "NBan");
                if (nBan != null)
                {
                    result.SellerTaxCode = GetElementValue(nBan, "MST");
                    result.SellerName = GetElementValue(nBan, "Ten");
                    result.SellerAddress = GetElementValue(nBan, "DChi");
                    result.SellerWebsite = GetElementValue(nBan, "Website");
                }

                // 3. Thông tin Người Mua (NMua)
                var nMua = FindElement(ndHDon, "NMua") ?? FindElement(root, "NMua");
                if (nMua != null)
                {
                    result.BuyerTaxCode = GetElementValue(nMua, "MST");
                    result.BuyerCitizenId = GetElementValue(nMua, "CMND", "CCCD", "HChieu");
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
                        var discountRate = ParseDecimal(GetElementValue(hhdvu, "TLCKhau"), 0);
                        var discountAmount = ParseDecimal(GetElementValue(hhdvu, "STCKhau"), 0);

                        // Nếu XML chuẩn TCT không lưu TThue ở từng dòng HHDVu mà chỉ có thuế suất, tính tiền thuế dòng
                        if (taxAmount == 0 && taxRate > 0 && amountBeforeTax > 0)
                        {
                            taxAmount = Math.Round(amountBeforeTax * taxRate / 100m, 0);
                        }

                        var total = amountBeforeTax + taxAmount;
                        var rate = result.ExchangeRate > 0 ? result.ExchangeRate : 1;

                        var line = new ParsedInvoiceDetail
                        {
                            LineNumber = int.TryParse(GetElementValue(hhdvu, "STT"), out var n) ? n : stt++,
                            ItemCode = GetElementValue(hhdvu, "MHHDVu")?.Trim(),
                            ItemName = itemName,
                            Unit = unit,
                            Quantity = ParseDecimal(GetElementValue(hhdvu, "SLuong"), 1),
                            UnitPrice = ParseDecimal(GetElementValue(hhdvu, "DGia"), 0),
                            DiscountRate = discountRate,
                            DiscountAmount = discountAmount,
                            AmountBeforeTax = amountBeforeTax,
                            TaxRate = taxRate,
                            TaxAmount = taxAmount,
                            TotalAmount = total,
                            AmountBeforeTaxForeign = amountBeforeTax,
                            TaxAmountForeign = taxAmount,
                            TotalAmountForeign = total,
                            AmountBeforeTaxVnd = Math.Round(amountBeforeTax * rate, 0),
                            TaxAmountVnd = Math.Round(taxAmount * rate, 0),
                            TotalAmountVnd = Math.Round(total * rate, 0)
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
                    result.DiscountAmount = ParseDecimal(GetElementValue(tToan, "TTCKTMai", "STCKhau"), 0);
                    result.FeeAmount = ParseDecimal(GetElementValue(tToan, "TTChiPhi", "TTFee", "TTPhi"), 0);
                }

                // Nếu thẻ tổng chưa có, tự tính từ chi tiết
                if (result.TotalAmount == 0 && result.Details.Any())
                {
                    result.AmountBeforeTax = result.Details.Sum(d => d.AmountBeforeTax);
                    result.TaxAmount = result.Details.Sum(d => d.TaxAmount);
                    result.TotalAmount = result.AmountBeforeTax + result.TaxAmount;
                }

                var exRate = result.ExchangeRate > 0 ? result.ExchangeRate : 1;
                result.AmountBeforeTaxVnd = Math.Round(result.AmountBeforeTax * exRate, 0);
                result.TaxAmountVnd = Math.Round(result.TaxAmount * exRate, 0);
                result.TotalAmountVnd = Math.Round(result.TotalAmount * exRate, 0);

                // Validation quy tắc nghiệp vụ hóa đơn
                ValidateInvoiceData(result);

                // Đảm bảo LookupUrl và LookupCode luôn được thiết lập chính xác
                if (string.IsNullOrWhiteSpace(result.LookupUrl))
                {
                    result.LookupUrl = ResolveLookupUrl(null, result.SellerTaxCode, result.SellerWebsite, null);
                }
                if (string.IsNullOrWhiteSpace(result.LookupCode))
                {
                    result.LookupCode = !string.IsNullOrWhiteSpace(result.TaxAuthorityCode) ? result.TaxAuthorityCode : result.InvoiceNumber;
                }

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

        public static string ResolveLookupUrl(string? msttcgp, string? sellerTaxCode, string? sellerWebsite, string? rawLookupUrl)
        {
            if (!string.IsNullOrWhiteSpace(rawLookupUrl) && rawLookupUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                var cleanRaw = rawLookupUrl.Trim();
                if (cleanRaw.Contains("vinvoice.viettel.vn", StringComparison.OrdinalIgnoreCase) && cleanRaw.EndsWith("/tra-cuu-hoa-don", StringComparison.OrdinalIgnoreCase))
                {
                    return "https://vinvoice.viettel.vn/tracuu";
                }
                if (cleanRaw.Contains("meinvoice.vn", StringComparison.OrdinalIgnoreCase) && cleanRaw.Contains("tra-cuu-hoa-don", StringComparison.OrdinalIgnoreCase))
                {
                    return "https://meinvoice.vn/tra-cuu";
                }
                return cleanRaw;
            }

            var gp = (msttcgp ?? string.Empty).Trim();
            var stc = (sellerTaxCode ?? string.Empty).Trim();

            // Viettel Solutions / Viettel Telecom (Route: /tracuu)
            if (gp == "0100109106" || stc == "0100109106" || stc == "0308808576" || stc == "0301260925" || stc == "1001099064-002" || stc.StartsWith("0100109106") || gp.StartsWith("0100109106"))
                return "https://vinvoice.viettel.vn/tracuu";

            // MISA
            if (gp == "0101243150" || stc == "0101243150" || gp.StartsWith("0101243150") || stc.StartsWith("0101243150"))
                return "https://meinvoice.vn/tra-cuu";

            // VNPT
            if (gp == "0106869738" || stc == "0106869738" || gp == "0100684378" || stc == "0100684378")
                return "https://portaltool.vnpt-invoice.com.vn/";

            // BKAV
            if (gp == "0101360697" || stc == "0101360697")
                return "https://van.ehoadon.vn/";

            // FPT
            if (gp == "0104128565" || stc == "0104128565")
                return "https://portal.einvoice.fpt.com.vn";

            // CyberBill
            if (gp == "0109282176" || stc == "0109282176")
                return "https://tracuu.cyberbill.vn";

            // EasyInvoice (Softdreams)
            if (gp == "0401486901" || stc == "0401486901")
                return "https://tracuu.easyinvoice.vn/";

            // Thái Sơn E-Invoice
            if (gp == "0309478306" || stc == "0309478306")
                return "https://einvoice.vn/tra-cuu";

            // Mobifone
            if (gp == "0100686209" || stc == "0100686209")
                return "https://mobifoneinvoice.vn/tra-cuu";

            // VNPAY
            if (gp == "0102182292" || stc == "0102182292")
                return "https://vnpayinvoice.vn/tra-cuu";

            if (!string.IsNullOrWhiteSpace(sellerWebsite))
            {
                var web = sellerWebsite.Trim();
                if (web.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return web;
                return "https://" + web;
            }

            return "https://hoadondientu.gdt.gov.vn/";
        }
    }
}
