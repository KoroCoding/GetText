@echo off
rem GetText setup: installs .NET 10 SDK and Python 3.11 (via winget) if missing,
rem builds the app, sets up the AI models, and creates shortcuts.
rem All steps and messages are in setup.ps1 (this file stays ASCII so it runs on any code page).
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup.ps1" %*
echo.
pause
