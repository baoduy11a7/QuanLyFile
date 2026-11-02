using InvoiceManager.Data;
using InvoiceManager.Models.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace InvoiceManager.Services
{
    public class TaxAccountContext : ITaxAccountContext
    {
        private const string SessionKeyCurrentTaxAccountId = "ActiveTaxAccountId";
        private const string CookieKeyCurrentTaxAccountId = "ActiveTaxAccountId";

        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ApplicationDbContext _db;
        private readonly UserManager<ApplicationUser> _userManager;

        public TaxAccountContext(
            IHttpContextAccessor httpContextAccessor,
            ApplicationDbContext db,
            UserManager<ApplicationUser> userManager)
        {
            _httpContextAccessor = httpContextAccessor;
            _db = db;
            _userManager = userManager;
        }

        private HttpContext? HttpContext => _httpContextAccessor.HttpContext;

        public async Task<int?> GetCurrentTaxAccountIdAsync()
        {
            var context = HttpContext;
            if (context == null) return null;

            // 1. Kiểm tra Session
            var sessionVal = context.Session.GetInt32(SessionKeyCurrentTaxAccountId);
            if (sessionVal.HasValue && sessionVal.Value > 0)
            {
                if (await HasAccessToTaxAccountAsync(sessionVal.Value))
                    return sessionVal.Value;
            }

            // 2. Kiểm tra Cookie ghi nhớ lâu dài (365 ngày)
            if (context.Request.Cookies.TryGetValue(CookieKeyCurrentTaxAccountId, out var cookieVal) 
                && int.TryParse(cookieVal, out var taxId) && taxId > 0)
            {
                if (await HasAccessToTaxAccountAsync(taxId))
                {
                    context.Session.SetInt32(SessionKeyCurrentTaxAccountId, taxId);
                    return taxId;
                }
            }

            // 3. Kiểm tra tài khoản được đánh dấu IsDefault trong CSDL của người dùng
            var userId = context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!string.IsNullOrEmpty(userId))
            {
                var defaultMapping = await _db.UserTaxAccounts
                    .AsNoTracking()
                    .Include(u => u.TaxAccount)
                    .Where(u => u.UserId == userId && u.IsDefault && u.TaxAccount != null && u.TaxAccount.IsActive)
                    .OrderByDescending(u => u.AssignedAt)
                    .FirstOrDefaultAsync();

                if (defaultMapping != null)
                {
                    await SetCurrentTaxAccountAsync(defaultMapping.TaxAccountId);
                    return defaultMapping.TaxAccountId;
                }
            }

            // 4. Fallback lấy công ty đầu tiên trong danh sách được phép truy cập
            var accessible = await GetAccessibleTaxAccountsAsync();
            if (accessible.Any())
            {
                var defaultAccount = accessible.FirstOrDefault();
                if (defaultAccount != null)
                {
                    await SetCurrentTaxAccountAsync(defaultAccount.Id);
                    return defaultAccount.Id;
                }
            }

            return null;
        }

        public async Task<TaxAccount?> GetCurrentTaxAccountAsync()
        {
            var id = await GetCurrentTaxAccountIdAsync();
            if (!id.HasValue) return null;

            return await _db.TaxAccounts.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id.Value);
        }

        public async Task SetCurrentTaxAccountAsync(int taxAccountId)
        {
            var context = HttpContext;
            if (context != null)
            {
                if (taxAccountId <= 0)
                {
                    context.Session.Remove(SessionKeyCurrentTaxAccountId);
                    context.Response.Cookies.Delete(CookieKeyCurrentTaxAccountId);
                }
                else
                {
                    context.Session.SetInt32(SessionKeyCurrentTaxAccountId, taxAccountId);
                    context.Response.Cookies.Append(CookieKeyCurrentTaxAccountId, taxAccountId.ToString(), new CookieOptions
                    {
                        HttpOnly = true,
                        SameSite = SameSiteMode.Lax,
                        IsEssential = true,
                        Expires = DateTimeOffset.UtcNow.AddDays(365)
                    });

                    // Đồng bộ lưu trạng thái công ty đang chọn (IsDefault = true) vào CSDL để ghi nhớ vĩnh viễn
                    var userId = context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
                    if (!string.IsNullOrEmpty(userId))
                    {
                        var userMappings = await _db.UserTaxAccounts
                            .Where(u => u.UserId == userId)
                            .ToListAsync();

                        // Nếu là Kế toán và chọn công ty chưa có trong bảng UserTaxAccounts thì tự tạo mapping để lưu IsDefault
                        if (context.User!.IsInRole("Accountant") && !userMappings.Any(m => m.TaxAccountId == taxAccountId))
                        {
                            var newMapping = new UserTaxAccount
                            {
                                UserId = userId,
                                TaxAccountId = taxAccountId,
                                IsDefault = true,
                                CanManage = true,
                                AssignedAt = DateTime.Now
                            };
                            _db.UserTaxAccounts.Add(newMapping);
                            userMappings.Add(newMapping);
                        }

                        if (userMappings.Any())
                        {
                            bool changed = true;
                            foreach (var m in userMappings)
                            {
                                m.IsDefault = (m.TaxAccountId == taxAccountId);
                            }
                            if (changed)
                            {
                                await _db.SaveChangesAsync();
                            }
                        }
                    }
                }
            }
        }

        public async Task<List<TaxAccount>> GetAccessibleTaxAccountsAsync()
        {
            var context = HttpContext;
            if (context?.User == null || !context.User.Identity?.IsAuthenticated == true)
            {
                return new List<TaxAccount>();
            }

            // Yêu cầu 2: Tài khoản Kế toán (Accountant) quản lý được tất cả công ty và hộ kinh doanh
            if (context.User.IsInRole("Accountant"))
            {
                return await _db.TaxAccounts
                    .AsNoTracking()
                    .Where(t => t.IsActive)
                    .OrderByDescending(t => t.CreatedAt)
                    .ToListAsync();
            }

            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
            {
                return new List<TaxAccount>();
            }

            // Yêu cầu 1: Tài khoản Admin chỉ được xem đúng 1 công ty hoặc hộ kinh doanh của mình
            if (context.User.IsInRole("Admin"))
            {
                var ownCompany = await _db.UserTaxAccounts
                    .AsNoTracking()
                    .Where(u => u.UserId == userId && u.TaxAccount != null && u.TaxAccount.IsActive)
                    .OrderByDescending(u => u.IsDefault)
                    .ThenByDescending(u => u.AssignedAt)
                    .Select(u => u.TaxAccount!)
                    .FirstOrDefaultAsync();

                return ownCompany != null ? new List<TaxAccount> { ownCompany } : new List<TaxAccount>();
            }

            var userAccounts = await _db.UserTaxAccounts
                .AsNoTracking()
                .Where(u => u.UserId == userId && u.TaxAccount != null && u.TaxAccount.IsActive)
                .Select(u => u.TaxAccount!)
                .OrderByDescending(t => t.CreatedAt)
                .ToListAsync();

            return userAccounts;
        }

        public async Task<bool> HasAccessToTaxAccountAsync(int taxAccountId)
        {
            var context = HttpContext;
            if (context?.User == null || !context.User.Identity?.IsAuthenticated == true)
            {
                return false;
            }

            // Kế toán được quyền truy cập và quản lý tất cả công ty / hộ kinh doanh đang hoạt động
            if (context.User.IsInRole("Accountant"))
            {
                return await _db.TaxAccounts.AnyAsync(t => t.Id == taxAccountId && t.IsActive);
            }

            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId)) return false;

            // Admin chỉ được truy cập đúng công ty / hộ kinh doanh duy nhất của mình
            if (context.User.IsInRole("Admin"))
            {
                var ownCompanyId = await _db.UserTaxAccounts
                    .AsNoTracking()
                    .Where(u => u.UserId == userId && u.TaxAccount != null && u.TaxAccount.IsActive)
                    .OrderByDescending(u => u.IsDefault)
                    .ThenByDescending(u => u.AssignedAt)
                    .Select(u => (int?)u.TaxAccountId)
                    .FirstOrDefaultAsync();

                return ownCompanyId.HasValue && ownCompanyId.Value == taxAccountId;
            }

            return await _db.UserTaxAccounts
                .AnyAsync(u => u.UserId == userId && u.TaxAccountId == taxAccountId && u.TaxAccount!.IsActive);
        }
    }
}
