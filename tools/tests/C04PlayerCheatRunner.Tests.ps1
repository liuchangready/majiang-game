<#
    SCRIPT_TOOL_FIXTURE negative matrix for D04-C/R1-06 rework.
    Exercises real owned processes, durable writer cleanup, strict BuildReport
    validation, source-input freeze, package receipt/closure validation, path
    safety and partial-artifact sealing. No Unity/Player/GAME execution.
#>
$ErrorActionPreference = 'Stop'
$runner = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'tools\unity\run-p45-c04-player-cheats.ps1'
. $runner -SelfTest

$passed = 0
$failures = New-Object System.Collections.Generic.List[string]
$testRoot = Join-Path $root ('build\rework-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null

function Assert-Equal { param($Actual, $Expected, [string]$Label) if ($Actual -eq $Expected) { $script:passed++ } else { $script:failures.Add("$Label expected=[$Expected] actual=[$Actual]") | Out-Null } }
function Assert-True { param($Condition, [string]$Label) if ($Condition) { $script:passed++ } else { $script:failures.Add("$Label condition was false") | Out-Null } }
function Assert-Contains { param([object[]]$Values, [string]$Expected, [string]$Label) if (@($Values) -contains $Expected) { $script:passed++ } else { $script:failures.Add("$Label expected=$Expected actual=$(@($Values) -join ',')") | Out-Null } }
function Assert-Match { param([object[]]$Values, [string]$Pattern, [string]$Label) if (@($Values | Where-Object { [string]$_ -match $Pattern }).Count -gt 0) { $script:passed++ } else { $script:failures.Add("$Label no match $Pattern actual=$(@($Values) -join ',')") | Out-Null } }

function New-FixtureReport {
    param([string]$OutputPath, [int]$Errors = 0, [string]$Result = 'Succeeded', [string[]]$StepMessages = @(), [switch]$OmitBytesAndSha, [string]$Exception = '')
    $steps = @([ordered]@{ name = 'Preprocess Player'; depth = 0; durationSeconds = 1.0; messages = @($StepMessages | ForEach-Object { [ordered]@{ type = 'Error'; content = $_ } }) })
    if ($StepMessages.Count -eq 0) { $steps[0].messages = @([ordered]@{ type = 'Log'; content = 'ok' }) }
    $outputFull = Get-C04FullPath $OutputPath
    $bytes = (Get-Item -LiteralPath $outputFull).Length
    $report = [ordered]@{
        schema = 1; result = $Result; errors = $Errors; warnings = 0; target = 'StandaloneWindows64'
        output = $outputFull; bytes = $bytes; outputSha256 = Get-C04Sha256 $outputFull; outputExists = $true
        startedUtc = '2026-11-03T00:00:00.0000000Z'; finishedUtc = '2026-11-03T00:01:00.0000000Z'; durationSeconds = 60.0
        unityVersion = '6000.3.27f1'; productName = 'Mahjong Cheater'; exception = $Exception
        scenes = @('Assets/Scenes/Boot.unity','Assets/Scenes/Menu.unity','Assets/Scenes/Lobby.unity','Assets/Scenes/Table.unity','Assets/Scenes/Spectator.unity','Assets/Scenes/Settlement.unity')
        plugins = @([ordered]@{ name = 'Mahjong.Domain.dll'; bytes = 1; sha256 = ('a' * 64) })
        stepCount = 1; stepErrorCount = $StepMessages.Count; stepExceptionCount = 0; stepWarningCount = 0; stepDiagnostics = @($StepMessages); steps = $steps
    }
    if ($OmitBytesAndSha) { $report.Remove('bytes'); $report.Remove('outputSha256') }
    return $report
}

function New-PluginFixture {
    param([string]$Dir)
    New-Item -ItemType Directory -Force -Path $Dir | Out-Null
    $outputs = New-Object System.Collections.Generic.List[object]
    foreach ($name in @('Mahjong.Domain.dll','Mahjong.Contracts.dll','Mahjong.Contracts.Wire.dll','Mahjong.TableRuntime.dll','Mahjong.Client.dll','Mahjong.LocalHost.dll','Mahjong.Ai.dll','Google.Protobuf.dll')) {
        $path = Join-Path $Dir $name
        [System.IO.File]::WriteAllText($path, "fixture-$name", (New-Object System.Text.UTF8Encoding($false)))
        $outputs.Add([ordered]@{ Name = $name; Length = (Get-Item -LiteralPath $path).Length; Sha256 = (Get-C04Sha256 $path); SourceKind = 'project'; SourcePath = 'fixture'; PluginPath = 'fixture'; Project = $name.Replace('.dll','') }) | Out-Null
    }
    $provenance = [ordered]@{ schemaVersion = 2; kind = 'mahjong-unity-shared-plugin-provenance'; evidenceScope = 'scratch'; pluginDir = $Dir; build = [ordered]@{ exitCode = 0 }; inputs = @([ordered]@{ Path = 'fixture.cs'; Length = 1; Sha256 = ('b' * 64) }); outputs = $outputs.ToArray() }
    $provenancePath = Join-Path $Dir 'shared-plugin-provenance.json'
    Write-C04Json -Path $provenancePath -Object $provenance
    return [pscustomobject]@{ dir = $Dir; provenance = $provenancePath; outputs = $outputs.ToArray() }
}

function New-PackageFixture {
    param([string]$Dir, [switch]$ValidEvidence, [switch]$UnityArtifact)
    New-Item -ItemType Directory -Force -Path $Dir | Out-Null
    $exe = Join-Path $Dir 'Mahjong.exe'
    Copy-Item -LiteralPath (Get-Command powershell.exe).Source -Destination $exe -Force
    if ($UnityArtifact) {
        $marker = 'UnityPlayer.dll'
        $exeBytes = [byte[]](0x4D,0x5A) + [System.Text.Encoding]::ASCII.GetBytes($marker) + (New-Object byte[] 70000)
        [System.IO.File]::WriteAllBytes($exe, $exeBytes)
        $pluginDir = Join-Path $Dir 'Mahjong_Data\Managed'
        New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $pluginDir 'Mahjong.Domain.dll'), 'fixture')
        [System.IO.File]::WriteAllBytes((Join-Path $Dir 'UnityPlayer.dll'), ([byte[]](0x4D,0x5A) + (New-Object byte[] 1050000)))
        [System.IO.File]::WriteAllText((Join-Path $Dir 'Mahjong_Data\globalgamemanagers'), ('g' * 2048))
    }    $plugin = New-PluginFixture -Dir (Join-Path $Dir 'plugins')
    $closure = Get-C04PluginClosure -ProvenancePath $plugin.provenance -PluginDir $plugin.dir
    Assert-True $closure.ok 'fixture plugin closure'
    $closurePath = Join-Path $Dir 'c04-plugin-closure.csv'
    $rows = @($closure.files | ForEach-Object { [pscustomobject]@{ path = $_.name; bytes = $_.bytes; sha256 = $_.sha256 } })
    $null = Write-C04ManifestFile -Path $closurePath -Rows $rows -Kind 'plugin-closure'
    $provenanceCopy = Join-Path $Dir 'c04-plugin-provenance.json'
    Copy-Item -LiteralPath $plugin.provenance -Destination $provenanceCopy -Force
    $reportPath = Join-Path $Dir 'c04-build-report.json'
    $report = New-FixtureReport -OutputPath $exe
    $report.plugins = @($closure.files | Where-Object { $_.name.EndsWith('.dll') } | ForEach-Object { [ordered]@{ name = $_.name; bytes = $_.bytes; sha256 = $_.sha256 } })
    Write-C04Json -Path $reportPath -Object $report

    $head = ((git -C $root rev-parse HEAD) -join '').Trim()
    $identity = Get-C04SourceIdentity
    $evidence = Join-Path $Dir 'evidence'
    foreach ($d in @('staging','staging\repo','build\player','staging\repo\unity\Mahjong.Unity\Library\SharedPluginProvenance','staging\repo\unity\Mahjong.Unity\Assets\Plugins\Mahjong')) { New-Item -ItemType Directory -Force -Path (Join-Path $evidence $d) | Out-Null }
    $sourceManifest = Join-Path $evidence 'source-input-manifest.csv'
    $null = Write-C04ManifestFile -Path $sourceManifest -Rows @($identity.inputRows) -Kind 'source'
    $stagedManifest = Join-Path $evidence 'staging\staged-project-input-manifest.csv'
    $null = Write-C04ManifestFile -Path $stagedManifest -Rows @([pscustomobject]@{ path = 'fixture.txt'; bytes = 1; sha256 = ('c' * 64) }) -Kind 'staged'
    $stagedFinalManifest = Join-Path $evidence 'staging\staged-project-final-manifest.csv'
    $null = Write-C04ManifestFile -Path $stagedFinalManifest -Rows @([pscustomobject]@{ path = 'fixture-final.txt'; bytes = 1; sha256 = ('d' * 64) }) -Kind 'staged-final'
    $stagedCodeManifest = Join-Path $evidence 'staging\staged-code-manifest.csv'
    $null = Write-C04ManifestFile -Path $stagedCodeManifest -Rows @() -Kind 'staged-code'
    Copy-Item -LiteralPath $closurePath -Destination (Join-Path $evidence 'staging\plugin-closure.csv') -Force
    Copy-Item -LiteralPath $reportPath -Destination (Join-Path $evidence 'build\player\c04-build-report.json') -Force
    Copy-Item -LiteralPath $exe -Destination (Join-Path $evidence 'build\player\Mahjong.exe') -Force
    Copy-Item -LiteralPath $provenanceCopy -Destination (Join-Path $evidence 'staging\repo\unity\Mahjong.Unity\Library\SharedPluginProvenance\provenance.json') -Force
    if ($ValidEvidence) {
        $inputFile = Join-Path $evidence 'staging\repo\fixture.cs'
        [System.IO.File]::WriteAllText($inputFile, 'fixture-input')
        $prov = Get-Content -LiteralPath $provenanceCopy -Raw | ConvertFrom-Json
        $prov.inputs[0].Length = (Get-Item -LiteralPath $inputFile).Length
        $prov.inputs[0].Sha256 = Get-C04Sha256 $inputFile
        Write-C04Json -Path $provenanceCopy -Object $prov
        Copy-Item -LiteralPath $provenanceCopy -Destination (Join-Path $evidence 'staging\repo\unity\Mahjong.Unity\Library\SharedPluginProvenance\provenance.json') -Force
        foreach ($file in @($closure.files)) {
            Copy-Item -LiteralPath (Join-Path $plugin.dir $file.name) -Destination (Join-Path $evidence ('staging\repo\unity\Mahjong.Unity\Assets\Plugins\Mahjong\' + $file.name)) -Force
        }
        $projectRoot = Join-Path $evidence 'staging\repo\unity\Mahjong.Unity'
        [System.IO.File]::WriteAllText((Join-Path $projectRoot 'fixture.txt'), 'fixture')
        [System.IO.File]::WriteAllText((Join-Path $projectRoot 'fixture-final.txt'), 'final')
        $stagedRows = Get-C04FileTable -Root $projectRoot -ExcludeDirectoryNames @('Library','Temp','Logs','obj','UserSettings') -ExcludeRelativeDirectories @('.vscode') -ExcludeFileSuffixes @('.csproj','.slnx','.pdb.meta')
        $null = Write-C04ManifestFile -Path $stagedManifest -Rows $stagedRows -Kind 'staged'
        $null = Write-C04ManifestFile -Path $stagedFinalManifest -Rows $stagedRows -Kind 'staged-final'
        $null = Write-C04ManifestFile -Path $stagedCodeManifest -Rows @([pscustomobject]@{ path = 'fixture.cs'; bytes = (Get-Item -LiteralPath $inputFile).Length; sha256 = (Get-C04Sha256 $inputFile) }) -Kind 'staged-code'
    }
    $seal = Write-C04PackageSeal -PackageRoot $Dir -PlayerPath $exe -ReportPath $reportPath -ProvenancePath $provenanceCopy -PluginClosurePath $closurePath -BuildEvidenceRoot $evidence -SourceHead $head -SourceIdentitySha256 $identity.identitySha256 -SourceManifestSha256 (Get-C04Sha256 $sourceManifest) -StagedInputManifestSha256 (Get-C04Sha256 $stagedManifest) -StagedFinalManifestSha256 (Get-C04Sha256 $stagedFinalManifest) -StagedCodeManifestSha256 (Get-C04Sha256 $stagedCodeManifest) -BuildCommit $head -BuildId 'fixture-build' -SourceInputFileCount $identity.inputFileCount -SourceInputBytes $identity.inputBytes -StagedInputFileCount 1 -PluginClosure $closure
    return [pscustomobject]@{ dir = $Dir; exe = $exe; report = $reportPath; provenance = $provenanceCopy; closure = $closure; closurePath = $closurePath; seal = $seal; head = $head; identity = $identity }
}
# 1. Quoting and bounded process basics
Assert-Equal (ConvertTo-C04QuotedArgument 'plain') 'plain' 'quote-plain'
Assert-Equal (ConvertTo-C04QuotedArgument 'C:\a b\c') '"C:\a b\c"' 'quote-space'
Assert-Equal (ConvertTo-C04QuotedArgument '') '""' 'quote-empty'
Assert-Equal (ConvertTo-C04QuotedArgument 'a"b') '"a\"b"' 'quote-quote'
Assert-Equal (ConvertTo-C04QuotedArgument 'a b\') '"a b\\"' 'quote-tail'

$out = Join-Path $testRoot 'ok.out'; $err = Join-Path $testRoot 'ok.err'
$ok = Start-C04BoundedProcess -FilePath powershell.exe -Arguments @('-NoProfile','-Command','Write-Output ''OUT''; [Console]::Error.WriteLine(''ERR''); exit 0') -TimeoutSeconds 30 -StdOutPath $out -StdErrPath $err -Label 'ok' -DrainGrace 5 -Heartbeat 0
Assert-Equal $ok.exitCode 0 'process-exit0'
Assert-True $ok.drainComplete 'process-drain'
Assert-True ($ok.ownedProcessesRemaining -eq 0 -and -not $ok.cleanupFailed) 'process-owned-clean'
Assert-True ((Get-Content $out -Raw) -match 'OUT') 'process-stdout'
$nz = Start-C04BoundedProcess -FilePath powershell.exe -Arguments @('-NoProfile','-Command','exit 7') -TimeoutSeconds 30 -StdOutPath (Join-Path $testRoot 'nz.out') -StdErrPath (Join-Path $testRoot 'nz.err') -Label 'nz' -DrainGrace 5 -Heartbeat 0
Assert-Equal $nz.exitCode 7 'process-native7'
$to = Start-C04BoundedProcess -FilePath powershell.exe -Arguments @('-NoProfile','-Command','Start-Sleep -Seconds 30') -TimeoutSeconds 1 -StdOutPath (Join-Path $testRoot 'to.out') -StdErrPath (Join-Path $testRoot 'to.err') -Label 'to' -DrainGrace 2 -Heartbeat 0
Assert-True ($to.timedOut -and -not $to.exitCodeKnown) 'process-timeout'
Assert-True ($null -eq (Get-Process -Id $to.pid -ErrorAction SilentlyContinue)) 'process-timeout-gone'
$threw = $false
try { Start-C04BoundedProcess -FilePath powershell.exe -Arguments @('-NoProfile','-Command','exit 0') -TimeoutSeconds 0 -Label 'zero' } catch { $threw = $_.Exception.Message -match 'timeout must be positive' }
Assert-True $threw 'process-zero-timeout-rejected'

# 2. Detached writer survives the parent; owned cleanup must stop it.
$marker = Join-Path $testRoot 'writes.txt'
$childScript = Join-Path $testRoot 'child.ps1'
[System.IO.File]::WriteAllText($childScript, 'param([string]$Marker) for($i=0;$i -lt 100;$i++){ [System.IO.File]::AppendAllText($Marker,''x''); Start-Sleep -Milliseconds 100 }')
$parentScript = Join-Path $testRoot 'parent.ps1'
$parentText = 'param([string]$Child,[string]$Marker) $p=Start-Process powershell.exe -ArgumentList @(''-NoProfile'',''-File'',$Child,''-Marker'',$Marker) -PassThru -WindowStyle Hidden; Start-Sleep -Seconds 2; exit 0'
[System.IO.File]::WriteAllText($parentScript, $parentText)
$parent = Start-C04BoundedProcess -FilePath powershell.exe -Arguments @('-NoProfile','-File',$parentScript,'-Child',$childScript,'-Marker',$marker) -TimeoutSeconds 10 -StdOutPath (Join-Path $testRoot 'parent.out') -StdErrPath (Join-Path $testRoot 'parent.err') -Label 'detached' -DrainGrace 2 -Heartbeat 0
$before = if (Test-Path $marker) { (Get-Item $marker).Length } else { 0 }
Start-Sleep -Milliseconds 400
$after = if (Test-Path $marker) { (Get-Item $marker).Length } else { 0 }
Assert-True ($parent.drainComplete -and $parent.ownedProcessesRemaining -eq 0) 'detached-clean'
Assert-Equal $before $after 'detached-no-writes-after-return'

# 3. Strict BuildReport: no generic/summary/known bypass.
$reportExe = Join-Path $testRoot 'fixture.exe'; [System.IO.File]::WriteAllText($reportExe, 'fixture')
$goodPath = Join-Path $testRoot 'good.json'; Write-C04Json $goodPath (New-FixtureReport -OutputPath $reportExe)
Assert-True (Test-C04BuildReport -Path $goodPath -ExpectedOutput $reportExe).ok 'report-good'
$summaryOnly = Join-Path $testRoot 'summary-only.json'; Write-C04Json $summaryOnly (New-FixtureReport -OutputPath $reportExe -Errors 99 -StepMessages @('Error building Player: 99 errors'))
$summaryCheck = Test-C04BuildReport -Path $summaryOnly -ExpectedOutput $reportExe
Assert-True (-not $summaryCheck.ok) 'report-summary-only-rejected'
Assert-Contains $summaryCheck.errors 'BUILD_REPORT_ERRORS:99' 'report-summary-count'
$oneKnown = Join-Path $testRoot 'one-known.json'; Write-C04Json $oneKnown (New-FixtureReport -OutputPath $reportExe -Errors 99 -StepMessages @('Host type is not matching any asset type at Path Packages/x.urtshader.'))
$oneCheck = Test-C04BuildReport -Path $oneKnown -ExpectedOutput $reportExe
Assert-True (-not $oneCheck.ok) 'report-one-known-98-unattributed-rejected'
Assert-Match $oneCheck.errors 'BUILD_REPORT_ATTRIBUTION_COUNT_MISMATCH:99!=' 'report-attribution-count'
$missing = Join-Path $testRoot 'missing.json'; Write-C04Json $missing (New-FixtureReport -OutputPath $reportExe -OmitBytesAndSha -Exception 'boom')
$missingCheck = Test-C04BuildReport -Path $missing -ExpectedOutput $reportExe
Assert-True (-not $missingCheck.ok) 'report-missing-binding-rejected'
Assert-Contains $missingCheck.errors 'BUILD_REPORT_BYTES_MISSING' 'report-missing-bytes'
Assert-Contains $missingCheck.errors 'BUILD_REPORT_OUTPUT_SHA_MISSING' 'report-missing-sha'
Assert-Contains $missingCheck.errors 'BUILD_REPORT_EXCEPTION_PRESENT' 'report-exception'

# 4. Source identity binds tracked tests and ignored Unity inputs.
$fixtureRepo = Join-Path $testRoot 'source-fixture'
foreach ($d in @('tools/unity','dotnet','tests','unity/Mahjong.Unity/Assets','unity/Mahjong.Unity/ProjectSettings')) { New-Item -ItemType Directory -Force -Path (Join-Path $fixtureRepo $d) | Out-Null }
foreach ($f in @('build-shared.ps1','check-shared-plugins.ps1','shared-plugin-provenance-lib.ps1','run-p45-c04-player-cheats.ps1')) { [System.IO.File]::WriteAllText((Join-Path $fixtureRepo "tools/unity/$f"),'# fixture') }
[System.IO.File]::WriteAllText((Join-Path $fixtureRepo '.gitignore'), '*.asset')
[System.IO.File]::WriteAllText((Join-Path $fixtureRepo 'tests/Test.cs'), '// before')
[System.IO.File]::WriteAllText((Join-Path $fixtureRepo 'unity/Mahjong.Unity/Assets/Code.cs'), '// code')
[System.IO.File]::WriteAllText((Join-Path $fixtureRepo 'unity/Mahjong.Unity/ProjectSettings/ProjectSettings.asset'), 'settings')
& git -C $fixtureRepo init -q
& git -C $fixtureRepo -c user.name=Test -c user.email=test@example.invalid add .
& git -C $fixtureRepo -c user.name=Test -c user.email=test@example.invalid commit -q -m fixture
$id1 = Get-C04SourceIdentity $fixtureRepo
[System.IO.File]::WriteAllText((Join-Path $fixtureRepo 'tests/Test.cs'), '// after')
[System.IO.File]::WriteAllText((Join-Path $fixtureRepo 'unity/Mahjong.Unity/Assets/Extra.asset'), 'ignored input')
$id2 = Get-C04SourceIdentity $fixtureRepo
Assert-True ($id1.identitySha256 -ne $id2.identitySha256) 'source-identity-tests-and-ignored'
$staged = New-C04StagedRepository -SourceRoot $fixtureRepo -StagingRepo (Join-Path $testRoot 'staged-fixture')
Assert-True (Test-Path (Join-Path $staged.project 'Assets/Extra.asset')) 'staging-copies-ignored'
$orig = Write-C04ManifestFile -Path (Join-Path $testRoot 'staged-original.csv') -Rows (Get-C04FileTable $staged.project)
[System.IO.File]::AppendAllText((Join-Path $staged.project 'Assets/Extra.asset'), ' changed')
$diff = @(Test-C04ManifestAgainstRoot -Root $staged.project -ManifestPath $orig.path)
Assert-Match $diff 'STAGING_CHANGED:Assets/Extra.asset' 'staging-asset-mutation-detected'

# 5. Package seal requires real source/build/provenance/report/plugin relations.
$package = New-PackageFixture -Dir (Join-Path $testRoot 'package') -ValidEvidence -UnityArtifact
$check = Test-C04PackageSeal -PlayerPath $package.exe -ExpectedSourceHead $package.head -ExpectedBuildCommit $package.head -CurrentSourceIdentitySha256 $package.identity.identitySha256
Assert-True $check.ok 'package-seal-good'
$fakeHead = ('a' * 40)
Assert-Match (Test-C04PackageSeal -PlayerPath $package.exe -ExpectedSourceHead $fakeHead -CurrentSourceIdentitySha256 $package.identity.identitySha256).errors 'PACKAGE_SEAL_(SOURCE|BUILD)_HEAD_NOT_IN_HISTORY|PACKAGE_SEAL_SOURCE_HEAD' 'package-fake-head'
# Restored coverage: plugin closure identity negatives.
$badPlugin = New-PluginFixture -Dir (Join-Path $testRoot 'bad-plugin')
[System.IO.File]::WriteAllText((Join-Path $badPlugin.dir 'Mahjong.Domain.dll'), 'changed')
Assert-Contains (Get-C04PluginClosure -ProvenancePath $badPlugin.provenance -PluginDir $badPlugin.dir).errors 'PLUGIN_HASH_MISMATCH:Mahjong.Domain.dll' 'plugin-hash-mismatch'
Remove-Item (Join-Path $badPlugin.dir 'Mahjong.Ai.dll') -Force
Assert-Contains (Get-C04PluginClosure -ProvenancePath $badPlugin.provenance -PluginDir $badPlugin.dir).errors 'PLUGIN_MISSING:Mahjong.Ai.dll' 'plugin-missing'
[System.IO.File]::WriteAllText((Join-Path $badPlugin.dir 'unexpected.dll'), 'x')
Assert-Contains (Get-C04PluginClosure -ProvenancePath $badPlugin.provenance -PluginDir $badPlugin.dir).errors 'PLUGIN_UNEXPECTED:unexpected.dll' 'plugin-unexpected'

# Restored coverage: source identity/docs-only, package identity/commit, evidence extra/hash.
$idBeforeDocs = Get-C04SourceIdentity $fixtureRepo
[System.IO.File]::WriteAllText((Join-Path $fixtureRepo 'docs.md'), 'docs')
& git -C $fixtureRepo -c user.name=Test -c user.email=test@example.invalid add docs.md
& git -C $fixtureRepo -c user.name=Test -c user.email=test@example.invalid commit -q -m docs
$idAfterDocs = Get-C04SourceIdentity $fixtureRepo
Assert-Equal $idBeforeDocs.identitySha256 $idAfterDocs.identitySha256 'source-identity-docs-stable'
Assert-Contains (Test-C04PackageSeal -PlayerPath $package.exe -ExpectedSourceHead $package.head -CurrentSourceIdentitySha256 ('e' * 64)).errors 'PACKAGE_SEAL_SOURCE_IDENTITY_MISMATCH' 'package-wrong-identity'
Assert-Contains (Test-C04PackageSeal -PlayerPath $package.exe -ExpectedSourceHead $package.head -ExpectedBuildCommit ('f' * 40) -CurrentSourceIdentitySha256 $package.identity.identitySha256).errors 'PACKAGE_SEAL_BUILD_COMMIT_MISMATCH' 'package-wrong-commit'

$evidenceExtra = Join-Path $testRoot 'evidence-extra'
New-Item -ItemType Directory -Force -Path $evidenceExtra | Out-Null
[System.IO.File]::WriteAllText((Join-Path $evidenceExtra 'a.txt'), 'a')
$null = Write-C04EvidenceSeal -EvidenceRoot $evidenceExtra -Status 'FAIL' -SourceHead $package.head
Assert-Equal (@(Test-C04EvidenceSeal $evidenceExtra)).Count 0 'evidence-extra-valid'
[System.IO.File]::WriteAllText((Join-Path $evidenceExtra 'late.txt'), 'late')
Assert-Contains (Test-C04EvidenceSeal $evidenceExtra) 'EVIDENCE_EXTRA:late.txt' 'evidence-extra-detected'
Remove-Item (Join-Path $evidenceExtra 'late.txt') -Force
[System.IO.File]::AppendAllText((Join-Path $evidenceExtra 'a.txt'), 'x')
Assert-Contains (Test-C04EvidenceSeal $evidenceExtra) 'EVIDENCE_HASH:a.txt' 'evidence-hash-detected'

# Restored coverage: quoted path and large dual-stream native process.
$spaceDir = Join-Path $testRoot 'space dir'; New-Item -ItemType Directory -Force -Path $spaceDir | Out-Null
$spacePath = Join-Path $spaceDir 'a file.txt'; [System.IO.File]::WriteAllText($spacePath, 'x')
$echoScript = Join-Path $testRoot 'echo.ps1'; [System.IO.File]::WriteAllText($echoScript, 'param([string]$Value) Write-Output $Value')
$echoOut = Join-Path $testRoot 'echo.out'
$echo = Start-C04BoundedProcess -FilePath powershell.exe -Arguments @('-NoProfile','-ExecutionPolicy','Bypass','-File',$echoScript,'-Value',$spacePath) -TimeoutSeconds 30 -StdOutPath $echoOut -StdErrPath (Join-Path $testRoot 'echo.err') -Label 'quoted' -DrainGrace 3 -Heartbeat 0
Assert-Equal ((Get-Content $echoOut -Raw).Trim()) $spacePath 'process-quoted-path'
$bigOut = Join-Path $testRoot 'big.out'; $bigErr = Join-Path $testRoot 'big.err'
$big = Start-C04BoundedProcess -FilePath powershell.exe -Arguments @('-NoProfile','-Command','[Console]::Out.Write(("x" * 500000)); [Console]::Error.Write(("y" * 500000)); exit 0') -TimeoutSeconds 60 -StdOutPath $bigOut -StdErrPath $bigErr -Label 'big' -DrainGrace 5 -Heartbeat 0
Assert-True ($big.drainComplete -and (Get-Item $bigOut).Length -gt 400000 -and (Get-Item $bigErr).Length -gt 400000) 'process-big-dual-stream'
[System.IO.File]::WriteAllText((Join-Path $package.dir 'late.dll'), 'late')
Assert-Contains (Test-C04PackageSeal -PlayerPath $package.exe -ExpectedSourceHead $package.head -CurrentSourceIdentitySha256 $package.identity.identitySha256).errors 'PACKAGE_EXTRA_FILE:late.dll' 'package-extra'
Remove-Item (Join-Path $package.dir 'late.dll') -Force
[System.IO.File]::AppendAllText($package.exe, 'tamper')
Assert-Contains (Test-C04PackageSeal -PlayerPath $package.exe -ExpectedSourceHead $package.head -CurrentSourceIdentitySha256 $package.identity.identitySha256).errors 'PACKAGE_HASH_MISMATCH:Mahjong.exe' 'package-tamper'
$synthetic = Join-Path $testRoot 'synthetic'
New-Item -ItemType Directory -Force -Path $synthetic | Out-Null
[System.IO.File]::WriteAllText((Join-Path $synthetic 'Mahjong.exe'), 'not a real binary')
Assert-Contains (Test-C04PackageSeal -PlayerPath (Join-Path $synthetic 'Mahjong.exe') -ExpectedSourceHead $fakeHead).errors 'PACKAGE_SEAL_MISSING' 'synthetic-missing-seal'

# 6. Evidence seal covers partial build artifacts.
$failure = Join-Path $testRoot 'failure'
New-Item -ItemType Directory -Force -Path (Join-Path $failure 'build/player') | Out-Null
[System.IO.File]::WriteAllText((Join-Path $failure 'build/player/partial.dll'), 'partial')
[System.IO.File]::WriteAllText((Join-Path $failure 'summary.json'), '{"status":"FAIL"}')
$null = Write-C04EvidenceSeal -EvidenceRoot $failure -Status 'FAIL' -SourceHead ($package.head)
Assert-Equal (@(Test-C04EvidenceSeal $failure)).Count 0 'evidence-failure-valid'
[System.IO.File]::AppendAllText((Join-Path $failure 'build/player/partial.dll'), 'tampered')
Assert-Match (Test-C04EvidenceSeal $failure) 'EVIDENCE_PARTIAL_(HASH|MISSING)' 'evidence-partial-tamper'

# 7. Path safety rejects an ancestor junction.
$target = Join-Path $testRoot 'junction-target'; New-Item -ItemType Directory -Path $target | Out-Null
$link = Join-Path $testRoot 'junction-link'; New-Item -ItemType Junction -Path $link -Value $target | Out-Null
$threw = $false
try { Assert-C04PathSafe -Path (Join-Path $link 'new-evidence') -Label 'junction' } catch { $threw = $true }
Assert-True $threw 'path-ancestor-junction-rejected'

# 8. Control categories and exit mapping.
Assert-Equal (Get-C04ExitCodeForFailure 'UNITY_BUILD_TIMEOUT') 4 'exit-timeout'
Assert-Equal (Get-C04ExitCodeForFailure 'CANCELLED') 5 'exit-cancel'
Assert-Equal (Get-C04ExitCodeForFailure 'UNKNOWN_EXIT') 7 'exit-unknown'
Assert-Equal (Get-C04ExitCodeForFailure 'MUTEX_TIMEOUT') 6 'exit-mutex'
Assert-Equal (Get-C04ExitCodeForFailure 'BUILD_FAILED') 3 'exit-build'
Assert-Equal (Get-C04ExitCodeForFailure 'CASE_FAILED') 1 'exit-case'

# 9. No retired local verifier fallback; fixed CLI.
Assert-True ($null -eq (Get-Command Assert-C04EvidenceStrict -ErrorAction SilentlyContinue)) 'strict-removed'
$args = Get-C04VerifierArguments -Verifier 'C:\v\C04PlayerCheatVerifier2.dll' -EvidenceJson 'C:\e\case.json' -CaseName 'negative' -ExpectedSourceHead $package.head -PackageManifest 'C:\p\manifest.csv'
Assert-Equal ($args -join '|') ('C:\v\C04PlayerCheatVerifier2.dll|verify|--evidence|C:\e\case.json|--case|negative|--source-head|' + $package.head + '|--package-manifest|C:\p\manifest.csv') 'verifier-cli'

# 10. Receipt/provenance/closure relations cannot be bypassed.
$receipt = Join-Path $package.dir 'c04-build-receipt.json'
$savedReceipt = [System.IO.File]::ReadAllText($receipt)
[System.IO.File]::Delete($receipt)
Assert-Contains (Test-C04PackageSeal -PlayerPath $package.exe -ExpectedSourceHead $package.head -CurrentSourceIdentitySha256 $package.identity.identitySha256).errors 'PACKAGE_RECEIPT_MISSING' 'package-receipt-missing'
[System.IO.File]::WriteAllText($receipt, $savedReceipt)
$provPath = Join-Path $package.dir 'c04-plugin-provenance.json'
$savedProv = [System.IO.File]::ReadAllText($provPath)
$prov = $savedProv | ConvertFrom-Json
$prov.inputs = @()
Write-C04Json -Path $provPath -Object $prov
Assert-Match (Test-C04PackageSeal -PlayerPath $package.exe -ExpectedSourceHead $package.head -CurrentSourceIdentitySha256 $package.identity.identitySha256).errors 'PACKAGE_PROVENANCE_(INPUTS_EMPTY|HASH_MISMATCH)' 'package-provenance-empty-inputs'
[System.IO.File]::WriteAllText($provPath, $savedProv)

# 11. Main SkipBuild rejects a synthetic package instead of sealing PASS.
$syntheticRoot = Join-Path $testRoot 'synthetic-main'
New-Item -ItemType Directory -Force -Path $syntheticRoot | Out-Null
[System.IO.File]::WriteAllText((Join-Path $syntheticRoot 'Mahjong.exe'), '14-byte-text')
$skipEvidence = Join-Path $testRoot 'synthetic-skip-evidence'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Case build -SkipBuild -EvidenceRoot $skipEvidence -PlayerPath (Join-Path $syntheticRoot 'Mahjong.exe') -SourceHead $fakeHead -BuildCommit 'fabricated-build' -MutexWaitSeconds 5 *> (Join-Path $testRoot 'synthetic-skip.log')
Assert-True ($LASTEXITCODE -ne 0) 'synthetic-skip-native-nonzero'
Assert-True ((-not (Test-Path (Join-Path $skipEvidence 'seal.json'))) -or ((Get-Content (Join-Path $skipEvidence 'seal.json') -Raw | ConvertFrom-Json).status -ne 'PASS')) 'synthetic-skip-no-pass-seal'
Assert-True ((Get-Content (Join-Path $testRoot 'synthetic-skip.log') -Raw) -match 'SKIPBUILD_INVALID|PACKAGE_SEAL|MUTEX') 'synthetic-skip-rejected'

# 12. Unknown/native errors are still rejected after the strict gate.
$unknownPath = Join-Path $testRoot 'unknown.json'; Write-C04Json $unknownPath (New-FixtureReport -OutputPath $reportExe -Errors 1 -StepMessages @('Unknown compiler failure'))
$unknown = Test-C04BuildReport -Path $unknownPath -ExpectedOutput $reportExe
Assert-True (-not $unknown.ok) 'report-unknown-rejected'
Assert-Match $unknown.errors 'BUILD_REPORT_STEP_ERROR:.*Unknown compiler failure' 'report-unknown-attributed'
Write-Host "C04PlayerCheatRunner.Tests passed=$passed failed=$($failures.Count)"
foreach ($failure in $failures) { Write-Host "FAIL $failure" }
if ($failures.Count -gt 0) { exit 1 }
exit 0
