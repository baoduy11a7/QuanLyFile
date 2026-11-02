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

                        if (userMappings.Any())
                        {
                            bool changed = false;
                            foreach (var m in userMappings)
                            {
                                bool shouldBeDefault = (m.TaxAccountId == taxAccountId);
                                if (m.IsDefault != shouldBeDefault)
                                {
                                    m.IsDefault = shouldBeDefault;
                                    changed = true;
                                }
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
                // Nếu chưa đăng nhập (từ ngoài màn hình Login), trả về danh sách các công ty đang hoạt động
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

            var userAccounts = await _db.UserTaxAccounts
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
                return await _db.TaxAccounts.AnyAsync(t => t.Id == taxAccountId && t.IsActive);
            }

            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId)) return false;

            return await _db.UserTaxAccounts
                .AnyAsync(u => u.UserId == userId && u.TaxAccountId == taxAccountId && u.TaxAccount!.IsActive);
        }
    }
}
