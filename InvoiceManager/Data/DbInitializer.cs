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
    }
}
