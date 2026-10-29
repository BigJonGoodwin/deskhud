<#
.SYNOPSIS
    Installs deskhud-suite for the current user: GlazeWM (deskhud-stacks fork), YASB with the deskhud
    theme, and deskhud. No admin rights needed.

.DESCRIPTION
    - Programs go to %LOCALAPPDATA%\Programs\deskhud-suite\bin (added to your PATH). YASB is used from
      Program Files when it is installed there; otherwise the pinned YASB MSI is downloaded, checked
      against its SHA-256 and unpacked (msiexec /a, no admin) to %LOCALAPPDATA%\Programs\deskhud-suite\yasb.
    - Configs: ~/.glzr/glazewm/config.yaml, ~/.config/yasb/ (config.yaml, styles.css,
      deskhud_colors.css), ~/.config/deskhud/settings.json and ~/.config/whkdrc. A file already there
      is copied to ~/.deskhud-suite-backups/<time>/ first.
    - Three logon tasks in Task Scheduler \deskhud-suite\ (GlazeWM, YASB, deskhud), not elevated.
    - Then starts them, unless komorebi or another deskhud is running.

    Run from a release zip (bin\ next to this script) or from the deskhud repo, where it builds deskhud
    with ..\build.ps1 -Offline and takes GlazeWM from ..\..\glazewm\target\release.

.PARAMETER WhatIf
    Dry run: prints every step and changes nothing.
.PARAMETER TargetProfile
    Test install: treats this folder as your user profile (configs, programs, backups all go under it),
    writes the logon tasks as XML files and the PATH change as a text file under
    <folder>\AppData\Local\Programs\deskhud-suite\fake-system instead of registering them, and starts nothing.
.PARAMETER NoKeyboardHook
    deskhud runs no hotkeys (setting keyboard.hook = false); GlazeWM gets Alt-based keybindings instead.
.PARAMETER KeepConfigs
    Leave config files that already exist untouched (only missing ones are written).
.PARAMETER NoStart
    Install and register the tasks, but start nothing (they start at the next sign-in).
.PARAMETER NoBuild
    Repo layout only: use the deskhud already built in ..\bin\offline instead of building it.
.PARAMETER GlazeWmZip
    Path or https URL of a zip holding glazewm.exe (and glazewm-cli.exe, glazewm-watcher.exe), used
    when this folder has no GlazeWM build. Releases of the fork: https://github.com/BigJonGoodwin/glazewm/releases
.PARAMETER YasbMsi
    A YASB .msi you already have, instead of downloading the pinned one.
.PARAMETER SkipYasb
    Do not look for or install YASB (no YASB task is created).
.PARAMETER AllowYasbHashMismatch
    Accept a downloaded YASB MSI whose SHA-256 differs from the pinned one (the preview build is
    replaced in place when YASB publishes a new one).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File install.ps1 -WhatIf
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File install.ps1 -TargetProfile $env:TEMP\fake-profile
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$TargetProfile,
    [switch]$NoKeyboardHook,
    [switch]$KeepConfigs,
    [switch]$NoStart,
    [switch]$NoBuild,
    [string]$GlazeWmZip,
    [string]$YasbMsi,
    [switch]$SkipYasb,
    [switch]$AllowYasbHashMismatch
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'suite-common.ps1')

$paths = Get-SuitePaths $TargetProfile
$here = $PSScriptRoot
$isRelease = Test-Path (Join-Path $here 'bin\deskhud.exe')
$defaults = Join-Path $here 'defaults'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupDir = Join-Path $paths.Home ".deskhud-suite-backups\$stamp"
$written = New-Object System.Collections.ArrayList
$backedUp = New-Object System.Collections.ArrayList
$pathAdded = New-Object System.Collections.ArrayList
$tasks = New-Object System.Collections.ArrayList

Write-Host "deskhud-suite install$(if ($WhatIfPreference) { ' (dry run: nothing is changed)' })"
Write-Host "  profile:  $($paths.Home)$(if ($paths.Fake) { '  (test profile)' })"
Write-Host "  programs: $($paths.Install)"

# ---------- where the programs come from ----------

function Find-Sources {
    $src = @{}
    if ($isRelease) {
        $src.Deskhud = Join-Path $here 'bin\deskhud.exe'
        $src.GlazeDir = Join-Path $here 'bin'
        $src.Settings = Join-Path $defaults 'deskhud\settings.json'
        return $src
    }
    # Repo layout: packaging\ inside the deskhud repo, glazewm\ next to the repo.
    $repo = Split-Path $here
    $src.Deskhud = Join-Path $repo 'bin\offline\deskhud.exe'
    $src.Build = Join-Path $repo 'build.ps1'
    $src.GlazeDir = [IO.Path]::GetFullPath((Join-Path $repo '..\glazewm\target\release'))
    $src.Settings = Join-Path $repo 'settings.example.json'
    $src
}
$src = Find-Sources

if ($src.Build) {
    if ($NoBuild) {
        if (-not (Test-Path $src.Deskhud)) { throw "-NoBuild, but there is no $($src.Deskhud) yet" }
        Write-Host "  using the existing build $($src.Deskhud)"
    } elseif ($WhatIfPreference) {
        if (Test-Path $src.Deskhud) { Write-Host "  using the existing build $($src.Deskhud)" }
        else { Write-Host "What if: build deskhud with $($src.Build) -Offline" }
    } else {
        Write-Host "  building deskhud ($($src.Build) -Offline)"
        & powershell -NoProfile -ExecutionPolicy Bypass -File $src.Build -Offline | Write-Host
        if ($LASTEXITCODE -ne 0) { throw "deskhud build failed" }
    }
}

$glazeExes = @('glazewm.exe', 'glazewm-cli.exe', 'glazewm-watcher.exe')
if (-not (Test-Path (Join-Path $src.GlazeDir 'glazewm.exe'))) {
    if (-not $GlazeWmZip) {
        throw "No GlazeWM build in $($src.GlazeDir). Build the fork (cargo build --release in glazewm\) or pass -GlazeWmZip <zip or URL>."
    }
    $zip = $GlazeWmZip
    $unpacked = Join-Path $paths.Downloads "glazewm-$stamp"
    if ($GlazeWmZip -match '^https://') {
        $zip = Join-Path $paths.Downloads 'glazewm.zip'
        Invoke-Step $PSCmdlet "download $GlazeWmZip" {
            New-Item -ItemType Directory -Force $paths.Downloads | Out-Null
            Invoke-WebRequest -UseBasicParsing $GlazeWmZip -OutFile $zip
        }
    }
    Invoke-Step $PSCmdlet "unpack $zip" { Expand-Archive $zip -DestinationPath $unpacked }
    $src.GlazeDir = $unpacked
    if (-not $WhatIfPreference) {
        $found = Get-ChildItem $unpacked -Recurse -Filter glazewm.exe | Select-Object -First 1
        if (-not $found) { throw "$zip holds no glazewm.exe" }
        $src.GlazeDir = $found.DirectoryName
    }
}

# ---------- programs ----------

Write-Host "Programs"
Stop-SuiteProcesses $PSCmdlet $paths
Invoke-Step $PSCmdlet "create $($paths.Bin)" { New-Item -ItemType Directory -Force $paths.Bin | Out-Null }
Invoke-Step $PSCmdlet "copy deskhud.exe to $($paths.Bin)" { Copy-Item $src.Deskhud $paths.Bin -Force }
foreach ($exe in $glazeExes) {
    $from = Join-Path $src.GlazeDir $exe
    if (Test-Path $from) {
        Invoke-Step $PSCmdlet "copy $exe from $($src.GlazeDir)" { Copy-Item $from $paths.Bin -Force }
    }
}

function Find-Yasb {
    $candidates = @(
        (Join-Path $env:ProgramFiles 'YASB\yasb.exe'),
        (Join-Path $paths.Local 'Programs\YASB\yasb.exe')
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    if (Test-Path $paths.Yasb) {
        $own = Get-ChildItem $paths.Yasb -Recurse -Filter yasb.exe -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($own) { return $own.FullName }
    }
    $null
}

$yasbExe = $null
if (-not $SkipYasb) {
    $yasbExe = Find-Yasb
    if ($yasbExe) {
        Write-Host "  YASB found: $yasbExe (version $((Get-Item $yasbExe).VersionInfo.ProductVersion)); the theme needs the preview build or 2.0.8+"
    } else {
        $arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
        $pin = $YasbRelease[$arch]
        $msi = $YasbMsi
        if (-not $msi) {
            $msi = Join-Path $paths.Downloads (Split-Path $pin.Url -Leaf)
            Invoke-Step $PSCmdlet "download YASB from $($pin.Url)" {
                New-Item -ItemType Directory -Force $paths.Downloads | Out-Null
                Invoke-WebRequest -UseBasicParsing $pin.Url -OutFile $msi
                $hash = (Get-FileHash $msi -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($hash -ne $pin.Sha256) {
                    $msg = "YASB MSI SHA-256 is $hash, expected $($pin.Sha256)."
                    if (-not $AllowYasbHashMismatch) { throw "$msg The preview build may have been replaced; check it and rerun with -AllowYasbHashMismatch." }
                    Write-Warning $msg
                }
            }
        }
        # An administrative install only unpacks the MSI's files, so it needs no admin rights.
        Invoke-Step $PSCmdlet "unpack YASB ($msi) to $($paths.Yasb)" {
            New-Item -ItemType Directory -Force $paths.Yasb | Out-Null
            $p = Start-Process msiexec.exe -Wait -PassThru -ArgumentList @('/a', "`"$msi`"", '/qn', "TARGETDIR=`"$($paths.Yasb)`"")
            if ($p.ExitCode -ne 0) { throw "msiexec /a failed with exit code $($p.ExitCode)" }
        }
        $yasbExe = Find-Yasb
        if (-not $yasbExe) {
            if (-not $WhatIfPreference) { throw "YASB unpacked, but no yasb.exe under $($paths.Yasb)" }
            $yasbExe = Join-Path $paths.Yasb 'PFiles64\YASB\yasb.exe'
        }
    }
}

# ---------- PATH ----------

function Add-UserPath([string]$dir) {
    $current = [Environment]::GetEnvironmentVariable('Path', 'User')
    if ($paths.Fake) {
        $file = Join-Path $paths.FakeSystem 'user-path.txt'
        if (Test-Path $file) { $current = Get-Content $file -Raw }
    }
    $parts = @(($current -split ';') | Where-Object { $_ })
    if ($parts | Where-Object { $_.TrimEnd('\') -ieq $dir }) { return }
    Invoke-Step $PSCmdlet "add $dir to your PATH" {
        $new = (@($parts) + $dir) -join ';'
        if ($paths.Fake) {
            New-Item -ItemType Directory -Force $paths.FakeSystem | Out-Null
            Set-Content -Path (Join-Path $paths.FakeSystem 'user-path.txt') -Value $new -NoNewline
        } else {
            [Environment]::SetEnvironmentVariable('Path', $new, 'User')
        }
        [void]$pathAdded.Add($dir)
    }
}
Add-UserPath $paths.Bin
# deskhud calls yasbc.exe by name; an unpacked YASB is not on the PATH otherwise.
if ($yasbExe -and $yasbExe.StartsWith($paths.Yasb, 'OrdinalIgnoreCase')) { Add-UserPath (Split-Path $yasbExe) }

# ---------- configs ----------

Write-Host "Configs"
<# Writes $Content to $Target, first copying an existing, different file into the backup folder. #>
function Write-Config([string]$Target, [string]$Content, [switch]$OnlyIfMissing) {
    $exists = Test-Path $Target
    if ($exists -and ($OnlyIfMissing -or $KeepConfigs)) { Write-Host "  keep $Target"; return }
    if ($exists -and ((Get-Content $Target -Raw -Encoding UTF8) -ceq $Content)) { Write-Host "  unchanged $Target"; return }
    $rel = $Target.Substring($paths.Home.Length).TrimStart('\')
    if ($exists) {
        $backup = Join-Path $backupDir $rel
        Invoke-Step $PSCmdlet "back up $Target to $backup" {
            New-Item -ItemType Directory -Force (Split-Path $backup) | Out-Null
            Copy-Item $Target $backup
            [void]$backedUp.Add(@{ file = $Target; backup = $backup })
        }
    }
    Invoke-Step $PSCmdlet "write $Target" {
        New-Item -ItemType Directory -Force (Split-Path $Target) | Out-Null
        [IO.File]::WriteAllText($Target, $Content, (New-Object Text.UTF8Encoding $false))
        [void]$written.Add($Target)
    }
}
function Read-Default([string]$path) { [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8) }

$glazeConfig = Read-Default (Join-Path $defaults 'glazewm\config.yaml')
if ($NoKeyboardHook) {
    $alt = Read-Default (Join-Path $defaults 'glazewm\keybindings-alt.yaml')
    $glazeConfig = $glazeConfig -replace '(?m)^# Empty while deskhud runs the hotkeys.*\r?\nkeybindings: \[\]\r?$', $alt.TrimEnd()
}
Write-Config (Join-Path $paths.Home '.glzr\glazewm\config.yaml') $glazeConfig

$yasbDir = Join-Path $paths.Home '.config\yasb'
Write-Config (Join-Path $yasbDir 'config.yaml') (Read-Default (Join-Path $defaults 'yasb\config.yaml'))
Write-Config (Join-Path $yasbDir 'styles.css') (Read-Default (Join-Path $defaults 'yasb\styles.css'))
# deskhud rewrites the palette from the wallpaper; one already there is as good as the default.
Write-Config (Join-Path $yasbDir 'deskhud_colors.css') (Read-Default (Join-Path $defaults 'yasb\deskhud_colors.css')) -OnlyIfMissing

# settings.json: the example, pointed at the installed GlazeWM and with the chosen keyboard hook.
$settings = Read-Default $src.Settings
$glazeJson = (Join-Path $paths.Bin 'glazewm.exe').Replace('\', '\\')
$settings = $settings -replace '"glazewm_exe"\s*:\s*"[^"]*"', ('"glazewm_exe": "' + $glazeJson.Replace('$', '$$') + '"')
$hook = if ($NoKeyboardHook) { 'false' } else { 'true' }
$settings = $settings -replace '"hook"\s*:\s*(true|false)', ('"hook": ' + $hook)
Write-Config (Join-Path $paths.Home '.config\deskhud\settings.json') $settings

Write-Config (Join-Path $paths.Home '.config\whkdrc') (Read-Default (Join-Path $defaults 'whkdrc'))

# ---------- logon tasks ----------

Write-Host "Logon tasks ($TaskFolder)"
$taskDefs = @(
    @{ Name = 'GlazeWM'; Exe = (Join-Path $paths.Bin 'glazewm.exe'); Args = 'start'; Delay = 'PT0S'
       Description = 'deskhud-suite: GlazeWM tiling window manager' },
    @{ Name = 'YASB'; Exe = $yasbExe; Args = ''; Delay = 'PT2S'
       Description = 'deskhud-suite: YASB status bar' },
    @{ Name = 'deskhud'; Exe = (Join-Path $paths.Bin 'deskhud.exe'); Args = ''; Delay = 'PT3S'
       Description = 'deskhud-suite: bar island, hotkeys, wallpaper colours' }
)
foreach ($t in $taskDefs) {
    if (-not $t.Exe) { Write-Host "  skip $($t.Name) (not installed)"; continue }
    $xml = New-LogonTaskXml $t.Exe $t.Args $t.Delay $t.Description
    if ($paths.Fake) {
        $file = Join-Path $paths.FakeSystem "task-$($t.Name).xml"
        Invoke-Step $PSCmdlet "write task $TaskFolder$($t.Name) to $file" {
            New-Item -ItemType Directory -Force $paths.FakeSystem | Out-Null
            [IO.File]::WriteAllText($file, $xml, [Text.Encoding]::Unicode)
            [void]$tasks.Add("$TaskFolder$($t.Name)")
        }
        continue
    }
    Invoke-Step $PSCmdlet "register task $TaskFolder$($t.Name) -> $($t.Exe) $($t.Args)" {
        try {
            Register-ScheduledTask -TaskPath $TaskFolder -TaskName $t.Name -Xml $xml -Force | Out-Null
            [void]$tasks.Add("$TaskFolder$($t.Name)")
        } catch {
            # Some Windows setups do not let a standard user create a task folder; use the root then.
            $name = "deskhud-suite $($t.Name)"
            Write-Warning "Could not create $TaskFolder$($t.Name) ($($_.Exception.Message)); registering \$name instead."
            Register-ScheduledTask -TaskPath '\' -TaskName $name -Xml $xml -Force | Out-Null
            [void]$tasks.Add("\$name")
        }
    }
}

# A komorebi setup that also starts at logon would fight GlazeWM for the windows.
if (-not $paths.Fake) {
    $old = @(Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object {
        $_.TaskPath -ne $TaskFolder -and ($_.Actions | Where-Object { $_.Execute -match 'komorebi' })
    })
    foreach ($o in $old) {
        Write-Warning "Logon task $($o.TaskPath)$($o.TaskName) starts komorebi. Disable it so komorebi and GlazeWM do not both run (this installer leaves it alone)."
    }
}

# ---------- manifest ----------

# A reinstall keeps the first install's backups (the configs from before deskhud-suite), so
# uninstall -RestoreBackup puts back those, not the suite's own earlier defaults.
if (Test-Path $paths.Manifest) {
    $previous = Get-Content $paths.Manifest -Raw | ConvertFrom-Json
    $mine = @($backedUp)
    $backedUp.Clear()
    foreach ($b in @($previous.backups)) { if ($b) { [void]$backedUp.Add(@{ file = $b.file; backup = $b.backup }) } }
    foreach ($b in $mine) { if (-not ($backedUp | Where-Object { $_.file -ieq $b.file })) { [void]$backedUp.Add($b) } }
    foreach ($w in @($previous.written)) { if ($w -and -not ($written -icontains $w)) { [void]$written.Add($w) } }
    foreach ($p in @($previous.path_added)) { if ($p -and -not ($pathAdded -icontains $p)) { [void]$pathAdded.Add($p) } }
}

$manifest = [ordered]@{
    installed_at = (Get-Date).ToString('s')
    install_dir  = $paths.Install
    yasb_exe     = $yasbExe
    path_added   = @($pathAdded)
    tasks        = @($tasks)
    written      = @($written)
    backups      = @($backedUp)
}
Invoke-Step $PSCmdlet "write $($paths.Manifest)" {
    New-Item -ItemType Directory -Force $paths.Install | Out-Null
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -Path $paths.Manifest -Encoding UTF8
}

# ---------- start ----------

if ($paths.Fake -or $NoStart -or $WhatIfPreference) {
    Write-Host "Not starting anything$(if ($paths.Fake) { ' (test profile)' }). The tasks run at your next sign-in."
    return
}
Write-Host "Starting"
$komorebi = Get-Process komorebi -ErrorAction SilentlyContinue
$otherDeskhud = Get-Process deskhud -ErrorAction SilentlyContinue | Where-Object { $_.Path -and -not $_.Path.StartsWith($paths.Bin + '\', 'OrdinalIgnoreCase') }
foreach ($task in $tasks) {
    $name = Split-Path $task -Leaf
    if ($komorebi -and $name -match 'GlazeWM') { Write-Warning "komorebi is running: not starting GlazeWM. Stop komorebi, then run the $task task."; continue }
    if ($otherDeskhud -and $name -match 'deskhud$') { Write-Warning "Another deskhud is running ($($otherDeskhud[0].Path)): not starting this one."; continue }
    if ($name -match 'YASB' -and (Get-Process yasb -ErrorAction SilentlyContinue)) { Write-Host "  YASB is already running; it picks up the new config by itself"; continue }
    $folder = $task.Substring(0, $task.Length - $name.Length)
    Invoke-Step $PSCmdlet "start $task" { Start-ScheduledTask -TaskPath $folder -TaskName $name }
}
Write-Host "Done. Hotkeys: see README.md, or Win+Shift+/ on the bar."
