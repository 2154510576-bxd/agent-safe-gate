#编排路由层 检索加生成 分支路由
from __future__ import annotations

import os
import random
import re
import time
from datetime import datetime

from .retriever import Retriever, format_context
from .config import get_provider_config, knowledge_dir
from .llm import LLMAdapter
from .feishu import (
    FeishuBitableClient,
    FeishuError,
    build_reimbursement_record,
    filter_by_schema,
    public_view,
    ALLOWED_TYPES,
    DEPT_WHITELIST,
    CITY_WHITELIST,
    CITY_TIER,
    validate_enum_fields,
)
from .users import ROLE_EMPLOYEE, ROLE_FINANCE, ROLE_ADMIN

SYSTEM_CONSULT = (
    "你是小财，一个企业财务报销助手。只能依据下方[资料]回答用户问题；"
    "若资料中没有相关信息，如实说明「资料中未提及」，不要编造或猜测。"
    "回答简洁、用中文、面向普通员工。"
    "严禁输出「已记录/预审通过/提交到飞书/确认后帮您提交」等系统操作话术，"
    "也不要复述资料里的示例对话；你只负责解答报销制度疑问。"
)

#备用 LLM 抽取提示词 默认不启用
FILL_EXTRACT_SYSTEM_PROMPT = (
    "你是报销信息的结构化抽取引擎，只输出一个 JSON 对象（字段：applicant/department/"
    "category/amount/reason/has_invoice/city/date），category 仅取差旅费/招待费/办公用品/"
    "交通费，has_invoice 仅取有票/无票/未提，date 格式 YYYY/M/D。"
)

#意图识别 命中关键词分支 未命中兜底 A
INTENT_RULES = [
    ("D", ["进度", "到哪", "查一下", "我的报销单", "审批没", "到账", "现在什么状态", "查询", "查"]),
    ("B", ["填", "提交", "上传", "创建", "新建", "开报销单", "怎么报", "报一笔",
           "帮我报", "我要报销", "我要报", "报销单"]),
    ("C", ["合规", "预审", "能不能报", "有没有风险", "超标", "能不能过"]),
]

#城市白名单 抽取与分级共用
CITY_KEYWORDS = CITY_WHITELIST

#部门简称映射 不确定的留空追问
DEPT_ALIASES = {
    "研发": "研发部", "研发部": "研发部",
    "技术": "信息技术部", "信息技术": "信息技术部", "信息": "信息技术部", "it": "信息技术部",
    "人力": "人力资源部", "人力资源": "人力资源部", "hr": "人力资源部",
    "行政": "行政部", "财务": "财务部", "市场": "市场部", "销售": "销售部",
    "生产": "生产部", "采购": "采购部", "质管": "质量管理部", "质量": "质量管理部",
    "客服": "客户服务部", "客户": "客户服务部",
}


def normalize_department(raw: str) -> str:
    #部门名归一为标准枚举
    if not raw:
        return ""
    if raw in DEPT_WHITELIST:
        return raw
    core = raw.replace("部门", "").replace("部", "").strip().lower()
    return DEPT_ALIASES.get(core, "")

#报销类型模糊映射 招待费优先
CATEGORY_MAP = [
    ("招待费", ["招待", "请客", "客户吃饭", "吃饭", "用餐", "宴请", "业务招待"]),
    ("差旅费", ["差旅", "出差", "住酒店", "住宿", "高铁", "车票", "机票", "火车"]),
    ("办公用品", ["办公用品", "办公", "采购", "文具", "耗材", "低值"]),
    ("交通费", ["交通费", "打车", "滴滴", "加油", "油费", "过路费", "市内交通"]),
]

#专项流程类别 不直交须引导走专项
SPECIAL_CATEGORY_MAP = [
    ("团建/员工活动", ["团建", "员工活动", "活动费"]),
    ("通讯费", ["通讯费", "话费", "电话费"]),
    ("培训费", ["培训", "职业发展"]),
    ("市场推广费", ["推广", "宣传", "市场推广"]),
    ("加班餐补", ["加班餐", "误餐", "餐补"]),
    ("私车公用", ["私车公用", "公里补贴", "里程"]),
]

#政策问句标记 命中放行到 A
_A_QUESTION_MARKERS = ("吗", "呢", "？", "?", "多少", "什么", "标准", "规定", "介绍",
                      "说明", "可以", "能否", "是否", "为什么", "有啥")

#填单引导口令 直接输出引导不走抽取
FILL_GUIDE_TRIGGERS = {"填单引导", "填单指南"}

#引导开场收尾话术素材 随机切换
_FILL_GUIDE_OPENERS = (
    "来啦来啦~ 填单引导这就安排上 (๑•̀ㅂ•́)و✧",
    "别急别急，小财奴手把手教您填 ~(＾▽＾)~",
    "好嘞好嘞~ 其实填报销单超简单，听我细细道来 ✨",
    "收到！先花 30 秒看完这段，填单不迷路 (￣▽￣)／",
)
_FILL_GUIDE_CLOSERS = (
    "一次性说完或者分开说都行，我帮您记着呢~ ✨",
    "想到哪说到哪，剩下的交给我帮您补齐 (´▽`ʃ♡ƪ)",
    "随时补充，我随时候着~ 加油，报销人！💪",
    "看懂了吗？没看懂再问我一遍也好呀 (๑´ㅂ`๑)",
)
#报销类型词 命中视为填单续答
_TYPE_WORDS = ("差旅费", "招待费", "办公费", "交通费", "办公用品", "报销")
#姓名排除词 常见口语与代词
_SKIP_NAME = {"好的", "谢谢", "没有", "麻烦", "请问", "帮我", "这个", "那个",
              "我们", "你们", "他们", "她们", "自己", "大家", "没事", "在哪", "这里"}
#非人名字符 含则排除
_NON_NAME_CHARS = set(
    "票费电发开报填审请查购采差出住餐吃车机油路话训推广活动团私司单金额类型部门"
    "理由事因为用于您我他她们这那没了的是不也都还再就才已有和与否如果比较标准规定"
    "介绍说明说么怎帮想要去回在哪能可该应或等及与"
)

#中文数字 日期月份用
_CN_NUM = {
    "零": 0, "一": 1, "二": 2, "两": 2, "三": 3, "四": 4, "五": 5, "六": 6, "七": 7,
    "八": 8, "九": 9, "十": 10, "〇": 0,
}

#进度查询的伪姓名词
_D_NAME_BLOCKLIST = {"进度", "报销", "记录", "单号", "状态", "结果", "详情", "信息"}
_D_PRONOUN_CHARS = set("我你他她它谁咱")


def _cn_num_token(tok: str):
    #中文或阿拉伯数字转 int
    tok = tok.strip()
    if not tok:
        return None
    if tok.isdigit():
        return int(tok)
    if "十" in tok:
        if tok == "十":
            return 10
        if tok.startswith("十"):
            return 10 + (_cn_num_token(tok[1:]) or 0)
        if tok.endswith("十"):
            return (_cn_num_token(tok[:-1]) or 1) * 10
        a, b = tok.split("十", 1)
        return (_cn_num_token(a) or 1) * 10 + (_cn_num_token(b) or 0)
    val = 0
    for ch in tok:
        d = _CN_NUM.get(ch)
        if d is None:
            return None
        val = val * 10 + d
    return val if val > 0 else None


def _normalize_date(text: str) -> str:
    #日期归一为 YYYY/M/D
    if not text:
        return ""
    #阿拉伯日期格式
    m = re.search(r"(\d{4})[/\-.](\d{1,2})[/\-.](\d{1,2})", text)
    if m:
        return f"{m.group(1)}/{int(m.group(2))}/{int(m.group(3))}"
    #中文日期带年
    m = re.search(r"(\d{4})年\s*([0-9一-龥]{1,3})月\s*([0-9一-龥]{1,3})[日号]?", text)
    if m:
        mo = _cn_num_token(m.group(2))
        da = _cn_num_token(m.group(3))
        if mo and da:
            return f"{m.group(1)}/{mo}/{da}"
    #中文日期不带年
    m = re.search(r"([0-9一-龥]{1,3})月\s*([0-9一-龥]{1,3})[日号]", text)
    if m:
        mo = _cn_num_token(m.group(1))
        da = _cn_num_token(m.group(2))
        if mo and da:
            return f"{datetime.now().year}/{mo}/{da}"
    return ""


class XiaoCaiAgent:
    #检索加生成加分支路由的统一编排

    def __init__(
        self,
        llm: LLMAdapter,
        retriever: Retriever,
        compressor=None,
        top_k: int = 3,
        default_category: str | None = "报销制度",
        feishu=None,
        table_id: str | None = None,
    ) -> None:
        #保存依赖与配置
        self.llm = llm
        self.retriever = retriever
        self.compressor = compressor
        self.top_k = top_k
        self.default_category = default_category
        self.feishu = feishu          #飞书客户端 可插拔 未接入则降级
        self.table_id = table_id      #报销单主表

    #意图识别 关键词命中分支
    def classify_intent(self, text: str) -> str:
        t = text or ""
        #进度查询 优先级最高
        if any(k in t for k in ("进度", "到哪", "查一下", "我的报销单", "审批没",
                                "到账", "现在什么状态", "查询", "查")):
            return "D"
        #合规预审
        if any(k in t for k in ("合规", "预审", "能不能报", "有没有风险", "超标", "能不能过")):
            return "C"
        #政策问句优先判 A 咨询
        if any(q in t for q in _A_QUESTION_MARKERS):
            return "A"
        #填单意图 含报销动作类型或票据
        if any(k in t for k in ("填", "提交", "上传", "创建", "新建", "开报销单", "怎么报",
                                "报一笔", "帮我报", "我要报销", "我要报", "我想报",
                                "报销单", "报销", "发票")):
            return "B"
        #续答含报销类型词按填单处理
        if any(k in t for k in _TYPE_WORDS):
            return "B"
        return "A"

    #路由辅助 上下文粘性
    @staticmethod
    def _has_strong_intent(text: str) -> bool:
        #强意图 要离开填单
        t = text or ""
        return any(k in t for k in ("进度", "到哪", "查一下", "我的报销单", "审批没",
                                    "到账", "现在什么状态", "查询", "查",
                                    "合规", "预审", "能不能报", "有没有风险", "超标", "能不能过"))

    @staticmethod
    def _is_explicit_question(text: str) -> bool:
        #政策问句应放行到 A
        t = text or ""
        return any(q in t for q in _A_QUESTION_MARKERS)

    @classmethod
    def _progress_followup_keyword(cls, question: str) -> str:
        #续答关键词 单号或姓名
        t = (question or "").strip()
        #单号格式
        m = re.search(r"(BX-?\s*\d[\d-]*)", t, re.IGNORECASE)
        if m:
            return m.group(1).replace(" ", "").upper()
        core = re.sub(r"[的了吧呀哦呢]+$", "", t)
        core = re.sub(r"^(我是|我叫|我的名字是)", "", core)
        if not re.fullmatch(r"[一-龥]{2,4}", core):
            return ""
        if core in _SKIP_NAME or core in _D_NAME_BLOCKLIST:
            return ""
        if any(ch in _D_PRONOUN_CHARS for ch in core):
            return ""
        if any(ch in _NON_NAME_CHARS for ch in core):
            return ""
        return core

    @classmethod
    def _is_progress_name_followup(cls, question: str, history) -> bool:
        #上一轮要姓名 本轮回姓名或单号
        last_assistant = ""
        for msg in reversed(list(history or [])):
            if isinstance(msg, dict) and msg.get("role") == "assistant":
                last_assistant = str(msg.get("content", ""))
                break
        if "请提供姓名或报销单号" not in last_assistant:
            return False
        return bool(cls._progress_followup_keyword(question))

    @staticmethod
    def _is_fill_in_progress(draft) -> bool:
        #草稿是否仍在进行中
        if not isinstance(draft, dict):
            return False
        missing_core = not (draft.get("applicant") and draft.get("department")
                           and draft.get("category") and draft.get("amount"))
        blocked = draft.get("has_invoice") == "无票"
        return bool(missing_core or blocked)

    #统一入口 按意图分发
    def route(self, question: str, history=None, base_draft=None, user: dict | None = None,
              image_b64: str = "", image_mime: str = "image/jpeg",
              invoice_fields: dict | None = None) -> dict:
        #按意图路由到对应分支
        if (question or "").strip() in FILL_GUIDE_TRIGGERS:
            return self._handle_fill_guide()
        intent = self.classify_intent(question)
        #填单中继续保持 B 分支
        _sticky = self._is_fill_in_progress(base_draft) or (isinstance(base_draft, dict) and base_draft.get("await_confirm"))
        if intent != "B" and base_draft and _sticky:
            if not self._has_strong_intent(question) and not self._is_explicit_question(question):
                return self._handle_fill(question, base_draft=base_draft, user=user,
                                         image_b64=image_b64, image_mime=image_mime,
                                         invoice_fields=invoice_fields)
        #D 续答 继续走进度查询
        if intent == "A" and self._is_progress_name_followup(question, history):
            return self._handle_progress(question, user=user)
        if intent == "A":
            return self._handle_consult(question, history=history)
        if intent == "B":
            return self._handle_fill(question, base_draft=base_draft, user=user,
                                     image_b64=image_b64, image_mime=image_mime,
                                     invoice_fields=invoice_fields)
        if intent == "C":
            #合规预审仅财务和管理员
            if user and user.get("role") not in (ROLE_FINANCE, ROLE_ADMIN):
                return {
                    "intent": "C",
                    "answer": "🛡️ 合规预审仅对财务/管理员开放哦~ "
                              "您可以把单子提交上来等财务预审，有疑问也可以直接问我报销制度。",
                    "compliance": "", "risk_summary": "", "record_id": None,
                }
            return self._handle_compliance(question, base_draft=base_draft, user=user)
        return self._handle_progress(question, user=user)

    #A 咨询分支
    def _handle_consult(self, question: str, history=None) -> dict:
        #检索资料后交给 LLM 作答
        chunks = self.retriever.query(
            question, top_k=self.top_k, categories=self.default_category
        )
        ctx = format_context(chunks, compress=self.compressor, query=question)
        user = f"问题：{question}\n\n[资料]\n{ctx}"
        result = self.llm.chat(user, system=SYSTEM_CONSULT, history=history)
        return {
            "intent": "A",
            "answer": result.text,
            "chunks": chunks,
            "model": result.model,
        }

    #本地规则抽取字段 三类分支共用
    @staticmethod
    def _extract_fields(text: str) -> dict:
        t = text or ""
        result = {
            "applicant": "", "department": "", "category": "", "amount": 0.0,
            "reason": "", "has_invoice": "", "city": "", "date": "", "special": None,
        }

        #金额
        m = re.search(r"(\d+(?:\.\d+)?)\s*(?:元|块|￥|¥|rmb|RMB)?", t)
        if m:
            try:
                result["amount"] = float(m.group(1))
            except ValueError:
                result["amount"] = 0.0

        #票据状态 先判无票
        if re.search(r"没发票|无发票|没票|丢了|没有发票|没开|未开|没开发票", t):
            result["has_invoice"] = "无票"
        elif re.search(r"有票|有发票|电子发票|已开|开票了|开了票|发票有|发票都留着|发票在|带着发票", t):
            result["has_invoice"] = "有票"
        else:
            result["has_invoice"] = "未提"

        #先判专项流程类别
        for name, kws in SPECIAL_CATEGORY_MAP:
            if any(k in t for k in kws):
                result["special"] = name
                break

        #报销类型 先精确字面再模糊映射
        if not result["special"]:
            for cat in ALLOWED_TYPES:
                if cat in t:
                    result["category"] = cat
                    break
            if not result["category"]:
                for cat, kws in CATEGORY_MAP:
                    if any(k in t for k in kws):
                        result["category"] = cat
                        break

        #城市仅在白名单内匹配
        for c in CITY_KEYWORDS:
            if c in t:
                result["city"] = c
                break

        #部门抽取并归一为标准枚举
        m = re.search(r"([一-龥]{1,6}部门)", t)
        if not m:
            m = re.search(r"部门([一-龥]{1,6})", t)
        if not m:
            m = re.search(r"([一-龥]{2,6}部)(?=[，,\s。的]|$)", t)
        if m:
            result["department"] = normalize_department(m.group(1))

        #申请人 我是或部门或查询等句式
        m = re.search(r"我是\s*([一-龥]{2,4})", t)
        if not m:
            m = re.search(r"([一-龥]{2,4})\s*[,，]?\s*(?:研发部|市场部|传媒部门|电脑部门|部门)", t)
        if not m:
            m = re.search(r"查一下\s*([一-龥]{2,3})", t) or re.search(r"查\s*([一-龥]{2,3})的?报销", t)
        if m:
            result["applicant"] = m.group(1)
        else:
            #兜底姓名 只取 2 到 3 个净汉字
            for tk in re.findall(r"[一-龥]{2,3}", t):
                if tk in _SKIP_NAME:
                    continue
                if any(ch in _NON_NAME_CHARS for ch in tk):
                    continue
                result["applicant"] = tk
                break

        #事由 事由或用于或因为之后
        m = re.search(r"(?:事由|用于|因为)[：: ]?([一-龥0-9，,。]{2,30})", t)
        if m:
            result["reason"] = m.group(1).strip(" ，,。")
        else:
            #动作词后的描述须含两个实字
            m = re.search(r"(?:出差|请客|采购|打车)([一-龥0-9，,。 ]{2,20})", t)
            if m:
                tail = m.group(1).strip(" ，,。")
                if len(re.sub(r"[费元\s，,。0-9]", "", tail)) >= 2:
                    result["reason"] = m.group(0).strip(" ，,。")

        #日期
        result["date"] = _normalize_date(t)
        return result

    #合规预审核心规则
    @staticmethod
    def _city_tier(city: str):
        #住宿分级上限 元每晚
        if not city:
            return None
        return CITY_TIER.get(city, 250)  #非白名单按三线 250

    def _compliance_check(self, f: dict):
        #产出合规结果与风险摘要
        risks: list = []
        cat = f.get("category", "")
        amount = f.get("amount", 0) or 0
        has_invoice = f.get("has_invoice", "")
        date_str = f.get("date", "")
        city = f.get("city", "")

        #无票阻断 未提仅提示
        if has_invoice == "无票":
            return ("阻断", "票据状态为「无票」，缺少合规发票，提交前无法放行")
        if has_invoice == "未提":
            risks.append("票据状态未提，提交前需确认合规发票")

        #额度校验
        if cat == "招待费" and amount > 2000:
            risks.append("单次招待费超2000元，需总经理事前审批")
        if cat == "办公用品" and amount > 5000:
            risks.append("超5000元，走采购流程，不适用个人报销")
        if cat == "差旅费":
            tier = self._city_tier(city)
            if tier is not None:
                if amount > tier:
                    risks.append(f"{city}属住宿分级上限{tier}元/晚，超标需事前审批")
            else:
                if amount > 3000:
                    risks.append("差旅费金额较大，请确认住宿城市与晚数是否符合分级标准")

        #时限 距开票超 30 天
        if date_str:
            try:
                y, mo, d = (int(x) for x in date_str.split("/"))
                dt = datetime(y, mo, d)
                if (datetime.now() - dt).days > 30:
                    risks.append("距开票超30天，需附情况说明")
            except Exception:
                pass

        if not risks:
            return ("通过", "无")
        return ("风险", "；".join(risks))

    #取回可读单号
    def _fit_schema(self, rec: dict) -> dict:
        #按真实表字段过滤记录
        fn = getattr(self.feishu, "field_names", None)
        if fn is None:
            return rec
        try:
            names = fn(self.table_id)
        except FeishuError:
            return rec
        return filter_by_schema(rec, names)

    def _fetch_bill_no(self, rid: str) -> str:
        #取回飞书自动编号单号
        if not (self.feishu and rid):
            return ""
        for attempt in range(2):
            try:
                rec = self.feishu.get_record(self.table_id, rid)
                no = rec.get("fields", {}).get("单号", "")
                if no:
                    return no
            except Exception:
                pass
            if attempt == 0:
                time.sleep(0.4)  #首查为空则短延迟重试一次
        return ""

    #识别确认提交意图
    @staticmethod
    def _is_confirm(text: str) -> bool:
        #否定词优先拦截 避免误触发
        t = text or ""
        if any(w in t for w in ("不提交", "不确认", "不要提交", "不用提交", "暂不", "先不", "别提交")):
            return False
        return any(k in t for k in ("确认提交", "确认", "确定提交", "确定", "提交吧",
                                    "确认吧", "帮我提交", "没问题", "好的提交",
                                    "可以提交", "ok", "OK", "提交"))

    #票据状态主要提醒
    @staticmethod
    def _invoice_reminder(has_invoice: str) -> str:
        #按票据状态给礼貌提醒
        if has_invoice == "有票":
            return "🧾 您已带发票，这会加快审核进度，财务小伙伴能更快处理~"
        if has_invoice == "无票":
            return "🧾 暂未带发票，无发票需要更多核验时间，建议您尽快补开发票哦~"
        #未提时引导补充票据状态
        return "🧾 温馨提示：有发票会加快审核进度，无发票需要更多核验时间，建议补充一下票据状态~"

    #非必填的软提示
    @staticmethod
    def _soft_hints(f: dict) -> list:
        #返回可选的补充建议
        hints = []
        if not f.get("reason"):
            hints.append("💡 补充具体「事由」可以提高审核通过率哦~")
        #差旅费缺城市时提醒
        if f.get("category") == "差旅费" and not f.get("city"):
            hints.append("💡 补充消费「城市」可以加快审核速度~")
        return hints

    #填单追问与提交
    def _ask_missing(self, f: dict, missing: list) -> str:
        #列出已填与待补字段
        lines = ["您好呀~ 我是小财奴，帮您梳理了一下报销信息 📝"]
        filled = []
        if f["applicant"]:
            filled.append(f"👤 申请人：{f['applicant']}")
        if f["department"]:
            filled.append(f"🏢 部门：{f['department']}")
        if f["category"]:
            filled.append(f"💼 报销类型：{f['category']}")
        if f["amount"]:
            filled.append(f"💰 金额：¥{f['amount']:.0f}")
        if f["has_invoice"] and f["has_invoice"] != "未提":
            filled.append(f"🧾 票据：{f['has_invoice']}")
        if filled:
            lines.append("已记录：" + " · ".join(filled))
        need = []
        if "申请人" in missing:
            need.append("👤 申请人（您怎么称呼呀？）")
        if "部门" in missing:
            need.append("🏢 部门（您属于哪个部门呀？）")
        if "报销类型" in missing:
            need.append("💼 报销类型（差旅/招待/办公用品/交通费？）")
        if "金额" in missing:
            need.append("💰 金额（花了多少呀？）")
        lines.append("✏️ 还需要补齐这几项哦：")
        lines.extend(need)
        #票据状态提醒
        lines.append("")
        lines.append(self._invoice_reminder(f["has_invoice"] or "未提"))
        #顺带给出软提示
        hints = self._soft_hints(f)
        if hints:
            lines.append("")
            lines.append("另外，补充以下信息能更快通过审核：")
            lines.extend(hints)
        lines.append("补齐后我会请您核对信息，确认无误再上传飞书~")
        return "\n".join(lines)

    #确认前回显摘要
    def _ask_confirm(self, f: dict, has_invoice: str) -> str:
        #回显已填信息待用户确认
        lines = ["✨ 信息已收集齐全，麻烦您核对一下~"]
        rows = [
            f"👤 申请人：{f['applicant']}",
            f"🏢 部门：{f['department']}",
            f"💼 报销类型：{f['category']}",
            f"💰 金额：¥{f['amount']:.0f}",
        ]
        if f.get("city"):
            rows.append(f"📍 城市：{f['city']}")
        if f.get("reason"):
            rows.append(f"📝 事由：{f['reason']}")
        rows.append(f"🧾 票据：{has_invoice}")
        lines.append("📋 当前已填信息：")
        lines.extend(rows)
        #事由备注提示
        lines.append("📝 备注：报销的主要事情或用途，请通过「事由」补充告诉我，方便财务审核归类~")
        #票据状态提醒
        lines.append(self._invoice_reminder(has_invoice))
        #城市日期等软提醒
        soft = [h for h in self._soft_hints(f) if "事由" not in h]
        if soft:
            lines.append("")
            lines.append("💡 还有这些可补充（非必填，补了更快通过）：")
            lines.extend(soft)
        lines.append("")
        lines.append("✅ 请确认信息，回复「确认提交」即可开始上传飞书~")
        return "\n".join(lines)

    #无票软确认 给二选一
    def _ask_no_invoice_confirm(self, f: dict) -> str:
        #无票坚持提交时软确认不硬拦
        lines = [
            "🧾 温馨提示：这一单您说的是「无票」哦~",
            "按《报销制度》，没有合规发票的报销，财务是无法正常审核通过的。",
            "",
            "您可以选择：",
            "🧾 回复「有发票了」—— 补上票据状态，我帮您正常提交；",
            "🚀 回复「仍要提交」—— 我按您的决定提交给财务，"
            "并标注「无票提交，财务将驳回」。",
            "",
            "（也可以继续补充或修改其他报销信息~）",
        ]
        return "\n".join(lines)

    #跨轮草稿合并
    @staticmethod
    def _merge_draft(base: dict, new: dict) -> dict:
        #非空值覆盖或补全空字段
        if not base:
            return new
        merged = dict(base)
        for k, v in new.items():
            if v in (None, "", 0.0, []):
                continue
            if k == "applicant" and base.get("applicant"):
                continue
            #未提不覆盖已有票据状态
            if k == "has_invoice" and v == "未提" and base.get("has_invoice") in ("有票", "无票"):
                continue
            merged[k] = v
        return merged

    #按钮口令的全参数引导
    def _handle_fill_guide(self) -> dict:
        #讲清全部所需参数
        opener = random.choice(_FILL_GUIDE_OPENERS)
        closer = random.choice(_FILL_GUIDE_CLOSERS)
        lines = [
            opener,
            "",
            "【必填 · 一项都不能少】",
            "👤 申请人 —— 您怎么称呼呀？",
            "🏢 部门 —— 您在哪个部门高就呀？",
            "💼 报销类型 —— 差旅费 / 招待费 / 办公用品 / 交通费，四选一",
            "💰 金额 —— 这次一共花了多少呀？",
            "",
            "【重点提醒 · 直接影响审核速度】",
            "🧾 票据状态 —— 有发票会加快审核进度；无发票需要更多核验时间哦",
            "📝 事由 —— 报销的主要事情或用途写在这里，财务小伙伴全靠它归类~",
            "",
            "【选填 · 补了更好】",
            "📍 城市 —— 出差住宿的话说一声城市，可以加快审核",
            "📅 发票日期 —— 像 2026/10/1 这样就行，不说我帮您按今天记",
            "",
            closer,
            "💡 也可以一句话搞定：「我是张三，研发部，出差住酒店 800 元，有发票」",
        ]
        return {
            "intent": "B",
            "answer": "\n".join(lines),
            "record_id": None,
            "bill_no": "",
            "draft": {},  #空草稿 承接后续补充
        }

    #填单主干 合并草稿后追问或提交
    def _handle_fill(self, question: str, base_draft=None, user: dict | None = None,
                     image_b64: str = "", image_mime: str = "image/jpeg",
                     invoice_fields: dict | None = None) -> dict:
        #抽取本轮字段并合并历史草稿
        f = self._extract_fields(question)
        if base_draft:
            f = self._merge_draft(base_draft, f)
            #保留待确认标记
            if base_draft.get("await_confirm"):
                f["await_confirm"] = True
        #姓名以账号为准 部门以口述为准
        if user:
            if user.get("name"):
                f["applicant"] = user["name"]
            if not f.get("department") and user.get("department"):
                f["department"] = user["department"]
        #并入 OCR 发票号与销售方
        if invoice_fields:
            for _k in ("invoice_no", "invoice_seller"):
                _v = str(invoice_fields.get(_k) or "").strip()
                if _v:
                    f[_k] = _v[:64]
        #有票时清除无票确认标记
        if f.get("has_invoice") == "有票":
            f.pop("no_invoice_warned", None)
        result = {"intent": "B", "record_id": None, "bill_no": "", "answer": "", "draft": f}

        #专项流程不直交 引导走专项
        if f.get("special"):
            result["draft"] = None  #清空草稿避免粘连
            result["answer"] = (
                f"🚦「{f['special']}」不在小财直交范围，需走专项流程提交~ "
                "（团建/通讯/培训/推广/加班餐/私车公用请按对应渠道办理，不要伪装成 4 类报销哦）"
            )
            return result

        #必填项 申请人部门类型金额
        missing = []
        if not f.get("applicant"):
            missing.append("申请人")
        if not f.get("department"):
            missing.append("部门")
        if not f.get("category"):
            missing.append("报销类型")
        if not f.get("amount") or f["amount"] <= 0:
            missing.append("金额")

        if missing:
            result["answer"] = self._ask_missing(f, missing)
            return result

        #票据状态归一 未提不阻断
        has_invoice = f["has_invoice"] if f["has_invoice"] in ("有票", "无票") else "未提"

        #必填齐全 进入确认态 不直接写表
        if f.get("await_confirm") and self._is_confirm(question):
            #无票先软确认一次 用户坚持则放行
            compliance, risk_summary = self._compliance_check({**f, "has_invoice": has_invoice})
            if compliance == "阻断" and has_invoice == "无票":
                if not f.get("no_invoice_warned"):
                    #首次坚持 软确认不写表
                    result["draft"]["await_confirm"] = True
                    result["draft"]["no_invoice_warned"] = True
                    result["answer"] = self._ask_no_invoice_confirm(f)
                    return result
                #再次坚持 放行写表并标驳回
                answer, rid, bill_no = self._do_submit(
                    f, has_invoice,
                    compliance="驳回",
                    risk_summary="无票提交（用户确认坚持），财务将驳回",
                    employee_id=(user or {}).get("employee_id", ""),
                    image_b64=image_b64, image_mime=image_mime,
                    username=(user or {}).get("username", ""),
                )
            else:
                answer, rid, bill_no = self._do_submit(
                    f, has_invoice, employee_id=(user or {}).get("employee_id", ""),
                    image_b64=image_b64, image_mime=image_mime,
                    username=(user or {}).get("username", ""))
            result["answer"] = answer
            result["record_id"] = rid
            result["bill_no"] = bill_no
            #提交成功才清空草稿
            if rid:
                result["draft"] = None
            return result

        #未确认 回显核对等待确认提交
        result["draft"]["await_confirm"] = True
        result["answer"] = self._ask_confirm(f, has_invoice)
        return result

    #主服务器唯一写入口
    @staticmethod
    def _submit_via_backend(f: dict, has_invoice: str, invoice_info_only: bool = False,
                            username: str = ""):
        #提交给主服务器 未启用返回 None
        try:
            from api import backend_client  #延迟导入 未启用则无开销
        except Exception:
            return None
        if not backend_client.enabled():
            return None

        invoice_info = "；".join(
            x for x in (str(f.get("invoice_no") or "").strip(),
                        str(f.get("invoice_seller") or "").strip()) if x
        )
        try:
            data = backend_client.submit_as(
                username or f.get("username", ""),
                department=f.get("department", ""),
                rtype=f.get("category", ""),
                amount=f.get("amount", 0),
                reason=f.get("reason", ""),
                has_invoice=has_invoice,
                city=f.get("city", ""),
                date_str=f.get("date", ""),
                invoice_info=invoice_info,
            )
        except backend_client.BackendRejected as e:
            #业务规则拒绝 绝不回落直连飞书
            return (f"⚠️ 这张单没有提交成功：{e}", None, "")
        except backend_client.BackendError as e:
            if backend_client.mode() == "required":
                return (f"😥 主服务器暂时不可用，单据没有提交（{e}）。请稍后重试或联系管理员。", None, "")
            #仅基础设施故障才回落直连飞书
            print(f"[backend] 提交未走主服务器，回落直连飞书：{e}")
            return None

        rid = data.get("record_id") or ""
        bill_no = data.get("bill_no") or ""
        if not rid:
            return (f"😥 主服务器没有返回单据编号（{data.get('message', '')}），请稍后在进度查询里确认。", None, "")

        parts = [f"👤 {f.get('applicant', '')}"]
        if f.get("department"):
            parts.append(f"🏢 {f['department']}")
        if f.get("category"):
            parts.append(f"💼 {f['category']}")
        parts.append(f"💰 ¥{float(f.get('amount') or 0):.0f}")
        if f.get("city"):
            parts.append(f"📍 {f['city']}")
        if f.get("reason"):
            parts.append(f"📝 {f['reason']}")

        answer_lines = [
            "✅ 报销单已成功提交啦！",
            "",
            f"📋 单号：{bill_no}" if bill_no else "📋 单号：已成功提交，稍后可在「进度查询」中输入姓名查看",
            " · ".join(parts),
            "📊 当前状态：待审核",
        ]
        hints = XiaoCaiAgent._soft_hints(f)
        if hints:
            answer_lines.append("")
            answer_lines.extend(hints)
        answer_lines.append("财务小伙伴会尽快审核，结果出来会有通知~")
        return ("\n".join(answer_lines), rid, bill_no)

    #确认后真正写表
    def _do_submit(self, f: dict, has_invoice: str,
                   compliance: str | None = None, risk_summary: str | None = None,
                   employee_id: str = "", image_b64: str = "", image_mime: str = "image/jpeg",
                   username: str = ""):
        #预审加写库并回显结果
        if compliance is None:
            compliance, risk_summary = self._compliance_check({**f, "has_invoice": has_invoice})
        if compliance == "阻断":
            return (f"🧾 {risk_summary}，补齐合规发票后我马上帮您提交~", None, "")

        #先走主服务器写入口
        backend_result = self._submit_via_backend(f, has_invoice, invoice_info_only=False,
                                                  username=username)
        if backend_result is not None:
            return backend_result

        rec = build_reimbursement_record(
            applicant=f["applicant"], department=f["department"], rtype=f["category"],
            amount=f["amount"], reason=f.get("reason", ""), has_invoice=has_invoice,
            city=f.get("city", ""), date_str=f.get("date", ""), compliance=compliance, risk_summary=risk_summary,
            status="待审核", employee_id=employee_id,
        )
        #有发票信息才写该列
        invoice_info = "；".join(
            x for x in (str(f.get("invoice_no") or "").strip(),
                        str(f.get("invoice_seller") or "").strip()) if x
        )
        if invoice_info:
            rec["发票信息"] = invoice_info
        rec = self._fit_schema(rec)  #旧表无该列时剔除
        #写前枚举校验 非法值拒写
        enum_errors = validate_enum_fields(rec)
        if enum_errors:
            bad = "；".join(f"{fld}={val!r}" for fld, val in enum_errors)
            return (f"⚠️ 以下字段值不在企业标准枚举内，已拒绝写入以保护数据：{bad}。"
                    f"请按规范填写后重试~", None, "")
        if self.feishu and self.table_id:
            try:
                ids = self.feishu.create_records(self.table_id, [rec])
                rid = ids[0] if ids else None
                bill_no = self._fetch_bill_no(rid) if rid else ""
                #上传发票附件 失败不阻断
                attach_note = ""
                if image_b64 and rid:
                    attach_note = self._attach_invoice(rid, image_b64, image_mime)
                #取不到单号时诚实兜底
                if bill_no:
                    bill_line = f"📋 单号：{bill_no}"
                else:
                    bill_line = "📋 单号：已成功提交，稍后可在「进度查询」中输入姓名查看"
                #回显已提交参数
                parts = [f"👤 {f['applicant']}"]
                if f.get("department"):
                    parts.append(f"🏢 {f['department']}")
                if f.get("category"):
                    parts.append(f"💼 {f['category']}")
                parts.append(f"💰 ¥{f['amount']:.0f}")
                if f.get("city"):
                    parts.append(f"📍 {f['city']}")
                if f.get("reason"):
                    parts.append(f"📝 {f['reason']}")
                answer_lines = [
                    "✅ 报销单已成功提交到飞书表格啦！",
                    "",
                    bill_line,
                    " · ".join(parts),
                    "📊 当前状态：待审核",
                ]
                if compliance != "通过":
                    answer_lines.append(f"📌 合规结果：{compliance} / {risk_summary}")
                if attach_note:
                    answer_lines.append(attach_note)
                #提交后仍给软提示
                hints = self._soft_hints(f)
                if hints:
                    answer_lines.append("")
                    answer_lines.extend(hints)
                answer_lines.append(
                    "财务小伙伴会尽快审核，结果出来会有通知~ 您也可以随时问我"
                    "「我的报销进度怎么样了」来查看状态哦 💡"
                )
                return ("\n".join(answer_lines), rid, bill_no)
            except FeishuError as e:
                return (f"😥 抱歉，提交没能成功（{e}），需要我帮您重新提交吗？", None, "")
        return (
            f"📋 填单引导：信息齐全（申请人 {f['applicant']}，{f['department']}，"
            f"{f['category']} ¥{f['amount']:.0f}），飞书未接入，未实际写入。",
            None,
            "",
        )

    #发票附件存档
    _MIME_EXT = {"image/jpeg": "jpg", "image/png": "png", "image/webp": "webp"}

    def _attach_invoice(self, record_id: str, image_b64: str, image_mime: str = "image/jpeg") -> str:
        #上传发票附件并关联单据
        try:
            import base64 as _b64
            file_bytes = _b64.b64decode(image_b64, validate=True)
        except Exception as e:
            return f"📎 发票图片数据异常，未存档（{e}）；单据本身已提交成功"
        if not file_bytes:
            return "📎 发票图片为空，未存档；单据本身已提交成功"
        ext = self._MIME_EXT.get(image_mime, "jpg")
        try:
            token = self.feishu.upload_attachment(f"发票.{ext}", file_bytes, image_mime)
            names = self.feishu.field_names(self.table_id)
            if "发票附件" not in names:
                return ("📎 发票图片已上传，但当前表暂无「发票附件」列，未关联到单据"
                        "（可按 TABLE_SETUP 手册加列后重传）")
            self.feishu.update_record(self.table_id, record_id,
                                      {"发票附件": [{"file_token": token}]})
            return "📎 发票图片已存档到这张报销单，财务可直接点开核对~"
        except FeishuError as e:
            return f"📎 发票图片存档未成功（{e}）；单据本身已提交成功，可稍后补传"
        except Exception as e:  #附件异常不影响主流程
            return f"📎 发票图片存档未成功（{e}）；单据本身已提交成功，可稍后补传"

    #合规预审并回写
    def _handle_compliance(self, question: str, base_draft=None, user: dict | None = None) -> dict:
        #抽取并合并草稿后预审
        f = self._extract_fields(question)
        if base_draft:
            f = self._merge_draft(base_draft, f)
        result = {"intent": "C", "compliance": "", "risk_summary": "", "record_id": None, "answer": ""}

        if f["special"]:
            result["answer"] = (
                f"🚦「{f['special']}」不在小财直交范围，须走专项流程，无法按 4 类预审哦~"
            )
            return result

        compliance, risk_summary = self._compliance_check(f)
        result["compliance"] = compliance
        result["risk_summary"] = risk_summary

        if compliance == "阻断":
            result["answer"] = f"🧾 {risk_summary}，补齐合规发票后我再帮您预审~"
            return result

        #信息齐全才回写 否则仅给建议
        ready = bool(f["applicant"] and f["category"] and f["amount"] > 0 and f["has_invoice"] == "有票")
        backend_done = False
        if ready:
            #先走主服务器写入口
            via = self._submit_via_backend(f, f["has_invoice"],
                                           username=(user or {}).get("username", ""))
            if via is not None:
                backend_answer, backend_rid, _ = via
                result["record_id"] = backend_rid
                if backend_answer:
                    result["answer"] = backend_answer
                backend_done = True

        if ready and not backend_done and self.feishu and self.table_id:
            try:
                rec = build_reimbursement_record(
                    applicant=f["applicant"], department=f["department"], rtype=f["category"],
                    amount=f["amount"], reason=f["reason"], has_invoice=f["has_invoice"],
                    city=f["city"], date_str=f["date"], compliance=compliance, risk_summary=risk_summary,
                    status="待审核",
                )
                rec = self._fit_schema(rec)  #旧表无该列时剔除
                #写前枚举校验 非法值拒写
                enum_errors = validate_enum_fields(rec)
                if enum_errors:
                    bad = "；".join(f"{fld}={val!r}" for fld, val in enum_errors)
                    result["answer"] = f"⚠️ 预审通过，但字段值不在企业标准枚举内，已拒绝回写飞书：{bad}。"
                    return result
                ids = self.feishu.create_records(self.table_id, [rec])
                result["record_id"] = ids[0] if ids else None
            except FeishuError as e:
                result["answer"] = f"⚠️ 预审完成（{compliance}），但飞书回写失败：{e}。"

        if not result["answer"]:
            if compliance == "通过":
                result["answer"] = "✅ 合规预审：通过，无风险点。"
            else:
                result["answer"] = f"✅ 合规预审：存在风险。风险点：{risk_summary}"
        return result

    #进度查询 按申请人或单号
    def _d_extract_keyword(self, question: str) -> str:
        #进度查询词抽取
        q = question or ""
        bx = re.search(r"(BX-?\d[\d-]*)", q)
        if bx:
            return bx.group(1)

        def _valid(name: str) -> str:
            #姓名校验 过滤伪姓名
            name = (name or "").strip()
            if not re.fullmatch(r"[一-龥]{2,4}", name):
                return ""
            if name in _D_NAME_BLOCKLIST or name in _SKIP_NAME:
                return ""
            if any(ch in _D_PRONOUN_CHARS for ch in name):
                return ""
            if any(ch in _NON_NAME_CHARS for ch in name):
                return ""
            return name

        #姓名加查询词的组合句式
        for pat in (
            r"查(?:询|一下)?\s*([一-龥]{2,4}?)\s*的?\s*(?:进度|报销|单号|记录)",
            r"([一-龥]{2,4}?)\s*的\s*(?:进度|报销|记录|单号|结果)",
        ):
            m = re.search(pat, q)
            if m:
                name = _valid(m.group(1))
                if name:
                    return name

        #纯姓名 可带语气后缀
        bare = _valid(re.sub(r"[的了吧呀哦呢]+$", "", q.strip()))
        if bare:
            return bare

        #兜底取申请人或工号
        f = self._extract_fields(q)
        if f["applicant"] and f["applicant"] not in _D_NAME_BLOCKLIST:
            return f["applicant"]
        am = re.search(r"([A-Za-z0-9]{2,})", q)
        return am.group(1) if am else ""

    @staticmethod
    def _record_hit(keyword: str, fields: dict, question: str) -> bool:
        #判断记录是否命中查询词
        if not keyword:
            return False
        no = str(fields.get("单号", "") or "").strip()
        if no:
            k = keyword.upper().replace("-", "")
            n = no.upper().replace("-", "")
            if k in n or n in k:
                return True
        applicant = str(fields.get("申请人", "") or "").strip()
        if applicant and (keyword in applicant or applicant in keyword):
            return True
        for v in fields.values():
            s = str(v).strip()
            if len(s) >= 2 and not s.isdigit() and s in question:
                return True
        return False

    #展示格式化
    @staticmethod
    def _fmt_ts(v) -> str:
        #毫秒时间戳转日期时间串
        if v in (None, ""):
            return ""
        try:
            n = float(v)
        except (TypeError, ValueError):
            return str(v).strip()
        if n <= 0:
            return ""
        try:
            return datetime.fromtimestamp(n / 1000).strftime("%Y/%m/%d %H:%M")
        except (OverflowError, OSError, ValueError):
            return str(v)

    @staticmethod
    def _fmt_attach(v) -> str:
        #附件列转为附件名
        if isinstance(v, list) and v:
            names = []
            for it in v:
                if isinstance(it, dict):
                    names.append(str(it.get("name") or "附件"))
                else:
                    names.append(str(it))
            return "、".join(n for n in names if n)
        if isinstance(v, str) and v.strip():
            return v.strip()
        return ""

    def _handle_progress(self, question: str, user: dict | None = None) -> dict:
        #查飞书并汇总进度
        keyword = self._d_extract_keyword(question)
        hit_question = question
        #员工只能查自己 防越权查询
        if user and user.get("role") == ROLE_EMPLOYEE:
            keyword = user.get("name") or user.get("username") or keyword
            hit_question = keyword
        result = {"intent": "D", "records": [], "answer": ""}

        if not keyword:
            result["answer"] = "⏳ 请提供姓名或报销单号（如 BX000013），我帮您查进度~"
            return result

        if self.feishu and self.table_id:
            try:
                recs = self.feishu.list_records(self.table_id, page_size=50)
                matched = [
                    public_view(r) for r in recs
                    if self._record_hit(keyword, r.get("fields", {}), hit_question)
                ]
                result["records"] = matched
                if matched:
                    lines = ["好的，帮您查到啦~"]
                    for i, m in enumerate(matched, 1):
                        st = str(m.get("状态") or "").strip()
                        emoji = {"已通过": "✅", "待审核": "⏳", "审核中": "🔍", "已驳回": "❌"}.get(st, "📊")
                        amount = m.get("金额")
                        amount_s = f"¥{amount}" if amount not in (None, "") else "无"
                        #全字段展示 空值显示无
                        lines += [
                            f"{i}. 📋 单号 {m.get('单号') or '无'}",
                            f"   👤 申请人：{m.get('申请人') or '无'} · 部门：{m.get('部门') or '无'} · 工号：{m.get('工号') or '无'}",
                            f"   💼 报销类型：{m.get('报销类型') or '无'} · 金额：{amount_s}",
                            f"   📝 事由：{m.get('事由') or '无'}",
                            f"   🏙️ 城市：{m.get('城市') or '无'} · 发票日期：{self._fmt_ts(m.get('发票日期')) or '无'}",
                            f"   🕐 提交时间：{self._fmt_ts(m.get('记录提交时间')) or '无'}",
                            f"   🧾 票据状态：{m.get('票据状态') or '无'} · 发票信息：{m.get('发票信息') or '无'} · 发票附件：{self._fmt_attach(m.get('发票附件')) or '无'}",
                            f"   📊 状态：{emoji} {st or '无'}",
                            f"   📌 合规结果：{m.get('合规结果') or '无'} · 风险摘要：{m.get('风险摘要') or '无'}",
                            f"   👮‍♂️ 审核人：{m.get('审核人') or '无'} · 审批意见：{m.get('审批意见') or '无'} · 审批时间：{self._fmt_ts(m.get('审批时间')) or '无'}",
                        ]
                    lines.append("💡 有驳回单建议联系审核人，待审核请耐心等待审批~")
                    result["answer"] = "\n".join(lines)
                else:
                    result["answer"] = "⏳ 没有找到相关的报销记录呢~ 请确认姓名或单号是否正确？"
            except FeishuError as e:
                result["answer"] = f"⏳ 飞书查询失败（{e}），建议稍后重试或联系财务同学~"
        else:
            result["answer"] = "⏳ 进度查询需要接入飞书（M3），当前未接入。"
        return result

    #便捷构造
    @classmethod
    def from_config(cls, compressor=None, top_k: int = 3, default_category="报销制度", with_feishu: bool = True):
        #按 .env 与知识库一键装配
        llm = LLMAdapter.from_env()
        retriever = Retriever.from_directory(knowledge_dir())
        feishu = None
        table_id = None
        if with_feishu and os.getenv("FEISHU_APP_ID"):
            feishu = FeishuBitableClient.from_env()
            table_id = os.getenv("FEISHU_TABLE_ID", "")
        return cls(
            llm,
            retriever,
            compressor=compressor,
            top_k=top_k,
            default_category=default_category,
            feishu=feishu,
            table_id=table_id,
        )
