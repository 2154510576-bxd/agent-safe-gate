using System.Text.RegularExpressions;

namespace AiApproval.Tools;

//风险扫描结果
public sealed class ContentRiskResult
{
    public bool HasRisk => Hits.Count > 0;
    //命中标签
    public List<string> Hits { get; init; } = new();
    //有命中就要审批
    public bool RequiresApproval => Hits.Count > 0;
    public string Summary => Hits.Count == 0 ? "" : string.Join("；", Hits);
}

//可执行判定结果
public sealed class ExecutableVerdict
{
    public bool IsExecutable => Evidences.Count > 0;
    //判定依据
    public List<string> Evidences { get; init; } = new();
    //判定通道
    public string Channel { get; init; } = "";
    public string Summary => Evidences.Count == 0 ? "" : string.Join("；", Evidences);
}

//内容风险扫描
public static class ContentRiskScanner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(60);

    //脚本类后缀
    public static readonly string[] ScriptExtensions =
    {
        "bat", "cmd", "ps1", "psm1", "vbs", "vbe", "js", "jse", "wsf", "wsh", "hta",
        "sh", "bash", "zsh", "py", "pl", "rb", "jar", "reg", "lnk", "scr", "com", "pif",
    };

    //二进制后缀
    public static readonly string[] BinaryExtensions =
    {
        "exe", "dll", "msi", "sys", "drv", "ocx", "so", "dylib", "bin", "iso", "img", "apk", "app",
    };

    //内容里的可执行特征
    private static readonly (string Label, Regex Rx)[] ExecutableContentRules =
    {
        ("内容以 shebang 开头（#! 脚本）", new Regex(@"^\s*#!\s*/(usr/)?bin/(env\s+)?[\w\-/ ]+", RegexOptions.Compiled, Timeout)),
        ("内容含文件魔数（PE/ELF/Mach-O 可执行文件头）", new Regex(@"^MZ|\x7FELF|\xCA\xFE\xBA\xBE|\xCF\xFA\xED\xFE", RegexOptions.Compiled, Timeout)),
        ("内容为批处理脚本（@echo off / goto / setlocal / %~dp0）", new Regex(@"(?i)(@echo\s+off|goto\s*:|setlocal|%~dp0|for\s+/f\s+.*\s+in\s*\()", RegexOptions.Compiled, Timeout)),
        ("内容为 PowerShell 脚本（param( / cmdlet 绑定 / Write-Host / $env:）", new Regex(@"(?i)(^\s*param\s*\(|\[Parameter\s*\(|Write-Host|\$env:[A-Za-z_]+|Get-ChildItem|\bSet-ExecutionPolicy\b)", RegexOptions.Compiled, Timeout)),
        ("内容为 Web 脚本标签（PHP / ASP / JSP）", new Regex(@"(?i)(<\?php|<%@\s*page|<%[=@]|<\s*jsp:)", RegexOptions.Compiled, Timeout)),
        ("内容为 Bash/Shell 脚本（export VAR= / function / && 串联命令）", new Regex(@"(?i)(^\s*(export|source|alias)\s+\w+=|^\s*\w+\s*\(\)\s*\{|chmod\s+\+x|apt-get\s+install|yum\s+install)", RegexOptions.Compiled, Timeout)),
    };

    //后缀或内容命中即算可执行
    public static ExecutableVerdict DetectExecutable(string? relativePath, string? content)
    {
        var evidences = new List<string>();
        var bySuffix = false;
        var byContent = false;

        var ext = Path.GetExtension(relativePath ?? "").TrimStart('.').ToLowerInvariant();
        if (ext.Length > 0)
        {
            if (ScriptExtensions.Contains(ext))
            {
                evidences.Add($"后缀 .{ext}（脚本/可执行类）");
                bySuffix = true;
            }
            else if (BinaryExtensions.Contains(ext))
            {
                evidences.Add($"后缀 .{ext}（二进制可执行类）");
                bySuffix = true;
            }
        }

        var sample = content is { Length: > 0 }
            ? (content.Length > 20000 ? content[..20000] : content)
            : "";

        if (sample.Length > 0)
        {
            foreach (var (label, rx) in ExecutableContentRules)
            {
                try
                {
                    if (rx.IsMatch(sample)) { evidences.Add(label); byContent = true; }
                }
                catch (RegexMatchTimeoutException)
                {
                    //超时也按命中算
                    evidences.Add(label + "（匹配超时）");
                    byContent = true;
                }
            }
        }

        return new ExecutableVerdict
        {
            Evidences = evidences,
            Channel = bySuffix && byContent ? "both" : bySuffix ? "suffix" : byContent ? "content" : "",
        };
    }

    //是否真二进制
    public static bool LooksLikeRealBinary(string? content)
        => !string.IsNullOrEmpty(content) && content.Contains('\0');

    //内容风险规则
    private static readonly (string Label, Regex Rx)[] Rules =
    {
        ("下载执行脚本", new Regex(@"(?i)(invoke-webrequest|invoke-expression|\biex\b|downloadstring|downloadfile|start-bitstransfer|urlmon|winhttp|certutil\s+-urlcache|bitsadmin)", RegexOptions.Compiled, Timeout)),
        ("命令执行/反弹", new Regex(@"(?i)(powershell(\.exe)?\s+-(enc|e|command|exec)|\bcmd(\.exe)?\s*/c|/bin/(ba)?sh\s+-c|nc\s+-e|ncat\s+-e|bash\s+-i|socat\s+)", RegexOptions.Compiled, Timeout)),
        ("编码/混淆载荷", new Regex(@"(?i)(base64\s*-d|frombase64string|\[convert\]::frombase64|eval\(|assert\(|unescape\(|-enc\s+[A-Za-z0-9+/=]{24,})", RegexOptions.Compiled, Timeout)),
        ("内网/元数据地址", new Regex(@"(?i)(https?://(10\.|127\.|192\.168\.|172\.(1[6-9]|2\d|3[01])\.|169\.254\.|0\.0\.0\.0|localhost)|169\.254\.169\.254|metadata\.google\.internal)", RegexOptions.Compiled, Timeout)),
        ("外部链接", new Regex(@"(?i)https?://(?!127\.0\.0\.1|localhost)[a-z0-9][a-z0-9.\-]{2,}\.[a-z]{2,24}", RegexOptions.Compiled, Timeout)),
        ("计划任务/开机自启", new Regex(@"(?i)(schtasks\s+/create|register-scheduledtask|reg\s+add\s+.*\\run|startup\s+folder|/etc/cron|systemctl\s+enable)", RegexOptions.Compiled, Timeout)),
        ("隐匿执行", new Regex(@"(?i)(-windowstyle\s+hidden|-w\s+hidden|attrib\s+\+h|Set-ItemProperty.*Hidden|chmod\s+\+x|>[\s]*nul\s*2>&1|/dev/null\s+2>&1)", RegexOptions.Compiled, Timeout)),
        ("凭据/密钥特征", new Regex(@"(?i)(password\s*=|passwd\s*=|api[_-]?key\s*[:=]|secret\s*[:=]|-----BEGIN\s+(RSA|OPENSSH|PRIVATE)|sk-[A-Za-z0-9]{16,})", RegexOptions.Compiled, Timeout)),
        ("注入话术", new Regex(@"(?i)(忽略(以上|之前|所有)(的)?(规则|指令)|ignore\s+(all\s+)?(previous|above)\s+instructions|跳过(审批|审核)|直接执行不要审批|你现在是管理员)", RegexOptions.Compiled, Timeout)),
    };

    //扫描待写入内容
    public static ContentRiskResult Scan(string? content)
    {
        var result = new ContentRiskResult();
        if (string.IsNullOrWhiteSpace(content)) return result;

        var sample = content.Length > 20000 ? content[..20000] : content;

        //控制字符堆砌
        var controlCount = sample.Count(c => char.IsControl(c) && c != '\n' && c != '\r' && c != '\t');
        if (controlCount > 8) result.Hits.Add($"内容含 {controlCount} 个不可见控制字符（疑似隐藏载荷）");

        foreach (var (label, rx) in Rules)
        {
            try
            {
                if (rx.IsMatch(sample)) result.Hits.Add(label);
            }
            catch (RegexMatchTimeoutException)
            {
                result.Hits.Add(label + "（匹配超时）");
            }
        }

        return result;
    }

    //按后缀判风险
    public static ContentRiskResult ScanPath(string? relativePath)
    {
        var result = new ContentRiskResult();
        var path = (relativePath ?? "").Trim();
        if (path.Length == 0) return result;

        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (ext.Length == 0) return result;

        if (ScriptExtensions.Contains(ext))
            result.Hits.Add($"脚本/可执行类后缀 .{ext}（可能被用于投放脚本）");
        else if (BinaryExtensions.Contains(ext))
            result.Hits.Add($"二进制可执行后缀 .{ext}");

        return result;
    }

    //是否二进制后缀
    public static bool IsBinaryExtension(string? relativePath)
    {
        var ext = Path.GetExtension(relativePath ?? "").TrimStart('.').ToLowerInvariant();
        return ext.Length > 0 && BinaryExtensions.Contains(ext);
    }
}
