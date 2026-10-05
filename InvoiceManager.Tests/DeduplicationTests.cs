using InvoiceManager.Data;
using InvoiceManager.Models.Entities;
using InvoiceManager.Services;
using InvoiceManager.Services.Parsers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace InvoiceManager.Tests
{
    public class DeduplicationTests
    {
        private ApplicationDbContext CreateInMemoryDbContext()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            return new ApplicationDbContext(options);
        }

        [Fact]
        public async Task ImportXml_WhenInvoiceAlreadyExistsWithSameBusinessKey_SkipsDuplicate()
        {
            // Arrange
            using var db = CreateInMemoryDbContext();
            int taxAccountId = 1;
            string sellerTaxCode = "0101234567";
            string symbol = "1C25TKT";
            string number = "0000001";
            string invoiceType = "MuaVao";

            // Tạo sẵn hóa đơn ban đầu trong Database
            db.Invoices.Add(new Invoice
            {
                TaxAccountId = taxAccountId,
                InvoiceType = invoiceType,
                SellerTaxCode = sellerTaxCode,
                SellerName = "CÔNG TY BÁN HÀNG A",
                InvoiceSymbol = symbol,
                InvoiceNumber = number,
                IssueDate = new DateTime(2026, 1, 15),
                AmountBeforeTax = 1000000,
                TaxAmount = 100000,
                TotalAmount = 1100000
            });
            await db.SaveChangesAsync();

            // Mock Parser trả về kết quả parse trùng với hóa đơn trên
            var mockParser = new Mock<IInvoiceParser>();
            mockParser.Setup(p => p.ParseAsync(It.IsAny<Stream>(), It.IsAny<string>()))
                .ReturnsAsync(new ParsedInvoiceResult
                {
                    Success = true,
                    SellerTaxCode = sellerTaxCode,
                    SellerName = "CÔNG TY BÁN HÀNG A",
                    InvoiceSymbol = symbol,
                    InvoiceNumber = number,
                    IssueDate = new DateTime(2026, 1, 15),
                    AmountBeforeTax = 1000000,
                    TaxAmount = 100000,
                    TotalAmount = 1100000
                });

            var mockFactory = new Mock<IInvoiceParserFactory>();
            mockFactory.Setup(f => f.GetParser(It.IsAny<string>())).Returns(mockParser.Object);

            var mockAudit = new Mock<IAuditLogService>();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InvoiceSettings:StorageRootPath"] = Path.Combine(Path.GetTempPath(), "InvoiceTestFiles")
            }).Build();

            var service = new InvoiceImportService(db, mockFactory.Object, mockAudit.Object, config, NullLogger<InvoiceImportService>.Instance);

            var sampleXmlBytes = Encoding.UTF8.GetBytes("<xml>Test</xml>");
            using var ms = new MemoryStream(sampleXmlBytes);

            // Act
            var result = await service.ImportXmlAsync(ms, "test.xml", taxAccountId, invoiceType, "user-test");

            // Assert
            Assert.Equal(0, result.SuccessCount);
            Assert.Equal(1, result.SkippedDuplicateCount);
            Assert.Contains("đã tồn tại trên hệ thống", result.Messages[0]);
        }
    }
}
