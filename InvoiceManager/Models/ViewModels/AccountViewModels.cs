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
    }

    public class CreateTaxAccountViewModel
    {
        [Required(ErrorMessage = "Mã số thuế là bắt buộc")]
        [StringLength(20, MinimumLength = 10, ErrorMessage = "Mã số thuế phải có từ 10 đến 14 ký tự")]
        [Display(Name = "Mã số thuế")]
        public string TaxCode { get; set; } = string.Empty;

        [Required(ErrorMessage = "Tên công ty là bắt buộc")]
        [StringLength(255)]
        [Display(Name = "Tên công ty / Đơn vị")]
        public string CompanyName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Địa chỉ trụ sở là bắt buộc")]
        [StringLength(500)]
        [Display(Name = "Địa chỉ đăng ký")]
        public string Address { get; set; } = string.Empty;

        [EmailAddress(ErrorMessage = "Email không hợp lệ")]
        [Display(Name = "Email nhận thông báo hóa đơn")]
        public string? Email { get; set; }

        [Phone(ErrorMessage = "Số điện thoại không hợp lệ")]
        [Display(Name = "Số điện thoại")]
        public string? PhoneNumber { get; set; }

        [Display(Name = "Người đại diện")]
        public string? Representative { get; set; }
    }
}
