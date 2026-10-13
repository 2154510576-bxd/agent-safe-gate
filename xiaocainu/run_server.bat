@echo off
REM 小财奴 API 网关启动器
REM 必须用已装好 pydantic/fastapi/uvicorn/httpx 的受管 VENV 解释器（基础版 3.13.12 缺 pydantic，会启动即崩）。
cd /d "%~dp0"
"C:\Users\Administrator\.workbuddy\binaries\python\envs\default\Scripts\python.exe" -m api.main 0.0.0.0 8000
