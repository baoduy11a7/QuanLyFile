using InvoiceManager.Models.ViewModels;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace InvoiceManager.Services
{
    public interface IOriginalInvoiceConfigService
    {
        Task<OriginalInvoiceConfigModel> GetConfigAsync(int taxAccountId, CancellationToken ct = default);
        Task<bool> SaveConfigAsync(OriginalInvoiceConfigModel model, CancellationToken ct = default);
        Task<OriginalInvoiceConfigModel> ResetToDefaultAsync(int taxAccountId, CancellationToken ct = default);
        Task<(bool Success, string Message, string? LastConnectedAt)> TestGovConnectionAsync(int taxAccountId, string? username, string? password, CancellationToken ct = default);
        string BuildResolvedStoragePath(OriginalInvoiceConfigModel config, string companyTaxCode, DateTime issueDate, string sellerTaxCode, string invoiceSymbol, string invoiceNumber, string extension);
    }
}

