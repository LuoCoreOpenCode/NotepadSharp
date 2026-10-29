@echo off
chcp 65001 >nul
title NotepadSharp 启动器
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run_retry.ps1"
echo.
pause
