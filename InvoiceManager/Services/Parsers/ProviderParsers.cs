using System.IO;
using System.Threading.Tasks;

namespace InvoiceManager.Services.Parsers
{
    public class MisaInvoiceParser : StandardTctParser
    {
        public new string ProviderName => "MISA";

        public new bool CanParse(string xmlContent)
        {
            if (string.IsNullOrWhiteSpace(xmlContent)) return false;
            return xmlContent.Contains("meInvoice") || xmlContent.Contains("misa.vn") || xmlContent.Contains("MISA");
        }
    }

    public class ViettelInvoiceParser : StandardTctParser
    {
        public new string ProviderName => "Viettel";

        public new bool CanParse(string xmlContent)
        {
            if (string.IsNullOrWhiteSpace(xmlContent)) return false;
            return xmlContent.Contains("sinvoice") || xmlContent.Contains("viettel.vn") || xmlContent.Contains("Viettel");
        }
    }

    public class VnptInvoiceParser : StandardTctParser
    {
        public new string ProviderName => "VNPT";

        public new bool CanParse(string xmlContent)
        {
            if (string.IsNullOrWhiteSpace(xmlContent)) return false;
            return xmlContent.Contains("vnpt-invoice") || xmlContent.Contains("vnpt.vn") || xmlContent.Contains("VNPT");
        }
    }
}
