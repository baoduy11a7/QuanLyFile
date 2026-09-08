using System.IO;
using System.Threading.Tasks;

namespace InvoiceManager.Services.Parsers
{
    public interface IInvoiceParser
    {
        string ProviderName { get; }
        bool CanParse(string xmlContent);
        Task<ParsedInvoiceResult> ParseAsync(Stream xmlStream, string fileName);
    }
}
