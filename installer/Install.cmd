@echo off
rem Double-clickable wrapper: runs install.ps1 with a process-scoped execution policy bypass.
rem Uses PowerShell 7 (pwsh) when available, otherwise Windows PowerShell 5.1.
setlocal
set "PS_EXE=powershell.exe"
where pwsh.exe >nul 2>&1 && set "PS_EXE=pwsh.exe"
"%PS_EXE%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
set "EXIT_CODE=%ERRORLEVEL%"
echo.
if not "%EXIT_CODE%"=="0" echo L'installation a echoue (code %EXIT_CODE%).
pause
exit /b %EXIT_CODE%
