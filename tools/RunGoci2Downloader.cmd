@echo off
setlocal
cd /d "%~dp0"

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Goci2Downloader.ps1" %*
set EXITCODE=%ERRORLEVEL%

echo.
if not "%EXITCODE%"=="0" (
  echo GOCI-II downloader failed. Exit code: %EXITCODE%
) else (
  echo GOCI-II downloader completed.
)

pause
exit /b %EXITCODE%
