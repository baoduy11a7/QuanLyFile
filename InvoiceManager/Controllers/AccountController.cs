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

            var model = new UserSettingsViewModel
            {
                User = user,
                Roles = roles,
                AccessibleTaxAccounts = accessibleTaxAccounts,
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
                foreach (var u in allUsers)
                {
                    var uRoles = await _userManager.GetRolesAsync(u);
                    model.AllUsers.Add(new UserListItemViewModel
                    {
                        Id = u.Id,
                        UserName = u.UserName ?? "",
                        Email = u.Email ?? "",
                        FullName = u.FullName,
                        PhoneNumber = u.PhoneNumber,
                        IsActive = u.IsActive,
                        Roles = uRoles,
                        CreatedAt = u.CreatedAt
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

                // Gán quyền truy cập cho tất cả công ty hiện tại
                var taxAccounts = await _taxAccountContext.GetAccessibleTaxAccountsAsync();
                foreach (var acc in taxAccounts)
                {
                    _db.UserTaxAccounts.Add(new UserTaxAccount
                    {
                        UserId = newUser.Id,
                        TaxAccountId = acc.Id,
                        AssignedAt = DateTime.Now
                    });
                }
                await _db.SaveChangesAsync();

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
    }
}
