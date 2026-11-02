using InvoiceManager.Models.Entities;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace InvoiceManager.Models.ViewModels
{
    public class LoginViewModel
    {
        [Required(ErrorMessage = "Vui lòng nhập Email hoặc Tên đăng nhập")]
        [Display(Name = "Tài khoản")]
        public string UserNameOrEmail { get; set; } = string.Empty;

        [Required(ErrorMessage = "Vui lòng nhập mật khẩu")]
        [DataType(DataType.Password)]
        [Display(Name = "Mật khẩu")]
        public string Password { get; set; } = string.Empty;

        [Display(Name = "Ghi nhớ đăng nhập")]
        public bool RememberMe { get; set; } = true;

        public string? ReturnUrl { get; set; }

        [Display(Name = "Công ty / Hộ kinh doanh làm việc")]
        public int? SelectedTaxAccountId { get; set; }
    }

    public class CreateTaxAccountViewModel
    {
        public string BusinessType { get; set; } = "DoanhNghiep"; // "DoanhNghiep" hoặc "HoKinhDoanh"

        [Required(ErrorMessage = "Mã số thuế là bắt buộc")]
        [StringLength(20, MinimumLength = 10, ErrorMessage = "Mã số thuế phải có từ 10 đến 14 ký tự")]
        [Display(Name = "Mã số thuế (*)")]
        public string TaxCode { get; set; } = string.Empty;

        [Required(ErrorMessage = "Tên gợi nhớ là bắt buộc")]
        [StringLength(150)]
        [Display(Name = "Tên gợi nhớ (*)")]
        public string ShortName { get; set; } = string.Empty;

        [StringLength(100)]
        [Display(Name = "Tên kế toán")]
        public string? AccountantName { get; set; }

        [Required(ErrorMessage = "Tên công ty / hộ kinh doanh là bắt buộc")]
        [StringLength(255)]
        [Display(Name = "Tên công ty")]
        public string CompanyName { get; set; } = string.Empty;

        [StringLength(100)]
        [Display(Name = "Tên giám đốc")]
        public string? Representative { get; set; }

        [StringLength(500)]
        [Display(Name = "Địa chỉ công ty / hộ kinh doanh")]
        public string? Address { get; set; }

        [EmailAddress(ErrorMessage = "Email không hợp lệ")]
        [Display(Name = "Email nhận thông báo hóa đơn")]
        public string? Email { get; set; }

        [Phone(ErrorMessage = "Số điện thoại không hợp lệ")]
        [Display(Name = "Số điện thoại")]
        public string? PhoneNumber { get; set; }

        // Thông tin kết nối Tài khoản Hóa đơn điện tử (hoadondientu.gdt.gov.vn)
        public string? EInvoiceUsername { get; set; }
        public string? EInvoicePassword { get; set; }

        // Thông tin kết nối Tài khoản Thuế điện tử (thuedientu.gdt.gov.vn)
        public string? ETaxUsername { get; set; }
        public string? ETaxPassword { get; set; }

        public string? ReturnUrl { get; set; }
    }

    public class UpdateProfileViewModel
    {
        [Required(ErrorMessage = "Họ và tên là bắt buộc")]
        [StringLength(100, ErrorMessage = "Họ và tên tối đa 100 ký tự")]
        [Display(Name = "Họ và tên")]
        public string FullName { get; set; } = string.Empty;

        [Phone(ErrorMessage = "Số điện thoại không hợp lệ")]
        [Display(Name = "Số điện thoại")]
        public string? PhoneNumber { get; set; }
    }

    public class ChangePasswordViewModel
    {
        [Required(ErrorMessage = "Vui lòng nhập mật khẩu hiện tại")]
        [DataType(DataType.Password)]
        [Display(Name = "Mật khẩu hiện tại")]
        public string CurrentPassword { get; set; } = string.Empty;

        [Required(ErrorMessage = "Vui lòng nhập mật khẩu mới")]
        [StringLength(100, MinimumLength = 6, ErrorMessage = "Mật khẩu mới phải có ít nhất 6 ký tự")]
        [DataType(DataType.Password)]
        [Display(Name = "Mật khẩu mới")]
        public string NewPassword { get; set; } = string.Empty;

        [DataType(DataType.Password)]
        [Display(Name = "Xác nhận mật khẩu mới")]
        [Compare("NewPassword", ErrorMessage = "Xác nhận mật khẩu mới không khớp")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }

    public class CreateUserViewModel
    {
        [Required(ErrorMessage = "Email là bắt buộc")]
        [EmailAddress(ErrorMessage = "Email không hợp lệ")]
        [Display(Name = "Email / Tên đăng nhập")]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "Họ và tên là bắt buộc")]
        [StringLength(100)]
        [Display(Name = "Họ và tên")]
        public string FullName { get; set; } = string.Empty;

        [Phone(ErrorMessage = "Số điện thoại không hợp lệ")]
        [Display(Name = "Số điện thoại")]
        public string? PhoneNumber { get; set; }

        [Required(ErrorMessage = "Mật khẩu khởi tạo là bắt buộc")]
        [StringLength(100, MinimumLength = 6, ErrorMessage = "Mật khẩu phải từ 6 ký tự")]
        [DataType(DataType.Password)]
        [Display(Name = "Mật khẩu")]
        public string Password { get; set; } = string.Empty;

        [Required(ErrorMessage = "Vui lòng chọn vai trò")]
        [Display(Name = "Vai trò người dùng")]
        public string Role { get; set; } = "Accountant"; // Admin, Accountant, Viewer

        [Display(Name = "Công ty được phân quyền")]
        public List<int> SelectedTaxAccountIds { get; set; } = new();
    }

    public class UserListItemViewModel
    {
        public string Id { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string? PhoneNumber { get; set; }
        public bool IsActive { get; set; }
        public IList<string> Roles { get; set; } = new List<string>();
        public DateTime CreatedAt { get; set; }
        public List<int> AssignedTaxAccountIds { get; set; } = new();
        public List<string> AssignedTaxAccountNames { get; set; } = new();
    }

    public class UserSettingsViewModel
    {
        public ApplicationUser User { get; set; } = null!;
        public IList<string> Roles { get; set; } = new List<string>();
        public List<TaxAccount> AccessibleTaxAccounts { get; set; } = new();
        public List<TaxAccount> AllSystemTaxAccounts { get; set; } = new();
        public UpdateProfileViewModel Profile { get; set; } = new();
        public ChangePasswordViewModel ChangePassword { get; set; } = new();
        public List<UserListItemViewModel> AllUsers { get; set; } = new();
        public bool IsAdmin { get; set; }
    }
}
