using InvoiceManager.Data;
using InvoiceManager.Models.Entities;
using InvoiceManager.Models.ViewModels;
using InvoiceManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Security.Claims;
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

            var accountIds = accounts.Select(a => a.Id).ToList();

            var invoiceCounts = await _db.Invoices
                .AsNoTracking()
                .Where(i => accountIds.Contains(i.TaxAccountId))
                .GroupBy(i => new { i.TaxAccountId, i.InvoiceType })
                .Select(g => new
                {
                    g.Key.TaxAccountId,
                    g.Key.InvoiceType,
                    Count = g.Count()
                })
                .ToListAsync();

            var viewModels = accounts.Select(a => new TaxAccountListItemViewModel
            {
                Id = a.Id,
                TaxCode = a.TaxCode,
                CompanyName = a.CompanyName,
                Address = a.Address,
                Email = a.Email,
                PhoneNumber = a.PhoneNumber,
                Representative = a.Representative,
                IsActive = a.IsActive,
                CreatedAt = a.CreatedAt,
                AccountingSoftware = "SMART PRO",
                PurchaseInvoiceCount = invoiceCounts.FirstOrDefault(x => x.TaxAccountId == a.Id && x.InvoiceType == "MuaVao")?.Count ?? 0,
                SoldInvoiceCount = invoiceCounts.FirstOrDefault(x => x.TaxAccountId == a.Id && x.InvoiceType == "BanRa")?.Count ?? 0,
                IsCurrent = a.Id == currentId
            }).ToList();

            return View(viewModels);
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
        [Authorize(Roles = "Admin,Accountant")]
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
        [Authorize(Roles = "Admin,Accountant")]
        public async Task<IActionResult> Create(CreateTaxAccountViewModel model)
        {
            if (!ModelState.IsValid) return View(model);

            var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(currentUserId)) return Challenge();

            // Kiểm tra xem chính tài khoản hiện tại đã có công ty này chưa
            var userAcc = await _db.UserTaxAccounts
                .Include(u => u.TaxAccount)
                .FirstOrDefaultAsync(u => u.UserId == currentUserId && u.TaxAccount != null && u.TaxAccount.TaxCode == model.TaxCode.Trim());

            if (userAcc?.TaxAccount != null)
            {
                var existing = userAcc.TaxAccount;
                existing.IsActive = true;
                existing.CompanyName = model.CompanyName.Trim();
                existing.Address = model.Address.Trim();
                if (!string.IsNullOrEmpty(model.Email)) existing.Email = model.Email.Trim();
                if (!string.IsNullOrEmpty(model.PhoneNumber)) existing.PhoneNumber = model.PhoneNumber.Trim();
                if (!string.IsNullOrEmpty(model.Representative)) existing.Representative = model.Representative.Trim();

                await _db.SaveChangesAsync();
                await _taxAccountContext.SetCurrentTaxAccountAsync(existing.Id);
                TempData["SuccessMessage"] = $"Đã cập nhật/kích hoạt lại công ty {existing.CompanyName} (MST: {existing.TaxCode}) thành công!";
                return RedirectToAction(nameof(Index));
            }

            // Nếu người dùng hiện tại chưa quản lý công ty này -> tạo mới riêng biệt để đảm bảo bảo mật dữ liệu độc lập
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

            _db.UserTaxAccounts.Add(new UserTaxAccount
            {
                UserId = currentUserId,
                TaxAccountId = account.Id,
                IsDefault = true,
                CanManage = true,
                AssignedAt = DateTime.Now
            });
            await _db.SaveChangesAsync();

            await _taxAccountContext.SetCurrentTaxAccountAsync(account.Id);

            await _auditLog.LogActionAsync("Thêm mới Tài khoản thuế", $"MST: {account.TaxCode}", account.CompanyName, account.Id);
            TempData["SuccessMessage"] = $"Thêm mới công ty {account.CompanyName} thành công!";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize]
        public async Task<IActionResult> Delete(int id)
        {
            var hasAccess = await _taxAccountContext.HasAccessToTaxAccountAsync(id);
            if (!hasAccess && !User.IsInRole("Admin"))
            {
                TempData["ErrorMessage"] = "Bạn không có quyền xóa doanh nghiệp này.";
                return RedirectToAction(nameof(Index));
            }

            var account = await _db.TaxAccounts.FindAsync(id);
            if (account == null)
            {
                TempData["ErrorMessage"] = "Không tìm thấy tài khoản thuế cần xóa.";
                return RedirectToAction(nameof(Index));
            }

            try
            {
                // 1. Dọn dẹp các dữ liệu phụ thuộc
                var invoices = await _db.Invoices
                    .Include(i => i.Details)
                    .Where(i => i.TaxAccountId == id)
                    .ToListAsync();
                _db.Invoices.RemoveRange(invoices);

                var userMappings = await _db.UserTaxAccounts
                    .Where(u => u.TaxAccountId == id)
                    .ToListAsync();
                _db.UserTaxAccounts.RemoveRange(userMappings);

                var remoteConns = await _db.RemoteConnections
                    .Where(r => r.TaxAccountId == id)
                    .ToListAsync();
                _db.RemoteConnections.RemoveRange(remoteConns);

                var syncLogs = await _db.SyncLogs
                    .Where(s => s.TaxAccountId == id)
                    .ToListAsync();
                _db.SyncLogs.RemoveRange(syncLogs);

                var featureRequests = await _db.FeatureRequests
                    .Where(f => f.TaxAccountId == id)
                    .ToListAsync();
                _db.FeatureRequests.RemoveRange(featureRequests);

                // Giữ vết kiểm toán: set TaxAccountId = null
                var auditLogs = await _db.AuditLogs
                    .Where(a => a.TaxAccountId == id)
                    .ToListAsync();
                foreach (var log in auditLogs)
                {
                    log.TaxAccountId = null;
                }

                // 2. Xóa tài khoản thuế
                _db.TaxAccounts.Remove(account);
                await _db.SaveChangesAsync();

                // 3. Nếu đang kích hoạt tài khoản này, tự động chuyển sang tài khoản khác hoặc xóa session
                var currentId = await _taxAccountContext.GetCurrentTaxAccountIdAsync();
                if (currentId == id)
                {
                    var accessible = await _taxAccountContext.GetAccessibleTaxAccountsAsync();
                    var nextAccount = accessible.FirstOrDefault(a => a.Id != id);
                    if (nextAccount != null)
                    {
                        await _taxAccountContext.SetCurrentTaxAccountAsync(nextAccount.Id);
                    }
                    else
                    {
                        await _taxAccountContext.SetCurrentTaxAccountAsync(0);
                    }
                }

                await _auditLog.LogActionAsync(
                    "Xóa Tài khoản thuế",
                    $"MST: {account.TaxCode}",
                    $"Đã xóa tài khoản {account.CompanyName} cùng {invoices.Count} hóa đơn liên quan.",
                    null);

                TempData["SuccessMessage"] = $"Đã xóa thành công tài khoản thuế: {account.CompanyName} (MST: {account.TaxCode})!";
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = $"Lỗi khi xóa tài khoản thuế: {ex.Message}";
            }

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
        [Authorize(Roles = "Admin,Accountant")]
        public async Task<IActionResult> QuickAddFromTaxCode(string taxCode, string? returnUrl = null)
        {
            var lookup = await _taxCodeLookupService.LookupAsync(taxCode);
            if (!lookup.Success)
            {
                TempData["ErrorMessage"] = lookup.Message ?? "Không thể lấy thông tin từ mã số thuế này.";
                return RedirectToAction(nameof(Lookup), new { taxCode });
            }

            var cleanTaxCode = lookup.TaxCode!.Trim();
            var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(currentUserId)) return Challenge();

            var userAcc = await _db.UserTaxAccounts
                .Include(u => u.TaxAccount)
                .FirstOrDefaultAsync(u => u.UserId == currentUserId && u.TaxAccount != null && u.TaxAccount.TaxCode == cleanTaxCode);

            TaxAccount account;
            if (userAcc?.TaxAccount != null)
            {
                account = userAcc.TaxAccount;
                account.IsActive = true;
                if (!string.IsNullOrWhiteSpace(lookup.CompanyName)) account.CompanyName = lookup.CompanyName.Trim();
                if (!string.IsNullOrWhiteSpace(lookup.Address)) account.Address = lookup.Address.Trim();
            }
            else
            {
                account = new TaxAccount
                {
                    TaxCode = cleanTaxCode,
                    CompanyName = lookup.CompanyName?.Trim() ?? "Công ty mới",
                    Address = lookup.Address?.Trim() ?? "",
                    IsActive = true,
                    CreatedAt = DateTime.Now
                };
                _db.TaxAccounts.Add(account);
                await _db.SaveChangesAsync();

                _db.UserTaxAccounts.Add(new UserTaxAccount
                {
                    UserId = currentUserId,
                    TaxAccountId = account.Id,
                    IsDefault = true,
                    CanManage = true,
                    AssignedAt = DateTime.Now
                });
            }

            await _db.SaveChangesAsync();
            await _taxAccountContext.SetCurrentTaxAccountAsync(account.Id);

            await _auditLog.LogActionAsync("Thêm/Kích hoạt từ Cổng Thuế", $"MST: {account.TaxCode}", account.CompanyName, account.Id);
            TempData["SuccessMessage"] = $"Đã thêm và kích hoạt thành công doanh nghiệp {account.CompanyName} (MST: {account.TaxCode}) vào tài khoản của bạn!";

            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return Redirect(returnUrl);
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin,Accountant")]
        public async Task<IActionResult> UpdateFromTaxCode(string taxCode, string? returnUrl = null)
        {
            var lookup = await _taxCodeLookupService.LookupAsync(taxCode);
            if (!lookup.Success)
            {
                TempData["ErrorMessage"] = lookup.Message ?? "Không thể cào thông tin từ mã số thuế này.";
                return RedirectToAction(nameof(Lookup), new { taxCode });
            }

            var cleanTaxCode = lookup.TaxCode!.Trim();
            var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(currentUserId)) return Challenge();

            var userAcc = await _db.UserTaxAccounts
                .Include(u => u.TaxAccount)
                .FirstOrDefaultAsync(u => u.UserId == currentUserId && u.TaxAccount != null && u.TaxAccount.TaxCode == cleanTaxCode);

            if (userAcc?.TaxAccount == null)
            {
                TempData["ErrorMessage"] = $"Bạn chưa quản lý tài khoản thuế {cleanTaxCode} trong danh sách của mình.";
                return RedirectToAction(nameof(Lookup), new { taxCode });
            }

            var existingAccount = userAcc.TaxAccount;
            var oldName = existingAccount.CompanyName;
            existingAccount.IsActive = true;
            existingAccount.CompanyName = lookup.CompanyName?.Trim() ?? existingAccount.CompanyName;
            existingAccount.Address = lookup.Address?.Trim() ?? existingAccount.Address;

            await _db.SaveChangesAsync();
            await _taxAccountContext.SetCurrentTaxAccountAsync(existingAccount.Id);

            await _auditLog.LogActionAsync("Cập nhật lại từ Cổng Thuế", $"MST: {existingAccount.TaxCode}", $"Đổi từ '{oldName}' thành '{existingAccount.CompanyName}'", existingAccount.Id);
            TempData["SuccessMessage"] = $"Đã cập nhật lại thông tin doanh nghiệp '{existingAccount.CompanyName}' (MST: {existingAccount.TaxCode}) thành công từ Cổng Thuế!";

            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return Redirect(returnUrl);
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin,Accountant")]
        public async Task<IActionResult> QuickCrawlAndSelect([FromBody] QuickCrawlRequest model)
        {
            if (string.IsNullOrWhiteSpace(model?.TaxCode))
            {
                return Json(new { success = false, message = "Vui lòng nhập mã số thuế cần cào thông tin." });
            }

            var lookup = await _taxCodeLookupService.LookupAsync(model.TaxCode);
            if (!lookup.Success)
            {
                return Json(new { success = false, message = lookup.Message ?? "Không thể cào thông tin từ mã số thuế này." });
            }

            var cleanTaxCode = lookup.TaxCode!.Trim();
            var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(currentUserId))
            {
                return Json(new { success = false, message = "Phiên làm việc hết hạn. Vui lòng đăng nhập lại." });
            }

            var userAcc = await _db.UserTaxAccounts
                .Include(u => u.TaxAccount)
                .FirstOrDefaultAsync(u => u.UserId == currentUserId && u.TaxAccount != null && u.TaxAccount.TaxCode == cleanTaxCode);

            bool isNew = false;
            TaxAccount account;

            if (userAcc?.TaxAccount == null)
            {
                isNew = true;
                account = new TaxAccount
                {
                    TaxCode = cleanTaxCode,
                    CompanyName = lookup.CompanyName?.Trim() ?? "Doanh nghiệp mới",
                    Address = lookup.Address?.Trim() ?? "",
                    IsActive = true,
                    CreatedAt = DateTime.Now
                };
                _db.TaxAccounts.Add(account);
                await _db.SaveChangesAsync();

                _db.UserTaxAccounts.Add(new UserTaxAccount
                {
                    UserId = currentUserId,
                    TaxAccountId = account.Id,
                    IsDefault = true,
                    CanManage = true,
                    AssignedAt = DateTime.Now
                });
                await _db.SaveChangesAsync();
            }
            else
            {
                account = userAcc.TaxAccount;
                account.IsActive = true;
                if (!string.IsNullOrWhiteSpace(lookup.CompanyName)) account.CompanyName = lookup.CompanyName.Trim();
                if (!string.IsNullOrWhiteSpace(lookup.Address)) account.Address = lookup.Address.Trim();
                await _db.SaveChangesAsync();
            }

            await _taxAccountContext.SetCurrentTaxAccountAsync(account.Id);

            await _auditLog.LogActionAsync(
                isNew ? "Cào và thêm Doanh nghiệp mới" : "Cào và kích hoạt lại Doanh nghiệp",
                $"MST: {account.TaxCode}",
                account.CompanyName,
                account.Id);

            return Json(new
            {
                success = true,
                isNew = isNew,
                taxAccountId = account.Id,
                taxCode = account.TaxCode,
                companyName = account.CompanyName,
                address = account.Address,
                status = lookup.Status ?? "Đang hoạt động",
                message = isNew
                    ? $"Đã cào thông tin và thêm doanh nghiệp '{account.CompanyName}' (MST: {account.TaxCode}) vào tài khoản của bạn thành công!"
                    : $"Đã cào thông tin và cập nhật doanh nghiệp '{account.CompanyName}' (MST: {account.TaxCode}) thành công!"
            });
        }
    }
}
