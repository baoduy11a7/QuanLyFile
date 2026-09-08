using InvoiceManager.Data;
using InvoiceManager.Models.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Security.Claims;
using System.Threading.Tasks;

namespace InvoiceManager.Services
{
    public class AuditLogService : IAuditLogService
    {
        private readonly ApplicationDbContext _db;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ILogger<AuditLogService> _logger;

        public AuditLogService(
            ApplicationDbContext db,
            IHttpContextAccessor httpContextAccessor,
            ILogger<AuditLogService> logger)
        {
            _db = db;
            _httpContextAccessor = httpContextAccessor;
            _logger = logger;
        }

        public async Task LogActionAsync(string action, string target, string? details = null, int? taxAccountId = null)
        {
            var httpContext = _httpContextAccessor.HttpContext;
            string? userId = null;
            string? userName = "Anonymous";
            string? ipAddress = "127.0.0.1";

            if (httpContext != null)
            {
                userId = httpContext.User?.FindFirstValue(ClaimTypes.NameIdentifier);
                userName = httpContext.User?.Identity?.Name ?? (string.IsNullOrEmpty(userId) ? "Anonymous" : "User-" + userId);
                ipAddress = httpContext.Connection?.RemoteIpAddress?.ToString();
            }

            var audit = new AuditLog
            {
                UserId = userId,
                UserName = userName,
                Action = action,
                Target = target,
                Details = details,
                IpAddress = ipAddress,
                TaxAccountId = taxAccountId,
                Timestamp = DateTime.Now
            };

            try
            {
                _db.AuditLogs.Add(audit);
                await _db.SaveChangesAsync();

                _logger.LogInformation("[AUDIT] User: {User} | Action: {Action} | Target: {Target} | IP: {IP} | Details: {Details}",
                    userName, action, target, ipAddress, details);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi ghi AuditLog: {Action} trên {Target}", action, target);
            }
        }
    }
}
