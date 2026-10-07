using InvoiceManager.Data;
using InvoiceManager.Models.Entities;
using InvoiceManager.Models.ViewModels;
using InvoiceManager.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace InvoiceManager.Tests
{
    public class ExcelImportIntegrationTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<ApplicationDbContext> _options;

        public ExcelImportIntegrationTests()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            _options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options;

            using var context = new ApplicationDbContext(_options);
            context.Database.EnsureCreated();
        }

        public void Dispose()
        {
            _connection.Dispose();
        }

        [Fact]
        public async Task SaveImportAsync_WithValidRows_CommitsToDbAndLogsAudit()
        {
            // Arrange
            using var db = new ApplicationDbContext(_options);
            
            // Seed TaxAccount với Id = 10
            var taxAccount = new TaxAccount
            {
                Id = 10,
                TaxCode = "0109999999",
                CompanyName = "Công ty TNHH Thử Nghiệm",
                Address = "Hà Nội"
            };
            db.TaxAccounts.Add(taxAccount);
            await db.SaveChangesAsync();

            var auditLogMock = new Mock<IAuditLogService>();
            auditLogMock.Setup(a => a.LogActionAsync(
                It.Is<string>(s => s.Contains("Import Excel")),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<int?>()))
                .Returns(Task.CompletedTask);

            var loggerMock = new Mock<ILogger<ExcelImportService>>();
            var service = new ExcelImportService(db, auditLogMock.Object, loggerMock.Object);

            var validRows = new List<ExcelImportRowDto>
            {
                new ExcelImportRowDto
                {
                    RowIndex = 2,
                    InvoiceSymbol = "1C25TXX",
                    InvoiceNumber = "00000001",
                    IssueDate = new DateTime(2026, 1, 15),
                    SellerTaxCode = "0101234567",
                    SellerName = "Công ty TNHH Thép Việt",
                    SellerAddress = "Hà Nội",
                    AmountBeforeTax = 10000000,
                    TaxAmount = 1000000,
                    TotalAmount = 11000000,
                    InvoiceType = "MuaVao",
                    Notes = "Nhập vật tư",
                    IsValid = true
                },
                new ExcelImportRowDto
                {
                    RowIndex = 3,
                    InvoiceSymbol = "2C25TKT",
                    InvoiceNumber = "00000002",
                    IssueDate = new DateTime(2026, 1, 16),
                    SellerTaxCode = "0309876543",
                    SellerName = "Công ty TNHH Dịch vụ Vận tải",
                    AmountBeforeTax = 2000000,
                    TaxAmount = 160000,
                    TotalAmount = 2160000,
                    InvoiceType = "MuaVao",
                    IsValid = true
                }
            };

            // Act
            var result = await service.SaveImportAsync(validRows, taxAccountId: 10, userId: "test-user-id");

            // Assert
            Assert.True(result.Success);
            Assert.Equal(2, result.SuccessCount);
            Assert.Equal(0, result.FailedCount);

            // Kiểm tra trong CSDL
            var savedInvoices = await db.Invoices
                .Include(i => i.Details)
                .Where(i => i.TaxAccountId == 10)
                .ToListAsync();

            Assert.Equal(2, savedInvoices.Count);

            var inv1 = savedInvoices.First(i => i.InvoiceNumber == "00000001");
            Assert.Equal("1C25TXX", inv1.InvoiceSymbol);
            Assert.Equal("0101234567", inv1.SellerTaxCode);
            Assert.Equal(11000000m, inv1.TotalAmount);
            Assert.Equal("ManualExcel", inv1.SourceProvider);
            Assert.Equal("test-user-id", inv1.ImportedByUserId);
            Assert.Single(inv1.Details); // có 1 detail sinh tự động
            Assert.Equal(10000000m, inv1.Details.First().AmountBeforeTax);

            // Kiểm tra AuditLog được gọi
            auditLogMock.Verify(a => a.LogActionAsync(
                "Import Excel",
                It.Is<string>(s => s.Contains("2/2")),
                It.IsAny<string?>(),
                10), Times.Once);
        }
    }
}
