@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Huaxiazi-Install.ps1" %*
exit /b %ERRORLEVEL%
