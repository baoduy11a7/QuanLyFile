using InvoiceManager.Services.Parsers;
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
using System.Security.Cryptography;
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

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _tokenCache =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _tokenFileLock = new();

        private static string GetTokenStoreFilePath()
        {
            var baseDir = Directory.GetCurrentDirectory();
            var appData = Path.Combine(baseDir, "App_Data");
            if (!Directory.Exists(appData))
            {
                try { Directory.CreateDirectory(appData); } catch { }
            }
            return Path.Combine(appData, "gdt_tokens.json");
        }

        public static void CacheToken(string? key, string? token, int? taxAccountId = null)
        {
            if (string.IsNullOrWhiteSpace(token)) return;
            var cleanToken = token.Trim();
            if (cleanToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                cleanToken = cleanToken.Substring(7).Trim();
            }

            if (!string.IsNullOrWhiteSpace(key))
            {
                _tokenCache[$"mst:{key.Trim()}"] = cleanToken;
            }
            if (taxAccountId.HasValue && taxAccountId.Value > 0)
            {
                _tokenCache[$"acc:{taxAccountId.Value}"] = cleanToken;
            }

            var sub = ExtractJwtSubject(cleanToken);
            if (!string.IsNullOrWhiteSpace(sub))
            {
                _tokenCache[$"mst:{sub}"] = cleanToken;
            }

            PersistTokenCacheToDisk();
        }

        public static string? GetCachedToken(string? taxCode = null, int? taxAccountId = null)
        {
            EnsureTokenCacheLoadedFromDisk();

            if (!string.IsNullOrWhiteSpace(taxCode))
            {
                var cleanMst = taxCode.Trim();
                if (_tokenCache.TryGetValue($"mst:{cleanMst}", out var tok) && !IsJwtExpired(tok))
                {
                    return tok;
                }
                if (_tokenCache.TryGetValue(cleanMst, out var tokDirect) && !IsJwtExpired(tokDirect))
                {
                    return tokDirect;
                }
            }

            if (taxAccountId.HasValue && taxAccountId.Value > 0)
            {
                if (_tokenCache.TryGetValue($"acc:{taxAccountId.Value}", out var tok) && !IsJwtExpired(tok))
                {
                    return tok;
                }
                if (_tokenCache.TryGetValue($"ACC_{taxAccountId.Value}", out var tokUpper) && !IsJwtExpired(tokUpper))
                {
                    return tokUpper;
                }
            }

            // Fallback thông minh: Duyệt toàn bộ cache tìm token chưa hết hạn khớp với MST (trích xuất qua sub trong JWT)
            var cleanMstFilter = !string.IsNullOrWhiteSpace(taxCode) ? taxCode.Trim() : null;
            string? firstValidToken = null;
            foreach (var kvp in _tokenCache)
            {
                if (IsJwtExpired(kvp.Value)) continue;

                if (firstValidToken == null)
                {
                    firstValidToken = kvp.Value;
                }

                if (!string.IsNullOrEmpty(cleanMstFilter))
                {
                    var sub = ExtractJwtSubject(kvp.Value);
                    if (string.Equals(sub, cleanMstFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        return kvp.Value;
                    }
                }
            }

            // Nếu không yêu cầu MST cụ thể hoặc trong hệ thống chỉ có 1 token Cổng thuế đang hoạt động thì tự động tái sử dụng
            if (firstValidToken != null && string.IsNullOrEmpty(cleanMstFilter))
            {
                return firstValidToken;
            }

            return null;
        }

        public static void InvalidateCachedToken(string? taxCode = null, int? taxAccountId = null)
        {
            if (!string.IsNullOrWhiteSpace(taxCode))
                _tokenCache.TryRemove($"mst:{taxCode.Trim()}", out _);
            if (taxAccountId.HasValue && taxAccountId.Value > 0)
                _tokenCache.TryRemove($"acc:{taxAccountId.Value}", out _);
            PersistTokenCacheToDisk();
        }

        public static bool IsJwtExpired(string? token)
        {
            if (string.IsNullOrWhiteSpace(token)) return true;
            try
            {
                var parts = token.Trim().Split('.');
                if (parts.Length < 2) return false; // Non-JWT mock token
                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                switch (payload.Length % 4)
                {
                    case 2: payload += "=="; break;
                    case 3: payload += "="; break;
                }
                var jsonBytes = Convert.FromBase64String(payload);
                using var doc = JsonDocument.Parse(jsonBytes);
                if (doc.RootElement.TryGetProperty("exp", out var expProp) && expProp.TryGetInt64(out var expUnix))
                {
                    var expUtc = DateTimeOffset.FromUnixTimeSeconds(expUnix).UtcDateTime;
                    return DateTime.UtcNow >= expUtc.AddMinutes(-1);
                }
            }
            catch { }
            return false;
        }

        private static string? ExtractJwtSubject(string? token)
        {
            if (string.IsNullOrWhiteSpace(token)) return null;
            try
            {
                var parts = token.Trim().Split('.');
                if (parts.Length < 2) return null;
                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                switch (payload.Length % 4)
                {
                    case 2: payload += "=="; break;
                    case 3: payload += "="; break;
                }
                var jsonBytes = Convert.FromBase64String(payload);
                using var doc = JsonDocument.Parse(jsonBytes);
                if (doc.RootElement.TryGetProperty("sub", out var subProp))
                {
                    return subProp.GetString();
                }
            }
            catch { }
            return null;
        }

        private static void EnsureTokenCacheLoadedFromDisk()
        {
            try
            {
                var baseDir = Directory.GetCurrentDirectory();
                var candidatePaths = new[]
                {
                    GetTokenStoreFilePath(),
                    Path.Combine(baseDir, "InvoiceManager", "App_Data", "gdt_tokens.json")
                };

                lock (_tokenFileLock)
                {
                    foreach (var filePath in candidatePaths)
                    {
                        if (!File.Exists(filePath)) continue;
                        var json = File.ReadAllText(filePath, Encoding.UTF8);
                        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                        if (dict != null)
                        {
                            foreach (var kv in dict)
                            {
                                if (!string.IsNullOrWhiteSpace(kv.Value) && !IsJwtExpired(kv.Value))
                                {
                                    var cleanToken = kv.Value.Trim();
                                    _tokenCache[kv.Key] = cleanToken;
                                    var sub = ExtractJwtSubject(cleanToken);
                                    if (!string.IsNullOrWhiteSpace(sub))
                                    {
                                        _tokenCache[$"mst:{sub}"] = cleanToken;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private static void PersistTokenCacheToDisk()
        {
            try
            {
                var filePath = GetTokenStoreFilePath();
                lock (_tokenFileLock)
                {
                    var dict = new Dictionary<string, string>(_tokenCache, StringComparer.OrdinalIgnoreCase);
                    var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(filePath, json, Encoding.UTF8);
                }
            }
            catch { }
        }

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
            if (client.BaseAddress == null)
            {
                var baseUrl = _options.BaseUrl.TrimEnd('/') + "/";
                client.BaseAddress = new Uri(baseUrl);
            }
            client.DefaultRequestHeaders.Clear();
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

        private async Task<string?> RunAutoCaptchaSolverAsync(string args, CancellationToken ct)
        {
            try
            {
                var scriptPath = Path.Combine(Directory.GetCurrentDirectory(), "tools", "auto_captcha_solver.py");
                if (!File.Exists(scriptPath))
                {
                    scriptPath = Path.Combine(AppContext.BaseDirectory, "tools", "auto_captcha_solver.py");
                }
                if (!File.Exists(scriptPath))
                {
                    var parentDir = Directory.GetParent(Directory.GetCurrentDirectory())?.FullName;
                    if (!string.IsNullOrEmpty(parentDir))
                    {
                        scriptPath = Path.Combine(parentDir, "tools", "auto_captcha_solver.py");
                    }
                }

                if (!File.Exists(scriptPath))
                {
                    _logger.LogWarning("Không tìm thấy auto_captcha_solver.py tại: {Path}", scriptPath);
                    return null;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = "python",
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

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                await process.WaitForExitAsync(linkedCts.Token);
                var output = await stdoutTask;
                var stderr = await stderrTask;

                if (!string.IsNullOrEmpty(stderr))
                {
                    _logger.LogDebug("AutoCaptchaSolver stderr: {Err}", stderr);
                }

                return output?.Trim();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lỗi khi chạy AutoCaptchaSolver: {Args}", args);
                return null;
            }
        }

        public async Task<CaptchaChallenge?> GetCaptchaAsync(CancellationToken ct = default)
        {
            // 1. Ưu tiên lấy trực tiếp siêu tốc qua HttpClient (~150ms - 300ms)
            try
            {
                using var ctsDirect = CancellationTokenSource.CreateLinkedTokenSource(ct);
                ctsDirect.CancelAfter(TimeSpan.FromSeconds(5));

                var client = CreateClient();
                var endpoint = _options.CaptchaEndpoint.TrimStart('/');
                var response = await client.GetAsync(endpoint, ctsDirect.Token);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync(ctsDirect.Token);
                    using var doc = JsonDocument.Parse(content);
                    var root = doc.RootElement;

                    string key = string.Empty;
                    string rawImage = string.Empty;

                    if (root.TryGetProperty("key", out var keyProp)) key = keyProp.GetString() ?? string.Empty;
                    else if (root.TryGetProperty("ckey", out var ckeyProp)) key = ckeyProp.GetString() ?? string.Empty;

                    if (root.TryGetProperty("content", out var contentProp)) rawImage = contentProp.GetString() ?? string.Empty;
                    else if (root.TryGetProperty("image", out var imgProp)) rawImage = imgProp.GetString() ?? string.Empty;
                    else if (root.TryGetProperty("svg", out var svgProp)) rawImage = svgProp.GetString() ?? string.Empty;

                    if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(rawImage))
                    {
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

                        string? autoSolvedCode = null;
                        if (isSvg && !string.IsNullOrEmpty(rawImage))
                        {
                            try
                            {
                                var tmp = Path.Combine(Path.GetTempPath(), $"cap_{Guid.NewGuid():N}.svg");
                                await File.WriteAllTextAsync(tmp, rawImage, ct);
                                var solvedOut = await RunAutoCaptchaSolverAsync($"solve-svg \"{tmp}\"", ct);
                                try { File.Delete(tmp); } catch { }
                                if (!string.IsNullOrEmpty(solvedOut))
                                {
                                    using var dDoc = JsonDocument.Parse(solvedOut);
                                    if (dDoc.RootElement.TryGetProperty("code", out var cp))
                                    {
                                        autoSolvedCode = cp.GetString();
                                    }
                                }
                            }
                            catch { }
                        }

                        return new CaptchaChallenge
                        {
                            Key = key,
                            ImageBase64 = formattedBase64,
                            IsSvg = isSvg,
                            SvgContent = isSvg ? rawImage : null,
                            AutoSolvedCode = autoSolvedCode
                        };
                    }
                }
                else
                {
                    _logger.LogWarning("HttpClient lấy captcha trả về mã {Code}, chuyển sang Headless Agent...", response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lấy Captcha trực tiếp qua HttpClient thất bại hoặc hết hạn, chuyển sang Headless Chrome Agent.");
            }

            // 2. Fallback sang Headless Chrome Agent nếu HttpClient bị chặn hoặc lỗi
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

                            string? autoSolvedCode = null;
                            if (isSvg && !string.IsNullOrEmpty(content))
                            {
                                try
                                {
                                    var tmp = Path.Combine(Path.GetTempPath(), $"cap_{Guid.NewGuid():N}.svg");
                                    await File.WriteAllTextAsync(tmp, content, ct);
                                    var solvedOut = await RunAutoCaptchaSolverAsync($"solve-svg \"{tmp}\"", ct);
                                    try { File.Delete(tmp); } catch { }
                                    if (!string.IsNullOrEmpty(solvedOut))
                                    {
                                        using var dDoc = JsonDocument.Parse(solvedOut);
                                        if (dDoc.RootElement.TryGetProperty("code", out var codeProp))
                                        {
                                            autoSolvedCode = codeProp.GetString();
                                        }
                                    }
                                }
                                catch { }
                            }

                            return new CaptchaChallenge
                            {
                                Key = key,
                                ImageBase64 = formatted,
                                IsSvg = isSvg,
                                SvgContent = isSvg ? content : null,
                                AutoSolvedCode = autoSolvedCode
                            };
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lỗi khi lấy captcha qua Headless Agent.");
            }

            return null;
        }

        public async Task<LoginResult> LoginAsync(ProviderCredentials cred, CancellationToken ct = default)
        {
            if (!string.IsNullOrWhiteSpace(cred.BearerToken))
            {
                _logger.LogInformation("Sử dụng Bearer Token trực tiếp từ phiên đăng nhập Cổng Thuế.");
                CacheToken(cred.Username ?? cred.TaxCode, cred.BearerToken);
                return LoginResult.Ok(cred.BearerToken.Trim(), DateTime.UtcNow.AddHours(4));
            }

            // Nếu gọi tự động trong nền (không có CaptchaCode), ưu tiên sử dụng Token phiên làm việc đang còn hạn trong cache
            if (string.IsNullOrWhiteSpace(cred.CaptchaCode))
            {
                var cachedTok = GetCachedToken(cred.Username ?? cred.TaxCode);
                if (!string.IsNullOrWhiteSpace(cachedTok))
                {
                    _logger.LogInformation("Tái sử dụng Bearer Token Cổng Thuế đang còn hạn cho MST: {Mst}", cred.Username ?? cred.TaxCode);
                    return LoginResult.Ok(cachedTok, DateTime.UtcNow.AddHours(4));
                }

                // TỰ ĐỘNG GIẢI CAPTCHA BẰNG AI OCR VÀ ĐĂNG NHẬP NGẦM (KHÔNG CẦN NGƯỜI DÙNG NHẬP)
                if (!string.IsNullOrWhiteSpace(cred.Password))
                {
                    _logger.LogInformation("Khởi chạy bộ giải tự động Captcha Cổng Thuế bằng AI OCR cho MST: {Mst}...", cred.Username);
                    var safeUser = (cred.Username ?? cred.TaxCode).Trim().Replace("\"", "\\\"");
                    var safePass = cred.Password.Replace("\"", "\\\"");
                    var autoLoginArgs = $"auto-login --username \"{safeUser}\" --password \"{safePass}\"";
                    
                    var autoRes = await RunHeadlessAgentAsync(autoLoginArgs, ct);
                    if (string.IsNullOrEmpty(autoRes))
                    {
                        autoRes = await RunAutoCaptchaSolverAsync(autoLoginArgs, ct);
                    }

                    if (!string.IsNullOrEmpty(autoRes))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(autoRes);
                            if (doc.RootElement.TryGetProperty("success", out var succ) && succ.GetBoolean())
                            {
                                var tok = doc.RootElement.TryGetProperty("token", out var tp) ? tp.GetString() : null;
                                if (!string.IsNullOrEmpty(tok))
                                {
                                    _logger.LogInformation("Tự động giải Captcha và đăng nhập Cổng Thuế THÀNH CÔNG cho MST: {Mst}!", cred.Username);
                                    CacheToken(cred.Username, tok);
                                    return LoginResult.Ok(tok, DateTime.UtcNow.AddHours(4));
                                }
                            }
                            else if (doc.RootElement.TryGetProperty("message", out var msgProp))
                            {
                                var errMsg = msgProp.GetString();
                                if (!string.IsNullOrEmpty(errMsg))
                                {
                                    _logger.LogWarning("Tự động đăng nhập Cổng Thuế thất bại: {Msg}", errMsg);
                                    return LoginResult.Fail(errMsg, false);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Lỗi phân tích kết quả auto-login: {Output}", autoRes);
                        }
                    }

                    return LoginResult.Fail("Tự động giải mã Captcha và kết nối Cổng Thuế thất bại. Vui lòng kiểm tra lại mật khẩu hoặc trạng thái tài khoản thuế.", false);
                }
                else
                {
                    return LoginResult.Fail("Doanh nghiệp chưa cấu hình mật khẩu Cổng Thuế. Vui lòng thiết lập mật khẩu.", false);
                }
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
                                CacheToken(cred.Username, token);
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

                CacheToken(cred.Username, tokenDirect);
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
                var symbol = GetJsonStringValue(item, "khhdon")?.Trim() ?? "";
                var rawNumber = GetJsonStringValue(item, "shdon")?.Trim() ?? "";
                if (string.IsNullOrEmpty(symbol) || string.IsNullOrEmpty(rawNumber)) return null;

                // Chuẩn hóa ký hiệu: bỏ ký tự mẫu số ở đầu nếu dài >= 7 (vd 1C26TAA -> C26TAA)
                if (symbol.Length >= 7 && char.IsDigit(symbol[0]))
                {
                    symbol = symbol.Substring(1);
                }

                // Chuyển đổi ngày lập sang giờ Việt Nam (UTC+7) chuẩn xác, tránh bị lùi 1 ngày
                var dateStr = GetJsonStringValue(item, "tdlap");
                DateTime issueDate = StandardTctParser.ParseVietnamInvoiceDate(dateStr, DateTime.Today);

                var id = GetJsonStringValue(item, "id");
                var khmshdon = GetJsonStringValue(item, "khmshdon");
                var sellerMst = GetJsonStringValue(item, "nbmst");
                var sellerName = GetJsonStringValue(item, "nbten");
                var sellerAddress = GetJsonStringValue(item, "nbdchi");
                var buyerMst = GetJsonStringValue(item, "nmdmst");
                if (string.IsNullOrEmpty(buyerMst)) buyerMst = GetJsonStringValue(item, "nmmst");
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

                var rawKhhdon = GetJsonStringValue(item, "khhdon")?.Trim() ?? "";
                bool isCashRegister = (rawKhhdon.Length >= 4 && char.ToUpperInvariant(rawKhhdon[3]) == 'M')
                                      || (rawKhhdon.Length >= 2 && char.ToUpperInvariant(rawKhhdon[1]) == 'M')
                                      || (symbol.Length >= 4 && char.ToUpperInvariant(symbol[3]) == 'M')
                                      || (symbol.Length >= 2 && char.ToUpperInvariant(symbol[1]) == 'M');

                var mcqt = GetJsonStringValue(item, "mcqt");

                // Thuộc tính mở rộng chuẩn hóa
                var signedStr = GetJsonStringValue(item, "tdky");
                if (string.IsNullOrEmpty(signedStr)) signedStr = GetJsonStringValue(item, "nbcks");
                if (string.IsNullOrEmpty(signedStr)) signedStr = GetJsonStringValue(item, "ngayky");
                DateTime? signedDate = !string.IsNullOrEmpty(signedStr) 
                    ? StandardTctParser.ParseVietnamInvoiceDate(signedStr, issueDate) 
                    : issueDate;

                var buyerCccd = GetJsonStringValue(item, "nmcccd");
                if (string.IsNullOrEmpty(buyerCccd)) buyerCccd = GetJsonStringValue(item, "nmcmnd");
                if (string.IsNullOrEmpty(buyerCccd)) buyerCccd = GetJsonStringValue(item, "cccd");

                var dvtTe = GetJsonStringValue(item, "dvtte");
                if (string.IsNullOrEmpty(dvtTe)) dvtTe = "VND";

                var tGia = GetJsonDecimalValue(item, "tgia");
                if (tGia <= 0) tGia = 1;

                var htTToan = GetJsonStringValue(item, "htttoan");
                if (string.IsNullOrEmpty(htTToan)) htTToan = "TM/CK";

                var tChat = GetJsonStringValue(item, "tchat");
                var invoiceProperty = tChat switch
                {
                    "1" => "Hàng hoá/dịch vụ",
                    "2" => "Hóa đơn thay thế",
                    "3" => "Hóa đơn điều chỉnh",
                    "4" => "Hóa đơn đã bị xóa bỏ/hủy",
                    "5" => "Hóa đơn chiết khấu",
                    "6" => "Hóa đơn điều chỉnh chiết khấu",
                    _ => string.IsNullOrWhiteSpace(tChat) ? "Hàng hoá/dịch vụ" : tChat
                };

                var hsgcma = GetJsonStringValue(item, "hsgcma");
                if (string.IsNullOrWhiteSpace(hsgcma))
                {
                    hsgcma = !string.IsNullOrWhiteSpace(mcqt) ? "Đã cấp mã hoá đơn" : "CQT đã tiếp nhận";
                }

                var bsx = GetJsonStringValue(item, "bsx");
                if (string.IsNullOrEmpty(bsx)) bsx = GetJsonStringValue(item, "bsxe");
                if (string.IsNullOrEmpty(bsx)) bsx = GetJsonStringValue(item, "bienso");

                var replacedNote = GetJsonStringValue(item, "shdtthe");
                if (string.IsNullOrEmpty(replacedNote)) replacedNote = GetJsonStringValue(item, "gchu");
                if (string.IsNullOrEmpty(replacedNote)) replacedNote = GetJsonStringValue(item, "lydo");

                var lookupCode = GetJsonStringValue(item, "mattchdon");
                if (string.IsNullOrEmpty(lookupCode)) lookupCode = GetJsonStringValue(item, "matracuu");
                if (string.IsNullOrEmpty(lookupCode)) lookupCode = GetJsonStringValue(item, "mtcdon");

                var sellerWebsite = GetJsonStringValue(item, "wbnguoiban");
                if (string.IsNullOrEmpty(sellerWebsite)) sellerWebsite = GetJsonStringValue(item, "website");

                var lookupUrl = GetJsonStringValue(item, "urlhd");
                if (string.IsNullOrEmpty(lookupUrl)) lookupUrl = GetJsonStringValue(item, "urltra");
                if (string.IsNullOrEmpty(lookupUrl)) lookupUrl = GetJsonStringValue(item, "url");

                var msttcgp = GetJsonStringValue(item, "msttcgp");
                if (string.IsNullOrEmpty(msttcgp)) msttcgp = GetJsonStringValue(item, "mstgp");

                lookupUrl = StandardTctParser.ResolveLookupUrl(msttcgp, sellerMst, sellerWebsite, lookupUrl);
                if (string.IsNullOrEmpty(lookupCode))
                {
                    lookupCode = !string.IsNullOrWhiteSpace(mcqt) ? mcqt : normalizedNumber;
                }

                var discountAmount = GetJsonDecimalValue(item, "ttcktmai");
                if (discountAmount == 0) discountAmount = GetJsonDecimalValue(item, "stckhau");

                var feeAmount = GetJsonDecimalValue(item, "ttphi");
                if (feeAmount == 0) feeAmount = GetJsonDecimalValue(item, "ttchiphi");

                var amountBeforeTax = GetJsonDecimalValue(item, "tgtcthue");
                var taxAmount = GetJsonDecimalValue(item, "tgtthue");
                var totalAmount = GetJsonDecimalValue(item, "tgtttbso");
                if (totalAmount == 0) totalAmount = amountBeforeTax + taxAmount;

                var amountBeforeTaxVnd = dvtTe.Equals("VND", StringComparison.OrdinalIgnoreCase) ? amountBeforeTax : Math.Round(amountBeforeTax * tGia, 0);
                var taxAmountVnd = dvtTe.Equals("VND", StringComparison.OrdinalIgnoreCase) ? taxAmount : Math.Round(taxAmount * tGia, 0);
                var totalAmountVnd = dvtTe.Equals("VND", StringComparison.OrdinalIgnoreCase) ? totalAmount : Math.Round(totalAmount * tGia, 0);

                return new RemoteInvoice
                {
                    ProviderKey = !string.IsNullOrEmpty(id) ? id : $"{sellerMst}_{symbol}_{normalizedNumber}",
                    TemplateCode = !string.IsNullOrEmpty(khmshdon) ? khmshdon : "1",
                    InvoiceSymbol = symbol,
                    InvoiceNumber = normalizedNumber,
                    RawInvoiceNumber = rawNumber,
                    IssueDate = issueDate,
                    SignedDate = signedDate,
                    SellerTaxCode = sellerMst,
                    SellerName = sellerName,
                    SellerAddress = sellerAddress,
                    SellerWebsite = sellerWebsite,
                    BuyerTaxCode = buyerMst,
                    BuyerCitizenId = buyerCccd,
                    BuyerName = buyerName,
                    BuyerAddress = buyerAddress,
                    AmountBeforeTax = amountBeforeTax,
                    TaxAmount = taxAmount,
                    TotalAmount = totalAmount,
                    Currency = dvtTe,
                    ExchangeRate = tGia,
                    PaymentMethod = htTToan,
                    InvoiceProperty = invoiceProperty,
                    VerificationResult = hsgcma,
                    VehicleNumber = bsx,
                    ReplacedOrAdjustedNote = replacedNote,
                    LookupCode = lookupCode,
                    LookupUrl = lookupUrl,
                    DiscountAmount = discountAmount,
                    FeeAmount = feeAmount,
                    AmountBeforeTaxVnd = amountBeforeTaxVnd,
                    TaxAmountVnd = taxAmountVnd,
                    TotalAmountVnd = totalAmountVnd,
                    InvoiceType = defaultType,
                    CqtCode = mcqt,
                    Status = MapGdtStatus(GetJsonStringValue(item, "ttxly")),
                    IsCashRegister = isCashRegister
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

        private static (string Nbmst, string Khmshdon, string Khhdon, string Shdon, bool IsCashRegister) ResolveGdtInvoiceParams(RemoteInvoice inv)
        {
            var nbmst = (inv.SellerTaxCode ?? string.Empty).Trim();
            var rawSym = (inv.InvoiceSymbol ?? string.Empty).Trim();
            var khmshdon = !string.IsNullOrWhiteSpace(inv.TemplateCode) ? inv.TemplateCode.Trim() : "1";
            var khhdon = rawSym;

            if (!string.IsNullOrEmpty(rawSym) && char.IsDigit(rawSym[0]) && rawSym.Length >= 6)
            {
                khmshdon = rawSym[0].ToString();
                khhdon = rawSym.Substring(1);
            }

            var shdonRaw = !string.IsNullOrWhiteSpace(inv.RawInvoiceNumber) ? inv.RawInvoiceNumber.Trim() : (inv.InvoiceNumber ?? string.Empty).Trim();
            var shdon = long.TryParse(shdonRaw, out var numVal) ? numVal.ToString() : shdonRaw.TrimStart('0');
            if (string.IsNullOrEmpty(shdon)) shdon = shdonRaw;

            bool isCashRegister = khhdon.Length >= 4 && char.ToUpperInvariant(khhdon[3]) == 'M';
            return (nbmst, khmshdon, khhdon, shdon, isCashRegister);
        }

        public static bool IsValidPdfSignature(byte[]? bytes)
        {
            if (bytes == null || bytes.Length < 5) return false;
            // Ký hiệu chuẩn %PDF- (0x25 0x50 0x44 0x46 0x2D)
            if (bytes[0] != 0x25 || bytes[1] != 0x50 || bytes[2] != 0x44 || bytes[3] != 0x46 || bytes[4] != 0x2D)
            {
                return false;
            }
            return !IsInvalidOrErrorPayload(bytes);
        }

        public async Task<OriginalPdfFetchResult> FetchOriginalPdfAsync(RemoteInvoice inv, string token, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return new OriginalPdfFetchResult
                {
                    Success = false,
                    HttpStatusCode = 401,
                    ErrorCode = "NOT_LOGGED_IN",
                    ErrorMessage = "Chưa đăng nhập tài khoản thuế hoặc chưa có Token xác thực Tổng cục Thuế."
                };
            }

            if (IsJwtExpired(token))
            {
                return new OriginalPdfFetchResult
                {
                    Success = false,
                    HttpStatusCode = 401,
                    ErrorCode = "TOKEN_EXPIRED",
                    ErrorMessage = "Token phiên làm việc Tổng cục Thuế đã hết hạn. Vui lòng đăng nhập lại tài khoản thuế."
                };
            }

            var (nbmst, khmshdon, khhdon, shdon, isCashRegister) = ResolveGdtInvoiceParams(inv);
            var shdonPrimary = shdon;
            if (string.IsNullOrWhiteSpace(nbmst) || string.IsNullOrWhiteSpace(khhdon) || string.IsNullOrWhiteSpace(shdon))
            {
                return new OriginalPdfFetchResult
                {
                    Success = false,
                    HttpStatusCode = 400,
                    ErrorCode = "INVALID_INVOICE_PARAMS",
                    ErrorMessage = "Sai hoặc thiếu thông tin mã số thuế người bán, ký hiệu hóa đơn hoặc số hóa đơn."
                };
            }

            await ApplyRateLimitAsync(ct);
            var client = CreateClient(token);
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

            // 1. Gọi trực tiếp endpoint export-pdf đã cấu hình (/api/query/invoices/export-pdf)
            var pdfEndpoint = (_options.ExportPdfEndpoint ?? "/api/query/invoices/export-pdf").TrimStart('/');
            var pdfQueryUrl = $"{pdfEndpoint}?nbmst={Uri.EscapeDataString(nbmst)}&khhdon={Uri.EscapeDataString(khhdon)}&shdon={Uri.EscapeDataString(shdon)}&khmshdon={Uri.EscapeDataString(khmshdon)}";
            if (!string.IsNullOrWhiteSpace(inv.ProviderKey) && Guid.TryParse(inv.ProviderKey, out _))
            {
                pdfQueryUrl += $"&id={Uri.EscapeDataString(inv.ProviderKey)}";
            }

            int pdfStatusCode = 0;
            string pdfErrorDetail = string.Empty;

            try
            {
                client.DefaultRequestHeaders.Remove("request-id");
                client.DefaultRequestHeaders.TryAddWithoutValidation("request-id", Guid.NewGuid().ToString());
                client.DefaultRequestHeaders.Remove("Action");
                client.DefaultRequestHeaders.TryAddWithoutValidation("Action", Uri.EscapeDataString("Xuất pdf (hóa đơn)"));

                using var ctsPdf = CancellationTokenSource.CreateLinkedTokenSource(ct);
                ctsPdf.CancelAfter(TimeSpan.FromSeconds(12));

                _logger.LogInformation("Gọi API Tổng cục Thuế lấy PDF gốc: GET {BaseUrl}/{Url}", _options.BaseUrl.TrimEnd('/'), pdfQueryUrl);
                var respPdf = await client.GetAsync(pdfQueryUrl, ctsPdf.Token);
                pdfStatusCode = (int)respPdf.StatusCode;
                var pdfBodyBytes = await respPdf.Content.ReadAsByteArrayAsync(ct);

                if (respPdf.StatusCode == HttpStatusCode.Unauthorized)
                {
                    InvalidateCachedToken(nbmst);
                    return new OriginalPdfFetchResult
                    {
                        Success = false,
                        HttpStatusCode = 401,
                        ErrorCode = "TOKEN_EXPIRED",
                        VerifiedEndpoint = $"GET /{pdfQueryUrl}",
                        ErrorMessage = "Token xác thực Tổng cục Thuế đã hết hạn hoặc không hợp lệ (HTTP 401). Vui lòng đăng nhập lại tài khoản thuế."
                    };
                }

                if (respPdf.StatusCode == HttpStatusCode.Forbidden)
                {
                    var bodyStr = pdfBodyBytes != null && pdfBodyBytes.Length > 0
                        ? Encoding.UTF8.GetString(pdfBodyBytes, 0, Math.Min(pdfBodyBytes.Length, 500))
                        : "HTTP 403 Forbidden";
                    return new OriginalPdfFetchResult
                    {
                        Success = false,
                        HttpStatusCode = 403,
                        ErrorCode = "GDT_FORBIDDEN",
                        VerifiedEndpoint = $"GET /{pdfQueryUrl}",
                        ErrorMessage = $"Cổng Thuế từ chối truy cập (HTTP 403): {ExtractGdtErrorMessage(bodyStr)}"
                    };
                }

                if (respPdf.IsSuccessStatusCode && pdfBodyBytes != null && pdfBodyBytes.Length > 0)
                {
                    // Kiểm tra chữ ký %PDF-
                    if (IsValidPdfSignature(pdfBodyBytes))
                    {
                        var normNum = long.TryParse(shdon, out var nv) ? nv.ToString("D7") : shdon;
                        return new OriginalPdfFetchResult
                        {
                            Success = true,
                            PdfBytes = pdfBodyBytes,
                            FileName = $"GDT_{khmshdon}{khhdon}_{normNum}.pdf",
                            Sha256Hash = ComputeSha256(pdfBodyBytes),
                            Source = "GDT-ExportPdf",
                            HttpStatusCode = 200,
                            VerifiedEndpoint = $"GET /{pdfQueryUrl}"
                        };
                    }

                    // Nếu trả về gói ZIP trong export-pdf thì kiểm tra bên trong ZIP có file .pdf hợp lệ không
                    if (pdfBodyBytes.Length > 4 && pdfBodyBytes[0] == 0x50 && pdfBodyBytes[1] == 0x4B && pdfBodyBytes[2] == 0x03 && pdfBodyBytes[3] == 0x04)
                    {
                        var extractedPdf = TryExtractValidPdfFromZip(pdfBodyBytes);
                        if (extractedPdf != null)
                        {
                            var normNum = long.TryParse(shdon, out var nv) ? nv.ToString("D7") : shdon;
                            return new OriginalPdfFetchResult
                            {
                                Success = true,
                                PdfBytes = extractedPdf,
                                FileName = $"GDT_{khmshdon}{khhdon}_{normNum}.pdf",
                                Sha256Hash = ComputeSha256(extractedPdf),
                                Source = "GDT-ExportPdfZip",
                                HttpStatusCode = 200,
                                VerifiedEndpoint = $"GET /{pdfQueryUrl}"
                            };
                        }
                    }

                    // Nếu trả về HTML, JSON, XML hoặc dữ liệu lỗi -> Từ chối, không lưu file giả
                    pdfErrorDetail = $"Phản hồi HTTP 200 nhưng dữ liệu không phải PDF hợp lệ (Thiếu chữ ký %PDF-, Content-Type: {respPdf.Content.Headers.ContentType?.MediaType ?? "unknown"})";
                    return new OriginalPdfFetchResult
                    {
                        Success = false,
                        HttpStatusCode = 422,
                        ErrorCode = "INVALID_PDF_FORMAT",
                        VerifiedEndpoint = $"GET /{pdfQueryUrl}",
                        ErrorMessage = $"API Tổng cục Thuế trả về dữ liệu không phải file PDF hợp lệ ({pdfErrorDetail}). Hệ thống từ chối lưu và hiển thị file lỗi."
                    };
                }
                else
                {
                    var errText = pdfBodyBytes != null && pdfBodyBytes.Length > 0
                        ? Encoding.UTF8.GetString(pdfBodyBytes, 0, Math.Min(pdfBodyBytes.Length, 500))
                        : $"HTTP {pdfStatusCode}";
                    pdfErrorDetail = $"HTTP {pdfStatusCode}: {ExtractGdtErrorMessage(errText)}";
                }
            }
            catch (TaskCanceledException)
            {
                return new OriginalPdfFetchResult
                {
                    Success = false,
                    HttpStatusCode = 504,
                    ErrorCode = "GDT_TIMEOUT",
                    VerifiedEndpoint = $"GET /{pdfQueryUrl}",
                    ErrorMessage = "API Tổng cục Thuế không phản hồi (Quá thời gian chờ kết nối)."
                };
            }
            catch (HttpRequestException exHttp)
            {
                return new OriginalPdfFetchResult
                {
                    Success = false,
                    HttpStatusCode = 502,
                    ErrorCode = "GDT_UNREACHABLE",
                    VerifiedEndpoint = $"GET /{pdfQueryUrl}",
                    ErrorMessage = $"Không thể kết nối tới API Tổng cục Thuế: {exHttp.Message}"
                };
            }

            // 2. Nếu /api/query/invoices/export-pdf trả về 404/lỗi, kiểm tra gói hồ sơ gốc từ /api/query/invoices/export-xml
            var packageResult = await DownloadAllInvoiceFilesAsync(inv, token, ct);
            if (packageResult.PdfFile != null && IsValidPdfSignature(packageResult.PdfFile.Content))
            {
                return new OriginalPdfFetchResult
                {
                    Success = true,
                    PdfBytes = packageResult.PdfFile.Content,
                    FileName = packageResult.PdfFile.FileName,
                    Sha256Hash = packageResult.PdfFile.Sha256Hash,
                    Source = packageResult.PdfFile.Source,
                    HttpStatusCode = 200,
                    VerifiedEndpoint = $"GET /{pdfQueryUrl} -> PackageZip",
                    CompanionPackage = packageResult
                };
            }

            // 2a. Thử tải trực tiếp PDF gốc của nhà cung cấp phần mềm (ví dụ MISA meInvoice) qua TransactionID trong XML
            if (packageResult.XmlFile != null && packageResult.XmlFile.Content?.Length > 0)
            {
                var vendorPdf = await TryDownloadVendorPdfFromXmlAsync(packageResult.XmlFile.Content, ct);
                if (vendorPdf != null && IsValidPdfSignature(vendorPdf))
                {
                    var normNum = long.TryParse(shdonPrimary, out var nv) ? nv.ToString("D7") : inv.InvoiceNumber;
                    return new OriginalPdfFetchResult
                    {
                        Success = true,
                        PdfBytes = vendorPdf,
                        FileName = $"HD_{khmshdon}{khhdon}_{normNum}_GocNCC.pdf",
                        Sha256Hash = ComputeSha256(vendorPdf),
                        Source = "Supplier-DirectPdf",
                        HttpStatusCode = 200,
                        VerifiedEndpoint = "Supplier Portal Direct",
                        CompanionPackage = packageResult
                    };
                }
            }

            // 2b. Nếu gói ZIP gốc của Tổng cục Thuế chứa bộ bản thể hiện chính thức (invoice.html + details.js),
            // kết xuất trực tiếp sang PDF gốc của Tổng cục Thuế bằng Headless Browser
            if (packageResult.ZipFile != null && packageResult.ZipFile.Content?.Length > 0)
            {
                var renderedBytes = await TryRenderOfficialGdtZipPackageToPdfAsync(packageResult.ZipFile.Content, ct);
                if (renderedBytes != null && IsValidPdfSignature(renderedBytes))
                {
                    var normNum = long.TryParse(shdonPrimary, out var nv) ? nv.ToString("D7") : inv.InvoiceNumber;
                    return new OriginalPdfFetchResult
                    {
                        Success = true,
                        PdfBytes = renderedBytes,
                        FileName = $"HD_{khmshdon}{khhdon}_{normNum}_Official.pdf",
                        Sha256Hash = ComputeSha256(renderedBytes),
                        Source = "GDT-OfficialPackagePdf",
                        HttpStatusCode = 200,
                        VerifiedEndpoint = $"GET /{pdfQueryUrl} -> OfficialPackageRender",
                        CompanionPackage = packageResult
                    };
                }
            }

            if (packageResult.Success)
            {
                var fileListStr = string.Join(", ", packageResult.Files.ConvertAll(f => f.FileName));
                return new OriginalPdfFetchResult
                {
                    Success = false,
                    HttpStatusCode = 404,
                    ErrorCode = "PDF_NOT_PROVIDED_BY_GDT",
                    VerifiedEndpoint = $"GET /{pdfQueryUrl}",
                    CompanionPackage = packageResult,
                    ErrorMessage = $"Tổng cục Thuế không cung cấp file PDF gốc cho hóa đơn {khmshdon}{khhdon}-{inv.InvoiceNumber}. " +
                                   $"Endpoint /{pdfEndpoint} trả về {pdfErrorDetail}; " +
                                   $"gói dữ liệu gốc của Tổng cục Thuế chỉ cung cấp ({fileListStr}), không có tệp PDF."
                };
            }

            return new OriginalPdfFetchResult
            {
                Success = false,
                HttpStatusCode = pdfStatusCode == 404 ? 404 : (pdfStatusCode > 0 ? pdfStatusCode : 502),
                ErrorCode = "INVOICE_OR_PDF_NOT_FOUND",
                VerifiedEndpoint = $"GET /{pdfQueryUrl}",
                ErrorMessage = !string.IsNullOrWhiteSpace(packageResult.ErrorMessage)
                    ? $"Không lấy được PDF gốc từ Tổng cục Thuế ({pdfErrorDetail}). Chi tiết: {packageResult.ErrorMessage}"
                    : $"Không tìm thấy file PDF gốc trên Tổng cục Thuế ({pdfErrorDetail}). Vui lòng kiểm tra lại mã số thuế ({nbmst}), ký hiệu ({khmshdon}{khhdon}) hoặc số hóa đơn ({shdon})."
            };
        }

        private static byte[]? TryExtractValidPdfFromZip(byte[] zipBytes)
        {
            try
            {
                using var zipMs = new MemoryStream(zipBytes);
                using var archive = new ZipArchive(zipMs, ZipArchiveMode.Read);
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.Name)) continue;
                    if (!entry.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) continue;
                    using var entryStream = entry.Open();
                    using var outMs = new MemoryStream();
                    entryStream.CopyTo(outMs);
                    var bytes = outMs.ToArray();
                    if (IsValidPdfSignature(bytes))
                    {
                        return bytes;
                    }
                }
            }
            catch { }
            return null;
        }

        private static string ExtractGdtErrorMessage(string rawBody)
        {
            if (string.IsNullOrWhiteSpace(rawBody)) return "Không có phản hồi chi tiết";
            if (rawBody.Contains("Request Rejected", StringComparison.OrdinalIgnoreCase))
            {
                return "Yêu cầu bị Tường lửa (F5 WAF) của Tổng cục Thuế chặn";
            }
            try
            {
                using var doc = JsonDocument.Parse(rawBody);
                var root = doc.RootElement;
                if (root.TryGetProperty("message", out var msgProp) && msgProp.ValueKind == JsonValueKind.String)
                {
                    return msgProp.GetString() ?? rawBody;
                }
                if (root.TryGetProperty("error", out var errProp) && errProp.ValueKind == JsonValueKind.String)
                {
                    var path = root.TryGetProperty("path", out var pProp) ? pProp.GetString() : "";
                    return $"{errProp.GetString()} {(string.IsNullOrEmpty(path) ? "" : $"({path})")}".Trim();
                }
            }
            catch { }
            return rawBody.Length > 200 ? rawBody.Substring(0, 200) + "..." : rawBody;
        }

        public async Task<DownloadInvoiceResult> DownloadAllInvoiceFilesAsync(RemoteInvoice inv, string token, CancellationToken ct = default)
        {
            var result = new DownloadInvoiceResult();
            await ApplyRateLimitAsync(ct);
            var client = CreateClient(token);

            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

            var (nbmst, khmshdon, khhdon, shdonPrimary, isCashRegister) = ResolveGdtInvoiceParams(inv);
            var defaultEndpoint = isCashRegister ? "api/sco-query/invoices/export-xml" : _options.ExportXmlEndpoint.TrimStart('/');

            // Xây dựng danh sách tham số truy vấn theo đúng chuẩn API Tổng cục Thuế:
            // Tham số chuẩn: nbmst, khhdon (6 ký tự, không gồm số mẫu số đầu), shdon (số nguyên không đệm 0), khmshdon (1, 2...)
            var queryAttempts = new List<(string Endpoint, string Khmshdon, string Khhdon, string Shdon)>
            {
                (defaultEndpoint, khmshdon, khhdon, shdonPrimary)
            };

            if (khmshdon != "2")
            {
                queryAttempts.Add((defaultEndpoint, "2", khhdon, shdonPrimary));
            }
            if (long.TryParse(shdonPrimary, out var numVal))
            {
                var d7 = numVal.ToString("D7");
                if (d7 != shdonPrimary)
                {
                    queryAttempts.Add((defaultEndpoint, khmshdon, khhdon, d7));
                }
            }

            byte[]? rawBytes = null;
            string? lastError = null;

            foreach (var attempt in queryAttempts)
            {
                var tryUrl = $"{attempt.Endpoint}?nbmst={Uri.EscapeDataString(nbmst)}&khhdon={Uri.EscapeDataString(attempt.Khhdon)}&shdon={Uri.EscapeDataString(attempt.Shdon)}&khmshdon={Uri.EscapeDataString(attempt.Khmshdon)}";
                try
                {
                    client.DefaultRequestHeaders.Remove("request-id");
                    client.DefaultRequestHeaders.TryAddWithoutValidation("request-id", Guid.NewGuid().ToString());
                    client.DefaultRequestHeaders.Remove("Action");
                    client.DefaultRequestHeaders.TryAddWithoutValidation("Action", Uri.EscapeDataString("Xuất xml (hóa đơn)"));

                    using var ctsTry = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    ctsTry.CancelAfter(TimeSpan.FromSeconds(10));
                    var resp = await client.GetAsync(tryUrl, ctsTry.Token);
                    if (resp.IsSuccessStatusCode)
                    {
                        var b = await resp.Content.ReadAsByteArrayAsync(ct);
                        if (b != null && b.Length > 0 && !IsInvalidOrErrorPayload(b))
                        {
                            rawBytes = b;
                            break;
                        }
                    }
                    else
                    {
                        var errBody = await resp.Content.ReadAsStringAsync(ct);
                        lastError = $"HTTP {(int)resp.StatusCode}: {ExtractGdtErrorMessage(errBody)}";
                        if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                        {
                            break;
                        }
                    }
                }
                catch (Exception exTry)
                {
                    lastError = exTry.Message;
                    _logger.LogWarning("Thử tải tệp cho {Sym}-{Num} gặp ngoại lệ: {Msg}", attempt.Khhdon, attempt.Shdon, exTry.Message);
                }

                await ApplyRateLimitAsync(ct);
            }

            if (rawBytes == null || rawBytes.Length == 0)
            {
                result.ErrorMessage = lastError ?? "Tổng cục Thuế không trả về dữ liệu hồ sơ gốc.";
            }

            if (rawBytes != null && rawBytes.Length > 0 && !IsInvalidOrErrorPayload(rawBytes))
            {
                // Trường hợp A: File ZIP nén (PK\x03\x04)
                if (rawBytes.Length > 4 && rawBytes[0] == 0x50 && rawBytes[1] == 0x4B && rawBytes[2] == 0x03 && rawBytes[3] == 0x04)
                {
                    // Giữ nguyên gói ZIP gốc từ Cổng Thuế
                    result.Files.Add(new DownloadedInvoiceFile
                    {
                        FileName = $"GDT_{inv.InvoiceSymbol}_{inv.InvoiceNumber}_Package.zip",
                        FileExtension = ".zip",
                        ContentType = "application/zip",
                        Content = rawBytes,
                        Source = "GDT-PackageZip",
                        Sha256Hash = ComputeSha256(rawBytes)
                    });

                    // Bóc tách các file hóa đơn hợp lệ bên trong gói ZIP (.xml, .pdf)
                    try
                    {
                        using var zipMs = new MemoryStream(rawBytes);
                        using var archive = new ZipArchive(zipMs, ZipArchiveMode.Read);
                        foreach (var entry in archive.Entries)
                        {
                            if (string.IsNullOrWhiteSpace(entry.Name)) continue;
                            var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                            // Trích xuất file .xml, .pdf hoặc .html (bản thể hiện gốc của Tổng cục Thuế / nhà cung cấp)
                            if (ext != ".xml" && ext != ".pdf" && ext != ".html") continue;

                            using var entryStream = entry.Open();
                            using var outMs = new MemoryStream();
                            await entryStream.CopyToAsync(outMs, ct);
                            var entryBytes = outMs.ToArray();

                            if (entryBytes.Length == 0 || IsInvalidOrErrorPayload(entryBytes)) continue;
                            if (ext == ".pdf" && !IsValidPdfSignature(entryBytes)) continue;

                            string contentType = ext == ".pdf" ? "application/pdf" : (ext == ".html" ? "text/html" : "application/xml");

                            result.Files.Add(new DownloadedInvoiceFile
                            {
                                FileName = entry.Name,
                                FileExtension = ext,
                                ContentType = contentType,
                                Content = entryBytes,
                                Source = "GDT-ZipEntry",
                                Sha256Hash = ComputeSha256(entryBytes)
                            });
                        }
                    }
                    catch (Exception exZip)
                    {
                        _logger.LogWarning(exZip, "Lỗi khi giải nén các file trong gói ZIP của HĐ {Symbol}-{Number}", inv.InvoiceSymbol, shdonPrimary);
                    }
                }
                // Trường hợp B: File PDF trực tiếp (%PDF-)
                else if (IsValidPdfSignature(rawBytes))
                {
                    result.Files.Add(new DownloadedInvoiceFile
                    {
                        FileName = $"GDT_{inv.InvoiceSymbol}_{inv.InvoiceNumber}.pdf",
                        FileExtension = ".pdf",
                        ContentType = "application/pdf",
                        Content = rawBytes,
                        Source = "GDT-ExportXml",
                        Sha256Hash = ComputeSha256(rawBytes)
                    });
                }
                // Trường hợp C: File XML trực tiếp
                else
                {
                    result.Files.Add(new DownloadedInvoiceFile
                    {
                        FileName = $"GDT_{inv.InvoiceSymbol}_{inv.InvoiceNumber}.xml",
                        FileExtension = ".xml",
                        ContentType = "application/xml",
                        Content = rawBytes,
                        Source = "GDT-ExportXml",
                        Sha256Hash = ComputeSha256(rawBytes)
                    });
                }
            }

            // 1b. Ưu tiên hàng đầu: Tải trực tiếp file PDF bản thể hiện gốc từ hệ thống Nhà cung cấp HĐĐT
            // (Viettel SInvoice, MISA meInvoice, Visnam Vin-HOADON, TS24 XuatHoaDon...) thông qua thông tin tra cứu trong XML
            if (result.PdfFile == null && result.XmlFile != null && result.XmlFile.Content.Length > 0)
            {
                var vendorPdf = await TryDownloadVendorPdfFromXmlAsync(result.XmlFile.Content, ct);
                if (vendorPdf != null && IsValidPdfSignature(vendorPdf) && vendorPdf.Length > 3500)
                {
                    var normNum = long.TryParse(shdonPrimary, out var nv) ? nv.ToString("D7") : inv.InvoiceNumber;
                    result.Files.Add(new DownloadedInvoiceFile
                    {
                        FileName = $"HD_{khmshdon}{khhdon}_{normNum}_GocNCC.pdf",
                        FileExtension = ".pdf",
                        ContentType = "application/pdf",
                        Content = vendorPdf,
                        Source = "Supplier-DirectPdf",
                        Sha256Hash = ComputeSha256(vendorPdf)
                    });
                }
            }

            // 2. Nếu chưa có file PDF trong kết quả, truy vấn endpoint PDF chuyên dụng của Cổng Thuế
            if (result.PdfFile == null)
            {
                var pdfBytes = await TryFetchPdfEndpointAsync(client, inv, shdonPrimary, ct);
                if (pdfBytes != null && IsValidPdfSignature(pdfBytes))
                {
                    result.Files.Add(new DownloadedInvoiceFile
                    {
                        FileName = $"GDT_{inv.InvoiceSymbol}_{inv.InvoiceNumber}.pdf",
                        FileExtension = ".pdf",
                        ContentType = "application/pdf",
                        Content = pdfBytes,
                        Source = "GDT-ExportPdf",
                        Sha256Hash = ComputeSha256(pdfBytes)
                    });
                }
            }

            // 3. Nếu gói ZIP trả về từ /api/query/invoices/export-xml chứa bộ bản thể hiện gốc của Tổng cục Thuế
            // (invoice.html + details.js + viewinvoice-bg.jpg + sign-check.jpg + invoice.xml),
            // kết xuất trực tiếp gói bản thể hiện gốc của Tổng cục Thuế sang PDF chuẩn (%PDF-)
            if (result.PdfFile == null && result.ZipFile != null && result.ZipFile.Content.Length > 0)
            {
                var officialPkgPdf = await TryRenderOfficialGdtZipPackageToPdfAsync(result.ZipFile.Content, ct);
                if (officialPkgPdf != null && IsValidPdfSignature(officialPkgPdf))
                {
                    var normNum = long.TryParse(shdonPrimary, out var nv) ? nv.ToString("D7") : inv.InvoiceNumber;
                    result.Files.Add(new DownloadedInvoiceFile
                    {
                        FileName = $"GDT_{khmshdon}{khhdon}_{normNum}.pdf",
                        FileExtension = ".pdf",
                        ContentType = "application/pdf",
                        Content = officialPkgPdf,
                        Source = "GDT-OfficialPackagePdf",
                        Sha256Hash = ComputeSha256(officialPkgPdf)
                    });
                }
            }

            return result;
        }

        public static async Task<byte[]?> TryRenderOfficialGdtZipPackageToPdfAsync(byte[] zipBytes, CancellationToken ct = default)
        {
            if (zipBytes == null || zipBytes.Length <= 4) return null;
            if (zipBytes[0] != 0x50 || zipBytes[1] != 0x4B || zipBytes[2] != 0x03 || zipBytes[3] != 0x04) return null;

            try
            {
                using (var checkMs = new MemoryStream(zipBytes))
                using (var checkArchive = new ZipArchive(checkMs, ZipArchiveMode.Read))
                {
                    bool hasOfficialHtml = false;
                    foreach (var entry in checkArchive.Entries)
                    {
                        if (string.Equals(entry.Name, "invoice.html", StringComparison.OrdinalIgnoreCase))
                        {
                            hasOfficialHtml = true;
                            break;
                        }
                    }
                    if (!hasOfficialHtml) return null;
                }

                string? browserPath = ResolveHeadlessBrowserPath();
                if (string.IsNullOrEmpty(browserPath)) return null;

                var tempWorkDir = Path.Combine(Path.GetTempPath(), "GdtOfficialPkg_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempWorkDir);

                try
                {
                    using (var extractMs = new MemoryStream(zipBytes))
                    using (var archive = new ZipArchive(extractMs, ZipArchiveMode.Read))
                    {
                        foreach (var entry in archive.Entries)
                        {
                            if (string.IsNullOrWhiteSpace(entry.Name)) continue;
                            var safeName = Path.GetFileName(entry.Name);
                            var destPath = Path.Combine(tempWorkDir, safeName);
                            using var entryStream = entry.Open();
                            using var fileStream = File.Create(destPath);
                            await entryStream.CopyToAsync(fileStream, ct);
                        }
                    }

                    var htmlPath = Path.Combine(tempWorkDir, "invoice.html");
                    if (!File.Exists(htmlPath)) return null;

                    // Giữ nguyên hình nền (viewinvoice-bg.jpg), khung viền đôi và dấu tích chữ ký số (sign-check.jpg) của Tổng cục Thuế khi in PDF
                    var htmlContent = await File.ReadAllTextAsync(htmlPath, Encoding.UTF8, ct);
                    const string printPreserveCss = @"<style>
                        @media print {
                            * { -webkit-print-color-adjust: exact !important; print-color-adjust: exact !important; }
                            .main-page {
                                background-image: url(viewinvoice-bg.jpg) !important;
                                background-repeat: no-repeat !important;
                                background-position: center center !important;
                                background-size: 180% !important;
                                border: 3px double rgba(145, 87, 21, 0.69) !important;
                            }
                        }
                    </style>";
                    if (htmlContent.Contains("</head>", StringComparison.OrdinalIgnoreCase))
                    {
                        htmlContent = htmlContent.Replace("</head>", printPreserveCss + "</head>", StringComparison.OrdinalIgnoreCase);
                        await File.WriteAllTextAsync(htmlPath, htmlContent, Encoding.UTF8, ct);
                    }

                    var outputPdfPath = Path.Combine(tempWorkDir, "gdt_official_invoice.pdf");
                    var htmlFileUri = new Uri(htmlPath).AbsoluteUri;

                    var psi = new ProcessStartInfo
                    {
                        FileName = browserPath,
                        Arguments = $"--headless=new --disable-gpu --no-sandbox --disable-dev-shm-usage --allow-file-access-from-files --no-pdf-header-footer --print-to-pdf=\"{outputPdfPath}\" \"{htmlFileUri}\"",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };

                    using var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        cts.CancelAfter(TimeSpan.FromSeconds(15));
                        await proc.WaitForExitAsync(cts.Token);

                        if (File.Exists(outputPdfPath))
                        {
                            var pdfBytes = await File.ReadAllBytesAsync(outputPdfPath, ct);
                            if (IsValidPdfSignature(pdfBytes))
                            {
                                return pdfBytes;
                            }
                        }
                    }
                }
                finally
                {
                    try { Directory.Delete(tempWorkDir, true); } catch { }
                }
            }
            catch { }

            return null;
        }

        private static string? ResolveHeadlessBrowserPath()
        {
            var envChrome = Environment.GetEnvironmentVariable("CHROME_PATH");
            if (!string.IsNullOrEmpty(envChrome) && File.Exists(envChrome)) return envChrome;

            string[] candidates =
            {
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
                "/usr/bin/chromium",
                "/usr/bin/chromium-browser",
                "/usr/bin/google-chrome"
            };

            foreach (var c in candidates)
            {
                if (File.Exists(c)) return c;
            }

            return null;
        }

        public static async Task<byte[]?> TryDownloadVendorPdfFromXmlAsync(byte[] xmlBytes, CancellationToken ct = default)
        {
            if (xmlBytes == null || xmlBytes.Length == 0) return null;
            try
            {
                var xmlText = Encoding.UTF8.GetString(xmlBytes);

                // Trích xuất MSTTCGP, MST người bán (<NBan><MST>), DLHDon Id, và từ điển các trường <TTin> (<TTruong> <-> <DLieu>)
                string? msttcgp = null;
                var mTcgp = System.Text.RegularExpressions.Regex.Match(xmlText, @"<MSTTCGP>\s*([^<\s]+)\s*</MSTTCGP>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (mTcgp.Success) msttcgp = mTcgp.Groups[1].Value.Trim();

                string? sellerTaxCode = null;
                var mSeller = System.Text.RegularExpressions.Regex.Match(xmlText, @"<NBan>[\s\S]*?<MST>\s*([^<\s]+)\s*</MST>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (mSeller.Success) sellerTaxCode = mSeller.Groups[1].Value.Trim();

                string? dlhdonId = null;
                var mId = System.Text.RegularExpressions.Regex.Match(xmlText, @"<DLHDon\s+Id=""([^""]+)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (mId.Success) dlhdonId = mId.Groups[1].Value.Trim();

                var ttinMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (System.Text.RegularExpressions.Match mTtin in System.Text.RegularExpressions.Regex.Matches(xmlText, @"<TTin>([\s\S]*?)</TTin>", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    var block = mTtin.Groups[1].Value;
                    var mTruong = System.Text.RegularExpressions.Regex.Match(block, @"<TTruong>\s*([^<]+?)\s*</TTruong>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    var mDlieu = System.Text.RegularExpressions.Regex.Match(block, @"<DLieu>\s*([^<]+?)\s*</DLieu>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (mTruong.Success && mDlieu.Success)
                    {
                        var k = mTruong.Groups[1].Value.Trim();
                        var v = mDlieu.Groups[1].Value.Trim();
                        if (!string.IsNullOrEmpty(k) && !string.IsNullOrEmpty(v) && !ttinMap.ContainsKey(k))
                        {
                            ttinMap[k] = v;
                        }
                    }
                }

                using var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (_, _, _, _) => true
                };
                using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

                // =========================================================================
                // 1. VIETTEL SInvoice (MSTTCGP: 0100109106 - vinvoice.viettel.vn)
                // Trường XML: <TTruong>Mã số bí mật</TTruong> + <NBan><MST>
                // =========================================================================
                string? viettelSecret = null;
                if (ttinMap.TryGetValue("Mã số bí mật", out var vs1)) viettelSecret = vs1;
                else if (ttinMap.TryGetValue("MaSoBiMat", out var vs2)) viettelSecret = vs2;
                else if (ttinMap.TryGetValue("reservationCode", out var vs3)) viettelSecret = vs3;

                if (!string.IsNullOrEmpty(viettelSecret) && !string.IsNullOrEmpty(sellerTaxCode))
                {
                    string[] viettelGetUrls =
                    {
                        $"https://api-vinvoice.viettel.vn/services/einvoiceapplication/sync/utility/generatePDF?reservationCode={Uri.EscapeDataString(viettelSecret)}&supplierTaxCode={Uri.EscapeDataString(sellerTaxCode)}&taxCode={Uri.EscapeDataString(sellerTaxCode)}",
                        $"https://api-vinvoice.viettel.vn/services/einvoicequery/sync/utility/generatePDF?reservationCode={Uri.EscapeDataString(viettelSecret)}&supplierTaxCode={Uri.EscapeDataString(sellerTaxCode)}&taxCode={Uri.EscapeDataString(sellerTaxCode)}"
                    };

                    foreach (var vUrl in viettelGetUrls)
                    {
                        try
                        {
                            var resp = await httpClient.GetAsync(vUrl, ct);
                            if (resp.IsSuccessStatusCode)
                            {
                                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                                if (IsValidPdfSignature(bytes) && bytes.Length > 3500)
                                {
                                    return bytes;
                                }
                            }
                        }
                        catch { }
                    }

                    try
                    {
                        var postUrl = $"https://api-vinvoice.viettel.vn/services/einvoicequery/sync/utility/downloadPDF?taxCode={Uri.EscapeDataString(sellerTaxCode)}";
                        var payload = JsonSerializer.Serialize(new
                        {
                            supplierTaxCode = sellerTaxCode,
                            reservationCode = viettelSecret,
                            taxCode = sellerTaxCode
                        });
                        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                        var resp = await httpClient.PostAsync(postUrl, content, ct);
                        if (resp.IsSuccessStatusCode)
                        {
                            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                            if (IsValidPdfSignature(bytes) && bytes.Length > 3500)
                            {
                                return bytes;
                            }
                        }
                    }
                    catch { }
                }

                // =========================================================================
                // 2. VISNAM Vin-HOADON (MSTTCGP: 0401486901 - tracuu.vin-hoadon.com)
                // Trường XML: <TTruong>QuanLy_SoBaoMat</TTruong> + <NBan><MST>
                // =========================================================================
                if (ttinMap.TryGetValue("QuanLy_SoBaoMat", out var vinSoBaoMat) && !string.IsNullOrEmpty(vinSoBaoMat) && !string.IsNullOrEmpty(sellerTaxCode))
                {
                    string[] vinEndpoints =
                    {
                        "https://tracuu.vin-hoadon.com/api/services/hddt/TraCuuHoaDon/DownloadPdfFromMail",
                        "https://tracuu.vin-hoadon.com/api/services/hddt/TraCuuHoaDon/DownloadPdfFromTraCuu"
                    };

                    var vinPayload = JsonSerializer.Serialize(new
                    {
                        doanhNghiep_MST = sellerTaxCode,
                        maSoBiMat = vinSoBaoMat,
                        key = "",
                        captcha = ""
                    });

                    foreach (var vinUrl in vinEndpoints)
                    {
                        try
                        {
                            using var content = new StringContent(vinPayload, Encoding.UTF8, "application/json");
                            var resp = await httpClient.PostAsync(vinUrl, content, ct);
                            if (resp.IsSuccessStatusCode)
                            {
                                var rawResp = await resp.Content.ReadAsByteArrayAsync(ct);
                                if (IsValidPdfSignature(rawResp) && rawResp.Length > 3500)
                                {
                                    return rawResp;
                                }

                                var jsonStr = Encoding.UTF8.GetString(rawResp);
                                using var doc = JsonDocument.Parse(jsonStr);
                                if (doc.RootElement.TryGetProperty("result", out var resElem) && resElem.ValueKind == JsonValueKind.Object)
                                {
                                    string? b64 = null;
                                    if (resElem.TryGetProperty("fileData", out var fdElem) && fdElem.ValueKind == JsonValueKind.String)
                                        b64 = fdElem.GetString();
                                    else if (resElem.TryGetProperty("base64", out var b64Elem) && b64Elem.ValueKind == JsonValueKind.String)
                                        b64 = b64Elem.GetString();

                                    if (!string.IsNullOrWhiteSpace(b64))
                                    {
                                        var pdfBytes = Convert.FromBase64String(b64.Trim());
                                        if (IsValidPdfSignature(pdfBytes) && pdfBytes.Length > 3500)
                                        {
                                            return pdfBytes;
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }

                // =========================================================================
                // 3. TS24 XuatHoaDon (MSTTCGP: 0309478306 - tracuu.xuathoadon.vn)
                // Trường XML: <TTruong>MNHDon</TTruong>
                // =========================================================================
                if (ttinMap.TryGetValue("MNHDon", out var ts24Code) && !string.IsNullOrEmpty(ts24Code))
                {
                    string[] ts24Urls =
                    {
                        $"https://tracuu.xuathoadon.vn/invoice/download/pdf/1/{Uri.EscapeDataString(ts24Code)}",
                        $"https://tracuu.xuathoadon.vn/invoice/download/print-pdf/1/{Uri.EscapeDataString(ts24Code)}"
                    };

                    foreach (var tsUrl in ts24Urls)
                    {
                        try
                        {
                            var resp = await httpClient.GetAsync(tsUrl, ct);
                            if (resp.IsSuccessStatusCode)
                            {
                                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                                if (IsValidPdfSignature(bytes) && bytes.Length > 3500)
                                {
                                    return bytes;
                                }
                            }
                        }
                        catch { }
                    }
                }

                // =========================================================================
                // 4. MISA meInvoice (MSTTCGP: 0101243150 - meinvoice.vn)
                // Trường XML: <TTruong>TransactionID</TTruong> hoặc <DLHDon Id="...">
                // =========================================================================
                string? misaCode = null;
                if (ttinMap.TryGetValue("TransactionID", out var transId) && !string.IsNullOrEmpty(transId))
                {
                    misaCode = transId;
                }
                else if (string.Equals(msttcgp, "0101243150", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(dlhdonId))
                {
                    misaCode = dlhdonId;
                }
                else if (!string.IsNullOrEmpty(dlhdonId) && dlhdonId.Length >= 8 && dlhdonId.Length <= 20 &&
                         !dlhdonId.StartsWith("_") && !dlhdonId.Contains("-") && !dlhdonId.Contains(" ") &&
                         !string.Equals(dlhdonId, "data123", StringComparison.OrdinalIgnoreCase))
                {
                    misaCode = dlhdonId;
                }

                if (!string.IsNullOrEmpty(misaCode))
                {
                    string[] misaUrls =
                    {
                        $"https://www.meinvoice.vn/tra-cuu/DownloadHandler.ashx?Type=pdf&Code={Uri.EscapeDataString(misaCode)}",
                        $"https://meinvoice.vn/tra-cuu/DownloadHandler.ashx?Type=pdf&Code={Uri.EscapeDataString(misaCode)}"
                    };

                    foreach (var mUrl in misaUrls)
                    {
                        try
                        {
                            var resp = await httpClient.GetAsync(mUrl, ct);
                            if (resp.IsSuccessStatusCode)
                            {
                                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                                if (IsValidPdfSignature(bytes) && bytes.Length > 3500)
                                {
                                    return bytes;
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return null;
        }

        public async Task<byte[]> DownloadXmlAsync(RemoteInvoice inv, string token, CancellationToken ct = default)
        {
            var files = await DownloadAllInvoiceFilesAsync(inv, token, ct);
            return files.XmlFile?.Content ?? Array.Empty<byte>();
        }

        public async Task<(byte[]? XmlBytes, byte[]? PdfBytes)> DownloadInvoiceFilesAsync(RemoteInvoice inv, string token, CancellationToken ct = default)
        {
            var files = await DownloadAllInvoiceFilesAsync(inv, token, ct);
            return (files.XmlFile?.Content, files.PdfFile?.Content);
        }

        private static string ComputeSha256(byte[] data)
        {
            if (data == null || data.Length == 0) return string.Empty;
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(data);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static bool IsInvalidOrErrorPayload(byte[]? data)
        {
            if (data == null || data.Length < 10) return true;

            var prefixLen = Math.Min(data.Length, 512);
            var prefixStr = Encoding.UTF8.GetString(data, 0, prefixLen);

            if (prefixStr.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
                prefixStr.Contains("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase) ||
                prefixStr.Contains("Request Rejected", StringComparison.OrdinalIgnoreCase) ||
                prefixStr.Contains("The requested URL was rejected", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (data[0] == (byte)'{' && (prefixStr.Contains("\"message\":", StringComparison.OrdinalIgnoreCase) ||
                                        prefixStr.Contains("\"status\":", StringComparison.OrdinalIgnoreCase) ||
                                        prefixStr.Contains("\"error\":", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return false;
        }

        private async Task<byte[]?> TryFetchPdfEndpointAsync(HttpClient client, RemoteInvoice inv, string shdonPrimary, CancellationToken ct)
        {
            var (nbmst, khmshdon, khhdon, shdon, _) = ResolveGdtInvoiceParams(inv);
            var ep = (_options.ExportPdfEndpoint ?? "/api/query/invoices/export-pdf").TrimStart('/');

            try
            {
                var pdfUrl = $"{ep}?nbmst={Uri.EscapeDataString(nbmst)}&khhdon={Uri.EscapeDataString(khhdon)}&shdon={Uri.EscapeDataString(shdon)}&khmshdon={Uri.EscapeDataString(khmshdon)}";
                client.DefaultRequestHeaders.Remove("request-id");
                client.DefaultRequestHeaders.TryAddWithoutValidation("request-id", Guid.NewGuid().ToString());
                client.DefaultRequestHeaders.Remove("Action");
                client.DefaultRequestHeaders.TryAddWithoutValidation("Action", Uri.EscapeDataString("Xuất pdf (hóa đơn)"));

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(8));
                var resp = await client.GetAsync(pdfUrl, cts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                    if (IsValidPdfSignature(bytes))
                    {
                        return bytes;
                    }
                }
            }
            catch { }

            return null;
        }
    }
}
