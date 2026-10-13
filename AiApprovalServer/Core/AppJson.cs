using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiApproval.Core;

//全局 JSON 约定
public static class AppJson
{
    public static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static readonly JsonSerializerOptions Disk = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    //序列化，disk=true 缩进落盘
    public static string Serialize(object value, bool disk = false)
        => JsonSerializer.Serialize(value, disk ? Disk : Wire);

    //反序列化，失败返回 null
    public static T? Deserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try { return JsonSerializer.Deserialize<T>(json, Wire); }
        catch { return default; }
    }

    //从模型输出里提取第一个 JSON 对象
    public static string? ExtractFirstJsonObject(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;

        var candidate = StripFence(text).Trim();
        if (candidate.StartsWith('{') && Balanced(candidate) is { } whole) return whole;

        var start = candidate.IndexOf('{');
        if (start < 0) return null;
        return Balanced(candidate[start..]);
    }

    //花括号配平截取
    private static string? Balanced(string text)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0) return text[..(i + 1)];
                    break;
            }
        }
        return null;
    }

    //剥掉围栏标记
    public static string StripFence(string text)
    {
        var t = text.Trim();
        if (!t.StartsWith("```", StringComparison.Ordinal)) return t;

        var firstNewline = t.IndexOf('\n');
        if (firstNewline > 0) t = t[(firstNewline + 1)..];
        var lastFence = t.LastIndexOf("```", StringComparison.Ordinal);
        if (lastFence >= 0) t = t[..lastFence];
        return t.Trim();
    }
}
