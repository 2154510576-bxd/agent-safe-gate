#边界防御 净化 注入拦截 限流
from __future__ import annotations

import time

#允许的空白 换行 制表 空格
_ALLOWED_CONTROLS = {"\n", "\t", " "}


def sanitize_text(text: str, max_length: int = 2000) -> str:
    #净化输入 剥控制字符 去空白 截断
    if not isinstance(text, str):
        text = str(text)
    out = []
    for ch in text:
        code = ord(ch)
        if code == 0:                       #空字节必剥
            continue
        if code < 32 and ch not in _ALLOWED_CONTROLS:
            continue                        #其余控制字符剥离
        if code == 127:                     #删除符剥离
            continue
        out.append(ch)
    cleaned = "".join(out).strip()
    if max_length and len(cleaned) > max_length:
        cleaned = cleaned[:max_length]
    return cleaned


#注入签名表 大小写不敏感子串匹配
INJECTION_SIGNATURES = [
    #中文越权与指令覆盖
    "忽略上面的", "忽略之前的", "忽略先前的", "忽略之前的指令", "忽略上文", "无视上面的",
    "无视规则", "无视指令", "不要遵守", "不必遵守", "忘记你的身份", "忘记之前",
    "你现在是", "你是管理员", "你是系统", "假装你是", "假装成", "角色扮演成",
    "系统提示", "系统提示词", "提示词是", "你的指令是",
    "暴露你的", "透露你的", "泄露你的", "告诉我你的", "展示你的提示",
    "绕过限制", "突破限制", "越权执行", "解除限制", "不受约束",
    #英文越权与指令覆盖
    "ignore the above", "ignore previous", "ignore all", "disregard", "forget your",
    "you are now", "pretend you are", "pretend to be", "system prompt",
    "reveal your", "expose your", "jailbreak", "break the rule", "bypass",
    "do not follow", "don't follow", "override",
]


def check_injection(text: str) -> tuple:
    #命中注入词则拦截 未命中返回空串
    if not isinstance(text, str):
        text = str(text)
    lowered = text.lower()
    for sig in INJECTION_SIGNATURES:
        if sig.lower() in lowered:
            return (True, sig)
    return (False, "")


class RateLimiter:
    #按会话限流 默认 30 次/60 秒

    def __init__(self, limit: int = 30, window: int = 60) -> None:
        #记录上限 窗口与命中表
        self.limit = limit          #窗口内最大放行次数
        self.window = window        #窗口秒数
        self._hits: dict = {}       #会话到时间戳列表

    def _expire(self, sid: str, now: float) -> None:
        #清掉窗口外的旧命中
        cutoff = now - self.window
        hits = self._hits.get(sid)
        if hits:
            kept = [t for t in hits if t > cutoff]
            if kept:
                self._hits[sid] = kept
            else:
                self._hits.pop(sid, None)

    def allow(self, sid: str, now: float | None = None) -> bool:
        #放行则记一次命中 超限返回 False
        now = now if now is not None else time.time()
        self._expire(sid, now)
        hits = self._hits.setdefault(sid, [])
        if len(hits) >= self.limit:
            return False
        hits.append(now)
        return True

    def count(self, sid: str, now: float | None = None) -> int:
        #窗口内已命中次数
        now = now if now is not None else time.time()
        self._expire(sid, now)
        return len(self._hits.get(sid, []))

    def would_allow(self, sid: str, now: float | None = None) -> bool:
        #只读判断能否放行 不记命中
        now = now if now is not None else time.time()
        self._expire(sid, now)
        return len(self._hits.get(sid, [])) < self.limit
