@echo off
rem ST-013: runs the whole loop on this machine and prints PASS or FAIL for every step (docs\dev\e2e.md).
rem Passes any arguments on, for example -InjectClicks. The exit code is the run's: 0 when every step passed.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-e2e.ps1" %*
exit /b %errorlevel%
