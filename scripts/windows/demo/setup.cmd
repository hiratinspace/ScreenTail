@echo off
rem Runs setup.ps1 with the execution policy it needs, passing any arguments on (for example -NewKey or -Provider claude).
rem On failure the window waits for a key, so a double-clicked run does not vanish before it can be read.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup.ps1" %*
if errorlevel 1 (
    echo.
    pause
    exit /b 1
)
