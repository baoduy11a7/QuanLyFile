using InvoiceManager.Services.Providers.Common;
using InvoiceManager.Services.Providers.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
            var baseUrl = _options.BaseUrl.TrimEnd('/') + "/";
            client.BaseAddress = new Uri(baseUrl);
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            client.DefaultRequestHeaders.Add("User-Agent", _options.UserAgent);
            client.DefaultRequestHeaders.Add("Referer", "https://hoadondientu.gdt.gov.vn/");
            client.DefaultRequestHeaders.Add("Origin", "https://hoadondientu.gdt.gov.vn");
            client.DefaultRequestHeaders.TryAddWithoutValidation("request-id", Guid.NewGuid().ToString());
            client.DefaultRequestHeaders.TryAddWithoutValidation("End-Point", "/tra-cuu/tra-cuu-hoa-don");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Action", "");

            if (!string.IsNullOrWhiteSpace(bearerToken))
            {
                var cleanToken = bearerToken.Trim();
                if (cleanToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    cleanToken = cleanToken.Substring(7).Trim();
                }
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cleanToken);
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

        private async Task<string?> RunHeadlessAgentAsync(string args, CancellationToken ct)
        {
            try
            {
                var scriptPath = Path.Combine(Directory.GetCurrentDirectory(), "tools", "gdt_agent.js");
                if (!File.Exists(scriptPath))
                {
                    scriptPath = Path.Combine(AppContext.BaseDirectory, "tools", "gdt_agent.js");
                }
                if (!File.Exists(scriptPath))
                {
                    scriptPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "tools", "gdt_agent.js");
                }
                if (!File.Exists(scriptPath))
                {
                    var parentDir = Directory.GetParent(Directory.GetCurrentDirectory())?.FullName;
                    if (!string.IsNullOrEmpty(parentDir))
                    {
                        scriptPath = Path.Combine(parentDir, "tools", "gdt_agent.js");
                    }
                }

                if (!File.Exists(scriptPath))
                {
                    _logger.LogWarning("Không tìm thấy gdt_agent.js tại: {Path}", scriptPath);
                    return null;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = "node",
                    Arguments = $"\"{scriptPath}\" {args}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };

                using var process = Process.Start(psi);
                if (process == null) return null;

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                await process.WaitForExitAsync(ct);
                var output = await stdoutTask;
                var stderr = await stderrTask;

                if (!string.IsNullOrEmpty(stderr))
                {
                    _logger.LogWarning("Headless Agent stderr: {Error}", stderr);
                }

                if (process.ExitCode != 0 || string.IsNullOrEmpty(output))
                {
                    _logger.LogWarning("Headless Chrome Agent kết thúc với mã lỗi {Code}. Output: {Out}. Stderr: {Err}", process.ExitCode, output, stderr);
                    return null;
                }

                return output?.Trim();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lỗi khi chạy headless Chrome agent: {Args}", args);
                return null;
            }
        }

        public async Task<CaptchaChallenge?> GetCaptchaAsync(CancellationToken ct = default)
        {
            // 1. Thử lấy Captcha qua Headless Chrome Agent (đồng bộ chuẩn session browser)
            try
            {
                var agentOutput = await RunHeadlessAgentAsync("get-captcha", ct);
                if (!string.IsNullOrEmpty(agentOutput))
                {
                    using var doc = JsonDocument.Parse(agentOutput);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("success", out var succ) && succ.GetBoolean())
                    {
                        string key = root.TryGetProperty("key", out var kp) ? kp.GetString() ?? "" : "";
                        string content = root.TryGetProperty("content", out var cp) ? cp.GetString() ?? "" : "";
                        if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(content))
                        {
                            bool isSvg = content.TrimStart().StartsWith("<svg", StringComparison.OrdinalIgnoreCase);
                            string formatted = isSvg
                                ? "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(content))
                                : (content.StartsWith("data:image", StringComparison.OrdinalIgnoreCase) ? content : "data:image/png;base64," + content);

                            return new CaptchaChallenge
                            {
                                Key = key,
                                ImageBase64 = formatted,
                                IsSvg = isSvg,
                                SvgContent = isSvg ? content : null
                            };
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lỗi khi lấy captcha qua Headless Agent, chuyển sang HttpClient.");
            }

            // 2. Fallback sang HttpClient truyền thống
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
            if (!string.IsNullOrWhiteSpace(cred.BearerToken))
            {
                _logger.LogInformation("Sử dụng Bearer Token trực tiếp từ phiên đăng nhập Cổng Thuế.");
                return LoginResult.Ok(cred.BearerToken.Trim(), DateTime.UtcNow.AddHours(4));
            }

            if (string.IsNullOrWhiteSpace(cred.Username) || string.IsNullOrWhiteSpace(cred.Password))
            {
                return LoginResult.Fail("Vui lòng cung cấp đầy đủ tên đăng nhập và mật khẩu hoặc Bearer Token.");
            }

            try
            {
                // 1. Thử đăng nhập qua Headless Chrome Agent (để vượt qua lớp bảo mật F5 WAF của Cổng Thuế)
                var safeUser = cred.Username.Trim().Replace("\"", "\\\"");
                var safePass = cred.Password.Replace("\"", "\\\"");
                var safeCaptcha = (cred.CaptchaCode?.Trim() ?? string.Empty).Replace("\"", "\\\"");
                var safeKey = (cred.CaptchaKey ?? string.Empty).Replace("\"", "\\\"");

                var agentArgs = $"login --username \"{safeUser}\" --password \"{safePass}\" --captcha \"{safeCaptcha}\" --key \"{safeKey}\"";
                var agentOutput = await RunHeadlessAgentAsync(agentArgs, ct);
                if (!string.IsNullOrEmpty(agentOutput))
                {
                    try
                    {
                        using var agentDoc = JsonDocument.Parse(agentOutput);
                        var agentRoot = agentDoc.RootElement;
                        if (agentRoot.TryGetProperty("success", out var successProp) && successProp.GetBoolean())
                        {
                            string? token = agentRoot.TryGetProperty("token", out var tokProp) ? tokProp.GetString() : null;
                            if (!string.IsNullOrEmpty(token))
                            {
                                _logger.LogInformation("Đăng nhập Cổng Thuế thành công qua Headless Chrome Agent cho MST: {Mst}", cred.Username);
                                return LoginResult.Ok(token, DateTime.UtcNow.AddHours(4));
                            }
                        }
                        else
                        {
                            string msg = agentRoot.TryGetProperty("message", out var msgProp) ? msgProp.GetString() ?? "Đăng nhập không thành công" : "Đăng nhập không thành công";
                            _logger.LogWarning("Headless Chrome Agent trả về lỗi: {Msg}", msg);
                            return LoginResult.Fail(msg, true);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Không thể phân tích phản hồi từ Headless Chrome Agent: {Output}", agentOutput);
                    }
                }

                // 2. Fallback sang HttpClient truyền thống nếu Headless Agent không chạy được
                await ApplyRateLimitAsync(ct);
                var client = CreateClient();

                var payload = new
                {
                    username = cred.Username.Trim(),
                    password = cred.Password,
                    cvalue = cred.CaptchaCode?.Trim() ?? string.Empty,
                    ckey = cred.CaptchaKey ?? string.Empty
                };

                var jsonContent = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                var endpoint = _options.LoginEndpoint.TrimStart('/');
                var response = await client.PostAsync(endpoint, jsonContent, ct);

                var responseBody = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                {
                    string errorMsg = "Đăng nhập Cổng Thuế không thành công.";
                    if (responseBody.Contains("Request Rejected", StringComparison.OrdinalIgnoreCase))
                    {
                        errorMsg = "Tường lửa an ninh (F5 WAF) của Cổng Thuế đã chặn yêu cầu do nghi ngờ truy cập bất thường.";
                    }
                    else
                    {
                        try
                        {
                            using var errDoc = JsonDocument.Parse(responseBody);
                            if (errDoc.RootElement.TryGetProperty("message", out var msgProp))
                            {
                                errorMsg = msgProp.GetString() ?? errorMsg;
                            }
                        }
                        catch { }
                    }

                    bool isRenderEnv = string.Equals(Environment.GetEnvironmentVariable("RENDER"), "true", StringComparison.OrdinalIgnoreCase);
                    string tip = isRenderEnv
                        ? " [Lưu ý: Máy chủ Render đặt tại Singapore bị Cổng Thuế Việt Nam chặn IP nước ngoài. Vui lòng thao tác trên máy cá nhân Localhost hoặc sử dụng chức năng nạp file XML/Excel]."
                        : " [Gợi ý: Vui lòng kiểm tra lại mật khẩu Cổng Thuế hoặc bấm đổi mã Captcha mới nếu thao tác quá 60 giây].";

                    if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        return LoginResult.Fail($"Cổng thuế từ chối xác thực ({response.StatusCode}): {errorMsg}{tip}", true);
                    }

                    return LoginResult.Fail($"Lỗi từ Cổng thuế ({response.StatusCode}): {errorMsg}{tip}", true);
                }

                using var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;
                string? tokenDirect = null;

                if (root.TryGetProperty("token", out var tokenProp)) tokenDirect = tokenProp.GetString();
                else if (root.TryGetProperty("access_token", out var accTokenProp)) tokenDirect = accTokenProp.GetString();
                else if (root.TryGetProperty("jwt", out var jwtProp)) tokenDirect = jwtProp.GetString();

                if (string.IsNullOrEmpty(tokenDirect))
                {
                    return LoginResult.Fail("Đăng nhập thành công nhưng không nhận được mã xác thực (Token).");
                }

                return LoginResult.Ok(tokenDirect, DateTime.UtcNow.AddHours(4));
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
                        var fromStr = chunk.Start.ToString("dd'/'MM'/'yyyy", CultureInfo.InvariantCulture);
                        var toStr = chunk.End.ToString("dd'/'MM'/'yyyy", CultureInfo.InvariantCulture);
                        var query = $"{baseEndpoint}?sort=tdlap:desc&size={_options.PageSize}";
                        query += $"&search=tdlap=ge={fromStr}T00:00:00;tdlap=le={toStr}T23:59:59";

                        if (!string.IsNullOrEmpty(state))
                        {
                            query += $"&state={Uri.EscapeDataString(state)}";
                        }

                        _logger.LogInformation("Quét hóa đơn Cổng Thuế ({Type}): {From} -> {To} | Trang {Page} | State: {State}",
                            invType, fromStr, toStr, page, state ?? "INIT");

                        HttpResponseMessage response;
                        try
                        {
                            client.DefaultRequestHeaders.Remove("request-id");
                            client.DefaultRequestHeaders.TryAddWithoutValidation("request-id", Guid.NewGuid().ToString());
                            response = await client.GetAsync(query, ct);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Lỗi kết nối khi truy vấn hóa đơn từ Cổng Thuế đoạn ngày {From} - {To}", fromStr, toStr);
                            break;
                        }

                        if (!response.IsSuccessStatusCode)
                        {
                            var errBody = await response.Content.ReadAsStringAsync(ct);
                            _logger.LogWarning("Truy vấn trực tiếp Cổng Thuế trả về mã {StatusCode} (URL: {Query}). Body: {ErrBody}. Thử lấy qua Headless Chrome Agent...", response.StatusCode, query, errBody);
                            
                            var searchArg = $"tdlap=ge={fromStr}T00:00:00;tdlap=le={toStr}T23:59:59";
                            var agentFetchArgs = $"fetch-invoices --token \"{token}\" --endpoint \"/{baseEndpoint}\" --search \"{searchArg}\"";
                            var agentFetchOut = await RunHeadlessAgentAsync(agentFetchArgs, ct);
                            var agentInvoices = new List<RemoteInvoice>();

                            if (!string.IsNullOrEmpty(agentFetchOut))
                            {
                                try
                                {
                                    using var fetchDoc = JsonDocument.Parse(agentFetchOut);
                                    if (fetchDoc.RootElement.TryGetProperty("datas", out var arr) && arr.ValueKind == JsonValueKind.Array)
                                    {
                                        int rawAgentCount = 0;
                                        foreach (var item in arr.EnumerateArray())
                                        {
                                            rawAgentCount++;
                                            var remote = ParseInvoiceItem(item, invType, req.TaxCode);
                                            if (remote != null)
                                            {
                                                agentInvoices.Add(remote);
                                            }
                                        }
                                        _logger.LogInformation("Headless Chrome Agent trả về {RawCount} bản ghi thô (Parse thành công: {SuccessCount})",
                                            rawAgentCount, agentInvoices.Count);
                                    }
                                    else if (fetchDoc.RootElement.TryGetProperty("message", out var agentErrMsg))
                                    {
                                        _logger.LogWarning("Headless Chrome Agent trả về lỗi: {Msg}", agentErrMsg.GetString());
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "Lỗi phân tích hóa đơn từ Headless Chrome Agent");
                                }
                            }

                            foreach (var remote in agentInvoices)
                            {
                                yield return remote;
                            }
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

                        int totalRawItems = 0;
                        int parsedItemCount = 0;

                        if (itemsArray.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in itemsArray.EnumerateArray())
                            {
                                totalRawItems++;
                                var remote = ParseInvoiceItem(item, invType, req.TaxCode);
                                if (remote != null)
                                {
                                    parsedItemCount++;
                                    yield return remote;
                                }
                            }
                        }

                        _logger.LogInformation("Cổng Thuế trả về {RawCount} bản ghi JSON thô (Bóc tách thành công: {SuccessCount}) cho chu kỳ {From} - {To}",
                            totalRawItems, parsedItemCount, fromStr, toStr);

                        // Kiểm tra phân trang qua cursor "state"
                        if (root.TryGetProperty("state", out var nextStateProp) && nextStateProp.ValueKind == JsonValueKind.String)
                        {
                            var nextState = nextStateProp.GetString();
                            if (!string.IsNullOrEmpty(nextState) && nextState != state && totalRawItems > 0)
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

        private static string GetJsonStringValue(JsonElement item, string prop)
        {
            if (!item.TryGetProperty(prop, out var p)) return string.Empty;
            switch (p.ValueKind)
            {
                case JsonValueKind.String:
                    return p.GetString()?.Trim() ?? string.Empty;
                case JsonValueKind.Number:
                    return p.GetRawText().Trim();
                case JsonValueKind.True:
                    return "true";
                case JsonValueKind.False:
                    return "false";
                default:
                    return string.Empty;
            }
        }

        private static decimal GetJsonDecimalValue(JsonElement item, string prop)
        {
            if (!item.TryGetProperty(prop, out var p)) return 0;
            if (p.ValueKind == JsonValueKind.Number)
            {
                return p.TryGetDecimal(out var d) ? d : 0;
            }
            if (p.ValueKind == JsonValueKind.String)
            {
                var s = p.GetString();
                if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return d;
                if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.CurrentCulture, out d)) return d;
            }
            return 0;
        }

        private RemoteInvoice? ParseInvoiceItem(JsonElement item, string defaultType, string currentTaxCode)
        {
            try
            {
                var symbol = GetJsonStringValue(item, "khhdon");
                var rawNumber = GetJsonStringValue(item, "shdon");
                if (string.IsNullOrEmpty(symbol) || string.IsNullOrEmpty(rawNumber)) return null;

                DateTime issueDate = DateTime.Now;
                var dateStr = GetJsonStringValue(item, "tdlap");
                if (!string.IsNullOrEmpty(dateStr))
                {
                    if (DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) issueDate = d;
                    else if (DateTime.TryParse(dateStr, out d)) issueDate = d;
                }

                var id = GetJsonStringValue(item, "id");
                var sellerMst = GetJsonStringValue(item, "nbmst");
                var sellerName = GetJsonStringValue(item, "nbten");
                var sellerAddress = GetJsonStringValue(item, "nbdchi");
                var buyerMst = GetJsonStringValue(item, "nmdmst");
                var buyerName = GetJsonStringValue(item, "nmten");
                var buyerAddress = GetJsonStringValue(item, "nmdchi");

                // Chuẩn hóa số hóa đơn 7 chữ số nhưng vẫn bảo lưu rawNumber gốc cho việc tải XML
                var normalizedNumber = rawNumber;
                if (long.TryParse(rawNumber, out var numVal))
                {
                    normalizedNumber = numVal.ToString("D7");
                }
                else
                {
                    normalizedNumber = rawNumber.PadLeft(7, '0');
                }

                return new RemoteInvoice
                {
                    ProviderKey = !string.IsNullOrEmpty(id) ? id : $"{sellerMst}_{symbol}_{normalizedNumber}",
                    InvoiceSymbol = symbol,
                    InvoiceNumber = normalizedNumber,
                    RawInvoiceNumber = rawNumber,
                    IssueDate = issueDate,
                    SellerTaxCode = sellerMst,
                    SellerName = sellerName,
                    SellerAddress = sellerAddress,
                    BuyerTaxCode = buyerMst,
                    BuyerName = buyerName,
                    BuyerAddress = buyerAddress,
                    AmountBeforeTax = GetJsonDecimalValue(item, "tgtcthue"),
                    TaxAmount = GetJsonDecimalValue(item, "tgtthue"),
                    TotalAmount = GetJsonDecimalValue(item, "tgtttbso"),
                    InvoiceType = defaultType,
                    CqtCode = GetJsonStringValue(item, "mcqt"),
                    Status = MapGdtStatus(GetJsonStringValue(item, "ttxly"))
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lỗi khi parse 1 dòng hóa đơn từ response Cổng Thuế: {RawItem}", item.GetRawText());
                return null;
            }
        }

        private static string MapGdtStatus(string? ttxly)
        {
            return ttxly switch
            {
                "1" => "Mới lập",
                "2" => "Đã gửi CQT",
                "3" => "CQT đã tiếp nhận",
                "4" => "Không tiếp nhận",
                "5" => "Đã cấp mã",
                "6" => "Từ chối cấp mã",
                "7" => "Đã thay thế",
                "8" => "Đã điều chỉnh",
                "9" => "Đã bị hủy",
                _ => string.IsNullOrWhiteSpace(ttxly) ? "Hóa đơn mới" : ttxly
            };
        }

        public async Task<byte[]> DownloadXmlAsync(RemoteInvoice inv, string token, CancellationToken ct = default)
        {
            await ApplyRateLimitAsync(ct);
            var client = CreateClient(token);

            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

            var endpoint = _options.ExportXmlEndpoint.TrimStart('/');
            var shdonPrimary = !string.IsNullOrEmpty(inv.RawInvoiceNumber) ? inv.RawInvoiceNumber : inv.InvoiceNumber;

            // 1. Thử URL chuẩn của Cổng Thuế: nbmst, khhdon, shdon với timeout ngắn 5s
            var url = $"{endpoint}?nbmst={Uri.EscapeDataString(inv.SellerTaxCode)}&khhdon={Uri.EscapeDataString(inv.InvoiceSymbol)}&shdon={Uri.EscapeDataString(shdonPrimary)}";

            HttpResponseMessage? response = null;
            try
            {
                client.DefaultRequestHeaders.Remove("request-id");
                client.DefaultRequestHeaders.TryAddWithoutValidation("request-id", Guid.NewGuid().ToString());
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                response = await client.GetAsync(url, cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Tải XML HĐ {Symbol}-{Number} lỗi: {Msg}", inv.InvoiceSymbol, shdonPrimary, ex.Message);
            }

            // Nếu gặp lỗi 500 (Internal Server Error) hoặc 400 (Bad Request), Cổng thuế không hỗ trợ file XML gốc cho HĐ này
            // Trả về mảng rỗng ngay trong vài mili-giây để luồng chính tự động fallback sang nạp metadata trực tiếp
            if (response == null || response.StatusCode == HttpStatusCode.InternalServerError || response.StatusCode == HttpStatusCode.BadRequest)
            {
                return Array.Empty<byte>();
            }

            // 2. Chỉ khi gặp 404 và ký hiệu chưa có tiền tố mẫu số "1", thử thêm "1" một lần duy nhất
            if (!response.IsSuccessStatusCode && response.StatusCode == HttpStatusCode.NotFound && !string.IsNullOrEmpty(inv.InvoiceSymbol) && !char.IsDigit(inv.InvoiceSymbol[0]))
            {
                var symbolWithMau = $"1{inv.InvoiceSymbol}";
                var retryMauUrl = $"{endpoint}?nbmst={Uri.EscapeDataString(inv.SellerTaxCode)}&khhdon={Uri.EscapeDataString(symbolWithMau)}&shdon={Uri.EscapeDataString(shdonPrimary)}";
                try
                {
                    client.DefaultRequestHeaders.Remove("request-id");
                    client.DefaultRequestHeaders.TryAddWithoutValidation("request-id", Guid.NewGuid().ToString());
                    using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts2.CancelAfter(TimeSpan.FromSeconds(3));
                    var retryMauResp = await client.GetAsync(retryMauUrl, cts2.Token);
                    if (retryMauResp.IsSuccessStatusCode)
                    {
                        response = retryMauResp;
                    }
                }
                catch { }
            }

            if (response == null || !response.IsSuccessStatusCode)
            {
                return Array.Empty<byte>();
            }

            var rawBytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (rawBytes == null || rawBytes.Length == 0)
            {
                return Array.Empty<byte>();
            }

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
