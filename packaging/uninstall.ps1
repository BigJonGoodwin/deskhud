<#
.SYNOPSIS
    Removes deskhud-suite for the current user.

.DESCRIPTION
    Stops the suite's own GlazeWM, YASB and deskhud (deskhud gives the Windows taskbar back), removes the
    \deskhud-suite\ logon tasks, takes its folders off your PATH and deletes
    %LOCALAPPDATA%\Programs\deskhud-suite. Your configs stay where they are unless you pass
    -RestoreBackup, which puts back the files the installer backed up. A YASB installed in Program Files
    is left installed (uninstall it from Settings > Apps if you want).

.PARAMETER WhatIf
    Dry run: prints every step and changes nothing.
.PARAMETER TargetProfile
    Undo a test install made with install.ps1 -TargetProfile <folder>.
.PARAMETER RestoreBackup
    Put back the config files that the last install replaced.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$TargetProfile,
    [switch]$RestoreBackup
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'suite-common.ps1')

$paths = Get-SuitePaths $TargetProfile
Write-Host "deskhud-suite uninstall$(if ($WhatIfPreference) { ' (dry run: nothing is changed)' })"
Write-Host "  profile:  $($paths.Home)$(if ($paths.Fake) { '  (test profile)' })"

$manifest = $null
if (Test-Path $paths.Manifest) { $manifest = Get-Content $paths.Manifest -Raw | ConvertFrom-Json }
else { Write-Warning "No $($paths.Manifest); removing the default tasks and folders only." }

Stop-SuiteProcesses $PSCmdlet $paths

# ---------- logon tasks ----------

$taskList = if ($manifest -and $manifest.tasks) { @($manifest.tasks) } else { $TaskNames | ForEach-Object { "$TaskFolder$_" } }
foreach ($task in $taskList) {
    $name = Split-Path $task -Leaf
    $folder = $task.Substring(0, $task.Length - $name.Length)
    if ($paths.Fake) {
        $file = Join-Path $paths.FakeSystem "task-$name.xml"
        if (Test-Path $file) { Invoke-Step $PSCmdlet "remove task $task ($file)" { Remove-Item $file } }
        continue
    }
    if (Get-ScheduledTask -TaskPath $folder -TaskName $name -ErrorAction SilentlyContinue) {
        Invoke-Step $PSCmdlet "remove task $task" { Unregister-ScheduledTask -TaskPath $folder -TaskName $name -Confirm:$false }
    }
}
if (-not $paths.Fake) {
    $service = New-Object -ComObject Schedule.Service
    $service.Connect()
    try {
        $suiteFolder = $service.GetFolder($TaskFolder.TrimEnd('\'))
        if ($suiteFolder.GetTasks(1).Count -eq 0) {
            Invoke-Step $PSCmdlet "remove the empty task folder $TaskFolder" { $service.GetFolder('\').DeleteFolder($TaskFolder.Trim('\'), 0) }
        }
    } catch { }
}

# ---------- PATH ----------

$added = if ($manifest -and $manifest.path_added) { @($manifest.path_added) } else { @($paths.Bin) }
$pathFile = Join-Path $paths.FakeSystem 'user-path.txt'
$current = if ($paths.Fake) { if (Test-Path $pathFile) { Get-Content $pathFile -Raw } else { '' } } else { [Environment]::GetEnvironmentVariable('Path', 'User') }
$parts = @(($current -split ';') | Where-Object { $_ })
$kept = @($parts | Where-Object { $p = $_.TrimEnd('\'); -not ($added | Where-Object { $_.TrimEnd('\') -ieq $p }) })
if ($kept.Count -ne $parts.Count) {
    Invoke-Step $PSCmdlet "remove $($added -join ', ') from your PATH" {
        if ($paths.Fake) { Set-Content -Path $pathFile -Value ($kept -join ';') -NoNewline }
        else { [Environment]::SetEnvironmentVariable('Path', ($kept -join ';'), 'User') }
    }
}

# ---------- configs ----------

if ($RestoreBackup) {
    if (-not ($manifest -and $manifest.backups)) { Write-Host "  no backed-up configs to restore" }
    foreach ($b in @($manifest.backups)) {
        if (Test-Path $b.backup) { Invoke-Step $PSCmdlet "restore $($b.file) from $($b.backup)" { Copy-Item $b.backup $b.file -Force } }
        else { Write-Warning "Backup $($b.backup) is gone; $($b.file) stays as it is." }
    }
} elseif ($manifest -and $manifest.written) {
    Write-Host "  configs left in place (run with -RestoreBackup to put back the previous ones):"
    $manifest.written | ForEach-Object { Write-Host "    $_" }
}

# ---------- programs ----------

if (Test-Path $paths.Install) {
    Invoke-Step $PSCmdlet "delete $($paths.Install)" { Remove-Item $paths.Install -Recurse -Force }
}
Write-Host "Done.$(if (-not $paths.Fake) { ' Sign out and back in (or restart Explorer) if the taskbar does not come back.' })"
