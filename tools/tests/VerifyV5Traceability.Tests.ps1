# VerifyV5Traceability.Tests.ps1
# Reproducible self-test for tools/traceability/validate-implementation-trace.ps1.
#
# Design:
#   - Positive case: the validator must PASS the untouched real repository
#     (RESULT=PASS, exit 0, tracked-main-kt=266, rows=266).
#   - Negative cases: mutate an in-memory copy of the real doc implementation-v1.json,
#     write it to a throwaway TEMP directory, and require the validator to exit 1 with
#     the expected failure marker(s). The repository itself is never modified.
#   - The script is ASCII-only and PowerShell 5.1 compatible, lives IN git, so a clean
#     checkout can re-run it (the old artifacts-only self-test could not).
#   - Cases 1..10 keep the original round-1 negative categories; cases 11..20 cover the
#     five independently injected negatives from the 2026-10-24 external review plus five
#     further hardening categories (gate cycle, path escape, anchor category, non-ancestor
#     acceptance commit, acceptance doc missing at commit). Case 21 pins the controlled
#     V5 module vocabulary.
#
# Exit 0 = every case behaved as expected. Exit 1 = at least one case broke.

param(
    [string]$RepoRoot = "",
    [string]$OutLog = ""
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrEmpty($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $scriptDir "..\..")).Path
}
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).ProviderPath

$validator = Join-Path $RepoRoot "tools\traceability\validate-implementation-trace.ps1"
if (-not (Test-Path -LiteralPath $validator)) {
    Write-Output "SELFTEST=FAIL validator-not-found $validator"
    exit 1
}

# Locate the real matrix exactly like the validator does (the doc directory name is
# non-ASCII, so no literal path appears in this ASCII-only script).
$matrixCandidates = @(Get-ChildItem -LiteralPath (Join-Path $RepoRoot "doc") -Recurse -Filter "implementation-v1.json" -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
if ($matrixCandidates.Count -ne 1) {
    Write-Output "SELFTEST=FAIL matrix-not-unique count=$($matrixCandidates.Count)"
    exit 1
}
$matrixPath = $matrixCandidates[0]

$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("v5trace-selftest-" + [System.IO.Path]::GetRandomFileName())
New-Item -ItemType Directory -Force -Path $tempDir | Out-Null

$log = New-Object System.Collections.Generic.List[string]
function Log([string]$s) { $script:log.Add($s) | Out-Null }

function Write-Utf8NoBom([string]$p, [string]$s) {
    [System.IO.File]::WriteAllText($p, $s, (New-Object System.Text.UTF8Encoding($false)))
}

function Invoke-ValidatorJson([string]$jsonFile) {
    $ErrorActionPreference = "Continue"
    $out = & powershell -NoProfile -ExecutionPolicy Bypass -File $script:validator -RepoRoot $script:RepoRoot -JsonPath $jsonFile 2>&1
    $code = $LASTEXITCODE
    $ErrorActionPreference = "Stop"
    $text = ($out | ForEach-Object { $_.ToString() }) -join "`n"
    return @{ code = $code; text = $text }
}

function Invoke-ValidatorRepo {
    $ErrorActionPreference = "Continue"
    $out = & powershell -NoProfile -ExecutionPolicy Bypass -File $script:validator -RepoRoot $script:RepoRoot 2>&1
    $code = $LASTEXITCODE
    $ErrorActionPreference = "Stop"
    $text = ($out | ForEach-Object { $_.ToString() }) -join "`n"
    return @{ code = $code; text = $text }
}

$script:caseNo = 0
$script:results = @()

function Run-Case([string]$name, [string]$jsonText, [int]$expectExit, [string[]]$expectMarkers) {
    $script:caseNo++
    $file = Join-Path $script:tempDir ("case-" + $name + ".json")
    Write-Utf8NoBom $file $jsonText
    $r = Invoke-ValidatorJson $file
    $ok = ($r.code -eq $expectExit)
    Log ("=== CASE " + $name + " exit=" + $r.code + " (expect " + $expectExit + ")")
    foreach ($line in ($r.text -split "`n")) { if ($line -match "^(RESULT|REASON)") { Log ("  " + $line.Trim()) } }
    if ($ok -and $expectMarkers.Count -gt 0) {
        foreach ($m in $expectMarkers) {
            if ($r.text.IndexOf($m) -lt 0) { $ok = $false; Log ("  MISSING-MARKER " + $m) }
        }
    }
    if (-not $ok) {
        foreach ($line in ($r.text -split "`n")) {
            if ($line -match "^FAIL") { Log ("  FIRST-FAIL " + $line.Trim()); break }
        }
    }
    if ($ok) { Log ("  CASE-OK " + $name) } else { Log ("  CASE-BROKEN " + $name) }
    $script:results += $ok
    return $ok
}

function New-Matrix {
    return ([System.IO.File]::ReadAllText($script:matrixPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json)
}
function Serialize([object]$m) {
    return $m | ConvertTo-Json -Depth 30
}
function First-RowWithAnchors($m) {
    return ($m.rows | Where-Object { @($_.v5Anchors).Count -gt 0 } | Select-Object -First 1)
}
function First-AcceptedRow($m) {
    return ($m.rows | Where-Object { $_.status -eq "accepted-component" } | Select-Object -First 1)
}
function First-PendingRow($m) {
    return ($m.rows | Where-Object { $_.status -eq "pending" } | Select-Object -First 1)
}

Log ("REPO=" + $RepoRoot)
Log ("MATRIX=" + $matrixPath)
Log ("TEMP=" + $tempDir)

# --- positive: real repository must pass ---
$pos = Invoke-ValidatorRepo
Log ("=== CASE pos-real-repo exit=" + $pos.code + " (expect 0)")
foreach ($line in ($pos.text -split "`n")) { if ($line -match "^(RESULT|INFO)") { Log ("  " + $line.Trim()) } }
$posOk = ($pos.code -eq 0 -and $pos.text.IndexOf("RESULT=PASS") -ge 0 -and $pos.text.IndexOf("tracked-main-kt=266") -ge 0 -and $pos.text.IndexOf("rows=266") -ge 0)
if ($posOk) { Log "  CASE-OK pos-real-repo" } else { Log "  CASE-BROKEN pos-real-repo" }
$script:results += $posOk

# --- n01: drop one row -> tracked source left without a row ---
$m = New-Matrix
$m.rows = @($m.rows | Select-Object -Skip 1)
$null = Run-Case "n01-missing-row" (Serialize $m) 1 @("missing-row-for-tracked-source", "coverage-mismatch")

# --- n02: duplicate the same source row ---
$m = New-Matrix
$arr = @($m.rows)
$m.rows = (@($arr[0]) + $arr)
$null = Run-Case "n02-duplicate-source" (Serialize $m) 1 @("duplicate-source-row")

# --- n03: unknown status value ---
$m = New-Matrix
$m.rows[0].status = "done"
$null = Run-Case "n03-unknown-status" (Serialize $m) 1 @("unknown-status")

# --- n04: v5 anchor path that git does not track and the worktree does not have ---
$m = New-Matrix
$t = First-RowWithAnchors $m
$t.v5Anchors[0].path = "dotnet/src/Nope.cs"
$null = Run-Case "n04-fake-anchor-path" (Serialize $m) 1 @("v5-anchor-not-tracked", "v5-anchor-path-missing")

# --- n05: symbol exists nowhere as a declaration in the anchored file ---
$m = New-Matrix
$t = First-RowWithAnchors $m
$t.v5Anchors[0].symbol = "NoSuchSymbol98765"
$null = Run-Case "n05-fake-anchor-symbol" (Serialize $m) 1 @("v5-anchor-symbol-not-declared")

# --- n06: pending row without gap summary and without a responsible task ---
$m = New-Matrix
$t = First-PendingRow $m
$t.gapSummary = ""
$t.responsibleTask = ""
$null = Run-Case "n06-pending-no-responsibility" (Serialize $m) 1 @("status-needs-gap-summary", "missing-responsible-task")

# --- n07: broken json ---
$null = Run-Case "n07-bad-json" "{ this is not json" 1 @("bad-json")

# --- n08: stats lie (anti all-pass trap) ---
$m = New-Matrix
$m.stats.byStatus.partial = 1 + $m.stats.byStatus.partial
$null = Run-Case "n08-stats-mismatch" (Serialize $m) 1 @("stats-by-status-mismatch")

# --- n09: extra row for a source git does not track ---
$m = New-Matrix
$extra = $m.rows[0].PSObject.Copy()
$extra.id = "ENG-999"
$extra.source = "ghost/Ghost.kt"
$m.rows = (@($m.rows) + @($extra))
$m.denominator.productionSources = 1 + $m.denominator.productionSources
$m.stats.byModule.engine = 1 + $m.stats.byModule.engine
$null = Run-Case "n09-untracked-extra-row" (Serialize $m) 1 @("row-not-a-tracked-production-source", "coverage-mismatch")

# --- n10: accepted-component without acceptance evidence ---
$m = New-Matrix
$t = First-AcceptedRow $m
$t.PSObject.Properties["acceptanceAnchors"].Value = @()
$null = Run-Case "n10-accepted-no-acceptance" (Serialize $m) 1 @("accepted-component-without-acceptance-anchor")

# --- n11 [review 1]: v4Symbol that does not exist in the row's own V4 source ---
$m = New-Matrix
$m.rows[0].v4Symbol = "CodexNonexistentType987654"
$null = Run-Case "n11-fake-v4-symbol" (Serialize $m) 1 @("v4-symbol-not-declared")

# --- n12 [review 2]: acceptance commit that does not exist in the repo ---
$m = New-Matrix
$t = First-AcceptedRow $m
$t.acceptanceAnchors[0].commit = "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef"
$null = Run-Case "n12-nonexistent-acceptance-commit" (Serialize $m) 1 @("acceptance-commit-not-in-repo")

# --- n13 [review 3]: v5 anchor without the required symbol field ---
$m = New-Matrix
$t = First-AcceptedRow $m
$t.v5Anchors[0].PSObject.Properties.Remove("symbol")
$null = Run-Case "n13-missing-anchor-symbol" (Serialize $m) 1 @("v5-anchor-symbol-missing")

# --- n14 [review 4]: row without V5 ownership (v5Targets and planTarget removed) ---
$m = New-Matrix
$m.rows[0].PSObject.Properties.Remove("v5Targets")
$m.rows[0].PSObject.Properties.Remove("planTarget")
$null = Run-Case "n14-missing-v5-ownership" (Serialize $m) 1 @("v5-targets-missing", "plan-target-missing")

# --- n15 [review 5]: responsible task and dependency fabricated outside the taskbook ---
$m = New-Matrix
$m.rows[0].responsibleTask = "P999.9"
$m.rows[0].dependencies = (@($m.rows[0].dependencies) + @("P888.8"))
$null = Run-Case "n15-nonexistent-task" (Serialize $m) 1 @("responsible-task-not-in-taskbook", "dependency-not-in-taskbook")

# --- n16: gate dependency cycle (G1 itself requires P2.1) ---
$m = New-Matrix
$m.rows[0].dependencies = @("G1")
$null = Run-Case "n16-gate-dependency-cycle" (Serialize $m) 1 @("gate-dependency-cycle")

# --- n17: anchor path escaping the repo root ---
$m = New-Matrix
$t = First-RowWithAnchors $m
$t.v5Anchors[0].path = "../escaped.kt"
$null = Run-Case "n17-anchor-path-escape" (Serialize $m) 1 @("anchor-path-not-canonical")

# --- n18: v5 anchor pointing at a test source instead of V5 production code ---
$m = New-Matrix
$t = First-RowWithAnchors $m
$t.v5Anchors[0].path = "server/src/test/kotlin/com/majiang/server/tcp/TcpTableServerCapacityAndLifecycleTest.kt"
$null = Run-Case "n18-anchor-category-mismatch" (Serialize $m) 1 @("v5-anchor-not-v5-source")

# --- n19: acceptance commit that exists but is NOT an ancestor of the frozen baseline ---
$m = New-Matrix
$t = First-AcceptedRow $m
$ErrorActionPreference = "Continue"
$nonAnc = (& git -C $RepoRoot rev-list ($m.baselineCommit + "..HEAD") --max-count=1 2>$null)
if (-not $nonAnc) { $nonAnc = "f7242623c15f67b39171118890ae91aa44667813" }
& git -C $RepoRoot cat-file -e ($nonAnc.Trim() + "^{commit}") 2>$null
if ($LASTEXITCODE -ne 0) { $nonAnc = "" }
$ErrorActionPreference = "Stop"
if ([string]::IsNullOrEmpty($nonAnc)) {
    Log "=== CASE n19-nonancestor-commit SKIPPED-ENV (no non-ancestor commit resolvable)"
    $script:results += $true
} else {
    $t.acceptanceAnchors[0].commit = $nonAnc.Trim()
    $null = Run-Case "n19-nonancestor-commit" (Serialize $m) 1 @("acceptance-commit-not-ancestor-of-baseline")
}

# --- n20: acceptance doc missing at an otherwise valid commit ---
$m = New-Matrix
$t = First-AcceptedRow $m
$t.acceptanceAnchors[0].doc = "doc/nonexistent-acceptance-98765.md"
$null = Run-Case "n20-doc-missing-at-commit" (Serialize $m) 1 @("acceptance-doc-missing-at-commit")

# --- n21: v5Targets outside the controlled module vocabulary ---
$m = New-Matrix
$m.rows[0].v5Targets = @("Mahjong.Nope")
$null = Run-Case "n21-unknown-v5-target" (Serialize $m) 1 @("unknown-v5-target-module")

# --- summary ---
$passCount = ($script:results | Where-Object { $_ }).Count
$total = $script:results.Count
Log ("SUMMARY cases=" + $total + " ok=" + $passCount)
Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
if ($passCount -eq $total) { Log "SELFTEST=PASS" } else { Log "SELFTEST=FAIL" }

$outText = ($log -join "`r`n") + "`r`n"
if (-not [string]::IsNullOrEmpty($OutLog)) {
    [System.IO.File]::WriteAllText($OutLog, $outText, (New-Object System.Text.UTF8Encoding($false)))
}
Write-Output $outText
if ($passCount -eq $total) { exit 0 } else { exit 1 }
