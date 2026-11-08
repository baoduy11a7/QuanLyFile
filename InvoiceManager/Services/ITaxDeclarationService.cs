using InvoiceManager.Models.Entities;
using InvoiceManager.Models.ViewModels;
using InvoiceManager.Services.TaxEngine;
using System;
using System.Threading.Tasks;

namespace InvoiceManager.Services
{
    public interface ITaxDeclarationService
    {
        Task<TaxDeclarationViewModel> CalculateDeclarationAsync(
            int taxAccountId,
            string periodType,
            int periodValue,
            int year,
            DateTime? customFrom = null,
            DateTime? customTo = null,
            decimal carriedOverTax = 0,
            bool? isZeroActivity = null,
            bool autoFetchPriorPeriod = false);

        string GenerateDeclarationXml(TaxDeclarationViewModel model);
        Task<byte[]> ExportDeclarationExcelAsync(TaxDeclarationViewModel model, int taxAccountId);
        Task<decimal> GetPriorPeriodTarget43Async(int taxAccountId, string periodType, int periodValue, int year);
        ITaxFormRegistry GetRegistry();
        TaxLegalVersionReport GenerateLegalReport(TaxDeclarationViewModel model);
    }
}
