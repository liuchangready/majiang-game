#requires -Version 5.1
param([string]$RepoRoot = ".", [string]$FixtureDir = "", [switch]$VerifyGitPins)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $RepoRoot).Path
$manifestPath = Join-Path $root 'tests/v5-golden/source-provenance-v1.json'
$manifestHash = 'b6b36fd35b76be74846093305e17ff357d849d9861a1c876ea9069c6968eaf67'
function Fail([string]$message) { Write-Output "FAIL: $message"; exit 1 }
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { Fail 'source-pin manifest missing' }
$bytes = [IO.File]::ReadAllBytes($manifestPath)
$actualHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -cne $manifestHash) { Fail "source-pin manifest SHA-256 mismatch: $actualHash" }
try { $manifest = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json } catch { Fail "source-pin manifest is invalid JSON: $($_.Exception.Message)" }
if ($manifest.schema -cne 'v5-frozen-v4-source-pins-v1') { Fail 'source-pin schema mismatch' }

$goldenDir = Join-Path $root 'tests/v5-golden'
$expectedFixtures = @(Get-ChildItem -LiteralPath $goldenDir -Filter '*.json' -File | Where-Object { $_.Name -notin @('catalog-v1.json','source-provenance-v1.json') } | ForEach-Object { 'tests/v5-golden/' + $_.Name } | Sort-Object)
$pinnedFixtures = @($manifest.fixtures | ForEach-Object { [string]$_.path } | Sort-Object)
if (($expectedFixtures -join "`n") -cne ($pinnedFixtures -join "`n")) { Fail 'fixture inventory differs from frozen pins' }

$sourceMap = @{}
foreach ($entry in $manifest.sources) {
    $fixture = [string]$entry.fixture
    $path = [string]$entry.path
    if ($fixture -notin $pinnedFixtures) { Fail "source pin names unknown fixture: $fixture" }
    if ($path -notmatch '^engine/src/[A-Za-z0-9_./-]+\.kt$' -or $path.Contains('..')) { Fail "non-canonical historical path: $path" }
    if ([string]$entry.commit -notmatch '^[0-9a-f]{40}$' -or [string]$entry.gitBlobSha1 -notmatch '^[0-9a-f]{40}$') { Fail "invalid historical commit/blob pin: $path" }
    if ($entry.PSObject.Properties.Name -contains 'sha256LfNormalized' -and [string]$entry.sha256LfNormalized -notmatch '^[0-9a-f]{64}$') { Fail "invalid historical SHA-256 pin: $path" }
    $key = $fixture + '|' + $path
    if ($sourceMap.ContainsKey($key)) { Fail "duplicate historical source pin: $key" }
    $sourceMap[$key] = $entry
}

function Get-Sha256Bytes([byte[]]$value) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($value))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}
function Add-HistoricalHashes($value, [hashtable]$map) {
    if ($null -eq $value) { return }
    if ($value -is [System.Array]) { foreach ($item in $value) { Add-HistoricalHashes $item $map }; return }
    if ($value -is [pscustomobject]) {
        $sourcePath = $null
        if ($value.PSObject.Properties.Name -contains 'file') { $sourcePath = [string]$value.file }
        elseif ($value.PSObject.Properties.Name -contains 'path') { $sourcePath = [string]$value.path }
        $sourceHash = $null
        if ($value.PSObject.Properties.Name -contains 'sha256LfNormalized') { $sourceHash = [string]$value.sha256LfNormalized }
        elseif ($value.PSObject.Properties.Name -contains 'sha256') { $sourceHash = [string]$value.sha256 }
        if ($sourcePath -like 'engine/src/*.kt' -and $sourceHash -match '^[0-9a-f]{64}$') { $map[$sourcePath] = $sourceHash }
        foreach ($property in $value.PSObject.Properties) { Add-HistoricalHashes $property.Value $map }
    }
}

foreach ($fixture in $manifest.fixtures) {
    $path = [string]$fixture.path
    $file = Join-Path $root ($path.Replace('/', [IO.Path]::DirectorySeparatorChar))
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { Fail "golden fixture missing: $path" }
    $raw = [IO.File]::ReadAllBytes($file)
    $text = [Text.UTF8Encoding]::new($false, $true).GetString($raw)
    $canonical = [Text.UTF8Encoding]::new($false).GetBytes($text.Replace("`r`n", "`n"))
    $hash = Get-Sha256Bytes $canonical
    if ($hash -cne [string]$fixture.sha256) { Fail "golden fixture bytes differ from frozen pin: $path" }
    try { $manifestFixture = $text | ConvertFrom-Json } catch { Fail "golden fixture JSON invalid: $path" }
    $seen = @{}
    foreach ($match in [regex]::Matches($text, 'engine/src/[A-Za-z0-9_./-]+\.kt')) { $seen[$match.Value] = $true }
    $pinned = @($manifest.sources | Where-Object { $_.fixture -ceq $path } | ForEach-Object { [string]$_.path })
    $seenText = @($seen.Keys | Sort-Object) -join "`n"
    $pinnedText = @($pinned | Sort-Object) -join "`n"
    if ($seenText -cne $pinnedText) { Fail "historical source paths differ from frozen pins: $path" }
    if ($fixture.PSObject.Properties.Name -contains 'generator' -and [string]$fixture.generator -cne [string]$manifestFixture.generator) { Fail "generator path differs from frozen pin: $path" }
    if ($fixture.PSObject.Properties.Name -contains 'generatedByCommand') {
        $acceptedCommands = @($fixture.acceptedGeneratorCommands | ForEach-Object { [string]$_ })
        if ([string]$manifestFixture.generatedByCommand -notin $acceptedCommands) { Fail "exporter command differs from frozen pin: $path" }
    }
    $observedHashes = @{}
    Add-HistoricalHashes $manifestFixture $observedHashes
    if ($manifestFixture.generator -and $manifestFixture.exporterSourceSha256) { $observedHashes[[string]$manifestFixture.generator] = [string]$manifestFixture.exporterSourceSha256 }
    foreach ($sourcePin in $manifest.sources | Where-Object { $_.fixture -ceq $path -and $_.PSObject.Properties.Name -contains 'sha256LfNormalized' }) {
        if (-not $observedHashes.ContainsKey([string]$sourcePin.path) -and -not [bool]$fixture.sourceHashesRequired) { continue }
        if (-not $observedHashes.ContainsKey([string]$sourcePin.path) -or $observedHashes[[string]$sourcePin.path] -cne [string]$sourcePin.sha256LfNormalized) {
            Fail "historical source hash differs from frozen pin: $($sourcePin.path)"
        }
        if ($sourcePin.PSObject.Properties.Name -contains 'sourceMetadata') {
            $actualMetadata = @($manifestFixture.sourceTree | Where-Object { $_.path -ceq $sourcePin.path })
            if ($actualMetadata.Count -ne 1) {
                Fail "historical source metadata is missing or ambiguous: $($sourcePin.path)"
                continue
            }
            foreach ($field in @('role','sha256Raw','bytesRaw','bytesLfNormalized','lineEnding','rawEqualsLfNormalized')) {
                if ([string]$actualMetadata[0].$field -cne [string]$sourcePin.sourceMetadata.$field) {
                    Fail "historical source metadata differs from frozen pin ($field): $($sourcePin.path)"
                }
            }
        }
    }
}

if ($VerifyGitPins) {
    foreach ($entry in $manifest.sources) {
        $resolved = (& git -C $root rev-parse --verify --quiet ($entry.commit + ':' + $entry.path) 2>$null | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or $resolved -cne [string]$entry.gitBlobSha1) { Fail "historical Git blob mismatch: $($entry.path) at $($entry.commit)" }
    }
}
Write-Output ("PASS: {0} frozen golden fixtures; {1} historical source blob pins; Git history checked={2}." -f $manifest.fixtures.Count, $manifest.sources.Count, [bool]$VerifyGitPins)
exit 0
