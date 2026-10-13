#模型与知识库配置入口
from __future__ import annotations

import os

PROVIDER_DEFAULTS = {
    "agnes": {
        "name": "agnes",
        "base_url": "https://api.agnes-ai.cn/v1",
        "api_key_env": "AGNES_API_KEY",
        "model_env": "AGNES_MODEL",
        "model_default": "agnes-2.5-flash",
        "api_key_default": "",
    },
    "deepseek": {
        "name": "deepseek",
        "base_url": "https://api.deepseek.com",
        "api_key_env": "DEEPSEEK_API_KEY",
        "model_env": "DEEPSEEK_MODEL",
        "model_default": "deepseek-chat",
        "api_key_default": "",
    },
    "ollama": {
        "name": "ollama",
        "base_url": "http://localhost:11434/v1",
        "api_key_env": "OLLAMA_API_KEY",
        "model_env": "OLLAMA_MODEL",
        "model_default": "qwen2.5:7b",
        "api_key_default": "ollama",  #本地 ollama 无需密钥
    },
}


def get_provider_config(provider: str | None = None) -> dict:
    #取当前供应商的模型配置
    provider = (provider or os.getenv("LLM_PROVIDER", "ollama")).lower()
    spec = PROVIDER_DEFAULTS.get(provider, PROVIDER_DEFAULTS["ollama"])
    base_url = os.getenv(f"{provider.upper()}_BASE_URL", spec["base_url"])
    api_key = os.getenv(spec["api_key_env"], spec.get("api_key_default", ""))
    model = os.getenv(spec["model_env"], spec["model_default"])
    return {"name": spec["name"], "base_url": base_url, "api_key": api_key, "model": model}


def knowledge_dir() -> str:
    #知识库目录
    return os.getenv("KNOWLEDGE_DIR", "knowledge")


def get_ocr_config() -> dict:
    #OCR 走视觉模型
    return {
        "name": os.getenv("OCR_PROVIDER", "deepseek-ocr"),
        "base_url": os.getenv("OCR_BASE_URL", os.getenv("DEEPSEEK_BASE_URL", "https://api.deepseek.com")),
        "api_key": os.getenv("OCR_API_KEY", os.getenv("DEEPSEEK_API_KEY", "")),
        "model": os.getenv("OCR_MODEL", os.getenv("DEEPSEEK_VL_MODEL", "deepseek-flash")),
        "detail": os.getenv("OCR_DETAIL", "high"),
        "max_image_mb": int(os.getenv("OCR_MAX_IMAGE_MB", "8")),
    }
