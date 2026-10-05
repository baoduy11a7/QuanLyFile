using InvoiceManager.Data;
using InvoiceManager.Models.Entities;
using InvoiceManager.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace InvoiceManager.Tests
{
    public class TaxCodeLookupTests
    {
        private ApplicationDbContext CreateInMemoryDbContext()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(databaseName: "TaxLookupTestDb_" + System.Guid.NewGuid())
                .Options;
            return new ApplicationDbContext(options);
        }

        [Fact]
        public async Task LookupAsync_ShortOrInvalidTaxCode_ReturnsFailure()
        {
            using var db = CreateInMemoryDbContext();
            var mockFactory = new Mock<IHttpClientFactory>();
            var mockLogger = new Mock<ILogger<TaxCodeLookupService>>();

            var service = new TaxCodeLookupService(mockFactory.Object, db, mockLogger.Object);

            var result = await service.LookupAsync("123");

            Assert.False(result.Success);
            Assert.Contains("không hợp lệ", result.Message);
        }

        [Fact]
        public async Task LookupAsync_MockSuccessResponse_ParsesCorrectly()
        {
            using var db = CreateInMemoryDbContext();
            db.TaxAccounts.Add(new TaxAccount
            {
                Id = 1,
                TaxCode = "0105880697",
                CompanyName = "Test Company",
                Address = "Test Address",
                CreatedAt = System.DateTime.Now
            });
            await db.SaveChangesAsync();

            var jsonResponse = @"{
                ""code"": ""00"",
                ""desc"": ""Success"",
                ""data"": {
                    ""id"": ""0105880697"",
                    ""name"": ""CÔNG TY CỔ PHẦN CHIẾU SÁNG VÀ THƯƠNG MẠI HÀ NỘI"",
                    ""internationalName"": ""HA NOI LIGHTING AND TRADE JOINT STOCK COMPANY"",
                    ""address"": ""Thôn Trường An, Xã An Khánh, TP Hà Nội"",
                    ""status"": ""NNT đang hoạt động""
                }
            }";

            var handlerMock = new Mock<HttpMessageHandler>();
            handlerMock
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(jsonResponse)
                });

            var httpClient = new HttpClient(handlerMock.Object);
            var mockFactory = new Mock<IHttpClientFactory>();
            mockFactory.Setup(f => f.CreateClient(TaxCodeLookupService.ClientName)).Returns(httpClient);
            var mockLogger = new Mock<ILogger<TaxCodeLookupService>>();

            var service = new TaxCodeLookupService(mockFactory.Object, db, mockLogger.Object);

            var result = await service.LookupAsync("0105880697");

            Assert.True(result.Success);
            Assert.Equal("0105880697", result.TaxCode);
            Assert.Equal("CÔNG TY CỔ PHẦN CHIẾU SÁNG VÀ THƯƠNG MẠI HÀ NỘI", result.CompanyName);
            Assert.Equal("Thôn Trường An, Xã An Khánh, TP Hà Nội", result.Address);
            Assert.Equal("NNT đang hoạt động", result.Status);
            Assert.True(result.ExistsInSystem);
            Assert.Equal(1, result.ExistingAccountId);
        }

        [Fact]
        public async Task LookupAsync_TaxCodeNotFound_ReturnsNotFoundMessage()
        {
            using var db = CreateInMemoryDbContext();

            var jsonResponse = @"{
                ""code"": ""51"",
                ""desc"": ""Tax not found - Mã số thuế không tồn tại"",
                ""data"": null
            }";

            var handlerMock = new Mock<HttpMessageHandler>();
            handlerMock
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(jsonResponse)
                });

            var httpClient = new HttpClient(handlerMock.Object);
            var mockFactory = new Mock<IHttpClientFactory>();
            mockFactory.Setup(f => f.CreateClient(TaxCodeLookupService.ClientName)).Returns(httpClient);
            var mockLogger = new Mock<ILogger<TaxCodeLookupService>>();

            var service = new TaxCodeLookupService(mockFactory.Object, db, mockLogger.Object);

            var result = await service.LookupAsync("9999999999");

            Assert.False(result.Success);
            Assert.Contains("không tồn tại", result.Message);
        }

        [Fact]
        public async Task LookupAsync_LiveApiWithTargetMst_Succeeds()
        {
            using var db = CreateInMemoryDbContext();
            var realHttpClient = new HttpClient();
            var mockFactory = new Mock<IHttpClientFactory>();
            mockFactory.Setup(f => f.CreateClient(TaxCodeLookupService.ClientName)).Returns(realHttpClient);
            var mockLogger = new Mock<ILogger<TaxCodeLookupService>>();

            var service = new TaxCodeLookupService(mockFactory.Object, db, mockLogger.Object);

            var result = await service.LookupAsync("0105880697");

            Assert.True(result.Success);
            Assert.Equal("0105880697", result.TaxCode);
            Assert.Contains("CHIẾU SÁNG VÀ THƯƠNG MẠI HÀ NỘI", result.CompanyName);
        }
    }
}
