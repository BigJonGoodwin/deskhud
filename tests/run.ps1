# Offline tests: builds deskhud with build.ps1 -Offline (no running deskhud is touched), builds
# tests\Tests.cs against it and runs it on the fixtures. Never talks to GlazeWM, komorebi or YASB, and
# keeps deskhud's log and caches in a temp folder instead of %LOCALAPPDATA%\deskhud.
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$repo = Split-Path $here
& (Join-Path $repo 'build.ps1') -Offline | Out-Host

$bin = Join-Path $here 'bin'
New-Item -ItemType Directory -Force $bin | Out-Null
Copy-Item (Join-Path $repo 'bin\offline\deskhud.exe') $bin -Force
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:exe /platform:anycpu /warn:4 "/out:$bin\deskhud.tests.exe" "/r:$bin\deskhud.exe" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll `
    "$here\Tests.cs"
if ($LASTEXITCODE -ne 0) { throw "test build failed" }

$env:DESKHUD_STATE_DIR = Join-Path $env:TEMP 'deskhud-tests'
& "$bin\deskhud.tests.exe" (Join-Path $here 'fixtures')
exit $LASTEXITCODE
