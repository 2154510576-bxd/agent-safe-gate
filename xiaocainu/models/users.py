#用户表 账号存飞书 Bitable
from __future__ import annotations

import time

from .feishu import FeishuBitableClient, FeishuError
try:
    #包内导入走这条
    from ..security.auth import hash_password, verify_password
except ImportError:  # pragma: no cover
    #顶层包导入时回退绝对导入
    from security.auth import hash_password, verify_password

#用户表字段 与建表文档一致
USER_FIELDS = ["工号", "姓名", "用户名", "角色", "密码哈希", "盐",
               "账号状态", "创建时间", "最后登录时间"]

#三种角色
ROLE_EMPLOYEE = "员工"
ROLE_FINANCE = "财务"
ROLE_ADMIN = "管理员"
ALLOWED_ROLES = {ROLE_EMPLOYEE, ROLE_FINANCE, ROLE_ADMIN}

#对外可见字段 不含哈希与盐
PUBLIC_USER_FIELDS = ["工号", "姓名", "用户名", "角色", "账号状态",
                      "创建时间", "最后登录时间"]

_STATUS_OK = "启用"
_STATUS_DISABLED = "停用"


def _ms_now() -> int:
    #当前毫秒时间戳
    return int(time.time() * 1000)


class UserStore:
    #用户表读取层

    def __init__(self, client: FeishuBitableClient, table_id: str,
                 cache_ttl: float = 60.0) -> None:
        #读表缓存 停用最多延迟 60 秒
        self.client = client
        self.table_id = table_id
        self.cache_ttl = cache_ttl
        self._cache: list = []       #缓存记录
        self._cached_at: float = 0.0

    def _load(self, force: bool = False) -> list:
        #按需刷新缓存
        now = time.time()
        #用 >= 而不是 >：cache_ttl=0 才真的等于"不缓存"（时钟粒度可能让两次调用取到同一个 now）
        if force or not self._cache or (now - self._cached_at) >= self.cache_ttl:
            recs = self.client.list_records(self.table_id, page_size=200)
            self._cache = [
                {"record_id": r.get("record_id"), "fields": self._normalize(r.get("fields", {}))}
                for r in recs
            ]
            self._cached_at = now
        return self._cache

    @staticmethod
    def _normalize(fields: dict) -> dict:
        #字段取值并归一化
        user = {k: fields.get(k) for k in USER_FIELDS}
        for k in ("工号", "姓名", "用户名", "角色", "账号状态"):
            v = user.get(k)
            user[k] = v.strip() if isinstance(v, str) else ""
        return user

    def get_by_username(self, username: str):
        #按用户名精确查找
        name = (username or "").strip()
        if not name:
            return None
        for item in self._load():
            if item["fields"].get("用户名") == name:
                return item
        return None

    def list_users(self) -> list:
        #全量用户 屏蔽凭据
        out = []
        for item in self._load():
            u = {k: item["fields"].get(k, "") for k in PUBLIC_USER_FIELDS}
            u["record_id"] = item["record_id"]
            out.append(u)
        return out

    def invalidate(self) -> None:
        #清缓存 下次读最新
        self._cache = []
        self._cached_at = 0.0

    def touch_last_login(self, username: str) -> None:
        #回写最后登录时间
        try:
            item = self.get_by_username(username)
            if item and item.get("record_id"):
                self.client.update_record(self.table_id, item["record_id"],
                                          {"最后登录时间": _ms_now()})
        except FeishuError:
            pass


def make_user_record(*, username: str, name: str, role: str,
                     password: str, employee_id: str = "", status: str = _STATUS_OK) -> dict:
    #生成用户表记录 口令只存哈希
    if role not in ALLOWED_ROLES:
        raise ValueError(f"角色必须是 {'/'.join(sorted(ALLOWED_ROLES))}，收到：{role!r}")
    if not (username or "").strip():
        raise ValueError("用户名不能为空")
    if not password:
        raise ValueError("密码不能为空")
    salt_hex, hash_hex = hash_password(password)
    return {
        "工号": employee_id or "",
        "姓名": name or username,
        "用户名": username.strip(),
        "角色": role,
        "密码哈希": hash_hex,
        "盐": salt_hex,
        "账号状态": status,
        "创建时间": _ms_now(),
    }


def authenticate(store: UserStore, username: str, password: str):
    #登录校验 防用户名枚举
    item = store.get_by_username(username)
    if not item:
        return None, "用户名或密码错误"
    f = item["fields"]
    if f.get("账号状态") == _STATUS_DISABLED:
        return None, "账号已停用，请联系管理员"
    if not verify_password(password or "", f.get("盐", ""), f.get("密码哈希", "")):
        return None, "用户名或密码错误"
    return item, ""


def reset_password(store: UserStore, username: str, password: str) -> str:
    #重设口令 每次换新盐
    item = store.get_by_username(username)
    if not item:
        raise ValueError(f"用户不存在：{username}")
    if not password:
        raise ValueError("密码不能为空")
    salt_hex, hash_hex = hash_password(password)
    store.client.update_record(store.table_id, item["record_id"],
                               {"密码哈希": hash_hex, "盐": salt_hex})
    store.invalidate()
    return username


def set_user_status(store: UserStore, username: str, enabled: bool) -> str:
    #启用或停用账号
    item = store.get_by_username(username)
    if not item:
        raise ValueError(f"用户不存在：{username}")
    store.client.update_record(store.table_id, item["record_id"],
                               {"账号状态": _STATUS_OK if enabled else _STATUS_DISABLED})
    store.invalidate()
    return username
