@echo off
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0scripts\Start-ClaudeBrowser.ps1"
if errorlevel 1 pause
