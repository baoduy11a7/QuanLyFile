using InvoiceManager.Data;
using InvoiceManager.Models.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace InvoiceManager.Services
{
    public class TaxCodeLookupService : ITaxCodeLookupService
    {
        private readonly HttpClient _httpClient;
        private readonly ApplicationDbContext _db;
        private readonly ILogger<TaxCodeLookupService> _logger;

        public const string ClientName = "TaxCodeLookupClient";
        private const string BaseApiUrl = "https://api.vietqr.io/v2/business/";

        public TaxCodeLookupService(
            IHttpClientFactory httpClientFactory,
            ApplicationDbContext db,
            ILogger<TaxCodeLookupService> logger)
        {
            _httpClient = httpClientFactory.CreateClient(ClientName);
            _db = db;
            _logger = logger;
        }

        public async Task<TaxCodeLookupResult> LookupAsync(string taxCode, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(taxCode))
            {
                return new TaxCodeLookupResult
                {
                    Success = false,
                    Message = "Vui lòng nhập mã số thuế cần tra cứu."
                };
            }

            // Chuẩn hóa MST: loại bỏ khoảng trắng, giữ ký tự số và dấu gạch nối (nếu là chi nhánh -001)
            var cleanTaxCode = Regex.Replace(taxCode.Trim(), @"[^\d\-]", "");

            if (cleanTaxCode.Length < 10)
            {
                return new TaxCodeLookupResult
                {
                    Success = false,
                    Message = "Mã số thuế không hợp lệ. MST doanh nghiệp phải từ 10 đến 14 ký tự."
                };
            }

            try
            {
                var requestUrl = $"{BaseApiUrl}{Uri.EscapeDataString(cleanTaxCode)}";
                using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
                request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

                using var response = await _httpClient.SendAsync(request, cancellationToken);
                var content = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Tra cứu MST {TaxCode} thất bại với mã HTTP {StatusCode}: {Content}", cleanTaxCode, response.StatusCode, content);
                    return new TaxCodeLookupResult
                    {
                        Success = false,
                        Message = $"Không thể tra cứu thông tin từ Cổng Thuế (HTTP {response.StatusCode}). Vui lòng thử lại sau."
                    };
                }

                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;

                var code = root.TryGetProperty("code", out var codeProp) ? codeProp.GetString() : null;
                var desc = root.TryGetProperty("desc", out var descProp) ? descProp.GetString() : null;

                if (code == "00" && root.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Object)
                {
                    var id = dataProp.TryGetProperty("id", out var idProp) ? idProp.GetString() : cleanTaxCode;
                    var name = dataProp.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                    var internationalName = dataProp.TryGetProperty("internationalName", out var inProp) ? inProp.GetString() : null;
                    var shortName = dataProp.TryGetProperty("shortName", out var snProp) ? snProp.GetString() : null;
                    var address = dataProp.TryGetProperty("address", out var addrProp) ? addrProp.GetString() : null;
                    var status = dataProp.TryGetProperty("status", out var stProp) ? stProp.GetString() : "Đang hoạt động";

                    // Kiểm tra xem MST này đã có trong cơ sở dữ liệu hệ thống chưa
                    var existingAccount = await _db.TaxAccounts
                        .AsNoTracking()
                        .FirstOrDefaultAsync(t => t.TaxCode == cleanTaxCode || t.TaxCode == id, cancellationToken);

                    return new TaxCodeLookupResult
                    {
                        Success = true,
                        TaxCode = id ?? cleanTaxCode,
                        CompanyName = name,
                        InternationalName = internationalName,
                        ShortName = shortName,
                        Address = address,
                        Status = status,
                        Source = "Cổng thông tin Tổng cục Thuế (gdt.gov.vn)",
                        ExistsInSystem = existingAccount != null,
                        ExistingAccountId = existingAccount?.Id
                    };
                }

                if (code == "51")
                {
                    return new TaxCodeLookupResult
                    {
                        Success = false,
                        Message = "Mã số thuế không tồn tại trên hệ thống dữ liệu Thuế Quốc gia."
                    };
                }

                return new TaxCodeLookupResult
                {
                    Success = false,
                    Message = desc ?? "Không tìm thấy thông tin doanh nghiệp theo mã số thuế này."
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi ngoại lệ khi tra cứu MST {TaxCode}", cleanTaxCode);
                return new TaxCodeLookupResult
                {
                    Success = false,
                    Message = "Lỗi kết nối khi cào dữ liệu từ Cổng Thuế: " + ex.Message
                };
            }
        }
    }
}
