# Sets up SymphonyRecomp with the online co-op mod and every character (Windows 10/11).
# Started by "Setup (Windows).bat". Run it again any time: it skips what's done.
$ErrorActionPreference = "Stop"

$Here = Split-Path -Parent $MyInvocation.MyCommand.Path   # files\
$Kit = Split-Path -Parent $Here                           # the folder the player unzipped
$Game = Join-Path $Here "SymphonyRecomp"
$Ps1Dir = Join-Path $Kit "1 - PlayStation game"
$PspDir = Join-Path $Kit "2 - PSP game"
$SatDir = Join-Path $Kit "3 - Saturn game"
$CueName = "Castlevania - Symphony of the Night (USA).cue"   # the name the engine's config expects
$pins = @{}
Get-Content (Join-Path $Here "engine\base-commit.txt") | ForEach-Object { $p = $_ -split '\s+'; if ($p.Length -ge 2) { $pins[$p[0]] = $p[1] } }
# the exact releases the characters are built from (SHA-1 of Track 1 / the .iso)
$Ps1Sha = "f967119e006695a59a6442237f9fc7c7811cf7bf"   # US, SLUS-00067
$PspSha = "25bb86c2c86f29e8a9d09ee0ae2a45fc6252befb"   # The Dracula X Chronicles, EU, ULES-00841
$SatSha = "ebf3209692dcc4df8d4448bbcada9ab9266549d0"   # Akumajou Dracula X, JP, T-9527G

function Say($m) { Write-Host ""; Write-Host "==> $m" -ForegroundColor Cyan }
function Die($m) { Write-Host ""; Write-Host "Stopped: $m" -ForegroundColor Red; exit 1 }
function Run($exe) { & $exe @args; if ($LASTEXITCODE -ne 0) { Die "$exe $args failed. Double-click Setup again to retry; if it keeps failing, report it with this window's text." } }

# ---- your three games first (so nothing downloads before they're there)

# A .cue and its .bin files in a folder: the .cue, and the .bin names it asks for, as
# they're really named.
function Read-Cue($dir, $what) {
    $cue = Get-ChildItem -Path $dir -Filter *.cue -Recurse -Depth 1 -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $cue) { Die "Your $what files aren't there yet.`nDrag your own $what .cue and .bin files into the folder`n    $(Split-Path -Leaf $dir)`nthen double-click Setup again." }
    $want = @()
    foreach ($line in Get-Content $cue.FullName) { if ($line -match '^\s*FILE\s+"([^"]+)"') { $want += $Matches[1] } }
    $have = @(Get-ChildItem -Path $cue.DirectoryName -Filter *.bin | Sort-Object Name | ForEach-Object { $_.Name })
    $bins = @()
    for ($i = 0; $i -lt $want.Count; $i++) {
        if (Test-Path (Join-Path $cue.DirectoryName $want[$i])) { $bins += $want[$i] }
        elseif ($have.Count -eq $want.Count) { $bins += $have[$i] }   # renamed dump: match by track order
        else { $bins = @(); break }
    }
    if ($want.Count -eq 0 -or $bins.Count -ne $want.Count) {
        Write-Host "  Your .cue file asks for:"; $want | ForEach-Object { Write-Host "    $_" }
        Write-Host "  but the folder has:"; $have | ForEach-Object { Write-Host "    $_" }
        Die "Put all the .bin files that belong to your $what .cue in '$(Split-Path -Leaf $dir)' (just one game), then double-click Setup again."
    }
    return @{ Cue = $cue; Bins = $bins }
}

# Is this the exact release? (checked once: remembered by file size and date)
function Check-Sha($file, $sha, $what, $exactly) {
    $item = Get-Item $file
    $stamp = Join-Path $Here (".checked-" + ($item.Name -replace '[^A-Za-z0-9.]', '_'))
    $id = "$($item.Length) $($item.LastWriteTimeUtc.Ticks)"
    if ((Test-Path $stamp) -and ((Get-Content $stamp -Raw).Trim() -eq $id)) { return }
    Say "Checking your $what"
    if ((Get-FileHash -Algorithm SHA1 $file).Hash.ToLower() -ne $sha) { Die "$($item.Name) isn't the $what this needs.`nIt must be an unmodified dump of exactly: $exactly" }
    Set-Content -Path $stamp -Value $id
}

$ps1 = Read-Cue $Ps1Dir "PlayStation game"
$ps1T1 = Join-Path $ps1.Cue.DirectoryName $ps1.Bins[0]
$pspIso = Get-ChildItem -Path $PspDir -Filter *.iso -Recurse -Depth 1 -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $pspIso) { Die "Your PSP game isn't there yet.`nDrag your own Castlevania: The Dracula X Chronicles .iso into the folder`n    2 - PSP game`nthen double-click Setup again." }
$sat = Read-Cue $SatDir "Saturn game"
$satT1 = Join-Path $sat.Cue.DirectoryName $sat.Bins[0]

Check-Sha $ps1T1 $Ps1Sha "PlayStation game" "Castlevania: Symphony of the Night, USA (SLUS-00067), Track 1"
Check-Sha $pspIso.FullName $PspSha "PSP game" "Castlevania: The Dracula X Chronicles, Europe (ULES-00841)"
Check-Sha $satT1 $SatSha "Saturn game" "Akumajou Dracula X: Gekka no Yasoukyoku, Japan (T-9527G), Track 1"

# ---- tools
function Refresh-Path { $env:PATH = [Environment]::GetEnvironmentVariable("PATH", "Machine") + ";" + [Environment]::GetEnvironmentVariable("PATH", "User") }
if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        Die "Git is needed. Install it from https://git-scm.com/download/win (all the default options are fine), then double-click Setup again."
    }
    Say "Installing Git (free; Windows may ask for permission: click Yes)"
    winget install --id Git.Git -e --silent --accept-source-agreements --accept-package-agreements
    Refresh-Path
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { $env:PATH += ";C:\Program Files\Git\cmd" }
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Die "Git was installed but this window can't see it yet. Close it and double-click Setup again." }
}

# The game's window, sound and menu libraries (GLFW, OpenAL, cimgui) need Microsoft's
# Visual C++ runtime; without it the game can't open a window or play sound.
if (-not (Test-Path (Join-Path $env:WINDIR "System32\vcruntime140.dll"))) {
    Say "Installing the Microsoft Visual C++ runtime (Windows may ask for permission: click Yes)"
    $vc = Join-Path $env:TEMP "vc_redist.x64.exe"
    Invoke-WebRequest https://aka.ms/vs/17/release/vc_redist.x64.exe -OutFile $vc
    Start-Process -FilePath $vc -ArgumentList "/install", "/quiet", "/norestart" -Verb RunAs -Wait
    if (-not (Test-Path (Join-Path $env:WINDIR "System32\vcruntime140.dll"))) {
        Die "The Visual C++ runtime didn't install. Install it from https://aka.ms/vs/17/release/vc_redist.x64.exe then double-click Setup again."
    }
}

$DotnetDir = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet"
$env:DOTNET_ROOT = $DotnetDir
$env:PATH = "$DotnetDir;$env:PATH"
$haveSdk = $false
try { $haveSdk = [bool]((dotnet --list-sdks 2>$null) -match '^10\.') } catch { }
if (-not $haveSdk) {
    Say "Installing .NET 10 (Microsoft's free runtime for the game, into your user folder)"
    $installer = Join-Path $env:TEMP "dotnet-install.ps1"
    Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile $installer
    & $installer -Channel 10.0 -InstallDir $DotnetDir
}

# ---- the game engine, at the exact version the mod was made for
if (-not (Test-Path (Join-Path $Game ".git"))) {
    Say "Downloading SymphonyRecomp"
    # (LF line endings, so the patch applies exactly)
    Run git -c core.autocrlf=false clone https://github.com/BlackLabelHQ/SymphonyRecomp.git $Game
    Run git -C $Game config core.autocrlf false
}
Set-Location $Game
$applied = $false
if (Test-Path "RecompOne\.git") {
    git -C RecompOne apply --reverse --check (Join-Path $Here "engine\recompone.patch") 2>$null
    $applied = ($LASTEXITCODE -eq 0)
    if ($applied) { git apply --reverse --check (Join-Path $Here "engine\outer.patch") 2>$null; $applied = ($LASTEXITCODE -eq 0) }
}
if (-not $applied) {
    Say "Checking out the engine version the mod was made for"
    Run git fetch -q origin
    # (an older version of the co-op patch may be in: drop it first; untracked files,
    # like your disc and the built game, aren't touched)
    Run git reset -q --hard
    if (Test-Path "RecompOne\.git") { Run git -C RecompOne reset -q --hard }
    Run git checkout -q $pins["SymphonyRecomp"]
    Run git -c core.autocrlf=false submodule update --init --recursive
    Run git -C RecompOne config core.autocrlf false
    Run git -C RecompOne checkout -q $pins["RecompOne"]
    Say "Applying the co-op engine patch"
    Run git apply --ignore-whitespace (Join-Path $Here "engine\outer.patch")
    Run git -C RecompOne apply --ignore-whitespace (Join-Path $Here "engine\recompone.patch")
} else { Say "Engine patch already applied" }

# ---- your PlayStation game into the engine's disc folder (the .cue under the name its config expects)
Say "Copying your PlayStation game"
$discDir = Join-Path $Game "disc"
New-Item -ItemType Directory -Force $discDir | Out-Null
foreach ($b in $ps1.Bins) {
    $src = Join-Path $ps1.Cue.DirectoryName $b; $dst = Join-Path $discDir $b
    if (-not (Test-Path $dst) -or (Get-Item $dst).Length -ne (Get-Item $src).Length) { Copy-Item $src $dst -Force }
}
# (with the .bin names it really has)
$i = 0
$cueText = foreach ($line in Get-Content $ps1.Cue.FullName) {
    if ($line -match '^\s*FILE\s+"[^"]+"\s+(\w+)') { "FILE `"$($ps1.Bins[$i])`" $($Matches[1])"; $i++ } else { $line }
}
Set-Content -Path (Join-Path $discDir $CueName) -Value $cueText
$settings = Join-Path $Game "settings.json"
$cuePath = Join-Path $discDir $CueName
if (Test-Path $settings) {
    $j = Get-Content $settings -Raw | ConvertFrom-Json
    $j | Add-Member -NotePropertyName CdPath -NotePropertyValue $cuePath -Force
    $j | ConvertTo-Json -Depth 20 | Set-Content $settings
} else {
    @{ CdPath = $cuePath } | ConvertTo-Json | Set-Content $settings
}

# ---- the characters, from your PSP and Saturn games: the kit's character builder
# (files\extractor) puts your discs' data into their programs (files\characters\blank,
# built with none), and makes their sprites, effects, sounds and the Saturn select screen
Say "Building the characters from your PSP and Saturn games"
$x = Join-Path $Here "extractor"
Run dotnet build -v q -c Release -o (Join-Path $x "bin\out") (Join-Path $x "extractor.csproj")
Run dotnet (Join-Path $x "bin\out\sotn-extract.dll") characters (Join-Path $Here "decomp-mods\recipes") (Join-Path $Here "characters\blank") $discDir `
    --ps1 $ps1T1 --psp $pspIso.FullName --saturn $satT1
Copy-Item (Join-Path $Here "characters\funcmaps\*.json") (Join-Path $Game "config\funcmaps") -Force

# ---- the mod
Say "Installing the Online Co-op mod"
$modDst = Join-Path $Game "mods\coop-online"
if (Test-Path $modDst) { Remove-Item -Recurse -Force $modDst }
Copy-Item -Recurse (Join-Path $Here "mods\coop-online") $modDst
# (mods\maria was a mod of its own before 2026-10-26: it's part of this one now)
$mariaDst = Join-Path $Game "mods\maria"
if (Test-Path $mariaDst) { Remove-Item -Recurse -Force $mariaDst }
$ini = Join-Path $Game "interface.ini"
$lines = @()
if (Test-Path $ini) { $lines = Get-Content $ini | Where-Object { $_ -notmatch '^mods\.(coop(-online)?|maria)\.enabled=' } }
if ($lines.Count -eq 0 -or $lines[0] -ne "[RecompOne]") { $lines = @("[RecompOne]") + ($lines | Where-Object { $_ -ne "[RecompOne]" }) }
$lines = @($lines[0], "mods.coop-online.enabled=True", "mods.coop.enabled=False") + $lines[1..($lines.Count)]
Set-Content -Path $ini -Value ($lines | Where-Object { $_ -ne $null })

# ---- build (the game's code from your discs: again whenever the characters changed)
$stamp = Join-Path $Here ".recompiled"
$sha = [System.Security.Cryptography.SHA1]::Create()
$bytes = @()
foreach ($f in "config\funcmaps\maria.json", "config\funcmaps\mariasat.json", "config\funcmaps\richter2.json", (Join-Path $Here "engine\base-commit.txt")) { $bytes += [IO.File]::ReadAllBytes((Resolve-Path $f)) }
$wantStamp = -join ($sha.ComputeHash([byte[]]$bytes) | ForEach-Object { $_.ToString("x2") })
if (-not (Test-Path (Join-Path $Game "generated")) -or -not (Test-Path $stamp) -or ((Get-Content $stamp -Raw).Trim() -ne $wantStamp)) {
    Say "Building the game from your discs (takes 5-15 minutes; let it run)"
    Run dotnet run --project RecompOne/RecompOne.Recompiler config/sotn.json
    Set-Content -Path $stamp -Value $wantStamp
}
Say "Building"
Run dotnet build RecompOne/RecompOne.sln -v q
Run dotnet build -v q

Write-Host ""
Write-Host "All set! Double-click 'Play (Windows)' to start." -ForegroundColor Green
