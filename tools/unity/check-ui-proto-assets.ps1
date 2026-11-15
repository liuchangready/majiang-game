#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ProjectRoot)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$assetRoot = Join-Path $projectRoot 'unity\Mahjong.Unity\Assets'
$tileRoot = Join-Path $assetRoot 'Art\UI\V4Prototype\Tiles2D5'
$builderPath = Join-Path $assetRoot 'Editor\V5ArtPrototypeBuilder.cs'
$prefabPath = Join-Path $assetRoot 'Prefabs\Prototypes\V5ArtPrototype.prefab'
$expected = [ordered]@{
    'tile_standing_front.png' = '95bab7f48b7b81746b28342d0cac4285'
    'tile_flat_front.png' = '97dd25ad6eb288c438e0903bfd85a3d5'
    'tile_flat_back.png' = '909d72c2ef8895740a330c9996b11e49'
    'tile_shadow_soft.png' = 'ff6d508606e8205468471d588199d802'
    'tile_glow.png' = '38ce08f501112344dae2ad5196b1ce03'
    'riichi_stick.png' = '5d9792b9dc8e3064187b4aaa78bd829e'
}

foreach ($path in @($builderPath, $prefabPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required prototype reference file is missing: $path" }
}
$builder = [IO.File]::ReadAllText($builderPath)
$prefab = [IO.File]::ReadAllText($prefabPath)
foreach ($name in $expected.Keys) {
    $imagePath = Join-Path $tileRoot $name
    $metaPath = "$imagePath.meta"
    if (-not (Test-Path -LiteralPath $imagePath -PathType Leaf)) { throw "Missing V5 art source: $imagePath" }
    if (-not (Test-Path -LiteralPath $metaPath -PathType Leaf)) { throw "Missing V5 art meta: $metaPath" }
    $bytes = [IO.File]::ReadAllBytes($imagePath)
    if ($bytes.Length -lt 24 -or $bytes[0] -ne 0x89 -or $bytes[1] -ne 0x50 -or $bytes[2] -ne 0x4e -or $bytes[3] -ne 0x47 -or $bytes[4] -ne 0x0d -or $bytes[5] -ne 0x0a -or $bytes[6] -ne 0x1a -or $bytes[7] -ne 0x0a) {
        throw "Invalid or empty PNG: $imagePath"
    }
    $meta = [IO.File]::ReadAllText($metaPath)
    $guidMatch = [regex]::Match($meta, '(?m)^guid:\s*([0-9a-f]{32})')
    if (-not $guidMatch.Success -or $guidMatch.Groups[1].Value -ne $expected[$name]) {
        throw "Unexpected asset GUID for $metaPath; expected $($expected[$name])."
    }
    if (-not $builder.Contains($name)) { throw "Builder does not request required V5 art: $name" }
    if (-not $prefab.Contains($expected[$name])) { throw "Prototype prefab does not reference required art GUID $($expected[$name]) ($name)." }
}
Write-Output 'UI_PROTO_ASSET_PREFLIGHT_PASS: six PNG files, six original GUID metas, builder references, and prefab GUID references verified.'
