@echo off
setlocal
set "APP=%~dp0artifacts\publish\staging\2.1.0\App\电商拆包智能录像.exe"
if not exist "%APP%" (
  echo Published app was not found. Run scripts\publish.ps1 first.
  pause
  exit /b 1
)
start "电商拆包智能录像" "%APP%"
