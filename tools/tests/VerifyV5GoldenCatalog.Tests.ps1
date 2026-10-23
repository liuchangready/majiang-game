#requires -Version 5.1
<#
.SYNOPSIS
Self-test for tools/verify-v5-golden-catalog.ps1.

.DESCRIPTION
Copies the catalog, the five fixtures, the five exporters, the five C# readers
and the manifest into an exclusive TEMP tree. It then injects one meaningful
defect per case and asserts that the validator fails with the expected code.
The real tracked fixtures are never written; their SHA-256 values are compared
before and after the run.

Exit codes: 0 = PASS, 1 = FAIL, 2 = BLOCKED.
#>
param(
    [string]$RepoRoot = '',
    [string]$LogPath = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$script:Failures = New-Object System.Collections.Generic.List[string]

function Add-Failure {
    param([string]$Message)
    $script:Failures.Add($Message) | Out-Null
}

function Get-Sha256 {
    param([string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Read-JsonFile {
    param([string]$Path)
    return (Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json)
}

function Write-JsonFile {
    param([string]$Path, [object]$Value)
    $text = $Value | ConvertTo-Json -Depth 30
    [System.IO.File]::WriteAllText($Path, $text, (New-Object System.Text.UTF8Encoding($false)))
}

function New-BaseTree {
    param([string]$Root, [string]$SourceRepo)
    New-Item -ItemType Directory -Force -Path (Join-Path $Root 'tests\v5-golden') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $Root 'doc') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $Root 'engine\src\test\kotlin\com\majiang\model') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $Root 'engine\src\test\kotlin\com\majiang\rules') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $Root 'engine\src\test\kotlin\com\majiang\v3') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $Root 'dotnet\tests\Mahjong.Domain.Tests') | Out-Null

    Copy-Item -LiteralPath (Join-Path $SourceRepo 'tests\v5-golden\physical-v1.json') -Destination (Join-Path $Root 'tests\v5-golden\physical-v1.json')
    Copy-Item -LiteralPath (Join-Path $SourceRepo 'tests\v5-golden\scoring-v1.json') -Destination (Join-Path $Root 'tests\v5-golden\scoring-v1.json')
    Copy-Item -LiteralPath (Join-Path $SourceRepo 'tests\v5-golden\round-v1.json') -Destination (Join-Path $Root 'tests\v5-golden\round-v1.json')
    Copy-Item -LiteralPath (Join-Path $SourceRepo 'tests\v5-golden\cheat-v1.json') -Destination (Join-Path $Root 'tests\v5-golden\cheat-v1.json')
    Copy-Item -LiteralPath (Join-Path $SourceRepo 'tests\v5-golden\shuffle-v1.json') -Destination (Join-Path $Root 'tests\v5-golden\shuffle-v1.json')
    Copy-Item -LiteralPath (Join-Path $SourceRepo 'tests\v5-golden\catalog-v1.json') -Destination (Join-Path $Root 'tests\v5-golden\catalog-v1.json')

    $manifest = @(Get-ChildItem -LiteralPath (Join-Path $SourceRepo 'doc') -Recurse -Filter 'V5-0-GOLDEN-MANIFEST.md' -File)[0]
    $manifestDir = Join-Path $Root ('doc\' + $manifest.Directory.Name)
    New-Item -ItemType Directory -Force -Path $manifestDir | Out-Null
    Copy-Item -LiteralPath $manifest.FullName -Destination (Join-Path $manifestDir $manifest.Name)

    $exporterMap = @{
        'engine\src\test\kotlin\com\majiang\model\V5PhysicalGoldenExporterTest.kt' = 'engine\src\test\kotlin\com\majiang\model'
        'engine\src\test\kotlin\com\majiang\rules\V5ScoringGoldenExporterTest.kt' = 'engine\src\test\kotlin\com\majiang\rules'
        'engine\src\test\kotlin\com\majiang\rules\V5RoundGoldenExportTest.kt' = 'engine\src\test\kotlin\com\majiang\rules'
        'engine\src\test\kotlin\com\majiang\v3\V5CheatGoldenExporterTest.kt' = 'engine\src\test\kotlin\com\majiang\v3'
        'engine\src\test\kotlin\com\majiang\v3\V5ShuffleGoldenExporterTest.kt' = 'engine\src\test\kotlin\com\majiang\v3'
    }
    foreach ($relative in $exporterMap.Keys) {
        $source = Join-Path $SourceRepo ($relative -replace '/', '\')
        $destinationDir = Join-Path $Root $exporterMap[$relative]
        Copy-Item -LiteralPath $source -Destination (Join-Path $destinationDir ([System.IO.Path]::GetFileName($relative)))
    }

    foreach ($name in @('PhysicalGoldenTests.cs', 'ScoringGoldenTests.cs', 'RoundReplayGoldenTests.cs', 'CheatGoldenTests.cs', 'ShuffleGoldenTests.cs')) {
        Copy-Item -LiteralPath (Join-Path $SourceRepo ('dotnet\tests\Mahjong.Domain.Tests\' + $name)) -Destination (Join-Path $Root ('dotnet\tests\Mahjong.Domain.Tests\' + $name))
    }
}

function New-CaseTree {
    param([string]$BaseRoot, [string]$CaseRoot)
    if (Test-Path -LiteralPath $CaseRoot) {
        Remove-Item -LiteralPath $CaseRoot -Recurse -Force
    }
    Copy-Item -LiteralPath $BaseRoot -Destination $CaseRoot -Recurse
}

function Invoke-Validator {
    param([string]$Root)
    $validator = Join-Path $RepoRoot 'tools\verify-v5-golden-catalog.ps1'
    $catalog = Join-Path $Root 'tests\v5-golden\catalog-v1.json'
    $manifest = @(Get-ChildItem -LiteralPath (Join-Path $Root 'doc') -Recurse -Filter 'V5-0-GOLDEN-MANIFEST.md' -File)[0].FullName
    $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $validator -RepoRoot $Root -CatalogPath $catalog -ManifestPath $manifest 2>&1
    $code = $LASTEXITCODE
    return [pscustomobject]@{ Code = $code; Output = (@($output) -join "`n") }
}

function Update-CatalogFixtureSha {
    param([string]$Root, [string]$FixtureId)
    $catalogPath = Join-Path $Root 'tests\v5-golden\catalog-v1.json'
    $catalog = Read-JsonFile $catalogPath
    foreach ($fixture in @($catalog.fixtures)) {
        if ([string]$fixture.id -eq $FixtureId) {
            $fixturePath = Join-Path $Root ($fixture.path -replace '/', '\')
            $fixture.sha256 = Get-Sha256 $fixturePath
            $fixture.bytes = (Get-Item -LiteralPath $fixturePath).Length
        }
    }
    Write-JsonFile $catalogPath $catalog
}

function Set-JsonProperty {
    param([object]$Object, [string]$Name, [object]$Value)
    $Object.$Name = $Value
}

$repo = if ([string]::IsNullOrWhiteSpace($RepoRoot)) { (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path } else { (Resolve-Path $RepoRoot).Path }
$RepoRoot = $repo
$artifactDir = Join-Path $repo 'artifacts\golden-catalog'
New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null
if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $LogPath = Join-Path $artifactDir 'validator-selftest.log'
}

foreach ($tool in @('Get-FileHash', 'ConvertFrom-Json')) {
    if ($null -eq (Get-Command $tool -ErrorAction SilentlyContinue)) {
        Write-Output ('BLOCKED missing-tool {0}' -f $tool)
        exit 2
    }
}

$realFixtures = @('physical-v1.json', 'scoring-v1.json', 'round-v1.json', 'cheat-v1.json', 'shuffle-v1.json')
$beforeHashes = @{}
foreach ($name in $realFixtures) {
    $beforeHashes[$name] = Get-Sha256 (Join-Path $repo ('tests\v5-golden\' + $name))
}

$tempRoot = Join-Path $env:TEMP ('v5-golden-selftest-' + [guid]::NewGuid().ToString('N'))
$baseRoot = Join-Path $tempRoot 'base'
$caseRoot = Join-Path $tempRoot 'case'
New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
New-BaseTree -Root $baseRoot -SourceRepo $repo

$script:CaseResults = @()
function Invoke-NegativeCase {
    param(
        [string]$Name,
        [string]$ExpectedCode,
        [scriptblock]$Mutator
    )
    New-CaseTree -BaseRoot $baseRoot -CaseRoot $caseRoot
    & $Mutator $caseRoot
    $result = Invoke-Validator $caseRoot
    $ok = ($result.Code -eq 1) -and $result.Output.Contains($ExpectedCode)
    $script:CaseResults += [pscustomobject]@{
        Name = $Name
        Expected = $ExpectedCode
        ExitCode = $result.Code
        Ok = $ok
        Output = $result.Output
    }
    if (-not $ok) {
        Add-Failure ('{0}: expected exit1 containing {1}, got exit{2}; output={3}' -f $Name, $ExpectedCode, $result.Code, $result.Output)
    }
}

# Positive baseline.
$positive = Invoke-Validator $baseRoot
$positiveOk = ($positive.Code -eq 0) -and $positive.Output.Contains('PASS')
$script:CaseResults += [pscustomobject]@{ Name = 'positive-baseline'; Expected = 'PASS'; ExitCode = $positive.Code; Ok = $positiveOk; Output = $positive.Output }
if (-not $positiveOk) {
    Add-Failure ('positive-baseline: expected exit0 PASS, got exit{0}; output={1}' -f $positive.Code, $positive.Output)
}

# 1. Missing fixture.
Invoke-NegativeCase -Name 'missing-fixture' -ExpectedCode 'FIXTURE_MISSING' -Mutator {
    param($root)
    Remove-Item -LiteralPath (Join-Path $root 'tests\v5-golden\physical-v1.json') -Force
}

# 2. Fixture byte change without catalog update.
Invoke-NegativeCase -Name 'sha-change' -ExpectedCode 'FIXTURE_SHA_MISMATCH' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\cheat-v1.json'
    $text = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
    $text = $text.Replace('"rulesetVersion": 1', '"rulesetVersion": 2')
    [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($false)))
}

# 3. Provenance mismatch: catalog sourceV4Commit no longer equals fixture v4Commit.
Invoke-NegativeCase -Name 'source-commit' -ExpectedCode 'SOURCE_COMMIT_MISMATCH' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\catalog-v1.json'
    $catalog = Read-JsonFile $path
    foreach ($fixture in @($catalog.fixtures)) {
        if ([string]$fixture.id -eq 'physical-v1') { $fixture.sourceV4Commit = '0000000000000000000000000000000000000000' }
    }
    Write-JsonFile $path $catalog
}

# 4. Catalog rulesetVersion does not equal fixture rulesetVersion.
Invoke-NegativeCase -Name 'ruleset-version' -ExpectedCode 'RULESET_VERSION_MISMATCH' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\catalog-v1.json'
    $catalog = Read-JsonFile $path
    foreach ($fixture in @($catalog.fixtures)) {
        if ([string]$fixture.id -eq 'scoring-v1') { $fixture.rulesetVersion = 2 }
    }
    Write-JsonFile $path $catalog
}

# 5. Catalog extractionStatus does not equal fixture extractionStatus.
Invoke-NegativeCase -Name 'extraction-status' -ExpectedCode 'EXTRACTION_STATUS_MISMATCH' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\catalog-v1.json'
    $catalog = Read-JsonFile $path
    foreach ($fixture in @($catalog.fixtures)) {
        if ([string]$fixture.id -eq 'cheat-v1') { $fixture.extractionStatus = 'pending' }
    }
    Write-JsonFile $path $catalog
}

# 6. Catalog generatedByCommand does not equal fixture generatedByCommand.
Invoke-NegativeCase -Name 'command' -ExpectedCode 'COMMAND_MISMATCH' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\catalog-v1.json'
    $catalog = Read-JsonFile $path
    foreach ($fixture in @($catalog.fixtures)) {
        if ([string]$fixture.id -eq 'shuffle-v1') { $fixture.generatedByCommand = 'bogus command' }
    }
    Write-JsonFile $path $catalog
}

# 7. Round dual-source commit mismatch.
Invoke-NegativeCase -Name 'round-engine-commit' -ExpectedCode 'SOURCE_ENGINE_COMMIT_MISMATCH' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\catalog-v1.json'
    $catalog = Read-JsonFile $path
    foreach ($fixture in @($catalog.fixtures)) {
        if ([string]$fixture.id -eq 'round-v1') { $fixture.sourceV4EngineCommit = '0000000000000000000000000000000000000000' }
    }
    Write-JsonFile $path $catalog
}

# 8. physical exporterSourceSha256 in the fixture must be checked against the real exporter.
Invoke-NegativeCase -Name 'exporter-source-field' -ExpectedCode 'EXPORTER_SOURCE_SHA_MISMATCH' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\physical-v1.json'
    $json = Read-JsonFile $path
    $json.exporterSourceSha256 = '0000000000000000000000000000000000000000000000000000000000000000'
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'physical-v1'
}

# 9. Unknown schema (fixture changed and catalog hash refreshed so schema is the only defect).
Invoke-NegativeCase -Name 'unknown-schema' -ExpectedCode 'FIXTURE_SCHEMA_UNKNOWN' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\physical-v1.json'
    $json = Read-JsonFile $path
    $json.schema = 'v5-evil-golden-v1'
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'physical-v1'
}

# 4. Duplicate case ID in actual fixture, with counts refreshed to isolate the duplicate.
Invoke-NegativeCase -Name 'duplicate-case-id' -ExpectedCode 'DUPLICATE_CASE_ID' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\physical-v1.json'
    $json = Read-JsonFile $path
    $samples = @($json.samples)
    $json.samples = @($samples + $samples[0])
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'physical-v1'
    $catalogPath = Join-Path $root 'tests\v5-golden\catalog-v1.json'
    $catalog = Read-JsonFile $catalogPath
    foreach ($fixture in @($catalog.fixtures)) {
        if ([string]$fixture.id -eq 'physical-v1') {
            $fixture.caseTotal = 7
            foreach ($group in @($fixture.groups)) {
                if ([string]$group.name -eq 'samples') {
                    $group.count = 7
                    $group.ids = @()
                }
            }
        }
    }
    $catalog.denominators.fixtureConcreteCases = [int]$catalog.denominators.fixtureConcreteCases + 1
    Write-JsonFile $catalogPath $catalog
}

# 5. Bad anchor.
Invoke-NegativeCase -Name 'bad-anchor' -ExpectedCode 'ANCHOR_MISSING' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\catalog-v1.json'
    $catalog = Read-JsonFile $path
    foreach ($requirement in @($catalog.requirements)) {
        if ([string]$requirement.id -eq 'G-001a') {
            $requirement.anchors = @('physical-v1:samples/G-999')
        }
    }
    Write-JsonFile $path $catalog
}

# 6. Declared group count mismatch.
Invoke-NegativeCase -Name 'bad-group-count' -ExpectedCode 'GROUP_COUNT_MISMATCH' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\catalog-v1.json'
    $catalog = Read-JsonFile $path
    foreach ($fixture in @($catalog.fixtures)) {
        if ([string]$fixture.id -eq 'scoring-v1') {
            foreach ($group in @($fixture.groups)) {
                if ([string]$group.name -eq 'winSamples') { $group.count = 47 }
            }
        }
    }
    Write-JsonFile $path $catalog
}

# 7. Missing requirement.
Invoke-NegativeCase -Name 'missing-requirement' -ExpectedCode 'REQUIREMENT_COUNT_MISMATCH' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\catalog-v1.json'
    $catalog = Read-JsonFile $path
    $catalog.requirements = @($catalog.requirements)[0..71]
    Write-JsonFile $path $catalog
}

# 8. Duplicate requirement ID.
Invoke-NegativeCase -Name 'duplicate-requirement' -ExpectedCode 'DUPLICATE_REQUIREMENT_ID' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\catalog-v1.json'
    $catalog = Read-JsonFile $path
    $reqs = @($catalog.requirements)
    $catalog.requirements = @($reqs + $reqs[0])
    $catalog.denominators.manifestRequirements = 74
    Write-JsonFile $path $catalog
}

# 9. Unknown coverage.
Invoke-NegativeCase -Name 'unknown-coverage' -ExpectedCode 'COVERAGE_UNKNOWN' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\catalog-v1.json'
    $catalog = Read-JsonFile $path
    foreach ($requirement in @($catalog.requirements)) {
        if ([string]$requirement.id -eq 'G-001a') { $requirement.coverage = 'done' }
    }
    Write-JsonFile $path $catalog
}

# 10. C# fact count mismatch.
Invoke-NegativeCase -Name 'fact-count' -ExpectedCode 'CSHARP_FACT_COUNT_MISMATCH' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\catalog-v1.json'
    $catalog = Read-JsonFile $path
    foreach ($fixture in @($catalog.fixtures)) {
        if ([string]$fixture.id -eq 'physical-v1') { $fixture.csharp.factCount = 8 }
    }
    Write-JsonFile $path $catalog
}

# 11. Missing required case key.
Invoke-NegativeCase -Name 'case-structure' -ExpectedCode 'CASE_STRUCTURE_MISSING' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\physical-v1.json'
    $json = Read-JsonFile $path
    $samples = @($json.samples)
    $samples[0].PSObject.Properties.Remove('expected')
    $json.samples = $samples
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'physical-v1'
}

# 12. ID-only singleton must fail: roundTerminal loses input/settlement/expected.
Invoke-NegativeCase -Name 'singleton-id-only' -ExpectedCode 'CASE_STRUCTURE_MISSING' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\round-v1.json'
    $json = Read-JsonFile $path
    $json.roundTerminal = [pscustomobject]@{ sampleId = 'RG-001' }
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'round-v1'
}

# 13. requiredKeys declared as null must be rejected.
Invoke-NegativeCase -Name 'requiredkeys-null' -ExpectedCode 'GROUP_STRUCTURE_INVALID' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\catalog-v1.json'
    $catalog = Read-JsonFile $path
    foreach ($fixture in @($catalog.fixtures)) {
        if ([string]$fixture.id -eq 'physical-v1') {
            foreach ($group in @($fixture.groups)) {
                if ([string]$group.name -eq 'samples') { $group.requiredKeys = $null }
            }
        }
    }
    Write-JsonFile $path $catalog
}

# 14. requiredKeys declared as a scalar instead of an array must be rejected.
Invoke-NegativeCase -Name 'requiredkeys-scalar' -ExpectedCode 'GROUP_STRUCTURE_INVALID' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\catalog-v1.json'
    $catalog = Read-JsonFile $path
    foreach ($fixture in @($catalog.fixtures)) {
        if ([string]$fixture.id -eq 'physical-v1') {
            foreach ($group in @($fixture.groups)) {
                if ([string]$group.name -eq 'samples') { $group.requiredKeys = 'sampleId' }
            }
        }
    }
    Write-JsonFile $path $catalog
}

# 15. Nested object key removal must fail (ledger.chargeOnce).
Invoke-NegativeCase -Name 'nested-object-key' -ExpectedCode 'GROUP_KEYS_MISMATCH' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\cheat-v1.json'
    $json = Read-JsonFile $path
    $json.ledger.PSObject.Properties.Remove('chargeOnce')
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'cheat-v1'
}

# 16. Required path with wrong type must fail (roundTerminal.expected as string).
Invoke-NegativeCase -Name 'singleton-wrong-type' -ExpectedCode 'CASE_STRUCTURE_TYPE' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\round-v1.json'
    $json = Read-JsonFile $path
    $json.roundTerminal.expected = 'not-an-object'
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'round-v1'
}

# 17. Exporter byte change.
Invoke-NegativeCase -Name 'exporter-sha' -ExpectedCode 'EXPORTER_SHA_MISMATCH' -Mutator {
    param($root)
    $path = Join-Path $root 'engine\src\test\kotlin\com\majiang\model\V5PhysicalGoldenExporterTest.kt'
    [System.IO.File]::AppendAllText($path, "`n// selftest mutation`n", (New-Object System.Text.UTF8Encoding($false)))
}

# 12. Declared ID mismatch.
Invoke-NegativeCase -Name 'group-id-mismatch' -ExpectedCode 'GROUP_IDS_MISMATCH' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\catalog-v1.json'
    $catalog = Read-JsonFile $path
    foreach ($fixture in @($catalog.fixtures)) {
        if ([string]$fixture.id -eq 'physical-v1') {
            foreach ($group in @($fixture.groups)) {
                if ([string]$group.name -eq 'samples') { $group.ids[0] = 'G-999a' }
            }
        }
    }
    Write-JsonFile $path $catalog
}

# 13. Manifest row removal.
Invoke-NegativeCase -Name 'manifest-row-removed' -ExpectedCode 'MANIFEST_ROW_COUNT' -Mutator {
    param($root)
    $manifest = @(Get-ChildItem -LiteralPath (Join-Path $root 'doc') -Recurse -Filter 'V5-0-GOLDEN-MANIFEST.md' -File)[0]
    $lines = @([System.IO.File]::ReadAllLines($manifest.FullName, [System.Text.Encoding]::UTF8))
    $kept = @()
    $removed = $false
    foreach ($line in $lines) {
        if (-not $removed -and $line -match '^\|\s*G-001a\s*\|') {
            $removed = $true
            continue
        }
        $kept += $line
    }
    [System.IO.File]::WriteAllLines($manifest.FullName, $kept, (New-Object System.Text.UTF8Encoding($false)))
}

# 14. Catalog schema mutation.
Invoke-NegativeCase -Name 'catalog-schema' -ExpectedCode 'CATALOG_SCHEMA' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\catalog-v1.json'
    $catalog = Read-JsonFile $path
    $catalog.schema = 'v5-golden-catalog-v2'
    Write-JsonFile $path $catalog
}

# Real fixture immutability check.
$afterHashes = @{}
foreach ($name in $realFixtures) {
    $afterHashes[$name] = Get-Sha256 (Join-Path $repo ('tests\v5-golden\' + $name))
}
foreach ($name in $realFixtures) {
    if ($beforeHashes[$name] -ne $afterHashes[$name]) {
        Add-Failure ('real fixture changed during selftest: {0} {1} -> {2}' -f $name, $beforeHashes[$name], $afterHashes[$name])
    }
}

$logLines = @()
$logLines += '# v5 golden catalog validator self-test'
$logLines += ('repo=' + $repo)
$logLines += ('temp=' + $tempRoot)
$logLines += ''
foreach ($case in $script:CaseResults) {
    $status = if ($case.Ok) { 'PASS' } else { 'FAIL' }
    $logLines += ('{0} {1} expected={2} exit={3}' -f $status, $case.Name, $case.Expected, $case.ExitCode)
    $logLines += $case.Output
    $logLines += ''
}
if ($script:Failures.Count -gt 0) {
    $logLines += 'SELFTEST FAIL'
    $logLines += $script:Failures
}
else {
    $logLines += ('SELFTEST PASS cases={0} positives=1 negatives={1}' -f $script:CaseResults.Count, ($script:CaseResults.Count - 1))
    $logLines += 'real-fixtures-unchanged=true'
}
[System.IO.File]::WriteAllLines($LogPath, $logLines, (New-Object System.Text.UTF8Encoding($false)))

try {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force
}
catch {
    Add-Failure ('temp cleanup failed: ' + $_.Exception.Message)
}

if ($script:Failures.Count -gt 0) {
    $script:Failures | Select-Object -First 20 | ForEach-Object { Write-Output ('FAIL {0}' -f $_) }
    Write-Output ('SELFTEST FAIL log=' + $LogPath)
    exit 1
}

$passCount = @($caseResults | Where-Object { $_.Ok }).Count
Write-Output ('SELFTEST PASS cases={0} positives=1 negatives={1} log={2}' -f $script:CaseResults.Count, ($script:CaseResults.Count - 1), $LogPath)
Write-Output ('real-fixtures-unchanged=true')
exit 0
