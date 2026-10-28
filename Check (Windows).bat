@echo off
rem Checks that this computer has everything the game needs (Windows 10/11, x64),
rem and fixes what it can. Safe to run any time, on a fresh setup or a copied folder.
title SotN Online Co-op - install check
set "CHECK_SELF=%~f0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "iex ((Get-Content -LiteralPath $env:CHECK_SELF -Raw) -split '#\s*POWERSHELL\s*#')[1]"
echo.
pause
exit /b
# POWERSHELL #
$ErrorActionPreference = "Continue"
$Kit = Split-Path -Parent $env:CHECK_SELF
if (-not (Test-Path (Join-Path $Kit "files")) -and (Split-Path -Leaf $Kit) -eq "files") { $Kit = Split-Path -Parent $Kit }
$Files = Join-Path $Kit "files"
$Game = Join-Path $Files "SymphonyRecomp"
$CueName = "Castlevania - Symphony of the Night (USA).cue"
$script:problems = 0; $script:fixed = 0

function Ok($m)    { Write-Host "  [ OK ]  $m" -ForegroundColor Green }
function Info($m)  { Write-Host "  [info]  $m" -ForegroundColor Gray }
function Warn($m)  { Write-Host "  [warn]  $m" -ForegroundColor Yellow }
function Bad($m)   { Write-Host "  [FAIL]  $m" -ForegroundColor Red; $script:problems++ }
function Fixed($m) { Write-Host "  [FIXD]  $m" -ForegroundColor Cyan; $script:fixed++ }
function Head($m)  { Write-Host ""; Write-Host $m -ForegroundColor White }
function Ask($q)   { $a = Read-Host "          $q [Y/n]"; return ($a -eq "" -or $a -match '^[Yy]') }

Write-Host "SotN Online Co-op: install check" -ForegroundColor White
Write-Host "Folder: $Kit"

# ------------------------------------------------------------------ system
Head "Windows"
$os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
if ($os) { Info "$($os.Caption) ($($os.Version)), $([math]::Round($os.TotalVisibleMemorySize / 1MB, 1)) GB RAM" }
$arch = $env:PROCESSOR_ARCHITECTURE; if ($env:PROCESSOR_ARCHITEW6432) { $arch = $env:PROCESSOR_ARCHITEW6432 }
if ($arch -eq "AMD64") { Ok "64-bit (x64)" }
elseif ($arch -eq "ARM64") { Warn "ARM64 Windows: not tested (x64 PCs are)" }
else { Bad "32-bit Windows ($arch): the game needs 64-bit Windows" }
$drive = (Get-Item $Kit).PSDrive
if ($drive -and $drive.Free) { $gb = [math]::Round($drive.Free / 1GB, 1); if ($gb -lt 2) { Warn "only $gb GB free on $($drive.Name):" } else { Ok "$gb GB free on $($drive.Name):" } }
foreach ($g in (Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue)) { Info "graphics: $($g.Name) (driver $($g.DriverVersion); needs OpenGL 3.3, any GPU from the last 10 years)" }

# ------------------------------------------------------------------ runtimes
Head "Microsoft Visual C++ runtime (window, sound and menu libraries need it)"
function Have-VC {
    $k = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64" -ErrorAction SilentlyContinue
    if (-not $k) { $k = Get-ItemProperty "HKLM:\SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x64" -ErrorAction SilentlyContinue }
    $files = (Test-Path "$env:WINDIR\System32\vcruntime140.dll") -and (Test-Path "$env:WINDIR\System32\vcruntime140_1.dll") -and (Test-Path "$env:WINDIR\System32\msvcp140.dll")
    return @{ Installed = ($k -and $k.Installed -eq 1 -and $files); Version = $(if ($k) { $k.Version } else { "" }) }
}
$vc = Have-VC
if ($vc.Installed) { Ok "installed ($($vc.Version))" }
else {
    Bad "missing (the game opens no window and plays no sound without it)"
    if (Ask "Download and install it now (from Microsoft)?") {
        try {
            $exe = Join-Path $env:TEMP "vc_redist.x64.exe"
            Invoke-WebRequest https://aka.ms/vs/17/release/vc_redist.x64.exe -OutFile $exe -UseBasicParsing
            Write-Host "          installing (Windows may ask for permission: click Yes)..."
            Start-Process -FilePath $exe -ArgumentList "/install", "/quiet", "/norestart" -Verb RunAs -Wait
            if ((Have-VC).Installed) { $script:problems--; Fixed "Visual C++ runtime installed" } else { Warn "still not found: install https://aka.ms/vs/17/release/vc_redist.x64.exe by hand" }
        } catch { Warn "couldn't install: $($_.Exception.Message)" }
    }
}

Head ".NET 10 SDK (runs the game and compiles the mod)"
$DotnetDir = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet"
function Find-Sdk {
    foreach ($d in @((Join-Path $DotnetDir "dotnet.exe"), "$env:ProgramFiles\dotnet\dotnet.exe")) {
        if (Test-Path $d) {
            $sdks = & $d --list-sdks 2>$null | Where-Object { $_ -match '^10\.' }
            if ($sdks) { return @{ Exe = $d; Sdk = (($sdks | Select-Object -Last 1) -split ' ')[0] } }
        }
    }
    return $null
}
$sdk = Find-Sdk
if ($sdk) {
    Ok ".NET SDK $($sdk.Sdk) ($($sdk.Exe))"
} else {
    Bad "not found (Play needs the .NET 10 SDK)"
    if (Ask "Install it now (Microsoft's installer, into your user folder, no admin needed)?") {
        try {
            $ps1 = Join-Path $env:TEMP "dotnet-install.ps1"
            Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile $ps1 -UseBasicParsing
            & $ps1 -Channel 10.0 -InstallDir $DotnetDir
            $sdk = Find-Sdk
            if ($sdk) { $script:problems--; Fixed ".NET SDK $($sdk.Sdk) installed" } else { Warn "still not found after installing" }
        } catch { Warn "couldn't install: $($_.Exception.Message)" }
    }
}

Head "Git (only needed to run Setup; a copied folder can play without it)"
$git = Get-Command git -ErrorAction SilentlyContinue
if ($git) { Ok (& git --version) } else { Info "not installed (Setup installs it if you run Setup)" }

# ------------------------------------------------------------------ the game
Head "The game"
if (-not (Test-Path $Game)) {
    Bad "no game folder at files\SymphonyRecomp: run 'Setup (Windows)' first (or copy the whole folder from a computer that has it)"
} else {
    $bin = Join-Path $Game "bin\Debug\net10.0"
    if (Test-Path (Join-Path $bin "sotn.dll")) { Ok "game built (files\SymphonyRecomp\bin)" } else { Bad "game not built yet: run 'Setup (Windows)'" }
    if (Test-Path (Join-Path $Game "generated")) { Ok "recompiled code present" } else { Bad "recompiled code (generated) missing: run 'Setup (Windows)'" }
    $native = Join-Path $bin "runtimes\win-x64\native"
    $missing = @("glfw3.dll", "soft_oal.dll", "cimgui.dll", "SDL2.dll") | Where-Object { -not (Test-Path (Join-Path $native $_)) }
    if (Test-Path $bin) { if ($missing) { Bad "missing Windows libraries in bin\...\win-x64\native: $($missing -join ', ') (run 'Setup (Windows)' to rebuild)" } else { Ok "window/sound/menu libraries (GLFW, OpenAL, cimgui, SDL2)" } }

    # the disc: settings.json holds a full path, which breaks when the folder is copied or moved
    $disc = Join-Path $Game "disc"
    $settings = Join-Path $Game "settings.json"
    $cue = Join-Path $disc $CueName
    if (-not (Test-Path $cue)) {
        $any = Get-ChildItem $disc -Filter *.cue -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($any) { $cue = $any.FullName }
    }
    if (-not (Test-Path $cue)) {
        Bad "no disc in files\SymphonyRecomp\disc: put your PlayStation .cue + .bin in '1 - PlayStation game' and run 'Setup (Windows)'"
    } else {
        $bad = @()
        foreach ($line in Get-Content $cue) { if ($line -match '^\s*FILE\s+"([^"]+)"') { $p = Join-Path $disc $Matches[1]; if (-not (Test-Path $p)) { $bad += $Matches[1] } elseif ((Get-Item $p).Length -eq 0 -and $Matches[1] -notmatch 'Track 0?1') { Info "$($Matches[1]) is empty (an audio track; the game works without it)" } } }
        if ($bad) { Bad "the .cue asks for files that aren't in the disc folder: $($bad -join ', ')" } else { Ok "disc: $(Split-Path -Leaf $cue)" }
        $j = $null
        if (Test-Path $settings) { try { $j = Get-Content $settings -Raw | ConvertFrom-Json } catch { } }
        if ($j -and $j.CdPath -eq $cue) { Ok "settings point at the disc" }
        elseif ((Test-Path $settings) -and -not $j) { Bad "settings.json can't be read (damaged?): delete it and run 'Setup (Windows)'" }
        else {
            try {
                if (-not $j) { $j = [pscustomobject]@{} }
                $j | Add-Member -NotePropertyName CdPath -NotePropertyValue $cue -Force
                $j | ConvertTo-Json -Depth 20 | Set-Content $settings
                Fixed "settings now point at this folder's disc (it was copied or moved)"
            } catch { Bad "couldn't update settings.json: $($_.Exception.Message)" }
        }
    }
    # the characters built from the PSP and Saturn games (Setup's character builder)
    $missing = @()
    foreach ($f in "maria\MARIA.BIN", "maria\SD_RIH.VB", "psp\sd_alk.spk", "mariasat\MARIASAT.BIN", "mariasat\MARIASAT.SPR", "richter2\RICHTER2.BIN", "charsel\CHARSEL.BIN") {
        if (-not (Test-Path (Join-Path $disc $f))) { $missing += $f }
    }
    if ($missing) { Bad "characters not built yet ($($missing -join ', ')): put your PSP game in '2 - PSP game' and your Saturn game in '3 - Saturn game', start Docker Desktop, and run 'Setup (Windows)'" }
    else { Ok "all characters built (PSP Maria, Saturn Maria, long-coat Richter, the select screen, PSP voices)" }

    Head "The Online Co-op mod"
    $mod = Join-Path $Game "mods\coop-online"
    if (Test-Path (Join-Path $mod "mod.json")) {
        $n = @(Get-ChildItem (Join-Path $mod "source") -Filter *.cs -Recurse -ErrorAction SilentlyContinue).Count
        Ok "installed ($n source files)"
        $kitMod = Join-Path $Files "mods\coop-online"
        if (Test-Path $kitMod) {
            $new = Get-ChildItem $kitMod -Recurse -File | Where-Object { $rel = $_.FullName.Substring($kitMod.Length); $dst = Join-Path $mod $rel; -not (Test-Path $dst) -or (Get-FileHash $dst).Hash -ne (Get-FileHash $_.FullName).Hash }
            if ($new) {
                Warn "the kit has a newer mod than the installed one ($(@($new).Count) files differ)"
                if (Ask "Install the kit's mod now?") {
                    Remove-Item -Recurse -Force $mod; Copy-Item -Recurse $kitMod $mod; Fixed "mod updated from the kit"
                }
            } else { Ok "matches the kit's copy" }
        }
    } else {
        Bad "not installed: run 'Setup (Windows)'"
    }
    $ini = Join-Path $Game "interface.ini"
    $lines = @(); if (Test-Path $ini) { $lines = @(Get-Content $ini) }
    if ($lines -contains "mods.coop-online.enabled=True" -and -not ($lines -contains "mods.coop.enabled=True")) { Ok "enabled" }
    elseif (Test-Path $mod) {
        $lines = @($lines | Where-Object { $_ -notmatch '^mods\.coop(-online)?\.enabled=' -and $_ -ne "[RecompOne]" })
        Set-Content -Path $ini -Value (@("[RecompOne]", "mods.coop-online.enabled=True", "mods.coop.enabled=False") + $lines)
        Fixed "mod switched on (and the local co-op mod off)"
    }

    # ---------------------------------------------------------------- network
    Head "Network"
    $lobby = ""
    $lobbyFile = Join-Path $mod "lobby.txt"
    if (Test-Path $lobbyFile) { $lobby = (Get-Content $lobbyFile | Select-Object -First 1).Trim() }
    if (-not $lobby) { Bad "no lobby server address (mods\coop-online\lobby.txt)" }
    else {
        $hostPart, $portPart = $lobby -split ':'; if (-not $portPart) { $portPart = 7777 }
        try {
            $u = New-Object System.Net.Sockets.UdpClient
            $u.Client.ReceiveTimeout = 2000
            $msg = [Text.Encoding]::ASCII.GetBytes("LBlist`n")
            $ep = New-Object System.Net.IPEndPoint ([System.Net.IPAddress]::Any), 0
            $reply = $null
            for ($i = 0; $i -lt 3 -and -not $reply; $i++) {
                [void]$u.Send($msg, $msg.Length, $hostPart, [int]$portPart)
                try { $reply = $u.Receive([ref]$ep) } catch { }
            }
            $u.Close()
            if ($reply) {
                $rows = @(([Text.Encoding]::UTF8.GetString($reply, 2, $reply.Length - 2) -split "`n") | Select-Object -Skip 1 | Where-Object { $_ })
                Ok "lobby server $lobby answers ($($rows.Count) game(s) listed)"
            } else { Bad "lobby server $lobby doesn't answer (UDP $portPart): no internet, or a network that blocks UDP (some work/school networks)" }
        } catch { Bad "couldn't reach the lobby server: $($_.Exception.Message)" }
    }
    $exe = if ($sdk) { $sdk.Exe } else { Join-Path $DotnetDir "dotnet.exe" }
    try {
        $rules = @(Get-NetFirewallApplicationFilter -Program $exe -ErrorAction Stop | Get-NetFirewallRule | Where-Object { $_.Direction -eq "Inbound" })
        if ($rules | Where-Object { $_.Action -eq "Block" -and $_.Enabled -eq "True" }) { Warn "Windows Firewall has a BLOCK rule for ${exe}: hosting may fail (Windows Security > Firewall > Allow an app, tick dotnet)" }
        elseif ($rules | Where-Object { $_.Action -eq "Allow" }) { Ok "firewall allows the game" }
        else { Info "first time you Play, Windows asks whether the game may use the network: click Allow" }
    } catch { Info "couldn't read firewall rules (fine: Windows asks on first Play; click Allow)" }
}

# ------------------------------------------------------------------ summary
Write-Host ""
if ($script:problems -le 0) {
    Write-Host "All good$(if ($script:fixed) { " ($($script:fixed) thing(s) fixed)" }). Double-click 'Play (Windows)' to start." -ForegroundColor Green
} else {
    Write-Host "$($script:problems) problem(s) above$(if ($script:fixed) { ", $($script:fixed) fixed" }). Fix them (or run 'Setup (Windows)'), then run this check again." -ForegroundColor Red
}
