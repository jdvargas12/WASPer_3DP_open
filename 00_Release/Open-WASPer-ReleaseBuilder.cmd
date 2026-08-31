@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0WASPer-ReleaseBuilder.ps1"
if errorlevel 1 (
  echo.
  echo WASPer release failed. Review the messages above.
  pause
)
endlocal
