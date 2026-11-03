@echo off
rem Double-click me to play. Keep this window open while you play (it is the game log).
rem (This file never changes: updates replace what it starts, files\play-windows.ps1.)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0files\play-windows.ps1" %*
if errorlevel 1 pause
