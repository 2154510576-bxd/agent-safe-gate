#LLM 适配器 本地与云端统一入口
from __future__ import annotations

import os
import time
import logging
from dataclasses import dataclass, field
from typing import Dict, List, Optional, Protocol, runtime_checkable

from .config import get_provider_config

logger = logging.getLogger("xiaocai.llm")


@dataclass
class LLMResult:
    #归一化模型返回
    text: str
    model: str
    provider: str
    latency_ms: int
    usage: Dict = field(default_factory=dict)


@runtime_checkable
class LLMProvider(Protocol):
    #模型后端接口
    name: str

    def complete(
        self, system: str, messages: List[Dict[str, str]], **kw
    ) -> LLMResult:
        #发起一次补全
        ...


class OpenAICompatProvider:
    #OpenAI 兼容后端 本地与云端通用

    def __init__(
        self,
        name: str,
        base_url: str,
        api_key: str,
        model: str,
        timeout: int = 60,
    ) -> None:
        from openai import OpenAI  #延迟导入

        self.name = name
        self.model = model
        self._client = OpenAI(base_url=base_url, api_key=api_key, timeout=timeout)

    def complete(
        self,
        system: str,
        messages: List[Dict[str, str]],
        temperature: float = 0.1,
        max_tokens: int = 1024,
        **kw,
    ) -> LLMResult:
        #调模型并归一化返回
        chat = [{"role": "system", "content": system}] if system else []
        chat += messages
        t0 = time.time()
        resp = self._client.chat.completions.create(
            model=self.model,
            messages=chat,
            temperature=temperature,
            max_tokens=max_tokens,
        )
        latency = int((time.time() - t0) * 1000)
        usage: Dict = {}
        if resp.usage:
            try:
                usage = resp.usage.model_dump()
            except Exception:
                usage = {}
        return LLMResult(
            text=resp.choices[0].message.content or "",
            model=self.model,
            provider=self.name,
            latency_ms=latency,
            usage=usage,
        )


class MockProvider:
    #测试用假后端 不联网不花钱

    name = "mock"

    def __init__(self, model: str = "mock-model", canned: str = "[MOCK] 这是模拟回复。") -> None:
        #默认假模型名与假回复
        self.model = model
        self.canned = canned
        self.calls: List[Dict] = []

    def complete(
        self, system: str, messages: List[Dict[str, str]], **kw
    ) -> LLMResult:
        #记录调用并直接返回假回复
        self.calls.append({"system": system, "messages": messages, "kw": kw})
        time.sleep(0.01)
        return LLMResult(
            text=self.canned,
            model=self.model,
            provider=self.name,
            latency_ms=10,
            usage={},
        )


class LLMAdapter:
    #对外统一入口

    def __init__(self, provider: LLMProvider, retries: int = 2) -> None:
        #保存后端与重试次数
        self.provider = provider
        self.retries = retries

    @classmethod
    def from_env(cls) -> "LLMAdapter":
        #切后端只改 .env
        cfg = get_provider_config()
        p: LLMProvider = OpenAICompatProvider(
            name=cfg["name"],
            base_url=cfg["base_url"],
            api_key=cfg["api_key"],
            model=cfg["model"],
        )
        return cls(p)

    def chat(
        self,
        user_prompt: str,
        system: str = "",
        history: Optional[List[Dict]] = None,
        max_retries: Optional[int] = None,
        **kw,
    ) -> LLMResult:
        #拼消息并按次数重试
        messages = list(history or [])
        messages.append({"role": "user", "content": user_prompt})
        retries = self.retries if max_retries is None else max_retries
        last_err: Optional[Exception] = None
        for attempt in range(1, retries + 1):
            try:
                return self.provider.complete(system, messages, **kw)
            except Exception as e:
                last_err = e
                #429 指数退避重试
                if _is_rate_limit(e) and attempt < retries:
                    backoff = min(2 ** attempt, 30)
                    logger.warning(
                        "LLM 速率限制(429)，%ss 后重试(第%d次)", backoff, attempt
                    )
                    time.sleep(backoff)
                else:
                    logger.warning("LLM 调用失败(第%d次): %s", attempt, e)
        raise RuntimeError(f"LLM 调用在 {retries} 次重试后仍失败: {last_err}")


def _is_rate_limit(exc: Exception) -> bool:
    #粗略识别 429 限流报错
    s = str(exc)
    return (
        "429" in s
        or "RateLimit" in s
        or "rate limit" in s.lower()
        or "速率限制" in s
    )
