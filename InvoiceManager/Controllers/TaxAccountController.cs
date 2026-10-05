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
        private readonly ITaxCodeLookupService _taxCodeLookupService;

        public TaxAccountController(
            ApplicationDbContext db,
            ITaxAccountContext taxAccountContext,
            IAuditLogService auditLog,
            ITaxCodeLookupService taxCodeLookupService)
        {
            _db = db;
            _taxAccountContext = taxAccountContext;
            _auditLog = auditLog;
            _taxCodeLookupService = taxCodeLookupService;
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
        public IActionResult Create(string? taxCode = null, string? companyName = null, string? address = null)
        {
            return View(new CreateTaxAccountViewModel
            {
                TaxCode = taxCode ?? "",
                CompanyName = companyName ?? "",
                Address = address ?? ""
            });
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

        [HttpGet]
        public async Task<IActionResult> LookupApi(string taxCode)
        {
            var result = await _taxCodeLookupService.LookupAsync(taxCode);
            return Json(result);
        }

        [HttpGet]
        public async Task<IActionResult> Lookup(string? taxCode)
        {
            TaxCodeLookupResult? result = null;
            if (!string.IsNullOrWhiteSpace(taxCode))
            {
                result = await _taxCodeLookupService.LookupAsync(taxCode);
            }
            ViewBag.InitialTaxCode = taxCode ?? "";
            return View(result);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> QuickAddFromTaxCode(string taxCode)
        {
            var lookup = await _taxCodeLookupService.LookupAsync(taxCode);
            if (!lookup.Success)
            {
                TempData["ErrorMessage"] = lookup.Message ?? "Không thể lấy thông tin từ mã số thuế này.";
                return RedirectToAction(nameof(Lookup), new { taxCode });
            }

            var cleanTaxCode = lookup.TaxCode!.Trim();
            var existing = await _db.TaxAccounts.AnyAsync(t => t.TaxCode == cleanTaxCode);
            if (existing)
            {
                TempData["ErrorMessage"] = $"Mã số thuế {cleanTaxCode} đã tồn tại trong hệ thống.";
                return RedirectToAction(nameof(Index));
            }

            var account = new TaxAccount
            {
                TaxCode = cleanTaxCode,
                CompanyName = lookup.CompanyName?.Trim() ?? "Công ty mới",
                Address = lookup.Address?.Trim() ?? "",
                IsActive = true,
                CreatedAt = DateTime.Now
            };

            _db.TaxAccounts.Add(account);
            await _db.SaveChangesAsync();

            await _auditLog.LogActionAsync("Thêm nhanh từ Cổng Thuế", $"MST: {account.TaxCode}", account.CompanyName, account.Id);
            TempData["SuccessMessage"] = $"Đã thêm thành công doanh nghiệp {account.CompanyName} (MST: {account.TaxCode}) từ Cổng Thuế!";
            return RedirectToAction(nameof(Index));
        }
    }
}
