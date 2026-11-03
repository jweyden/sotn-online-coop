# Starts the game (started by "Play (Windows).bat"), after checking for an update.
# Host and join in the game: the title screen's main menu, Triangle (Netplay).
# Optional, for direct connections:  play-windows.ps1 host   |   play-windows.ps1 join 100.x.y.z
$Here = Split-Path -Parent $MyInvocation.MyCommand.Path   # files\
$Game = Join-Path $Here "SymphonyRecomp"
if (-not (Test-Path (Join-Path $Game "bin"))) { Write-Host "The game isn't set up yet: double-click `"Setup (Windows)`" first."; exit 1 }

& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Here "update-windows.ps1")
if ($LASTEXITCODE -ne 0) { exit 1 }

$env:DOTNET_ROOT = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
if ($args.Count -ge 1 -and $args[0] -eq "host") { $env:COOP_ONLINE = "host:7777" }
if ($args.Count -ge 2 -and $args[0] -eq "join") { $env:COOP_ONLINE = "join:$($args[1]):7777" }
# (a copied or moved folder: point the settings at this folder's disc)
$s = Join-Path $Game "settings.json"
$c = Join-Path $Game "disc\Castlevania - Symphony of the Night (USA).cue"
if ((Test-Path $s) -and (Test-Path $c)) {
    $j = $null
    try { $j = Get-Content $s -Raw | ConvertFrom-Json } catch { }
    if ($j -and $j.CdPath -ne $c) { $j | Add-Member -NotePropertyName CdPath -NotePropertyValue $c -Force; $j | ConvertTo-Json -Depth 20 | Set-Content $s }
}
Set-Location $Game
& dotnet run --no-build
exit $LASTEXITCODE
