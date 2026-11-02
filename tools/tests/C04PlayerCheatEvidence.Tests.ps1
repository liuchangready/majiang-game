[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$DotNetPath,
    [Parameter(Mandatory = $true)][string]$VerifierDll,
    [Parameter(Mandatory = $true)][string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$module = Join-Path $repo 'tools/unity/c04-player-cheats-evidence.psm1'
if (-not (Test-Path -LiteralPath $DotNetPath -PathType Leaf)) { throw "DOTNET_MISSING: $DotNetPath" }
if (-not (Test-Path -LiteralPath $VerifierDll -PathType Leaf)) { throw "VERIFIER_DLL_MISSING: $VerifierDll" }
if (Test-Path -LiteralPath $OutputRoot) { throw "REFUSE_OVERWRITE: $OutputRoot" }
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
Import-Module $module -Force

$head = '63206301378338d1360fce6d3a5783b121072051'
$artifact = Join-Path $OutputRoot 'player.bin'
[System.IO.File]::WriteAllBytes($artifact, [byte[]](1, 2, 3, 4))
$artifactHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $artifact).Hash
$package = Join-Path $OutputRoot 'package-manifest.json'
@{
    kind = 'c04-player-package-manifest'
    sourceHead = $head
    files = @(@{ path = 'player.bin'; bytes = 4; sha256 = $artifactHash })
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $package -Encoding UTF8

$failures = New-Object System.Collections.Generic.List[string]
$count = 0
$realRoot = Join-Path $OutputRoot 'localhost-authorized'
$captureOutput = @(& $DotNetPath $VerifierDll capture-localhost-authorized `
    --output-root $realRoot --source-head $head --package-manifest $package 2>&1)
$captureExit = $LASTEXITCODE
$count++
if ($captureExit -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $realRoot 'case-authorized.json') -PathType Leaf)) {
    $failures.Add("real LocalHost production-channel capture failed native=$captureExit output=$($captureOutput -join ' ')") | Out-Null
} else {
    $realPositive = Invoke-C04PlayerCheatEvidence -DotNetPath $DotNetPath -VerifierDll $VerifierDll `
        -Evidence (Join-Path $realRoot 'case-authorized.json') -Case authorized -SourceHead $head -PackageManifest $package
    $count++
    if ($realPositive.NativeExit -ne 0 -or $realPositive.Output -notmatch '"status":"PASS"' -or
        $realPositive.Output -notmatch '"physicalCount":136' -or $realPositive.Output -notmatch '"balance":99') {
        $failures.Add("actual LocalHost serialized command/receipt/publication/recovery originals failed verification: $($realPositive.Output)") | Out-Null
    }
}

foreach ($case in @('authorized', 'negative')) {
    $caseRoot = Join-Path $OutputRoot $case
    New-Item -ItemType Directory -Force -Path $caseRoot | Out-Null
    # Historical Strict-style fields deliberately claim PASS with plausible hashes and no
    # original bytes. The new CLI must stop on the absent raw index for both case kinds.
    $caseJson = Join-Path $caseRoot ('case-' + $case + '.json')
    @{
        schemaVersion = 1
        layer = 'WINDOWS_PLAYER'
        caseName = $case
        sourceHead = $head
        status = 'PASS'
        EvidenceSource = 'GAME'
        rawPayloadSha256 = ('A' * 64)
        assertions = @(@{ id = 'G3-05-FINAL'; pass = $true; expected = '136'; actual = '136' })
        requests = @(@{ requestId = ('B' * 32); status = 'APPLIED'; payloadSha256 = ('C' * 64); rawPayloadSha256 = ('D' * 64) })
        rawEvidenceIndex = 'raw-index.json'
        rawEvidenceIndexSha256 = ('A' * 64)
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $caseJson -Encoding UTF8
    $result = Invoke-C04PlayerCheatEvidence -DotNetPath $DotNetPath -VerifierDll $VerifierDll `
        -Evidence $caseJson -Case $case -SourceHead $head -PackageManifest $package
    $count++
    if ($result.NativeExit -eq 0 -or $result.Output -notmatch 'CAPTURE_FILE_MISSING') {
        $failures.Add("$case accepted evidence without raw originals or returned no structured missing-file reason") | Out-Null
    }
}

$escapeRoot = Join-Path $OutputRoot 'escape'
New-Item -ItemType Directory -Force -Path $escapeRoot | Out-Null
$escapeCase = Join-Path $escapeRoot 'case-authorized.json'
@{ sourceHead = $head; rawEvidenceIndex = '../escape.json'; rawEvidenceIndexSha256 = ('A' * 64) } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $escapeCase -Encoding UTF8
$escape = Invoke-C04PlayerCheatEvidence -DotNetPath $DotNetPath -VerifierDll $VerifierDll `
    -Evidence $escapeCase -Case authorized -SourceHead $head -PackageManifest $package
$count++
if ($escape.NativeExit -eq 0 -or $escape.Output -notmatch 'CAPTURE_PATH_TRAVERSAL') {
    $failures.Add('parent traversal was accepted or did not return its structural error') | Out-Null
}

# A syntactically valid caller-selected SHA-256 is not evidence. Keep the index
# metadata hash current so the CLI must reach and reject the raw member itself.
$hashRoot = Join-Path $OutputRoot 'arbitrary-raw-hash'
Copy-Item -LiteralPath $realRoot -Destination $hashRoot -Recurse
$indexPath = Join-Path $hashRoot 'raw/index.json'
$index = Get-Content -LiteralPath $indexPath -Raw | ConvertFrom-Json
$index.actions[0].command.sha256 = 'A' * 64
$indexJson = $index | ConvertTo-Json -Depth 20
[System.IO.File]::WriteAllText($indexPath, $indexJson, [System.Text.UTF8Encoding]::new($false))
$casePath = Join-Path $hashRoot 'case-authorized.json'
$metadata = Get-Content -LiteralPath $casePath -Raw | ConvertFrom-Json
$metadata.rawEvidenceIndexSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $indexPath).Hash
[System.IO.File]::WriteAllText($casePath, ($metadata | ConvertTo-Json -Depth 10), [System.Text.UTF8Encoding]::new($false))
$arbitraryHash = Invoke-C04PlayerCheatEvidence -DotNetPath $DotNetPath -VerifierDll $VerifierDll `
    -Evidence $casePath -Case authorized -SourceHead $head -PackageManifest $package
$count++
if ($arbitraryHash.NativeExit -eq 0 -or $arbitraryHash.Output -notmatch 'RAW_FILE_SHA_MISMATCH') {
    $failures.Add('arbitrary valid 64-hex raw hash was accepted or not reported') | Out-Null
}

# Missing one exact accepted private publication must fail even when all the
# surrounding historical summary claims and other raw members remain intact.
$missingRoot = Join-Path $OutputRoot 'missing-publication'
Copy-Item -LiteralPath $realRoot -Destination $missingRoot -Recurse
$indexPath = Join-Path $missingRoot 'raw/index.json'
$index = Get-Content -LiteralPath $indexPath -Raw | ConvertFrom-Json
$index.actions[0].acceptedPrivatePublication.path = 'raw/does-not-exist.pb'
$indexJson = $index | ConvertTo-Json -Depth 20
[System.IO.File]::WriteAllText($indexPath, $indexJson, [System.Text.UTF8Encoding]::new($false))
$casePath = Join-Path $missingRoot 'case-authorized.json'
$metadata = Get-Content -LiteralPath $casePath -Raw | ConvertFrom-Json
$metadata.rawEvidenceIndexSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $indexPath).Hash
[System.IO.File]::WriteAllText($casePath, ($metadata | ConvertTo-Json -Depth 10), [System.Text.UTF8Encoding]::new($false))
$missingPublication = Invoke-C04PlayerCheatEvidence -DotNetPath $DotNetPath -VerifierDll $VerifierDll `
    -Evidence $casePath -Case authorized -SourceHead $head -PackageManifest $package
$count++
if ($missingPublication.NativeExit -eq 0 -or $missingPublication.Output -notmatch 'CAPTURE_FILE_MISSING') {
    $failures.Add('missing exact accepted private publication was accepted or not reported') | Out-Null
}

$badPackage = Join-Path $OutputRoot 'wrong-package.json'
@{ kind = 'c04-player-package-manifest'; sourceHead = ('F' * 40); files = @(@{ path = 'player.bin'; bytes = 4; sha256 = $artifactHash }) } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $badPackage -Encoding UTF8
$badPackageResult = Invoke-C04PlayerCheatEvidence -DotNetPath $DotNetPath -VerifierDll $VerifierDll `
    -Evidence (Join-Path $OutputRoot 'authorized/case-authorized.json') -Case authorized -SourceHead $head -PackageManifest $badPackage
$count++
if ($badPackageResult.NativeExit -eq 0 -or $badPackageResult.Output -notmatch 'PACKAGE_SOURCE_HEAD_MISMATCH') {
    $failures.Add('wrong source/package binding was accepted or not reported') | Out-Null
}

Write-Host "C04PlayerCheatEvidence.Tests total=$count passed=$($count - $failures.Count) failed=$($failures.Count) layer=TOOL_FIXTURE"
foreach ($failure in $failures) { Write-Host "FAIL $failure" }
if ($failures.Count -gt 0) { exit 1 }
exit 0
