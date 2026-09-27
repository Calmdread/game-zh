using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace GameZh;

public sealed record TranslationOptions(string Url, string ApiKey, string Model, int TimeoutSeconds, double Temperature, int MaxTokens);

public interface ITranslationService
{
    Task<string> TranslateAsync(string text, string previous, TranslationOptions options, CancellationToken token);
}

public sealed class TranslationException(string message, bool retryable = false) : Exception(message)
{
    public bool Retryable { get; } = retryable;
}

public sealed class OpenAiTranslator : ITranslationService, IDisposable
{
    static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    { Timeout = Timeout.InfiniteTimeSpan };
    readonly SemaphoreSlim limit = new(1, 1);
    readonly Dictionary<string, string> cache = new();
    readonly Queue<string> order = new();
    DateTime lastRequestUtc = DateTime.MinValue;

    public async Task<string> TestAsync(TranslationOptions options, CancellationToken token) =>
        await TranslateCoreAsync("Hello.", "", options, token);

    public async Task<string> TranslateAsync(string text, string previous, TranslationOptions options, CancellationToken token)
    {
        string key = options.Url + "\n" + options.Model + "\n" + text;
        lock (cache) if (cache.TryGetValue(key, out var cached)) return cached;
        string result = await TranslateCoreAsync(text, previous, options, token);
        lock (cache)
        {
            if (!cache.ContainsKey(key))
            {
                cache[key] = result;
                order.Enqueue(key);
                while (order.Count > 256) cache.Remove(order.Dequeue());
            }
        }
        return result;
    }

    async Task<string> TranslateCoreAsync(string text, string previous, TranslationOptions options, CancellationToken token)
    {
        Uri endpoint = NormalizeEndpoint(options.Url);
        if (string.IsNullOrWhiteSpace(options.Model)) throw new TranslationException("请填写模型名称。", false);
        if (endpoint.Scheme != Uri.UriSchemeHttps && !endpoint.IsLoopback)
            throw new TranslationException("远程 API 地址必须使用 HTTPS；本机服务可使用 HTTP。", false);
        await limit.WaitAsync(token);
        try
        {
            TimeSpan spacing = TimeSpan.FromMilliseconds(700) - (DateTime.UtcNow - lastRequestUtc);
            if (spacing > TimeSpan.Zero) await Task.Delay(spacing, token);
            string system = "你是游戏本地化译者。将用户给出的游戏文字自然、准确地翻译成简体中文。保留人名、数字、专有名词和术语的一致性。只输出译文，不解释，不添加原文中没有的内容。";
            string user = string.IsNullOrWhiteSpace(previous)
                ? "待翻译：\n" + text[..Math.Min(text.Length, 1500)]
                : "上一句（仅供理解上下文）：\n" + previous[..Math.Min(previous.Length, 200)] + "\n\n待翻译：\n" + text[..Math.Min(text.Length, 1500)];
            var payload = new
            {
                model = options.Model,
                messages = new[] { new { role = "system", content = system }, new { role = "user", content = user } },
                temperature = options.Temperature,
                max_tokens = options.MaxTokens,
                stream = false
            };
            string body = JsonSerializer.Serialize(payload);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                token.ThrowIfCancellationRequested();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    if (!string.IsNullOrWhiteSpace(options.ApiKey))
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
                    lastRequestUtc = DateTime.UtcNow;
                    using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    string json = await response.Content.ReadAsStringAsync(timeout.Token);
                    if (!response.IsSuccessStatusCode)
                    {
                        bool retry = response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                        string message = response.StatusCode switch
                        {
                            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "API 认证失败，请检查 API Key 和服务权限。",
                            HttpStatusCode.TooManyRequests => "接口触发限流，请稍后重试或降低识别频率。",
                            HttpStatusCode.NotFound => "接口地址或模型不存在，请检查 API 地址与模型名称。",
                            _ => $"翻译接口返回 HTTP {(int)response.StatusCode}。请检查服务状态与参数。"
                        };
                        if (retry && attempt < 2) { await Task.Delay(500 * (attempt + 1), token); continue; }
                        throw new TranslationException(message, retry);
                    }
                    try
                    {
                        using var document = JsonDocument.Parse(json);
                        var content = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content");
                        string translation = content.ValueKind == JsonValueKind.String ? content.GetString() ?? "" : "";
                        translation = translation.Trim();
                        if (translation.Length == 0) throw new TranslationException("接口返回空译文，请检查模型配置。", false);
                        return translation;
                    }
                    catch (KeyNotFoundException) { throw new TranslationException("接口响应不是兼容的聊天补全格式。", false); }
                    catch (JsonException) { throw new TranslationException("接口响应不是有效 JSON。", false); }
                    catch (InvalidOperationException) { throw new TranslationException("接口响应缺少译文内容。", false); }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    if (attempt < 2) { await Task.Delay(500 * (attempt + 1), token); continue; }
                    throw new TranslationException("翻译请求超时，请检查网络或增加超时时间。", true);
                }
                catch (HttpRequestException ex) when (attempt < 2)
                {
                    AppLog.Write("network_retry", ex.GetType().Name);
                    await Task.Delay(500 * (attempt + 1), token);
                }
                catch (HttpRequestException)
                {
                    throw new TranslationException("无法连接翻译服务，请检查网络和 API 地址。", true);
                }
            }
            throw new TranslationException("翻译重试已用尽。", true);
        }
        finally { limit.Release(); }
    }

    public static Uri NormalizeEndpoint(string input)
    {
        if (!Uri.TryCreate(input?.Trim(), UriKind.Absolute, out var uri))
            throw new TranslationException("请输入完整的 API 地址，例如 https://example.com/v1/chat/completions。", false);
        string path = uri.AbsolutePath.TrimEnd('/');
        if (!path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            var builder = new UriBuilder(uri);
            builder.Path = (path == "/" ? "/v1" : path) + "/chat/completions";
            uri = builder.Uri;
        }
        return uri;
    }

    public void Dispose() => limit.Dispose();
}
