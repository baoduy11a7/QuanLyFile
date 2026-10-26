using InvoiceManager.Data;
using InvoiceManager.Models.Entities;
using InvoiceManager.Services;
using InvoiceManager.Services.Parsers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace InvoiceManager.Tests
{
    public class TaxAndCompanyDataPipelineTests
    {
        private ApplicationDbContext CreateInMemoryDbContext()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(databaseName: "PipelineTestDb_" + Guid.NewGuid())
                .Options;
            return new ApplicationDbContext(options);
        }

        // =========================================================================
        // 1. KIỂM THỬ CÀO & TRA CỨU THÔNG TIN MÃ SỐ THUẾ (MST)
        // =========================================================================

        [Theory]
        [InlineData("0314094922", true, "BẢO DUY")]
        [InlineData("0314094922-001", true, "CHI NHÁNH 001")]
        [InlineData("  0314.094.922  ", true, "BẢO DUY")]
        public async Task TaxCodeLookup_StandardAndBranchFormats_ResolvesSuccessfully(string inputMst, bool expectedSuccess, string expectedNameKeyword)
        {
            using var db = CreateInMemoryDbContext();
            var mockFactory = new Mock<IHttpClientFactory>();
            var mockLogger = new Mock<ILogger<TaxCodeLookupService>>();

            var service = new TaxCodeLookupService(mockFactory.Object, db, mockLogger.Object, new Mock<ITaxAccountContext>().Object);
            var result = await service.LookupAsync(inputMst);

            Assert.Equal(expectedSuccess, result.Success);
            Assert.Contains(expectedNameKeyword, result.CompanyName);
        }

        [Fact]
        public async Task TaxCodeLookup_BranchResolvesParentCompanyFromDatabase()
        {
            using var db = CreateInMemoryDbContext();
            // Thêm đơn vị chủ quản vào CSDL
            db.TaxAccounts.Add(new TaxAccount
            {
                Id = 10,
                TaxCode = "0105880697",
                CompanyName = "CÔNG TY CỔ PHẦN CHIẾU SÁNG HÀ NỘI",
                Address = "Hà Nội",
                IsActive = true,
                CreatedAt = DateTime.Now
            });
            await db.SaveChangesAsync();

            var mockFactory = new Mock<IHttpClientFactory>();
            var mockLogger = new Mock<ILogger<TaxCodeLookupService>>();
            var service = new TaxCodeLookupService(mockFactory.Object, db, mockLogger.Object, new Mock<ITaxAccountContext>().Object);

            // Tra cứu chi nhánh
            var result = await service.LookupAsync("0105880697-001");

            Assert.True(result.Success);
            Assert.Contains("CHIẾU SÁNG HÀ NỘI", result.CompanyName);
            Assert.Contains("Chi nhánh 0105880697-001", result.CompanyName);
        }

        // =========================================================================
        // 2. KIỂM THỬ XỬ LÝ DỮ LIỆU VÀO CHO TỪNG CÔNG TY (INPUT DATA PROCESSING)
        // =========================================================================

        [Fact]
        public async Task InputProcessing_ImportsXmlPerCompany_WithDetailsAndAmounts()
        {
            using var db = CreateInMemoryDbContext();

            // Khởi tạo 2 công ty khác nhau
            var companyA = new TaxAccount { Id = 1, TaxCode = "0314094922", CompanyName = "CÔNG TY BẢO DUY", CreatedAt = DateTime.Now };
            var companyB = new TaxAccount { Id = 2, TaxCode = "0100109106", CompanyName = "TẬP ĐOÀN VIETTEL", CreatedAt = DateTime.Now };
            db.TaxAccounts.AddRange(companyA, companyB);
            await db.SaveChangesAsync();

            var mockParserFactory = new Mock<IInvoiceParserFactory>();
            var mockParser = new Mock<IInvoiceParser>();

            mockParser.Setup(p => p.ParseAsync(It.IsAny<Stream>(), It.IsAny<string>()))
                .ReturnsAsync(new ParsedInvoiceResult
                {
                    Success = true,
                    InvoiceSymbol = "1C25TKT",
                    InvoiceNumber = "00000099",
                    IssueDate = new DateTime(2026, 1, 10),
                    SellerTaxCode = "0104128565",
                    SellerName = "NHÀ CUNG CẤP CÔNG NGHỆ",
                    BuyerTaxCode = "0314094922",
                    BuyerName = "CÔNG TY BẢO DUY",
                    AmountBeforeTax = 20000000m,
                    TaxAmount = 2000000m,
                    TotalAmount = 22000000m,
                    HasTaxCode = true,
                    Status = "Hóa đơn mới",
                    Details = new List<ParsedInvoiceDetail>
                    {
                        new ParsedInvoiceDetail
                        {
                            LineNumber = 1,
                            ItemName = "Máy chủ Dell PowerEdge",
                            Unit = "Chiếc",
                            Quantity = 1,
                            UnitPrice = 20000000m,
                            AmountBeforeTax = 20000000m,
                            TaxRate = 10,
                            TaxAmount = 2000000m,
                            TotalAmount = 22000000m
                        }
                    }
                });

            mockParserFactory.Setup(f => f.GetParser(It.IsAny<string>())).Returns(mockParser.Object);

            var mockAudit = new Mock<IAuditLogService>();
            var mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c.GetSection("InvoiceSettings:StorageRootPath").Value).Returns("App_Data/TestRawFiles");
            var mockLogger = new Mock<ILogger<InvoiceImportService>>();

            var importService = new InvoiceImportService(db, mockParserFactory.Object, mockAudit.Object, mockConfig.Object, mockLogger.Object);

            // Nạp hóa đơn vào Công ty A
            using var xmlStream = new MemoryStream(Encoding.UTF8.GetBytes("<HDon></HDon>"));
            var result = await importService.ImportXmlAsync(xmlStream, "test_inv.xml", companyA.Id, "MuaVao", "user-1");

            Assert.Equal(1, result.SuccessCount);

            // Kiểm tra phân tách dữ liệu: Công ty A có 1 hóa đơn, Công ty B có 0 hóa đơn
            var countA = await db.Invoices.CountAsync(i => i.TaxAccountId == companyA.Id);
            var countB = await db.Invoices.CountAsync(i => i.TaxAccountId == companyB.Id);

            Assert.Equal(1, countA);
            Assert.Equal(0, countB);

            var savedInvoice = await db.Invoices.Include(i => i.Details).FirstAsync(i => i.TaxAccountId == companyA.Id);
            Assert.Equal("1C25TKT", savedInvoice.InvoiceSymbol);
            Assert.Equal("00000099", savedInvoice.InvoiceNumber);
            Assert.Equal(22000000m, savedInvoice.TotalAmount);
            Assert.Single(savedInvoice.Details);
            Assert.Equal("Máy chủ Dell PowerEdge", savedInvoice.Details.First().ItemName);
        }

        [Fact]
        public async Task InputProcessing_PreventsDuplicateImportWithinSameCompany()
        {
            using var db = CreateInMemoryDbContext();
            var company = new TaxAccount { Id = 1, TaxCode = "0314094922", CompanyName = "CÔNG TY BẢO DUY", CreatedAt = DateTime.Now };
            db.TaxAccounts.Add(company);
            await db.SaveChangesAsync();

            var mockParserFactory = new Mock<IInvoiceParserFactory>();
            var mockParser = new Mock<IInvoiceParser>();

            var parsedData = new ParsedInvoiceResult
            {
                Success = true,
                InvoiceSymbol = "1C25TKT",
                InvoiceNumber = "00000001",
                IssueDate = new DateTime(2026, 1, 1),
                SellerTaxCode = "0104128565",
                AmountBeforeTax = 1000m,
                TaxAmount = 100m,
                TotalAmount = 1100m
            };

            mockParser.Setup(p => p.ParseAsync(It.IsAny<Stream>(), It.IsAny<string>())).ReturnsAsync(parsedData);
            mockParserFactory.Setup(f => f.GetParser(It.IsAny<string>())).Returns(mockParser.Object);
            var mockAudit = new Mock<IAuditLogService>();
            var mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c.GetSection("InvoiceSettings:StorageRootPath").Value).Returns("App_Data/TestRawFiles");
            var mockLogger = new Mock<ILogger<InvoiceImportService>>();

            var importService = new InvoiceImportService(db, mockParserFactory.Object, mockAudit.Object, mockConfig.Object, mockLogger.Object);

            // Import lần 1
            using var stream1 = new MemoryStream(Encoding.UTF8.GetBytes("<HDon></HDon>"));
            var res1 = await importService.ImportXmlAsync(stream1, "test1.xml", company.Id, "MuaVao", "u1");
            Assert.Equal(1, res1.SuccessCount);

            // Import lần 2 (cùng hóa đơn)
            using var stream2 = new MemoryStream(Encoding.UTF8.GetBytes("<HDon></HDon>"));
            var res2 = await importService.ImportXmlAsync(stream2, "test1.xml", company.Id, "MuaVao", "u1");
            Assert.Equal(0, res2.SuccessCount);
            Assert.Equal(1, res2.SkippedDuplicateCount);
        }

        // =========================================================================
        // 3. KIỂM THỬ XUẤT DỮ LIỆU RA CHO TỪNG CÔNG TY (DATA EXPORT PER COMPANY)
        // =========================================================================

        [Fact]
        public async Task ExportPerCompany_StrictlyIsolatesDataBetweenCompanies()
        {
            using var db = CreateInMemoryDbContext();

            // Công ty 1
            var comp1 = new TaxAccount { Id = 1, TaxCode = "0314094922", CompanyName = "CÔNG TY BẢO DUY", Address = "TP. HCM" };
            // Công ty 2
            var comp2 = new TaxAccount { Id = 2, TaxCode = "0100109106", CompanyName = "TẬP ĐOÀN VIETTEL", Address = "Hà Nội" };
            db.TaxAccounts.AddRange(comp1, comp2);

            // Hóa đơn của Công ty 1
            db.Invoices.Add(new Invoice
            {
                Id = 1,
                TaxAccountId = 1,
                InvoiceType = "MuaVao",
                InvoiceSymbol = "1C25TAA",
                InvoiceNumber = "00000001",
                IssueDate = new DateTime(2026, 1, 1),
                SellerTaxCode = "9999999999",
                SellerName = "NHÀ CUNG CẤP CỦA BẢO DUY",
                TotalAmount = 50000000m
            });

            // Hóa đơn của Công ty 2
            db.Invoices.Add(new Invoice
            {
                Id = 2,
                TaxAccountId = 2,
                InvoiceType = "MuaVao",
                InvoiceSymbol = "2C25TBB",
                InvoiceNumber = "00000002",
                IssueDate = new DateTime(2026, 1, 2),
                SellerTaxCode = "8888888888",
                SellerName = "NHÀ CUNG CẤP CỦA VIETTEL",
                TotalAmount = 90000000m
            });

            await db.SaveChangesAsync();

            var exportService = new ExportService();

            // Lấy hóa đơn của riêng Công ty 1 để xuất
            var comp1Invoices = await db.Invoices
                .Include(i => i.Details)
                .Where(i => i.TaxAccountId == comp1.Id)
                .ToListAsync();

            Assert.Single(comp1Invoices);
            Assert.Equal("1C25TAA", comp1Invoices[0].InvoiceSymbol);

            // Xuất báo cáo cho Công ty 1
            var excelBytes = await exportService.ExportDetailedExcelAsync(
                comp1Invoices, 
                comp1, 
                "BẢNG KÊ CHI TIẾT HÓA ĐƠN MUA VÀO");

            Assert.NotNull(excelBytes);

            using var ms = new MemoryStream(excelBytes);
            using var wb = new ClosedXML.Excel.XLWorkbook(ms);
            var sheet = wb.Worksheets.First();

            // Kiểm tra file xuất của Công ty 1 chứa đúng thông tin Công ty 1
            Assert.Contains("BẢO DUY", sheet.Cell("A1").GetString());
            Assert.Contains("0314094922", sheet.Cell("A2").GetString());

            // Đảm bảo tuyệt đối KHÔNG có dữ liệu của Công ty 2
            Assert.Contains("NHÀ CUNG CẤP CỦA BẢO DUY", sheet.Cell(8, 6).GetString());
            Assert.DoesNotContain("VIETTEL", sheet.Cell(8, 6).GetString());
        }
    }
}
