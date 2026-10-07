using ClosedXML.Excel;
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
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace InvoiceManager.Tests
{
    public class ExcelImportTests
    {
        private ApplicationDbContext CreateInMemoryDbContext()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            return new ApplicationDbContext(options);
        }

        private InvoiceImportService CreateService(ApplicationDbContext db)
        {
            var mockFactory = new Mock<IInvoiceParserFactory>();
            var mockAudit = new Mock<IAuditLogService>();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InvoiceSettings:StorageRootPath"] = Path.Combine(Path.GetTempPath(), "InvoiceTestFiles_" + Guid.NewGuid().ToString("N"))
            }).Build();

            return new InvoiceImportService(db, mockFactory.Object, mockAudit.Object, config, NullLogger<InvoiceImportService>.Instance);
        }

        [Fact]
        public async Task GenerateExcelTemplate_ReturnsValidWorkbookWithHeadersAndSampleRows()
        {
            // Arrange
            using var db = CreateInMemoryDbContext();
            var service = CreateService(db);

            // Act
            var templateBytes = await service.GenerateExcelTemplateAsync("MuaVao");

            // Assert
            Assert.NotNull(templateBytes);
            Assert.True(templateBytes.Length > 0);

            using var ms = new MemoryStream(templateBytes);
            using var workbook = new XLWorkbook(ms);
            var sheet = workbook.Worksheet("Bảng kê hóa đơn");
            Assert.NotNull(sheet);

            // Row 4 has headers
            var headerSymbol = sheet.Cell("B4").GetString();
            Assert.Contains("Ký hiệu", headerSymbol);

            var headerNumber = sheet.Cell("C4").GetString();
            Assert.Contains("Số hóa đơn", headerNumber);

            // Sample rows exist
            var sampleRow1Number = sheet.Cell("C5").GetString();
            Assert.Equal("00000001", sampleRow1Number);
        }

        [Fact]
        public async Task ImportExcel_FromTemplateFile_ImportsInvoicesAndDetailsSuccessfully()
        {
            // Arrange
            using var db = CreateInMemoryDbContext();
            var service = CreateService(db);
            int taxAccountId = 1;
            string invoiceType = "MuaVao";

            // Sinh file mẫu chuẩn
            var templateBytes = await service.GenerateExcelTemplateAsync(invoiceType);
            using var ms = new MemoryStream(templateBytes);

            // Act
            var result = await service.ImportExcelAsync(ms, "Mau_Nhap_Hoa_Don_MuaVao.xlsx", taxAccountId, invoiceType, "user-test");

            // Assert
            Assert.True(result.SuccessCount >= 2, $"Expected at least 2 invoices imported, but got {result.SuccessCount}");
            Assert.Equal(0, result.FailedCount);
            Assert.Equal(0, result.SkippedDuplicateCount);

            var invoices = await db.Invoices.Include(i => i.Details).Where(i => i.TaxAccountId == taxAccountId).ToListAsync();
            Assert.True(invoices.Count >= 2);

            var inv2 = invoices.FirstOrDefault(i => i.InvoiceNumber == "00000002");
            Assert.NotNull(inv2);
            // Hóa đơn 00000002 có 2 dòng chi tiết
            Assert.Equal(2, inv2.Details.Count);
        }

        [Fact]
        public async Task ImportExcel_WhenImportingTwice_SkipsDuplicates()
        {
            // Arrange
            using var db = CreateInMemoryDbContext();
            var service = CreateService(db);
            int taxAccountId = 1;
            string invoiceType = "MuaVao";

            var templateBytes = await service.GenerateExcelTemplateAsync(invoiceType);

            // Act 1: Import lần đầu
            using (var ms1 = new MemoryStream(templateBytes))
            {
                var result1 = await service.ImportExcelAsync(ms1, "test.xlsx", taxAccountId, invoiceType, "user-test");
                Assert.True(result1.SuccessCount >= 2);
            }

            // Act 2: Import lại cùng file
            using (var ms2 = new MemoryStream(templateBytes))
            {
                var result2 = await service.ImportExcelAsync(ms2, "test.xlsx", taxAccountId, invoiceType, "user-test");

                // Assert: Phải bỏ qua vì trùng lặp (Idempotent)
                Assert.Equal(0, result2.SuccessCount);
                Assert.True(result2.SkippedDuplicateCount >= 2);
            }
        }
    }
}
