using AiApproval.Ai;
using AiApproval.Core;
using AiApproval.Security;
using AiApproval.Services;
using AiApproval.Tools;

namespace AiApproval.Api;

//鉴权接口：健康检查、登录、改密
public static class AuthApi
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/public/health", (AppConfig cfg, AiClient ai) => Api.Ok(new
        {
            status = "ok",
            serverTime = DateTime.UtcNow,
            aiConfigured = ai.IsConfigured,
            aiModel = ai.IsConfigured ? ai.Model : null,
            //只有最近调用成功才算 online
            aiStatus = ai.Status,
            aiLastSuccessAt = ai.LastSuccessAt,
            aiLastFailureAt = ai.LastFailureAt,
            aiLastError = ai.IsConfigured ? ai.LastError : "",
            mode = ai.Status switch
            {
                "online" => "AI 意图解析 + AI 安全审核（物理隔离）",
                "degraded" => "AI 已配置但调用失败 → 已降级为本地意图解析，变更类操作全部转人工审批",
                _ => "本地意图解析（AI 未配置）+ 审核不可用 → 变更类操作全部转人工审批",
            },
            selfRegistration = cfg.AllowSelfRegistration,
        }));

        app.MapPost("/api/auth/login", LoginAsync);
        app.MapPost("/api/auth/register", RegisterAsync);

        app.MapPost("/api/auth/logout", async (HttpContext context, AuditLog audit) =>
        {
            var identity = Api.Identity(context);
            if (identity is null) return Api.Error(401, "UNAUTHENTICATED", "请先登录");

            await audit.WriteAsync(identity.User.Id, identity.User.UserName, identity.User.Role, Api.Ip(context),
                "auth.logout", identity.User.UserName, "SUCCESS", "客户端主动退出");
            return Api.Ok(new { message = "已退出登录" });
        });

        app.MapGet("/api/auth/me", (HttpContext context) =>
        {
            var identity = Api.Identity(context);
            if (identity is null) return Api.Error(401, "UNAUTHENTICATED", "请先登录");

            return Api.Ok(new
            {
                id = identity.User.Id,
                userName = identity.User.UserName,
                displayName = identity.User.DisplayName,
                employeeId = identity.User.EmployeeId,
                role = identity.User.Role,
                roleLabel = Roles.Label(identity.User.Role),
                emailMasked = identity.User.EmailMasked,
                permissions = PermissionList(identity.User.Role),
                lastLoginAt = identity.User.LastLoginAt,
            });
        });

        app.MapPost("/api/auth/password", async (HttpContext context, UserService users, SecurityGuard guard) =>
        {
            var identity = Api.Identity(context);
            if (identity is null) return Api.Error(401, "UNAUTHENTICATED", "请先登录");

            if (!guard.TryConsume($"password:{identity.User.Id}", 5, TimeSpan.FromMinutes(15)))
                return Api.Error(429, "RATE_LIMITED", "修改口令过于频繁，请稍后再试");

            var body = Api.ReadBody<ChangePasswordRequest>(context);
            if (body is null) return Api.Error(400, "BAD_REQUEST", "请求体格式错误");

            var error = UserService.ValidatePassword(body.NewPassword, identity.User.UserName);
            if (error is not null) return Api.Error(400, "WEAK_PASSWORD", error);

            var ok = await users.ChangePasswordAsync(identity.User.Id, body.NewPassword, identity.User.UserName, Api.Ip(context));
            if (!ok) return Api.Error(500, "FAILED", "口令修改失败");

            //令牌世代 +1，旧令牌失效
            return Api.Ok(new { message = "口令已更新，请使用新口令重新登录（旧令牌已全部失效）", relogin = true });
        });
    }

    //列出权限键与中文名
    private static List<object> PermissionList(string role)
        => AccountPolicy.PermissionsOf(role)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => (object)new { key = p, label = Permissions.Label(p) })
            .ToList();

    //登录：限流、锁定与审计
    private static async Task<IResult> LoginAsync(HttpContext context, UserService users, JwtService jwt,
        AuditLog audit, SecurityGuard guard, AppConfig cfg)
    {
        var ip = Api.Ip(context);

        if (guard.IsBanned(ip, out _, out _))
            return Api.Error(403, "IP_BANNED", "请求被安全策略拒绝");

        if (!guard.TryConsume($"login:ip:{ip}", cfg.Limits.LoginPerFiveMinutesPerIp, TimeSpan.FromMinutes(5)))
        {
            guard.Strike(ip, "登录接口请求过频", weight: 1, loginFail: true);
            await audit.WriteAsync("", "", "", ip, "auth.rate-limit", "login", "BLOCKED", "登录频率超限");
            return Api.Error(429, "RATE_LIMITED", "登录尝试过于频繁，请稍后再试");
        }

        var body = Api.ReadBody<LoginRequest>(context);
        if (body is null) return Api.Error(400, "BAD_REQUEST", "请求体格式错误");

        var userName = Api.Clean(body.UserName, 64);
        var password = body.Password ?? "";
        if (userName.Length == 0 || password.Length is 0 or > 128)
            return Api.Error(400, "BAD_REQUEST", "请输入用户名与口令");

        var result = await users.LoginAsync(userName, password, ip);

        if (!result.Success)
        {
            guard.Strike(ip, $"登录失败（{Api.Clean(userName, 32)}）", weight: 1, loginFail: true);

            if (result.Locked)
                await audit.WriteAsync("", userName, "", ip, "auth.lockout", userName, "LOCKED",
                    $"连续失败触发账号锁定至 {result.LockedUntil:HH:mm:ss}（UTC）");

            await Task.Delay(result.AccountNotFound ? 120 : 60);
            return Api.Error(401, "AUTH_FAILED", result.Error);
        }

        var user = result.User!;
        var token = jwt.Issue(user.Id, user.UserName, user.Role, user.TokenEpoch, out var signKey, out var expiresAt);

        await audit.WriteAsync(user.Id, user.UserName, user.Role, ip, "auth.login", user.UserName,
            "SUCCESS", $"角色={Roles.Label(user.Role)}");

        return Api.Ok(new
        {
            token,
            signKey,
            expiresAt,
            user = new
            {
                id = user.Id,
                userName = user.UserName,
                displayName = user.DisplayName,
                employeeId = user.EmployeeId,
                role = user.Role,
                roleLabel = Roles.Label(user.Role),
                emailMasked = user.EmailMasked,
                permissions = PermissionList(user.Role),
            },
            //前端据此对写请求签名
            signing = new
            {
                algorithm = "HMAC-SHA256",
                canonical = "METHOD\\nPATH?QUERY\\nX-Timestamp\\nX-Nonce\\nSHA256_HEX(rawBody)",
                headers = new[] { "X-Timestamp", "X-Nonce", "X-Signature" },
                requiredFor = new[] { "POST", "PUT", "PATCH", "DELETE" },
            },
        });
    }

    //注册账号是管理员行为
    private static async Task<IResult> RegisterAsync(HttpContext context, UserService users, AuditLog audit,
        SecurityGuard guard, AppConfig cfg)
    {
        var ip = Api.Ip(context);
        var identity = Api.Identity(context);
        if (identity is null) return Api.Error(401, "UNAUTHENTICATED", "请先登录后再创建账号");

        if (!guard.TryConsume($"register:user:{identity.User.Id}", 20, TimeSpan.FromHours(1)))
        {
            guard.Strike(ip, "创建账号过频", weight: 1, waf: true);
            return Api.Error(429, "RATE_LIMITED", "创建账号过于频繁");
        }

        var body = Api.ReadBody<RegisterRequest>(context);
        if (body is null) return Api.Error(400, "BAD_REQUEST", "请求体格式错误");

        var role = Api.Clean(body.Role, 16).ToLowerInvariant() is { Length: > 0 } r ? r : Roles.Employee;
        var policyError = AccountPolicy.CanCreateAccount(identity.User.Role, role);
        if (policyError is not null)
        {
            await audit.WriteAsync(identity.User.Id, identity.User.UserName, identity.User.Role, ip,
                "auth.register", Api.Clean(body.UserName, 32), "DENIED", policyError);
            guard.Strike(ip, $"越权创建账号（{identity.User.UserName}）", weight: 2, waf: true);
            return Api.Error(403, "FORBIDDEN", policyError);
        }

        var (user, error) = await users.CreateAsync(
            Api.Clean(body.UserName, 32), body.Password ?? "", Api.Clean(body.DisplayName, 32),
            role, Api.Clean(body.Email, 128), identity.User.UserName);

        if (user is null)
        {
            await audit.WriteAsync(identity.User.Id, identity.User.UserName, identity.User.Role, ip, "auth.register",
                Api.Clean(body.UserName, 32), "FAIL", error);
            return Api.Error(400, "REGISTER_FAILED", error);
        }

        await audit.WriteAsync(identity.User.Id, identity.User.UserName, identity.User.Role, ip, "auth.register",
            user.UserName, "SUCCESS",
            $"由 {identity.User.UserName} 创建：角色={Roles.Label(user.Role)} 工号={user.EmployeeId} 镜像={user.MirrorState}");

        var demo = users.RevealDemoPassword(user);
        return Api.Ok(new
        {
            message = $"已创建账号 {user.UserName}（{Roles.Label(user.Role)}，工号 {user.EmployeeId}）",
            userName = user.UserName,
            employeeId = user.EmployeeId,
            role = user.Role,
            roleLabel = Roles.Label(user.Role),
            mirrorState = user.MirrorState,
            mirrorDemoPassword = demo ?? "",
            mirrorNote = user.MirrorState switch
            {
                "synced" => "已同步到飞书用户表",
                "skipped" => "未配置飞书，已跳过镜像（不影响本机账号）",
                _ => "飞书镜像失败，已静默处理（不影响本机账号），可在管理后台重试",
            },
        });
    }
}

public sealed class ChangePasswordRequest
{
    public string NewPassword { get; set; } = "";
}
