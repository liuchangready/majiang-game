#requires -Version 5.1
<#
.SYNOPSIS
Read-only validator for the four accepted V5 companion golden fixtures:
  - tests/v5-golden/movement-v1.json
  - tests/v5-golden/shuffle-reversed-v1.json
  - tests/v5-golden/visibility-v1.json
  - tests/v5-golden/ai-discard-flags-v1.json

.DESCRIPTION
Validates file existence, exact byte count, BOM-less UTF-8 encoding, exact raw SHA-256
digests against accepted Git LF and Windows CRLF profiles, JSON syntax, distinct
fixture identity/schema, embedded V4/V5 source/provenance commit pins, case counts,
case identifier uniqueness and completeness, and physical conservation invariants
(including 136 unique physical tiles 0..135 on shuffle walls).
Optionally verifies git commit blob pins against historical freeze commits when requested.

Guarantees:
  - Completely read-only: never modifies, overwrites, or creates files in the repository.
  - Strict path boundary: rejects lexical path traversal and directory junctions / reparse points.
  - No fake pass: full CLI validation ALWAYS enforces byte integrity and raw hashes.
    Internal structure test mode (-InternalStructureTestMode) is restricted to testing
    semantic/structure parsing branches without emitting a golden pass certification.

Exit codes:
  0 = PASS (all 4 companion fixtures verified)
  1 = FAIL (validation failure: missing, hash mismatch, syntax error, schema error, etc.)
  2 = BLOCKED (missing required tools: Get-FileHash, ConvertFrom-Json, or git when requested)
#>
param(
    [string]$RepoRoot = '',
    [string]$FixtureDir = '',
    [switch]$VerifyGitPins,
    [switch]$InternalStructureTestMode
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

foreach ($tool in @('Get-FileHash', 'ConvertFrom-Json')) {
    if ($null -eq (Get-Command $tool -ErrorAction SilentlyContinue)) {
        Write-Output ("BLOCKED: missing required tool '{0}'" -f $tool)
        exit 2
    }
}

if ($VerifyGitPins) {
    if ($null -eq (Get-Command 'git' -ErrorAction SilentlyContinue)) {
        Write-Output "BLOCKED: missing required tool 'git' for -VerifyGitPins"
        exit 2
    }
}

function Get-Prop {
    param([object]$Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-ArrayLength {
    param([object]$Val)
    if ($null -eq $Val) { return 0 }
    if ($Val -is [System.Array]) { return $Val.Length }
    return @($Val).Length
}

$resolvedRepo = if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
} else {
    if (-not (Test-Path -LiteralPath $RepoRoot)) {
        Write-Output ("FAIL: RepoRoot does not exist: '{0}'" -f $RepoRoot)
        exit 1
    }
    (Resolve-Path $RepoRoot).Path
}

$canonicalRepoRoot = [System.IO.Path]::GetFullPath($resolvedRepo)
if (-not $canonicalRepoRoot.EndsWith([System.IO.Path]::DirectorySeparatorChar.ToString())) {
    $canonicalRepoRoot += [System.IO.Path]::DirectorySeparatorChar
}

$resolvedFixtureDir = if ([string]::IsNullOrWhiteSpace($FixtureDir)) {
    Join-Path $canonicalRepoRoot 'tests\v5-golden'
} else {
    [System.IO.Path]::GetFullPath($FixtureDir)
}

function Test-HasReparsePoint {
    param([string]$PathToCheck)
    if ([string]::IsNullOrWhiteSpace($PathToCheck)) { return $false }
    $curr = [System.IO.Path]::GetFullPath($PathToCheck)
    while (-not [string]::IsNullOrWhiteSpace($curr)) {
        if (Test-Path -LiteralPath $curr) {
            $attr = [System.IO.File]::GetAttributes($curr)
            if (($attr.value__ -band [System.IO.FileAttributes]::ReparsePoint.value__) -ne 0) {
                return $true
            }
        }
        $parent = [System.IO.Path]::GetDirectoryName($curr)
        if ($parent -eq $curr -or [string]::IsNullOrWhiteSpace($parent)) { break }
        $curr = $parent
    }
    return $false
}

function Test-IsInsideRepo {
    param([string]$PathToCheck)
    if (Test-HasReparsePoint $PathToCheck) {
        return $false
    }
    $canonical = [System.IO.Path]::GetFullPath($PathToCheck)
    return $canonical.StartsWith($canonicalRepoRoot, [System.StringComparison]::OrdinalIgnoreCase)
}

if (-not (Test-IsInsideRepo $resolvedFixtureDir)) {
    Write-Output ("FAIL: PATH_ESCAPE_DETECTED FixtureDir '{0}' escapes RepoRoot '{1}' (reparse points or out-of-boundary paths forbidden)" -f $resolvedFixtureDir, $canonicalRepoRoot)
    exit 1
}

$script:Failures = New-Object System.Collections.Generic.List[string]

function Add-Failure {
    param([string]$Code, [string]$Message)
    $script:Failures.Add(('{0}: {1}' -f $Code, $Message)) | Out-Null
}

function Get-Sha256Raw {
    param([string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Test-ShaFormat {
    param([string]$Hash)
    if ([string]::IsNullOrWhiteSpace($Hash)) { return $false }
    if ($Hash.Length -ne 40) { return $false }
    if ($Hash -match '^0{40}$') { return $false }
    return ($Hash -match '^[0-9a-fA-F]{40}$')
}

function Test-HasBom {
    param([byte[]]$Bytes)
    if ($null -eq $Bytes -or $Bytes.Length -lt 2) { return $false }
    if ($Bytes.Length -ge 3 -and $Bytes[0] -eq 0xEF -and $Bytes[1] -eq 0xBB -and $Bytes[2] -eq 0xBF) {
        return $true
    }
    if ($Bytes[0] -eq 0xFF -and $Bytes[1] -eq 0xFE) { return $true }
    if ($Bytes[0] -eq 0xFE -and $Bytes[1] -eq 0xFF) { return $true }
    return $false
}

function Get-StrictUtf8String {
    param([byte[]]$Bytes)
    $utf8Strict = New-Object System.Text.UTF8Encoding($false, $true)
    return $utf8Strict.GetString($Bytes)
}

function Test-WallTilesConservation {
    param([object]$Wall, [string]$WallName, [string]$CaseId)
    $len = Get-ArrayLength $Wall
    if ($len -ne 136) {
        return @{ Code = 'WALL_SIZE_INVALID'; Message = ("shuffle-reversed-v1: case '{0}' {1} must contain exactly 136 tiles (got {2})" -f $CaseId, $WallName, $len) }
    }
    $seen = New-Object 'System.Collections.Generic.HashSet[int]'
    foreach ($item in @($Wall)) {
        if ($null -eq $item) {
            return @{ Code = 'WALL_CONSERVATION_INVALID'; Message = ("shuffle-reversed-v1: case '{0}' {1} contains null tile" -f $CaseId, $WallName) }
        }
        $val = 0
        $isInt = $false
        if ($item -is [int] -or $item -is [long] -or $item -is [int16] -or $item -is [byte]) {
            $val = [int]$item
            $isInt = $true
        } elseif ([int]::TryParse([string]$item, [ref]$val)) {
            if ([string]$item -match '^-?\d+$') {
                $isInt = $true
            }
        }
        if (-not $isInt) {
            return @{ Code = 'WALL_CONSERVATION_INVALID'; Message = ("shuffle-reversed-v1: case '{0}' {1} contains non-integer tile value '{2}'" -f $CaseId, $WallName, $item) }
        }
        if ($val -lt 0 -or $val -gt 135) {
            return @{ Code = 'WALL_CONSERVATION_INVALID'; Message = ("shuffle-reversed-v1: case '{0}' {1} contains tile out of range [0..135]: {2}" -f $CaseId, $WallName, $val) }
        }
        if (-not $seen.Add($val)) {
            return @{ Code = 'WALL_CONSERVATION_INVALID'; Message = ("shuffle-reversed-v1: case '{0}' {1} contains duplicate tile {2}, violating 136-tile physical conservation" -f $CaseId, $WallName, $val) }
        }
    }
    if ($seen.Count -ne 136) {
        return @{ Code = 'WALL_CONSERVATION_INVALID'; Message = ("shuffle-reversed-v1: case '{0}' {1} does not contain all 136 unique tiles in 0..135 (count={2})" -f $CaseId, $WallName, $seen.Count) }
    }
    return $null
}

$script:AcceptedCompanionPins = @{
    'movement-v1' = [pscustomobject]@{
        Id = 'movement-v1'
        FileName = 'movement-v1.json'
        RelativePath = 'tests/v5-golden/movement-v1.json'
        IdentityKind = 'schema'
        ExpectedIdentityValue = 'v5-movement-golden-v1'
        Sha256RawLf = 'C2D5469E6C532476E5DA48C4478E7C080A401B7F2F41F095B7A8B880AB6C7C13'
        Sha256RawCrlf = '5927F44A650DB963813C352B9786147A5953772862CA33722FDD9CE3E17FA3C5'
        BytesLf = 1278595
        BytesCrlf = 1331161
        FreezeCommit = 'fdd0287b415db3438b23694bb35e4b72d76d50aa'
        GitBlob = '25331005f30edfb2870cba8ee4a1b582400f346b'
        Generator = 'engine/src/test/kotlin/com/majiang/model/V5TileMovementGoldenExporterTest.kt'
        SourceCommitProp = 'v4Commit'
        ExpectedSourceCommit = 'd6bc69fa930d005b355a1273ec732fbdf82bbe2f'
        CaseIdProperty = 'caseId'
        ExpectedCaseCount = 8
        ExpectedCaseIds = @(
            'MV-G004-DRAW-DISCARD',
            'MV-G005-PON',
            'MV-G005-CHI',
            'MV-G006-CLOSED-KAN-RINSHAN',
            'MV-G006-ADDED-KAN-RINSHAN',
            'MV-G006-OPEN-KAN-RINSHAN',
            'MV-G007-DUPLICATE-PON-REJECTION',
            'MV-G007-FIFTH-RINSHAN-REJECTION'
        )
    }
    'shuffle-reversed-v1' = [pscustomobject]@{
        Id = 'shuffle-reversed-v1'
        FileName = 'shuffle-reversed-v1.json'
        RelativePath = 'tests/v5-golden/shuffle-reversed-v1.json'
        IdentityKind = 'schema'
        ExpectedIdentityValue = 'v5-shuffle-reversed-golden-v1'
        Sha256RawLf = '5A5EA43F774E8341D740E6B5840AAFD4B671AEDFF9830D59DA161D758C14B619'
        Sha256RawCrlf = '81D21F0A0C0C6FDA6EFFB029364D5BC8F4EE30C84F1431246FDD9DB5309F5902'
        BytesLf = 75847
        BytesCrlf = 77359
        FreezeCommit = '8734494936aad0d451511156a454b989d508468b'
        GitBlob = '16ae21dafdf487707bf8f4064a879de22779654f'
        Generator = 'engine/src/test/kotlin/com/majiang/v3/V5ShuffleReversedGoldenExporterTest.kt'
        SourceCommitProp = 'v4BaseCommit'
        ExpectedSourceCommit = '03084f08af6e2ddefa2ebc6e094de5dac2474fd4'
        CaseIdProperty = 'seed'
        ExpectedCaseCount = 32
        ExpectedCaseIds = @(0..31 | ForEach-Object { [string]$_ })
    }
    'visibility-v1' = [pscustomobject]@{
        Id = 'visibility-v1'
        FileName = 'visibility-v1.json'
        RelativePath = 'tests/v5-golden/visibility-v1.json'
        IdentityKind = 'schema'
        ExpectedIdentityValue = 'v5-visibility-golden-v1'
        Sha256RawLf = '30291EC270EFB6F2A7D8C6984D53E7AE2DBD5AE45A41A7114EE69288749F346E'
        Sha256RawCrlf = '6506C0729A36E4625E157A5E6EA2CCB79B9C2FF2E4F3F57988BBB9535CC52B94'
        BytesLf = 141007
        BytesCrlf = 147185
        FreezeCommit = '4086bf3db898d65b9e2119df3247a9beb89ba121'
        GitBlob = 'eddcc00035978be64bca27efc27ec352ce686bf5'
        Generator = 'engine/src/test/kotlin/com/majiang/projection/V5VisibilityGoldenExporterTest.kt'
        SourceCommitProp = 'v4SourceCommit'
        ExpectedSourceCommit = 'e8a9e9adc401becef6d05d4cf5de972e998df976'
        ExpectedRenderShaLf = '642cfdda1c11587c63ae9f8587c24c4959f7ed3e8bea5bd51d70a36a7b4cb4b8'
        CaseIdProperty = 'id'
        ExpectedCaseCount = 4
        ExpectedCaseIds = @(
            'VIS-G057A-SNAPSHOT-COPY',
            'VIS-G057B-EVENT-VISIBILITY',
            'VIS-G057C-PUBLIC-PROJECTION',
            'VIS-G057D-SEAT-VIEW-ISOLATION'
        )
    }
    'ai-discard-flags-v1' = [pscustomobject]@{
        Id = 'ai-discard-flags-v1'
        FileName = 'ai-discard-flags-v1.json'
        RelativePath = 'tests/v5-golden/ai-discard-flags-v1.json'
        IdentityKind = 'fixture'
        ExpectedIdentityValue = 'ai-discard-flags-v1'
        Sha256RawLf = '448BAB14DEF9809DBDCC4BE1148CD53370FEE1A44541535794AC7DFBE5B5CBE8'
        Sha256RawCrlf = 'DB1A872F324A8FB994020FB90700E5F64E4E90ECAA63E1CB3FC3A660CAE10AEE'
        BytesLf = 14480
        BytesCrlf = 14984
        FreezeCommit = 'daa834a45fb651b786fc4f13a3d1edfc4d8d4a39'
        GitBlob = 'ef329f5562b47e6e1f9304f247d7a3bb36cf7125'
        Generator = 'engine/src/test/kotlin/com/majiang/state/V5AiDiscardFlagsGoldenExporterTest.kt'
        SourceCommitProp = 'provenance.generatedAtHead'
        ExpectedSourceCommit = '85134e9ce4fd67556f6d2f355249c14f4d3b42d4'
        ExpectedV4EngineLastCommit = '3f5370c30983a4047dade0835bb2c49dacc5ebbf'
        ExpectedRenderSha = 'caf13b8250b7421e89f0e879631d4f9ed6264badda0ea39fbad0d0dbb305e4b8'
        CaseIdProperty = 'caseId'
        ExpectedCaseCount = 3
        ExpectedCaseIds = @(
            'riichi-river',
            'claimed-discard',
            'riichi-discard-claimed'
        )
    }
}

$seenFixtureIdentities = @{}

foreach ($fixtureKey in @('movement-v1', 'shuffle-reversed-v1', 'visibility-v1', 'ai-discard-flags-v1')) {
    $pin = $script:AcceptedCompanionPins[$fixtureKey]
    $filePath = Join-Path $resolvedFixtureDir $pin.FileName

    if (-not (Test-IsInsideRepo $filePath)) {
        Add-Failure 'PATH_ESCAPE_DETECTED' ("{0}: File path '{1}' escapes RepoRoot '{2}' (reparse points or out-of-boundary paths forbidden)" -f $pin.Id, $filePath, $canonicalRepoRoot)
        continue
    }

    if (-not (Test-Path -LiteralPath $filePath)) {
        Add-Failure 'FIXTURE_MISSING' ("{0}: File not found at '{1}'" -f $pin.Id, $filePath)
        continue
    }

    $rawBytes = $null
    try {
        $rawBytes = [System.IO.File]::ReadAllBytes($filePath)
    } catch {
        Add-Failure 'FIXTURE_READ_ERROR' ("{0}: Failed to read raw bytes from '{1}': {2}" -f $pin.Id, $filePath, $_.Exception.Message)
        continue
    }

    if ($null -eq $rawBytes -or $rawBytes.Length -eq 0) {
        Add-Failure 'FIXTURE_EMPTY' ("{0}: File size is 0 bytes" -f $pin.Id)
        continue
    }

    if (Test-HasBom $rawBytes) {
        Add-Failure 'INVALID_ENCODING' ("{0}: File contains Byte Order Mark (BOM). Golden fixtures must be pure BOM-less UTF-8." -f $pin.Id)
    }

    if (-not $InternalStructureTestMode) {
        $actualRawSha = Get-Sha256Raw $filePath
        $matchedProfile = $null
        if ($actualRawSha -ceq $pin.Sha256RawLf -and $rawBytes.Length -eq $pin.BytesLf) {
            $matchedProfile = 'LF'
        } elseif ($actualRawSha -ceq $pin.Sha256RawCrlf -and $rawBytes.Length -eq $pin.BytesCrlf) {
            $matchedProfile = 'CRLF'
        }

        if ($null -eq $matchedProfile) {
            Add-Failure 'HASH_MISMATCH' ("{0}: raw bytes SHA-256 or length does not match accepted LF or CRLF profiles (actual: SHA={1}, bytes={2}; expected LF=(SHA {3}, {4}B) or CRLF=(SHA {5}, {6}B))" -f $pin.Id, $actualRawSha, $rawBytes.Length, $pin.Sha256RawLf, $pin.BytesLf, $pin.Sha256RawCrlf, $pin.BytesCrlf)
        }
    }

    $rawText = $null
    try {
        $rawText = Get-StrictUtf8String $rawBytes
    } catch {
        Add-Failure 'INVALID_ENCODING' ("{0}: File content is not valid UTF-8: {1}" -f $pin.Id, $_.Exception.Message)
        continue
    }

    $json = $null
    try {
        $json = $rawText | ConvertFrom-Json
    }
    catch {
        Add-Failure 'INVALID_JSON' ("{0}: JSON syntax error: {1}" -f $pin.Id, $_.Exception.Message)
        continue
    }

    if ($null -eq $json) {
        Add-Failure 'INVALID_JSON' ("{0}: Deserialized object is null" -f $pin.Id)
        continue
    }

    $actualIdentity = if ($pin.IdentityKind -eq 'fixture') { [string](Get-Prop $json 'fixture') } else { [string](Get-Prop $json 'schema') }
    if (-not [string]::IsNullOrWhiteSpace($actualIdentity)) {
        if ($seenFixtureIdentities.ContainsKey($actualIdentity)) {
            Add-Failure 'DUPLICATE_FIXTURE_IDENTITY' ("{0}: duplicate fixture identity '{1}' (already declared on '{2}')" -f $pin.Id, $actualIdentity, $seenFixtureIdentities[$actualIdentity])
        }
        else {
            $seenFixtureIdentities[$actualIdentity] = $pin.Id
        }
    }

    if ($pin.IdentityKind -eq 'fixture') {
        if ([string]::IsNullOrWhiteSpace($actualIdentity) -or $actualIdentity -cne $pin.ExpectedIdentityValue) {
            Add-Failure 'FIXTURE_IDENTITY_MISMATCH' ("{0}: 'fixture' expected '{1}', got '{2}'" -f $pin.Id, $pin.ExpectedIdentityValue, $actualIdentity)
        }

        $ver = Get-Prop $json 'version'
        if ($null -eq $ver -or $ver -ne 1) {
            Add-Failure 'VERSION_MISMATCH' ("{0}: 'version' expected 1, got '{1}'" -f $pin.Id, $ver)
        }
    }
    else {
        if ([string]::IsNullOrWhiteSpace($actualIdentity) -or $actualIdentity -cne $pin.ExpectedIdentityValue) {
            Add-Failure 'SCHEMA_MISMATCH' ("{0}: 'schema' expected '{1}', got '{2}'" -f $pin.Id, $pin.ExpectedIdentityValue, $actualIdentity)
        }

        $genVer = Get-Prop $json 'generatorVersion'
        if ($null -eq $genVer -or $genVer -ne 1) {
            Add-Failure 'VERSION_MISMATCH' ("{0}: 'generatorVersion' expected 1, got '{1}'" -f $pin.Id, $genVer)
        }
        $ruleVer = Get-Prop $json 'rulesetVersion'
        if ($null -eq $ruleVer -or $ruleVer -ne 1) {
            Add-Failure 'VERSION_MISMATCH' ("{0}: 'rulesetVersion' expected 1, got '{1}'" -f $pin.Id, $ruleVer)
        }
    }

    if ($pin.Id -eq 'movement-v1') {
        $gen = [string](Get-Prop $json 'generator')
        if ($gen -cne $pin.Generator) {
            Add-Failure 'GENERATOR_MISMATCH' ("{0}: generator expected '{1}', got '{2}'" -f $pin.Id, $pin.Generator, $gen)
        }
        $v4Commit = [string](Get-Prop $json 'v4Commit')
        if (-not (Test-ShaFormat $v4Commit) -or $v4Commit -cne $pin.ExpectedSourceCommit) {
            Add-Failure 'SOURCE_COMMIT_INVALID' ("{0}: v4Commit invalid or mismatched: '{1}'" -f $pin.Id, $v4Commit)
        }
    }
    elseif ($pin.Id -eq 'shuffle-reversed-v1') {
        $gen = [string](Get-Prop $json 'generator')
        if ($gen -cne $pin.Generator) {
            Add-Failure 'GENERATOR_MISMATCH' ("{0}: generator expected '{1}', got '{2}'" -f $pin.Id, $pin.Generator, $gen)
        }
        $v4Base = [string](Get-Prop $json 'v4BaseCommit')
        if (-not (Test-ShaFormat $v4Base) -or $v4Base -cne $pin.ExpectedSourceCommit) {
            Add-Failure 'SOURCE_COMMIT_INVALID' ("{0}: v4BaseCommit invalid or mismatched: '{1}'" -f $pin.Id, $v4Base)
        }
    }
    elseif ($pin.Id -eq 'visibility-v1') {
        $gen = [string](Get-Prop $json 'generator')
        if ($gen -cne $pin.Generator) {
            Add-Failure 'GENERATOR_MISMATCH' ("{0}: generator expected '{1}', got '{2}'" -f $pin.Id, $pin.Generator, $gen)
        }
        $v4Source = [string](Get-Prop $json 'v4SourceCommit')
        if (-not (Test-ShaFormat $v4Source) -or $v4Source -cne $pin.ExpectedSourceCommit) {
            Add-Failure 'SOURCE_COMMIT_INVALID' ("{0}: v4SourceCommit invalid or mismatched: '{1}'" -f $pin.Id, $v4Source)
        }
        $renderSha = [string](Get-Prop $json 'renderSha256LfNormalized')
        if ([string]::IsNullOrWhiteSpace($renderSha) -or $renderSha -cne $pin.ExpectedRenderShaLf) {
            Add-Failure 'RENDER_SHA_MISMATCH' ("{0}: renderSha256LfNormalized expected '{1}', got '{2}'" -f $pin.Id, $pin.ExpectedRenderShaLf, $renderSha)
        }
    }
    elseif ($pin.Id -eq 'ai-discard-flags-v1') {
        $prov = Get-Prop $json 'provenance'
        if ($null -eq $prov) {
            Add-Failure 'STRUCTURE_INVALID' ("{0}: missing 'provenance' object" -f $pin.Id)
        }
        else {
            $gen = [string](Get-Prop $prov 'generator')
            if ($gen -cne $pin.Generator) {
                Add-Failure 'GENERATOR_MISMATCH' ("{0}: provenance.generator expected '{1}', got '{2}'" -f $pin.Id, $pin.Generator, $gen)
            }
            $genHead = [string](Get-Prop $prov 'generatedAtHead')
            if (-not (Test-ShaFormat $genHead) -or $genHead -cne $pin.ExpectedSourceCommit) {
                Add-Failure 'PROVENANCE_SHA_INVALID' ("{0}: provenance.generatedAtHead invalid or mismatched: '{1}'" -f $pin.Id, $genHead)
            }
            $v4Engine = [string](Get-Prop $prov 'v4EngineLastCommit')
            if (-not (Test-ShaFormat $v4Engine) -or $v4Engine -cne $pin.ExpectedV4EngineLastCommit) {
                Add-Failure 'PROVENANCE_SHA_INVALID' ("{0}: provenance.v4EngineLastCommit invalid or mismatched: '{1}'" -f $pin.Id, $v4Engine)
            }
            $renderSha = [string](Get-Prop $prov 'renderSha256')
            if ([string]::IsNullOrWhiteSpace($renderSha) -or $renderSha -cne $pin.ExpectedRenderSha) {
                Add-Failure 'RENDER_SHA_MISMATCH' ("{0}: provenance.renderSha256 expected '{1}', got '{2}'" -f $pin.Id, $pin.ExpectedRenderSha, $renderSha)
            }
        }
    }

    $casesVal = Get-Prop $json 'cases'
    if ($null -eq $casesVal -or -not ($casesVal -is [System.Array])) {
        Add-Failure 'STRUCTURE_INVALID' ("{0}: missing or non-array 'cases' property" -f $pin.Id)
        continue
    }

    $casesLength = Get-ArrayLength $casesVal
    if ($casesLength -ne $pin.ExpectedCaseCount) {
        Add-Failure 'CASE_COUNT_MISMATCH' ("{0}: expected {1} cases, got {2}" -f $pin.Id, $pin.ExpectedCaseCount, $casesLength)
    }

    $rootCaseCount = Get-Prop $json 'caseCount'
    if ($null -ne $rootCaseCount) {
        if ($rootCaseCount -ne $casesLength) {
            Add-Failure 'CASE_COUNT_MISMATCH' ("{0}: root caseCount ({1}) does not match cases array length ({2})" -f $pin.Id, $rootCaseCount, $casesLength)
        }
    }

    $seenCaseIds = @{}
    foreach ($c in @($casesVal)) {
        if ($null -eq $c) {
            Add-Failure 'CASE_STRUCTURE_INVALID' ("{0}: case element is null" -f $pin.Id)
            continue
        }

        $rawCid = Get-Prop $c $pin.CaseIdProperty
        $cid = if ($null -ne $rawCid) { [string]$rawCid } else { $null }

        if ([string]::IsNullOrWhiteSpace($cid)) {
            Add-Failure 'CASE_STRUCTURE_INVALID' ("{0}: case is missing non-empty '{1}' identifier" -f $pin.Id, $pin.CaseIdProperty)
            continue
        }

        if ($seenCaseIds.ContainsKey($cid)) {
            Add-Failure 'DUPLICATE_CASE_ID' ("{0}: duplicate case identifier '{1}'" -f $pin.Id, $cid)
        }
        $seenCaseIds[$cid] = $true
    }

    foreach ($expectedCid in $pin.ExpectedCaseIds) {
        if (-not $seenCaseIds.ContainsKey($expectedCid)) {
            Add-Failure 'CASE_ID_MISMATCH' ("{0}: missing expected case identifier '{1}'" -f $pin.Id, $expectedCid)
        }
    }

    if ($pin.Id -eq 'movement-v1') {
        foreach ($c in @($casesVal)) {
            $cid = [string](Get-Prop $c 'caseId')
            $initState = Get-Prop $c 'initialState'
            $steps = Get-Prop $c 'steps'
            $stepsLen = Get-ArrayLength $steps
            if ($null -eq $initState -or $stepsLen -eq 0) {
                Add-Failure 'CASE_STRUCTURE_INVALID' ("movement-v1: case '{0}' missing required initialState or non-empty steps array" -f $cid)
                break
            }
        }
    }
    elseif ($pin.Id -eq 'shuffle-reversed-v1') {
        foreach ($c in @($casesVal)) {
            $cid = [string](Get-Prop $c 'seed')
            $w3 = Get-Prop $c 'threeCasterWall'
            $wr = Get-Prop $c 'reversedWall'

            $resW3 = Test-WallTilesConservation -Wall $w3 -WallName 'threeCasterWall' -CaseId $cid
            if ($null -ne $resW3) {
                Add-Failure $resW3.Code $resW3.Message
                break
            }

            $resWr = Test-WallTilesConservation -Wall $wr -WallName 'reversedWall' -CaseId $cid
            if ($null -ne $resWr) {
                Add-Failure $resWr.Code $resWr.Message
                break
            }
        }
    }
    elseif ($pin.Id -eq 'visibility-v1') {
        foreach ($c in @($casesVal)) {
            $cid = [string](Get-Prop $c 'id')
            $inp = Get-Prop $c 'input'
            $exp = Get-Prop $c 'expected'
            $srcOnly = Get-Prop $c 'sourceOnly'
            if ($null -eq $inp -or $null -eq $exp -or $null -eq $srcOnly) {
                Add-Failure 'CASE_STRUCTURE_INVALID' ("visibility-v1: case '{0}' missing required input, expected, or sourceOnly section" -f $cid)
                break
            }
        }
    }
    elseif ($pin.Id -eq 'ai-discard-flags-v1') {
        $fs = Get-Prop $json 'flagSemantics'
        if ($null -eq $fs) {
            Add-Failure 'STRUCTURE_INVALID' "ai-discard-flags-v1: missing 'flagSemantics' section"
        }
        else {
            $r = Get-Prop $fs 'isRiichi'
            $cl = Get-Prop $fs 'isClaimed'
            if ($null -eq $r -or $null -eq $cl) {
                Add-Failure 'STRUCTURE_INVALID' "ai-discard-flags-v1: flagSemantics missing 'isRiichi' or 'isClaimed'"
            }
        }
    }

    if ($VerifyGitPins) {
        $gitRef = ("{0}:{1}" -f $pin.FreezeCommit, $pin.RelativePath)
        try {
            $gitOut = & git -C $canonicalRepoRoot rev-parse $gitRef 2>&1
            if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($gitOut)) {
                Add-Failure 'GIT_PIN_MISMATCH' ("{0}: git rev-parse failed for '{1}': {2}" -f $pin.Id, $gitRef, ($gitOut -join ' '))
            }
            else {
                $actualBlob = ($gitOut | Select-Object -First 1).Trim()
                if ($actualBlob -cne $pin.GitBlob) {
                    Add-Failure 'GIT_PIN_MISMATCH' ("{0}: git blob mismatch. Expected '{1}', got '{2}' at commit {3}" -f $pin.Id, $pin.GitBlob, $actualBlob, $pin.FreezeCommit)
                }
            }
        }
        catch {
            Add-Failure 'GIT_PIN_MISMATCH' ("{0}: git execution exception for '{1}': {2}" -f $pin.Id, $gitRef, $_.Exception.Message)
        }
    }
}

$outputLines = @()
if ($InternalStructureTestMode) {
    $outputLines += ("# V5 Companion Golden Fixtures - Internal Structure Test Mode - {0}" -f (Get-Date -Format 'o'))
    $outputLines += ("RepoRoot: {0}" -f $canonicalRepoRoot)
    $outputLines += ("FixtureDir: {0}" -f $resolvedFixtureDir)
    if ($script:Failures.Count -gt 0) {
        $outputLines += ""
        $outputLines += ("[STRUCTURE_TEST_MODE] FAIL: {0} failure(s) detected:" -f $script:Failures.Count)
        foreach ($err in $script:Failures) {
            $outputLines += ("  - {0}" -f $err)
        }
        Write-Output ($outputLines -join "`n")
        exit 1
    }
    $outputLines += ""
    $outputLines += "[STRUCTURE_TEST_MODE] PASS: 4/4 companion golden fixtures structural invariants verified."
    Write-Output ($outputLines -join "`n")
    exit 0
}

$outputLines += ("# V5 Companion Golden Fixtures Verification - {0}" -f (Get-Date -Format 'o'))
$outputLines += ("RepoRoot: {0}" -f $canonicalRepoRoot)
$outputLines += ("FixtureDir: {0}" -f $resolvedFixtureDir)
$outputLines += ("VerifyGitPins: {0}" -f [bool]$VerifyGitPins)

if ($script:Failures.Count -gt 0) {
    $outputLines += ""
    $outputLines += ("FAIL: {0} failure(s) detected:" -f $script:Failures.Count)
    foreach ($err in $script:Failures) {
        $outputLines += ("  - {0}" -f $err)
    }
    Write-Output ($outputLines -join "`n")
    exit 1
}

$outputLines += ""
$outputLines += "PASS: 4/4 companion golden fixtures verified successfully:"
$outputLines += "  - movement-v1.json (schema: v5-movement-golden-v1, 8 cases, SHA-256 LF: C2D5469E6C532476E5DA48C4478E7C080A401B7F2F41F095B7A8B880AB6C7C13)"
$outputLines += "  - shuffle-reversed-v1.json (schema: v5-shuffle-reversed-golden-v1, 32 cases, SHA-256 LF: 5A5EA43F774E8341D740E6B5840AAFD4B671AEDFF9830D59DA161D758C14B619)"
$outputLines += "  - visibility-v1.json (schema: v5-visibility-golden-v1, 4 cases, SHA-256 LF: 30291EC270EFB6F2A7D8C6984D53E7AE2DBD5AE45A41A7114EE69288749F346E)"
$outputLines += "  - ai-discard-flags-v1.json (fixture: ai-discard-flags-v1, 3 cases, SHA-256 LF: 448BAB14DEF9809DBDCC4BE1148CD53370FEE1A44541535794AC7DFBE5B5CBE8)"
if ($VerifyGitPins) {
    $outputLines += "All 4 historical freeze commit git blobs verified equal to pinned hashes."
}

Write-Output ($outputLines -join "`n")
exit 0
