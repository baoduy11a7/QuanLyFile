using InvoiceManager.Models.Entities;
using InvoiceManager.Models.ViewModels;
using InvoiceManager.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;

namespace InvoiceManager.Controllers
{
    public class AccountController : Controller
    {
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IAuditLogService _auditLog;
        private readonly ITaxAccountContext _taxAccountContext;

        public AccountController(
            SignInManager<ApplicationUser> signInManager,
            UserManager<ApplicationUser> userManager,
            IAuditLogService auditLog,
            ITaxAccountContext taxAccountContext)
        {
            _signInManager = signInManager;
            _userManager = userManager;
            _auditLog = auditLog;
            _taxAccountContext = taxAccountContext;
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
    }
}
