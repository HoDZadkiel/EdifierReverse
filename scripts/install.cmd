@echo off
rem One-click install: double-click. Arguments are passed to install.ps1 (e.g. install.cmd -Uninstall).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
echo.
pause
