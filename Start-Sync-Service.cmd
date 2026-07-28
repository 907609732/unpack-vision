@echo off
setlocal
set "SERVICE=%~dp0artifacts\publish\staging\2.1.0\App\Service\电商拆包智能录像兼容同步服务.exe"
if not exist "%SERVICE%" (
  echo Published service was not found. Run scripts\publish.ps1 first.
  pause
  exit /b 1
)
start "电商拆包智能录像兼容同步服务" /min "%SERVICE%"
