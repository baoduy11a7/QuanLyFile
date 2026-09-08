using InvoiceManager.Models.Entities;
using System.Threading.Tasks;

namespace InvoiceManager.Services
{
    public interface ISyncService
    {
        Task<SyncLog> SyncInvoicesForTaxAccountAsync(int taxAccountId, string syncType = "Thủ công");
        Task SyncAllActiveTaxAccountsAsync();
    }
}
