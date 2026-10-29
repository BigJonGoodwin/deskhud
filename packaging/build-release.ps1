<#
.SYNOPSIS
    Builds the deskhud-suite release zip: dist\deskhud-suite-<version>-x64.zip and its .sha256.

.DESCRIPTION
    Builds deskhud with ..\build.ps1 -Offline (leaves any running deskhud alone), takes GlazeWM from
    ..\..\glazewm\target\release (build the deskhud-stacks fork there first: cargo build --release),
    checks that defaults\yasb matches the YASB fork's themes\deskhud, and zips:

        install.ps1, uninstall.ps1, suite-common.ps1, README.md
        bin\        deskhud.exe, glazewm.exe, glazewm-cli.exe, glazewm-watcher.exe
        defaults\   glazewm, yasb, whkdrc, deskhud\settings.json (from ..\settings.example.json)
        licenses\   deskhud and GlazeWM (GPL-3.0), YASB (MIT)

.PARAMETER Version
    Release name; defaults to `git describe` of the deskhud repo.
.PARAMETER NoBuild
    Use the deskhud already built in ..\bin\offline.
.PARAMETER SyncTheme
    Copy the YASB fork's themes\deskhud into defaults\yasb first, instead of only warning when they differ.
#>
param(
    [string]$Version,
    [switch]$SyncTheme,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$repo = Split-Path $here
$suite = Split-Path $repo
$glazeDir = Join-Path $suite 'glazewm\target\release'
$themeDir = Join-Path $suite 'yasb\themes\deskhud'

if (-not $Version) { $Version = (& git -C $repo describe --tags --always --dirty).Trim() }
$name = "deskhud-suite-$Version-x64"
$dist = Join-Path $here 'dist'
$stage = Join-Path $dist $name
if (Test-Path $stage) { throw "$stage already exists; remove it or pass another -Version." }

# The theme lives in the YASB fork; defaults\yasb is the copy the installer uses.
if (Test-Path $themeDir) {
    foreach ($f in 'config.yaml', 'styles.css', 'deskhud_colors.css') {
        $theirs = Join-Path $themeDir $f
        $ours = Join-Path $here "defaults\yasb\$f"
        if ((Get-FileHash $theirs).Hash -ne (Get-FileHash $ours).Hash) {
            if ($SyncTheme) { Copy-Item $theirs $ours -Force; "synced defaults\yasb\$f from the YASB fork" }
            else { Write-Warning "defaults\yasb\$f differs from $theirs (rerun with -SyncTheme to take the fork's)." }
        }
    }
} else { Write-Warning "No YASB fork at $themeDir; using defaults\yasb as it is." }

if (-not $NoBuild) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'build.ps1') -Offline
    if ($LASTEXITCODE -ne 0) { throw "deskhud build failed" }
}
if (-not (Test-Path (Join-Path $glazeDir 'glazewm.exe'))) { throw "No GlazeWM build in $glazeDir (cargo build --release in the glazewm fork)." }

New-Item -ItemType Directory -Force (Join-Path $stage 'bin'), (Join-Path $stage 'licenses') | Out-Null
Copy-Item (Join-Path $here 'install.ps1'), (Join-Path $here 'uninstall.ps1'), (Join-Path $here 'suite-common.ps1'), (Join-Path $here 'README.md') $stage
Copy-Item (Join-Path $here 'defaults') $stage -Recurse
New-Item -ItemType Directory -Force (Join-Path $stage 'defaults\deskhud') | Out-Null
Copy-Item (Join-Path $repo 'settings.example.json') (Join-Path $stage 'defaults\deskhud\settings.json')

Copy-Item (Join-Path $repo 'bin\offline\deskhud.exe') (Join-Path $stage 'bin')
foreach ($exe in 'glazewm.exe', 'glazewm-cli.exe', 'glazewm-watcher.exe') {
    $from = Join-Path $glazeDir $exe
    if (Test-Path $from) { Copy-Item $from (Join-Path $stage 'bin') }
    elseif ($exe -eq 'glazewm.exe') { throw "missing $from" }
    else { Write-Warning "no $exe in $glazeDir" }
}

Copy-Item (Join-Path $repo 'LICENSE') (Join-Path $stage 'licenses\deskhud-LICENSE.txt')
$glazeLicense = Join-Path $suite 'glazewm\LICENSE.md'
if (Test-Path $glazeLicense) { Copy-Item $glazeLicense (Join-Path $stage 'licenses\glazewm-LICENSE.md') } else { Write-Warning "no GlazeWM license at $glazeLicense" }
$yasbLicense = Join-Path $suite 'yasb\LICENSE'
if (Test-Path $yasbLicense) { Copy-Item $yasbLicense (Join-Path $stage 'licenses\yasb-LICENSE.txt') } else { Write-Warning "no YASB license at $yasbLicense" }

$zip = Join-Path $dist "$name.zip"
Compress-Archive -Path $stage -DestinationPath $zip -Force
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$zip.sha256" -Value "$hash  $name.zip" -Encoding ASCII
"built $zip"
"sha256 $hash"
