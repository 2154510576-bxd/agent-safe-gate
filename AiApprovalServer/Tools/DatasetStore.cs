using System.Text;
using System.Text.RegularExpressions;
using AiApproval.Core;

namespace AiApproval.Tools;

//数据集字段
public sealed class DatasetField
{
    public string Name { get; init; } = "";
    public string Label { get; init; } = "";
    //字段类型
    public string Type { get; init; } = "text";
    public bool Writable { get; init; } = true;
    public int MaxLength { get; init; } = 100;
    public string[]? EnumValues { get; init; }
}

//数据集结构
public sealed class DatasetSchema
{
    public string Name { get; init; } = "";
    public string Label { get; init; } = "";
    public string KeyField { get; init; } = "id";
    //主键前缀
    public string KeyPrefix { get; init; } = "X";
    public List<DatasetField> Fields { get; init; } = new();

    //按名字取字段
    public DatasetField? Field(string name)
        => Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
}

//业务数据集定义（白名单）
public static class DatasetCatalog
{
    public static readonly Dictionary<string, DatasetSchema> All = new(StringComparer.OrdinalIgnoreCase)
    {
        ["customers"] = new DatasetSchema
        {
            Name = "customers",
            Label = "甲方客户档案",
            KeyPrefix = "C",
            Fields =
            {
                new DatasetField { Name = "id", Label = "客户编号", Type = "text", Writable = false, MaxLength = 24 },
                new DatasetField { Name = "name", Label = "公司名称", Type = "text", MaxLength = 40 },
                new DatasetField { Name = "contact", Label = "联系人", Type = "text", MaxLength = 20 },
                new DatasetField { Name = "phone", Label = "联系电话", Type = "phone", MaxLength = 11 },
                new DatasetField { Name = "industry", Label = "所属行业", Type = "enum", EnumValues = new[] { "制造", "科技", "物流", "教育", "传媒", "金融", "其它" } },
                new DatasetField { Name = "level", Label = "客户等级", Type = "enum", EnumValues = new[] { "普通", "白银", "黄金", "铂金" } },
                new DatasetField { Name = "status", Label = "合作状态", Type = "enum", EnumValues = new[] { "潜在", "洽谈中", "合作中", "已暂停", "已终止" } },
                new DatasetField { Name = "owner", Label = "负责员工", Type = "text", MaxLength = 32 },
                new DatasetField { Name = "requirement", Label = "需求摘要", Type = "text", MaxLength = 200 },
                new DatasetField { Name = "remark", Label = "备注", Type = "text", MaxLength = 200 },
                new DatasetField { Name = "account_user_id", Label = "关联甲方账号", Type = "text", Writable = false, MaxLength = 32 },
                new DatasetField { Name = "updated_at", Label = "更新时间", Type = "date", Writable = false, MaxLength = 10 },
            },
        },
        ["requirements"] = new DatasetSchema
        {
            Name = "requirements",
            Label = "甲方需求单",
            KeyPrefix = "R",
            Fields =
            {
                new DatasetField { Name = "id", Label = "需求编号", Type = "text", Writable = false, MaxLength = 24 },
                new DatasetField { Name = "customer_id", Label = "甲方客户编号", Type = "text", Writable = false, MaxLength = 24 },
                new DatasetField { Name = "title", Label = "需求标题", Type = "text", MaxLength = 60 },
                new DatasetField { Name = "detail", Label = "需求描述", Type = "text", MaxLength = 500 },
                new DatasetField { Name = "budget", Label = "预算(元)", Type = "decimal", MaxLength = 16 },
                new DatasetField { Name = "expect_date", Label = "期望交付日", Type = "date", MaxLength = 10 },
                new DatasetField { Name = "status", Label = "需求状态", Type = "enum", EnumValues = new[] { "待评估", "已报价", "进行中", "已交付", "已关闭" } },
                new DatasetField { Name = "owner", Label = "负责员工", Type = "text", MaxLength = 32 },
                new DatasetField { Name = "created_by", Label = "提交账号", Type = "text", Writable = false, MaxLength = 32 },
                new DatasetField { Name = "created_at", Label = "创建日期", Type = "date", Writable = false, MaxLength = 10 },
                new DatasetField { Name = "updated_at", Label = "更新时间", Type = "date", Writable = false, MaxLength = 10 },
            },
        },
        ["orders"] = new DatasetSchema
        {
            Name = "orders",
            Label = "订单记录",
            KeyPrefix = "O",
            Fields =
            {
                new DatasetField { Name = "id", Label = "订单号", Type = "text", Writable = false, MaxLength = 24 },
                new DatasetField { Name = "customer_id", Label = "客户编号", Type = "text", MaxLength = 24 },
                new DatasetField { Name = "amount", Label = "金额(元)", Type = "decimal", MaxLength = 16 },
                new DatasetField { Name = "status", Label = "订单状态", Type = "enum", EnumValues = new[] { "待付款", "已付款", "已发货", "已完成", "已取消" } },
                new DatasetField { Name = "created_at", Label = "创建日期", Type = "date", MaxLength = 10 },
            },
        },
        ["contracts"] = new DatasetSchema
        {
            Name = "contracts",
            Label = "合同台账",
            KeyPrefix = "K",
            Fields =
            {
                new DatasetField { Name = "id", Label = "合同编号", Type = "text", Writable = false, MaxLength = 24 },
                new DatasetField { Name = "customer_id", Label = "客户编号", Type = "text", MaxLength = 24 },
                new DatasetField { Name = "amount", Label = "合同金额(元)", Type = "decimal", MaxLength = 16 },
                new DatasetField { Name = "sign_date", Label = "签署日期", Type = "date", MaxLength = 10 },
                new DatasetField { Name = "status", Label = "状态", Type = "enum", EnumValues = new[] { "草拟", "待签", "生效中", "已归档" } },
            },
        },
    };

    //数据集是否存在
    public static bool IsKnown(string? name) => !string.IsNullOrWhiteSpace(name) && All.ContainsKey(name.Trim());

    //生成给 AI 的字段清单
    public static string DescribeForPrompt()
    {
        var sb = new StringBuilder();
        foreach (var schema in All.Values)
        {
            sb.Append("- ").Append(schema.Name).Append("（").Append(schema.Label).Append("，主键 ")
              .Append(schema.KeyField).Append("）：");

            sb.Append(string.Join("；", schema.Fields.Select(f =>
            {
                var type = f.Type == "enum" && f.EnumValues is not null
                    ? "枚举[" + string.Join("/", f.EnumValues) + "]"
                    : f.Type switch
                    {
                        "int" => "整数",
                        "decimal" => "金额",
                        "phone" => "手机号",
                        "date" => "日期yyyy-MM-dd",
                        _ => "文本",
                    };
                return $"{f.Name}({f.Label},{type}{(f.Writable ? "" : ",只读")})";
            })));

            sb.Append('\n');
        }
        return sb.ToString();
    }

    //数据集种子行（空）
    public static List<Dictionary<string, string>> CreateSeedRows(string dataset)
        => new();

    //拼客户行
    private static Dictionary<string, string> ClientRow(string id, string name, string contact, string phone, string industry,
        string level, string status, string owner, string requirement, string remark, string updatedAt)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = id, ["name"] = name, ["contact"] = contact, ["phone"] = phone,
            ["industry"] = industry, ["level"] = level, ["status"] = status, ["owner"] = owner,
            ["requirement"] = requirement, ["remark"] = remark, ["account_user_id"] = "", ["updated_at"] = updatedAt,
        };

    //拼需求行
    private static Dictionary<string, string> RequirementRow(string id, string customerId, string title, string detail,
        string budget, string expectDate, string status, string owner, string createdBy, string createdAt, string updatedAt)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = id, ["customer_id"] = customerId, ["title"] = title, ["detail"] = detail,
            ["budget"] = budget, ["expect_date"] = expectDate, ["status"] = status, ["owner"] = owner,
            ["created_by"] = createdBy, ["created_at"] = createdAt, ["updated_at"] = updatedAt,
        };

    //拼订单行
    private static Dictionary<string, string> OrderRow(string id, string customerId, string amount, string status, string createdAt)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = id, ["customer_id"] = customerId, ["amount"] = amount,
            ["status"] = status, ["created_at"] = createdAt,
        };

    //拼合同行
    private static Dictionary<string, string> ContractRow(string id, string customerId, string amount, string signDate, string status)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = id, ["customer_id"] = customerId, ["amount"] = amount,
            ["sign_date"] = signDate, ["status"] = status,
        };
}

//业务数据读写
public sealed class DatasetStore
{
    private static readonly Regex PhoneRx = new("^1[3-9]\\d{9}$", RegexOptions.Compiled);
    private static readonly Regex DateRx = new("^\\d{4}-\\d{2}-\\d{2}$", RegexOptions.Compiled);

    //枚举口语别名表
    private static readonly Dictionary<string, (string[] Aliases, string Value)[]> EnumAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["客户等级"] = new (string[], string)[]
        {
            (new[] { "重要", "很重要", "高价值", "大客户", "重点", "vip", "顶级", "最高" }, "铂金"),
            (new[] { "较高", "中高", "优质", "核心" }, "黄金"),
            (new[] { "中等", "一般", "常规" }, "白银"),
            (new[] { "低", "次要", "小客户", "低价值" }, "普通"),
        },
        ["合作状态"] = new (string[], string)[]
        {
            (new[] { "意向", "线索", "待开发", "陌生" }, "潜在"),
            (new[] { "洽谈", "在谈", "跟进", "沟通中", "谈判" }, "洽谈中"),
            (new[] { "合作", "成交", "签约", "已签约", "在合作", "活跃" }, "合作中"),
            (new[] { "暂停", "搁置", "暂缓", "待定" }, "已暂停"),
            (new[] { "终止", "流失", "结束", "不再合作" }, "已终止"),
        },
        ["需求状态"] = new (string[], string)[]
        {
            (new[] { "评估", "评估中", "评审中", "新提交" }, "待评估"),
            (new[] { "报价", "出价", "已给报价" }, "已报价"),
            (new[] { "进行", "在做", "开发中", "执行中", "已开工" }, "进行中"),
            (new[] { "交付", "完成", "已完成", "上线", "搞定了" }, "已交付"),
            (new[] { "关闭", "取消", "作废", "不做了" }, "已关闭"),
        },
        ["订单状态"] = new (string[], string)[]
        {
            (new[] { "未付款", "待付", "待支付" }, "待付款"),
            (new[] { "已付", "付款完成", "已支付" }, "已付款"),
            (new[] { "发货", "已发出", "在途" }, "已发货"),
            (new[] { "完成", "已收货", "结单" }, "已完成"),
            (new[] { "取消", "作废" }, "已取消"),
        },
        ["合同状态"] = new (string[], string)[]
        {
            (new[] { "起草", "草稿", "拟定" }, "草拟"),
            (new[] { "等签", "审批中" }, "待签"),
            (new[] { "生效", "执行中", "有效" }, "生效中"),
            (new[] { "归档", "完成" }, "已归档"),
        },
        ["所属行业"] = new (string[], string)[]
        {
            (new[] { "制造业", "工厂", "工业", "车间" }, "制造"),
            (new[] { "it", "互联网", "软件", "科技公司", "技术" }, "科技"),
            (new[] { "运输", "快递", "仓储" }, "物流"),
            (new[] { "学校", "培训", "教育机构" }, "教育"),
            (new[] { "广告", "媒体", "文化" }, "传媒"),
            (new[] { "银行", "保险", "证券" }, "金融"),
            (new[] { "其他", "其它行业" }, "其它"),
        },
    };

    private readonly JsonStore _store;

    public DatasetStore(JsonStore store, AppConfig cfg)
    {
        _store = store;
        _ = cfg;
    }

    //取数据集文件
    private static string FileOf(string dataset) => $"{StoreNames.Datasets}/{dataset.ToLowerInvariant()}.json";

    //读所有行
    public async Task<List<Dictionary<string, string>>> RowsAsync(string dataset)
        => await _store.ReadListAsync<Dictionary<string, string>>(FileOf(dataset)).ConfigureAwait(false);

    //播种种子行
    public async Task EnsureSeedAsync()
    {
        foreach (var name in DatasetCatalog.All.Keys)
        {
            var rows = await RowsAsync(name).ConfigureAwait(false);
            if (rows.Count == 0)
            {
                var seed = DatasetCatalog.CreateSeedRows(name);
                if (seed.Count > 0)
                    await _store.WriteAtomicAsync(FileOf(name), seed).ConfigureAwait(false);
            }
        }
    }

    //按字段精确查找
    public async Task<Dictionary<string, string>?> FindByFieldAsync(string dataset, string field, string value)
        => (await ListByFieldAsync(dataset, field, value, 1).ConfigureAwait(false)).FirstOrDefault();

    //按字段列出
    public async Task<List<Dictionary<string, string>>> ListByFieldAsync(string dataset, string field, string value, int limit = 50)
    {
        var rows = await RowsAsync(dataset).ConfigureAwait(false);
        return rows.Where(r => Get(r, field).Equals(value, StringComparison.OrdinalIgnoreCase))
            .Take(Math.Clamp(limit, 1, 200))
            .ToList();
    }

    //生成下一个主键
    public async Task<string> NextKeyAsync(string dataset)
    {
        var schema = DatasetCatalog.All[dataset];
        var rows = await RowsAsync(dataset).ConfigureAwait(false);

        var max = 1000;
        foreach (var row in rows)
        {
            var id = Get(row, schema.KeyField);
            if (id.Length < 2 || !char.IsLetter(id[0])) continue;
            if (!string.Equals(id[0].ToString(), schema.KeyPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (int.TryParse(id[1..], out var number) && number > max) max = number;
        }

        return schema.KeyPrefix + (max + 1).ToString();
    }

    //新建一行
    public async Task<(bool Ok, string Error, string Id)> CreateRowAsync(string dataset, Dictionary<string, string> values)
    {
        if (!DatasetCatalog.IsKnown(dataset)) return (false, "未知数据集", "");

        var schema = DatasetCatalog.All[dataset];
        var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var readOnlyValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kv in values)
            readOnlyValues[kv.Key] = kv.Value ?? "";

        foreach (var field in schema.Fields)
        {
            if (!field.Writable)
            {
                row[field.Name] = field.Name switch
                {
                    "created_at" or "updated_at" => today,
                    _ => "",
                };
                continue;
            }

            var raw = readOnlyValues.TryGetValue(field.Name, out var v) ? v : "";

            if (string.IsNullOrWhiteSpace(raw))
            {
                if (field.Type == "enum" && field.EnumValues is not null && field.Name == "status")
                    row[field.Name] = field.EnumValues[0];
                else
                    row[field.Name] = "";
                continue;
            }

            var error = ValidateValue(schema, field.Name, raw.Trim(), out var normalized);
            if (error is not null) return (false, error, "");

            row[field.Name] = normalized;
        }

        var id = await NextKeyAsync(dataset).ConfigureAwait(false);

        await _store.MutateAsync<Dictionary<string, string>, bool>(FileOf(dataset), rows =>
        {
            row[schema.KeyField] = id;
            rows.Add(row);
            return true;
        }).ConfigureAwait(false);

        return (true, "", id);
    }

    //按条件查询
    public async Task<(bool Ok, string Error, List<Dictionary<string, string>> Rows, string Query)> QueryAsync(
        string dataset, string? field, string? op, string? value, int limit)
    {
        if (!DatasetCatalog.IsKnown(dataset))
            return (false, $"未知数据集：{dataset}", new(), "");

        var schema = DatasetCatalog.All[dataset];
        limit = Math.Clamp(limit <= 0 ? 20 : limit, 1, 50);

        var rows = await RowsAsync(dataset).ConfigureAwait(false);

        var where = "";
        if (!string.IsNullOrWhiteSpace(field))
        {
            var f = schema.Field(field);
            if (f is null) return (false, $"数据集 {schema.Label} 不存在字段：{field}", new(), "");

            var normalizedOp = (op ?? "eq").Trim().ToLowerInvariant() switch
            {
                "eq" or "=" => "eq",
                "contains" or "like" => "contains",
                "gte" or ">=" => "gte",
                "lte" or "<=" => "lte",
                _ => "",
            };
            if (normalizedOp.Length == 0) return (false, $"不支持的比较方式：{op}", new(), "");

            var rawValue = (value ?? "").Trim();
            if (rawValue.Length > 100) return (false, "查询值过长", new(), "");

            rows = normalizedOp switch
            {
                "eq" => rows.Where(r => Get(r, f.Name).Equals(rawValue, StringComparison.OrdinalIgnoreCase)).ToList(),
                "contains" => rows.Where(r => Get(r, f.Name).Contains(rawValue, StringComparison.OrdinalIgnoreCase)).ToList(),
                "gte" => rows.Where(r => CompareDecimal(Get(r, f.Name), rawValue) >= 0).ToList(),
                "lte" => rows.Where(r => CompareDecimal(Get(r, f.Name), rawValue) <= 0).ToList(),
                _ => rows,
            };

            where = normalizedOp switch
            {
                "eq" => $"{f.Name} = '{rawValue}'",
                "contains" => $"{f.Name} LIKE '%{rawValue}%'",
                "gte" => $"{f.Name} >= {rawValue}",
                _ => $"{f.Name} <= {rawValue}",
            };
        }

        var query = $"SELECT * FROM {dataset}" + (where.Length > 0 ? $" WHERE {where}" : "") + $" LIMIT {limit}";
        return (true, "", rows.Take(limit).ToList(), query);
    }

    //按主键找行
    public async Task<Dictionary<string, string>?> FindRowAsync(string dataset, string id)
    {
        var rows = await RowsAsync(dataset).ConfigureAwait(false);
        var key = DatasetCatalog.All[dataset].KeyField;
        return rows.FirstOrDefault(r => Get(r, key).Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    //服务端专用字段写入
    public async Task<bool> SetFieldServerSideAsync(string dataset, string rowId, string field, string value)
    {
        if (!DatasetCatalog.IsKnown(dataset)) return false;
        var schema = DatasetCatalog.All[dataset];

        return await _store.MutateAsync<Dictionary<string, string>, bool>(FileOf(dataset), rows =>
        {
            var row = rows.FirstOrDefault(r => Get(r, schema.KeyField).Equals(rowId, StringComparison.OrdinalIgnoreCase));
            if (row is null) return false;

            var key = row.Keys.FirstOrDefault(k => string.Equals(k, field, StringComparison.OrdinalIgnoreCase)) ?? field;
            row[key] = value;
            return true;
        }).ConfigureAwait(false);
    }

    //旧数据补齐字段
    public async Task<int> EnsureSchemaFieldsAsync(string dataset)
    {
        if (!DatasetCatalog.IsKnown(dataset)) return 0;
        var schema = DatasetCatalog.All[dataset];

        return await _store.MutateAsync<Dictionary<string, string>, int>(FileOf(dataset), rows =>
        {
            var patched = 0;
            foreach (var row in rows)
            {
                foreach (var field in schema.Fields)
                {
                    if (row.Keys.Any(k => string.Equals(k, field.Name, StringComparison.OrdinalIgnoreCase))) continue;

                    row[field.Name] = field.Name switch
                    {
                        "industry" => "其它",
                        "status" when field.EnumValues is { Length: > 0 } => field.EnumValues[0],
                        _ => "",
                    };
                    patched++;
                }
            }
            return patched;
        }).ConfigureAwait(false);
    }

    //校验字段值
    public string? ValidateValue(DatasetSchema schema, string field, string value, out string normalized)
    {
        normalized = "";
        var f = schema.Field(field);
        if (f is null) return $"数据集 {schema.Label} 不存在字段：{field}";
        if (!f.Writable) return $"字段 {f.Label} 只读，不允许修改";
        if (value is null) return "值不能为空";
        if (value.Length > f.MaxLength) return $"字段 {f.Label} 长度不得超过 {f.MaxLength}";
        if (value.Any(c => char.IsControl(c) && c != '\t')) return "值包含控制字符";

        switch (f.Type)
        {
            case "int":
                if (!int.TryParse(value, out var iv) || iv < 0 || iv > 1_000_000_000) return $"{f.Label} 必须是 0-1000000000 的整数";
                normalized = iv.ToString();
                break;
            case "decimal":
                if (!decimal.TryParse(value, out var dv) || dv < 0 || dv > 1_000_000_000m) return $"{f.Label} 必须是非负金额";
                normalized = dv.ToString("0.00");
                break;
            case "enum":
            {
                var options = f.EnumValues ?? Array.Empty<string>();
                var exact = options.FirstOrDefault(o => o.Equals(value, StringComparison.OrdinalIgnoreCase));
                if (exact is not null) { normalized = exact; break; }

                //口语说法收敛到枚举
                var mapped = MapEnumValue(f.Label, value, options);
                if (mapped is null)
                    return $"{f.Label}只能是：{string.Join(" / ", options)}（也接受常见说法，例如“重要”“跟进”“已完成”）";
                normalized = mapped;
                break;
            }
            case "phone":
                if (!PhoneRx.IsMatch(value)) return $"{f.Label} 必须是 11 位手机号";
                normalized = value;
                break;
            case "date":
                if (!DateRx.IsMatch(value) || !DateTime.TryParse(value, out _)) return $"{f.Label} 必须是 yyyy-MM-dd 格式的有效日期";
                normalized = value;
                break;
            default:
                if (value.Length == 0) return $"{f.Label} 不能为空";
                normalized = value;
                break;
        }

        return null;
    }

    //更新单字段
    public async Task<(bool Ok, string Error, string OldValue, Dictionary<string, string>? Row)> UpdateFieldAsync(
        string dataset, string id, string field, string value)
    {
        if (!DatasetCatalog.IsKnown(dataset)) return (false, "未知数据集", "", null);
        var schema = DatasetCatalog.All[dataset];

        var error = ValidateValue(schema, field, value, out var normalized);
        if (error is not null) return (false, error, "", null);

        var result = await _store.MutateAsync<Dictionary<string, string>, (bool, string, string, Dictionary<string, string>?)>(
            FileOf(dataset), rows =>
            {
                var key = schema.KeyField;
                var row = rows.FirstOrDefault(r => Get(r, key).Equals(id, StringComparison.OrdinalIgnoreCase));
                if (row is null) return (false, "记录不存在", "", null);

                var old = Get(row, field);
                var target = row.FirstOrDefault(kv => string.Equals(kv.Key, field, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrEmpty(target.Key)) row[field] = normalized;
                else row[target.Key] = normalized;

                if (schema.Field("updated_at") is not null)
                {
                    var stamp = row.FirstOrDefault(kv => string.Equals(kv.Key, "updated_at", StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrEmpty(stamp.Key)) row[stamp.Key] = DateTime.UtcNow.ToString("yyyy-MM-dd");
                }

                var snapshot = new Dictionary<string, string>(row, StringComparer.OrdinalIgnoreCase);
                return (true, "", old, snapshot);
            }).ConfigureAwait(false);

        return (result.Item1, result.Item2, result.Item3, result.Item4);
    }

    //删一行
    public async Task<(bool Ok, string Error, string RowJson)> DeleteRowAsync(string dataset, string id)
    {
        if (!DatasetCatalog.IsKnown(dataset)) return (false, "未知数据集", "");

        var schema = DatasetCatalog.All[dataset];
        return await _store.MutateAsync<Dictionary<string, string>, (bool, string, string)>(FileOf(dataset), rows =>
        {
            var key = schema.KeyField;
            var row = rows.FirstOrDefault(r => Get(r, key).Equals(id, StringComparison.OrdinalIgnoreCase));
            if (row is null) return (false, "记录不存在", "");

            var json = AppJson.Serialize(row, disk: true);
            rows.Remove(row);
            return (true, "", json);
        }).ConfigureAwait(false);
    }

    //插回一行
    public async Task<(bool Ok, string Error)> InsertRowAsync(string dataset, string rowJson)
    {
        if (!DatasetCatalog.IsKnown(dataset)) return (false, "未知数据集");

        var row = AppJson.Deserialize<Dictionary<string, string>>(rowJson);
        if (row is null) return (false, "行数据非法");

        var schema = DatasetCatalog.All[dataset];
        row = new Dictionary<string, string>(row, StringComparer.OrdinalIgnoreCase);
        if (!row.ContainsKey(schema.KeyField)) return (false, "缺少主键");

        return await _store.MutateAsync<Dictionary<string, string>, (bool, string)>(FileOf(dataset), rows =>
        {
            var key = schema.KeyField;
            rows.RemoveAll(r => Get(r, key).Equals(Get(row, key), StringComparison.OrdinalIgnoreCase));
            rows.Add(row);
            return (true, "");
        }).ConfigureAwait(false);
    }

    //按字段集合恢复一行
    public async Task<(bool Ok, string Error)> RestoreFieldsAsync(string dataset, string id, Dictionary<string, string> fields)
    {
        if (!DatasetCatalog.IsKnown(dataset)) return (false, "未知数据集");

        var schema = DatasetCatalog.All[dataset];
        return await _store.MutateAsync<Dictionary<string, string>, (bool, string)>(FileOf(dataset), rows =>
        {
            var key = schema.KeyField;
            var row = rows.FirstOrDefault(r => Get(r, key).Equals(id, StringComparison.OrdinalIgnoreCase));
            if (row is null) return (false, "记录不存在，无法回退");

            foreach (var kv in fields)
            {
                var target = row.FirstOrDefault(x => string.Equals(x.Key, kv.Key, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrEmpty(target.Key)) row[kv.Key] = kv.Value;
                else row[target.Key] = kv.Value;
            }
            return (true, "");
        }).ConfigureAwait(false);
    }

    //取字段值
    private static string Get(Dictionary<string, string> row, string field)
    {
        var kv = row.FirstOrDefault(x => string.Equals(x.Key, field, StringComparison.OrdinalIgnoreCase));
        return kv.Key is null ? "" : kv.Value;
    }

    //比较数字大小
    private static int CompareDecimal(string left, string right)
    {
        var okLeft = decimal.TryParse(left, out var l);
        var okRight = decimal.TryParse(right, out var r);
        if (okLeft && okRight) return l.CompareTo(r);
        return string.CompareOrdinal(left, right);
    }

    //口语说法映射到枚举
    public static string? MapEnumValue(string fieldLabel, string input, string[] options)
    {
        if (options.Length == 0) return null;
        var v = Squash(input);
        if (v.Length == 0) return null;

        var exact = options.FirstOrDefault(o => Squash(o) == v);
        if (exact is not null) return exact;

        foreach (var table in EnumAliases)
        {
            if (!LabelMatches(fieldLabel, table.Key)) continue;

            //别名全等
            foreach (var (aliases, value) in table.Value)
            {
                if (!options.Contains(value, StringComparer.Ordinal)) continue;
                if (aliases.Any(a => Squash(a) == v)) return value;
            }

            //别名包含
            foreach (var (aliases, value) in table.Value)
            {
                if (!options.Contains(value, StringComparer.Ordinal)) continue;
                if (aliases.Any(a => v.Contains(Squash(a), StringComparison.Ordinal))) return value;
                if (v.Contains(Squash(value), StringComparison.Ordinal)) return value;
            }
        }

        return null;
    }

    //字段名匹配
    private static bool LabelMatches(string fieldLabel, string tableKey)
        => fieldLabel.Contains(tableKey, StringComparison.OrdinalIgnoreCase)
           || tableKey.Contains(fieldLabel, StringComparison.OrdinalIgnoreCase);

    //去空白转小写
    private static string Squash(string? s)
        => new string((s ?? "").Where(c => !char.IsWhiteSpace(c) && c != '"' && c != '\'').ToArray()).ToLowerInvariant();

    //用关键字找客户
    public async Task<List<Dictionary<string, string>>> FindClientsByKeywordAsync(string keyword, int limit = 10)
    {
        var rows = await RowsAsync("customers").ConfigureAwait(false);
        var k = (keyword ?? "").Trim();
        if (k.Length == 0) return new List<Dictionary<string, string>>();

        var exact = rows.Where(r => Get(r, "id").Equals(k, StringComparison.OrdinalIgnoreCase)
                                    || Get(r, "name").Equals(k, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact;

        var hits = rows.Where(r =>
                Get(r, "name").Contains(k, StringComparison.OrdinalIgnoreCase) ||
                Get(r, "contact").Contains(k, StringComparison.OrdinalIgnoreCase) ||
                Get(r, "industry").Contains(k, StringComparison.OrdinalIgnoreCase) ||
                Get(r, "owner").Contains(k, StringComparison.OrdinalIgnoreCase) ||
                Get(r, "id").Contains(k, StringComparison.OrdinalIgnoreCase))
            .Take(Math.Clamp(limit, 1, 50))
            .ToList();

        return hits.Count > 0 ? hits : exact;
    }

    //用关键字找需求
    public async Task<List<Dictionary<string, string>>> FindRequirementsByKeywordAsync(string keyword, int limit = 10)
    {
        var rows = await RowsAsync("requirements").ConfigureAwait(false);
        var k = (keyword ?? "").Trim();
        if (k.Length == 0) return new List<Dictionary<string, string>>();

        var exact = rows.Where(r => Get(r, "id").Equals(k, StringComparison.OrdinalIgnoreCase)
                                    || Get(r, "title").Equals(k, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact;

        var hits = rows.Where(r =>
                Get(r, "title").Contains(k, StringComparison.OrdinalIgnoreCase) ||
                Get(r, "detail").Contains(k, StringComparison.OrdinalIgnoreCase) ||
                Get(r, "id").Contains(k, StringComparison.OrdinalIgnoreCase))
            .Take(Math.Clamp(limit, 1, 50))
            .ToList();

        return hits.Count > 0 ? hits : exact;
    }

    //拼行摘要
    public static string DescribeRow(Dictionary<string, string> row)    {
        var sb = new StringBuilder();
        foreach (var kv in row) sb.Append(kv.Key).Append('=').Append(kv.Value).Append("  ");
        return sb.ToString().TrimEnd();
    }
}
