<#
.SYNOPSIS
    P4.2-B real LocalHost activation gate.

.DESCRIPTION
    Runs, from a fresh shared build, the complete real-local-host verification:
      1. fresh dotnet solution build + Unity plugin copy + provenance verification
      2. static asmdef/source boundary check
      3. the complete 16-case boundary negative matrix
      4. Unity EditMode tests from a fresh compile
      5. Unity PlayMode tests from a fresh compile
      6. StrictCompiled boundary check against the compiled assemblies
      7. Client and TableRuntime regression suites
      8. Windows 64-bit Player build and the real Lobby -> StartLocalGame -> Table
         smoke (`-smoke-localhost`), including a legal action and a confirmed
         incremental visible event
      9. per-file SHA-256 evidence manifest and default / cr-at-eol diff checks

    Player is part of the activation gate. `-SkipPlayer` exists only for a
    diagnostic run and is recorded as NOT_RUN, never as a pass.

    All paths are resolved relative to the repository root. Absolute OutputDir
    and LogDir values are accepted as-is (the old shell script's absolute-path
    Join-Path bug is deliberately not repeated).
#>
[CmdletBinding()]
param(
    [string]$UnityExe = "D:\Unity\Hub\Editor\6000.3.27f1\Editor\Unity.exe",
    [string]$ProjectPath = "unity/Mahjong.Unity",
    [string]$OutputDir = "build/p42b-localhost-activation",
    [string]$LogDir = "build/p42b-localhost-activation/logs",
    [string]$DotnetPath,
    [switch]$SkipPlayer,
    [switch]$SkipRegression
)

$ErrorActionPreference = "Stop"
# Keep dotnet output ASCII so the test-run success assertion does not depend on
# the machine locale or the .ps1 file encoding.
$env:DOTNET_CLI_UI_LANGUAGE = "en"
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Set-Location -LiteralPath $repoRoot

function Resolve-RepoPath([string]$value) {
    if ([System.IO.Path]::IsPathRooted($value)) { return [System.IO.Path]::GetFullPath($value) }
    return [System.IO.Path]::GetFullPath((Join-Path $repoRoot $value))
}

function Invoke-Logged([string]$label, [scriptblock]$action, [string]$logPath) {
    Write-Host "== $label =="
    $output = & $action 2>&1
    $exit = $LASTEXITCODE
    $output | Out-File -LiteralPath $logPath -Encoding utf8
    $output | ForEach-Object { Write-Host $_ }
    if ($null -eq $exit) { $exit = 0 }
    Write-Host "$label exit=$exit log=$logPath"
    return [int]$exit
}

$resolvedUnity = $UnityExe
if (-not (Test-Path -LiteralPath $resolvedUnity)) { throw "Unity executable not found: $resolvedUnity" }

$resolvedProject = Resolve-RepoPath $ProjectPath
if (-not (Test-Path -LiteralPath $resolvedProject)) { throw "Unity project not found: $resolvedProject" }

$resolvedOutput = Resolve-RepoPath $OutputDir
$resolvedLogs = Resolve-RepoPath $LogDir
New-Item -ItemType Directory -Force -Path $resolvedOutput | Out-Null
New-Item -ItemType Directory -Force -Path $resolvedLogs | Out-Null

$dotnet = if ($DotnetPath) { $DotnetPath } elseif (Test-Path 'D:\Unity\dotnet\dotnet.exe') { 'D:\Unity\dotnet\dotnet.exe' } else { 'dotnet' }
$summary = [ordered]@{
    StartedAtUtc = [DateTime]::UtcNow.ToString('o')
    SourceHead = (& git rev-parse HEAD).Trim()
    FreshSharedBuild = 'NOT_RUN'
    Provenance = 'NOT_RUN'
    StaticBoundary = 'NOT_RUN'
    NegativeMatrix = 'NOT_RUN'
    EditMode = 'NOT_RUN'
    PlayMode = 'NOT_RUN'
    StrictCompiled = 'NOT_RUN'
    ClientTests = 'NOT_RUN'
    TableRuntimeTests = 'NOT_RUN'
    PlayerBuild = 'NOT_RUN'
    PlayerSmoke = 'NOT_RUN'
    DefaultDiffCheck = 'NOT_RUN'
    CrAtEolDiffCheck = 'NOT_RUN'
}

$failed = $false
function Note([string]$key, [string]$value) { $summary[$key] = $value }

# 1. Fresh shared build (the script removes its own Release bin/obj first).
$freshLog = Join-Path $resolvedLogs 'fresh-shared-build.log'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'tools/unity/build-shared.ps1') `
    -Configuration Release -DotnetPath $dotnet *> $freshLog
$freshExit = $LASTEXITCODE
if ($freshExit -ne 0) {
    Note 'FreshSharedBuild' "FAIL(exit=$freshExit)"
    throw "Fresh shared build failed (exit=$freshExit). See $freshLog"
}
Note 'FreshSharedBuild' "PASS(exit=$freshExit)"
$summary.FreshSharedBuildLog = $freshLog

# 2. Provenance of the freshly copied plugin closure.
$provLog = Join-Path $resolvedLogs 'provenance.log'
$provJson = Join-Path $resolvedLogs 'provenance.json'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'tools/unity/check-shared-plugins.ps1') `
    -ProjectRoot $repoRoot -Configuration Release -Json *> $provLog
$provExit = $LASTEXITCODE
if ($provExit -ne 0) {
    Note 'Provenance' "FAIL(exit=$provExit)"
    throw "Fresh plugin provenance verification failed (exit=$provExit). See $provLog"
}
Copy-Item -LiteralPath (Join-Path $resolvedProject 'Library/SharedPluginProvenance/provenance.json') -Destination $provJson -Force
Note 'Provenance' "PASS(exit=$provExit)"

# 3. Static boundary policy.
$boundaryScript = Join-Path $repoRoot 'tools/unity/check-unity-boundaries.ps1'
$staticLog = Join-Path $resolvedLogs 'boundary-static.log'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $boundaryScript *> $staticLog
$staticExit = $LASTEXITCODE
if ($staticExit -ne 0) {
    Note 'StaticBoundary' "FAIL(exit=$staticExit)"
    throw "Static boundary check failed (exit=$staticExit). See $staticLog"
}
Note 'StaticBoundary' "PASS(exit=$staticExit)"

# 4. Negative matrix (16 cases).
$negLog = Join-Path $resolvedLogs 'boundary-negative.log'
$negText = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $boundaryScript -TestNegativeMatrix 2>&1
$negExit = $LASTEXITCODE
$negText | Out-File -LiteralPath $negLog -Encoding utf8
$negText | ForEach-Object { Write-Host $_ }
if ($negExit -ne 0) {
    Note 'NegativeMatrix' "FAIL(exit=$negExit)"
    throw "Boundary negative matrix failed (exit=$negExit). See $negLog"
}
$negMatch = [regex]::Match(($negText -join "`n"), 'All (\d+) negative test cases passed successfully')
$negCount = if ($negMatch.Success) { $negMatch.Groups[1].Value } else { '?' }
if ($negCount -ne '16') {
    Note 'NegativeMatrix' "FAIL(unexpected count=$negCount)"
    throw "Negative matrix did not report the expected 16/16 blocked cases (reported $negCount)"
}
Note 'NegativeMatrix' "PASS($negCount/$negCount blocked,exit=$negExit)"

# 5. Unity EditMode / PlayMode from a fresh compile.
function Invoke-UnityTests([string]$platform, [string]$xmlPath, [string]$logPath) {
    if (Test-Path -LiteralPath $xmlPath) { Remove-Item -LiteralPath $xmlPath -Force }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $resolvedUnity
    $psi.Arguments = "-batchmode -runTests -testPlatform $platform -projectPath `"$resolvedProject`" -testResults `"$xmlPath`" -logFile `"$logPath`""
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $proc = [System.Diagnostics.Process]::Start($psi)
    if (-not $proc.WaitForExit(900000)) {
        $proc.Kill()
        throw "$platform tests timed out after 900 seconds"
    }
    return $proc.ExitCode
}

function Read-TestCounts([string]$xmlPath, [string]$scope) {
    [xml]$xml = Get-Content -LiteralPath $xmlPath
    $cases = @($xml.SelectNodes('//test-case'))
    $scoped = @($cases | Where-Object { ([string]$_.fullname) -like "*$scope*" })
    return [ordered]@{
        Total = [int]$xml.'test-run'.total
        Passed = [int]$xml.'test-run'.passed
        Failed = [int]$xml.'test-run'.failed
        Errors = [int]$xml.'test-run'.failed
        Skipped = [int]$xml.'test-run'.skipped
        NotRun = [int]$xml.'test-run'.inconclusive
        Scoped = $scoped.Count
        ScopedPassed = @($scoped | Where-Object { $_.result -eq 'Passed' }).Count
    }
}

$editXml = Join-Path $resolvedOutput 'editmode-results.xml'
$editLog = Join-Path $resolvedLogs 'editmode.log'
$editExit = Invoke-UnityTests 'EditMode' $editXml $editLog
if ($editExit -ne 0 -or -not (Test-Path -LiteralPath $editXml)) {
    Note 'EditMode' "FAIL(exit=$editExit, xml=$(Test-Path -LiteralPath $editXml))"
    throw "EditMode tests failed (exit=$editExit). See $editLog"
}
$editCounts = Read-TestCounts $editXml 'LocalHostCompositionTests'
Note 'EditMode' "PASS(total=$($editCounts.Total),passed=$($editCounts.Passed),failed=$($editCounts.Failed),skipped=$($editCounts.Skipped),scoped=$($editCounts.Scoped)/$($editCounts.ScopedPassed) passed,exit=$editExit)"
if ($editCounts.Failed -ne 0 -or $editCounts.Skipped -ne 0 -or $editCounts.Total -eq 0 -or $editCounts.Passed -ne $editCounts.Total) {
    throw "EditMode counts are not a clean pass: $($editCounts | ConvertTo-Json -Compress)"
}
if ($editCounts.Scoped -eq 0 -or $editCounts.ScopedPassed -ne $editCounts.Scoped) {
    throw "EditMode scoped LocalHostCompositionTests are not a clean nonzero pass: $($editCounts | ConvertTo-Json -Compress)"
}

$playXml = Join-Path $resolvedOutput 'playmode-results.xml'
$playLog = Join-Path $resolvedLogs 'playmode.log'
$playExit = Invoke-UnityTests 'PlayMode' $playXml $playLog
if ($playExit -ne 0 -or -not (Test-Path -LiteralPath $playXml)) {
    Note 'PlayMode' "FAIL(exit=$playExit, xml=$(Test-Path -LiteralPath $playXml))"
    throw "PlayMode tests failed (exit=$playExit). See $playLog"
}
$playCounts = Read-TestCounts $playXml 'LocalHostCompositionPlayModeTests'
Note 'PlayMode' "PASS(total=$($playCounts.Total),passed=$($playCounts.Passed),failed=$($playCounts.Failed),skipped=$($playCounts.Skipped),scoped=$($playCounts.Scoped)/$($playCounts.ScopedPassed) passed,exit=$playExit)"
if ($playCounts.Failed -ne 0 -or $playCounts.Skipped -ne 0 -or $playCounts.Total -eq 0 -or $playCounts.Passed -ne $playCounts.Total) {
    throw "PlayMode counts are not a clean pass: $($playCounts | ConvertTo-Json -Compress)"
}
if ($playCounts.Scoped -lt 6 -or $playCounts.ScopedPassed -ne $playCounts.Scoped) {
    throw "PlayMode scoped LocalHostCompositionPlayModeTests are not a clean expected pass: $($playCounts | ConvertTo-Json -Compress)"
}

# 6. Strict compiled boundary after the fresh Unity compile.
$strictLog = Join-Path $resolvedLogs 'strict-compiled.log'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $boundaryScript -StrictCompiled *> $strictLog
$strictExit = $LASTEXITCODE
if ($strictExit -ne 0) {
    Note 'StrictCompiled' "FAIL(exit=$strictExit)"
    throw "StrictCompiled boundary check failed (exit=$strictExit). See $strictLog"
}
Note 'StrictCompiled' "PASS(exit=$strictExit)"

# 7. Shared-core regression suites.
function Invoke-DotnetTests([string]$project, [string]$label, [string]$logPath) {
    # The TableRuntime test project is deliberately outside Mahjong.sln, so it
    # has no --no-build artifact from build-shared. Let dotnet build and run it;
    # an empty "0 tests" result must never be accepted as green.
    $text = (& $dotnet test $project -c Release --nologo 2>&1 | Out-String)
    $exit = $LASTEXITCODE
    $text | Out-File -LiteralPath $logPath -Encoding utf8
    Write-Host $text
    if ($text -notmatch 'Passed!') {
        throw "$label did not report a passed test run (exit=$exit)"
    }
    if ($null -eq $exit) { $exit = 0 }
    return [int]$exit
}

if (-not $SkipRegression) {
    $clientProject = Join-Path $repoRoot 'dotnet/tests/Mahjong.Client.Tests/Mahjong.Client.Tests.csproj'
    $clientLog = Join-Path $resolvedLogs 'client-tests.log'
    $clientExit = Invoke-DotnetTests $clientProject 'Client tests' $clientLog
    if ($clientExit -ne 0) { Note 'ClientTests' "FAIL(exit=$clientExit)"; throw "Client tests failed (exit=$clientExit)" }
    Note 'ClientTests' "PASS(exit=$clientExit)"

    $runtimeProject = Join-Path $repoRoot 'dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj'
    $runtimeLog = Join-Path $resolvedLogs 'tableruntime-tests.log'
    $runtimeExit = Invoke-DotnetTests $runtimeProject 'TableRuntime tests' $runtimeLog
    if ($runtimeExit -ne 0) { Note 'TableRuntimeTests' "FAIL(exit=$runtimeExit)"; throw "TableRuntime tests failed (exit=$runtimeExit)" }
    Note 'TableRuntimeTests' "PASS(exit=$runtimeExit)"
} else {
    Note 'ClientTests' 'NOT_RUN(-SkipRegression)'
    Note 'TableRuntimeTests' 'NOT_RUN(-SkipRegression)'
}

# 8. Player build + real local smoke.
$playerDir = Join-Path $resolvedOutput 'player'
$playerExe = Join-Path $playerDir 'Mahjong.exe'
$playerData = Join-Path $resolvedOutput 'player-data'
$playerShots = Join-Path $resolvedOutput 'screenshots'
if (-not $SkipPlayer) {
    if (Test-Path -LiteralPath $playerDir) { Remove-Item -LiteralPath $playerDir -Recurse -Force }
    if (Test-Path -LiteralPath $playerData) { Remove-Item -LiteralPath $playerData -Recurse -Force }
    if (Test-Path -LiteralPath $playerShots) { Remove-Item -LiteralPath $playerShots -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $playerDir | Out-Null
    New-Item -ItemType Directory -Force -Path $playerData | Out-Null
    New-Item -ItemType Directory -Force -Path $playerShots | Out-Null

    $playerBuildLog = Join-Path $resolvedLogs 'player-build.log'
    $psiBuild = New-Object System.Diagnostics.ProcessStartInfo
    $psiBuild.FileName = $resolvedUnity
    $psiBuild.Arguments = "-batchmode -quit -projectPath `"$resolvedProject`" -buildWindows64Player `"$playerExe`" -logFile `"$playerBuildLog`""
    $psiBuild.UseShellExecute = $false
    $psiBuild.CreateNoWindow = $true
    $procBuild = [System.Diagnostics.Process]::Start($psiBuild)
    if (-not $procBuild.WaitForExit(900000)) { $procBuild.Kill(); throw "Player build timed out" }
    if ($procBuild.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $playerExe)) {
        Note 'PlayerBuild' "FAIL(exit=$($procBuild.ExitCode), exe=$(Test-Path -LiteralPath $playerExe))"
        throw "Player build failed (exit=$($procBuild.ExitCode)). See $playerBuildLog"
    }
    Note 'PlayerBuild' "PASS(exit=$($procBuild.ExitCode))"

    $playerRunLog = Join-Path $resolvedLogs 'player-localhost.log'
    $psiRun = New-Object System.Diagnostics.ProcessStartInfo
    $psiRun.FileName = $playerExe
    $psiRun.Arguments = "-screen-width 1280 -screen-height 720 -screen-fullscreen 0 -logfile `"$playerRunLog`" -smoke-localhost `"$playerShots`" -smoke-data `"$playerData`""
    $psiRun.UseShellExecute = $false
    $procRun = [System.Diagnostics.Process]::Start($psiRun)
    if (-not $procRun.WaitForExit(180000)) {
        $procRun.Kill()
        Note 'PlayerSmoke' 'FAIL(timeout)'
        throw "Player local smoke timed out after 180 seconds. See $playerRunLog"
    }
    if ($procRun.ExitCode -ne 0) {
        Note 'PlayerSmoke' "FAIL(exit=$($procRun.ExitCode))"
        throw "Player local smoke failed (exit=$($procRun.ExitCode)). See $playerRunLog"
    }
    if (-not (Test-Path -LiteralPath $playerRunLog)) { throw "Player log missing: $playerRunLog" }
    $playerText = Get-Content -LiteralPath $playerRunLog -Raw
    if ($playerText -match 'SMOKE_ERROR:') {
        Note 'PlayerSmoke' 'FAIL(SMOKE_ERROR)'
        throw "Player log contains SMOKE_ERROR: $($matches[0])"
    }
    $required = @(
        'SCENE_LOAD_COMPLETED:Lobby',
        'SMOKE_STEP:LOCAL_START_CLICKED',
        'SCENE_LOAD_COMPLETED:Table',
        'SMOKE_STEP:LOCAL_TABLE_LOADED',
        'SMOKE_STEP:LOCAL_SNAPSHOT',
        'SMOKE_STEP:LOCAL_ACTION_SUBMITTED',
        'SMOKE_STEP:LOCAL_VISIBLE_EVENT',
        'SMOKE_STEP:LOCAL_RETURN_TO_MENU',
        'SMOKE_STEP:LOCAL_RETURNED_MENU',
        'SMOKE_SUCCESS: Application shutdown completed cleanly.'
    )
    $last = -1
    foreach ($step in $required) {
        $idx = $playerText.IndexOf($step, [Math]::Max(0, $last))
        if ($idx -lt 0) { Note 'PlayerSmoke' "FAIL(missing $step)"; throw "Player log missing ordered step '$step'" }
        $last = $idx + $step.Length
    }
    $shots = @('01-menu.png', '02-lobby.png', '03-local-table.png', '04-local-action.png')
    foreach ($shot in $shots) {
        $path = Join-Path $playerShots $shot
        if (-not (Test-Path -LiteralPath $path)) { throw "Player screenshot missing: $path" }
        if ((Get-Item -LiteralPath $path).Length -lt 1000) { throw "Player screenshot too small: $path" }
        $bytes = [System.IO.File]::ReadAllBytes($path)
        if ($bytes.Length -lt 8 -or $bytes[0] -ne 0x89 -or $bytes[1] -ne 0x50) { throw "Not a PNG: $path" }
    }
    Note 'PlayerSmoke' "PASS(exit=$($procRun.ExitCode))"
} else {
    Note 'PlayerBuild' 'NOT_RUN(-SkipPlayer)'
    Note 'PlayerSmoke' 'NOT_RUN(-SkipPlayer)'
}

# 9. Diff hygiene and evidence manifest.
$diffLog = Join-Path $resolvedLogs 'diff-check.log'
# Only the delivery scope is checked. Unity regenerates tracked scenes/SDF/URP
# assets while importing; those generated bytes are restored after the test run
# and must never be part of the source diff gate. New (untracked) scope files are
# checked separately below because `git diff --check` does not see them.
$scopePaths = @(
    'tools/unity/check-unity-boundaries.ps1',
    'tools/unity/p42-boundary-policy.json',
    'unity/Mahjong.Unity/Assets/Scripts/App/AppComposition.cs',
    'unity/Mahjong.Unity/Assets/Scripts/App/AppRoot.cs',
    'unity/Mahjong.Unity/Assets/Scripts/App/AppRouter.cs',
    'unity/Mahjong.Unity/Assets/Scripts/App/GameBootstrapper.cs',
    'unity/Mahjong.Unity/Assets/Scripts/App/Mahjong.Unity.App.asmdef',
    'unity/Mahjong.Unity/Assets/Scripts/UI/IAppNavigation.cs',
    'unity/Mahjong.Unity/Assets/Tests/EditMode/LocalHostCompositionTests.cs',
    'unity/Mahjong.Unity/Assets/Tests/PlayMode/LocalHostCompositionPlayModeTests.cs',
    # ASCII-only pathspec: Windows PowerShell 5.1 reads this file as ANSI and a
    # literal Chinese directory name would silently match nothing, making the
    # diff gate look green while skipping the report. The guard below proves the
    # pathspec actually selected the tracked report file.
    'doc/v5-*/V5-P4.2-B-LOCALHOST-COMPOSITION-DELIVERY.md'
)
$oldErrorAction = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    $diffDefault = (& git diff --check -- $scopePaths 2>&1 | Out-String)
    $defaultExit = $LASTEXITCODE
    $diffCr = (& git -c core.whitespace=cr-at-eol diff --check -- $scopePaths 2>&1 | Out-String)
    $crExit = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $oldErrorAction
}

$untrackedScope = @(
    'tools/unity/test-p42-localhost.ps1',
    'unity/Mahjong.Unity/Assets/Scripts/App/LocalTableChannelFactory.cs',
    'unity/Mahjong.Unity/Assets/Scripts/App/LocalTableChannelFactory.cs.meta'
)
$untrackedProblems = @()
foreach ($path in $untrackedScope) {
    $full = Join-Path $repoRoot $path
    if (-not (Test-Path -LiteralPath $full)) { $untrackedProblems += "MISSING $path"; continue }
    $lines = Get-Content -LiteralPath $full
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '[ \t]+$') { $untrackedProblems += "TRAILING_WS $path`:$($i + 1)" }
    }
    $bytes = [System.IO.File]::ReadAllBytes($full)
    for ($i = 0; $i -lt $bytes.Length - 1; $i++) {
        if ($bytes[$i] -eq 13 -and $bytes[$i + 1] -eq 10) {
            $untrackedProblems += "CRLF $path"
            break
        }
    }
}

@(
    "scope paths: $($scopePaths -join ', ')",
    "default exit=$defaultExit",
    $diffDefault,
    "cr-at-eol exit=$crExit",
    $diffCr,
    "untracked scope problems=$($untrackedProblems.Count)",
    $untrackedProblems
) | Out-File -LiteralPath $diffLog -Encoding utf8
if ($untrackedProblems.Count -gt 0) {
    Note 'DefaultDiffCheck' "FAIL(untracked scope whitespace/line-ending)"
    throw "Untracked scope whitespace/line-ending check failed: $($untrackedProblems -join '; ')"
}
if ($defaultExit -ne 0) { Note 'DefaultDiffCheck' "FAIL(exit=$defaultExit)"; throw 'git diff --check failed' }
if ($crExit -ne 0) { Note 'CrAtEolDiffCheck' "FAIL(exit=$crExit)"; throw 'git -c core.whitespace=cr-at-eol diff --check failed' }
Note 'DefaultDiffCheck' "PASS(exit=$defaultExit)"
Note 'CrAtEolDiffCheck' "PASS(exit=$crExit)"

# Scope diff, for the delivery report. Generated from the same scope paths as
# the checks above and included in the evidence manifest.
$scopeDiffLog = Join-Path $resolvedOutput 'scope-diff.txt'
$scopeStatus = @(& git status --porcelain -- $scopePaths $untrackedScope 2>&1)
$scopeStat = @(& git diff --stat -- $scopePaths 2>&1)
$docMatches = @(& git ls-files -- 'doc/v5-*/V5-P4.2-B-LOCALHOST-COMPOSITION-DELIVERY.md' 2>&1)
if ($docMatches.Count -ne 1) {
    throw "The ASCII delivery-report pathspec does not select exactly one tracked file (matched=$($docMatches.Count))."
}
@(
    "HEAD: $($summary.SourceHead)",
    "git status --porcelain (scope):",
    $scopeStatus,
    "",
    "git diff --stat (tracked scope):",
    $scopeStat
) | Out-File -LiteralPath $scopeDiffLog -Encoding utf8

$summary.EditModeCounts = $editCounts
$summary.PlayModeCounts = $playCounts
$summary.CompletedAtUtc = [DateTime]::UtcNow.ToString('o')
$summary | ConvertTo-Json -Depth 6 | Out-File -LiteralPath (Join-Path $resolvedOutput 'run-summary.json') -Encoding utf8

$manifest = Join-Path $resolvedOutput 'evidence-manifest.csv'
Get-ChildItem -LiteralPath $resolvedOutput -Recurse -File |
    Where-Object { $_.FullName -ne $manifest } |
    ForEach-Object {
        [pscustomobject]@{
            Path = $_.FullName
            Length = $_.Length
            SHA256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToUpperInvariant()
        }
    } | Sort-Object Path | Export-Csv -LiteralPath $manifest -NoTypeInformation -Encoding utf8
Write-Host "EVIDENCE_MANIFEST=$manifest"
Write-Host "MANIFEST_SHA256=$((Get-FileHash -Algorithm SHA256 -LiteralPath $manifest).Hash.ToUpperInvariant())"

Write-Host "=========================================================="
$summary.GetEnumerator() | ForEach-Object { Write-Host ("{0,-24} : {1}" -f $_.Key, $_.Value) }
Write-Host "P42B_LOCALHOST_ACTIVATION: PASS"
exit 0
