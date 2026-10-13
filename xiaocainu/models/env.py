#极简 .env 加载器 纯标准库
from __future__ import annotations

import os


def _project_root() -> str:
    #项目根在两级上层
    return os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def load_env(path: str | None = None) -> None:
    #把 .env 注入环境变量 已有值优先
    candidates: list[str] = []
    if path:
        candidates.append(path)
    candidates.append(os.path.join(os.getcwd(), ".env"))
    candidates.append(os.path.join(_project_root(), ".env"))

    target = next((c for c in candidates if os.path.isfile(c)), None)
    if not target:
        return

    with open(target, "r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            key, _, val = line.partition("=")
            key = key.strip()
            val = val.strip()
            #去掉引号包裹
            if len(val) >= 2 and val[0] == val[-1] and val[0] in ("'", '"'):
                val = val[1:-1]
            if key and key not in os.environ:
                os.environ[key] = val
