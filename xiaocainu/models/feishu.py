#飞书多维表格客户端 纯标准库实现
from __future__ import annotations

import os
import re
import time
import json as _json
import urllib.request
import urllib.parse
from datetime import datetime, timezone, timedelta
from typing import Dict, List, Optional, Protocol, runtime_checkable


#飞书接口异常
class FeishuError(Exception):
    """飞书接口错误。"""


#传输层接口 可插拔
@runtime_checkable
class FeishuTransport(Protocol):
    #发一次请求 返回解析后的 JSON
    def request(
        self,
        method: str,
        url: str,
        *,
        json: Optional[dict] = None,
        params: Optional[dict] = None,
        headers: Optional[dict] = None,
        data: Optional[bytes] = None,
    ) -> dict:
        #由实现方补齐
        ...


#真实传输层 基于 urllib
class UrllibTransport:
    def __init__(self, timeout: int = 10, verbose: bool = False) -> None:
        #超时与调试开关
        self.timeout = timeout
        self.verbose = verbose

    def request(
        self,
        method: str,
        url: str,
        *,
        json: Optional[dict] = None,
        params: Optional[dict] = None,
        headers: Optional[dict] = None,
        data: Optional[bytes] = None,
    ) -> dict:
        #发起 HTTP 请求
        if params:
            sep = "&" if "?" in url else "?"
            url = url + sep + urllib.parse.urlencode(params)
        if json is not None:
            data = _json.dumps(json).encode("utf-8")
        req = urllib.request.Request(url, data=data, method=method)
        for k, v in (headers or {}).items():
            req.add_header(k, v)
        #JSON 请求补默认头
        if json is not None and "content-type" not in {k.lower() for k in (headers or {})}:
            req.add_header("Content-Type", "application/json")
        try:
            with urllib.request.urlopen(req, timeout=self.timeout) as resp:
                raw = resp.read().decode("utf-8")
        except urllib.error.HTTPError as e:
            #HTTP 错误也解析 body
            try:
                raw = e.read().decode("utf-8")
            except Exception:
                raise FeishuError(f"飞书 HTTP 错误 {e.code}") from e
        if self.verbose:
            #调试开关 打印原始回传
            print(f"[Feishu raw] {method} {url}\n  {raw}")
        try:
            return _json.loads(raw)
        except Exception as exc:  #统一转成飞书异常
            raise FeishuError(f"飞书响应解析失败: {exc}") from exc


#multipart 编码
def _build_multipart(
    fields: Dict[str, str],
    file_field: str,
    filename: str,
    content_type: str,
    file_bytes: bytes,
    boundary: str,
) -> bytes:
    #拼 multipart 报文
    parts: list = []
    for k, v in fields.items():
        parts.append(
            (
                f"--{boundary}\r\n"
                f"Content-Disposition: form-data; name=\"{k}\"\r\n\r\n"
                f"{v}\r\n"
            ).encode("utf-8")
        )
    parts.append(
        (
            f"--{boundary}\r\n"
            f"Content-Disposition: form-data; name=\"{file_field}\"; filename=\"{filename}\"\r\n"
            f"Content-Type: {content_type}\r\n\r\n"
        ).encode("utf-8")
    )
    parts.append(file_bytes)
    parts.append(f"\r\n--{boundary}--\r\n".encode("utf-8"))
    return b"".join(parts)


#飞书 Bitable 客户端 鉴权与读写
class FeishuBitableClient:
    #飞书开放平台地址
    BASE = "https://open.feishu.cn/open-apis"

    def __init__(
        self,
        app_id: str,
        app_secret: str,
        app_token: str = "",
        transport: Optional[FeishuTransport] = None,
        token_margin: int = 60,
        verbose: bool = False,
    ) -> None:
        self.app_id = app_id
        self.app_secret = app_secret  #凭证只存实例不打印
        self.app_token = app_token  #多维表格 base id
        self.transport = transport or UrllibTransport(verbose=verbose)
        self.token_margin = token_margin
        self.verbose = verbose
        self._token: Optional[str] = None
        self._token_exp: float = 0.0
        self._fields_cache: Dict[str, set] = {}  #字段名进程内缓存

    @classmethod
    def from_env(cls) -> "FeishuBitableClient":
        #从环境变量读凭证
        return cls(
            os.getenv("FEISHU_APP_ID", ""),
            os.getenv("FEISHU_APP_SECRET", ""),
            os.getenv("FEISHU_APP_TOKEN", ""),
        )

    #取令牌 缓存并提前续期
    def _ensure_token(self) -> str:
        now = time.time()
        if self._token and now < self._token_exp:
            return self._token
        resp = self.transport.request(
            "POST",
            f"{self.BASE}/auth/v3/tenant_access_token/internal",
            json={"app_id": self.app_id, "app_secret": self.app_secret},
        )
        if resp.get("code", -1) != 0:
            #只透传服务端 msg 不含本地密钥
            raise FeishuError(f"获取飞书令牌失败 code={resp.get('code')} msg={resp.get('msg')}")
        self._token = resp.get("tenant_access_token")
        expire = resp.get("expire", 7200)
        self._token_exp = now + max(0, int(expire) - self.token_margin)
        return self._token

    def _auth_headers(self) -> dict:
        #带上令牌的请求头
        return {"Authorization": f"Bearer {self._ensure_token()}"}

    def _require_app_token(self, app_token: Optional[str]) -> str:
        #取 app_token 缺失则报错
        at = app_token or self.app_token
        if not at:
            raise FeishuError("缺少 app_token（多维表格 base id）")
        return at

    #批量新增记录
    def create_records(
        self, table_id: str, records: List[dict], app_token: str = ""
    ) -> List[str]:
        #返回 record_id 列表
        at = self._require_app_token(app_token)
        payload = {"records": [{"fields": f} for f in records]}
        resp = self.transport.request(
            "POST",
            f"{self.BASE}/bitable/v1/apps/{at}/tables/{table_id}/records/batch_create",
            json=payload,
            headers=self._auth_headers(),
        )
        if resp.get("code", -1) != 0:
            raise FeishuError(f"飞书写入失败 code={resp.get('code')} msg={resp.get('msg')}")
        recs = resp.get("data", {}).get("records", [])
        ids = [r.get("record_id") for r in recs]
        if self.verbose:
            print(f"[Feishu create_records] 写入 {len(ids)} 条 -> {ids}")
        #只回 record_id 屏蔽原始响应
        return ids

    #更新单条记录的部分字段
    def update_record(
        self, table_id: str, record_id: str, fields: dict, app_token: str = ""
    ) -> str:
        #fields 只放要改的键
        at = self._require_app_token(app_token)
        resp = self.transport.request(
            "PUT",
            f"{self.BASE}/bitable/v1/apps/{at}/tables/{table_id}/records/{record_id}",
            json={"fields": fields},
            headers=self._auth_headers(),
        )
        if resp.get("code", -1) != 0:
            raise FeishuError(f"飞书更新失败 code={resp.get('code')} msg={resp.get('msg')}")
        rid = resp.get("data", {}).get("record", {}).get("record_id") or record_id
        if self.verbose:
            print(f"[Feishu update_record] {rid} 更新字段 {list(fields.keys())}")
        return rid

    #上传附件 返回 file_token
    def upload_attachment(
        self,
        filename: str,
        file_bytes: bytes,
        content_type: str = "application/octet-stream",
        app_token: str = "",
    ) -> str:
        #拼 multipart 上传报文
        at = self._require_app_token(app_token)
        if not file_bytes:
            raise FeishuError("附件内容为空")
        if len(file_bytes) > 20 * 1024 * 1024:
            raise FeishuError(f"附件超过 20MB 上限（{len(file_bytes)} 字节）")
        boundary = "xiaocainu-m9-3a9f1c7d5e2b"
        body = _build_multipart(
            fields={
                "file_name": filename,
                "parent_type": "bitable_file",
                "parent_node": at,
                "size": str(len(file_bytes)),
            },
            file_field="file",
            filename=filename,
            content_type=content_type,
            file_bytes=file_bytes,
            boundary=boundary,
        )
        resp = self.transport.request(
            "POST",
            f"{self.BASE}/drive/v1/medias/upload_all",
            data=body,
            headers={
                **self._auth_headers(),
                "Content-Type": f"multipart/form-data; boundary={boundary}",
            },
        )
        if resp.get("code", -1) != 0:
            raise FeishuError(f"飞书附件上传失败 code={resp.get('code')} msg={resp.get('msg')}")
        token = (resp.get("data") or {}).get("file_token", "")
        if not token:
            raise FeishuError("飞书附件上传响应缺少 file_token")
        if self.verbose:
            print(f"[Feishu upload_attachment] {filename} -> file_token={token}")
        return token

    #查询记录
    def list_records(
        self,
        table_id: str,
        *,
        page_size: int = 20,
        filter_expr: str = "",
        app_token: str = "",
    ) -> List[dict]:
        #返回 record_id 与字段
        at = self._require_app_token(app_token)
        params: Dict[str, object] = {"page_size": page_size}
        if filter_expr:
            params["filter"] = filter_expr
        resp = self.transport.request(
            "GET",
            f"{self.BASE}/bitable/v1/apps/{at}/tables/{table_id}/records",
            params=params,
            headers=self._auth_headers(),
        )
        if resp.get("code", -1) != 0:
            raise FeishuError(f"飞书查询失败 code={resp.get('code')} msg={resp.get('msg')}")
        items = resp.get("data", {}).get("items", [])
        return [{"record_id": it.get("record_id"), "fields": it.get("fields", {})} for it in items]

    #查询表字段定义
    def list_fields(self, table_id: str, *, app_token: str = "") -> List[dict]:
        #写前探测表结构
        at = self._require_app_token(app_token)
        resp = self.transport.request(
            "GET",
            f"{self.BASE}/bitable/v1/apps/{at}/tables/{table_id}/fields",
            headers=self._auth_headers(),
        )
        if resp.get("code", -1) != 0:
            raise FeishuError(f"飞书字段查询失败 code={resp.get('code')} msg={resp.get('msg')}")
        return resp.get("data", {}).get("items", [])

    def field_names(self, table_id: str, *, app_token: str = "", use_cache: bool = True) -> set:
        #字段名集合 进程内缓存
        cached = self._fields_cache.get(table_id)
        if use_cache and cached is not None:
            return cached
        items = self.list_fields(table_id, app_token=app_token)
        names = {it.get("field_name", "") for it in items} - {""}
        self._fields_cache[table_id] = names
        return names

    def get_record(self, table_id: str, record_id: str, app_token: str = "") -> dict:
        #取单条记录
        at = self._require_app_token(app_token)
        resp = self.transport.request(
            "GET",
            f"{self.BASE}/bitable/v1/apps/{at}/tables/{table_id}/records/{record_id}",
            headers=self._auth_headers(),
        )
        if resp.get("code", -1) != 0:
            raise FeishuError(f"飞书单条查询失败 code={resp.get('code')} msg={resp.get('msg')}")
        data = resp.get("data", {})
        #兼容两种返回结构
        record = data.get("record") if isinstance(data.get("record"), dict) else data
        return {
            "record_id": record.get("record_id") or record.get("id"),
            "fields": record.get("fields", {}),
        }


#报销单字段清单 兼容旧表缺列
REIMBURSEMENT_FIELDS = [
    "事由", "单号", "合规结果", "审核人", "报销类型",
    "记录提交时间", "状态", "申请人", "票据状态", "部门", "金额", "风险摘要",
    "城市", "发票日期",
    "工号", "审批意见", "审批时间",
    "发票信息", "发票附件",  #发票号码与发票附件
]
#直交四类报销类型
ALLOWED_TYPES = {"差旅费", "招待费", "办公用品", "交通费"}

#企业标准枚举白名单 写库前强校验
DEPT_WHITELIST = (
    "研发部", "市场部", "财务部", "人力资源部", "行政部", "生产部", "销售部",
    "采购部", "信息技术部", "质量管理部", "客户服务部",
)
#城市字典
_CITY_T1 = ("北京", "上海", "广州", "深圳")  #一线
_CITY_T2 = ("成都", "杭州", "武汉", "西安", "南京", "重庆", "苏州", "天津", "长沙",
            "郑州", "青岛", "宁波", "东莞", "无锡", "沈阳", "昆明", "佛山", "合肥", "济南")  #新一线
_CITY_T3 = ("福州", "厦门", "哈尔滨", "太原", "南昌", "南宁", "贵阳", "石家庄", "兰州",
            "大连", "常州", "温州", "烟台", "泉州")  #二线
CITY_WHITELIST = _CITY_T1 + _CITY_T2 + _CITY_T3
#城市住宿上限分三档
CITY_TIER = {c: 500 for c in _CITY_T1}
CITY_TIER.update({c: 350 for c in _CITY_T2})
CITY_TIER.update({c: 250 for c in _CITY_T3})
#票据与状态枚举
ALLOWED_INVOICE = ("有票", "无票", "未提")
COMPLIANCE_RESULTS = ("通过", "风险", "驳回", "超2000需总经理审批")
STATUS_VALUES = ("待审核", "审核中", "已通过", "已驳回")


def validate_enum_fields(record: dict) -> list:
    #返回非法字段值 空即全合法
    errors = []
    for field, allowed in (
        ("部门", DEPT_WHITELIST),
        ("城市", CITY_WHITELIST),
        ("报销类型", ALLOWED_TYPES),
        ("票据状态", ALLOWED_INVOICE),
        ("合规结果", COMPLIANCE_RESULTS),
        ("状态", STATUS_VALUES),
    ):
        v = record.get(field)
        if v and v not in allowed:
            errors.append((field, v))
    return errors

COMPANY_NAME = "示例科技有限公司"  #占位 比赛替换真实公司名


def _parse_date_to_ts(date_str):
    #日期转东八区当天零点毫秒
    if not date_str:
        return None
    s = str(date_str).strip()
    cn = timezone(timedelta(hours=8))  #固定东八区 避免时区漂移
    for fmt in ("%Y/%m/%d", "%Y-%m-%d", "%Y.%m.%d", "%Y年%m月%d日", "%Y%m%d"):
        try:
            dt = datetime.strptime(s, fmt).replace(tzinfo=cn)
            return int(dt.timestamp() * 1000)
        except ValueError:
            continue
    return None


def build_reimbursement_record(
    *,
    applicant: str = "",
    department: str = "",
    rtype: str = "",
    amount=0,
    reason: str = "",
    has_invoice: str = "",
    city: str = "",
    date_str: str = "",
    compliance: str = "",
    risk_summary: str = "",
    status: str = "待审核",
    employee_id: str = "",
) -> dict:
    #拼报销单字段字典
    now_ts = int(time.time() * 1000)
    invoice_ts = _parse_date_to_ts(date_str) or now_ts
    try:
        amount_val = float(amount)
    except (TypeError, ValueError):
        amount_val = 0.0
    rec = {
        "申请人": applicant,
        "部门": department,
        "报销类型": rtype,
        "金额": amount_val,
        "事由": reason,
        "票据状态": has_invoice,
        "状态": status,
        "城市": city,
        "发票日期": invoice_ts,
        "记录提交时间": now_ts,
        "合规结果": compliance,
        "风险摘要": risk_summary,
    }
    if employee_id:
        rec["工号"] = employee_id
    return rec


def public_view(record: dict) -> dict:
    #只留业务字段给聊天层
    fields = record.get("fields", {})
    return {k: v for k, v in fields.items() if k in REIMBURSEMENT_FIELDS}


def filter_by_schema(record: dict, available_fields) -> dict:
    #按真实表字段过滤 剔除未知键
    allow = set(available_fields or ())
    if not allow:
        return record
    return {k: v for k, v in record.items() if k in allow}
