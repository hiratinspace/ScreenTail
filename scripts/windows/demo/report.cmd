@echo off
rem Runs report.ps1 with the execution policy it needs. It writes one text file to the Desktop and opens it.
rem The window waits for a key at the end, so a double-clicked run does not vanish before it can be read.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0report.ps1" %*
if errorlevel 1 (
    echo.
    pause
    exit /b 1
)
echo.
pause
