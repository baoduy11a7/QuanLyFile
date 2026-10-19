using ClosedXML.Excel;
using InvoiceManager.Models.Entities;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InvoiceManager.Services
{
    public class ExportService : IExportService
    {
        static ExportService()
        {
            QuestPDF.Settings.License = LicenseType.Community;
        }
        public Task<byte[]> ExportDetailedExcelAsync(List<Invoice> invoices, TaxAccount? taxAccount, string title)
        {
            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add("Chi tiết hàng hóa HĐ");
            ws.ShowGridLines = true;

            // 1. Header công ty & Báo cáo
            ws.Cell("A1").Value = taxAccount != null ? $"ĐƠN VỊ: {taxAccount.CompanyName.ToUpper()}" : "BẢNG KÊ CHI TIẾT HÀNG HÓA DỊCH VỤ HÓA ĐƠN ĐIỆN TỬ";
            ws.Cell("A1").Style.Font.Bold = true;
            ws.Cell("A1").Style.Font.FontSize = 11;
            ws.Range("A1:H1").Merge();

            ws.Cell("A2").Value = taxAccount != null ? $"Mã số thuế: {taxAccount.TaxCode} - Địa chỉ: {taxAccount.Address}" : "";
            ws.Cell("A2").Style.Font.Italic = true;
            ws.Range("A2:H2").Merge();

            ws.Cell("A4").Value = title.ToUpper();
            ws.Cell("A4").Style.Font.Bold = true;
            ws.Cell("A4").Style.Font.FontSize = 15;
            ws.Cell("A4").Style.Font.FontColor = XLColor.FromHtml("#1E3A8A");
            ws.Range("A4:P4").Merge().Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

            ws.Cell("A5").Value = $"Ngày xuất báo cáo: {DateTime.Now:dd/MM/yyyy HH:mm} - Tổng số hóa đơn: {invoices.Count:N0}";
            ws.Range("A5:P5").Merge().Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center).Font.SetItalic(true);

            // 2. Table Headers
            int startRow = 7;
            bool isBanRa = title.Contains("BÁN RA", StringComparison.OrdinalIgnoreCase)
                || (invoices.Any() && invoices.All(i => i.InvoiceType == "BanRa"));

            string partnerTaxCodeHeader = isBanRa ? "MST Người mua" : "MST Người bán";
            string partnerNameHeader = isBanRa ? "Tên người mua" : "Tên người bán";

            string[] headers = new[]
            {
                "STT", "Ký hiệu HĐ", "Số hóa đơn", "Ngày lập", partnerTaxCodeHeader, partnerNameHeader, 
                "STT Dòng", "Tên hàng hóa / Dịch vụ", "ĐVT", "Số lượng", "Đơn giá (VND)", 
                "Tiền chưa thuế (VND)", "Thuế suất (%)", "Tiền thuế GTGT (VND)", "Tổng cộng (VND)", "Trạng thái HĐ"
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

            // 3. Rows
            int currentRow = startRow + 1;
            int invoiceIndex = 1;

            foreach (var inv in invoices)
            {
                var details = inv.Details.OrderBy(d => d.LineNumber).ToList();
                if (!details.Any())
                {
                    // Hóa đơn không có dòng chi tiết
                    details.Add(new InvoiceDetail
                    {
                        LineNumber = 1,
                        ItemName = "Chi tiết tổng hợp theo hóa đơn",
                        Quantity = 1,
                        UnitPrice = inv.AmountBeforeTax,
                        AmountBeforeTax = inv.AmountBeforeTax,
                        TaxRate = inv.AmountBeforeTax > 0 ? Math.Round((inv.TaxAmount / inv.AmountBeforeTax) * 100, 0) : 0,
                        TaxAmount = inv.TaxAmount,
                        TotalAmount = inv.TotalAmount
                    });
                }

                foreach (var d in details)
                {
                    ws.Cell(currentRow, 1).Value = invoiceIndex;
                    ws.Cell(currentRow, 1).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                    ws.Cell(currentRow, 2).Value = inv.InvoiceSymbol;
                    ws.Cell(currentRow, 3).Value = inv.InvoiceNumber;
                    ws.Cell(currentRow, 4).Value = inv.IssueDate.ToString("dd/MM/yyyy");

                    string rowPartnerTaxCode = isBanRa || inv.InvoiceType == "BanRa"
                        ? (!string.IsNullOrEmpty(inv.BuyerTaxCode) ? inv.BuyerTaxCode : (inv.SellerTaxCode ?? ""))
                        : (!string.IsNullOrEmpty(inv.SellerTaxCode) ? inv.SellerTaxCode : (inv.BuyerTaxCode ?? ""));
                    string rowPartnerName = isBanRa || inv.InvoiceType == "BanRa"
                        ? (!string.IsNullOrEmpty(inv.BuyerName) ? inv.BuyerName : (inv.SellerName ?? ""))
                        : (!string.IsNullOrEmpty(inv.SellerName) ? inv.SellerName : (inv.BuyerName ?? ""));

                    ws.Cell(currentRow, 5).Value = rowPartnerTaxCode;
                    ws.Cell(currentRow, 6).Value = rowPartnerName;
                    ws.Cell(currentRow, 7).Value = d.LineNumber;
                    ws.Cell(currentRow, 8).Value = d.ItemName;
                    ws.Cell(currentRow, 9).Value = d.Unit ?? "";
                    
                    ws.Cell(currentRow, 10).Value = d.Quantity;
                    ws.Cell(currentRow, 10).Style.NumberFormat.Format = "#,##0.##";

                    ws.Cell(currentRow, 11).Value = d.UnitPrice;
                    ws.Cell(currentRow, 11).Style.NumberFormat.Format = "#,##0";

                    ws.Cell(currentRow, 12).Value = d.AmountBeforeTax;
                    ws.Cell(currentRow, 12).Style.NumberFormat.Format = "#,##0";

                    ws.Cell(currentRow, 13).Value = d.TaxRate == -1 ? "KCT" : $"{d.TaxRate}%";

                    ws.Cell(currentRow, 14).Value = d.TaxAmount;
                    ws.Cell(currentRow, 14).Style.NumberFormat.Format = "#,##0";

                    ws.Cell(currentRow, 15).Value = d.TotalAmount;
                    ws.Cell(currentRow, 15).Style.NumberFormat.Format = "#,##0";

                    ws.Cell(currentRow, 16).Value = inv.Status;

                    // Border cho từng ô
                    for (int c = 1; c <= headers.Length; c++)
                    {
                        ws.Cell(currentRow, c).Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin);
                        ws.Cell(currentRow, c).Style.Border.OutsideBorderColor = XLColor.FromHtml("#E5E7EB");
                    }

                    if (invoiceIndex % 2 == 0)
                    {
                        ws.Range(currentRow, 1, currentRow, headers.Length).Style.Fill.BackgroundColor = XLColor.FromHtml("#F9FAFB");
                    }

                    currentRow++;
                }

                invoiceIndex++;
            }

            // 4. Tổng cộng
            ws.Cell(currentRow, 1).Value = "TỔNG CỘNG";
            ws.Range(currentRow, 1, currentRow, 11).Merge().Style.Font.SetBold(true).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Right);
            
            ws.Cell(currentRow, 12).FormulaA1 = $"SUM(L{startRow + 1}:L{currentRow - 1})";
            ws.Cell(currentRow, 12).Style.Font.Bold = true;
            ws.Cell(currentRow, 12).Style.NumberFormat.Format = "#,##0";

            ws.Cell(currentRow, 14).FormulaA1 = $"SUM(N{startRow + 1}:N{currentRow - 1})";
            ws.Cell(currentRow, 14).Style.Font.Bold = true;
            ws.Cell(currentRow, 14).Style.NumberFormat.Format = "#,##0";

            ws.Cell(currentRow, 15).FormulaA1 = $"SUM(O{startRow + 1}:O{currentRow - 1})";
            ws.Cell(currentRow, 15).Style.Font.Bold = true;
            ws.Cell(currentRow, 15).Style.NumberFormat.Format = "#,##0";

            ws.Range(currentRow, 1, currentRow, headers.Length).Style.Fill.BackgroundColor = XLColor.FromHtml("#FEF3C7");
            ws.Range(currentRow, 1, currentRow, headers.Length).Style.Border.SetOutsideBorder(XLBorderStyleValues.Medium);

            ws.Columns().AdjustToContents();
            ws.Column(1).Width = 7;  // Thu gọn cột STT vừa vặn số thứ tự
            ws.Column(6).Width = Math.Max(35, ws.Column(6).Width + 3); // Cột Tên người bán tự động mở rộng theo tên dài nhất, không bị ngắt chữ
            ws.Column(8).Width = Math.Max(35, ws.Column(8).Width + 3); // Cột Tên hàng hóa tự động mở rộng theo tên dài nhất

            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            return Task.FromResult(stream.ToArray());
        }

        public Task<byte[]> ExportSummaryExcelAsync(List<Invoice> invoices, TaxAccount? taxAccount, string title)
        {
            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add("Bảng kê tổng hợp HĐ");
            ws.ShowGridLines = true;

            // 1. Header công ty & Báo cáo
            ws.Cell("A1").Value = taxAccount != null ? $"ĐƠN VỊ: {taxAccount.CompanyName.ToUpper()}" : "BẢNG KÊ TỔNG HỢP HÓA ĐƠN ĐIỆN TỬ";
            ws.Cell("A1").Style.Font.Bold = true;
            ws.Range("A1:F1").Merge();

            ws.Cell("A2").Value = taxAccount != null ? $"Mã số thuế: {taxAccount.TaxCode} - Địa chỉ: {taxAccount.Address}" : "";
            ws.Cell("A2").Style.Font.Italic = true;
            ws.Range("A2:F2").Merge();

            ws.Cell("A4").Value = title.ToUpper();
            ws.Cell("A4").Style.Font.Bold = true;
            ws.Cell("A4").Style.Font.FontSize = 15;
            ws.Cell("A4").Style.Font.FontColor = XLColor.FromHtml("#065F46");
            ws.Range("A4:L4").Merge().Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

            ws.Cell("A5").Value = $"Ngày xuất: {DateTime.Now:dd/MM/yyyy HH:mm} - Tổng số hóa đơn: {invoices.Count:N0}";
            ws.Range("A5:L5").Merge().Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center).Font.SetItalic(true);

            // 2. Table Headers
            int startRow = 7;
            bool isBanRa = title.Contains("BÁN RA", StringComparison.OrdinalIgnoreCase)
                || (invoices.Any() && invoices.All(i => i.InvoiceType == "BanRa"));

            string partnerTaxCodeHeader = isBanRa ? "MST Người mua" : "MST Người bán";
            string partnerNameHeader = isBanRa ? "Tên người mua" : "Tên người bán";

            string[] headers = new[]
            {
                "STT", "Ký hiệu", "Số HĐ", "Ngày lập", partnerTaxCodeHeader, partnerNameHeader, 
                "Tiền chưa thuế (VND)", "Tiền thuế GTGT (VND)", "Tổng tiền thanh toán (VND)", 
                "Có mã CQT", "Trạng thái", "Chứng từ kế toán"
            };

            for (int col = 0; col < headers.Length; col++)
            {
                var cell = ws.Cell(startRow, col + 1);
                cell.Value = headers[col];
                cell.Style.Font.Bold = true;
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#059669");
                cell.Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                cell.Style.Alignment.SetVertical(XLAlignmentVerticalValues.Center);
                cell.Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin);
            }
            ws.Row(startRow).Height = 28;

            // 3. Rows
            int currentRow = startRow + 1;
            int idx = 1;

            foreach (var inv in invoices)
            {
                ws.Cell(currentRow, 1).Value = idx;
                ws.Cell(currentRow, 1).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                ws.Cell(currentRow, 2).Value = inv.InvoiceSymbol;
                ws.Cell(currentRow, 3).Value = inv.InvoiceNumber;
                ws.Cell(currentRow, 4).Value = inv.IssueDate.ToString("dd/MM/yyyy");

                string rowPartnerTaxCode = isBanRa || inv.InvoiceType == "BanRa"
                    ? (!string.IsNullOrEmpty(inv.BuyerTaxCode) ? inv.BuyerTaxCode : (inv.SellerTaxCode ?? ""))
                    : (!string.IsNullOrEmpty(inv.SellerTaxCode) ? inv.SellerTaxCode : (inv.BuyerTaxCode ?? ""));
                string rowPartnerName = isBanRa || inv.InvoiceType == "BanRa"
                    ? (!string.IsNullOrEmpty(inv.BuyerName) ? inv.BuyerName : (inv.SellerName ?? ""))
                    : (!string.IsNullOrEmpty(inv.SellerName) ? inv.SellerName : (inv.BuyerName ?? ""));

                ws.Cell(currentRow, 5).Value = rowPartnerTaxCode;
                ws.Cell(currentRow, 6).Value = rowPartnerName;

                ws.Cell(currentRow, 7).Value = inv.AmountBeforeTax;
                ws.Cell(currentRow, 7).Style.NumberFormat.Format = "#,##0";

                ws.Cell(currentRow, 8).Value = inv.TaxAmount;
                ws.Cell(currentRow, 8).Style.NumberFormat.Format = "#,##0";

                ws.Cell(currentRow, 9).Value = inv.TotalAmount;
                ws.Cell(currentRow, 9).Style.NumberFormat.Format = "#,##0";

                ws.Cell(currentRow, 10).Value = inv.HasTaxCode ? "Có mã CQT" : "Không mã";
                ws.Cell(currentRow, 11).Value = inv.Status;
                ws.Cell(currentRow, 12).Value = inv.IsReconciled ? $"Đã vào sổ ({inv.ReconciledRefNo})" : "Chưa vào sổ";

                for (int c = 1; c <= headers.Length; c++)
                {
                    ws.Cell(currentRow, c).Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin);
                    ws.Cell(currentRow, c).Style.Border.OutsideBorderColor = XLColor.FromHtml("#E5E7EB");
                }

                if (idx % 2 == 0)
                {
                    ws.Range(currentRow, 1, currentRow, headers.Length).Style.Fill.BackgroundColor = XLColor.FromHtml("#F0FDF4");
                }

                currentRow++;
                idx++;
            }

            // Tổng cộng
            ws.Cell(currentRow, 1).Value = "TỔNG CỘNG";
            ws.Range(currentRow, 1, currentRow, 6).Merge().Style.Font.SetBold(true).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Right);

            ws.Cell(currentRow, 7).FormulaA1 = $"SUM(G{startRow + 1}:G{currentRow - 1})";
            ws.Cell(currentRow, 7).Style.Font.Bold = true;
            ws.Cell(currentRow, 7).Style.NumberFormat.Format = "#,##0";

            ws.Cell(currentRow, 8).FormulaA1 = $"SUM(H{startRow + 1}:H{currentRow - 1})";
            ws.Cell(currentRow, 8).Style.Font.Bold = true;
            ws.Cell(currentRow, 8).Style.NumberFormat.Format = "#,##0";

            ws.Cell(currentRow, 9).FormulaA1 = $"SUM(I{startRow + 1}:I{currentRow - 1})";
            ws.Cell(currentRow, 9).Style.Font.Bold = true;
            ws.Cell(currentRow, 9).Style.NumberFormat.Format = "#,##0";

            ws.Range(currentRow, 1, currentRow, headers.Length).Style.Fill.BackgroundColor = XLColor.FromHtml("#D1FAE5");
            ws.Range(currentRow, 1, currentRow, headers.Length).Style.Border.SetOutsideBorder(XLBorderStyleValues.Medium);

            ws.Columns().AdjustToContents();
            ws.Column(1).Width = 7; // Thu gọn cột STT vừa vặn số thứ tự
            ws.Column(6).Width = Math.Max(35, ws.Column(6).Width + 3); // Cột Tên người bán tự động mở rộng theo tên dài nhất, không bị ngắt chữ

            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            return Task.FromResult(stream.ToArray());
        }

        public Task<byte[]> ExportSmartProExcelAsync(List<Invoice> invoices, TaxAccount? taxAccount, string title)
        {
            using var workbook = new XLWorkbook();
            string myTaxCode = taxAccount?.TaxCode?.Trim() ?? "";

            // Phân loại danh sách hóa đơn: Chuẩn (OK) và Cần xem xét (Review)
            var okInvoices = new List<Invoice>();
            var reviewInvoices = new List<Invoice>();

            foreach (var inv in invoices.OrderBy(i => i.IssueDate).ThenBy(i => i.InvoiceNumber))
            {
                bool isBanRa = inv.InvoiceType == "BanRa";
                string sellerMst = (inv.SellerTaxCode ?? "").Trim();
                string buyerMst = (inv.BuyerTaxCode ?? "").Trim();

                bool isCancelled = inv.Status.Contains("hủy", StringComparison.OrdinalIgnoreCase);
                bool isReplaced = inv.Status.Contains("thay thế", StringComparison.OrdinalIgnoreCase);
                bool isAdjusted = inv.Status.Contains("điều chỉnh", StringComparison.OrdinalIgnoreCase);
                bool isMissingMst = isBanRa ? string.IsNullOrEmpty(buyerMst) : string.IsNullOrEmpty(sellerMst);
                bool isHighRisk = inv.RiskLevel == "HighRisk";

                if (isCancelled || isReplaced || isAdjusted || isMissingMst || isHighRisk)
                {
                    reviewInvoices.Add(inv);
                }
                else
                {
                    okInvoices.Add(inv);
                }
            }

            // Tạo 4 Sheets theo cấu trúc chuẩn Smart Pro (Hình 3 & Mẫu bố cục người dùng)
            var wsTongQuat = workbook.Worksheets.Add("HoaDon_TongQuat");
            var wsKtscOk = workbook.Worksheets.Add("Smart_KTSC_OK");
            var wsKtscReview = workbook.Worksheets.Add("Smart_KTSC_CAN_XEM_XET");

            bool isBanRaOverall = title.Contains("BÁN RA", StringComparison.OrdinalIgnoreCase)
                || (invoices.Any() && invoices.All(i => i.InvoiceType == "BanRa"));
            var wsBangKe = workbook.Worksheets.Add(isBanRaOverall ? "BangKe_BanRa" : "BangKe_MuaVao");

            // Màu sắc nhận diện các Tab sheet
            wsTongQuat.TabColor = XLColor.FromHtml("#F59E0B"); // Cam
            wsKtscOk.TabColor = XLColor.FromHtml("#06B6D4");    // Xanh ngọc Cyan (Active tab)
            wsKtscReview.TabColor = XLColor.FromHtml("#8B5CF6");// Tím
            wsBangKe.TabColor = XLColor.FromHtml("#10B981");    // Xanh lá

            // 1. Xây dựng Sheet Smart_KTSC_OK
            BuildSmartKtscSheet(
                wsKtscOk,
                okInvoices,
                taxAccount,
                "SỔ KẾ TOÁN CHI TIẾT - PHẦN MỀM SMART PRO (KTSC - HỢP LỆ)",
                XLColor.FromHtml("#0284C7"),
                false,
                myTaxCode);

            // 2. Xây dựng Sheet Smart_KTSC_CAN_XEM_XET
            BuildSmartKtscSheet(
                wsKtscReview,
                reviewInvoices,
                taxAccount,
                "SỔ KẾ TOÁN CHI TIẾT - DANH SÁCH CẦN XEM XÉT (KTSC - LƯU Ý)",
                XLColor.FromHtml("#7C3AED"),
                true,
                myTaxCode);

            // 3. Xây dựng Sheet HoaDon_TongQuat
            BuildTongQuatSheet(wsTongQuat, invoices, taxAccount, "BẢNG KÊ TỔNG QUÁT HÓA ĐƠN ĐIỆN TỬ");

            // 4. Xây dựng Sheet BangKe_BanRa / BangKe_MuaVao
            BuildBangKeGtgtSheet(
                wsBangKe,
                invoices,
                taxAccount,
                isBanRaOverall ? "BẢNG KÊ HÓA ĐƠN CHỨNG TỪ HÀNG HÓA DỊCH VỤ BÁN RA" : "BẢNG KÊ HÓA ĐƠN CHỨNG TỪ HÀNG HÓA DỊCH VỤ MUA VÀO",
                isBanRaOverall);

            using var outStream = new MemoryStream();
            workbook.SaveAs(outStream);
            return Task.FromResult(outStream.ToArray());
        }

        private void BuildSmartKtscSheet(
            IXLWorksheet ws,
            List<Invoice> targetInvoices,
            TaxAccount? taxAccount,
            string sheetTitle,
            XLColor themeColor,
            bool isReviewSheet,
            string myTaxCode)
        {
            ws.ShowGridLines = true;

            // --- 1. HEADER CÔNG TY (Góc trên bên trái) ---
            ws.Cell("A1").Value = taxAccount != null ? $"ĐƠN VỊ: {taxAccount.CompanyName.ToUpper()}" : "ĐƠN VỊ: CÔNG TY QUẢN LÝ TÀI CHÍNH KẾ TOÁN";
            ws.Cell("A1").Style.Font.Bold = true;
            ws.Cell("A1").Style.Font.FontSize = 10;
            ws.Range("A1:H1").Merge();

            ws.Cell("A2").Value = taxAccount != null ? $"MÃ SỐ THUẾ: {taxAccount.TaxCode}  -  ĐỊA CHỈ: {taxAccount.Address}" : "MÃ SỐ THUẾ: HỆ THỐNG KẾ TOÁN DOANH NGHIỆP";
            ws.Cell("A2").Style.Font.Italic = true;
            ws.Cell("A2").Style.Font.FontSize = 9;
            ws.Range("A2:H2").Merge();

            // --- 2. TIÊU ĐỀ BÁO CÁO (Chính giữa, chữ xanh dương đậm) ---
            ws.Cell("A4").Value = sheetTitle.ToUpper();
            ws.Cell("A4").Style.Font.Bold = true;
            ws.Cell("A4").Style.Font.FontSize = 14;
            ws.Cell("A4").Style.Font.FontColor = themeColor;
            ws.Range("A4:U4").Merge().Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

            var fromDate = targetInvoices.Any() ? targetInvoices.Min(i => i.IssueDate) : DateTime.Now;
            var toDate = targetInvoices.Any() ? targetInvoices.Max(i => i.IssueDate) : DateTime.Now;
            ws.Cell("A5").Value = $"Kỳ hạch toán: Từ ngày {fromDate:dd/MM/yyyy} đến ngày {toDate:dd/MM/yyyy}  |  Tổng số chứng từ: {targetInvoices.Count:N0}";
            ws.Range("A5:U5").Merge().Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center).Font.SetItalic(true).Font.SetFontSize(9);

            // --- 3. TIÊU ĐỀ CỘT BẢNG (Dòng 7) ---
            int startRow = 7;
            string[] ktscHeaders = new[]
            {
                "NIBOT_GHICHU", "LCT", "SR_HD", "SOCT", "NGAYCT", "SO_HD", "NGAY_HD",
                "DIENGIAI", "TKNO", "MADTPNNO", "TKCO", "MADTPNCO", "MADMNO", "MADMCO",
                "DVT", "SOLUONG", "DONGIA", "THANHTIEN", "THUESUAT", "TIENTHUE", "TONGTIEN"
            };

            for (int col = 0; col < ktscHeaders.Length; col++)
            {
                var cell = ws.Cell(startRow, col + 1);
                cell.Value = ktscHeaders[col];
                cell.Style.Font.Bold = true;
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Fill.BackgroundColor = themeColor;
                cell.Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                cell.Style.Alignment.SetVertical(XLAlignmentVerticalValues.Center);
                cell.Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin);
                cell.Style.Border.OutsideBorderColor = XLColor.FromHtml("#CBD5E1");
            }

            // Khối 3 cột đối soát/bổ trợ bên phải (Cột W, X, Y - cách cột V trắng)
            ws.Column(22).Width = 3; // Cột V ngăn cách
            string[] auxHeaders = new[] { "LOAI_HD", "CQT_CODE", "TRANG_THAI" };
            for (int col = 0; col < auxHeaders.Length; col++)
            {
                var cell = ws.Cell(startRow, 23 + col);
                cell.Value = auxHeaders[col];
                cell.Style.Font.Bold = true;
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E293B"); // Dark Slate
                cell.Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                cell.Style.Alignment.SetVertical(XLAlignmentVerticalValues.Center);
                cell.Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin);
                cell.Style.Border.OutsideBorderColor = XLColor.FromHtml("#CBD5E1");
            }
            ws.Row(startRow).Height = 26;

            // --- 4. CÁC DÒNG DỮ LIỆU CHỨNG TỪ (Bắt đầu từ dòng 8) ---
            int curRow = startRow + 1;
            int dataStartRow = curRow;

            foreach (var inv in targetInvoices)
            {
                bool isBanRa = inv.InvoiceType == "BanRa";
                string lctDefault = isBanRa ? "HDBR" : "HDMV";
                string cleanSymbol = (inv.InvoiceSymbol ?? "").Trim();
                string cleanNumber = (inv.InvoiceNumber ?? "").Trim();
                string soHdShort = cleanNumber.TrimStart('0');
                if (string.IsNullOrEmpty(soHdShort)) soHdShort = "1";

                string srHd = $"{cleanSymbol}{soHdShort}";
                string ngayHd = inv.IssueDate.ToString("dd/MM/yyyy");

                string sellerMst = (inv.SellerTaxCode ?? "").Trim();
                string buyerMst = (inv.BuyerTaxCode ?? "").Trim();
                string partnerName = isBanRa ? (inv.BuyerName ?? "") : (inv.SellerName ?? "");
                if (string.IsNullOrEmpty(partnerName)) partnerName = isBanRa ? "Khách hàng vãng lai" : "Nhà cung cấp";

                bool isCancelled = inv.Status.Contains("hủy", StringComparison.OrdinalIgnoreCase);
                bool isReplaced = inv.Status.Contains("thay thế", StringComparison.OrdinalIgnoreCase);
                bool isAdjusted = inv.Status.Contains("điều chỉnh", StringComparison.OrdinalIgnoreCase);

                string notePrefix = isReviewSheet
                    ? (isCancelled ? "HĐ_ĐÃ_HỦY" : (isReplaced ? "HĐ_THAY_THẾ" : (isAdjusted ? "HĐ_ĐIỀU_CHỈNH" : "CẦN_KIỂM_TRA")))
                    : "NIBOT_OK";

                var details = inv.Details.OrderBy(d => d.LineNumber).ToList();
                if (!details.Any())
                {
                    details.Add(new InvoiceDetail
                    {
                        LineNumber = 1,
                        ItemCode = GenerateSmartSlug(partnerName),
                        ItemName = $"Bán hàng hóa/dịch vụ theo HĐ số {soHdShort}",
                        Unit = "Lần",
                        Quantity = 1,
                        UnitPrice = inv.AmountBeforeTax,
                        AmountBeforeTax = inv.AmountBeforeTax,
                        TaxRate = inv.AmountBeforeTax > 0 ? Math.Round((inv.TaxAmount / inv.AmountBeforeTax) * 100, 0) : 0,
                        TaxAmount = inv.TaxAmount,
                        TotalAmount = inv.TotalAmount
                    });
                }

                // 4.1. Điền từng dòng hàng hóa
                foreach (var item in details)
                {
                    string madm = !string.IsNullOrEmpty(item.ItemCode)
                        ? item.ItemCode.Trim().ToUpper()
                        : GenerateSmartSlug(item.ItemName);

                    string dienGiai = isBanRa
                        ? $"Bán hàng cho {partnerName.ToUpper()}"
                        : $"Mua hàng của {partnerName.ToUpper()}";

                    if (!string.IsNullOrEmpty(item.ItemName) && !item.ItemName.StartsWith("Bán hàng"))
                    {
                        dienGiai += $" - {item.ItemName}";
                    }

                    string tkNo = isBanRa ? "131" : "1561";
                    string tkCo = isBanRa ? "5111" : "331";

                    ws.Cell(curRow, 1).Value = notePrefix;
                    ws.Cell(curRow, 2).Value = lctDefault;
                    ws.Cell(curRow, 3).Value = srHd;
                    ws.Cell(curRow, 4).Value = soHdShort;
                    ws.Cell(curRow, 5).Value = ngayHd;
                    if (int.TryParse(soHdShort, out var num)) ws.Cell(curRow, 6).Value = num;
                    else ws.Cell(curRow, 6).Value = soHdShort;
                    ws.Cell(curRow, 7).Value = ngayHd;
                    ws.Cell(curRow, 8).Value = dienGiai;

                    ws.Cell(curRow, 9).Value = tkNo;
                    ws.Cell(curRow, 10).Value = isBanRa ? (string.IsNullOrEmpty(buyerMst) ? "KHACHLE" : buyerMst) : myTaxCode;
                    ws.Cell(curRow, 11).Value = tkCo;
                    ws.Cell(curRow, 12).Value = isBanRa ? myTaxCode : sellerMst;
                    ws.Cell(curRow, 13).Value = "";
                    ws.Cell(curRow, 14).Value = madm;

                    ws.Cell(curRow, 15).Value = item.Unit ?? "";
                    ws.Cell(curRow, 16).Value = item.Quantity;
                    ws.Cell(curRow, 17).Value = item.UnitPrice;
                    ws.Cell(curRow, 18).Value = item.AmountBeforeTax;
                    ws.Cell(curRow, 19).Value = item.TaxRate;
                    ws.Cell(curRow, 20).Value = item.TaxAmount;
                    ws.Cell(curRow, 21).Value = item.TotalAmount;

                    // Khối 3 cột phụ trợ bên phải (Chữ màu xanh dương)
                    ws.Cell(curRow, 23).Value = isBanRa ? "BÁN RA" : "MUA VÀO";
                    ws.Cell(curRow, 24).Value = !string.IsNullOrEmpty(inv.TaxAuthorityCode) ? inv.TaxAuthorityCode : (inv.HasTaxCode ? "CÓ MÃ" : "KHÔNG MÃ");
                    ws.Cell(curRow, 25).Value = inv.Status;

                    for (int c = 23; c <= 25; c++)
                    {
                        ws.Cell(curRow, c).Style.Font.FontColor = XLColor.FromHtml("#1D4ED8"); // Blue text
                        ws.Cell(curRow, c).Style.Font.Bold = true;
                        ws.Cell(curRow, c).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                    }

                    // Định dạng số & viền
                    ws.Cell(curRow, 16).Style.NumberFormat.Format = "#,##0.00";
                    ws.Cell(curRow, 17).Style.NumberFormat.Format = "#,##0";
                    ws.Cell(curRow, 18).Style.NumberFormat.Format = "#,##0";
                    ws.Cell(curRow, 20).Style.NumberFormat.Format = "#,##0";
                    ws.Cell(curRow, 21).Style.NumberFormat.Format = "#,##0";

                    for (int c = 1; c <= 21; c++)
                    {
                        ws.Cell(curRow, c).Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin);
                        ws.Cell(curRow, c).Style.Border.OutsideBorderColor = XLColor.FromHtml("#E2E8F0");
                    }
                    for (int c = 23; c <= 25; c++)
                    {
                        ws.Cell(curRow, c).Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin);
                        ws.Cell(curRow, c).Style.Border.OutsideBorderColor = XLColor.FromHtml("#E2E8F0");
                    }

                    // Nếu hóa đơn có trạng thái cảnh báo, highlight nhẹ dòng
                    if (isCancelled || isReplaced || isAdjusted)
                    {
                        ws.Cell(curRow, 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#FEE2E2");
                        ws.Cell(curRow, 1).Style.Font.FontColor = XLColor.FromHtml("#B91C1C");
                        ws.Cell(curRow, 1).Style.Font.Bold = true;
                    }

                    curRow++;
                }

                // 4.2. Điền dòng thuế GTGT riêng (nếu thuế > 0)
                if (inv.TaxAmount > 0)
                {
                    string tkNoThue = isBanRa ? "131" : "1331";
                    string tkCoThue = isBanRa ? "33311" : "331";

                    ws.Cell(curRow, 1).Value = notePrefix.Replace("OK", "THUE");
                    ws.Cell(curRow, 2).Value = isBanRa ? "HDTAN" : lctDefault;
                    ws.Cell(curRow, 3).Value = srHd;
                    ws.Cell(curRow, 4).Value = soHdShort;
                    ws.Cell(curRow, 5).Value = ngayHd;
                    if (int.TryParse(soHdShort, out var numT)) ws.Cell(curRow, 6).Value = numT;
                    else ws.Cell(curRow, 6).Value = soHdShort;
                    ws.Cell(curRow, 7).Value = ngayHd;
                    ws.Cell(curRow, 8).Value = $"Thuế GTGT HĐ số {cleanNumber} - {cleanSymbol}";

                    ws.Cell(curRow, 9).Value = tkNoThue;
                    ws.Cell(curRow, 10).Value = isBanRa ? (string.IsNullOrEmpty(buyerMst) ? "KHACHLE" : buyerMst) : myTaxCode;
                    ws.Cell(curRow, 11).Value = tkCoThue;
                    ws.Cell(curRow, 12).Value = isBanRa ? myTaxCode : sellerMst;
                    ws.Cell(curRow, 13).Value = "";
                    ws.Cell(curRow, 14).Value = "";

                    ws.Cell(curRow, 15).Value = "";
                    ws.Cell(curRow, 16).Value = 0;
                    ws.Cell(curRow, 17).Value = 0;
                    ws.Cell(curRow, 18).Value = 0;
                    ws.Cell(curRow, 19).Value = inv.AmountBeforeTax > 0 ? Math.Round((inv.TaxAmount / inv.AmountBeforeTax) * 100, 0) : 0;
                    ws.Cell(curRow, 20).Value = inv.TaxAmount;
                    ws.Cell(curRow, 21).Value = inv.TaxAmount;

                    // Khối 3 cột phụ trợ bên phải cho dòng thuế
                    ws.Cell(curRow, 23).Value = isBanRa ? "THUẾ BÁN RA" : "THUẾ MUA VÀO";
                    ws.Cell(curRow, 24).Value = !string.IsNullOrEmpty(inv.TaxAuthorityCode) ? inv.TaxAuthorityCode : "THUẾ GTGT";
                    ws.Cell(curRow, 25).Value = inv.Status;

                    for (int c = 23; c <= 25; c++)
                    {
                        ws.Cell(curRow, c).Style.Font.FontColor = XLColor.FromHtml("#1D4ED8");
                        ws.Cell(curRow, c).Style.Font.Bold = true;
                        ws.Cell(curRow, c).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                    }

                    // HIGHLIGHT MÀU ĐỎ NỔI BẬT DÒNG THUẾ GTGT (Y hệt các ô màu đỏ xuất hiện định kỳ trong ảnh mẫu!)
                    ws.Cell(curRow, 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#FEE2E2");
                    ws.Cell(curRow, 1).Style.Font.FontColor = XLColor.FromHtml("#B91C1C");
                    ws.Cell(curRow, 2).Style.Fill.BackgroundColor = XLColor.FromHtml("#EF4444"); // Đỏ tươi
                    ws.Cell(curRow, 2).Style.Font.FontColor = XLColor.White;
                    ws.Cell(curRow, 2).Style.Font.Bold = true;

                    ws.Cell(curRow, 20).Style.NumberFormat.Format = "#,##0";
                    ws.Cell(curRow, 21).Style.NumberFormat.Format = "#,##0";

                    for (int c = 1; c <= 21; c++)
                    {
                        ws.Cell(curRow, c).Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin);
                        ws.Cell(curRow, c).Style.Border.OutsideBorderColor = XLColor.FromHtml("#E2E8F0");
                    }
                    for (int c = 23; c <= 25; c++)
                    {
                        ws.Cell(curRow, c).Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin);
                        ws.Cell(curRow, c).Style.Border.OutsideBorderColor = XLColor.FromHtml("#E2E8F0");
                    }

                    curRow++;
                }
            }

            int lastDataRow = Math.Max(dataStartRow, curRow - 1);

            // --- 5. DÒNG TỔNG CỘNG CHÍNH ---
            ws.Cell(curRow, 1).Value = "TỔNG CỘNG PHÁT SINH SỔ KTSC:";
            ws.Range(curRow, 1, curRow, 17).Merge().Style.Font.SetBold(true).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Right);

            ws.Cell(curRow, 18).FormulaA1 = lastDataRow >= dataStartRow ? $"SUM(R{dataStartRow}:R{lastDataRow})" : "0";
            ws.Cell(curRow, 20).FormulaA1 = lastDataRow >= dataStartRow ? $"SUM(T{dataStartRow}:T{lastDataRow})" : "0";
            ws.Cell(curRow, 21).FormulaA1 = lastDataRow >= dataStartRow ? $"SUM(U{dataStartRow}:U{lastDataRow})" : "0";

            ws.Cell(curRow, 18).Style.Font.Bold = true;
            ws.Cell(curRow, 20).Style.Font.Bold = true;
            ws.Cell(curRow, 21).Style.Font.Bold = true;

            ws.Cell(curRow, 18).Style.NumberFormat.Format = "#,##0";
            ws.Cell(curRow, 20).Style.NumberFormat.Format = "#,##0";
            ws.Cell(curRow, 21).Style.NumberFormat.Format = "#,##0";

            ws.Range(curRow, 1, curRow, 21).Style.Fill.BackgroundColor = XLColor.FromHtml("#E2E8F0");
            ws.Range(curRow, 1, curRow, 21).Style.Border.SetTopBorder(XLBorderStyleValues.Thin);
            ws.Range(curRow, 1, curRow, 21).Style.Border.SetBottomBorder(XLBorderStyleValues.Double);

            int totalRow = curRow;
            curRow++;

            // --- 6. KHỐI TỔNG KẾT MÀU VÀNG NỔI BẬT (#FFFF00) Ở CHÂN BẢNG (Như trong ảnh mẫu) ---
            void FormatYellowRow(int r)
            {
                for (int c = 1; c <= 21; c++)
                {
                    ws.Cell(r, c).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFFF00"); // Yellow
                    ws.Cell(r, c).Style.Font.Bold = true;
                    ws.Cell(r, c).Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin);
                    ws.Cell(r, c).Style.Border.OutsideBorderColor = XLColor.Black;
                }
                for (int c = 23; c <= 25; c++)
                {
                    ws.Cell(r, c).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFFF00");
                    ws.Cell(r, c).Style.Font.Bold = true;
                    ws.Cell(r, c).Style.Font.FontColor = XLColor.FromHtml("#1D4ED8");
                    ws.Cell(r, c).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                    ws.Cell(r, c).Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin);
                    ws.Cell(r, c).Style.Border.OutsideBorderColor = XLColor.Black;
                }
            }

            // Dòng Vàng 1: Tổng Doanh số tiền hàng chưa thuế
            ws.Range(curRow, 1, curRow, 17).Merge().Value = "TỔNG CỘNG DOANH SỐ TIỀN HÀNG CHƯA THUẾ (THANHTIEN):";
            ws.Range(curRow, 1, curRow, 17).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Right);
            ws.Cell(curRow, 18).FormulaA1 = $"=R{totalRow}";
            ws.Cell(curRow, 18).Style.NumberFormat.Format = "#,##0";
            ws.Range(curRow, 19, curRow, 21).Merge();
            ws.Range(curRow, 23, curRow, 25).Merge().Value = "[KHỚP TIỀN HÀNG]";
            FormatYellowRow(curRow);
            curRow++;

            // Dòng Vàng 2: Tổng Tiền thuế GTGT
            ws.Range(curRow, 1, curRow, 17).Merge().Value = "TỔNG CỘNG TIỀN THUẾ GIÁ TRỊ GIA TĂNG (TIENTHUE):";
            ws.Range(curRow, 1, curRow, 17).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Right);
            ws.Range(curRow, 18, curRow, 19).Merge();
            ws.Cell(curRow, 20).FormulaA1 = $"=T{totalRow}";
            ws.Cell(curRow, 20).Style.NumberFormat.Format = "#,##0";
            ws.Cell(curRow, 21).Value = "";
            ws.Range(curRow, 23, curRow, 25).Merge().Value = "[KHỚP TIỀN THUẾ]";
            FormatYellowRow(curRow);
            curRow++;

            // Dòng Vàng 3: Tổng Tiền thanh toán toàn bộ
            ws.Range(curRow, 1, curRow, 17).Merge().Value = "TỔNG CỘNG TIỀN THANH TOÁN TOÀN BỘ HÓA ĐƠN (TONGTIEN):";
            ws.Range(curRow, 1, curRow, 17).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Right);
            ws.Range(curRow, 18, curRow, 20).Merge();
            ws.Cell(curRow, 21).FormulaA1 = $"=U{totalRow}";
            ws.Cell(curRow, 21).Style.NumberFormat.Format = "#,##0";
            ws.Range(curRow, 23, curRow, 25).Merge().Value = "[KHỚP TỔNG THANH TOÁN]";
            FormatYellowRow(curRow);
            curRow++;

            // Dòng Vàng 4: Trong đó Doanh số thuế suất 0% / KCT
            decimal sum0 = targetInvoices.Where(i => i.TaxAmount == 0).Sum(i => i.AmountBeforeTax);
            ws.Range(curRow, 1, curRow, 17).Merge().Value = "TRONG ĐÓ: DOANH SỐ HÀNG HÓA THUẾ SUẤT 0% & KCT:";
            ws.Range(curRow, 1, curRow, 17).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Right);
            ws.Cell(curRow, 18).Value = sum0;
            ws.Cell(curRow, 18).Style.NumberFormat.Format = "#,##0";
            ws.Range(curRow, 19, curRow, 21).Merge();
            ws.Range(curRow, 23, curRow, 25).Merge().Value = "[THUẾ SUẤT 0% / KCT]";
            FormatYellowRow(curRow);
            curRow++;

            // Dòng Vàng 5: Trong đó Doanh số thuế suất 5%
            decimal sum5 = targetInvoices.Where(i => i.TaxAmount > 0 && Math.Round((i.TaxAmount / (i.AmountBeforeTax > 0 ? i.AmountBeforeTax : 1)) * 100, 0) == 5).Sum(i => i.AmountBeforeTax);
            ws.Range(curRow, 1, curRow, 17).Merge().Value = "TRONG ĐÓ: DOANH SỐ HÀNG HÓA THUẾ SUẤT 5%:";
            ws.Range(curRow, 1, curRow, 17).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Right);
            ws.Cell(curRow, 18).Value = sum5;
            ws.Cell(curRow, 18).Style.NumberFormat.Format = "#,##0";
            ws.Range(curRow, 19, curRow, 21).Merge();
            ws.Range(curRow, 23, curRow, 25).Merge().Value = "[THUẾ SUẤT 5%]";
            FormatYellowRow(curRow);
            curRow++;

            // Dòng Vàng 6: Trong đó Doanh số thuế suất 8% & 10%
            decimal sum10 = targetInvoices.Where(i => i.TaxAmount > 0 && Math.Round((i.TaxAmount / (i.AmountBeforeTax > 0 ? i.AmountBeforeTax : 1)) * 100, 0) >= 8).Sum(i => i.AmountBeforeTax);
            ws.Range(curRow, 1, curRow, 17).Merge().Value = "TRONG ĐÓ: DOANH SỐ HÀNG HÓA THUẾ SUẤT 8% & 10%:";
            ws.Range(curRow, 1, curRow, 17).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Right);
            ws.Cell(curRow, 18).Value = sum10;
            ws.Cell(curRow, 18).Style.NumberFormat.Format = "#,##0";
            ws.Range(curRow, 19, curRow, 21).Merge();
            ws.Range(curRow, 23, curRow, 25).Merge().Value = "[THUẾ SUẤT 8% / 10%]";
            FormatYellowRow(curRow);
            curRow++;

            // --- 7. PHẦN CHỮ KÝ (Signatures) ---
            curRow += 2;
            ws.Range(curRow, 15, curRow, 21).Merge().Value = $"Ngày {DateTime.Now:dd} tháng {DateTime.Now:MM} năm {DateTime.Now:yyyy}";
            ws.Range(curRow, 15, curRow, 21).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center).Font.SetItalic(true);

            curRow++;
            ws.Range(curRow, 2, curRow, 5).Merge().Value = "NGƯỜI LẬP BIỂU";
            ws.Range(curRow, 8, curRow, 12).Merge().Value = "KẾ TOÁN TRƯỞNG";
            ws.Range(curRow, 15, curRow, 21).Merge().Value = "GIÁM ĐỐC / ĐẠI DIỆN PHÁP LUẬT";

            ws.Range(curRow, 2, curRow, 5).Style.Font.SetBold(true).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            ws.Range(curRow, 8, curRow, 12).Style.Font.SetBold(true).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            ws.Range(curRow, 15, curRow, 21).Style.Font.SetBold(true).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

            curRow++;
            ws.Range(curRow, 2, curRow, 5).Merge().Value = "(Ký, ghi rõ họ tên)";
            ws.Range(curRow, 8, curRow, 12).Merge().Value = "(Ký, ghi rõ họ tên)";
            ws.Range(curRow, 15, curRow, 21).Merge().Value = "(Ký, đóng dấu, ghi rõ họ tên)";

            ws.Range(curRow, 2, curRow, 5).Style.Font.SetItalic(true).Font.SetFontSize(9).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            ws.Range(curRow, 8, curRow, 12).Style.Font.SetItalic(true).Font.SetFontSize(9).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            ws.Range(curRow, 15, curRow, 21).Style.Font.SetItalic(true).Font.SetFontSize(9).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

            // Tối ưu độ rộng cột
            ws.Columns().AdjustToContents();
            ws.Column(1).Width = Math.Max(15, ws.Column(1).Width);
            ws.Column(8).Width = Math.Max(34, ws.Column(8).Width); // Diễn giải
            ws.Column(22).Width = 3; // Cột V ngăn cách
        }

        private void BuildTongQuatSheet(
            IXLWorksheet ws,
            List<Invoice> invoices,
            TaxAccount? taxAccount,
            string sheetTitle)
        {
            ws.ShowGridLines = true;

            ws.Cell("A1").Value = taxAccount != null ? $"ĐƠN VỊ: {taxAccount.CompanyName.ToUpper()}" : "ĐƠN VỊ: DOANH NGHIỆP NỘP THUẾ";
            ws.Cell("A1").Style.Font.Bold = true;
            ws.Cell("A1").Style.Font.FontSize = 10;
            ws.Range("A1:H1").Merge();

            ws.Cell("A2").Value = taxAccount != null ? $"MÃ SỐ THUẾ: {taxAccount.TaxCode}  -  ĐỊA CHỈ: {taxAccount.Address}" : "";
            ws.Cell("A2").Style.Font.Italic = true;
            ws.Cell("A2").Style.Font.FontSize = 9;
            ws.Range("A2:H2").Merge();

            ws.Cell("A4").Value = sheetTitle.ToUpper();
            ws.Cell("A4").Style.Font.Bold = true;
            ws.Cell("A4").Style.Font.FontSize = 14;
            ws.Cell("A4").Style.Font.FontColor = XLColor.FromHtml("#D97706"); // Amber
            ws.Range("A4:O4").Merge().Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

            var fromDate = invoices.Any() ? invoices.Min(i => i.IssueDate) : DateTime.Now;
            var toDate = invoices.Any() ? invoices.Max(i => i.IssueDate) : DateTime.Now;
            ws.Cell("A5").Value = $"Kỳ báo cáo: Từ ngày {fromDate:dd/MM/yyyy} đến ngày {toDate:dd/MM/yyyy}  |  Tổng số hóa đơn: {invoices.Count:N0}";
            ws.Range("A5:O5").Merge().Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center).Font.SetItalic(true).Font.SetFontSize(9);

            int startRow = 7;
            string[] tqHeaders = new[]
            {
                "STT", "Loại HĐ", "Ký hiệu HĐ", "Số HĐ", "Ngày lập",
                "MST Người bán", "Tên Người bán", "MST Người mua", "Tên Người mua",
                "Tiền chưa thuế (VND)", "Tiền thuế GTGT (VND)", "Tổng cộng (VND)",
                "Số mặt hàng", "Trạng thái", "Mã CQT"
            };

            for (int col = 0; col < tqHeaders.Length; col++)
            {
                var cell = ws.Cell(startRow, col + 1);
                cell.Value = tqHeaders[col];
                cell.Style.Font.Bold = true;
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#D97706");
                cell.Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                cell.Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin);
            }
            ws.Row(startRow).Height = 26;

            int rowTq = startRow + 1;
            int sttTq = 1;
            foreach (var inv in invoices.OrderBy(i => i.IssueDate))
            {
                ws.Cell(rowTq, 1).Value = sttTq++;
                ws.Cell(rowTq, 1).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                ws.Cell(rowTq, 2).Value = inv.InvoiceType == "BanRa" ? "BÁN RA" : "MUA VÀO";
                ws.Cell(rowTq, 3).Value = inv.InvoiceSymbol;
                ws.Cell(rowTq, 4).Value = inv.InvoiceNumber;
                ws.Cell(rowTq, 5).Value = inv.IssueDate.ToString("dd/MM/yyyy");
                ws.Cell(rowTq, 6).Value = inv.SellerTaxCode;
                ws.Cell(rowTq, 7).Value = inv.SellerName;
                ws.Cell(rowTq, 8).Value = inv.BuyerTaxCode ?? "";
                ws.Cell(rowTq, 9).Value = inv.BuyerName ?? "";
                ws.Cell(rowTq, 10).Value = inv.AmountBeforeTax;
                ws.Cell(rowTq, 11).Value = inv.TaxAmount;
                ws.Cell(rowTq, 12).Value = inv.TotalAmount;
                ws.Cell(rowTq, 13).Value = inv.Details.Count;
                ws.Cell(rowTq, 14).Value = inv.Status;
                ws.Cell(rowTq, 15).Value = inv.TaxAuthorityCode ?? "";

                ws.Cell(rowTq, 10).Style.NumberFormat.Format = "#,##0";
                ws.Cell(rowTq, 11).Style.NumberFormat.Format = "#,##0";
                ws.Cell(rowTq, 12).Style.NumberFormat.Format = "#,##0";

                for (int c = 1; c <= 15; c++)
                {
                    ws.Cell(rowTq, c).Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin);
                    ws.Cell(rowTq, c).Style.Border.OutsideBorderColor = XLColor.FromHtml("#E2E8F0");
                }

                rowTq++;
            }

            int lastDataRow = rowTq - 1;
            ws.Cell(rowTq, 1).Value = "TỔNG CỘNG:";
            ws.Range(rowTq, 1, rowTq, 9).Merge().Style.Font.SetBold(true).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Right);
            ws.Cell(rowTq, 10).FormulaA1 = lastDataRow >= startRow + 1 ? $"SUM(J{startRow + 1}:J{lastDataRow})" : "0";
            ws.Cell(rowTq, 11).FormulaA1 = lastDataRow >= startRow + 1 ? $"SUM(K{startRow + 1}:K{lastDataRow})" : "0";
            ws.Cell(rowTq, 12).FormulaA1 = lastDataRow >= startRow + 1 ? $"SUM(L{startRow + 1}:L{lastDataRow})" : "0";

            ws.Cell(rowTq, 10).Style.Font.Bold = true;
            ws.Cell(rowTq, 11).Style.Font.Bold = true;
            ws.Cell(rowTq, 12).Style.Font.Bold = true;

            ws.Cell(rowTq, 10).Style.NumberFormat.Format = "#,##0";
            ws.Cell(rowTq, 11).Style.NumberFormat.Format = "#,##0";
            ws.Cell(rowTq, 12).Style.NumberFormat.Format = "#,##0";

            ws.Range(rowTq, 1, rowTq, 15).Style.Fill.BackgroundColor = XLColor.FromHtml("#FEF3C7");
            ws.Range(rowTq, 1, rowTq, 15).Style.Border.SetTopBorder(XLBorderStyleValues.Thin);
            ws.Range(rowTq, 1, rowTq, 15).Style.Border.SetBottomBorder(XLBorderStyleValues.Double);

            ws.Columns().AdjustToContents();
            ws.Column(1).Width = 7; // Thu gọn cột STT vừa vặn số thứ tự
        }

        private void BuildBangKeGtgtSheet(
            IXLWorksheet ws,
            List<Invoice> invoices,
            TaxAccount? taxAccount,
            string sheetTitle,
            bool isBanRaOverall)
        {
            ws.ShowGridLines = true;

            ws.Cell("A1").Value = taxAccount != null ? $"ĐƠN VỊ: {taxAccount.CompanyName.ToUpper()}" : "ĐƠN VỊ: DOANH NGHIỆP NỘP THUẾ";
            ws.Cell("A1").Style.Font.Bold = true;
            ws.Cell("A1").Style.Font.FontSize = 10;
            ws.Range("A1:F1").Merge();

            ws.Cell("A2").Value = taxAccount != null ? $"MÃ SỐ THUẾ: {taxAccount.TaxCode}  -  ĐỊA CHỈ: {taxAccount.Address}" : "";
            ws.Cell("A2").Style.Font.Italic = true;
            ws.Cell("A2").Style.Font.FontSize = 9;
            ws.Range("A2:F2").Merge();

            ws.Cell("A4").Value = sheetTitle.ToUpper();
            ws.Cell("A4").Style.Font.Bold = true;
            ws.Cell("A4").Style.Font.FontSize = 14;
            ws.Cell("A4").Style.Font.FontColor = XLColor.FromHtml("#059669"); // Emerald
            ws.Range("A4:J4").Merge().Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

            var fromDate = invoices.Any() ? invoices.Min(i => i.IssueDate) : DateTime.Now;
            var toDate = invoices.Any() ? invoices.Max(i => i.IssueDate) : DateTime.Now;
            ws.Cell("A5").Value = $"Kỳ kê khai: Từ ngày {fromDate:dd/MM/yyyy} đến ngày {toDate:dd/MM/yyyy}  |  Tổng số hóa đơn: {invoices.Count:N0}";
            ws.Range("A5:J5").Merge().Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center).Font.SetItalic(true).Font.SetFontSize(9);

            int startRow = 7;
            string[] bkHeaders = new[]
            {
                "STT", "Ký hiệu HĐ", "Số HĐ", "Ngày lập", "Tên đơn vị đối tác", "Mã số thuế",
                "Doanh số chưa thuế", "Thuế GTGT", "Thuế suất", "Ghi chú"
            };

            for (int col = 0; col < bkHeaders.Length; col++)
            {
                var cell = ws.Cell(startRow, col + 1);
                cell.Value = bkHeaders[col];
                cell.Style.Font.Bold = true;
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#059669");
                cell.Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                cell.Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin);
            }
            ws.Row(startRow).Height = 26;

            int rowBk = startRow + 1;
            int sttBk = 1;
            foreach (var inv in invoices.OrderBy(i => i.IssueDate))
            {
                bool isBanRa = inv.InvoiceType == "BanRa";
                ws.Cell(rowBk, 1).Value = sttBk++;
                ws.Cell(rowBk, 1).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                ws.Cell(rowBk, 2).Value = inv.InvoiceSymbol;
                ws.Cell(rowBk, 3).Value = inv.InvoiceNumber;
                ws.Cell(rowBk, 4).Value = inv.IssueDate.ToString("dd/MM/yyyy");
                ws.Cell(rowBk, 5).Value = isBanRa ? (inv.BuyerName ?? "") : inv.SellerName;
                ws.Cell(rowBk, 6).Value = isBanRa ? (inv.BuyerTaxCode ?? "") : inv.SellerTaxCode;
                ws.Cell(rowBk, 7).Value = inv.AmountBeforeTax;
                ws.Cell(rowBk, 8).Value = inv.TaxAmount;
                ws.Cell(rowBk, 9).Value = inv.AmountBeforeTax > 0 ? $"{Math.Round((inv.TaxAmount / inv.AmountBeforeTax) * 100, 0)}%" : "0%";
                ws.Cell(rowBk, 10).Value = inv.Status;

                ws.Cell(rowBk, 7).Style.NumberFormat.Format = "#,##0";
                ws.Cell(rowBk, 8).Style.NumberFormat.Format = "#,##0";

                for (int c = 1; c <= 10; c++)
                {
                    ws.Cell(rowBk, c).Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin);
                    ws.Cell(rowBk, c).Style.Border.OutsideBorderColor = XLColor.FromHtml("#E2E8F0");
                }

                rowBk++;
            }

            int lastDataRow = rowBk - 1;
            ws.Cell(rowBk, 1).Value = "TỔNG CỘNG:";
            ws.Range(rowBk, 1, rowBk, 6).Merge().Style.Font.SetBold(true).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Right);
            ws.Cell(rowBk, 7).FormulaA1 = lastDataRow >= startRow + 1 ? $"SUM(G{startRow + 1}:G{lastDataRow})" : "0";
            ws.Cell(rowBk, 8).FormulaA1 = lastDataRow >= startRow + 1 ? $"SUM(H{startRow + 1}:H{lastDataRow})" : "0";

            ws.Cell(rowBk, 7).Style.Font.Bold = true;
            ws.Cell(rowBk, 8).Style.Font.Bold = true;

            ws.Cell(rowBk, 7).Style.NumberFormat.Format = "#,##0";
            ws.Cell(rowBk, 8).Style.NumberFormat.Format = "#,##0";

            ws.Range(rowBk, 1, rowBk, 10).Style.Fill.BackgroundColor = XLColor.FromHtml("#D1FAE5");
            ws.Range(rowBk, 1, rowBk, 10).Style.Border.SetTopBorder(XLBorderStyleValues.Thin);
            ws.Range(rowBk, 1, rowBk, 10).Style.Border.SetBottomBorder(XLBorderStyleValues.Double);

            ws.Columns().AdjustToContents();
            ws.Column(1).Width = 7; // Thu gọn cột STT vừa vặn số thứ tự
        }

        private static string GenerateSmartSlug(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "HANGHOA";
            var normalized = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (var c in normalized)
            {
                var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
                if (cat != System.Globalization.CharUnicodeInfo.GetUnicodeCategory('a') &&
                    char.IsLetterOrDigit(c))
                {
                    sb.Append(c);
                }
                else if (char.IsLetterOrDigit(c))
                {
                    sb.Append(c);
                }
            }
            string result = sb.ToString().ToUpper();
            return result.Length > 12 ? result.Substring(0, 12) : (result.Length == 0 ? "HANGHOA" : result);
        }

        public async Task<byte[]> ExportInvoicesZipAsync(List<Invoice> invoices)
        {
            using var zipStream = new MemoryStream();
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, true))
            {
                var entryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var inv in invoices)
                {
                    // 1. Gói file HTML thể hiện chuẩn
                    var htmlContent = GenerateInvoiceHtml(inv);
                    var htmlEntryName = $"HD_{inv.InvoiceSymbol}_{inv.InvoiceNumber}.html";
                    if (entryNames.Contains(htmlEntryName))
                    {
                        htmlEntryName = $"HD_{inv.InvoiceSymbol}_{inv.InvoiceNumber}_{inv.Id}.html";
                    }
                    entryNames.Add(htmlEntryName);

                    var htmlEntry = archive.CreateEntry(htmlEntryName);
                    using (var entryStream = htmlEntry.Open())
                    using (var writer = new StreamWriter(entryStream, Encoding.UTF8))
                    {
                        await writer.WriteAsync(htmlContent);
                    }

                    // 2. Gói file XML gốc nếu có
                    if (!string.IsNullOrEmpty(inv.RawXmlPath) && File.Exists(inv.RawXmlPath))
                    {
                        var xmlEntryName = $"XML_Goc_{inv.InvoiceSymbol}_{inv.InvoiceNumber}.xml";
                        if (entryNames.Contains(xmlEntryName))
                        {
                            xmlEntryName = $"XML_Goc_{inv.InvoiceSymbol}_{inv.InvoiceNumber}_{inv.Id}.xml";
                        }
                        entryNames.Add(xmlEntryName);

                        var xmlEntry = archive.CreateEntry(xmlEntryName);
                        using var fs = File.OpenRead(inv.RawXmlPath);
                        using var entryStream = xmlEntry.Open();
                        await fs.CopyToAsync(entryStream);
                    }
                }
            }

            return zipStream.ToArray();
        }

        public async Task<byte[]> ExportInvoicesPdfZipAsync(List<Invoice> invoices)
        {
            using var zipStream = new MemoryStream();
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, true))
            {
                var entryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var inv in invoices)
                {
                    try
                    {
                        byte[] pdfBytes = await GenerateInvoicePdfAsync(inv);
                        var entryName = $"HD_{inv.InvoiceSymbol}_{inv.InvoiceNumber}_{inv.SellerTaxCode}.pdf";
                        if (entryNames.Contains(entryName))
                        {
                            entryName = $"HD_{inv.InvoiceSymbol}_{inv.InvoiceNumber}_{inv.SellerTaxCode}_{inv.Id}.pdf";
                        }
                        entryNames.Add(entryName);

                        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                        using var entryStream = entry.Open();
                        await entryStream.WriteAsync(pdfBytes, 0, pdfBytes.Length);
                    }
                    catch
                    {
                        // Tiếp tục với các hóa đơn khác nếu có lỗi đơn lẻ
                    }
                }
            }

            return zipStream.ToArray();
        }

        public async Task<byte[]> GenerateInvoicePdfAsync(Invoice invoice)
        {
            // 1. Nếu đã có file PDF gốc tải lên hoặc tải từ cổng thuế và là file PDF hợp lệ
            if (!string.IsNullOrEmpty(invoice.RawPdfPath) && File.Exists(invoice.RawPdfPath))
            {
                try
                {
                    var existingBytes = await File.ReadAllBytesAsync(invoice.RawPdfPath);
                    if (existingBytes.Length > 4 && existingBytes[0] == 0x25 && existingBytes[1] == 0x50 && existingBytes[2] == 0x44 && existingBytes[3] == 0x46)
                    {
                        return existingBytes;
                    }
                }
                catch { }
            }

            // 2. Kiểm tra thư mục cache đã sinh PDF trước đó và đảm bảo là file PDF hợp lệ (bắt đầu bằng %PDF)
            var cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "App_Data", "GeneratedPdfs");
            Directory.CreateDirectory(cacheDir);
            var cachedPdfPath = Path.Combine(cacheDir, $"HD_{invoice.Id}_{invoice.InvoiceSymbol}_{invoice.InvoiceNumber}.pdf");
            if (File.Exists(cachedPdfPath))
            {
                try
                {
                    var cachedBytes = await File.ReadAllBytesAsync(cachedPdfPath);
                    if (cachedBytes.Length > 4 && cachedBytes[0] == 0x25 && cachedBytes[1] == 0x50 && cachedBytes[2] == 0x44 && cachedBytes[3] == 0x46)
                    {
                        return cachedBytes;
                    }
                    else
                    {
                        // File cache cũ lỗi hoặc chỉ là text HTML cũ, xóa đi
                        File.Delete(cachedPdfPath);
                    }
                }
                catch { }
            }

            // 3. Sử dụng QuestPDF để sinh tài liệu PDF vector chuẩn Nghị định 123 (chạy mượt trên Windows, Linux và Docker Render)
            try
            {
                var pdfBytes = GenerateNativePdf(invoice);
                if (pdfBytes != null && pdfBytes.Length > 0)
                {
                    try { await File.WriteAllBytesAsync(cachedPdfPath, pdfBytes); } catch { }
                    return pdfBytes;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"QuestPDF render exception: {ex.Message}");
            }

            // 4. Fallback phương án dự phòng dùng Edge Headless nếu có
            string edgePath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
            if (!File.Exists(edgePath))
            {
                edgePath = @"C:\Program Files\Microsoft\Edge\Application\msedge.exe";
            }

            var tempDir = Path.Combine(Path.GetTempPath(), "InvoicePdfGen");
            Directory.CreateDirectory(tempDir);
            var tempGuid = Guid.NewGuid().ToString("N");
            var tempHtml = Path.Combine(tempDir, $"{tempGuid}.html");
            var tempPdf = Path.Combine(tempDir, $"{tempGuid}.pdf");

            try
            {
                if (File.Exists(edgePath))
                {
                    var html = GenerateInvoiceHtml(invoice);
                    await File.WriteAllTextAsync(tempHtml, html, Encoding.UTF8);

                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = edgePath,
                        Arguments = $"--headless=new --disable-gpu --no-pdf-header-footer --print-to-pdf=\"{tempPdf}\" \"{tempHtml}\"",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                    };

                    using var proc = System.Diagnostics.Process.Start(psi);
                    if (proc != null)
                    {
                        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
                        await proc.WaitForExitAsync(cts.Token);

                        if (File.Exists(tempPdf) && new FileInfo(tempPdf).Length > 0)
                        {
                            var pdfBytes = await File.ReadAllBytesAsync(tempPdf);
                            try { File.Copy(tempPdf, cachedPdfPath, true); } catch { }
                            return pdfBytes;
                        }
                    }
                }
            }
            catch { }
            finally
            {
                try { if (File.Exists(tempHtml)) File.Delete(tempHtml); } catch { }
                try { if (File.Exists(tempPdf)) File.Delete(tempPdf); } catch { }
            }

            // Fallback cuối cùng: Gọi lại QuestPDF
            return GenerateNativePdf(invoice);
        }

        public byte[] GenerateNativePdf(Invoice invoice)
        {
            var document = Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(25, Unit.Point);
                    page.PageColor(Colors.White);
                    page.DefaultTextStyle(x => x.FontSize(9).FontColor(Colors.Grey.Darken3));

                    page.Header().Element(c => ComposePdfHeader(c, invoice));
                    page.Content().Element(c => ComposePdfContent(c, invoice));
                    page.Footer().Element(c => ComposePdfFooter(c, invoice));
                });
            });

            return document.GeneratePdf();
        }

        private void ComposePdfHeader(IContainer container, Invoice invoice)
        {
            container.Column(col =>
            {
                col.Item().Row(row =>
                {
                    row.RelativeItem(3).Column(left =>
                    {
                        left.Item().Text(string.IsNullOrWhiteSpace(invoice.SellerName) ? "ĐƠN VỊ BÁN HÀNG" : invoice.SellerName.ToUpper())
                            .Bold().FontSize(10).FontColor(Colors.Blue.Darken3);
                        left.Item().Text($"Mã số thuế: {invoice.SellerTaxCode}").Bold().FontSize(9);
                        if (!string.IsNullOrWhiteSpace(invoice.SellerAddress))
                        {
                            left.Item().Text($"Địa chỉ: {invoice.SellerAddress}").FontSize(8).FontColor(Colors.Grey.Darken2);
                        }
                    });

                    row.RelativeItem(2).AlignRight().Column(right =>
                    {
                        right.Item().Text($"Mẫu số - Ký hiệu: {invoice.InvoiceSymbol}").Bold().FontSize(8.5f);
                        right.Item().Text($"Số: {invoice.InvoiceNumber}").Bold().FontSize(12).FontColor(Colors.Red.Darken2);
                        if (!string.IsNullOrWhiteSpace(invoice.TaxAuthorityCode))
                        {
                            right.Item().Text($"Mã CQT: {invoice.TaxAuthorityCode}").FontSize(7.5f).FontColor(Colors.Grey.Darken1);
                        }
                    });
                });

                col.Item().PaddingTop(10).AlignCenter().Column(center =>
                {
                    center.Item().AlignCenter().Text("HÓA ĐƠN GIÁ TRỊ GIA TĂNG").Bold().FontSize(14).FontColor(Colors.Blue.Darken4);
                    center.Item().AlignCenter().Text("(Bản thể hiện của hóa đơn điện tử theo Nghị định 123/2020/NĐ-CP & Thông tư 78/2021/TT-BTC)").Italic().FontSize(8).FontColor(Colors.Grey.Darken1);
                    center.Item().AlignCenter().Text($"Ngày {invoice.IssueDate:dd} tháng {invoice.IssueDate:MM} năm {invoice.IssueDate:yyyy}").FontSize(8.5f);
                });

                col.Item().PaddingTop(8).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
            });
        }

        private void ComposePdfContent(IContainer container, Invoice invoice)
        {
            container.PaddingTop(8).Column(col =>
            {
                // Thông tin người mua
                col.Item().Border(0.8f).BorderColor(Colors.Grey.Lighten2).Background(Colors.Grey.Lighten4).Padding(6).Column(buyer =>
                {
                    buyer.Item().Row(r =>
                    {
                        r.AutoItem().Text("Đơn vị mua hàng: ").Bold();
                        r.RelativeItem().Text(string.IsNullOrWhiteSpace(invoice.BuyerName) ? "Người mua không lấy hóa đơn / Khách lẻ" : invoice.BuyerName);
                    });
                    buyer.Item().Row(r =>
                    {
                        r.AutoItem().Text("Mã số thuế: ").Bold();
                        r.RelativeItem().Text(string.IsNullOrWhiteSpace(invoice.BuyerTaxCode) ? "---" : invoice.BuyerTaxCode);
                    });
                    if (!string.IsNullOrWhiteSpace(invoice.BuyerAddress))
                    {
                        buyer.Item().Row(r =>
                        {
                            r.AutoItem().Text("Địa chỉ: ").Bold();
                            r.RelativeItem().Text(invoice.BuyerAddress);
                        });
                    }
                });

                col.Item().Height(8);

                // Bảng danh sách hàng hóa
                col.Item().Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.ConstantColumn(24);   // STT
                        columns.RelativeColumn(3.8f); // Tên hàng hóa
                        columns.RelativeColumn(1.0f); // ĐVT
                        columns.RelativeColumn(1.1f); // Số lượng
                        columns.RelativeColumn(1.6f); // Đơn giá
                        columns.RelativeColumn(1.8f); // Thành tiền
                        columns.RelativeColumn(0.9f); // Thuế
                    });

                    table.Header(header =>
                    {
                        header.Cell().Element(HeaderStyle).AlignCenter().Text("STT");
                        header.Cell().Element(HeaderStyle).Text("Tên hàng hóa, dịch vụ");
                        header.Cell().Element(HeaderStyle).AlignCenter().Text("ĐVT");
                        header.Cell().Element(HeaderStyle).AlignRight().Text("Số lượng");
                        header.Cell().Element(HeaderStyle).AlignRight().Text("Đơn giá");
                        header.Cell().Element(HeaderStyle).AlignRight().Text("Thành tiền");
                        header.Cell().Element(HeaderStyle).AlignCenter().Text("Thuế");

                        static IContainer HeaderStyle(IContainer c) =>
                            c.DefaultTextStyle(x => x.Bold().FontSize(8).FontColor(Colors.White))
                             .Background(Colors.Blue.Darken2)
                             .Border(0.5f).BorderColor(Colors.Blue.Darken3)
                             .PaddingVertical(4).PaddingHorizontal(3);
                    });

                    var details = invoice.Details.OrderBy(d => d.LineNumber).ToList();
                    if (!details.Any())
                    {
                        details.Add(new InvoiceDetail
                        {
                            LineNumber = 1,
                            ItemName = "Hàng hóa, dịch vụ theo hóa đơn điện tử",
                            Quantity = 1,
                            UnitPrice = invoice.AmountBeforeTax,
                            AmountBeforeTax = invoice.AmountBeforeTax,
                            TaxRate = invoice.AmountBeforeTax > 0 ? Math.Round((invoice.TaxAmount / invoice.AmountBeforeTax) * 100, 0) : 0,
                            TaxAmount = invoice.TaxAmount,
                            TotalAmount = invoice.TotalAmount
                        });
                    }

                    for (int i = 0; i < details.Count; i++)
                    {
                        var item = details[i];
                        var bg = i % 2 == 1 ? Colors.Grey.Lighten5 : Colors.White;

                        table.Cell().Element(c => CellStyle(c, bg)).AlignCenter().Text(item.LineNumber.ToString());
                        table.Cell().Element(c => CellStyle(c, bg)).Text(item.ItemName);
                        table.Cell().Element(c => CellStyle(c, bg)).AlignCenter().Text(item.Unit ?? "");
                        table.Cell().Element(c => CellStyle(c, bg)).AlignRight().Text(item.Quantity.ToString("#,##0.##"));
                        table.Cell().Element(c => CellStyle(c, bg)).AlignRight().Text(item.UnitPrice.ToString("#,##0"));
                        table.Cell().Element(c => CellStyle(c, bg)).AlignRight().Text(item.AmountBeforeTax.ToString("#,##0"));
                        table.Cell().Element(c => CellStyle(c, bg)).AlignCenter().Text(item.TaxRate >= 0 ? $"{item.TaxRate:0}%" : "KCT");
                    }

                    static IContainer CellStyle(IContainer c, string backgroundColor) =>
                        c.Background(backgroundColor)
                         .Border(0.5f).BorderColor(Colors.Grey.Lighten2)
                         .PaddingVertical(3).PaddingHorizontal(3)
                         .DefaultTextStyle(x => x.FontSize(7.5f));
                });

                // Tổng cộng tiền
                col.Item().PaddingTop(6).AlignRight().Width(260).Column(totals =>
                {
                    totals.Item().Row(r =>
                    {
                        r.RelativeItem().Text("Cộng tiền hàng:");
                        r.AutoItem().Text($"{invoice.AmountBeforeTax:#,##0} đ").Bold();
                    });
                    totals.Item().Row(r =>
                    {
                        r.RelativeItem().Text("Tiền thuế GTGT:");
                        r.AutoItem().Text($"{invoice.TaxAmount:#,##0} đ").Bold();
                    });
                    totals.Item().PaddingTop(2).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten1);
                    totals.Item().Row(r =>
                    {
                        r.RelativeItem().Text("Tổng thanh toán:").Bold();
                        r.AutoItem().Text($"{invoice.TotalAmount:#,##0} đ").Bold().FontSize(10).FontColor(Colors.Red.Darken2);
                    });
                });

                // Tiền viết bằng chữ
                col.Item().PaddingTop(4).Text($"Số tiền viết bằng chữ: {NumberToVietnameseWords((long)Math.Round(invoice.TotalAmount))}").Italic().FontSize(8.5f);

                // Chữ ký điện tử
                col.Item().PaddingTop(12).Row(sig =>
                {
                    sig.RelativeItem().AlignCenter().Column(b =>
                    {
                        b.Item().Text("NGƯỜI MUA HÀNG").Bold().FontSize(8.5f);
                        b.Item().Text("(Ký, ghi rõ họ tên)").FontSize(7.5f).Italic();
                    });

                    sig.RelativeItem().AlignCenter().Column(s =>
                    {
                        s.Item().Text("NGƯỜI BÁN HÀNG").Bold().FontSize(8.5f);
                        s.Item().Text("(Chữ ký điện tử, chữ ký số)").FontSize(7.5f).Italic();
                        s.Item().PaddingTop(5).Border(1.2f).BorderColor(Colors.Green.Darken1).Background(Colors.Green.Lighten5).Padding(4).Column(stamp =>
                        {
                            stamp.Item().AlignCenter().Text("✓ ĐÃ KÝ ĐIỆN TỬ HỢP LỆ").Bold().FontSize(7.5f).FontColor(Colors.Green.Darken2);
                            stamp.Item().AlignCenter().Text(invoice.SellerName).FontSize(6.8f).FontColor(Colors.Green.Darken3);
                            stamp.Item().AlignCenter().Text($"Ngày ký: {invoice.IssueDate:dd/MM/yyyy}").FontSize(6.5f).FontColor(Colors.Grey.Darken1);
                        });
                    });
                });
            });
        }

        private void ComposePdfFooter(IContainer container, Invoice invoice)
        {
            container.Column(col =>
            {
                col.Item().LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten2);
                col.Item().PaddingTop(3).Row(row =>
                {
                    row.RelativeItem().Text($"Tra cứu: {invoice.InvoiceSymbol} - Số {invoice.InvoiceNumber} | Cổng HĐĐT: hoadondientu.gdt.gov.vn").FontSize(7).FontColor(Colors.Grey.Darken1);
                    row.AutoItem().Text(x =>
                    {
                        x.Span("Trang ");
                        x.CurrentPageNumber();
                        x.Span(" / ");
                        x.TotalPages();
                    });
                });
            });
        }

        private static readonly string[] Digits = { "không", "một", "hai", "ba", "bốn", "năm", "sáu", "bảy", "tám", "chín" };

        public static string NumberToVietnameseWords(long number)
        {
            if (number == 0) return "Không đồng.";
            if (number < 0) return "Âm " + NumberToVietnameseWords(-number);

            string result = "";
            string[] units = { "", " nghìn", " triệu", " tỷ", " nghìn tỷ", " triệu tỷ" };
            int unitIndex = 0;

            while (number > 0)
            {
                long group = number % 1000;
                if (group > 0)
                {
                    string groupText = ReadThreeDigits((int)group, number >= 1000);
                    result = groupText + units[unitIndex] + (string.IsNullOrEmpty(result) ? "" : " ") + result;
                }
                number /= 1000;
                unitIndex++;
            }

            result = result.Trim();
            if (!string.IsNullOrEmpty(result))
            {
                result = char.ToUpper(result[0]) + result.Substring(1) + " đồng.";
            }
            return result;
        }

        private static string ReadThreeDigits(int number, bool hasHigherGroups)
        {
            int hundreds = number / 100;
            int tens = (number % 100) / 10;
            int ones = number % 10;

            var sb = new StringBuilder();

            if (hundreds > 0 || hasHigherGroups)
            {
                sb.Append(Digits[hundreds]).Append(" trăm");
            }

            if (tens > 1)
            {
                if (sb.Length > 0) sb.Append(" ");
                sb.Append(Digits[tens]).Append(" mươi");
                if (ones == 1) sb.Append(" mốt");
                else if (ones == 4) sb.Append(" tư");
                else if (ones == 5) sb.Append(" lăm");
                else if (ones > 0) sb.Append(" ").Append(Digits[ones]);
            }
            else if (tens == 1)
            {
                if (sb.Length > 0) sb.Append(" ");
                sb.Append("mười");
                if (ones == 1) sb.Append(" một");
                else if (ones == 5) sb.Append(" lăm");
                else if (ones > 0) sb.Append(" ").Append(Digits[ones]);
            }
            else if (tens == 0 && ones > 0)
            {
                if (sb.Length > 0) sb.Append(" lẻ ");
                sb.Append(Digits[ones]);
            }

            return sb.ToString().Trim();
        }

        public string GenerateInvoiceHtml(Invoice invoice)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang='vi'>");
            sb.AppendLine("<head>");
            sb.AppendLine("<meta charset='utf-8'/>");
            sb.AppendLine($"<title>Hóa đơn điện tử - {invoice.InvoiceSymbol}-{invoice.InvoiceNumber}</title>");
            sb.AppendLine("<style>");
            sb.AppendLine("body { font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif; margin: 20px; color: #1f2937; background: #f3f4f6; }");
            sb.AppendLine(".invoice-box { max-width: 850px; margin: auto; padding: 30px; border: 1px solid #cbd5e1; background: #ffffff; box-shadow: 0 4px 6px -1px rgba(0,0,0,0.1); border-radius: 8px; }");
            sb.AppendLine(".header-title { text-align: center; margin-bottom: 20px; }");
            sb.AppendLine(".header-title h2 { margin: 0; color: #1e3a8a; text-transform: uppercase; font-size: 22px; letter-spacing: 0.5px; }");
            sb.AppendLine(".header-title p { margin: 5px 0; font-size: 14px; color: #4b5563; }");
            sb.AppendLine(".inv-meta { display: flex; justify-content: space-between; margin-bottom: 20px; font-size: 13px; border-bottom: 2px dashed #e2e8f0; padding-bottom: 15px; }");
            sb.AppendLine(".party-box { margin-bottom: 15px; font-size: 14px; line-height: 1.6; }");
            sb.AppendLine(".party-box strong { color: #0f172a; }");
            sb.AppendLine("table.items { width: 100%; border-collapse: collapse; margin-top: 15px; font-size: 13px; }");
            sb.AppendLine("table.items th, table.items td { border: 1px solid #cbd5e1; padding: 8px 10px; }");
            sb.AppendLine("table.items th { background: #f1f5f9; color: #1e293b; font-weight: 600; text-align: center; }");
            sb.AppendLine(".text-right { text-align: right; }");
            sb.AppendLine(".text-center { text-align: center; }");
            sb.AppendLine(".totals { margin-top: 15px; width: 100%; font-size: 14px; }");
            sb.AppendLine(".totals td { padding: 6px 10px; }");
            sb.AppendLine(".sign-box { display: flex; justify-content: space-between; margin-top: 40px; padding: 0 40px; text-align: center; }");
            sb.AppendLine(".stamp { margin-top: 15px; border: 2px solid #16a34a; color: #16a34a; padding: 10px; border-radius: 6px; font-size: 12px; font-weight: bold; display: inline-block; background: #f0fdf4; }");
            sb.AppendLine("</style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");

            sb.AppendLine("<div class='invoice-box'>");
            sb.AppendLine("<div class='header-title'>");
            sb.AppendLine("<h2>HÓA ĐƠN GIÁ TRỊ GIA TĂNG</h2>");
            sb.AppendLine("<p>(Bản thể hiện của hóa đơn điện tử theo Nghị định 123/2020/NĐ-CP & Thông tư 78/2021/TT-BTC)</p>");
            sb.AppendLine($"<p>Ngày lập: <strong>{invoice.IssueDate:dd/MM/yyyy}</strong></p>");
            sb.AppendLine("</div>");

            sb.AppendLine("<div class='inv-meta'>");
            sb.AppendLine($"<div><strong>Ký hiệu:</strong> <span style='color:#dc2626; font-weight:bold;'>{invoice.InvoiceSymbol}</span><br><strong>Số hóa đơn:</strong> <span style='color:#dc2626; font-weight:bold; font-size:16px;'>{invoice.InvoiceNumber}</span></div>");
            sb.AppendLine($"<div class='text-right'><strong>Phân loại:</strong> {(invoice.HasTaxCode ? "HĐ có mã của Cơ quan Thuế" : "HĐ không mã")}<br><strong>Mã CQT:</strong> <span style='font-family:monospace;'>{(string.IsNullOrEmpty(invoice.TaxAuthorityCode) ? "Chưa cấp / Không áp dụng" : invoice.TaxAuthorityCode)}</span></div>");
            sb.AppendLine("</div>");

            sb.AppendLine("<div class='party-box'>");
            sb.AppendLine($"<div><strong>Đơn vị bán hàng:</strong> {invoice.SellerName}</div>");
            sb.AppendLine($"<div><strong>Mã số thuế:</strong> <span style='letter-spacing:1px; font-weight:bold;'>{invoice.SellerTaxCode}</span></div>");
            sb.AppendLine($"<div><strong>Địa chỉ:</strong> {invoice.SellerAddress}</div>");
            sb.AppendLine("</div>");

            sb.AppendLine("<div class='party-box' style='border-top:1px solid #e2e8f0; padding-top:10px;'>");
            sb.AppendLine($"<div><strong>Họ tên người mua / Đơn vị:</strong> {invoice.BuyerName}</div>");
            sb.AppendLine($"<div><strong>Mã số thuế:</strong> <span style='letter-spacing:1px; font-weight:bold;'>{invoice.BuyerTaxCode}</span></div>");
            sb.AppendLine($"<div><strong>Địa chỉ:</strong> {invoice.BuyerAddress}</div>");
            sb.AppendLine("</div>");

            sb.AppendLine("<table class='items'>");
            sb.AppendLine("<thead><tr><th style='width:50px;'>STT</th><th>Tên hàng hóa, dịch vụ</th><th style='width:60px;'>ĐVT</th><th style='width:70px;'>Số lượng</th><th style='width:100px;'>Đơn giá</th><th style='width:120px;'>Thành tiền</th></tr></thead>");
            sb.AppendLine("<tbody>");

            var details = invoice.Details.OrderBy(d => d.LineNumber).ToList();
            if (details.Any())
            {
                foreach (var d in details)
                {
                    sb.AppendLine($"<tr><td class='text-center'>{d.LineNumber}</td><td>{d.ItemName}</td><td class='text-center'>{d.Unit}</td><td class='text-right'>{d.Quantity:N0}</td><td class='text-right'>{d.UnitPrice:N0} đ</td><td class='text-right'>{d.AmountBeforeTax:N0} đ</td></tr>");
                }
            }
            else
            {
                sb.AppendLine($"<tr><td class='text-center'>1</td><td>Hóa đơn tổng hợp dịch vụ</td><td class='text-center'>Gói</td><td class='text-right'>1</td><td class='text-right'>{invoice.AmountBeforeTax:N0} đ</td><td class='text-right'>{invoice.AmountBeforeTax:N0} đ</td></tr>");
            }

            sb.AppendLine("</tbody>");
            sb.AppendLine("</table>");

            sb.AppendLine("<table class='totals'>");
            sb.AppendLine($"<tr><td class='text-right' style='width:75%;'><strong>Cộng tiền hàng (chưa thuế):</strong></td><td class='text-right'><strong>{invoice.AmountBeforeTax:N0} VND</strong></td></tr>");
            sb.AppendLine($"<tr><td class='text-right'><strong>Tiền thuế GTGT:</strong></td><td class='text-right'><strong>{invoice.TaxAmount:N0} VND</strong></td></tr>");
            sb.AppendLine($"<tr style='background:#f8fafc;'><td class='text-right'><strong style='font-size:16px; color:#1e40af;'>Tổng cộng tiền thanh toán:</strong></td><td class='text-right'><strong style='font-size:16px; color:#1e40af;'>{invoice.TotalAmount:N0} VND</strong></td></tr>");
            sb.AppendLine("</table>");

            sb.AppendLine("<div class='sign-box'>");
            sb.AppendLine("<div><strong>NGƯỜI MUA HÀNG</strong><br><span style='font-size:12px; color:#6b7280;'>(Ký, ghi rõ họ tên)</span></div>");
            sb.AppendLine("<div><strong>NGƯỜI BÁN HÀNG</strong><br><span style='font-size:12px; color:#6b7280;'>(Chữ ký điện tử / Chữ ký số)</span><br>");
            sb.AppendLine($"<div class='stamp'>✓ KÝ BỞI: {invoice.SellerName}<br>Ký ngày: {invoice.IssueDate:dd/MM/yyyy HH:mm:ss}</div>");
            sb.AppendLine("</div>");
            sb.AppendLine("</div>");

            sb.AppendLine("</div>");
            sb.AppendLine("</body>");
            sb.AppendLine("</html>");

            return sb.ToString();
        }
    }
}
