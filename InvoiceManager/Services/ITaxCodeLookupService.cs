using InvoiceManager.Models.ViewModels;
using System.Threading;
using System.Threading.Tasks;

namespace InvoiceManager.Services
{
    public interface ITaxCodeLookupService
    {
        Task<TaxCodeLookupResult> LookupAsync(string taxCode, CancellationToken cancellationToken = default);
    }
}
