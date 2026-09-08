using InvoiceManager.Services;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace InvoiceManager.Jobs
{
    public class InvoiceAutoSyncJob
    {
        private readonly ISyncService _syncService;
        private readonly ILogger<InvoiceAutoSyncJob> _logger;

        public InvoiceAutoSyncJob(ISyncService syncService, ILogger<InvoiceAutoSyncJob> logger)
        {
            _syncService = syncService;
            _logger = logger;
        }

        public async Task ExecuteAsync()
        {
            _logger.LogInformation("Hangfire Job: Bắt đầu quét và đồng bộ hóa đơn tự động lúc {Time}...", DateTime.Now);
            try
            {
                await _syncService.SyncAllActiveTaxAccountsAsync();
                _logger.LogInformation("Hangfire Job: Hoàn tất quét đồng bộ hóa đơn.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Hangfire Job: Lỗi trong quá trình quét đồng bộ hóa đơn.");
            }
        }
    }
}
