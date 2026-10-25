<#
.SYNOPSIS
    Independent external-copy probe for the N3 physical-v1 golden binding.

.DESCRIPTION
    Copies the real V4-exported tests/v5-golden/physical-v1.json into a brand-new
    GUID TEMP directory (plus the pinned V4 sources/exporter needed by the golden
    test), applies exactly one mutation, and runs the compiled
    Mahjong.Domain.Tests.PhysicalGoldenTests class through MAHJONG_REPO_ROOT.

    Policy (D-R1-02):
    - Positive controls require native exit 0, Total == 9, Errors == 0,
      Failed == 0, Skipped == 0, Not Run == 0.
    - Negative controls require native exit != 0, Total == 9, Errors == 0,
      Failed > 0, Skipped == 0, Not Run == 0.
    - Missing summary, Total == 0, runner/tool errors, skipped/not-run tests,
      and any exit/summary contradiction are hard failures and are never
      classified as an expected negative.
    - A mocked summary is used only to self-test the classifier; it is not
      counted as a domain fixture probe.

    Cleanup (D-R1-02):
    - The TEMP root must be an absolute path whose parent is exactly the
      process TEMP directory and whose leaf is the GUID directory created by
      this invocation.  The path is validated again before recursive removal;
      an unsafe path is never deleted.

    Environment:
    - MAHJONG_REPO_ROOT is restored in a per-probe finally block.

    Exit codes:
    - 0: self-tests pass, completed == declared, no infrastructure failure,
         no unexpected pass.
    - 1: at least one unexpected domain result (for example a false green).
    - 2: tool/parser/cleanup/self-test failure, or incomplete matrix.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = '',
    [string]$EvidenceRoot = '',
    [string]$DotnetPath = 'D:/Unity/dotnet/dotnet.exe',
    [switch]$SelfTestOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:ExpectedTotal = 9
$script:ToolFailures = 0
$script:UnexpectedResults = 0

function Write-Step {
    param([string]$Message)
    Write-Host ('[{0}] {1}' -f (Get-Date -Format 'HH:mm:ss'), $Message)
}

function Get-Sha256 {
    param([string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Get-Sample {
    param([object]$Json, [string]$SampleId)
    $sample = $Json.samples | Where-Object { $_.sampleId -eq $SampleId } | Select-Object -First 1
    if ($null -eq $sample) { throw "sample not found: $SampleId" }
    return $sample
}

function Get-G002Case {
    param([object]$Json, [string]$CaseId)
    $case = (Get-Sample -Json $Json -SampleId 'G-002').expected.cases |
        Where-Object { $_.caseId -eq $CaseId } |
        Select-Object -First 1
    if ($null -eq $case) { throw "G-002 case not found: $CaseId" }
    return $case
}

function Get-FrameworkSummary {
    param([string]$Text, [string]$LogPath)

    $summaryMatches = [regex]::Matches(
        $Text,
        'Total:\s*(\d+),\s*Errors:\s*(\d+),\s*Failed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Not Run:\s*(\d+)')
    if ($summaryMatches.Count -ne 1) {
        throw ("expected exactly one framework summary in {0}, found {1}" -f $LogPath, $summaryMatches.Count)
    }

    $match = $summaryMatches[0]
    return [pscustomobject]@{
        Total   = [int]$match.Groups[1].Value
        Errors  = [int]$match.Groups[2].Value
        Failed  = [int]$match.Groups[3].Value
        Skipped = [int]$match.Groups[4].Value
        NotRun  = [int]$match.Groups[5].Value
    }
}

function Get-ProbeClassification {
    param(
        [int]$NativeExit,
        [int]$Total,
        [int]$Errors,
        [int]$Failed,
        [int]$Skipped,
        [int]$NotRun,
        [bool]$ExpectPass
    )

    if ($Total -ne $script:ExpectedTotal) { return 'INFRA_FAIL' }
    if ($Skipped -ne 0) { return 'INFRA_FAIL' }
    if ($NotRun -ne 0) { return 'INFRA_FAIL' }

    if ($ExpectPass) {
        if ($NativeExit -eq 0 -and $Errors -eq 0 -and $Failed -eq 0) { return 'PASS' }
        return 'INFRA_FAIL'
    }

    if ($NativeExit -eq 0 -and $Errors -eq 0 -and $Failed -eq 0) { return 'UNEXPECTED_PASS' }
    if ($NativeExit -ne 0 -and $Errors -eq 0 -and $Failed -gt 0) { return 'EXPECTED_FAIL' }
    return 'INFRA_FAIL'
}

function Assert-SafeProbeRoot {
    param([string]$Root, [string]$ExpectedLeaf)

    if ([string]::IsNullOrWhiteSpace($Root)) { throw 'cleanup refused: empty root' }
    if ([string]::IsNullOrWhiteSpace($ExpectedLeaf)) { throw 'cleanup refused: empty expected leaf' }

    $tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $full = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $separator = [IO.Path]::DirectorySeparatorChar

    if (-not $full.StartsWith($tempBase + $separator, [StringComparison]::OrdinalIgnoreCase)) {
        throw "cleanup refused: root is outside TEMP: $full"
    }

    $parent = [IO.Path]::GetDirectoryName($full)
    if ($null -eq $parent) { throw "cleanup refused: no parent for $full" }
    $parent = $parent.TrimEnd($separator)
    if (-not $parent.Equals($tempBase, [StringComparison]::OrdinalIgnoreCase)) {
        throw "cleanup refused: parent is not TEMP: $parent"
    }

    $leaf = [IO.Path]::GetFileName($full)
    if ($leaf -cne $ExpectedLeaf) {
        throw "cleanup refused: leaf $leaf is not the created leaf $ExpectedLeaf"
    }
    if ($leaf -notmatch '^n3-physical-probe-[0-9a-f]{32}$') {
        throw "cleanup refused: leaf has unexpected shape: $leaf"
    }

    return $full
}

function New-ProbeRoot {
    param([string]$AgentsPath, [string[]]$SourceFiles, [string]$Repo)

    $tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $leaf = 'n3-physical-probe-' + [guid]::NewGuid().ToString('N')
    $root = [IO.Path]::GetFullPath((Join-Path $tempBase $leaf))

    New-Item -ItemType Directory -Path $root | Out-Null
    Copy-Item -LiteralPath $AgentsPath -Destination $root
    New-Item -ItemType Directory -Path (Join-Path $root 'dotnet') | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $root 'tests/v5-golden') -Force | Out-Null
    foreach ($relative in $SourceFiles) {
        $destination = Join-Path $root ($relative -replace '/', '\')
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $Repo ($relative -replace '/', '\')) -Destination $destination
    }

    return [pscustomobject]@{ Root = $root; Leaf = $leaf }
}

function Invoke-Probe {
    param(
        [string]$Name,
        [bool]$ExpectPass,
        [scriptblock]$Mutation,
        [string]$Repo,
        [string]$ProbeDir,
        [string]$FixtureSource,
        [string]$AgentsPath,
        [string[]]$SourceFiles,
        [string]$Dotnet,
        [string]$Runner
    )

    $root = $null
    $leaf = $null
    $originalEnv = [Environment]::GetEnvironmentVariable('MAHJONG_REPO_ROOT', 'Process')
    $envRestored = $false
    try {
        $created = New-ProbeRoot -AgentsPath $AgentsPath -SourceFiles $SourceFiles -Repo $Repo
        $root = $created.Root
        $leaf = $created.Leaf

        $json = Get-Content -Raw -Encoding UTF8 -LiteralPath $FixtureSource | ConvertFrom-Json
        if ($null -ne $Mutation) { & $Mutation $json }
        $destination = Join-Path $root 'tests/v5-golden/physical-v1.json'
        [IO.File]::WriteAllText($destination, ($json | ConvertTo-Json -Depth 100), [Text.UTF8Encoding]::new($false))
        $fixtureSha = Get-Sha256 $destination
        Copy-Item -LiteralPath $destination -Destination (Join-Path $ProbeDir ($Name + '.json'))

        $env:MAHJONG_REPO_ROOT = $root
        $log = Join-Path $ProbeDir ($Name + '.log')
        $output = & $Dotnet $Runner -class 'Mahjong.Domain.Tests.PhysicalGoldenTests' -noColor -noLogo 2>&1
        $nativeExit = $LASTEXITCODE
        $output | Out-File -LiteralPath $log -Encoding utf8

        $summary = Get-FrameworkSummary -Text ($output | Out-String) -LogPath $log
        $status = Get-ProbeClassification -NativeExit $nativeExit -Total $summary.Total `
            -Errors $summary.Errors -Failed $summary.Failed -Skipped $summary.Skipped `
            -NotRun $summary.NotRun -ExpectPass $ExpectPass
        $ok = ($status -eq 'PASS' -and $ExpectPass) -or ($status -eq 'EXPECTED_FAIL' -and -not $ExpectPass)

        return [pscustomobject]@{
            name          = $Name
            expect        = $(if ($ExpectPass) { 'PASS' } else { 'FAIL_EXPECTED' })
            status        = $status
            ok            = $ok
            nativeExit    = $nativeExit
            total         = $summary.Total
            errors        = $summary.Errors
            failed        = $summary.Failed
            skipped       = $summary.Skipped
            notRun        = $summary.NotRun
            fixtureSha256 = $fixtureSha
            log           = $log
        }
    }
    finally {
        [Environment]::SetEnvironmentVariable('MAHJONG_REPO_ROOT', $originalEnv, 'Process')
        $envRestored = ([Environment]::GetEnvironmentVariable('MAHJONG_REPO_ROOT', 'Process') -eq $originalEnv)

        if ($null -ne $root) {
            $safeRoot = Assert-SafeProbeRoot -Root $root -ExpectedLeaf $leaf
            if (Test-Path -LiteralPath $safeRoot) {
                Remove-Item -LiteralPath $safeRoot -Recurse -Force
            }
        }

        if (-not $envRestored) { throw "MAHJONG_REPO_ROOT was not restored for $Name" }
    }
}

function Invoke-SelfTests {
    param([string]$EvidenceRoot)

    $selfDir = Join-Path $EvidenceRoot 'self-tests'
    New-Item -ItemType Directory -Path $selfDir -Force | Out-Null

    # Classifier matrix: a mock is only a policy self-test, never a domain result.
    $classifierCases = @(
        @{ name = 'positive-ok'; exit = 0; total = 9; errors = 0; failed = 0; skipped = 0; notRun = 0; expectPass = $true; want = 'PASS' },
        @{ name = 'negative-ok'; exit = 1; total = 9; errors = 0; failed = 1; skipped = 0; notRun = 0; expectPass = $false; want = 'EXPECTED_FAIL' },
        @{ name = 'negative-unexpected-pass'; exit = 0; total = 9; errors = 0; failed = 0; skipped = 0; notRun = 0; expectPass = $false; want = 'UNEXPECTED_PASS' },
        @{ name = 'negative-native0-failed1'; exit = 0; total = 9; errors = 0; failed = 1; skipped = 0; notRun = 0; expectPass = $false; want = 'INFRA_FAIL' },
        @{ name = 'negative-tool-error'; exit = 2; total = 9; errors = 1; failed = 0; skipped = 0; notRun = 0; expectPass = $false; want = 'INFRA_FAIL' },
        @{ name = 'negative-errors-and-failed'; exit = 1; total = 9; errors = 1; failed = 1; skipped = 0; notRun = 0; expectPass = $false; want = 'INFRA_FAIL' },
        @{ name = 'negative-no-failed'; exit = 1; total = 9; errors = 0; failed = 0; skipped = 0; notRun = 0; expectPass = $false; want = 'INFRA_FAIL' },
        @{ name = 'positive-exit-nonzero'; exit = 1; total = 9; errors = 0; failed = 0; skipped = 0; notRun = 0; expectPass = $true; want = 'INFRA_FAIL' },
        @{ name = 'positive-failed'; exit = 0; total = 9; errors = 0; failed = 1; skipped = 0; notRun = 0; expectPass = $true; want = 'INFRA_FAIL' },
        @{ name = 'zero-total'; exit = 0; total = 0; errors = 0; failed = 0; skipped = 0; notRun = 0; expectPass = $true; want = 'INFRA_FAIL' },
        @{ name = 'skipped'; exit = 0; total = 9; errors = 0; failed = 0; skipped = 1; notRun = 0; expectPass = $true; want = 'INFRA_FAIL' },
        @{ name = 'not-run'; exit = 0; total = 9; errors = 0; failed = 0; skipped = 0; notRun = 1; expectPass = $true; want = 'INFRA_FAIL' }
    )

    $classifierRows = New-Object System.Collections.ArrayList
    foreach ($case in $classifierCases) {
        $actual = Get-ProbeClassification -NativeExit $case['exit'] -Total $case['total'] `
            -Errors $case['errors'] -Failed $case['failed'] -Skipped $case['skipped'] `
            -NotRun $case['notRun'] -ExpectPass $case['expectPass']
        $ok = ($actual -eq $case['want'])
        $null = $classifierRows.Add([pscustomobject]@{
            name       = $case['name']
            expected   = $case['want']
            actual     = $actual
            ok         = $ok
            nativeExit = $case['exit']
            total      = $case['total']
            errors     = $case['errors']
            failed     = $case['failed']
            skipped    = $case['skipped']
            notRun     = $case['notRun']
        })
        if (-not $ok) { throw "classifier self-test failed: $($case['name']) expected $($case['want']) actual $actual" }
    }
    $classifierRows | Export-Csv -LiteralPath (Join-Path $selfDir 'classifier.csv') -NoTypeInformation -Encoding UTF8

    # Parser self-test: missing summary and zero-total must be rejected.
    $missingRejected = $false
    try {
        Get-FrameworkSummary -Text 'no summary here' -LogPath 'synthetic-missing' | Out-Null
    }
    catch {
        $missingRejected = $true
    }

    $zeroSummary = Get-FrameworkSummary -Text 'Total: 0, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0' -LogPath 'synthetic-zero'
    $zeroStatus = Get-ProbeClassification -NativeExit 0 -Total $zeroSummary.Total -Errors $zeroSummary.Errors `
        -Failed $zeroSummary.Failed -Skipped $zeroSummary.Skipped -NotRun $zeroSummary.NotRun -ExpectPass $true

    $contradictionSummary = Get-FrameworkSummary -Text 'Total: 9, Errors: 0, Failed: 1, Skipped: 0, Not Run: 0' -LogPath 'synthetic-contradiction'
    $contradictionStatus = Get-ProbeClassification -NativeExit 0 -Total $contradictionSummary.Total `
        -Errors $contradictionSummary.Errors -Failed $contradictionSummary.Failed -Skipped $contradictionSummary.Skipped `
        -NotRun $contradictionSummary.NotRun -ExpectPass $true

    $zeroExitSummary = Get-FrameworkSummary -Text 'Total: 9, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0' -LogPath 'synthetic-zero-exit'
    $zeroExitStatus = Get-ProbeClassification -NativeExit 1 -Total $zeroExitSummary.Total `
        -Errors $zeroExitSummary.Errors -Failed $zeroExitSummary.Failed -Skipped $zeroExitSummary.Skipped `
        -NotRun $zeroExitSummary.NotRun -ExpectPass $false

    $parserRows = @(
        [pscustomobject]@{ name = 'missing-summary'; rejected = $missingRejected; expected = $true },
        [pscustomobject]@{ name = 'zero-total'; rejected = ($zeroStatus -eq 'INFRA_FAIL'); expected = $true },
        [pscustomobject]@{ name = 'exit0-with-failed'; rejected = ($contradictionStatus -eq 'INFRA_FAIL'); expected = $true },
        [pscustomobject]@{ name = 'negative-exit1-zero-failed'; rejected = ($zeroExitStatus -eq 'INFRA_FAIL'); expected = $true }
    )
    foreach ($row in $parserRows) {
        if ($row.rejected -ne $row.expected) { throw "parser self-test failed: $($row.name)" }
    }
    $parserRows | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $selfDir 'parser-self-test.json') -Encoding UTF8

    # Cleanup path safety self-test: invalid/traversal paths must be rejected without deletion.
    $tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $validLeaf = 'n3-physical-probe-' + [guid]::NewGuid().ToString('N')
    $validPath = Join-Path $tempBase $validLeaf
    $validAccepted = $false
    try {
        $null = Assert-SafeProbeRoot -Root $validPath -ExpectedLeaf $validLeaf
        $validAccepted = $true
    }
    catch {
        $validAccepted = $false
    }

    $cleanupCases = @(
        @{ name = 'valid-created-shape'; root = $validPath; leaf = $validLeaf; shouldAccept = $true },
        @{ name = 'wrong-leaf-shape'; root = (Join-Path $tempBase 'n3-physical-probe-1234'); leaf = 'n3-physical-probe-1234'; shouldAccept = $false },
        @{ name = 'wrong-parent'; root = (Join-Path (Join-Path $tempBase 'sub') $validLeaf); leaf = $validLeaf; shouldAccept = $false },
        @{ name = 'wrong-expected-leaf'; root = $validPath; leaf = ('n3-physical-probe-' + [guid]::NewGuid().ToString('N')); shouldAccept = $false },
        @{ name = 'traversal'; root = (Join-Path $tempBase ('..\n3-physical-probe-' + [guid]::NewGuid().ToString('N'))); leaf = ''; shouldAccept = $false }
    )

    $cleanupRows = New-Object System.Collections.ArrayList
    foreach ($case in $cleanupCases) {
        $accepted = $false
        try {
            $null = Assert-SafeProbeRoot -Root $case['root'] -ExpectedLeaf $case['leaf']
            $accepted = $true
        }
        catch {
            $accepted = $false
        }
        $ok = ($accepted -eq $case['shouldAccept'])
        $null = $cleanupRows.Add([pscustomobject]@{
            name         = $case['name']
            expected     = $case['shouldAccept']
            accepted     = $accepted
            ok           = $ok
        })
        if (-not $ok) { throw "cleanup self-test failed: $($case['name'])" }
    }
    $cleanupRows | Export-Csv -LiteralPath (Join-Path $selfDir 'cleanup.csv') -NoTypeInformation -Encoding UTF8

    [pscustomobject]@{
        classifierCases = $classifierCases.Count
        parserCases     = $parserRows.Count
        cleanupCases    = $cleanupCases.Count
        validPathAccepted = $validAccepted
        allPassed       = $true
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $selfDir 'self-test-summary.json') -Encoding UTF8
}

# ---------------------------------------------------------------------------
# Paths and evidence root
# ---------------------------------------------------------------------------

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
}
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path

$FixtureRelative = 'tests/v5-golden/physical-v1.json'
$FixturePath = Join-Path $RepoRoot ($FixtureRelative -replace '/', '\')
$AgentsPath = Join-Path $RepoRoot 'AGENTS.md'
$Runner = Join-Path $RepoRoot 'dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll'

$sourceFiles = @(
    'engine/src/main/kotlin/com/majiang/model/Tile.kt',
    'engine/src/main/kotlin/com/majiang/model/TileLocation.kt',
    'engine/src/main/kotlin/com/majiang/model/TilePool.kt',
    'engine/src/main/kotlin/com/majiang/state/StateInvariants.kt',
    'engine/src/main/kotlin/com/majiang/stacking/ShuffleWallGenerator.kt',
    'engine/src/main/kotlin/com/majiang/stacking/ShuffleSlots.kt',
    'engine/src/main/kotlin/com/majiang/stacking/ShuffleConfig.kt',
    'engine/src/testFixtures/kotlin/com/majiang/testing/TestTableBuilder.kt',
    'engine/src/test/kotlin/com/majiang/model/V5PhysicalGoldenExporterTest.kt'
) | Select-Object -Unique

foreach ($required in @($FixturePath, $AgentsPath, $Runner, $DotnetPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "required tool/input missing: $required"
    }
}
foreach ($relative in $sourceFiles) {
    $source = Join-Path $RepoRoot ($relative -replace '/', '\')
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "pinned V4 source missing: $source"
    }
}

if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
    $EvidenceRoot = Join-Path ([IO.Path]::GetTempPath()) ('physical-binding-evidence-' + [guid]::NewGuid().ToString('N'))
}
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
if (Test-Path -LiteralPath $EvidenceRoot) {
    throw "evidence root already exists; every run needs a new directory: $EvidenceRoot"
}
New-Item -ItemType Directory -Path $EvidenceRoot | Out-Null
$ProbeDir = Join-Path $EvidenceRoot 'probes'
New-Item -ItemType Directory -Path $ProbeDir -Force | Out-Null

$fixtureShaBefore = Get-Sha256 $FixturePath
$head = ((& git -C $RepoRoot rev-parse HEAD 2>&1 | Out-String).Trim())
[pscustomobject]@{
    evidenceRoot      = $EvidenceRoot
    repoRoot          = $RepoRoot
    head              = $head
    fixture           = $FixtureRelative
    fixtureSha256     = $fixtureShaBefore
    dotnet            = $DotnetPath
    dotnetVersion     = ((& $DotnetPath --version 2>&1 | Out-String).Trim())
    startedAtUtc      = (Get-Date).ToUniversalTime().ToString('o')
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $EvidenceRoot 'meta.json') -Encoding UTF8

Write-Step ('evidence root: ' + $EvidenceRoot)
Write-Step ('fixture SHA256: ' + $fixtureShaBefore)

Invoke-SelfTests -EvidenceRoot $EvidenceRoot
Write-Step 'classifier/parser/cleanup self-tests passed'

if ($SelfTestOnly) {
    Write-Host 'PROBE-SELFTEST-PASS: policy self-tests only; no domain fixture probe was run.'
    exit 0
}

# ---------------------------------------------------------------------------
# Case matrix
# ---------------------------------------------------------------------------

$probes = @(
    # Existing positive and negative matrix (kept intact).
    @{ name = 'baseline'; expectPass = $true; mutation = $null },
    @{ name = 'control-g001a-rank'; expectPass = $false; mutation = { param($j) (Get-Sample $j 'G-001a').expected.tiles[0].rank = 99 } },
    @{ name = 'g001a-distinct-count'; expectPass = $false; mutation = { param($j) (Get-Sample $j 'G-001a').expected.distinctIdentities = 135 } },
    @{ name = 'g001b-boundary-terminal'; expectPass = $false; mutation = { param($j) (Get-Sample $j 'G-001b').expected.boundarySamples[3].isTerminal = $false } },
    @{ name = 'g001b-boundary-suit-unknown'; expectPass = $false; mutation = { param($j) (Get-Sample $j 'G-001b').expected.boundarySamples[0].suit = 'BAMBOO' } },
    @{ name = 'g001b-suit-order'; expectPass = $false; mutation = { param($j) (Get-Sample $j 'G-001b').expected.suitOrder = (Get-Sample $j 'G-001b').expected.suitOrder.Replace('SOU 9..17', 'SOU 8..17') } },
    @{ name = 'g001b-group-suit'; expectPass = $false; mutation = { param($j) (Get-Sample $j 'G-001b').expected.groups[0].suit = 'SOU' } },
    @{ name = 'g001c-boundary-rank'; expectPass = $false; mutation = { param($j) (Get-Sample $j 'G-001c').expected.boundaries[0].rank = 99 } },
    @{ name = 'g001c-boundary-honor'; expectPass = $false; mutation = { param($j) (Get-Sample $j 'G-001c').expected.boundaries[1].isHonor = $false } },
    @{ name = 'g001c-boundary-copy'; expectPass = $false; mutation = { param($j) (Get-Sample $j 'G-001c').expected.boundaries[1].copyIndex = 2 } },
    @{ name = 'g001c-boundary-missing'; expectPass = $false; mutation = { param($j) (Get-Sample $j 'G-001c').expected.boundaries[0].PSObject.Properties.Remove('rank') } },
    @{ name = 'g001c-boundary-wrongtype'; expectPass = $false; mutation = { param($j) (Get-Sample $j 'G-001c').expected.boundaries[0].rank = '1' } },
    @{ name = 'g001c-valid-suit'; expectPass = $false; mutation = { param($j) (Get-Sample $j 'G-001c').expected.validRoundTrips[0].suit = 'DRAGON' } },
    @{ name = 'g001c-exception-map'; expectPass = $false; mutation = { param($j) (Get-Sample $j 'G-001c').expected.csharpExceptionMapping.csharpExceptionType = 'Nonexistent.Exception' } },
    @{ name = 'g001c-exception-map-extra'; expectPass = $false; mutation = { param($j) (Get-Sample $j 'G-001c').expected.csharpExceptionMapping | Add-Member -NotePropertyName extra -NotePropertyValue 'x' } },
    @{ name = 'g001c-invalid-input'; expectPass = $false; mutation = { param($j) (Get-Sample $j 'G-001c').expected.invalid[0].input = 0 } },
    @{ name = 'g001c-invalid-message'; expectPass = $false; mutation = { param($j) (Get-Sample $j 'G-001c').expected.invalid[1].message = 'tampered' } },
    @{ name = 'g001c-invalid-category'; expectPass = $false; mutation = { param($j) (Get-Sample $j 'G-001c').expected.invalid[0].semanticCategory = 'unknown-category' } },
    @{ name = 'g002-message'; expectPass = $false; mutation = { param($j) (Get-Sample $j 'G-002').expected.cases[1].message = 'tampered' } },
    @{ name = 'g002-mapping'; expectPass = $false; mutation = { param($j) (Get-Sample $j 'G-002').expected.cases[1].csharpMapping = 'invalid-seat' } },
    @{ name = 'g003a-digest'; expectPass = $false; mutation = { param($j) (Get-Sample $j 'G-003a').expected.cases[0].wallCanonicalSha256 = '0000000000000000000000000000000000000000000000000000000000000000' } },
    @{ name = 'g003a-position-zone'; expectPass = $false; mutation = { param($j) (Get-Sample $j 'G-003a').expected.cases[0].positions[0].zone = 'DEAD_WALL' } },
    @{ name = 'g003a-aggregate'; expectPass = $false; mutation = { param($j) (Get-Sample $j 'G-003a').expected.allConserved = $false } },
    @{ name = 'g003b-target-rank'; expectPass = $false; mutation = { param($j) (Get-Sample $j 'G-003b').expected.probeA.target.rank = 99 } },
    @{ name = 'g003b-actual-sha'; expectPass = $false; mutation = { param($j) (Get-Sample $j 'G-003b').expected.probeB.before.actualTilesSha256 = '0000000000000000000000000000000000000000000000000000000000000000' } },
    @{ name = 'hash-v4-reason'; expectPass = $false; mutation = { param($j) $j.hashCrosswalk.v4Reason = 'tampered' } },
    @{ name = 'hash-csharp-verification'; expectPass = $false; mutation = { param($j) $j.hashCrosswalk.csharpHashVerification = 'tampered' } },
    @{ name = 'hash-extra-key'; expectPass = $false; mutation = { param($j) $j.hashCrosswalk | Add-Member -NotePropertyName extra -NotePropertyValue 'x' } },

    # D-R1-01: every accepted G-002 case must use the exact factory branch.
    # loc-wall-default is seatless; loc-hand-0 is seat-required.
    @{ name = 'g002-accepted-wall-mapping-unknown'; expectPass = $false; mutation = { param($j) (Get-G002Case $j 'loc-wall-default').csharpMapping = 'unknown-mapping' } },
    @{ name = 'g002-accepted-wall-mapping-wrong'; expectPass = $false; mutation = { param($j) (Get-G002Case $j 'loc-wall-default').csharpMapping = 'seat-factory' } },
    @{ name = 'g002-accepted-wall-mapping-missing'; expectPass = $false; mutation = { param($j) (Get-G002Case $j 'loc-wall-default').PSObject.Properties.Remove('csharpMapping') } },
    @{ name = 'g002-accepted-wall-mapping-null'; expectPass = $false; mutation = { param($j) (Get-G002Case $j 'loc-wall-default').csharpMapping = $null } },
    @{ name = 'g002-accepted-wall-mapping-wrongtype'; expectPass = $false; mutation = { param($j) (Get-G002Case $j 'loc-wall-default').csharpMapping = 7 } },
    @{ name = 'g002-accepted-hand-mapping-unknown'; expectPass = $false; mutation = { param($j) (Get-G002Case $j 'loc-hand-0').csharpMapping = 'unknown-mapping' } },
    @{ name = 'g002-accepted-hand-mapping-wrong'; expectPass = $false; mutation = { param($j) (Get-G002Case $j 'loc-hand-0').csharpMapping = 'seatless-factory' } },
    @{ name = 'g002-accepted-hand-mapping-missing'; expectPass = $false; mutation = { param($j) (Get-G002Case $j 'loc-hand-0').PSObject.Properties.Remove('csharpMapping') } },
    @{ name = 'g002-accepted-hand-mapping-null'; expectPass = $false; mutation = { param($j) (Get-G002Case $j 'loc-hand-0').csharpMapping = $null } },
    @{ name = 'g002-accepted-hand-mapping-wrongtype'; expectPass = $false; mutation = { param($j) (Get-G002Case $j 'loc-hand-0').csharpMapping = 7 } }
)

$results = New-Object System.Collections.ArrayList
$declared = $probes.Count

foreach ($probe in $probes) {
    try {
        $result = Invoke-Probe -Name $probe['name'] -ExpectPass $probe['expectPass'] `
            -Mutation $probe['mutation'] -Repo $RepoRoot -ProbeDir $ProbeDir `
            -FixtureSource $FixturePath -AgentsPath $AgentsPath -SourceFiles $sourceFiles `
            -Dotnet $DotnetPath -Runner $Runner
        $null = $results.Add($result)
        if (-not $result.ok) {
            if ($result.status -eq 'UNEXPECTED_PASS') { $script:UnexpectedResults++ } else { $script:ToolFailures++ }
            Write-Host ('UNEXPECTED: {0} expected {1} status {2} exit={3} total={4} failed={5} errors={6} skipped={7} notRun={8}; {9}' -f `
                $result.name, $result.expect, $result.status, $result.nativeExit, $result.total, $result.failed, `
                $result.errors, $result.skipped, $result.notRun, $result.log)
        }
    }
    catch {
        $script:ToolFailures++
        $null = $results.Add([pscustomobject]@{
            name          = $probe['name']
            expect        = $(if ($probe['expectPass']) { 'PASS' } else { 'FAIL_EXPECTED' })
            status        = 'TOOL_FAILURE'
            ok            = $false
            nativeExit    = -1
            total         = -1
            errors        = -1
            failed        = -1
            skipped       = -1
            notRun        = -1
            fixtureSha256 = ''
            log           = ''
        })
        Write-Host ('TOOL-FAILURE: {0}: {1}' -f $probe['name'], $_.Exception.Message)
    }
}

$results | Export-Csv -LiteralPath (Join-Path $EvidenceRoot 'probe-results.csv') -NoTypeInformation -Encoding UTF8
$results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $EvidenceRoot 'probe-results.json') -Encoding UTF8

$fixtureShaAfter = Get-Sha256 $FixturePath
$fixturePreserved = ($fixtureShaAfter -eq $fixtureShaBefore)
$completed = $results.Count
$passed = ($completed -eq $declared -and $script:ToolFailures -eq 0 -and $script:UnexpectedResults -eq 0 -and $fixturePreserved)

[pscustomobject]@{
    evidenceRoot        = $EvidenceRoot
    head                = $head
    declared            = $declared
    completed           = $completed
    passed              = (@($results | Where-Object { $_.status -eq 'PASS' }).Count)
    expectedFails       = (@($results | Where-Object { $_.status -eq 'EXPECTED_FAIL' }).Count)
    unexpectedPasses    = $script:UnexpectedResults
    toolFailures        = $script:ToolFailures
    fixtureSha256Before = $fixtureShaBefore
    fixtureSha256After  = $fixtureShaAfter
    fixturePreserved    = $fixturePreserved
    finishedAtUtc       = (Get-Date).ToUniversalTime().ToString('o')
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $EvidenceRoot 'summary.json') -Encoding UTF8

Write-Host ''
Write-Host '=== PROBE SUMMARY ==='
$results | Format-Table name, expect, status, nativeExit, total, failed, errors, skipped, notRun -AutoSize | Out-String -Width 220 | Write-Host
Write-Host ("declared={0} completed={1} pass={2} expectedFail={3} unexpectedPass={4} toolFailures={5} fixturePreserved={6}" -f `
    $declared, $completed, (@($results | Where-Object { $_.status -eq 'PASS' }).Count), `
    (@($results | Where-Object { $_.status -eq 'EXPECTED_FAIL' }).Count), `
    $script:UnexpectedResults, $script:ToolFailures, $fixturePreserved)
Write-Host ('evidence: ' + $EvidenceRoot)

if ($completed -ne $declared) {
    Write-Host 'PROBE-FAIL: completed != declared.'
    exit 2
}
if ($script:ToolFailures -gt 0) {
    Write-Host 'PROBE-FAIL: infrastructure/tool failure; not an expected domain negative.'
    exit 2
}
if ($script:UnexpectedResults -gt 0) {
    Write-Host 'PROBE-FAIL: unexpected domain result(s); false green is not accepted.'
    exit 1
}
if (-not $fixturePreserved) {
    Write-Host 'PROBE-FAIL: repository fixture changed during the probe.'
    exit 2
}

Write-Host 'PROBE-PASS: every positive control stayed green and every negative produced a real failed test.'
exit 0
