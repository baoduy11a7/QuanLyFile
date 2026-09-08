using System.Threading.Tasks;

namespace InvoiceManager.Services
{
    public interface IAuditLogService
    {
        Task LogActionAsync(string action, string target, string? details = null, int? taxAccountId = null);
    }
}
