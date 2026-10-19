"""
M8 · 认证运行时（装配层）
========================
把 security/auth.py（纯函数）+ models/users.py（飞书用户表）+ security/guard.py（限流）
装配成网关可用的 AuthService：login / verify / logout / 会话-用户绑定。

设计要点
---------
- 登录限流**只计失败次数**（would_allow 预检 + 失败才 hit），正常登录不消耗配额；
- 账号权威在飞书用户表：角色直接取表内「角色」列（员工/财务/管理员），无二次映射；
- token 校验用 AUTH_SECRET HMAC 验签，logout 走内存 jti 黑名单；
- sid 绑定：服务端不再盲信 body 里的 session_id——某 sid 首次出现即绑定到当前用户，
  后续同 sid 换用户 → 403（防会话串用/越权读取他人草稿与上下文）。
"""
from __future__ import annotations

import os
import secrets

from security.auth import TokenRevoker, issue_token, verify_token
from security.guard import RateLimiter
from models import users
from models.feishu import FeishuBitableClient

# 登录响应/前端展示用的用户字段（绝不含 密码哈希/盐）
PUBLIC_USER_KEYS = ("工号", "姓名", "用户名", "角色")


class AuthService:
    def __init__(self, user_store: users.UserStore, secret: str, ttl: int = 7200,
                 login_limiter: RateLimiter | None = None) -> None:
        self.users = user_store
        self.secret = secret
        self.ttl = ttl
        self.login_limiter = login_limiter or RateLimiter(limit=5, window=300)
        self.revoker = TokenRevoker()
        self.sid_bindings: dict = {}  # sid -> username（内存级，重启即清）

    # -------------------------------------------------- 登录
    def login(self, username: str, password: str, client_ip: str = ""):
        """登录。返回 (http_status, payload)。失败只计失败次数的限流配额。

        账号权威在飞书用户表（口令哈希加盐存储）：三角色标签直接取表内「角色」列，
        不做二次映射——角色即数据，改权限只需改表。
        """
        username = (username or "").strip()
        key = f"{username}|{client_ip}"
        if not self.login_limiter.would_allow(key):
            return 429, {"error": "登录尝试过于频繁，请几分钟后再试"}

        item, err = users.authenticate(self.users, username, password or "")
        if err:
            self.login_limiter.allow(key)  # 仅失败计数
            return 401, {"error": err}
        f = item["fields"]
        claims = {
            "username": f.get("用户名", ""),
            "name": f.get("姓名") or f.get("用户名", ""),
            "role": f.get("角色", ""),
            "employee_id": f.get("工号", ""),
        }
        public = {k: f.get(k, "") for k in PUBLIC_USER_KEYS}
        return self._issue(username, claims, public, "飞书用户表校验")

    def _issue(self, username: str, claims: dict, public: dict, note: str):
        """签发本地会话 token（登录成功统一出口；note 注明本次账号权威来源）。"""
        token = issue_token(claims, self.secret, self.ttl)
        self.users.touch_last_login(claims.get("username") or username)  # 失败静默，不影响登录
        payload = {
            "token": token,
            "session_id": secrets.token_hex(8),  # 服务端生成的新 sid，前端切换使用
            "user": public,
        }
        if note:
            payload["backend"] = note
        return 200, payload

    # -------------------------------------------------- 校验 / 登出
    def verify(self, token: str):
        """校验 token。合法返回 claims；非法/过期/已登出返回 None。"""
        return verify_token(token or "", self.secret, revoked=self.revoker)

    def logout(self, token: str) -> bool:
        """登出：把 jti 拉黑。返回是否成功校验过该 token。"""
        claims = verify_token(token or "", self.secret, revoked=self.revoker)
        if not claims:
            return False
        self.revoker.revoke(claims.get("jti", ""))
        # 顺带清掉该用户的 sid 绑定，防残留
        self.sid_bindings = {s: u for s, u in self.sid_bindings.items()
                             if u != claims.get("username")}
        return True

    # -------------------------------------------------- sid ↔ 用户绑定
    def bind_sid(self, sid: str, username: str):
        """首次使用某 sid 绑定到用户；同 sid 换用户拒绝。返回 (ok, error)。"""
        sid = (sid or "default")[:64]
        bound = self.sid_bindings.get(sid)
        if bound is None:
            self.sid_bindings[sid] = username
            return True, ""
        if bound != username:
            return False, f"该会话已绑定用户 {bound}，请刷新页面获取新会话"
        return True, ""


def build_auth():
    """按 .env 装配 AuthService。未配置 FEISHU_USER_TABLE_ID → None（网关 fail-closed 401）。"""
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
