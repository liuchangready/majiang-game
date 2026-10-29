#requires -Version 5.1
<#
.SYNOPSIS
    Real-process matrix for the production tools\verify-v5.ps1 capture boundary.

.DESCRIPTION
    D-CAPTURE-01: Windows PowerShell 5.1 wrapped native stderr as a
    RemoteException under $ErrorActionPreference='Stop', the pipeline aborted,
    and Invoke-Captured returned $null instead of the real native exit code.
    The old behavior also lost the stderr diagnostic as ordinary log text.

    This is a tool-boundary test, not a re-run of the formal .NET suite. It
    extracts the REAL production Invoke-Captured function from
    tools\verify-v5.ps1 with the PowerShell AST and calls it against real child
    PowerShell processes. The test never mirrors the production algorithm.

    Coverage:
      - native exits 0 / 1 / 2 / 17, each with stdout AND stderr
      - stderr-only output
      - no output at all
      - a child script path and arguments containing spaces
      - Unicode stdout and stderr
      - large interleaved stdout/stderr (deadlock guard)
      - a real PowerShell throw (must be a null/failing exit, never 0)
      - a non-existent executable (must be a null/failing exit, never 0)
      - a PowerShell-only scriptblock that runs no native command (null exit)
      - a deliberately polluted LASTEXITCODE: a real native exit must replace
        it, while a failing invocation must NOT reuse it
      - $ErrorActionPreference and $LASTEXITCODE restored after every call

    Evidence (logs, child script, results.json, SHAs) is written only under this
    worktree's ignored artifacts directory, in a fresh GUID-named tree. No
    production source, formal test project, database or cluster is touched.

    Exit codes: 0 = every case behaved as expected; 1 = at least one case failed.
#>
param(
    [string]$EvidenceDir,
    # Optional override used only to replay this same matrix against the frozen
    # pre-fix verifier blob when recording the D-CAPTURE-01 failure evidence.
    # Normal runs omit it and exercise tools\verify-v5.ps1 in this worktree.
    [string]$VerifyScriptPath
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$verifyScript = $(if ($VerifyScriptPath) { [IO.Path]::GetFullPath($VerifyScriptPath) } else { Join-Path $repoRoot 'tools\verify-v5.ps1' })
if (-not (Test-Path -LiteralPath $verifyScript)) { throw "Production verifier not found: $verifyScript" }

$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($verifyScript, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors -and $parseErrors.Count) { throw "Production verifier does not parse: $($parseErrors[0].Message)" }
$captureAst = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-Captured' }, $true)
if (-not $captureAst) { throw 'Cannot extract production function Invoke-Captured from tools\verify-v5.ps1' }
. ([scriptblock]::Create($captureAst.Extent.Text))

if (-not (Get-Command -Name Invoke-Captured -CommandType Function -ErrorAction SilentlyContinue)) {
    throw 'Extracted Invoke-Captured is not callable'
}

$evidenceDir = $EvidenceDir
if (-not $evidenceDir) {
    $evidenceDir = Join-Path $repoRoot ('artifacts/captured-exit-selftest-' + [guid]::NewGuid().ToString('N'))
}
$evidenceDir = [IO.Path]::GetFullPath($evidenceDir)
New-Item -ItemType Directory -Force -Path $evidenceDir | Out-Null

$hostExe = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
$spaceDir = Join-Path $evidenceDir 'space dir'
New-Item -ItemType Directory -Force -Path $spaceDir | Out-Null
$childPath = Join-Path $spaceDir 'child probe.ps1'

$childSource = @'
param(
    [int]$ExitCode = 0,
    [string]$StdoutText = '',
    [string]$StderrText = '',
    [string]$EchoArg = '',
    [int]$BulkLines = 0,
    [int]$BulkBytes = 256
)
if ($StdoutText) { [Console]::Out.WriteLine($StdoutText) }
if ($StderrText) { [Console]::Error.WriteLine($StderrText) }
if ($EchoArg) { [Console]::Out.WriteLine('echo-arg=' + $EchoArg) }
if ($BulkLines -gt 0) {
    $payload = 'x' * $BulkBytes
    for ($i = 0; $i -lt $BulkLines; $i++) {
        [Console]::Out.WriteLine(('OUT-{0:D4}-{1}' -f $i, $payload))
        [Console]::Error.WriteLine(('ERR-{0:D4}-{1}' -f $i, $payload))
    }
    [Console]::Out.WriteLine('BULK-OUT-END')
    [Console]::Error.WriteLine('BULK-ERR-END')
}
exit $ExitCode
'@
Set-Content -LiteralPath $childPath -Value $childSource -Encoding UTF8

$results = New-Object System.Collections.Generic.List[object]

# Preserve the caller's own script/global LASTEXITCODE and the host's native
# preference for the whole matrix. The synthetic preference exercises the
# restoration path even on Windows PowerShell 5.1, where the variable does not
# exist by default; it is removed again at the end.
$originalScriptExitCode = $script:LASTEXITCODE
$originalGlobalExitCode = $global:LASTEXITCODE
$originalNativePreference = Get-Variable -Name 'PSNativeCommandUseErrorActionPreference' -ErrorAction SilentlyContinue
$hadOriginalNativePreference = ($null -ne $originalNativePreference)
$originalNativePreferenceValue = $(if ($hadOriginalNativePreference) { $originalNativePreference.Value } else { $null })
$syntheticNativePreference = $false
if (-not $hadOriginalNativePreference) {
    $script:PSNativeCommandUseErrorActionPreference = $true
    $syntheticNativePreference = $true
}

function Add-CaptureCase {
    param(
        [string]$Case,
        [object]$ExpectedExit,
        [scriptblock]$Command,
        [string[]]$MustContain = @(),
        [string[]]$MustNotContain = @(),
        [switch]$AllowEmptyLog,
        [ValidateSet('global', 'local', 'script')]
        [string]$Scope = 'global',
        [int]$Sentinel = 9876,
        [int]$GlobalSentinel = 4242,
        [int]$MaxSeconds = 60
    )

    $caseDir = Join-Path $evidenceDir ("case-{0}" -f $Case)
    New-Item -ItemType Directory -Force -Path $caseDir | Out-Null
    $logPath = Join-Path $caseDir 'capture.log'

    $beforeEap = $ErrorActionPreference
    $prefBefore = Get-Variable -Name 'PSNativeCommandUseErrorActionPreference' -ErrorAction SilentlyContinue
    $prefExistedBefore = ($null -ne $prefBefore)
    $prefValueBefore = $(if ($prefExistedBefore) { $prefBefore.Value } else { $null })

    # Set the sentinel in the exact scope under test. For a local sentinel the
    # script/global baseline is deliberately different, so a production read of
    # an ancestor value cannot masquerade as the native exit code.
    $script:LASTEXITCODE = $GlobalSentinel
    $global:LASTEXITCODE = $GlobalSentinel
    if ($Scope -eq 'local') {
        $local:LASTEXITCODE = $Sentinel
    }
    else {
        $script:LASTEXITCODE = $Sentinel
        $global:LASTEXITCODE = $Sentinel
    }

    $actual = $null
    $leaked = $null
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $actual = Invoke-Captured -LogPath $logPath -Command $Command
    }
    catch {
        $leaked = $_
    }
    $stopwatch.Stop()
    $afterEap = $ErrorActionPreference
    $afterLocal = $local:LASTEXITCODE
    $afterScript = $script:LASTEXITCODE
    $afterGlobal = $global:LASTEXITCODE
    $afterUnqualified = $LASTEXITCODE
    $prefAfter = Get-Variable -Name 'PSNativeCommandUseErrorActionPreference' -ErrorAction SilentlyContinue
    $prefExistedAfter = ($null -ne $prefAfter)
    $prefValueAfter = $(if ($prefExistedAfter) { $prefAfter.Value } else { $null })

    $logText = ''
    if (Test-Path -LiteralPath $logPath -PathType Leaf) {
        $logText = Get-Content -LiteralPath $logPath -Raw
        if ($null -eq $logText) { $logText = '' }
    }

    $failures = New-Object System.Collections.Generic.List[string]
    if ($leaked) {
        $failures.Add("Invoke-Captured leaked an exception: $($leaked.Exception.GetType().FullName): $($leaked.Exception.Message)")
    }
    if ($null -eq $ExpectedExit) {
        if ($null -ne $actual) { $failures.Add("expected a null/failing exit, got '$actual'") }
    }
    elseif ($null -eq $actual) {
        $failures.Add("expected exit $ExpectedExit, got null")
    }
    elseif ([int]$actual -ne [int]$ExpectedExit) {
        $failures.Add("expected exit $ExpectedExit, got '$actual'")
    }
    if (-not $AllowEmptyLog -and [string]::IsNullOrEmpty($logText)) {
        $failures.Add('captured log is empty')
    }
    foreach ($marker in $MustContain) {
        if (-not $logText.Contains($marker)) { $failures.Add("captured log is missing marker: $marker") }
    }
    $forbiddenMarkers = @($MustNotContain)
    if ($null -ne $ExpectedExit) {
        # A real native exit is evidence, not an invocation exception. Any
        # "invocation threw" line in a native-exit case means the capture
        # boundary swallowed or misclassified the run.
        $forbiddenMarkers += 'invocation threw an exception'
    }
    foreach ($marker in $forbiddenMarkers) {
        if ($logText.Contains($marker)) { $failures.Add("captured log contains forbidden marker: $marker") }
    }
    if ($afterEap -ne $beforeEap) {
        $failures.Add("ErrorActionPreference was not restored: '$beforeEap' -> '$afterEap'")
    }
    if ($prefExistedBefore -ne $prefExistedAfter) {
        $failures.Add("PSNativeCommandUseErrorActionPreference existence changed: $prefExistedBefore -> $prefExistedAfter")
    }
    elseif ($prefExistedBefore -and $prefValueBefore -ne $prefValueAfter) {
        $failures.Add("PSNativeCommandUseErrorActionPreference was not restored: '$prefValueBefore' -> '$prefValueAfter'")
    }

    $expectedLocal = $(if ($Scope -eq 'local') { $Sentinel } else { $null })
    $expectedScript = $(if ($Scope -eq 'local') { $GlobalSentinel } else { $Sentinel })
    $expectedGlobal = $(if ($Scope -eq 'local') { $GlobalSentinel } else { $Sentinel })
    if ($null -eq $expectedLocal) {
        if ($null -ne $afterLocal) { $failures.Add("caller local LASTEXITCODE unexpectedly set to '$afterLocal'") }
    }
    elseif ($null -eq $afterLocal -or [int]$afterLocal -ne [int]$expectedLocal) {
        $failures.Add("caller local LASTEXITCODE was not preserved: expected $expectedLocal, got '$afterLocal'")
    }
    if ($null -eq $afterScript -or [int]$afterScript -ne [int]$expectedScript) {
        $failures.Add("script LASTEXITCODE was not restored: expected $expectedScript, got '$afterScript'")
    }
    if ($null -eq $afterGlobal -or [int]$afterGlobal -ne [int]$expectedGlobal) {
        $failures.Add("global LASTEXITCODE was not restored: expected $expectedGlobal, got '$afterGlobal'")
    }
    if ($null -eq $afterUnqualified -or [int]$afterUnqualified -ne [int]$Sentinel) {
        $failures.Add("unqualified LASTEXITCODE was not preserved: expected $Sentinel, got '$afterUnqualified'")
    }
    if ($stopwatch.Elapsed.TotalSeconds -gt $MaxSeconds) {
        $failures.Add(("case exceeded {0}s (actual {1:N1}s); possible deadlock" -f $MaxSeconds, $stopwatch.Elapsed.TotalSeconds))
    }

    $record = [pscustomobject]@{
        Case                   = $Case
        Scope                  = $Scope
        Sentinel               = $Sentinel
        GlobalSentinel         = $(if ($Scope -eq 'local') { $GlobalSentinel } else { $Sentinel })
        ExpectedExit           = $(if ($null -eq $ExpectedExit) { 'NULL' } else { "$ExpectedExit" })
        ActualExit             = $(if ($null -eq $actual) { 'NULL' } else { "$actual" })
        AfterLocal             = $(if ($null -eq $afterLocal) { 'NULL' } else { "$afterLocal" })
        AfterScript            = $(if ($null -eq $afterScript) { 'NULL' } else { "$afterScript" })
        AfterGlobal            = $(if ($null -eq $afterGlobal) { 'NULL' } else { "$afterGlobal" })
        Seconds                = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 3)
        Pass                   = ($failures.Count -eq 0)
        Failures               = ($failures -join '; ')
        LogPath                = $logPath
        LogBytes               = $logText.Length
    }
    $results.Add($record)
    Write-Host ("[{0}] {1} scope={2} expected={3} actual={4} local={5} script={6} global={7} {8}" -f $(if ($record.Pass) { 'PASS' } else { 'FAIL' }), $Case, $Scope, $record.ExpectedExit, $record.ActualExit, $record.AfterLocal, $record.AfterScript, $record.AfterGlobal, $record.Failures)
    return $record
}

# --- exits 0 / 1 / 2 / 17, each with both streams -----------------------------
foreach ($code in @(0, 1, 2, 17)) {
    $stdoutMarker = "case-exit-$code-stdout"
    $stderrMarker = "case-exit-$code-stderr"
    Add-CaptureCase -Case ("exit{0}-both-streams" -f $code) -ExpectedExit $code -Command {
        & $hostExe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $childPath -ExitCode $code -StdoutText $stdoutMarker -StderrText $stderrMarker
    } -MustContain @($stdoutMarker, $stderrMarker) -MaxSeconds 30 | Out-Null
}

# --- stderr only --------------------------------------------------------------
Add-CaptureCase -Case 'stderr-only-exit17' -ExpectedExit 17 -Command {
    & $hostExe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $childPath -ExitCode 17 -StderrText 'case-stderr-only-diagnostic'
} -MustContain @('case-stderr-only-diagnostic') -MustNotContain @('case-stderr-only-stdout-marker') -MaxSeconds 30 | Out-Null

# --- no output ----------------------------------------------------------------
Add-CaptureCase -Case 'no-output-exit0' -ExpectedExit 0 -Command {
    & $hostExe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $childPath -ExitCode 0
} -AllowEmptyLog -MaxSeconds 30 | Out-Null

# --- spaces in the script path and in argument values --------------------------
Add-CaptureCase -Case 'spaces-path-and-args' -ExpectedExit 0 -Command {
    & $hostExe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $childPath -ExitCode 0 -StdoutText 'case-spaces-stdout' -StderrText 'case-spaces-stderr' -EchoArg '参数 with spaces 值'
} -MustContain @('case-spaces-stdout', 'case-spaces-stderr', 'echo-arg=参数 with spaces 值') -MaxSeconds 30 | Out-Null

# --- Unicode on both streams ---------------------------------------------------
Add-CaptureCase -Case 'unicode-both-streams' -ExpectedExit 0 -Command {
    & $hostExe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $childPath -ExitCode 0 -StdoutText '输出-麻将-Ω-テスト' -StderrText '诊断-麻将-Ω-テスト'
} -MustContain @('输出-麻将-Ω-テスト', '诊断-麻将-Ω-テスト') -MaxSeconds 30 | Out-Null

# --- large interleaved dual streams (deadlock guard) ---------------------------
Add-CaptureCase -Case 'bulk-dual-streams-no-deadlock' -ExpectedExit 0 -Command {
    & $hostExe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $childPath -ExitCode 0 -BulkLines 1200 -BulkBytes 256
} -MustContain @('OUT-0000-', 'OUT-1199-', 'ERR-0000-', 'ERR-1199-', 'BULK-OUT-END', 'BULK-ERR-END') -MaxSeconds 60 | Out-Null

# --- real PowerShell throw -----------------------------------------------------
Add-CaptureCase -Case 'powershell-throw-fails-closed' -ExpectedExit $null -Command {
    throw 'selftest intentional throw'
} -MustContain @('selftest intentional throw') -MaxSeconds 15 | Out-Null

# --- non-existent executable ---------------------------------------------------
Add-CaptureCase -Case 'missing-executable-fails-closed' -ExpectedExit $null -Command {
    & (Join-Path $env:WINDIR 'capture-probe-does-not-exist.exe')
} -MustContain @('capture-probe-does-not-exist.exe') -MaxSeconds 15 | Out-Null

# --- a real non-terminating PowerShell error is not native stderr --------------
Add-CaptureCase -Case 'powershell-write-error-fails-closed' -ExpectedExit $null -Command {
    Write-Error 'selftest non-terminating error'
} -MustContain @('selftest non-terminating error') -MaxSeconds 15 | Out-Null

# --- native exit 0 plus a later PowerShell error must still fail-closed -------
Add-CaptureCase -Case 'native-stderr-then-powershell-error-fails-closed' -ExpectedExit $null -Command {
    & $hostExe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $childPath -ExitCode 0 -StderrText 'prior-native-stderr-diagnostic'
    Write-Error 'later-powershell-error'
} -MustContain @('prior-native-stderr-diagnostic', 'later-powershell-error') -MaxSeconds 15 | Out-Null

# --- no native command executed (must not reuse a stale LASTEXITCODE) ----------
Add-CaptureCase -Case 'no-native-command-fails-closed' -ExpectedExit $null -Command {
    Write-Output 'powershell-only-output'
} -MustContain @('powershell-only-output') -MaxSeconds 15 | Out-Null

# --- polluted LASTEXITCODE: a throw must not reuse it --------------------------
Add-CaptureCase -Case 'polluted-lastexitcode-throw-fails-closed' -ExpectedExit $null -Sentinel 4242 -Command {
    throw 'selftest throw after polluted LASTEXITCODE'
} -MustContain @('selftest throw after polluted LASTEXITCODE') -MaxSeconds 15 | Out-Null

# --- polluted LASTEXITCODE: a real native exit must replace it ------------------
Add-CaptureCase -Case 'real-exit-overrides-polluted-lastexitcode' -ExpectedExit 17 -Sentinel 4242 -Command {
    & $hostExe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $childPath -ExitCode 17 -StdoutText 'case-override-stdout' -StderrText 'case-override-stderr'
} -MustContain @('case-override-stdout', 'case-override-stderr') -MaxSeconds 30 | Out-Null

# --- D-CAPTURE-01-SCOPE: caller-local and script-scope shadows ----------------
# A caller function can hold $local:LASTEXITCODE. That value must never be
# mistaken for a native exit, and a real native code must never be masked by it.
function Add-ScopedNativeMatrix {
    param(
        [ValidateSet('local', 'script')]
        [string]$Scope,
        [int]$Sentinel,
        [int]$GlobalSentinel = 4242
    )
    $prefix = '{0}{1}' -f $Scope, $Sentinel

    Add-CaptureCase -Case "$prefix-no-native" -ExpectedExit $null -Scope $Scope `
        -Sentinel $Sentinel -GlobalSentinel $GlobalSentinel -Command {
        'no native command in scoped matrix'
    } -MustContain @('no native command in scoped matrix') -MaxSeconds 15 | Out-Null

    foreach ($code in @(0, 1, 2, 17)) {
        $stdoutMarker = "$prefix-exit$code-stdout"
        $stderrMarker = "$prefix-exit$code-stderr"
        Add-CaptureCase -Case "$prefix-exit$code-both" -ExpectedExit $code -Scope $Scope `
            -Sentinel $Sentinel -GlobalSentinel $GlobalSentinel -Command {
            & $hostExe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $childPath -ExitCode $code -StdoutText $stdoutMarker -StderrText $stderrMarker
        } -MustContain @($stdoutMarker, $stderrMarker) -MaxSeconds 30 | Out-Null
    }

    $throwMessage = "$prefix intentional throw"
    Add-CaptureCase -Case "$prefix-throw" -ExpectedExit $null -Scope $Scope `
        -Sentinel $Sentinel -GlobalSentinel $GlobalSentinel -Command {
        throw $throwMessage
    } -MustContain @($throwMessage) -MaxSeconds 15 | Out-Null

    $missingName = "$prefix-missing-executable.exe"
    Add-CaptureCase -Case "$prefix-missing-executable" -ExpectedExit $null -Scope $Scope `
        -Sentinel $Sentinel -GlobalSentinel $GlobalSentinel -Command {
        & (Join-Path $env:WINDIR $missingName)
    } -MustContain @($missingName) -MaxSeconds 15 | Out-Null
}

Add-ScopedNativeMatrix -Scope local -Sentinel 0
Add-ScopedNativeMatrix -Scope local -Sentinel 4242 -GlobalSentinel 7777
Add-ScopedNativeMatrix -Scope script -Sentinel 0
Add-ScopedNativeMatrix -Scope script -Sentinel 4242

# Restore the test harness's own caller state after the scoped cases.
if ($syntheticNativePreference) {
    Remove-Variable -Name 'PSNativeCommandUseErrorActionPreference' -Scope Script -ErrorAction SilentlyContinue
}
elseif ($hadOriginalNativePreference) {
    $script:PSNativeCommandUseErrorActionPreference = $originalNativePreferenceValue
}
$script:LASTEXITCODE = $originalScriptExitCode
$global:LASTEXITCODE = $originalGlobalExitCode

$failed = @($results | Where-Object { -not $_.Pass })
$summary = [ordered]@{
    generatedUtc           = (Get-Date).ToUniversalTime().ToString('o')
    productionScript       = $verifyScript
    productionScriptSha256 = (Get-FileHash -LiteralPath $verifyScript -Algorithm SHA256).Hash
    childScript            = $childPath
    childScriptSha256      = (Get-FileHash -LiteralPath $childPath -Algorithm SHA256).Hash
    powershellVersion      = $PSVersionTable.PSVersion.ToString()
    powerShellExe          = $hostExe
    cases                  = $results.Count
    passed                 = @($results | Where-Object { $_.Pass }).Count
    failed                 = $failed.Count
    results                = $results
}
$summaryPath = Join-Path $evidenceDir 'results.json'
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
$results | Format-Table -AutoSize Case, ExpectedExit, ActualExit, Seconds, Pass, Failures

Write-Output ('CAPTURED-EXIT MATRIX: {0}/{1} passed, {2} failed' -f $summary.passed, $summary.cases, $summary.failed)
Write-Output "Evidence: $evidenceDir"
if ($failed.Count -gt 0) { exit 1 }
exit 0
