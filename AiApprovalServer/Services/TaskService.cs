using AiApproval.Ai;
using AiApproval.Core;
using AiApproval.Security;
using AiApproval.Tools;

namespace AiApproval.Services;

//任务服务：审批与执行
public sealed class TaskService
{
    private readonly JsonStore _store;
    private readonly AuditLog _audit;
    private readonly ApprovalSigner _signer;
    private readonly Outbox _outbox;
    private readonly Harness _harness;
    private readonly UserService _users;
    private readonly AppConfig _cfg;
    private readonly ILogger<TaskService> _logger;
    private readonly byte[] _rawInputKey;

    public TaskService(JsonStore store, AuditLog audit, ApprovalSigner signer, Outbox outbox, Harness harness,
        UserService users, AppConfig cfg, ILogger<TaskService> logger)
    {
        _store = store;
        _audit = audit;
        _signer = signer;
        _outbox = outbox;
        _harness = harness;
        _users = users;
        _cfg = cfg;
        _logger = logger;
        _rawInputKey = cfg.DeriveKey("raw-input");
    }

    //建任务并落盘
    public async Task<AgentTask> CreateAsync(RequestIdentity identity, IntentPlan plan, RuleEvaluation eval,
        ReviewOutcome review, string rawInput, List<string> injectionFlags, bool requiresApproval)
    {
        var task = new AgentTask
        {
            Id = "t_" + Crypto.RandomHex(8),
            UserId = identity.User.Id,
            UserName = identity.User.UserName,
            UserRole = identity.User.Role,
            Intent = plan.Intent,
            ToolName = plan.Tool,
            ActionJson = eval.Outcome.CanonicalAction,
            RawInputLength = rawInput.Length,
            RiskLevel = eval.Outcome.RiskLevel,
            Rule = eval.Outcome,
            Review = review,
            InjectionFlags = injectionFlags,
            Status = requiresApproval ? TaskState.PendingApproval : TaskState.Executing,
            StatusReason = requiresApproval ? string.Join("；", eval.Outcome.Reasons) : "策略允许自动执行",
            CreatedAt = DateTime.UtcNow,
            ApprovalExpiresAt = DateTime.UtcNow.AddMinutes(_cfg.ApprovalTokenMinutes),
        };

        //原始输入加密存放
        task.RawInputCipher = Crypto.AesGcmEncrypt(rawInput, _rawInputKey, "task-raw|" + task.Id);

        if (requiresApproval)
        {
            var token = _signer.CreateToken(task.Id, task.ApprovalExpiresAt);
            task.ApprovalTokenHash = _signer.HashToken(token);

            var link = $"{_cfg.Mail.PublicBaseUrl}/admin?task={task.Id}&token={Uri.EscapeDataString(token)}";
            await _outbox.EnqueueAsync(
                _cfg.Mail.ApproverAddress,
                $"【待审批】{task.UserName} 申请 {ToolCatalog.Get(task.ToolName)?.Title ?? task.ToolName}",
                BuildNoticeBody(task, link),
                task.Id,
                "",                 //发给审批人的通知，不进提交人收件箱
                task.UserName,
                link).ConfigureAwait(false);
        }

        await _store.MutateAsync<AgentTask, bool>(StoreNames.Tasks, list =>
        {
            list.Add(task);
            return true;
        }).ConfigureAwait(false);

        await _audit.WriteAsync(task.UserId, task.UserName, task.UserRole, identity.Ip, "task.create", task.Id,
            requiresApproval ? "PENDING" : "AUTO",
            $"工具={task.ToolName} 风险={task.RiskLevel} 意图={task.Intent}").ConfigureAwait(false);

        return task;
    }

    //拼审批通知正文
    private static string BuildNoticeBody(AgentTask task, string link) => string.Join('\n', new[]
    {
        "有新的高危操作等待审批。",
        "",
        $"任务编号：{task.Id}",
        $"提交人：{task.UserName}（{Roles.Label(task.UserRole)}）",
        $"意图摘要：{task.Intent}",
        $"工具：{task.ToolName}",
        $"风险等级：{Risk.Label(task.RiskLevel)}",
        $"规则判定：{string.Join("；", task.Rule.Reasons.DefaultIfEmpty("通过"))}",
        $"结构审核：{task.Review.Verdict}（{task.Review.Reason}）",
        $"规范化动作：{task.ActionJson}",
        "",
        "请登录管理后台查看完整上下文（含用户原始输入）后再决定：",
        link,
        "",
        "说明：出于数据安全考虑，邮件中不包含用户原始输入与完整参数，需登录后台查看。",
        $"（该链接已签名，{task.ApprovalExpiresAt:yyyy-MM-dd HH:mm} UTC 前有效，且只能使用一次）",
    });

    //记录被拦截的任务
    public async Task<AgentTask> CreateRejectedAsync(RequestIdentity identity, IntentPlan plan, RuleEvaluation eval,
        ReviewOutcome review, string rawInput, List<string> injectionFlags, string status, string reason)
    {
        var task = new AgentTask
        {
            Id = "t_" + Crypto.RandomHex(8),
            UserId = identity.User.Id,
            UserName = identity.User.UserName,
            UserRole = identity.User.Role,
            Intent = plan.Intent,
            ToolName = plan.Tool,
            ActionJson = eval.Outcome.CanonicalAction,
            RawInputLength = rawInput.Length,
            RiskLevel = eval.Outcome.RiskLevel,
            Rule = eval.Outcome,
            Review = review,
            InjectionFlags = injectionFlags,
            Status = status,
            StatusReason = reason,
            CreatedAt = DateTime.UtcNow,
            DecidedAt = DateTime.UtcNow,
            DecidedBy = status == TaskState.RejectedAi ? "ai-reviewer" : "rule-engine",
        };

        task.RawInputCipher = Crypto.AesGcmEncrypt(rawInput, _rawInputKey, "task-raw|" + task.Id);

        await _store.MutateAsync<AgentTask, bool>(StoreNames.Tasks, list =>
        {
            list.Add(task);
            return true;
        }).ConfigureAwait(false);

        await _audit.WriteAsync(task.UserId, task.UserName, task.UserRole, identity.Ip,
            status == TaskState.RejectedAi ? "task.reject-ai" : "task.reject-rule", task.Id,
            status, $"{task.ToolName}：{reason}").ConfigureAwait(false);

        return task;
    }

    //列出任务
    public async Task<List<AgentTask>> ListAsync(string? userId = null, string? status = null, int limit = 100)
    {
        var all = await _store.ReadListAsync<AgentTask>(StoreNames.Tasks).ConfigureAwait(false);
        IEnumerable<AgentTask> query = all.OrderByDescending(t => t.CreatedAt);
        if (!string.IsNullOrWhiteSpace(userId)) query = query.Where(t => t.UserId == userId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(t => t.Status == status);
        return query.Take(Math.Clamp(limit, 1, 300)).ToList();
    }

    //按 id 取任务
    public async Task<AgentTask?> GetAsync(string id)
    {
        var all = await _store.ReadListAsync<AgentTask>(StoreNames.Tasks).ConfigureAwait(false);
        return all.FirstOrDefault(t => t.Id == id);
    }

    //统计待审批数
    public async Task<int> CountPendingAsync()
        => (await ListAsync(null, TaskState.PendingApproval, 300).ConfigureAwait(false)).Count;

    //解密用户原始输入
    public string DecryptRawInput(AgentTask task)
        => string.IsNullOrEmpty(task.RawInputCipher)
            ? ""
            : Crypto.AesGcmDecrypt(task.RawInputCipher, _rawInputKey, "task-raw|" + task.Id) ?? "（解密失败：密钥已更换或数据被篡改）";

    //自动执行任务
    public async Task<AgentTask> ExecuteAsync(AgentTask task, string via)
    {
        var result = await _harness.ExecuteAsync(task).ConfigureAwait(false);

        var updated = await _store.MutateAsync<AgentTask, AgentTask?>(StoreNames.Tasks, list =>
        {
            var target = list.FirstOrDefault(t => t.Id == task.Id);
            if (target is null) return null;

            target.ExecutedAt = DateTime.UtcNow;
            target.Status = result.Ok ? TaskState.Executed : TaskState.Failed;
            target.StatusReason = via;
            target.ResultJson = AppJson.Serialize(new
            {
                ok = result.Ok,
                summary = result.Summary,
                data = result.Data,
            }, disk: true);
            target.VersionIds = result.VersionIds;
            return target;
        }).ConfigureAwait(false);

        await _audit.WriteAsync(task.UserId, task.UserName, task.UserRole, "", "task.execute", task.Id,
            result.Ok ? "SUCCESS" : "FAILED", $"{via}：{result.Summary}").ConfigureAwait(false);

        return updated ?? task;
    }

    //管理员裁决（批准与打回同一入口）
    public async Task<(bool Ok, string Error, AgentTask? Task)> DecideAsync(string taskId, bool approve, string reason,
        RequestIdentity admin, string via)
    {
        if (reason.Trim().Length < 2)
            return (false, "请填写不少于 2 个字符的审批意见", null);

        var claimed = await _store.MutateAsync<AgentTask, AgentTask?>(StoreNames.Tasks, list =>
        {
            var target = list.FirstOrDefault(t => t.Id == taskId);
            if (target is null) return null;
            if (target.Status != TaskState.PendingApproval) return null;

            if (ApproveExpired(target)) return null;

            target.Status = approve ? TaskState.Executing : TaskState.RejectedAdmin;
            target.DecidedAt = DateTime.UtcNow;
            target.DecidedBy = admin.User.UserName;
            target.DecisionReason = reason.Trim();
            target.StatusReason = $"{admin.User.UserName}（{via}）";
            return target;
        }).ConfigureAwait(false);

        if (claimed is null)
        {
            var current = await GetAsync(taskId).ConfigureAwait(false);
            if (current is null) return (false, "任务不存在", null);
            if (current.Status == TaskState.PendingApproval && ApproveExpired(current))
                return (false, "该任务已超过审批有效期，已作废", current);
            return (false, $"该任务当前状态为「{TaskState.Label(current.Status)}」，不能再次审批（可能已被处理）", current);
        }

        if (!approve)
        {
            await _audit.WriteAsync(admin.User.Id, admin.User.UserName, admin.User.Role, admin.Ip,
                "approval.reject", taskId, "REJECTED", $"via={via} 意见={reason}").ConfigureAwait(false);

            await NotifyOwnerAsync(claimed, "已打回", $"审批人：{admin.User.UserName}\n打回理由：{reason}").ConfigureAwait(false);
            return (true, "", claimed);
        }

        await _audit.WriteAsync(admin.User.Id, admin.User.UserName, admin.User.Role, admin.Ip,
            "approval.approve", taskId, "APPROVED", $"via={via} 意见={reason}").ConfigureAwait(false);

        var executed = await ExecuteAsync(claimed, $"审批通过（{admin.User.UserName}，{via}）").ConfigureAwait(false);

        var resultSummary = ExtractResultSummary(executed.ResultJson);
        await NotifyOwnerAsync(executed, executed.Status == TaskState.Executed ? "已执行" : "执行失败",
            $"审批人：{admin.User.UserName}\n审批意见：{reason}" + (resultSummary.Length > 0 ? $"\n执行结果：{resultSummary}" : ""))
            .ConfigureAwait(false);

        return (true, "", executed);
    }

    //通知提交人
    private async Task NotifyOwnerAsync(AgentTask task, string outcome, string detail)
    {
        var user = await _users.FindByIdAsync(task.UserId).ConfigureAwait(false);
        var address = user is null ? "" : _users.DecryptEmail(user);

        await _outbox.EnqueueAsync(address,
            $"【{outcome}】你的操作申请 {task.Id}",
            $"任务编号：{task.Id}\n意图：{task.Intent}\n工具：{task.ToolName}\n当前状态：{TaskState.Label(task.Status)}\n{detail}",
            task.Id, task.UserId, task.UserName, "").ConfigureAwait(false);
    }

    //取执行摘要
    private static string ExtractResultSummary(string resultJson)
    {
        var dto = AppJson.Deserialize<ExecuteSummaryDto>(resultJson);
        return dto?.Summary ?? "";
    }

    private sealed class ExecuteSummaryDto
    {
        public bool Ok { get; set; }
        public string? Summary { get; set; }
    }

    //邮件令牌审批（一次性、有有效期）
    public async Task<(bool Ok, string Error)> DecideByTokenAsync(string taskId, string token, string reason, RequestIdentity admin)
    {
        var task = await GetAsync(taskId).ConfigureAwait(false);
        if (task is null) return (false, "任务不存在");
        if (task.Status != TaskState.PendingApproval) return (false, "该任务已不在待审批状态");
        if (task.ApprovalTokenUsed) return (false, "该审批链接已被使用过");

        if (!_signer.Verify(taskId, token, out _, out var verifyError))
        {
            await _audit.WriteAsync(admin.User.Id, admin.User.UserName, admin.User.Role, admin.Ip,
                "approval.token", taskId, "FAIL", verifyError).ConfigureAwait(false);
            return (false, verifyError);
        }

        if (!Crypto.FixedEquals(_signer.HashToken(token), task.ApprovalTokenHash))
        {
            await _audit.WriteAsync(admin.User.Id, admin.User.UserName, admin.User.Role, admin.Ip,
                "approval.token", taskId, "FAIL", "令牌与任务不匹配（疑似越权审批）").ConfigureAwait(false);
            return (false, "审批令牌与任务不匹配");
        }

        await _store.MutateAsync<AgentTask, bool>(StoreNames.Tasks, list =>
        {
            var target = list.FirstOrDefault(t => t.Id == taskId);
            if (target is null) return false;
            target.ApprovalTokenUsed = true;
            return true;
        }).ConfigureAwait(false);

        var (ok, error, _) = await DecideAsync(taskId, true, reason, admin, "email-token").ConfigureAwait(false);
        return (ok, error);
    }

    //超时处理：先提醒，后作废
    public async Task<int> ExpireOverdueAsync()
    {
        var warnWindow = TimeSpan.FromMinutes(30);

        //即将过期提醒（每条只提醒一次）
        var toWarn = await _store.MutateAsync<AgentTask, List<string>>(StoreNames.Tasks, list =>
        {
            var ids = new List<string>();
            foreach (var task in list.Where(t => t.Status == TaskState.PendingApproval && !t.ExpiryWarned
                                                 && t.ApprovalExpiresAt != default
                                                 && t.ApprovalExpiresAt > DateTime.UtcNow
                                                 && t.ApprovalExpiresAt - DateTime.UtcNow <= warnWindow))
            {
                task.ExpiryWarned = true;
                ids.Add(task.Id);
            }
            return ids;
        }).ConfigureAwait(false);

        foreach (var id in toWarn)
        {
            var task = await GetAsync(id).ConfigureAwait(false);
            if (task is null) continue;
            await NotifyOwnerAsync(task, "审批即将过期",
                $"审批有效期到 {task.ApprovalExpiresAt:yyyy-MM-dd HH:mm}（UTC）为止。"
                + "如仍需执行请在后台催办；不需要了可以直接撤回（撤回不执行任何动作）。").ConfigureAwait(false);
        }

        //过期作废并通知提交人
        var expired = await _store.MutateAsync<AgentTask, List<string>>(StoreNames.Tasks, list =>
        {
            var ids = new List<string>();
            foreach (var task in list.Where(t => t.Status == TaskState.PendingApproval && ApproveExpired(t)))
            {
                task.Status = TaskState.Expired;
                task.StatusReason = "审批超时自动作废";
                ids.Add(task.Id);
            }
            return ids;
        }).ConfigureAwait(false);

        foreach (var id in expired)
        {
            await _audit.WriteAsync("system", "system", "system", "", "task.expire", id, "EXPIRED", "审批超时自动作废").ConfigureAwait(false);
            var task = await GetAsync(id).ConfigureAwait(false);
            if (task is not null)
                await NotifyOwnerAsync(task, "审批超时作废",
                    "审批超时已自动作废（本次未执行任何操作）。如需继续，请重新提交。").ConfigureAwait(false);
        }

        if (toWarn.Count > 0 || expired.Count > 0)
            _logger.LogInformation("审批时效维护：提醒 {Warn} 个、作废 {Expired} 个", toWarn.Count, expired.Count);

        return expired.Count;
    }

    //是否已过审批有效期
    private static bool ApproveExpired(AgentTask task)
        => task.ApprovalExpiresAt != default && task.ApprovalExpiresAt < DateTime.UtcNow;

    //用户视图
    public object ToUserView(AgentTask task) => new
    {
        id = task.Id,
        intent = task.Intent,
        tool = task.ToolName,
        action = AppJson.Deserialize<object>(task.ActionJson),
        riskLevel = task.RiskLevel,
        riskLabel = Risk.Label(task.RiskLevel),
        status = task.Status,
        statusLabel = TaskState.Label(task.Status),
        statusReason = task.StatusReason,
        ruleReasons = task.Rule.Reasons,
        reviewVerdict = task.Review.Verdict,
        reviewReason = task.Review.Reason,
        injectionFlags = task.InjectionFlags,
        createdAt = task.CreatedAt,
        decidedAt = task.DecidedAt,
        decidedBy = task.DecidedBy,
        decisionReason = task.DecisionReason,
        executedAt = task.ExecutedAt,
        result = AppJson.Deserialize<object>(task.ResultJson),
        versionIds = task.VersionIds,
        rollbacked = task.RolledBack,
        approvalExpiresAt = task.ApprovalExpiresAt,
    };

    //管理端视图（含解密后的原始输入）
    public object ToAdminView(AgentTask task) => new
    {
        id = task.Id,
        userId = task.UserId,
        userName = task.UserName,
        userRole = task.UserRole,
        userRoleLabel = Roles.Label(task.UserRole),
        intent = task.Intent,
        tool = task.ToolName,
        action = AppJson.Deserialize<object>(task.ActionJson),
        rawInput = DecryptRawInput(task),
        rawInputLength = task.RawInputLength,
        riskLevel = task.RiskLevel,
        riskLabel = Risk.Label(task.RiskLevel),
        rule = new { decision = task.Rule.Decision, requiresApproval = task.Rule.RequiresApproval, reasons = task.Rule.Reasons },
        review = new
        {
            verdict = task.Review.Verdict,
            available = task.Review.Available,
            risk = task.Review.RiskLevel,
            category = task.Review.Category,
            reason = task.Review.Reason,
        },
        injectionFlags = task.InjectionFlags,
        status = task.Status,
        statusLabel = TaskState.Label(task.Status),
        statusReason = task.StatusReason,
        createdAt = task.CreatedAt,
        decidedAt = task.DecidedAt,
        decidedBy = task.DecidedBy,
        decisionReason = task.DecisionReason,
        executedAt = task.ExecutedAt,
        result = AppJson.Deserialize<object>(task.ResultJson),
        versionIds = task.VersionIds,
        rollbacked = task.RolledBack,
        approvalUsed = task.ApprovalTokenUsed,
        approvalExpiresAt = task.ApprovalExpiresAt,
    };
}
