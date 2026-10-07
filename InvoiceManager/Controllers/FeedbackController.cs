using InvoiceManager.Data;
using InvoiceManager.Models.Entities;
using InvoiceManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace InvoiceManager.Controllers
{
    [Authorize]
    public class FeedbackController : Controller
    {
        private readonly ApplicationDbContext _db;
        private readonly ITaxAccountContext _taxAccountContext;
        private readonly IAuditLogService _auditLog;
        private readonly UserManager<ApplicationUser> _userManager;

        public FeedbackController(
            ApplicationDbContext db,
            ITaxAccountContext taxAccountContext,
            IAuditLogService auditLog,
            UserManager<ApplicationUser> userManager)
        {
            _db = db;
            _taxAccountContext = taxAccountContext;
            _auditLog = auditLog;
            _userManager = userManager;
        }

        [HttpGet]
        public async Task<IActionResult> Index(string? requestType, string? status, string? keyword)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            var taxAccount = await _taxAccountContext.GetCurrentTaxAccountAsync();
            bool isAdmin = User.IsInRole("Admin");

            var query = _db.FeatureRequests
                .Include(f => f.TaxAccount)
                .AsQueryable();

            if (!isAdmin && taxAccountId.HasValue)
            {
                query = query.Where(f => f.TaxAccountId == taxAccountId.Value);
            }

            if (!string.IsNullOrWhiteSpace(requestType))
            {
                query = query.Where(f => f.RequestType == requestType);
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                query = query.Where(f => f.Status == status);
            }

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                var kw = keyword.Trim();
                query = query.Where(f => f.Title.Contains(kw) || f.Description.Contains(kw) || (f.ContactName != null && f.ContactName.Contains(kw)));
            }

            var requests = await query.OrderByDescending(f => f.CreatedAt).ToListAsync();

            // Tổng hợp
            ViewBag.TotalCount = requests.Count;
            ViewBag.PendingCount = requests.Count(r => r.Status == "Chờ tiếp nhận");
            ViewBag.ProcessingCount = requests.Count(r => r.Status == "Đang xử lý");
            ViewBag.CompletedCount = requests.Count(r => r.Status == "Đã xử lý");
            ViewBag.CurrentTaxAccount = taxAccount;
            ViewBag.RequestTypeFilter = requestType;
            ViewBag.StatusFilter = status;
            ViewBag.Keyword = keyword;
            ViewBag.IsAdmin = isAdmin;

            return View(requests);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(string requestType, string title, string description, string? contactName, string? contactPhone)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            if (!taxAccountId.HasValue)
            {
                var accounts = await _taxAccountContext.GetAccessibleTaxAccountsAsync();
                if (accounts.Any())
                {
                    taxAccountId = accounts.First().Id;
                }
                else
                {
                    if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                        return Json(new { success = false, message = "Chưa chọn tài khoản thuế." });

                    TempData["ErrorMessage"] = "Chưa chọn tài khoản thuế làm việc.";
                    return RedirectToAction(nameof(Index));
                }
            }

            var user = await _userManager.GetUserAsync(User);
            var item = new FeatureRequest
            {
                TaxAccountId = taxAccountId.Value,
                RequestType = string.IsNullOrWhiteSpace(requestType) ? "Báo lỗi" : requestType.Trim(),
                Title = string.IsNullOrWhiteSpace(title) ? "Yêu cầu từ người dùng" : title.Trim(),
                Description = description?.Trim() ?? "",
                ContactName = !string.IsNullOrWhiteSpace(contactName) ? contactName.Trim() : (user?.FullName ?? User.Identity?.Name),
                ContactPhone = !string.IsNullOrWhiteSpace(contactPhone) ? contactPhone.Trim() : user?.PhoneNumber,
                Status = "Chờ tiếp nhận",
                CreatedAt = DateTime.Now
            };

            _db.FeatureRequests.Add(item);
            await _db.SaveChangesAsync();

            await _auditLog.LogActionAsync("Gửi phản hồi / yêu cầu", $"{item.RequestType}: {item.Title}", item.Description, taxAccountId.Value);

            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
            {
                return Json(new { success = true, message = "Yêu cầu của bạn đã được gửi thành công! Đội ngũ kỹ thuật sẽ tiếp nhận và phản hồi sớm nhất." });
            }

            TempData["SuccessMessage"] = "Gửi yêu cầu / báo lỗi thành công!";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [Authorize(Roles = "Admin")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateStatus(int id, string status)
        {
            var item = await _db.FeatureRequests.FindAsync(id);
            if (item == null)
            {
                TempData["ErrorMessage"] = "Không tìm thấy yêu cầu.";
                return RedirectToAction(nameof(Index));
            }

            item.Status = status;
            await _db.SaveChangesAsync();

            await _auditLog.LogActionAsync("Cập nhật trạng thái yêu cầu", $"ID {item.Id}: {item.Title}", $"Trạng thái mới: {status}", item.TaxAccountId);

            TempData["SuccessMessage"] = $"Cập nhật trạng thái yêu cầu #{item.Id} thành: {status}";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [Authorize(Roles = "Admin")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var item = await _db.FeatureRequests.FindAsync(id);
            if (item != null)
            {
                _db.FeatureRequests.Remove(item);
                await _db.SaveChangesAsync();
                await _auditLog.LogActionAsync("Xóa yêu cầu", $"ID {item.Id}: {item.Title}", null, item.TaxAccountId);
                TempData["SuccessMessage"] = "Đã xóa yêu cầu thành công.";
            }

            return RedirectToAction(nameof(Index));
        }
    }
}
