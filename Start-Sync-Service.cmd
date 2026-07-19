@echo off
setlocal
set "SERVICE=%~dp0artifacts\publish\Service\UnpackVision.Service.exe"
if not exist "%SERVICE%" (
  echo Published service was not found. Run scripts\publish.ps1 first.
  pause
  exit /b 1
)
start "UnpackVision Sync" /min "%SERVICE%"
