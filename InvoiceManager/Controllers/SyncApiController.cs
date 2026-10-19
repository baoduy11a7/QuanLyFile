using InvoiceManager.Data;
using InvoiceManager.Models.Entities;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace InvoiceManager.Controllers
{
    [ApiController]
    [Route("api/sync")]
    [Route("api/[controller]")]
    [EnableCors("AllowExtension")]
    public class SyncApiController : ControllerBase
    {
        private readonly ApplicationDbContext _db;

        public SyncApiController(ApplicationDbContext db)
        {
            _db = db;
        }

        /// <summary>
        /// API kiểm tra kết nối từ Chrome Extension và lấy danh sách công ty đang có
        /// </summary>
        [HttpGet("companies")]
        public async Task<IActionResult> GetCompanies()
        {
            var companies = await _db.TaxAccounts
                .Where(t => t.IsActive)
                .OrderBy(t => t.TaxCode)
                .Select(t => new
                {
                    id = t.Id,
                    taxCode = t.TaxCode,
                    companyName = t.CompanyName
                })
                .ToListAsync();

            return Ok(new
            {
                success = true,
                message = "Kết nối thành công tới máy chủ InvoiceManager.",
                serverTime = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"),
                companies
            });
        }

        /// <summary>
        /// API nhận hóa đơn do Chrome Extension quét trực tiếp từ Cổng Thuế đẩy về
        /// </summary>
        [HttpPost("push-invoices")]
        public async Task<IActionResult> PushInvoices([FromBody] ExtensionPushInvoicesRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.TaxCode) || request.Invoices == null)
            {
                return BadRequest(new { success = false, message = "Dữ liệu hóa đơn gửi lên không hợp lệ." });
            }

            var cleanTaxCode = request.TaxCode.Trim();
            var taxAccount = await _db.TaxAccounts.FirstOrDefaultAsync(t => t.TaxCode == cleanTaxCode && t.IsActive);
            if (taxAccount == null)
            {
                // Nếu chưa có, tìm kiếm linh hoạt hoặc thông báo
                taxAccount = await _db.TaxAccounts.FirstOrDefaultAsync(t => t.TaxCode.Contains(cleanTaxCode) && t.IsActive);
                if (taxAccount == null)
                {
                    return NotFound(new
                    {
                        success = false,
                        message = $"Không tìm thấy doanh nghiệp có MST '{cleanTaxCode}' trên hệ thống. Vui lòng thêm công ty này trong mục Quản lý Doanh nghiệp trước."
                    });
                }
            }

            int insertedCount = 0;
            int duplicateCount = 0;
            var invoiceType = string.Equals(request.InvoiceType, "BanRa", StringComparison.OrdinalIgnoreCase) ? "BanRa" : "MuaVao";

            foreach (var item in request.Invoices)
            {
                if (string.IsNullOrWhiteSpace(item.InvoiceSymbol) || string.IsNullOrWhiteSpace(item.InvoiceNumber))
                {
                    continue;
                }

                var cleanSymbol = item.InvoiceSymbol.Trim();
                var cleanNumber = item.InvoiceNumber.Trim().PadLeft(7, '0');
                var sellerMst = (item.SellerTaxCode ?? string.Empty).Trim();

                // Kiểm tra chống trùng lặp theo quy chuẩn Idempotent
                var existing = await _db.Invoices.Include(i => i.Details).FirstOrDefaultAsync(i =>
                    i.TaxAccountId == taxAccount.Id &&
                    i.InvoiceType == invoiceType &&
                    i.SellerTaxCode == sellerMst &&
                    i.InvoiceSymbol == cleanSymbol &&
                    i.InvoiceNumber == cleanNumber);

                if (existing != null)
                {
                    // Nếu hóa đơn đã có nhưng chưa có dòng chi tiết hàng hóa, cập nhật bổ sung chi tiết
                    if (!existing.Details.Any() && item.Details != null && item.Details.Any())
                    {
                        foreach (var d in item.Details)
                        {
                            existing.Details.Add(new InvoiceDetail
                            {
                                InvoiceId = existing.Id,
                                LineNumber = d.LineNumber,
                                ItemCode = d.ItemCode,
                                ItemName = !string.IsNullOrWhiteSpace(d.ItemName) ? d.ItemName.Trim() : "Hàng hóa/dịch vụ",
                                Unit = d.Unit,
                                Quantity = d.Quantity > 0 ? d.Quantity : 1,
                                UnitPrice = d.UnitPrice,
                                AmountBeforeTax = d.AmountBeforeTax,
                                TaxRate = d.TaxRate,
                                TaxAmount = d.TaxAmount,
                                TotalAmount = d.TotalAmount > 0 ? d.TotalAmount : (d.AmountBeforeTax + d.TaxAmount)
                            });
                        }
                    }
                    duplicateCount++;
                    continue;
                }

                // Nhận diện phân loại
                bool hasTaxCode = !string.IsNullOrWhiteSpace(item.CqtCode);
                bool isCashRegister = cleanSymbol.Contains("M", StringComparison.OrdinalIgnoreCase);

                var newInvoice = new Invoice
                {
                    TaxAccountId = taxAccount.Id,
                    InvoiceType = invoiceType,
                    InvoiceSymbol = cleanSymbol,
                    InvoiceNumber = cleanNumber,
                    IssueDate = item.IssueDate > DateTime.MinValue ? item.IssueDate : DateTime.Now,
                    SellerTaxCode = sellerMst,
                    SellerName = !string.IsNullOrWhiteSpace(item.SellerName) ? item.SellerName.Trim() : "Không có tên",
                    SellerAddress = item.SellerAddress ?? string.Empty,
                    BuyerTaxCode = (item.BuyerTaxCode ?? string.Empty).Trim(),
                    BuyerName = item.BuyerName ?? string.Empty,
                    BuyerAddress = item.BuyerAddress ?? string.Empty,
                    AmountBeforeTax = item.AmountBeforeTax,
                    TaxAmount = item.TaxAmount,
                    TotalAmount = item.TotalAmount,
                    TaxAuthorityCode = item.CqtCode,
                    HasTaxCode = hasTaxCode,
                    IsCashRegister = isCashRegister,
                    Status = !string.IsNullOrWhiteSpace(item.Status) ? item.Status : "Hóa đơn mới",
                    SourceProvider = "ChromeExtension-GDT",
                    RiskLevel = "Normal",
                    ImportedAt = DateTime.Now
                };

                // Bổ sung chi tiết từng mặt hàng
                if (item.Details != null && item.Details.Any())
                {
                    foreach (var d in item.Details)
                    {
                        newInvoice.Details.Add(new InvoiceDetail
                        {
                            LineNumber = d.LineNumber,
                            ItemCode = d.ItemCode,
                            ItemName = !string.IsNullOrWhiteSpace(d.ItemName) ? d.ItemName.Trim() : "Hàng hóa/dịch vụ",
                            Unit = d.Unit,
                            Quantity = d.Quantity > 0 ? d.Quantity : 1,
                            UnitPrice = d.UnitPrice,
                            AmountBeforeTax = d.AmountBeforeTax,
                            TaxRate = d.TaxRate,
                            TaxAmount = d.TaxAmount,
                            TotalAmount = d.TotalAmount > 0 ? d.TotalAmount : (d.AmountBeforeTax + d.TaxAmount)
                        });
                    }
                }

                _db.Invoices.Add(newInvoice);
                insertedCount++;
            }

            if (insertedCount > 0 || duplicateCount > 0)
            {
                await _db.SaveChangesAsync();
            }

            // Ghi nhật ký đồng bộ SyncLog
            _db.SyncLogs.Add(new SyncLog
            {
                TaxAccountId = taxAccount.Id,
                SyncedAt = DateTime.Now,
                Status = "Thành công",
                NewInvoiceCount = insertedCount,
                SyncType = "Chrome Extension (GDT)",
                ErrorMessage = $"Đã nhận {request.Invoices.Count} HĐ ({insertedCount} mới, {duplicateCount} đã tồn tại/cập nhật)."
            });
            await _db.SaveChangesAsync();

            return Ok(new
            {
                success = true,
                total = request.Invoices.Count,
                inserted = insertedCount,
                duplicated = duplicateCount,
                message = $"Đồng bộ thành công {insertedCount} hóa đơn mới vào công ty {taxAccount.CompanyName} ({duplicateCount} hóa đơn đã tồn tại)."
            });
        }
    }

    public class ExtensionPushInvoicesRequest
    {
        public string TaxCode { get; set; } = string.Empty;
        public string InvoiceType { get; set; } = "MuaVao";
        public List<ExtensionInvoiceItem> Invoices { get; set; } = new();
    }

    public class ExtensionInvoiceItem
    {
        public string? ProviderKey { get; set; }
        public string InvoiceSymbol { get; set; } = string.Empty;
        public string InvoiceNumber { get; set; } = string.Empty;
        public DateTime IssueDate { get; set; }
        public string? SellerTaxCode { get; set; }
        public string? SellerName { get; set; }
        public string? SellerAddress { get; set; }
        public string? BuyerTaxCode { get; set; }
        public string? BuyerName { get; set; }
        public string? BuyerAddress { get; set; }
        public decimal AmountBeforeTax { get; set; }
        public decimal TaxAmount { get; set; }
        public decimal TotalAmount { get; set; }
        public string? CqtCode { get; set; }
        public string? Status { get; set; }
        public string? RawXml { get; set; }
        public List<ExtensionInvoiceDetailItem>? Details { get; set; }
    }

    public class ExtensionInvoiceDetailItem
    {
        public int LineNumber { get; set; } = 1;
        public string? ItemCode { get; set; }
        public string ItemName { get; set; } = string.Empty;
        public string? Unit { get; set; }
        public decimal Quantity { get; set; } = 1;
        public decimal UnitPrice { get; set; } = 0;
        public decimal AmountBeforeTax { get; set; } = 0;
        public decimal TaxRate { get; set; } = 10;
        public decimal TaxAmount { get; set; } = 0;
        public decimal TotalAmount { get; set; } = 0;
    }
}
