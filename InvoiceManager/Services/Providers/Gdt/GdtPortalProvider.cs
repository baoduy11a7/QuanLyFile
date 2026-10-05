using InvoiceManager.Services.Providers.Common;
using InvoiceManager.Services.Providers.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace InvoiceManager.Services.Providers.Gdt
{
    public class GdtPortalProvider : IInvoiceSourceProvider
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly GdtPortalOptions _options;
        private readonly ILogger<GdtPortalProvider> _logger;

        public const string ClientName = "GdtPortalClient";
        public string ProviderName => "Gdt";
        public string DisplayName => "Cổng Tổng cục Thuế (hoadondientu.gdt.gov.vn)";

        public GdtPortalProvider(
            IHttpClientFactory httpClientFactory,
            IOptions<GdtPortalOptions> options,
            ILogger<GdtPortalProvider> logger)
        {
            _httpClientFactory = httpClientFactory;
            _options = options.Value;
            _logger = logger;
        }

        private HttpClient CreateClient(string? bearerToken = null)
        {
            var client = _httpClientFactory.CreateClient(ClientName);
            client.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/'));
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            client.DefaultRequestHeaders.Add("User-Agent", _options.UserAgent);
            client.DefaultRequestHeaders.Add("Referer", "https://hoadondientu.gdt.gov.vn/");
            client.DefaultRequestHeaders.Add("Origin", "https://hoadondientu.gdt.gov.vn");

            if (!string.IsNullOrEmpty(bearerToken))
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            }

            return client;
        }

        private async Task ApplyRateLimitAsync(CancellationToken ct)
        {
            if (_options.RateLimitDelayMs > 0)
            {
                await Task.Delay(_options.RateLimitDelayMs, ct);
            }
        }

        public async Task<CaptchaChallenge?> GetCaptchaAsync(CancellationToken ct = default)
        {
            try
            {
                var client = CreateClient();
                var endpoint = _options.CaptchaEndpoint.TrimStart('/');
                var response = await client.GetAsync(endpoint, ct);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Không thể lấy captcha từ Cổng thuế. StatusCode: {Code}", response.StatusCode);
                    return null;
                }

                var content = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;

                string key = string.Empty;
                string rawImage = string.Empty;

                if (root.TryGetProperty("key", out var keyProp)) key = keyProp.GetString() ?? string.Empty;
                else if (root.TryGetProperty("ckey", out var ckeyProp)) key = ckeyProp.GetString() ?? string.Empty;

                if (root.TryGetProperty("content", out var contentProp)) rawImage = contentProp.GetString() ?? string.Empty;
                else if (root.TryGetProperty("image", out var imgProp)) rawImage = imgProp.GetString() ?? string.Empty;
                else if (root.TryGetProperty("svg", out var svgProp)) rawImage = svgProp.GetString() ?? string.Empty;

                if (string.IsNullOrEmpty(rawImage))
                {
                    // Trường hợp trả trực tiếp image bytes
                    var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/svg+xml";
                    var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                    var b64 = Convert.ToBase64String(bytes);
                    return new CaptchaChallenge
                    {
                        Key = key,
                        ImageBase64 = $"data:{contentType};base64,{b64}",
                        IsSvg = contentType.Contains("svg")
                    };
                }

                // Nếu là chuỗi SVG hoặc Base64
                bool isSvg = rawImage.TrimStart().StartsWith("<svg", StringComparison.OrdinalIgnoreCase);
                string formattedBase64;
                if (isSvg)
                {
                    formattedBase64 = "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(rawImage));
                }
                else if (!rawImage.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
                {
                    formattedBase64 = "data:image/png;base64," + rawImage;
                }
                else
                {
                    formattedBase64 = rawImage;
                }

                return new CaptchaChallenge
                {
                    Key = key,
                    ImageBase64 = formattedBase64,
                    IsSvg = isSvg,
                    SvgContent = isSvg ? rawImage : null
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi gọi API lấy Captcha từ Cổng thuế.");
                return null;
            }
        }

        public async Task<LoginResult> LoginAsync(ProviderCredentials cred, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(cred.Username) || string.IsNullOrWhiteSpace(cred.Password))
            {
                return LoginResult.Fail("Vui lòng cung cấp đầy đủ tên đăng nhập và mật khẩu.");
            }

            try
            {
                await ApplyRateLimitAsync(ct);
                var client = CreateClient();

                var payload = new
                {
                    username = cred.Username.Trim(),
                    password = cred.Password,
                    cpassword = cred.CaptchaCode?.Trim() ?? string.Empty,
                    ckey = cred.CaptchaKey ?? string.Empty
                };

                var jsonContent = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                var endpoint = _options.LoginEndpoint.TrimStart('/');
                var response = await client.PostAsync(endpoint, jsonContent, ct);

                var responseBody = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                {
                    string errorMsg = "Đăng nhập Cổng Thuế không thành công.";
                    try
                    {
                        using var errDoc = JsonDocument.Parse(responseBody);
                        if (errDoc.RootElement.TryGetProperty("message", out var msgProp))
                        {
                            errorMsg = msgProp.GetString() ?? errorMsg;
                        }
                    }
                    catch { }

                    if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        return LoginResult.Fail($"Cổng thuế từ chối xác thực (401/403): {errorMsg}", true);
                    }

                    return LoginResult.Fail($"Lỗi từ Cổng thuế ({response.StatusCode}): {errorMsg}", true);
                }

                using var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;
                string? token = null;

                if (root.TryGetProperty("token", out var tokenProp)) token = tokenProp.GetString();
                else if (root.TryGetProperty("access_token", out var accTokenProp)) token = accTokenProp.GetString();
                else if (root.TryGetProperty("jwt", out var jwtProp)) token = jwtProp.GetString();

                if (string.IsNullOrEmpty(token))
                {
                    return LoginResult.Fail("Đăng nhập thành công nhưng không nhận được mã xác thực (Token).");
                }

                return LoginResult.Ok(token, DateTime.UtcNow.AddHours(4));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi trong quá trình kết nối đăng nhập Cổng Thuế.");
                return LoginResult.Fail($"Lỗi kết nối Cổng Thuế: {ex.Message}");
            }
        }

        public async IAsyncEnumerable<RemoteInvoice> FetchInvoicesAsync(
            FetchRequest req,
            string token,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var dateChunks = DateRangeSplitter.Split(req.FromDate, req.ToDate, 31);
            var typesToFetch = new List<string>();

            if (req.InvoiceType == "All")
            {
                typesToFetch.Add("MuaVao");
                typesToFetch.Add("BanRa");
            }
            else
            {
                typesToFetch.Add(req.InvoiceType);
            }

            var client = CreateClient(token);

            foreach (var chunk in dateChunks)
            {
                foreach (var invType in typesToFetch)
                {
                    ct.ThrowIfCancellationRequested();

                    string baseEndpoint = invType == "MuaVao" 
                        ? _options.PurchaseQueryEndpoint.TrimStart('/') 
                        : _options.SoldQueryEndpoint.TrimStart('/');

                    string? state = null;
                    bool hasMore = true;
                    int page = 1;

                    while (hasMore)
                    {
                        ct.ThrowIfCancellationRequested();
                        await ApplyRateLimitAsync(ct);

                        // Cấu trúc URL query chuẩn của Cổng Thuế
                        // tdlap=ge=dd/MM/yyyyT00:00:00;tdlap=le=dd/MM/yyyyT23:59:59
                        var fromStr = chunk.Start.ToString("dd/MM/yyyy");
                        var toStr = chunk.End.ToString("dd/MM/yyyy");
                        var query = $"{baseEndpoint}?sort=tdlap:desc&size={_options.PageSize}";
                        query += $"&search=tdlap=ge={fromStr}T00:00:00;tdlap=le={toStr}T23:59:59";

                        if (!string.IsNullOrEmpty(state))
                        {
                            query += $"&state={Uri.EscapeDataString(state)}";
                        }

                        HttpResponseMessage response;
                        try
                        {
                            response = await client.GetAsync(query, ct);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Lỗi khi truy vấn hóa đơn từ Cổng Thuế đoạn ngày {From} - {To}", fromStr, toStr);
                            break;
                        }

                        if (!response.IsSuccessStatusCode)
                        {
                            _logger.LogWarning("Truy vấn Cổng Thuế trả về mã {StatusCode} cho endpoint {Query}", response.StatusCode, query);
                            break;
                        }

                        var content = await response.Content.ReadAsStringAsync(ct);
                        using var doc = JsonDocument.Parse(content);
                        var root = doc.RootElement;

                        JsonElement itemsArray = default;
                        if (root.TryGetProperty("datas", out var datasProp) && datasProp.ValueKind == JsonValueKind.Array)
                        {
                            itemsArray = datasProp;
                        }
                        else if (root.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Array)
                        {
                            itemsArray = dataProp;
                        }
                        else if (root.ValueKind == JsonValueKind.Array)
                        {
                            itemsArray = root;
                        }

                        int itemCount = 0;
                        if (itemsArray.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in itemsArray.EnumerateArray())
                            {
                                var remote = ParseInvoiceItem(item, invType, req.TaxCode);
                                if (remote != null)
                                {
                                    itemCount++;
                                    yield return remote;
                                }
                            }
                        }

                        // Kiểm tra phân trang qua cursor "state"
                        if (root.TryGetProperty("state", out var nextStateProp) && nextStateProp.ValueKind == JsonValueKind.String)
                        {
                            var nextState = nextStateProp.GetString();
                            if (!string.IsNullOrEmpty(nextState) && nextState != state && itemCount > 0)
                            {
                                state = nextState;
                                page++;
                            }
                            else
                            {
                                hasMore = false;
                            }
                        }
                        else
                        {
                            hasMore = false;
                        }
                    }
                }
            }
        }

        private RemoteInvoice? ParseInvoiceItem(JsonElement item, string defaultType, string currentTaxCode)
        {
            try
            {
                string GetString(string prop) => item.TryGetProperty(prop, out var p) ? p.GetString() ?? string.Empty : string.Empty;
                decimal GetDecimal(string prop)
                {
                    if (!item.TryGetProperty(prop, out var p)) return 0;
                    if (p.ValueKind == JsonValueKind.Number) return p.GetDecimal();
                    if (decimal.TryParse(p.GetString(), out var val)) return val;
                    return 0;
                }

                var symbol = GetString("khhdon");
                var number = GetString("shdon");
                if (string.IsNullOrEmpty(symbol) || string.IsNullOrEmpty(number)) return null;

                DateTime issueDate = DateTime.Now;
                var dateStr = GetString("tdlap");
                if (DateTime.TryParse(dateStr, out var d)) issueDate = d;

                var id = GetString("id");
                var sellerMst = GetString("nbmst");
                var sellerName = GetString("nbten");
                var buyerMst = GetString("nmdmst");
                var buyerName = GetString("nmten");

                return new RemoteInvoice
                {
                    ProviderKey = !string.IsNullOrEmpty(id) ? id : $"{sellerMst}_{symbol}_{number}",
                    InvoiceSymbol = symbol,
                    InvoiceNumber = number.PadLeft(7, '0'),
                    IssueDate = issueDate,
                    SellerTaxCode = sellerMst,
                    SellerName = sellerName,
                    BuyerTaxCode = buyerMst,
                    BuyerName = buyerName,
                    AmountBeforeTax = GetDecimal("tgtcthue"),
                    TaxAmount = GetDecimal("tgtthue"),
                    TotalAmount = GetDecimal("tgtttbso"),
                    InvoiceType = defaultType,
                    CqtCode = GetString("mcqt"),
                    Status = GetString("ttxly")
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lỗi khi parse 1 dòng hóa đơn từ response Cổng Thuế.");
                return null;
            }
        }

        public async Task<byte[]> DownloadXmlAsync(RemoteInvoice inv, string token, CancellationToken ct = default)
        {
            await ApplyRateLimitAsync(ct);
            var client = CreateClient(token);

            // Cổng thuế hỗ trợ tải XML theo query nbmst, khhdon, shdon hoặc id
            var endpoint = _options.ExportXmlEndpoint.TrimStart('/');
            var url = $"{endpoint}?nbmst={Uri.EscapeDataString(inv.SellerTaxCode)}&khhdon={Uri.EscapeDataString(inv.InvoiceSymbol)}&shdon={Uri.EscapeDataString(inv.InvoiceNumber)}";

            if (!string.IsNullOrEmpty(inv.ProviderKey) && !inv.ProviderKey.Contains("_"))
            {
                url += $"&id={Uri.EscapeDataString(inv.ProviderKey)}";
            }

            var response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var rawBytes = await response.Content.ReadAsByteArrayAsync(ct);

            // Kiểm tra xem phản hồi có phải là file ZIP không (chữ ký PK\x03\x04)
            if (rawBytes.Length > 4 && rawBytes[0] == 0x50 && rawBytes[1] == 0x4B && rawBytes[2] == 0x03 && rawBytes[3] == 0x04)
            {
                using var zipMs = new MemoryStream(rawBytes);
                using var archive = new ZipArchive(zipMs, ZipArchiveMode.Read);
                foreach (var entry in archive.Entries)
                {
                    if (entry.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    {
                        using var entryStream = entry.Open();
                        using var outMs = new MemoryStream();
                        await entryStream.CopyToAsync(outMs, ct);
                        return outMs.ToArray();
                    }
                }
            }

            return rawBytes;
        }
    }
}
