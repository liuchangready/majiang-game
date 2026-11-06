<#
.SYNOPSIS
    P4.3-A table model test runner.

.DESCRIPTION
    Builds the standalone Mahjong.Unity.TableModel.Tests project fresh,
    executes the freshly built test DLL with the xUnit v3 direct runner,
    parses the XML + console totals, then runs the existing Unity static
    boundary check and its full negative mutation matrix.

    This runner proves the pure C# model against real Mahjong.Client /
    Contracts / Contracts.Wire only. It never starts Unity, so Unity Editor,
    PlayMode, Player, IL2CPP, Android, iOS and device verification remain
    NOT_RUN and must not be inferred from a green result here.

    Every run uses an exclusive output directory and its own TEMP/TMP under
    this worktree. A compile failure, zero tests, any failure/skip/not-run,
    cancellation, timeout, a weakened negative matrix, or a missing XML file
    is a hard failure. No complete.flag is created.
#>
[CmdletBinding()]
param(
    [string]$OutputDir,
    [string]$DotnetPath = "D:\Unity\dotnet\dotnet.exe",
    [int]$TestTimeoutSeconds = 300,
    [int]$BoundaryTimeoutSeconds = 300,
    [int]$NegativeTimeoutSeconds = 600
)

$ErrorActionPreference = "Stop"
$repo = (Get-Location).Path
$project = Join-Path $repo "dotnet\tests\Mahjong.Unity.TableModel.Tests\Mahjong.Unity.TableModel.Tests.csproj"
$testDir = Join-Path $repo "dotnet\tests\Mahjong.Unity.TableModel.Tests"
$scriptPath = $PSCommandPath
$startedUtc = (Get-Date).ToUniversalTime()

if (-not (Test-Path -LiteralPath $project)) {
    throw "Test project not found: $project"
}

if (-not (Test-Path -LiteralPath $DotnetPath)) {
    throw "dotnet executable not found: $DotnetPath"
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmssfffZ")
    $OutputDir = Join-Path $repo ("build\p43a-models\" + $stamp + "-" + $PID)
}

$output = [System.IO.Path]::GetFullPath($OutputDir)
if (-not $output.StartsWith($repo + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDir must stay inside this worktree: $output"
}

if (Test-Path -LiteralPath $output) {
    throw "OutputDir already exists; use a new exclusive directory: $output"
}

$binDir = Join-Path $output "bin"
$logDir = Join-Path $output "logs"
$tempDir = Join-Path $output "temp"
New-Item -ItemType Directory -Force -Path $binDir, $logDir, $tempDir | Out-Null

$env:TEMP = $tempDir
$env:TMP = $tempDir
$env:DOTNET_NOLOGO = "1"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_CLI_UI_LANGUAGE = "en"
$env:TESTINGPLATFORM_TELEMETRY_OPTOUT = "1"

function Quote-Argument([string]$value) {
    if ($value -notmatch '[\s"]') { return $value }
    return '"' + ($value -replace '"', '\"') + '"'
}

function Invoke-NativeProcess {
    param(
        [string]$FileName,
        [string[]]$Arguments,
        [string]$WorkingDirectory,
        [string]$LogPath,
        [int]$TimeoutSeconds
    )

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FileName
    $psi.Arguments = (($Arguments | ForEach-Object { Quote-Argument $_ }) -join " ")
    $psi.WorkingDirectory = $WorkingDirectory
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8

    $process = [System.Diagnostics.Process]::Start($psi)
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $timedOut = $false
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $timedOut = $true
        try { $process.Kill() } catch { }
        try { $process.WaitForExit(10000) | Out-Null } catch { }
    }

    $stdout = $stdoutTask.Result
    $stderr = $stderrTask.Result
    $exitCode = if ($timedOut) { -1 } else { $process.ExitCode }
    $text = @(
        "COMMAND: $FileName $($psi.Arguments)",
        "WORKDIR: $WorkingDirectory",
        "NATIVE_EXIT: $exitCode",
        "TIMED_OUT: $timedOut",
        "STDOUT:",
        $stdout,
        "STDERR:",
        $stderr
    ) -join [Environment]::NewLine
    [System.IO.File]::WriteAllText($LogPath, $text, (New-Object System.Text.UTF8Encoding($false)))

    return [pscustomobject]@{
        ExitCode = $exitCode
        TimedOut = $timedOut
        Output = $stdout
        Error = $stderr
        CommandLine = "$FileName $($psi.Arguments)"
        LogPath = $LogPath
    }
}

function Assert-Success($result, [string]$label) {
    if ($result.TimedOut) {
        throw "$label timed out. See $($result.LogPath)"
    }

    if ($result.ExitCode -ne 0) {
        throw "$label failed with native exit $($result.ExitCode). See $($result.LogPath)"
    }
}

function Remove-OwnedBuildTree([string]$path, [string]$allowedRoot) {
    $full = [System.IO.Path]::GetFullPath($path)
    $root = [System.IO.Path]::GetFullPath($allowedRoot)
    if (-not ($full.StartsWith($root + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase))) {
        throw "Refusing recursive delete outside owned root: $full"
    }

    if (Test-Path -LiteralPath $full) {
        Remove-Item -LiteralPath $full -Recurse -Force
    }
}

# Remove only this test project's ignored bin/obj so the compiled model is fresh.
foreach ($name in @("bin", "obj")) {
    Remove-OwnedBuildTree (Join-Path $testDir $name) $testDir
}

$failed = $false
$failureMessage = $null
$build = $null
$run = $null
$boundary = $null
$negativeCountResult = $null
$negative = $null
$total = 0
$passed = 0
$failedCount = 0
$skipped = 0
$errors = 0
$notRun = 0
$declaredCount = 0
$actualNegative = 0
$testXmlPath = Join-Path $output "results.xml"
$testJunitPath = Join-Path $output "results-junit.xml"
$testLogPath = Join-Path $logDir "test.log"
$buildLogPath = Join-Path $logDir "build.log"
$boundaryLogPath = Join-Path $logDir "boundary.log"
$negativeLogPath = Join-Path $logDir "boundary-negative.log"
$dllPath = Join-Path $binDir "Mahjong.Unity.TableModel.Tests.dll"

try {
    Write-Host "[1/4] Fresh build -> $output"
    $build = Invoke-NativeProcess -FileName $DotnetPath -Arguments @(
        "build", $project, "-c", "Release", "-o", $binDir, "--no-incremental",
        "-p:UseSharedCompilation=false", "-v:minimal"
    ) -WorkingDirectory $repo -LogPath $buildLogPath -TimeoutSeconds 300
    Assert-Success $build "Fresh build"
    if (-not (Test-Path -LiteralPath $dllPath)) {
        throw "Fresh build did not produce $dllPath"
    }

    Write-Host "[2/4] Run fresh test DLL with xUnit v3 direct runner"
    $run = Invoke-NativeProcess -FileName $DotnetPath -Arguments @(
        $dllPath, "-noColor", "-noLogo", "-result-xml", $testXmlPath, "-result-junit", $testJunitPath
    ) -WorkingDirectory $repo -LogPath $testLogPath -TimeoutSeconds $TestTimeoutSeconds
    Assert-Success $run "Table model tests"

    if (-not (Test-Path -LiteralPath $testXmlPath)) {
        throw "Test runner did not produce $testXmlPath"
    }

    [xml]$xml = Get-Content -LiteralPath $testXmlPath -Raw
    $assemblies = @($xml.assemblies.assembly)
    if ($assemblies.Count -eq 0) {
        throw "Test XML contains no assemblies."
    }

    $total = 0
    $passed = 0
    $failedCount = 0
    $skipped = 0
    $errors = 0
    $notRun = 0
    foreach ($assembly in $assemblies) {
        $total += [int]$assembly.total
        $passed += [int]$assembly.passed
        $failedCount += [int]$assembly.failed
        $skipped += [int]$assembly.skipped
        $errors += [int]$assembly.errors
        $notRun += [int]$assembly.'not-run'
    }

    if ($total -le 0) { throw "Test XML reports zero tests." }
    if ($failedCount -gt 0 -or $errors -gt 0) { throw "Test XML reports $failedCount failed / $errors errors." }
    if ($skipped -gt 0 -or $notRun -gt 0) { throw "Test XML reports $skipped skipped / $notRun not-run." }
    if ($passed -ne $total) { throw "Test XML passed $passed of $total." }

    $summaryMatch = [regex]::Match($run.Output, 'Total:\s*(\d+)(?:,\s*Errors:\s*(\d+))?(?:,\s*Failed:\s*(\d+))?(?:,\s*Skipped:\s*(\d+))?(?:,\s*Not Run:\s*(\d+))?')
    if (-not $summaryMatch.Success) {
        throw "Console summary was not parseable; refusing to trust XML alone."
    }

    $consoleTotal = [int]$summaryMatch.Groups[1].Value
    $consoleErrors = if ($summaryMatch.Groups[2].Success) { [int]$summaryMatch.Groups[2].Value } else { 0 }
    $consoleFailed = if ($summaryMatch.Groups[3].Success) { [int]$summaryMatch.Groups[3].Value } else { 0 }
    $consoleSkipped = if ($summaryMatch.Groups[4].Success) { [int]$summaryMatch.Groups[4].Value } else { 0 }
    $consoleNotRun = if ($summaryMatch.Groups[5].Success) { [int]$summaryMatch.Groups[5].Value } else { 0 }
    if ($consoleTotal -ne $total -or $consoleErrors -ne $errors -or $consoleFailed -ne $failedCount -or
        $consoleSkipped -ne $skipped -or $consoleNotRun -ne $notRun) {
        throw "Console summary and XML disagree."
    }

    Write-Host "      Tests: total=$total passed=$passed failed=$failedCount skipped=$skipped errors=$errors notRun=$notRun"

    Write-Host "[3/4] Existing Unity static boundary check"
    $boundary = Invoke-NativeProcess -FileName "powershell.exe" -Arguments @(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $repo "tools\unity\check-unity-boundaries.ps1")
    ) -WorkingDirectory $repo -LogPath $boundaryLogPath -TimeoutSeconds $BoundaryTimeoutSeconds
    Assert-Success $boundary "Unity boundary check"

    Write-Host "[4/4] Existing 13-case boundary negative matrix"
    $negativeCountResult = Invoke-NativeProcess -FileName "powershell.exe" -Arguments @(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $repo "tools\unity\check-unity-boundaries.ps1"), "-GetNegativeMatrixCount"
    ) -WorkingDirectory $repo -LogPath (Join-Path $logDir "boundary-negative-count.log") -TimeoutSeconds 60
    Assert-Success $negativeCountResult "Boundary negative case count"
    $declaredCount = 0
    if (-not [int]::TryParse($negativeCountResult.Output.Trim(), [ref]$declaredCount)) {
        throw "Could not parse the negative case count: $($negativeCountResult.Output)"
    }

    if ($declaredCount -ne 13) {
        throw "The frozen negative matrix must contain 13 cases, but the checker declared $declaredCount."
    }

    $negative = Invoke-NativeProcess -FileName "powershell.exe" -Arguments @(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $repo "tools\unity\check-unity-boundaries.ps1"), "-TestNegativeMatrix"
    ) -WorkingDirectory $repo -LogPath $negativeLogPath -TimeoutSeconds $NegativeTimeoutSeconds
    Assert-Success $negative "Boundary negative matrix"
    $negativePass = [regex]::Match($negative.Output, 'All\s+(\d+)\s+negative test cases passed successfully!')
    if (-not $negativePass.Success) {
        throw "Negative matrix did not report a full pass."
    }

    $actualNegative = [int]$negativePass.Groups[1].Value
    if ($actualNegative -ne 13 -or $actualNegative -ne $declaredCount) {
        throw "Negative matrix reported $actualNegative/$declaredCount; expected 13/13."
    }

    Write-Host "      Boundary negative matrix: 13/13"
}
catch {
    $failed = $true
    $failureMessage = $_.Exception.Message
    Write-Host "FAILED: $failureMessage" -ForegroundColor Red
}

$finishedUtc = (Get-Date).ToUniversalTime()
$head = (& git -C $repo rev-parse HEAD 2>$null | Out-String).Trim()
$branch = (& git -C $repo rev-parse --abbrev-ref HEAD 2>$null | Out-String).Trim()

$prodFiles = @(
    "unity/Mahjong.Unity/Assets/Scripts/Table/PlayerTableViewState.cs",
    "unity/Mahjong.Unity/Assets/Scripts/Table/PlayerTableViewState.cs.meta",
    "unity/Mahjong.Unity/Assets/Scripts/Table/PlayerTableProjectionMapper.cs",
    "unity/Mahjong.Unity/Assets/Scripts/Table/PlayerTableProjectionMapper.cs.meta",
    "unity/Mahjong.Unity/Assets/Scripts/Table/TableInteractionState.cs",
    "unity/Mahjong.Unity/Assets/Scripts/Table/TableInteractionState.cs.meta",
    "unity/Mahjong.Unity/Assets/Scripts/Table/TableCommandIntentFactory.cs",
    "unity/Mahjong.Unity/Assets/Scripts/Table/TableCommandIntentFactory.cs.meta",
    "unity/Mahjong.Unity/Assets/Scripts/Table/ConfirmedAnimationQueue.cs",
    "unity/Mahjong.Unity/Assets/Scripts/Table/ConfirmedAnimationQueue.cs.meta"
)
$testFiles = @(
    "dotnet/tests/Mahjong.Unity.TableModel.Tests/Mahjong.Unity.TableModel.Tests.csproj",
    "dotnet/tests/Mahjong.Unity.TableModel.Tests/P43TestData.cs",
    "dotnet/tests/Mahjong.Unity.TableModel.Tests/TestTransport.cs",
    "dotnet/tests/Mahjong.Unity.TableModel.Tests/TestPayload.cs",
    "dotnet/tests/Mahjong.Unity.TableModel.Tests/PlayerTableProjectionTests.cs",
    "dotnet/tests/Mahjong.Unity.TableModel.Tests/TableCommandIntentFactoryTests.cs",
    "dotnet/tests/Mahjong.Unity.TableModel.Tests/TableInteractionStateTests.cs",
    "dotnet/tests/Mahjong.Unity.TableModel.Tests/ConfirmedAnimationQueueTests.cs",
    "dotnet/tests/Mahjong.Unity.TableModel.Tests/RealSessionIntegrationTests.cs",
    "tools/unity/test-p43-models.ps1"
)

$hashes = @()
foreach ($relative in ($prodFiles + $testFiles)) {
    $path = Join-Path $repo $relative
    if (Test-Path -LiteralPath $path) {
        $item = Get-Item -LiteralPath $path
        $hash = Get-FileHash -LiteralPath $path -Algorithm SHA256
        $hashes += [pscustomobject]@{ path = $relative; bytes = $item.Length; sha256 = $hash.Hash }
    }
}
foreach ($evidencePath in @($dllPath, $testXmlPath, $testJunitPath, $buildLogPath, $testLogPath, $boundaryLogPath, $negativeLogPath)) {
    if (Test-Path -LiteralPath $evidencePath) {
        $item = Get-Item -LiteralPath $evidencePath
        $hash = Get-FileHash -LiteralPath $evidencePath -Algorithm SHA256
        $hashes += [pscustomobject]@{ path = $evidencePath.Substring($output.Length).TrimStart('\'); bytes = $item.Length; sha256 = $hash.Hash }
    }
}

$evidence = [ordered]@{
    schema = "p43a-table-models-v1"
    status = if ($failed) { "FAIL" } else { "PASS" }
    failure = $failureMessage
    head = $head
    branch = $branch
    startedUtc = $startedUtc.ToString("o")
    finishedUtc = $finishedUtc.ToString("o")
    durationSeconds = [math]::Round(($finishedUtc - $startedUtc).TotalSeconds, 3)
    outputDir = $output
    dotnet = $DotnetPath
    build = [ordered]@{
        command = if ($build) { $build.CommandLine } else { $null }
        nativeExit = if ($build) { $build.ExitCode } else { $null }
        timedOut = if ($build) { $build.TimedOut } else { $null }
        log = if ($build) { $build.LogPath } else { $null }
    }
    tests = [ordered]@{
        command = if ($run) { $run.CommandLine } else { $null }
        nativeExit = if ($run) { $run.ExitCode } else { $null }
        timedOut = if ($run) { $run.TimedOut } else { $null }
        total = $total
        passed = $passed
        failed = $failedCount
        skipped = $skipped
        errors = $errors
        notRun = $notRun
        xml = $testXmlPath
        junit = $testJunitPath
        log = $testLogPath
        dll = $dllPath
    }
    boundary = [ordered]@{
        command = if ($boundary) { $boundary.CommandLine } else { $null }
        nativeExit = if ($boundary) { $boundary.ExitCode } else { $null }
        timedOut = if ($boundary) { $boundary.TimedOut } else { $null }
        log = $boundaryLogPath
    }
    negativeMatrix = [ordered]@{
        declaredCases = $declaredCount
        reportedCases = $actualNegative
        command = if ($negative) { $negative.CommandLine } else { $null }
        nativeExit = if ($negative) { $negative.ExitCode } else { $null }
        timedOut = if ($negative) { $negative.TimedOut } else { $null }
        log = $negativeLogPath
    }
    unityEditor = "NOT_RUN"
    unityPlayMode = "NOT_RUN"
    unityPlayer = "NOT_RUN"
    android = "NOT_RUN"
    ios = "BLOCKED"
    note = "Pure C# model and real Client/Contracts/Wire wiring only. This is not a Unity Editor, PlayMode, Player, IL2CPP, Android, iOS or device pass."
    completeFlagCreated = $false
    hashes = $hashes
}

$evidenceJson = Join-Path $output "p43a-evidence.json"
$evidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $evidenceJson -Encoding UTF8
$hashCsv = Join-Path $output "evidence-sha256.csv"
$hashes | ForEach-Object { "{0},{1},{2}" -f $_.path, $_.bytes, $_.sha256 } | Set-Content -LiteralPath $hashCsv -Encoding UTF8

Write-Host ""
if ($failed) {
    Write-Host "P4.3-A MODEL VERIFICATION FAILED: $failureMessage" -ForegroundColor Red
    Write-Host "Evidence: $output"
    exit 1
}

Write-Host "P4.3-A MODEL VERIFICATION PASSED"
Write-Host ("  tests          : {0}/{0} passed, failed/skipped/errors/not-run=0" -f $total)
Write-Host "  boundary       : PASS"
Write-Host "  negative matrix: 13/13"
Write-Host "  Unity Editor/PlayMode/Player/Android/iOS/device: NOT_RUN (not inferred)"
Write-Host "  evidence       : $output"
exit 0
