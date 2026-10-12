using ClosedXML.Excel;
using InvoiceManager.Models.Entities;
using InvoiceManager.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace InvoiceManager.Tests
{
    public class ExportServiceTests
    {
        private readonly ExportService _exportService = new ExportService();

        [Fact]
        public async Task ExportDetailedExcelAsync_MuaVao_GeneratesValidWorkbookWithCompanyHeader()
        {
            var company = new TaxAccount
            {
                Id = 1,
                TaxCode = "0314094922",
                CompanyName = "CÔNG TY TNHH GIẢI PHÁP CÔNG NGHỆ BẢO DUY",
                Address = "Khu Phố 6, Phường Linh Trung, TP. Thủ Đức, TP. Hồ Chí Minh"
            };

            var invoices = new List<Invoice>
            {
                new Invoice
                {
                    Id = 101,
                    TaxAccountId = 1,
                    InvoiceType = "MuaVao",
                    InvoiceSymbol = "1C25TKT",
                    InvoiceNumber = "00000001",
                    IssueDate = new DateTime(2026, 1, 15),
                    SellerTaxCode = "0100109106",
                    SellerName = "TẬP ĐOÀN VIỄN THÔNG QUÂN ĐỘI",
                    BuyerTaxCode = "0314094922",
                    BuyerName = "CÔNG TY TNHH GIẢI PHÁP CÔNG NGHỆ BẢO DUY",
                    AmountBeforeTax = 10000000m,
                    TaxAmount = 1000000m,
                    TotalAmount = 11000000m,
                    Status = "Hóa đơn gốc",
                    Details = new List<InvoiceDetail>
                    {
                        new InvoiceDetail
                        {
                            LineNumber = 1,
                            ItemName = "Cước dịch vụ Internet cáp quang",
                            Unit = "Tháng",
                            Quantity = 1,
                            UnitPrice = 10000000m,
                            AmountBeforeTax = 10000000m,
                            TaxRate = 10,
                            TaxAmount = 1000000m,
                            TotalAmount = 11000000m
                        }
                    }
                }
            };

            string title = "BẢNG KÊ CHI TIẾT HÀNG HÓA - HÓA ĐƠN MUA VÀO";
            var bytes = await _exportService.ExportDetailedExcelAsync(invoices, company, title);

            Assert.NotNull(bytes);
            Assert.True(bytes.Length > 0);

            using var ms = new MemoryStream(bytes);
            using var workbook = new XLWorkbook(ms);
            var ws = workbook.Worksheets.FirstOrDefault();
            Assert.NotNull(ws);

            // Kiểm tra thông tin công ty ở Header A1, A2
            Assert.Contains("BẢO DUY", ws.Cell("A1").GetString());
            Assert.Contains("0314094922", ws.Cell("A2").GetString());

            // Kiểm tra tiêu đề báo cáo
            Assert.Contains("MUA VÀO", ws.Cell("A4").GetString());

            // Kiểm tra tiêu đề cột người bán (do là Mua Vào)
            Assert.Equal("MST Người bán", ws.Cell(7, 5).GetString());
            Assert.Equal("Tên người bán", ws.Cell(7, 6).GetString());

            // Dòng dữ liệu đầu tiên (row 8)
            Assert.Equal("0100109106", ws.Cell(8, 5).GetString());
            Assert.Contains("VIỄN THÔNG", ws.Cell(8, 6).GetString());
            Assert.Equal("Cước dịch vụ Internet cáp quang", ws.Cell(8, 8).GetString());
        }

        [Fact]
        public async Task ExportDetailedExcelAsync_BanRa_DisplaysBuyerPartnerInformation()
        {
            var company = new TaxAccount
            {
                Id = 1,
                TaxCode = "0314094922",
                CompanyName = "CÔNG TY TNHH GIẢI PHÁP CÔNG NGHỆ BẢO DUY",
                Address = "Khu Phố 6, Phường Linh Trung, TP. Thủ Đức, TP. Hồ Chí Minh"
            };

            var invoices = new List<Invoice>
            {
                new Invoice
                {
                    Id = 202,
                    TaxAccountId = 1,
                    InvoiceType = "BanRa",
                    InvoiceSymbol = "1C25TAA",
                    InvoiceNumber = "00000088",
                    IssueDate = new DateTime(2026, 2, 20),
                    SellerTaxCode = "0314094922",
                    SellerName = "CÔNG TY TNHH GIẢI PHÁP CÔNG NGHỆ BẢO DUY",
                    BuyerTaxCode = "0100107518",
                    BuyerName = "NGÂN HÀNG THƯƠNG MẠI CỔ PHẦN QUÂN ĐỘI",
                    AmountBeforeTax = 50000000m,
                    TaxAmount = 5000000m,
                    TotalAmount = 55000000m,
                    Status = "Hóa đơn mới",
                    Details = new List<InvoiceDetail>
                    {
                        new InvoiceDetail
                        {
                            LineNumber = 1,
                            ItemName = "Bản quyền phần mềm Core Banking Integration",
                            Unit = "Hệ thống",
                            Quantity = 1,
                            UnitPrice = 50000000m,
                            AmountBeforeTax = 50000000m,
                            TaxRate = 10,
                            TaxAmount = 5000000m,
                            TotalAmount = 55000000m
                        }
                    }
                }
            };

            string title = "BẢNG KÊ CHI TIẾT HÀNG HÓA - HÓA ĐƠN BÁN RA";
            var bytes = await _exportService.ExportDetailedExcelAsync(invoices, company, title);

            Assert.NotNull(bytes);
            using var ms = new MemoryStream(bytes);
            using var workbook = new XLWorkbook(ms);
            var ws = workbook.Worksheets.FirstOrDefault();
            Assert.NotNull(ws);

            // Kiểm tra cột Header chuyển thành MST Người mua & Tên người mua
            Assert.Equal("MST Người mua", ws.Cell(7, 5).GetString());
            Assert.Equal("Tên người mua", ws.Cell(7, 6).GetString());

            // Dữ liệu hiển thị thông tin Người mua (khách hàng)
            Assert.Equal("0100107518", ws.Cell(8, 5).GetString());
            Assert.Contains("NGÂN HÀNG", ws.Cell(8, 6).GetString());
        }

        [Fact]
        public async Task ExportSummaryExcelAsync_BanRa_GeneratesCorrectSummaryAndBuyerInfo()
        {
            var company = new TaxAccount
            {
                Id = 1,
                TaxCode = "0314094922",
                CompanyName = "CÔNG TY TNHH GIẢI PHÁP CÔNG NGHỆ BẢO DUY"
            };

            var invoices = new List<Invoice>
            {
                new Invoice
                {
                    Id = 301,
                    TaxAccountId = 1,
                    InvoiceType = "BanRa",
                    InvoiceSymbol = "1C25TXX",
                    InvoiceNumber = "00000005",
                    IssueDate = new DateTime(2026, 3, 1),
                    SellerTaxCode = "0314094922",
                    SellerName = "CÔNG TY TNHH GIẢI PHÁP CÔNG NGHỆ BẢO DUY",
                    BuyerTaxCode = "0301234567",
                    BuyerName = "CÔNG TY KHÁCH HÀNG A",
                    AmountBeforeTax = 20000000m,
                    TaxAmount = 2000000m,
                    TotalAmount = 22000000m,
                    HasTaxCode = true,
                    Status = "Đã duyệt"
                }
            };

            string title = "BẢNG KÊ TỔNG HỢP HÓA ĐƠN BÁN RA";
            var bytes = await _exportService.ExportSummaryExcelAsync(invoices, company, title);

            Assert.NotNull(bytes);
            using var ms = new MemoryStream(bytes);
            using var workbook = new XLWorkbook(ms);
            var ws = workbook.Worksheets.FirstOrDefault();
            Assert.NotNull(ws);

            Assert.Equal("MST Người mua", ws.Cell(7, 5).GetString());
            Assert.Equal("0301234567", ws.Cell(8, 5).GetString());
            Assert.Equal("CÔNG TY KHÁCH HÀNG A", ws.Cell(8, 6).GetString());
            Assert.Equal(20000000, ws.Cell(8, 7).GetDouble());
            Assert.Equal(2000000, ws.Cell(8, 8).GetDouble());
            Assert.Equal(22000000, ws.Cell(8, 9).GetDouble());
        }

        [Fact]
        public async Task ExportInvoicesZipAsync_PackagesHtmlAndXmlWithoutDuplicateNameConflict()
        {
            // Tạo 2 hóa đơn cùng ký hiệu & số hóa đơn nhưng khác Id
            var inv1 = new Invoice
            {
                Id = 1,
                InvoiceSymbol = "1C25TKT",
                InvoiceNumber = "00000001",
                IssueDate = DateTime.Now,
                SellerTaxCode = "0100109106",
                SellerName = "Người bán 1",
                BuyerTaxCode = "0314094922",
                BuyerName = "Người mua 1",
                AmountBeforeTax = 1000,
                TaxAmount = 100,
                TotalAmount = 1100
            };

            var inv2 = new Invoice
            {
                Id = 2,
                InvoiceSymbol = "1C25TKT",
                InvoiceNumber = "00000001",
                IssueDate = DateTime.Now,
                SellerTaxCode = "0200209207",
                SellerName = "Người bán 2",
                BuyerTaxCode = "0314094922",
                BuyerName = "Người mua 2",
                AmountBeforeTax = 2000,
                TaxAmount = 200,
                TotalAmount = 2200
            };

            var zipBytes = await _exportService.ExportInvoicesZipAsync(new List<Invoice> { inv1, inv2 });
            Assert.NotNull(zipBytes);
            Assert.True(zipBytes.Length > 0);

            using var zipStream = new MemoryStream(zipBytes);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

            // Phải chứa ít nhất 2 file HTML hợp lệ
            var entries = archive.Entries.ToList();
            Assert.Equal(2, entries.Count);
            Assert.Contains(entries, e => e.Name.Contains("00000001"));
        }
    }
}
