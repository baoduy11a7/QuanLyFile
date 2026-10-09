using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using InvoiceManager.Models.Entities;
using InvoiceManager.Services;
using Xunit;

namespace InvoiceManager.Tests
{
    public class InvoicePdfGenerationTests
    {
        [Fact]
        public async Task GenerateInvoicePdfAsync_ReturnsValidPdfBinary()
        {
            var exportService = new ExportService();
            var invoice = new Invoice
            {
                Id = 1,
                InvoiceSymbol = "1C25TTW",
                InvoiceNumber = "00000019",
                IssueDate = new DateTime(2026, 10, 9),
                SellerTaxCode = "0104128565",
                SellerName = "CÔNG TY CỔ PHẦN CÔNG NGHỆ THÔNG TIN VIỆT NAM",
                SellerAddress = "Tầng 5, Tòa nhà FPT, Cầu Giấy, Hà Nội",
                BuyerTaxCode = "0314094922",
                BuyerName = "CÔNG TY TNHH GIẢI PHÁP CÔNG NGHỆ BẢO DUY",
                BuyerAddress = "TP. Hồ Chí Minh",
                AmountBeforeTax = 10000000m,
                TaxAmount = 1000000m,
                TotalAmount = 11000000m,
                Details = new List<InvoiceDetail>
                {
                    new InvoiceDetail
                    {
                        LineNumber = 1,
                        ItemName = "Bản quyền phần mềm quản lý hóa đơn",
                        Unit = "Gói",
                        Quantity = 1,
                        UnitPrice = 10000000m,
                        AmountBeforeTax = 10000000m,
                        TaxRate = 10,
                        TaxAmount = 1000000m,
                        TotalAmount = 11000000m
                    }
                }
            };

            var pdfBytes = await exportService.GenerateInvoicePdfAsync(invoice);

            Assert.NotNull(pdfBytes);
            Assert.True(pdfBytes.Length > 100, "PDF byte length must be greater than 100");
            
            // PDF binary file MUST start with "%PDF-" header (ASCII 0x25, 0x50, 0x44, 0x46, 0x2D)
            string header = Encoding.ASCII.GetString(pdfBytes, 0, 5);
            Assert.Equal("%PDF-", header);
        }

        [Fact]
        public async Task ExportInvoicesPdfZipAsync_ContainsValidPdfFiles()
        {
            var exportService = new ExportService();
            var invoices = new List<Invoice>
            {
                new Invoice
                {
                    Id = 19,
                    InvoiceSymbol = "1C25TTW",
                    InvoiceNumber = "00000019",
                    IssueDate = new DateTime(2026, 10, 9),
                    SellerTaxCode = "0104128565",
                    SellerName = "CÔNG TY THỰC NGHIỆM",
                    AmountBeforeTax = 5000000m,
                    TaxAmount = 500000m,
                    TotalAmount = 5500000m
                }
            };

            var zipBytes = await exportService.ExportInvoicesPdfZipAsync(invoices);

            Assert.NotNull(zipBytes);
            Assert.True(zipBytes.Length > 200);

            using var zipStream = new MemoryStream(zipBytes);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
            
            var entry = archive.GetEntry("HD_1C25TTW_00000019_0104128565.pdf");
            Assert.NotNull(entry);

            using var entryStream = entry.Open();
            using var ms = new MemoryStream();
            await entryStream.CopyToAsync(ms);
            var entryBytes = ms.ToArray();

            Assert.True(entryBytes.Length > 100);
            string header = Encoding.ASCII.GetString(entryBytes, 0, 5);
            Assert.Equal("%PDF-", header);
        }

        [Theory]
        [InlineData(0, "Không đồng.")]
        [InlineData(15000000, "Mười lăm triệu đồng.")]
        [InlineData(1250000, "Một triệu hai trăm năm mươi nghìn đồng.")]
        public void NumberToVietnameseWords_ConvertsCorrectly(long number, string expected)
        {
            var words = ExportService.NumberToVietnameseWords(number);
            Assert.Equal(expected, words);
        }
    }
}
