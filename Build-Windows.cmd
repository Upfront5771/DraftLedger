@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build-windows.ps1"
if errorlevel 1 (
  echo Build failed. Review the output above.
  pause
  exit /b 1
)
pause
