#requires -Version 5.1
<#
.SYNOPSIS
Self-test for tools/verify-v5-golden-catalog.ps1.

.DESCRIPTION
Copies the catalog, the six fixtures (including accepted ai-v1), the six
exporters, the six catalog C# readers, the accepted AI state-binding reader and
the manifest into an exclusive TEMP tree. It then injects one meaningful
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
    New-Item -ItemType Directory -Force -Path (Join-Path $Root 'engine\src\test\kotlin\com\majiang\state') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $Root 'dotnet\tests\Mahjong.Domain.Tests') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $Root 'dotnet\tests\Mahjong.Ai.Tests') | Out-Null

    Copy-Item -LiteralPath (Join-Path $SourceRepo 'tests\v5-golden\physical-v1.json') -Destination (Join-Path $Root 'tests\v5-golden\physical-v1.json')
    Copy-Item -LiteralPath (Join-Path $SourceRepo 'tests\v5-golden\scoring-v1.json') -Destination (Join-Path $Root 'tests\v5-golden\scoring-v1.json')
    Copy-Item -LiteralPath (Join-Path $SourceRepo 'tests\v5-golden\round-v1.json') -Destination (Join-Path $Root 'tests\v5-golden\round-v1.json')
    Copy-Item -LiteralPath (Join-Path $SourceRepo 'tests\v5-golden\cheat-v1.json') -Destination (Join-Path $Root 'tests\v5-golden\cheat-v1.json')
    Copy-Item -LiteralPath (Join-Path $SourceRepo 'tests\v5-golden\shuffle-v1.json') -Destination (Join-Path $Root 'tests\v5-golden\shuffle-v1.json')
    Copy-Item -LiteralPath (Join-Path $SourceRepo 'tests\v5-golden\ai-v1.json') -Destination (Join-Path $Root 'tests\v5-golden\ai-v1.json')
    Copy-Item -LiteralPath (Join-Path $SourceRepo 'tests\v5-golden\catalog-v1.json') -Destination (Join-Path $Root 'tests\v5-golden\catalog-v1.json')

    $manifest = @(Get-ChildItem -LiteralPath (Join-Path $SourceRepo 'doc') -Recurse -Filter 'V5-0-GOLDEN-MANIFEST.md' -File)[0]
    $manifestDir = Join-Path $Root ('doc\' + $manifest.Directory.Name)
    New-Item -ItemType Directory -Force -Path $manifestDir | Out-Null
    Copy-Item -LiteralPath $manifest.FullName -Destination (Join-Path $manifestDir $manifest.Name)
    $acceptedReport = @(Get-ChildItem -LiteralPath (Join-Path $SourceRepo 'doc') -Recurse -Filter 'V5-N3-AI-REPLAY-BINDING.md' -File)[0]
    Copy-Item -LiteralPath $acceptedReport.FullName -Destination (Join-Path $manifestDir $acceptedReport.Name)

    $exporterMap = @{
        'engine\src\test\kotlin\com\majiang\model\V5PhysicalGoldenExporterTest.kt' = 'engine\src\test\kotlin\com\majiang\model'
        'engine\src\test\kotlin\com\majiang\rules\V5ScoringGoldenExporterTest.kt' = 'engine\src\test\kotlin\com\majiang\rules'
        'engine\src\test\kotlin\com\majiang\rules\V5RoundGoldenExportTest.kt' = 'engine\src\test\kotlin\com\majiang\rules'
        'engine\src\test\kotlin\com\majiang\v3\V5CheatGoldenExporterTest.kt' = 'engine\src\test\kotlin\com\majiang\v3'
        'engine\src\test\kotlin\com\majiang\v3\V5ShuffleGoldenExporterTest.kt' = 'engine\src\test\kotlin\com\majiang\v3'
        'engine\src\test\kotlin\com\majiang\state\V5AiGoldenExporterTest.kt' = 'engine\src\test\kotlin\com\majiang\state'
    }
    foreach ($relative in $exporterMap.Keys) {
        $source = Join-Path $SourceRepo ($relative -replace '/', '\')
        $destinationDir = Join-Path $Root $exporterMap[$relative]
        Copy-Item -LiteralPath $source -Destination (Join-Path $destinationDir ([System.IO.Path]::GetFileName($relative)))
    }

    foreach ($name in @('PhysicalGoldenTests.cs', 'ScoringGoldenTests.cs', 'RoundReplayGoldenTests.cs', 'CheatGoldenTests.cs', 'ShuffleGoldenTests.cs')) {
        Copy-Item -LiteralPath (Join-Path $SourceRepo ('dotnet\tests\Mahjong.Domain.Tests\' + $name)) -Destination (Join-Path $Root ('dotnet\tests\Mahjong.Domain.Tests\' + $name))
    }
    foreach ($name in @('AiReplayGoldenTests.cs', 'AiReplayStateGoldenTests.cs')) {
        Copy-Item -LiteralPath (Join-Path $SourceRepo ('dotnet\tests\Mahjong.Ai.Tests\' + $name)) -Destination (Join-Path $Root ('dotnet\tests\Mahjong.Ai.Tests\' + $name))
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
if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $artifactDir = Join-Path $repo 'artifacts\golden-catalog'
    New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null
    $LogPath = Join-Path $artifactDir 'validator-selftest.log'
}

foreach ($tool in @('Get-FileHash', 'ConvertFrom-Json')) {
    if ($null -eq (Get-Command $tool -ErrorAction SilentlyContinue)) {
        Write-Output ('BLOCKED missing-tool {0}' -f $tool)
        exit 2
    }
}

$realFixtures = @('physical-v1.json', 'scoring-v1.json', 'round-v1.json', 'cheat-v1.json', 'shuffle-v1.json', 'ai-v1.json')
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

# 17. roundTerminal.input={} must fail its nested consumed fields.
Invoke-NegativeCase -Name 'terminal-empty-input' -ExpectedCode 'CASE_STRUCTURE_MISSING' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\round-v1.json'
    $json = Read-JsonFile $path
    $json.roundTerminal.input = [pscustomobject]@{}
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'round-v1'
}

# 18. controlEpoch.operations[0]={} must fail the op discriminator/fields.
Invoke-NegativeCase -Name 'clock-operation-empty' -ExpectedCode 'CASE_STRUCTURE_MISSING' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\round-v1.json'
    $json = Read-JsonFile $path
    $json.controlEpoch.operations = @([pscustomobject]@{})
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'round-v1'
}

# 19. ledger.chargeOnce={} must fail its consumed result fields.
Invoke-NegativeCase -Name 'ledger-charge-empty' -ExpectedCode 'CASE_STRUCTURE_MISSING' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\cheat-v1.json'
    $json = Read-JsonFile $path
    $json.ledger.chargeOnce = [pscustomobject]@{}
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'cheat-v1'
}

# 20. null consumed array must fail.
Invoke-NegativeCase -Name 'terminal-array-null' -ExpectedCode 'CASE_STRUCTURE_MISSING' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\round-v1.json'
    $json = Read-JsonFile $path
    $json.roundTerminal.input.scores = $null
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'round-v1'
}

# 21. null operations array must fail.
Invoke-NegativeCase -Name 'clock-operations-null' -ExpectedCode 'CASE_STRUCTURE_MISSING' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\round-v1.json'
    $json = Read-JsonFile $path
    $json.controlEpoch.operations = $null
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'round-v1'
}

# 22. null operation element must fail with a structural type error.
Invoke-NegativeCase -Name 'clock-operation-null' -ExpectedCode 'CASE_STRUCTURE_TYPE' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\round-v1.json'
    $json = Read-JsonFile $path
    $json.controlEpoch.operations = @($null)
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'round-v1'
}

# 23. Middle score sample with null input must fail, not be skipped as first/last.
Invoke-NegativeCase -Name 'score-middle-null-input' -ExpectedCode 'CASE_STRUCTURE_MISSING' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\scoring-v1.json'
    $json = Read-JsonFile $path
    $json.scoreSamples[70].input = $null
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'scoring-v1'
}

# 24. Middle random sample with null context must fail.
Invoke-NegativeCase -Name 'random-middle-null-context' -ExpectedCode 'CASE_STRUCTURE_MISSING' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\scoring-v1.json'
    $json = Read-JsonFile $path
    $json.randomSamples[5000].context = $null
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'scoring-v1'
}

# 25. Middle random sample with wrong context type must fail.
Invoke-NegativeCase -Name 'random-middle-wrong-context' -ExpectedCode 'CASE_STRUCTURE_TYPE' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\scoring-v1.json'
    $json = Read-JsonFile $path
    $json.randomSamples[5000].context = 'not-an-object'
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'scoring-v1'
}

# 26. Empty consumed objects: score input/expected and random context.
Invoke-NegativeCase -Name 'empty-consumed-objects' -ExpectedCode 'CASE_STRUCTURE_MISSING' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\scoring-v1.json'
    $json = Read-JsonFile $path
    $json.scoreSamples[70].input = [pscustomobject]@{}
    $json.scoreSamples[70].expected = [pscustomobject]@{}
    $json.randomSamples[5000].context = [pscustomobject]@{}
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'scoring-v1'
}

# 27. Non-empty consumed objects missing required fields.
Invoke-NegativeCase -Name 'missing-consumed-fields' -ExpectedCode 'CASE_STRUCTURE_MISSING' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\scoring-v1.json'
    $json = Read-JsonFile $path
    $json.scoreSamples[70].input.PSObject.Properties.Remove('fu')
    $json.randomSamples[5000].context.PSObject.Properties.Remove('winTile')
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'scoring-v1'
}

# 28. Consumed fields with wrong types.
Invoke-NegativeCase -Name 'wrong-consumed-types' -ExpectedCode 'CASE_STRUCTURE_TYPE' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\scoring-v1.json'
    $json = Read-JsonFile $path
    $json.scoreSamples[70].input.fu = 'not-a-number'
    $json.randomSamples[5000].context.isTsumo = 'not-a-boolean'
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'scoring-v1'
}

# 29. Deep empty expected.dora object must fail.
Invoke-NegativeCase -Name 'win-dora-empty' -ExpectedCode 'CASE_STRUCTURE_MISSING' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\scoring-v1.json'
    $json = Read-JsonFile $path
    $json.winSamples[24].expected.dora = [pscustomobject]@{}
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'scoring-v1'
}

# 30. Empty payment-plan component element must fail.
Invoke-NegativeCase -Name 'score-payment-component-empty' -ExpectedCode 'CASE_STRUCTURE_MISSING' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\scoring-v1.json'
    $json = Read-JsonFile $path
    $json.scoreSamples[70].expected.paymentPlans.dealerTsumo.components[0] = [pscustomobject]@{}
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'scoring-v1'
}

# 31. Empty physical tile element must fail.
Invoke-NegativeCase -Name 'physical-tile-empty' -ExpectedCode 'CASE_STRUCTURE_MISSING' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\physical-v1.json'
    $json = Read-JsonFile $path
    $json.samples[0].expected.tiles[0] = [pscustomobject]@{}
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'physical-v1'
}

# 32. Physical tile element with wrong field type must fail.
Invoke-NegativeCase -Name 'physical-tile-wrong-type' -ExpectedCode 'CASE_STRUCTURE_TYPE' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\physical-v1.json'
    $json = Read-JsonFile $path
    $json.samples[0].expected.tiles[0].tileId = 'not-a-number'
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'physical-v1'
}

# 33. Random closed/dora elements must be numbers.
Invoke-NegativeCase -Name 'random-closed-object' -ExpectedCode 'CASE_STRUCTURE_TYPE' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\scoring-v1.json'
    $json = Read-JsonFile $path
    $json.randomSamples[5000].closed[0] = [pscustomobject]@{}
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'scoring-v1'
}

# 34. Random meld elements must carry type/tiles/fromPlayer/calledTile.
Invoke-NegativeCase -Name 'random-meld-empty' -ExpectedCode 'CASE_STRUCTURE_MISSING' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\scoring-v1.json'
    $json = Read-JsonFile $path
    $json.randomSamples[5000].melds = @([pscustomobject]@{})
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'scoring-v1'
}

# 35. Random win yaku elements must carry code/han.
Invoke-NegativeCase -Name 'random-yaku-empty' -ExpectedCode 'CASE_STRUCTURE_MISSING' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\scoring-v1.json'
    $json = Read-JsonFile $path
    $target = -1
    for ($i = 0; $i -lt $json.randomSamples.Count; $i++) {
        $sample = $json.randomSamples[$i]
        if ($sample.expected.status -eq 'win' -and $sample.expected.yaku.Count -gt 0) { $target = $i; break }
    }
    if ($target -lt 0) { throw 'no win random sample with yaku' }
    $json.randomSamples[$target].expected.yaku[0] = [pscustomobject]@{}
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'scoring-v1'
}

# 36. Removing an existing nullable key must fail (missing vs null).
Invoke-NegativeCase -Name 'required-nullable-key-missing' -ExpectedCode 'CASE_STRUCTURE_MISSING' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\scoring-v1.json'
    $json = Read-JsonFile $path
    $json.scoreSamples[70].expected.PSObject.Properties.Remove('capCode')
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'scoring-v1'
}

# 37. Random dora/ura elements must be numbers.
Invoke-NegativeCase -Name 'random-dora-object' -ExpectedCode 'CASE_STRUCTURE_TYPE' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\scoring-v1.json'
    $json = Read-JsonFile $path
    $json.randomSamples[5000].dora = @([pscustomobject]@{})
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'scoring-v1'
}

# 38. Fixture schema is a fixed literal; case change must fail.
Invoke-NegativeCase -Name 'fixture-schema-case' -ExpectedCode 'FIXTURE_SCHEMA_UNKNOWN' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\physical-v1.json'
    $json = Read-JsonFile $path
    $json.schema = 'V5-PHYSICAL-GOLDEN-V1'
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'physical-v1'
}

# 39. Catalog schema is a fixed literal; case change must fail.
Invoke-NegativeCase -Name 'catalog-schema-case' -ExpectedCode 'CATALOG_SCHEMA' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\catalog-v1.json'
    $catalog = Read-JsonFile $path
    $catalog.schema = 'V5-GOLDEN-CATALOG-V1'
    Write-JsonFile $path $catalog
}

# 40. Catalog extractionStatus must be exactly 'exported'.
Invoke-NegativeCase -Name 'extraction-status-case' -ExpectedCode 'EXTRACTION_STATUS_MISMATCH' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\catalog-v1.json'
    $catalog = Read-JsonFile $path
    foreach ($fixture in @($catalog.fixtures)) {
        if ([string]$fixture.id -eq 'cheat-v1') { $fixture.extractionStatus = 'Exported' }
    }
    Write-JsonFile $path $catalog
}

# 41. Fixture extractionStatus case change must fail.
Invoke-NegativeCase -Name 'fixture-extraction-case' -ExpectedCode 'EXTRACTION_STATUS_MISMATCH' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\cheat-v1.json'
    $json = Read-JsonFile $path
    $json.extractionStatus = 'Exported'
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'cheat-v1'
}

# 42. Unknown random expected.status must fail, even if only status remains.
Invoke-NegativeCase -Name 'random-status-unknown' -ExpectedCode 'CASE_STRUCTURE_UNKNOWN_VARIANT' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\scoring-v1.json'
    $json = Read-JsonFile $path
    $json.randomSamples[5000].expected = [pscustomobject]@{ status = 'invalid-status' }
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'scoring-v1'
}

# 43. Random status is case-sensitive: WIN is not win.
Invoke-NegativeCase -Name 'random-status-case' -ExpectedCode 'CASE_STRUCTURE_UNKNOWN_VARIANT' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\scoring-v1.json'
    $json = Read-JsonFile $path
    $json.randomSamples[5000].expected.status = 'WIN'
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'scoring-v1'
}

# 44. Other branch discriminators must be exact literals.
Invoke-NegativeCase -Name 'branch-discriminators-unknown' -ExpectedCode 'CASE_STRUCTURE_UNKNOWN_VARIANT' -Mutator {
    param($root)
    $roundPath = Join-Path $root 'tests\v5-golden\round-v1.json'
    $round = Read-JsonFile $roundPath
    $round.matchProgression[0].expected.type = 'NextRoundX'
    $round.controlEpoch.operations[0].op = 'INITIAL'
    Write-JsonFile $roundPath $round
    Update-CatalogFixtureSha -Root $root -FixtureId 'round-v1'
    $scorePath = Join-Path $root 'tests\v5-golden\scoring-v1.json'
    $score = Read-JsonFile $scorePath
    $score.settlementSamples[0].input.settlementKind = 'RON'
    Write-JsonFile $scorePath $score
    Update-CatalogFixtureSha -Root $root -FixtureId 'scoring-v1'
}

# 45. Physical G-001b boundarySamples must carry copyIndex.
Invoke-NegativeCase -Name 'physical-boundary-copyindex-missing' -ExpectedCode 'CASE_STRUCTURE_MISSING' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\physical-v1.json'
    $json = Read-JsonFile $path
    $json.samples[1].expected.boundarySamples[0].PSObject.Properties.Remove('copyIndex')
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'physical-v1'
}

# 46. copyIndex null is not a valid number.
Invoke-NegativeCase -Name 'physical-boundary-copyindex-null' -ExpectedCode 'CASE_STRUCTURE_MISSING' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\physical-v1.json'
    $json = Read-JsonFile $path
    $json.samples[1].expected.boundarySamples[0].copyIndex = $null
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'physical-v1'
}

# 47. copyIndex wrong type.
Invoke-NegativeCase -Name 'physical-boundary-copyindex-type' -ExpectedCode 'CASE_STRUCTURE_TYPE' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\physical-v1.json'
    $json = Read-JsonFile $path
    $json.samples[1].expected.boundarySamples[0].copyIndex = 'not-a-number'
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'physical-v1'
}

# 48. G-002 case element losing its consumed fields.
Invoke-NegativeCase -Name 'physical-zone-case-empty' -ExpectedCode 'CASE_STRUCTURE_MISSING' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\physical-v1.json'
    $json = Read-JsonFile $path
    $json.samples[3].expected.cases[0] = [pscustomobject]@{}
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'physical-v1'
}

# 49. G-003a redFiveTileIds elements must be numbers.
Invoke-NegativeCase -Name 'physical-redfive-object' -ExpectedCode 'CASE_STRUCTURE_TYPE' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\physical-v1.json'
    $json = Read-JsonFile $path
    $json.samples[4].expected.cases[0].redFiveTileIds[0] = [pscustomobject]@{}
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'physical-v1'
}

# 50. G-003a positions element must retain its four consumed fields.
Invoke-NegativeCase -Name 'physical-position-empty' -ExpectedCode 'CASE_STRUCTURE_MISSING' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\physical-v1.json'
    $json = Read-JsonFile $path
    $json.samples[4].expected.cases[0].positions[0] = [pscustomobject]@{}
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'physical-v1'
}

# 51. G-003a wall elements must be numbers.
Invoke-NegativeCase -Name 'physical-wall-object' -ExpectedCode 'CASE_STRUCTURE_TYPE' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\physical-v1.json'
    $json = Read-JsonFile $path
    $json.samples[4].expected.cases[0].wall[0] = [pscustomobject]@{}
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'physical-v1'
}

# 52. G-003a baseWall elements must be numbers.
Invoke-NegativeCase -Name 'physical-basewall-object' -ExpectedCode 'CASE_STRUCTURE_TYPE' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\physical-v1.json'
    $json = Read-JsonFile $path
    $json.samples[4].expected.cases[0].baseWall[0] = [pscustomobject]@{}
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'physical-v1'
}

# 53. G-002 accepted branch requires zoneOut.
Invoke-NegativeCase -Name 'physical-zoneout-missing' -ExpectedCode 'CASE_STRUCTURE_MISSING' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\physical-v1.json'
    $json = Read-JsonFile $path
    $case = $json.samples[3].expected.cases | Where-Object { $_.outcome -eq 'accepted' } | Select-Object -First 1
    $case.PSObject.Properties.Remove('zoneOut')
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'physical-v1'
}

# 54. G-002 rejected branch requires exceptionType/semanticCategory.
Invoke-NegativeCase -Name 'physical-rejected-field-missing' -ExpectedCode 'CASE_STRUCTURE_MISSING' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\physical-v1.json'
    $json = Read-JsonFile $path
    $case = $json.samples[3].expected.cases | Where-Object { $_.outcome -eq 'rejected' } | Select-Object -First 1
    $case.PSObject.Properties.Remove('exceptionType')
    $case.PSObject.Properties.Remove('semanticCategory')
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'physical-v1'
}

# 55. G-001c invalid element requires input.
Invoke-NegativeCase -Name 'physical-invalid-input-missing' -ExpectedCode 'CASE_STRUCTURE_MISSING' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\physical-v1.json'
    $json = Read-JsonFile $path
    $json.samples[2].expected.invalid[0].PSObject.Properties.Remove('input')
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'physical-v1'
}

# 56. G-003b duplicate-key limitation object must exist.
Invoke-NegativeCase -Name 'g003b-duplicatekey-missing' -ExpectedCode 'CASE_STRUCTURE_MISSING' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\physical-v1.json'
    $json = Read-JsonFile $path
    $json.samples[5].expected.PSObject.Properties.Remove('duplicateKeyStaticLimitation')
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'physical-v1'
}

# 57. G-003b probeB.after.poolZone must exist.
Invoke-NegativeCase -Name 'g003b-probeb-poolzone-missing' -ExpectedCode 'CASE_STRUCTURE_MISSING' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\physical-v1.json'
    $json = Read-JsonFile $path
    $json.samples[5].expected.probeB.after.PSObject.Properties.Remove('poolZone')
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'physical-v1'
}

# 58. G-003b probeA.after.location must exist and be null.
Invoke-NegativeCase -Name 'g003b-location-missing' -ExpectedCode 'CASE_STRUCTURE_MISSING' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\physical-v1.json'
    $json = Read-JsonFile $path
    $json.samples[5].expected.probeA.after.PSObject.Properties.Remove('location')
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'physical-v1'
}

# 59. G-003b missingTileIds elements must be numbers.
Invoke-NegativeCase -Name 'g003b-missingtileids-object' -ExpectedCode 'CASE_STRUCTURE_TYPE' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\physical-v1.json'
    $json = Read-JsonFile $path
    $json.samples[5].expected.probeA.after.missingTileIds[0] = [pscustomobject]@{}
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'physical-v1'
}

# 60. G-003b csharpMapping.status must exist.
Invoke-NegativeCase -Name 'g003b-csharpmapping-missing' -ExpectedCode 'CASE_STRUCTURE_MISSING' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\physical-v1.json'
    $json = Read-JsonFile $path
    $json.samples[5].expected.probeB.csharpMapping.PSObject.Properties.Remove('status')
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'physical-v1'
}

# 61. Catalog single-sample declared id must equal actual sampleId.
Invoke-NegativeCase -Name 'sample-id-wrong' -ExpectedCode 'GROUP_IDS_MISMATCH' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\catalog-v1.json'
    $catalog = Read-JsonFile $path
    foreach ($fixture in @($catalog.fixtures)) {
        if ([string]$fixture.id -eq 'round-v1') {
            foreach ($group in @($fixture.groups)) {
                if ([string]$group.name -eq 'roundTerminal') { $group.ids = @('RG-999') }
            }
        }
    }
    Write-JsonFile $path $catalog
}

# 62. Missing declared ids must not be silently ignored.
Invoke-NegativeCase -Name 'sample-id-missing' -ExpectedCode 'GROUP_IDS_MISMATCH' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\catalog-v1.json'
    $catalog = Read-JsonFile $path
    foreach ($fixture in @($catalog.fixtures)) {
        if ([string]$fixture.id -eq 'round-v1') {
            foreach ($group in @($fixture.groups)) {
                if ([string]$group.name -eq 'roundTerminal') { $group.PSObject.Properties.Remove('ids') }
            }
        }
    }
    Write-JsonFile $path $catalog
}

# 63. Empty declared ids.
Invoke-NegativeCase -Name 'sample-id-empty' -ExpectedCode 'GROUP_IDS_MISMATCH' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\catalog-v1.json'
    $catalog = Read-JsonFile $path
    foreach ($fixture in @($catalog.fixtures)) {
        if ([string]$fixture.id -eq 'round-v1') {
            foreach ($group in @($fixture.groups)) {
                if ([string]$group.name -eq 'roundTerminal') { $group.ids = @() }
            }
        }
    }
    Write-JsonFile $path $catalog
}

# 64. Multiple declared ids for a singleton.
Invoke-NegativeCase -Name 'sample-id-multi' -ExpectedCode 'GROUP_IDS_MISMATCH' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\catalog-v1.json'
    $catalog = Read-JsonFile $path
    foreach ($fixture in @($catalog.fixtures)) {
        if ([string]$fixture.id -eq 'round-v1') {
            foreach ($group in @($fixture.groups)) {
                if ([string]$group.name -eq 'roundTerminal') { $group.ids = @('RG-001','RG-002') }
            }
        }
    }
    Write-JsonFile $path $catalog
}

# 65. Declared ids wrong type.
Invoke-NegativeCase -Name 'sample-id-scalar' -ExpectedCode 'GROUP_STRUCTURE_INVALID' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\catalog-v1.json'
    $catalog = Read-JsonFile $path
    foreach ($fixture in @($catalog.fixtures)) {
        if ([string]$fixture.id -eq 'round-v1') {
            foreach ($group in @($fixture.groups)) {
                if ([string]$group.name -eq 'roundTerminal') { $group.ids = 'RG-001' }
            }
        }
    }
    Write-JsonFile $path $catalog
}

# 66. Actual singleton id changed while catalog declaration stays.
Invoke-NegativeCase -Name 'sample-id-actual-change' -ExpectedCode 'GROUP_IDS_MISMATCH' -Mutator {
    param($root)
    $path = Join-Path $root 'tests\v5-golden\round-v1.json'
    $json = Read-JsonFile $path
    $json.roundTerminal.sampleId = 'RG-999'
    Write-JsonFile $path $json
    Update-CatalogFixtureSha -Root $root -FixtureId 'round-v1'
}

# 67. Exporter byte change.
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

# 15. AI fixture loses an execution case; catalog group count still declares 7.
Invoke-NegativeCase -Name 'ai-missing-case' -ExpectedCode 'GROUP_COUNT_MISMATCH' -Mutator {
    param($root)
    $fixturePath = Join-Path $root 'tests\v5-golden\ai-v1.json'
    $fixture = Read-JsonFile $fixturePath
    $fixture.cases = @($fixture.cases[0..5])
    Write-JsonFile $fixturePath $fixture
    Update-CatalogFixtureSha -Root $root -FixtureId 'ai-v1'
}

# 16. AI duplicate case ID inside the real cases array.
Invoke-NegativeCase -Name 'ai-duplicate-case' -ExpectedCode 'DUPLICATE_CASE_ID' -Mutator {
    param($root)
    $fixturePath = Join-Path $root 'tests\v5-golden\ai-v1.json'
    $fixture = Read-JsonFile $fixturePath
    $fixture.cases[1].id = $fixture.cases[0].id
    Write-JsonFile $fixturePath $fixture
    Update-CatalogFixtureSha -Root $root -FixtureId 'ai-v1'
}

# 17. Unknown AI schema, with catalog and fixture hash refreshed to isolate it.
Invoke-NegativeCase -Name 'ai-unknown-schema' -ExpectedCode 'FIXTURE_SCHEMA_UNKNOWN' -Mutator {
    param($root)
    $fixturePath = Join-Path $root 'tests\v5-golden\ai-v1.json'
    $fixture = Read-JsonFile $fixturePath
    $fixture.schema = 'v5-ai-golden-v2'
    Write-JsonFile $fixturePath $fixture
    Update-CatalogFixtureSha -Root $root -FixtureId 'ai-v1'
    $catalogPath = Join-Path $root 'tests\v5-golden\catalog-v1.json'
    $catalog = Read-JsonFile $catalogPath
    foreach ($entry in @($catalog.fixtures)) {
        if ([string]$entry.id -eq 'ai-v1') { $entry.schema = 'v5-ai-golden-v2' }
    }
    Write-JsonFile $catalogPath $catalog
}

# 18. AI case loses a required semantic field.
Invoke-NegativeCase -Name 'ai-missing-semantic-field' -ExpectedCode 'CASE_STRUCTURE_MISSING' -Mutator {
    param($root)
    $fixturePath = Join-Path $root 'tests\v5-golden\ai-v1.json'
    $fixture = Read-JsonFile $fixturePath
    $fixture.cases[0].v5.expected.PSObject.Properties.Remove('mappingBasis')
    Write-JsonFile $fixturePath $fixture
    Update-CatalogFixtureSha -Root $root -FixtureId 'ai-v1'
}

# 19. AI decisionClock field with the wrong JSON type.
Invoke-NegativeCase -Name 'ai-wrong-type' -ExpectedCode 'CASE_STRUCTURE_TYPE' -Mutator {
    param($root)
    $fixturePath = Join-Path $root 'tests\v5-golden\ai-v1.json'
    $fixture = Read-JsonFile $fixturePath
    $fixture.decisionClock.baseMillis = '3000'
    Write-JsonFile $fixturePath $fixture
    Update-CatalogFixtureSha -Root $root -FixtureId 'ai-v1'
}

# 20. AI declared caseTotal drifts from the actual cases group.
Invoke-NegativeCase -Name 'ai-count-drift' -ExpectedCode 'CASE_TOTAL_MISMATCH' -Mutator {
    param($root)
    $catalogPath = Join-Path $root 'tests\v5-golden\catalog-v1.json'
    $catalog = Read-JsonFile $catalogPath
    foreach ($entry in @($catalog.fixtures)) {
        if ([string]$entry.id -eq 'ai-v1') { $entry.caseTotal = 8 }
    }
    Write-JsonFile $catalogPath $catalog
}

# 21. AI fixture byte drift without a catalog hash refresh.
Invoke-NegativeCase -Name 'ai-hash-drift' -ExpectedCode 'FIXTURE_SHA_MISMATCH' -Mutator {
    param($root)
    $fixturePath = Join-Path $root 'tests\v5-golden\ai-v1.json'
    [System.IO.File]::AppendAllText($fixturePath, "`n", (New-Object System.Text.UTF8Encoding($false)))
}

# 22. G-049 must never be promoted to full.
Invoke-NegativeCase -Name 'ai-g049-full' -ExpectedCode 'G049_COVERAGE_MUST_BE_PARTIAL' -Mutator {
    param($root)
    $catalogPath = Join-Path $root 'tests\v5-golden\catalog-v1.json'
    $catalog = Read-JsonFile $catalogPath
    foreach ($requirement in @($catalog.requirements)) {
        if ([string]$requirement.id -eq 'G-049') {
            $requirement.coverage = 'full'
            $requirement.partialScope.fullClaim = $true
        }
    }
    $catalog.denominators.requirementCoverage.full = 18
    $catalog.denominators.requirementCoverage.partial = 20
    $catalog.denominators.requirementCoverage.none = 32
    Write-JsonFile $catalogPath $catalog
}

# 23. AI consumer path points to a missing C# reader.
Invoke-NegativeCase -Name 'ai-bad-consumer' -ExpectedCode 'CSHARP_MISSING' -Mutator {
    param($root)
    $catalogPath = Join-Path $root 'tests\v5-golden\catalog-v1.json'
    $catalog = Read-JsonFile $catalogPath
    foreach ($entry in @($catalog.fixtures)) {
        if ([string]$entry.id -eq 'ai-v1') { $entry.csharp.path = 'dotnet/tests/Mahjong.Ai.Tests/NoSuchAiGoldenTests.cs' }
    }
    Write-JsonFile $catalogPath $catalog
}

# 24. AI source-pin anchor no longer matches the fixture.
Invoke-NegativeCase -Name 'ai-bad-source-anchor' -ExpectedCode 'SOURCE_ENGINE_COMMIT_MISMATCH' -Mutator {
    param($root)
    $catalogPath = Join-Path $root 'tests\v5-golden\catalog-v1.json'
    $catalog = Read-JsonFile $catalogPath
    foreach ($entry in @($catalog.fixtures)) {
        if ([string]$entry.id -eq 'ai-v1') { $entry.sourceV4EngineCommit = '0000000000000000000000000000000000000000' }
    }
    Write-JsonFile $catalogPath $catalog
}

# 25. AI binding anchor line is outside the real consumer file.
Invoke-NegativeCase -Name 'ai-bad-binding-line' -ExpectedCode 'AI_BINDING_ANCHOR_INVALID' -Mutator {
    param($root)
    $catalogPath = Join-Path $root 'tests\v5-golden\catalog-v1.json'
    $catalog = Read-JsonFile $catalogPath
    foreach ($entry in @($catalog.fixtures)) {
        if ([string]$entry.id -eq 'ai-v1') { $entry.bindings[0].consumerLines = '99999' }
    }
    Write-JsonFile $catalogPath $catalog
}

# 26. Delta 29 must not be merged into the catalog case denominator.
Invoke-NegativeCase -Name 'ai-delta29-merge' -ExpectedCode 'CASE_ACCOUNTING_MISMATCH' -Mutator {
    param($root)
    $catalogPath = Join-Path $root 'tests\v5-golden\catalog-v1.json'
    $catalog = Read-JsonFile $catalogPath
    $catalog.caseAccounting.catalogCaseTotal = 10418
    Write-JsonFile $catalogPath $catalog
}

# 27. Refresh both fixture and catalog hash but leave the accepted immutable pin.
Invoke-NegativeCase -Name 'ai-immutable-pin' -ExpectedCode 'IMMUTABLE_FIXTURE_PIN_MISMATCH' -Mutator {
    param($root)
    $fixturePath = Join-Path $root 'tests\v5-golden\ai-v1.json'
    [System.IO.File]::AppendAllText($fixturePath, "`n", (New-Object System.Text.UTF8Encoding($false)))
    Update-CatalogFixtureSha -Root $root -FixtureId 'ai-v1'
    $catalogPath = Join-Path $root 'tests\v5-golden\catalog-v1.json'
    $catalog = Read-JsonFile $catalogPath
    $newSha = Get-Sha256 $fixturePath
    $newBytes = (Get-Item -LiteralPath $fixturePath).Length
    foreach ($pin in @($catalog.immutableFixturePins)) {
        if ([string]$pin.id -eq 'ai-v1') {
            $pin.sha256 = $newSha
            $pin.bytes = $newBytes
        }
    }
    Write-JsonFile $catalogPath $catalog
}

# 28. G-049 partial with no anchor must fail.
Invoke-NegativeCase -Name 'ai-g049-anchor-missing' -ExpectedCode 'ANCHOR_MISSING' -Mutator {
    param($root)
    $catalogPath = Join-Path $root 'tests\v5-golden\catalog-v1.json'
    $catalog = Read-JsonFile $catalogPath
    foreach ($requirement in @($catalog.requirements)) {
        if ([string]$requirement.id -eq 'G-049') { $requirement.anchors = @() }
    }
    Write-JsonFile $catalogPath $catalog
}

# 29. Accepted AI state-binding Fact count drift.
Invoke-NegativeCase -Name 'ai-accepted-binding-fact' -ExpectedCode 'AI_ACCEPTED_BINDING_FACT_MISMATCH' -Mutator {
    param($root)
    $catalogPath = Join-Path $root 'tests\v5-golden\catalog-v1.json'
    $catalog = Read-JsonFile $catalogPath
    foreach ($entry in @($catalog.fixtures)) {
        if ([string]$entry.id -eq 'ai-v1') { $entry.acceptedBindings[0].factCount = 4 }
    }
    Write-JsonFile $catalogPath $catalog
}

# 30. AI byte-freeze must not be assigned to the source-only 205506f commit.
Invoke-NegativeCase -Name 'ai-freeze-to-source-pin' -ExpectedCode 'AI_FIXTURE_FREEZE_COMMIT_MISMATCH' -Mutator {
    param($root)
    $catalogPath = Join-Path $root 'tests\v5-golden\catalog-v1.json'
    $catalog = Read-JsonFile $catalogPath
    foreach ($entry in @($catalog.fixtures)) {
        if ([string]$entry.id -eq 'ai-v1') {
            $entry.fixtureFreezeCommit = '205506f07f6a1ccfef6b32cf97390431e3349f8d'
            $entry.sourcePinRelation = 'source-pin-equals-fixture-freeze'
        }
    }
    foreach ($pin in @($catalog.immutableFixturePins)) {
        if ([string]$pin.id -eq 'ai-v1') { $pin.fixtureFreezeCommit = '205506f07f6a1ccfef6b32cf97390431e3349f8d' }
    }
    Write-JsonFile $catalogPath $catalog
}

# 31. Current manifest-only G-049 promotion to full must fail even when catalog is partial.
Invoke-NegativeCase -Name 'manifest-g049-full' -ExpectedCode 'MANIFEST_COVERAGE_MISMATCH' -Mutator {
    param($root)
    $manifest = @(Get-ChildItem -LiteralPath (Join-Path $root 'doc') -Recurse -Filter 'V5-0-GOLDEN-MANIFEST.md' -File)[0]
    $text = [System.IO.File]::ReadAllText($manifest.FullName, [System.Text.Encoding]::UTF8)
    $old = '| G-049 | `pending-v4-replay` | `partial` |'
    $new = '| G-049 | `pending-v4-replay` | `full` |'
    if (-not $text.Contains($old)) { throw 'manifest G-049 anchor missing' }
    [System.IO.File]::WriteAllText($manifest.FullName, $text.Replace($old, $new), (New-Object System.Text.UTF8Encoding($false)))
}

# 32. Current manifest coverage drift on a non-G-049 row must fail.
Invoke-NegativeCase -Name 'manifest-coverage-drift' -ExpectedCode 'MANIFEST_COVERAGE_MISMATCH' -Mutator {
    param($root)
    $manifest = @(Get-ChildItem -LiteralPath (Join-Path $root 'doc') -Recurse -Filter 'V5-0-GOLDEN-MANIFEST.md' -File)[0]
    $text = [System.IO.File]::ReadAllText($manifest.FullName, [System.Text.Encoding]::UTF8)
    $old = '| G-048 | `pending` | `partial` |'
    $new = '| G-048 | `pending` | `none` |'
    if (-not $text.Contains($old)) { throw 'manifest G-048 anchor missing' }
    [System.IO.File]::WriteAllText($manifest.FullName, $text.Replace($old, $new), (New-Object System.Text.UTF8Encoding($false)))
}

# 33. Accepted AI state-binding commit identity must be the accepted commit, not zeros.
Invoke-NegativeCase -Name 'ai-accepted-commit-zero' -ExpectedCode 'AI_ACCEPTED_BINDING_COMMIT_MISMATCH' -Mutator {
    param($root)
    $catalogPath = Join-Path $root 'tests\v5-golden\catalog-v1.json'
    $catalog = Read-JsonFile $catalogPath
    foreach ($entry in @($catalog.fixtures)) {
        if ([string]$entry.id -eq 'ai-v1') { $entry.acceptedBindings[0].commit = '0000000000000000000000000000000000000000' }
    }
    Write-JsonFile $catalogPath $catalog
}

# 34. Accepted AI binding class must be a real class declared in its source file.
Invoke-NegativeCase -Name 'ai-accepted-fake-class' -ExpectedCode 'AI_ACCEPTED_BINDING_CLASS_MISMATCH' -Mutator {
    param($root)
    $catalogPath = Join-Path $root 'tests\v5-golden\catalog-v1.json'
    $catalog = Read-JsonFile $catalogPath
    foreach ($entry in @($catalog.fixtures)) {
        if ([string]$entry.id -eq 'ai-v1') { $entry.acceptedBindings[0].class = 'Mahjong.Ai.Tests.NonexistentTests' }
    }
    Write-JsonFile $catalogPath $catalog
}

# 35. A syntactically valid line number with no read-point token must fail.
Invoke-NegativeCase -Name 'ai-anchor-line-one' -ExpectedCode 'AI_BINDING_ANCHOR_EVIDENCE_MISSING' -Mutator {
    param($root)
    $catalogPath = Join-Path $root 'tests\v5-golden\catalog-v1.json'
    $catalog = Read-JsonFile $catalogPath
    foreach ($entry in @($catalog.fixtures)) {
        if ([string]$entry.id -eq 'ai-v1') { $entry.bindings[0].consumerLines = '1' }
    }
    Write-JsonFile $catalogPath $catalog
}

# 36. A binding jsonPath that does not exist in the fixture must fail.
Invoke-NegativeCase -Name 'ai-binding-nonexistent-path' -ExpectedCode 'AI_BINDING_PATH_MISSING' -Mutator {
    param($root)
    $catalogPath = Join-Path $root 'tests\v5-golden\catalog-v1.json'
    $catalog = Read-JsonFile $catalogPath
    foreach ($entry in @($catalog.fixtures)) {
        if ([string]$entry.id -eq 'ai-v1') { $entry.bindings[0].jsonPath = '$.notARealFixtureField' }
    }
    Write-JsonFile $catalogPath $catalog
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
