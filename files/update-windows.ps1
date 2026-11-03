# Checks for a newer version of the kit and, if you say yes, updates it (run by
# play-windows.ps1 before the game starts). Never touches your games, the built game,
# your saves or your settings. The kit you had is kept in "files.prev" (one update back).
#   update.txt (on GitHub): version=, kind= (quick | rebuild), summary=, zip=, sha256=
# Offline, or GitHub not answering: nothing happens and the game starts.
# Exit code: 0 = go on and play, 1 = stop (Setup needs something first).
$ErrorActionPreference = "Stop"
$Here = Split-Path -Parent $MyInvocation.MyCommand.Path   # files\
$Kit = Split-Path -Parent $Here
$Url = if ($env:COOP_UPDATE_URL) { $env:COOP_UPDATE_URL } else { "https://raw.githubusercontent.com/jweyden/sotn-online-coop/main/update.txt" }
$Note = Join-Path $Here "update-available.txt"   # (the game's Netplay screen shows it)

$verFile = Join-Path $Here "version.txt"
if (-not (Test-Path $verFile) -or $env:COOP_NO_UPDATE -eq "1") { exit 0 }
$have = (Get-Content $verFile -Raw).Trim()
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $info = (Invoke-WebRequest -UseBasicParsing -TimeoutSec 5 $Url).Content
} catch { exit 0 }
$f = @{}
foreach ($line in ($info -split "`n")) { $k, $v = $line.TrimEnd("`r") -split "=", 2; if ($v -ne $null) { $f[$k] = $v } }
$new = $f["version"]; $kind = $f["kind"]; $summary = $f["summary"]

function Newer($a, $b) {
    $x = $a -split '\.'; $y = $b -split '\.'
    for ($i = 0; $i -lt 4; $i++) {
        $p = 0; $q = 0
        if ($i -lt $x.Count -and -not [int]::TryParse($x[$i], [ref]$p)) { return $false }
        if ($i -lt $y.Count -and -not [int]::TryParse($y[$i], [ref]$q)) { return $false }
        if ($p -gt $q) { return $true }; if ($p -lt $q) { return $false }
    }
    return $false
}
if (-not $new -or -not $f["zip"] -or -not $f["sha256"] -or -not (Newer $new $have)) { Remove-Item $Note -ErrorAction SilentlyContinue; exit 0 }
function Later { Set-Content -Path $Note -Value @($new, $summary); Write-Host "  Later, then. (Everyone in a game needs the same version.)"; exit 0 }

Write-Host ""
Write-Host "  A new version is out: v$new (you have v$have)" -ForegroundColor Cyan
if ($summary) { Write-Host "  $summary" }
if ($kind -eq "rebuild") {
    Write-Host "  This one rebuilds the game from your discs: 5-15 minutes."
    $a = Read-Host "  Update now? [y/N]"
    if ($a -notmatch '^[Yy]') { Later }
} else {
    Write-Host "  It takes about a minute."
    $a = Read-Host "  Update now? [Y/n]"
    if ($a -ne "" -and $a -notmatch '^[Yy]') { Later }
}

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("sotn-update-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $tmp | Out-Null
function Fail($why) {
    Write-Host "  The update didn't work: $why" -ForegroundColor Yellow
    Write-Host "  Starting the game as it is. (Try again next time.)"
    Set-Content -Path $Note -Value @($new, $summary)
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
    exit 0
}
try {
    Write-Host "  Downloading v$new..."
    $zip = Join-Path $tmp "kit.zip"
    try { $ProgressPreference = "SilentlyContinue"; Invoke-WebRequest -UseBasicParsing -TimeoutSec 600 $f["zip"] -OutFile $zip } catch { Fail "couldn't download it" }
    if ((Get-FileHash -Algorithm SHA256 $zip).Hash.ToLower() -ne $f["sha256"].ToLower()) { Fail "the download is damaged" }
    try { Expand-Archive -Path $zip -DestinationPath (Join-Path $tmp "new") -Force } catch { Fail "couldn't unpack it" }
    $newFiles = Get-ChildItem -Path (Join-Path $tmp "new") -Directory -Recurse -Depth 1 | Where-Object { $_.Name -eq "files" } | Select-Object -First 1
    if (-not $newFiles -or -not (Test-Path (Join-Path $newFiles.FullName "version.txt"))) { Fail "it isn't a kit" }
    $newRoot = Split-Path -Parent $newFiles.FullName

    # the kit's own files: the old ones go to files.prev, the new ones in
    $prev = Join-Path $Kit "files.prev"
    if (Test-Path $prev) { Remove-Item -Recurse -Force $prev }
    New-Item -ItemType Directory -Force (Join-Path $prev "files") | Out-Null
    foreach ($item in Get-ChildItem -Force $newFiles.FullName) {
        $dst = Join-Path $Here $item.Name
        if (Test-Path $dst) { Move-Item $dst (Join-Path $prev "files") }
        Move-Item $item.FullName $dst
    }
    # the launchers and the READ ME (files, not the game folders; a launcher that's
    # the same isn't touched: the one running now is read from disk as it goes)
    foreach ($item in Get-ChildItem -File $newRoot) {
        $dst = Join-Path $Kit $item.Name
        if (Test-Path $dst) {
            if ((Get-FileHash $dst).Hash -eq (Get-FileHash $item.FullName).Hash) { continue }
            Move-Item $dst $prev
        }
        Move-Item $item.FullName $dst
    }
} finally { Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue }
Remove-Item $Note -ErrorAction SilentlyContinue

Write-Host "  Updated to v$new. Setting it up..." -ForegroundColor Cyan
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Here "setup-windows.ps1")
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "  Setup stopped (see above). Do what it says, then double-click Setup."
    Write-Host "  (Your previous version is in files.prev.)"
    exit 1
}
exit 0
