@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-mstscX.ps1" %*
set "buildExitCode=%errorlevel%"
if not "%buildExitCode%"=="0" echo ERROR: Installer build failed.
pause
exit /b %buildExitCode%
