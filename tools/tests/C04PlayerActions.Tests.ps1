#requires -Version 5.1
[CmdletBinding()]
param([string]$PowerShellPath = 'powershell.exe')

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$runner = Join-Path $repo 'tools/unity/run-p45-c04-player-actions.ps1'
$runId = [Guid]::NewGuid().ToString('N')
$fixtureRoot = Join-Path (Join-Path $repo 'build') $runId
function Invoke-C04Tool([string[]]$Arguments) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $PowerShellPath
    $psi.Arguments = (($Arguments | ForEach-Object { '"' + $_.Replace('"','\"') + '"' }) -join ' ')
    $psi.WorkingDirectory = $repo
    $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $proc = New-Object System.Diagnostics.Process; $proc.StartInfo = $psi
    if (-not $proc.Start()) { throw 'Unable to start PowerShell test process.' }
    $outTask = $proc.StandardOutput.ReadToEndAsync(); $errTask = $proc.StandardError.ReadToEndAsync()
    $proc.WaitForExit()
    return [pscustomobject]@{ exitCode=$proc.ExitCode; stdout=$outTask.Result; stderr=$errTask.Result }
}

$positive = Invoke-C04Tool @('-NoProfile','-ExecutionPolicy','Bypass','-File',$runner,'-RunRoot',$fixtureRoot,'-FixtureOnly')
if ($positive.exitCode -ne 0 -or $positive.stdout -notmatch 'C04_TOOL_FIXTURES_PASS positive=1 negative=10') {
    throw "Positive/negative C04 tool fixtures failed. exit=$($positive.exitCode) stdout=$($positive.stdout) stderr=$($positive.stderr)"
}
$before = Get-ChildItem -LiteralPath $fixtureRoot -Recurse -File | Get-FileHash -Algorithm SHA256 | Sort-Object Path
$overwrite = Invoke-C04Tool @('-NoProfile','-ExecutionPolicy','Bypass','-File',$runner,'-RunRoot',$fixtureRoot,'-FixtureOnly')
if ($overwrite.exitCode -eq 0) { throw 'Runner accepted an existing evidence root.' }
$after = Get-ChildItem -LiteralPath $fixtureRoot -Recurse -File | Get-FileHash -Algorithm SHA256 | Sort-Object Path
if (($before | ConvertTo-Json -Compress) -cne ($after | ConvertTo-Json -Compress)) { throw 'Overwrite rejection modified sealed fixture evidence.' }
$badRoot = Join-Path (Join-Path $repo 'build') 'not-a-guid'
$path = Invoke-C04Tool @('-NoProfile','-ExecutionPolicy','Bypass','-File',$runner,'-RunRoot',$badRoot,'-FixtureOnly')
if ($path.exitCode -eq 0 -or (Test-Path -LiteralPath $badRoot)) { throw 'Runner accepted or created a non-GUID output root.' }
Write-Host 'C04_TOOL_TESTS_PASS positive=1 negative=12 outputRoot=' $fixtureRoot
