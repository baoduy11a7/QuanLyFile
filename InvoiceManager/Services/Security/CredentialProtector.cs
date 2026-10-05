using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using System;

namespace InvoiceManager.Services.Security
{
    public interface ICredentialProtector
    {
        string? Protect(string? clearText);
        string? Unprotect(string? cipherText);
    }

    public class CredentialProtector : ICredentialProtector
    {
        private readonly IDataProtector _protector;
        private readonly ILogger<CredentialProtector> _logger;

        public CredentialProtector(IDataProtectionProvider provider, ILogger<CredentialProtector> logger)
        {
            _protector = provider.CreateProtector("InvoiceManager.RemoteConnections.Credentials.v1");
            _logger = logger;
        }

        public string? Protect(string? clearText)
        {
            if (string.IsNullOrEmpty(clearText)) return null;
            try
            {
                return _protector.Protect(clearText);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi mã hóa thông tin mật khẩu kết nối.");
                return null;
            }
        }

        public string? Unprotect(string? cipherText)
        {
            if (string.IsNullOrEmpty(cipherText)) return null;
            try
            {
                return _protector.Unprotect(cipherText);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Không thể giải mã mật khẩu đã lưu (có thể khóa mã hóa đã thay đổi).");
                return null;
            }
        }
    }
}
