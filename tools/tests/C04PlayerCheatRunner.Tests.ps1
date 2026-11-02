<#
    SCRIPT_TOOL_FIXTURE negative matrix for D04-C/R1-06 runner mechanisms.
    These tests exercise real PowerShell/.NET processes, the package/evidence
    sealing functions and BuildReport attribution. They do not run Unity, the
    Player or a game.
#>
$ErrorActionPreference = 'Stop'
$runner = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'tools\unity\run-p45-c04-player-cheats.ps1'
. $runner -SelfTest

$passed = 0
$failures = New-Object System.Collections.Generic.List[string]
$testRoot = Join-Path $root ('build\tool-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
$powershell = (Get-Command powershell.exe).Source

function Assert-Equal {
    param($Actual, $Expected, [string]$Label)
    if ($Actual -eq $Expected) { $script:passed++ }
    else { $script:failures.Add("$Label expected=[$Expected] actual=[$Actual]") | Out-Null }
}

function Assert-True {
    param($Condition, [string]$Label)
    if ($Condition) { $script:passed++ }
    else { $script:failures.Add("$Label condition was false") | Out-Null }
}

function Assert-Contains {
    param([object[]]$Values, [string]$Expected, [string]$Label)
    if (@($Values) -contains $Expected) { $script:passed++ }
    else { $script:failures.Add("$Label expected=$Expected actual=$(@($Values) -join ',')") | Out-Null }
}

function Assert-Match {
    param([object[]]$Values, [string]$Pattern, [string]$Label)
    if (@($Values | Where-Object { [string]$_ -match $Pattern }).Count -gt 0) { $script:passed++ }
    else { $script:failures.Add("$Label no value matched $Pattern actual=$(@($Values) -join ',')") | Out-Null }
}

function New-FixtureReport {
    param(
        [string]$OutputPath,
        [int]$Errors = 0,
        [string]$Result = 'Succeeded',
        [string[]]$StepMessages = @()
    )
    $steps = @([ordered]@{
        name = 'Preprocess Player'
        depth = 0
        durationSeconds = 1.0
        messages = @($StepMessages | ForEach-Object { [ordered]@{ type = 'Error'; content = $_ } })
    })
    if ($StepMessages.Count -eq 0) {
        $steps[0].messages = @([ordered]@{ type = 'Log'; content = 'ok' })
    }
    $outputFull = Get-C04FullPath $OutputPath
    $bytes = (Get-Item -LiteralPath $outputFull).Length
    return [ordered]@{
        schema = 1
        result = $Result
        errors = $Errors
        warnings = 0
        target = 'StandaloneWindows64'
        output = $outputFull
        bytes = $bytes
        outputSha256 = Get-C04Sha256 $outputFull
        outputExists = $true
        startedUtc = '2026-11-03T00:00:00.0000000Z'
        finishedUtc = '2026-11-03T00:01:00.0000000Z'
        durationSeconds = 60.0
        unityVersion = '6000.3.27f1'
        productName = 'Mahjong Cheater'
        exception = ''
        scenes = @('Assets/Scenes/Boot.unity','Assets/Scenes/Menu.unity','Assets/Scenes/Lobby.unity','Assets/Scenes/Table.unity','Assets/Scenes/Spectator.unity','Assets/Scenes/Settlement.unity')
        plugins = @()
        stepCount = 1
        stepErrorCount = $StepMessages.Count
        stepExceptionCount = 0
        stepWarningCount = 0
        stepDiagnostics = @($StepMessages)
        steps = $steps
    }
}

function New-PluginFixture {
    param([string]$Dir)
    New-Item -ItemType Directory -Force -Path $Dir | Out-Null
    $required = @('Mahjong.Domain.dll','Mahjong.Contracts.dll','Mahjong.Contracts.Wire.dll','Mahjong.TableRuntime.dll','Mahjong.Client.dll','Mahjong.LocalHost.dll','Mahjong.Ai.dll','Google.Protobuf.dll')
    $outputs = New-Object System.Collections.Generic.List[object]
    foreach ($name in $required) {
        $path = Join-Path $Dir $name
        [System.IO.File]::WriteAllText($path, "fixture-$name", (New-Object System.Text.UTF8Encoding($false)))
        $outputs.Add([ordered]@{ Name = $name; Length = (Get-Item -LiteralPath $path).Length; Sha256 = (Get-C04Sha256 $path); SourceKind = 'project'; SourcePath = 'fixture'; PluginPath = 'fixture'; Project = $name.Replace('.dll','') }) | Out-Null
    }
    $provenance = [ordered]@{
        schemaVersion = 2
        kind = 'mahjong-unity-shared-plugin-provenance'
        evidenceScope = 'scratch'
        build = [ordered]@{ exitCode = 0 }
        inputs = @()
        outputs = $outputs.ToArray()
    }
    $provenancePath = Join-Path $Dir 'shared-plugin-provenance.json'
    Write-C04Json -Path $provenancePath -Object $provenance
    return [pscustomobject]@{ dir = $Dir; provenance = $provenancePath; outputs = $outputs.ToArray() }
}

function New-PackageFixture {
    param([string]$Dir)
    New-Item -ItemType Directory -Force -Path $Dir | Out-Null
    $exe = Join-Path $Dir 'Mahjong.exe'
    [System.IO.File]::WriteAllText($exe, 'fixture-player', (New-Object System.Text.UTF8Encoding($false)))
    $dataDir = Join-Path $Dir 'Mahjong_Data'
    New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $dataDir 'globalgamemanagers'), 'fixture-data', (New-Object System.Text.UTF8Encoding($false)))
    $plugin = New-PluginFixture -Dir (Join-Path $Dir 'plugins')
    $closure = Get-C04PluginClosure -ProvenancePath $plugin.provenance -PluginDir $plugin.dir
    Assert-True $closure.ok 'package-fixture plugin closure'
    $provenanceCopy = Join-Path $Dir 'c04-plugin-provenance.json'
    Copy-Item -LiteralPath $plugin.provenance -Destination $provenanceCopy -Force
    $reportPath = Join-Path $Dir 'c04-build-report.json'
    Write-C04Json -Path $reportPath -Object (New-FixtureReport -OutputPath $exe)
    $seal = Write-C04PackageSeal -PackageRoot $Dir -PlayerPath $exe -ReportPath $reportPath -ProvenancePath $provenanceCopy -SourceHead ('a' * 40) -SourceIdentitySha256 ('b' * 64) -BuildCommit 'c' -PluginClosure $closure
    return [pscustomobject]@{ dir = $Dir; exe = $exe; report = $reportPath; provenance = $provenanceCopy; seal = $seal; closure = $closure }
}

# 1. Windows command-line quoting
Assert-Equal (ConvertTo-C04QuotedArgument 'plain') 'plain' 'quote-plain'
Assert-Equal (ConvertTo-C04QuotedArgument 'C:\a b\c') '"C:\a b\c"' 'quote-spaces'
Assert-Equal (ConvertTo-C04QuotedArgument '') '""' 'quote-empty'
Assert-Equal (ConvertTo-C04QuotedArgument 'a"b') '"a\"b"' 'quote-embedded-quote'
Assert-Equal (ConvertTo-C04QuotedArgument 'a b\') '"a b\\"' 'quote-trailing-backslash'

# 2. Owned bounded process: success, nonzero, dual stream drain
$outPath = Join-Path $testRoot 'ok.out'
$errPath = Join-Path $testRoot 'ok.err'
$ok = Start-C04BoundedProcess -FilePath $powershell -Arguments @('-NoProfile', '-Command', 'Write-Output "OUT"; [Console]::Error.WriteLine("ERR"); exit 0') -TimeoutSeconds 30 -StdOutPath $outPath -StdErrPath $errPath -Label 'fixture-ok' -DrainGrace 5 -Heartbeat 0
Assert-Equal $ok.exitCode 0 'process-success-exit'
Assert-True $ok.exitCodeKnown 'process-success-known'
Assert-True $ok.drainComplete 'process-success-drained'
Assert-True ((Get-Content -LiteralPath $outPath -Raw) -match 'OUT') 'process-success-stdout'
Assert-True ((Get-Content -LiteralPath $errPath -Raw) -match 'ERR') 'process-success-stderr'

$nonzero = Start-C04BoundedProcess -FilePath $powershell -Arguments @('-NoProfile', '-Command', 'exit 7') -TimeoutSeconds 30 -StdOutPath (Join-Path $testRoot 'nz.out') -StdErrPath (Join-Path $testRoot 'nz.err') -Label 'fixture-nonzero' -DrainGrace 5 -Heartbeat 0
Assert-Equal $nonzero.exitCode 7 'process-nonzero-exit'
Assert-True (-not $nonzero.timedOut) 'process-nonzero-not-timeout'

$bigOut = Join-Path $testRoot 'big.out'
$bigErr = Join-Path $testRoot 'big.err'
$big = Start-C04BoundedProcess -FilePath $powershell -Arguments @('-NoProfile', '-Command', '[Console]::Out.Write(("x" * 500000)); [Console]::Error.Write(("y" * 500000)); exit 0') -TimeoutSeconds 60 -StdOutPath $bigOut -StdErrPath $bigErr -Label 'fixture-big' -DrainGrace 10 -Heartbeat 0
Assert-True $big.drainComplete 'process-big-drained'
Assert-True ((Get-Item -LiteralPath $bigOut).Length -gt 400000) 'process-big-stdout'
Assert-True ((Get-Item -LiteralPath $bigErr).Length -gt 400000) 'process-big-stderr'

# 3. Quoted path argument arrives byte-exact
$spaceDir = Join-Path $testRoot 'c04 space dir'
New-Item -ItemType Directory -Force -Path $spaceDir | Out-Null
$spacePath = Join-Path $spaceDir 'a file.txt'
[System.IO.File]::WriteAllText($spacePath, 'x')
$echoScript = Join-Path $testRoot 'echo-arg.ps1'
[System.IO.File]::WriteAllText($echoScript, 'param([string]$Value) Write-Output $Value', (New-Object System.Text.UTF8Encoding($false)))
$echoOut = Join-Path $testRoot 'echo.out'
$echo = Start-C04BoundedProcess -FilePath $powershell -Arguments @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $echoScript, '-Value', $spacePath) -TimeoutSeconds 30 -StdOutPath $echoOut -StdErrPath (Join-Path $testRoot 'echo.err') -Label 'fixture-quoted' -DrainGrace 5 -Heartbeat 0
Assert-Equal ((Get-Content -LiteralPath $echoOut -Raw).Trim()) $spacePath 'process-quoted-path'

# 4. Bounded timeout kills only the owned root
$timeout = Start-C04BoundedProcess -FilePath $powershell -Arguments @('-NoProfile', '-Command', 'Start-Sleep -Seconds 30') -TimeoutSeconds 1 -StdOutPath (Join-Path $testRoot 'to.out') -StdErrPath (Join-Path $testRoot 'to.err') -Label 'fixture-timeout' -DrainGrace 3 -Heartbeat 0
Assert-True $timeout.timedOut 'process-timeout-flag'
Assert-True (-not $timeout.exitCodeKnown) 'process-timeout-exit-unknown'
Assert-True ($timeout.durationSeconds -lt 20) 'process-timeout-bounded'
$remaining = Get-Process -Id $timeout.pid -ErrorAction SilentlyContinue
Assert-True ($null -eq $remaining) 'process-timeout-root-gone'

# 5. Parent exits while an inherited-pipe descendant is still alive
$descendantScript = Join-Path $testRoot 'descendant-writer.ps1'
$marker = Join-Path $testRoot 'descendant.pid'
$descendantText = @'
param([string]$MarkerPath)
$child = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-Command','Start-Sleep -Seconds 30') -PassThru -NoNewWindow
[System.IO.File]::WriteAllText($MarkerPath, [string]$child.Id)
Write-Output "child=$($child.Id)"
'@
[System.IO.File]::WriteAllText($descendantScript, $descendantText, (New-Object System.Text.UTF8Encoding($false)))
$descendant = Start-C04BoundedProcess -FilePath $powershell -Arguments @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $descendantScript, '-MarkerPath', $marker) -TimeoutSeconds 45 -StdOutPath (Join-Path $testRoot 'desc.out') -StdErrPath (Join-Path $testRoot 'desc.err') -Label 'fixture-descendant' -DrainGrace 2 -Heartbeat 0
Assert-True $descendant.descendantWriter 'process-descendant-detected'
Assert-True $descendant.drainComplete 'process-descendant-drained'
Assert-True ($descendant.ownedDescendantsStopped -ge 1) 'process-descendant-stopped'
if (Test-Path -LiteralPath $marker) {
    $childId = [int](Get-Content -LiteralPath $marker -Raw)
    Assert-True ($null -eq (Get-Process -Id $childId -ErrorAction SilentlyContinue)) 'process-descendant-gone'
}

# 6. Cancellation and unrelated foreign process survival
$sentinel = Start-Process -FilePath $powershell -ArgumentList @('-NoProfile', '-Command', 'Start-Sleep -Seconds 30') -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 300
$cancelFile = Join-Path $testRoot 'cancel.flag'
$cancelRunner = Start-Process -FilePath $powershell -ArgumentList @('-NoProfile', '-Command', "Start-Sleep -Milliseconds 800; [System.IO.File]::WriteAllText('$($cancelFile.Replace('\','\\'))','x')") -PassThru -WindowStyle Hidden
$cancel = Start-C04BoundedProcess -FilePath $powershell -Arguments @('-NoProfile', '-Command', 'Start-Sleep -Seconds 30') -TimeoutSeconds 60 -CancelFilePath $cancelFile -StdOutPath (Join-Path $testRoot 'cancel.out') -StdErrPath (Join-Path $testRoot 'cancel.err') -Label 'fixture-cancel' -DrainGrace 3 -Heartbeat 0
Assert-True $cancel.cancelled 'process-cancel-flag'
Assert-True (-not $cancel.timedOut) 'process-cancel-not-timeout'
Assert-True ($null -ne (Get-Process -Id $sentinel.Id -ErrorAction SilentlyContinue)) 'process-foreign-sentinel-survives'
try { Stop-Process -Id $sentinel.Id -Force -ErrorAction SilentlyContinue } catch { }

# 7. Evidence seal: success, extra file, changed file, missing file, manifest tamper
function New-EvidenceFixture {
    param([string]$Dir)
    New-Item -ItemType Directory -Force -Path $Dir | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $Dir 'a.txt'), 'a')
    [System.IO.File]::WriteAllText((Join-Path $Dir 'b.txt'), 'b')
    return (Write-C04EvidenceSeal -EvidenceRoot $Dir -Status 'PASS' -SourceHead ('a' * 40))
}
$sealDir = Join-Path $testRoot 'seal'
$null = New-EvidenceFixture -Dir $sealDir
Assert-Equal (@(Test-C04EvidenceSeal -EvidenceRoot $sealDir)).Count 0 'seal-valid'
[System.IO.File]::WriteAllText((Join-Path $sealDir 'late.txt'), 'late')
Assert-Contains (Test-C04EvidenceSeal -EvidenceRoot $sealDir) 'EVIDENCE_EXTRA:late.txt' 'seal-extra-after-write'
Remove-Item -LiteralPath (Join-Path $sealDir 'late.txt') -Force
[System.IO.File]::WriteAllText((Join-Path $sealDir 'a.txt'), 'changed')
Assert-Contains (Test-C04EvidenceSeal -EvidenceRoot $sealDir) 'EVIDENCE_HASH:a.txt' 'seal-hash-change'
Remove-Item -LiteralPath (Join-Path $sealDir 'b.txt') -Force
Assert-Contains (Test-C04EvidenceSeal -EvidenceRoot $sealDir) 'EVIDENCE_MISSING:b.txt' 'seal-missing'
[System.IO.File]::WriteAllText((Join-Path $sealDir 'b.txt'), 'b')
Add-Content -LiteralPath (Join-Path $sealDir 'manifest.csv') -Value 'tamper'
Assert-Contains (Test-C04EvidenceSeal -EvidenceRoot $sealDir) 'EVIDENCE_SEAL_MANIFEST_HASH' 'seal-manifest-tamper'

# 8. BuildReport attribution: real counts preserved, step errors surfaced
$reportDir = Join-Path $testRoot 'report'
New-Item -ItemType Directory -Force -Path $reportDir | Out-Null
$reportExe = Join-Path $reportDir 'Mahjong.exe'
[System.IO.File]::WriteAllText($reportExe, 'x')
$goodReport = Join-Path $reportDir 'good.json'
Write-C04Json -Path $goodReport -Object (New-FixtureReport -OutputPath $reportExe)
$good = Test-C04BuildReport -Path $goodReport -ExpectedOutput $reportExe
Assert-True $good.ok 'build-report-good'

$errored = Join-Path $reportDir 'errors3.json'
Write-C04Json -Path $errored -Object (New-FixtureReport -OutputPath $reportExe -Errors 3 -StepMessages @('boom-1','boom-2','boom-3'))
$erroredCheck = Test-C04BuildReport -Path $errored -ExpectedOutput $reportExe
Assert-Contains $erroredCheck.errors 'BUILD_REPORT_ERRORS:3' 'build-report-errors-count'
Assert-Match $erroredCheck.errors 'BUILD_REPORT_STEP_ERROR:step=Preprocess Player type=Error message=boom-1' 'build-report-step-attribution'

$unattributed = Join-Path $reportDir 'unattributed.json'
Write-C04Json -Path $unattributed -Object (New-FixtureReport -OutputPath $reportExe -Errors 3)
$unattributedCheck = Test-C04BuildReport -Path $unattributed -ExpectedOutput $reportExe
Assert-Contains $unattributedCheck.errors 'BUILD_REPORT_UNATTRIBUTED_ERRORS' 'build-report-unattributed'

$failedReport = Join-Path $reportDir 'failed.json'
Write-C04Json -Path $failedReport -Object (New-FixtureReport -OutputPath $reportExe -Result 'Failed')
Assert-Contains (Test-C04BuildReport -Path $failedReport -ExpectedOutput $reportExe).errors 'BUILD_REPORT_RESULT:Failed' 'build-report-failed-result'

$knownReport = New-FixtureReport -OutputPath $reportExe -Errors 3 -StepMessages @(
    'Host type is not matching any asset type at Path Packages/com.unity.render-pipelines.universal/Shaders/AutodeskInteractive/AutodeskInteractive.shadergraph.',
    'Host type is not matching any asset type at Path Packages/com.unity.render-pipelines.core/Editor/Lighting/ProbeVolume/VirtualOffset/TraceVirtualOffset.urtshader.',
    'Error building Player: 2 errors'
)
$knownPath = Join-Path $reportDir 'known-nonfatal.json'
Write-C04Json -Path $knownPath -Object $knownReport
$known = Test-C04BuildReport -Path $knownPath -ExpectedOutput $reportExe -AllowedNonFatalStepErrors $script:C04KnownNonFatalBuildErrors
Assert-True $known.ok 'build-report-known-nonfatal-accepted'
Assert-Equal ([int]$known.report.errors) 3 'build-report-known-count-preserved'
Assert-Equal @($known.knownNonFatalStepErrors).Count 3 'build-report-known-attribution'

$unknownKnownReport = New-FixtureReport -OutputPath $reportExe -Errors 2 -StepMessages @(
    'Host type is not matching any asset type at Path Packages/com.unity.render-pipelines.universal/Shaders/AutodeskInteractive/AutodeskInteractive.shadergraph.',
    'Unknown compiler failure'
)
$unknownKnownPath = Join-Path $reportDir 'known-plus-unknown.json'
Write-C04Json -Path $unknownKnownPath -Object $unknownKnownReport
$unknownKnown = Test-C04BuildReport -Path $unknownKnownPath -ExpectedOutput $reportExe -AllowedNonFatalStepErrors $script:C04KnownNonFatalBuildErrors
Assert-True (-not $unknownKnown.ok) 'build-report-unknown-not-accepted'
Assert-Match $unknownKnown.errors 'BUILD_REPORT_STEP_ERROR:.*Unknown compiler failure' 'build-report-unknown-attributed'

# 9. Plugin closure negatives
$pluginDir = Join-Path $testRoot 'plugins'
$plugin = New-PluginFixture -Dir $pluginDir
$closure = Get-C04PluginClosure -ProvenancePath $plugin.provenance -PluginDir $pluginDir
Assert-True $closure.ok 'plugin-closure-good'
[System.IO.File]::WriteAllText((Join-Path $pluginDir 'Mahjong.Domain.dll'), 'changed')
Assert-Contains (Get-C04PluginClosure -ProvenancePath $plugin.provenance -PluginDir $pluginDir).errors 'PLUGIN_HASH_MISMATCH:Mahjong.Domain.dll' 'plugin-hash-mismatch'
Remove-Item -LiteralPath (Join-Path $pluginDir 'Mahjong.Ai.dll') -Force
Assert-Contains (Get-C04PluginClosure -ProvenancePath $plugin.provenance -PluginDir $pluginDir).errors 'PLUGIN_MISSING:Mahjong.Ai.dll' 'plugin-missing'
[System.IO.File]::WriteAllText((Join-Path $pluginDir 'unexpected.dll'), 'x')
Assert-Contains (Get-C04PluginClosure -ProvenancePath $plugin.provenance -PluginDir $pluginDir).errors 'PLUGIN_UNEXPECTED:unexpected.dll' 'plugin-unexpected'

# 10. Complete package seal positives/negatives
$packageDir = Join-Path $testRoot 'package'
$package = New-PackageFixture -Dir $packageDir
$head = ('a' * 40)
$identity = ('b' * 64)
$sealed = Test-C04PackageSeal -PlayerPath $package.exe -ExpectedSourceHead $head -ExpectedBuildCommit 'c' -CurrentSourceIdentitySha256 $identity
Assert-True $sealed.ok 'package-seal-good'
[System.IO.File]::WriteAllText((Join-Path $packageDir 'late.dll'), 'late')
Assert-Contains (Test-C04PackageSeal -PlayerPath $package.exe -ExpectedSourceHead $head -ExpectedBuildCommit 'c' -CurrentSourceIdentitySha256 $identity).errors 'PACKAGE_EXTRA_FILE:late.dll' 'package-extra'
Remove-Item -LiteralPath (Join-Path $packageDir 'late.dll') -Force
[System.IO.File]::AppendAllText($package.exe, 'tamper')
Assert-Contains (Test-C04PackageSeal -PlayerPath $package.exe -ExpectedSourceHead $head -ExpectedBuildCommit 'c' -CurrentSourceIdentitySha256 $identity).errors 'PACKAGE_HASH_MISMATCH:Mahjong.exe' 'package-player-tamper'
Assert-Contains (Test-C04PackageSeal -PlayerPath $package.exe -ExpectedSourceHead ('d' * 40) -ExpectedBuildCommit 'c' -CurrentSourceIdentitySha256 $identity).errors 'PACKAGE_SEAL_SOURCE_HEAD_MISMATCH' 'package-wrong-source'
Assert-Contains (Test-C04PackageSeal -PlayerPath $package.exe -ExpectedSourceHead $head -ExpectedBuildCommit 'c' -CurrentSourceIdentitySha256 ('e' * 64)).errors 'PACKAGE_SEAL_SOURCE_IDENTITY_MISMATCH' 'package-wrong-identity'
Assert-Contains (Test-C04PackageSeal -PlayerPath $package.exe -ExpectedSourceHead $head -ExpectedBuildCommit 'z' -CurrentSourceIdentitySha256 $identity).errors 'PACKAGE_SEAL_BUILD_COMMIT_MISMATCH' 'package-wrong-commit'

$missingSeal = Join-Path $testRoot 'missing-seal'
New-Item -ItemType Directory -Force -Path $missingSeal | Out-Null
[System.IO.File]::WriteAllText((Join-Path $missingSeal 'Mahjong.exe'), 'x')
Assert-Contains (Test-C04PackageSeal -PlayerPath (Join-Path $missingSeal 'Mahjong.exe') -ExpectedSourceHead $head).errors 'PACKAGE_SEAL_MISSING' 'package-seal-missing'

# 11. Source identity is commit/working-tree bound
$gitRoot = Join-Path $testRoot 'git-source'
New-Item -ItemType Directory -Force -Path (Join-Path $gitRoot 'tools') | Out-Null
& git -C $gitRoot init -q
& git -C $gitRoot config user.email 'c04@example.invalid'
& git -C $gitRoot config user.name 'C04 Test'
[System.IO.File]::WriteAllText((Join-Path $gitRoot 'tools\a.txt'), 'one')
& git -C $gitRoot add tools/a.txt
& git -C $gitRoot commit -q -m initial
$identity1 = Get-C04SourceIdentity -RepoRoot $gitRoot
$identity2 = Get-C04SourceIdentity -RepoRoot $gitRoot
Assert-Equal $identity1.identitySha256 $identity2.identitySha256 'source-identity-stable'
[System.IO.File]::WriteAllText((Join-Path $gitRoot 'tools\a.txt'), 'two')
$identity3 = Get-C04SourceIdentity -RepoRoot $gitRoot
Assert-True ($identity1.identitySha256 -ne $identity3.identitySha256) 'source-identity-changes'
[System.IO.File]::WriteAllText((Join-Path $gitRoot 'doc.txt'), 'docs only')
& git -C $gitRoot add doc.txt
& git -C $gitRoot commit -q -m docs
$identity4 = Get-C04SourceIdentity -RepoRoot $gitRoot
Assert-Equal $identity3.identitySha256 $identity4.identitySha256 'source-identity-docs-commit-stable'

# 11b. Exit classification separates control/resource/unknown/build failures.
Assert-Equal (Get-C04ExitCodeForFailure 'MUTEX_TIMEOUT') 6 'exit-mutex'
Assert-Equal (Get-C04ExitCodeForFailure 'UNKNOWN_EXIT') 7 'exit-unknown'
Assert-Equal (Get-C04ExitCodeForFailure 'CANCELLED') 5 'exit-cancel'
Assert-Equal (Get-C04ExitCodeForFailure 'PLAYER_TIMEOUT') 4 'exit-timeout'
Assert-Equal (Get-C04ExitCodeForFailure 'BUILD_FAILED') 3 'exit-build'
Assert-Equal (Get-C04ExitCodeForFailure 'SKIPBUILD_INVALID') 2 'exit-precondition'
Assert-Equal (Get-C04ExitCodeForFailure 'CASE_FAILED') 1 'exit-evidence'

# 12. No retired local Strict verifier fallback
Assert-True ($null -eq (Get-Command Assert-C04EvidenceStrict -ErrorAction SilentlyContinue)) 'no-local-strict-fallback'
$caseDir = Join-Path $testRoot 'case'
New-Item -ItemType Directory -Force -Path $caseDir | Out-Null
Assert-Contains (Get-C04CaseStructureErrors -EvidenceJson (Join-Path $caseDir 'missing.json')) 'MISSING_EVIDENCE_JSON' 'case-missing'
[System.IO.File]::WriteAllText((Join-Path $caseDir 'bad.json'), '{')
Assert-Contains (Get-C04CaseStructureErrors -EvidenceJson (Join-Path $caseDir 'bad.json')) 'EVIDENCE_JSON_MALFORMED' 'case-malformed'
[System.IO.File]::WriteAllText((Join-Path $caseDir 'good.json'), '{}')
Assert-Equal (@(Get-C04CaseStructureErrors -EvidenceJson (Join-Path $caseDir 'good.json'))).Count 0 'case-structure-good'

# 13. Fixed A verifier CLI arguments
$verifierArgs = Get-C04VerifierArguments -Verifier 'C:\v\C04PlayerCheatVerifier2.dll' -EvidenceJson 'C:\e\case.json' -CaseName 'negative' -ExpectedSourceHead $head -PackageManifest 'C:\p\manifest.csv'
Assert-Equal ($verifierArgs -join '|') ('C:\v\C04PlayerCheatVerifier2.dll|verify|--evidence|C:\e\case.json|--case|negative|--source-head|' + $head + '|--package-manifest|C:\p\manifest.csv') 'verifier-cli-contract'

Write-Host "C04PlayerCheatRunner.Tests passed=$passed failed=$($failures.Count)"
foreach ($failure in $failures) { Write-Host "FAIL $failure" }
if ($failures.Count -gt 0) { exit 1 }
exit 0
