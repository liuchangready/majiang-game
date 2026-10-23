# validate-implementation-trace.ps1
# Read-only machine validator for doc/*/implementation-v1.json (schema v5-implementation-trace-v1).
# Compatible with Windows PowerShell 5.1. No writes, no installs, no global changes.
# Exit 0 = all checks passed. Exit 1 = at least one check failed. Exit 2 = usage/environment error.
#
# Checks (fail -> exit 1):
#   1. JSON parses; schema field matches v5-implementation-trace-v1.
#   2. baselineCommit is a 40-hex sha and exists as a commit object in the repo (read-only git).
#   3. Rows are compared against REAL tracked production sources via git ls-files:
#      only "<module>/src/main/**/*.kt" counts (src/test, generated code, non-kt excluded).
#      Missing row / extra row / duplicate (module,source) -> exit 1.
#   4. Controlled enums: module in {engine,protocol,server,client,presentation,lwjgl3,android};
#      status in the 5-value vocabulary; responsibleTask matches ^(P[0-9]+\.[0-9]+[a-z]?|G[0-5])$;
#      dependency entries match the same pattern.
#   5. Anchor hygiene: v5Anchors/testAnchors paths must exist and actually contain the declared
#      symbol as a type declaration (class/struct/interface/enum/record/object). Acceptance anchors
#      must point at an existing doc and a 7..40 hex commit. Golden sample paths must exist.
#   6. Status-specific responsibility: accepted-component rows require v5Anchors AND
#      acceptanceAnchors; every other status requires a non-empty gapSummary AND a valid
#      responsibleTask. (No "pending without a responsible task".)
#   7. Self-consistency: denominator.productionSources == row count == enumerated source count;
#      stats.byStatus / stats.byModule recomputed from rows must equal the declared values.
#      (Deliberately NOT hardcoded to all-pass: numbers must match the real repo and the rows.)

param(
    [string]$RepoRoot = ".",
    [string]$JsonPath = ""
)

# Default JSON path contains non-ASCII directory names; PS 5.1 reads .ps1 as ANSI without a BOM,
# so the default is resolved by search instead of a literal to keep this script pure ASCII.
if ([string]::IsNullOrEmpty($JsonPath)) {
    $candidates = @(Get-ChildItem -LiteralPath (Join-Path $RepoRoot "doc") -Recurse -Filter "implementation-v1.json" -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
    if ($candidates.Count -eq 0) {
        Write-Output "RESULT=ERROR"
        Write-Output "REASON=implementation-v1.json-not-found-under-doc"
        exit 2
    }
    if ($candidates.Count -gt 1) {
        Write-Output "RESULT=ERROR"
        Write-Output "REASON=multiple-implementation-v1-json-found"
        exit 2
    }
    $JsonPath = $candidates[0].Substring((Resolve-Path -LiteralPath $RepoRoot).ProviderPath.Length + 1)
}
$absJson = Join-Path $RepoRoot $JsonPath

$ErrorActionPreference = "Stop"
$failures = New-Object System.Collections.Generic.List[string]
$notesOut = New-Object System.Collections.Generic.List[string]

function Fail([string]$msg) { $script:failures.Add($msg) | Out-Null }
function Note([string]$msg) { $script:notesOut.Add($msg) | Out-Null }

# --- resolve repo ---
try {
    $RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).ProviderPath
} catch {
    Write-Output "RESULT=ERROR"
    Write-Output "REASON=repo-root-not-found $RepoRoot"
    exit 2
}

function Read-Utf8([string]$p) {
    return [System.IO.File]::ReadAllText($p, [System.Text.Encoding]::UTF8)
}

# --- 1. JSON parse ---
if (-not (Test-Path -LiteralPath $absJson)) {
    Write-Output "RESULT=FAIL"
    Write-Output "REASON=json-file-not-found $JsonPath"
    exit 1
}
try {
    $doc = Read-Utf8 $absJson | ConvertFrom-Json
} catch {
    Write-Output "RESULT=FAIL"
    Write-Output "REASON=bad-json $($_.Exception.Message)"
    exit 1
}
if ($doc.schema -ne "v5-implementation-trace-v1") {
    Fail ("bad-schema expected=v5-implementation-trace-v1 actual=" + $doc.schema)
}

# --- 2. baseline commit exists (read-only) ---
$bc = $doc.baselineCommit
if (-not ($bc -match "^[0-9a-f]{40}$")) {
    Fail ("bad-baseline-commit format: " + $bc)
} else {
    $null = & git -C $RepoRoot rev-parse --verify --quiet ($bc + "^{commit}") 2>$null
    if ($LASTEXITCODE -ne 0) { Fail ("baseline-commit-not-in-repo: " + $bc) }
}

# --- 3. enumerate real tracked production sources ---
# Plan-path convention per module: row.source = tracked path minus the module's package root.
# Real package layouts differ per module (server/client/presentation carry an extra package
# segment; presentation also has a root-level MahjongGame.kt under com/majiang), so each module
# lists candidate prefixes longest-first. Anything that maps to no row under any candidate fails.
$pkgPrefix = @{
    "engine"       = @("engine/src/main/kotlin/com/majiang/");
    "protocol"     = @("protocol/src/main/kotlin/com/majiang/");
    "server"       = @("server/src/main/kotlin/com/majiang/server/", "server/src/main/kotlin/com/majiang/");
    "client"       = @("client/src/main/kotlin/com/majiang/client/", "client/src/main/kotlin/com/majiang/");
    "presentation" = @("presentation/src/main/kotlin/com/majiang/presentation/", "presentation/src/main/kotlin/com/majiang/");
    "lwjgl3"       = @("lwjgl3/src/main/kotlin/com/majiang/");
    "android"      = @("android/src/main/kotlin/com/majiang/")
}
$modules = @("engine","protocol","server","client","presentation","lwjgl3","android")
$allowedStatus = @("accepted-component","partial","pending","replaced-by-v5-contract","platform-blocked")

$ErrorActionPreference = "Continue"
$lines = & git -C $RepoRoot ls-files -- "*.kt" 2>$null
$lsExit = $LASTEXITCODE
$ErrorActionPreference = "Stop"
if ($lsExit -ne 0) { Write-Output "RESULT=FAIL"; Write-Output "REASON=git-ls-files-failed"; exit 1 }
$realSet = @{}
$realCount = 0
foreach ($l in $lines) {
    if ($l -notmatch "/src/main/") { continue }        # main sources only
    if ($l -match "/src/test/")   { continue }         # never tests
    if ($l -notmatch "\.kt$")     { continue }         # kotlin only
    $hit = $false
    foreach ($m in $modules) {
        $modulePrefix = $m + "/src/main/"
        if (-not $l.StartsWith($modulePrefix)) { continue }
        foreach ($c in $pkgPrefix[$m]) {
            if ($l.StartsWith($c) -and $l.Length -gt $c.Length) {
                $rel = $l.Substring($c.Length)
                if ([string]::IsNullOrEmpty($rel) -or $rel -notmatch "\.kt$") { continue }
                $key = $m + "|" + $rel
                if ($realSet.ContainsKey($key)) { Fail ("enumerated-duplicate: " + $key) }
                $realSet[$key] = $true
                $realCount++
                $hit = $true
                break
            }
        }
        if ($hit) { break }
    }
    if (-not $hit) { Fail ("main-kt-maps-to-no-plan-source: " + $l) }
}

# --- index rows ---
$rowKeys = @{}
$rows = @($doc.rows)
if ($null -eq $doc.rows) { Fail "rows-array-missing" ; $rows = @() }
foreach ($r in $rows) {
    $k = $r.module + "|" + $r.source
    if ($rowKeys.ContainsKey($k)) { Fail ("duplicate-source-row: " + $k) } else { $rowKeys[$k] = $r }
}

# --- 3b. set comparison ---
foreach ($k in $realSet.Keys) {
    if (-not $rowKeys.ContainsKey($k)) { Fail ("missing-row-for-tracked-source: " + $k) }
}
foreach ($k in $rowKeys.Keys) {
    if (-not $realSet.ContainsKey($k)) {
        if ($k -match "\|") { Fail ("row-not-a-tracked-production-source: " + $k) }
        else { Fail ("row-malformed-key: " + $k) }
    }
}

# --- hygiene of row.source itself ---
foreach ($r in $rows) {
    $s = $r.source
    if ([string]::IsNullOrEmpty($s)) { Fail ("empty-source in module=" + $r.module); continue }
    if ($s -notmatch "\.kt$")           { Fail ("source-not-kt: " + $s) }
    if ($s -match "(^/|[\\:]|\.\.)")    { Fail ("source-not-module-relative: " + $s) }
    if ($s -match "src/test|/build/|generated") { Fail ("source-looks-like-test-or-generated: " + $s) }
}

# --- 4/5/6. per-row enum, anchors, responsibility ---
$taskPattern = "^(P[0-9]+\.[0-9]+[a-z]?|G[0-5])$"

function Test-AnchorSymbol([string]$file, [string]$symbol) {
    $text = Read-Utf8 $file
    $rx = [regex]::Escape($symbol)
    $pattern = "(class|struct|interface|enum|record|object)[^=\r\n{]{0,160}\b" + $rx + "\b"
    return [regex]::IsMatch($text, $pattern)
}

$statusCount = @{}
$moduleCount = @{}
foreach ($r in $rows) {
    $tag = "[" + $r.id + " " + $r.module + "/" + $r.source + "]"

    if ($modules -notcontains $r.module) { Fail ($tag + " unknown-module: " + $r.module) }
    if ($allowedStatus -notcontains $r.status) { Fail ($tag + " unknown-status: " + $r.status) }
    if (-not $r.responsibleTask) { Fail ($tag + " missing-responsible-task") }
    elseif ($r.responsibleTask -notmatch $taskPattern) { Fail ($tag + " bad-responsible-task: " + $r.responsibleTask) }
    foreach ($d in @($r.dependencies)) {
        if ($d -and ($d -notmatch $taskPattern)) { Fail ($tag + " bad-dependency: " + $d) }
    }

    $statusCount[$r.status] = 1 + ($statusCount[$r.status] | ForEach-Object { if ($_) { $_ } else { 0 } })
    $moduleCount[$r.module] = 1 + ($moduleCount[$r.module] | ForEach-Object { if ($_) { $_ } else { 0 } })

    # anchors: path exists + symbol declared
    foreach ($a in @($r.v5Anchors)) {
        if (-not $a) { continue }
        $ap = Join-Path $RepoRoot ($a.path -replace "/", "\")
        if (-not (Test-Path -LiteralPath $ap -PathType Leaf)) { Fail ($tag + " v5-anchor-path-missing: " + $a.path); continue }
        if ($a.symbol -and -not (Test-AnchorSymbol $ap $a.symbol)) { Fail ($tag + " v5-anchor-symbol-not-declared: " + $a.symbol + " in " + $a.path) }
    }
    foreach ($a in @($r.testAnchors)) {
        if (-not $a) { continue }
        $ap = Join-Path $RepoRoot ($a.path -replace "/", "\")
        if (-not (Test-Path -LiteralPath $ap -PathType Leaf)) { Fail ($tag + " test-anchor-path-missing: " + $a.path); continue }
        if ($a.symbol -and -not (Test-AnchorSymbol $ap $a.symbol)) { Fail ($tag + " test-anchor-symbol-not-declared: " + $a.symbol + " in " + $a.path) }
    }
    foreach ($a in @($r.acceptanceAnchors)) {
        if (-not $a) { continue }
        $ap = Join-Path $RepoRoot ($a.doc -replace "/", "\")
        if (-not (Test-Path -LiteralPath $ap -PathType Leaf)) { Fail ($tag + " acceptance-doc-missing: " + $a.doc) }
        if (-not ($a.commit -match "^[0-9a-f]{7,40}$")) { Fail ($tag + " acceptance-commit-bad: " + $a.commit) }
    }
    foreach ($g in @($r.goldenSamples)) {
        if (-not $g) { continue }
        $gp = Join-Path $RepoRoot ($g -replace "/", "\")
        if (-not (Test-Path -LiteralPath $gp -PathType Leaf)) { Fail ($tag + " golden-sample-missing: " + $g) }
    }
    if ([string]::IsNullOrEmpty($r.v4Symbol)) { Fail ($tag + " missing-v4-symbol") }

    # status-specific responsibility rules
    if ($r.status -eq "accepted-component") {
        if (@($r.v5Anchors).Count -eq 0 -or -not @($r.v5Anchors)[0]) { Fail ($tag + " accepted-component-without-v5-anchor") }
        if (@($r.acceptanceAnchors).Count -eq 0 -or -not @($r.acceptanceAnchors)[0]) { Fail ($tag + " accepted-component-without-acceptance-anchor") }
    } else {
        if ([string]::IsNullOrWhiteSpace($r.gapSummary)) { Fail ($tag + " status-needs-gap-summary: " + $r.status) }
        if (-not $r.responsibleTask) { Fail ($tag + " status-needs-responsible-task: " + $r.status) }
    }
}

# --- 7. denominator + stats self-consistency ---
if ($doc.denominator.productionSources -ne $rows.Count) {
    Fail ("denominator-mismatch declared=" + $doc.denominator.productionSources + " rows=" + $rows.Count)
}
if ($rows.Count -ne $realCount) {
    Fail ("coverage-mismatch rows=" + $rows.Count + " enumerated-tracked=" + $realCount)
}
foreach ($st in $allowedStatus) {
    $declared = 0
    if ($doc.stats.byStatus.PSObject.Properties[$st]) { $declared = $doc.stats.byStatus.$st }
    $computed = 0
    if ($statusCount[$st]) { $computed = $statusCount[$st] }
    if ($declared -ne $computed) { Fail ("stats-by-status-mismatch " + $st + " declared=" + $declared + " computed=" + $computed) }
}
foreach ($m in $modules) {
    $declared = 0
    if ($doc.stats.byModule.PSObject.Properties[$m]) { $declared = $doc.stats.byModule.$m }
    $computed = 0
    if ($moduleCount[$m]) { $computed = $moduleCount[$m] }
    if ($declared -ne $computed) { Fail ("stats-by-module-mismatch " + $m + " declared=" + $declared + " computed=" + $computed) }
}

# --- golden samples header list must exist too ---
foreach ($g in @($doc.goldenSamplesInBaseline)) {
    if (-not $g) { continue }
    $gp = Join-Path $RepoRoot ($g -replace "/", "\")
    if (-not (Test-Path -LiteralPath $gp -PathType Leaf)) { Fail ("header-golden-sample-missing: " + $g) }
}

# --- report ---
Note ("tracked-main-kt=" + $realCount)
Note ("rows=" + $rows.Count)
$statLine = ""
foreach ($st in $allowedStatus) {
    $c = 0; if ($statusCount[$st]) { $c = $statusCount[$st] }
    $statLine += $st + "=" + $c + " "
}
Note ("by-status " + $statLine.Trim())
$modLine = ""
foreach ($m in $modules) {
    $c = 0; if ($moduleCount[$m]) { $c = $moduleCount[$m] }
    $modLine += $m + "=" + $c + " "
}
Note ("by-module " + $modLine.Trim())
Note ("baseline=" + $bc)

foreach ($n in $notesOut) { Write-Output ("INFO " + $n) }
if ($failures.Count -gt 0) {
    Write-Output ("RESULT=FAIL failures=" + $failures.Count)
    foreach ($f in $failures) { Write-Output ("FAIL " + $f) }
    exit 1
}
Write-Output "RESULT=PASS"
exit 0
