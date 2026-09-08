using InvoiceManager.Models.Entities;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace InvoiceManager.Services
{
    public interface ITaxAccountContext
    {
        Task<TaxAccount?> GetCurrentTaxAccountAsync();
        Task<int?> GetCurrentTaxAccountIdAsync();
        Task SetCurrentTaxAccountAsync(int taxAccountId);
        Task<List<TaxAccount>> GetAccessibleTaxAccountsAsync();
        Task<bool> HasAccessToTaxAccountAsync(int taxAccountId);
    }
}
