#适配层单测 用 Mock 不联网
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from models.llm import LLMAdapter, MockProvider, LLMResult


def test_mock_basic():
    #消息组装与返回归一化
    mock = MockProvider()
    adapter = LLMAdapter(mock)
    r = adapter.chat("你好", system="你是小财报销助手")
    assert isinstance(r, LLMResult)
    assert r.provider == "mock"
    assert r.text.startswith("[MOCK]")
    #system 与 user 都正确组装
    assert mock.calls[0]["system"] == "你是小财报销助手"
    assert mock.calls[0]["messages"][-1]["content"] == "你好"
    print("[PASS] test_mock_basic")


def test_retry_then_fail():
    #重试两次后抛出异常
    class Flaky(MockProvider):
        #总是抛错的假提供方
        def __init__(self):
            #计数初始化
            super().__init__()
            self.n = 0

        def complete(self, system, messages, **kw):
            #每次调用都抛错
            self.n += 1
            raise RuntimeError("boom")

    flaky = Flaky()
    adapter = LLMAdapter(flaky, retries=2)
    raised = False
    try:
        adapter.chat("x")
    except RuntimeError:
        raised = True
    assert raised, "应当抛出 RuntimeError"
    assert flaky.n == 2, "应当重试 2 次"
    print("[PASS] test_retry_then_fail")


def test_history_preserved():
    #历史消息被带上
    mock = MockProvider()
    adapter = LLMAdapter(mock)
    adapter.chat("第一句", history=[{"role": "user", "content": "上一条"}])
    assert mock.calls[0]["messages"][0]["content"] == "上一条"
    print("[PASS] test_history_preserved")


def test_rate_limit_backoff():
    #429 时指数退避重试
    import time as _time

    sleeps = []
    real_sleep = _time.sleep
    _time.sleep = lambda s: sleeps.append(s)  #用记录代替真实等待

    class RateLimitedThenOK(MockProvider):
        #前两次 429 后成功
        def __init__(self):
            #计数初始化
            super().__init__()
            self.n = 0

        def complete(self, system, messages, **kw):
            #前两次抛 429
            self.n += 1
            if self.n <= 2:
                raise RuntimeError("Error code: 429 - 速率限制")
            return MockProvider.complete(self, system, messages, **kw)

    rl = RateLimitedThenOK()
    adapter = LLMAdapter(rl, retries=4)
    r = adapter.chat("x")
    _time.sleep = real_sleep  #还原 sleep
    assert r.text.startswith("[MOCK]"), "第三次应成功返回"
    assert rl.n == 3, f"前两次429退避+第三次成功, got {rl.n}"
    #退避间隔应为 2s 与 4s
    assert sleeps[:2] == [2, 4], f"退避间隔应为 [2,4], got {sleeps}"
    print("[PASS] test_rate_limit_backoff")


if __name__ == "__main__":
    test_mock_basic()
    test_retry_then_fail()
    test_history_preserved()
    test_rate_limit_backoff()
    print("\n全部 M1 单测通过")
