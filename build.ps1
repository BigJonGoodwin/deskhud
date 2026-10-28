# Builds deskhud.exe with the C# compiler that ships with Windows (.NET Framework 4.8); nothing to install.
#   build.ps1            builds bin\deskhud.exe; a deskhud running from that file is stopped first (it
#                        holds the exe open) and started again afterwards.
#   build.ps1 -Offline   builds bin\offline\deskhud.exe and leaves every running deskhud alone (tests).
param([switch]$Offline)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out = if ($Offline) { Join-Path $root 'bin\offline\deskhud.exe' } else { Join-Path $root 'bin\deskhud.exe' }
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

# Only the deskhud started from this very file: another copy (an installed one, say) is not ours to stop.
$running = $null
if (-not $Offline) {
    $running = Get-Process deskhud -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $out }
    if ($running) {
        # Ask the daemon to quit so it gives the taskbar back; kill only if it does not.
        & $out quit
        $running | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue
        $running | Where-Object { -not $_.HasExited } | Stop-Process
    }
}

# UI Automation (Yasb.cs) and WIC's image decoders (Palette.cs, for WebP wallpapers) live with WPF's
# assemblies, which csc does not search by default.
$wpf = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\WPF'
& $csc /nologo /target:winexe /platform:anycpu /optimize+ /warn:4 `
    "/out:$out" "/win32manifest:$root\src\app.manifest" "/lib:$wpf" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    /r:System.Web.Extensions.dll /r:Microsoft.CSharp.dll `
    /r:UIAutomationClient.dll /r:UIAutomationTypes.dll /r:WindowsBase.dll /r:PresentationCore.dll /r:System.Xaml.dll `
    "$root\src\*.cs"
if ($LASTEXITCODE -ne 0) { throw "build failed" }

# Start through explorer so the daemon runs as a normal desktop process, not a child of this shell.
if ($running) { Start-Process explorer.exe -ArgumentList "`"$out`"" }
"built $out"
