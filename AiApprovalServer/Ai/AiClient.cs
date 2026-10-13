using System.Diagnostics;
using System.Net;
using System.Text;
using AiApproval.Core;

namespace AiApproval.Ai;

public sealed class AiCallResult
{
    public bool Ok { get; init; }
    public string Content { get; init; } = "";
    public string Error { get; init; } = "";
    public long LatencyMs { get; init; }
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
}

//AI 客户端
public sealed class AiClient
{
    private static readonly string[] DefaultAllowedHosts = { "api.deepseek.com" };

    private readonly AppConfig _cfg;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _concurrency;
    private readonly ILogger<AiClient> _logger;

    public AiClient(AppConfig cfg, ILogger<AiClient> logger)
    {
        _cfg = cfg;
        _logger = logger;
        _concurrency = new SemaphoreSlim(Math.Max(1, cfg.Ai.MaxConcurrentCalls), Math.Max(1, cfg.Ai.MaxConcurrentCalls));
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Clamp(cfg.Ai.TimeoutSeconds + 5, 5, 120)) };
        _http.DefaultRequestHeaders.Add("User-Agent", "AiApprovalServer/1.0");
    }

    public bool IsConfigured => _cfg.AiConfigured;

    public string Model => _cfg.Ai.Model;

    //最近一次成功调用时间
    public DateTime? LastSuccessAt { get; private set; }

    //最近一次失败时间与原因（脱敏）
    public DateTime? LastFailureAt { get; private set; }
    public string LastError { get; private set; } = "";

    //对外状态：off / degraded / online
    public string Status
    {
        get
        {
            if (!IsConfigured) return "off";
            if (LastFailureAt is { } failed && (LastSuccessAt is null || failed > LastSuccessAt)) return "degraded";
            return "online";
        }
    }

    //出站主机白名单（自建网关需显式放行）
    private HashSet<string> AllowedHosts()
    {
        var configured = (_cfg.Ai.AllowedHosts ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(h => h.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return configured.Count > 0 ? configured : DefaultAllowedHosts.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    //校验接口地址与协议
    public bool IsUrlAllowed(out string reason)
    {
        reason = "";
        if (!Uri.TryCreate(_cfg.Ai.BaseUrl, UriKind.Absolute, out var uri))
        {
            reason = "AI 接口地址非法";
            return false;
        }

        if (!AllowedHosts().Contains(uri.Host))
        {
            reason = $"AI 接口主机不在白名单：{uri.Host}（自建网关请在 Ai:AllowedHosts 中显式放行）";
            return false;
        }

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return true;

        //明文 http 只放本机 / 内网网关
        if (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && (uri.IsLoopback
                || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                || (IPAddress.TryParse(uri.Host, out var httpIp) && IsPrivate(httpIp))))
            return true;

        reason = "AI 接口必须使用 https（仅本机/内网自建网关可用明文 http）";
        return false;
    }

    //单轮对话
    public async Task<AiCallResult> CompleteAsync(string systemPrompt, string userContent,
        double temperature = 0.2, int? maxTokens = null, bool jsonMode = true, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return new AiCallResult { Ok = false, Error = "AI 未配置（缺少 ApiKey）" };

        if (!IsUrlAllowed(out var urlReason))
            return new AiCallResult { Ok = false, Error = urlReason };

        var payload = new Dictionary<string, object?>
        {
            ["model"] = _cfg.Ai.Model,
            ["messages"] = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userContent },
            },
            ["temperature"] = temperature,
            ["stream"] = false,
            ["max_tokens"] = maxTokens ?? _cfg.Ai.MaxOutputTokens,
        };

        if (jsonMode)
            payload["response_format"] = new { type = "json_object" };

        var json = AppJson.Serialize(payload);
        var attempts = Math.Clamp(_cfg.Ai.MaxRetries, 0, 3) + 1;
        var stopwatch = Stopwatch.StartNew();
        var lastError = "";

        await _concurrency.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_cfg.Ai.TimeoutSeconds, 5, 120)));

                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, _cfg.Ai.BaseUrl);
                    request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _cfg.Ai.ApiKey);

                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
                        .ConfigureAwait(false);

                    var text = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        //只保留状态码，不回传上游响应体
                        lastError = $"AI 接口返回 {(int)response.StatusCode}";
                        if ((int)response.StatusCode is >= 400 and < 500 && (int)response.StatusCode != 429)
                            break;
                        continue;
                    }

                    var (content, promptTokens, completionTokens) = ParseResponse(text);
                    if (content is null)
                    {
                        lastError = "AI 返回结构无法解析";
                        continue;
                    }

                    LastSuccessAt = DateTime.UtcNow;
                    LastError = "";

                    return new AiCallResult
                    {
                        Ok = true,
                        Content = content,
                        LatencyMs = stopwatch.ElapsedMilliseconds,
                        PromptTokens = promptTokens,
                        CompletionTokens = completionTokens,
                    };                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    lastError = $"AI 调用超时（{_cfg.Ai.TimeoutSeconds}s）";
                }
                catch (Exception ex)
                {
                    //只记异常类型与描述，不含密钥
                    lastError = "AI 调用异常：" + Describe(ex);
                }

                if (attempt < attempts)
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt + Random.Shared.Next(0, 150)), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _concurrency.Release();
        }

        _logger.LogWarning("AI 调用失败：{Error}", lastError);
        LastFailureAt = DateTime.UtcNow;
        LastError = lastError;
        return new AiCallResult { Ok = false, Error = lastError, LatencyMs = stopwatch.ElapsedMilliseconds };
    }

    //异常链压成一行
    private static string Describe(Exception ex)
    {
        var parts = new List<string>();
        var current = ex;
        var depth = 0;
        while (current is not null && depth < 3)
        {
            parts.Add($"{current.GetType().Name}({Trim(current.Message)})");
            current = current.InnerException;
            depth++;
        }
        return string.Join(" <- ", parts);

        static string Trim(string message)
            => string.IsNullOrEmpty(message) ? "" : message.Length <= 120 ? message : message[..120];
    }

    //解析响应取正文与用量
    private static (string? Content, int PromptTokens, int CompletionTokens) ParseResponse(string raw)    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var root = doc.RootElement;

            var content = root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
                ? choices[0].TryGetProperty("message", out var message) && message.TryGetProperty("content", out var c)
                    ? c.GetString()
                    : null
                : null;

            var promptTokens = 0;
            var completionTokens = 0;
            if (root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("prompt_tokens", out var p)) promptTokens = p.GetInt32();
                if (usage.TryGetProperty("completion_tokens", out var q)) completionTokens = q.GetInt32();
            }

            return (content, promptTokens, completionTokens);
        }
        catch
        {
            return (null, 0, 0);
        }
    }

    //是否私网 / 回环 / 元数据地址
    private static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || b[0] == 127
                || b[0] == 0
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                || b[0] >= 224;
        }
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return true;
            var b = ip.GetAddressBytes();
            if (b.All(x => x == 0)) return true;
            if ((b[0] & 0xFE) == 0xFC) return true;
            if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) return true;
        }
        return false;
    }
}
