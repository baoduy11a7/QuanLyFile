using InvoiceManager.Services.Providers.Mock;
using InvoiceManager.Services.Providers.Models;
using InvoiceManager.Services.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System;
using System.Threading.Tasks;
using Xunit;

namespace InvoiceManager.Tests
{
    public class TokenRefreshAndRetryTests
    {
        [Fact]
        public async Task MockProvider_Login_FailsWhenCredentialsIncomplete()
        {
            var provider = new MockInvoiceSourceProvider();

            // Thiếu password
            var result1 = await provider.LoginAsync(new ProviderCredentials
            {
                TaxCode = "0101234567",
                Username = "admin",
                Password = ""
            });

            Assert.False(result1.Success);
            Assert.Contains("đầy đủ tên đăng nhập và mật khẩu", result1.ErrorMessage);

            // Thiếu captcha
            var result2 = await provider.LoginAsync(new ProviderCredentials
            {
                TaxCode = "0101234567",
                Username = "admin",
                Password = "SecretPassword123",
                CaptchaCode = ""
            });

            Assert.False(result2.Success);
            Assert.Contains("Captcha", result2.ErrorMessage);
        }

        [Fact]
        public async Task MockProvider_Login_SucceedsWithValidCredentials()
        {
            var provider = new MockInvoiceSourceProvider();

            var result = await provider.LoginAsync(new ProviderCredentials
            {
                TaxCode = "0101234567",
                Username = "admin",
                Password = "SecretPassword123",
                CaptchaCode = "ABCD"
            });

            Assert.True(result.Success);
            Assert.NotNull(result.Token);
            Assert.StartsWith("mock-token-", result.Token);
        }

        [Fact]
        public void CredentialProtector_EncryptAndDecrypt_PreservesOriginalPassword()
        {
            // Sử dụng EphemeralDataProtectionProvider cho unit test
            var dpProvider = new EphemeralDataProtectionProvider();
            var protector = new CredentialProtector(dpProvider, NullLogger<CredentialProtector>.Instance);

            var originalPass = "P@ssw0rdCổngThuế2026!";
            var cipher = protector.Protect(originalPass);

            Assert.NotNull(cipher);
            Assert.NotEqual(originalPass, cipher);

            var decrypted = protector.Unprotect(cipher);
            Assert.Equal(originalPass, decrypted);
        }

        [Fact]
        public async Task GdtPortalProvider_GetCaptcha_ReturnsValidCaptchaChallenge()
        {
            var mockFactory = new Mock<System.Net.Http.IHttpClientFactory>();
            var httpClient = new System.Net.Http.HttpClient();
            mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

            var options = Microsoft.Extensions.Options.Options.Create(new InvoiceManager.Services.Providers.Gdt.GdtPortalOptions
            {
                BaseUrl = "https://hoadondientu.gdt.gov.vn",
                CaptchaEndpoint = "/api/captcha"
            });

            var provider = new InvoiceManager.Services.Providers.Gdt.GdtPortalProvider(
                mockFactory.Object, options, NullLogger<InvoiceManager.Services.Providers.Gdt.GdtPortalProvider>.Instance);

            var captcha = await provider.GetCaptchaAsync();

            Assert.NotNull(captcha);
            Assert.False(string.IsNullOrWhiteSpace(captcha.Key));
            Assert.True(captcha.IsSvg);
            Assert.False(string.IsNullOrWhiteSpace(captcha.SvgContent));
        }
    }
}
