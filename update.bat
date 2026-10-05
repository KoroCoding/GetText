@echo off
rem GetText update: rebuild after overwriting changed files (Python env and models are kept).
rem Messages are in update.ps1 (this file stays ASCII so it runs on any code page).
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0update.ps1"
echo.
pause
