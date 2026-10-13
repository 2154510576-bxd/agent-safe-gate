#认证运行时装配层
from __future__ import annotations

import os
import secrets

from security.auth import TokenRevoker, issue_token, verify_token
from security.guard import RateLimiter
from models import users
from models.feishu import FeishuBitableClient

#对外用户字段 不含口令哈希
PUBLIC_USER_KEYS = ("工号", "姓名", "用户名", "角色")

#主服务器四角色 -> 本网关三角色标签
ROLE_LABELS = {"employee": "员工", "finance": "财务", "admin": "管理员", "superadmin": "管理员"}


class AuthService:
    #认证服务
    def __init__(self, user_store: users.UserStore, secret: str, ttl: int = 7200,
                 login_limiter: RateLimiter | None = None) -> None:
        #装配用户表 密钥 限流器
        self.users = user_store
        self.secret = secret
        self.ttl = ttl
        self.login_limiter = login_limiter or RateLimiter(limit=5, window=300)
        self.revoker = TokenRevoker()
        self.sid_bindings: dict = {}  #sid -> 用户名 内存级 重启即清

    def login(self, username: str, password: str, client_ip: str = ""):
        #登录 返回状态与载荷
        username = (username or "").strip()
        key = f"{username}|{client_ip}"
        if not self.login_limiter.would_allow(key):
            return 429, {"error": "登录尝试过于频繁，请几分钟后再试"}

        #账号权威在主服务器：先由它判定口令
        primary = self._login_via_backend(username, password or "")
        if primary is not None:
            status, claims, public, note = primary
            if status != 200:
                self.login_limiter.allow(key)  #仅失败计数
                return status, {"error": note}
            return self._issue(username, claims, public, note)

        #主服务器未启用或连不上：按本地用户表兜底（BACKEND_MODE=required 时上面已经拒绝）
        item, err = users.authenticate(self.users, username, password or "")
        if err:
            self.login_limiter.allow(key)  #仅失败计数
            return 401, {"error": err}
        f = item["fields"]
        claims = {
            "username": f.get("用户名", ""),
            "name": f.get("姓名") or f.get("用户名", ""),
            "role": f.get("角色", ""),
            "employee_id": f.get("工号", ""),
        }
        public = {k: f.get(k, "") for k in PUBLIC_USER_KEYS}
        return self._issue(username, claims, public, "本地用户表校验（主服务器未启用或不可达）")

    def _issue(self, username: str, claims: dict, public: dict, note: str):
        #签发本地会话 token
        token = issue_token(claims, self.secret, self.ttl)
        self.users.touch_last_login(claims.get("username") or username)  #失败静默忽略
        payload = {
            "token": token,
            "session_id": secrets.token_hex(8),  #服务端新生成的 sid
            "user": public,
        }
        if note:
            payload["backend"] = note
        return 200, payload

    @staticmethod
    def _login_via_backend(username: str, password: str):
        #先问主服务器 返回 None 表示要回落本地表
        try:
            from . import backend_client
        except Exception:
            return None
        if not backend_client.enabled() or not password:
            return None
        required = backend_client.mode() == "required"
        try:
            user = backend_client.login(username, password)
        except backend_client.BackendRejected as e:
            #主服务器明确拒绝：口令错或账号不可用 一律不再回落
            return 401, None, None, str(e) or "用户名或口令错误"
        except backend_client.BackendError as e:
            if required:
                return 503, None, None, f"主服务器登录失败，已按 required 模式拒绝登录：{e}"
            print(f"[backend] 主服务器不可用（{e}）；改用本地用户表校验")
            return None
        role = str((user or {}).get("role") or "")
        claims = {
            "username": (user or {}).get("userName") or username,
            "name": (user or {}).get("displayName") or (user or {}).get("userName") or username,
            "role": ROLE_LABELS.get(role, "员工"),
            "employee_id": (user or {}).get("employeeId", ""),
        }
        public = {
            "工号": claims["employee_id"],
            "姓名": claims["name"],
            "用户名": claims["username"],
            "角色": claims["role"],
        }
        note = f"主服务器已校验（角色={claims['role']}，工号={claims['employee_id']}）"
        return 200, claims, public, note

    def verify(self, token: str):
        #校验 token
        return verify_token(token or "", self.secret, revoked=self.revoker)

    def logout(self, token: str) -> bool:
        #登出 把 jti 拉黑
        claims = verify_token(token or "", self.secret, revoked=self.revoker)
        if not claims:
            return False
        self.revoker.revoke(claims.get("jti", ""))
        #清掉该用户的 sid 绑定
        self.sid_bindings = {s: u for s, u in self.sid_bindings.items()
                             if u != claims.get("username")}
        return True

    def bind_sid(self, sid: str, username: str):
        #sid 首次出现即绑定用户
        sid = (sid or "default")[:64]
        bound = self.sid_bindings.get(sid)
        if bound is None:
            self.sid_bindings[sid] = username
            return True, ""
        if bound != username:
            return False, f"该会话已绑定用户 {bound}，请刷新页面获取新会话"
        return True, ""


def build_auth():
    #按 .env 装配认证服务
    table_id = os.getenv("FEISHU_USER_TABLE_ID", "")
    if not (os.getenv("FEISHU_APP_ID") and table_id):
        print("[M8][安全] 未配置 FEISHU_USER_TABLE_ID，认证墙处于关闭状态——"
              "所有受保护端点将返回 401。请按 docs/TABLE_SETUP.md 建用户表并填写配置。")
        return None
    secret = os.getenv("AUTH_SECRET", "")
    if not secret:
        secret = secrets.token_hex(32)
        print("[M8][警告] AUTH_SECRET 未设置，本次启动使用临时随机密钥（重启后全体登录态失效）。"
              "请尽快在 .env 固化 AUTH_SECRET。")
    ttl = int(os.getenv("AUTH_TOKEN_TTL", "7200"))
    limit = int(os.getenv("AUTH_LOGIN_LIMIT", "5"))
    window = int(os.getenv("AUTH_LOGIN_WINDOW", "300"))
    client = FeishuBitableClient.from_env()
    store = users.UserStore(client, table_id)
    print(f"[M8] 认证已启用：用户表={table_id} tokenTTL={ttl}s 登录限流={limit}次/{window}s（仅失败计数）")
    return AuthService(store, secret, ttl=ttl,
                       login_limiter=RateLimiter(limit=limit, window=window))
