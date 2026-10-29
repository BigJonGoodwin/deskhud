# Shared by install.ps1 and uninstall.ps1: where deskhud-suite lives, the logon tasks, and stopping
# the suite's own processes. Dot-source it; it defines functions and nothing runs.

# Pinned YASB: the preview build of 2026-10-25. The deskhud theme needs YASB's adaptive bar style and
# audio visualizer, which the stable 2.0.7 release lacks. Switch to the 2.0.8 release once it is out
# (https://github.com/amnweb/yasb/releases) and update the hashes from its checksums.txt.
$script:YasbRelease = @{
    x64   = @{ Url = 'https://github.com/amnweb/yasb/releases/download/preview/yasb-preview-x64.msi'
               Sha256 = '9b6c61afe5b70abc357f7233ca80623d1e84444e1ff2509ded44feb1e85c12f9' }
    arm64 = @{ Url = 'https://github.com/amnweb/yasb/releases/download/preview/yasb-preview-aarch64.msi'
               Sha256 = '4a9f8494bc31856375976157af4620a5f625e79dbd334dcd7f3cf7872bb99f70' }
}

$script:TaskFolder = '\deskhud-suite\'
$script:TaskNames = @('GlazeWM', 'YASB', 'deskhud')

<# The folders the suite uses. With $TargetProfile (tests) everything goes under that folder as if it
   were the user profile, and nothing outside it is touched. #>
function Get-SuitePaths([string]$TargetProfile) {
    $fake = $false
    $homeDir = $env:USERPROFILE
    $localDir = $env:LOCALAPPDATA
    if ($TargetProfile) {
        $full = [IO.Path]::GetFullPath($TargetProfile).TrimEnd('\')
        if ($full -ine $env:USERPROFILE.TrimEnd('\')) {
            $fake = $true
            $homeDir = $full
            $localDir = Join-Path $full 'AppData\Local'
        }
    }
    $install = Join-Path $localDir 'Programs\deskhud-suite'
    [pscustomobject]@{
        Fake       = $fake
        Home       = $homeDir
        Local      = $localDir
        Install    = $install
        Bin        = Join-Path $install 'bin'
        Yasb       = Join-Path $install 'yasb'
        Downloads  = Join-Path $install 'downloads'
        Manifest   = Join-Path $install 'install.json'
        # Stand-in for Task Scheduler and the user PATH in a fake profile.
        FakeSystem = Join-Path $install 'fake-system'
    }
}

<# Runs $Action unless -WhatIf is on, in which case it only says what it would do. #>
function Invoke-Step($Cmdlet, [string]$What, [scriptblock]$Action) {
    if ($Cmdlet.ShouldProcess($What)) {
        Write-Host "  $What"
        & $Action
    }
}

<# The suite's processes: those running from its own bin or yasb folder. Anything else (another copy of
   deskhud, a YASB from Program Files) belongs to someone else and is left alone. #>
function Get-SuiteProcesses($Paths) {
    Get-Process deskhud, glazewm, glazewm-watcher, yasb -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and ($_.Path.StartsWith($Paths.Bin + '\', 'OrdinalIgnoreCase') -or
                      $_.Path.StartsWith($Paths.Yasb + '\', 'OrdinalIgnoreCase'))
    }
}

<# Asks the suite's own processes to quit (deskhud gives the taskbar back on quit), then stops what is left. #>
function Stop-SuiteProcesses($Cmdlet, $Paths) {
    $running = @(Get-SuiteProcesses $Paths)
    if (-not $running) { return }
    Invoke-Step $Cmdlet ("stop " + (($running | ForEach-Object { $_.Name }) -join ', ')) {
        $deskhud = Join-Path $Paths.Bin 'deskhud.exe'
        $glazewm = Join-Path $Paths.Bin 'glazewm.exe'
        if ($running | Where-Object { $_.Name -eq 'deskhud' }) { & $deskhud quit }
        if ($running | Where-Object { $_.Name -eq 'glazewm' }) { & $glazewm command wm-exit }
        $running | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue
        $running | Where-Object { -not $_.HasExited } | Stop-Process -ErrorAction SilentlyContinue
    }
}

<# Task Scheduler XML for a logon task that runs as the user, not elevated, at normal priority (tasks
   default to below normal, which makes a window manager feel sluggish), with no time limit. #>
function New-LogonTaskXml([string]$Exe, [string]$Arguments, [string]$Delay, [string]$Description) {
    $user = [Security.SecurityElement]::Escape("$env:USERDOMAIN\$env:USERNAME")
    $exeX = [Security.SecurityElement]::Escape($Exe)
    $argX = [Security.SecurityElement]::Escape($Arguments)
    $descX = [Security.SecurityElement]::Escape($Description)
    $argLine = if ($Arguments) { "      <Arguments>$argX</Arguments>`r`n" } else { '' }
    @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>$descX</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>$user</UserId>
      <Delay>$Delay</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>$user</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>4</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>$exeX</Command>
$argLine    </Exec>
  </Actions>
</Task>
"@
}
