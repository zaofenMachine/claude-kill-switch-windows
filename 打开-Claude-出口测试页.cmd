@echo off
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0scripts\Start-ClaudeBrowser.ps1" -TestPage
if errorlevel 1 pause
