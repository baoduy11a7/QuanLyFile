using InvoiceManager.Data;
using InvoiceManager.Models.Entities;
using InvoiceManager.Models.ViewModels;
using InvoiceManager.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace InvoiceManager.Controllers
{
    public class AccountController : Controller
    {
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IAuditLogService _auditLog;
        private readonly ITaxAccountContext _taxAccountContext;
        private readonly ApplicationDbContext _db;

        public AccountController(
            SignInManager<ApplicationUser> signInManager,
            UserManager<ApplicationUser> userManager,
            IAuditLogService auditLog,
            ITaxAccountContext taxAccountContext,
            ApplicationDbContext db)
        {
            _signInManager = signInManager;
            _userManager = userManager;
            _auditLog = auditLog;
            _taxAccountContext = taxAccountContext;
            _db = db;
        }

        [HttpGet]
        public IActionResult Login(string? returnUrl = null)
        {
            if (User.Identity?.IsAuthenticated == true)
            {
                return RedirectToAction("Index", "Invoice");
            }

            return View(new LoginViewModel { ReturnUrl = returnUrl });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(LoginViewModel model)
        {
            if (!ModelState.IsValid)
            {
                return View(model);
            }

            var user = await _userManager.FindByEmailAsync(model.UserNameOrEmail) 
                    ?? await _userManager.FindByNameAsync(model.UserNameOrEmail);

            if (user == null || !user.IsActive)
            {
                ModelState.AddModelError(string.Empty, "Tài khoản không tồn tại hoặc đã bị khóa.");
                return View(model);
            }

            var result = await _signInManager.PasswordSignInAsync(user.UserName!, model.Password, model.RememberMe, lockoutOnFailure: false);
            if (result.Succeeded)
            {
                await _auditLog.LogActionAsync("Đăng nhập hệ thống", user.UserName!);

                // Đặt tài khoản thuế mặc định cho user
                var accounts = await _taxAccountContext.GetAccessibleTaxAccountsAsync();
                if (accounts.Any())
                {
                    await _taxAccountContext.SetCurrentTaxAccountAsync(accounts.First().Id);
                }

                if (!string.IsNullOrEmpty(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl))
                {
                    return Redirect(model.ReturnUrl);
                }

                return RedirectToAction("Index", "Invoice");
            }

            ModelState.AddModelError(string.Empty, "Mật khẩu không chính xác. Vui lòng kiểm tra lại.");
            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize]
        public async Task<IActionResult> Logout()
        {
            await _auditLog.LogActionAsync("Đăng xuất hệ thống", User.Identity?.Name ?? "User");
            await _signInManager.SignOutAsync();
            return RedirectToAction(nameof(Login));
        }

        [HttpGet]
        public IActionResult AccessDenied()
        {
            return View();
        }

        [HttpGet]
        [Authorize]
        public async Task<IActionResult> Settings()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return RedirectToAction(nameof(Login));

            var roles = await _userManager.GetRolesAsync(user);
            var accessibleTaxAccounts = await _taxAccountContext.GetAccessibleTaxAccountsAsync();
            var allSystemTaxAccounts = await _db.TaxAccounts.Where(t => t.IsActive).OrderBy(t => t.TaxCode).ToListAsync();

            var model = new UserSettingsViewModel
            {
                User = user,
                Roles = roles,
                AccessibleTaxAccounts = accessibleTaxAccounts,
                AllSystemTaxAccounts = allSystemTaxAccounts,
                Profile = new UpdateProfileViewModel
                {
                    FullName = user.FullName,
                    PhoneNumber = user.PhoneNumber
                },
                IsAdmin = roles.Contains("Admin")
            };

            if (model.IsAdmin)
            {
                var allUsers = await _userManager.Users.ToListAsync();
                var userTaxMappings = await _db.UserTaxAccounts
                    .Include(u => u.TaxAccount)
                    .Where(u => u.TaxAccount != null && u.TaxAccount.IsActive)
                    .ToListAsync();

                foreach (var u in allUsers)
                {
                    var uRoles = await _userManager.GetRolesAsync(u);
                    var userComps = userTaxMappings.Where(m => m.UserId == u.Id).ToList();
                    model.AllUsers.Add(new UserListItemViewModel
                    {
                        Id = u.Id,
                        UserName = u.UserName ?? "",
                        Email = u.Email ?? "",
                        FullName = u.FullName,
                        PhoneNumber = u.PhoneNumber,
                        IsActive = u.IsActive,
                        Roles = uRoles,
                        CreatedAt = u.CreatedAt,
                        AssignedTaxAccountIds = userComps.Select(c => c.TaxAccountId).ToList(),
                        AssignedTaxAccountNames = userComps.Select(c => $"{c.TaxAccount!.TaxCode} - {c.TaxAccount.CompanyName}").ToList()
                    });
                }
            }

            return View(model);
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateProfile(UpdateProfileViewModel model)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return RedirectToAction(nameof(Login));

            if (!ModelState.IsValid)
            {
                TempData["ErrorMessage"] = "Thông tin không hợp lệ. Vui lòng kiểm tra lại.";
                return RedirectToAction(nameof(Settings));
            }

            user.FullName = model.FullName.Trim();
            user.PhoneNumber = model.PhoneNumber?.Trim();

            var result = await _userManager.UpdateAsync(user);
            if (result.Succeeded)
            {
                await _auditLog.LogActionAsync("Cập nhật thông tin tài khoản", user.UserName!);
                TempData["SuccessMessage"] = "Cập nhật thông tin cá nhân thành công!";
            }
            else
            {
                TempData["ErrorMessage"] = "Lỗi: " + string.Join(", ", result.Errors.Select(e => e.Description));
            }

            return RedirectToAction(nameof(Settings));
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return RedirectToAction(nameof(Login));

            if (!ModelState.IsValid)
            {
                TempData["ErrorMessage"] = "Dữ liệu mật khẩu không hợp lệ.";
                return RedirectToAction(nameof(Settings));
            }

            var result = await _userManager.ChangePasswordAsync(user, model.CurrentPassword, model.NewPassword);
            if (result.Succeeded)
            {
                await _signInManager.RefreshSignInAsync(user);
                await _auditLog.LogActionAsync("Đổi mật khẩu tài khoản", user.UserName!);
                TempData["SuccessMessage"] = "Đổi mật khẩu thành công!";
            }
            else
            {
                TempData["ErrorMessage"] = "Lỗi đổi mật khẩu: " + string.Join(", ", result.Errors.Select(e => e.Description));
            }

            return RedirectToAction(nameof(Settings));
        }

        [HttpPost]
        [Authorize(Roles = "Admin")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateUser(CreateUserViewModel model)
        {
            if (!ModelState.IsValid)
            {
                TempData["ErrorMessage"] = "Dữ liệu tạo tài khoản không hợp lệ.";
                return RedirectToAction(nameof(Settings));
            }

            var existing = await _userManager.FindByEmailAsync(model.Email);
            if (existing != null)
            {
                TempData["ErrorMessage"] = "Email này đã được sử dụng trong hệ thống.";
                return RedirectToAction(nameof(Settings));
            }

            var newUser = new ApplicationUser
            {
                UserName = model.Email.Trim(),
                Email = model.Email.Trim(),
                FullName = model.FullName.Trim(),
                PhoneNumber = model.PhoneNumber?.Trim(),
                IsActive = true,
                CreatedAt = DateTime.Now
            };

            var result = await _userManager.CreateAsync(newUser, model.Password);
            if (result.Succeeded)
            {
                string targetRole = model.Role switch
                {
                    "Admin" => "Admin",
                    "Viewer" => "Viewer",
                    _ => "Accountant"
                };

                await _userManager.AddToRoleAsync(newUser, targetRole);

                // Chỉ gán các công ty được Admin tích chọn cụ thể (không auto-gán tất cả công ty)
                if (model.SelectedTaxAccountIds != null && model.SelectedTaxAccountIds.Any())
                {
                    foreach (var taxId in model.SelectedTaxAccountIds)
                    {
                        _db.UserTaxAccounts.Add(new UserTaxAccount
                        {
                            UserId = newUser.Id,
                            TaxAccountId = taxId,
                            AssignedAt = DateTime.Now
                        });
                    }
                    await _db.SaveChangesAsync();
                }

                await _auditLog.LogActionAsync("Tạo người dùng mới", $"{newUser.UserName} ({targetRole})");
                TempData["SuccessMessage"] = $"Tạo tài khoản {newUser.UserName} thành công!";
            }
            else
            {
                TempData["ErrorMessage"] = "Lỗi tạo tài khoản: " + string.Join(", ", result.Errors.Select(e => e.Description));
            }

            return RedirectToAction(nameof(Settings));
        }

        [HttpPost]
        [Authorize(Roles = "Admin")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateUserCompanies(string userId, List<int>? selectedTaxAccountIds)
        {
            var targetUser = await _userManager.FindByIdAsync(userId);
            if (targetUser == null)
            {
                TempData["ErrorMessage"] = "Không tìm thấy người dùng.";
                return RedirectToAction(nameof(Settings));
            }

            // Xóa các phân quyền hiện tại của user này
            var currentAssignments = await _db.UserTaxAccounts.Where(u => u.UserId == userId).ToListAsync();
            _db.UserTaxAccounts.RemoveRange(currentAssignments);

            // Thêm các công ty mới được chọn
            if (selectedTaxAccountIds != null && selectedTaxAccountIds.Any())
            {
                foreach (var taxId in selectedTaxAccountIds)
                {
                    _db.UserTaxAccounts.Add(new UserTaxAccount
                    {
                        UserId = userId,
                        TaxAccountId = taxId,
                        AssignedAt = DateTime.Now
                    });
                }
            }

            await _db.SaveChangesAsync();

            await _auditLog.LogActionAsync(
                "Phân quyền công ty cho người dùng",
                $"{targetUser.UserName} ({selectedTaxAccountIds?.Count ?? 0} công ty)");

            TempData["SuccessMessage"] = $"Cập nhật phân quyền công ty cho {targetUser.UserName} thành công!";
            return RedirectToAction(nameof(Settings));
        }

        [HttpPost]
        [Authorize(Roles = "Admin")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleUserStatus(string userId)
        {
            var currentUser = await _userManager.GetUserAsync(User);
            if (currentUser != null && currentUser.Id == userId)
            {
                TempData["ErrorMessage"] = "Bạn không thể tự khóa tài khoản của chính mình.";
                return RedirectToAction(nameof(Settings));
            }

            var targetUser = await _userManager.FindByIdAsync(userId);
            if (targetUser == null)
            {
                TempData["ErrorMessage"] = "Không tìm thấy người dùng.";
                return RedirectToAction(nameof(Settings));
            }

            targetUser.IsActive = !targetUser.IsActive;
            await _userManager.UpdateAsync(targetUser);

            await _auditLog.LogActionAsync(
                targetUser.IsActive ? "Mở khóa tài khoản" : "Khóa tài khoản",
                targetUser.UserName!);

            TempData["SuccessMessage"] = $"Đã {(targetUser.IsActive ? "mở khóa" : "khóa")} tài khoản {targetUser.UserName}!";
            return RedirectToAction(nameof(Settings));
        }

        [HttpPost]
        [Authorize(Roles = "Admin")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetUserPassword(string userId, string newPassword)
        {
            if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 6)
            {
                TempData["ErrorMessage"] = "Mật khẩu mới phải có ít nhất 6 ký tự.";
                return RedirectToAction(nameof(Settings));
            }

            var targetUser = await _userManager.FindByIdAsync(userId);
            if (targetUser == null)
            {
                TempData["ErrorMessage"] = "Không tìm thấy người dùng.";
                return RedirectToAction(nameof(Settings));
            }

            var token = await _userManager.GeneratePasswordResetTokenAsync(targetUser);
            var result = await _userManager.ResetPasswordAsync(targetUser, token, newPassword);

            if (result.Succeeded)
            {
                await _auditLog.LogActionAsync("Reset mật khẩu người dùng", targetUser.UserName!);
                TempData["SuccessMessage"] = $"Đã đặt lại mật khẩu thành công cho tài khoản {targetUser.UserName}!";
            }
            else
            {
                TempData["ErrorMessage"] = "Lỗi đặt lại mật khẩu: " + string.Join(", ", result.Errors.Select(e => e.Description));
            }

            return RedirectToAction(nameof(Settings));
        }

        [HttpPost]
        [Authorize(Roles = "Admin")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteUser(string userId)
        {
            var currentUser = await _userManager.GetUserAsync(User);
            if (currentUser != null && currentUser.Id == userId)
            {
                TempData["ErrorMessage"] = "Bạn không thể tự xóa tài khoản của chính mình.";
                return RedirectToAction(nameof(Settings));
            }

            var targetUser = await _userManager.FindByIdAsync(userId);
            if (targetUser == null)
            {
                TempData["ErrorMessage"] = "Không tìm thấy người dùng cần xóa.";
                return RedirectToAction(nameof(Settings));
            }

            // Bảo vệ tài khoản Admin cuối cùng
            var isTargetAdmin = await _userManager.IsInRoleAsync(targetUser, "Admin");
            if (isTargetAdmin)
            {
                var adminUsers = await _userManager.GetUsersInRoleAsync("Admin");
                if (adminUsers.Count <= 1)
                {
                    TempData["ErrorMessage"] = "Không thể xóa tài khoản Quản trị viên (Admin) duy nhất của hệ thống.";
                    return RedirectToAction(nameof(Settings));
                }
            }

            try
            {
                // 1. Tìm các công ty mà user này đang quản lý
                var userTaxMappings = await _db.UserTaxAccounts
                    .Where(u => u.UserId == userId)
                    .ToListAsync();

                // 2. Dọn dẹp các công ty riêng chỉ thuộc về user này
                foreach (var mapping in userTaxMappings)
                {
                    bool hasOtherUsers = await _db.UserTaxAccounts
                        .AnyAsync(u => u.TaxAccountId == mapping.TaxAccountId && u.UserId != userId);

                    if (!hasOtherUsers)
                    {
                        var taxAcc = await _db.TaxAccounts.FindAsync(mapping.TaxAccountId);
                        if (taxAcc != null)
                        {
                            var invoices = await _db.Invoices
                                .Include(i => i.Details)
                                .Where(i => i.TaxAccountId == taxAcc.Id)
                                .ToListAsync();
                            _db.Invoices.RemoveRange(invoices);

                            var remoteConns = await _db.RemoteConnections
                                .Where(r => r.TaxAccountId == taxAcc.Id)
                                .ToListAsync();
                            _db.RemoteConnections.RemoveRange(remoteConns);

                            var syncLogs = await _db.SyncLogs
                                .Where(s => s.TaxAccountId == taxAcc.Id)
                                .ToListAsync();
                            _db.SyncLogs.RemoveRange(syncLogs);

                            var featureRequests = await _db.FeatureRequests
                                .Where(f => f.TaxAccountId == taxAcc.Id)
                                .ToListAsync();
                            _db.FeatureRequests.RemoveRange(featureRequests);

                            var taxAudits = await _db.AuditLogs
                                .Where(a => a.TaxAccountId == taxAcc.Id)
                                .ToListAsync();
                            foreach (var ta in taxAudits)
                            {
                                ta.TaxAccountId = null;
                            }

                            _db.TaxAccounts.Remove(taxAcc);
                        }
                    }
                }

                // 3. Xóa các mapping UserTaxAccounts
                _db.UserTaxAccounts.RemoveRange(userTaxMappings);

                // 4. Gỡ liên kết UserId trong AuditLog
                var userAuditLogs = await _db.AuditLogs
                    .Where(a => a.UserId == userId)
                    .ToListAsync();
                foreach (var al in userAuditLogs)
                {
                    al.UserId = null;
                }

                await _db.SaveChangesAsync();

                // 5. Xóa user qua UserManager
                var result = await _userManager.DeleteAsync(targetUser);
                if (result.Succeeded)
                {
                    await _auditLog.LogActionAsync("Xóa người dùng hệ thống", targetUser.UserName!);
                    TempData["SuccessMessage"] = $"Đã xóa thành công tài khoản: {targetUser.FullName} ({targetUser.Email})!";
                }
                else
                {
                    TempData["ErrorMessage"] = "Lỗi khi xóa tài khoản: " + string.Join(", ", result.Errors.Select(e => e.Description));
                }
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "Lỗi hệ thống khi xóa tài khoản: " + ex.Message;
            }

            return RedirectToAction(nameof(Settings));
        }
    }
}
