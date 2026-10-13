using System.Collections.Concurrent;
using AiApproval.Core;

namespace AiApproval.Security;

public sealed class BanRecord
{
    public string Ip { get; set; } = "";
    public DateTime BannedAt { get; set; }
    public DateTime BannedUntil { get; set; }
    public int Strikes { get; set; }
    public string Reason { get; set; } = "";
    public int HoneypotHits { get; set; }
    public int WafHits { get; set; }
    public int LoginFails { get; set; }
    public List<string> LastEvents { get; set; } = new();
}

public sealed class IpState
{
    public int Strikes;
    public int HoneypotHits;
    public int WafHits;
    public int LoginFails;
    public DateTime LastSeen;
    public DateTime? BannedUntil;
    public string LastReason = "";
    public readonly List<string> Events = new();
}

//运行时防线状态机（内存实现）
public sealed class SecurityGuard
{
    private sealed class Window
    {
        public long StartTicks;
        public int Count;
    }

    private readonly object _sync = new();
    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IpState> _ips = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _nonces = new(StringComparer.Ordinal);
    private readonly LimitOptions _limits;
    private readonly int _nonceWindowSeconds;

    public long WafBlocks { get; private set; }
    public long HoneypotHits { get; private set; }
    public long LoginFailures { get; private set; }
    public long RateLimited { get; private set; }
    public long ReplayRejected { get; private set; }
    public long SignatureRejected { get; private set; }
    public long BannedRequests { get; private set; }

    //记录一次签名校验失败
    public void NoteSignatureRejected() => SignatureRejected++;

    //后台写操作每分钟上限
    public int AdminWritePerMinute => _limits.AdminWritePerMinutePerAdmin;



    public SecurityGuard(LimitOptions limits, int nonceWindowSeconds)
    {
        _limits = limits;
        _nonceWindowSeconds = nonceWindowSeconds;
    }

    //限流

    //固定窗口计数
    public bool TryConsume(string key, int limit, TimeSpan window)
    {
        var now = DateTime.UtcNow.Ticks;
        var w = _windows.GetOrAdd(key, _ => new Window { StartTicks = now, Count = 0 });

        lock (_sync)
        {
            if (now - w.StartTicks > window.Ticks)
            {
                w.StartTicks = now;
                w.Count = 0;
            }

            if (w.Count >= limit)
            {
                RateLimited++;
                return false;
            }

            w.Count++;
            return true;
        }
    }

    //剩余配额
    public int Remaining(string key, int limit, TimeSpan window)
    {
        if (!_windows.TryGetValue(key, out var w)) return limit;
        lock (_sync)
        {
            if (DateTime.UtcNow.Ticks - w.StartTicks > window.Ticks) return limit;
            return Math.Max(0, limit - w.Count);
        }
    }

    //攻击计分与封禁

    //是否已被封禁
    public bool IsBanned(string ip, out DateTime until, out string reason)
    {
        until = default;
        reason = "";
        lock (_sync)
        {
            if (!_ips.TryGetValue(ip, out var state)) return false;
            if (state.BannedUntil is { } t && t > DateTime.UtcNow)
            {
                until = t;
                reason = state.LastReason;
                BannedRequests++;
                return true;
            }
            return false;
        }
    }

    //记一次攻击特征，weight 累加
    public bool Strike(string ip, string reason, int weight = 1, bool honeypot = false, bool waf = false, bool loginFail = false)
    {
        if (weight <= 0) return false;

        lock (_sync)
        {
            if (!_ips.TryGetValue(ip, out var state))
            {
                state = new IpState { LastSeen = DateTime.UtcNow };
                _ips[ip] = state;
            }

            state.Strikes += weight;
            state.LastSeen = DateTime.UtcNow;
            state.LastReason = reason;
            if (honeypot) { state.HoneypotHits++; HoneypotHits++; }
            if (waf) { state.WafHits++; WafBlocks++; }
            if (loginFail) { state.LoginFails++; LoginFailures++; }

            state.Events.Add($"{DateTime.UtcNow:HH:mm:ss} {reason} (+{weight})");
            if (state.Events.Count > 12) state.Events.RemoveAt(0);

            var threshold = honeypot && state.HoneypotHits >= _limits.HoneypotStrikesBeforeBan
                ? _limits.HoneypotStrikesBeforeBan
                : _limits.WafStrikesBeforeBan;

            var triggered = (honeypot && state.HoneypotHits >= _limits.HoneypotStrikesBeforeBan)
                            || state.Strikes >= _limits.WafStrikesBeforeBan;

            if (triggered && (state.BannedUntil is null || state.BannedUntil < DateTime.UtcNow))
            {
                state.BannedUntil = DateTime.UtcNow.AddMinutes(_limits.BanMinutes);
                state.LastReason = $"自动封禁：{reason}（阈值 {threshold}）";
                return true;
            }

            return false;
        }
    }

    //手动封禁
    public void Ban(string ip, string reason, int minutes)
    {
        lock (_sync)
        {
            if (!_ips.TryGetValue(ip, out var state))
            {
                state = new IpState();
                _ips[ip] = state;
            }
            state.BannedUntil = DateTime.UtcNow.AddMinutes(minutes);
            state.LastReason = reason;
            state.Events.Add($"{DateTime.UtcNow:HH:mm:ss} 封禁：{reason}（{minutes} 分钟）");
        }
    }

    //解封
    public bool Unban(string ip)
    {
        lock (_sync)
        {
            if (!_ips.TryGetValue(ip, out var state)) return false;
            state.BannedUntil = null;
            state.Strikes = 0;
            state.HoneypotHits = 0;
            state.WafHits = 0;
            state.LastReason = "已由管理员解封";
            state.Events.Add($"{DateTime.UtcNow:HH:mm:ss} 管理员解封");
            return true;
        }
    }

    //安全态势快照
    public object Snapshot()
    {
        lock (_sync)
        {
            var now = DateTime.UtcNow;
            var list = _ips.OrderByDescending(kv => kv.Value.Strikes).Take(50).Select(kv => new
            {
                ip = kv.Key,
                strikes = kv.Value.Strikes,
                honeypotHits = kv.Value.HoneypotHits,
                wafHits = kv.Value.WafHits,
                loginFails = kv.Value.LoginFails,
                banned = kv.Value.BannedUntil is { } t && t > now,
                bannedUntil = kv.Value.BannedUntil,
                lastReason = kv.Value.LastReason,
                lastSeen = kv.Value.LastSeen,
                events = kv.Value.Events,
            }).ToList();

            return new
            {
                wafBlocks = WafBlocks,
                honeypotHits = HoneypotHits,
                loginFailures = LoginFailures,
                rateLimited = RateLimited,
                replayRejected = ReplayRejected,
                signatureRejected = SignatureRejected,
                bannedRequests = BannedRequests,
                activeBans = list.Count(x => x.banned),
                trackedIps = _ips.Count,
                offsets = list,
            };
        }
    }

    //Anti-Replay

    //一次性随机数校验，防重放
    public bool TryUseNonce(string subject, string nonce, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(nonce) || nonce.Length < 16 || nonce.Length > 128)
        {
            reason = "随机数长度非法";
            ReplayRejected++;
            return false;
        }

        var key = subject + "|" + nonce;
        var now = DateTime.UtcNow;

        lock (_sync)
        {
            if (_nonces.Count > 20000) Cleanup(now);
            if (_nonces.ContainsKey(key))
            {
                reason = "随机数已被使用（重放）";
                ReplayRejected++;
                return false;
            }
            _nonces[key] = now.AddSeconds(_nonceWindowSeconds);
            return true;
        }
    }

    //清理过期状态
    private void Cleanup(DateTime now)
    {
        foreach (var key in _nonces.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList())
            _nonces.Remove(key);

        foreach (var key in _ips.Where(kv => kv.Value.LastSeen < now.AddHours(-6)
                                            && (kv.Value.BannedUntil is null || kv.Value.BannedUntil < now))
                     .Select(kv => kv.Key).ToList())
            _ips.Remove(key);
    }

    public static string ClientIp(HttpContext context)
    {
        //只信任连接对端地址
        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
