using InvoiceManager.Models.Entities;
using InvoiceManager.Models.ViewModels;
using System;
using System.Threading.Tasks;

namespace InvoiceManager.Services
{
    public interface ITaxDeclarationService
    {
        Task<TaxDeclarationViewModel> CalculateDeclarationAsync(int taxAccountId, string periodType, int periodValue, int year, DateTime? customFrom = null, DateTime? customTo = null, decimal carriedOverTax = 0);
        string GenerateDeclarationXml(TaxDeclarationViewModel model);
        Task<byte[]> ExportDeclarationExcelAsync(TaxDeclarationViewModel model, int taxAccountId);
    }
}

