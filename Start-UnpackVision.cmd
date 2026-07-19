@echo off
setlocal
set "APP=%~dp0artifacts\publish\App-1.3.1\拆包智录.exe"
if not exist "%APP%" (
  echo Published app was not found. Run scripts\publish.ps1 first.
  pause
  exit /b 1
)
start "拆包智录" "%APP%"
