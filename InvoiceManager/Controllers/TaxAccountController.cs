using InvoiceManager.Data;
using InvoiceManager.Models.Entities;
using InvoiceManager.Models.ViewModels;
using InvoiceManager.Services;
using InvoiceManager.Services.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
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
        private readonly ICredentialProtector? _credentialProtector;

        public TaxAccountController(
            ApplicationDbContext db,
            ITaxAccountContext taxAccountContext,
            IAuditLogService auditLog,
            ITaxCodeLookupService taxCodeLookupService,
            ICredentialProtector? credentialProtector = null)
        {
            _db = db;
            _taxAccountContext = taxAccountContext;
            _auditLog = auditLog;
            _taxCodeLookupService = taxCodeLookupService;
            _credentialProtector = credentialProtector;
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

            var remoteConns = await _db.RemoteConnections
                .AsNoTracking()
                .Where(r => accountIds.Contains(r.TaxAccountId))
                .ToListAsync();

            var viewModels = accounts.Select(a => new TaxAccountListItemViewModel
            {
                Id = a.Id,
                TaxCode = a.TaxCode,
                BusinessType = string.IsNullOrWhiteSpace(a.BusinessType) ? "DoanhNghiep" : a.BusinessType,
                ShortName = a.ShortName,
                AccountantName = a.AccountantName,
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
                IsCurrent = a.Id == currentId,
                HasEInvoiceConnected = remoteConns.Any(r => r.TaxAccountId == a.Id && r.ProviderName == "Gdt" && !string.IsNullOrEmpty(r.Username)),
                HasETaxConnected = remoteConns.Any(r => r.TaxAccountId == a.Id && r.ProviderName == "Etax" && !string.IsNullOrEmpty(r.Username))
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
                var displayName = !string.IsNullOrWhiteSpace(account?.ShortName) ? $"{account.CompanyName} ({account.ShortName})" : account?.CompanyName;
                await _auditLog.LogActionAsync("Chuyển đổi Công ty / HKD", $"MST: {account?.TaxCode} ({displayName})", null, id);
                TempData["SuccessMessage"] = $"Đã chuyển sang làm việc với: {account?.CompanyName} (MST: {account?.TaxCode})";
            }
            else
            {
                TempData["ErrorMessage"] = "Bạn không có quyền truy cập công ty / hộ kinh doanh này.";
            }

            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return Redirect(returnUrl);
            }

            return RedirectToAction("Index", "Invoice");
        }

        private bool IsRestrictedAdmin() => User?.IsInRole("Admin") == true && User?.IsInRole("Accountant") != true;

        [HttpGet]
        public IActionResult Create(string? taxCode = null, string? companyName = null, string? address = null, string? shortName = null)
        {
            if (IsRestrictedAdmin())
            {
                TempData["ErrorMessage"] = "Tài khoản Admin chỉ được xem công ty / hộ kinh doanh của mình, không có quyền thêm mới.";
                return RedirectToAction(nameof(Index));
            }

            return View(new CreateTaxAccountViewModel
            {
                BusinessType = "DoanhNghiep",
                TaxCode = taxCode ?? "",
                ShortName = shortName ?? companyName ?? "",
                CompanyName = companyName ?? "",
                Address = address ?? ""
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(CreateTaxAccountViewModel model)
        {
            if (IsRestrictedAdmin())
            {
                TempData["ErrorMessage"] = "Tài khoản Admin chỉ được xem công ty / hộ kinh doanh của mình, không có quyền thêm mới.";
                return RedirectToAction(nameof(Index));
            }

            if (string.IsNullOrWhiteSpace(model.ShortName) && !string.IsNullOrWhiteSpace(model.CompanyName))
            {
                model.ShortName = model.CompanyName.Trim();
                ModelState.Remove(nameof(model.ShortName));
            }
            if (string.IsNullOrWhiteSpace(model.CompanyName) && !string.IsNullOrWhiteSpace(model.ShortName))
            {
                model.CompanyName = model.ShortName.Trim();
                ModelState.Remove(nameof(model.CompanyName));
            }

            if (!ModelState.IsValid) return View(model);

            var account = await UpsertCompanyAndConnectionsAsync(model);

            TempData["SuccessMessage"] = $"Đã lưu và ghi nhớ công ty / hộ kinh doanh '{account.CompanyName}' (MST: {account.TaxCode}) thành công! Lần sau bạn chỉ cần chọn từ danh sách.";

            if (!string.IsNullOrEmpty(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl))
            {
                return Redirect(model.ReturnUrl);
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateAjax([FromBody] CreateTaxAccountViewModel model)
        {
            if (IsRestrictedAdmin())
            {
                return Json(new { success = false, message = "Tài khoản Admin chỉ được xem công ty / hộ kinh doanh của mình, không có quyền thêm mới." });
            }

            if (model == null || string.IsNullOrWhiteSpace(model.TaxCode))
            {
                return Json(new { success = false, message = "Vui lòng nhập Mã số thuế (*)." });
            }

            var cleanTaxCode = model.TaxCode.Trim();
            if (cleanTaxCode.Length < 10)
            {
                return Json(new { success = false, message = "Mã số thuế phải từ 10 đến 14 ký tự." });
            }

            if (string.IsNullOrWhiteSpace(model.ShortName) && string.IsNullOrWhiteSpace(model.CompanyName))
            {
                return Json(new { success = false, message = "Vui lòng nhập Tên gợi nhớ (*) hoặc Tên công ty / hộ kinh doanh." });
            }

            if (string.IsNullOrWhiteSpace(model.ShortName))
            {
                model.ShortName = model.CompanyName!.Trim();
            }
            if (string.IsNullOrWhiteSpace(model.CompanyName))
            {
                model.CompanyName = model.ShortName!.Trim();
            }

            try
            {
                var account = await UpsertCompanyAndConnectionsAsync(model);

                return Json(new
                {
                    success = true,
                    id = account.Id,
                    taxCode = account.TaxCode,
                    businessType = account.BusinessType,
                    shortName = account.ShortName,
                    companyName = account.CompanyName,
                    accountantName = account.AccountantName,
                    representative = account.Representative,
                    address = account.Address,
                    message = $"Đã lưu và ghi nhớ '{account.CompanyName}' (MST: {account.TaxCode}) thành công! Lần sau bạn chỉ cần chọn công ty trong danh sách."
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = $"Lỗi khi lưu công ty: {ex.Message}" });
            }
        }

        private async Task<TaxAccount> UpsertCompanyAndConnectionsAsync(CreateTaxAccountViewModel model)
        {
            var cleanTaxCode = model.TaxCode.Trim();
            var businessType = string.Equals(model.BusinessType, "HoKinhDoanh", StringComparison.OrdinalIgnoreCase)
                ? "HoKinhDoanh"
                : "DoanhNghiep";
            var companyName = !string.IsNullOrWhiteSpace(model.CompanyName)
                ? model.CompanyName.Trim()
                : model.ShortName.Trim();
            var shortName = !string.IsNullOrWhiteSpace(model.ShortName)
                ? model.ShortName.Trim()
                : companyName;
            var address = model.Address?.Trim() ?? "";
            var accountantName = model.AccountantName?.Trim();
            var representative = model.Representative?.Trim();

            var currentUserId = User?.FindFirstValue(ClaimTypes.NameIdentifier);

            TaxAccount? account = null;

            if (!string.IsNullOrEmpty(currentUserId))
            {
                var userAcc = await _db.UserTaxAccounts
                    .Include(u => u.TaxAccount)
                    .FirstOrDefaultAsync(u => u.UserId == currentUserId && u.TaxAccount != null && u.TaxAccount.TaxCode == cleanTaxCode);
                account = userAcc?.TaxAccount;
            }

            if (account == null)
            {
                account = await _db.TaxAccounts.FirstOrDefaultAsync(t => t.TaxCode == cleanTaxCode);
            }

            bool isNew = false;
            if (account != null)
            {
                account.IsActive = true;
                account.BusinessType = businessType;
                account.ShortName = shortName;
                account.CompanyName = companyName;
                if (!string.IsNullOrEmpty(address)) account.Address = address;
                if (!string.IsNullOrEmpty(accountantName)) account.AccountantName = accountantName;
                if (!string.IsNullOrEmpty(representative)) account.Representative = representative;
                if (!string.IsNullOrEmpty(model.Email)) account.Email = model.Email.Trim();
                if (!string.IsNullOrEmpty(model.PhoneNumber)) account.PhoneNumber = model.PhoneNumber.Trim();
                await _db.SaveChangesAsync();
            }
            else
            {
                isNew = true;
                account = new TaxAccount
                {
                    TaxCode = cleanTaxCode,
                    BusinessType = businessType,
                    ShortName = shortName,
                    AccountantName = accountantName,
                    CompanyName = companyName,
                    Representative = representative,
                    Address = address,
                    Email = model.Email?.Trim(),
                    PhoneNumber = model.PhoneNumber?.Trim(),
                    IsActive = true,
                    CreatedAt = DateTime.Now
                };
                _db.TaxAccounts.Add(account);
                await _db.SaveChangesAsync();
            }

            // Liên kết công ty vào tài khoản Kế toán hiện tại để ghi nhớ vĩnh viễn cho các lần sau
            if (!string.IsNullOrEmpty(currentUserId))
            {
                await EnsureUserCompanyMappingAsync(currentUserId, account.Id, setAsDefault: true);
            }

            // Lưu kết nối Tài khoản Hóa đơn điện tử (Provider = "Gdt") nếu có nhập
            if (!string.IsNullOrWhiteSpace(model.EInvoiceUsername) || !string.IsNullOrWhiteSpace(model.EInvoicePassword))
            {
                var einvUser = !string.IsNullOrWhiteSpace(model.EInvoiceUsername) ? model.EInvoiceUsername.Trim() : cleanTaxCode;
                await UpsertRemoteConnectionAsync(account.Id, "Gdt", einvUser, model.EInvoicePassword);
            }

            // Lưu kết nối Tài khoản Thuế điện tử (Provider = "Etax") nếu có nhập
            if (!string.IsNullOrWhiteSpace(model.ETaxUsername) || !string.IsNullOrWhiteSpace(model.ETaxPassword))
            {
                var etaxUser = !string.IsNullOrWhiteSpace(model.ETaxUsername) ? model.ETaxUsername.Trim() : $"{cleanTaxCode}-QL";
                await UpsertRemoteConnectionAsync(account.Id, "Etax", etaxUser, model.ETaxPassword);
            }

            await _taxAccountContext.SetCurrentTaxAccountAsync(account.Id);

            if (User?.Identity?.IsAuthenticated == true)
            {
                await _auditLog.LogActionAsync(
                    isNew ? "Thêm mới Công ty / Hộ kinh doanh" : "Cập nhật Công ty / Hộ kinh doanh",
                    $"MST: {account.TaxCode} ({account.ShortName})",
                    account.CompanyName,
                    account.Id);
            }

            return account;
        }

        private async Task EnsureUserCompanyMappingAsync(string userId, int taxAccountId, bool setAsDefault)
        {
            var userMappings = await _db.UserTaxAccounts
                .Where(u => u.UserId == userId)
                .ToListAsync();

            var targetMapping = userMappings.FirstOrDefault(u => u.TaxAccountId == taxAccountId);
            if (targetMapping == null)
            {
                targetMapping = new UserTaxAccount
                {
                    UserId = userId,
                    TaxAccountId = taxAccountId,
                    IsDefault = setAsDefault,
                    CanManage = true,
                    AssignedAt = DateTime.Now
                };
                _db.UserTaxAccounts.Add(targetMapping);
                userMappings.Add(targetMapping);
            }

            if (setAsDefault)
            {
                foreach (var m in userMappings)
                {
                    m.IsDefault = (m.TaxAccountId == taxAccountId);
                }
            }

            await _db.SaveChangesAsync();
        }

        private async Task UpsertRemoteConnectionAsync(int taxAccountId, string providerName, string username, string? plainPassword)
        {
            var conn = await _db.RemoteConnections
                .FirstOrDefaultAsync(r => r.TaxAccountId == taxAccountId && r.ProviderName == providerName);

            var encrypted = (!string.IsNullOrWhiteSpace(plainPassword) && _credentialProtector != null)
                ? _credentialProtector.Protect(plainPassword)
                : plainPassword;

            if (conn == null)
            {
                conn = new RemoteConnection
                {
                    TaxAccountId = taxAccountId,
                    ProviderName = providerName,
                    Username = username,
                    RememberPassword = true,
                    EncryptedPassword = encrypted,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                _db.RemoteConnections.Add(conn);
            }
            else
            {
                conn.Username = username;
                conn.RememberPassword = true;
                if (!string.IsNullOrWhiteSpace(plainPassword))
                {
                    conn.EncryptedPassword = encrypted;
                }
                conn.UpdatedAt = DateTime.UtcNow;
            }

            await _db.SaveChangesAsync();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize]
        public async Task<IActionResult> Delete(int id)
        {
            if (IsRestrictedAdmin() || !await _taxAccountContext.HasAccessToTaxAccountAsync(id))
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
        [AllowAnonymous]
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
        public async Task<IActionResult> QuickAddFromTaxCode(string taxCode, string? returnUrl = null)
        {
            if (IsRestrictedAdmin())
            {
                TempData["ErrorMessage"] = "Tài khoản Admin chỉ được xem công ty / hộ kinh doanh của mình, không có quyền thêm mới.";
                return RedirectToAction(nameof(Index));
            }

            var lookup = await _taxCodeLookupService.LookupAsync(taxCode);
            if (!lookup.Success)
            {
                TempData["ErrorMessage"] = lookup.Message ?? "Không thể lấy thông tin từ mã số thuế này.";
                return RedirectToAction(nameof(Lookup), new { taxCode });
            }

            var account = await UpsertCompanyAndConnectionsAsync(new CreateTaxAccountViewModel
            {
                BusinessType = "DoanhNghiep",
                TaxCode = lookup.TaxCode!.Trim(),
                ShortName = lookup.ShortName?.Trim() ?? lookup.CompanyName?.Trim() ?? "Công ty mới",
                CompanyName = lookup.CompanyName?.Trim() ?? "Công ty mới",
                Address = lookup.Address?.Trim() ?? ""
            });

            TempData["SuccessMessage"] = $"Đã thêm và ghi nhớ thành công doanh nghiệp {account.CompanyName} (MST: {account.TaxCode})! Lần sau bạn chỉ cần chọn công ty trong danh sách.";

            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return Redirect(returnUrl);
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateFromTaxCode(string taxCode, string? returnUrl = null)
        {
            if (IsRestrictedAdmin())
            {
                TempData["ErrorMessage"] = "Tài khoản Admin không có quyền thêm hoặc cập nhật công ty khác.";
                return RedirectToAction(nameof(Index));
            }

            var lookup = await _taxCodeLookupService.LookupAsync(taxCode);
            if (!lookup.Success)
            {
                TempData["ErrorMessage"] = lookup.Message ?? "Không thể cào thông tin từ mã số thuế này.";
                return RedirectToAction(nameof(Lookup), new { taxCode });
            }

            var account = await UpsertCompanyAndConnectionsAsync(new CreateTaxAccountViewModel
            {
                BusinessType = "DoanhNghiep",
                TaxCode = lookup.TaxCode!.Trim(),
                ShortName = lookup.ShortName?.Trim() ?? lookup.CompanyName?.Trim() ?? "",
                CompanyName = lookup.CompanyName?.Trim() ?? "",
                Address = lookup.Address?.Trim() ?? ""
            });

            TempData["SuccessMessage"] = $"Đã cập nhật lại thông tin doanh nghiệp '{account.CompanyName}' (MST: {account.TaxCode}) thành công từ Cổng Thuế!";

            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return Redirect(returnUrl);
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> QuickCrawlAndSelect([FromBody] QuickCrawlRequest model)
        {
            if (IsRestrictedAdmin())
            {
                return Json(new { success = false, message = "Tài khoản Admin chỉ được xem công ty / hộ kinh doanh của mình, không có quyền thêm mới." });
            }

            if (string.IsNullOrWhiteSpace(model?.TaxCode))
            {
                return Json(new { success = false, message = "Vui lòng nhập mã số thuế cần cào thông tin." });
            }

            var lookup = await _taxCodeLookupService.LookupAsync(model.TaxCode);
            if (!lookup.Success)
            {
                return Json(new { success = false, message = lookup.Message ?? "Không thể cào thông tin từ mã số thuế này." });
            }

            var account = await UpsertCompanyAndConnectionsAsync(new CreateTaxAccountViewModel
            {
                BusinessType = "DoanhNghiep",
                TaxCode = lookup.TaxCode!.Trim(),
                ShortName = lookup.ShortName?.Trim() ?? lookup.CompanyName?.Trim() ?? "Doanh nghiệp mới",
                CompanyName = lookup.CompanyName?.Trim() ?? "Doanh nghiệp mới",
                Address = lookup.Address?.Trim() ?? ""
            });

            return Json(new
            {
                success = true,
                isNew = true,
                taxAccountId = account.Id,
                taxCode = account.TaxCode,
                companyName = account.CompanyName,
                address = account.Address,
                status = lookup.Status ?? "Đang hoạt động",
                message = $"Đã cào thông tin và ghi nhớ doanh nghiệp '{account.CompanyName}' (MST: {account.TaxCode}) thành công!"
            });
        }
    }
}
