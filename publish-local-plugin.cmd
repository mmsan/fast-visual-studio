@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish-local-plugin.ps1" %*
exit /b %errorlevel%
