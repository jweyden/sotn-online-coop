@echo off
rem Double-click me. Sets everything up (run me again after fixing anything I ask for).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0files\setup-windows.ps1"
echo.
pause
