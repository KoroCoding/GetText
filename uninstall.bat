@echo off
rem GetText uninstall: removes shortcuts and (after asking) the AI models, minutes and settings.
rem All steps and messages are in uninstall.ps1 (this file stays ASCII so it runs on any code page).
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1" %*
echo.
pause
