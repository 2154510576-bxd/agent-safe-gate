#轻量 RAG 检索器 纯标准库实现
from __future__ import annotations

import os
import re
import math
from dataclasses import dataclass, field


@dataclass
class Chunk:
    #检索返回的最小单元

    text: str
    source: str = ""
    category: str = ""
    score: float = 0.0
    meta: dict = field(default_factory=dict)


class Retriever:
    #基于 BM25 的轻量检索器

    def __init__(
        self,
        chunks: list | None = None,
        k1: float = 1.5,
        b: float = 0.75,
        reranker=None,
    ) -> None:
        self.k1 = k1
        self.b = b
        self.reranker = reranker  #可选重排回调
        self._docs: list[tuple[Chunk, list[str]]] = []  #文档与分词
        self._df: dict[str, int] = {}  #词频
        self._idf: dict[str, float] = {}
        self._avgdl: float = 0.0
        if chunks:
            self.add(chunks)

    #分词
    @staticmethod
    def tokenize(text: str) -> list[str]:
        #ASCII 词加汉字单字
        raw = re.findall(r"[a-zA-Z0-9]+|[\u4e00-\u9fff]", text or "")
        return [t.lower() for t in raw]

    #建立索引
    def add(self, chunks: list) -> None:
        #支持 Chunk 元组或字符串
        parsed: list[tuple[Chunk, list[str]]] = []
        for c in chunks:
            if isinstance(c, Chunk):
                text, source, category = c.text, c.source, c.category
            elif isinstance(c, tuple) and len(c) == 2:
                text, source = c
                category = ""
            else:
                text, source = str(c), ""
                category = ""
            #类别取文件名
            if not category and source:
                category = os.path.splitext(os.path.basename(source))[0]
            toks = self.tokenize(text)
            parsed.append(
                (
                    Chunk(
                        text=text,
                        source=source,
                        category=category,
                        meta={"tokens": len(toks)},
                    ),
                    toks,
                )
            )

        for _, toks in parsed:
            for t in set(toks):
                self._df[t] = self._df.get(t, 0) + 1
        self._docs.extend(parsed)

        total = sum(len(toks) for _, toks in self._docs)
        self._avgdl = total / len(self._docs) if self._docs else 0.0
        n = len(self._docs)
        for t, df in self._df.items():
            #Okapi IDF 加 1 平滑
            self._idf[t] = math.log(1 + (n - df + 0.5) / (df + 0.5))

    #类别匹配
    @staticmethod
    def _match_category(chunk: Chunk, categories) -> bool:
        #类别或来源子串命中
        if not categories:
            return True
        if isinstance(categories, str):
            cats = [categories]
        else:
            cats = list(categories)
        hay = (chunk.category + " " + chunk.source).lower()
        return any(cat.strip().lower() in hay for cat in cats if cat.strip())

    #查询
    def query(
        self,
        question: str,
        top_k: int = 3,
        categories=None,
        min_score_ratio: float = 0.0,
        min_score: float = 0.0,
    ) -> list[Chunk]:
        #检索主入口 支持类别过滤与截断
        q_tokens = self.tokenize(question)
        if not q_tokens or not self._docs:
            return []

        #先按类别缩小候选
        docs = [
            (c, t) for (c, t) in self._docs if self._match_category(c, categories)
        ]
        if not docs:
            return []

        #BM25 打分并降序
        scored: list[tuple[float, Chunk]] = []
        for chunk, toks in docs:
            s = self._bm25(q_tokens, toks)
            chunk.score = round(s, 4)
            scored.append((s, chunk))
        scored.sort(key=lambda x: x[0], reverse=True)

        #多捞候选给重排
        cand_k = max(top_k * 4, 12)
        pool: list[Chunk] = [c for _, c in scored[:cand_k]]

        #可选重排 默认关闭
        if self.reranker is not None:
            pool = list(self.reranker(question, pool))

        #按相关度截断噪音
        if pool:
            top_score = max(c.score for c in pool)
            floor = min_score
            if min_score_ratio > 0:
                floor = max(floor, top_score * min_score_ratio)
            kept = [c for c in pool if c.score >= floor]
        else:
            kept = []

        return kept[:top_k]

    def _bm25(self, q_tokens: list[str], doc_tokens: list[str]) -> float:
        #算 BM25 得分
        doc_len = len(doc_tokens)
        freq: dict[str, int] = {}
        for t in doc_tokens:
            freq[t] = freq.get(t, 0) + 1
        score = 0.0
        denom_base = self.k1 * (1 - self.b + self.b * doc_len / (self._avgdl or 1))
        for qt in set(q_tokens):
            if qt not in self._idf:
                continue
            f = freq.get(qt, 0)
            if f == 0:
                continue
            idf = self._idf[qt]
            score += idf * (f * (self.k1 + 1)) / (f + denom_base)
        return score

    #按目录建索引
    @classmethod
    def from_directory(
        cls,
        directory: str,
        chunk_by: str = "heading",
        max_chars: int = 1200,
        overlap: int = 120,
        reranker=None,
        **kw,
    ) -> "Retriever":
        #读目录文档并切块建索引
        return cls(
            load_documents(
                directory, chunk_by=chunk_by, max_chars=max_chars, overlap=overlap
            ),
            reranker=reranker,
            **kw,
        )


#文档切块与加载
def split_markdown(text: str, max_chars: int = 1200, overlap: int = 120) -> list[str]:
    #按 Markdown 标题切块
    text = (text or "").strip()
    if not text:
        return []
    lines = text.splitlines()
    sections: list[tuple[int, str, list[str]]] = []  #层级 标题 正文行
    cur_level = 0
    cur_heading = ""
    cur_body: list[str] = []
    for ln in lines:
        m = re.match(r"^(#{1,6})\s+(.*)$", ln)
        if m:
            if cur_heading or cur_body:
                sections.append((cur_level, cur_heading, cur_body))
            cur_level = len(m.group(1))
            cur_heading = m.group(2).strip()
            cur_body = []
        else:
            cur_body.append(ln)
    if cur_heading or cur_body:
        sections.append((cur_level, cur_heading, cur_body))

    chunks: list[str] = []
    for level, heading, body in sections:
        body_text = "\n".join(body).strip()
        if not body_text:  #跳过无正文的标题节
            continue
        marker = "#" * level if level else ""
        block = (f"{marker} {heading}\n{body_text}" if heading else body_text).strip()
        if not block:
            continue
        if len(block) <= max_chars:
            chunks.append(block)
        else:
            #超长节再按字符切
            for piece in split_text(body_text, max_chars=max_chars, overlap=overlap):
                chunks.append(f"{marker} {heading}\n{piece}" if heading else piece)
    return chunks


def split_text(text: str, max_chars: int = 400, overlap: int = 80) -> list[str]:
    #按长度切块 优先空行分段
    text = (text or "").strip()
    if not text:
        return []
    paras = [p.strip() for p in re.split(r"\n\s*\n", text) if p.strip()]
    if not paras:
        paras = [text]

    chunks: list[str] = []
    buf = ""
    for p in paras:
        if len(buf) + len(p) + 1 <= max_chars:
            buf = (buf + "\n" + p) if buf else p
        else:
            if buf:
                chunks.append(buf)
            if len(p) > max_chars:
                step = max(1, max_chars - overlap)
                for i in range(0, len(p), step):
                    chunks.append(p[i : i + max_chars])
                buf = ""
            else:
                buf = p
    if buf:
        chunks.append(buf)
    return chunks


def load_documents(
    directory: str,
    chunk_by: str = "heading",
    max_chars: int = 1200,
    overlap: int = 120,
) -> list[Chunk]:
    #读目录下文档并切成 Chunk
    chunks: list[Chunk] = []
    if not os.path.isdir(directory):
        return chunks
    for name in sorted(os.listdir(directory)):
        if not name.lower().endswith((".md", ".txt")):
            continue
        path = os.path.join(directory, name)
        try:
            with open(path, encoding="utf-8") as f:
                content = f.read()
        except OSError:
            continue
        category = os.path.splitext(name)[0]
        if chunk_by == "char":
            pieces = split_text(content, max_chars=max_chars, overlap=overlap)
        else:
            pieces = split_markdown(content, max_chars=max_chars, overlap=overlap)
        for piece in pieces:
            chunks.append(Chunk(text=piece, source=name, category=category))
    return chunks


#拼给模型的上下文
def format_context(
    chunks: list[Chunk],
    max_chars: int = 1500,
    compress=None,
    query: str = "",
) -> str:
    #拼成可直接塞进 prompt 的文本
    parts: list[str] = []
    total = 0
    for i, c in enumerate(chunks, 1):
        body = c.text
        if compress is not None:
            body = compress(query, body)
        if not body or not body.strip():  #压缩后为空则跳过
            continue
        block = f"[资料{i}]({c.source})\n{body}"
        if total + len(block) > max_chars and parts:
            break
        parts.append(block)
        total += len(block)
    return "\n\n".join(parts)
