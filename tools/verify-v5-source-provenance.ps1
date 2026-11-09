#requires -Version 5.1
param([string]$RepoRoot = ".", [string]$FixtureDir = "", [switch]$VerifyGitPins)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $RepoRoot).Path
$manifestPath = Join-Path $root 'tests/v5-golden/source-provenance-v1.json'
$manifestHash = 'fda67b11efc3b00275d992b6b8c8b01f6ea926f75c8e155243fe73d5b66d10c9'
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
    $key = $fixture + '|' + $path
    if ($sourceMap.ContainsKey($key)) { Fail "duplicate historical source pin: $key" }
    $sourceMap[$key] = $entry
}

foreach ($fixture in $manifest.fixtures) {
    $path = [string]$fixture.path
    $file = Join-Path $root ($path.Replace('/', [IO.Path]::DirectorySeparatorChar))
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { Fail "golden fixture missing: $path" }
    $raw = [IO.File]::ReadAllBytes($file)
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -cne [string]$fixture.sha256) { Fail "golden fixture bytes differ from frozen pin: $path" }
    $text = [Text.Encoding]::UTF8.GetString($raw)
    try { $null = $text | ConvertFrom-Json } catch { Fail "golden fixture JSON invalid: $path" }
    $seen = @{}
    foreach ($match in [regex]::Matches($text, 'engine/src/[A-Za-z0-9_./-]+\.kt')) { $seen[$match.Value] = $true }
    $pinned = @($manifest.sources | Where-Object { $_.fixture -ceq $path } | ForEach-Object { [string]$_.path })
    $seenText = @($seen.Keys | Sort-Object) -join "`n"
    $pinnedText = @($pinned | Sort-Object) -join "`n"
    if ($seenText -cne $pinnedText) { Fail "historical source paths differ from frozen pins: $path" }
}

if ($VerifyGitPins) {
    foreach ($entry in $manifest.sources) {
        $resolved = (& git -C $root rev-parse --verify --quiet ($entry.commit + ':' + $entry.path) 2>$null | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or $resolved -cne [string]$entry.gitBlobSha1) { Fail "historical Git blob mismatch: $($entry.path) at $($entry.commit)" }
    }
}
Write-Output ("PASS: {0} frozen golden fixtures; {1} historical source blob pins; Git history checked={2}." -f $manifest.fixtures.Count, $manifest.sources.Count, [bool]$VerifyGitPins)
exit 0
