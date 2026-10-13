using AiApproval.Core;
using AiApproval.Services;

namespace AiApproval.Tools;

//规则校验结果
public sealed class RuleEvaluation
{
    public RuleOutcome Outcome { get; init; } = new();
    //动作摘要（给审批人看）
    public string ActionSummary { get; init; } = "";
}

//规则引擎（硬防线）
public sealed class RuleEngine
{
    private static readonly string[] ProtectedPathMarkers =
    {
        ".env", ".ssh", ".git", "web.config", "appsettings", "secrets.json", "id_rsa", ".htaccess",
    };

    private readonly PathGuard _paths;
    private readonly DatasetStore _datasets;
    private readonly UserService _users;
    private readonly AppConfig _cfg;

    public RuleEngine(PathGuard paths, DatasetStore datasets, UserService users, AppConfig cfg)
    {
        _paths = paths;
        _datasets = datasets;
        _users = users;
        _cfg = cfg;
    }

    //做主校验
    public async Task<RuleEvaluation> EvaluateAsync(string role, string userId, string toolName,
        Dictionary<string, string> rawArgs, bool injectionSuspected)
    {
        var outcome = new RuleOutcome();
        var args = Normalize(rawArgs);

        var tool = ToolCatalog.Get(toolName);
        if (tool is null)
            return Deny($"工具 {Sanitize(toolName)} 不在允许的目录中");

        //管理端工具按角色组判
        if (tool.AdminOnly && !Roles.IsAdminSide(role))
            return Deny("该工具仅管理员可用");

        var missing = tool.Permissions.Where(p => !ToolCatalog.HasPermission(role, p)).ToList();
        if (missing.Count > 0)
            return Deny($"当前角色（{Roles.Label(role)}）缺少权限：{string.Join("、", missing.Select(Permissions.Label))}");

        //未知参数一律拒绝
        var unknown = args.Keys.Where(k => !tool.AllowedParamNames.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
        if (unknown.Count > 0)
            return Deny($"包含未定义的参数：{string.Join("、", unknown.Select(Sanitize))}");

        foreach (var name in tool.RequiredParams)
        {
            if (!args.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
                return Deny($"缺少必填参数：{name}");
        }

        outcome.RiskLevel = tool.RiskLevel;
        outcome.RequiresApproval = tool.RequiresApproval;

        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var summary = "";

        switch (tool.Name)
        {
            case "list_files":
            case "read_file":
            {
                var check = await CheckFileAccessAsync(userId, role, args, requireShareWrite: false);
                if (check.Error is not null) return Deny(check.Error);

                normalized["scope"] = check.Scope;
                normalized["path"] = check.Relative;
                summary = tool.Name == "read_file"
                    ? $"读取文件：{Describe(check.Scope, check.Relative)}"
                    : $"列目录：{Describe(check.Scope, string.IsNullOrEmpty(check.Relative) ? "/" : check.Relative)}";

                if (tool.Name == "read_file" && check.IsDirectory)
                    return Deny("目标是一个目录，请使用 list_files");
                break;
            }

            case "write_file":
            {
                var check = await CheckFileAccessAsync(userId, role, args, requireShareWrite: true);
                if (check.Error is not null) return Deny(check.Error);

                var content = args.GetValueOrDefault("content") ?? "";
                if (content.Length > ToolCatalog.MaxFileContentChars)
                    return Deny($"写入内容过长（{content.Length} 字符，上限 {ToolCatalog.MaxFileContentChars}）");
                if (content.Any(c => char.IsControl(c) && c != '\n' && c != '\r' && c != '\t' && c != '\0'))
                    return Deny("内容包含不允许的控制字符");
                if (string.IsNullOrEmpty(check.Relative))
                    return Deny("必须指定文件名");

                //真二进制只能拒绝
                if (ContentRiskScanner.LooksLikeRealBinary(content))
                    return Deny("检测到二进制内容（含 NUL 字节），本工具只能写文本。"
                                + "如果确实要落地二进制：请改为写入 base64 文本文件（例如 app.bin.b64）并在说明里标注来源，"
                                + "或使用专门的文件上传通道。");

                var mode = (args.GetValueOrDefault("mode") ?? "create").Trim().ToLowerInvariant();
                if (mode is not ("create" or "overwrite" or "append"))
                    return Deny("mode 只能是 create / overwrite / append");

                var exists = File.Exists(check.FullPath);

                //已存在就让用户选
                if (mode == "create" && exists)
                {
                    return Ask($"{check.Relative} 已经存在，请确认你要怎么做",
                        new List<string>
                        {
                            $"append：把内容追加到 {check.Relative}（保留原有内容）",
                            $"overwrite：覆盖 {check.Relative}（原内容会进快照，可回退）",
                            $"rename_file：改成别的文件名（例如加 -v2 后缀）",
                        });
                }

                //风险分级
                var executable = ContentRiskScanner.DetectExecutable(check.Relative, content);
                var contentRisk = ContentRiskScanner.Scan(content);
                var risks = executable.Evidences.Concat(contentRisk.Hits).Distinct().ToList();
                var areaLabel = check.Scope == "share" ? "共享工作区" : "私人工作区";

                if (executable.IsExecutable)
                {
                    //可执行一律转审批
                    outcome.RequiresApproval = true;
                    outcome.RiskLevel = Risk.Max(outcome.RiskLevel, Risk.High);
                    outcome.Reasons.Add($"判定为可执行/脚本内容（判定通道：{executable.Channel}）：" + executable.Summary);
                    outcome.Translations.Add("可执行性判定：" + executable.Summary);
                }

                if (contentRisk.HasRisk)
                {
                    outcome.RequiresApproval = true;
                    outcome.RiskLevel = Risk.Max(outcome.RiskLevel, Risk.High);
                    outcome.Reasons.Add("内容风险扫描命中：" + contentRisk.Summary);
                    outcome.Translations.Add("片段风险扫描：" + contentRisk.Summary);
                }

                //覆盖的分级判断
                if (exists && mode == "overwrite" && (check.Scope == "share" || executable.IsExecutable || contentRisk.HasRisk))
                {
                    outcome.RequiresApproval = true;
                    outcome.RiskLevel = Risk.Max(outcome.RiskLevel, Risk.High);
                    outcome.Reasons.Add("覆盖已有文件会丢失历史内容（已存快照可回退，但仍需人工确认）");
                }

                if (check.Scope == "share")
                {
                    outcome.RequiresApproval = true;
                    outcome.RiskLevel = Risk.Max(outcome.RiskLevel, Risk.High);
                    outcome.Reasons.Add("写入共享工作区会影响其他成员，需要人工确认");
                }

                //私人区普通文本可免审批
                var isPrivate = check.Scope == "private";
                var isPlainText = !executable.IsExecutable && !contentRisk.HasRisk;
                var smallEnough = content.Length <= 20000;
                if (isPrivate && isPlainText && !injectionSuspected && smallEnough)
                {
                    outcome.SandboxAutoAllowed = true;
                    outcome.Translations.Add($"私人工作区普通文本文件（{ModeLabel(mode)}）→ 允许免人工审批"
                        + (exists && mode == "overwrite" ? "，覆盖前已存快照可一键回退" : "")
                        + "（审计照常记录）");
                }

                normalized["scope"] = check.Scope;
                normalized["path"] = check.Relative;
                normalized["content"] = content;
                normalized["mode"] = mode;
                normalized["area"] = areaLabel;
                if (risks.Count > 0) normalized["risk_hits"] = string.Join("；", risks);

                summary = $"{ModeLabel(mode)}文件：{Describe(check.Scope, check.Relative)}（{content.Length} 字符，{areaLabel}）"
                          + (risks.Count > 0 ? $"【风险：{string.Join("；", risks)}】" : "");
                break;
            }

            case "rename_file":
            {
                var check = await CheckFileAccessAsync(userId, role, args, requireShareWrite: true);
                if (check.Error is not null) return Deny(check.Error);
                if (string.IsNullOrEmpty(check.Relative)) return Deny("必须指定要改名的文件");
                if (!File.Exists(check.FullPath)) return Deny($"文件不存在：{check.Relative}");

                var newName = (args.GetValueOrDefault("new_name") ?? "").Trim();
                if (newName.Length == 0 || newName.Length > 120) return Deny("新文件名长度需在 1-120 字符之间");
                if (newName.Contains('/') || newName.Contains('\\') || newName.Contains(".."))
                    return Deny("新文件名只能是文件名本身（不含目录、不含 ..）");
                if (newName.IndexOfAny(new[] { ':', '*', '?', '"', '<', '>', '|', '\0' }) >= 0)
                    return Deny("新文件名包含非法字符");

                var oldExt = Path.GetExtension(check.Relative).TrimStart('.').ToLowerInvariant();
                var newExt = Path.GetExtension(newName).TrimStart('.').ToLowerInvariant();

                //改成可执行后缀要审批
                var newNameVerdict = ContentRiskScanner.DetectExecutable(newName, null);
                var oldContentVerdict = ContentRiskScanner.DetectExecutable(check.Relative, null);
                var extChanged = !string.Equals(oldExt, newExt, StringComparison.OrdinalIgnoreCase);

                if (newNameVerdict.IsExecutable || (!extChanged && oldContentVerdict.IsExecutable))
                {
                    outcome.RequiresApproval = true;
                    outcome.RiskLevel = Risk.Max(outcome.RiskLevel, Risk.High);
                    outcome.Reasons.Add($"目标文件名判定为可执行/脚本：{newNameVerdict.Summary}（改名会改变文件的执行语义）");
                    outcome.Translations.Add("可执行性判定（新名）：" + newNameVerdict.Summary);
                }

                if (extChanged)
                {
                    outcome.RequiresApproval = true;
                    outcome.RiskLevel = Risk.Max(outcome.RiskLevel, Risk.High);
                    outcome.Reasons.Add($"修改扩展名属于风险操作：{(oldExt.Length == 0 ? "（无后缀）" : "." + oldExt)} → {(newExt.Length == 0 ? "（无后缀）" : "." + newExt)}");
                }

                if (check.Scope == "share")
                {
                    outcome.RequiresApproval = true;
                    outcome.RiskLevel = Risk.Max(outcome.RiskLevel, Risk.High);
                    outcome.Reasons.Add("共享工作区改名会影响其他成员");
                }

                //同后缀改名可免审批
                if (check.Scope == "private" && !extChanged && !newNameVerdict.IsExecutable && !injectionSuspected)
                {
                    outcome.SandboxAutoAllowed = true;
                    outcome.Translations.Add("私人工作区同后缀改名 → 允许免人工审批（仍留存审计）");
                }

                normalized["scope"] = check.Scope;
                normalized["path"] = check.Relative;
                normalized["new_name"] = newName;
                summary = $"重命名：{Describe(check.Scope, check.Relative)} → {newName}";
                break;
            }

            case "create_folder":
            {
                var check = await CheckFileAccessAsync(userId, role, args, requireShareWrite: true);
                if (check.Error is not null) return Deny(check.Error);
                if (string.IsNullOrEmpty(check.Relative)) return Deny("必须指定要新建的目录名");
                if (Directory.Exists(check.FullPath)) return Deny($"目录已存在：{check.Relative}");
                if (File.Exists(check.FullPath)) return Deny($"同名文件已存在：{check.Relative}");

                if (check.Scope == "share")
                {
                    outcome.RequiresApproval = true;
                    outcome.RiskLevel = Risk.Max(outcome.RiskLevel, Risk.High);
                    outcome.Reasons.Add("在共享区新建目录会影响其他成员");
                }
                else
                {
                    outcome.SandboxAutoAllowed = !injectionSuspected;
                    outcome.Translations.Add("沙箱内低风险操作：个人工作区新建目录 → 允许免人工审批");
                }

                normalized["scope"] = check.Scope;
                normalized["path"] = check.Relative;
                summary = $"新建目录：{Describe(check.Scope, check.Relative)}";
                break;
            }

            case "get_file_info":
            {
                var check = await CheckFileAccessAsync(userId, role, args, requireShareWrite: false);
                if (check.Error is not null) return Deny(check.Error);
                if (string.IsNullOrEmpty(check.Relative)) return Deny("必须指定文件名或目录名");

                normalized["scope"] = check.Scope;
                normalized["path"] = check.Relative;
                summary = $"查看文件信息：{Describe(check.Scope, check.Relative)}";
                break;
            }

            case "my_workspace":
            {
                summary = "查看我的可用区域与权限";
                break;
            }

            case "my_notifications":
            {
                var limit = 10;
                if (args.TryGetValue("limit", out var limitRaw) && !string.IsNullOrWhiteSpace(limitRaw)
                    && int.TryParse(limitRaw, out var parsed)) limit = Math.Clamp(parsed, 1, 50);
                normalized["limit"] = limit.ToString();
                summary = "查看我的通知";
                break;
            }

            case "cancel_my_task":
            {
                var taskId = (args.GetValueOrDefault("task_id") ?? "").Trim();
                if (taskId.Length > 0 && (taskId.Length > 64 || !taskId.StartsWith("t_", StringComparison.OrdinalIgnoreCase)))
                    return Deny("任务号格式非法（形如 t_xxxxxxxx）");

                normalized["task_id"] = taskId;
                normalized["reason"] = (args.GetValueOrDefault("reason") ?? "").Trim();

                //撤回无副作用可免审批
                outcome.SandboxAutoAllowed = !injectionSuspected;
                outcome.Translations.Add("撤回申请无副作用（只取消排队，不执行任何动作）→ 允许免人工审批");

                summary = taskId.Length > 0 ? $"撤回我的申请：{taskId}" : "撤回我最新一条待审批申请";
                break;
            }

            case "delete_file":
            {
                var check = await CheckFileAccessAsync(userId, role, args, requireShareWrite: true);
                if (check.Error is not null) return Deny(check.Error);
                if (string.IsNullOrEmpty(check.Relative))
                    return Deny("必须指定要删除的文件。系统**不支持批量/整目录删除**：请一次指定一个文件，每条删除都会单独审批并留存快照。");
                if (Directory.Exists(check.FullPath) && !File.Exists(check.FullPath))
                    return Deny("目标是一个目录。系统**不支持批量/整目录删除**（避免一次误操作清空工作区）：请逐个文件删除，每次都会单独审批。");
                if (!File.Exists(check.FullPath)) return Deny("目标文件不存在");

                var verdict = ContentRiskScanner.DetectExecutable(check.Relative, null);

                if (check.Scope == "share")
                {
                    outcome.RequiresApproval = true;
                    outcome.RiskLevel = Risk.Max(outcome.RiskLevel, Risk.High);
                    outcome.Reasons.Add("删除共享工作区的文件会影响其他成员");
                }

                if (verdict.IsExecutable)
                {
                    outcome.RequiresApproval = true;
                    outcome.RiskLevel = Risk.Max(outcome.RiskLevel, Risk.High);
                    outcome.Reasons.Add("目标是可执行/脚本文件（" + verdict.Summary + "），删除需要人工确认");
                    outcome.Translations.Add("可执行性判定：" + verdict.Summary);
                }

                //私人区普通文件可直删
                if (check.Scope == "private" && !verdict.IsExecutable && !injectionSuspected)
                {
                    outcome.SandboxAutoAllowed = true;
                    outcome.Translations.Add("私人工作区普通文件删除 → 允许免人工审批（删除前已存快照，可一键回退）");
                }

                normalized["scope"] = check.Scope;
                normalized["path"] = check.Relative;
                summary = $"删除文件：{Describe(check.Scope, check.Relative)}";
                break;
            }

            case "query_records":
            {
                var dataset = args.GetValueOrDefault("dataset") ?? "";
                if (!DatasetCatalog.IsKnown(dataset))
                    return Deny("dataset 只能是 customers / orders / contracts");

                var limit = 20;
                if (args.TryGetValue("limit", out var limitRaw) && !string.IsNullOrWhiteSpace(limitRaw))
                {
                    if (!int.TryParse(limitRaw, out limit) || limit < 1)
                        return Deny("limit 必须是正整数");
                    limit = Math.Min(limit, 50);
                }

                normalized["dataset"] = dataset.ToLowerInvariant();
                normalized["limit"] = limit.ToString();
                foreach (var key in new[] { "field", "op", "value" })
                    if (args.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
                        normalized[key] = v.Trim();

                if (normalized.ContainsKey("field"))
                {
                    var schema = DatasetCatalog.All[dataset];
                    if (schema.Field(normalized["field"]) is null)
                        return Deny($"数据集 {schema.Label} 不存在字段 {Sanitize(normalized["field"])}");
                }

                summary = $"查询数据：{dataset}" + (normalized.TryGetValue("field", out var f) ? $"（{f} {normalized.GetValueOrDefault("op", "eq")} {normalized.GetValueOrDefault("value", "")}）" : "");
                break;
            }

            case "update_record":
            {
                var dataset = args.GetValueOrDefault("dataset") ?? "";
                if (!DatasetCatalog.IsKnown(dataset)) return Deny("dataset 只能是 customers / orders / contracts");

                var schema = DatasetCatalog.All[dataset];
                var id = (args.GetValueOrDefault("id") ?? "").Trim();
                var field = (args.GetValueOrDefault("field") ?? "").Trim();
                var value = args.GetValueOrDefault("value") ?? "";

                if (id.Length is 0 or > 24) return Deny("记录主键格式非法");
                var valueError = _datasets.ValidateValue(schema, field, value, out var normalizedValue);
                if (valueError is not null) return Deny(valueError);

                var row = await _datasets.FindRowAsync(dataset, id).ConfigureAwait(false);
                if (row is null) return Deny($"记录不存在：{dataset}/{Sanitize(id)}");

                normalized["dataset"] = dataset.ToLowerInvariant();
                normalized["id"] = id;
                normalized["field"] = field;
                normalized["value"] = normalizedValue;
                summary = $"修改数据：{schema.Label} {id} 的 {field} 改为 {normalizedValue}";
                break;
            }

            case "delete_record":
            {
                var dataset = args.GetValueOrDefault("dataset") ?? "";
                if (!DatasetCatalog.IsKnown(dataset)) return Deny("dataset 只能是 customers / orders / contracts");

                var schema = DatasetCatalog.All[dataset];
                var id = (args.GetValueOrDefault("id") ?? "").Trim();
                var reason = (args.GetValueOrDefault("reason") ?? "").Trim();
                if (reason.Length < 2) return Deny("删除业务数据必须填写不少于 2 个字符的原因");

                var row = await _datasets.FindRowAsync(dataset, id).ConfigureAwait(false);
                if (row is null) return Deny($"记录不存在：{dataset}/{Sanitize(id)}");

                normalized["dataset"] = dataset.ToLowerInvariant();
                normalized["id"] = id;
                normalized["reason"] = reason;
                summary = $"删除数据：{schema.Label} 中的 {id}（原因：{reason}）";
                break;
            }

            case "send_notice":
            {
                var to = (args.GetValueOrDefault("to") ?? "").Trim().ToLowerInvariant();
                if (to is not ("self" or "approver"))
                    return Deny("收件人只允许 self（本人）或 approver（安全审批人）");

                var subject = (args.GetValueOrDefault("subject") ?? "").Trim();
                var body = args.GetValueOrDefault("body") ?? "";
                if (subject.Length is 0 or > 80) return Deny("标题长度需在 1-80 字符之间");
                if (body.Length is 0 or > 1000) return Deny("正文长度需在 1-1000 字符之间");

                if (to == "self")
                {
                    var user = await _users.FindByIdAsync(userId).ConfigureAwait(false);
                    var email = user is null ? "" : _users.DecryptEmail(user);
                    if (string.IsNullOrWhiteSpace(email))
                        return Deny("你的账号尚未登记邮箱，无法发送给自己（可由管理员在后台补充）");
                }
                else if (string.IsNullOrWhiteSpace(_cfg.Mail.ApproverAddress))
                {
                    return Deny("系统未配置安全审批人邮箱");
                }

                normalized["to"] = to;
                normalized["subject"] = subject;
                normalized["body"] = body;
                summary = $"发送通知给 {(to == "self" ? "本人" : "安全审批人")}：{subject}";
                break;
            }

            //检索与统计
            case "search_files":
            {
                var scope = (args.GetValueOrDefault("scope") ?? "private").Trim().ToLowerInvariant();
                if (scope is not ("private" or "share")) return Deny("scope 只能是 private 或 share");
                if (scope == "share" && !ToolCatalog.HasPermission(role, Permissions.ShareRead))
                    return Deny($"当前角色（{Roles.Label(role)}）无权检索共享文件区");

                var keyword = (args.GetValueOrDefault("keyword") ?? "").Trim();
                var content = (args.GetValueOrDefault("content") ?? "").Trim();
                var ext = (args.GetValueOrDefault("ext") ?? "").Trim().TrimStart('.');
                var path = (args.GetValueOrDefault("path") ?? "").Trim();

                if (keyword.Length > 60 || content.Length > 60) return Deny("关键字过长（≤60 字符）");
                if (ext.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(ext, "^[A-Za-z0-9]{1,8}$"))
                    return Deny("扩展名只能是字母数字（例如 txt / md）");

                var resolved = _paths.Resolve(userId, scope, path);
                if (!resolved.Ok) return Deny(resolved.Error);

                var limit = 30;
                if (args.TryGetValue("limit", out var limitRaw) && !string.IsNullOrWhiteSpace(limitRaw)
                    && int.TryParse(limitRaw, out var parsed)) limit = Math.Clamp(parsed, 1, 100);

                normalized["scope"] = scope;
                normalized["path"] = resolved.Relative;
                if (keyword.Length > 0) normalized["keyword"] = keyword;
                if (content.Length > 0) normalized["content"] = content;
                if (ext.Length > 0) normalized["ext"] = ext.ToLowerInvariant();
                normalized["limit"] = limit.ToString();

                summary = "检索文件：" + (keyword.Length > 0 ? $"文件名含「{Sanitize(keyword)}」" : "全部文件")
                          + (content.Length > 0 ? $"、内容含「{Sanitize(content)}」" : "")
                          + (ext.Length > 0 ? $"、扩展名 {ext}" : "")
                          + $"（{(scope == "share" ? "共享区" : "个人区")}/{resolved.Relative}）";
                break;
            }

            case "read_files":
            {
                var scope = (args.GetValueOrDefault("scope") ?? "private").Trim().ToLowerInvariant();
                if (scope is not ("private" or "share")) return Deny("scope 只能是 private 或 share");
                if (scope == "share" && !ToolCatalog.HasPermission(role, Permissions.ShareRead))
                    return Deny($"当前角色（{Roles.Label(role)}）无权读取共享文件区");

                var raw = (args.GetValueOrDefault("paths") ?? "").Trim();
                if (raw.Length == 0) return Deny("请提供要读取的文件路径列表（最多 5 个，逗号分隔）");

                var parts = raw.Split(new[] { ',', '，', ';', '；', '、', '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 0) return Deny("请提供要读取的文件路径列表");
                if (parts.Length > 5) return Deny($"一次最多读取 5 个文件（收到 {parts.Length} 个）");

                var canonicalPaths = new List<string>();
                foreach (var part in parts)
                {
                    var resolved = _paths.Resolve(userId, scope, part);
                    if (!resolved.Ok) return Deny(resolved.Error);
                    if (string.IsNullOrEmpty(resolved.Relative)) return Deny("必须指定文件名");
                    if (!File.Exists(resolved.FullPath)) return Deny($"文件不存在：{resolved.Relative}（可先用 search_files 找到正确路径）");
                    canonicalPaths.Add(resolved.Relative);
                }

                normalized["scope"] = scope;
                normalized["paths"] = string.Join(",", canonicalPaths);
                summary = $"批量读取 {canonicalPaths.Count} 个文件：{string.Join("、", canonicalPaths)}";
                break;
            }

            case "count_records":
            {
                var dataset = (args.GetValueOrDefault("dataset") ?? "").Trim();
                if (dataset.Length > 0 && !DatasetCatalog.IsKnown(dataset))
                    return Deny("dataset 不是已知的数据集（或留空统计全部）");

                //统计对所有角色开放
                normalized["dataset"] = dataset.ToLowerInvariant();
                normalized["scope"] = "all";

                summary = "统计业务数据条数" + (dataset.Length > 0 ? $"（{dataset}）" : "（全部）");
                break;
            }

            case "list_users":
            {
                var keyword = (args.GetValueOrDefault("keyword") ?? "").Trim();
                if (keyword.Length > 40) return Deny("关键字过长（≤40 字符）");

                var userRole = (args.GetValueOrDefault("role") ?? "").Trim().ToLowerInvariant();
                if (userRole.Length > 0 && !Roles.IsValid(userRole))
                    return Deny("role 只能是 user / staff / admin（或留空）");

                var limit = 50;
                if (args.TryGetValue("limit", out var limitRaw) && !string.IsNullOrWhiteSpace(limitRaw)
                    && int.TryParse(limitRaw, out var parsed)) limit = Math.Clamp(parsed, 1, 100);

                normalized["keyword"] = keyword;
                normalized["role"] = userRole;
                normalized["limit"] = limit.ToString();
                summary = $"查询账号名册（{Roles.Label(role)}）"
                          + (keyword.Length > 0 ? $"：关键字「{Sanitize(keyword)}」" : "")
                          + (userRole.Length > 0 ? $"，角色 {Roles.Label(userRole)}" : "");
                break;
            }

            case "my_tasks":
                summary = "查看本人任务列表";
                break;

            case "my_profile":
                summary = "查看本人权限";
                break;

            case "a_pending_tasks":
                summary = "（管理端）拉取待审批任务清单";
                break;

            case "a_task_detail":
            {
                var taskId = (args.GetValueOrDefault("task_id") ?? "").Trim();
                if (taskId.Length is 0 or > 64) return Deny("task_id 格式非法");
                normalized["task_id"] = taskId;
                summary = $"（管理端）读取任务 {taskId} 的完整上下文";
                break;
            }

            case "a_audit_search":
            {
                var keyword = (args.GetValueOrDefault("keyword") ?? "").Trim();
                if (keyword.Length > 60) return Deny("关键字过长");
                var limit = 30;
                if (args.TryGetValue("limit", out var l) && !string.IsNullOrWhiteSpace(l) && int.TryParse(l, out var parsed))
                    limit = Math.Clamp(parsed, 1, 100);
                normalized["keyword"] = keyword;
                normalized["limit"] = limit.ToString();
                summary = $"（管理端）审计检索：{Sanitize(keyword)}";
                break;
            }

            case "a_activity_summary":
            {
                var user = (args.GetValueOrDefault("user") ?? "").Trim();
                if (user.Length > 32) return Deny("用户名过长");
                var days = 7;
                if (args.TryGetValue("days", out var d) && !string.IsNullOrWhiteSpace(d) && int.TryParse(d, out var parsedDays))
                    days = Math.Clamp(parsedDays, 1, 90);
                var limit = 20;
                if (args.TryGetValue("limit", out var l2) && !string.IsNullOrWhiteSpace(l2) && int.TryParse(l2, out var parsedLimit))
                    limit = Math.Clamp(parsedLimit, 1, 50);

                normalized["user"] = user;
                normalized["days"] = days.ToString();
                normalized["limit"] = limit.ToString();
                summary = $"（管理端）使用情况汇总：最近 {days} 天" + (user.Length > 0 ? $"，账号 {Sanitize(user)}" : "，全部账号");
                break;
            }

            case "a_task_list":
            {
                var status = (args.GetValueOrDefault("status") ?? "").Trim();
                if (status.Length > 0 && status is not ("PENDING_APPROVAL" or "EXECUTING" or "EXECUTED"
                    or "REJECTED_RULE" or "REJECTED_AI" or "REJECTED_ADMIN" or "EXPIRED" or "FAILED"))
                    return Deny("status 取值非法（可用：PENDING_APPROVAL/EXECUTING/EXECUTED/REJECTED_RULE/REJECTED_AI/REJECTED_ADMIN/EXPIRED/FAILED）");

                var user = (args.GetValueOrDefault("user") ?? "").Trim();
                var filterTool = (args.GetValueOrDefault("tool") ?? "").Trim();
                if (user.Length > 32) return Deny("用户名过长");
                if (filterTool.Length > 40) return Deny("工具名过长");

                var limit = 20;
                if (args.TryGetValue("limit", out var l3) && !string.IsNullOrWhiteSpace(l3) && int.TryParse(l3, out var parsedLimit2))
                    limit = Math.Clamp(parsedLimit2, 1, 100);

                normalized["status"] = status;
                normalized["user"] = user;
                normalized["tool"] = filterTool;
                normalized["limit"] = limit.ToString();
                summary = $"（管理端）任务清单：{(status.Length > 0 ? status : "全部状态")}"
                          + (user.Length > 0 ? $"，提交人 {Sanitize(user)}" : "")
                          + (filterTool.Length > 0 ? $"，工具 {Sanitize(filterTool)}" : "");
                break;
            }

            default:
                return Deny("该工具尚未接入规则引擎");
        }

        //疑似注入强制转人工
        if (injectionSuspected && tool.StateChanging)
        {
            outcome.RequiresApproval = true;
            outcome.Reasons.Add("输入包含疑似提示词注入/越权诱导特征，需人工确认");
        }

        var canonical = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in normalized) canonical[kv.Key] = kv.Value;
        outcome.NormalizedArgs = normalized;
        outcome.CanonicalAction = AppJson.Serialize(new { tool = tool.Name, args = canonical });

        if (tool.RequiresApproval && outcome.Reasons.Count == 0)
            outcome.Reasons.Add("该操作按策略必须经人工审批");

        return new RuleEvaluation { Outcome = outcome, ActionSummary = summary };
    }

    //校验文件访问
    private Task<(string? Error, string FullPath, string Relative, string Scope, bool IsDirectory)> CheckFileAccessAsync(
        string userId, string role, Dictionary<string, string> args, bool requireShareWrite)
    {
        var scope = (args.GetValueOrDefault("scope") ?? "private").Trim().ToLowerInvariant();
        if (scope is not ("private" or "share"))
            return Task.FromResult<(string?, string, string, string, bool)>(("scope 只能是 private 或 share", "", "", "", false));

        if (scope == "share")
        {
            var needed = requireShareWrite ? Permissions.ShareWrite : Permissions.ShareRead;
            if (!ToolCatalog.HasPermission(role, needed))
                return Task.FromResult<(string?, string, string, string, bool)>(($"当前角色（{Roles.Label(role)}）无权访问共享文件区", "", "", "", false));
        }

        var resolved = _paths.Resolve(userId, scope, args.GetValueOrDefault("path"));
        if (!resolved.Ok)
            return Task.FromResult<(string?, string, string, string, bool)>((resolved.Error, "", "", scope, false));

        foreach (var marker in ProtectedPathMarkers)
        {
            if (resolved.Relative.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<(string?, string, string, string, bool)>(($"路径命中系统保护关键字（{marker}），已拒绝", "", "", scope, false));
        }

        var isDir = Directory.Exists(resolved.FullPath);
        return Task.FromResult<(string?, string, string, string, bool)>((null, resolved.FullPath, resolved.Relative, scope, isDir));
    }

    //返回拒绝
    private static RuleEvaluation Deny(string reason) => new()
    {
        Outcome = new RuleOutcome
        {
            Decision = "DENY",
            RiskLevel = Risk.High,
            Reasons = { reason },
        },
        ActionSummary = "已被规则引擎拒绝",
    };

    //规范化参数
    private static Dictionary<string, string> Normalize(Dictionary<string, string>? raw)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (raw is null) return result;

        foreach (var kv in raw)
        {
            if (string.IsNullOrWhiteSpace(kv.Key)) continue;
            var key = kv.Key.Trim();
            if (key.Length > 40) continue;
            result[key] = (kv.Value ?? "").Trim();
        }
        return result;
    }

    //拼区域和路径
    private static string Describe(string scope, string relative)
        => (scope == "share" ? "共享区/" : "个人区/") + (string.IsNullOrEmpty(relative) ? "" : relative);

    //写入模式中文名
    private static string ModeLabel(string mode) => mode switch
    {
        "create" => "新建",
        "overwrite" => "覆盖",
        "append" => "追加",
        _ => "写入",
    };

    //只留可打印字符
    private static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var cleaned = new string(value.Where(c => !char.IsControl(c)).ToArray());
        return cleaned.Length <= 60 ? cleaned : cleaned[..60] + "...";
    }

    //需要澄清：交回候选
    private static RuleEvaluation Ask(string question, List<string> candidates) => new()
    {
        Outcome = new RuleOutcome
        {
            Decision = "ASK",
            RiskLevel = Risk.Low,
            Reasons = { question },
            Translations = candidates,
        },
        ActionSummary = "信息不足，需要澄清：" + question,
    };

    //候选解析结果
    private sealed class ResolveResult
    {
        public Dictionary<string, string>? Row { get; init; }
        public string? Error { get; init; }
        public List<Dictionary<string, string>> Candidates { get; init; } = new();
        public string Translation { get; init; } = "";
        public string Keyword { get; init; } = "";
    }

    //渲染客户候选
    private static List<string> DescribeClients(IEnumerable<Dictionary<string, string>> rows)
        => rows.Select(r => $"{Get(r, "id")} {Get(r, "name")}（{Get(r, "status")}，负责人 {(Get(r, "owner").Length == 0 ? "未指派" : Get(r, "owner"))}）").ToList();

    //渲染需求候选
    private static List<string> DescribeRequirements(IEnumerable<Dictionary<string, string>> rows)
        => rows.Select(r => $"{Get(r, "id")} {Get(r, "title")}（{Get(r, "status")}，甲方 {Get(r, "customer_id")}）").ToList();

    //字段名口语收敛
    private static string MapFieldName(string dataset, string field)
    {
        if (!DatasetCatalog.IsKnown(dataset)) return field;

        var schema = DatasetCatalog.All[dataset];
        var name = (field ?? "").Trim();
        if (name.Length == 0) return name;

        var direct = schema.Fields.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (direct is not null) return direct.Name;

        var aliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = new[] { "公司名称", "公司名", "客户名称", "客户名", "名字", "名称" },
            ["contact"] = new[] { "联系人", "对接人", "联系对象" },
            ["phone"] = new[] { "电话", "联系电话", "手机", "手机号" },
            ["industry"] = new[] { "行业", "所属行业", "领域" },
            ["level"] = new[] { "等级", "客户等级", "级别", "重要性" },
            ["status"] = new[] { "状态", "合作状态", "需求状态", "进展" },
            ["owner"] = new[] { "负责人", "负责员工", "跟进人", "归属" },
            ["requirement"] = new[] { "需求", "需求摘要", "诉求" },
            ["remark"] = new[] { "备注", "说明", "附注" },
            ["title"] = new[] { "标题", "需求标题" },
            ["detail"] = new[] { "描述", "需求描述", "详情", "内容" },
            ["budget"] = new[] { "预算", "金额", "报价" },
            ["expect_date"] = new[] { "期望交付日", "交付日期", "期望日期", "交付时间" },
        };

        foreach (var kv in aliases)
        {
            if (!schema.Fields.Any(f => f.Name.Equals(kv.Key, StringComparison.OrdinalIgnoreCase) && f.Writable)) continue;
            if (kv.Value.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase))) return kv.Key;
        }

        foreach (var kv in aliases)
        {
            if (!schema.Fields.Any(f => f.Name.Equals(kv.Key, StringComparison.OrdinalIgnoreCase) && f.Writable)) continue;
            if (kv.Value.Any(a => name.Contains(a, StringComparison.OrdinalIgnoreCase))) return kv.Key;
        }

        return name;
    }

    //取字段值
    private static string Get(Dictionary<string, string> row, string field)
    {
        var kv = row.FirstOrDefault(x => string.Equals(x.Key, field, StringComparison.OrdinalIgnoreCase));
        return kv.Key is null ? "" : kv.Value;
    }
}
