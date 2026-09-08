using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InvoiceManager.Services.Parsers
{
    public interface IInvoiceParserFactory
    {
        IInvoiceParser GetParser(string xmlPreview);
    }

    public class InvoiceParserFactory : IInvoiceParserFactory
    {
        private readonly IEnumerable<IInvoiceParser> _parsers;

        public InvoiceParserFactory(IEnumerable<IInvoiceParser> parsers)
        {
            _parsers = parsers;
        }

        public IInvoiceParser GetParser(string xmlPreview)
        {
            foreach (var parser in _parsers)
            {
                if (parser.CanParse(xmlPreview))
                {
                    return parser;
                }
            }

            // Mặc định dùng StandardTctParser
            return _parsers.OfType<StandardTctParser>().FirstOrDefault() ?? new StandardTctParser();
        }
    }
}
