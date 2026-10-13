using System.Text;
using AiApproval.Core;
using AiApproval.Security;
using AiApproval.Services;

namespace AiApproval.Tools;

//记录怎么撤销这一步
public sealed class UndoLog
{
    public string Kind { get; set; } = "file";     //file | dataset | notice
    public string Scope { get; set; } = "private";
    public string Relative { get; set; } = "";
    public bool Existed { get; set; }
    public string Dataset { get; set; } = "";
    public string Id { get; set; } = "";
    public string RowJson { get; set; } = "";
    public string Field { get; set; } = "";
    public string OldValue { get; set; } = "";
    public bool Inserted { get; set; }
    public string NoticeId { get; set; } = "";
    public string RollbackOf { get; set; } = "";
    //改名后的新名字
    public string RenamedTo { get; set; } = "";
}

public sealed class HarnessResult
{
    public bool Ok { get; init; }
    public string Summary { get; init; } = "";
    public object? Data { get; init; }
    public List<string> VersionIds { get; init; } = new();
}

//工具执行层
public sealed class Harness
{
    private const int MaxReadBytes = 131072;

    private readonly PathGuard _paths;
    private readonly DatasetStore _datasets;
    private readonly VersionStore _versions;
    private readonly Outbox _outbox;
    private readonly UserService _users;
    private readonly AuditLog _audit;
    private readonly AppConfig _cfg;
    private readonly JsonStore _store;

    public Harness(PathGuard paths, DatasetStore datasets, VersionStore versions, Outbox outbox,
        UserService users, AuditLog audit, AppConfig cfg, JsonStore store)
    {
        _paths = paths;
        _datasets = datasets;
        _versions = versions;
        _outbox = outbox;
        _users = users;
        _audit = audit;
        _cfg = cfg;
        _store = store;
    }

    //按工具名分发
    public async Task<HarnessResult> ExecuteAsync(AgentTask task, CancellationToken ct = default)
    {
        var args = task.Rule.NormalizedArgs;
        var tool = ToolCatalog.Get(task.ToolName);
        if (tool is null) return Fail("工具不在目录中");

        try
        {
            return task.ToolName switch
            {
                "list_files" => ListFiles(task, args),
                "read_file" => await ReadFileAsync(task, args).ConfigureAwait(false),
                "write_file" => await WriteFileAsync(task, args).ConfigureAwait(false),
                "delete_file" => await DeleteFileAsync(task, args).ConfigureAwait(false),
                "query_records" => await QueryAsync(task, args).ConfigureAwait(false),
                "update_record" => await UpdateRecordAsync(task, args).ConfigureAwait(false),
                "delete_record" => await DeleteRecordAsync(task, args).ConfigureAwait(false),
                "send_notice" => await SendNoticeAsync(task, args).ConfigureAwait(false),
                //检索与统计
                "search_files" => await SearchFilesAsync(task, args).ConfigureAwait(false),
                "read_files" => await ReadFilesAsync(task, args).ConfigureAwait(false),
                "rename_file" => await RenameFileAsync(task, args).ConfigureAwait(false),
                "create_folder" => await CreateFolderAsync(task, args).ConfigureAwait(false),
                "get_file_info" => FileInfoOf(task, args),
                "my_workspace" => await MyWorkspaceAsync(task).ConfigureAwait(false),
                "my_notifications" => await MyNotificationsAsync(task, args).ConfigureAwait(false),
                "cancel_my_task" => await CancelMyTaskAsync(task, args).ConfigureAwait(false),
                "a_activity_summary" => await AdminActivitySummaryAsync(task, args).ConfigureAwait(false),
                "a_task_list" => await AdminTaskListAsync(task, args).ConfigureAwait(false),
                "count_records" => await CountRecordsAsync(task, args).ConfigureAwait(false),
                "list_users" => await ListUsersAsync(task, args).ConfigureAwait(false),
                "my_profile" => await ProfileAsync(task).ConfigureAwait(false),
                "my_tasks" => new HarnessResult { Ok = true, Summary = "任务列表由任务服务直接返回", Data = null },
                _ => Fail("该工具由上层服务处理，未接入 Harness"),
            };
        }
        catch (Exception ex)
        {
            await _audit.WriteAsync(task.UserId, task.UserName, task.UserRole, "", "harness.error",
                task.ToolName, "ERROR", ex.Message).ConfigureAwait(false);
            return Fail($"执行失败：{ex.Message}");
        }
    }

    //只读工具

    //列目录内容
    private HarnessResult ListFiles(AgentTask task, Dictionary<string, string> args)
    {
        var scope = args.GetValueOrDefault("scope") ?? "private";
        var resolved = _paths.Resolve(task.UserId, scope, args.GetValueOrDefault("path"));
        if (!resolved.Ok) return Fail(resolved.Error);
        if (!Directory.Exists(resolved.FullPath)) return Fail("目录不存在");

        var entries = new List<object>();
        foreach (var dir in Directory.EnumerateDirectories(resolved.FullPath).OrderBy(d => d, StringComparer.Ordinal).Take(200))
        {
            var info = new DirectoryInfo(dir);
            entries.Add(new
            {
                name = info.Name,
                type = "dir",
                size = 0,
                modified = info.LastWriteTimeUtc,
                link = info.LinkTarget is not null,
            });
        }

        foreach (var file in Directory.EnumerateFiles(resolved.FullPath).OrderBy(f => f, StringComparer.Ordinal).Take(200))
        {
            var info = new FileInfo(file);
            entries.Add(new
            {
                name = info.Name,
                type = "file",
                size = info.Length,
                modified = info.LastWriteTimeUtc,
                link = info.LinkTarget is not null,
            });
        }

        return new HarnessResult
        {
            Ok = true,
            Summary = $"列出 {(scope == "share" ? "共享区" : "个人区")}/{resolved.Relative}，共 {entries.Count} 项",
            Data = new { scope, path = resolved.Relative, entries },
        };
    }

    //读文件
    private async Task<HarnessResult> ReadFileAsync(AgentTask task, Dictionary<string, string> args)
    {
        var scope = args.GetValueOrDefault("scope") ?? "private";
        var resolved = _paths.Resolve(task.UserId, scope, args.GetValueOrDefault("path"));
        if (!resolved.Ok) return Fail(resolved.Error);

        var info = new FileInfo(resolved.FullPath);
        if (!info.Exists)
        {
            //给相近文件名提示
            var dir = Path.GetDirectoryName(resolved.FullPath);
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                var stem = Path.GetFileNameWithoutExtension(resolved.Relative);
                candidates = Directory.EnumerateFiles(dir)
                    .Select(Path.GetFileName)
                    .Where(n => n is not null && stem.Length > 0 && n!.Contains(stem, StringComparison.OrdinalIgnoreCase))
                    .Take(5)
                    .ToList()!;
            }

            return Fail($"文件不存在：{resolved.Relative}"
                        + (candidates.Count > 0 ? $"；目录里相近的文件有：{string.Join("、", candidates)}" : "；可先用 search_files 按关键字检索"));
        }
        if (info.Length > MaxReadBytes) return Fail($"文件过大（{info.Length} 字节，上限 {MaxReadBytes}）");

        var bytes = await File.ReadAllBytesAsync(resolved.FullPath).ConfigureAwait(false);
        if (bytes.Contains((byte)0))
        {
            var head = string.Join(" ", bytes.Take(8).Select(b => b.ToString("X2")));
            return Fail($"这是二进制文件（首字节 {head}…），文本读取工具无法展示。"
                        + "如需要了解内容，可先查看文件信息（get_file_info 看大小/哈希），或用专门的下载通道取回本地查看。");
        }

        var content = new UTF8Encoding(false).GetString(bytes);
        if (content.Length > 20000) content = content[..20000] + "\n...（内容过长，已截断展示）";

        return new HarnessResult
        {
            Ok = true,
            Summary = $"读取 {(scope == "share" ? "共享区" : "个人区")}/{resolved.Relative}（{bytes.Length} 字节）",
            Data = new { scope, path = resolved.Relative, size = bytes.Length, sha256 = Crypto.Sha256Hex(bytes), content },
        };
    }

    //查业务数据
    private async Task<HarnessResult> QueryAsync(AgentTask task, Dictionary<string, string> args)
    {
        var (ok, error, rows, query) = await _datasets.QueryAsync(
            args.GetValueOrDefault("dataset") ?? "",
            args.GetValueOrDefault("field"),
            args.GetValueOrDefault("op"),
            args.GetValueOrDefault("value"),
            int.TryParse(args.GetValueOrDefault("limit"), out var limit) ? limit : 20).ConfigureAwait(false);

        if (!ok) return Fail(error);
        return new HarnessResult
        {
            Ok = true,
            Summary = $"查询命中 {rows.Count} 条记录",
            Data = new { query, columns = rows.Count > 0 ? rows[0].Keys.ToList() : new List<string>(), rows },
        };
    }

    //看我的资料
    private async Task<HarnessResult> ProfileAsync(AgentTask task)
    {
        var user = await _users.FindByIdAsync(task.UserId).ConfigureAwait(false);
        if (user is null) return Fail("账号不存在");

        return new HarnessResult
        {
            Ok = true,
            Summary = $"当前角色：{Roles.Label(user.Role)}",
            Data = new
            {
                userName = user.UserName,
                displayName = user.DisplayName,
                role = user.Role,
                roleLabel = Roles.Label(user.Role),
                emailMasked = user.EmailMasked,
                permissions = ToolCatalog.PermissionsOf(user.Role).Select(p => new { key = p, label = Permissions.Label(p) }).ToList(),
            },
        };
    }

    //写操作（先落快照）

    //写文件
    private async Task<HarnessResult> WriteFileAsync(AgentTask task, Dictionary<string, string> args)
    {
        var scope = args.GetValueOrDefault("scope") ?? "private";
        var resolved = _paths.Resolve(task.UserId, scope, args.GetValueOrDefault("path"));
        if (!resolved.Ok) return Fail(resolved.Error);

        var mode = args.GetValueOrDefault("mode") ?? "create";
        var content = args.GetValueOrDefault("content") ?? "";

        var exists = File.Exists(resolved.FullPath);
        var previous = exists ? await File.ReadAllBytesAsync(resolved.FullPath).ConfigureAwait(false) : null;

        var newBytes = mode switch
        {
            "append" => Concat(previous, Encoding.UTF8.GetBytes(content)),
            _ => Encoding.UTF8.GetBytes(content),
        };

        var directory = Path.GetDirectoryName(resolved.FullPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        await File.WriteAllBytesAsync(resolved.FullPath, newBytes).ConfigureAwait(false);

        var version = await _versions.RecordAsync(task.Id, task.UserId, task.UserName, "file",
            exists ? (mode == "append" ? "append" : "overwrite") : "create",
            $"{scope}:{resolved.Relative}", previous, newBytes,
            AppJson.Serialize(new UndoLog
            {
                Kind = "file",
                Scope = scope,
                Relative = resolved.Relative,
                Existed = exists,
            })).ConfigureAwait(false);

        return new HarnessResult
        {
            Ok = true,
            Summary = $"{(exists ? "已更新" : "已创建")}文件 {(scope == "share" ? "共享区" : "个人区")}/{resolved.Relative}（{newBytes.Length} 字节）",
            Data = new
            {
                scope,
                path = resolved.Relative,
                size = newBytes.Length,
                sha256 = Crypto.Sha256Hex(newBytes),
                diff = version.DiffText,
                addedLines = version.AddedLines,
                removedLines = version.RemovedLines,
            },
            VersionIds = { version.Id },
        };
    }

    //删文件
    private async Task<HarnessResult> DeleteFileAsync(AgentTask task, Dictionary<string, string> args)
    {
        var scope = args.GetValueOrDefault("scope") ?? "private";
        var resolved = _paths.Resolve(task.UserId, scope, args.GetValueOrDefault("path"));
        if (!resolved.Ok) return Fail(resolved.Error);

        var info = new FileInfo(resolved.FullPath);
        if (!info.Exists) return Fail("文件不存在");

        var previous = await File.ReadAllBytesAsync(resolved.FullPath).ConfigureAwait(false);
        File.Delete(resolved.FullPath);

        var version = await _versions.RecordAsync(task.Id, task.UserId, task.UserName, "file", "delete",
            $"{scope}:{resolved.Relative}", previous, null,
            AppJson.Serialize(new UndoLog
            {
                Kind = "file",
                Scope = scope,
                Relative = resolved.Relative,
                Existed = true,
            })).ConfigureAwait(false);

        return new HarnessResult
        {
            Ok = true,
            Summary = $"已删除文件 {(scope == "share" ? "共享区" : "个人区")}/{resolved.Relative}（快照已保存，可一键回退）",
            Data = new { scope, path = resolved.Relative, deletedSize = previous.Length, snapshotSha256 = version.PreviousHash },
            VersionIds = { version.Id },
        };
    }

    //改记录字段
    private async Task<HarnessResult> UpdateRecordAsync(AgentTask task, Dictionary<string, string> args)
    {
        var dataset = args.GetValueOrDefault("dataset") ?? "";
        var id = args.GetValueOrDefault("id") ?? "";
        var field = args.GetValueOrDefault("field") ?? "";
        var value = args.GetValueOrDefault("value") ?? "";

        var before = await _datasets.FindRowAsync(dataset, id).ConfigureAwait(false);
        if (before is null) return Fail("记录不存在");

        var (ok, error, oldValue, rowNow) = await _datasets.UpdateFieldAsync(dataset, id, field, value).ConfigureAwait(false);
        if (!ok) return Fail(error);

        var undo = AppJson.Serialize(new UndoLog
        {
            Kind = "dataset",
            Dataset = dataset,
            Id = id,
            Field = field,
            OldValue = oldValue,
            RowJson = AppJson.Serialize(before, disk: true),
        });

        var version = await _versions.RecordAsync(task.Id, task.UserId, task.UserName, "dataset", "update",
            $"{dataset}/{id}", Encoding.UTF8.GetBytes(AppJson.Serialize(before, disk: true)),
            Encoding.UTF8.GetBytes(AppJson.Serialize(rowNow ?? before, disk: true)), undo).ConfigureAwait(false);

        return new HarnessResult
        {
            Ok = true,
            Summary = $"已修改 {dataset}/{id} 的 {field}：{oldValue} → {value}",
            Data = new
            {
                dataset, id, field,
                oldValue, newValue = value,
                reverseSql = $"UPDATE {dataset} SET {field} = '{oldValue}' WHERE id = '{id}';  -- 回退用（仅记录，不拼接执行）",
                diff = version.DiffText,
            },
            VersionIds = { version.Id },
        };
    }

    //删记录
    private async Task<HarnessResult> DeleteRecordAsync(AgentTask task, Dictionary<string, string> args)
    {
        var dataset = args.GetValueOrDefault("dataset") ?? "";
        var id = args.GetValueOrDefault("id") ?? "";
        var reason = args.GetValueOrDefault("reason") ?? "";

        var before = await _datasets.FindRowAsync(dataset, id).ConfigureAwait(false);
        if (before is null) return Fail("记录不存在");

        var (ok, error, rowJson) = await _datasets.DeleteRowAsync(dataset, id).ConfigureAwait(false);
        if (!ok) return Fail(error);

        var undo = AppJson.Serialize(new UndoLog
        {
            Kind = "dataset",
            Dataset = dataset,
            Id = id,
            RowJson = rowJson,
        });

        var version = await _versions.RecordAsync(task.Id, task.UserId, task.UserName, "dataset", "delete",
            $"{dataset}/{id}", Encoding.UTF8.GetBytes(AppJson.Serialize(before, disk: true)), null, undo).ConfigureAwait(false);

        return new HarnessResult
        {
            Ok = true,
            Summary = $"已删除 {dataset}/{id}（原因：{reason}，整行已转存，可一键还原）",
            Data = new
            {
                dataset, id, reason,
                reverseSql = $"INSERT INTO {dataset} VALUES ({DatasetStore.DescribeRow(before)});  -- 回退用（仅记录，不拼接执行）",
                rowBackup = before,
            },
            VersionIds = { version.Id },
        };
    }

    //发通知
    private async Task<HarnessResult> SendNoticeAsync(AgentTask task, Dictionary<string, string> args)
    {
        var to = args.GetValueOrDefault("to") ?? "self";
        var subject = args.GetValueOrDefault("subject") ?? "";
        var body = args.GetValueOrDefault("body") ?? "";

        string address;
        if (to == "self")
        {
            var user = await _users.FindByIdAsync(task.UserId).ConfigureAwait(false);
            address = user is null ? "" : _users.DecryptEmail(user);
        }
        else
        {
            address = _cfg.Mail.ApproverAddress;
        }

        if (string.IsNullOrWhiteSpace(address)) return Fail("收件人地址不可用");

        var message = await _outbox.EnqueueAsync(address, subject, body, task.Id, task.UserId, task.UserName, "").ConfigureAwait(false);

        var undo = AppJson.Serialize(new UndoLog { Kind = "notice", NoticeId = message.Id });
        var version = await _versions.RecordAsync(task.Id, task.UserId, task.UserName, "notice", "send",
            message.Id, null, Encoding.UTF8.GetBytes($"{subject}\n{body}"), undo, "recall_notice").ConfigureAwait(false);

        return new HarnessResult
        {
            Ok = true,
            Summary = $"通知已投递到出站箱（收件人：{(to == "self" ? "本人" : "安全审批人")}，状态 {message.Status}）",
            Data = new { noticeId = message.Id, to = to, addressMasked = Crypto.Mask(address), subject, status = message.Status },
            VersionIds = { version.Id },
        };
    }










    //取字段值
    private static string Get(Dictionary<string, string> row, string field)
    {
        var kv = row.FirstOrDefault(x => string.Equals(x.Key, field, StringComparison.OrdinalIgnoreCase));
        return kv.Key is null ? "" : kv.Value;
    }

    //检索与统计

    //按名/后缀/内容检索
    private async Task<HarnessResult> SearchFilesAsync(AgentTask task, Dictionary<string, string> args)
    {
        const int maxScanned = 800;
        const int maxContentBytes = 262144;   //单文件最多读 256KB
        const int maxPreviewPerFile = 3;

        var scope = args.GetValueOrDefault("scope") ?? "private";
        var resolved = _paths.Resolve(task.UserId, scope, args.GetValueOrDefault("path"));
        if (!resolved.Ok) return Fail(resolved.Error);
        if (!Directory.Exists(resolved.FullPath)) return Fail("目录不存在");

        var keyword = (args.GetValueOrDefault("keyword") ?? "").Trim();
        var content = (args.GetValueOrDefault("content") ?? "").Trim();
        var ext = (args.GetValueOrDefault("ext") ?? "").Trim().TrimStart('.');
        var limit = int.TryParse(args.GetValueOrDefault("limit"), out var parsedLimit) ? Math.Clamp(parsedLimit, 1, 100) : 30;

        var matches = new List<object>();
        var scanned = 0;

        foreach (var file in Directory.EnumerateFiles(resolved.FullPath, "*", SearchOption.AllDirectories))
        {
            if (++scanned > maxScanned) break;

            var info = new FileInfo(file);
            if (info.LinkTarget is not null) continue;                        //跳过软链接
            if (ext.Length > 0 && !info.Extension.TrimStart('.').Equals(ext, StringComparison.OrdinalIgnoreCase)) continue;
            if (keyword.Length > 0 && !info.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)) continue;

            string? preview = null;
            if (content.Length > 0)
            {
                if (info.Length > maxContentBytes) continue;

                byte[] bytes;
                try { bytes = await File.ReadAllBytesAsync(file).ConfigureAwait(false); }
                catch { continue; }
                if (bytes.Contains((byte)0)) continue;                        //跳过二进制

                var text = Encoding.UTF8.GetString(bytes);
                if (!text.Contains(content, StringComparison.OrdinalIgnoreCase)) continue;

                var lines = text.Replace("\r\n", "\n").Split('\n')
                    .Where(l => l.Contains(content, StringComparison.OrdinalIgnoreCase))
                    .Take(maxPreviewPerFile)
                    .Select(l => l.Trim().Length > 120 ? l.Trim()[..120] + "…" : l.Trim());
                preview = string.Join(" | ", lines);
            }

            matches.Add(new
            {
                path = Path.GetRelativePath(resolved.FullPath, file).Replace('\\', '/'),
                size = info.Length,
                modified = info.LastWriteTimeUtc,
                preview,
            });

            if (matches.Count >= limit) break;
        }

        var summary = $"检索到 {matches.Count} 个文件"
                      + (keyword.Length > 0 ? $"（文件名含「{keyword}」）" : "")
                      + (content.Length > 0 ? $"（内容含「{content}」）" : "")
                      + (scanned > maxScanned ? $"；已扫描 {maxScanned} 个文件后停止（请缩小范围）" : "");

        return new HarnessResult
        {
            Ok = true,
            Summary = summary,
            Data = new { scope, path = resolved.Relative, scanned, count = matches.Count, files = matches },
        };
    }

    //批量读文件
    private async Task<HarnessResult> ReadFilesAsync(AgentTask task, Dictionary<string, string> args)
    {
        const int maxPerFileChars = 8000;
        const int maxTotalChars = 24000;

        var scope = args.GetValueOrDefault("scope") ?? "private";
        var paths = (args.GetValueOrDefault("paths") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var results = new List<object>();
        var totalChars = 0;

        foreach (var path in paths)
        {
            var resolved = _paths.Resolve(task.UserId, scope, path);
            if (!resolved.Ok) return Fail(resolved.Error);

            var info = new FileInfo(resolved.FullPath);
            if (!info.Exists) return Fail($"文件不存在：{resolved.Relative}");

            string text;
            try
            {
                var bytes = await File.ReadAllBytesAsync(resolved.FullPath).ConfigureAwait(false);
                if (bytes.Contains((byte)0)) return Fail($"这是二进制文件，不支持在线读取：{resolved.Relative}");
                text = Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex)
            {
                return Fail($"读取失败：{resolved.Relative}（{ex.GetType().Name}）");
            }

            var truncated = text.Length > maxPerFileChars;
            var piece = truncated ? text[..maxPerFileChars] : text;
            if (totalChars + piece.Length > maxTotalChars) piece = piece[..Math.Max(0, maxTotalChars - totalChars)];
            totalChars += piece.Length;

            results.Add(new
            {
                path = resolved.Relative,
                size = info.Length,
                modified = info.LastWriteTimeUtc,
                truncated,
                content = piece,
            });

            if (totalChars >= maxTotalChars) break;
        }

        return new HarnessResult
        {
            Ok = true,
            Summary = $"已读取 {results.Count} 个文件（合计约 {totalChars} 字符）",
            Data = new { scope, count = results.Count, files = results },
        };
    }

    //业务数据统计
    private async Task<HarnessResult> CountRecordsAsync(AgentTask task, Dictionary<string, string> args)
    {
        var dataset = (args.GetValueOrDefault("dataset") ?? "").Trim().ToLowerInvariant();
        var scope = args.GetValueOrDefault("scope") ?? "all";
        var clientId = args.GetValueOrDefault("client_id") ?? "";
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var datasets = dataset.Length > 0
            ? new List<string> { dataset }
            : new List<string> { "customers", "requirements", "orders", "contracts" };

        foreach (var name in datasets)
        {
            var rows = await _datasets.RowsAsync(name).ConfigureAwait(false);

            if (scope == "mine")
            {
                if (name == "customers")
                    counts[name] = string.IsNullOrEmpty(clientId) ? 0 : rows.Count(r => Get(r, "id").Equals(clientId, StringComparison.OrdinalIgnoreCase));
                else
                    counts[name] = string.IsNullOrEmpty(clientId) ? 0 : rows.Count(r => Get(r, "customer_id").Equals(clientId, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                counts[name] = rows.Count;
            }
        }

        var total = counts.Values.Sum();
        var parts = counts.Select(kv => $"{DatasetCatalog.All[kv.Key].Label} {kv.Value} 条");
        var scopeNote = scope == "mine" ? "（范围：本公司）" : "（范围：全部）";

        return new HarnessResult
        {
            Ok = true,
            Summary = $"统计完成{scopeNote}：" + string.Join("，", parts),
            Data = new { scope, dataset = dataset, counts, total, scopeNote },
        };
    }

    //账号名册（管理员）
    private async Task<HarnessResult> ListUsersAsync(AgentTask task, Dictionary<string, string> args)
    {
        var keyword = (args.GetValueOrDefault("keyword") ?? "").Trim();
        var roleFilter = (args.GetValueOrDefault("role") ?? "").Trim().ToLowerInvariant();
        var limit = int.TryParse(args.GetValueOrDefault("limit"), out var parsedLimit) ? Math.Clamp(parsedLimit, 1, 100) : 50;

        var all = await _users.AllAsync().ConfigureAwait(false);

        var filtered = all.Where(u =>
            (roleFilter.Length == 0 || u.Role == roleFilter) &&
            (keyword.Length == 0
             || u.UserName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
             || u.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(u => u.Role).ThenBy(u => u.UserName)
            .ToList();

        var users = filtered.Take(limit).Select(u => new
        {
            userName = u.UserName,
            displayName = u.DisplayName,
            role = u.Role,
            roleLabel = Roles.Label(u.Role),
            disabled = u.Disabled,
            locked = u.IsLocked,
            emailMasked = u.EmailMasked,
            createdAt = u.CreatedAt,
            lastLoginAt = u.LastLoginAt,
            aiCallsToday = u.AiCallsToday,
        }).ToList();

        var byRole = all.GroupBy(u => u.Role)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var distribution = string.Join("，", byRole.Select(kv => $"{Roles.Label(kv.Key)} {kv.Value} 个"));

        return new HarnessResult
        {
            Ok = true,
            Summary = $"账号总数 {all.Count} 个（{distribution}）"
                      + (keyword.Length > 0 || roleFilter.Length > 0 ? $"；符合条件 {filtered.Count} 个" : ""),
            Data = new
            {
                total = all.Count,
                matched = filtered.Count,
                returned = users.Count,
                byRole = byRole.Select(kv => new { role = kv.Key, roleLabel = Roles.Label(kv.Key), count = kv.Value }).ToList(),
                users,
                note = "账号名册为敏感信息：本次读取已记入审计；不包含口令、盐值等任何凭据字段。",
            },
        };
    }


    //文件管理

    //同目录改名
    private async Task<HarnessResult> RenameFileAsync(AgentTask task, Dictionary<string, string> args)
    {
        var scope = args.GetValueOrDefault("scope") ?? "private";
        var resolved = _paths.Resolve(task.UserId, scope, args.GetValueOrDefault("path"));
        if (!resolved.Ok) return Fail(resolved.Error);
        if (!File.Exists(resolved.FullPath)) return Fail($"文件不存在：{resolved.Relative}");

        var newName = (args.GetValueOrDefault("new_name") ?? "").Trim();
        var parent = Path.GetDirectoryName(resolved.Relative)?.Replace('\\', '/') ?? "";
        var newRelative = parent.Length > 0 ? parent + "/" + newName : newName;

        //新路径也要过守卫
        var target = _paths.Resolve(task.UserId, scope, newRelative);
        if (!target.Ok) return Fail(target.Error);
        if (target.Relative == resolved.Relative) return Fail("新旧文件名相同");
        if (File.Exists(target.FullPath) || Directory.Exists(target.FullPath)) return Fail($"目标已存在：{target.Relative}");

        var previous = await File.ReadAllBytesAsync(resolved.FullPath).ConfigureAwait(false);
        var directory = Path.GetDirectoryName(target.FullPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.Move(resolved.FullPath, target.FullPath);

        var version = await _versions.RecordAsync(task.Id, task.UserId, task.UserName, "file", "rename",
            $"{scope}:{resolved.Relative}", previous, null,
            AppJson.Serialize(new UndoLog
            {
                Kind = "file",
                Scope = scope,
                Relative = resolved.Relative,
                Existed = true,
                RenamedTo = target.Relative,
            })).ConfigureAwait(false);

        return new HarnessResult
        {
            Ok = true,
            Summary = $"已重命名：{(scope == "share" ? "共享区" : "个人区")}/{resolved.Relative} → {target.Relative}"
                      + "（回退会删除新文件名并恢复旧文件）",
            Data = new { scope, from = resolved.Relative, to = target.Relative, size = previous.Length },
            VersionIds = { version.Id },
        };
    }

    //新建目录
    private Task<HarnessResult> CreateFolderAsync(AgentTask task, Dictionary<string, string> args)
    {
        var scope = args.GetValueOrDefault("scope") ?? "private";
        var resolved = _paths.Resolve(task.UserId, scope, args.GetValueOrDefault("path"));
        if (!resolved.Ok) return Task.FromResult(Fail(resolved.Error));
        if (string.IsNullOrEmpty(resolved.Relative)) return Task.FromResult(Fail("必须指定要新建的目录名"));
        if (Directory.Exists(resolved.FullPath)) return Task.FromResult(Fail($"目录已存在：{resolved.Relative}"));

        Directory.CreateDirectory(resolved.FullPath);
        return Task.FromResult(new HarnessResult
        {
            Ok = true,
            Summary = $"已新建目录：{(scope == "share" ? "共享区" : "个人区")}/{resolved.Relative}",
            Data = new { scope, path = resolved.Relative },
        });
    }

    //取文件信息
    private HarnessResult FileInfoOf(AgentTask task, Dictionary<string, string> args)
    {
        var scope = args.GetValueOrDefault("scope") ?? "private";
        var resolved = _paths.Resolve(task.UserId, scope, args.GetValueOrDefault("path"));
        if (!resolved.Ok) return Fail(resolved.Error);
        if (string.IsNullOrEmpty(resolved.Relative)) return Fail("必须指定文件名或目录名");

        var file = new FileInfo(resolved.FullPath);
        var dir = new DirectoryInfo(resolved.FullPath);

        if (file.Exists)
        {
            var bytes = file.Length <= 4 * 1024 * 1024
                ? System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(resolved.FullPath))
                : Array.Empty<byte>();

            return new HarnessResult
            {
                Ok = true,
                Summary = $"文件 {(scope == "share" ? "共享区" : "个人区")}/{resolved.Relative}：存在，{file.Length} 字节，修改于 {file.LastWriteTimeUtc:yyyy-MM-dd HH:mm}",
                Data = new
                {
                    exists = true,
                    type = "file",
                    path = resolved.Relative,
                    scope,
                    size = file.Length,
                    modifiedUtc = file.LastWriteTimeUtc,
                    sha256 = bytes.Length > 0 ? Convert.ToHexString(bytes).ToLowerInvariant() : "（文件较大，未计算）",
                },
            };
        }

        if (dir.Exists)
        {
            var files = dir.GetFiles().Length;
            var subDirs = dir.GetDirectories().Length;
            return new HarnessResult
            {
                Ok = true,
                Summary = $"目录 {(scope == "share" ? "共享区" : "个人区")}/{resolved.Relative}：存在，含 {files} 个文件、{subDirs} 个子目录",
                Data = new { exists = true, type = "dir", path = resolved.Relative, scope, files, subDirs, modifiedUtc = dir.LastWriteTimeUtc },
            };
        }

        return new HarnessResult
        {
            Ok = true,
            Summary = $"不存在：{(scope == "share" ? "共享区" : "个人区")}/{resolved.Relative}",
            Data = new { exists = false, path = resolved.Relative, scope },
        };
    }

    //我的可用区域
    private async Task<HarnessResult> MyWorkspaceAsync(AgentTask task)
    {
        var role = task.UserRole;
        var privateRoot = _paths.UserRoot(task.UserId);
        var privateFiles = Directory.Exists(privateRoot) ? Directory.GetFiles(privateRoot, "*", SearchOption.AllDirectories).Length : 0;

        var areas = new List<object>
        {
            new { key = "private", name = "个人工作区", allowed = true, files = privateFiles, note = "你可以读写自己的文件；新建/追加/改名（不改后缀）属低风险，可免审批；覆盖与删除必须人工审批" },
        };

        if (ToolCatalog.HasPermission(role, Permissions.ShareRead))
        {
            var shareRoot = _paths.ShareRoot();
            var shareFiles = Directory.Exists(shareRoot) ? Directory.GetFiles(shareRoot, "*", SearchOption.AllDirectories).Length : 0;
            areas.Add(new
            {
                key = "share",
                name = "共享文件区",
                allowed = true,
                files = shareFiles,
                note = "员工可读；写入共享区会影响其他成员，因此一律要人工审批",
            });
        }
        else
        {
            areas.Add(new
            {
                key = "share",
                name = "共享文件区",
                allowed = false,
                files = 0,
                note = $"当前角色（{Roles.Label(role)}）无权访问共享区；如需使用请联系管理员调整角色",
            });
        }

        var quota = await _users.TryConsumeAiQuotaAsync(task.UserId).ConfigureAwait(false);

        return new HarnessResult
        {
            Ok = true,
            Summary = $"当前角色 {Roles.Label(role)}：可用区域 {areas.Count} 个（个人工作区 {privateFiles} 个文件"
                      + (ToolCatalog.HasPermission(role, Permissions.ShareRead) ? "、共享文件区可读" : "、共享区无权")
                      + "）",
            Data = new
            {
                role,
                roleLabel = Roles.Label(role),
                areas,
                permissions = ToolCatalog.PermissionsOf(role).Select(p => new { key = p, label = Permissions.Label(p) }).ToList(),
                limits = new
                {
                    noBulkDelete = "系统不支持批量/整目录删除：请逐个文件删除，每次都会单独审批并留存快照",
                    scriptSuffixNeedsApproval = "写入或改名为 .bat/.ps1/.sh/.exe 等可执行后缀会被强制转人工审批",
                    overwriteNeedsApproval = "覆盖已有文件必须人工审批（原内容进快照，可一键回退）",
                },
            },
        };
    }

    //看我的通知
    private async Task<HarnessResult> MyNotificationsAsync(AgentTask task, Dictionary<string, string> args)
    {
        var limit = int.TryParse(args.GetValueOrDefault("limit"), out var parsed) ? Math.Clamp(parsed, 1, 50) : 10;
        var messages = await _outbox.ListAsync(task.UserId, limit).ConfigureAwait(false);

        return new HarnessResult
        {
            Ok = true,
            Summary = messages.Count == 0 ? "目前没有发给你的通知" : $"你有 {messages.Count} 条通知（最近一条：{messages[0].Subject}）",
            Data = new
            {
                count = messages.Count,
                items = messages.Select(m => new
                {
                    createdAt = m.CreatedAt,
                    subject = m.Subject,
                    body = m.Body,
                    status = m.Status,
                    taskId = m.TaskId,
                }).ToList(),
            },
        };
    }

    //撤回自己的申请
    private async Task<HarnessResult> CancelMyTaskAsync(AgentTask task, Dictionary<string, string> args)
    {
        var requestedId = (args.GetValueOrDefault("task_id") ?? "").Trim();
        var reason = (args.GetValueOrDefault("reason") ?? "").Trim();

        var cancelled = await _store.MutateAsync<AgentTask, (bool Ok, string Message, string Id)>(StoreNames.Tasks, list =>
        {
            var candidates = list.Where(t => t.UserId == task.UserId && t.Status == TaskState.PendingApproval)
                .OrderByDescending(t => t.CreatedAt)
                .ToList();

            var target = requestedId.Length > 0
                ? candidates.FirstOrDefault(t => t.Id.Equals(requestedId, StringComparison.OrdinalIgnoreCase))
                : candidates.FirstOrDefault();

            if (target is null)
            {
                return requestedId.Length > 0
                    ? (false, $"没有找到你的待审批申请：{requestedId}（可能已被审批或已过期）", "")
                    : (false, "你目前没有待审批的申请可以撤回", "");
            }

            target.Status = TaskState.Cancelled;
            target.StatusReason = $"申请人撤回{(reason.Length > 0 ? "：" + reason : "")}";
            target.DecidedAt = DateTime.UtcNow;
            target.DecidedBy = task.UserName;
            target.DecisionReason = reason;
            return (true, $"已撤回申请 {target.Id}（{target.Intent}）：不会执行任何动作", target.Id);
        }).ConfigureAwait(false);

        if (cancelled.Ok)
        {
            await _audit.WriteAsync(task.UserId, task.UserName, task.UserRole, "", "task.cancel",
                cancelled.Id, "CANCELLED", "申请人主动撤回" + (reason.Length > 0 ? "：" + reason : "")).ConfigureAwait(false);
            await _outbox.EnqueueAsync("", $"[已撤回] 你的操作申请 {cancelled.Id}", cancelled.Message, cancelled.Id,
                task.UserId, task.UserName, "").ConfigureAwait(false);
            return new HarnessResult { Ok = true, Summary = cancelled.Message, Data = new { taskId = cancelled.Id, status = TaskState.Cancelled } };
        }

        return Fail(cancelled.Message);
    }

    //管理端只读分析

    //账号使用情况汇总
    private async Task<HarnessResult> AdminActivitySummaryAsync(AgentTask task, Dictionary<string, string> args)
    {
        var userFilter = (args.GetValueOrDefault("user") ?? "").Trim();
        var days = int.TryParse(args.GetValueOrDefault("days"), out var parsedDays) ? Math.Clamp(parsedDays, 1, 90) : 7;
        var limit = int.TryParse(args.GetValueOrDefault("limit"), out var parsedLimit) ? Math.Clamp(parsedLimit, 1, 50) : 20;

        var since = DateTime.UtcNow.AddDays(-days);
        var tasks = await _store.ReadListAsync<AgentTask>(StoreNames.Tasks).ConfigureAwait(false);
        var recent = tasks.Where(t => t.CreatedAt >= since
                                      && (userFilter.Length == 0 || t.UserName.Contains(userFilter, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var auditLines = await _store.ReadLinesAsync(StoreNames.Audit).ConfigureAwait(false);
        var aiCalls = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in auditLines)
        {
            var entry = AppJson.Deserialize<AuditEntry>(line);
            if (entry is null || entry.Action != "ai.plan" || entry.Time < since) continue;
            var key = entry.ActorName.Length > 0 ? entry.ActorName : "system";
            aiCalls[key] = aiCalls.GetValueOrDefault(key) + 1;
        }

        var rows = recent.GroupBy(t => t.UserName)
            .Select(g => new
            {
                userName = g.Key,
                role = g.First().UserRole,
                roleLabel = Roles.Label(g.First().UserRole),
                taskCount = g.Count(),
                executed = g.Count(t => t.Status == TaskState.Executed),
                pending = g.Count(t => t.Status == TaskState.PendingApproval),
                rejectedByRule = g.Count(t => t.Status == TaskState.RejectedRule),
                rejectedByAi = g.Count(t => t.Status == TaskState.RejectedAi),
                rejectedByAdmin = g.Count(t => t.Status == TaskState.RejectedAdmin),
                failed = g.Count(t => t.Status == TaskState.Failed),
                cancelled = g.Count(t => t.Status == TaskState.Cancelled),
                injectedInputs = g.Count(t => (t.InjectionFlags?.Count ?? 0) > 0),
                aiCalls = aiCalls.GetValueOrDefault(g.Key),
                lastActionAt = g.Max(t => t.CreatedAt),
                topTools = g.GroupBy(t => t.ToolName).OrderByDescending(x => x.Count())
                    .Take(3).Select(x => $"{x.Key}×{x.Count()}").ToList(),
            })
            .OrderByDescending(r => r.taskCount)
            .Take(limit)
            .ToList();

        await _audit.WriteAsync(task.UserId, task.UserName, task.UserRole, "", "admin.activity-summary", $"last{days}d", "SUCCESS",
            $"使用情况汇总：涉及 {rows.Count} 个账号、{recent.Count} 条任务").ConfigureAwait(false);

        var totals = new
        {
            users = rows.Count,
            tasks = recent.Count,
            executed = recent.Count(t => t.Status == TaskState.Executed),
            pending = recent.Count(t => t.Status == TaskState.PendingApproval),
            rejected = recent.Count(t => TaskState.IsRejected(t.Status)),
            injectionSuspects = recent.Count(t => (t.InjectionFlags?.Count ?? 0) > 0),
        };

        return new HarnessResult
        {
            Ok = true,
            Summary = $"最近 {days} 天：{totals.users} 个账号提交了 {totals.tasks} 条任务"
                      + $"（执行 {totals.executed}、待审批 {totals.pending}、被拦/打回 {totals.rejected}、疑似注入输入 {totals.injectionSuspects}）",
            Data = new
            {
                window = new { days, since },
                totals,
                byUser = rows,
                note = "本读取已记审计（admin.activity-summary）；数据来自 tasks.json 与 audit.jsonl。",
            },
        };
    }

    //任务清单
    private async Task<HarnessResult> AdminTaskListAsync(AgentTask task, Dictionary<string, string> args)
    {
        var status = (args.GetValueOrDefault("status") ?? "").Trim();
        var userFilter = (args.GetValueOrDefault("user") ?? "").Trim();
        var toolFilter = (args.GetValueOrDefault("tool") ?? "").Trim();
        var limit = int.TryParse(args.GetValueOrDefault("limit"), out var parsedLimit) ? Math.Clamp(parsedLimit, 1, 100) : 20;

        var tasks = await _store.ReadListAsync<AgentTask>(StoreNames.Tasks).ConfigureAwait(false);
        var query = tasks.AsEnumerable();
        if (status.Length > 0) query = query.Where(t => t.Status.Equals(status, StringComparison.OrdinalIgnoreCase));
        if (userFilter.Length > 0) query = query.Where(t => t.UserName.Contains(userFilter, StringComparison.OrdinalIgnoreCase));
        if (toolFilter.Length > 0) query = query.Where(t => t.ToolName.Contains(toolFilter, StringComparison.OrdinalIgnoreCase));

        var filtered = query.OrderByDescending(t => t.CreatedAt).ToList();
        var items = filtered.Take(limit).Select(t => new
        {
            id = t.Id,
            createdAt = t.CreatedAt,
            userName = t.UserName,
            roleLabel = Roles.Label(t.UserRole),
            tool = t.ToolName,
            intent = t.Intent,
            status = t.Status,
            statusLabel = TaskState.Label(t.Status),
            risk = t.RiskLevel,
            decidedBy = t.DecidedBy,
            decisionReason = t.DecisionReason,
        }).ToList();

        await _audit.WriteAsync(task.UserId, task.UserName, task.UserRole, "", "admin.task-list",
            $"{status}/{userFilter}/{toolFilter}", "SUCCESS", $"任务清单查询命中 {filtered.Count} 条").ConfigureAwait(false);

        return new HarnessResult
        {
            Ok = true,
            Summary = $"任务清单命中 {filtered.Count} 条" + (items.Count < filtered.Count ? $"（返回前 {items.Count} 条）" : ""),
            Data = new
            {
                total = filtered.Count,
                returned = items.Count,
                byStatus = filtered.GroupBy(t => t.Status).ToDictionary(g => g.Key, g => g.Count()),
                items,
            },
        };
    }

    //一键回退

    //执行回退
    public async Task<(bool Ok, string Message, string NewVersionId, object? Data)> RollbackAsync(
        VersionRecord version, string actorId, string actorName, string actorRole, string revertTaskId)
    {
        var undo = AppJson.Deserialize<UndoLog>(version.UndoLogJson) ?? new UndoLog { Kind = version.Kind };

        switch (version.Kind)
        {
            case "file":
            {
                var resolved = _paths.Resolve(version.UserId, undo.Scope, undo.Relative);
                if (!resolved.Ok) return (false, resolved.Error, "", null);

                var current = File.Exists(resolved.FullPath)
                    ? await File.ReadAllBytesAsync(resolved.FullPath).ConfigureAwait(false)
                    : null;

                if (version.PreviousExisted)
                {
                    var restore = await _versions.ReadBlobAsync(version.PreviousBlob).ConfigureAwait(false);
                    if (restore is null) return (false, "快照内容缺失，无法回退", "", null);

                    var dir = Path.GetDirectoryName(resolved.FullPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    await File.WriteAllBytesAsync(resolved.FullPath, restore).ConfigureAwait(false);

                    //回退改名要删新名
                    if (!string.IsNullOrEmpty(undo.RenamedTo))
                    {
                        var renamed = _paths.Resolve(version.UserId, undo.Scope, undo.RenamedTo);
                        if (renamed.Ok && File.Exists(renamed.FullPath)) File.Delete(renamed.FullPath);
                    }
                }
                else if (File.Exists(resolved.FullPath))
                {
                    File.Delete(resolved.FullPath);
                }

                var newVersion = await _versions.RecordAsync(revertTaskId, version.UserId, actorName, "file", "rollback",
                    version.Target, current,
                    version.PreviousExisted ? await _versions.ReadBlobAsync(version.PreviousBlob).ConfigureAwait(false) : null,
                    AppJson.Serialize(new UndoLog
                    {
                        Kind = "file",
                        Scope = undo.Scope,
                        Relative = undo.Relative,
                        Existed = current is not null,
                        RollbackOf = version.Id,
                    })).ConfigureAwait(false);

                await MarkRevertedAsync(version.Id, actorName, revertTaskId).ConfigureAwait(false);
                return (true, $"已回退文件 {version.Target}", newVersion.Id,
                    new { kind = "file", target = version.Target, restored = version.PreviousExisted });
            }

            case "dataset":
            {
                if (!string.IsNullOrEmpty(undo.RowJson) && string.IsNullOrEmpty(undo.Field))
                {
                    //回退删除：整行插回
                    var (ok, error) = await _datasets.InsertRowAsync(undo.Dataset, undo.RowJson).ConfigureAwait(false);
                    if (!ok) return (false, error, "", null);

                    var nv = await RecordDatasetUndoAsync(revertTaskId, version, actorName, "rollback-insert", undo).ConfigureAwait(false);
                    await MarkRevertedAsync(version.Id, actorName, revertTaskId).ConfigureAwait(false);
                    return (true, $"已还原 {undo.Dataset}/{undo.Id}", nv, new { kind = "dataset", action = "insert-restore" });
                }

                if (!string.IsNullOrEmpty(undo.Field))
                {
                    //回退更新：恢复旧值
                    var (ok, error) = await _datasets.RestoreFieldsAsync(undo.Dataset, undo.Id,
                        new Dictionary<string, string> { [undo.Field] = undo.OldValue }).ConfigureAwait(false);
                    if (!ok) return (false, error, "", null);

                    var nv = await RecordDatasetUndoAsync(revertTaskId, version, actorName, "rollback-update", undo).ConfigureAwait(false);
                    await MarkRevertedAsync(version.Id, actorName, revertTaskId).ConfigureAwait(false);
                    return (true, $"已把 {undo.Dataset}/{undo.Id} 的 {undo.Field} 恢复为 {undo.OldValue}", nv,
                        new { kind = "dataset", action = "restore-field" });
                }

                if (undo.Inserted)
                {
                    var (ok, error, _) = await _datasets.DeleteRowAsync(undo.Dataset, undo.Id).ConfigureAwait(false);
                    if (!ok) return (false, error, "", null);
                    var nv = await RecordDatasetUndoAsync(revertTaskId, version, actorName, "rollback-delete", undo).ConfigureAwait(false);
                    await MarkRevertedAsync(version.Id, actorName, revertTaskId).ConfigureAwait(false);
                    return (true, $"已撤销插入 {undo.Dataset}/{undo.Id}", nv, new { kind = "dataset", action = "delete-inserted" });
                }

                return (false, "该版本没有可用的反向操作数据", "", null);
            }

            case "notice":
            {
                var ok = await _outbox.RecallAsync(undo.NoticeId, actorName).ConfigureAwait(false);
                if (!ok) return (false, "通知不存在或已撤回", "", null);

                await MarkRevertedAsync(version.Id, actorName, revertTaskId).ConfigureAwait(false);
                return (true, $"已执行补偿事务：撤回通知 {undo.NoticeId}", "", new { kind = "notice", action = "recall" });
            }

            default:
                return (false, "不支持回退的版本类型", "", null);
        }
    }

    //记数据回退
    private async Task<string> RecordDatasetUndoAsync(string revertTaskId, VersionRecord version, string actorName,
        string operation, UndoLog undo)
    {
        var row = await _datasets.FindRowAsync(undo.Dataset, undo.Id).ConfigureAwait(false);
        var bytes = row is null ? null : Encoding.UTF8.GetBytes(AppJson.Serialize(row, disk: true));

        var nv = await _versions.RecordAsync(revertTaskId, version.UserId, actorName, "dataset", operation,
            $"{undo.Dataset}/{undo.Id}", bytes, null,
            AppJson.Serialize(new UndoLog
            {
                Kind = "dataset",
                Dataset = undo.Dataset,
                Id = undo.Id,
                Field = undo.Field,
                OldValue = undo.OldValue,
                RowJson = row is null ? "" : AppJson.Serialize(row, disk: true),
                Inserted = operation == "rollback-insert",
                RollbackOf = version.Id,
            })).ConfigureAwait(false);

        return nv.Id;
    }

    //标记已回退
    private async Task MarkRevertedAsync(string versionId, string actorName, string revertTaskId)
        => await _versions.UpdateAsync(versionId, v =>
        {
            v.Reverted = true;
            v.RevertedAt = DateTime.UtcNow;
            v.RevertedBy = actorName;
            v.RevertTaskId = revertTaskId;
        }).ConfigureAwait(false);

    //拼接字节
    private static byte[] Concat(byte[]? first, byte[] second)
    {
        if (first is null) return second;
        var result = new byte[first.Length + second.Length];
        Buffer.BlockCopy(first, 0, result, 0, first.Length);
        Buffer.BlockCopy(second, 0, result, first.Length, second.Length);
        return result;
    }

    //返回失败
    private static HarnessResult Fail(string message) => new() { Ok = false, Summary = message };
}
