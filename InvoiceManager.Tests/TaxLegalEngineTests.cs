using ClosedXML.Excel;
using InvoiceManager.Data;
using InvoiceManager.Models.Entities;
using InvoiceManager.Models.ViewModels;
using InvoiceManager.Services;
using InvoiceManager.Services.TaxEngine;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace InvoiceManager.Tests
{
    public class TaxLegalEngineTests : IDisposable
    {
        private readonly ApplicationDbContext _db;
        private readonly ITaxFormRegistry _registry;
        private readonly ITaxExpressionEvaluator _evaluator;
        private readonly TaxDeclarationService _taxService;

        public TaxLegalEngineTests()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;

            _db = new ApplicationDbContext(options);
            _registry = new TaxFormRegistry();
            _evaluator = new TaxExpressionEvaluator();
            _taxService = new TaxDeclarationService(_db, _registry, _evaluator);

            SeedCompany();
        }

        public void Dispose()
        {
            _db.Database.EnsureDeleted();
            _db.Dispose();
        }

        private void SeedCompany()
        {
            _db.TaxAccounts.Add(new TaxAccount
            {
                Id = 1,
                TaxCode = "0317668491",
                CompanyName = "CÔNG TY TNHH MÔI TRƯỜNG NDT",
                Address = "490/1 An Dương Vương, Phường Bình Phú, TP Hồ Chí Minh",
                Email = "moitruongndt@gmail.com",
                IsActive = true
            });
            _db.SaveChanges();
        }

        [Fact]
        public void Test1_DetectCyclicDependency_ThrowsInvalidOperationException()
        {
            // Kiểm tra khả năng phát hiện phụ thuộc vòng tuần hoàn (Cyclic Dependency)
            var formulas = new Dictionary<string, string>
            {
                { "[23]", "[24] + 100" },
                { "[24]", "[25] * 2" },
                { "[25]", "[23] - 50" } // Phụ thuộc vòng: 23 -> 24 -> 25 -> 23
            };

            var ex = Assert.Throws<InvalidOperationException>(() =>
            {
                _evaluator.DetectCyclicDependencies(formulas);
            });

            Assert.Contains("Phát hiện phụ thuộc vòng", ex.Message);
        }

        [Fact]
        public void Test2_ExpressionEvaluator_CalculatesCorrectDecimalValues()
        {
            // Kiểm tra engine tính toán trên Decimal không dùng eval
            var context = new Dictionary<string, decimal>
            {
                { "[23]", 100000000m },
                { "[24]", 10000000m },
                { "[25]", 10000000m },
                { "[35]", 25000000m },
                { "[22]", 5000000m }
            };

            // Công thức thuế phải nộp: MAX(0, [35] - [25] - [22])
            var resultTaxToPay = _evaluator.Evaluate("MAX(0, [35] - [25] - [22])", context);
            Assert.Equal(10000000m, resultTaxToPay);

            // Công thức thuế còn được khấu trừ: MAX(0, ([25] + [22]) - [35])
            var resultCarriedOver = _evaluator.Evaluate("MAX(0, ([25] + [22]) - [35])", context);
            Assert.Equal(0m, resultCarriedOver);
        }

        [Fact]
        public void Test3_TransitionPeriod_ResolvesCorrectFormVersion()
        {
            // Kiểm tra kỳ chuyển giao: Trước 01/07/2026 áp dụng TT 80/2021/TT-BTC
            var q2_2026_From = new DateTime(2026, 4, 1);
            var q2_2026_To = new DateTime(2026, 6, 30);
            var versionQ2 = _registry.ResolveVersionForPeriod(q2_2026_From, q2_2026_To);

            Assert.NotNull(versionQ2);
            Assert.Equal("01_GTGT_TT80", versionQ2.Code);
            Assert.Contains("80/2021/TT-BTC", versionQ2.LegalDocRef);

            // Từ 01/07/2026 áp dụng TT 89/2026/TT-BTC
            var q3_2026_From = new DateTime(2026, 7, 1);
            var q3_2026_To = new DateTime(2026, 9, 30);
            var versionQ3 = _registry.ResolveVersionForPeriod(q3_2026_From, q3_2026_To);

            Assert.NotNull(versionQ3);
            Assert.Equal("01_GTGT_TT89", versionQ3.Code);
            Assert.Contains("89/2026/TT-BTC", versionQ3.LegalDocRef);
        }

        [Fact]
        public void Test4_CashPaymentThreshold_ChangesFrom20MTo5MAfterJuly2026()
        {
            // Ngưỡng thanh toán không dùng tiền mặt:
            // Trước 01/07/2026 là 20.000.000 VNĐ
            var thresholdBefore = _registry.GetParameterValue("CashPaymentThreshold", new DateTime(2026, 6, 15));
            Assert.Equal(20000000m, thresholdBefore);

            // Từ 01/07/2026 là 5.000.000 VNĐ theo Luật Thuế GTGT 48/2024/QH15
            var thresholdAfter = _registry.GetParameterValue("CashPaymentThreshold", new DateTime(2026, 7, 15));
            Assert.Equal(5000000m, thresholdAfter);
        }

        [Fact]
        public async Task Test5_ZeroActivityPeriod_LocksAllFieldsAndCarriesForwardTax()
        {
            // Case 1: Kỳ không phát sinh hoạt động mua bán ([21] = true)
            // Nhập thuế kỳ trước chuyển sang [22] = 15.000.000đ
            var decl = await _taxService.CalculateDeclarationAsync(
                taxAccountId: 1,
                periodType: "Quarter",
                periodValue: 3,
                year: 2026,
                carriedOverTax: 15000000m,
                isZeroActivity: true);

            Assert.True(decl.Indicator21_NoActivity);
            Assert.Equal(15000000m, decl.Target22_CarriedOverDeduction); // [22]
            Assert.Equal(0m, decl.Target23_PurchaseAmountBeforeTax); // [23] = 0
            Assert.Equal(0m, decl.Target24_PurchaseTaxAmount); // [24] = 0
            Assert.Equal(0m, decl.Target25_DeductibleTaxAmount); // [25] = 0
            Assert.Equal(0m, decl.Target34_TotalSalesAmount); // [34] = 0
            Assert.Equal(0m, decl.Target35_TotalSalesTax); // [35] = 0
            Assert.Equal(0m, decl.Target40_TaxToPay); // [40] = 0
            Assert.Equal(15000000m, decl.Target43_CarriedForwardDeduction); // [43] = [22]
            Assert.Contains("89/2026/TT-BTC", decl.LegalDocRef);
        }

        [Fact]
        public async Task Test6_FullTaxRatesAndNQ110Reduction_CalculatesProperly()
        {
            // Case 3: Đầy đủ thuế suất 0%, 5%, 8% (NQ 110), 10%
            // Hóa đơn đầu vào hợp lệ
            _db.Invoices.Add(new Invoice
            {
                Id = 10,
                TaxAccountId = 1,
                InvoiceType = "MuaVao",
                InvoiceSymbol = "1C26TAA",
                InvoiceNumber = "0000101",
                IssueDate = new DateTime(2026, 8, 10),
                SellerTaxCode = "0100109106",
                SellerName = "NHÀ CUNG CẤP A",
                BuyerTaxCode = "0317668491",
                AmountBeforeTax = 100000000m,
                TaxAmount = 10000000m,
                TotalAmount = 110000000m,
                Status = "Hóa đơn gốc",
                PaymentMethod = "Chuyển khoản"
            });

            // Hóa đơn đầu ra: 0%
            _db.Invoices.Add(new Invoice
            {
                Id = 20,
                TaxAccountId = 1,
                InvoiceType = "BanRa",
                InvoiceSymbol = "1C26TBB",
                InvoiceNumber = "0000201",
                IssueDate = new DateTime(2026, 8, 15),
                SellerTaxCode = "0317668491",
                BuyerTaxCode = "0109998888",
                BuyerName = "KHÁCH HÀNG XUẤT KHẨU",
                AmountBeforeTax = 50000000m,
                TaxAmount = 0m,
                TotalAmount = 50000000m,
                Status = "Hóa đơn gốc",
                Details = new List<InvoiceDetail>
                {
                    new InvoiceDetail { LineNumber = 1, ItemName = "Dịch vụ XK", TaxRate = 0, AmountBeforeTax = 50000000m, TaxAmount = 0m, TotalAmount = 50000000m }
                }
            });

            // Hóa đơn đầu ra: 5%
            _db.Invoices.Add(new Invoice
            {
                Id = 21,
                TaxAccountId = 1,
                InvoiceType = "BanRa",
                InvoiceSymbol = "1C26TBB",
                InvoiceNumber = "0000202",
                IssueDate = new DateTime(2026, 8, 16),
                SellerTaxCode = "0317668491",
                BuyerTaxCode = "0109998889",
                BuyerName = "KHÁCH HÀNG NƯỚC SẠCH",
                AmountBeforeTax = 20000000m,
                TaxAmount = 1000000m,
                TotalAmount = 21000000m,
                Status = "Hóa đơn gốc",
                Details = new List<InvoiceDetail>
                {
                    new InvoiceDetail { LineNumber = 1, ItemName = "Nước sạch", TaxRate = 5, AmountBeforeTax = 20000000m, TaxAmount = 1000000m, TotalAmount = 21000000m }
                }
            });

            // Hóa đơn đầu ra: 8% (Theo Nghị quyết 110)
            _db.Invoices.Add(new Invoice
            {
                Id = 22,
                TaxAccountId = 1,
                InvoiceType = "BanRa",
                InvoiceSymbol = "1C26TBB",
                InvoiceNumber = "0000203",
                IssueDate = new DateTime(2026, 8, 17),
                SellerTaxCode = "0317668491",
                BuyerTaxCode = "0109998890",
                BuyerName = "KHÁCH HÀNG HÓA CHẤT",
                AmountBeforeTax = 100000000m,
                TaxAmount = 8000000m,
                TotalAmount = 108000000m,
                Status = "Hóa đơn gốc",
                Details = new List<InvoiceDetail>
                {
                    new InvoiceDetail { LineNumber = 1, ItemName = "Hàng giảm 8%", TaxRate = 8, AmountBeforeTax = 100000000m, TaxAmount = 8000000m, TotalAmount = 108000000m }
                }
            });

            // Hóa đơn đầu ra: 10%
            _db.Invoices.Add(new Invoice
            {
                Id = 23,
                TaxAccountId = 1,
                InvoiceType = "BanRa",
                InvoiceSymbol = "1C26TBB",
                InvoiceNumber = "0000204",
                IssueDate = new DateTime(2026, 8, 18),
                SellerTaxCode = "0317668491",
                BuyerTaxCode = "0109998891",
                BuyerName = "KHÁCH HÀNG XỬ LÝ MÔI TRƯỜNG",
                AmountBeforeTax = 80000000m,
                TaxAmount = 8000000m,
                TotalAmount = 88000000m,
                Status = "Hóa đơn gốc",
                Details = new List<InvoiceDetail>
                {
                    new InvoiceDetail { LineNumber = 1, ItemName = "Dịch vụ 10%", TaxRate = 10, AmountBeforeTax = 80000000m, TaxAmount = 8000000m, TotalAmount = 88000000m }
                }
            });

            _db.SaveChanges();

            var decl = await _taxService.CalculateDeclarationAsync(
                taxAccountId: 1,
                periodType: "Quarter",
                periodValue: 3,
                year: 2026,
                carriedOverTax: 2000000m); // [22] = 2.000.000đ

            // Kiểm tra doanh thu đầu ra
            // [27] (0%) = 50.000.000
            // [28] (5%) = 20.000.000, [29] = 1.000.000
            // [32] (10%) = 80.000.000, [32a] = 8.000.000
            // [30] (8% NQ110) = 100.000.000, [31] = 8.000.000
            // [34] (Tổng doanh thu) = 50m + 20m + 80m + 100m = 250.000.000
            // [35] = [29] + [31] + [32a] = 1m + 8m + 8m = 17.000.000
            Assert.Equal(50000000m, decl.Target27_SalesAmount5);
            Assert.Equal(20000000m, decl.Target28_SalesTax5);
            Assert.Equal(1000000m, decl.Target29_SalesAmount8And10);
            Assert.Equal(80000000m, decl.Target32_SalesAmount10);
            Assert.Equal(8000000m, decl.Target32a_SalesTax10);
            Assert.Equal(100000000m, decl.Sales8Amount);
            Assert.Equal(8000000m, decl.Sales8Tax);
            Assert.Equal(250000000m, decl.Target34_TotalSalesAmount);
            Assert.Equal(17000000m, decl.Target35_TotalSalesTax);

            // Đầu vào khấu trừ: [25] = 10.000.000, [22] = 2.000.000 => Tổng khấu trừ = 12.000.000
            // Thuế phải nộp: [40] = [35] - ([25] + [22]) = 17.000.000 - 12.000.000 = 5.000.000
            Assert.Equal(10000000m, decl.Target25_DeductibleTaxAmount);
            Assert.Equal(5000000m, decl.Target40_TaxToPay);
            Assert.Equal(0m, decl.Target43_CarriedForwardDeduction);

            // Kiểm tra Phụ lục giảm thuế theo NQ 110:
            // Thuế đã giảm 2% của 100.000.000 = 2.000.000
            Assert.NotEmpty(decl.Tax8PercentReductionItems);
            Assert.Single(decl.Tax8PercentReductionItems);
            Assert.Equal(2000000m, decl.Sales8ReducedTax);

            // Kiểm tra Xuất XML & Excel
            var xmlString = _taxService.GenerateDeclarationXml(decl);
            Assert.NotNull(xmlString);
            Assert.Contains("0317668491", xmlString);
            Assert.Contains("CÔNG TY TNHH MÔI TRƯỜNG NDT", xmlString);
            Assert.Contains("<ct40>5000000</ct40>", xmlString);

            var excelBytes = await _taxService.ExportDeclarationExcelAsync(decl, 1);
            Assert.NotNull(excelBytes);
            Assert.True(excelBytes.Length > 0);

            using var ms = new MemoryStream(excelBytes);
            using var wb = new XLWorkbook(ms);
            Assert.NotNull(wb.Worksheet("Tờ khai 01_GTGT"));
            Assert.NotNull(wb.Worksheet("Bảng kê mua vào 01-2"));
            Assert.NotNull(wb.Worksheet("Bảng kê bán ra 01-1"));
            Assert.NotNull(wb.Worksheet("Phụ lục 8% NQ110"));

            // Đảm bảo thông tin công ty đúng yêu cầu
            var wsHeader = wb.Worksheet("Bảng kê mua vào 01-2");
            var cellCompany = wsHeader.Cell(1, 1).GetString();
            Assert.Contains("CÔNG TY TNHH MÔI TRƯỜNG NDT", cellCompany);
        }

        [Fact]
        public async Task Test7_CashInvoiceExceedingThreshold_TriggersWarning()
        {
            // Case 4: Hóa đơn tiền mặt >= 5.000.000đ ở kỳ Quý 3/2026 (sau 01/07/2026)
            _db.Invoices.Add(new Invoice
            {
                Id = 30,
                TaxAccountId = 1,
                InvoiceType = "MuaVao",
                InvoiceSymbol = "1C26TAA",
                InvoiceNumber = "0000301",
                IssueDate = new DateTime(2026, 8, 20),
                SellerTaxCode = "0301260925",
                SellerName = "VIỆN Y TẾ CÔNG CỘNG TP HCM",
                BuyerTaxCode = "0317668491",
                AmountBeforeTax = 8000000m,
                TaxAmount = 800000m,
                TotalAmount = 8800000m, // >= 5.000.000đ
                Status = "Hóa đơn gốc",
                PaymentMethod = "Tiền mặt" // Thanh toán tiền mặt
            });
            _db.SaveChanges();

            var decl = await _taxService.CalculateDeclarationAsync(
                taxAccountId: 1,
                periodType: "Quarter",
                periodValue: 3,
                year: 2026);

            // Kiểm tra cảnh báo pháp luật
            Assert.NotEmpty(decl.ValidationAlerts);
            var cashWarning = decl.ValidationAlerts.FirstOrDefault(a => a.Contains("không đủ điều kiện khấu trừ") || a.Contains("tiền mặt"));
            Assert.NotNull(cashWarning);
            Assert.True(cashWarning.Contains("5,000,000") || cashWarning.Contains("5.000.000"));
        }
    }
}
