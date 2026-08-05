@echo off
setlocal
cd /d "%~dp0"
title Codex Usage Notifier 5.0 Installer
echo Codex Usage Notifier 5.0 installer
echo.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" -InstallCodexIfMissing -UpdateCodex
set EXITCODE=%ERRORLEVEL%
echo.
if not "%EXITCODE%"=="0" echo Installation failed with exit code %EXITCODE%.
if "%EXITCODE%"=="0" echo Installation and validation completed.
pause
exit /b %EXITCODE%
