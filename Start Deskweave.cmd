@echo off
if exist "%~dp0out\Deskweave.exe" (
  start "" "%~dp0out\Deskweave.exe"
  exit /b
)
echo Deskweave has not been built yet. Run tools\publish.ps1 first.
pause
