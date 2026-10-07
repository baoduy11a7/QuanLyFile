using ClosedXML.Excel;
using InvoiceManager.Data;
using InvoiceManager.Models.Entities;
using InvoiceManager.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace InvoiceManager.Tests
{
    public class ExcelImportValidationTests
    {
        private ApplicationDbContext CreateInMemoryDbContext()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;

            return new ApplicationDbContext(options);
        }

        private Mock<IAuditLogService> CreateMockAuditLog()
        {
            var mock = new Mock<IAuditLogService>();
            mock.Setup(a => a.LogActionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>()))
                .Returns(Task.CompletedTask);
            return mock;
        }

        [Fact]
        public async Task GenerateTemplateAsync_ShouldReturnValidExcelWithTwoSheets()
        {
            // Arrange
            using var db = CreateInMemoryDbContext();
            var auditLog = CreateMockAuditLog();
            var logger = new Mock<ILogger<ExcelImportService>>();
            var service = new ExcelImportService(db, auditLog.Object, logger.Object);

            // Act
            var bytes = await service.GenerateTemplateAsync();

            // Assert
            Assert.NotNull(bytes);
            Assert.True(bytes.Length > 0);

            using var ms = new MemoryStream(bytes);
            using var workbook = new XLWorkbook(ms);

            Assert.Equal(2, workbook.Worksheets.Count);
            Assert.NotNull(workbook.Worksheet("MauNhapHoaDon"));
            Assert.NotNull(workbook.Worksheet("HuongDanSuDung"));
        }

        [Fact]
        public async Task PreviewExcelAsync_WithValidData_ReturnsValidResult()
        {
            // Arrange
            using var db = CreateInMemoryDbContext();
            var auditLog = CreateMockAuditLog();
            var logger = new Mock<ILogger<ExcelImportService>>();
            var service = new ExcelImportService(db, auditLog.Object, logger.Object);

            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add("Data");
            ws.Cell(1, 1).Value = "Ký hiệu";
            ws.Cell(1, 2).Value = "Số HĐ";
            ws.Cell(1, 3).Value = "Ngày lập";
            ws.Cell(1, 4).Value = "MST Người bán";
            ws.Cell(1, 5).Value = "Tên Người bán";
            ws.Cell(1, 6).Value = "Chưa thuế";
            ws.Cell(1, 7).Value = "Tiền thuế";
            ws.Cell(1, 8).Value = "Thanh toán";

            // Row 2: Valid
            ws.Cell(2, 1).Value = "1C25TXX";
            ws.Cell(2, 2).Value = "00000123";
            ws.Cell(2, 3).Value = "15/01/2026";
            ws.Cell(2, 4).Value = "0101234567";
            ws.Cell(2, 5).Value = "Công ty ABC";
            ws.Cell(2, 6).Value = 1000000;
            ws.Cell(2, 7).Value = 100000;
            ws.Cell(2, 8).Value = 1100000;

            using var ms = new MemoryStream();
            workbook.SaveAs(ms);
            ms.Position = 0;

            // Act
            var preview = await service.PreviewExcelAsync(ms, 1, "MuaVao");

            // Assert
            Assert.True(preview.Success);
            Assert.Equal(1, preview.TotalRows);
            Assert.Equal(1, preview.ValidCount);
            Assert.Equal(0, preview.ErrorCount);

            var row = preview.Rows[0];
            Assert.True(row.IsValid);
            Assert.Equal("1C25TXX", row.InvoiceSymbol);
            Assert.Equal("00000123", row.InvoiceNumber);
            Assert.Equal("0101234567", row.SellerTaxCode);
            Assert.Equal(1000000m, row.AmountBeforeTax);
            Assert.Equal(1100000m, row.TotalAmount);
        }

        [Fact]
        public async Task PreviewExcelAsync_WithInvalidDateAndTaxCode_MarksRowAsInvalid()
        {
            // Arrange
            using var db = CreateInMemoryDbContext();
            var auditLog = CreateMockAuditLog();
            var logger = new Mock<ILogger<ExcelImportService>>();
            var service = new ExcelImportService(db, auditLog.Object, logger.Object);

            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add("Data");
            ws.Cell(1, 1).Value = "Ký hiệu";
            ws.Cell(1, 2).Value = "Số HĐ";
            ws.Cell(1, 3).Value = "Ngày lập";
            ws.Cell(1, 4).Value = "MST Người bán";
            ws.Cell(1, 5).Value = "Tên Người bán";

            // Row 2: Invalid Date & Invalid Tax Code
            ws.Cell(2, 1).Value = "1C25TXX";
            ws.Cell(2, 2).Value = "00000124";
            ws.Cell(2, 3).Value = "invalid-date-format";
            ws.Cell(2, 4).Value = "12345"; // < 10 digits
            ws.Cell(2, 5).Value = "Công ty XYZ";

            using var ms = new MemoryStream();
            workbook.SaveAs(ms);
            ms.Position = 0;

            // Act
            var preview = await service.PreviewExcelAsync(ms, 1, "MuaVao");

            // Assert
            Assert.True(preview.Success);
            Assert.Equal(1, preview.TotalRows);
            Assert.Equal(0, preview.ValidCount);
            Assert.Equal(1, preview.ErrorCount);

            var row = preview.Rows[0];
            Assert.False(row.IsValid);
            Assert.Contains(row.ErrorMessages, e => e.Contains("Ngày lập"));
            Assert.Contains(row.ErrorMessages, e => e.Contains("Mã số thuế"));
        }

        [Fact]
        public async Task PreviewExcelAsync_WithDuplicateInDb_MarksRowAsDuplicate()
        {
            // Arrange
            using var db = CreateInMemoryDbContext();
            // Seed 1 invoice in DB
            db.Invoices.Add(new Invoice
            {
                TaxAccountId = 1,
                InvoiceType = "MuaVao",
                SellerTaxCode = "0101234567",
                InvoiceSymbol = "1C25TXX",
                InvoiceNumber = "00000123",
                SellerName = "Công ty đã có",
                TotalAmount = 500000
            });
            await db.SaveChangesAsync();

            var auditLog = CreateMockAuditLog();
            var logger = new Mock<ILogger<ExcelImportService>>();
            var service = new ExcelImportService(db, auditLog.Object, logger.Object);

            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add("Data");
            ws.Cell(1, 1).Value = "Ký hiệu";
            ws.Cell(1, 2).Value = "Số HĐ";
            ws.Cell(1, 3).Value = "Ngày lập";
            ws.Cell(1, 4).Value = "MST Người bán";
            ws.Cell(1, 5).Value = "Tên Người bán";

            // Row 2: Duplicates the seeded invoice
            ws.Cell(2, 1).Value = "1C25TXX";
            ws.Cell(2, 2).Value = "00000123";
            ws.Cell(2, 3).Value = "15/01/2026";
            ws.Cell(2, 4).Value = "0101234567";
            ws.Cell(2, 5).Value = "Công ty ABC";

            using var ms = new MemoryStream();
            workbook.SaveAs(ms);
            ms.Position = 0;

            // Act
            var preview = await service.PreviewExcelAsync(ms, 1, "MuaVao");

            // Assert
            Assert.Equal(1, preview.TotalRows);
            Assert.Equal(1, preview.ErrorCount);
            Assert.False(preview.Rows[0].IsValid);
            Assert.Contains(preview.Rows[0].ErrorMessages, e => e.Contains("đã tồn tại"));
        }

        [Fact]
        public async Task PreviewExcelAsync_WithInternalDuplicates_MarksSecondRowAsDuplicate()
        {
            // Arrange
            using var db = CreateInMemoryDbContext();
            var auditLog = CreateMockAuditLog();
            var logger = new Mock<ILogger<ExcelImportService>>();
            var service = new ExcelImportService(db, auditLog.Object, logger.Object);

            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add("Data");
            ws.Cell(1, 1).Value = "Ký hiệu";
            ws.Cell(1, 2).Value = "Số HĐ";
            ws.Cell(1, 3).Value = "Ngày lập";
            ws.Cell(1, 4).Value = "MST Người bán";
            ws.Cell(1, 5).Value = "Tên Người bán";

            // Row 2
            ws.Cell(2, 1).Value = "1C25TXX";
            ws.Cell(2, 2).Value = "00000123";
            ws.Cell(2, 3).Value = "15/01/2026";
            ws.Cell(2, 4).Value = "0101234567";
            ws.Cell(2, 5).Value = "Công ty ABC";

            // Row 3: Same key as Row 2
            ws.Cell(3, 1).Value = "1C25TXX";
            ws.Cell(3, 2).Value = "00000123";
            ws.Cell(3, 3).Value = "16/01/2026";
            ws.Cell(3, 4).Value = "0101234567";
            ws.Cell(3, 5).Value = "Công ty ABC";

            using var ms = new MemoryStream();
            workbook.SaveAs(ms);
            ms.Position = 0;

            // Act
            var preview = await service.PreviewExcelAsync(ms, 1, "MuaVao");

            // Assert
            Assert.Equal(2, preview.TotalRows);
            Assert.True(preview.Rows[0].IsValid);
            Assert.False(preview.Rows[1].IsValid);
            Assert.Contains(preview.Rows[1].ErrorMessages, e => e.Contains("Trùng lặp với một dòng khác trong cùng file"));
        }

        [Fact]
        public async Task PreviewExcelAsync_WithReorderedColumns_MapsCorrectly()
        {
            // Arrange: Header-based mapping test
            using var db = CreateInMemoryDbContext();
            var auditLog = CreateMockAuditLog();
            var logger = new Mock<ILogger<ExcelImportService>>();
            var service = new ExcelImportService(db, auditLog.Object, logger.Object);

            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add("Data");
            // Random column order
            ws.Cell(1, 1).Value = "Tên Người bán";
            ws.Cell(1, 2).Value = "MST Người bán";
            ws.Cell(1, 3).Value = "Số HĐ";
            ws.Cell(1, 4).Value = "Ký hiệu";
            ws.Cell(1, 5).Value = "Ngày lập";

            ws.Cell(2, 1).Value = "Công ty TNHH Báo Mới";
            ws.Cell(2, 2).Value = "0309876543";
            ws.Cell(2, 3).Value = "999";
            ws.Cell(2, 4).Value = "2C25TKT";
            ws.Cell(2, 5).Value = "22/02/2026";

            using var ms = new MemoryStream();
            workbook.SaveAs(ms);
            ms.Position = 0;

            // Act
            var preview = await service.PreviewExcelAsync(ms, 1, "MuaVao");

            // Assert
            Assert.True(preview.Success);
            Assert.Equal(1, preview.ValidCount);
            var row = preview.Rows[0];
            Assert.Equal("2C25TKT", row.InvoiceSymbol);
            Assert.Equal("0000999", row.InvoiceNumber); // normalized with padding
            Assert.Equal("0309876543", row.SellerTaxCode);
            Assert.Equal("Công ty TNHH Báo Mới", row.SellerName);
        }

        [Fact]
        public async Task PreviewExcelAsync_WithRealWorldTaxDeclarationFormat_MapsAndValidatesSuccessfully()
        {
            // Arrange: Mô phỏng chính xác cấu trúc file MUA_VAO_Q2.xlsx của người dùng
            using var db = CreateInMemoryDbContext();
            var auditLog = CreateMockAuditLog();
            var logger = new Mock<ILogger<ExcelImportService>>();
            var service = new ExcelImportService(db, auditLog.Object, logger.Object);

            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add("Sheet1");

            // Row 1-3: Tiêu đề công ty
            ws.Cell(1, 1).Value = "HÓA ĐƠN MUA VÀO";
            ws.Cell(2, 1).Value = "MST: 0317748323";
            ws.Cell(3, 1).Value = "Tên DN: CÔNG TY TNHH CƠ KHÍ XÂY DỰNG ĐÔ THỊ HÀ NỘI";

            // Row 4: Header thực tế từ file của user (ngắn gọn: Người bán, Ngày, Số, Tiền Chưa thuế...)
            ws.Cell(4, 1).Value = "Loại HĐ";
            ws.Cell(4, 2).Value = "MST người bán";
            ws.Cell(4, 3).Value = "Người bán";
            ws.Cell(4, 4).Value = "Địa chỉ người bán";
            ws.Cell(4, 5).Value = "MST người mua";
            ws.Cell(4, 6).Value = "Người Mua";
            ws.Cell(4, 7).Value = "Địa chỉ người mua";
            ws.Cell(4, 8).Value = "Ngày";
            ws.Cell(4, 9).Value = "HTTT";
            ws.Cell(4, 10).Value = "Ký hiệu";
            ws.Cell(4, 11).Value = "Số";
            ws.Cell(4, 12).Value = "Trạng thái HĐ";
            ws.Cell(4, 13).Value = "Kết quả kiểm tra";
            ws.Cell(4, 14).Value = "Tiền Chưa thuế";
            ws.Cell(4, 15).Value = "Tiền Thuế";

            // Row 5: Hóa đơn 1 (Máy tính tiền, MST 10 số, ngày 4/1/2026, thuế 0)
            ws.Cell(5, 1).Value = "V";
            ws.Cell(5, 2).Value = "0302028844";
            ws.Cell(5, 3).Value = "DOANH NGHIỆP TƯ NHÂN NGUYÊN LAI";
            ws.Cell(5, 4).Value = "TP. Hồ Chí Minh";
            ws.Cell(5, 8).Value = "4/1/2026";
            ws.Cell(5, 10).Value = "1C26MNL";
            ws.Cell(5, 11).Value = "147428";
            ws.Cell(5, 14).Value = 800040.00;
            ws.Cell(5, 15).Value = 0.00;

            // Row 6: Hóa đơn 2 (MST chi nhánh 10-3 số: 0107307812-002)
            ws.Cell(6, 1).Value = "V";
            ws.Cell(6, 2).Value = "0107307812-002";
            ws.Cell(6, 3).Value = "CÔNG TY CỔ PHẦN GIAO NHẬN HÀNG HÓA NASCO - CHI NHÁNH MIỀN NAM";
            ws.Cell(6, 8).Value = "4/9/2026";
            ws.Cell(6, 10).Value = "1C26TNG";
            ws.Cell(6, 11).Value = "2186";
            ws.Cell(6, 14).Value = 164475.00;
            ws.Cell(6, 15).Value = 13158.00;

            // Row 7: Hóa đơn 3 (MST 12 số CCCD cá nhân/hộ kinh doanh: 031183009618)
            ws.Cell(7, 1).Value = "V";
            ws.Cell(7, 2).Value = "031183009618";
            ws.Cell(7, 3).Value = "HỘ KINH DOANH NGUYỄN THỊ HUYỀN LÃNG";
            ws.Cell(7, 8).Value = "4/7/2026";
            ws.Cell(7, 10).Value = "2C26THL";
            ws.Cell(7, 11).Value = "5";
            ws.Cell(7, 14).Value = 600000.00;
            ws.Cell(7, 15).Value = 0.00;

            using var ms = new MemoryStream();
            workbook.SaveAs(ms);
            ms.Position = 0;

            // Act
            var preview = await service.PreviewExcelAsync(ms, 1, "MuaVao");

            // Assert
            Assert.True(preview.Success, preview.ErrorMessage);
            var readRowsDesc = string.Join("; ", preview.Rows.Select(r => $"Row {r.RowIndex}: {r.InvoiceSymbol}-{r.InvoiceNumber}"));
            Assert.True(preview.TotalRows == 3, $"Expected 3 rows but got {preview.TotalRows}. Read rows: [{readRowsDesc}]");

            // Kiểm tra dòng 1
            var row1 = preview.Rows[0];
            Assert.True(row1.IsValid);
            Assert.Equal("1C26MNL", row1.InvoiceSymbol);
            Assert.Equal("0147428", row1.InvoiceNumber);
            Assert.Equal("0302028844", row1.SellerTaxCode);
            Assert.Equal("DOANH NGHIỆP TƯ NHÂN NGUYÊN LAI", row1.SellerName);
            Assert.Equal(800040m, row1.TotalAmount); // Tự động tính Chưa thuế + Thuế khi không có cột Tổng

            // Kiểm tra dòng 2 (Chi nhánh 10-3 số)
            var row2 = preview.Rows[1];
            Assert.True(row2.IsValid);
            Assert.Equal("0107307812-002", row2.SellerTaxCode);
            Assert.Equal("0002186", row2.InvoiceNumber);

            // Kiểm tra dòng 3 (MST 12 số CCCD)
            var row3 = preview.Rows[2];
            Assert.True(row3.IsValid);
            Assert.Equal("031183009618", row3.SellerTaxCode);
            Assert.Equal("0000005", row3.InvoiceNumber);
        }
    }
}
