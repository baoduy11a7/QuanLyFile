using InvoiceManager.Data;
using InvoiceManager.Models.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
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

            // 2. Kiểm tra Cookie
            if (context.Request.Cookies.TryGetValue(CookieKeyCurrentTaxAccountId, out var cookieVal) 
                && int.TryParse(cookieVal, out var taxId) && taxId > 0)
            {
                if (await HasAccessToTaxAccountAsync(taxId))
                {
                    context.Session.SetInt32(SessionKeyCurrentTaxAccountId, taxId);
                    return taxId;
                }
            }

            // 3. Fallback lấy tài khoản mặc định hoặc đầu tiên của người dùng
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

        public Task SetCurrentTaxAccountAsync(int taxAccountId)
        {
            var context = HttpContext;
            if (context != null)
            {
                context.Session.SetInt32(SessionKeyCurrentTaxAccountId, taxAccountId);
                context.Response.Cookies.Append(CookieKeyCurrentTaxAccountId, taxAccountId.ToString(), new CookieOptions
                {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Lax,
                    Expires = DateTimeOffset.UtcNow.AddDays(30)
                });
            }

            return Task.CompletedTask;
        }

        public async Task<List<TaxAccount>> GetAccessibleTaxAccountsAsync()
        {
            var context = HttpContext;
            if (context?.User == null || !context.User.Identity?.IsAuthenticated == true)
            {
                // Nếu chưa đăng nhập, trả về tài khoản kích hoạt đầu tiên (để hiển thị mẫu nếu cần)
                return await _db.TaxAccounts.Where(t => t.IsActive).Take(5).ToListAsync();
            }

            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            var isAdmin = context.User.IsInRole("Admin");

            if (isAdmin)
            {
                return await _db.TaxAccounts.Where(t => t.IsActive).OrderBy(t => t.TaxCode).ToListAsync();
            }

            if (string.IsNullOrEmpty(userId))
            {
                return new List<TaxAccount>();
            }

            // User thường: chỉ lấy các tài khoản được gán
            return await _db.UserTaxAccounts
                .Where(u => u.UserId == userId && u.TaxAccount!.IsActive)
                .Select(u => u.TaxAccount!)
                .OrderBy(t => t.TaxCode)
                .ToListAsync();
        }

        public async Task<bool> HasAccessToTaxAccountAsync(int taxAccountId)
        {
            var context = HttpContext;
            if (context?.User == null || !context.User.Identity?.IsAuthenticated == true)
            {
                return true; // Cho phép chế độ xem trước nếu chưa đăng nhập
            }

            if (context.User.IsInRole("Admin"))
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
