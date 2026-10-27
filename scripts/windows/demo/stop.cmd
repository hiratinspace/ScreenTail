@echo off
rem Runs stop.ps1 with the execution policy it needs.
rem On failure the window waits for a key, so a double-clicked run does not vanish before it can be read.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0stop.ps1" %*
if errorlevel 1 (
    echo.
    pause
    exit /b 1
)
