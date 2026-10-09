# STRAFTAT x TF2 Rockets installer.
# Finds STRAFTAT through Steam, installs BepInEx 5.4.23.5 (downloaded from BepInEx's official GitHub release and
# checked against its SHA-256), then copies the mod from this folder into STRAFTAT. Nothing from STRAFTAT or Team
# Fortress 2 is downloaded: the mod reads the TF2 models and sounds from your own TF2 install when the game starts.
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$BepInExUrl = 'https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip'
$BepInExSha256 = '82F9878551030F54657792C0740D9D51A09500EEAE1FBA21106B0C441E6732C4'
$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
$ModFolder = Join-Path $Here 'BepInEx\plugins\StraftatRockets'

function Get-SteamLibraries {
    $roots = @()
    foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam') {
        $item = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
        if ($item.SteamPath) { $roots += ($item.SteamPath -replace '/', '\') }
        if ($item.InstallPath) { $roots += $item.InstallPath }
    }
    $libs = @()
    foreach ($root in ($roots | Select-Object -Unique)) {
        $libs += $root
        $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) {
                $libs += ($m.Groups[1].Value -replace '\\\\', '\')
            }
        }
    }
    $libs | Select-Object -Unique
}

function Find-Game([string]$folder, [string]$marker) {
    foreach ($lib in Get-SteamLibraries) {
        $dir = Join-Path $lib "steamapps\common\$folder"
        if (Test-Path (Join-Path $dir $marker)) { return $dir }
    }
    return $null
}

Write-Host 'STRAFTAT x TF2 Rockets installer' -ForegroundColor Cyan
if (-not (Test-Path (Join-Path $ModFolder 'StraftatRockets.dll'))) {
    throw "StraftatRockets.dll was not found next to this script ($ModFolder). Unzip the whole release first."
}

$straftat = Find-Game 'STRAFTAT' 'STRAFTAT.exe'
if (-not $straftat) {
    $straftat = Read-Host 'STRAFTAT was not found through Steam. Paste the folder that contains STRAFTAT.exe'
    if (-not (Test-Path (Join-Path $straftat 'STRAFTAT.exe'))) { throw "STRAFTAT.exe is not in $straftat" }
}
Write-Host "STRAFTAT: $straftat"

if (Get-Process -Name 'STRAFTAT' -ErrorAction SilentlyContinue) {
    Read-Host 'STRAFTAT is running. Close it, then press Enter to continue' | Out-Null
}

# BepInEx (the mod loader). An existing BepInEx keeps its config and other plugins.
$bepCore = Join-Path $straftat 'BepInEx\core\BepInEx.dll'
if (Test-Path $bepCore) {
    Write-Host 'BepInEx is already installed.'
} else {
    $zip = Join-Path ([IO.Path]::GetTempPath()) 'BepInEx_win_x64_5.4.23.5.zip'
    Write-Host 'Downloading BepInEx 5.4.23.5 from github.com/BepInEx ...'
    Invoke-WebRequest -Uri $BepInExUrl -OutFile $zip -UseBasicParsing
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash
    if ($hash -ne $BepInExSha256) { Remove-Item $zip; throw "BepInEx download failed its checksum ($hash)." }
    Expand-Archive -Path $zip -DestinationPath $straftat -Force
    Remove-Item $zip
    Write-Host 'BepInEx installed.'
}

# The mod
$target = Join-Path $straftat 'BepInEx\plugins\StraftatRockets'
New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item -Path (Join-Path $ModFolder '*') -Destination $target -Recurse -Force
Write-Host "STRAFTAT x TF2 Rockets installed to $target" -ForegroundColor Green

$tf2 = Find-Game 'Team Fortress 2' 'tf\tf2_misc_dir.vpk'
if ($tf2) {
    Write-Host "Team Fortress 2 found at ${tf2}: the TF2 launchers, rockets and sounds will load from it."
} else {
    Write-Host 'Team Fortress 2 was not found. The mod still plays, but with STRAFTAT''s launcher look.' -ForegroundColor Yellow
    Write-Host 'Install TF2 (free on Steam) for The Original and the other TF2 launchers, rockets and sounds.' -ForegroundColor Yellow
}
Write-Host 'Start STRAFTAT from Steam. In game, press F8 to open the launcher look and viewmodel panel.'
