using System;
using System.Collections.Generic;
using System.Linq;

namespace InvoiceManager.Services.Providers
{
    public interface IInvoiceSourceProviderFactory
    {
        IInvoiceSourceProvider GetProvider(string name);
        IEnumerable<IInvoiceSourceProvider> GetAllProviders();
    }

    public class InvoiceSourceProviderFactory : IInvoiceSourceProviderFactory
    {
        private readonly IEnumerable<IInvoiceSourceProvider> _providers;

        public InvoiceSourceProviderFactory(IEnumerable<IInvoiceSourceProvider> providers)
        {
            _providers = providers;
        }

        public IInvoiceSourceProvider GetProvider(string name)
        {
            var provider = _providers.FirstOrDefault(p => string.Equals(p.ProviderName, name, StringComparison.OrdinalIgnoreCase));
            if (provider == null)
            {
                throw new NotSupportedException($"Cổng hóa đơn '{name}' chưa được hỗ trợ hoặc chưa được cấu hình.");
            }
            return provider;
        }

        public IEnumerable<IInvoiceSourceProvider> GetAllProviders()
        {
            return _providers;
        }
    }
}
