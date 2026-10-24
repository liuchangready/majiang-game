#requires -Version 5.1
<#
.SYNOPSIS
Read-only validator for tests/v5-golden/catalog-v1.json.

.DESCRIPTION
Checks file existence, SHA-256, schema, declared group counts/IDs, duplicate IDs,
requirement anchors and manifest coverage. It never writes to the repository,
never executes production code and never modifies fixtures.

Exit codes: 0 = PASS, 1 = FAIL, 2 = BLOCKED (missing local tool).
#>
param(
    [string]$RepoRoot = '',
    [string]$CatalogPath = '',
    [string]$ManifestPath = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$script:Failures = New-Object System.Collections.Generic.List[string]
$script:Catalog = $null
$script:FixturesById = @{}
$script:ActualGroups = @{}

function Add-Failure {
    param([string]$Code, [string]$Message)
    $script:Failures.Add(('{0}: {1}' -f $Code, $Message)) | Out-Null
}

function Get-Prop {
    param([object]$Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-ArrayProp {
    param([object]$Object, [string]$Name)
    $value = Get-Prop -Object $Object -Name $Name
    if ($null -eq $value) { return }
    return $value
}

function Read-JsonFile {
    param([string]$Path)
    $text = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
    return ($text | ConvertFrom-Json)
}

function Get-Sha256 {
    param([string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Get-Sha256Lf {
    param([string]$Path)
    $text = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
    $text = $text.Replace("`r`n", "`n")
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($text)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '')
    }
    finally {
        $sha.Dispose()
    }
}

function Get-JsonValueByPath {
    param([object]$Root, [string]$Path)
    $current = $Root
    foreach ($segment in ($Path -split '\.')) {
        if ($null -eq $current) { return $null }
        $property = $current.PSObject.Properties[$segment]
        if ($null -eq $property) { return $null }
        $current = $property.Value
    }
    if ($current -is [System.Array]) {
        return (, $current)
    }
    return $current
}

function Get-CompositeKey {
    param([object]$Item, [object[]]$Fields)
    $parts = @()
    foreach ($field in $Fields) {
        $parts += [string](Get-Prop -Object $Item -Name ([string]$field))
    }
    return ($parts -join '/')
}

function Test-ValueType {
    param([object]$Value, [string]$Type)
    switch ($Type) {
        'object' { return ($Value -is [System.Management.Automation.PSCustomObject] -or $Value -is [System.Collections.IDictionary]) }
        'array' { return ($Value -is [System.Array]) }
        'string' { return ($Value -is [string]) }
        'number' { return ($Value -is [int] -or $Value -is [long] -or $Value -is [double] -or $Value -is [decimal]) }
        'boolean' { return ($Value -is [bool]) }
        'string-or-null' { return ($null -eq $Value -or $Value -is [string]) }
        'null' { return ($null -eq $Value) }
        default { return $false }
    }
}

function Get-GroupShape {
    param([string]$FixtureId, [string]$GroupName)
    $shapes = Get-Prop -Object $script:Catalog -Name 'groupShapes'
    if ($null -eq $shapes) { return $null }
    return (Get-Prop -Object $shapes -Name ('{0}:{1}' -f $FixtureId, $GroupName))
}

function Get-DeclaredStringArray {
    param([object]$Holder, [string]$Name, [string]$Label)
    $property = $Holder.PSObject.Properties[$Name]
    if ($null -eq $property) { return @() }
    if ($null -eq $property.Value -or -not ($property.Value -is [System.Array])) {
        Add-Failure 'GROUP_STRUCTURE_INVALID' ("{0}: '{1}' must be a non-null array" -f $Label, $Name)
        return @()
    }
    return @($property.Value)
}

function Test-ShapeRules {
    param([object]$Object, [object[]]$Rules, [string]$Label)
    foreach ($rule in $Rules) {
        $path = [string](Get-Prop -Object $rule -Name 'path')
        $type = [string](Get-Prop -Object $rule -Name 'type')
        if ([string]::IsNullOrWhiteSpace($path) -or [string]::IsNullOrWhiteSpace($type)) {
            Add-Failure 'GROUP_STRUCTURE_INVALID' ("{0}: shape rule needs path and type" -f $Label)
            continue
        }
        $value = Get-JsonValueByPath -Root $Object -Path $path
        if ($null -eq $value) {
            if ($type -ne 'string-or-null' -and $type -ne 'null') {
                Add-Failure 'CASE_STRUCTURE_MISSING' ("{0}: missing path '{1}'" -f $Label, $path)
                continue
            }
        }
        elseif ($true) {
            $typeOk = switch ($type) {
                'object' { $value -is [System.Management.Automation.PSCustomObject] -or $value -is [System.Collections.IDictionary] }
                'array' { $value -is [System.Array] }
                'string' { $value -is [string] }
                'number' { $value -is [int] -or $value -is [long] -or $value -is [double] -or $value -is [decimal] }
                'boolean' { $value -is [bool] }
                'string-or-null' { $value -is [string] }
                'null' { $false }
                default { $false }
            }
            if (-not $typeOk) {
                Add-Failure 'CASE_STRUCTURE_TYPE' ("{0}: path '{1}' expected {2}" -f $Label, $path, $type)
                continue
            }
        }
        $length = Get-Prop -Object $rule -Name 'length'
        if ($null -ne $length -and $value -is [System.Array] -and $value.Count -ne [int]$length) {
            Add-Failure 'CASE_STRUCTURE_LENGTH' ("{0}: path '{1}' expected length {2}, actual {3}" -f $Label, $path, $length, $value.Count)
        }
    }
}

function Test-ElementRules {
    param([object]$Array, [object]$Spec, [string]$Label)
    $discriminator = [string](Get-Prop -Object $Spec -Name 'discriminator')
    $variants = Get-Prop -Object $Spec -Name 'variants'
    if ([string]::IsNullOrWhiteSpace($discriminator) -or $null -eq $variants) {
        Add-Failure 'GROUP_STRUCTURE_INVALID' ("{0}: elementRules needs discriminator and variants" -f $Label)
        return
    }
    foreach ($item in @($Array)) {
        if ($null -eq $item -or -not ($item -is [System.Management.Automation.PSCustomObject])) {
            Add-Failure 'CASE_STRUCTURE_TYPE' ("{0}: array element must be an object" -f $Label)
            continue
        }
        $key = [string](Get-Prop -Object $item -Name $discriminator)
        if ([string]::IsNullOrWhiteSpace($key)) {
            Add-Failure 'CASE_STRUCTURE_MISSING' ("{0}: element missing discriminator '{1}'" -f $Label, $discriminator)
            continue
        }
        $variant = $variants.PSObject.Properties[$key]
        if ($null -eq $variant) {
            Add-Failure 'CASE_STRUCTURE_UNKNOWN_VARIANT' ("{0}: unknown '{1}'='{2}'" -f $Label, $discriminator, $key)
            continue
        }
        Test-ShapeRules -Object $item -Rules @($variant.Value) -Label ('{0}[{1}]' -f $Label, $key)
    }
}

function Resolve-Anchor {
    param([string]$Anchor, [string]$RequirementId)
    $separator = $Anchor.IndexOf(':')
    if ($separator -lt 1) {
        Add-Failure 'ANCHOR_MISSING' ("{0}: anchor has no fixture prefix: {1}" -f $RequirementId, $Anchor)
        return
    }
    $fixtureId = $Anchor.Substring(0, $separator)
    $rest = $Anchor.Substring($separator + 1)
    if (-not $script:FixturesById.ContainsKey($fixtureId)) {
        Add-Failure 'ANCHOR_FIXTURE_MISSING' ("{0}: fixture not in catalog: {1}" -f $RequirementId, $fixtureId)
        return
    }
    $parts = $rest -split '/', 2
    $groupName = $parts[0]
    $groupKey = ('{0}:{1}' -f $fixtureId, $groupName)
    if (-not $script:ActualGroups.ContainsKey($groupKey)) {
        Add-Failure 'ANCHOR_GROUP_MISSING' ("{0}: group not found: {1}" -f $RequirementId, $groupKey)
        return
    }
    if ($parts.Count -eq 1) {
        return
    }
    $caseId = $parts[1]
    $group = $script:ActualGroups[$groupKey]
    $ids = @($group.ids)
    if (-not ($ids -contains $caseId)) {
        Add-Failure 'ANCHOR_MISSING' ("{0}: case not found: {1}/{2}" -f $RequirementId, $groupKey, $caseId)
    }
}

function Test-Group {
    param([object]$Fixture, [object]$Group)
    $fixtureId = [string]$Fixture.id
    $groupName = [string]$Group.name
    $jsonPath = [string]$Group.jsonPath
    $kind = [string]$Group.kind
    $declaredCount = [int]$Group.count
    $actual = Get-JsonValueByPath -Root $Fixture.raw -Path $jsonPath
    if ($null -eq $actual) {
        Add-Failure 'GROUP_MISSING' ("{0}:{1}: jsonPath '{2}' not found" -f $fixtureId, $groupName, $jsonPath)
        return
    }

    $shape = Get-GroupShape -FixtureId $fixtureId -GroupName $groupName
    if ($null -eq $shape) {
        Add-Failure 'GROUP_SHAPE_MISSING' ("{0}:{1}: no groupShapes entry" -f $fixtureId, $groupName)
    }
    $shapeRequiredKeys = @()
    $shapeRules = @()
    $elementRules = $null
    if ($null -ne $shape) {
        $shapeRequiredKeys = @(Get-DeclaredStringArray -Holder $shape -Name 'requiredKeys' -Label ('{0}:{1}.shape' -f $fixtureId, $groupName))
        $rulesProperty = $shape.PSObject.Properties['rules']
        if ($null -ne $rulesProperty) {
            if ($null -eq $rulesProperty.Value -or -not ($rulesProperty.Value -is [System.Array])) {
                Add-Failure 'GROUP_STRUCTURE_INVALID' ("{0}:{1}.shape.rules must be a non-null array" -f $fixtureId, $groupName)
            }
            else {
                $shapeRules = @($rulesProperty.Value)
            }
        }
        $elementRules = Get-Prop -Object $shape -Name 'elementRules'
    }

    if ($kind -eq 'array') {
        $items = @($actual)
        if ($items.Count -ne $declaredCount) {
            Add-Failure 'GROUP_COUNT_MISMATCH' ("{0}:{1}: declared {2}, actual {3}" -f $fixtureId, $groupName, $declaredCount, $items.Count)
        }
        $requiredKeys = @(Get-DeclaredStringArray -Holder $Group -Name 'requiredKeys' -Label ('{0}:{1}' -f $fixtureId, $groupName))
        $requiredKeys = @(@($requiredKeys) + @($shapeRequiredKeys) | Select-Object -Unique)
        if ($requiredKeys.Count -gt 0) {
            foreach ($item in $items) {
                $itemKeys = @($item.PSObject.Properties | ForEach-Object { $_.Name })
                $missingKeys = @($requiredKeys | Where-Object { $itemKeys -notcontains $_ })
                if ($missingKeys.Count -gt 0) {
                    Add-Failure 'CASE_STRUCTURE_MISSING' ("{0}:{1}: case missing keys [{2}]" -f $fixtureId, $groupName, ($missingKeys -join ','))
                    break
                }
            }
        }
        if (($shapeRules.Count -gt 0 -or $null -ne $elementRules) -and $items.Count -gt 0) {
            foreach ($target in $items) {
                if ($shapeRules.Count -gt 0) {
                    Test-ShapeRules -Object $target -Rules $shapeRules -Label ('{0}:{1}' -f $fixtureId, $groupName)
                }
                if ($null -ne $elementRules) {
                    foreach ($elementProperty in $elementRules.PSObject.Properties) {
                        $arrayValue = Get-JsonValueByPath -Root $target -Path $elementProperty.Name
                        if ($null -eq $arrayValue) {
                            Add-Failure 'CASE_STRUCTURE_MISSING' ("{0}:{1}: missing element array '{2}'" -f $fixtureId, $groupName, $elementProperty.Name)
                            continue
                        }
                        if (-not ($arrayValue -is [System.Array])) {
                            Add-Failure 'CASE_STRUCTURE_TYPE' ("{0}:{1}: element array '{2}' expected array" -f $fixtureId, $groupName, $elementProperty.Name)
                            continue
                        }
                        Test-ElementRules -Array $arrayValue -Spec $elementProperty.Value -Label ('{0}:{1}.{2}' -f $fixtureId, $groupName, $elementProperty.Name)
                    }
                }
            }
        }
        $ids = @()
        $idField = Get-Prop -Object $Group -Name 'idField'
        $keyFields = @(Get-ArrayProp -Object $Group -Name 'keyFields')
        if (-not [string]::IsNullOrWhiteSpace([string]$idField)) {
            $ids = @($items | ForEach-Object { [string](Get-Prop -Object $_ -Name $idField) })
        }
        elseif ($keyFields.Count -gt 0) {
            $ids = @($items | ForEach-Object { Get-CompositeKey -Item $_ -Fields $keyFields })
        }
        $script:ActualGroups[('{0}:{1}' -f $fixtureId, $groupName)] = @{ ids = $ids; isCase = [bool](Get-Prop -Object $Group -Name 'countAsCase') }

        if (@($ids).Count -ne @($ids | Select-Object -Unique).Count) {
            Add-Failure 'DUPLICATE_CASE_ID' ("{0}:{1}: duplicate case IDs in actual fixture" -f $fixtureId, $groupName)
        }

        $declaredIds = @(Get-ArrayProp -Object $Group -Name 'ids')
        if ($declaredIds.Count -gt 0) {
            if (@($declaredIds).Count -ne @($ids).Count) {
                Add-Failure 'GROUP_IDS_MISMATCH' ("{0}:{1}: declared id count {2}, actual {3}" -f $fixtureId, $groupName, @($declaredIds).Count, @($ids).Count)
            }
            else {
                for ($i = 0; $i -lt @($declaredIds).Count; $i++) {
                    if ([string]$declaredIds[$i] -ne [string]$ids[$i]) {
                        Add-Failure 'GROUP_IDS_MISMATCH' ("{0}:{1}: id[{2}] declared '{3}', actual '{4}'" -f $fixtureId, $groupName, $i, $declaredIds[$i], $ids[$i])
                        break
                    }
                }
            }
        }

        $pattern = Get-Prop -Object $Group -Name 'idPattern'
        if (-not [string]::IsNullOrWhiteSpace([string]$pattern)) {
            foreach ($id in $ids) {
                if (-not [regex]::IsMatch([string]$id, [string]$pattern)) {
                    Add-Failure 'GROUP_ID_PATTERN_MISMATCH' ("{0}:{1}: id '{2}' does not match {3}" -f $fixtureId, $groupName, $id, $pattern)
                    break
                }
            }
            $firstId = Get-Prop -Object $Group -Name 'firstId'
            $lastId = Get-Prop -Object $Group -Name 'lastId'
            if ($ids.Count -gt 0 -and ([string]$firstId -ne [string]$ids[0] -or [string]$lastId -ne [string]$ids[$ids.Count - 1])) {
                Add-Failure 'GROUP_IDS_MISMATCH' ("{0}:{1}: first/last id mismatch" -f $fixtureId, $groupName)
            }
        }

        $declaredKeys = @(Get-ArrayProp -Object $Group -Name 'keys')
        if ($declaredKeys.Count -gt 0) {
            if ((@($declaredKeys) -join '|') -ne (@($ids) -join '|')) {
                Add-Failure 'GROUP_KEYS_MISMATCH' ("{0}:{1}: declared keys differ from actual" -f $fixtureId, $groupName)
            }
        }
    }
    elseif ($kind -eq 'object') {
        $keys = @($actual.PSObject.Properties | ForEach-Object { $_.Name })
        if ($keys.Count -ne $declaredCount) {
            Add-Failure 'GROUP_COUNT_MISMATCH' ("{0}:{1}: declared {2}, actual {3}" -f $fixtureId, $groupName, $declaredCount, $keys.Count)
        }
        $declaredKeys = @(Get-DeclaredStringArray -Holder $Group -Name 'keys' -Label ('{0}:{1}' -f $fixtureId, $groupName))
        $declaredKeys = @(@($declaredKeys) + @($shapeRequiredKeys) | Select-Object -Unique)
        if ($declaredKeys.Count -gt 0) {
            $missing = @($declaredKeys | Where-Object { $keys -notcontains $_ })
            $extra = @($keys | Where-Object { $declaredKeys -notcontains $_ })
            if ($missing.Count -gt 0 -or $extra.Count -gt 0) {
                Add-Failure 'GROUP_KEYS_MISMATCH' ("{0}:{1}: missing=[{2}] extra=[{3}]" -f $fixtureId, $groupName, ($missing -join ','), ($extra -join ','))
            }
        }
        if ($shapeRules.Count -gt 0) {
            Test-ShapeRules -Object $actual -Rules $shapeRules -Label ('{0}:{1}' -f $fixtureId, $groupName)
        }
        $ids = @()
        $idField = Get-Prop -Object $Group -Name 'idField'
        if (-not [string]::IsNullOrWhiteSpace([string]$idField)) {
            $ids = @([string](Get-Prop -Object $actual -Name $idField))
        }
        else {
            $ids = @($keys)
        }
        $script:ActualGroups[('{0}:{1}' -f $fixtureId, $groupName)] = @{ ids = $ids; isCase = [bool](Get-Prop -Object $Group -Name 'countAsCase') }
    }
    elseif ($kind -eq 'sample') {
        if ($declaredCount -ne 1) {
            Add-Failure 'GROUP_COUNT_MISMATCH' ("{0}:{1}: sample group must declare count=1, got {2}" -f $fixtureId, $groupName, $declaredCount)
        }
        $idField = Get-Prop -Object $Group -Name 'idField'
        $sampleId = [string](Get-Prop -Object $actual -Name $idField)
        if ([string]::IsNullOrWhiteSpace($sampleId)) {
            Add-Failure 'GROUP_IDS_MISMATCH' ("{0}:{1}: sampleId missing" -f $fixtureId, $groupName)
        }
        $requiredKeys = @(Get-DeclaredStringArray -Holder $Group -Name 'requiredKeys' -Label ('{0}:{1}' -f $fixtureId, $groupName))
        $requiredKeys = @(@($requiredKeys) + @($shapeRequiredKeys) | Select-Object -Unique)
        if ($requiredKeys.Count -gt 0) {
            $itemKeys = @($actual.PSObject.Properties | ForEach-Object { $_.Name })
            $missingKeys = @($requiredKeys | Where-Object { $itemKeys -notcontains $_ })
            if ($missingKeys.Count -gt 0) {
                Add-Failure 'CASE_STRUCTURE_MISSING' ("{0}:{1}: sample missing keys [{2}]" -f $fixtureId, $groupName, ($missingKeys -join ','))
            }
        }
        if ($shapeRules.Count -gt 0 -or $null -ne $elementRules) {
            if ($shapeRules.Count -gt 0) {
                Test-ShapeRules -Object $actual -Rules $shapeRules -Label ('{0}:{1}' -f $fixtureId, $groupName)
            }
            if ($null -ne $elementRules) {
                foreach ($elementProperty in $elementRules.PSObject.Properties) {
                    $arrayValue = Get-JsonValueByPath -Root $actual -Path $elementProperty.Name
                    if ($null -eq $arrayValue) {
                        Add-Failure 'CASE_STRUCTURE_MISSING' ("{0}:{1}: missing element array '{2}'" -f $fixtureId, $groupName, $elementProperty.Name)
                        continue
                    }
                    if (-not ($arrayValue -is [System.Array])) {
                        Add-Failure 'CASE_STRUCTURE_TYPE' ("{0}:{1}: element array '{2}' expected array" -f $fixtureId, $groupName, $elementProperty.Name)
                        continue
                    }
                    Test-ElementRules -Array $arrayValue -Spec $elementProperty.Value -Label ('{0}:{1}.{2}' -f $fixtureId, $groupName, $elementProperty.Name)
                }
            }
        }
        $script:ActualGroups[('{0}:{1}' -f $fixtureId, $groupName)] = @{ ids = @($sampleId); isCase = [bool](Get-Prop -Object $Group -Name 'countAsCase') }
    }
    else {
        Add-Failure 'GROUP_KIND_UNKNOWN' ("{0}:{1}: unknown kind '{2}'" -f $fixtureId, $groupName, $kind)
    }
}

# ---------------------------------------------------------------------------
# Tool and path checks
# ---------------------------------------------------------------------------
foreach ($tool in @('Get-FileHash', 'ConvertFrom-Json')) {
    if ($null -eq (Get-Command $tool -ErrorAction SilentlyContinue)) {
        Write-Output ('BLOCKED missing-tool {0}' -f $tool)
        exit 2
    }
}

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}
if ([string]::IsNullOrWhiteSpace($CatalogPath)) {
    $CatalogPath = Join-Path $RepoRoot 'tests\v5-golden\catalog-v1.json'
}
if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $manifestCandidates = @(Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'doc') -Recurse -Filter 'V5-0-GOLDEN-MANIFEST.md' -File -ErrorAction SilentlyContinue)
    if ($manifestCandidates.Count -gt 0) {
        $ManifestPath = $manifestCandidates[0].FullName
    }
    else {
        $ManifestPath = Join-Path $RepoRoot 'doc\V5-0-GOLDEN-MANIFEST.md'
    }
}

if (-not (Test-Path -LiteralPath $CatalogPath -PathType Leaf)) {
    Add-Failure 'CATALOG_MISSING' $CatalogPath
}
if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
    Add-Failure 'MANIFEST_MISSING' $ManifestPath
}

if ($script:Failures.Count -gt 0) {
    $script:Failures | ForEach-Object { Write-Output ('FAIL {0}' -f $_) }
    exit 1
}

try {
    $script:Catalog = Read-JsonFile $CatalogPath
}
catch {
    Add-Failure 'CATALOG_JSON_INVALID' $_.Exception.Message
    Write-Output ('FAIL {0}' -f $script:Failures[0])
    exit 1
}

# ---------------------------------------------------------------------------
# Catalog header
# ---------------------------------------------------------------------------
if ([string](Get-Prop $script:Catalog 'schema') -ne 'v5-golden-catalog-v1') {
    Add-Failure 'CATALOG_SCHEMA' ('expected v5-golden-catalog-v1, got {0}' -f (Get-Prop $script:Catalog 'schema'))
}
if ([int](Get-Prop $script:Catalog 'catalogVersion') -ne 1) {
    Add-Failure 'CATALOG_VERSION' ('expected 1, got {0}' -f (Get-Prop $script:Catalog 'catalogVersion'))
}

$baseline = Get-Prop $script:Catalog 'baseline'
if ($null -eq $baseline) {
    Add-Failure 'CATALOG_FIELD_MISSING' 'baseline'
}
else {
    $freeze = [string](Get-Prop $baseline 'freezeCommit')
    if ($freeze -notmatch '^[0-9a-fA-F]{40}$') {
        Add-Failure 'BASELINE_COMMIT_INVALID' $freeze
    }
}

$denominators = Get-Prop $script:Catalog 'denominators'
if ($null -eq $denominators) {
    Add-Failure 'CATALOG_FIELD_MISSING' 'denominators'
    $denominators = [pscustomobject]@{}
}

# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------
$allowedSchemas = @{
    'physical-v1' = 'v5-physical-golden-v1'
    'scoring-v1' = 'v5-scoring-golden-v1'
    'round-v1' = 'v5-round-golden-v1'
    'cheat-v1' = 'v5-cheat-golden-v1'
    'shuffle-v1' = 'v5-shuffle-golden-v1'
}

$fixtures = @(Get-ArrayProp $script:Catalog 'fixtures')
if ($fixtures.Count -ne 5) {
    Add-Failure 'FIXTURE_COUNT' ('expected 5, got {0}' -f $fixtures.Count)
}
$fixtureIds = @()
$fixtureCaseTotal = 0
$csharpFacts = 0
$scoringRandom = 0

foreach ($fixture in $fixtures) {
    $fixtureId = [string](Get-Prop $fixture 'id')
    $fixtureIds += $fixtureId
    $script:FixturesById[$fixtureId] = $fixture
    $relativePath = [string](Get-Prop $fixture 'path')
    if ($relativePath -notmatch '^tests/v5-golden/[A-Za-z0-9._-]+\.json$' -or $relativePath.Contains('..')) {
        Add-Failure 'FIXTURE_PATH_INVALID' ("{0}: {1}" -f $fixtureId, $relativePath)
        continue
    }
    $fullPath = Join-Path $RepoRoot ($relativePath -replace '/', '\')
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        Add-Failure 'FIXTURE_MISSING' $relativePath
        continue
    }
    $file = Get-Item -LiteralPath $fullPath
    $actualSha = Get-Sha256 $fullPath
    if ($actualSha -ne ([string](Get-Prop $fixture 'sha256')).ToUpperInvariant()) {
        Add-Failure 'FIXTURE_SHA_MISMATCH' ("{0}: catalog {1}, actual {2}" -f $fixtureId, (Get-Prop $fixture 'sha256'), $actualSha)
    }
    if ([int64]$file.Length -ne [int64](Get-Prop $fixture 'bytes')) {
        Add-Failure 'FIXTURE_BYTES_MISMATCH' ("{0}: catalog {1}, actual {2}" -f $fixtureId, (Get-Prop $fixture 'bytes'), $file.Length)
    }
    try {
        $raw = Read-JsonFile $fullPath
    }
    catch {
        Add-Failure 'FIXTURE_JSON_INVALID' ("{0}: {1}" -f $fixtureId, $_.Exception.Message)
        continue
    }
    $fixture | Add-Member -NotePropertyName raw -NotePropertyValue $raw -Force

    $actualSchema = [string](Get-Prop $raw 'schema')
    $declaredSchema = [string](Get-Prop $fixture 'schema')
    if ($allowedSchemas.ContainsKey($fixtureId)) {
        if ($actualSchema -ne $allowedSchemas[$fixtureId]) {
            Add-Failure 'FIXTURE_SCHEMA_UNKNOWN' ("{0}: actual schema '{1}' not the accepted '{2}'" -f $fixtureId, $actualSchema, $allowedSchemas[$fixtureId])
        }
    }
    else {
        Add-Failure 'FIXTURE_SCHEMA_UNKNOWN' ("{0}: fixture id is not in the accepted set" -f $fixtureId)
    }
    if ($actualSchema -ne $declaredSchema) {
        Add-Failure 'FIXTURE_SCHEMA_MISMATCH' ("{0}: declared '{1}', actual '{2}'" -f $fixtureId, $declaredSchema, $actualSchema)
    }
    $rawRuleset = Get-Prop $raw 'rulesetVersion'
    $catalogRuleset = Get-Prop $fixture 'rulesetVersion'
    if ($null -eq $rawRuleset -or $null -eq $catalogRuleset -or [int]$rawRuleset -ne [int]$catalogRuleset) {
        Add-Failure 'RULESET_VERSION_MISMATCH' ("{0}: catalog {1}, fixture {2}" -f $fixtureId, $catalogRuleset, $rawRuleset)
    }

    $rawGenerator = Get-Prop $raw 'generatorVersion'
    $catalogGenerator = Get-Prop $fixture 'generatorVersion'
    if (($null -eq $rawGenerator) -ne ($null -eq $catalogGenerator)) {
        Add-Failure 'GENERATOR_VERSION_MISMATCH' ("{0}: catalog {1}, fixture {2}" -f $fixtureId, $catalogGenerator, $rawGenerator)
    }
    elseif ($null -ne $rawGenerator -and [int]$rawGenerator -ne [int]$catalogGenerator) {
        Add-Failure 'GENERATOR_VERSION_MISMATCH' ("{0}: catalog {1}, fixture {2}" -f $fixtureId, $catalogGenerator, $rawGenerator)
    }

    $rawStatus = [string](Get-Prop $raw 'extractionStatus')
    $catalogStatus = [string](Get-Prop $fixture 'extractionStatus')
    if ($catalogStatus -ne $rawStatus) {
        Add-Failure 'EXTRACTION_STATUS_MISMATCH' ("{0}: catalog '{1}', fixture '{2}'" -f $fixtureId, $catalogStatus, $rawStatus)
    }
    if ($rawStatus -ne 'exported') {
        Add-Failure 'EXTRACTION_STATUS_NOT_EXPORTED' ("{0}: fixture status '{1}'" -f $fixtureId, $rawStatus)
    }

    $rawCommand = [string](Get-Prop $raw 'generatedByCommand')
    $catalogCommand = [string](Get-Prop $fixture 'generatedByCommand')
    if ($catalogCommand -ne $rawCommand) {
        Add-Failure 'COMMAND_MISMATCH' ("{0}: catalog command differs from fixture generatedByCommand" -f $fixtureId)
    }

    $commit = [string](Get-Prop $fixture 'sourceV4Commit')
    $rawCommit = [string](Get-Prop $raw 'v4Commit')
    if ($commit -notmatch '^[0-9a-fA-F]{40}$') {
        Add-Failure 'SOURCE_COMMIT_INVALID' ("{0}: {1}" -f $fixtureId, $commit)
    }
    if ($commit -ne $rawCommit) {
        Add-Failure 'SOURCE_COMMIT_MISMATCH' ("{0}: catalog {1}, fixture {2}" -f $fixtureId, $commit, $rawCommit)
    }

    if ($fixtureId -eq 'round-v1') {
        $rawEngineCommit = [string](Get-Prop $raw 'v4EngineSourceCommit')
        $catalogEngineCommit = [string](Get-Prop $fixture 'sourceV4EngineCommit')
        if ($catalogEngineCommit -ne $rawEngineCommit) {
            Add-Failure 'SOURCE_ENGINE_COMMIT_MISMATCH' ("{0}: catalog {1}, fixture {2}" -f $fixtureId, $catalogEngineCommit, $rawEngineCommit)
        }
        $rawDotnetCommit = [string](Get-Prop $raw 'v4DotnetRulesCommit')
        $catalogDotnetCommit = [string](Get-Prop $fixture 'sourceV4DotnetCommit')
        if ($catalogDotnetCommit -ne $rawDotnetCommit) {
            Add-Failure 'SOURCE_DOTNET_COMMIT_MISMATCH' ("{0}: catalog {1}, fixture {2}" -f $fixtureId, $catalogDotnetCommit, $rawDotnetCommit)
        }
    }

    $exporter = Get-Prop $fixture 'exporter'
    if ($null -eq $exporter) {
        Add-Failure 'CATALOG_FIELD_MISSING' ("{0}.exporter" -f $fixtureId)
    }
    else {
        $exporterPath = [string](Get-Prop $exporter 'path')
        $exporterFull = Join-Path $RepoRoot ($exporterPath -replace '/', '\')
        if (-not (Test-Path -LiteralPath $exporterFull -PathType Leaf)) {
            Add-Failure 'EXPORTER_MISSING' $exporterPath
        }
        else {
            $rawSha = [string](Get-Prop $exporter 'sha256Raw')
            if ($rawSha -and (Get-Sha256 $exporterFull) -ne $rawSha.ToUpperInvariant()) {
                Add-Failure 'EXPORTER_SHA_MISMATCH' ("{0}: raw hash" -f $fixtureId)
            }
            $lfSha = [string](Get-Prop $exporter 'sha256Lf')
            $actualLf = Get-Sha256Lf $exporterFull
            if ($lfSha -and $actualLf -ne $lfSha.ToUpperInvariant()) {
                Add-Failure 'EXPORTER_SHA_MISMATCH' ("{0}: LF-normalized hash" -f $fixtureId)
            }
            $declaredLf = [string](Get-Prop $exporter 'declaredSha256Lf')
            if ($declaredLf -and $actualLf -ne $declaredLf.ToUpperInvariant()) {
                Add-Failure 'EXPORTER_SHA_MISMATCH' ("{0}: fixture-declared LF hash" -f $fixtureId)
            }
            if ($fixtureId -eq 'physical-v1') {
                $rawExporterSha = [string](Get-Prop $raw 'exporterSourceSha256')
                if ([string]::IsNullOrWhiteSpace($rawExporterSha)) {
                    Add-Failure 'EXPORTER_SOURCE_FIELD_MISSING' ("{0}: fixture exporterSourceSha256 missing" -f $fixtureId)
                }
                else {
                    if ($rawExporterSha.ToUpperInvariant() -ne $actualLf) {
                        Add-Failure 'EXPORTER_SOURCE_SHA_MISMATCH' ("{0}: fixture exporterSourceSha256 {1}, actual LF {2}" -f $fixtureId, $rawExporterSha, $actualLf)
                    }
                    if ([string]::IsNullOrWhiteSpace($declaredLf)) {
                        Add-Failure 'EXPORTER_DECLARED_SOURCE_MISSING' ("{0}: catalog declaredSha256Lf missing" -f $fixtureId)
                    }
                    elseif ($declaredLf.ToUpperInvariant() -ne $rawExporterSha.ToUpperInvariant()) {
                        Add-Failure 'EXPORTER_DECLARED_SOURCE_MISMATCH' ("{0}: catalog {1}, fixture {2}" -f $fixtureId, $declaredLf, $rawExporterSha)
                    }
                }
            }
        }
    }

    $csharp = Get-Prop $fixture 'csharp'
    if ($null -eq $csharp) {
        Add-Failure 'CATALOG_FIELD_MISSING' ("{0}.csharp" -f $fixtureId)
    }
    else {
        $csharpPath = [string](Get-Prop $csharp 'path')
        $csharpFull = Join-Path $RepoRoot ($csharpPath -replace '/', '\')
        if (-not (Test-Path -LiteralPath $csharpFull -PathType Leaf)) {
            Add-Failure 'CSHARP_MISSING' $csharpPath
        }
        else {
            $actualFacts = @(Select-String -LiteralPath $csharpFull -Pattern '^\s*\[Fact\]').Count
            if ($actualFacts -ne [int](Get-Prop $csharp 'factCount')) {
                Add-Failure 'CSHARP_FACT_COUNT_MISMATCH' ("{0}: declared {1}, actual {2}" -f $fixtureId, (Get-Prop $csharp 'factCount'), $actualFacts)
            }
            $text = [System.IO.File]::ReadAllText($csharpFull, [System.Text.Encoding]::UTF8)
            $fileName = [System.IO.Path]::GetFileName($relativePath)
            if (-not $text.Contains($fileName)) {
                Add-Failure 'CSHARP_FIXTURE_REFERENCE_MISSING' ("{0}: {1} not referenced in {2}" -f $fixtureId, $fileName, $csharpPath)
            }
            $csharpFacts += [int](Get-Prop $csharp 'factCount')
        }
    }

    $groups = @(Get-ArrayProp $fixture 'groups')
    $caseTotal = 0
    foreach ($group in $groups) {
        Test-Group -Fixture $fixture -Group $group
        if ([bool](Get-Prop $group 'countAsCase')) {
            $caseTotal += [int](Get-Prop $group 'count')
        }
        if ($fixtureId -eq 'scoring-v1' -and [string](Get-Prop $group 'name') -eq 'randomSamples') {
            $scoringRandom = [int](Get-Prop $group 'count')
        }
    }
    if ($caseTotal -ne [int](Get-Prop $fixture 'caseTotal')) {
        Add-Failure 'CASE_TOTAL_MISMATCH' ("{0}: declared {1}, computed {2}" -f $fixtureId, (Get-Prop $fixture 'caseTotal'), $caseTotal)
    }
    $fixtureCaseTotal += [int](Get-Prop $fixture 'caseTotal')
}

if (@($fixtureIds | Select-Object -Unique).Count -ne @($fixtureIds).Count) {
    Add-Failure 'DUPLICATE_FIXTURE_ID' ($fixtureIds -join ',')
}

# Global duplicate case IDs within each fixture.
foreach ($fixtureId in $fixtureIds) {
    $allCaseIds = @()
    foreach ($key in @($script:ActualGroups.Keys)) {
        if ($key.StartsWith($fixtureId + ':') -and $script:ActualGroups[$key].isCase) {
            $allCaseIds += @($script:ActualGroups[$key].ids)
        }
    }
    $unique = @($allCaseIds | Where-Object { $_ -ne '' } | Select-Object -Unique)
    $nonEmpty = @($allCaseIds | Where-Object { $_ -ne '' })
    if ($nonEmpty.Count -ne $unique.Count) {
        Add-Failure 'DUPLICATE_CASE_ID' ("{0}: duplicate case ID across groups" -f $fixtureId)
    }
}

# ---------------------------------------------------------------------------
# Requirements and anchors
# ---------------------------------------------------------------------------
$requirements = @(Get-ArrayProp $script:Catalog 'requirements')
$requirementIds = @()
$coverageCounts = @{ full = 0; partial = 0; none = 0; 'v5-only' = 0 }
$manifestStatuses = @('pending', 'pending-v4-replay', 'v5-only')
$coverageValues = @('full', 'partial', 'none', 'v5-only')

foreach ($requirement in $requirements) {
    $id = [string](Get-Prop $requirement 'id')
    $requirementIds += $id
    if ($id -notmatch '^G-[0-9]{3}[a-z]?$') {
        Add-Failure 'REQUIREMENT_ID_INVALID' $id
    }
    $coverage = [string](Get-Prop $requirement 'coverage')
    if ($coverageValues -notcontains $coverage) {
        Add-Failure 'COVERAGE_UNKNOWN' ("{0}: {1}" -f $id, $coverage)
        continue
    }
    $coverageCounts[$coverage] = [int]$coverageCounts[$coverage] + 1
    if ($coverage -eq 'full') {
        $evidence = [string](Get-Prop $requirement 'csharpEvidence')
        if ([string]::IsNullOrWhiteSpace($evidence)) {
            Add-Failure 'FULL_EVIDENCE_MISSING' ("{0}: full requires csharpEvidence method/fields" -f $id)
        }
    }

    $manifestStatus = [string](Get-Prop $requirement 'manifestStatus')
    if ($manifestStatuses -notcontains $manifestStatus) {
        Add-Failure 'MANIFEST_STATUS_UNKNOWN' ("{0}: {1}" -f $id, $manifestStatus)
    }
    if ([string]::IsNullOrWhiteSpace([string](Get-Prop $requirement 'owner'))) {
        Add-Failure 'OWNER_MISSING' $id
    }

    $anchors = @(Get-ArrayProp $requirement 'anchors')
    if (($coverage -eq 'full' -or $coverage -eq 'partial') -and $anchors.Count -eq 0) {
        Add-Failure 'ANCHOR_MISSING' ("{0}: {1} requires at least one anchor" -f $id, $coverage)
    }
    if (($coverage -eq 'none' -or $coverage -eq 'v5-only') -and $anchors.Count -ne 0) {
        Add-Failure 'ANCHOR_UNEXPECTED' ("{0}: {1} must not declare anchors" -f $id, $coverage)
    }
    foreach ($anchor in $anchors) {
        Resolve-Anchor -Anchor ([string]$anchor) -RequirementId $id
    }
}

if (@($requirementIds | Select-Object -Unique).Count -ne @($requirementIds).Count) {
    Add-Failure 'DUPLICATE_REQUIREMENT_ID' 'catalog requirements contain duplicate IDs'
}
if ($requirements.Count -ne [int](Get-Prop $denominators 'manifestRequirements')) {
    Add-Failure 'REQUIREMENT_COUNT_MISMATCH' ("{0}: catalog {1}, denominator {2}" -f 'manifestRequirements', $requirements.Count, (Get-Prop $denominators 'manifestRequirements'))
}

# Mechanical coverage counts must match the declared denominators.
$declaredCoverage = Get-Prop $denominators 'requirementCoverage'
if ($null -eq $declaredCoverage) {
    Add-Failure 'DENOMINATOR_MISMATCH' 'requirementCoverage missing'
}
else {
    foreach ($key in $coverageValues) {
        if ([int](Get-Prop $declaredCoverage $key) -ne [int]$coverageCounts[$key]) {
            Add-Failure 'COVERAGE_COUNT_MISMATCH' ("{0}: declared {1}, computed {2}" -f $key, (Get-Prop $declaredCoverage $key), $coverageCounts[$key])
        }
    }
}

# ---------------------------------------------------------------------------
# Manifest cross-check (73 rows, historical statuses retained)
# ---------------------------------------------------------------------------
if (Test-Path -LiteralPath $ManifestPath -PathType Leaf) {
    $manifestLines = [System.IO.File]::ReadAllLines($ManifestPath, [System.Text.Encoding]::UTF8)
    $manifestRows = @{}
    foreach ($line in $manifestLines) {
        if ($line -match '^###\s+8\.5\s') {
            break
        }
        $match = [regex]::Match($line, '^\|\s*(G-[0-9A-Za-z-]+)\s*\|(.*)\|\s*$')
        if ($match.Success) {
            $id = $match.Groups[1].Value
            $cells = @($match.Groups[2].Value -split '\|' | ForEach-Object { $_.Trim() })
            $lastCell = $cells[$cells.Count - 1]
            $statusMatch = [regex]::Match($lastCell, '`([^`]+)`')
            $statusToken = if ($statusMatch.Success) { $statusMatch.Groups[1].Value } else { $lastCell }
            if (-not $manifestRows.ContainsKey($id)) {
                $manifestRows[$id] = [pscustomobject]@{ Status = $statusToken; Raw = $lastCell }
            }
        }
    }
    $manifestIds = @($manifestRows.Keys | Sort-Object)
    if ($manifestIds.Count -ne 73) {
        Add-Failure 'MANIFEST_ROW_COUNT' ('expected 73 G rows, got {0}' -f $manifestIds.Count)
    }
    if (($manifestIds -join ',') -ne (@($requirementIds | Sort-Object) -join ',')) {
        Add-Failure 'REQUIREMENT_SET_MISMATCH' 'catalog requirements and manifest G rows differ'
    }
    foreach ($requirement in $requirements) {
        $id = [string](Get-Prop $requirement 'id')
        $status = [string](Get-Prop $requirement 'manifestStatus')
        if (-not $manifestRows.ContainsKey($id)) {
            Add-Failure 'MANIFEST_ROW_MISSING' $id
        }
        elseif ([string]$manifestRows[$id].Status -ne $status) {
            Add-Failure 'MANIFEST_ROW_MISMATCH' ("{0}: catalog status '{1}' not the manifest token '{2}'" -f $id, $status, $manifestRows[$id].Status)
        }
    }
}

# ---------------------------------------------------------------------------
# Cross-denominator checks
# ---------------------------------------------------------------------------
if ($fixtures.Count -ne [int](Get-Prop $denominators 'fixtures')) {
    Add-Failure 'DENOMINATOR_MISMATCH' ('fixtures: {0} vs {1}' -f $fixtures.Count, (Get-Prop $denominators 'fixtures'))
}
if ($fixtureCaseTotal -ne [int](Get-Prop $denominators 'fixtureConcreteCases')) {
    Add-Failure 'DENOMINATOR_MISMATCH' ('fixtureConcreteCases: {0} vs {1}' -f $fixtureCaseTotal, (Get-Prop $denominators 'fixtureConcreteCases'))
}
if ($scoringRandom -ne [int](Get-Prop $denominators 'scoringRandomInputs')) {
    Add-Failure 'DENOMINATOR_MISMATCH' ('scoringRandomInputs: {0} vs {1}' -f $scoringRandom, (Get-Prop $denominators 'scoringRandomInputs'))
}
if ($csharpFacts -ne [int](Get-Prop $denominators 'csharpGoldenFacts')) {
    Add-Failure 'DENOMINATOR_MISMATCH' ('csharpGoldenFacts: {0} vs {1}' -f $csharpFacts, (Get-Prop $denominators 'csharpGoldenFacts'))
}
if ($fixtures.Count -ne [int](Get-Prop $denominators 'csharpGoldenClasses')) {
    Add-Failure 'DENOMINATOR_MISMATCH' ('csharpGoldenClasses: {0} vs {1}' -f $fixtures.Count, (Get-Prop $denominators 'csharpGoldenClasses'))
}

# ---------------------------------------------------------------------------
# Result
# ---------------------------------------------------------------------------
if ($script:Failures.Count -gt 0) {
    $script:Failures | Select-Object -First 40 | ForEach-Object { Write-Output ('FAIL {0}' -f $_) }
    if ($script:Failures.Count -gt 40) {
        Write-Output ('FAIL ... {0} more failures' -f ($script:Failures.Count - 40))
    }
    exit 1
}

$catalogSha = Get-Sha256 $CatalogPath
Write-Output ('PASS catalog={0} sha256={1} fixtures={2} requirements={3} cases={4} random={5} facts={6} coverage=full:{7}/partial:{8}/none:{9}/v5-only:{10}' -f `
    $CatalogPath, $catalogSha, $fixtures.Count, $requirements.Count, $fixtureCaseTotal, $scoringRandom, $csharpFacts, `
    $coverageCounts['full'], $coverageCounts['partial'], $coverageCounts['none'], $coverageCounts['v5-only'])
exit 0
