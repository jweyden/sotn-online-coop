@echo off
rem Starts the game (started by "Play (Windows).bat"). Host and join in the game:
rem the title screen's main menu, Triangle (Netplay).
rem Optional, for direct connections:  play-windows.bat host   |   play-windows.bat join 100.x.y.z
if not exist "%~dp0SymphonyRecomp\bin" (
    echo The game isn't set up yet: double-click "Setup (Windows)" first.
    pause
    exit /b 1
)
set "DOTNET_ROOT=%LOCALAPPDATA%\Microsoft\dotnet"
set "PATH=%DOTNET_ROOT%;%PATH%"
if /I "%~1"=="host" set "COOP_ONLINE=host:7777"
if /I "%~1"=="join" set "COOP_ONLINE=join:%~2:7777"
rem (a copied or moved folder: point the settings at this folder's disc)
set "SR=%~dp0SymphonyRecomp"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$s=Join-Path $env:SR 'settings.json'; $c=Join-Path $env:SR 'disc\Castlevania - Symphony of the Night (USA).cue'; if ((Test-Path $s) -and (Test-Path $c)) { $j=$null; try { $j=Get-Content $s -Raw | ConvertFrom-Json } catch { }; if ($j -and $j.CdPath -ne $c) { $j | Add-Member -NotePropertyName CdPath -NotePropertyValue $c -Force; $j | ConvertTo-Json -Depth 20 | Set-Content $s } }"
cd /d "%~dp0SymphonyRecomp"
dotnet run --no-build
if errorlevel 1 pause
