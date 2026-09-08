using InvoiceManager.Data;
using InvoiceManager.Models.Entities;
using InvoiceManager.Models.ViewModels;
using InvoiceManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Threading.Tasks;

namespace InvoiceManager.Controllers
{
    [Authorize]
    public class TaxAccountController : Controller
    {
        private readonly ApplicationDbContext _db;
        private readonly ITaxAccountContext _taxAccountContext;
        private readonly IAuditLogService _auditLog;

        public TaxAccountController(
            ApplicationDbContext db,
            ITaxAccountContext taxAccountContext,
            IAuditLogService auditLog)
        {
            _db = db;
            _taxAccountContext = taxAccountContext;
            _auditLog = auditLog;
        }

        public async Task<IActionResult> Index()
        {
            var accounts = await _taxAccountContext.GetAccessibleTaxAccountsAsync();
            var currentId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            ViewBag.CurrentTaxAccountId = currentId;
            return View(accounts);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Switch(int id, string? returnUrl = null)
        {
            if (await _taxAccountContext.HasAccessToTaxAccountAsync(id))
            {
                await _taxAccountContext.SetCurrentTaxAccountAsync(id);
                var account = await _db.TaxAccounts.FindAsync(id);
                await _auditLog.LogActionAsync("Chuyển đổi Tài khoản thuế", $"MST: {account?.TaxCode} ({account?.CompanyName})", null, id);
                TempData["SuccessMessage"] = $"Đã chuyển sang quản lý: {account?.CompanyName} (MST: {account?.TaxCode})";
            }
            else
            {
                TempData["ErrorMessage"] = "Bạn không có quyền truy cập tài khoản thuế này.";
            }

            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return Redirect(returnUrl);
            }

            return RedirectToAction("Index", "Invoice");
        }

        [HttpGet]
        [Authorize(Roles = "Admin")]
        public IActionResult Create()
        {
            return View(new CreateTaxAccountViewModel());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Create(CreateTaxAccountViewModel model)
        {
            if (!ModelState.IsValid) return View(model);

            var existing = await _db.TaxAccounts.AnyAsync(t => t.TaxCode == model.TaxCode.Trim());
            if (existing)
            {
                ModelState.AddModelError(nameof(model.TaxCode), "Mã số thuế này đã tồn tại trong hệ thống.");
                return View(model);
            }

            var account = new TaxAccount
            {
                TaxCode = model.TaxCode.Trim(),
                CompanyName = model.CompanyName.Trim(),
                Address = model.Address.Trim(),
                Email = model.Email?.Trim(),
                PhoneNumber = model.PhoneNumber?.Trim(),
                Representative = model.Representative?.Trim(),
                IsActive = true,
                CreatedAt = DateTime.Now
            };

            _db.TaxAccounts.Add(account);
            await _db.SaveChangesAsync();

            await _auditLog.LogActionAsync("Thêm mới Tài khoản thuế", $"MST: {account.TaxCode}", account.CompanyName, account.Id);
            TempData["SuccessMessage"] = $"Thêm mới công ty {account.CompanyName} thành công!";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SubmitFeedback(string requestType, string title, string description, string? contactName, string? contactPhone)
        {
            var taxAccountId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
            if (!taxAccountId.HasValue) return Json(new { success = false, message = "Chưa chọn tài khoản thuế." });

            var feedback = new FeatureRequest
            {
                TaxAccountId = taxAccountId.Value,
                RequestType = string.IsNullOrWhiteSpace(requestType) ? "Báo lỗi" : requestType,
                Title = string.IsNullOrWhiteSpace(title) ? "Yêu cầu hỗ trợ" : title,
                Description = description ?? "",
                ContactName = contactName,
                ContactPhone = contactPhone,
                Status = "Chờ tiếp nhận",
                CreatedAt = DateTime.Now
            };

            _db.FeatureRequests.Add(feedback);
            await _db.SaveChangesAsync();

            await _auditLog.LogActionAsync("Gửi yêu cầu/báo lỗi", feedback.Title, feedback.Description, taxAccountId.Value);

            return Json(new { success = true, message = "Yêu cầu của bạn đã được gửi tới đội ngũ kỹ thuật. Chúng tôi sẽ xử lý sớm nhất!" });
        }
    }
}
