using InvoiceManager.Services.Providers.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace InvoiceManager.Services.Providers
{
    public interface IInvoiceSourceProvider
    {
        string ProviderName { get; }
        string DisplayName { get; }

        Task<CaptchaChallenge?> GetCaptchaAsync(CancellationToken ct = default);
        Task<LoginResult> LoginAsync(ProviderCredentials cred, CancellationToken ct = default);
        IAsyncEnumerable<RemoteInvoice> FetchInvoicesAsync(FetchRequest req, string token, CancellationToken ct = default);
        Task<byte[]> DownloadXmlAsync(RemoteInvoice inv, string token, CancellationToken ct = default);
    }
}
