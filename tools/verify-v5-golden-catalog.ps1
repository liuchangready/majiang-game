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

# Accepted fixture bytes are an immutable audit boundary. A future fixture hash
# refresh cannot pass by updating only catalog-v1.json: the verifier must change
# deliberately, which is the review gate for any new fixture version.
$script:AcceptedFixturePins = @{
    'physical-v1' = [pscustomobject]@{ Sha256 = 'B9384534E826473007511FD34096085C56EFD0E0F49E877978E50F847EDA0995'; Bytes = 836369; SourceV4Commit = '32f645ec9b134e9ad261a2df89437416b20b3119'; FixtureFreezeCommit = '205506f07f6a1ccfef6b32cf97390431e3349f8d' }
    'scoring-v1'  = [pscustomobject]@{ Sha256 = '53B1811079033E9044E222F7BB1FA263506FD724F09D6B13F84FE9CC2FCCD94E'; Bytes = 8315131; SourceV4Commit = '97831c88f664a0ff6589b7fa062464d2ca057e29'; FixtureFreezeCommit = '205506f07f6a1ccfef6b32cf97390431e3349f8d' }
    'round-v1'    = [pscustomobject]@{ Sha256 = 'DF5FC5F8199C039823BAC1C668F911637EEB80E7F8A785C84D011401CF2452B7'; Bytes = 43474; SourceV4Commit = '21a7d2c495397b943fc1a170dc23ebc0dc77d4e9'; FixtureFreezeCommit = '205506f07f6a1ccfef6b32cf97390431e3349f8d' }
    'cheat-v1'    = [pscustomobject]@{ Sha256 = 'A196CBBD97AB8F61BE8679B74C8F17DF2713CB37AEF0922E5F3C9F725B28AB4E'; Bytes = 42120; SourceV4Commit = 'a96c195c852e035ff6434584135718bbb8a05d29'; FixtureFreezeCommit = '205506f07f6a1ccfef6b32cf97390431e3349f8d' }
    'shuffle-v1'  = [pscustomobject]@{ Sha256 = '386061B42691CCF36381E2D87DC4787367958C8E9E96A93BA69D1E3FE5DAC282'; Bytes = 99467; SourceV4Commit = '37b4dd610c5e7908dc4938378278c1475e0eee73'; FixtureFreezeCommit = '205506f07f6a1ccfef6b32cf97390431e3349f8d' }
    'ai-v1'       = [pscustomobject]@{ Sha256 = 'E898CDB6E1034D1DC8229E807A919E7F64E03062D3E9AD64DED36317402375C9'; Bytes = 51271; SourceV4Commit = '205506f07f6a1ccfef6b32cf97390431e3349f8d'; FixtureFreezeCommit = '205506f07f6a1ccfef6b32cf97390431e3349f8d' }
}
$script:HistoricalFixtureFreezeCommit = '205506f07f6a1ccfef6b32cf97390431e3349f8d'

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
        $value = $Object
        $exists = $true
        foreach ($segment in ($path -split '\.')) {
            if ($null -eq $value) { $exists = $false; break }
            $property = $value.PSObject.Properties[$segment]
            if ($null -eq $property) { $exists = $false; break }
            $value = $property.Value
        }
        if (-not $exists) {
            Add-Failure 'CASE_STRUCTURE_MISSING' ("{0}: missing path '{1}'" -f $Label, $path)
            continue
        }
        if ($null -eq $value) {
            if ($type -ne 'string-or-null' -and $type -ne 'number-or-null' -and $type -ne 'null') {
                Add-Failure 'CASE_STRUCTURE_MISSING' ("{0}: null path '{1}'" -f $Label, $path)
                continue
            }
        }
        elseif ($true) {
            $typeOk = switch ($type) {
                'object' { $value -is [System.Management.Automation.PSCustomObject] -or $value -is [System.Collections.IDictionary] }
                'array' { $value -is [System.Array] }
                'string' { $value -is [string] }
                'number' { $value -is [int] -or $value -is [long] -or $value -is [double] -or $value -is [decimal] }
                'number-or-null' { $value -is [int] -or $value -is [long] -or $value -is [double] -or $value -is [decimal] }
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

function Test-ConditionalRules {
    param([object]$Object, [object[]]$Rules, [string]$Label)
    foreach ($conditional in $Rules) {
        $path = [string](Get-Prop -Object $conditional -Name 'path')
        $equals = [string](Get-Prop -Object $conditional -Name 'equals')
        $rules = @(Get-ArrayProp -Object $conditional -Name 'rules')
        if ([string]::IsNullOrWhiteSpace($path) -or $rules.Count -eq 0) {
            Add-Failure 'GROUP_STRUCTURE_INVALID' ("{0}: conditional rule needs path and rules" -f $Label)
            continue
        }
        $value = $Object
        foreach ($segment in ($path -split '\.')) {
            if ($null -eq $value) { break }
            $property = $value.PSObject.Properties[$segment]
            if ($null -eq $property) { $value = $null; break }
            $value = $property.Value
        }
        if ($null -ne $value -and [string]$value -ceq $equals) {
            Test-ShapeRules -Object $Object -Rules $rules -Label ('{0}[{1}={2}]' -f $Label, $path, $equals)
        }
    }
}

function Test-ArrayItemRules {
    param([object]$Object, [object]$Specs, [string]$Label)
    foreach ($property in $Specs.PSObject.Properties) {
        $path = $property.Name
        $spec = $property.Value
        $type = [string](Get-Prop -Object $spec -Name 'type')
        if ([string]::IsNullOrWhiteSpace($type)) {
            Add-Failure 'GROUP_STRUCTURE_INVALID' ("{0}: arrayItemRules '{1}' needs type" -f $Label, $path)
            continue
        }
        $value = $Object
        foreach ($segment in ($path -split '\.')) {
            if ($null -eq $value) { break }
            $prop = $value.PSObject.Properties[$segment]
            if ($null -eq $prop) { $value = $null; break }
            $value = $prop.Value
        }
        if ($null -eq $value) {
            Add-Failure 'CASE_STRUCTURE_MISSING' ("{0}: array '{1}' missing" -f $Label, $path)
            continue
        }
        if (-not ($value -is [System.Array])) {
            Add-Failure 'CASE_STRUCTURE_TYPE' ("{0}: array '{1}' expected array" -f $Label, $path)
            continue
        }
        foreach ($element in $value) {
            if ($type -eq 'object') {
                if (-not ($element -is [System.Management.Automation.PSCustomObject])) {
                    Add-Failure 'CASE_STRUCTURE_TYPE' ("{0}: array '{1}' element expected object" -f $Label, $path)
                    continue
                }
                $rules = @(Get-ArrayProp -Object $spec -Name 'rules')
                if ($rules.Count -gt 0) {
                    Test-ShapeRules -Object $element -Rules $rules -Label ('{0}.{1}' -f $Label, $path)
                }
                $nestedSpecs = Get-Prop -Object $spec -Name 'arrayItemRules'
                if ($null -ne $nestedSpecs) {
                    Test-ArrayItemRules -Object $element -Specs $nestedSpecs -Label ('{0}.{1}' -f $Label, $path)
                }
                $itemAllowed = Get-Prop -Object $spec -Name 'allowedValues'
                if ($null -ne $itemAllowed) {
                    Test-AllowedValues -Object $element -Specs $itemAllowed -Label ('{0}.{1}' -f $Label, $path)
                }
                $itemConditional = @(Get-ArrayProp -Object $spec -Name 'conditionalRules')
                if ($itemConditional.Count -gt 0) {
                    Test-ConditionalRules -Object $element -Rules $itemConditional -Label ('{0}.{1}' -f $Label, $path)
                }
            }
            elseif ($type -eq 'number') {
                if (-not ($element -is [int] -or $element -is [long] -or $element -is [double] -or $element -is [decimal])) {
                    Add-Failure 'CASE_STRUCTURE_TYPE' ("{0}: array '{1}' element expected number" -f $Label, $path)
                }
            }
            elseif ($type -eq 'string') {
                if (-not ($element -is [string])) {
                    Add-Failure 'CASE_STRUCTURE_TYPE' ("{0}: array '{1}' element expected string" -f $Label, $path)
                }
            }
            else {
                Add-Failure 'GROUP_STRUCTURE_INVALID' ("{0}: arrayItemRules '{1}' unknown type '{2}'" -f $Label, $path, $type)
            }
        }
    }
}

function Test-ConditionalArrayItemRules {
    param([object]$Object, [object[]]$Rules, [string]$Label)
    foreach ($conditional in $Rules) {
        $path = [string](Get-Prop -Object $conditional -Name 'path')
        $equals = [string](Get-Prop -Object $conditional -Name 'equals')
        $specs = Get-Prop -Object $conditional -Name 'arrayItemRules'
        if ([string]::IsNullOrWhiteSpace($path) -or $null -eq $specs) {
            Add-Failure 'GROUP_STRUCTURE_INVALID' ("{0}: conditional array rule needs path and arrayItemRules" -f $Label)
            continue
        }
        $value = $Object
        foreach ($segment in ($path -split '\.')) {
            if ($null -eq $value) { break }
            $prop = $value.PSObject.Properties[$segment]
            if ($null -eq $prop) { $value = $null; break }
            $value = $prop.Value
        }
        if ($null -ne $value -and [string]$value -ceq $equals) {
            Test-ArrayItemRules -Object $Object -Specs $specs -Label ('{0}[{1}={2}]' -f $Label, $path, $equals)
        }
    }
}

function Test-RandomSamplesFast {
    param([object[]]$Items, [string]$Label)
    foreach ($item in $Items) {
        try {
        if (-not ($item.id -is [string])) { Add-Failure 'CASE_STRUCTURE_TYPE' ("{0}: id expected string" -f $Label); continue }
        if (-not ($item.closed -is [System.Array])) { Add-Failure 'CASE_STRUCTURE_TYPE' ("{0}: closed expected array" -f $Label); continue }
        foreach ($tile in $item.closed) {
            if (-not ($tile -is [int] -or $tile -is [long] -or $tile -is [double] -or $tile -is [decimal])) { Add-Failure 'CASE_STRUCTURE_TYPE' ("{0}: closed element expected number" -f $Label); break }
        }
        if (-not ($null -eq $item.drawn -or $item.drawn -is [int] -or $item.drawn -is [long] -or $item.drawn -is [double] -or $item.drawn -is [decimal])) { Add-Failure 'CASE_STRUCTURE_TYPE' ("{0}: drawn expected number-or-null" -f $Label); continue }
        if (-not ($item.melds -is [System.Array])) { Add-Failure 'CASE_STRUCTURE_TYPE' ("{0}: melds expected array" -f $Label); continue }
        foreach ($meld in $item.melds) {
            if (-not ($meld -is [System.Management.Automation.PSCustomObject])) { Add-Failure 'CASE_STRUCTURE_TYPE' ("{0}: meld element expected object" -f $Label); break }
            if (-not ($meld.type -is [string])) { Add-Failure 'CASE_STRUCTURE_MISSING' ("{0}: meld.type missing" -f $Label); break }
            if (-not ($meld.tiles -is [System.Array])) { Add-Failure 'CASE_STRUCTURE_MISSING' ("{0}: meld.tiles missing" -f $Label); break }
            foreach ($tile in $meld.tiles) {
                if (-not ($tile -is [int] -or $tile -is [long] -or $tile -is [double] -or $tile -is [decimal])) { Add-Failure 'CASE_STRUCTURE_TYPE' ("{0}: meld.tiles element expected number" -f $Label); break }
            }
            if (-not ($meld.fromPlayer -is [int] -or $meld.fromPlayer -is [long] -or $meld.fromPlayer -is [double] -or $meld.fromPlayer -is [decimal])) { Add-Failure 'CASE_STRUCTURE_MISSING' ("{0}: meld.fromPlayer missing" -f $Label); break }
            if (-not ($meld.calledTile -is [int] -or $meld.calledTile -is [long] -or $meld.calledTile -is [double] -or $meld.calledTile -is [decimal])) { Add-Failure 'CASE_STRUCTURE_MISSING' ("{0}: meld.calledTile missing" -f $Label); break }
        }
        if (-not ($item.dora -is [System.Array])) { Add-Failure 'CASE_STRUCTURE_TYPE' ("{0}: dora expected array" -f $Label); continue }
        foreach ($tile in $item.dora) {
            if (-not ($tile -is [int] -or $tile -is [long] -or $tile -is [double] -or $tile -is [decimal])) { Add-Failure 'CASE_STRUCTURE_TYPE' ("{0}: dora element expected number" -f $Label); break }
        }
        if (-not ($item.ura -is [System.Array])) { Add-Failure 'CASE_STRUCTURE_TYPE' ("{0}: ura expected array" -f $Label); continue }
        foreach ($tile in $item.ura) {
            if (-not ($tile -is [int] -or $tile -is [long] -or $tile -is [double] -or $tile -is [decimal])) { Add-Failure 'CASE_STRUCTURE_TYPE' ("{0}: ura element expected number" -f $Label); break }
        }
        if (-not ($item.removedWinTile -is [bool])) { Add-Failure 'CASE_STRUCTURE_TYPE' ("{0}: removedWinTile expected boolean" -f $Label); continue }
        $ctx = $item.context
        if ($null -eq $ctx) { Add-Failure 'CASE_STRUCTURE_MISSING' ("{0}: context missing" -f $Label); continue }
        if (-not ($ctx -is [System.Management.Automation.PSCustomObject])) { Add-Failure 'CASE_STRUCTURE_TYPE' ("{0}: context expected object" -f $Label); continue }
        if (-not ($ctx.winTile -is [int] -or $ctx.winTile -is [long] -or $ctx.winTile -is [double] -or $ctx.winTile -is [decimal])) { Add-Failure 'CASE_STRUCTURE_MISSING' ("{0}: context.winTile missing/non-number" -f $Label); continue }
        if (-not ($ctx.isTsumo -is [bool])) { Add-Failure 'CASE_STRUCTURE_MISSING' ("{0}: context.isTsumo missing/non-boolean" -f $Label); continue }
        if (-not ($ctx.isClosed -is [bool])) { Add-Failure 'CASE_STRUCTURE_MISSING' ("{0}: context.isClosed missing/non-boolean" -f $Label); continue }
        if (-not ($ctx.seatWind -is [int] -or $ctx.seatWind -is [long] -or $ctx.seatWind -is [double] -or $ctx.seatWind -is [decimal])) { Add-Failure 'CASE_STRUCTURE_MISSING' ("{0}: context.seatWind missing/non-number" -f $Label); continue }
        if (-not ($ctx.roundWind -is [int] -or $ctx.roundWind -is [long] -or $ctx.roundWind -is [double] -or $ctx.roundWind -is [decimal])) { Add-Failure 'CASE_STRUCTURE_MISSING' ("{0}: context.roundWind missing/non-number" -f $Label); continue }
        foreach ($field in @('isRiichi','isDoubleRiichi','isIppatsu','isRinshan','isHaitei','isHoutei','isChankan','isTenhou','isChiihou')) {
            if (-not ($ctx.$field -is [bool])) { Add-Failure 'CASE_STRUCTURE_MISSING' ("{0}: context.{1} missing/non-boolean" -f $Label, $field); break }
        }
        $expected = $item.expected
        if (-not ($expected -is [System.Management.Automation.PSCustomObject])) { Add-Failure 'CASE_STRUCTURE_TYPE' ("{0}: expected expected object" -f $Label); continue }
        if (-not ($expected.status -is [string])) { Add-Failure 'CASE_STRUCTURE_MISSING' ("{0}: expected.status missing/non-string" -f $Label); continue }
        if ($expected.status -cne 'win' -and $expected.status -cne 'noWin') { Add-Failure 'CASE_STRUCTURE_UNKNOWN_VARIANT' ("{0}: expected.status '{1}' not in win/noWin" -f $Label, $expected.status); continue }
        if ($expected.status -ceq 'win') {
            if (-not ($expected.yaku -is [System.Array])) { Add-Failure 'CASE_STRUCTURE_MISSING' ("{0}: expected.yaku missing" -f $Label); continue }
            foreach ($yaku in $expected.yaku) {
                if (-not ($yaku -is [System.Management.Automation.PSCustomObject])) { Add-Failure 'CASE_STRUCTURE_TYPE' ("{0}: expected.yaku element expected object" -f $Label); break }
                if (-not ($yaku.code -is [string])) { Add-Failure 'CASE_STRUCTURE_MISSING' ("{0}: expected.yaku.code missing" -f $Label); break }
                if (-not ($yaku.han -is [int] -or $yaku.han -is [long] -or $yaku.han -is [double] -or $yaku.han -is [decimal])) { Add-Failure 'CASE_STRUCTURE_MISSING' ("{0}: expected.yaku.han missing" -f $Label); break }
            }
            if (-not ($expected.yakuHan -is [int] -or $expected.yakuHan -is [long] -or $expected.yakuHan -is [double] -or $expected.yakuHan -is [decimal])) { Add-Failure 'CASE_STRUCTURE_MISSING' ("{0}: expected.yakuHan missing" -f $Label); continue }
            if (-not ($expected.scoreHan -is [int] -or $expected.scoreHan -is [long] -or $expected.scoreHan -is [double] -or $expected.scoreHan -is [decimal])) { Add-Failure 'CASE_STRUCTURE_MISSING' ("{0}: expected.scoreHan missing" -f $Label); continue }
            if (-not ($expected.isYakuman -is [bool])) { Add-Failure 'CASE_STRUCTURE_MISSING' ("{0}: expected.isYakuman missing" -f $Label); continue }
            if (-not ($expected.yakumanCount -is [int] -or $expected.yakumanCount -is [long] -or $expected.yakumanCount -is [double] -or $expected.yakumanCount -is [decimal])) { Add-Failure 'CASE_STRUCTURE_MISSING' ("{0}: expected.yakumanCount missing" -f $Label); continue }
            if (-not ($expected.fu -is [int] -or $expected.fu -is [long] -or $expected.fu -is [double] -or $expected.fu -is [decimal])) { Add-Failure 'CASE_STRUCTURE_MISSING' ("{0}: expected.fu missing" -f $Label); continue }
            if (-not ($expected.basePoints -is [int] -or $expected.basePoints -is [long] -or $expected.basePoints -is [double] -or $expected.basePoints -is [decimal])) { Add-Failure 'CASE_STRUCTURE_MISSING' ("{0}: expected.basePoints missing" -f $Label); continue }
            if (-not ($null -eq $expected.capCode -or $expected.capCode -is [string])) { Add-Failure 'CASE_STRUCTURE_TYPE' ("{0}: expected.capCode expected string-or-null" -f $Label); continue }
            if (-not ($expected.totalPoints -is [int] -or $expected.totalPoints -is [long] -or $expected.totalPoints -is [double] -or $expected.totalPoints -is [decimal])) { Add-Failure 'CASE_STRUCTURE_MISSING' ("{0}: expected.totalPoints missing" -f $Label); continue }
        }
        }
        catch {
            Add-Failure 'CASE_STRUCTURE_MISSING' ("{0}: consumed field missing: {1}" -f $Label, $_.Exception.Message)
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
        $variantNames = @($variants.PSObject.Properties | ForEach-Object { $_.Name })
        if (-not ($variantNames -ccontains $key)) {
            Add-Failure 'CASE_STRUCTURE_UNKNOWN_VARIANT' ("{0}: unknown '{1}'='{2}'" -f $Label, $discriminator, $key)
            continue
        }
        $variant = $variants.PSObject.Properties[$key]
        Test-ShapeRules -Object $item -Rules @($variant.Value) -Label ('{0}[{1}]' -f $Label, $key)
    }
}

function Test-AllowedValues {
    param([object]$Object, [object]$Specs, [string]$Label)
    foreach ($property in $Specs.PSObject.Properties) {
        $path = $property.Name
        $allowed = @($property.Value)
        $value = $Object
        foreach ($segment in ($path -split '\.')) {
            if ($null -eq $value) { break }
            $prop = $value.PSObject.Properties[$segment]
            if ($null -eq $prop) { $value = $null; break }
            $value = $prop.Value
        }
        if ($null -eq $value -or -not ($allowed -ccontains [string]$value)) {
            Add-Failure 'CASE_STRUCTURE_UNKNOWN_VARIANT' ("{0}: '{1}'='{2}' not in accepted literals" -f $Label, $path, $value)
        }
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
    if (-not ($ids -ccontains $caseId)) {
        Add-Failure 'ANCHOR_MISSING' ("{0}: case not found: {1}/{2}" -f $RequirementId, $groupKey, $caseId)
    }
}

function Test-LineRangeAnchor {
    param([string]$Path, [string]$Lines, [string]$CodePrefix, [string]$Label)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Add-Failure ($CodePrefix + '_MISSING') ("{0}: consumer file not found: {1}" -f $Label, $Path)
        return
    }
    if ([string]::IsNullOrWhiteSpace($Lines) -or $Lines -notmatch '^[0-9]+(-[0-9]+)?(,[0-9]+(-[0-9]+)?)*$') {
        Add-Failure ($CodePrefix + '_INVALID') ("{0}: invalid line anchor '{1}'" -f $Label, $Lines)
        return
    }
    $lineCount = @([System.IO.File]::ReadAllLines($Path, [System.Text.Encoding]::UTF8)).Count
    foreach ($part in ($Lines -split ',')) {
        $bounds = $part -split '-'
        $start = [int]$bounds[0]
        $end = if ($bounds.Count -gt 1) { [int]$bounds[1] } else { $start }
        if ($start -lt 1 -or $end -lt $start -or $end -gt $lineCount) {
            Add-Failure ($CodePrefix + '_INVALID') ("{0}: line range '{1}' outside 1..{2}" -f $Label, $part, $lineCount)
        }
    }
}

function Test-AiBindings {
    param([object]$Fixture, [string]$RepoRoot)
    $bindings = @(Get-ArrayProp $Fixture 'bindings')
    if ($bindings.Count -eq 0) {
        Add-Failure 'AI_BINDING_MISSING' 'ai-v1 must declare value-binding anchors'
        return
    }
    $allowedKinds = @('IN_BIND', 'OUT_DIFF', 'SEM_MAP', 'INTERNAL', 'UNBOUND', 'UNBOUND_HC', 'UNA', 'META', 'CONTAINER')
    $seen = @{}
    foreach ($binding in $bindings) {
        $jsonPath = [string](Get-Prop $binding 'jsonPath')
        if ([string]::IsNullOrWhiteSpace($jsonPath) -or -not $jsonPath.StartsWith('$')) {
            Add-Failure 'AI_BINDING_INVALID' ("invalid jsonPath '{0}'" -f $jsonPath)
            continue
        }
        $kind = [string](Get-Prop $binding 'kind')
        if ($allowedKinds -notcontains $kind) {
            Add-Failure 'AI_BINDING_KIND_INVALID' ("{0}: unknown kind '{1}'" -f $jsonPath, $kind)
        }
        $seen[$jsonPath] = $true
        if ($kind -eq 'UNBOUND' -or $kind -eq 'UNBOUND_HC') {
            $owner = [string](Get-Prop $binding 'owner')
            if ($owner -notmatch '^GAP:N3-[A-Za-z0-9]+$') {
                Add-Failure 'AI_BINDING_OWNER_INVALID' ("{0}: missing GAP:N3-* owner" -f $jsonPath)
            }
        }
        else {
            $consumerPath = [string](Get-Prop $binding 'consumerPath')
            $consumerLines = [string](Get-Prop $binding 'consumerLines')
            if ([string]::IsNullOrWhiteSpace($consumerPath)) {
                Add-Failure 'AI_BINDING_CONSUMER_MISSING' ("{0}: bound field has no consumerPath" -f $jsonPath)
                continue
            }
            $full = Join-Path $RepoRoot ($consumerPath -replace '/', '\')
            Test-LineRangeAnchor -Path $full -Lines $consumerLines -CodePrefix 'AI_BINDING_ANCHOR' -Label $jsonPath
        }
    }
    foreach ($required in @('$.cases[*].v5.expected.mappingBasis', '$.v5Observation.mappingLimits', '$.cases[*].v4.events[*].text', '$.cases[*].v4Call.action.text')) {
        if (-not $seen.ContainsKey($required)) {
            Add-Failure 'AI_BINDING_GAP_MISSING' ("required unbound field not declared: {0}" -f $required)
        }
    }
    $partial = @(Get-ArrayProp $Fixture 'partialReason')
    if ($partial.Count -lt 3) {
        Add-Failure 'AI_PARTIAL_REASON_MISSING' 'ai-v1 must declare at least three partial reasons'
    }
}

function Test-AcceptedBindings {
    param([object]$Fixture, [string]$RepoRoot)
    $accepted = @(Get-ArrayProp $Fixture 'acceptedBindings')
    if ($accepted.Count -eq 0) {
        Add-Failure 'AI_ACCEPTED_BINDING_MISSING' 'ai-v1 must index its accepted state-binding test class'
        return
    }
    foreach ($binding in $accepted) {
        $relative = [string](Get-Prop $binding 'path')
        $full = Join-Path $RepoRoot ($relative -replace '/', '\')
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
            Add-Failure 'AI_ACCEPTED_BINDING_PATH_MISSING' $relative
            continue
        }
        $actualFacts = @(Select-String -LiteralPath $full -Pattern '^\s*\[Fact\]').Count
        if ($actualFacts -ne [int](Get-Prop $binding 'factCount')) {
            Add-Failure 'AI_ACCEPTED_BINDING_FACT_MISMATCH' ("{0}: declared {1}, actual {2}" -f $relative, (Get-Prop $binding 'factCount'), $actualFacts)
        }
        if ([string](Get-Prop $binding 'class') -notmatch '^[A-Za-z0-9_.]+$') {
            Add-Failure 'AI_ACCEPTED_BINDING_CLASS_INVALID' $relative
        }
        if ([string](Get-Prop $binding 'commit') -notmatch '^[0-9a-fA-F]{40}$') {
            Add-Failure 'AI_ACCEPTED_BINDING_COMMIT_INVALID' $relative
        }
        $reportRelative = [string](Get-Prop $binding 'report')
        if ([string]::IsNullOrWhiteSpace($reportRelative) -or -not (Test-Path -LiteralPath (Join-Path $RepoRoot ($reportRelative -replace '/', '\')) -PathType Leaf)) {
            Add-Failure 'AI_ACCEPTED_BINDING_REPORT_MISSING' $reportRelative
        }
    }
}

function Test-AiSourceFileSha {
    param([object]$Raw)
    $files = @(Get-Prop $Raw 'sourceFileSha256')
    if ($files.Count -ne 13) {
        Add-Failure 'AI_SOURCE_FILE_COUNT' ("expected 13, got {0}" -f $files.Count)
    }
    foreach ($file in $files) {
        if ([string](Get-Prop $file 'sha256') -notmatch '^[0-9a-fA-F]{64}$') {
            Add-Failure 'AI_SOURCE_SHA_INVALID' ([string](Get-Prop $file 'path'))
        }
        if ([string](Get-Prop $file 'lastCommit') -notmatch '^[0-9a-fA-F]{40}$') {
            Add-Failure 'AI_SOURCE_COMMIT_INVALID' ([string](Get-Prop $file 'path'))
        }
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
    $conditionalRules = @()
    $arrayItemRules = $null
    $conditionalArrayItemRules = @()
    $allowedValueSpecs = $null
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
        $conditionalProperty = $shape.PSObject.Properties['conditionalRules']
        if ($null -ne $conditionalProperty) {
            if ($null -eq $conditionalProperty.Value -or -not ($conditionalProperty.Value -is [System.Array])) {
                Add-Failure 'GROUP_STRUCTURE_INVALID' ("{0}:{1}.shape.conditionalRules must be a non-null array" -f $fixtureId, $groupName)
            }
            else {
                $conditionalRules = @($conditionalProperty.Value)
            }
        }
        $arrayItemRules = Get-Prop -Object $shape -Name 'arrayItemRules'
        $allowedValueSpecs = Get-Prop -Object $shape -Name 'allowedValues'
        $conditionalArrayProperty = $shape.PSObject.Properties['conditionalArrayItemRules']
        if ($null -ne $conditionalArrayProperty) {
            if ($null -eq $conditionalArrayProperty.Value -or -not ($conditionalArrayProperty.Value -is [System.Array])) {
                Add-Failure 'GROUP_STRUCTURE_INVALID' ("{0}:{1}.shape.conditionalArrayItemRules must be a non-null array" -f $fixtureId, $groupName)
            }
            else {
                $conditionalArrayItemRules = @($conditionalArrayProperty.Value)
            }
        }
    }

    if ($kind -eq 'array') {
        $items = @($actual)
        if ($items.Count -ne $declaredCount) {
            Add-Failure 'GROUP_COUNT_MISMATCH' ("{0}:{1}: declared {2}, actual {3}" -f $fixtureId, $groupName, $declaredCount, $items.Count)
        }
        $fastRandom = ($fixtureId -eq 'scoring-v1' -and $groupName -eq 'randomSamples')
        $requiredKeys = @(Get-DeclaredStringArray -Holder $Group -Name 'requiredKeys' -Label ('{0}:{1}' -f $fixtureId, $groupName))
        $requiredKeys = @(@($requiredKeys) + @($shapeRequiredKeys) | Select-Object -Unique)
        if (-not $fastRandom -and $requiredKeys.Count -gt 0) {
            foreach ($item in $items) {
                $itemKeys = @($item.PSObject.Properties | ForEach-Object { $_.Name })
                $missingKeys = @($requiredKeys | Where-Object { $itemKeys -notcontains $_ })
                if ($missingKeys.Count -gt 0) {
                    Add-Failure 'CASE_STRUCTURE_MISSING' ("{0}:{1}: case missing keys [{2}]" -f $fixtureId, $groupName, ($missingKeys -join ','))
                    break
                }
            }
        }
        if ($fastRandom -and $items.Count -gt 0) {
            Test-RandomSamplesFast -Items $items -Label ('{0}:{1}' -f $fixtureId, $groupName)
        }
        elseif (($shapeRules.Count -gt 0 -or $null -ne $elementRules -or $conditionalRules.Count -gt 0 -or $null -ne $arrayItemRules -or $conditionalArrayItemRules.Count -gt 0) -and $items.Count -gt 0) {
            foreach ($target in $items) {
                if ($shapeRules.Count -gt 0) {
                    Test-ShapeRules -Object $target -Rules $shapeRules -Label ('{0}:{1}' -f $fixtureId, $groupName)
                }
                if ($conditionalRules.Count -gt 0) {
                    Test-ConditionalRules -Object $target -Rules $conditionalRules -Label ('{0}:{1}' -f $fixtureId, $groupName)
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
                if ($null -ne $arrayItemRules) {
                    Test-ArrayItemRules -Object $target -Specs $arrayItemRules -Label ('{0}:{1}' -f $fixtureId, $groupName)
                }
                if ($conditionalArrayItemRules.Count -gt 0) {
                    Test-ConditionalArrayItemRules -Object $target -Rules $conditionalArrayItemRules -Label ('{0}:{1}' -f $fixtureId, $groupName)
                }
                if ($null -ne $allowedValueSpecs) {
                    Test-AllowedValues -Object $target -Specs $allowedValueSpecs -Label ('{0}:{1}' -f $fixtureId, $groupName)
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
                    if ([string]$declaredIds[$i] -cne [string]$ids[$i]) {
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
            if ($ids.Count -gt 0 -and ([string]$firstId -cne [string]$ids[0] -or [string]$lastId -cne [string]$ids[$ids.Count - 1])) {
                Add-Failure 'GROUP_IDS_MISMATCH' ("{0}:{1}: first/last id mismatch" -f $fixtureId, $groupName)
            }
        }

        $declaredKeys = @(Get-ArrayProp -Object $Group -Name 'keys')
        if ($declaredKeys.Count -gt 0) {
            if ((@($declaredKeys) -join '|') -cne (@($ids) -join '|')) {
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
        $idsProperty = $Group.PSObject.Properties['ids']
        if ($null -eq $idsProperty) {
            Add-Failure 'GROUP_IDS_MISMATCH' ("{0}:{1}: sample group has no declared ids" -f $fixtureId, $groupName)
        }
        elseif ($null -eq $idsProperty.Value -or -not ($idsProperty.Value -is [System.Array])) {
            Add-Failure 'GROUP_STRUCTURE_INVALID' ("{0}:{1}: declared ids must be a non-null array" -f $fixtureId, $groupName)
        }
        else {
            $declaredIds = @($idsProperty.Value)
            if ($declaredIds.Count -ne 1) {
                Add-Failure 'GROUP_IDS_MISMATCH' ("{0}:{1}: sample group must declare exactly one id, got {2}" -f $fixtureId, $groupName, $declaredIds.Count)
            }
            elseif ([string]$declaredIds[0] -cne $sampleId) {
                Add-Failure 'GROUP_IDS_MISMATCH' ("{0}:{1}: declared id '{2}', actual sampleId '{3}'" -f $fixtureId, $groupName, $declaredIds[0], $sampleId)
            }
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
        if ($shapeRules.Count -gt 0 -or $null -ne $elementRules -or $conditionalRules.Count -gt 0 -or $null -ne $arrayItemRules -or $conditionalArrayItemRules.Count -gt 0) {
            if ($shapeRules.Count -gt 0) {
                Test-ShapeRules -Object $actual -Rules $shapeRules -Label ('{0}:{1}' -f $fixtureId, $groupName)
            }
            if ($conditionalRules.Count -gt 0) {
                Test-ConditionalRules -Object $actual -Rules $conditionalRules -Label ('{0}:{1}' -f $fixtureId, $groupName)
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
            if ($null -ne $arrayItemRules) {
                Test-ArrayItemRules -Object $actual -Specs $arrayItemRules -Label ('{0}:{1}' -f $fixtureId, $groupName)
            }
            if ($conditionalArrayItemRules.Count -gt 0) {
                Test-ConditionalArrayItemRules -Object $actual -Rules $conditionalArrayItemRules -Label ('{0}:{1}' -f $fixtureId, $groupName)
            }
            if ($null -ne $allowedValueSpecs) {
                Test-AllowedValues -Object $actual -Specs $allowedValueSpecs -Label ('{0}:{1}' -f $fixtureId, $groupName)
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
if ([string](Get-Prop $script:Catalog 'schema') -cne 'v5-golden-catalog-v1') {
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
    $fixtureFreeze = [string](Get-Prop $baseline 'fixtureFreezeCommit')
    if ($fixtureFreeze -cne $script:HistoricalFixtureFreezeCommit) {
        Add-Failure 'BASELINE_FIXTURE_FREEZE_INVALID' ("expected {0}, got {1}" -f $script:HistoricalFixtureFreezeCommit, $fixtureFreeze)
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
    'ai-v1' = 'v5-ai-golden-v1'
}

$fixtures = @(Get-ArrayProp $script:Catalog 'fixtures')
if ($fixtures.Count -ne 6) {
    Add-Failure 'FIXTURE_COUNT' ('expected 6, got {0}' -f $fixtures.Count)
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
    $acceptedPin = $script:AcceptedFixturePins[$fixtureId]
    if ($null -eq $acceptedPin) {
        Add-Failure 'FIXTURE_ID_UNKNOWN' ("{0}: not in the accepted fixture pin set" -f $fixtureId)
    }
    else {
        if ($actualSha -cne $acceptedPin.Sha256) {
            Add-Failure 'IMMUTABLE_FIXTURE_PIN_MISMATCH' ("{0}: accepted {1}, actual {2}" -f $fixtureId, $acceptedPin.Sha256, $actualSha)
        }
        if ([int64]$file.Length -ne [int64]$acceptedPin.Bytes) {
            Add-Failure 'IMMUTABLE_FIXTURE_PIN_MISMATCH' ("{0}: accepted bytes {1}, actual {2}" -f $fixtureId, $acceptedPin.Bytes, $file.Length)
        }
        $catalogFixtureFreeze = [string](Get-Prop $fixture 'fixtureFreezeCommit')
        if ($catalogFixtureFreeze -cne $acceptedPin.FixtureFreezeCommit) {
            Add-Failure 'FIXTURE_FREEZE_COMMIT_MISMATCH' ("{0}: catalog '{1}', accepted '{2}'" -f $fixtureId, $catalogFixtureFreeze, $acceptedPin.FixtureFreezeCommit)
        }
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
        if ($actualSchema -cne $allowedSchemas[$fixtureId]) {
            Add-Failure 'FIXTURE_SCHEMA_UNKNOWN' ("{0}: actual schema '{1}' not the accepted '{2}'" -f $fixtureId, $actualSchema, $allowedSchemas[$fixtureId])
        }
    }
    else {
        Add-Failure 'FIXTURE_SCHEMA_UNKNOWN' ("{0}: fixture id is not in the accepted set" -f $fixtureId)
    }
    if ($actualSchema -cne $declaredSchema) {
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
    if ($catalogStatus -cne $rawStatus) {
        Add-Failure 'EXTRACTION_STATUS_MISMATCH' ("{0}: catalog '{1}', fixture '{2}'" -f $fixtureId, $catalogStatus, $rawStatus)
    }
    if ($rawStatus -cne 'exported') {
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
    if ($null -ne $acceptedPin -and $commit -cne $acceptedPin.SourceV4Commit) {
        Add-Failure 'IMMUTABLE_SOURCE_PIN_MISMATCH' ("{0}: accepted {1}, catalog {2}" -f $fixtureId, $acceptedPin.SourceV4Commit, $commit)
    }
    $fixtureFreezeValue = [string](Get-Prop $fixture 'fixtureFreezeCommit')
    if ($fixtureFreezeValue -cne $script:HistoricalFixtureFreezeCommit) {
        Add-Failure 'FIXTURE_FREEZE_COMMIT_MISMATCH' ("{0}: catalog '{1}', historical '{2}'" -f $fixtureId, $fixtureFreezeValue, $script:HistoricalFixtureFreezeCommit)
    }
    $relation = [string](Get-Prop $fixture 'sourcePinRelation')
    $expectedRelation = if ($commit -ceq $fixtureFreezeValue) { 'source-pin-equals-fixture-freeze' } else { 'source-pin-precedes-fixture-freeze' }
    if ($relation -cne $expectedRelation) {
        Add-Failure 'SOURCE_PIN_RELATION_MISMATCH' ("{0}: declared '{1}', expected '{2}'" -f $fixtureId, $relation, $expectedRelation)
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

    if ($fixtureId -eq 'ai-v1') {
        foreach ($mapping in @(
            @{ Catalog = 'sourceV4EngineCommit'; Raw = 'v4EngineSourceCommit'; Code = 'SOURCE_ENGINE_COMMIT_MISMATCH' },
            @{ Catalog = 'sourceV4AiCommit'; Raw = 'v4DotnetAiSourceCommit'; Code = 'SOURCE_AI_COMMIT_MISMATCH' },
            @{ Catalog = 'sourceV4DomainCommit'; Raw = 'v4DotnetDomainSourceCommit'; Code = 'SOURCE_DOMAIN_COMMIT_MISMATCH' }
        )) {
            $catalogValue = [string](Get-Prop $fixture $mapping.Catalog)
            $rawValue = [string](Get-Prop $raw $mapping.Raw)
            if ($catalogValue -cne $rawValue) {
                Add-Failure $mapping.Code ("{0}: catalog {1}, fixture {2}" -f $mapping.Catalog, $catalogValue, $rawValue)
            }
        }
        $generatedAtHead = [string](Get-Prop $fixture 'generatedAtHead')
        if ($generatedAtHead -notmatch '^[0-9a-fA-F]{40}$' -or $generatedAtHead -cne [string](Get-Prop $raw 'generatedAtHead')) {
            Add-Failure 'AI_GENERATED_AT_HEAD_MISMATCH' ("{0}: catalog '{1}', fixture '{2}'" -f $fixtureId, $generatedAtHead, (Get-Prop $raw 'generatedAtHead'))
        }
        if ([bool](Get-Prop $raw 'v4ProductionSourceDirty')) {
            Add-Failure 'AI_PRODUCTION_SOURCE_DIRTY' 'v4ProductionSourceDirty must be false'
        }
        if (@(Get-ArrayProp $raw 'v4ProductionSourceDirtyPaths').Count -ne 0) {
            Add-Failure 'AI_PRODUCTION_SOURCE_DIRTY' 'v4ProductionSourceDirtyPaths must be empty'
        }
        Test-AiSourceFileSha -Raw $raw
        Test-AcceptedBindings -Fixture $fixture -RepoRoot $RepoRoot
        Test-AiBindings -Fixture $fixture -RepoRoot $RepoRoot
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
# Source pins, immutable pins and case accounting
# ---------------------------------------------------------------------------
function Get-CaseGroupSum {
    param([string]$FixtureId, [string[]]$Names)
    $fixture = $script:FixturesById[$FixtureId]
    if ($null -eq $fixture) { return -1 }
    $sum = 0
    foreach ($group in @(Get-ArrayProp $fixture 'groups')) {
        if ([bool](Get-Prop $group 'countAsCase') -and ($Names -contains [string](Get-Prop $group 'name'))) {
            $sum += [int](Get-Prop $group 'count')
        }
    }
    return $sum
}

$sourcePins = Get-Prop $script:Catalog 'sourcePins'
if ($null -eq $sourcePins) {
    Add-Failure 'SOURCE_PINS_MISSING' 'sourcePins'
}
else {
    $catalogFreezePin = [string](Get-Prop $sourcePins 'catalogFreezeCommit')
    if ($catalogFreezePin -cne [string](Get-Prop $baseline 'freezeCommit')) {
        Add-Failure 'SOURCE_PIN_BASELINE_MISMATCH' ("sourcePins {0}, baseline {1}" -f $catalogFreezePin, (Get-Prop $baseline 'freezeCommit'))
    }
    $historicalPin = [string](Get-Prop $sourcePins 'historicalFixtureFreezeCommit')
    if ($historicalPin -cne $script:HistoricalFixtureFreezeCommit) {
        Add-Failure 'SOURCE_PIN_HISTORICAL_MISMATCH' ("expected {0}, got {1}" -f $script:HistoricalFixtureFreezeCommit, $historicalPin)
    }
}

$immutablePins = @(Get-ArrayProp $script:Catalog 'immutableFixturePins')
if ($immutablePins.Count -ne $fixtures.Count) {
    Add-Failure 'IMMUTABLE_PIN_SET_MISMATCH' ("pins {0}, fixtures {1}" -f $immutablePins.Count, $fixtures.Count)
}
foreach ($fixture in $fixtures) {
    $fixtureId = [string](Get-Prop $fixture 'id')
    $matches = @($immutablePins | Where-Object { [string](Get-Prop $_ 'id') -eq $fixtureId })
    if ($matches.Count -ne 1) {
        Add-Failure 'IMMUTABLE_PIN_MISSING' ("{0}: expected exactly one immutable pin, got {1}" -f $fixtureId, $matches.Count)
        continue
    }
    $pin = $matches[0]
    if ([string](Get-Prop $pin 'sha256') -cne [string](Get-Prop $fixture 'sha256')) {
        Add-Failure 'IMMUTABLE_PIN_ENTRY_MISMATCH' ("{0}: sha256" -f $fixtureId)
    }
    if ([int64](Get-Prop $pin 'bytes') -ne [int64](Get-Prop $fixture 'bytes')) {
        Add-Failure 'IMMUTABLE_PIN_ENTRY_MISMATCH' ("{0}: bytes" -f $fixtureId)
    }
    if ([string](Get-Prop $pin 'sourceV4Commit') -cne [string](Get-Prop $fixture 'sourceV4Commit')) {
        Add-Failure 'IMMUTABLE_PIN_ENTRY_MISMATCH' ("{0}: sourceV4Commit" -f $fixtureId)
    }
    if ([string](Get-Prop $pin 'fixtureFreezeCommit') -cne [string](Get-Prop $fixture 'fixtureFreezeCommit')) {
        Add-Failure 'IMMUTABLE_PIN_ENTRY_MISMATCH' ("{0}: fixtureFreezeCommit" -f $fixtureId)
    }
}

$fiveIds = @('physical-v1', 'scoring-v1', 'round-v1', 'cheat-v1', 'shuffle-v1')
$fiveTotal = 0
foreach ($fixtureId in $fiveIds) {
    $fiveTotal += [int](Get-Prop $script:FixturesById[$fixtureId] 'caseTotal')
}
$roundCatalog = [int](Get-Prop $script:FixturesById['round-v1'] 'caseTotal')
$cheatCatalog = [int](Get-Prop $script:FixturesById['cheat-v1'] 'caseTotal')
$roundAudit = Get-CaseGroupSum -FixtureId 'round-v1' -Names @('roundTerminal', 'chankanRon')
$cheatAudit = Get-CaseGroupSum -FixtureId 'cheat-v1' -Names @('tileMarkHandler', 'sleeveBloomHandler', 'beamSwapHandler')
$auditReplay = [int](Get-Prop $script:FixturesById['physical-v1'] 'caseTotal') +
    [int](Get-Prop $script:FixturesById['scoring-v1'] 'caseTotal') +
    $roundAudit +
    $cheatAudit +
    [int](Get-Prop $script:FixturesById['shuffle-v1'] 'caseTotal')
$aiTotal = [int](Get-Prop $script:FixturesById['ai-v1'] 'caseTotal')
$scoringCaseTotal = [int](Get-Prop $script:FixturesById['scoring-v1'] 'caseTotal')

$caseAccounting = Get-Prop $script:Catalog 'caseAccounting'
if ($null -eq $caseAccounting) {
    Add-Failure 'CASE_ACCOUNTING_MISSING' 'caseAccounting'
}
else {
    $caCatalog = [int64](Get-Prop $caseAccounting 'catalogCaseTotal')
    $caFive = [int64](Get-Prop $caseAccounting 'fiveFixtureCatalogCaseTotal')
    $caAudit = [int64](Get-Prop $caseAccounting 'frozenAuditReplayCaseTotal')
    $caDelta = [int64](Get-Prop $caseAccounting 'delta')
    $caAi = [int64](Get-Prop $caseAccounting 'aiIncrement')
    if ($caCatalog -ne $fixtureCaseTotal) {
        Add-Failure 'CASE_ACCOUNTING_MISMATCH' ("catalogCaseTotal {0}, computed fixtures {1}" -f $caCatalog, $fixtureCaseTotal)
    }
    if ($caFive -ne $fiveTotal) {
        Add-Failure 'CASE_ACCOUNTING_MISMATCH' ("fiveFixtureCatalogCaseTotal {0}, computed {1}" -f $caFive, $fiveTotal)
    }
    if ($caAudit -ne $auditReplay) {
        Add-Failure 'CASE_ACCOUNTING_MISMATCH' ("frozenAuditReplayCaseTotal {0}, computed {1}" -f $caAudit, $auditReplay)
    }
    if ($caDelta -ne ($fiveTotal - $auditReplay)) {
        Add-Failure 'CASE_ACCOUNTING_MISMATCH' ("delta {0}, computed {1}" -f $caDelta, ($fiveTotal - $auditReplay))
    }
    if ($caAi -ne $aiTotal) {
        Add-Failure 'CASE_ACCOUNTING_MISMATCH' ("aiIncrement {0}, computed {1}" -f $caAi, $aiTotal)
    }
    if ($caCatalog -ne ($fiveTotal + $aiTotal)) {
        Add-Failure 'CASE_ACCOUNTING_MISMATCH' ("catalogCaseTotal {0} != five {1} + ai {2}" -f $caCatalog, $fiveTotal, $aiTotal)
    }
    if ($caCatalog -eq ($fiveTotal + $aiTotal + $scoringRandom)) {
        Add-Failure 'CASE_ACCOUNTING_DOUBLE_COUNT' 'catalogCaseTotal appears to add scoring random samples a second time'
    }
    if (-not [bool](Get-Prop $caseAccounting 'scoringRandomInputsAreSubsetOfScoringCases')) {
        Add-Failure 'CASE_ACCOUNTING_MISMATCH' 'scoringRandomInputsAreSubsetOfScoringCases must be true'
    }
    if ($scoringRandom -gt $scoringCaseTotal) {
        Add-Failure 'CASE_ACCOUNTING_MISMATCH' ("scoring random {0} exceeds scoring cases {1}" -f $scoringRandom, $scoringCaseTotal)
    }

    $breakdown = @(Get-ArrayProp $caseAccounting 'deltaBreakdown')
    if ($breakdown.Count -ne 2) {
        Add-Failure 'CASE_ACCOUNTING_MISMATCH' ("deltaBreakdown must have 2 entries, got {0}" -f $breakdown.Count)
    }
    else {
        $expected = @(
            @{ Fixture = 'round-v1'; Catalog = $roundCatalog; Audit = $roundAudit; Only = @('matchProgression', 'decisionClock', 'controlEpoch'); AuditGroups = @('roundTerminal', 'chankanRon') },
            @{ Fixture = 'cheat-v1'; Catalog = $cheatCatalog; Audit = $cheatAudit; Only = @('tileMovement'); AuditGroups = @('tileMarkHandler', 'sleeveBloomHandler', 'beamSwapHandler') }
        )
        foreach ($want in $expected) {
            $rows = @($breakdown | Where-Object { [string](Get-Prop $_ 'fixture') -eq $want.Fixture })
            if ($rows.Count -ne 1) {
                Add-Failure 'CASE_ACCOUNTING_MISMATCH' ("{0}: expected one breakdown row, got {1}" -f $want.Fixture, $rows.Count)
                continue
            }
            $row = $rows[0]
            $rowCatalog = [int64](Get-Prop $row 'catalogCaseTotal')
            $rowAudit = [int64](Get-Prop $row 'auditReplayCaseTotal')
            $rowDelta = [int64](Get-Prop $row 'delta')
            if ($rowCatalog -ne $want.Catalog -or $rowAudit -ne $want.Audit -or $rowDelta -ne ($want.Catalog - $want.Audit)) {
                Add-Failure 'CASE_ACCOUNTING_MISMATCH' ("{0}: catalog/audit/delta do not recompute" -f $want.Fixture)
            }
            $only = @(Get-ArrayProp $row 'catalogOnlyGroups')
            $auditGroups = @(Get-ArrayProp $row 'auditReplayGroups')
            if ((@($only) -join '|') -cne (@($want.Only) -join '|')) {
                Add-Failure 'CASE_ACCOUNTING_MISMATCH' ("{0}: catalogOnlyGroups differ" -f $want.Fixture)
            }
            if ((@($auditGroups) -join '|') -cne (@($want.AuditGroups) -join '|')) {
                Add-Failure 'CASE_ACCOUNTING_MISMATCH' ("{0}: auditReplayGroups differ" -f $want.Fixture)
            }
            $fixtureGroups = @(Get-ArrayProp $script:FixturesById[$want.Fixture] 'groups')
            $onlySum = 0
            foreach ($name in $only) {
                $groupRows = @($fixtureGroups | Where-Object { [string](Get-Prop $_ 'name') -eq [string]$name })
                if ($groupRows.Count -ne 1) {
                    Add-Failure 'CASE_ACCOUNTING_MISMATCH' ("{0}: group '{1}' not unique" -f $want.Fixture, $name)
                    continue
                }
                $onlySum += [int](Get-Prop $groupRows[0] 'count')
            }
            $auditSum = 0
            foreach ($name in $auditGroups) {
                $groupRows = @($fixtureGroups | Where-Object { [string](Get-Prop $_ 'name') -eq [string]$name })
                if ($groupRows.Count -ne 1) {
                    Add-Failure 'CASE_ACCOUNTING_MISMATCH' ("{0}: group '{1}' not unique" -f $want.Fixture, $name)
                    continue
                }
                $auditSum += [int](Get-Prop $groupRows[0] 'count')
            }
            if ($onlySum -ne $rowDelta -or $auditSum -ne $rowAudit) {
                Add-Failure 'CASE_ACCOUNTING_MISMATCH' ("{0}: group sums do not match delta/audit" -f $want.Fixture)
            }
        }
    }

    $aiFixture = $script:FixturesById['ai-v1']
    $aiExecutionGroup = [string](Get-Prop $caseAccounting 'aiExecutionCaseGroup')
    $execRows = @(Get-ArrayProp $aiFixture 'groups' | Where-Object { [string](Get-Prop $_ 'name') -eq $aiExecutionGroup })
    if ($execRows.Count -ne 1 -or -not [bool](Get-Prop $execRows[0] 'countAsCase') -or [int](Get-Prop $execRows[0] 'count') -ne $aiTotal) {
        Add-Failure 'CASE_ACCOUNTING_MISMATCH' 'aiExecutionCaseGroup must be the single countAsCase group equal to aiTotal'
    }
    foreach ($name in @(Get-ArrayProp $caseAccounting 'notExecutionCases')) {
        $rows = @(Get-ArrayProp $aiFixture 'groups' | Where-Object { [string](Get-Prop $_ 'name') -eq [string]$name })
        if ($rows.Count -ne 1) {
            Add-Failure 'CASE_ACCOUNTING_MISMATCH' ("ai-v1: notExecutionCases group '{0}' missing" -f $name)
        }
        elseif ([bool](Get-Prop $rows[0] 'countAsCase')) {
            Add-Failure 'CASE_ACCOUNTING_MISMATCH' ("ai-v1: '{0}' must not count as an execution case" -f $name)
        }
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

    if ($id -eq 'G-049') {
        if ($coverage -cne 'partial') {
            Add-Failure 'G049_COVERAGE_MUST_BE_PARTIAL' ("G-049 is partial-only; got '{0}'" -f $coverage)
        }
        if ($manifestStatus -cne 'pending-v4-replay') {
            Add-Failure 'G049_STATUS_MUST_BE_PENDING_V4_REPLAY' ("G-049 historical status must remain pending-v4-replay; got '{0}'" -f $manifestStatus)
        }
        $partialScope = Get-Prop $requirement 'partialScope'
        if ($null -eq $partialScope) {
            Add-Failure 'G049_PARTIAL_SCOPE_MISSING' 'G-049 must declare partialScope'
        }
        else {
            if ([bool](Get-Prop $partialScope 'fullClaim')) {
                Add-Failure 'G049_FULL_CLAIM_FORBIDDEN' 'G-049 partialScope.fullClaim must be false'
            }
            if ([bool](Get-Prop $partialScope 'closed')) {
                Add-Failure 'G049_CLOSED_CLAIM_FORBIDDEN' 'G-049 partialScope.closed must be false'
            }
            if (@(Get-ArrayProp $partialScope 'remainingGaps').Count -lt 3) {
                Add-Failure 'G049_PARTIAL_REASON_MISSING' 'G-049 must list at least three remaining gaps'
            }
        }
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
