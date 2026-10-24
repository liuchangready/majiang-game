# VerifyV5Traceability.Tests.ps1
# Reproducible self-test for tools/traceability/validate-implementation-trace.ps1.
#
# Design (round 3):
#   - Positive case: the validator must PASS the untouched real repository
#     (RESULT=PASS, exit 0, tracked-main-kt=266, rows=266).
#   - Real-repo negative cases: mutate an in-memory copy of the real doc implementation-v1.json,
#     write it to a throwaway TEMP file, and require the validator to exit 1 with the expected
#     failure marker(s). The repository itself is never modified.
#   - Typed-schema negative cases t01..t17: EVERY JSON primitive-type error is its own case with
#     its own marker (numbers/booleans in string fields, scalars/null/objects where arrays are
#     required, null/false array elements, anchor sub-field types, header denominator/stats/
#     vocabulary/gate list types). No case bundles several error types.
#   - Sandbox cases s1..s10: a THROWAWAY ISOLATED git repository is created under the system temp
#     directory with a baseline commit B and a child commit C. B carries a minimal production
#     source, a test source, a V5 C# source and the real taskbook; C adds post-baseline files and
#     post-baseline-only symbols. The cases prove that (a) the baseline-good matrix passes, (b) a
#     file tracked only AFTER the baseline cannot satisfy a row, (c) a symbol added to a source
#     only AFTER the baseline cannot serve as v4Symbol or as a V5 anchor symbol, and (d) an
#     acceptance commit that is a descendant of the baseline is rejected. Cases s7..s10 pin the
#     pseudo-symbol holes: a word that appears only in a COMMENT (s7), only inside a STRING
#     literal (s8), only as a GENERIC PARAMETER (s9), or only as an extension RECEIVER (s10)
#     must all fail; the declaration matchers strip comments/strings first and require real
#     declaration syntax. These cases are fully deterministic and never touch the real
#     repository's production code or index.
#   - NO-SKIP RULE: there is no skipped, defaulted or otherwise hidden-green case anywhere. If an
#     environment assumption (for example resolving a non-ancestor commit in the real repo) cannot
#     be met, the case is counted as FAILED and the self-test exits non-zero.
#   - The script is ASCII-only and PowerShell 5.1 compatible and lives IN git, so a clean checkout
#     can re-run it.
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

# Locate the real matrix and the taskbook exactly like the validator does (the doc directory
# name is non-ASCII, so no literal path appears in this ASCII-only script).
$matrixCandidates = @(Get-ChildItem -LiteralPath (Join-Path $RepoRoot "doc") -Recurse -Filter "implementation-v1.json" -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
if ($matrixCandidates.Count -ne 1) {
    Write-Output "SELFTEST=FAIL matrix-not-unique count=$($matrixCandidates.Count)"
    exit 1
}
$matrixPath = $matrixCandidates[0]
$taskbookCandidates = @(Get-ChildItem -LiteralPath (Join-Path $RepoRoot "doc") -Recurse -Filter "V5-0-1-IMPLEMENTATION-TASKS.md" -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
if ($taskbookCandidates.Count -ne 1) {
    Write-Output "SELFTEST=FAIL taskbook-not-unique count=$($taskbookCandidates.Count)"
    exit 1
}
$taskbookPath = $taskbookCandidates[0]

$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("v5trace-selftest-" + [System.IO.Path]::GetRandomFileName())
New-Item -ItemType Directory -Force -Path $tempDir | Out-Null

$log = New-Object System.Collections.Generic.List[string]
function Log([string]$s) { $script:log.Add($s) | Out-Null }

function Write-Utf8NoBom([string]$p, [string]$s) {
    [System.IO.File]::WriteAllText($p, $s, (New-Object System.Text.UTF8Encoding($false)))
}

function Invoke-ValidatorOn([string]$repoRoot, [string]$jsonFile) {
    $ErrorActionPreference = "Continue"
    $out = & powershell -NoProfile -ExecutionPolicy Bypass -File $script:validator -RepoRoot $repoRoot -JsonPath $jsonFile 2>&1
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

function Run-Case([string]$name, [string]$jsonText, [int]$expectExit, [string[]]$expectMarkers, [string]$repoRoot) {
    $script:caseNo++
    $file = Join-Path $script:tempDir ("case-" + $name + ".json")
    Write-Utf8NoBom $file $jsonText
    if ([string]::IsNullOrEmpty($repoRoot)) { $repoRoot = $script:RepoRoot }
    $r = Invoke-ValidatorOn $repoRoot $file
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

# --- positive: real repository must pass ---
$pos = Invoke-ValidatorRepo
Log ("=== CASE pos-real-repo exit=" + $pos.code + " (expect 0)")
foreach ($line in ($pos.text -split "`n")) { if ($line -match "^(RESULT|INFO)") { Log ("  " + $line.Trim()) } }
$posOk = ($pos.code -eq 0 -and $pos.text.IndexOf("RESULT=PASS") -ge 0 -and $pos.text.IndexOf("tracked-main-kt=266") -ge 0 -and $pos.text.IndexOf("rows=266") -ge 0)
if ($posOk) { Log "  CASE-OK pos-real-repo" } else { Log "  CASE-BROKEN pos-real-repo" }
$script:results += $posOk

# --- n01: drop one row -> baseline source left without a row ---
$m = New-Matrix
$m.rows = @($m.rows | Select-Object -Skip 1)
$null = Run-Case "n01-missing-row" (Serialize $m) 1 @("missing-row-for-tracked-source", "coverage-mismatch") ""

# --- n02: duplicate the same source row ---
$m = New-Matrix
$arr = @($m.rows)
$m.rows = (@($arr[0]) + $arr)
$null = Run-Case "n02-duplicate-source" (Serialize $m) 1 @("duplicate-source-row") ""

# --- n03: unknown status value ---
$m = New-Matrix
$m.rows[0].status = "done"
$null = Run-Case "n03-unknown-status" (Serialize $m) 1 @("unknown-status") ""

# --- n04: v5 anchor path that is not in the frozen baseline tree ---
$m = New-Matrix
$t = First-RowWithAnchors $m
$t.v5Anchors[0].path = "dotnet/src/Nope.cs"
$null = Run-Case "n04-anchor-not-in-baseline-tree" (Serialize $m) 1 @("v5-anchor-not-in-baseline-tree") ""

# --- n05: symbol exists nowhere as a declaration in the baseline blob of the anchored file ---
$m = New-Matrix
$t = First-RowWithAnchors $m
$t.v5Anchors[0].symbol = "NoSuchSymbol98765"
$null = Run-Case "n05-fake-anchor-symbol" (Serialize $m) 1 @("v5-anchor-symbol-not-declared-at-baseline") ""

# --- n06: pending row without gap summary and without a responsible task ---
$m = New-Matrix
$t = First-PendingRow $m
$t.gapSummary = ""
$t.responsibleTask = ""
$null = Run-Case "n06-pending-no-responsibility" (Serialize $m) 1 @("status-needs-gap-summary", "missing-responsible-task") ""

# --- n07: broken json ---
$null = Run-Case "n07-bad-json" "{ this is not json" 1 @("bad-json") ""

# --- n08: stats lie (anti all-pass trap) ---
$m = New-Matrix
$m.stats.byStatus.partial = 1 + $m.stats.byStatus.partial
$null = Run-Case "n08-stats-mismatch" (Serialize $m) 1 @("stats-by-status-mismatch") ""

# --- n09: extra row for a source that is not in the frozen baseline tree ---
$m = New-Matrix
$extra = $m.rows[0].PSObject.Copy()
$extra.id = "ENG-999"
$extra.source = "ghost/Ghost.kt"
$m.rows = (@($m.rows) + @($extra))
$m.denominator.productionSources = 1 + $m.denominator.productionSources
$m.stats.byModule.engine = 1 + $m.stats.byModule.engine
$null = Run-Case "n09-untracked-extra-row" (Serialize $m) 1 @("row-not-a-tracked-production-source", "coverage-mismatch") ""

# --- n10: accepted-component without acceptance evidence ---
$m = New-Matrix
$t = First-AcceptedRow $m
$t.PSObject.Properties["acceptanceAnchors"].Value = @()
$null = Run-Case "n10-accepted-no-acceptance" (Serialize $m) 1 @("accepted-component-without-acceptance-anchor") ""

# --- n11 [review 1]: v4Symbol that does not exist in the row's own baseline source blob ---
$m = New-Matrix
$m.rows[0].v4Symbol = "CodexNonexistentType987654"
$null = Run-Case "n11-fake-v4-symbol" (Serialize $m) 1 @("v4-symbol-not-declared-at-baseline") ""

# --- n12 [review 2]: acceptance commit that does not exist in the repo ---
$m = New-Matrix
$t = First-AcceptedRow $m
$t.acceptanceAnchors[0].commit = "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef"
$null = Run-Case "n12-nonexistent-acceptance-commit" (Serialize $m) 1 @("acceptance-commit-not-in-repo") ""

# --- n13 [review 3]: v5 anchor without the required symbol field ---
$m = New-Matrix
$t = First-AcceptedRow $m
$t.v5Anchors[0].PSObject.Properties.Remove("symbol")
$null = Run-Case "n13-missing-anchor-symbol" (Serialize $m) 1 @("v5-anchor-symbol-missing") ""

# --- n14 [review 4]: row without V5 ownership (v5Targets and planTarget removed) ---
$m = New-Matrix
$m.rows[0].PSObject.Properties.Remove("v5Targets")
$m.rows[0].PSObject.Properties.Remove("planTarget")
$null = Run-Case "n14-missing-v5-ownership" (Serialize $m) 1 @("row-field-missing v5Targets", "row-field-missing planTarget") ""

# --- n15 [review 5]: responsible task and dependency fabricated outside the taskbook ---
$m = New-Matrix
$m.rows[0].responsibleTask = "P999.9"
$m.rows[0].dependencies = (@($m.rows[0].dependencies) + @("P888.8"))
$null = Run-Case "n15-nonexistent-task" (Serialize $m) 1 @("responsible-task-not-in-taskbook", "dependency-not-in-taskbook") ""

# --- n16: gate dependency cycle (G1 itself requires P2.1) ---
$m = New-Matrix
$m.rows[0].dependencies = @("G1")
$null = Run-Case "n16-gate-dependency-cycle" (Serialize $m) 1 @("gate-dependency-cycle") ""

# --- n17: anchor path escaping the repo root ---
$m = New-Matrix
$t = First-RowWithAnchors $m
$t.v5Anchors[0].path = "../escaped.kt"
$null = Run-Case "n17-anchor-path-escape" (Serialize $m) 1 @("anchor-path-not-canonical") ""

# --- n18: v5 anchor pointing at a test source instead of V5 production code ---
$m = New-Matrix
$t = First-RowWithAnchors $m
$t.v5Anchors[0].path = "server/src/test/kotlin/com/majiang/server/tcp/TcpTableServerCapacityAndLifecycleTest.kt"
$null = Run-Case "n18-anchor-category-mismatch" (Serialize $m) 1 @("v5-anchor-not-v5-source") ""

# --- n19: acceptance commit that exists but is NOT an ancestor of the frozen baseline.
#          NO-SKIP: if the commit cannot be resolved the case counts as FAILED. ---
$m = New-Matrix
$t = First-AcceptedRow $m
$ErrorActionPreference = "Continue"
$nonAnc = (& git -C $RepoRoot rev-list ($m.baselineCommit + "..HEAD") --max-count=1 2>$null)
$n19rc = $LASTEXITCODE
$ErrorActionPreference = "Stop"
if ($n19rc -ne 0 -or [string]::IsNullOrEmpty($nonAnc)) {
    Log "=== CASE n19-nonancestor-commit exit=ENV-UNRESOLVABLE counted as FAILURE (no-skip rule)"
    Log "  CASE-BROKEN n19-nonancestor-commit"
    $script:results += $false
} else {
    $t.acceptanceAnchors[0].commit = $nonAnc.Trim()
    $null = Run-Case "n19-nonancestor-commit" (Serialize $m) 1 @("acceptance-commit-not-ancestor-of-baseline") ""
}

# --- n20: acceptance doc missing at an otherwise valid commit ---
$m = New-Matrix
$t = First-AcceptedRow $m
$t.acceptanceAnchors[0].doc = "doc/nonexistent-acceptance-98765.md"
$null = Run-Case "n20-doc-missing-at-commit" (Serialize $m) 1 @("acceptance-doc-missing-at-commit") ""

# --- n21: v5Targets outside the controlled module vocabulary ---
$m = New-Matrix
$m.rows[0].v5Targets = @("Mahjong.Nope")
$null = Run-Case "n21-unknown-v5-target" (Serialize $m) 1 @("unknown-v5-target-module") ""

# --- n22 [review round 3]: direct self-task-dependency must be rejected ---
$m = New-Matrix
$m.rows[0].dependencies = (@($m.rows[0].dependencies) + @($m.rows[0].responsibleTask))
$null = Run-Case "n22-self-task-dependency" (Serialize $m) 1 @("self-task-dependency") ""

# --- typed-schema negatives t01..t17: one JSON primitive-type error per case ---
# t01: number where a non-empty string is required
$m = New-Matrix
$m.rows[0].notes = 42
$null = Run-Case "t01-notes-number" (Serialize $m) 1 @("row-field-not-string notes") ""

# t02: boolean where a non-empty string is required
$m = New-Matrix
$m.rows[0].gapSummary = $true
$null = Run-Case "t02-gapsummary-boolean" (Serialize $m) 1 @("row-field-not-string gapSummary") ""

# t03: scalar string where a JSON array is required
$m = New-Matrix
$m.rows[0].dependencies = "P0.1"
$null = Run-Case "t03-dependencies-scalar" (Serialize $m) 1 @("row-field-not-array dependencies") ""

# t04: scalar string where a JSON array is required (v5Targets)
$m = New-Matrix
$m.rows[0].v5Targets = "Mahjong.Domain"
$null = Run-Case "t04-v5targets-scalar" (Serialize $m) 1 @("row-field-not-array v5Targets") ""

# t05: JSON null where a JSON array is required
$m = New-Matrix
$m.rows[0].testAnchors = $null
$null = Run-Case "t05-testanchors-null" (Serialize $m) 1 @("row-field-not-array testAnchors") ""

# t06: JSON object where a JSON array is required
$m = New-Matrix
$m.rows[0].dependencies = @{ P0 = "P0.1" }
$null = Run-Case "t06-dependencies-object" (Serialize $m) 1 @("row-field-not-array dependencies") ""

# t07: null element inside a real array must fail, never be skipped
$m = New-Matrix
$m.rows[0].v5Anchors = @($null)
$null = Run-Case "t07-anchor-element-null" (Serialize $m) 1 @("anchor-element-not-object v5Anchors") ""

# t08: non-string element inside a string array
$m = New-Matrix
$m.rows[0].dependencies = @("P0.1", 7)
$null = Run-Case "t08-dependency-element-number" (Serialize $m) 1 @("array-element-not-string dependencies") ""

# t09: null element inside goldenSamples
$m = New-Matrix
$m.rows[0].goldenSamples = @($null)
$null = Run-Case "t09-golden-element-null" (Serialize $m) 1 @("array-element-not-string goldenSamples") ""

# t10: anchor sub-field path typed as a number
$m = New-Matrix
$t = First-RowWithAnchors $m
$t.v5Anchors[0].path = 42
$null = Run-Case "t10-anchor-path-number" (Serialize $m) 1 @("anchor-field-not-string path") ""

# t11: header denominator.productionSources typed as a string
$m = New-Matrix
$m.denominator.productionSources = "266"
$null = Run-Case "t11-denominator-string" (Serialize $m) 1 @("header-field-not-number denominator.productionSources") ""

# t12: header stats value typed as a string
$m = New-Matrix
$m.stats.byStatus.partial = "126"
$null = Run-Case "t12-stats-value-string" (Serialize $m) 1 @("header-stats-value-not-number byStatus partial") ""

# t13: header stats with an unknown key
$m = New-Matrix
$m.stats.byModule | Add-Member -MemberType NoteProperty -Name "ghostmod" -Value 1 -Force
$null = Run-Case "t13-stats-key-unknown" (Serialize $m) 1 @("header-stats-key-unknown byModule ghostmod") ""

# t14: header status vocabulary deviating from the controlled set
$m = New-Matrix
$m.statusVocabulary = @("accepted-component","partial","pending","replaced-by-v5-contract")
$null = Run-Case "t14-status-vocabulary" (Serialize $m) 1 @("status-vocabulary-mismatch") ""

# t15: header gatePrerequisites entry typed as a scalar instead of an array
$m = New-Matrix
$m.gatePrerequisites.G0 = "P0.1"
$null = Run-Case "t15-gate-prereqs-scalar" (Serialize $m) 1 @("header-gate-prereqs-not-array G0") ""

# t16: header goldenSamplesInBaseline typed as a scalar string
$m = New-Matrix
$m.goldenSamplesInBaseline = "tests/v5-golden/x.json"
$null = Run-Case "t16-header-golden-scalar" (Serialize $m) 1 @("header-field-not-array goldenSamplesInBaseline") ""

# t17: header rows typed as a scalar instead of an array
$m = New-Matrix
$m.rows = "boo"
$null = Run-Case "t17-rows-scalar" (Serialize $m) 1 @("header-field-not-array rows") ""

# --- sandbox cases s1..s6: isolated throwaway git repo, baseline B + child commit C ---
$sb = Join-Path $tempDir "sandbox"
New-Item -ItemType Directory -Force -Path $sb | Out-Null
$sandboxOk = $true
$ErrorActionPreference = "Continue"
$null = & git init $sb 2>&1
$null = & git -C $sb add -A 2>&1
$ErrorActionPreference = "Stop"

function Write-SandboxFile([string]$rel, [string]$content) {
    $p = Join-Path $script:sb $rel
    $d = Split-Path -Parent $p
    if (-not (Test-Path -LiteralPath $d)) { New-Item -ItemType Directory -Force -Path $d | Out-Null }
    Write-Utf8NoBom $p $content
}

$ErrorActionPreference = "Continue"
try {
    New-Item -ItemType Directory -Force -Path (Join-Path $sb "doc") | Out-Null
    Copy-Item -LiteralPath $taskbookPath -Destination (Join-Path $sb "doc\V5-0-1-IMPLEMENTATION-TASKS.md")
    $sbDocDir = Join-Path $sb "doc"
    $tbCopied = @(Get-ChildItem -LiteralPath $sbDocDir -Recurse -Filter "V5-0-1-IMPLEMENTATION-TASKS.md" -ErrorAction SilentlyContinue)
    if ($tbCopied.Count -ne 1) { throw "taskbook copy not unique" }
    Write-SandboxFile "engine/src/main/kotlin/com/majiang/Demo.kt" ("class DemoEngine`n`n// sandbox production source`nfun ExtraEngine.polish() { }`n")
    Write-SandboxFile "engine/src/test/kotlin/com/majiang/DemoTest.kt" ("class DemoEngineTest`n")
    Write-SandboxFile "dotnet/src/DemoV5.cs" ("public sealed class DemoV5Type`n{`n    // this record is durable before use`n    public const string Label = `"class StringOnlySymbol in string`";`n}`nclass SandboxBox<T>`n{`n}`n")
    $null = & git -C $sb add -A 2>&1
    $null = & git -C $sb -c user.name=selftest -c user.email=selftest@example.invalid commit -m "B baseline" 2>&1
    $shaB = (& git -C $sb rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrEmpty($shaB)) { throw "baseline commit B failed" }
    $shaB = $shaB.Trim()
    # child commit C: post-baseline files and post-baseline-only symbols
    Write-SandboxFile "engine/src/main/kotlin/com/majiang/Demo.kt" ("class DemoEngine`n`n// sandbox production source`nfun ExtraEngine.polish() { }`n`nclass LaterEngineSymbol`n")
    Write-SandboxFile "engine/src/main/kotlin/com/majiang/Extra.kt" ("class ExtraEngine`n")
    Write-SandboxFile "dotnet/src/DemoV5.cs" ("public sealed class DemoV5Type`n{`n    // this record is durable before use`n    public const string Label = `"class StringOnlySymbol in string`";`n}`nclass SandboxBox<T>`n{`n}`nclass LaterV5Symbol`n")
    Write-SandboxFile "dotnet/src/ExtraV5.cs" ("public sealed class ExtraV5Type`n")
    Write-SandboxFile "doc/SIDE-ACCEPT.md" ("# side acceptance at C`n")
    $null = & git -C $sb add -A 2>&1
    $null = & git -C $sb -c user.name=selftest -c user.email=selftest@example.invalid commit -m "C post-baseline" 2>&1
    $shaC = (& git -C $sb rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrEmpty($shaC)) { throw "child commit C failed" }
    $shaC = $shaC.Trim()
} catch {
    $sandboxOk = $false
    Log ("=== SANDBOX-SETUP-FAILED " + $_.Exception.Message)
}
$ErrorActionPreference = "Stop"

if (-not $sandboxOk) {
    # NO-SKIP: a sandbox environment failure is an explicit self-test failure, never a pass.
    Log "  CASE-BROKEN sandbox-setup (counted as failures for s1..s6)"
    1..6 | ForEach-Object { $script:results += $false }
} else {
    Log ("SANDBOX-B=" + $shaB)
    Log ("SANDBOX-C=" + $shaC)

    # s1: the baseline-good matrix must PASS against the sandbox repo frozen at B
    $good = New-Matrix
    $good.baselineCommit = $shaB
    $good.baselineBranch = "sandbox"
    $good.basisNote = "sandbox baseline-good"
    $good.denominator.productionSources = 1
    $good.stats.byStatus = [pscustomobject]@{ pending = 1 }
    $good.stats.byModule = [pscustomobject]@{ engine = 1 }
    $good.goldenSamplesInBaseline = @()
    $good.rows = @([pscustomobject]@{
        id = "TST-001"; module = "engine"; source = "Demo.kt"; v4Symbol = "DemoEngine"
        planTarget = "plan demo target"; status = "pending"; responsibleTask = "P1.1"
        dependencies = @("P0.1"); gapSummary = "sandbox demo gap"; v5Targets = @("Mahjong.Domain")
        v5Anchors = @()
        testAnchors = @(@{ path = "engine/src/test/kotlin/com/majiang/DemoTest.kt"; symbol = "DemoEngineTest" })
        acceptanceAnchors = @(); goldenSamples = @(); notes = "sandbox demo notes"
    })
    $null = Run-Case "s1-sandbox-baseline-good" (Serialize $good) 0 @() $sb

    # s2: a file tracked only AFTER the baseline cannot satisfy a row
    $m = Serialize $good | ConvertFrom-Json
    $r2 = $m.rows[0].PSObject.Copy()
    $r2.id = "TST-002"
    $r2.source = "Extra.kt"
    $r2.v4Symbol = "ExtraEngine"
    $m.rows = (@($m.rows) + @($r2))
    $m.denominator.productionSources = 2
    $m.stats.byModule.engine = 2
    $null = Run-Case "s2-postbaseline-file-row" (Serialize $m) 1 @("row-not-a-tracked-production-source", "coverage-mismatch") $sb

    # s3: a symbol added to a source only AFTER the baseline cannot serve as v4Symbol
    $m = Serialize $good | ConvertFrom-Json
    $m.rows[0].v4Symbol = "LaterEngineSymbol"
    $null = Run-Case "s3-postbaseline-v4-symbol" (Serialize $m) 1 @("v4-symbol-not-declared-at-baseline") $sb

    # s4: a symbol added to a V5 file only AFTER the baseline cannot be an anchor symbol
    $m = Serialize $good | ConvertFrom-Json
    $m.rows[0].v5Anchors = @(@{ path = "dotnet/src/DemoV5.cs"; symbol = "LaterV5Symbol" })
    $null = Run-Case "s4-postbaseline-anchor-symbol" (Serialize $m) 1 @("v5-anchor-symbol-not-declared-at-baseline") $sb

    # s5: a V5 file that exists only AFTER the baseline cannot be an anchor at all
    $m = Serialize $good | ConvertFrom-Json
    $m.rows[0].v5Anchors = @(@{ path = "dotnet/src/ExtraV5.cs"; symbol = "ExtraV5Type" })
    $null = Run-Case "s5-postbaseline-anchor-file" (Serialize $m) 1 @("v5-anchor-not-in-baseline-tree") $sb

    # s6: an acceptance commit that is a DESCENDANT of the baseline is not evidence
    $m = Serialize $good | ConvertFrom-Json
    $m.rows[0].acceptanceAnchors = @(@{ doc = "doc/SIDE-ACCEPT.md"; commit = $shaC })
    $null = Run-Case "s6-nonancestor-acceptance-commit" (Serialize $m) 1 @("acceptance-commit-not-ancestor-of-baseline", "acceptance-doc-not-in-baseline-tree") $sb

    # s7: a word that appears only in a COMMENT of the anchored file is not a declaration
    $m = Serialize $good | ConvertFrom-Json
    $m.rows[0].v5Anchors = @(@{ path = "dotnet/src/DemoV5.cs"; symbol = "durable" })
    $null = Run-Case "s7-comment-only-anchor-symbol" (Serialize $m) 1 @("v5-anchor-symbol-not-declared-at-baseline") $sb

    # s8: a class name that appears only inside a STRING literal is not a declaration
    $m = Serialize $good | ConvertFrom-Json
    $m.rows[0].v5Anchors = @(@{ path = "dotnet/src/DemoV5.cs"; symbol = "StringOnlySymbol" })
    $null = Run-Case "s8-string-only-anchor-symbol" (Serialize $m) 1 @("v5-anchor-symbol-not-declared-at-baseline") $sb

    # s9: a generic PARAMETER name is not a declared type ("class SandboxBox<T>" declares
    # SandboxBox, not T)
    $m = Serialize $good | ConvertFrom-Json
    $m.rows[0].v5Anchors = @(@{ path = "dotnet/src/DemoV5.cs"; symbol = "T" })
    $null = Run-Case "s9-generic-param-anchor-symbol" (Serialize $m) 1 @("v5-anchor-symbol-not-declared-at-baseline") $sb

    # s10: an extension RECEIVER name is not a declaration in the receiver-bearing file
    # ("fun ExtraEngine.polish()" declares polish, not ExtraEngine)
    $m = Serialize $good | ConvertFrom-Json
    $m.rows[0].v4Symbol = "ExtraEngine"
    $null = Run-Case "s10-receiver-only-v4-symbol" (Serialize $m) 1 @("v4-symbol-not-declared-at-baseline") $sb
}

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
