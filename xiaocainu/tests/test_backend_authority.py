#账号权威链路单测 主服务器优先 纯离线
from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from api import backend_client, auth_service
from models import users

SECRET = "unit-test-secret-authority"


class FakeUserClient:
    #假的飞书用户表客户端
    def __init__(self, records):
        self.records = records
        self.updates = []

    def list_records(self, table_id, page_size=200):
        #返回预置记录
        return self.records

    def update_record(self, table_id, record_id, fields):
        #记录回写
        self.updates.append((record_id, fields))
        for r in self.records:
            if r.get("record_id") == record_id:
                r.setdefault("fields", {}).update(fields)
        return {"record_id": record_id}


def _row(username, password, role="员工", eid="E9001"):
    #构造一行本地用户表记录
    salt_hex, hash_hex = __import__("security.auth", fromlist=["hash_password"]).hash_password(password)
    return {
        "record_id": "rec-" + username,
        "fields": {
            "工号": eid, "姓名": username, "用户名": username, "角色": role,
            "密码哈希": hash_hex, "盐": salt_hex, "账号状态": "启用", "创建时间": 1,
        },
    }


def _service(records):
    #装配 AuthService
    store = users.UserStore(FakeUserClient(records), "tblTestUsers", cache_ttl=0)
    return auth_service.AuthService(store, SECRET)


class _Backend:
    #替身：模拟主服务器的四种回应

    def __init__(self, outcome, mode="preferred"):
        self.outcome = outcome
        self._mode = mode

    def __enter__(self):
        self._saved = (backend_client.enabled, backend_client.mode, backend_client.login)
        backend_client.enabled = lambda: True
        backend_client.mode = lambda: self._mode

        def fake_login(username, password):
            #按预设结果回应
            kind = self.outcome
            if kind == "ok":
                return {"userName": username, "displayName": "主服务器姓名", "role": "finance", "employeeId": "E0002"}
            if kind == "rejected":
                raise backend_client.BackendRejected("用户名或口令错误")
            raise backend_client.BackendUnavailable("无法连接主服务器")

        backend_client.login = fake_login
        return self

    def __exit__(self, *exc):
        backend_client.enabled, backend_client.mode, backend_client.login = self._saved
        return False


def test_backend_accepts_only_its_own_account():
    #只在本机账号库存在的账号也能登录（本地表里没有它）
    svc = _service([])
    with _Backend("ok"):
        code, payload = svc.login("employee", "Any#2026abcdef", "127.0.0.1")
    assert code == 200, payload
    assert payload["user"]["角色"] == "财务", payload
    assert payload["user"]["工号"] == "E0002", payload
    assert payload["user"]["用户名"] == "employee", payload
    assert "主服务器" in payload.get("backend", ""), payload
    print("[PASS] test_backend_accepts_only_its_own_account")


def test_backend_rejection_does_not_fall_back():
    #主服务器明确拒绝时不许用本地表兜底
    svc = _service([_row("localonly", "Local#2026abcd")])
    with _Backend("rejected"):
        code, payload = svc.login("localonly", "Local#2026abcd", "127.0.0.1")
    assert code == 401, payload
    assert "口令" in payload.get("error", ""), payload
    print("[PASS] test_backend_rejection_does_not_fall_back")


def test_backend_down_falls_back_in_preferred():
    #连不上时 preferred 模式回落到本地表
    svc = _service([_row("localonly", "Local#2026abcd")])
    with _Backend("down", mode="preferred"):
        code, payload = svc.login("localonly", "Local#2026abcd", "127.0.0.1")
    assert code == 200, payload
    assert payload["user"]["角色"] == "员工", payload
    assert "本地用户表" in payload.get("backend", ""), payload
    print("[PASS] test_backend_down_falls_back_in_preferred")


def test_backend_down_rejects_in_required():
    #连不上时 required 模式直接拒绝
    svc = _service([_row("localonly", "Local#2026abcd")])
    with _Backend("down", mode="required"):
        code, payload = svc.login("localonly", "Local#2026abcd", "127.0.0.1")
    assert code == 503, payload
    assert "required" in payload.get("error", ""), payload
    print("[PASS] test_backend_down_rejects_in_required")


def test_backend_disabled_uses_local_table():
    #未配置主服务器时保持原来的本地表行为
    svc = _service([_row("localonly", "Local#2026abcd")])
    saved = backend_client.enabled
    backend_client.enabled = lambda: False
    try:
        code, payload = svc.login("localonly", "Local#2026abcd", "127.0.0.1")
    finally:
        backend_client.enabled = saved
    assert code == 200, payload
    assert payload["user"]["用户名"] == "localonly", payload
    print("[PASS] test_backend_disabled_uses_local_table")


def test_role_label_mapping():
    #四角色映射到本网关的三个标签
    assert auth_service.ROLE_LABELS["employee"] == "员工"
    assert auth_service.ROLE_LABELS["finance"] == "财务"
    assert auth_service.ROLE_LABELS["admin"] == "管理员"
    assert auth_service.ROLE_LABELS["superadmin"] == "管理员"
    print("[PASS] test_role_label_mapping")


def test_backend_client_login_status_mapping():
    #4xx 归业务拒绝 5xx 归基础设施故障
    saved = backend_client._request
    try:
        backend_client._request = lambda *a, **k: (401, {"ok": False, "message": "用户名或口令错误"})
        try:
            backend_client.login("x", "y")
            raise AssertionError("401 应当抛 BackendRejected")
        except backend_client.BackendRejected:
            pass
        backend_client._request = lambda *a, **k: (503, {"ok": False, "message": "服务不可用"})
        try:
            backend_client.login("x", "y")
            raise AssertionError("503 应当抛 BackendError 而不是 BackendRejected")
        except backend_client.BackendRejected:
            raise AssertionError("503 不应被当成业务拒绝")
        except backend_client.BackendError:
            pass
    finally:
        backend_client._request = saved
    print("[PASS] test_backend_client_login_status_mapping")


if __name__ == "__main__":
    test_backend_accepts_only_its_own_account()
    test_backend_rejection_does_not_fall_back()
    test_backend_down_falls_back_in_preferred()
    test_backend_down_rejects_in_required()
    test_backend_disabled_uses_local_table()
    test_role_label_mapping()
    test_backend_client_login_status_mapping()
    print("\n账号权威链路单测全部通过")
