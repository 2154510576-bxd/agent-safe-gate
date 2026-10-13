#认证模块 口令哈希与签名 token
from __future__ import annotations

import base64
import hashlib
import hmac
import json
import secrets
import time

PBKDF2_ITERATIONS = 100_000
DEFAULT_TTL = 7200  #token 有效期 7200 秒


def hash_password(password: str, salt=None, iterations: int = PBKDF2_ITERATIONS):
    #生成盐与口令哈希 可复用旧盐
    if salt is None:
        salt = secrets.token_bytes(16)
    elif isinstance(salt, str):
        salt = bytes.fromhex(salt)
    digest = hashlib.pbkdf2_hmac("sha256", password.encode("utf-8"), salt, iterations)
    return salt.hex(), digest.hex()


def verify_password(password: str, salt_hex: str, hash_hex: str,
                    iterations: int = PBKDF2_ITERATIONS) -> bool:
    #恒时比对口令 异常一律 False
    try:
        salt = bytes.fromhex(salt_hex)
        expected = bytes.fromhex(hash_hex)
    except (ValueError, TypeError):
        return False
    if not salt or not expected:
        return False
    digest = hashlib.pbkdf2_hmac("sha256", password.encode("utf-8"), salt, iterations)
    return hmac.compare_digest(digest, expected)


def _b64url(data: bytes) -> str:
    #字节转无填充 b64url
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode("ascii")


def _b64url_decode(s: str) -> bytes:
    #b64url 补填充后解码
    pad = "=" * (-len(s) % 4)
    return base64.urlsafe_b64decode(s + pad)


def issue_token(claims: dict, secret: str, ttl: int = DEFAULT_TTL) -> str:
    #签发 token 并签名
    now = int(time.time())
    payload = dict(claims)
    payload.setdefault("iat", now)
    payload.setdefault("exp", now + int(ttl))
    payload.setdefault("jti", secrets.token_hex(8))
    body = _b64url(json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8"))
    sig = hmac.new(secret.encode("utf-8"), body.encode("ascii"), hashlib.sha256).hexdigest()
    return f"{body}.{sig}"


def verify_token(token: str, secret: str, now=None, revoked=None):
    #验签 查过期与撤销 失败返回 None
    if not token or "." not in token:
        return None
    body, sig = token.rsplit(".", 1)
    expected_sig = hmac.new(secret.encode("utf-8"), body.encode("ascii"),
                            hashlib.sha256).hexdigest()
    if not hmac.compare_digest(sig, expected_sig):
        return None
    try:
        payload = json.loads(_b64url_decode(body))
        if not isinstance(payload, dict):
            return None
    except Exception:
        return None
    current = int(time.time()) if now is None else int(now)
    exp = payload.get("exp")
    if not isinstance(exp, int) or current >= exp:
        return None
    jti = payload.get("jti", "")
    if revoked is not None and jti:
        if hasattr(revoked, "is_revoked"):
            if revoked.is_revoked(jti):
                return None
        elif jti in revoked:
            return None
    return payload


class TokenRevoker:
    #内存 jti 黑名单 重启即清空

    def __init__(self) -> None:
        #初始化已撤销集合
        self._revoked: set = set()

    def revoke(self, jti: str) -> None:
        #记入黑名单
        if jti:
            self._revoked.add(jti)

    def is_revoked(self, jti: str) -> bool:
        #判断 jti 是否已撤销
        return jti in self._revoked
