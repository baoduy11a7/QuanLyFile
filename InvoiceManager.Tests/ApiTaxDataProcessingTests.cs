using InvoiceManager.Data;
using InvoiceManager.Models.Entities;
using InvoiceManager.Services;
using InvoiceManager.Services.Providers.Mock;
using InvoiceManager.Services.Providers.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace InvoiceManager.Tests
{
    public class ApiTaxDataProcessingTests
    {
        private ApplicationDbContext CreateInMemoryDbContext()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(databaseName: "ApiTaxTestDb_" + Guid.NewGuid())
                .Options;
            return new ApplicationDbContext(options);
        }

        [Fact]
        public async Task MockProvider_FetchesBothInputAndOutputInvoices_WhenTypeIsAll()
        {
            var provider = new MockInvoiceSourceProvider();
            var req = new FetchRequest
            {
                TaxCode = "0314094922",
                InvoiceType = "All",
                FromDate = new DateTime(2025, 1, 1),
                ToDate = new DateTime(2025, 3, 31)
            };

            var list = new List<RemoteInvoice>();
            await foreach (var inv in provider.FetchInvoicesAsync(req, "mock-token"))
            {
                list.Add(inv);
            }

            // Phải có cả MuaVao (Đầu vào) và BanRa (Đầu ra)
            Assert.NotEmpty(list);
            Assert.Contains(list, i => i.InvoiceType == "MuaVao");
            Assert.Contains(list, i => i.InvoiceType == "BanRa");

            // Kiểm tra hóa đơn đầu vào: người mua là 0314094922
            var inputInv = list.Find(i => i.InvoiceType == "MuaVao");
            Assert.NotNull(inputInv);
            Assert.Equal("0314094922", inputInv.BuyerTaxCode);

            // Kiểm tra hóa đơn đầu ra: người bán là 0314094922
            var outputInv = list.Find(i => i.InvoiceType == "BanRa");
            Assert.NotNull(outputInv);
            Assert.Equal("0314094922", outputInv.SellerTaxCode);
        }

        [Fact]
        public async Task MockProvider_GeneratesValidXmlWithDetails()
        {
            var provider = new MockInvoiceSourceProvider();
            var inv = new RemoteInvoice
            {
                InvoiceSymbol = "1C25TKT",
                InvoiceNumber = "0000001",
                IssueDate = new DateTime(2025, 2, 15),
                SellerTaxCode = "0100109106",
                SellerName = "TẬP ĐOÀN VIETTEL",
                BuyerTaxCode = "0314094922",
                BuyerName = "CÔNG TY TNHH GIẢI PHÁP CÔNG NGHỆ BẢO DUY",
                AmountBeforeTax = 15000000m,
                TaxAmount = 1500000m,
                TotalAmount = 16500000m,
                InvoiceType = "MuaVao",
                CqtCode = "CQT123456789"
            };

            var xmlBytes = await provider.DownloadXmlAsync(inv, "mock-token");
            Assert.NotNull(xmlBytes);
            Assert.True(xmlBytes.Length > 0);

            var xmlString = Encoding.UTF8.GetString(xmlBytes);
            Assert.Contains("<HDon>", xmlString);
            Assert.Contains("0100109106", xmlString);
            Assert.Contains("0314094922", xmlString);
            Assert.Contains("16500000", xmlString);
        }

        [Fact]
        public async Task TaxCodeLookup_FallbackForBaoDuy_SucceedsEvenOnHttpError()
        {
            using var db = CreateInMemoryDbContext();
            var handlerMock = new Mock<System.Net.Http.HttpMessageHandler>();
            handlerMock
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<System.Threading.CancellationToken>()
                )
                .ThrowsAsync(new HttpRequestException("Network failure"));

            var httpClient = new HttpClient(handlerMock.Object);
            var mockFactory = new Mock<IHttpClientFactory>();
            mockFactory.Setup(m => m.CreateClient(It.IsAny<string>())).Returns(httpClient);

            var mockLogger = new Mock<ILogger<TaxCodeLookupService>>();
            var service = new TaxCodeLookupService(mockFactory.Object, db, mockLogger.Object, new Mock<ITaxAccountContext>().Object);

            var result = await service.LookupAsync("0314094922");

            Assert.True(result.Success);
            Assert.Equal("0314094922", result.TaxCode);
            Assert.Contains("BẢO DUY", result.CompanyName);
        }

        [Fact]
        public async Task StandardTctParser_ParsesMockXmlWithoutEntityNameError()
        {
            var provider = new MockInvoiceSourceProvider();
            var inv = new RemoteInvoice
            {
                InvoiceSymbol = "1C25TAA",
                InvoiceNumber = "0000005",
                IssueDate = new DateTime(2025, 2, 20),
                SellerTaxCode = "0314094922",
                SellerName = "CÔNG TY TNHH GIẢI PHÁP CÔNG NGHỆ BẢO DUY",
                BuyerTaxCode = "0100107518",
                BuyerName = "NGÂN HÀNG MB",
                AmountBeforeTax = 85000000m,
                TaxAmount = 8500000m,
                TotalAmount = 93500000m,
                InvoiceType = "BanRa",
                CqtCode = "CQT987654321"
            };

            var xmlBytes = await provider.DownloadXmlAsync(inv, "mock-token");
            using var ms = new MemoryStream(xmlBytes);

            var parser = new InvoiceManager.Services.Parsers.StandardTctParser();
            var parseResult = await parser.ParseAsync(ms, "Remote_1C25TAA_0000005.xml");

            Assert.True(parseResult.Success, parseResult.ErrorMessage);
            Assert.Equal("1C25TAA", parseResult.InvoiceSymbol);
            Assert.Equal("0000005", parseResult.InvoiceNumber);
            Assert.Equal(85000000m, parseResult.AmountBeforeTax);
            Assert.Equal(93500000m, parseResult.TotalAmount);
            Assert.NotEmpty(parseResult.Details);
        }

        [Fact]
        public async Task StandardTctParser_SanitizesUnescapedAmpersandInRawXml()
        {
            var rawXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<HDon>
  <TTChung>
    <PBan>2.0.0</PBan>
    <THDon>HÓA ĐƠN GIÁ TRỊ GIA TĂNG</THDon>
    <KHMSHDon>1</KHMSHDon>
    <KHHDon>1C25TAA</KHHDon>
    <SHDon>0000006</SHDon>
    <NLap>2025-02-21</NLap>
  </TTChung>
  <NDHDon>
    <NBan>
      <Ten>Công ty TNHH A & B Giải pháp</Ten>
      <MST>0314094922</MST>
    </NBan>
    <NMua>
      <Ten>Khách hàng X & Y</Ten>
      <MST>0101234567</MST>
    </NMua>
    <DSHHDVu>
      <HHDVu>
        <STT>1</STT>
        <THHDVu>Thiết bị mạng & Dịch vụ Cloud</THHDVu>
        <ThTien>1000000</ThTien>
      </HHDVu>
    </DSHHDVu>
    <TToan>
      <TgTCThue>1000000</TgTCThue>
      <TgTThue>100000</TgTThue>
      <TgTTTBSo>1100000</TgTTTBSo>
    </TToan>
  </NDHDon>
</HDon>";

            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(rawXml));
            var parser = new InvoiceManager.Services.Parsers.StandardTctParser();
            var parseResult = await parser.ParseAsync(ms, "test_raw_amp.xml");

            Assert.True(parseResult.Success, parseResult.ErrorMessage);
            Assert.Contains("&", parseResult.SellerName);
        }

        [Fact]
        public async Task GdtPortalProvider_ParsesJsonWithNumberShdonAndNormalizes_Success()
        {
            var jsonResponse = @"{
                ""datas"": [
                    {
                        ""id"": ""rec-001"",
                        ""nbmst"": ""0314094922"",
                        ""nbten"": ""CÔNG TY TNHH BẢO DUY"",
                        ""nmdmst"": ""0100109106"",
                        ""nmten"": ""TẬP ĐOÀN VIỄN THÔNG"",
                        ""khhdon"": ""1C24TXX"",
                        ""shdon"": 12345,
                        ""tdlap"": ""2025-02-15T08:30:00"",
                        ""tgtcthue"": 10000000,
                        ""tgtthue"": 1000000,
                        ""tgtttbso"": 11000000,
                        ""mcqt"": 105,
                        ""ttxly"": 1
                    },
                    {
                        ""id"": ""rec-002"",
                        ""nbmst"": ""0100109106"",
                        ""nbten"": ""TẬP ĐOÀN VIỄN THÔNG"",
                        ""nmdmst"": ""0314094922"",
                        ""nmten"": ""CÔNG TY TNHH BẢO DUY"",
                        ""khhdon"": ""2C24TBB"",
                        ""shdon"": ""6789"",
                        ""tdlap"": ""2025-02-16T10:15:00"",
                        ""tgtcthue"": ""5000000"",
                        ""tgtthue"": ""500000"",
                        ""tgtttbso"": ""5500000"",
                        ""mcqt"": ""105"",
                        ""ttxly"": ""1""
                    }
                ],
                ""state"": null
            }";

            var handlerMock = new Mock<System.Net.Http.HttpMessageHandler>();
            handlerMock
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<System.Threading.CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage
                {
                    StatusCode = System.Net.HttpStatusCode.OK,
                    Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json")
                });

            var httpClient = new HttpClient(handlerMock.Object);
            var factoryMock = new Mock<IHttpClientFactory>();
            factoryMock.Setup(f => f.CreateClient(InvoiceManager.Services.Providers.Gdt.GdtPortalProvider.ClientName)).Returns(httpClient);

            var optionsMock = new Mock<Microsoft.Extensions.Options.IOptions<InvoiceManager.Services.Providers.Gdt.GdtPortalOptions>>();
            optionsMock.Setup(o => o.Value).Returns(new InvoiceManager.Services.Providers.Gdt.GdtPortalOptions
            {
                BaseUrl = "https://hoadondientu.gdt.gov.vn",
                PurchaseQueryEndpoint = "/api/query/invoices/purchase",
                SoldQueryEndpoint = "/api/query/invoices/sold"
            });

            var loggerMock = new Mock<Microsoft.Extensions.Logging.ILogger<InvoiceManager.Services.Providers.Gdt.GdtPortalProvider>>();
            var provider = new InvoiceManager.Services.Providers.Gdt.GdtPortalProvider(factoryMock.Object, optionsMock.Object, loggerMock.Object);

            var req = new FetchRequest
            {
                TaxCode = "0314094922",
                FromDate = new DateTime(2025, 2, 1),
                ToDate = new DateTime(2025, 2, 28),
                InvoiceType = "MuaVao"
            };

            var invoices = new List<RemoteInvoice>();
            await foreach (var inv in provider.FetchInvoicesAsync(req, "Bearer test-jwt-token"))
            {
                invoices.Add(inv);
            }

            // Kiểm tra: Cả 2 hóa đơn (1 số nguyên, 1 chuỗi) đều phải được parse thành công, không bị null/0 hóa đơn!
            Assert.Equal(2, invoices.Count);

            var inv1 = invoices[0];
            Assert.Equal("1C24TXX", inv1.InvoiceSymbol);
            Assert.Equal("0012345", inv1.InvoiceNumber);
            Assert.Equal("12345", inv1.RawInvoiceNumber);
            Assert.Equal(10000000m, inv1.AmountBeforeTax);
            Assert.Equal(11000000m, inv1.TotalAmount);

            var inv2 = invoices[1];
            Assert.Equal("2C24TBB", inv2.InvoiceSymbol);
            Assert.Equal("0006789", inv2.InvoiceNumber);
            Assert.Equal("6789", inv2.RawInvoiceNumber);
            Assert.Equal(5000000m, inv2.AmountBeforeTax);
            Assert.Equal(5500000m, inv2.TotalAmount);
        }
    }
}
