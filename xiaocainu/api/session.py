#会话存储 内存级带过期
from __future__ import annotations

import time


class SessionStore:
    #进程内会话 存消息与草稿
    def __init__(self, ttl: int = 1800, max_turns: int = 20) -> None:
        self.ttl = ttl            #会话空闲过期秒数
        self.max_turns = max_turns  #最多保留的消息条数
        self._sessions: dict = {}

    def _expire(self) -> None:
        #清掉过期会话
        now = time.time()
        stale = [k for k, v in self._sessions.items() if now - v["ts"] > self.ttl]
        for k in stale:
            del self._sessions[k]

    def _touch(self, sid: str) -> dict:
        #取会话 没有就新建
        self._expire()
        s = self._sessions.get(sid)
        if s is None:
            s = {"messages": [], "draft": None, "ts": time.time()}
            self._sessions[sid] = s
        s["ts"] = time.time()
        return s

    def get_messages(self, sid: str) -> list:
        #取多轮对话历史
        return self._touch(sid)["messages"]

    def append(self, sid: str, role: str, content: str) -> None:
        #追加一条消息
        s = self._touch(sid)
        s["messages"].append({"role": role, "content": content})
        if len(s["messages"]) > self.max_turns:
            s["messages"] = s["messages"][-self.max_turns:]

    def get_draft(self, sid: str):
        #取跨轮草稿
        return self._touch(sid).get("draft")

    def set_draft(self, sid: str, draft: dict) -> None:
        #存跨轮草稿
        self._touch(sid)["draft"] = draft

    def count(self) -> int:
        #统计会话数
        self._expire()
        return len(self._sessions)
