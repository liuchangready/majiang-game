# validate-implementation-trace.ps1
# Read-only machine validator for doc/*/implementation-v1.json (schema v5-implementation-trace-v1).
# Compatible with Windows PowerShell 5.1. No writes, no installs, no global changes.
# Exit 0 = all checks passed. Exit 1 = at least one check failed. Exit 2 = usage/environment error.
#
# Checks (fail -> exit 1):
#   1. JSON parses; schema field matches v5-implementation-trace-v1; required header fields present.
#   2. baselineCommit is a 40-hex sha and exists as a commit object in the repo (read-only git).
#   2b. Taskbook is discovered under doc/ and parsed mechanically: every "### <id>" heading yields
#       the REAL task ID set; the heading structure also yields each gate's prerequisite task set
#       (tasks listed between the previous gate heading and the gate itself). The gate structure
#       must be complete (G0..G5) or the run fails.
#   3. Rows are compared against REAL tracked production sources via git ls-files:
#      only "<module>/src/main/**/*.kt" counts (src/test, generated code, non-kt excluded).
#      Missing row / extra row / duplicate (module,source) -> exit 1.
#   4. Typed schema per row:
#      - required non-empty strings: id module source v4Symbol planTarget status responsibleTask
#        gapSummary notes
#      - required arrays (may be empty only where noted): v5Targets (>=1), dependencies,
#        v5Anchors, testAnchors, acceptanceAnchors, goldenSamples
#      - id matches ^[A-Z]{3}-[0-9]{3}$ and is globally unique
#      - v5Targets entries are controlled V5 module names (Unity( planned ) allowed as prefix form)
#   4b. responsibleTask and every dependency must be a REAL taskbook task ID (from 2b, not a
#       regex guess). Anti-cycle: a dependency on gate Gx is rejected when the row's
#       responsibleTask is one of Gx's own prerequisites (derived from the taskbook headings).
#   5. Anchor hygiene:
#      - all anchor paths are canonical repo-relative (no backslash, no drive, no leading /,
#        no "." or ".." segments)
#      - v5Anchors: path under dotnet/src/ AND git-tracked; symbol required and declared in the
#        anchored file (type declaration form)
#      - testAnchors: path under a test source area (<module>/src/test/ or dotnet/tests/) AND
#        git-tracked; symbol required and declared
#      - acceptanceAnchors: doc under doc/ and present in the worktree; commit is 7..40 hex,
#        EXISTS as a commit, is an ANCESTOR OF baselineCommit, and the doc EXISTS AT that commit
#        (git cat-file). Format alone is not accepted evidence.
#      - goldenSamples / goldenSamplesInBaseline: under tests/v5-golden/ and present.
#   5b. v4Symbol must actually be declared in the row's own tracked V4 source file. Accepted
#       declaration forms cover real Kotlin source shapes: class/interface/object (incl.
#       enum/data/sealed/abstract variants), top-level/member/extension functions (incl. generic
#       and receiver forms), typealias, and val/var/const val property declarations. A symbol
#       that only appears as an import or usage is rejected.
#   6. Status-specific responsibility: accepted-component rows require v5Anchors AND
#      acceptanceAnchors; every other status requires a non-empty gapSummary AND a valid
#      responsibleTask. (No "pending without a responsible task".)
#   7. Self-consistency: denominator.productionSources == row count == enumerated source count;
#      stats.byStatus / stats.byModule recomputed from rows must equal the declared values.
#      (Deliberately NOT hardcoded to all-pass: numbers must match the real repo and the rows.)
#   7b. Header gatePrerequisites (documentation) must equal the taskbook-derived sets.

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
if ([System.IO.Path]::IsPathRooted($JsonPath)) {
    $absJson = $JsonPath
} else {
    $absJson = Join-Path $RepoRoot $JsonPath
}

$ErrorActionPreference = "Stop"
$failures = New-Object System.Collections.Generic.List[string]
$notesOut = New-Object System.Collections.Generic.List[string]

function Fail([string]$msg) { $script:failures.Add($msg) | Out-Null }
function Note([string]$msg) { $script:notesOut.Add($msg) | Out-Null }
function Has-Prop($obj, [string]$name) { return ($null -ne $obj -and $null -ne $obj.PSObject.Properties[$name]) }

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

# --- canonical repo-relative path (forward slashes only, no escape segments) ---
function Test-CanonicalRepoPath([string]$p) {
    if ([string]::IsNullOrEmpty($p)) { return $false }
    if ($p.IndexOf("\") -ge 0) { return $false }
    if ($p -match "^[A-Za-z]:") { return $false }
    if ($p.StartsWith("/")) { return $false }
    foreach ($seg in $p.Split("/")) {
        if ($seg -eq "" -or $seg -eq "." -or $seg -eq "..") { return $false }
    }
    return $true
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

# --- 1b. required header fields ---
foreach ($hf in @("schema","generatedDate","baselineCommit","baselineBranch","basisNote","denominator","statusVocabulary","goldenSamplesInBaseline","stats","rows","dependenciesSemantics","planTargetBasis","gatePrerequisites")) {
    if (-not (Has-Prop $doc $hf)) { Fail ("header-missing-field " + $hf) }
}

# --- 2. baseline commit exists (read-only) ---
$bc = $doc.baselineCommit
if (-not ("$bc" -match "^[0-9a-f]{40}$")) {
    Fail ("bad-baseline-commit format: " + $bc)
} else {
    $ErrorActionPreference = "Continue"
    $null = & git -C $RepoRoot rev-parse --verify --quiet ($bc + "^{commit}") 2>$null
    if ($LASTEXITCODE -ne 0) { Fail ("baseline-commit-not-in-repo: " + $bc) }
    $ErrorActionPreference = "Stop"
}

# --- 2b. taskbook: real task IDs + gate prerequisite sets, derived mechanically ---
$tbCandidates = @(Get-ChildItem -LiteralPath (Join-Path $RepoRoot "doc") -Recurse -Filter "V5-0-1-IMPLEMENTATION-TASKS.md" -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
if ($tbCandidates.Count -eq 0) {
    Write-Output "RESULT=ERROR"
    Write-Output "REASON=taskbook-not-found-under-doc"
    exit 2
}
if ($tbCandidates.Count -gt 1) {
    Write-Output "RESULT=ERROR"
    Write-Output "REASON=multiple-taskbooks-found"
    exit 2
}
$taskIds = @{}
$gatePrereqs = @{}
$gatesFound = New-Object System.Collections.Generic.List[string]
$curPrereqs = New-Object System.Collections.Generic.List[string]
foreach ($ln in ((Read-Utf8 $tbCandidates[0]) -split "`r?`n")) {
    if ($ln -match "^###\s+(P[0-9]+\.[0-9]+[a-z]?|G[0-5])(\s|$)") {
        $tid = $Matches[1]
        $taskIds[$tid] = $true
        if ($tid -match "^G[0-5]$") {
            $gatePrereqs[$tid] = $curPrereqs
            $gatesFound.Add($tid)
            $curPrereqs = New-Object System.Collections.Generic.List[string]
        } else {
            $curPrereqs.Add($tid)
        }
    }
}
if ($gatesFound.Count -lt 6) {
    Fail ("taskbook-gate-structure-incomplete gates=" + $gatesFound.Count)
}
Note ("taskbook-tasks=" + $taskIds.Count + " gates=" + $gatesFound.Count)

# --- 7b. header gatePrerequisites must equal the taskbook-derived sets ---
if (Has-Prop $doc "gatePrerequisites" -and $null -ne $doc.gatePrerequisites) {
    foreach ($g in $gatesFound) {
        $decl = $doc.gatePrerequisites.PSObject.Properties[$g]
        if ($null -eq $decl) { Fail ("header-gate-prereqs-missing-gate " + $g); continue }
        $declList = (@($doc.gatePrerequisites.$g) | Sort-Object) -join ","
        $derivedList = (@($gatePrereqs[$g]) | Sort-Object) -join ","
        if ($declList -ne $derivedList) {
            Fail ("header-gate-prereqs-mismatch " + $g + " declared=[" + $declList + "] taskbook=[" + $derivedList + "]")
        }
    }
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
# Controlled V5 module vocabulary for v5Targets. Unity( ... ) is the planned Unity assembly,
# matched by prefix so this script stays pure ASCII.
$allowedV5Targets = @("Mahjong.Domain","Mahjong.TableRuntime","Mahjong.Contracts","Mahjong.Contracts.Wire","Mahjong.Ai","Mahjong.Client","Mahjong.LobbyService","Mahjong.LocalHost","Mahjong.TableService","Mahjong.Infrastructure")

$ErrorActionPreference = "Continue"
$lines = & git -C $RepoRoot ls-files -- "*.kt" 2>$null
$lsExit = $LASTEXITCODE
$allFiles = & git -C $RepoRoot ls-files 2>$null
$ErrorActionPreference = "Stop"
if ($lsExit -ne 0) { Write-Output "RESULT=FAIL"; Write-Output "REASON=git-ls-files-failed"; exit 1 }
$trackedAll = @{}
foreach ($f in $allFiles) { if (-not $trackedAll.ContainsKey($f)) { $trackedAll[$f] = $true } }
$realSet = @{}
$realPath = @{}
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
                $realPath[$key] = $l
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
    if ($null -eq $r) { Fail "null-row"; continue }
    $k = "$($r.module)|$($r.source)"
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
    if ($null -eq $r) { continue }
    $s = $r.source
    if ([string]::IsNullOrEmpty($s)) { Fail ("empty-source in module=" + $r.module); continue }
    if ($s -notmatch "\.kt$")           { Fail ("source-not-kt: " + $s) }
    if ($s -match "(^/|[\\:]|\.\.)")    { Fail ("source-not-module-relative: " + $s) }
    if ($s -match "src/test|/build/|generated") { Fail ("source-looks-like-test-or-generated: " + $s) }
}

# --- symbol declaration checks ---
# C# / Kotlin type declarations for v5/test anchors (struct/record kept for C# test sources).
# ":" is excluded from the gap so a SUPERTYPE across "class X : Y" cannot satisfy the check.
function Test-AnchorSymbol([string]$file, [string]$symbol) {
    $text = Read-Utf8 $file
    $rx = [regex]::Escape($symbol)
    $pattern = "(class|struct|interface|enum|record|object)[^=\r\n{(:]{0,160}\b" + $rx + "\b"
    return [regex]::IsMatch($text, $pattern)
}

# Kotlin declaration forms for v4Symbol in the row's own V4 source file:
#   type declarations: (enum|data|sealed|abstract|open|inner|fun)* class/interface/object
#   functions: fun [typeparams] [Receiver.]name(   (covers top-level, member, extension)
#   extension receiver: fun Receiver.name(
#   typealias and property declarations: typealias X / const val X / val X / var X
# A symbol appearing only as an import or a usage does NOT satisfy any of these.
function Test-KotlinDeclaredSymbol([string]$text, [string]$symbol) {
    if ([string]::IsNullOrWhiteSpace($symbol)) { return $false }
    $rx = [regex]::Escape($symbol)
    if ([regex]::IsMatch($text, "(class|interface|object)[^=\r\n{(:]{0,160}\b" + $rx + "\b")) { return $true }
    if ([regex]::IsMatch($text, "\bfun\s+(<[^>\r\n]*>\s*)?([A-Za-z0-9_.?<>\[\]]+\.)?" + $rx + "\s*[(<:]")) { return $true }
    if ([regex]::IsMatch($text, "\bfun\s+" + $rx + "\s*[.]")) { return $true }
    if ([regex]::IsMatch($text, "\b(const\s+val|val|var|typealias)\s+(<[^>\r\n]*>\s*)?([A-Za-z0-9_.?<>\[\]]+\.)?" + $rx + "\b")) { return $true }
    return $false
}

# --- 4/4b/5/5b/6. per-row schema, taskbook check, anchors, responsibility ---
$statusCount = @{}
$moduleCount = @{}
$rowIds = @{}
foreach ($r in $rows) {
    if ($null -eq $r) { continue }
    $tag = "[" + $r.id + " " + $r.module + "/" + $r.source + "]"

    # 4. typed schema: required non-empty strings
    foreach ($f in @("id","module","source","v4Symbol","planTarget","status","responsibleTask","gapSummary","notes")) {
        if (-not (Has-Prop $r $f)) { Fail ($tag + " row-missing-required-field " + $f) }
        elseif ([string]::IsNullOrWhiteSpace("$($r.$f)")) { Fail ($tag + " row-empty-required-field " + $f) }
    }
    # id format + global uniqueness
    if ("$($r.id)" -notmatch "^[A-Z]{3}-[0-9]{3}$") {
        Fail ($tag + " bad-row-id: " + $r.id)
    }
    if ($rowIds.ContainsKey("$($r.id)")) { Fail ($tag + " duplicate-row-id: " + $r.id) }
    else { $rowIds["$($r.id)"] = $true }
    # required arrays (present as JSON arrays; empty is legal for deps/anchor/golden lists)
    foreach ($f in @("dependencies","v5Anchors","testAnchors","acceptanceAnchors","goldenSamples")) {
        if (-not (Has-Prop $r $f)) { Fail ($tag + " row-missing-required-field " + $f) }
    }
    if (-not (Has-Prop $r "v5Targets")) { Fail ($tag + " v5-targets-missing") }
    elseif ($null -eq @($r.v5Targets) -or @($r.v5Targets).Count -eq 0) { Fail ($tag + " v5-targets-empty") }
    else {
        foreach ($t in @($r.v5Targets)) {
            $ts = "$t"
            if ($ts.StartsWith("Unity(")) { continue }
            if ($allowedV5Targets -notcontains $ts) { Fail ($tag + " unknown-v5-target-module: " + $ts) }
        }
    }
    if (-not (Has-Prop $r "planTarget")) { Fail ($tag + " plan-target-missing") }

    if ($modules -notcontains $r.module) { Fail ($tag + " unknown-module: " + $r.module) }
    if ($allowedStatus -notcontains $r.status) { Fail ($tag + " unknown-status: " + $r.status) }

    # 4b. responsibility must reference REAL taskbook IDs; anti gate-cycle
    if (-not (Has-Prop $r "responsibleTask") -or [string]::IsNullOrWhiteSpace("$($r.responsibleTask)")) {
        Fail ($tag + " missing-responsible-task")
    } elseif (-not $taskIds.ContainsKey("$($r.responsibleTask)")) {
        Fail ($tag + " responsible-task-not-in-taskbook: " + $r.responsibleTask)
    }
    foreach ($d in @($r.dependencies)) {
        if (-not $d) { continue }
        $ds = "$d"
        if (-not $taskIds.ContainsKey($ds)) { Fail ($tag + " dependency-not-in-taskbook: " + $ds); continue }
        if ($ds -match "^G[0-5]$") {
            $prereqs = $gatePrereqs[$ds]
            if ($null -ne $prereqs -and $prereqs.Contains("$($r.responsibleTask)")) {
                Fail ($tag + " gate-dependency-cycle: " + $ds + " itself requires " + $r.responsibleTask)
            }
        }
    }

    $statusCount[$r.status] = 1 + ($statusCount[$r.status] | ForEach-Object { if ($_) { $_ } else { 0 } })
    $moduleCount[$r.module] = 1 + ($moduleCount[$r.module] | ForEach-Object { if ($_) { $_ } else { 0 } })

    # 5. v5Anchors: canonical, category (V5 C# source, tracked), symbol required + declared
    foreach ($a in @($r.v5Anchors)) {
        if (-not $a) { continue }
        if (-not (Has-Prop $a "path") -or [string]::IsNullOrWhiteSpace("$($a.path)")) { Fail ($tag + " v5-anchor-path-missing-field"); continue }
        if (-not (Test-CanonicalRepoPath "$($a.path)")) { Fail ($tag + " anchor-path-not-canonical: " + $a.path); continue }
        if (-not "$($a.path)".StartsWith("dotnet/src/")) { Fail ($tag + " v5-anchor-not-v5-source: " + $a.path) }
        elseif (-not $trackedAll.ContainsKey("$($a.path)")) { Fail ($tag + " v5-anchor-not-tracked: " + $a.path) }
        if (-not (Has-Prop $a "symbol") -or [string]::IsNullOrWhiteSpace("$($a.symbol)")) { Fail ($tag + " v5-anchor-symbol-missing"); continue }
        $ap = Join-Path $RepoRoot ("$($a.path)" -replace "/", "\")
        if (-not (Test-Path -LiteralPath $ap -PathType Leaf)) { Fail ($tag + " v5-anchor-path-missing: " + $a.path); continue }
        if (-not (Test-AnchorSymbol $ap "$($a.symbol)")) { Fail ($tag + " v5-anchor-symbol-not-declared: " + $a.symbol + " in " + $a.path) }
    }
    # 5. testAnchors: canonical, category (test sources, tracked), symbol required + declared
    foreach ($a in @($r.testAnchors)) {
        if (-not $a) { continue }
        if (-not (Has-Prop $a "path") -or [string]::IsNullOrWhiteSpace("$($a.path)")) { Fail ($tag + " test-anchor-path-missing-field"); continue }
        if (-not (Test-CanonicalRepoPath "$($a.path)")) { Fail ($tag + " anchor-path-not-canonical: " + $a.path); continue }
        $p = "$($a.path)"
        if (-not ($p.Contains("/src/test/") -or $p.StartsWith("dotnet/tests/"))) { Fail ($tag + " test-anchor-not-test-source: " + $p) }
        elseif (-not $trackedAll.ContainsKey($p)) { Fail ($tag + " test-anchor-not-tracked: " + $p) }
        if (-not (Has-Prop $a "symbol") -or [string]::IsNullOrWhiteSpace("$($a.symbol)")) { Fail ($tag + " test-anchor-symbol-missing"); continue }
        $ap = Join-Path $RepoRoot ($p -replace "/", "\")
        if (-not (Test-Path -LiteralPath $ap -PathType Leaf)) { Fail ($tag + " test-anchor-path-missing: " + $p); continue }
        if (-not (Test-AnchorSymbol $ap "$($a.symbol)")) { Fail ($tag + " test-anchor-symbol-not-declared: " + $a.symbol + " in " + $p) }
    }
    # 5. acceptanceAnchors: doc under doc/ + commit must be REAL evidence, not just 7..40 hex
    foreach ($a in @($r.acceptanceAnchors)) {
        if (-not $a) { continue }
        if (-not (Has-Prop $a "doc") -or [string]::IsNullOrWhiteSpace("$($a.doc)")) { Fail ($tag + " acceptance-doc-missing-field"); continue }
        if (-not (Has-Prop $a "commit") -or [string]::IsNullOrWhiteSpace("$($a.commit)")) { Fail ($tag + " acceptance-commit-missing-field"); continue }
        if (-not (Test-CanonicalRepoPath "$($a.doc)")) { Fail ($tag + " anchor-path-not-canonical: " + $a.doc); continue }
        if (-not "$($a.doc)".StartsWith("doc/")) { Fail ($tag + " acceptance-doc-not-under-doc: " + $a.doc) }
        if (-not ("$($a.commit)" -match "^[0-9a-f]{7,40}$")) { Fail ($tag + " acceptance-commit-bad: " + $a.commit); continue }
        $ap = Join-Path $RepoRoot ("$($a.doc)" -replace "/", "\")
        if (-not (Test-Path -LiteralPath $ap -PathType Leaf)) { Fail ($tag + " acceptance-doc-missing: " + $a.doc) }
        $ErrorActionPreference = "Continue"
        & git -C $RepoRoot cat-file -e ("$($a.commit)" + "^{commit}") 2>$null
        if ($LASTEXITCODE -ne 0) {
            Fail ($tag + " acceptance-commit-not-in-repo: " + $a.commit)
        } else {
            & git -C $RepoRoot merge-base --is-ancestor "$($a.commit)" $bc 2>$null
            if ($LASTEXITCODE -ne 0) { Fail ($tag + " acceptance-commit-not-ancestor-of-baseline: " + $a.commit) }
            & git -C $RepoRoot cat-file -e ("$($a.commit)" + ":" + "$($a.doc)") 2>$null
            if ($LASTEXITCODE -ne 0) { Fail ($tag + " acceptance-doc-missing-at-commit: " + $a.doc + "@" + $a.commit) }
        }
        $ErrorActionPreference = "Stop"
    }
    # 5. golden samples: controlled directory + presence (worktree and header list)
    foreach ($g in @($r.goldenSamples)) {
        if (-not $g) { continue }
        $gs = "$g"
        if (-not (Test-CanonicalRepoPath $gs)) { Fail ($tag + " anchor-path-not-canonical: " + $gs); continue }
        if (-not $gs.StartsWith("tests/v5-golden/")) { Fail ($tag + " golden-sample-path-not-controlled: " + $gs) }
        $gp = Join-Path $RepoRoot ($gs -replace "/", "\")
        if (-not (Test-Path -LiteralPath $gp -PathType Leaf)) { Fail ($tag + " golden-sample-missing: " + $gs) }
    }

    # 5b. v4Symbol must be declared in the row's own tracked V4 source file
    $srcKey = "$($r.module)|$($r.source)"
    if ($realPath.ContainsKey($srcKey)) {
        $srcFull = Join-Path $RepoRoot ($realPath[$srcKey] -replace "/", "\")
        $srcText = Read-Utf8 $srcFull
        if (-not (Test-KotlinDeclaredSymbol $srcText "$($r.v4Symbol)")) {
            Fail ($tag + " v4-symbol-not-declared: " + $r.v4Symbol + " in " + $realPath[$srcKey])
        }
    }

    # 6. status-specific responsibility rules
    if ($r.status -eq "accepted-component") {
        if (@($r.v5Anchors).Count -eq 0 -or -not @($r.v5Anchors)[0]) { Fail ($tag + " accepted-component-without-v5-anchor") }
        if (@($r.acceptanceAnchors).Count -eq 0 -or -not @($r.acceptanceAnchors)[0]) { Fail ($tag + " accepted-component-without-acceptance-anchor") }
    } else {
        if ([string]::IsNullOrWhiteSpace("$($r.gapSummary)")) { Fail ($tag + " status-needs-gap-summary: " + $r.status) }
        if (-not (Has-Prop $r "responsibleTask") -or [string]::IsNullOrWhiteSpace("$($r.responsibleTask)")) { Fail ($tag + " status-needs-responsible-task: " + $r.status) }
    }
}

# --- 7. denominator + stats self-consistency ---
$den = $doc.denominator
if ($null -eq $den) {
    Fail "header-missing-field denominator"
} elseif ($den.productionSources -ne $rows.Count) {
    Fail ("denominator-mismatch declared=" + $den.productionSources + " rows=" + $rows.Count)
}
if ($rows.Count -ne $realCount) {
    Fail ("coverage-mismatch rows=" + $rows.Count + " enumerated-tracked=" + $realCount)
}
foreach ($st in $allowedStatus) {
    $declared = 0
    if ($doc.stats -and $doc.stats.byStatus.PSObject.Properties[$st]) { $declared = $doc.stats.byStatus.$st }
    $computed = 0
    if ($statusCount[$st]) { $computed = $statusCount[$st] }
    if ($declared -ne $computed) { Fail ("stats-by-status-mismatch " + $st + " declared=" + $declared + " computed=" + $computed) }
}
foreach ($m in $modules) {
    $declared = 0
    if ($doc.stats -and $doc.stats.byModule.PSObject.Properties[$m]) { $declared = $doc.stats.byModule.$m }
    $computed = 0
    if ($moduleCount[$m]) { $computed = $moduleCount[$m] }
    if ($declared -ne $computed) { Fail ("stats-by-module-mismatch " + $m + " declared=" + $declared + " computed=" + $computed) }
}

# --- golden samples header list must exist and be controlled too ---
foreach ($g in @($doc.goldenSamplesInBaseline)) {
    if (-not $g) { continue }
    $gs = "$g"
    if (-not (Test-CanonicalRepoPath $gs)) { Fail ("anchor-path-not-canonical: " + $gs); continue }
    if (-not $gs.StartsWith("tests/v5-golden/")) { Fail ("golden-sample-path-not-controlled: " + $gs) }
    $gp = Join-Path $RepoRoot ($gs -replace "/", "\")
    if (-not (Test-Path -LiteralPath $gp -PathType Leaf)) { Fail ("header-golden-sample-missing: " + $gs) }
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
    $modLine += $m + "=$c "
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
