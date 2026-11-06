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
    $meta = Get-Content -LiteralPath $metaPath -Raw -Encoding UTF8 | ConvertFrom-Json
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
    $metaText = Get-Content -LiteralPath ($LogPath + '.meta.txt') -Raw -Encoding UTF8
    if ($metaText -notmatch ('nativeExit=' + [regex]::Escape([string]$ExpectedExit))) { throw "$Case meta.txt has no nativeExit=$ExpectedExit" }
    return $meta
}

foreach ($code in @(0, 1, 2, 17)) {
    $log = Join-Path $evidenceDir "exit-$code.log"
    $actual = Invoke-Native -FilePath $hostExe -Arguments @('-NoProfile', '-NonInteractive', '-File', $child, '-Code', "$code") -LogPath $log
    $output = Get-Content -LiteralPath $log -Raw -Encoding UTF8
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
$quoteOutput = Get-Content -LiteralPath $quoteLog -Raw -Encoding UTF8
$quoteMeta = Get-Content -LiteralPath ($quoteLog + '.meta.json') -Raw -Encoding UTF8 | ConvertFrom-Json
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
$secretMetaRaw = Get-Content -LiteralPath ($secretLog + '.meta.json') -Raw -Encoding UTF8
$secretMeta = $secretMetaRaw | ConvertFrom-Json
$secretOutput = Get-Content -LiteralPath $secretLog -Raw -Encoding UTF8
$secretPass = ($secretActual -eq 0) -and
    (-not $secretMetaRaw.Contains($secret)) -and
    (-not $secretMetaRaw.Contains('p@ssw0rd')) -and
    [bool]$secretMeta.redaction.applied -and
    (-not [bool]$secretMeta.redaction.secretValuesRecorded) -and
    ((@($secretMeta.argv) -join ' ').Contains('<redacted>')) -and
    ([string]$secretMeta.commandLine).Contains('Password=<redacted>') -and
    $secretOutput.Contains("unused-length=$($rawUnused.Length)")
Add-Result -Case 'redaction' -Pass $secretPass -Expected 'secret and Password value absent from metadata; native child still received the real argv' -Actual $secretMeta.redaction

# R1-02: quoted connection-string values may contain semicolons, doubled
# quotes, escaped quotes and whitespace. Redact the whole value in metadata
# while keeping the executed argv unchanged.
$redactionFixtures = @(
    [ordered]@{ name = 'password-double-quoted-semicolon'; text = 'Host=127.0.0.1;Password="SYNTH_HEAD;SYNTH_TAIL";Username=fixture'; tokens = @('SYNTH_HEAD', 'SYNTH_TAIL') },
    [ordered]@{ name = 'pwd-single-quoted-semicolon'; text = "Host=127.0.0.1;Pwd='SYNTH_HEAD;SYNTH_TAIL';Username=fixture"; tokens = @('SYNTH_HEAD', 'SYNTH_TAIL') },
    [ordered]@{ name = 'password-spacing-case'; text = 'Host=x; PASSWORD = "SYNTH_HEAD;SYNTH_TAIL" ;Username=fixture'; tokens = @('SYNTH_HEAD', 'SYNTH_TAIL') },
    [ordered]@{ name = 'password-doubled-quote'; text = 'Host=x;Password="SYNTH""HEAD;SYNTH_TAIL";Username=fixture'; tokens = @('SYNTH_HEAD', 'SYNTH_TAIL') },
    [ordered]@{ name = 'password-literal-backslash-doubled-quote'; text = 'Host=x;Password="SYNTH_HEAD\""X;SYNTH_TAIL";Username=fixture'; tokens = @('SYNTH_HEAD', 'SYNTH_TAIL') },
    [ordered]@{ name = 'pwd-literal-backslash-doubled-quote'; text = "Host=x;Pwd='SYNTH_HEAD\''X;SYNTH_TAIL';Username=fixture"; tokens = @('SYNTH_HEAD', 'SYNTH_TAIL') },
    [ordered]@{ name = 'password-unquoted'; text = 'Host=x;Password=SYNTH_HEAD;Username=fixture'; tokens = @('SYNTH_HEAD') },
    [ordered]@{ name = 'password-escaped-semicolon'; text = 'Host=x;Password=SYNTH_HEAD\;SYNTH_TAIL;Username=fixture'; tokens = @('SYNTH_HEAD', 'SYNTH_TAIL') }
)
foreach ($fixture in $redactionFixtures) {
    $redactionResult = Get-RedactedArgumentList -Arguments @($fixture.text)
    $sanitizedText = [string]$redactionResult.arguments[0]
    $leaked = @($fixture.tokens | Where-Object { $sanitizedText.Contains($_) }).Count -gt 0
    $fixturePass = [bool]$redactionResult.applied -and
        [int]$redactionResult.credentialFieldsRedacted -ge 1 -and
        (-not $leaked)
    Add-Result -Case ('redaction-' + $fixture.name) -Pass $fixturePass -Expected 'entire quoted/unquoted credential value redacted' -Actual ("applied={0} fields={1} leaked={2}" -f $redactionResult.applied, $redactionResult.credentialFieldsRedacted, $leaked)
}
$sensitiveResult = Get-RedactedArgumentList -Arguments @('--token', 'SYNTH_SENSITIVE') -SensitiveValues @('SYNTH_SENSITIVE')
$sensitivePass = [bool]$sensitiveResult.applied -and
    ((-not ([string]$sensitiveResult.arguments[0]).Contains('SYNTH_SENSITIVE')) -and
    (-not ([string]$sensitiveResult.arguments[1]).Contains('SYNTH_SENSITIVE')))
Add-Result -Case 'redaction-sensitive-values' -Pass $sensitivePass -Expected 'named SensitiveValues absent' -Actual $sensitiveResult.applied
$benignResult = Get-RedactedArgumentList -Arguments @('--host', '127.0.0.1', '--name', 'a b')
$benignPass = (-not [bool]$benignResult.applied) -and
    ([string]$benignResult.arguments[0] -ceq '--host') -and
    ([string]$benignResult.arguments[1] -ceq '127.0.0.1') -and
    ([string]$benignResult.arguments[2] -ceq '--name') -and
    ([string]$benignResult.arguments[3] -ceq 'a b')
Add-Result -Case 'redaction-benign-argv' -Pass $benignPass -Expected 'benign argv boundaries unchanged' -Actual $benignResult.applied

$quotedConnection = 'Host=127.0.0.1;Port=55592;Username=fixture;Password="SYNTH_HEAD;SYNTH_TAIL"'
$quotedLog = Join-Path $evidenceDir 'quoted-password.log'
$quotedActual = Invoke-Native -FilePath $hostExe -Arguments @(
    '-NoProfile', '-NonInteractive', '-File', $child, '-Code', '0', '-Unused', $quotedConnection, '-ReportUnusedLength'
) -LogPath $quotedLog
$quotedMetaRaw = Get-Content -LiteralPath ($quotedLog + '.meta.json') -Raw -Encoding UTF8
$quotedMetaText = Get-Content -LiteralPath ($quotedLog + '.meta.txt') -Raw -Encoding UTF8
$quotedMeta = $quotedMetaRaw | ConvertFrom-Json
$quotedOutput = Get-Content -LiteralPath $quotedLog -Raw -Encoding UTF8
$quotedPass = ($quotedActual -eq 0) -and
    (-not $quotedMetaRaw.Contains('SYNTH_HEAD')) -and
    (-not $quotedMetaRaw.Contains('SYNTH_TAIL')) -and
    (-not $quotedMetaText.Contains('SYNTH_HEAD')) -and
    (-not $quotedMetaText.Contains('SYNTH_TAIL')) -and
    [bool]$quotedMeta.redaction.applied -and
    [int]$quotedMeta.redaction.credentialFieldsRedacted -ge 1 -and
    $quotedOutput.Contains("unused-length=$($quotedConnection.Length)")
Add-Result -Case 'redaction-quoted-metadata' -Pass $quotedPass -Expected 'metadata and meta.txt contain no quoted credential tail; native child received full argv' -Actual $quotedMeta.redaction

# R2 residual: actual Npgsql accepts a literal backslash adjacent to a doubled
# quote. Both exact original inputs must be fully redacted in real metadata.
$grammarInputs = @(
    [ordered]@{ name = 'double'; text = 'Host=x;Password="SYNTH_HEAD\""X;SYNTH_TAIL";Username=fixture' },
    [ordered]@{ name = 'single'; text = "Host=x;Pwd='SYNTH_HEAD\''X;SYNTH_TAIL';Username=fixture" }
)
foreach ($grammarCase in $grammarInputs) {
    $grammarLog = Join-Path $evidenceDir ('grammar-metadata-' + $grammarCase.name + '.log')
    $grammarActual = Invoke-Native -FilePath $hostExe -Arguments @(
        '-NoProfile', '-NonInteractive', '-File', $child, '-Code', '0', '-Unused', $grammarCase.text, '-ReportUnusedLength'
    ) -LogPath $grammarLog
    $grammarMetaRaw = Get-Content -LiteralPath ($grammarLog + '.meta.json') -Raw -Encoding UTF8
    $grammarMetaText = Get-Content -LiteralPath ($grammarLog + '.meta.txt') -Raw -Encoding UTF8
    $grammarMeta = $grammarMetaRaw | ConvertFrom-Json
    $grammarOutput = Get-Content -LiteralPath $grammarLog -Raw -Encoding UTF8
    $grammarPass = ($grammarActual -eq 0) -and
        (-not $grammarMetaRaw.Contains('SYNTH_HEAD')) -and
        (-not $grammarMetaRaw.Contains('SYNTH_TAIL')) -and
        (-not $grammarMetaText.Contains('SYNTH_HEAD')) -and
        (-not $grammarMetaText.Contains('SYNTH_TAIL')) -and
        [bool]$grammarMeta.redaction.applied -and
        [int]$grammarMeta.redaction.credentialFieldsRedacted -ge 1 -and
        $grammarOutput.Contains("unused-length=$($grammarCase.text.Length)")
    Add-Result -Case ('redaction-grammar-metadata-' + $grammarCase.name) -Pass $grammarPass -Expected 'literal-backslash/doubled-quote metadata contains no head/tail; child argv length preserved' -Actual $grammarMeta.redaction
}

# R1-01: deterministic non-ASCII metadata must survive a UTF-8 no-BOM write and
# an explicit UTF-8 read without lossy ANSI decoding.
$unicodeLog = Join-Path $evidenceDir 'non-ascii-metadata.log'
$unicodeError = -join @(
    [char]0x672C, [char]0x5730, [char]0x5316, [char]0x9519, [char]0x8BEF,
    [char]0xFF1A, [char]0x627E, [char]0x4E0D, [char]0x5230, [char]0x6307,
    [char]0x5B9A, [char]0x7684, [char]0x6587, [char]0x4EF6, [char]0x3002
)
$unicodeRecord = [ordered]@{
    schema = 'p52-native-invocation.v1'
    exe = 'fixture.exe'
    exeSha256 = ''
    cwd = (Get-Location).Path
    argv = @('--probe')
    commandLine = '--probe'
    startedUtc = [DateTime]::UtcNow.ToString('o')
    endedUtc = [DateTime]::UtcNow.ToString('o')
    durationMs = 0
    pid = $null
    started = $false
    nativeExit = $null
    exitUnavailableReason = ''
    timedOut = $false
    killed = $false
    timeoutSeconds = 1
    logs = [ordered]@{
        stdout = Get-FileEvidence -Path (Join-Path $evidenceDir 'unicode.stdout')
        stderr = Get-FileEvidence -Path (Join-Path $evidenceDir 'unicode.stderr')
        merged = Get-FileEvidence -Path $unicodeLog
    }
    redaction = [ordered]@{ applied = $false; redactedArgumentCount = 0; credentialFieldsRedacted = 0; secretValuesRecorded = $false; note = 'none' }
    error = $unicodeError
}
Write-NativeInvocationMetadata -LogPath $unicodeLog -Record $unicodeRecord
$unicodeMeta = Get-Content -LiteralPath ($unicodeLog + '.meta.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$unicodeText = Get-Content -LiteralPath ($unicodeLog + '.meta.txt') -Raw -Encoding UTF8
$unicodePass = ($unicodeMeta.error -ceq $unicodeError) -and $unicodeText.Contains($unicodeError)
Add-Result -Case 'non-ascii-metadata' -Pass $unicodePass -Expected 'UTF-8 no-BOM metadata preserves exact localized error text' -Actual $unicodeMeta.error

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
    $timeoutText = Get-Content -LiteralPath $timeoutLog -Raw -Encoding UTF8
    $timeoutMeta = Get-Content -LiteralPath ($timeoutLog + '.meta.json') -Raw -Encoding UTF8 | ConvertFrom-Json
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
    $missingMeta = Get-Content -LiteralPath ($missingLog + '.meta.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $missingPass = (-not [bool]$missingMeta.started) -and
        ($null -eq $missingMeta.nativeExit) -and
        (-not [string]::IsNullOrWhiteSpace([string]$missingMeta.error)) -and
        (Test-Path -LiteralPath $missingLog)
}
Add-Result -Case 'startup-failure' -Pass $missingPass -Expected 'started=false nativeExit=null' -Actual $(if ($missingMeta) { $missingMeta.error } else { 'no metadata' })

# Quiet success: zero-byte merged output is legitimate.
$quietLog = Join-Path $evidenceDir 'quiet.log'
$quietActual = Invoke-Native -FilePath $hostExe -Arguments @('-NoProfile', '-NonInteractive', '-Command', 'exit 0') -LogPath $quietLog
$quietMeta = Get-Content -LiteralPath ($quietLog + '.meta.json') -Raw -Encoding UTF8 | ConvertFrom-Json
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
$killRecord = Get-Content -LiteralPath ($writerLog + '.kill.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$writerMeta = Get-Content -LiteralPath ($writerLog + '.meta.json') -Raw -Encoding UTF8 | ConvertFrom-Json
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
