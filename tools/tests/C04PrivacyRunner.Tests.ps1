#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$TestRunRoot = ''
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$runner = Join-Path $repo 'tools/unity/run-p45-c04-privacy-matrix.ps1'
if (-not (Test-Path -LiteralPath $runner -PathType Leaf)) { throw "Runner is missing: $runner" }
$script:TestRoot = $TestRunRoot
if ([string]::IsNullOrWhiteSpace($script:TestRoot)) {
    $script:TestRoot = Join-Path $repo ('build/runner-tests-' + [guid]::NewGuid().ToString('N'))
}
$script:TestRoot = [IO.Path]::GetFullPath($script:TestRoot)
if (Test-Path -LiteralPath $script:TestRoot) { throw 'Runner test root exists; refusing to overwrite evidence.' }
[IO.Directory]::CreateDirectory($script:TestRoot) | Out-Null

. $runner -LoadFunctionsOnly

$script:Tests = 0
$script:Failures = New-Object Collections.Generic.List[string]
$script:Results = New-Object Collections.Generic.List[object]

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Assert-Equal($Expected, $Actual, [string]$Message) {
    if (-not [object]::Equals($Expected, $Actual)) { throw "$Message expected=$Expected actual=$Actual" }
}
function Assert-Throws([scriptblock]$Action, [string]$MessagePart) {
    $caught = $null
    try { & $Action } catch { $caught = $_ }
    if ($null -eq $caught) { throw "Expected failure was not raised: $MessagePart" }
    if ($caught.Exception.Message -notlike ('*' + $MessagePart + '*')) {
        throw "Failure did not contain '$MessagePart': $($caught.Exception.Message)"
    }
}
function New-TestDirectory([string]$Name) {
    $path = Join-Path $script:TestRoot $Name
    [IO.Directory]::CreateDirectory($path) | Out-Null
    return $path
}
function Invoke-Case([string]$Name, [scriptblock]$Body) {
    $script:Tests++
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try {
        & $Body
        $script:Results.Add([pscustomobject]@{ name = $Name; result = 'PASS'; seconds = [Math]::Round($sw.Elapsed.TotalSeconds, 3); error = $null })
        Write-Host "PASS $Name"
    } catch {
        $script:Failures.Add($Name + ': ' + $_.Exception.Message)
        $script:Results.Add([pscustomobject]@{ name = $Name; result = 'FAIL'; seconds = [Math]::Round($sw.Elapsed.TotalSeconds, 3); error = $_.Exception.Message })
        Write-Host "FAIL $Name : $($_.Exception.Message)"
    }
}

function New-FixturePlayer {
    param([string]$Root, [string]$SourceHead = ('a' * 40))
    $playerRoot = Join-Path $Root 'player'
    [IO.Directory]::CreateDirectory($playerRoot) | Out-Null
    $exe = Join-Path $playerRoot 'Mahjong.exe'
    [IO.File]::WriteAllBytes($exe, [Text.Encoding]::UTF8.GetBytes('fixture-player-' + [guid]::NewGuid().ToString('N')))
    $managed = Join-Path $playerRoot 'Mahjong_Data/Managed'
    [IO.Directory]::CreateDirectory($managed) | Out-Null
    $plugins = [ordered]@{}
    foreach ($name in $script:C04RequiredPlugins) {
        $file = Join-Path $managed $name
        [IO.File]::WriteAllBytes($file, [Text.Encoding]::UTF8.GetBytes('fixture-' + $name + '-' + [guid]::NewGuid().ToString('N')))
        $plugins[$name] = (Get-C04Sha256File $file)
    }
    $manifestPath = Join-Path $playerRoot 'player-package.sha256'
    $files = @(Get-ChildItem -LiteralPath $playerRoot -Recurse -File | Where-Object { $_.FullName -ne $manifestPath } | Sort-Object FullName)
    $lines = New-Object Collections.Generic.List[string]
    [long]$bytes = 0
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($playerRoot.Length).TrimStart('\', '/') -replace '\\', '/'
        $lines.Add((Get-C04Sha256File $file.FullName) + '  ' + $relative)
        $bytes += $file.Length
    }
    Write-C04Utf8 $manifestPath (($lines -join "`n") + "`n")
    $marker = [ordered]@{
        schemaVersion = 1
        kind = 'c04-privacy-player-build'
        result = 'Succeeded'
        errors = 0
        warnings = 0
        target = 'StandaloneWindows64'
        output = $exe
        playerSha256 = (Get-C04Sha256File $exe)
        playerBytes = (Get-Item -LiteralPath $exe).Length
        packageFileCount = $files.Count
        packageBytes = $bytes
        packageManifest = $manifestPath
        packageManifestSha256 = (Get-C04Sha256File $manifestPath)
        bytes = $bytes
        sourceHead = $SourceHead
        buildCommit = $SourceHead
        errorMessages = @()
        scenes = @($script:C04RequiredScenes)
        plugins = $plugins
    }
    $markerPath = Join-Path $Root 'player-build.json'
    Write-C04Json $markerPath $marker
    return [pscustomobject]@{ root = $playerRoot; markerPath = $markerPath; marker = $marker; exe = $exe; manifest = $manifestPath }
}

function Write-MarkerVariant($Fixture, [string]$Name, [scriptblock]$Mutate) {
    $copy = $Fixture.marker | ConvertTo-Json -Depth 12 | ConvertFrom-Json
    & $Mutate $copy
    $path = Join-Path $Fixture.root ($Name + '.json')
    Write-C04Json $path $copy
    return $path
}

function Write-TestScript([string]$Path, [string]$Body) {
    Write-C04Utf8 $Path ($Body + "`n")
}

Invoke-Case 'source identity accepts clean frozen head and rejects dirty/mismatch' {
    $head = '0123456789abcdef0123456789abcdef01234567'
    $identity = Assert-C04SourceIdentity -Head $head -DirtyEntries @() -DiffCheckExit 0 -ExpectedHead $head
    Assert-Equal $head $identity.head 'clean head'
    Assert-Throws { Assert-C04SourceIdentity -Head $head -DirtyEntries @(' M file.cs') -DiffCheckExit 0 } 'dirty'
    Assert-Throws { Assert-C04SourceIdentity -Head $head -DirtyEntries @() -DiffCheckExit 1 } 'diff --check'
    Assert-Throws { Assert-C04SourceIdentity -Head $head -DirtyEntries @() -ExpectedHead ('b' * 40) } 'does not match'
}

Invoke-Case 'copy input closure is byte-identical and detects destination mutation' {
    $fake = New-TestDirectory 'copy-repo'
    $unity = Join-Path $fake 'unity/Mahjong.Unity'
    foreach ($name in @('Assets', 'Packages', 'ProjectSettings')) {
        $dir = Join-Path $unity $name
        [IO.Directory]::CreateDirectory($dir) | Out-Null
        Write-C04Utf8 (Join-Path $dir ($name + '.txt')) ('source-' + $name)
    }
    $destination = Join-Path (New-TestDirectory 'copy-out') 'staging'
    $copied = Copy-C04UnityInputs -Repo $fake -Destination $destination
    Assert-Equal $copied.source.sha256 $copied.staged.sha256 'copy hash'
    Assert-Equal $copied.source.count $copied.staged.count 'copy count'
    $stagedManifest = Get-C04SelectedManifest -Base $destination -RelativeRoots @('Assets', 'Packages', 'ProjectSettings')
    Assert-C04ManifestEqual $copied.source $stagedManifest 'staged'
    Write-C04Utf8 (Join-Path $destination 'Assets/Assets.txt') 'mutated'
    $mutated = Get-C04SelectedManifest -Base $destination -RelativeRoots @('Assets', 'Packages', 'ProjectSettings')
    Assert-Throws { Assert-C04ManifestEqual $copied.source $mutated 'staged' } 'input closure mismatch'
}

Invoke-Case 'Unity XML accepts only the exact passed case and denominator' {
    $dir = New-TestDirectory 'xml'
    $expected = 'Mahjong.Unity.Tests.PlayMode.C04PrivacyRoleSwitchPlayModeTests.RealRoom_FourPlayersSpectatorAndRoleSwitchUseIndependentSessions'
    $valid = @"
<?xml version="1.0" encoding="utf-8"?>
<test-run result="Passed" total="1" passed="1" failed="0" skipped="0" inconclusive="0" errors="0" not-run="0">
  <test-suite result="Passed" total="1" passed="1" failed="0" skipped="0">
    <test-case fullname="$expected" name="RealRoom" result="Passed" />
  </test-suite>
</test-run>
"@
    $validPath = Join-Path $dir 'valid.xml'
    Write-C04Utf8 $validPath $valid
    $parsed = Assert-C04UnityXml -XmlPath $validPath -ExpectedTests @($expected)
    Assert-Equal 1 $parsed.total 'valid total'
    Assert-Equal 1 $parsed.passed 'valid passed'
    $variants = @{
        skipped = ($valid -replace 'skipped="0"', 'skipped="7"' -replace 'total="1" passed="1" failed="0" skipped="7"', 'total="8" passed="1" failed="0" skipped="7"')
        errors = ($valid -replace 'errors="0"', 'errors="3"')
        failed = ($valid -replace 'failed="0"', 'failed="1"')
        wrongCase = ($valid -replace 'RealRoom_FourPlayersSpectatorAndRoleSwitchUseIndependentSessions', 'SomeOtherTest')
        wrongResult = ($valid -replace 'result="Passed"', 'result="Failed"')
        notrun = ($valid -replace 'not-run="0"', 'not-run="1"')
    }
    foreach ($pair in $variants.GetEnumerator()) {
        $path = Join-Path $dir ($pair.Key + '.xml')
        Write-C04Utf8 $path $pair.Value
        Assert-Throws { Assert-C04UnityXml -XmlPath $path -ExpectedTests @($expected) } 'Unity XML'
    }
    Assert-Throws { Assert-C04UnityXml -XmlPath (Join-Path $dir 'missing.xml') -ExpectedTests @($expected) } 'missing'
}

Invoke-Case 'build marker binds source, player, package and plugin closure' {
    $fixture = New-FixturePlayer -Root (New-TestDirectory 'marker-valid')
    $valid = Assert-C04BuildMarker -MarkerPath $fixture.markerPath -ExpectedSourceHead $fixture.marker.sourceHead -ExpectedBuildCommit $fixture.marker.buildCommit
    Assert-Equal $fixture.marker.playerSha256 $valid.playerSha256 'player sha'
    Assert-Equal $fixture.marker.packageFileCount $valid.packageFileCount 'package count'
    Assert-Equal $fixture.marker.packageBytes $valid.packageBytes 'package bytes'

    $wrongSource = Write-MarkerVariant $fixture 'wrong-source' { param($m) $m.sourceHead = ('b' * 40) }
    Assert-Throws { Assert-C04BuildMarker -MarkerPath $wrongSource -ExpectedSourceHead $fixture.marker.sourceHead -ExpectedBuildCommit $fixture.marker.buildCommit } 'sourceHead'
    $errors = Write-MarkerVariant $fixture 'marker-errors' { param($m) $m.errors = 3 }
    Assert-Throws { Assert-C04BuildMarker -MarkerPath $errors -ExpectedSourceHead $fixture.marker.sourceHead -ExpectedBuildCommit $fixture.marker.buildCommit } 'errors'
    $wrongBytes = Write-MarkerVariant $fixture 'marker-bytes' { param($m) $m.playerBytes = 1 }
    Assert-Throws { Assert-C04BuildMarker -MarkerPath $wrongBytes -ExpectedSourceHead $fixture.marker.sourceHead -ExpectedBuildCommit $fixture.marker.buildCommit } 'playerBytes'
    $wrongPlugin = Write-MarkerVariant $fixture 'marker-plugin' { param($m) $m.plugins.'Mahjong.LocalHost.dll' = ('0' * 64) }
    Assert-Throws { Assert-C04BuildMarker -MarkerPath $wrongPlugin -ExpectedSourceHead $fixture.marker.sourceHead -ExpectedBuildCommit $fixture.marker.buildCommit } 'plugin hash'
    $missingScene = Write-MarkerVariant $fixture 'marker-scene' { param($m) $m.scenes = @($m.scenes | Where-Object { $_ -ne 'Assets/Scenes/Boot.unity' }) }
    Assert-Throws { Assert-C04BuildMarker -MarkerPath $missingScene -ExpectedSourceHead $fixture.marker.sourceHead -ExpectedBuildCommit $fixture.marker.buildCommit } 'required scene'

    $fake = New-FixturePlayer -Root (New-TestDirectory 'marker-missing-file')
    Remove-Item -LiteralPath (Join-Path $fake.root 'Mahjong_Data/Managed/Mahjong.LocalHost.dll') -Force
    Assert-Throws { Assert-C04BuildMarker -MarkerPath $fake.markerPath -ExpectedSourceHead $fake.marker.sourceHead -ExpectedBuildCommit $fake.marker.buildCommit } 'plugin is missing'
}

Invoke-Case 'owned process drains large stdout/stderr and propagates native nonzero' {
    $dir = New-TestDirectory 'owned-process'
    $scriptPath = Join-Path $dir 'write-large.ps1'
    Write-TestScript $scriptPath '[Console]::Out.Write(("o" * 1048576)); [Console]::Error.Write(("e" * 1048576)); exit 7'
    $result = Invoke-C04OwnedProcess -Name 'large-output' -FileName 'powershell.exe' -Arguments @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $scriptPath) -WorkingDirectory $dir -TimeoutSeconds 30 -CloseGraceSeconds 5 -EvidenceDirectory $dir -HeartbeatSeconds 5
    Assert-Equal 7 $result.exitCode 'native exit'
    Assert-True (-not $result.timedOut) 'large output timed out'
    Assert-True (-not $result.aliveAfter) 'large output writer survived'
    Assert-True ((Get-Item -LiteralPath $result.stdoutPath).Length -ge 1048576) 'stdout was not fully drained'
    Assert-True ((Get-Item -LiteralPath $result.stderrPath).Length -ge 1048576) 'stderr was not fully drained'
    Assert-Throws { Assert-C04NativeSuccess $result 'large-output' } 'native exit 7'
}

Invoke-Case 'bounded timeout stops only the owned writer and leaves no residual process' {
    $dir = New-TestDirectory 'owned-timeout'
    $scriptPath = Join-Path $dir 'sleep.ps1'
    Write-TestScript $scriptPath 'Start-Sleep -Seconds 60'
    $result = Invoke-C04OwnedProcess -Name 'timeout-helper' -FileName 'powershell.exe' -Arguments @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $scriptPath) -WorkingDirectory $dir -TimeoutSeconds 1 -CloseGraceSeconds 2 -EvidenceDirectory $dir -HeartbeatSeconds 1
    Assert-True $result.timedOut 'timeout was not recorded'
    Assert-True (-not $result.aliveAfter) 'timed-out writer is still alive'
    Assert-True ($result.elapsedSeconds -lt 20) 'timeout exceeded its bound'
    Assert-Throws { Assert-C04NativeSuccess $result 'timeout-helper' } 'timed out'
    $stillThere = $true
    try { $null = Get-Process -Id $result.pid -ErrorAction Stop } catch { $stillThere = $false }
    Assert-True (-not $stillThere) 'timed-out owned PID still exists'
}

Invoke-Case 'normal cancellation file is bounded and leaves no residual writer' {
    $dir = New-TestDirectory 'owned-cancel'
    $scriptPath = Join-Path $dir 'sleep.ps1'
    Write-TestScript $scriptPath 'Start-Sleep -Seconds 60'
    $cancel = Join-Path $dir 'cancel.flag'
    $job = Start-Job -ScriptBlock {
        param($path) Start-Sleep -Seconds 1; [IO.File]::WriteAllText($path, 'cancel')
    } -ArgumentList $cancel
    $result = Invoke-C04OwnedProcess -Name 'cancel-helper' -FileName 'powershell.exe' -Arguments @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $scriptPath) -WorkingDirectory $dir -TimeoutSeconds 20 -CancelFile $cancel -CloseGraceSeconds 2 -EvidenceDirectory $dir -HeartbeatSeconds 1
    Wait-Job $job -Timeout 10 | Out-Null
    Remove-Job $job -Force
    Assert-True $result.cancelled 'cancel was not recorded'
    Assert-True (-not $result.aliveAfter) 'cancelled writer is still alive'
    Assert-Throws { Assert-C04NativeSuccess $result 'cancel-helper' } 'cancelled'
}

Invoke-Case 'mutex wait timeout does not acquire or release another process lock' {
    $name = 'Local\C04RunnerTests-' + [guid]::NewGuid().ToString('N')
    $dir = New-TestDirectory 'mutex'
    $ready = Join-Path $dir 'ready.flag'
    $holderScript = Join-Path $dir 'holder.ps1'
    $holderBody = '$m = New-Object Threading.Mutex($false, ''' + $name + '''); $null = $m.WaitOne(); [IO.File]::WriteAllText(''' + $ready + ''', ''ready''); Start-Sleep -Seconds 8'
    Write-TestScript $holderScript $holderBody
    $holder = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $holderScript) -PassThru -WindowStyle Hidden
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(5)
        while (-not (Test-Path -LiteralPath $ready) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 50 }
        Assert-True (Test-Path -LiteralPath $ready) 'mutex holder did not become ready'
        Assert-Throws { Acquire-C04Mutex -Name $name -TimeoutSeconds 1 -HeartbeatSeconds 1 } 'mutex wait exceeded'
    } finally {
        if (-not $holder.HasExited) { $holder.Kill(); $null = $holder.WaitForExit(5000) }
        $holder.Dispose()
    }
}

Invoke-Case 'evidence seal is stable and refuses a second seal' {
    $dir = New-TestDirectory 'seal'
    Write-C04Utf8 (Join-Path $dir 'raw.txt') 'stable'
    $summary = [ordered]@{ schemaVersion = 1; status = 'PASS'; runRoot = $dir; finishedUtc = [DateTimeOffset]::UtcNow.ToString('o') }
    $first = Write-C04SealedEvidence -RunRoot $dir -Summary $summary
    Assert-True (Test-Path -LiteralPath (Join-Path $dir 'summary.json')) 'summary missing'
    Assert-True (Test-Path -LiteralPath (Join-Path $dir 'manifest.sha256')) 'manifest missing'
    Assert-True (Test-Path -LiteralPath (Join-Path $dir 'seal.json')) 'seal missing'
    $hashBefore = Get-C04Sha256File (Join-Path $dir 'manifest.sha256')
    Start-Sleep -Milliseconds 200
    $hashAfter = Get-C04Sha256File (Join-Path $dir 'manifest.sha256')
    Assert-Equal $hashBefore $hashAfter 'manifest changed after seal'
    Assert-Throws { Write-C04SealedEvidence -RunRoot $dir -Summary $summary } 'already sealed'
}

$summary = [ordered]@{
    schemaVersion = 1
    kind = 'c04-privacy-runner-tests'
    result = $(if ($script:Failures.Count -eq 0) { 'PASS' } else { 'FAIL' })
    tests = $script:Tests
    failures = $script:Failures.Count
    cases = $script:Results
    runRoot = $script:TestRoot
    finishedUtc = [DateTimeOffset]::UtcNow.ToString('o')
}
Write-C04Json (Join-Path $script:TestRoot 'runner-tests.json') $summary
if ($script:Failures.Count -ne 0) {
    $script:Failures | ForEach-Object { Write-Host "FAILURE: $_" }
    Write-Host "C04_PRIVACY_RUNNER_TESTS_FAIL tests=$($script:Tests) failures=$($script:Failures.Count)"
    exit 1
}
Write-Host "C04_PRIVACY_RUNNER_TESTS_PASS tests=$($script:Tests) root=$script:TestRoot"
exit 0
