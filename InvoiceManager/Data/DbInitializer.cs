using System.IO;
using InvoiceManager.Services.Parsers;
using InvoiceManager.Models.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace InvoiceManager.Data
{
    public static class DbInitializer
    {
        public static async Task InitializeAsync(
            ApplicationDbContext context,
            UserManager<ApplicationUser> userManager,
            RoleManager<IdentityRole> roleManager)
        {
            if (context.Database.IsSqlServer())
            {
                await context.Database.MigrateAsync();
            }
            else
            {
                await context.Database.EnsureCreatedAsync();
            }

            // Đảm bảo các cột mới (BusinessType, ShortName, AccountantName) luôn tồn tại trên mọi hệ CSDL (SQL Server, SQLite, PostgreSQL)
            await EnsureTaxAccountColumnsAsync(context);

            // Đảm bảo các cột mới phục vụ quản lý hóa đơn chi tiết & toàn diện luôn tồn tại
            await EnsureInvoiceColumnsAsync(context);

            // Tự động chuẩn hóa & khắc phục lỗi ngày lập, tên hàng hóa, đơn vị tính cho các hóa đơn hiện có
            await RepairExistingInvoicesAsync(context);

            // 1. Seed Roles
            string[] roles = { "Admin", "Accountant", "Viewer" };
            foreach (var role in roles)
            {
                if (!await roleManager.RoleExistsAsync(role))
                {
                    await roleManager.CreateAsync(new IdentityRole(role));
                }
            }

            // 2. Seed Users
            var adminUser = await userManager.FindByEmailAsync("admin@invoicemanager.vn");
            if (adminUser == null)
            {
                adminUser = new ApplicationUser
                {
                    UserName = "admin@invoicemanager.vn",
                    Email = "admin@invoicemanager.vn",
                    FullName = "Nguyễn Văn Quản Trị (Admin)",
                    EmailConfirmed = true,
                    IsActive = true,
                    CreatedAt = DateTime.Now
                };
                var result = await userManager.CreateAsync(adminUser, "Admin@123456");
                if (result.Succeeded)
                {
                    await userManager.AddToRoleAsync(adminUser, "Admin");
                }
            }

            var accountantUser = await userManager.FindByEmailAsync("ketoan@invoicemanager.vn");
            if (accountantUser == null)
            {
                accountantUser = new ApplicationUser
                {
                    UserName = "ketoan@invoicemanager.vn",
                    Email = "ketoan@invoicemanager.vn",
                    FullName = "Trần Thị Mai (Kế toán trưởng)",
                    EmailConfirmed = true,
                    IsActive = true,
                    CreatedAt = DateTime.Now
                };
                var result = await userManager.CreateAsync(accountantUser, "Ketoan@123456");
                if (result.Succeeded)
                {
                    await userManager.AddToRoleAsync(accountantUser, "Accountant");
                }
            }

            var viewerUser = await userManager.FindByEmailAsync("viewer@invoicemanager.vn");
            if (viewerUser == null)
            {
                viewerUser = new ApplicationUser
                {
                    UserName = "viewer@invoicemanager.vn",
                    Email = "viewer@invoicemanager.vn",
                    FullName = "Lê Hoàng Nam (Giám sát / Kiểm toán)",
                    EmailConfirmed = true,
                    IsActive = true,
                    CreatedAt = DateTime.Now
                };
                var result = await userManager.CreateAsync(viewerUser, "Viewer@123456");
                if (result.Succeeded)
                {
                    await userManager.AddToRoleAsync(viewerUser, "Viewer");
                }
            }

            // 3. Seed Tax Accounts (Công ty)
            if (!await context.TaxAccounts.AnyAsync())
            {
                var company1 = new TaxAccount
                {
                    TaxCode = "0101234567",
                    BusinessType = "DoanhNghiep",
                    ShortName = "AN PHÁT TECH",
                    AccountantName = "Trần Thị Mai",
                    CompanyName = "CÔNG TY TNHH CÔNG NGHỆ VÀ DỊCH VỤ AN PHÁT",
                    Address = "Tầng 8, Tòa nhà Keangnam Landmark 72, Đường Phạm Hùng, Phường Mễ Trì, Quận Nam Từ Liêm, TP Hà Nội",
                    Email = "ketoan@anphat-tech.vn",
                    PhoneNumber = "0243.888.9999",
                    Representative = "Nguyễn Văn An",
                    IsActive = true,
                    CreatedAt = DateTime.Now.AddMonths(-12)
                };

                var company2 = new TaxAccount
                {
                    TaxCode = "0312987654",
                    BusinessType = "DoanhNghiep",
                    ShortName = "VIỆT THÀNH CORP",
                    AccountantName = "Trần Thị Mai",
                    CompanyName = "CÔNG TY CỔ PHẦN THƯƠNG MẠI SẢN XUẤT VIỆT THÀNH",
                    Address = "Số 120 Đường Nguyễn Thị Minh Khai, Phường Võ Thị Sáu, Quận 3, TP Hồ Chí Minh",
                    Email = "contact@vietthanhcorp.vn",
                    PhoneNumber = "0283.777.6666",
                    Representative = "Lê Thị Bích Thành",
                    IsActive = true,
                    CreatedAt = DateTime.Now.AddMonths(-6)
                };

                var company3 = new TaxAccount
                {
                    TaxCode = "0314094922",
                    BusinessType = "DoanhNghiep",
                    ShortName = "BẢO DUY TECH",
                    AccountantName = "Trần Thị Mai",
                    CompanyName = "CÔNG TY TNHH GIẢI PHÁP CÔNG NGHỆ BẢO DUY",
                    Address = "Khu Phố 6, Phường Linh Trung, Thành phố Thủ Đức, TP Hồ Chí Minh",
                    Email = "contact@baoduytech.vn",
                    PhoneNumber = "0287.300.9988",
                    Representative = "Bảo Duy",
                    IsActive = true,
                    CreatedAt = DateTime.Now.AddMonths(-3)
                };

                context.TaxAccounts.AddRange(company1, company2, company3);
                await context.SaveChangesAsync();

                // Gán quyền truy cập TaxAccount:
                // - Admin công ty: chỉ được xem đúng 1 công ty / hộ kinh doanh của mình
                // - Kế toán: quản lý tất cả các công ty / hộ kinh doanh
                if (adminUser != null)
                {
                    context.UserTaxAccounts.Add(
                        new UserTaxAccount { UserId = adminUser.Id, TaxAccountId = company1.Id, IsDefault = true, CanManage = false }
                    );
                }

                if (accountantUser != null)
                {
                    context.UserTaxAccounts.AddRange(
                        new UserTaxAccount { UserId = accountantUser.Id, TaxAccountId = company1.Id, IsDefault = false, CanManage = true },
                        new UserTaxAccount { UserId = accountantUser.Id, TaxAccountId = company2.Id, IsDefault = false, CanManage = true },
                        new UserTaxAccount { UserId = accountantUser.Id, TaxAccountId = company3.Id, IsDefault = true, CanManage = true }
                    );
                }

                if (viewerUser != null)
                {
                    context.UserTaxAccounts.AddRange(
                        new UserTaxAccount { UserId = viewerUser.Id, TaxAccountId = company1.Id, IsDefault = false, CanManage = false },
                        new UserTaxAccount { UserId = viewerUser.Id, TaxAccountId = company3.Id, IsDefault = true, CanManage = false }
                    );
                }

                await context.SaveChangesAsync();

                // Seed hóa đơn mẫu thực tế cho các công ty
                await SeedSampleInvoicesAsync(context, company1.Id, company1.TaxCode, company1.CompanyName);
                await SeedSampleInvoicesAsync(context, company2.Id, company2.TaxCode, company2.CompanyName);
                await SeedSampleInvoicesAsync(context, company3.Id, company3.TaxCode, company3.CompanyName);
            }
            else
            {
                // Chuẩn hóa phân quyền trên CSDL hiện có:
                // 1. Tài khoản Admin chỉ giữ lại đúng 1 công ty / hộ kinh doanh của mình
                if (adminUser != null)
                {
                    var adminMappings = await context.UserTaxAccounts
                        .Include(u => u.TaxAccount)
                        .Where(u => u.UserId == adminUser.Id)
                        .OrderByDescending(u => u.IsDefault)
                        .ThenByDescending(u => u.AssignedAt)
                        .ToListAsync();

                    if (adminMappings.Count > 1)
                    {
                        var primaryMapping = adminMappings.First();
                        primaryMapping.IsDefault = true;
                        primaryMapping.CanManage = false;
                        context.UserTaxAccounts.RemoveRange(adminMappings.Skip(1));
                        await context.SaveChangesAsync();
                    }
                    else if (adminMappings.Count == 0)
                    {
                        var firstActiveCompany = await context.TaxAccounts
                            .Where(t => t.IsActive)
                            .OrderBy(t => t.Id)
                            .FirstOrDefaultAsync();
                        if (firstActiveCompany != null)
                        {
                            context.UserTaxAccounts.Add(new UserTaxAccount
                            {
                                UserId = adminUser.Id,
                                TaxAccountId = firstActiveCompany.Id,
                                IsDefault = true,
                                CanManage = false,
                                AssignedAt = DateTime.Now
                            });
                            await context.SaveChangesAsync();
                        }
                    }
                }

                // 2. Tài khoản Kế toán có quyền quản lý tất cả các công ty / hộ kinh doanh đang hoạt động
                if (accountantUser != null)
                {
                    var allActiveIds = await context.TaxAccounts
                        .Where(t => t.IsActive)
                        .Select(t => t.Id)
                        .ToListAsync();

                    var existingAccIds = await context.UserTaxAccounts
                        .Where(u => u.UserId == accountantUser.Id)
                        .Select(u => u.TaxAccountId)
                        .ToListAsync();

                    bool hasAnyDefault = await context.UserTaxAccounts
                        .AnyAsync(u => u.UserId == accountantUser.Id && u.IsDefault);

                    bool addedAny = false;
                    foreach (var compId in allActiveIds)
                    {
                        if (!existingAccIds.Contains(compId))
                        {
                            context.UserTaxAccounts.Add(new UserTaxAccount
                            {
                                UserId = accountantUser.Id,
                                TaxAccountId = compId,
                                IsDefault = !hasAnyDefault,
                                CanManage = true,
                                AssignedAt = DateTime.Now
                            });
                            hasAnyDefault = true;
                            addedAny = true;
                        }
                    }
                    if (addedAny)
                    {
                        await context.SaveChangesAsync();
                    }
                }
            }
        }

        private static async Task EnsureTaxAccountColumnsAsync(ApplicationDbContext context)
        {
            if (context.Database.IsSqlServer())
            {
                var sql = @"
IF COL_LENGTH('TaxAccounts', 'BusinessType') IS NULL
    ALTER TABLE [TaxAccounts] ADD [BusinessType] nvarchar(30) NOT NULL DEFAULT 'DoanhNghiep';
IF COL_LENGTH('TaxAccounts', 'ShortName') IS NULL
    ALTER TABLE [TaxAccounts] ADD [ShortName] nvarchar(150) NULL;
IF COL_LENGTH('TaxAccounts', 'AccountantName') IS NULL
    ALTER TABLE [TaxAccounts] ADD [AccountantName] nvarchar(100) NULL;
";
                try { await context.Database.ExecuteSqlRawAsync(sql); } catch { }
            }
            else if (context.Database.IsSqlite())
            {
                try { await context.Database.ExecuteSqlRawAsync("ALTER TABLE TaxAccounts ADD COLUMN BusinessType TEXT NOT NULL DEFAULT 'DoanhNghiep';"); } catch { }
                try { await context.Database.ExecuteSqlRawAsync("ALTER TABLE TaxAccounts ADD COLUMN ShortName TEXT NULL;"); } catch { }
                try { await context.Database.ExecuteSqlRawAsync("ALTER TABLE TaxAccounts ADD COLUMN AccountantName TEXT NULL;"); } catch { }
            }
            else
            {
                // PostgreSQL
                try { await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"TaxAccounts\" ADD COLUMN IF NOT EXISTS \"BusinessType\" varchar(30) NOT NULL DEFAULT 'DoanhNghiep';"); } catch { }
                try { await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"TaxAccounts\" ADD COLUMN IF NOT EXISTS \"ShortName\" varchar(150) NULL;"); } catch { }
                try { await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"TaxAccounts\" ADD COLUMN IF NOT EXISTS \"AccountantName\" varchar(100) NULL;"); } catch { }
            }
        }

        private static async Task EnsureInvoiceColumnsAsync(ApplicationDbContext context)
        {
            if (context.Database.IsSqlServer())
            {
                var sql = @"
IF COL_LENGTH('Invoices', 'TemplateCode') IS NULL ALTER TABLE [Invoices] ADD [TemplateCode] nvarchar(20) NOT NULL DEFAULT '1';
IF COL_LENGTH('Invoices', 'SignedDate') IS NULL ALTER TABLE [Invoices] ADD [SignedDate] datetime2 NULL;
IF COL_LENGTH('Invoices', 'BuyerCitizenId') IS NULL ALTER TABLE [Invoices] ADD [BuyerCitizenId] nvarchar(50) NULL;
IF COL_LENGTH('Invoices', 'SellerWebsite') IS NULL ALTER TABLE [Invoices] ADD [SellerWebsite] nvarchar(500) NULL;
IF COL_LENGTH('Invoices', 'Currency') IS NULL ALTER TABLE [Invoices] ADD [Currency] nvarchar(20) NOT NULL DEFAULT 'VND';
IF COL_LENGTH('Invoices', 'ExchangeRate') IS NULL ALTER TABLE [Invoices] ADD [ExchangeRate] decimal(18,4) NOT NULL DEFAULT 1;
IF COL_LENGTH('Invoices', 'AmountBeforeTaxVnd') IS NULL ALTER TABLE [Invoices] ADD [AmountBeforeTaxVnd] decimal(18,2) NOT NULL DEFAULT 0;
IF COL_LENGTH('Invoices', 'TaxAmountVnd') IS NULL ALTER TABLE [Invoices] ADD [TaxAmountVnd] decimal(18,2) NOT NULL DEFAULT 0;
IF COL_LENGTH('Invoices', 'TotalAmountVnd') IS NULL ALTER TABLE [Invoices] ADD [TotalAmountVnd] decimal(18,2) NOT NULL DEFAULT 0;
IF COL_LENGTH('Invoices', 'DiscountAmount') IS NULL ALTER TABLE [Invoices] ADD [DiscountAmount] decimal(18,2) NOT NULL DEFAULT 0;
IF COL_LENGTH('Invoices', 'FeeAmount') IS NULL ALTER TABLE [Invoices] ADD [FeeAmount] decimal(18,2) NOT NULL DEFAULT 0;
IF COL_LENGTH('Invoices', 'PaymentMethod') IS NULL ALTER TABLE [Invoices] ADD [PaymentMethod] nvarchar(100) NULL;
IF COL_LENGTH('Invoices', 'InvoiceProperty') IS NULL ALTER TABLE [Invoices] ADD [InvoiceProperty] nvarchar(100) NULL;
IF COL_LENGTH('Invoices', 'VerificationResult') IS NULL ALTER TABLE [Invoices] ADD [VerificationResult] nvarchar(200) NULL;
IF COL_LENGTH('Invoices', 'VehicleNumber') IS NULL ALTER TABLE [Invoices] ADD [VehicleNumber] nvarchar(50) NULL;
IF COL_LENGTH('Invoices', 'ReplacedOrAdjustedNote') IS NULL ALTER TABLE [Invoices] ADD [ReplacedOrAdjustedNote] nvarchar(500) NULL;
IF COL_LENGTH('Invoices', 'LookupCode') IS NULL ALTER TABLE [Invoices] ADD [LookupCode] nvarchar(100) NULL;
IF COL_LENGTH('Invoices', 'LookupUrl') IS NULL ALTER TABLE [Invoices] ADD [LookupUrl] nvarchar(1000) NULL;

IF COL_LENGTH('InvoiceDetails', 'DiscountRate') IS NULL ALTER TABLE [InvoiceDetails] ADD [DiscountRate] decimal(5,2) NOT NULL DEFAULT 0;
IF COL_LENGTH('InvoiceDetails', 'DiscountAmount') IS NULL ALTER TABLE [InvoiceDetails] ADD [DiscountAmount] decimal(18,2) NOT NULL DEFAULT 0;
IF COL_LENGTH('InvoiceDetails', 'AmountBeforeTaxForeign') IS NULL ALTER TABLE [InvoiceDetails] ADD [AmountBeforeTaxForeign] decimal(18,2) NOT NULL DEFAULT 0;
IF COL_LENGTH('InvoiceDetails', 'TaxAmountForeign') IS NULL ALTER TABLE [InvoiceDetails] ADD [TaxAmountForeign] decimal(18,2) NOT NULL DEFAULT 0;
IF COL_LENGTH('InvoiceDetails', 'TotalAmountForeign') IS NULL ALTER TABLE [InvoiceDetails] ADD [TotalAmountForeign] decimal(18,2) NOT NULL DEFAULT 0;
IF COL_LENGTH('InvoiceDetails', 'AmountBeforeTaxVnd') IS NULL ALTER TABLE [InvoiceDetails] ADD [AmountBeforeTaxVnd] decimal(18,2) NOT NULL DEFAULT 0;
IF COL_LENGTH('InvoiceDetails', 'TaxAmountVnd') IS NULL ALTER TABLE [InvoiceDetails] ADD [TaxAmountVnd] decimal(18,2) NOT NULL DEFAULT 0;
IF COL_LENGTH('InvoiceDetails', 'TotalAmountVnd') IS NULL ALTER TABLE [InvoiceDetails] ADD [TotalAmountVnd] decimal(18,2) NOT NULL DEFAULT 0;
";
                try { await context.Database.ExecuteSqlRawAsync(sql); } catch { }
            }
            else if (context.Database.IsSqlite())
            {
                var cols = new[]
                {
                    "ALTER TABLE Invoices ADD COLUMN TemplateCode TEXT NOT NULL DEFAULT '1';",
                    "ALTER TABLE Invoices ADD COLUMN SignedDate TEXT NULL;",
                    "ALTER TABLE Invoices ADD COLUMN BuyerCitizenId TEXT NULL;",
                    "ALTER TABLE Invoices ADD COLUMN SellerWebsite TEXT NULL;",
                    "ALTER TABLE Invoices ADD COLUMN Currency TEXT NOT NULL DEFAULT 'VND';",
                    "ALTER TABLE Invoices ADD COLUMN ExchangeRate REAL NOT NULL DEFAULT 1;",
                    "ALTER TABLE Invoices ADD COLUMN AmountBeforeTaxVnd REAL NOT NULL DEFAULT 0;",
                    "ALTER TABLE Invoices ADD COLUMN TaxAmountVnd REAL NOT NULL DEFAULT 0;",
                    "ALTER TABLE Invoices ADD COLUMN TotalAmountVnd REAL NOT NULL DEFAULT 0;",
                    "ALTER TABLE Invoices ADD COLUMN DiscountAmount REAL NOT NULL DEFAULT 0;",
                    "ALTER TABLE Invoices ADD COLUMN FeeAmount REAL NOT NULL DEFAULT 0;",
                    "ALTER TABLE Invoices ADD COLUMN PaymentMethod TEXT NULL;",
                    "ALTER TABLE Invoices ADD COLUMN InvoiceProperty TEXT NULL;",
                    "ALTER TABLE Invoices ADD COLUMN VerificationResult TEXT NULL;",
                    "ALTER TABLE Invoices ADD COLUMN VehicleNumber TEXT NULL;",
                    "ALTER TABLE Invoices ADD COLUMN ReplacedOrAdjustedNote TEXT NULL;",
                    "ALTER TABLE Invoices ADD COLUMN LookupCode TEXT NULL;",
                    "ALTER TABLE Invoices ADD COLUMN LookupUrl TEXT NULL;",
                    "ALTER TABLE InvoiceDetails ADD COLUMN DiscountRate REAL NOT NULL DEFAULT 0;",
                    "ALTER TABLE InvoiceDetails ADD COLUMN DiscountAmount REAL NOT NULL DEFAULT 0;",
                    "ALTER TABLE InvoiceDetails ADD COLUMN AmountBeforeTaxForeign REAL NOT NULL DEFAULT 0;",
                    "ALTER TABLE InvoiceDetails ADD COLUMN TaxAmountForeign REAL NOT NULL DEFAULT 0;",
                    "ALTER TABLE InvoiceDetails ADD COLUMN TotalAmountForeign REAL NOT NULL DEFAULT 0;",
                    "ALTER TABLE InvoiceDetails ADD COLUMN AmountBeforeTaxVnd REAL NOT NULL DEFAULT 0;",
                    "ALTER TABLE InvoiceDetails ADD COLUMN TaxAmountVnd REAL NOT NULL DEFAULT 0;",
                    "ALTER TABLE InvoiceDetails ADD COLUMN TotalAmountVnd REAL NOT NULL DEFAULT 0;"
                };
                foreach (var c in cols)
                {
                    try { await context.Database.ExecuteSqlRawAsync(c); } catch { }
                }
            }
            else
            {
                // PostgreSQL
                var pgCols = new[]
                {
                    "ALTER TABLE \"Invoices\" ADD COLUMN IF NOT EXISTS \"TemplateCode\" varchar(20) NOT NULL DEFAULT '1';",
                    "ALTER TABLE \"Invoices\" ADD COLUMN IF NOT EXISTS \"SignedDate\" timestamp NULL;",
                    "ALTER TABLE \"Invoices\" ADD COLUMN IF NOT EXISTS \"BuyerCitizenId\" varchar(50) NULL;",
                    "ALTER TABLE \"Invoices\" ADD COLUMN IF NOT EXISTS \"SellerWebsite\" varchar(500) NULL;",
                    "ALTER TABLE \"Invoices\" ADD COLUMN IF NOT EXISTS \"Currency\" varchar(20) NOT NULL DEFAULT 'VND';",
                    "ALTER TABLE \"Invoices\" ADD COLUMN IF NOT EXISTS \"ExchangeRate\" numeric(18,4) NOT NULL DEFAULT 1;",
                    "ALTER TABLE \"Invoices\" ADD COLUMN IF NOT EXISTS \"AmountBeforeTaxVnd\" numeric(18,2) NOT NULL DEFAULT 0;",
                    "ALTER TABLE \"Invoices\" ADD COLUMN IF NOT EXISTS \"TaxAmountVnd\" numeric(18,2) NOT NULL DEFAULT 0;",
                    "ALTER TABLE \"Invoices\" ADD COLUMN IF NOT EXISTS \"TotalAmountVnd\" numeric(18,2) NOT NULL DEFAULT 0;",
                    "ALTER TABLE \"Invoices\" ADD COLUMN IF NOT EXISTS \"DiscountAmount\" numeric(18,2) NOT NULL DEFAULT 0;",
                    "ALTER TABLE \"Invoices\" ADD COLUMN IF NOT EXISTS \"FeeAmount\" numeric(18,2) NOT NULL DEFAULT 0;",
                    "ALTER TABLE \"Invoices\" ADD COLUMN IF NOT EXISTS \"PaymentMethod\" varchar(100) NULL;",
                    "ALTER TABLE \"Invoices\" ADD COLUMN IF NOT EXISTS \"InvoiceProperty\" varchar(100) NULL;",
                    "ALTER TABLE \"Invoices\" ADD COLUMN IF NOT EXISTS \"VerificationResult\" varchar(200) NULL;",
                    "ALTER TABLE \"Invoices\" ADD COLUMN IF NOT EXISTS \"VehicleNumber\" varchar(50) NULL;",
                    "ALTER TABLE \"Invoices\" ADD COLUMN IF NOT EXISTS \"ReplacedOrAdjustedNote\" varchar(500) NULL;",
                    "ALTER TABLE \"Invoices\" ADD COLUMN IF NOT EXISTS \"LookupCode\" varchar(100) NULL;",
                    "ALTER TABLE \"Invoices\" ADD COLUMN IF NOT EXISTS \"LookupUrl\" varchar(1000) NULL;",
                    "ALTER TABLE \"InvoiceDetails\" ADD COLUMN IF NOT EXISTS \"DiscountRate\" numeric(5,2) NOT NULL DEFAULT 0;",
                    "ALTER TABLE \"InvoiceDetails\" ADD COLUMN IF NOT EXISTS \"DiscountAmount\" numeric(18,2) NOT NULL DEFAULT 0;",
                    "ALTER TABLE \"InvoiceDetails\" ADD COLUMN IF NOT EXISTS \"AmountBeforeTaxForeign\" numeric(18,2) NOT NULL DEFAULT 0;",
                    "ALTER TABLE \"InvoiceDetails\" ADD COLUMN IF NOT EXISTS \"TaxAmountForeign\" numeric(18,2) NOT NULL DEFAULT 0;",
                    "ALTER TABLE \"InvoiceDetails\" ADD COLUMN IF NOT EXISTS \"TotalAmountForeign\" numeric(18,2) NOT NULL DEFAULT 0;",
                    "ALTER TABLE \"InvoiceDetails\" ADD COLUMN IF NOT EXISTS \"AmountBeforeTaxVnd\" numeric(18,2) NOT NULL DEFAULT 0;",
                    "ALTER TABLE \"InvoiceDetails\" ADD COLUMN IF NOT EXISTS \"TaxAmountVnd\" numeric(18,2) NOT NULL DEFAULT 0;",
                    "ALTER TABLE \"InvoiceDetails\" ADD COLUMN IF NOT EXISTS \"TotalAmountVnd\" numeric(18,2) NOT NULL DEFAULT 0;"
                };
                foreach (var c in pgCols)
                {
                    try { await context.Database.ExecuteSqlRawAsync(c); } catch { }
                }
            }
        }

        private static async Task SeedSampleInvoicesAsync(ApplicationDbContext context, int taxAccountId, string buyerTaxCode, string buyerName)
        {
            var now = DateTime.Now;
            var random = new Random(taxAccountId);

            var sampleSellers = new[]
            {
                new { TaxCode = "0100109106", Name = "TẬP ĐOÀN CÔNG NGHIỆP - VIỄN THÔNG QUÂN ĐỘI (VIETTEL)", Address = "Lô D26 Khu đô thị mới Cầu Giấy, Yên Hòa, Cầu Giấy, Hà Nội", Provider = "Viettel" },
                new { TaxCode = "0101243150", Name = "CÔNG TY CỔ PHẦN MISA", Address = "Tầng 9, Tòa nhà Technosoft, Phố Duy Tân, Cầu Giấy, Hà Nội", Provider = "MISA" },
                new { TaxCode = "0106869738", Name = "TỔNG CÔNG TY DỊCH VỤ VIỄN THÔNG (VNPT-VINAPHONE)", Address = "Tòa nhà VNPT, 57 Huỳnh Thúc Kháng, Đống Đa, Hà Nội", Provider = "VNPT" },
                new { TaxCode = "0104128565", Name = "CÔNG TY TNHH HỆ THỐNG THÔNG TIN FPT (FPT IS)", Address = "Tòa nhà FPT, Phố Duy Tân, Dịch Vọng Hậu, Cầu Giấy, Hà Nội", Provider = "MISA" },
                new { TaxCode = "0100107624", Name = "TẬP ĐOÀN XĂNG DẦU VIỆT NAM (PETROLIMEX)", Address = "Số 1 Khâm Thiên, Phường Khâm Thiên, Quận Đống Đa, Hà Nội", Provider = "TCT" },
                new { TaxCode = "0100101683", Name = "CÔNG TY CỔ PHẦN VĂN PHÒNG PHẨM HỒNG HÀ", Address = "25 Lý Thường Kiệt, Phan Chu Trinh, Hoàn Kiếm, Hà Nội", Provider = "BKAV" },
                new { TaxCode = "0100108740", Name = "TỔNG CÔNG TY ĐIỆN LỰC TP HÀ NỘI (EVN HANOI)", Address = "Số 69 Đinh Tiên Hoàng, Phường Lý Thái Tổ, Quận Hoàn Kiếm, Hà Nội", Provider = "TCT" },
                new { TaxCode = "0300588569", Name = "TỔNG CÔNG TY CẤP NƯỚC SÀI GÒN - TNHH MTV (SAWACO)", Address = "Số 1 Công Trường Quốc Tế, Phường Võ Thị Sáu, Quận 3, TP Hồ Chí Minh", Provider = "VNPT" }
            };

            var sampleItems = new[]
            {
                new { Name = "Dịch vụ kênh truyền cáp quang Leased Line 100Mbps", Unit = "Tháng", Price = 8500000m, TaxRate = 10m },
                new { Name = "Gói phần mềm kế toán doanh nghiệp MISA SME Enterprise 2026", Unit = "Gói", Price = 15900000m, TaxRate = 10m },
                new { Name = "Dịch vụ chữ ký số Token VNPT-CA 3 năm", Unit = "Gói", Price = 2750000m, TaxRate = 10m },
                new { Name = "Máy chủ Dell PowerEdge R750xs Server 2x Intel Xeon", Unit = "Bộ", Price = 78500000m, TaxRate = 8m },
                new { Name = "Switch mạng Cisco Catalyst 9200L 24 Port PoE+", Unit = "Chiếc", Price = 22400000m, TaxRate = 8m },
                new { Name = "Xăng RON 95-III cung cấp xe công tác", Unit = "Lít", Price = 22850m, TaxRate = 10m },
                new { Name = "Giấy in A4 Double A 70gsm (5 ram/thùng)", Unit = "Thùng", Price = 380000m, TaxRate = 8m },
                new { Name = "Tiền điện sinh hoạt và sản xuất kỳ 08/2026", Unit = "kWh", Price = 2450m, TaxRate = 8m },
                new { Name = "Dịch vụ đào tạo và chuyển giao công nghệ phần mềm nội bộ", Unit = "Khóa", Price = 12000000m, TaxRate = -1m } // Không chịu thuế
            };

            var invoices = new List<Invoice>();

            // Sinh 20 hóa đơn mua vào với ngày lập rải từ tháng trước đến hôm nay
            for (int i = 1; i <= 20; i++)
            {
                var seller = sampleSellers[i % sampleSellers.Length];
                var daysAgo = (20 - i) * 2;
                var issueDate = now.AddDays(-daysAgo);

                bool isCashRegister = (i % 7 == 0);
                bool hasTaxCode = !isCashRegister && (i % 5 != 0); // 80% có mã CQT
                string symbolPrefix = isCashRegister ? "C25M" : (hasTaxCode ? "1C25T" : "2C25T");
                string symbol = $"{symbolPrefix}{((char)('A' + (i % 26)))}{((char)('A' + ((i + 3) % 26)))}";
                string invoiceNum = i.ToString().PadLeft(8, '0');

                string status = "Hóa đơn mới";
                if (i == 3) status = "Đã điều chỉnh";
                else if (i == 7) status = "Đã thay thế";
                else if (i == 15) status = "Đã bị hủy";

                var invoice = new Invoice
                {
                    TaxAccountId = taxAccountId,
                    InvoiceSymbol = symbol,
                    InvoiceNumber = invoiceNum,
                    IssueDate = issueDate,
                    SellerTaxCode = seller.TaxCode,
                    SellerName = seller.Name,
                    SellerAddress = seller.Address,
                    BuyerTaxCode = buyerTaxCode,
                    BuyerName = buyerName,
                    InvoiceType = "MuaVao",
                    HasTaxCode = hasTaxCode,
                    IsCashRegister = isCashRegister,
                    TaxAuthorityCode = hasTaxCode ? $"0036{random.Next(100000, 999999)}A0284DF{i:X2}" : null,
                    Status = status,
                    SourceProvider = seller.Provider,
                    ImportedAt = issueDate.AddHours(4),
                    IsReconciled = (i % 2 == 0),
                    ReconciledRefNo = (i % 2 == 0) ? $"PC-2026/08/{100 + i}" : null,
                    ReconciledAt = (i % 2 == 0) ? issueDate.AddDays(1) : null,
                    RiskLevel = (i == 12) ? "Warning" : "Normal",
                    RiskReason = (i == 12) ? "Chênh lệch tiền thuế 150đ so với tính toán hệ thống" : null
                };

                // Add 1 - 3 details
                int detailCount = 1 + (i % 3);
                decimal subtotal = 0;
                decimal taxTotal = 0;

                for (int d = 0; d < detailCount; d++)
                {
                    var item = sampleItems[(i + d) % sampleItems.Length];
                    decimal qty = (item.Unit == "Lít" || item.Unit == "kWh") ? random.Next(150, 800) : random.Next(1, 10);
                    decimal lineAmount = Math.Round(qty * item.Price, 2);
                    decimal rate = item.TaxRate;
                    decimal lineTax = rate > 0 ? Math.Round(lineAmount * (rate / 100m), 2) : 0;

                    subtotal += lineAmount;
                    taxTotal += lineTax;

                    invoice.Details.Add(new InvoiceDetail
                    {
                        LineNumber = d + 1,
                        ItemCode = $"SP-{i:D3}-{d + 1}",
                        ItemName = item.Name,
                        Unit = item.Unit,
                        Quantity = qty,
                        UnitPrice = item.Price,
                        AmountBeforeTax = lineAmount,
                        TaxRate = rate,
                        TaxAmount = lineTax,
                        TotalAmount = lineAmount + lineTax
                    });
                }

                invoice.AmountBeforeTax = subtotal;
                invoice.TaxAmount = taxTotal;
                invoice.TotalAmount = subtotal + taxTotal;

                invoices.Add(invoice);
            }

            // Sinh thêm 5 hóa đơn bán ra (để test tab Bán ra)
            for (int i = 1; i <= 5; i++)
            {
                var issueDate = now.AddDays(-i * 3);
                string symbol = $"1C25TKT";
                string invoiceNum = (500 + i).ToString().PadLeft(8, '0');

                var invoice = new Invoice
                {
                    TaxAccountId = taxAccountId,
                    InvoiceSymbol = symbol,
                    InvoiceNumber = invoiceNum,
                    IssueDate = issueDate,
                    SellerTaxCode = buyerTaxCode,
                    SellerName = buyerName,
                    BuyerTaxCode = "0109988776",
                    BuyerName = $"CÔNG TY ĐỐI TÁC THƯƠNG MẠI {i} - VIỆT NAM",
                    InvoiceType = "BanRa",
                    HasTaxCode = true,
                    TaxAuthorityCode = $"0036{random.Next(100000, 999999)}A0994DF{i:X2}",
                    Status = "Hóa đơn mới",
                    SourceProvider = "MISA",
                    ImportedAt = issueDate.AddHours(2),
                    AmountBeforeTax = 45000000m * i,
                    TaxAmount = 4500000m * i,
                    TotalAmount = 49500000m * i,
                    IsReconciled = true,
                    ReconciledRefNo = $"PT-2026/{i:D3}"
                };

                invoice.Details.Add(new InvoiceDetail
                {
                    LineNumber = 1,
                    ItemCode = "DV-OUT-01",
                    ItemName = "Cung cấp dịch vụ giải pháp phần mềm và quản trị hạ tầng kỳ " + i,
                    Unit = "Gói",
                    Quantity = 1,
                    UnitPrice = invoice.AmountBeforeTax,
                    AmountBeforeTax = invoice.AmountBeforeTax,
                    TaxRate = 10,
                    TaxAmount = invoice.TaxAmount,
                    TotalAmount = invoice.TotalAmount
                });

                invoices.Add(invoice);
            }

            context.Invoices.AddRange(invoices);

            // Log đồng bộ mẫu
            context.SyncLogs.Add(new SyncLog
            {
                TaxAccountId = taxAccountId,
                SyncedAt = DateTime.Now.AddMinutes(-35),
                NewInvoiceCount = 3,
                Status = "Thành công",
                SyncType = "Thư mục tự động",
                ErrorMessage = null
            });

            await context.SaveChangesAsync();
        }

        public static async Task RepairExistingInvoicesAsync(ApplicationDbContext context)
        {
            try
            {
                var parser = new StandardTctParser();
                var invoices = await context.Invoices
                    .Include(i => i.Details)
                    .Include(i => i.Files)
                    .ToListAsync();

                int repairedCount = 0;
                string storageRoot = Path.Combine(Directory.GetCurrentDirectory(), "App_Data", "RawFiles");

                // Quét và lập chỉ mục toàn bộ tệp XML trên đĩa (App_Data/RawFiles) để đối soát chuẩn xác
                var xmlIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (Directory.Exists(storageRoot))
                {
                    var allXmlFiles = Directory.GetFiles(storageRoot, "*.xml", SearchOption.AllDirectories);
                    foreach (var xf in allXmlFiles)
                    {
                        try
                        {
                            using var sr = new StreamReader(File.OpenRead(xf), System.Text.Encoding.UTF8, true, 4096);
                            var head = sr.ReadToEnd();
                            int ttIdx = head.IndexOf("<TTChung", StringComparison.OrdinalIgnoreCase);
                            if (ttIdx >= 0)
                            {
                                string ExtractTag(string text, string tag)
                                {
                                    int s = text.IndexOf("<" + tag + ">", StringComparison.OrdinalIgnoreCase);
                                    if (s < 0) return "";
                                    s += tag.Length + 2;
                                    int e = text.IndexOf("</" + tag + ">", s, StringComparison.OrdinalIgnoreCase);
                                    return e > s ? text.Substring(s, e - s).Trim() : "";
                                }

                                string symbol = ExtractTag(head, "KHHDon").TrimStart('1').ToUpper();
                                string num = ExtractTag(head, "SHDon");
                                string normNum = long.TryParse(num, out var ln) ? ln.ToString() : num.TrimStart('0');

                                int nbanIdx = head.IndexOf("<NBan", StringComparison.OrdinalIgnoreCase);
                                string nbanMst = "";
                                if (nbanIdx >= 0)
                                {
                                    int nbanEnd = head.IndexOf("</NBan>", nbanIdx, StringComparison.OrdinalIgnoreCase);
                                    if (nbanEnd > nbanIdx)
                                    {
                                        var nbanBlock = head.Substring(nbanIdx, nbanEnd - nbanIdx);
                                        nbanMst = ExtractTag(nbanBlock, "MST");
                                    }
                                }

                                if (!string.IsNullOrEmpty(symbol) && !string.IsNullOrEmpty(normNum))
                                {
                                    xmlIndex[$"{symbol}_{normNum}"] = xf;
                                    if (!string.IsNullOrEmpty(nbanMst))
                                    {
                                        xmlIndex[$"{nbanMst}_{symbol}_{normNum}"] = xf;
                                        xmlIndex[$"{nbanMst}_{normNum}"] = xf;
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }

                foreach (var inv in invoices)
                {
                    bool modified = false;
                    bool isPlaceholder = !inv.Details.Any() ||
                        inv.Details.All(d => d.ItemName.StartsWith("Hàng hóa") || d.Unit == "Lô/Gói" || d.Unit == "Lần") ||
                        inv.Details.Any(d => d.TaxAmount == 0 && d.TaxRate > 0);

                    string? xmlPath = null;
                    if (!string.IsNullOrEmpty(inv.RawXmlPath) && File.Exists(inv.RawXmlPath))
                    {
                        xmlPath = inv.RawXmlPath;
                    }

                    if (xmlPath == null)
                    {
                        var xmlFile = inv.Files.FirstOrDefault(f => string.Equals(f.FileExtension, ".xml", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(f.FilePath) && File.Exists(f.FilePath));
                        if (xmlFile != null)
                        {
                            xmlPath = xmlFile.FilePath;
                        }
                    }

                    if (xmlPath == null)
                    {
                        var normNum = long.TryParse(inv.InvoiceNumber, out var n) ? n.ToString() : (inv.InvoiceNumber ?? "").TrimStart('0');
                        var cleanSymbol = (inv.InvoiceSymbol ?? "").TrimStart('1').ToUpper();
                        var sellerMst = (inv.SellerTaxCode ?? "").Trim();

                        if (!string.IsNullOrEmpty(sellerMst) && !string.IsNullOrEmpty(cleanSymbol) && xmlIndex.TryGetValue($"{sellerMst}_{cleanSymbol}_{normNum}", out var p1))
                        {
                            xmlPath = p1;
                        }
                        else if (!string.IsNullOrEmpty(cleanSymbol) && xmlIndex.TryGetValue($"{cleanSymbol}_{normNum}", out var p2))
                        {
                            xmlPath = p2;
                        }
                        else if (!string.IsNullOrEmpty(sellerMst) && xmlIndex.TryGetValue($"{sellerMst}_{normNum}", out var p3))
                        {
                            xmlPath = p3;
                        }
                    }

                    if (xmlPath != null && File.Exists(xmlPath))
                    {
                        try
                        {
                            using var fs = File.OpenRead(xmlPath);
                            var parsed = await parser.ParseAsync(fs, Path.GetFileName(xmlPath));
                            if (parsed.Success)
                            {
                                // 1. Khắc phục triệt để lỗi ngày lập lùi 1 ngày so với thực tế
                                if (parsed.IssueDate > DateTime.MinValue && (inv.IssueDate.Date != parsed.IssueDate.Date || inv.IssueDate != parsed.IssueDate))
                                {
                                    inv.IssueDate = parsed.IssueDate;
                                    modified = true;
                                }

                                // 2. Khắc phục triệt để lỗi tên hàng hóa và đơn vị tính: load chính xác từ XML gốc
                                if (parsed.Details.Any() && (isPlaceholder || inv.Details.Count != parsed.Details.Count))
                                {
                                    inv.Details.Clear();
                                    foreach (var d in parsed.Details)
                                    {
                                        var taxAmt = d.TaxAmount > 0 ? d.TaxAmount : (d.TaxRate > 0 ? Math.Round(d.AmountBeforeTax * d.TaxRate / 100m, 0) : 0);
                                        inv.Details.Add(new InvoiceDetail
                                        {
                                            LineNumber = d.LineNumber,
                                            ItemCode = d.ItemCode,
                                            ItemName = d.ItemName,
                                            Unit = d.Unit,
                                            Quantity = d.Quantity,
                                            UnitPrice = d.UnitPrice,
                                            DiscountRate = d.DiscountRate,
                                            DiscountAmount = d.DiscountAmount,
                                            AmountBeforeTax = d.AmountBeforeTax,
                                            TaxRate = d.TaxRate,
                                            TaxAmount = taxAmt,
                                            TotalAmount = d.AmountBeforeTax + taxAmt,
                                            AmountBeforeTaxForeign = d.AmountBeforeTaxForeign,
                                            TaxAmountForeign = d.TaxAmountForeign,
                                            TotalAmountForeign = d.TotalAmountForeign,
                                            AmountBeforeTaxVnd = d.AmountBeforeTaxVnd,
                                            TaxAmountVnd = d.TaxAmountVnd,
                                            TotalAmountVnd = d.TotalAmountVnd
                                        });
                                    }
                                    inv.SourceProvider = "TCT-Standard";
                                    modified = true;
                                }
                                else if (inv.Details.Any(d => d.TaxAmount == 0 && d.TaxRate > 0))
                                {
                                    foreach (var d in inv.Details.Where(d => d.TaxAmount == 0 && d.TaxRate > 0))
                                    {
                                        d.TaxAmount = Math.Round(d.AmountBeforeTax * d.TaxRate / 100m, 0);
                                        d.TotalAmount = d.AmountBeforeTax + d.TaxAmount;
                                        d.AmountBeforeTaxForeign = d.AmountBeforeTax;
                                        d.TaxAmountForeign = d.TaxAmount;
                                        d.TotalAmountForeign = d.TotalAmount;
                                        d.AmountBeforeTaxVnd = d.AmountBeforeTax;
                                        d.TaxAmountVnd = d.TaxAmount;
                                        d.TotalAmountVnd = d.TotalAmount;
                                    }
                                    modified = true;
                                }

                                // Bổ sung các thông tin mở rộng chuẩn xác từ XML gốc
                                if (!string.IsNullOrEmpty(parsed.TemplateCode)) inv.TemplateCode = parsed.TemplateCode;
                                if (parsed.SignedDate.HasValue) inv.SignedDate = parsed.SignedDate;
                                if (!string.IsNullOrEmpty(parsed.Currency)) inv.Currency = parsed.Currency;
                                if (parsed.ExchangeRate > 0) inv.ExchangeRate = parsed.ExchangeRate;
                                if (!string.IsNullOrEmpty(parsed.PaymentMethod)) inv.PaymentMethod = parsed.PaymentMethod;
                                if (!string.IsNullOrEmpty(parsed.InvoiceProperty)) inv.InvoiceProperty = parsed.InvoiceProperty;
                                if (!string.IsNullOrEmpty(parsed.VerificationResult)) inv.VerificationResult = parsed.VerificationResult;
                                if (!string.IsNullOrEmpty(parsed.BuyerCitizenId)) inv.BuyerCitizenId = parsed.BuyerCitizenId;
                                if (!string.IsNullOrEmpty(parsed.SellerWebsite)) inv.SellerWebsite = parsed.SellerWebsite;
                                if (!string.IsNullOrEmpty(parsed.VehicleNumber)) inv.VehicleNumber = parsed.VehicleNumber;
                                if (!string.IsNullOrEmpty(parsed.ReplacedOrAdjustedNote)) inv.ReplacedOrAdjustedNote = parsed.ReplacedOrAdjustedNote;
                                if (!string.IsNullOrEmpty(parsed.LookupCode)) inv.LookupCode = parsed.LookupCode;
                                if (!string.IsNullOrEmpty(parsed.LookupUrl)) inv.LookupUrl = parsed.LookupUrl;
                                inv.DiscountAmount = parsed.DiscountAmount;
                                inv.FeeAmount = parsed.FeeAmount;
                                inv.AmountBeforeTaxVnd = parsed.AmountBeforeTaxVnd;
                                inv.TaxAmountVnd = parsed.TaxAmountVnd;
                                inv.TotalAmountVnd = parsed.TotalAmountVnd;
                                modified = true;

                                if (!string.IsNullOrWhiteSpace(parsed.SellerAddress) && string.IsNullOrWhiteSpace(inv.SellerAddress))
                                {
                                    inv.SellerAddress = parsed.SellerAddress;
                                    modified = true;
                                }

                                if (!string.IsNullOrWhiteSpace(parsed.BuyerAddress) && string.IsNullOrWhiteSpace(inv.BuyerAddress))
                                {
                                    inv.BuyerAddress = parsed.BuyerAddress;
                                    modified = true;
                                }

                                if (string.IsNullOrEmpty(inv.RawXmlPath) || !File.Exists(inv.RawXmlPath))
                                {
                                    inv.RawXmlPath = xmlPath;
                                    modified = true;
                                }

                                if (!inv.Files.Any(f => f.FilePath == xmlPath))
                                {
                                    inv.Files.Add(new InvoiceFile
                                    {
                                        InvoiceId = inv.Id,
                                        TaxAccountId = inv.TaxAccountId,
                                        OriginalFileName = Path.GetFileName(xmlPath),
                                        StoredFileName = Path.GetFileName(xmlPath),
                                        FilePath = xmlPath,
                                        FileExtension = ".xml",
                                        FileSizeBytes = new FileInfo(xmlPath).Length,
                                        ContentType = "application/xml",
                                        Source = "AutoScan"
                                    });
                                    modified = true;
                                }
                            }
                        }
                        catch { }
                    }
                    else
                    {
                        // Trường hợp không có file XML gốc: chuẩn hóa các sai lệch phổ biến
                        if (inv.IssueDate.Hour >= 17)
                        {
                            inv.IssueDate = inv.IssueDate.Date.AddDays(1);
                            modified = true;
                        }

                        // Grab: mã số thuế 0301452948
                        if (inv.SellerTaxCode == "0301452948")
                        {
                            foreach (var d in inv.Details)
                            {
                                if (d.ItemName.StartsWith("Hàng hóa") || string.IsNullOrEmpty(d.ItemName))
                                {
                                    d.ItemName = "Cước dịch vụ vận chuyển Grab";
                                    d.Unit = "Chuyến";
                                    modified = true;
                                }
                            }
                        }

                        foreach (var d in inv.Details)
                        {
                            if (d.Unit == "Lô/Gói" || d.Unit == "Lần")
                            {
                                d.Unit = "Dịch vụ";
                                modified = true;
                            }
                        }
                    }

                    // Đảm bảo các trường mở rộng luôn có giá trị chuẩn chỉnh
                    if (string.IsNullOrEmpty(inv.TemplateCode)) { inv.TemplateCode = inv.InvoiceSymbol.Length >= 7 && char.IsDigit(inv.InvoiceSymbol[0]) ? inv.InvoiceSymbol[0].ToString() : "1"; modified = true; }
                    if (!inv.SignedDate.HasValue) { inv.SignedDate = inv.IssueDate; modified = true; }
                    if (string.IsNullOrEmpty(inv.Currency)) { inv.Currency = "VND"; modified = true; }
                    if (inv.ExchangeRate <= 0) { inv.ExchangeRate = 1; modified = true; }
                    if (string.IsNullOrEmpty(inv.PaymentMethod)) { inv.PaymentMethod = "TM/CK"; modified = true; }
                    if (string.IsNullOrEmpty(inv.InvoiceProperty)) { inv.InvoiceProperty = "Hàng hoá/dịch vụ"; modified = true; }
                    if (string.IsNullOrEmpty(inv.VerificationResult)) { inv.VerificationResult = inv.HasTaxCode ? "Đã cấp mã hoá đơn" : "CQT đã tiếp nhận"; modified = true; }
                    if (inv.TotalAmountVnd == 0)
                    {
                        var rate = inv.ExchangeRate > 0 ? inv.ExchangeRate : 1;
                        inv.AmountBeforeTaxVnd = Math.Round(inv.AmountBeforeTax * rate, 0);
                        inv.TaxAmountVnd = Math.Round(inv.TaxAmount * rate, 0);
                        inv.TotalAmountVnd = Math.Round(inv.TotalAmount * rate, 0);
                        modified = true;
                    }
                    if (string.IsNullOrEmpty(inv.LookupUrl))
                    {
                        inv.LookupUrl = StandardTctParser.ResolveLookupUrl(null, inv.SellerTaxCode, inv.SellerWebsite, null);
                        modified = true;
                    }
                    if (string.IsNullOrEmpty(inv.LookupCode))
                    {
                        inv.LookupCode = !string.IsNullOrWhiteSpace(inv.TaxAuthorityCode) ? inv.TaxAuthorityCode : inv.InvoiceNumber;
                        modified = true;
                    }
                    foreach (var d in inv.Details)
                    {
                        if (d.TotalAmountVnd == 0)
                        {
                            var rate = inv.ExchangeRate > 0 ? inv.ExchangeRate : 1;
                            d.AmountBeforeTaxForeign = d.AmountBeforeTax;
                            d.TaxAmountForeign = d.TaxAmount;
                            d.TotalAmountForeign = d.TotalAmount;
                            d.AmountBeforeTaxVnd = Math.Round(d.AmountBeforeTax * rate, 0);
                            d.TaxAmountVnd = Math.Round(d.TaxAmount * rate, 0);
                            d.TotalAmountVnd = Math.Round(d.TotalAmount * rate, 0);
                            modified = true;
                        }
                    }

                    if (modified) repairedCount++;
                }

                if (repairedCount > 0)
                {
                    await context.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Lỗi khi tự động chuẩn hóa hóa đơn: {ex.Message}");
            }
        }
    }
}
