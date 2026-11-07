using InvoiceManager.Data;
using InvoiceManager.Models.Entities;
using InvoiceManager.Services;
using InvoiceManager.Services.Providers.Gdt;
using InvoiceManager.Services.Providers.Models;
using InvoiceManager.Services.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace InvoiceManager.Tests
{
    public class InvoiceFileDownloadTests
    {
        [Fact]
        public async Task GdtPortalProvider_DownloadAll_WhenReturnsZipPackage_ExtractsXmlAndPdfAndPreservesZip()
        {
            // 1. Tạo gói ZIP nén chứa 1 XML và 1 PDF
            byte[] xmlBytes = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><HDon><SHDon>0000001</SHDon></HDon>");
            byte[] pdfBytes = Encoding.UTF8.GetBytes("%PDF-1.4\n%Invoice Content\n%%EOF");

            byte[] zipBytes;
            using (var ms = new MemoryStream())
            {
                using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
                {
                    var xmlEntry = archive.CreateEntry("invoice_1C25TKT_1.xml");
                    using (var entryStream = xmlEntry.Open())
                    {
                        entryStream.Write(xmlBytes, 0, xmlBytes.Length);
                    }

                    var pdfEntry = archive.CreateEntry("invoice_1C25TKT_1.pdf");
                    using (var entryStream = pdfEntry.Open())
                    {
                        entryStream.Write(pdfBytes, 0, pdfBytes.Length);
                    }
                }
                zipBytes = ms.ToArray();
            }

            var handlerMock = new Mock<HttpMessageHandler>();
            handlerMock.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.Is<HttpRequestMessage>(req => req.RequestUri != null && req.RequestUri.ToString().Contains("export-xml")),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new ByteArrayContent(zipBytes)
                });

            var httpClient = new HttpClient(handlerMock.Object)
            {
                BaseAddress = new Uri("https://hoadondientu.gdt.gov.vn/")
            };

            var factoryMock = new Mock<IHttpClientFactory>();
            factoryMock.Setup(f => f.CreateClient(GdtPortalProvider.ClientName)).Returns(httpClient);

            var options = Options.Create(new GdtPortalOptions
            {
                RateLimitDelayMs = 0
            });

            var provider = new GdtPortalProvider(factoryMock.Object, options, NullLogger<GdtPortalProvider>.Instance);

            var remoteInv = new RemoteInvoice
            {
                InvoiceSymbol = "1C25TKT",
                InvoiceNumber = "0000001",
                SellerTaxCode = "0101234567"
            };

            // Act
            var result = await provider.DownloadAllInvoiceFilesAsync(remoteInv, "mock_token");

            // Assert
            Assert.True(result.Success);
            Assert.Equal(3, result.Files.Count); // Gói ZIP gốc + XML entry + PDF entry

            var zipFile = result.ZipFile;
            Assert.NotNull(zipFile);
            Assert.Equal(".zip", zipFile.FileExtension);
            Assert.Equal("GDT-PackageZip", zipFile.Source);
            Assert.NotEmpty(zipFile.Sha256Hash);

            var xmlFile = result.XmlFile;
            Assert.NotNull(xmlFile);
            Assert.Equal(".xml", xmlFile.FileExtension);
            Assert.Equal("application/xml", xmlFile.ContentType);

            var pdfFile = result.PdfFile;
            Assert.NotNull(pdfFile);
            Assert.Equal(".pdf", pdfFile.FileExtension);
            Assert.Equal("application/pdf", pdfFile.ContentType);
        }

        [Fact]
        public async Task GdtPortalProvider_DownloadAll_WhenReturnsHtmlError_FiltersOutCorruptedData()
        {
            // Trả về HTML báo lỗi F5 WAF
            var htmlError = "<!DOCTYPE html><html><head><title>Request Rejected</title></head><body>The requested URL was rejected.</body></html>";
            var errorBytes = Encoding.UTF8.GetBytes(htmlError);

            var handlerMock = new Mock<HttpMessageHandler>();
            handlerMock.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new ByteArrayContent(errorBytes)
                });

            var httpClient = new HttpClient(handlerMock.Object)
            {
                BaseAddress = new Uri("https://hoadondientu.gdt.gov.vn/")
            };

            var factoryMock = new Mock<IHttpClientFactory>();
            factoryMock.Setup(f => f.CreateClient(GdtPortalProvider.ClientName)).Returns(httpClient);

            var options = Options.Create(new GdtPortalOptions { RateLimitDelayMs = 0 });
            var provider = new GdtPortalProvider(factoryMock.Object, options, NullLogger<GdtPortalProvider>.Instance);

            var remoteInv = new RemoteInvoice
            {
                InvoiceSymbol = "1C25TKT",
                InvoiceNumber = "0000001",
                SellerTaxCode = "0101234567"
            };

            // Act
            var result = await provider.DownloadAllInvoiceFilesAsync(remoteInv, "mock_token");

            // Assert: Không lưu tệp HTML lỗi
            Assert.False(result.Success);
            Assert.Empty(result.Files);
        }

        [Fact]
        public async Task InvoiceFileService_SaveDownloadedFilesAsync_SavesFilesAndEnforcesTenantIsolation()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "InvoiceManagerTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                    .UseInMemoryDatabase("InvoiceFileTestDb_" + Guid.NewGuid())
                    .Options;

                using var db = new ApplicationDbContext(options);

                var taxAccountA = new TaxAccount { Id = 1, TaxCode = "0101111111", CompanyName = "Company A" };
                var taxAccountB = new TaxAccount { Id = 2, TaxCode = "0102222222", CompanyName = "Company B" };
                db.TaxAccounts.AddRange(taxAccountA, taxAccountB);

                var invoiceA = new Invoice
                {
                    Id = 10,
                    TaxAccountId = 1,
                    InvoiceSymbol = "1C25TKT",
                    InvoiceNumber = "0000001",
                    SellerTaxCode = "0109999999",
                    SellerName = "Seller 1",
                    InvoiceType = "MuaVao"
                };
                db.Invoices.Add(invoiceA);
                await db.SaveChangesAsync();

                var inMemoryConfig = new Dictionary<string, string?>
                {
                    { "InvoiceSettings:StorageRootPath", tempDir }
                };
                var configuration = new ConfigurationBuilder().AddInMemoryCollection(inMemoryConfig).Build();

                var providerMock = new Mock<InvoiceManager.Services.Providers.IInvoiceSourceProvider>();
                var auditLogMock = new Mock<IAuditLogService>();
                var protectorMock = new Mock<ICredentialProtector>();

                var fileService = new InvoiceFileService(
                    db,
                    providerMock.Object,
                    auditLogMock.Object,
                    protectorMock.Object,
                    configuration,
                    NullLogger<InvoiceFileService>.Instance
                );

                var dlResult = new DownloadInvoiceResult();
                dlResult.Files.Add(new DownloadedInvoiceFile
                {
                    FileName = "HD_1C25TKT_0000001.xml",
                    FileExtension = ".xml",
                    ContentType = "application/xml",
                    Content = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><Test>XML</Test>"),
                    Source = "GDT-ExportXml"
                });
                dlResult.Files.Add(new DownloadedInvoiceFile
                {
                    FileName = "HD_1C25TKT_0000001.pdf",
                    FileExtension = ".pdf",
                    ContentType = "application/pdf",
                    Content = Encoding.UTF8.GetBytes("%PDF-1.4\nTest PDF\n%%EOF"),
                    Source = "GDT-ExportPdf"
                });

                // Act 1: Lưu file cho công ty A
                var savedFiles = await fileService.SaveDownloadedFilesAsync(10, 1, dlResult);

                // Assert
                Assert.Equal(2, savedFiles.Count);
                Assert.All(savedFiles, f => Assert.True(File.Exists(f.FilePath)));
                Assert.All(savedFiles, f => Assert.NotEmpty(f.Sha256Hash));

                // Act 2: Công ty B cố gắng tải file của công ty A
                var fileId = savedFiles[0].Id;
                var accessAttemptByB = await fileService.GetFileForDownloadAsync(fileId, taxAccountId: 2);
                Assert.Null(accessAttemptByB); // Bị từ chối do khác TaxAccountId

                // Act 3: Công ty A tải file của mình
                var accessAttemptByA = await fileService.GetFileForDownloadAsync(fileId, taxAccountId: 1);
                Assert.NotNull(accessAttemptByA);
                Assert.Equal("application/xml", accessAttemptByA.Value.ContentType);
                accessAttemptByA.Value.Stream.Dispose();

                // Act 4: Tải trọn bộ ZIP
                var zipPackage = await fileService.DownloadAllFilesAsZipAsync(10, taxAccountId: 1);
                Assert.NotNull(zipPackage);
                Assert.True(zipPackage.Value.ZipBytes.Length > 0);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public async Task InvoiceFileService_GetOrFetchOriginalPdfAsync_FetchesOnDemandAndCachesForSubsequentCalls()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "InvoiceManagerPdfCacheTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                    .UseInMemoryDatabase("InvoicePdfOnDemandDb_" + Guid.NewGuid())
                    .Options;

                using var db = new ApplicationDbContext(options);

                var taxAccount = new TaxAccount { Id = 14, TaxCode = "0317668491", CompanyName = "Test Company" };
                db.TaxAccounts.Add(taxAccount);

                var invoice = new Invoice
                {
                    Id = 247,
                    TaxAccountId = 14,
                    InvoiceSymbol = "C26TUL",
                    InvoiceNumber = "0001702",
                    IssueDate = new DateTime(2026, 10, 28),
                    SellerTaxCode = "0310208576",
                    SellerName = "CÔNG TY TNHH THƯƠNG MẠI HOÁ CHẤT UY LONG",
                    InvoiceType = "MuaVao"
                };
                db.Invoices.Add(invoice);
                await db.SaveChangesAsync();

                var inMemoryConfig = new Dictionary<string, string?>
                {
                    { "InvoiceSettings:StorageRootPath", tempDir }
                };
                var configuration = new ConfigurationBuilder().AddInMemoryCollection(inMemoryConfig).Build();

                byte[] validPdfBytes = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n<< /Type /Catalog >>\nendobj\n%%EOF");

                var providerMock = new Mock<InvoiceManager.Services.Providers.IInvoiceSourceProvider>();
                providerMock
                    .Setup(p => p.FetchOriginalPdfAsync(It.IsAny<RemoteInvoice>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new OriginalPdfFetchResult
                    {
                        Success = true,
                        PdfBytes = validPdfBytes,
                        FileName = "HD_1C26TUL_0001702.pdf",
                        ContentType = "application/pdf",
                        Source = "GDT-ExportPdf",
                        EndpointCalled = "GET https://hoadondientu.gdt.gov.vn/api/query/invoices/export-pdf?nbmst=0310208576&khhdon=C26TUL&shdon=1702&khmshdon=1",
                        HttpStatusCode = 200
                    });

                var auditLogMock = new Mock<IAuditLogService>();
                var protectorMock = new Mock<ICredentialProtector>();

                var fileService = new InvoiceFileService(
                    db,
                    providerMock.Object,
                    auditLogMock.Object,
                    protectorMock.Object,
                    configuration,
                    NullLogger<InvoiceFileService>.Instance
                );

                // Act 1: Lần bấm Xem PDF gốc đầu tiên (chưa đồng bộ file trước)
                var firstResult = await fileService.GetOrFetchOriginalPdfAsync(247, 14, "valid_test_token");

                // Assert 1: Gọi API Tổng cục Thuế, kiểm tra %PDF-, lưu cache và trả về nguyên byte
                Assert.True(firstResult.Success);
                Assert.Equal("GDT-ExportPdf", firstResult.Source);
                Assert.Equal(validPdfBytes, firstResult.PdfBytes);
                Assert.NotEmpty(firstResult.SavedFilePath);
                Assert.True(File.Exists(firstResult.SavedFilePath));
                Assert.NotEmpty(firstResult.Sha256Hash);
                providerMock.Verify(p => p.FetchOriginalPdfAsync(It.IsAny<RemoteInvoice>(), "valid_test_token", It.IsAny<CancellationToken>()), Times.Once);

                // Act 2: Lần bấm Xem PDF gốc thứ hai -> Lấy từ Cache mà không gọi lại API Tổng cục Thuế
                var secondResult = await fileService.GetOrFetchOriginalPdfAsync(247, 14, "valid_test_token");

                // Assert 2: Trả từ Cache, không gọi thêm lần nào tới provider
                Assert.True(secondResult.Success);
                Assert.Equal("Cache", secondResult.Source);
                Assert.Equal(validPdfBytes, secondResult.PdfBytes);
                providerMock.Verify(p => p.FetchOriginalPdfAsync(It.IsAny<RemoteInvoice>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public async Task GdtPortalProvider_FetchOriginalPdfAsync_WhenExportPdfReturns404AndZipHasOnlyXml_ReturnsSpecificErrorAndNeverFakesPdf()
        {
            byte[] xmlBytes = Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?><HDon><DLHDon><TTChung><KHMSHDon>1</KHMSHDon><KHHDon>C26TUL</KHHDon><SHDon>1702</SHDon></TTChung></DLHDon></HDon>");
            byte[] zipBytes;
            using (var ms = new MemoryStream())
            {
                using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
                {
                    var xmlEntry = archive.CreateEntry("invoice.xml");
                    using var entryStream = xmlEntry.Open();
                    entryStream.Write(xmlBytes, 0, xmlBytes.Length);
                }
                zipBytes = ms.ToArray();
            }

            var handlerMock = new Mock<HttpMessageHandler>();
            handlerMock.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.Is<HttpRequestMessage>(req => req.RequestUri != null && req.RequestUri.ToString().Contains("export-pdf")),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.NotFound,
                    Content = new StringContent("{\"timestamp\":\"30/10/2026 19:31:29\",\"status\":404,\"error\":\"Not Found\",\"path\":\"/invoices/export-pdf\"}", Encoding.UTF8, "application/json")
                });

            handlerMock.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.Is<HttpRequestMessage>(req => req.RequestUri != null && req.RequestUri.ToString().Contains("export-xml")),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new ByteArrayContent(zipBytes)
                });

            var httpClient = new HttpClient(handlerMock.Object)
            {
                BaseAddress = new Uri("https://hoadondientu.gdt.gov.vn/")
            };

            var factoryMock = new Mock<IHttpClientFactory>();
            factoryMock.Setup(f => f.CreateClient(GdtPortalProvider.ClientName)).Returns(httpClient);

            var options = Options.Create(new GdtPortalOptions { RateLimitDelayMs = 0 });
            var provider = new GdtPortalProvider(factoryMock.Object, options, NullLogger<GdtPortalProvider>.Instance);

            var remoteInv = new RemoteInvoice
            {
                InvoiceSymbol = "C26TUL",
                InvoiceNumber = "0001702",
                SellerTaxCode = "0310208576",
                IssueDate = new DateTime(2026, 10, 28)
            };

            var result = await provider.FetchOriginalPdfAsync(remoteInv, "mock_token");

            Assert.False(result.Success);
            Assert.Null(result.PdfBytes);
            Assert.Equal("PDF_NOT_PROVIDED_BY_GDT", result.ErrorCode);
            Assert.Contains("khhdon=C26TUL", result.EndpointCalled);
            Assert.Contains("shdon=1702", result.EndpointCalled);
            Assert.Contains("khmshdon=1", result.EndpointCalled);
            Assert.NotNull(result.CompanionPackage);
            Assert.True(result.CompanionPackage!.Success);
            Assert.NotNull(result.CompanionPackage.XmlFile);
        }

        [Fact]
        public async Task InvoiceFileService_GetOrFetchOriginalPdfAsync_WhenNoGdtToken_ReturnsPdfForAlreadyImportedInvoiceAndCachesIt()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "InvoiceManagerNoTokenPdfTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                    .UseInMemoryDatabase("InvoiceNoTokenPdfDb_" + Guid.NewGuid())
                    .Options;

                using var db = new ApplicationDbContext(options);

                var taxAccount14 = new TaxAccount { Id = 14, TaxCode = "0317668491", CompanyName = "CÔNG TY TNHH MÔI TRƯỜNG NDT" };
                var taxAccount15 = new TaxAccount { Id = 15, TaxCode = "0317668491", CompanyName = "CÔNG TY TNHH MÔI TRƯỜNG NDT" };
                db.TaxAccounts.AddRange(taxAccount14, taxAccount15);

                var gdtPdfPath = Path.Combine(tempDir, "GDT_C26TAA_0000123.pdf");
                var officialGdtPdfBytes = Encoding.ASCII.GetBytes("%PDF-1.4\n%Official GDT Package PDF\n%%EOF");
                await File.WriteAllBytesAsync(gdtPdfPath, officialGdtPdfBytes);

                var invoice14 = new Invoice
                {
                    Id = 299,
                    TaxAccountId = 14,
                    InvoiceSymbol = "C26TAA",
                    InvoiceNumber = "0000123",
                    IssueDate = new DateTime(2026, 10, 28),
                    SellerTaxCode = "0310208576",
                    SellerName = "CÔNG TY TNHH THƯƠNG MẠI HOÁ CHẤT UY LONG",
                    BuyerTaxCode = "0317668491",
                    BuyerName = "CÔNG TY TNHH MÔI TRƯỜNG NDT",
                    RawPdfPath = gdtPdfPath,
                    InvoiceType = "MuaVao"
                };

                var invoice15 = new Invoice
                {
                    Id = 300,
                    TaxAccountId = 15,
                    InvoiceSymbol = "C26TAA",
                    InvoiceNumber = "0000123",
                    IssueDate = new DateTime(2026, 10, 28),
                    SellerTaxCode = "0310208576",
                    SellerName = "CÔNG TY TNHH THƯƠNG MẠI HOÁ CHẤT UY LONG",
                    BuyerTaxCode = "0317668491",
                    BuyerName = "CÔNG TY TNHH MÔI TRƯỜNG NDT",
                    InvoiceType = "MuaVao"
                };
                db.Invoices.AddRange(invoice14, invoice15);
                await db.SaveChangesAsync();

                var inMemoryConfig = new Dictionary<string, string?>
                {
                    { "InvoiceSettings:StorageRootPath", tempDir }
                };
                var configuration = new ConfigurationBuilder().AddInMemoryCollection(inMemoryConfig).Build();

                var providerMock = new Mock<InvoiceManager.Services.Providers.IInvoiceSourceProvider>();
                var auditLogMock = new Mock<IAuditLogService>();
                var protectorMock = new Mock<ICredentialProtector>();

                var fileService = new InvoiceFileService(
                    db,
                    providerMock.Object,
                    auditLogMock.Object,
                    protectorMock.Object,
                    configuration,
                    NullLogger<InvoiceFileService>.Instance
                );

                // Act 1: Không truyền GDT token (giả lập phiên GDT đã hết hạn)
                var firstResult = await fileService.GetOrFetchOriginalPdfAsync(300, 15, token: null);

                // Assert 1: Hóa đơn đã lấy vào hệ thống vẫn xem được PDF gốc GDT ngay lập tức mà không cần đăng nhập lại
                Assert.True(firstResult.Success);
                Assert.Equal(officialGdtPdfBytes, firstResult.PdfBytes);
                Assert.True(GdtPortalProvider.IsValidPdfSignature(firstResult.PdfBytes));
                Assert.NotEmpty(firstResult.SavedFilePath!);
                Assert.True(File.Exists(firstResult.SavedFilePath));
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public async Task TryDownloadVendorPdfFromXmlAsync_Viettel_Misa_VinHoadon_Ts24_ReturnsAuthenticSupplierPdf()
        {
            // 1. Viettel SInvoice (VIỆN Y TẾ CÔNG CỘNG TP.HCM - 1C26TAA - 16767)
            var viettelXml = Encoding.UTF8.GetBytes(@"<HDon><DLHDon Id=""ID1""><TTChung><SHDon>16767</SHDon><MSTTCGP>0100109106</MSTTCGP></TTChung><NDHDon><NBan><MST>0301260925</MST></NBan></NDHDon><TTKhac><TTin><DLieu>8PBB72IUFNE8GMD</DLieu><KDLieu>string</KDLieu><TTruong>Mã số bí mật</TTruong></TTin></TTKhac></DLHDon></HDon>");
            var viettelPdf = await GdtPortalProvider.TryDownloadVendorPdfFromXmlAsync(viettelXml);
            Assert.NotNull(viettelPdf);
            Assert.True(GdtPortalProvider.IsValidPdfSignature(viettelPdf));
            Assert.True(viettelPdf!.Length > 100_000);

            // 2. Visnam Vin-HOADON (CÔNG TY CỔ PHẦN CÔNG NGHỆ VISE - 1C26TVS - 202)
            var vinXml = Encoding.UTF8.GetBytes(@"<HDon><DLHDon Id=""ID2""><TTChung><SHDon>202</SHDon><MSTTCGP>0401486901</MSTTCGP></TTChung><NDHDon><NBan><MST>0316802779</MST></NBan></NDHDon><TTKhac><TTin><TTruong>QuanLy_SoBaoMat</TTruong><KDLieu>string</KDLieu><DLieu>1J4DLMD3CMXW</DLieu></TTin></TTKhac></DLHDon></HDon>");
            var vinPdf = await GdtPortalProvider.TryDownloadVendorPdfFromXmlAsync(vinXml);
            Assert.NotNull(vinPdf);
            Assert.True(GdtPortalProvider.IsValidPdfSignature(vinPdf));
            Assert.True(vinPdf!.Length > 50_000);

            // 3. TS24 XuatHoaDon (CÔNG TY TNHH THƯƠNG MẠI HOÁ CHẤT UY LONG - 1C26TUL - 1702)
            var ts24Xml = Encoding.UTF8.GetBytes(@"<HDon><DLHDon Id=""ID3""><TTChung><SHDon>1702</SHDon><MSTTCGP>0309478306</MSTTCGP></TTChung><NDHDon><NBan><MST>0310208576</MST></NBan><TTKhac><TTin><TTruong>MNHDon</TTruong><KDLieu> Chuỗi ký tự </KDLieu><DLieu>2610281151509970317668491</DLieu></TTin></TTKhac></NDHDon></DLHDon></HDon>");
            var ts24Pdf = await GdtPortalProvider.TryDownloadVendorPdfFromXmlAsync(ts24Xml);
            Assert.NotNull(ts24Pdf);
            Assert.True(GdtPortalProvider.IsValidPdfSignature(ts24Pdf));
            Assert.True(ts24Pdf!.Length > 50_000);

            // 4. MISA meInvoice (CÔNG TY TNHH SINH HỌC PHƯƠNG NAM - 1C26TKT - 00001038)
            var misaXml = Encoding.UTF8.GetBytes(@"<HDon><DLHDon Id=""1ZF8CJ890BR3""><TTChung><SHDon>00001038</SHDon><MSTTCGP>0101243150</MSTTCGP></TTChung><NDHDon><NBan><MST>1100821033</MST></NBan></NDHDon><TTKhac><TTin><TTruong>TransactionID</TTruong><KDLieu>String</KDLieu><DLieu>1ZF8CJ890BR3</DLieu></TTin></TTKhac></DLHDon></HDon>");
            var misaPdf = await GdtPortalProvider.TryDownloadVendorPdfFromXmlAsync(misaXml);
            Assert.NotNull(misaPdf);
            Assert.True(GdtPortalProvider.IsValidPdfSignature(misaPdf));
            Assert.True(misaPdf!.Length > 50_000);
        }
    }
}
