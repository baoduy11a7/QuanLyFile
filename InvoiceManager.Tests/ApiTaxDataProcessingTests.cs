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
            var service = new TaxCodeLookupService(mockFactory.Object, db, mockLogger.Object);

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
    }
}
