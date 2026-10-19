@echo off
REM Xiaocainu API gateway launcher (start the finance/admin web console).
REM NOTE: all comments must stay ASCII here -- cmd.exe reads this file with the
REM system ANSI codepage (GBK on zh-CN); any UTF-8 Chinese comment would be
REM decoded as garbage and cmd would try to execute it as a command.
REM Must use the managed venv interpreter (base 3.13.12 lacks pydantic and would crash on startup).
cd /d "%~dp0"
"C:\Users\Administrator\.workbuddy\binaries\python\envs\default\Scripts\python.exe" -m api.main 0.0.0.0 8000