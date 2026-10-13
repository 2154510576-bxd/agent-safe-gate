#真打通测试 单次付费调用
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def load_env(path: str = ".env") -> None:
    #读 .env 只补未设置的键
    if not os.path.exists(path):
        print(f"[info] 未找到 {path}，改从当前环境变量读取")
        return
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            k, v = line.split("=", 1)
            os.environ.setdefault(k.strip(), v.strip())
    print(f"[info] 已载入 {path}")


def mask(s: str) -> str:
    #遮蔽密钥 只留头尾
    return (s[:4] + "****" + s[-2:]) if s and len(s) > 6 else ("**" if s else "<空>")


def main() -> None:
    #跑一次真实对话调用
    print("===== Agnes 真打通测试 开始 =====")
    load_env()
    provider = os.getenv("LLM_PROVIDER", "")
    print(f"[info] LLM_PROVIDER = {provider!r}")
    print(f"[info] AGNES_API_KEY = {mask(os.getenv('AGNES_API_KEY', ''))}")
    if provider != "agnes":
        print("[提示] 请设置 LLM_PROVIDER=agnes 后再运行（.env 或环境变量）")
        return
    if not os.getenv("AGNES_API_KEY"):
        print("[提示] 未检测到 AGNES_API_KEY，请先在 .env / 环境变量填写")
        return

    from models import LLMAdapter

    adapter = LLMAdapter.from_env()
    try:
        r = adapter.chat(
            "你好，用一句话介绍你自己。",
            system="你是小财，一个企业财务报销助手。",
        )
    except Exception as e:
        print(f"[错误] 调用失败: {type(e).__name__}: {e}")
        return
    print("provider :", r.provider)
    print("model    :", r.model)
    print("回复     :", r.text)
    print("耗时(ms) :", r.latency_ms)
    print("usage    :", r.usage)


if __name__ == "__main__":
    main()
