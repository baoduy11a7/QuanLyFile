using InvoiceManager.Data;
using InvoiceManager.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace InvoiceManager.Services
{
    public class SyncService : ISyncService
    {
        private readonly ApplicationDbContext _db;
        private readonly IInvoiceImportService _importService;
        private readonly ILogger<SyncService> _logger;
        private readonly string _watchFolder;

        public SyncService(
            ApplicationDbContext db,
            IInvoiceImportService importService,
            IConfiguration config,
            ILogger<SyncService> logger)
        {
            _db = db;
            _importService = importService;
            _logger = logger;
            _watchFolder = config.GetValue<string>("InvoiceSettings:WatchFolderPath") ?? "App_Data/WatchFolder";
        }

        public async Task<SyncLog> SyncInvoicesForTaxAccountAsync(int taxAccountId, string syncType = "Thủ công")
        {
            var taxAccount = await _db.TaxAccounts.FindAsync(taxAccountId);
            var syncLog = new SyncLog
            {
                TaxAccountId = taxAccountId,
                SyncedAt = DateTime.Now,
                SyncType = syncType,
                Status = "Thành công",
                NewInvoiceCount = 0
            };

            if (taxAccount == null)
            {
                syncLog.Status = "Thất bại";
                syncLog.ErrorMessage = "Không tìm thấy tài khoản thuế.";
                _db.SyncLogs.Add(syncLog);
                await _db.SaveChangesAsync();
                return syncLog;
            }

            try
            {
                var folder = Path.Combine(_watchFolder, taxAccountId.ToString());
                if (!Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                var files = Directory.GetFiles(folder, "*.xml", SearchOption.TopDirectoryOnly)
                    .Concat(Directory.GetFiles(folder, "*.zip", SearchOption.TopDirectoryOnly))
                    .ToList();

                if (!files.Any())
                {
                    syncLog.Status = "Thành công";
                    syncLog.ErrorMessage = "Đã kiểm tra thư mục theo dõi: Không có hóa đơn mới cần đồng bộ.";
                    _db.SyncLogs.Add(syncLog);
                    await _db.SaveChangesAsync();
                    return syncLog;
                }

                int imported = 0;
                var processedDir = Path.Combine(folder, "Processed");
                Directory.CreateDirectory(processedDir);

                foreach (var file in files)
                {
                    var isZip = file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
                    using var fs = File.OpenRead(file);

                    ImportBatchResult res;
                    if (isZip)
                    {
                        res = await _importService.ImportZipAsync(fs, taxAccountId, "MuaVao", "Hangfire-Sync");
                    }
                    else
                    {
                        res = await _importService.ImportXmlAsync(fs, Path.GetFileName(file), taxAccountId, "MuaVao", "Hangfire-Sync");
                    }

                    imported += res.SuccessCount;

                    // Di chuyển file đã xử lý vào Processed để tránh quét lại
                    fs.Close();
                    var dest = Path.Combine(processedDir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{Path.GetFileName(file)}");
                    File.Move(file, dest);
                }

                syncLog.NewInvoiceCount = imported;
                syncLog.Status = "Thành công";
                syncLog.ErrorMessage = $"Đã xử lý {files.Count} files, nhập thành công {imported} hóa đơn mới.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi đồng bộ hóa đơn cho TaxAccountId {Id}", taxAccountId);
                syncLog.Status = "Thất bại";
                syncLog.ErrorMessage = ex.Message;
            }

            _db.SyncLogs.Add(syncLog);
            await _db.SaveChangesAsync();
            return syncLog;
        }

        public async Task SyncAllActiveTaxAccountsAsync()
        {
            var accounts = await _db.TaxAccounts.Where(t => t.IsActive).ToListAsync();
            foreach (var acc in accounts)
            {
                await SyncInvoicesForTaxAccountAsync(acc.Id, "Hangfire Tự động");
            }
        }
    }
}
