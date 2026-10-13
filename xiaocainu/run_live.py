#本地实时联调 走完整链路写飞书表
from __future__ import annotations

import os
import sys
import json

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from models import (
    XiaoCaiAgent,
    LLMAdapter,
    MockProvider,
    FeishuBitableClient,
    Retriever,
)
from models.config import knowledge_dir


def build_agent() -> XiaoCaiAgent:
    #优先真实模型 缺依赖则降级
    try:
        llm = LLMAdapter.from_env()
    except ImportError:
        print("[提示] 未安装 openai，A 分支将用模拟回复；B/C/D 飞书功能正常。")
        llm = LLMAdapter(MockProvider())

    retriever = Retriever.from_directory(knowledge_dir())

    feishu = None
    table_id = ""
    if os.getenv("FEISHU_APP_ID"):
        feishu = FeishuBitableClient.from_env()
        table_id = os.getenv("FEISHU_TABLE_ID", "")
        print(f"[飞书] 已接入：table_id={table_id or '(未设置)'}")
    else:
        print("[飞书] 未检测到 FEISHU_APP_ID，B/C/D 走降级（不真发）。")

    return XiaoCaiAgent(llm, retriever, feishu=feishu, table_id=table_id)


def main() -> None:
    #交互式联调主循环
    agent = build_agent()
    print("\n===== 小财奴实时联调（输入问题，Ctrl+C 退出）=====")
    try:
        while True:
            try:
                text = input("\n你> ").strip()
            except EOFError:
                break
            if not text:
                continue
            if text in ("exit", "quit", "q"):
                break
            res = agent.route(text)
            print("小财> " + json.dumps(res, ensure_ascii=False, default=str))
    except KeyboardInterrupt:
        print("\n再见。")


if __name__ == "__main__":
    main()
