using AiApproval.Core;

namespace AiApproval.Tools;

//文件路径白名单入口
public sealed class PathGuard
{
    //非法字符
    private static readonly char[] InvalidChars = { '<', '>', ':', '"', '|', '?', '*', '\0', '\u0001' };
    //系统保留名
    private static readonly string[] ReservedNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    //相对路径长度上限
    public const int MaxRelativePathLength = 200;

    private readonly AppConfig _cfg;

    public PathGuard(AppConfig cfg) => _cfg = cfg;

    //用户工作区根
    public string UserRoot(string userId) => Path.GetFullPath(Path.Combine(_cfg.WorkspacesDir, "u_" + userId));

    //共享区根
    public string ShareRoot() => Path.GetFullPath(Path.Combine(_cfg.WorkspacesDir, "share"));

    //按范围取根目录
    public string ScopeRoot(string userId, string scope) =>
        string.Equals(scope, "share", StringComparison.OrdinalIgnoreCase) ? ShareRoot() : UserRoot(userId);

    //解析并校验路径
    public (bool Ok, string Error, string FullPath, string Relative) Resolve(string userId, string? scope, string? requestedPath)
    {
        var normalizedScope = string.Equals(scope?.Trim(), "share", StringComparison.OrdinalIgnoreCase) ? "share" : "private";
        var root = ScopeRoot(userId, normalizedScope);

        var raw = (requestedPath ?? "").Trim().Replace('\\', '/');
        //根目录写法统一
        while (raw.StartsWith("./", StringComparison.Ordinal)) raw = raw[2..];
        if (raw is "." or "/") raw = "";

        if (raw.Length > MaxRelativePathLength) return Fail($"路径过长（上限 {MaxRelativePathLength} 字符）");

        var unified = raw;

        //逐项拦截逃逸写法
        if (unified.Contains('\0')) return Fail("路径包含非法字符");
        if (unified.Contains("..")) return Fail("路径不得包含上级目录引用（..）");
        if (unified.StartsWith('/')) return Fail("禁止使用绝对路径");
        if (unified.Length >= 2 && unified[1] == ':') return Fail("禁止使用盘符路径");
        if (unified.Contains("$DATA", StringComparison.OrdinalIgnoreCase)) return Fail("禁止使用备用数据流");
        if (unified.Contains("::")) return Fail("路径包含非法字符");

        //检查每段文件名
        foreach (var segment in unified.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.IndexOfAny(InvalidChars) >= 0) return Fail("路径包含非法字符");
            var bareName = segment.Split('.')[0].ToUpperInvariant();
            if (ReservedNames.Contains(bareName)) return Fail($"禁止使用系统保留名：{segment}");
            if (segment.EndsWith('.') || segment.EndsWith(' ')) return Fail("目录名不得以点或空格结尾");
        }

        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(root, unified.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch
        {
            return Fail("路径格式非法");
        }

        //规范化后必须仍在根目录内
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            return Fail("目标路径超出你的授权目录");
        }

        //软链接一律拒绝
        if (HasReparsePointEscape(root, full, out var linkReason))
            return Fail(linkReason);

        var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
        if (relative == ".") relative = "";
        return (true, "", full, relative);
    }

    //软链接必须解析
    private static bool HasReparsePointEscape(string root, string target, out string reason)
    {
        reason = "";
        try
        {
            var relative = Path.GetRelativePath(root, target);
            if (relative == ".") return false;

            var current = root;
            foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (!File.Exists(current) && !Directory.Exists(current)) continue;

                var info = new FileInfo(current);
                if (info.Exists && info.LinkTarget is not null)
                {
                    reason = $"路径中包含符号链接：{segment}（出于安全策略已拒绝）";
                    return true;
                }

                var dirInfo = new DirectoryInfo(current);
                if (dirInfo.Exists && dirInfo.LinkTarget is not null)
                {
                    reason = $"路径中包含目录链接：{segment}（出于安全策略已拒绝）";
                    return true;
                }

                var attrs = File.GetAttributes(current);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                {
                    reason = $"路径命中重解析点：{segment}（出于安全策略已拒绝）";
                    return true;
                }
            }
        }
        catch
        {
            //读属性失败就放行
            return false;
        }

        return false;
    }

    //返回失败
    private static (bool, string, string, string) Fail(string error) => (false, error, "", "");
}
