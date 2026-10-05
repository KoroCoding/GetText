@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup_offline.ps1" %*
pause
