@echo off
cd /d "%~dp0"
if not exist "bin\Release\net10.0-windows10.0.19041.0\GetText.exe" dotnet build -c Release
start "" "bin\Release\net10.0-windows10.0.19041.0\GetText.exe"
