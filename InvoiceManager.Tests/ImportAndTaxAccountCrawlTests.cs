using InvoiceManager.Controllers;
using InvoiceManager.Data;
using InvoiceManager.Jobs.JobTracker;
using InvoiceManager.Models.Entities;
using InvoiceManager.Models.ViewModels;
using InvoiceManager.Services;
using InvoiceManager.Services.Providers;
using InvoiceManager.Services.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace InvoiceManager.Tests
{
    public class ImportAndTaxAccountCrawlTests
    {
        private ApplicationDbContext CreateInMemoryDbContext()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(databaseName: "ImportCrawlTestDb_" + Guid.NewGuid())
                .Options;
            return new ApplicationDbContext(options);
        }

        private ControllerContext CreateControllerContextWithUser(string userId)
        {
            var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(ClaimTypes.Name, "testuser"),
                new Claim(ClaimTypes.Role, "Admin")
            }, "TestAuth"));

            return new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = user }
            };
        }

        [Fact]
        public async Task ImportIndex_WhenNoTaxAccount_ReturnsViewResultWithoutRedirect()
        {
            using var db = CreateInMemoryDbContext();
            var mockImportService = new Mock<IInvoiceImportService>();
            var mockTaxAccountContext = new Mock<ITaxAccountContext>();
            var mockProviderFactory = new Mock<IInvoiceSourceProviderFactory>();
            var mockJobTracker = new Mock<IRemoteJobTracker>();
            var mockProtector = new Mock<ICredentialProtector>();
            var mockAudit = new Mock<IAuditLogService>();
            var mockLookup = new Mock<ITaxCodeLookupService>();

            // Simulate deleted account -> GetCurrentTaxAccountAsync returns null
            mockTaxAccountContext.Setup(c => c.GetCurrentTaxAccountAsync()).ReturnsAsync((TaxAccount?)null);

            var controller = new ImportController(
                mockImportService.Object,
                mockTaxAccountContext.Object,
                mockProviderFactory.Object,
                mockJobTracker.Object,
                mockProtector.Object,
                mockAudit.Object,
                mockLookup.Object,
                db);

            var result = await controller.Index();

            var viewResult = Assert.IsType<ViewResult>(result);
            Assert.Null(controller.ViewBag.TaxAccount);
        }

        [Fact]
        public async Task QuickCrawlAndSelect_OnImportController_CreatesAccountAndSetsContext()
        {
            using var db = CreateInMemoryDbContext();
            var mockImportService = new Mock<IInvoiceImportService>();
            var mockTaxAccountContext = new Mock<ITaxAccountContext>();
            var mockProviderFactory = new Mock<IInvoiceSourceProviderFactory>();
            var mockJobTracker = new Mock<IRemoteJobTracker>();
            var mockProtector = new Mock<ICredentialProtector>();
            var mockAudit = new Mock<IAuditLogService>();
            var mockLookup = new Mock<ITaxCodeLookupService>();

            mockLookup.Setup(l => l.LookupAsync("0314094922", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TaxCodeLookupResult
                {
                    Success = true,
                    TaxCode = "0314094922",
                    CompanyName = "CÔNG TY TNHH BẢO DUY",
                    Address = "Thủ Đức, TP.HCM",
                    Status = "Đang hoạt động"
                });

            var controller = new ImportController(
                mockImportService.Object,
                mockTaxAccountContext.Object,
                mockProviderFactory.Object,
                mockJobTracker.Object,
                mockProtector.Object,
                mockAudit.Object,
                mockLookup.Object,
                db)
            {
                ControllerContext = CreateControllerContextWithUser("user-123")
            };

            var req = new QuickCrawlRequest { TaxCode = "0314094922" };
            var result = await controller.QuickCrawlAndSelect(req);

            var jsonResult = Assert.IsType<JsonResult>(result);
            var successProp = jsonResult.Value?.GetType().GetProperty("success")?.GetValue(jsonResult.Value);
            var taxCodeProp = jsonResult.Value?.GetType().GetProperty("taxCode")?.GetValue(jsonResult.Value);
            Assert.True((bool)successProp!);
            Assert.Equal("0314094922", (string)taxCodeProp!);

            // Verify account in DB
            var accountInDb = await db.TaxAccounts.FirstOrDefaultAsync(t => t.TaxCode == "0314094922");
            Assert.NotNull(accountInDb);
            Assert.True(accountInDb.IsActive);
            Assert.Equal("CÔNG TY TNHH BẢO DUY", accountInDb.CompanyName);

            // Verify mapping
            var mapping = await db.UserTaxAccounts.FirstOrDefaultAsync(u => u.UserId == "user-123" && u.TaxAccountId == accountInDb.Id);
            Assert.NotNull(mapping);

            // Verify SetCurrentTaxAccount was called
            mockTaxAccountContext.Verify(c => c.SetCurrentTaxAccountAsync(accountInDb.Id), Times.Once);
        }

        [Fact]
        public async Task QuickCrawlAndSelect_WhenAccountWasDeletedOrInactive_ReactivatesAndUpdates()
        {
            using var db = CreateInMemoryDbContext();
            var existing = new TaxAccount
            {
                TaxCode = "0314094922",
                CompanyName = "Old Company Name",
                Address = "Old Address",
                IsActive = false
            };
            db.TaxAccounts.Add(existing);
            await db.SaveChangesAsync();

            var mockAudit = new Mock<IAuditLogService>();
            var mockLookup = new Mock<ITaxCodeLookupService>();
            var mockTaxAccountContext = new Mock<ITaxAccountContext>();

            mockLookup.Setup(l => l.LookupAsync("0314094922", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TaxCodeLookupResult
                {
                    Success = true,
                    TaxCode = "0314094922",
                    CompanyName = "CÔNG TY BẢO DUY MỚI",
                    Address = "Địa chỉ mới",
                    Status = "Đang hoạt động"
                });

            var controller = new TaxAccountController(db, mockTaxAccountContext.Object, mockAudit.Object, mockLookup.Object)
            {
                ControllerContext = CreateControllerContextWithUser("user-456")
            };

            var req = new QuickCrawlRequest { TaxCode = "0314094922" };
            var result = await controller.QuickCrawlAndSelect(req);

            var jsonResult = Assert.IsType<JsonResult>(result);
            var successProp = jsonResult.Value?.GetType().GetProperty("success")?.GetValue(jsonResult.Value);
            Assert.True((bool)successProp!);

            var updated = await db.TaxAccounts.FindAsync(existing.Id);
            Assert.NotNull(updated);
            Assert.True(updated.IsActive);
            Assert.Equal("CÔNG TY BẢO DUY MỚI", updated.CompanyName);
            Assert.Equal("Địa chỉ mới", updated.Address);

            mockTaxAccountContext.Verify(c => c.SetCurrentTaxAccountAsync(existing.Id), Times.Once);
        }
    }
}
