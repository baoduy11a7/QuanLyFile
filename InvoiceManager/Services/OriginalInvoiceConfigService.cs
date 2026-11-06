using InvoiceManager.Data;
using InvoiceManager.Models.Entities;
using InvoiceManager.Models.ViewModels;
using InvoiceManager.Services.Providers;
using InvoiceManager.Services.Providers.Gdt;
using InvoiceManager.Services.Providers.Models;
using InvoiceManager.Services.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace InvoiceManager.Services
{
    public class OriginalInvoiceConfigService : IOriginalInvoiceConfigService
    {
        private readonly ApplicationDbContext _db;
        private readonly ICredentialProtector _protector;
        private readonly IInvoiceSourceProviderFactory _providerFactory;
        private readonly IAuditLogService _auditLog;
        private readonly ILogger<OriginalInvoiceConfigService> _logger;
        private readonly string _configDirectory;

        public OriginalInvoiceConfigService(
            ApplicationDbContext db,
            ICredentialProtector protector,
            IInvoiceSourceProviderFactory providerFactory,
            IAuditLogService auditLog,
            IConfiguration config,
            ILogger<OriginalInvoiceConfigService> logger)
        {
            _db = db;
            _protector = protector;
            _providerFactory = providerFactory;
            _auditLog = auditLog;
            _logger = logger;

            var baseDir = Directory.GetCurrentDirectory();
            _configDirectory = Path.Combine(baseDir, "App_Data", "Config");
            if (!Directory.Exists(_configDirectory))
            {
                Directory.CreateDirectory(_configDirectory);
            }
        }

        private string GetConfigFilePath(int taxAccountId)
        {
            return Path.Combine(_configDirectory, $"original_invoice_config_{taxAccountId}.json");
        }

        public async Task<OriginalInvoiceConfigModel> GetConfigAsync(int taxAccountId, CancellationToken ct = default)
        {
            var account = await _db.TaxAccounts.AsNoTracking().FirstOrDefaultAsync(t => t.Id == taxAccountId, ct);
            var filePath = GetConfigFilePath(taxAccountId);

            OriginalInvoiceConfigModel config;
            if (File.Exists(filePath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(filePath, ct);
                    config = JsonSerializer.Deserialize<OriginalInvoiceConfigModel>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                             ?? new OriginalInvoiceConfigModel();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Lỗi khi đọc cấu hình hóa đơn gốc từ file JSON cho TaxAccountId {Id}. Sử dụng mặc định.", taxAccountId);
                    config = new OriginalInvoiceConfigModel();
                }
            }
            else
            {
                config = new OriginalInvoiceConfigModel();
            }

            config.TaxAccountId = taxAccountId;
            config.CompanyName = account?.CompanyName ?? "Doanh nghiệp";

            if (string.IsNullOrWhiteSpace(config.BuyerTaxCode) && account != null)
            {
                config.BuyerTaxCode = account.TaxCode;
            }

            // Đồng bộ trạng thái kết nối & tài khoản từ RemoteConnections
            var conn = await _db.RemoteConnections
                .AsNoTracking()
                .Where(r => r.ProviderName == "Gdt" && (r.TaxAccountId == taxAccountId || (account != null && r.Username == account.TaxCode)))
                .OrderByDescending(r => r.TaxAccountId == taxAccountId)
                .ThenByDescending(r => !string.IsNullOrEmpty(r.EncryptedPassword))
                .FirstOrDefaultAsync(ct);

            if (conn != null)
            {
                if (string.IsNullOrWhiteSpace(config.Username))
                {
                    config.Username = conn.Username;
                }
                config.HasSavedPassword = !string.IsNullOrEmpty(conn.EncryptedPassword);
                config.LastConnectedAt = conn.LastSyncAt?.ToString("dd/MM/yyyy HH:mm");
                config.ConnectionStatus = (conn.LastSyncStatus == "Success") ? "Đã kết nối" : (conn.LastSyncStatus ?? (config.HasSavedPassword ? "Đã lưu tài khoản" : "Chưa kết nối"));
            }
            else
            {
                if (string.IsNullOrWhiteSpace(config.Username) && account != null)
                {
                    config.Username = account.TaxCode;
                }
                config.HasSavedPassword = false;
                config.ConnectionStatus = "Chưa kết nối";
            }

            config.Password = null; // Bảo mật, không trả lại mật khẩu plain text
            return config;
        }

        public async Task<bool> SaveConfigAsync(OriginalInvoiceConfigModel model, CancellationToken ct = default)
        {
            if (model == null || model.TaxAccountId <= 0) return false;

            var account = await _db.TaxAccounts.FirstOrDefaultAsync(t => t.Id == model.TaxAccountId, ct);
            if (account == null) return false;

            // 1. Cập nhật RemoteConnection
            var effectiveUser = !string.IsNullOrWhiteSpace(model.Username) ? model.Username.Trim() : account.TaxCode;
            var conn = await _db.RemoteConnections
                .FirstOrDefaultAsync(r => r.TaxAccountId == account.Id && r.ProviderName == "Gdt", ct);

            if (conn == null)
            {
                conn = new RemoteConnection
                {
                    TaxAccountId = account.Id,
                    ProviderName = "Gdt",
                    Username = effectiveUser,
                    RememberPassword = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                _db.RemoteConnections.Add(conn);
            }
            else
            {
                conn.Username = effectiveUser;
                conn.RememberPassword = true;
                conn.UpdatedAt = DateTime.UtcNow;
            }

            if (!string.IsNullOrWhiteSpace(model.Password))
            {
                conn.EncryptedPassword = _protector.Protect(model.Password.Trim());
            }

            await _db.SaveChangesAsync(ct);

            // 2. Lưu file cấu hình JSON
            var filePath = GetConfigFilePath(model.TaxAccountId);
            var safeModelToPersist = JsonSerializer.Deserialize<OriginalInvoiceConfigModel>(
                JsonSerializer.Serialize(model));
            if (safeModelToPersist != null)
            {
                safeModelToPersist.Password = null; // Không lưu plain password vào JSON
                var json = JsonSerializer.Serialize(safeModelToPersist, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(filePath, json, ct);
            }

            // 3. Ghi Audit Log nếu bật
            if (model.EnableAuditLog)
            {
                await _auditLog.LogActionAsync(
                    "Cấu hình thông tin lấy hóa đơn gốc",
                    $"MST: {account.TaxCode}",
                    $"Lưu toàn diện cấu hình lấy hóa đơn gốc (Thư mục: {model.StorageFolder}, Định dạng: XML={model.DownloadXml}, PDF={model.DownloadPdf})",
                    account.Id);
            }

            return true;
        }

        public async Task<OriginalInvoiceConfigModel> ResetToDefaultAsync(int taxAccountId, CancellationToken ct = default)
        {
            var filePath = GetConfigFilePath(taxAccountId);
            if (File.Exists(filePath))
            {
                try { File.Delete(filePath); } catch { }
            }

            var defaultConfig = await GetConfigAsync(taxAccountId, ct);
            defaultConfig.ApiUrl = "https://hoadondientu.gdt.gov.vn";
            defaultConfig.AuthMethod = "AutoOcrCaptcha";
            defaultConfig.DateRangeType = "CurrentMonth";
            defaultConfig.InvoiceStatus = "All";
            defaultConfig.InvoiceType = "Purchase";
            defaultConfig.OnlyUnDownloaded = true;
            defaultConfig.SkipExistingFiles = true;
            defaultConfig.AllowReDownload = true;
            defaultConfig.DownloadXml = true;
            defaultConfig.DownloadPdf = true;
            defaultConfig.DownloadBoth = true;
            defaultConfig.StorageFolder = "HoaDonGoc";
            defaultConfig.AutoOrganizeByMst = true;
            defaultConfig.AutoOrganizeByYearMonth = true;
            defaultConfig.AutoOrganizeBySellerMst = false;
            defaultConfig.FileNamePattern = "{KHHDon}_{SHDon}_{MSTBan}";
            defaultConfig.OverwriteExistingFiles = false;
            defaultConfig.AutoDownloadAfterSync = true;
            defaultConfig.AutoRetryFailed = true;
            defaultConfig.MaxRetryCount = 3;
            defaultConfig.RetryDelaySeconds = 3;
            defaultConfig.EnableAuditLog = true;

            await SaveConfigAsync(defaultConfig, ct);
            return defaultConfig;
        }

        public async Task<(bool Success, string Message, string? LastConnectedAt)> TestGovConnectionAsync(
            int taxAccountId,
            string? username,
            string? password,
            CancellationToken ct = default)
        {
            var account = await _db.TaxAccounts.AsNoTracking().FirstOrDefaultAsync(t => t.Id == taxAccountId, ct);
            if (account == null)
            {
                return (false, "Không tìm thấy thông tin doanh nghiệp trong hệ thống.", null);
            }

            var effectiveUser = !string.IsNullOrWhiteSpace(username) ? username.Trim() : account.TaxCode;
            var pwdToTest = password;

            if (string.IsNullOrWhiteSpace(pwdToTest))
            {
                var conn = await _db.RemoteConnections
                    .AsNoTracking()
                    .FirstOrDefaultAsync(r => r.TaxAccountId == account.Id && r.ProviderName == "Gdt", ct);
                if (conn != null && !string.IsNullOrEmpty(conn.EncryptedPassword))
                {
                    pwdToTest = _protector.Unprotect(conn.EncryptedPassword);
                }
            }

            if (string.IsNullOrWhiteSpace(pwdToTest))
            {
                return (false, "Vui lòng nhập mật khẩu Cổng Thuế để kiểm tra kết nối.", null);
            }

            try
            {
                var provider = _providerFactory.GetProvider("Gdt");
                if (provider == null)
                {
                    return (false, "Không tìm thấy bộ kết nối Cổng Tổng cục Thuế.", null);
                }

                var cred = new ProviderCredentials
                {
                    TaxCode = account.TaxCode,
                    Username = effectiveUser,
                    Password = pwdToTest
                };

                var loginRes = await provider.LoginAsync(cred, ct);
                if (loginRes.Success)
                {
                    var nowStr = DateTime.Now.ToString("dd/MM/yyyy HH:mm");
                    GdtPortalProvider.CacheToken(effectiveUser, loginRes.Token, account.Id);

                    // Cập nhật trạng thái thành công vào DB
                    var connDb = await _db.RemoteConnections
                        .FirstOrDefaultAsync(r => r.TaxAccountId == account.Id && r.ProviderName == "Gdt", ct);
                    if (connDb != null)
                    {
                        connDb.LastSyncAt = DateTime.Now;
                        connDb.LastSyncStatus = "Success";
                        connDb.LastSyncMessage = "Xác thực kết nối Cổng Thuế thành công";
                        await _db.SaveChangesAsync(ct);
                    }

                    return (true, "Kết nối Cổng Tổng cục Thuế THÀNH CÔNG! Đã xác thực phiên làm việc hợp lệ.", nowStr);
                }
                else
                {
                    return (false, loginRes.ErrorMessage ?? "Đăng nhập Cổng Thuế không thành công. Vui lòng kiểm tra lại MST hoặc mật khẩu.", null);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lỗi khi kiểm tra kết nối Cổng Thuế cho TaxAccountId {Id}", taxAccountId);
                return (false, $"Lỗi kết nối Cổng Thuế: {ex.Message}", null);
            }
        }

        public string BuildResolvedStoragePath(
            OriginalInvoiceConfigModel config,
            string companyTaxCode,
            DateTime issueDate,
            string sellerTaxCode,
            string invoiceSymbol,
            string invoiceNumber,
            string extension)
        {
            var baseDir = Directory.GetCurrentDirectory();
            var folderName = !string.IsNullOrWhiteSpace(config.StorageFolder) ? config.StorageFolder.Trim() : "HoaDonGoc";

            var pathParts = new System.Collections.Generic.List<string> { baseDir, folderName };

            if (config.AutoOrganizeByMst && !string.IsNullOrWhiteSpace(companyTaxCode))
            {
                pathParts.Add(companyTaxCode.Trim());
            }

            if (config.AutoOrganizeByYearMonth)
            {
                pathParts.Add(issueDate.Year.ToString());
                pathParts.Add(issueDate.Month.ToString("D2"));
            }

            if (config.AutoOrganizeBySellerMst && !string.IsNullOrWhiteSpace(sellerTaxCode))
            {
                pathParts.Add(sellerTaxCode.Trim());
            }

            var folderPath = Path.Combine(pathParts.ToArray());
            if (!Directory.Exists(folderPath))
            {
                Directory.CreateDirectory(folderPath);
            }

            // Tên file theo cấu hình
            var pattern = !string.IsNullOrWhiteSpace(config.FileNamePattern)
                ? config.FileNamePattern
                : "{KHHDon}_{SHDon}_{MSTBan}";

            var cleanSymbol = (invoiceSymbol ?? "").Replace("/", "_").Replace("\\", "_").Trim();
            var cleanNumber = (invoiceNumber ?? "").PadLeft(7, '0').Trim();
            var cleanSeller = (sellerTaxCode ?? "").Trim();

            var fileName = pattern
                .Replace("{KHHDon}", cleanSymbol)
                .Replace("{SHDon}", cleanNumber)
                .Replace("{MSTBan}", cleanSeller)
                .Replace("{SoHoaDon}", cleanNumber)
                .Replace("{KyHieu}", cleanSymbol);

            var cleanExt = extension.StartsWith(".") ? extension : $".{extension}";
            return Path.Combine(folderPath, $"{fileName}{cleanExt}");
        }
    }
}

