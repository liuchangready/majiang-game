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
    $exited = $proc.WaitForExit(120000)
    if (-not $exited) {
        $null = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\taskkill.exe') -ArgumentList @('/PID',"$($proc.Id)",'/T','/F') -PassThru -WindowStyle Hidden -Wait
        $exited = $proc.WaitForExit(3000)
    }
    $drained = [System.Threading.Tasks.Task]::WaitAll([System.Threading.Tasks.Task[]]@($outTask,$errTask),3000)
    return [pscustomobject]@{ exitCode=if($exited){$proc.ExitCode}else{124}; timedOut=(-not $exited); outputDrained=$drained; stdout=if($drained){$outTask.Result}else{'[bounded stdout drain timeout]'}; stderr=if($drained){$errTask.Result}else{'[bounded stderr drain timeout]'} }
}

$positive = Invoke-C04Tool @('-NoProfile','-ExecutionPolicy','Bypass','-File',$runner,'-RunRoot',$fixtureRoot,'-FixtureOnly')
if ($positive.exitCode -ne 0 -or $positive.stdout -notmatch 'C04_TOOL_FIXTURES_PASS positive=1 negative=58 protobuf=PASS malformed=2 timeoutTree=PASS outputDrain=PASS outputFiles=\d+') {
    throw "Positive/negative C04 tool fixtures failed. exit=$($positive.exitCode) stdout=$($positive.stdout) stderr=$($positive.stderr)"
}
$sealPath = Join-Path $fixtureRoot 'fixture-seal.json'
$sealedFiles = Get-Content -LiteralPath $sealPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($sealedFiles.Count -lt 3 -or @($sealedFiles | Where-Object { $_.path -match '^(temp|nuget|dotnet-home|upm-cache|verifier-dotnet-artifacts)/' }).Count -ne 0) { throw 'Fixture did not seal a stable nonempty evidence set.' }
function Get-SealedFixtureSet {
    $rootPrefix = [IO.Path]::GetFullPath($fixtureRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    @($sealedFiles | ForEach-Object {
        $candidate = [IO.Path]::GetFullPath((Join-Path $fixtureRoot ([string]$_.path)))
        if (-not $candidate.StartsWith($rootPrefix,[StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $candidate -PathType Leaf)) { throw 'Fixture seal contains a missing or out-of-root artifact.' }
        $hash = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash
        $bytes = (Get-Item -LiteralPath $candidate).Length
        if ($hash -cne [string]$_.sha256 -or $bytes -ne [long]$_.bytes) { throw "Fixture sealed artifact differs from its recorded bytes/hash: $($_.path)." }
        [pscustomobject]@{ Path=$candidate; Hash=$hash; Bytes=$bytes }
    } | Sort-Object Path)
}
$before = @(Get-SealedFixtureSet)
$foreignBefore = (Get-FileHash -LiteralPath (Join-Path $fixtureRoot 'foreign-sentinel.txt') -Algorithm SHA256).Hash
$overwrite = Invoke-C04Tool @('-NoProfile','-ExecutionPolicy','Bypass','-File',$runner,'-RunRoot',$fixtureRoot,'-FixtureOnly')
if ($overwrite.exitCode -eq 0) { throw 'Runner accepted an existing evidence root.' }
$after = @(Get-SealedFixtureSet)
if (($before | ConvertTo-Json -Compress) -cne ($after | ConvertTo-Json -Compress)) { throw 'Overwrite rejection modified sealed fixture evidence.' }
if ((Get-FileHash -LiteralPath (Join-Path $fixtureRoot 'foreign-sentinel.txt') -Algorithm SHA256).Hash -cne $foreignBefore) { throw 'Overwrite rejection changed the foreign-owner sentinel.' }
$badRoot = Join-Path (Join-Path $repo 'build') 'not-a-guid'
$path = Invoke-C04Tool @('-NoProfile','-ExecutionPolicy','Bypass','-File',$runner,'-RunRoot',$badRoot,'-FixtureOnly')
if ($path.exitCode -eq 0 -or (Test-Path -LiteralPath $badRoot)) { throw 'Runner accepted or created a non-GUID output root.' }
Write-Host 'C04_TOOL_TESTS_PASS positive=1 negative=58 protobuf=PASS malformed=2 outputRoot=' $fixtureRoot
