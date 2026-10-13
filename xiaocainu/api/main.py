#API 网关 对外提供对话报销等接口
from __future__ import annotations

import os
import sys
import base64
from typing import Any, Dict, Tuple

from pydantic import BaseModel, Field

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from models import (
    XiaoCaiAgent,
    LLMAdapter,
    MockProvider,
    FeishuBitableClient,
    Retriever,
)
from models.config import knowledge_dir, get_ocr_config
from models.feishu import build_reimbursement_record
from models.ocr import (
    InvoiceOCREngine, MockOcrEngine, OCRError, build_invoice_preview,
)
from models.users import ROLE_EMPLOYEE
from .session import SessionStore
from .auth_service import AuthService, build_auth
from . import backend_client
from security.guard import sanitize_text, check_injection, RateLimiter

from fastapi import FastAPI, HTTPException, Header, Request
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import FileResponse, HTMLResponse


#请求模型 字段限长挡住非法入参
class ChatRequest(BaseModel):
    session_id: str = Field(default="default", max_length=64)
    text: str = Field(..., min_length=1, max_length=2000)  #长度上限 防超长请求体
    #可选发票图 base64
    image_b64: str = Field(default="", max_length=12_000_000)
    image_mime: str = Field(default="image/jpeg", max_length=32)
    #OCR 发票字段 前端回传
    invoice_no: str = Field(default="", max_length=64)
    invoice_seller: str = Field(default="", max_length=64)


class OcrRequest(BaseModel):
    #OCR 请求体 图片走 base64
    session_id: str = Field(default="default", max_length=64)
    image_b64: str = Field(..., min_length=8, max_length=12_000_000)
    filename: str = Field(default="", max_length=64)


class LoginRequest(BaseModel):
    #登录请求体
    username: str = Field(..., min_length=1, max_length=32)
    password: str = Field(..., min_length=1, max_length=64)


class ReimburseRequest(BaseModel):
    #填单请求体
    applicant: str = Field(default="", max_length=32)
    department: str = Field(default="", max_length=32)
    rtype: str = Field(default="", max_length=16)
    amount: float = Field(default=0, ge=0, le=10_000_000)
    reason: str = Field(default="", max_length=200)
    has_invoice: str = Field(default="", max_length=8)
    city: str = Field(default="", max_length=16)
    date_str: str = Field(default="", max_length=24)
    compliance: str = Field(default="通过", max_length=16)
    risk_summary: str = Field(default="", max_length=200)
    status: str = Field(default="待审核", max_length=16)


class ProgressRequest(BaseModel):
    #进度查询请求体
    applicant: str = Field(default="", max_length=32)
    bill_no: str = Field(default="", max_length=32)


def build_agent() -> XiaoCaiAgent:
    #装配 Agent 无飞书则降级
    try:
        llm = LLMAdapter.from_env()
    except ImportError:
        print("[M5] 未安装 openai，A 分支将用模拟回复；B/C/D 飞书功能正常。")
        llm = LLMAdapter(MockProvider())
    retriever = Retriever.from_directory(knowledge_dir())
    feishu = None
    table_id = ""
    if os.getenv("FEISHU_APP_ID"):
        feishu = FeishuBitableClient.from_env()
        table_id = os.getenv("FEISHU_TABLE_ID", "")
        print(f"[M5] 飞书已接入：table_id={table_id or '(未设置)'}")
    else:
        print("[M5] 未检测到 FEISHU_APP_ID，B/C/D 走降级（不真发）。")
    return XiaoCaiAgent(llm, retriever, feishu=feishu, table_id=table_id)


#进程级单例 启动时装配一次
AGENT = build_agent()
STORE = SessionStore()
#对话限流 按会话计数
CHAT_LIMITER = RateLimiter(limit=30, window=60)
#OCR 限流 窗口更紧
OCR_LIMITER = RateLimiter(limit=10, window=60)
#认证单例 未配置则一律拒绝
AUTH = build_auth()


#发票 OCR 装配与图片校验
_OCR_CFG = get_ocr_config()
OCR_MAX_BYTES = int(_OCR_CFG["max_image_mb"]) * 1024 * 1024
_OCR_MAX_MB = int(_OCR_CFG["max_image_mb"])
ALLOWED_IMAGE_MIMES = {"image/jpeg", "image/png", "image/webp", "image/gif"}


def build_ocr_engine():
    #装配视觉引擎 未配置返回 None
    key = _OCR_CFG["api_key"]
    if not key or "你的密钥" in key or len(key) < 20:
        return None
    try:
        return InvoiceOCREngine(
            base_url=_OCR_CFG["base_url"], api_key=key,
            model=_OCR_CFG["model"], detail=_OCR_CFG["detail"],
        )
    except ImportError:
        print("[M9] 未安装 openai，OCR 引擎不可用（/ocr 将返回 503）。")
        return None


OCR_ENGINE = build_ocr_engine()

#魔数白名单 不信任扩展名
_IMAGE_MAGIC = (
    (b"\xff\xd8\xff", "image/jpeg"),
    (b"\x89PNG\r\n\x1a\n", "image/png"),
    (b"GIF87a", "image/gif"),
    (b"GIF89a", "image/gif"),
    (b"RIFF", "image/webp"),  #还要看 WEBP 标识
)


def _strip_data_url(b64: str) -> str:
    #去掉 data URL 前缀
    if b64.startswith("data:"):
        return b64.partition(",")[2].strip()
    return b64.strip()


def _decode_image(image_b64: str, max_bytes: int) -> tuple[bytes, str] | tuple[None, None]:
    #解码图片 非法就返回空
    try:
        raw = base64.b64decode(_strip_data_url(image_b64), validate=True)
    except Exception:
        return None, None
    if not raw or len(raw) > max_bytes:
        return None, None
    for magic, mime in _IMAGE_MAGIC:
        if raw.startswith(magic):
            if mime == "image/webp" and raw[8:12] != b"WEBP":
                continue
            return raw, mime
    return None, None
app = FastAPI(title="小财奴 API 网关", version="1.2.0")

#前端单页路径 供 GET / 托管
_FRONTEND_PATH = os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "frontend", "index.html"
)

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_methods=["GET", "POST", "OPTIONS"],
    allow_headers=["Content-Type", "Authorization"],
)


#前端单页托管
@app.get("/")
def index():
    #返回前端页 文件缺失时给提示
    if os.path.exists(_FRONTEND_PATH):
        return FileResponse(_FRONTEND_PATH, media_type="text/html")
    return HTMLResponse("<h2>小财奴 API 网关</h2><p>前端文件 frontend/index.html 未找到。</p>")


#免认证端点白名单
_PUBLIC_ENDPOINTS = {("GET", "/health"), ("POST", "/login")}
_AUTH_USER_KEYS = ("username", "name", "role", "employee_id")


def _bearer(token: str) -> str:
    #去掉 Bearer 前缀
    t = token or ""
    if t.lower().startswith("bearer "):
        t = t[7:].strip()
    return t


def dispatch(method: str, path: str, body: Dict[str, Any], agent: XiaoCaiAgent,
             store: SessionStore, auth: AuthService | None = None,
             token: str = "", client_ip: str = "",
             ocr_engine=None) -> Tuple[int, Dict[str, Any]]:
    #按方法和路径分发处理
    if method == "GET" and path == "/health":
        return 200, {"status": "ok"}

    #认证墙 白名单外都要登录
    auth_ctx = None
    if (method, path) not in _PUBLIC_ENDPOINTS:
        if auth is None:
            return 401, {"error": "auth required (server auth not configured)"}
        auth_ctx = auth.verify(_bearer(token))
        if auth_ctx is None:
            return 401, {"error": "未登录或登录已过期，请重新登录"}

    if method == "POST" and path == "/login":
        if auth is None:
            return 503, {"error": "auth not configured (set FEISHU_USER_TABLE_ID in .env)"}
        username = sanitize_text(body.get("username") or "", max_length=32)
        password = body.get("password") or ""  #口令不净化 只限长度
        if not username or not password or len(password) > 64:
            return 400, {"error": "username and password required"}
        return auth.login(username, password, client_ip=client_ip)

    if method == "GET" and path == "/me":
        return 200, {"user": {k: auth_ctx.get(k, "") for k in _AUTH_USER_KEYS}}

    if method == "POST" and path == "/logout":
        auth.logout(_bearer(token))
        return 200, {"ok": True}

    if method == "POST" and path == "/chat":
        sid = (body.get("session_id") or "default")[:64]
        #会话绑定用户 换用户即拒绝
        ok, err = auth.bind_sid(sid, auth_ctx.get("username", ""))
        if not ok:
            return 403, {"error": err}
        #净化文本 截断超长
        text = sanitize_text(body.get("text") or "", max_length=2000)
        if not text:
            return 400, {"error": "text required"}
        #命中注入特征直接拒绝
        is_inj, matched = check_injection(text)
        if is_inj:
            return 400, {"error": "blocked: potential prompt injection", "matched": matched}
        #单会话滑动窗口限流
        if not CHAT_LIMITER.allow(sid):
            return 429, {"error": "rate limit exceeded, please try later"}
        #可选发票图与 OCR 字段
        image_b64 = str(body.get("image_b64") or "")
        image_mime = str(body.get("image_mime") or "image/jpeg")
        if image_b64:
            raw, real_mime = _decode_image(image_b64, OCR_MAX_BYTES)
            if raw is None:
                return 400, {"error": "invalid image (bad base64 / too large / unsupported type)"}
            image_mime = real_mime
            image_b64 = base64.b64encode(raw).decode()  #归一为裸 base64
        invoice_fields = None
        if body.get("invoice_no") or body.get("invoice_seller"):
            invoice_fields = {"invoice_no": str(body.get("invoice_no") or "")[:64],
                              "invoice_seller": str(body.get("invoice_seller") or "")[:64]}
        #业务处理
        history = store.get_messages(sid)
        base_draft = store.get_draft(sid)
        #登录身份传给 agent
        user_ctx = {k: auth_ctx.get(k, "") for k in _AUTH_USER_KEYS} if auth_ctx else None
        res = agent.route(text, history=history, base_draft=base_draft, user=user_ctx,
                          image_b64=image_b64, image_mime=image_mime,
                          invoice_fields=invoice_fields)
        answer = res.get("answer", "")
        store.append(sid, "user", text)
        store.append(sid, "assistant", answer)
        if res.get("intent") == "B":
            store.set_draft(sid, res.get("draft"))
        return 200, res

    if method == "POST" and path == "/ocr":
        #OCR 先校验图片再限流
        engine = ocr_engine if ocr_engine is not None else OCR_ENGINE
        if engine is None:
            return 503, {"error": "OCR not configured (set DEEPSEEK_API_KEY in .env)"}
        sid = (body.get("session_id") or "default")[:64]
        ok, err = auth.bind_sid(sid, auth_ctx.get("username", ""))
        if not ok:
            return 403, {"error": err}
        if not OCR_LIMITER.allow(f"ocr:{sid}"):
            return 429, {"error": "rate limit exceeded, please try later"}
        raw, mime = _decode_image(str(body.get("image_b64") or ""), OCR_MAX_BYTES)
        if raw is None:
            return 400, {"error": (f"invalid image (bad base64 / >{_OCR_MAX_MB}MB / "
                                   "unsupported type; jpeg/png/gif/webp only)")}
        filename = sanitize_text(body.get("filename") or "invoice", max_length=64) or "invoice"
        try:
            result = engine.extract(
                image_b64=base64.b64encode(raw).decode(),
                content_type=mime, filename=filename,
            )
        except OCRError as e:
            return 502, {"error": f"OCR failed: {e}"}
        fields = dict(result.fields)
        return 200, {
            "ok": True,
            "fields": fields,
            "warnings": result.warnings,
            "preview": build_invoice_preview(fields),
            "invoice_info": result.invoice_info,
            "mime": mime,
            "usage": result.usage,
            "model": result.model,
            "provider": result.provider,
            "latency_ms": result.latency_ms,
        }

    if method == "POST" and path == "/reimburse":
        #单据统一走主服务器写入
        username = auth_ctx.get("username", "")
        applicant = auth_ctx.get("name") or username or body.get("applicant", "")
        department = body.get("department") or auth_ctx.get("department") or ""
        if backend_client.enabled():
            try:
                data = backend_client.submit_as(
                    username,
                    department=department,
                    rtype=body.get("rtype", ""),
                    amount=body.get("amount", 0),
                    reason=body.get("reason", ""),
                    has_invoice=body.get("has_invoice", ""),
                    city=body.get("city", ""),
                    date_str=body.get("date_str", ""),
                )
                return 200, {
                    "record_id": data.get("record_id") or "",
                    "bill_no": data.get("bill_no") or "",
                    "status": "待审核",
                    "via": "main-server",
                    "message": data.get("message") or "",
                }
            except backend_client.BackendError as e:
                if backend_client.mode() == "required":
                    return 503, {"error": f"主服务器不可用，单据未提交：{e}"}
                print(f"[backend] /reimburse 未走主服务器，回落直连飞书：{e}")

        if not agent.feishu:
            return 503, {"error": "feishu not connected (set FEISHU_APP_ID in .env)"}
        f = body
        #申请人工号以登录身份为准
        try:
            rec = build_reimbursement_record(
                applicant=applicant,
                department=department,
                rtype=f.get("rtype", ""),
                amount=f.get("amount", 0),
                reason=f.get("reason", ""),
                has_invoice=f.get("has_invoice", ""),
                city=f.get("city", ""),
                date_str=f.get("date_str", ""),
                compliance=f.get("compliance", "通过"),
                risk_summary=f.get("risk_summary", ""),
                status=f.get("status", "待审核"),
                employee_id=auth_ctx.get("employee_id", ""),
            )
            rec = agent._fit_schema(rec)  #旧表无工号列时自动剔除
            ids = agent.feishu.create_records(agent.table_id, [rec])
            rid = ids[0] if ids else None
            bill_no = agent._fetch_bill_no(rid) if rid else ""
            return 200, {"record_id": rid, "bill_no": bill_no, "status": "待审核"}
        except Exception as e:  #返回飞书错误信息
            return 500, {"error": str(e)}

    if method == "POST" and path == "/progress":
        applicant = (body.get("applicant") or "").strip()
        bill_no = (body.get("bill_no") or "").strip()
        #员工只能查自己的进度
        if auth_ctx.get("role") == ROLE_EMPLOYEE:
            applicant = (auth_ctx.get("name") or auth_ctx.get("username") or "").strip()
            bill_no = ""
        if bill_no:
            q = f"查 {bill_no}"
        elif applicant:
            q = f"查一下{applicant}的报销"
        else:
            return 400, {"error": "applicant or bill_no required"}
        res = agent.route(q)
        return 200, res

    return 404, {"error": "not found", "path": path}


#HTTP 端点 包装 dispatch
def _client_ip(request: Request) -> str:
    #取客户端 IP
    return request.client.host if (request and request.client) else ""


@app.get("/health")
def health():
    #健康检查 免登录
    code, p = dispatch("GET", "/health", {}, AGENT, STORE)
    if code != 200:
        raise HTTPException(status_code=code, detail=p)
    return p


@app.post("/login")
def login(req: LoginRequest, request: Request):
    #账号口令登录 发 token
    code, p = dispatch("POST", "/login", req.model_dump(), AGENT, STORE,
                       auth=AUTH, client_ip=_client_ip(request))
    if code != 200:
        raise HTTPException(status_code=code, detail=p)
    return p


@app.get("/me")
def me(authorization: str = Header(default="")):
    #查当前登录用户
    code, p = dispatch("GET", "/me", {}, AGENT, STORE, auth=AUTH, token=authorization)
    if code != 200:
        raise HTTPException(status_code=code, detail=p)
    return p


@app.post("/logout")
def logout(request: Request, authorization: str = Header(default="")):
    #登出并拉黑 token
    code, p = dispatch("POST", "/logout", {}, AGENT, STORE,
                       auth=AUTH, token=authorization, client_ip=_client_ip(request))
    if code != 200:
        raise HTTPException(status_code=code, detail=p)
    return p


@app.post("/chat")
def chat(req: ChatRequest, request: Request, authorization: str = Header(default="")):
    #对话主入口
    code, p = dispatch("POST", "/chat", req.model_dump(), AGENT, STORE,
                       auth=AUTH, token=authorization, client_ip=_client_ip(request))
    if code != 200:
        raise HTTPException(status_code=code, detail=p)
    return p


@app.post("/ocr")
def ocr(req: OcrRequest, request: Request, authorization: str = Header(default="")):
    #OCR 识别发票字段
    code, p = dispatch("POST", "/ocr", req.model_dump(), AGENT, STORE,
                       auth=AUTH, token=authorization, client_ip=_client_ip(request))
    if code != 200:
        raise HTTPException(status_code=code, detail=p)
    return p


@app.post("/reimburse")
def reimburse(req: ReimburseRequest, request: Request, authorization: str = Header(default="")):
    #结构化填单直提
    code, p = dispatch("POST", "/reimburse", req.model_dump(), AGENT, STORE,
                       auth=AUTH, token=authorization, client_ip=_client_ip(request))
    if code != 200:
        raise HTTPException(status_code=code, detail=p)
    return p


@app.post("/progress")
def progress(req: ProgressRequest, request: Request, authorization: str = Header(default="")):
    #查询报销进度
    code, p = dispatch("POST", "/progress", req.model_dump(), AGENT, STORE,
                       auth=AUTH, token=authorization, client_ip=_client_ip(request))
    if code != 200:
        raise HTTPException(status_code=code, detail=p)
    return p


#启动入口
def run(host: str = "0.0.0.0", port: int = 8000) -> None:
    #起网关 默认 0.0.0.0:8000
    import uvicorn
    print(f"[M5] API 网关启动 http://{host}:{port}  (Ctrl+C 停止)")
    uvicorn.run(app, host=host, port=port)


if __name__ == "__main__":
    import argparse

    ap = argparse.ArgumentParser(description="小财奴 M5 API 网关 (FastAPI)")
    ap.add_argument("host", nargs="?", default="0.0.0.0")
    ap.add_argument("port", nargs="?", type=int, default=8000)
    args = ap.parse_args()
    run(args.host, args.port)
