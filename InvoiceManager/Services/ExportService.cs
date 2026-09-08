using ClosedXML.Excel;
using InvoiceManager.Models.Entities;
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
        public Task<byte[]> ExportDetailedExcelAsync(List<Invoice> invoices, TaxAccount? taxAccount, string title)
        {
            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add("Chi tiết hàng hóa HĐ");
            ws.ShowGridLines = true;

            // 1. Header công ty & Báo cáo
            ws.Cell("A1").Value = taxAccount != null ? $"ĐƠN VỊ: {taxAccount.CompanyName.ToUpper()}" : "BẢNG KÊ CHI TIẾT HÀNG HÓA DỊCH VỤ HÓA ĐƠN ĐIỆN TỬ";
            ws.Cell("A1").Style.Font.Bold = true;
            ws.Cell("A1").Style.Font.FontSize = 11;

            ws.Cell("A2").Value = taxAccount != null ? $"Mã số thuế: {taxAccount.TaxCode} - Địa chỉ: {taxAccount.Address}" : "";
            ws.Cell("A2").Style.Font.Italic = true;

            ws.Cell("A4").Value = title.ToUpper();
            ws.Cell("A4").Style.Font.Bold = true;
            ws.Cell("A4").Style.Font.FontSize = 15;
            ws.Cell("A4").Style.Font.FontColor = XLColor.FromHtml("#1E3A8A");
            ws.Range("A4:P4").Merge().Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

            ws.Cell("A5").Value = $"Ngày xuất báo cáo: {DateTime.Now:dd/MM/yyyy HH:mm} - Tổng số hóa đơn: {invoices.Count:N0}";
            ws.Range("A5:P5").Merge().Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center).Font.SetItalic(true);

            // 2. Table Headers
            int startRow = 7;
            string[] headers = new[]
            {
                "STT", "Ký hiệu HĐ", "Số hóa đơn", "Ngày lập", "MST Người bán", "Tên người bán", 
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
                    ws.Cell(currentRow, 2).Value = inv.InvoiceSymbol;
                    ws.Cell(currentRow, 3).Value = inv.InvoiceNumber;
                    ws.Cell(currentRow, 4).Value = inv.IssueDate.ToString("dd/MM/yyyy");
                    ws.Cell(currentRow, 5).Value = inv.SellerTaxCode;
                    ws.Cell(currentRow, 6).Value = inv.SellerName;
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
            ws.Column(6).Width = 35; // Tên người bán
            ws.Column(8).Width = 40; // Tên hàng hóa

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

            ws.Cell("A2").Value = taxAccount != null ? $"Mã số thuế: {taxAccount.TaxCode} - Địa chỉ: {taxAccount.Address}" : "";
            ws.Cell("A2").Style.Font.Italic = true;

            ws.Cell("A4").Value = title.ToUpper();
            ws.Cell("A4").Style.Font.Bold = true;
            ws.Cell("A4").Style.Font.FontSize = 15;
            ws.Cell("A4").Style.Font.FontColor = XLColor.FromHtml("#065F46");
            ws.Range("A4:L4").Merge().Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

            ws.Cell("A5").Value = $"Ngày xuất: {DateTime.Now:dd/MM/yyyy HH:mm} - Tổng số hóa đơn: {invoices.Count:N0}";
            ws.Range("A5:L5").Merge().Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center).Font.SetItalic(true);

            // 2. Table Headers
            int startRow = 7;
            string[] headers = new[]
            {
                "STT", "Ký hiệu", "Số HĐ", "Ngày lập", "MST Người bán", "Tên người bán", 
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
                ws.Cell(currentRow, 2).Value = inv.InvoiceSymbol;
                ws.Cell(currentRow, 3).Value = inv.InvoiceNumber;
                ws.Cell(currentRow, 4).Value = inv.IssueDate.ToString("dd/MM/yyyy");
                ws.Cell(currentRow, 5).Value = inv.SellerTaxCode;
                ws.Cell(currentRow, 6).Value = inv.SellerName;

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
            ws.Column(6).Width = 38;

            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            return Task.FromResult(stream.ToArray());
        }

        public async Task<byte[]> ExportInvoicesZipAsync(List<Invoice> invoices)
        {
            using var zipStream = new MemoryStream();
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, true))
            {
                foreach (var inv in invoices)
                {
                    // 1. Gói file HTML thể hiện chuẩn
                    var htmlContent = GenerateInvoiceHtml(inv);
                    var htmlEntryName = $"HD_{inv.InvoiceSymbol}_{inv.InvoiceNumber}.html";
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
                        var xmlEntry = archive.CreateEntry(xmlEntryName);
                        using var fs = File.OpenRead(inv.RawXmlPath);
                        using var entryStream = xmlEntry.Open();
                        await fs.CopyToAsync(entryStream);
                    }
                }
            }

            return zipStream.ToArray();
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
