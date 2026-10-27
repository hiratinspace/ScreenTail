@echo off
rem Runs start.ps1 with the execution policy it needs, passing any arguments on (for example -Rebuild).
rem On failure the window waits for a key, so a double-clicked run does not vanish before it can be read.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0start.ps1" %*
if errorlevel 1 (
    echo.
    pause
    exit /b 1
)
