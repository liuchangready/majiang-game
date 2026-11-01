#requires -Version 5.1
<#
.SYNOPSIS
    Native-invocation evidence matrix for tools\ci\verify-v5-table-postgres.ps1.

.DESCRIPTION
    Extracts the production Invoke-Native / Invoke-WriterAndKill functions with
    the PowerShell AST and exercises them against a real child process. This is
    a tool-boundary test, not a PostgreSQL or business-test run.

    Coverage:
      - native exits 0, 1, 2 and 17: return value, separated stdout/stderr and
        merged log, structured .meta.json/.meta.txt with the real exit code;
      - arguments containing spaces, embedded quotes and trailing backslashes;
      - timeout: the child is killed, the merged timeout log is written BEFORE
        the throw, and metadata records timedOut/killed and the real/no exit;
      - startup failure: metadata records started=false and nativeExit=null;
      - quiet success: an empty merged log is legitimate and still exits 0;
      - explicit secret value and Password= connection-string redaction without
        ever recording the secret value or the process environment;
      - the post-commit crash writer: real forced kill, integer exit code,
        kill.json and structured metadata.

    Fixtures are written only under this worktree's ignored artifacts directory,
    in a fresh GUID-named tree. Production sources are untouched.
#>
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$source = Join-Path $PSScriptRoot 'verify-v5-table-postgres.ps1'
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw "Production wrapper does not parse: $($parseErrors[0].Message)" }
$functionNames = @(
    'ConvertTo-NativeArgumentLine',
    'Get-RedactedArgumentList',
    'Read-FileBytesShared',
    'Get-FileEvidence',
    'Write-NativeInvocationMetadata',
    'Invoke-Native',
    'Invoke-WriterAndKill',
    'Get-Sha256'
)
foreach ($name in $functionNames) {
    $fn = $ast.Find({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
        }, $true)
    if (-not $fn) { throw "Cannot extract production function $name" }
    . ([scriptblock]::Create($fn.Extent.Text))
}

$evidenceDir = Join-Path $repoRoot ('artifacts/native-exit-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $evidenceDir | Out-Null
$child = Join-Path $evidenceDir 'child with spaces.ps1'
Set-Content -LiteralPath $child -Encoding UTF8 -Value @'
param([int]$Code, [int]$Delay = 0, [string]$Token = '', [string]$Unused = '', [switch]$ReportUnusedLength)
Write-Output "stdout code=$Code"
[Console]::Error.WriteLine("stderr code=$Code")
if ($Token) { Write-Output "stdout token=$Token" }
if ($ReportUnusedLength) { Write-Output "unused-length=$($Unused.Length)" }
if ($Delay) { Start-Sleep -Seconds $Delay }
exit $Code
'@
$hostExe = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
$results = New-Object System.Collections.Generic.List[object]

function Add-Result([string]$Case, [bool]$Pass, [string]$Expected, [object]$Actual) {
    $results.Add([pscustomobject]@{ Case = $Case; Pass = $Pass; Expected = $Expected; Actual = $Actual })
}

function Assert-Metadata([string]$Case, [string]$LogPath, [int]$ExpectedExit, [bool]$ExpectTimeout = $false, [bool]$ExpectKilled = $false) {
    $metaPath = $LogPath + '.meta.json'
    if (-not (Test-Path -LiteralPath $metaPath)) { throw "$Case missing $metaPath" }
    if (-not (Test-Path -LiteralPath ($LogPath + '.meta.txt'))) { throw "$Case missing .meta.txt" }
    $meta = Get-Content -LiteralPath $metaPath -Raw | ConvertFrom-Json
    if (-not [bool]$meta.started) { throw "$Case metadata started=false" }
    if ($null -eq $meta.nativeExit -or [int]$meta.nativeExit -ne $ExpectedExit) { throw "$Case metadata nativeExit=$($meta.nativeExit), expected $ExpectedExit" }
    if ([bool]$meta.timedOut -ne $ExpectTimeout) { throw "$Case metadata timedOut=$($meta.timedOut), expected $ExpectTimeout" }
    if ([bool]$meta.killed -ne $ExpectKilled) { throw "$Case metadata killed=$($meta.killed), expected $ExpectKilled" }
    if ([int]$meta.pid -le 0) { throw "$Case metadata pid is not positive" }
    if ([string]::IsNullOrWhiteSpace([string]$meta.commandLine)) { throw "$Case metadata commandLine is empty" }
    if ([string]::IsNullOrWhiteSpace([string]$meta.cwd)) { throw "$Case metadata cwd is empty" }
    if (@($meta.argv).Count -lt 1) { throw "$Case metadata argv is empty" }
    foreach ($stream in @('stdout', 'stderr', 'merged')) {
        $item = $meta.logs.$stream
        if (-not $item.exists) { throw "$Case metadata $stream path missing" }
        $actualSha = (Get-FileHash -LiteralPath $item.path -Algorithm SHA256).Hash
        if ($actualSha -ne $item.sha256) { throw "$Case metadata $stream SHA mismatch" }
        if ([long](Get-Item -LiteralPath $item.path).Length -ne [long]$item.bytes) { throw "$Case metadata $stream bytes mismatch" }
    }
    $metaText = Get-Content -LiteralPath ($LogPath + '.meta.txt') -Raw
    if ($metaText -notmatch ('nativeExit=' + [regex]::Escape([string]$ExpectedExit))) { throw "$Case meta.txt has no nativeExit=$ExpectedExit" }
    return $meta
}

foreach ($code in @(0, 1, 2, 17)) {
    $log = Join-Path $evidenceDir "exit-$code.log"
    $actual = Invoke-Native -FilePath $hostExe -Arguments @('-NoProfile', '-NonInteractive', '-File', $child, '-Code', "$code") -LogPath $log
    $output = Get-Content -LiteralPath $log -Raw
    $meta = Assert-Metadata -Case "exit-$code" -LogPath $log -ExpectedExit $code
    $pass = ($actual -eq $code) -and
        $output.Contains("stdout code=$code") -and
        $output.Contains("stderr code=$code") -and
        ([int]$meta.nativeExit -eq $code) -and
        (-not [bool]$meta.timedOut) -and
        (-not [bool]$meta.killed)
    Add-Result -Case "exit-$code" -Pass $pass -Expected $code -Actual $actual
}

# Arguments with spaces, an embedded quote, and a path separator: the native
# child must receive the exact token, not a re-split variant.
$quoteToken = 'quote" and space \ end'
$quoteLog = Join-Path $evidenceDir 'quoted-arguments.log'
$quoteActual = Invoke-Native -FilePath $hostExe -Arguments @(
    '-NoProfile', '-NonInteractive', '-File', $child, '-Code', '0', '-Token', $quoteToken
) -LogPath $quoteLog
$quoteOutput = Get-Content -LiteralPath $quoteLog -Raw
$quoteMeta = Get-Content -LiteralPath ($quoteLog + '.meta.json') -Raw | ConvertFrom-Json
$quoteArgv = @($quoteMeta.argv)
$quotePass = ($quoteActual -eq 0) -and
    $quoteOutput.Contains("stdout token=$quoteToken") -and
    ($quoteArgv[-1] -ceq $quoteToken)
Add-Result -Case 'quoted-arguments' -Pass $quotePass -Expected 'exact token' -Actual $quoteArgv[-1]

# Secret redaction is metadata-only: the child intentionally receives the value
# through an unused parameter so raw streams remain untouched and secret-free.
$secret = 'SECRET-' + [guid]::NewGuid().ToString('N')
$connection = 'Host=127.0.0.1;Port=55592;Database=x;Username=y;Password=p@ssw0rd'
$secretLog = Join-Path $evidenceDir 'redaction.log'
$rawUnused = $secret + ';' + $connection
$secretActual = Invoke-Native -FilePath $hostExe -Arguments @(
    '-NoProfile', '-NonInteractive', '-File', $child, '-Code', '0', '-Unused', $rawUnused, '-ReportUnusedLength'
) -LogPath $secretLog -SensitiveValues @($secret)
$secretMetaRaw = Get-Content -LiteralPath ($secretLog + '.meta.json') -Raw
$secretMeta = $secretMetaRaw | ConvertFrom-Json
$secretOutput = Get-Content -LiteralPath $secretLog -Raw
$secretPass = ($secretActual -eq 0) -and
    (-not $secretMetaRaw.Contains($secret)) -and
    (-not $secretMetaRaw.Contains('p@ssw0rd')) -and
    [bool]$secretMeta.redaction.applied -and
    (-not [bool]$secretMeta.redaction.secretValuesRecorded) -and
    ((@($secretMeta.argv) -join ' ').Contains('<redacted>')) -and
    ([string]$secretMeta.commandLine).Contains('Password=<redacted>') -and
    $secretOutput.Contains("unused-length=$($rawUnused.Length)")
Add-Result -Case 'redaction' -Pass $secretPass -Expected 'secret and Password value absent from metadata; native child still received the real argv' -Actual $secretMeta.redaction

# Timeout: the merged log must exist before the throw, with both real streams.
$timeoutLog = Join-Path $evidenceDir 'timeout.log'
$timedOut = $false
$timeoutMessage = ''
try {
    $null = Invoke-Native -FilePath $hostExe -Arguments @('-NoProfile', '-NonInteractive', '-File', $child, '-Delay', '20') -LogPath $timeoutLog -TimeoutSeconds 1
}
catch {
    $timeoutMessage = $_.Exception.Message
    if ($timeoutMessage -notlike 'Command timed out*') { throw }
    $timedOut = $true
}
$timeoutMeta = $null
$timeoutPass = $false
if ($timedOut -and (Test-Path -LiteralPath $timeoutLog)) {
    $timeoutText = Get-Content -LiteralPath $timeoutLog -Raw
    $timeoutMeta = Get-Content -LiteralPath ($timeoutLog + '.meta.json') -Raw | ConvertFrom-Json
    $timeoutPass = $timeoutText.Contains('stdout code=0') -and
        $timeoutText.Contains('stderr code=0') -and
        [bool]$timeoutMeta.timedOut -and
        [bool]$timeoutMeta.killed -and
        (-not [bool]$timeoutMeta.stillRunning) -and
        ($null -eq $timeoutMeta.nativeExit -or $timeoutMeta.nativeExit -is [int] -or $timeoutMeta.nativeExit -is [long]) -and
        (($null -ne $timeoutMeta.nativeExit) -or (-not [string]::IsNullOrWhiteSpace([string]$timeoutMeta.exitUnavailableReason)))
}
Add-Result -Case 'timeout' -Pass $timeoutPass -Expected 'throw with merged timeout log and metadata' -Actual $timeoutMessage

# Startup failure: no process, but a structured record with nativeExit=null.
$missingLog = Join-Path $evidenceDir 'startup-failure.log'
$missingThrew = $false
try {
    $null = Invoke-Native -FilePath (Join-Path $evidenceDir 'does-not-exist.exe') -Arguments @('--probe') -LogPath $missingLog
}
catch { $missingThrew = $true }
$missingMeta = $null
$missingPass = $false
if ($missingThrew -and (Test-Path -LiteralPath ($missingLog + '.meta.json'))) {
    $missingMeta = Get-Content -LiteralPath ($missingLog + '.meta.json') -Raw | ConvertFrom-Json
    $missingPass = (-not [bool]$missingMeta.started) -and
        ($null -eq $missingMeta.nativeExit) -and
        (-not [string]::IsNullOrWhiteSpace([string]$missingMeta.error)) -and
        (Test-Path -LiteralPath $missingLog)
}
Add-Result -Case 'startup-failure' -Pass $missingPass -Expected 'started=false nativeExit=null' -Actual $(if ($missingMeta) { $missingMeta.error } else { 'no metadata' })

# Quiet success: zero-byte merged output is legitimate.
$quietLog = Join-Path $evidenceDir 'quiet.log'
$quietActual = Invoke-Native -FilePath $hostExe -Arguments @('-NoProfile', '-NonInteractive', '-Command', 'exit 0') -LogPath $quietLog
$quietMeta = Get-Content -LiteralPath ($quietLog + '.meta.json') -Raw | ConvertFrom-Json
$quietPass = ($quietActual -eq 0) -and
    (Test-Path -LiteralPath $quietLog) -and
    ([long]$quietMeta.logs.merged.bytes -eq 0) -and
    ([int]$quietMeta.nativeExit -eq 0) -and
    [string]::IsNullOrWhiteSpace([string]$quietMeta.error)
Add-Result -Case 'quiet-success' -Pass $quietPass -Expected 'exit 0 with empty merged log' -Actual $quietActual

# Forced writer kill after the commit marker.
$writer = Join-Path $evidenceDir 'writer.ps1'
$marker = Join-Path $evidenceDir 'writer.marker'
$writerScript = "[IO.File]::WriteAllText('" + $marker.Replace("'", "''") + "', 'ready'); Start-Sleep -Seconds 30"
Set-Content -LiteralPath $writer -Encoding UTF8 -Value $writerScript
$DotnetPath = $hostExe
$writerLog = Join-Path $evidenceDir 'writer.log'
$killResult = Invoke-WriterAndKill -WorkerDll $writer -MarkerPath $marker -LogPath $writerLog -TimeoutSeconds 10
$killRecord = Get-Content -LiteralPath ($writerLog + '.kill.json') -Raw | ConvertFrom-Json
$writerMeta = Get-Content -LiteralPath ($writerLog + '.meta.json') -Raw | ConvertFrom-Json
$writerPass = $killResult.killed -and
    ($killResult.exitCode -ne 0) -and
    ($killRecord.exitCode -eq $killResult.exitCode) -and
    ($killRecord.pid -eq $killResult.pid) -and
    (-not (Get-Process -Id $killResult.pid -ErrorAction SilentlyContinue)) -and
    ($writerMeta.killReason -eq 'post-commit-marker') -and
    [bool]$writerMeta.killed -and
    ([int]$writerMeta.nativeExit -eq [int]$killResult.exitCode)
Add-Result -Case 'forced-writer' -Pass $writerPass -Expected 'nonzero' -Actual $killResult.exitCode

$results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidenceDir 'results.json') -Encoding UTF8
$results | Format-Table -AutoSize
Write-Output "Evidence: $evidenceDir"
$failed = @($results | Where-Object { -not $_.Pass })
Write-Output ("RESULT: {0}/{1} native evidence cases passed" -f ($results.Count - $failed.Count), $results.Count)
if ($failed.Count) { exit 1 }
exit 0
