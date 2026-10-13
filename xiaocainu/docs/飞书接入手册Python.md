```markdown
# 飞书多维表格 API 调用示例

> 语言：Python
> 依赖：`requests`
> 适用对象：飞书多维表格（Bitable）

---

## 一、核心概念

多维表格的层级结构为：**App（多维表格）→ Table（数据表）→ Record（记录）/ Field（字段）**。

调用 API 需要两个核心标识：
- **`app_token`**：多维表格 App 的唯一标识
- **`table_id`**：数据表的唯一标识

两者均可从多维表格 URL 中直接提取。

**URL 形态说明**：
- URL 以 `feishu.cn/base` 开头 → 路径中的高亮部分即为 `app_token`
- URL 以 `feishu.cn/wiki` 开头 → 需先调用知识库接口获取 `obj_token`，当 `obj_type` 为 `bitable` 时该值才是 `app_token`

---

## 二、鉴权

使用 `tenant_access_token` 或 `user_access_token`，与云文档 API 一致。

```python
import requests

APP_ID = "cli_xxx"
APP_SECRET = "xxx"

def get_tenant_token():
    url = "https://open.feishu.cn/open-apis/auth/v3/tenant_access_token/internal"
    resp = requests.post(url, json={
        "app_id": APP_ID,
        "app_secret": APP_SECRET
    })
    return resp.json()["tenant_access_token"]

TOKEN = get_tenant_token()
HEADERS = {
    "Authorization": f"Bearer {TOKEN}",
    "Content-Type": "application/json; charset=utf-8"
}
```

---

## 三、记录（Record）的增删查改

### 3.1 查 — 列出记录

```python
def list_records(app_token, table_id):
    url = f"https://open.feishu.cn/open-apis/bitable/v1/apps/{app_token}/tables/{table_id}/records"
    resp = requests.get(url, headers=HEADERS)
    return resp.json()["data"]["items"]
```

### 3.2 增 — 新增记录

`fields` 参数为字典，key 为字段名，value 为字段值。不同字段类型的数据格式不同：

| 字段类型 | 数据格式 |
|---|---|
| 文本 | 字符串 |
| 数字 | 数字 |
| 单选 | 选项值字符串（新值会自动创建选项） |
| 多选 | 字符串数组 |
| 日期 | 毫秒级时间戳 |
| 复选框 | `true` / `false` |
| 人员 | 用户的 `open_id` / `union_id` / `user_id` |
| 超链接 | `{"text": "文本", "link": "URL"}` |
| 附件 | 附件 token 数组 |

```python
def create_record(app_token, table_id, fields):
    url = f"https://open.feishu.cn/open-apis/bitable/v1/apps/{app_token}/tables/{table_id}/records"
    resp = requests.post(url, headers=HEADERS, json={"fields": fields})
    return resp.json()

# 示例：新增一条记录
result = create_record(app_token, table_id, {
    "任务名称": "测试任务",
    "优先级": "高",
    "完成状态": False
})
print(result)
```

> **批量新增**：使用 `/records/batch_create` 接口，单次最多 **500 条**。

### 3.3 改 — 更新记录

```python
def update_record(app_token, table_id, record_id, fields):
    url = (f"https://open.feishu.cn/open-apis/bitable/v1/apps/{app_token}"
           f"/tables/{table_id}/records/{record_id}")
    resp = requests.put(url, headers=HEADERS, json={"fields": fields})
    return resp.json()
```

> **批量更新**：使用 `/records/batch_update` 接口。

### 3.4 删 — 删除记录

```python
def delete_record(app_token, table_id, record_id):
    url = (f"https://open.feishu.cn/open-apis/bitable/v1/apps/{app_token}"
           f"/tables/{table_id}/records/{record_id}")
    resp = requests.delete(url, headers=HEADERS)
    return resp.json()
```

> `record_id` 可通过查询记录接口获取。
> **批量删除**：使用 `/records/batch_delete` 接口。

---

## 四、字段（Field）操作

### 4.1 列出字段

```python
def list_fields(app_token, table_id):
    url = (f"https://open.feishu.cn/open-apis/bitable/v1/apps/{app_token}"
           f"/tables/{table_id}/fields")
    resp = requests.get(url, headers=HEADERS)
    return resp.json()["data"]["items"]
```

### 4.2 创建字段

字段类型由 `type`（整数）和 `ui_type`（字符串）共同决定。例如 `type=2` 配合 `ui_type="Progress"` 创建进度字段，配合 `ui_type="Rating"` 创建评分字段。

```python
def create_field(app_token, table_id, field_name, type_, ui_type=None, property_=None):
    url = (f"https://open.feishu.cn/open-apis/bitable/v1/apps/{app_token}"
           f"/tables/{table_id}/fields")
    body = {
        "field_name": field_name,
        "type": type_
    }
    if ui_type:
        body["ui_type"] = ui_type
    if property_:
        body["property"] = property_
    resp = requests.post(url, headers=HEADERS, json=body)
    return resp.json()

# 示例：创建保留 2 位小数的数字字段
create_field(app_token, table_id, "金额", 2, property_={"formatter": "0.00"})
```

> **限制**：查找引用类型（`type=19`）不支持通过接口新增或更新。

### 4.3 更新字段

字段更新为**全量更新**，未在请求体中指定的选项将被删除。

```python
def update_field(app_token, table_id, field_id, body):
    url = (f"https://open.feishu.cn/open-apis/bitable/v1/apps/{app_token}"
           f"/tables/{table_id}/fields/{field_id}")
    resp = requests.put(url, headers=HEADERS, json=body)
    return resp.json()
```

---

## 五、数据表操作

### 5.1 列出数据表

```python
def list_tables(app_token):
    url = f"https://open.feishu.cn/open-apis/bitable/v1/apps/{app_token}/tables"
    resp = requests.get(url, headers=HEADERS)
    return resp.json()["data"]["items"]
```

### 5.2 创建数据表

```python
def create_table(app_token, name):
    url = f"https://open.feishu.cn/open-apis/bitable/v1/apps/{app_token}/tables"
    resp = requests.post(url, headers=HEADERS, json={"table": {"name": name}})
    return resp.json()
```

### 5.3 更新数据表

仅支持更新数据表名称。

```python
def update_table(app_token, table_id, new_name):
    url = f"https://open.feishu.cn/open-apis/bitable/v1/apps/{app_token}/tables/{table_id}"
    resp = requests.patch(url, headers=HEADERS, json={"name": new_name})
    return resp.json()
```

---

## 六、完整调用流程

1. 用 `App ID` 和 `App Secret` 换取 `tenant_access_token`
2. 解析多维表格 URL，获取 `app_token` 和 `table_id`
   - 若 URL 含 `/wiki/`，需先换取 `app_token`
3. 按需调用对应接口：
   - **记录**：列出 / 新增 / 更新 / 删除
   - **字段**：列出 / 新增 / 更新
   - **数据表**：列出 / 新增 / 更新名称

---

## 七、注意事项

| 事项 | 说明 |
|---|---|
| **并发限制** | 同一张表不支持并发调用写接口，会触发 `Write conflict` 错误 |
| **批量上限** | 批量新增记录单次最多 500 条 |
| **记录总量** | 单表记录数上限 20,000 条 |
| **数据表总量** | 每个多维表格中数据表加仪表盘最多 100 个 |
| **权限问题** | 使用 `tenant_access_token` 时，应用必须被添加为多维表格协作者 |
| **权限配置** | 在多维表格页面「...」→「添加文档应用」中将应用添加为可管理权限 |
| **频率限制** | 部分接口有独立频率限制，超限返回 429，需退避重试 |
| **同步数据表** | 从其它数据源同步的数据表，不支持增删改记录 |
```