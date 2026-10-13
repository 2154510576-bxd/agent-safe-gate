using AiApproval.Core;

namespace AiApproval.Security;

//审批链接签名，防 IDOR 越权审批
public sealed class ApprovalSigner
{
    private readonly byte[] _key;

    public ApprovalSigner(AppConfig cfg) => _key = cfg.DeriveMacKey("approval-link");

    //生成审批令牌
    public string CreateToken(string taskId, DateTime expiresAtUtc)
    {
        var exp = new DateTimeOffset(expiresAtUtc).ToUnixTimeSeconds().ToString();
        var sig = System.Security.Cryptography.HMACSHA256.HashData(_key, System.Text.Encoding.UTF8.GetBytes(Payload(taskId, exp)));
        return Base64Url(System.Text.Encoding.UTF8.GetBytes(exp)) + "." + Base64Url(sig);
    }

    //令牌入库只存摘要
    public string HashToken(string token) => Crypto.Sha256Hex(token);

    //校验令牌签名与过期
    public bool Verify(string taskId, string? token, out DateTime expiresAt, out string reason)
    {
        expiresAt = default;
        reason = "";

        if (string.IsNullOrWhiteSpace(token)) { reason = "缺少审批令牌"; return false; }

        var parts = token.Split('.');
        if (parts.Length != 2) { reason = "审批令牌格式错误"; return false; }

        string expRaw;
        byte[] providedSig;
        try
        {
            expRaw = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(Pad(parts[0])));
            providedSig = Convert.FromBase64String(Pad(parts[1]));
        }
        catch
        {
            reason = "审批令牌格式错误";
            return false;
        }

        if (!long.TryParse(expRaw, out var expUnix)) { reason = "审批令牌格式错误"; return false; }

        //签名按字节恒定时间比较
        var expectedSig = System.Security.Cryptography.HMACSHA256.HashData(_key, System.Text.Encoding.UTF8.GetBytes(Payload(taskId, expRaw)));
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expectedSig, providedSig))
        {
            reason = "审批令牌签名无效（可能被篡改）";
            return false;
        }

        expiresAt = DateTimeOffset.FromUnixTimeSeconds(expUnix).UtcDateTime;
        if (expiresAt <= DateTime.UtcNow) { reason = "审批令牌已过期"; return false; }

        return true;
    }

    private static string Payload(string taskId, string exp) => "approve|" + taskId + "|" + exp;

    private static string Base64Url(byte[] data)
        => Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string Pad(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        return (s.Length % 4) switch
        {
            2 => s + "==",
            3 => s + "=",
            _ => s,
        };
    }
}
