@echo off
setlocal
set "managerExe=%~dp0runtime\manager-app\ClaudeSafe.Manager.exe"
if not exist "%managerExe%" (
  echo Build the manager first: powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0scripts\Build-Manager.ps1"
  pause
  exit /b 1
)
if "%~1"=="" (
  start "" "%managerExe%"
) else (
  start "" "%managerExe%" --data "%~1"
)
