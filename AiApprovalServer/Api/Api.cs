using AiApproval.Core;
using AiApproval.Security;

namespace AiApproval.Api;

//统一响应与请求辅助
public static class Api
{
    //成功响应
    public static IResult Ok(object? data = null)
        => Results.Json(new { ok = true, data }, AppJson.Wire);

    //错误响应
    public static IResult Error(int status, string code, string message)
        => Results.Json(new { ok = false, code, message }, AppJson.Wire, statusCode: status);

    //取登录身份
    public static RequestIdentity? Identity(HttpContext context)
        => context.Items[SecurityMiddleware.IdentityKey] as RequestIdentity;

    //取客户端 IP
    public static string Ip(HttpContext context) => SecurityGuard.ClientIp(context);

    //从已缓冲的请求体反序列化
    public static T? ReadBody<T>(HttpContext context) where T : class
    {
        if (context.Items[SecurityMiddleware.RawBodyKey] is byte[] bytes && bytes.Length > 0)
            return AppJson.Deserialize<T>(System.Text.Encoding.UTF8.GetString(bytes));

        return null;
    }

    //清洗入参：限长、去控制字符
    public static string Clean(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var trimmed = value.Trim();
        var cleaned = new string(trimmed.Where(c => !char.IsControl(c) || c == '\n' || c == '\t').ToArray());
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
    }
}
