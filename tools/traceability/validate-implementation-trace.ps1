# validate-implementation-trace.ps1
# Read-only machine validator for doc/*/implementation-v1.json (schema v5-implementation-trace-v1).
# Compatible with Windows PowerShell 5.1. No writes, no installs, no global changes.
# Exit 0 = all checks passed. Exit 1 = at least one check failed. Exit 2 = usage/environment error.
#
# Checks (fail -> exit 1):
#   1. JSON parses; schema field matches v5-implementation-trace-v1; header fields present with
#      STRICT JSON types (see 4).
#   2. baselineCommit is a JSON string matching 40-hex and exists as a commit object (read-only git).
#   2b. Taskbook discovered under doc/ and parsed mechanically: every "### <id>" heading yields
#       the REAL task ID set; heading structure also yields each gate's prerequisite set. The
#       gate structure must be complete (G0..G5).
#   3. Row sources are compared against the FROZEN BASELINE tree (`git ls-tree -r baselineCommit`),
#       not the current index or worktree: only "<module>/src/main/**/*.kt" counts. Missing row /
#       extra row / duplicate (module,source) -> exit 1. A file tracked only AFTER the baseline
#       cannot satisfy a row.
#   4. Typed schema, enforced on raw JSON types (no string coercion):
#      - header strings: schema generatedDate baselineCommit baselineBranch basisNote
#        dependenciesSemantics planTargetBasis must be JSON strings, non-empty
#      - denominator: JSON object with numeric productionSources; stats: object of objects with
#        numeric values and no unknown keys; statusVocabulary: JSON string array equal to the
#        controlled vocabulary; rows: JSON array; gatePrerequisites: object of JSON string arrays;
#        goldenSamplesInBaseline: JSON string array
#      - per row, non-empty JSON strings: id module source v4Symbol planTarget status
#        responsibleTask gapSummary notes; JSON arrays: v5Targets dependencies v5Anchors
#        testAnchors acceptanceAnchors goldenSamples (null/scalar/object values are rejected,
#        empty arrays are legal only for the anchor/golden/deps lists)
#      - array elements are typed too: dependency/v5Target/golden entries must be JSON strings;
#        anchor entries must be JSON objects (a null/false element is a failure, never skipped)
#      - anchor sub-fields path/symbol/doc/commit must be JSON strings
#      - id matches ^[A-Z]{3}-[0-9]{3}$ and is globally unique; v5Targets entries are controlled
#        V5 module names (Unity( planned ) allowed as prefix form)
#   4b. responsibleTask and every dependency must be a REAL taskbook task ID (from 2b). A
#       dependency equal to the row's own responsibleTask is rejected (self-task-dependency).
#       A gate dependency Gx is rejected when responsibleTask is one of Gx's own prerequisites
#       (gate-dependency-cycle, derived from the taskbook headings).
#   5. Anchor hygiene against the FROZEN BASELINE (git ls-tree / git cat-file blob), never the
#      current worktree or index:
#      - all anchor paths canonical repo-relative (no backslash, drive, leading /, "." / "..")
#      - v5Anchors: path under dotnet/src/ AND present in the baseline tree; symbol required and
#        declared in the BASELINE BLOB of the anchored file (type declaration form)
#      - testAnchors: path under a test source area (<module>/src/test/ or dotnet/tests/) AND in
#        the baseline tree; symbol required and declared in the baseline blob
#      - acceptanceAnchors: doc under doc/ and present in the baseline tree; commit 7..40 hex,
#        EXISTS, is an ANCESTOR OF baselineCommit, and the doc EXISTS AT that commit (git cat-file)
#      - goldenSamples / goldenSamplesInBaseline: under tests/v5-golden/ and in the baseline tree
#   5b. v4Symbol must be declared in the BASELINE BLOB of the row's own source file. Accepted
#       forms cover real Kotlin shapes: class/interface/object (incl. enum/data/sealed variants),
#       top-level/member/extension functions (incl. generic and receiver forms), typealias, and
#       val/var/const val declarations. Import- or usage-only appearances are rejected.
#   6. Status-specific responsibility: accepted-component rows require v5Anchors AND
#      acceptanceAnchors; every other status requires a non-empty gapSummary AND a responsibleTask.
#   7. Self-consistency: denominator.productionSources == row count == enumerated baseline count;
#      stats.byStatus / stats.byModule recomputed from rows must equal the declared values.
#   7b. Header gatePrerequisites must equal the taskbook-derived sets.

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
# The comma before the expression preserves array-ness through the function return: without it,
# PowerShell unrolls a single-element JSON array to the bare element (and an empty array to $null),
# which would defeat the strict array type checks.
function Prop-Value($obj, [string]$name) { return ,$obj.PSObject.Properties[$name].Value }

# --- STRICT JSON type predicates (no coercion) ---
function Is-JsonString([object]$v)  { return ($null -ne $v -and $v -is [string]) }
function Is-NonEmptyString([object]$v) { return ($null -ne $v -and $v -is [string] -and $v.Trim().Length -gt 0) }
function Is-JsonArray([object]$v)   { return ($null -ne $v -and $v -is [System.Array]) }
function Is-JsonNumber([object]$v)  { return ($null -ne $v -and ($v -is [int] -or $v -is [long] -or $v -is [double] -or $v -is [decimal])) }
function Is-JsonObject([object]$v)  { return ($null -ne $v -and $v -is [System.Management.Automation.PSCustomObject]) }

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
if (-not (Is-NonEmptyString $doc.schema) -or $doc.schema -ne "v5-implementation-trace-v1") {
    Fail ("bad-schema expected=v5-implementation-trace-v1 actual=" + $doc.schema)
}

# --- 1b. required header fields with STRICT types ---
foreach ($hf in @("schema","generatedDate","baselineCommit","baselineBranch","basisNote","dependenciesSemantics","planTargetBasis")) {
    if (-not (Has-Prop $doc $hf)) { Fail ("header-field-missing " + $hf) }
    elseif (-not (Is-NonEmptyString (Prop-Value $doc $hf))) { Fail ("header-field-not-string " + $hf) }
}
foreach ($hf in @("denominator","statusVocabulary","goldenSamplesInBaseline","stats","rows","gatePrerequisites")) {
    if (-not (Has-Prop $doc $hf)) { Fail ("header-field-missing " + $hf) }
}

# rows must be a JSON array
$matrixRows = @()
if ((Has-Prop $doc "rows") -and -not (Is-JsonArray (Prop-Value $doc "rows"))) {
    Fail "header-field-not-array rows"
} elseif (Has-Prop $doc "rows") {
    # Direct assignment (no @()): the comma-protected array from Prop-Value arrives as one item
    # and must stay the row array, not be re-wrapped into a single-element array.
    $rv = Prop-Value $doc "rows"
    if ($null -eq $rv) { $matrixRows = @() } else { $matrixRows = @($rv) }
}

# statusVocabulary must be a JSON string array equal to the controlled vocabulary
$allowedStatus = @("accepted-component","partial","pending","replaced-by-v5-contract","platform-blocked")
if ((Has-Prop $doc "statusVocabulary") -and -not (Is-JsonArray (Prop-Value $doc "statusVocabulary"))) {
    Fail "header-field-not-array statusVocabulary"
} elseif (Has-Prop $doc "statusVocabulary") {
    $sv = Prop-Value $doc "statusVocabulary"   # protected array arrives intact via direct assignment
    $svBad = $false
    foreach ($s in $sv) { if (-not (Is-JsonString $s)) { Fail "array-element-not-string statusVocabulary"; $svBad = $true; break } }
    if (-not $svBad -and ((@($sv | Sort-Object) -join ",") -ne (@($allowedStatus | Sort-Object) -join ","))) {
        Fail "status-vocabulary-mismatch"
    }
}

# goldenSamplesInBaseline must be a JSON string array
if ((Has-Prop $doc "goldenSamplesInBaseline") -and -not (Is-JsonArray (Prop-Value $doc "goldenSamplesInBaseline"))) {
    Fail "header-field-not-array goldenSamplesInBaseline"
} elseif (Has-Prop $doc "goldenSamplesInBaseline") {
    foreach ($g in (Prop-Value $doc "goldenSamplesInBaseline")) {
        if (-not (Is-JsonString $g)) { Fail "array-element-not-string goldenSamplesInBaseline"; break }
    }
}

# denominator must be an object with a numeric productionSources
if ((Has-Prop $doc "denominator") -and -not (Is-JsonObject (Prop-Value $doc "denominator"))) {
    Fail "header-field-not-object denominator"
} elseif (Has-Prop $doc "denominator") {
    $den = Prop-Value $doc "denominator"
    if (-not (Has-Prop $den "productionSources")) { Fail "header-field-missing denominator.productionSources" }
    elseif (-not (Is-JsonNumber (Prop-Value $den "productionSources"))) { Fail "header-field-not-number denominator.productionSources" }
}

# stats must be an object of objects with numeric values and no unknown keys
if ((Has-Prop $doc "stats") -and -not (Is-JsonObject (Prop-Value $doc "stats"))) {
    Fail "header-field-not-object stats"
}
$modules = @("engine","protocol","server","client","presentation","lwjgl3","android")
if ((Has-Prop $doc "stats") -and (Is-JsonObject (Prop-Value $doc "stats"))) {
    $statsObj = Prop-Value $doc "stats"
    foreach ($part in @("byStatus","byModule")) {
        if (-not (Has-Prop $statsObj $part)) { Fail ("header-field-missing stats." + $part); continue }
        $pObj = Prop-Value $statsObj $part
        if (-not (Is-JsonObject $pObj)) { Fail ("header-field-not-object stats." + $part); continue }
        $expectedKeys = $allowedStatus
        if ($part -eq "byModule") { $expectedKeys = $modules }
        foreach ($p in $pObj.PSObject.Properties) {
            if ($expectedKeys -notcontains $p.Name) { Fail ("header-stats-key-unknown " + $part + " " + $p.Name) }
            if (-not (Is-JsonNumber $p.Value)) { Fail ("header-stats-value-not-number " + $part + " " + $p.Name) }
        }
    }
}

# gatePrerequisites must be an object of JSON string arrays keyed by gates
if ((Has-Prop $doc "gatePrerequisites") -and -not (Is-JsonObject (Prop-Value $doc "gatePrerequisites"))) {
    Fail "header-field-not-object gatePrerequisites"
} elseif (Has-Prop $doc "gatePrerequisites") {
    $gp = Prop-Value $doc "gatePrerequisites"
    foreach ($p in $gp.PSObject.Properties) {
        if ($p.Name -notmatch "^G[0-5]$") { Fail ("header-gate-prereqs-unknown-gate " + $p.Name); continue }
        if (-not (Is-JsonArray $p.Value)) { Fail ("header-gate-prereqs-not-array " + $p.Name); continue }
        foreach ($t in @($p.Value)) {
            if (-not (Is-JsonString $t)) { Fail ("array-element-not-string gatePrerequisites." + $p.Name); break }
        }
    }
}

# --- 2. baseline commit exists (read-only) ---
$bc = $doc.baselineCommit
if (-not (Is-NonEmptyString $bc) -or -not ($bc -match "^[0-9a-f]{40}$")) {
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
if ((Has-Prop $doc "gatePrerequisites") -and (Is-JsonObject (Prop-Value $doc "gatePrerequisites")) -and $gatesFound.Count -ge 6) {
    $gpObj = Prop-Value $doc "gatePrerequisites"
    foreach ($g in $gatesFound) {
        $decl = $gpObj.PSObject.Properties[$g]
        if ($null -eq $decl) { Fail ("header-gate-prereqs-missing-gate " + $g); continue }
        if (-not (Is-JsonArray $decl.Value)) { continue }
        $declList = (@($decl.Value) | Sort-Object) -join ","
        $derivedList = (@($gatePrereqs[$g]) | Sort-Object) -join ","
        if ($declList -ne $derivedList) {
            Fail ("header-gate-prereqs-mismatch " + $g + " declared=[" + $declList + "] taskbook=[" + $derivedList + "]")
        }
    }
}

# --- 3. FROZEN BASELINE tree is the authoritative source set ---
# The matrix describes the frozen snapshot at baselineCommit; neither the current index
# (git ls-files) nor the current worktree may substitute for it. Post-baseline files and
# post-baseline symbol edits must not pass as baseline evidence.
$ErrorActionPreference = "Continue"
$treeLines = & git -C $RepoRoot -c core.quotepath=off ls-tree -r --name-only $bc 2>$null
$treeExit = $LASTEXITCODE
$ErrorActionPreference = "Stop"
if ($treeExit -ne 0 -or @($treeLines).Count -eq 0) {
    Write-Output "RESULT=FAIL"
    Write-Output "REASON=baseline-tree-unreadable $bc"
    exit 1
}
$baselineTree = @{}
foreach ($t in $treeLines) { if (-not $baselineTree.ContainsKey($t)) { $baselineTree[$t] = $true } }

# Plan-path convention per module: row.source = tracked path minus the module's package root.
$pkgPrefix = @{
    "engine"       = @("engine/src/main/kotlin/com/majiang/");
    "protocol"     = @("protocol/src/main/kotlin/com/majiang/");
    "server"       = @("server/src/main/kotlin/com/majiang/server/", "server/src/main/kotlin/com/majiang/");
    "client"       = @("client/src/main/kotlin/com/majiang/client/", "client/src/main/kotlin/com/majiang/");
    "presentation" = @("presentation/src/main/kotlin/com/majiang/presentation/", "presentation/src/main/kotlin/com/majiang/");
    "lwjgl3"       = @("lwjgl3/src/main/kotlin/com/majiang/");
    "android"      = @("android/src/main/kotlin/com/majiang/")
}
$allowedV5Targets = @("Mahjong.Domain","Mahjong.TableRuntime","Mahjong.Contracts","Mahjong.Contracts.Wire","Mahjong.Ai","Mahjong.Client","Mahjong.LobbyService","Mahjong.LocalHost","Mahjong.TableService","Mahjong.Infrastructure")

$realSet = @{}
$realPath = @{}
$realCount = 0
foreach ($l in $baselineTree.Keys) {
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
$rows = $matrixRows
foreach ($r in $rows) {
    if ($null -eq $r) { Fail "rows-element-not-object"; continue }
    if (-not (Is-JsonObject $r)) { Fail "rows-element-not-object"; continue }
    $k = "$($r.module)|$($r.source)"
    if ($rowKeys.ContainsKey($k)) { Fail ("duplicate-source-row: " + $k) } else { $rowKeys[$k] = $r }
}

# --- 3b. set comparison (baseline tree vs rows) ---
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
    if ($null -eq $r -or -not (Is-JsonObject $r)) { continue }
    $s = $r.source
    if (-not (Is-NonEmptyString $s)) { continue }   # typed failure already reported in schema pass
    if ($s -notmatch "\.kt$")           { Fail ("source-not-kt: " + $s) }
    if ($s -match "(^/|[\\:]|\.\.)")    { Fail ("source-not-module-relative: " + $s) }
    if ($s -match "src/test|/build/|generated") { Fail ("source-looks-like-test-or-generated: " + $s) }
}

# --- symbol declaration checks ---
# A lexical state scanner strips comments and string/char literals BEFORE any declaration
# matching: a word that only occurs inside a comment or a literal is not a declaration. The
# scanner (replacing an earlier union regex that leaked nested constructs) handles:
#   - line + block comments; Kotlin block comments NEST (depth counted), C# do not
#   - Kotlin triple-quoted raw strings (no escapes, closed by """), C# verbatim @"..." ("" to
#     escape) and C# raw """...""" strings
#   - interpolated strings ($"..." / $@"...") and Kotlin string templates: the { } holes are
#     scanned as brace-nested CODE (nested strings/chars/comments allowed), so a quoted
#     literal inside a hole ("prefix { "class X" } suffix") cannot leak as source
#   - escape sequences and adjacent quote runs position-by-position
# Stripped spans become a single space; plain code (including lone division slashes) is kept.
# Trigger chars are written as numeric char codes so this file stays pure ASCII under
# PowerShell 5.1's ANSI read.
$script:lexTriggerChars = @([char]47, [char]34, [char]39)                        # / " '
$script:lexHoleChars = @([char]47, [char]34, [char]39, [char]123, [char]125)     # / " ' { }
$script:lexNewlines = @([char]13, [char]10)
$script:strippedCache = @{}

function ConvertTo-LexicallyStripped([string]$text, [bool]$isKotlin) {
    $len = $text.Length
    $sb = New-Object System.Text.StringBuilder($len)
    $i = 0
    while ($i -lt $len) {
        $n = $text.IndexOfAny($script:lexTriggerChars, $i)
        if ($n -lt 0) { [void]$sb.Append($text.Substring($i)); break }
        if ($n -gt $i) { [void]$sb.Append($text.Substring($i, $n - $i)) }
        $i = Invoke-LexToken $text $n $isKotlin $sb
        [void]$sb.Append(' ')
    }
    return $sb.ToString()
}

# Consumes one lexical token starting at $i (a comment, a string/char literal, or a lone '/'
# kept as code) and returns the index of the first character after it.
function Invoke-LexToken([string]$text, [int]$i, [bool]$isKotlin, [System.Text.StringBuilder]$sb) {
    $len = $text.Length
    $c = $text[$i]
    if ($c -eq [char]47) {
        if ($i + 1 -lt $len -and $text[$i + 1] -eq [char]47) {          # // line comment
            $j = $text.IndexOfAny($script:lexNewlines, $i + 1)
            if ($j -lt 0) { return $len }
            return $j
        }
        if ($i + 1 -lt $len -and $text[$i + 1] -eq [char]42) {          # /* block comment
            return Skip-LexBlockComment $text ($i + 2) $isKotlin
        }
        [void]$sb.Append('/')
        return $i + 1
    }
    if ($c -eq [char]34) { return Skip-LexString $text $i $isKotlin }
    if ($c -eq [char]39) { return Skip-LexChar $text $i }
    return $i + 1
}

# Block comment: Kotlin nests (depth counted), C# does not. Unterminated consumes to EOF.
function Skip-LexBlockComment([string]$text, [int]$p, [bool]$isKotlin) {
    $len = $text.Length
    $depth = 1
    while ($p -lt $len) {
        $open = -1
        if ($isKotlin) { $open = $text.IndexOf('/*', $p) }
        $close = $text.IndexOf('*/', $p)
        if ($close -lt 0) { return $len }
        if ($open -ge 0 -and $open -lt $close) { $depth++; $p = $open + 2 }
        else {
            $depth--
            if ($depth -le 0) { return $close + 2 }
            $p = $close + 2
        }
    }
    return $len
}

# Char literal: escapes skipped; an unterminated literal ends at the newline defensively.
function Skip-LexChar([string]$text, [int]$i) {
    $len = $text.Length
    $p = $i + 1
    while ($p -lt $len) {
        $c = $text[$p]
        if ($c -eq [char]92) { $p += 2; continue }                      # backslash escape
        if ($c -eq [char]39) { return $p + 1 }
        if ($c -eq [char]10 -or $c -eq [char]13) { return $p }
        $p += 1
    }
    return $len
}

# String dispatch at the opening quote ($i). Prefixes are read from the ORIGINAL text:
# C# $"..." / $@"..." / @"..." / """...""", Kotlin """...""". Hole opening is PER LANGUAGE:
# Kotlin normal strings open a template hole only at an unescaped "${"; C# normal strings
# never (unless the $" interpolation prefix is present, where "{" opens and "{{" escapes).
function Skip-LexString([string]$text, [int]$i, [bool]$isKotlin) {
    $prev = if ($i -gt 0) { $text[$i - 1] } else { ' ' }
    $prev2 = if ($i -gt 1) { $text[$i - 2] } else { ' ' }
    if ($text.IndexOf('"""', $i) -eq $i) {
        if ($isKotlin) { return Skip-LexKotlinRawString $text ($i + 3) }
        return Skip-LexRawString $text ($i + 3)
    }
    $interp = ($prev -eq [char]36) -or ($prev -eq [char]64 -and $prev2 -eq [char]36)
    $verbatim = $prev -eq [char]64
    if ($verbatim -and -not $isKotlin) { return Skip-LexVerbatimString $text ($i + 1) $interp }
    if ($isKotlin) { return Skip-LexNormalString $text ($i + 1) $true $false }
    return Skip-LexNormalString $text ($i + 1) $false $interp
}

# Normal string (escapes on). Hole opening is per language: Kotlin only at an unescaped "${"
# (a plain "{" is literal content and never opens a hole); C# only when the string carries the
# interpolation prefix ("{" opens, "{{" is an escaped literal brace). " closes the string.
function Skip-LexNormalString([string]$text, [int]$p, [bool]$isKotlin, [bool]$isInterp) {
    $len = $text.Length
    while ($p -lt $len) {
        $c = $text[$p]
        if ($c -eq [char]92) { $p += 2; continue }
        if ($c -eq [char]34) { return $p + 1 }
        if ($isKotlin) {
            if ($c -eq [char]36 -and $p + 1 -lt $len -and $text[$p + 1] -eq [char]123) {
                $p = Invoke-LexCodeHole $text ($p + 2) $true
                continue
            }
        }
        elseif ($isInterp -and $c -eq [char]123) {
            if ($p + 1 -lt $len -and $text[$p + 1] -eq [char]123) { $p += 2; continue }
            $p = Invoke-LexCodeHole $text ($p + 1) $false
            continue
        }
        if ($c -eq [char]10 -or $c -eq [char]13) { return $p }          # unterminated
        $p += 1
    }
    return $len
}

# Interpolation/template hole: scan as code until the "}" that closes the hole at brace depth
# zero. Nested strings, chars, comments and braces inside follow their own rules, so nested
# literals cannot terminate the hole or leak their content as declarations.
function Invoke-LexCodeHole([string]$text, [int]$p, [bool]$isKotlin) {
    $len = $text.Length
    $depth = 0
    while ($p -lt $len) {
        $n = $text.IndexOfAny($script:lexHoleChars, $p)
        if ($n -lt 0) { return $len }
        $c = $text[$n]
        if ($c -eq [char]123) { $depth++; $p = $n + 1; continue }
        if ($c -eq [char]125) {
            if ($depth -eq 0) { return $n + 1 }
            $depth--
            $p = $n + 1
            continue
        }
        if ($c -eq [char]47) {
            if ($n + 1 -lt $len -and $text[$n + 1] -eq [char]47) {
                $j = $text.IndexOfAny($script:lexNewlines, $n + 1)
                if ($j -lt 0) { return $len }
                $p = $j
                continue
            }
            if ($n + 1 -lt $len -and $text[$n + 1] -eq [char]42) {
                $p = Skip-LexBlockComment $text ($n + 2) $isKotlin
                continue
            }
            $p = $n + 1
            continue
        }
        if ($c -eq [char]34) { $p = Skip-LexString $text $n $isKotlin; continue }
        $p = Skip-LexChar $text $n
    }
    return $len
}

# C# verbatim string @"...": no escapes, "" is an escaped quote, spans newlines. Only an
# interpolated verbatim ($@"...") scans for "{" holes; a PLAIN verbatim string treats "{" as
# ordinary content (it must never terminate the string or expose its payload).
function Skip-LexVerbatimString([string]$text, [int]$p, [bool]$interp) {
    $len = $text.Length
    $trig = @([char]34)
    if ($interp) { $trig = @([char]34, [char]123) }
    while ($p -lt $len) {
        $n = $text.IndexOfAny($trig, $p)
        if ($n -lt 0) { return $len }
        if ($interp -and $text[$n] -eq [char]123) {
            if ($n + 1 -lt $len -and $text[$n + 1] -eq [char]123) { $p = $n + 2; continue }
            $p = Invoke-LexCodeHole $text ($n + 1) $false
            continue
        }
        if ($n + 1 -lt $len -and $text[$n + 1] -eq [char]34) { $p = $n + 2; continue }
        return $n + 1
    }
    return $len
}

# C# raw string """...""" (C# 11): closed by a run of at least three quotes.
function Skip-LexRawString([string]$text, [int]$p) {
    $len = $text.Length
    while ($p -lt $len) {
        $n = $text.IndexOf([char]34, $p)
        if ($n -lt 0) { return $len }
        $run = 0
        while ($n + $run -lt $len -and $text[$n + $run] -eq [char]34) { $run++ }
        if ($run -ge 3) { return $n + 3 }
        $p = $n + $run
    }
    return $len
}

# Kotlin raw string """...""" : no escapes, closed by """, ${...} template holes are scanned
# as code.
function Skip-LexKotlinRawString([string]$text, [int]$p) {
    $len = $text.Length
    while ($p -lt $len) {
        $hole = $text.IndexOf('${', $p)
        $end = $text.IndexOf('"""', $p)
        if ($hole -ge 0 -and ($end -lt 0 -or $hole -lt $end)) {
            $p = Invoke-LexCodeHole $text ($hole + 2) $true
            continue
        }
        if ($end -lt 0) { return $len }
        return $end + 3
    }
    return $len
}

function Get-StrippedText([string]$path) {
    if ($script:strippedCache.ContainsKey($path)) { return $script:strippedCache[$path] }
    $t = ConvertTo-LexicallyStripped (Get-BaselineBlob $path) ($path -match "\.kt$")
    $script:strippedCache[$path] = $t
    return $t
}

# A legal single identifier (enforced, not just documented) before any declaration matching.
function Test-IdentifierSymbol([string]$symbol) {
    return ($symbol -match "^[A-Za-z_][A-Za-z0-9_]*$")
}

# Type declarations for v5/test anchors: the declared name must DIRECTLY follow the keyword
# (only whitespace between). This rejects supertype positions ("class X : Y"), generic
# parameter positions ("class Box<T>"), keyword substrings and comment/string text. Modifiers
# (public/sealed/data/enum/fun ...) precede the keyword, so a bare keyword can never be the
# declared name. Language is chosen by file extension (.cs -> C# keywords, .kt -> Kotlin
# keywords, unknown -> either).
function Test-AnchorSymbol([string]$path, [string]$symbol) {
    if ([string]::IsNullOrWhiteSpace($symbol)) { return $false }
    if (-not (Test-IdentifierSymbol $symbol)) { return $false }
    $rx = [regex]::Escape($symbol)
    $t = Get-StrippedText $path
    if ($path -match "\.cs$") {
        return [regex]::IsMatch($t, "\b(?:class|struct|interface|enum|record)\s+" + $rx + "\b")
    }
    if ($path -match "\.kt$") {
        return [regex]::IsMatch($t, "\b(?:class|interface|object)\s+" + $rx + "\b")
    }
    return ([regex]::IsMatch($t, "\b(?:class|struct|interface|enum|record)\s+" + $rx + "\b") -or
            [regex]::IsMatch($t, "\b(?:class|interface|object)\s+" + $rx + "\b"))
}

# Kotlin declaration forms for v4Symbol on the row's own baseline blob (comments/strings
# stripped first). Declarations only:
#   type declarations: name directly after class/interface/object (enum/data/sealed/abstract/
#     open/inner/fun modifiers precede the keyword)
#   functions: fun [typeparams] [Receiver.]name(   (top-level, member, extension, generic and
#     receiver forms; the name must be immediately followed by "(")
#   typealias: name directly after the keyword
#   properties and function receivers are parsed STRUCTURALLY (Get-KotlinPropertyChain):
#     Segment := identifier [ balanced "<...>" type-args ] [ "?" ]
#     Chain   := Segment ( "." Segment )*      -- dots may carry legal whitespace both sides
#   so nullable receivers ("val String?.nullableProperty: Int"), multi-parameter/nested
#   generic receivers ("val Map<String, Int>.genericProperty: Int", comma/whitespace/variance
#   inside the type args) and qualified chains parse to their real LAST segment. A flat
#   character set is deliberately NOT used: it would truncate at "?" or inside generics and
#   fake a declaration out of the receiver (and reject the real name). The DECLARED name is
#   the LAST segment after val/var/const val; a symbol in a non-final chain segment is a
#   receiver and is NOT a declaration; a symbol inside type-args is consumed wholesale and
#   is NOT a declaration either.
# Receiver names, parameter types, supertypes and generic parameters are NOT declarations.

# Structurally parses one Kotlin declaration chain starting at index $start (right after the
# val/var/const val keyword or the fun type-parameter list):
#   Segment := identifier [ balanced "<...>" type-args ] [ "?" ]
#   Chain   := Segment ( "." Segment )*
# Type args are consumed as a balanced bracket group (commas, whitespace, variance
# modifiers, "?" and nested/qualified types inside are inert), "?" nullable suffixes are
# consumed, and dots may carry legal whitespace on either side. Returns $null when the text
# at $start is not a well-formed declaration chain; otherwise an object with:
#   Segments = chain segment names (LAST segment is the declared name, earlier are receiver
#              parts); type-arg contents never become segments
#   End      = index just past the chain (for the caller's tight-syntax check)
function Get-KotlinPropertyChain([string]$t, [int]$start) {
    $len = $t.Length
    $p = $start
    $segs = New-Object System.Collections.Generic.List[string]
    while ($true) {
        while ($p -lt $len -and [char]::IsWhiteSpace($t[$p])) { $p++ }
        if ($p -ge $len) { return $null }
        $c = $t[$p]
        if (-not (($c -ge [char]65 -and $c -le [char]90) -or ($c -ge [char]97 -and $c -le [char]122) -or $c -eq [char]95)) { return $null }
        $nameStart = $p
        $p++
        while ($p -lt $len) {
            $d = $t[$p]
            if (($d -ge [char]65 -and $d -le [char]90) -or ($d -ge [char]97 -and $d -le [char]122) -or ($d -ge [char]48 -and $d -le [char]57) -or $d -eq [char]95) { $p++ } else { break }
        }
        $segs.Add($t.Substring($nameStart, $p - $nameStart))
        # optional type args: balanced angle brackets (whitespace allowed before "<")
        $q = $p
        while ($q -lt $len -and [char]::IsWhiteSpace($t[$q])) { $q++ }
        if ($q -lt $len -and $t[$q] -eq [char]60) {
            $depth = 0
            while ($q -lt $len) {
                $g = $t[$q]
                if ($g -eq [char]60) { $depth++ } elseif ($g -eq [char]62) { $depth-- }
                $q++
                if ($depth -eq 0) { break }
            }
            if ($depth -ne 0) { return $null }   # unterminated type args: not a declaration
            $p = $q
        }
        # nullable suffix ("?"), whitespace allowed before it
        while ($p -lt $len -and [char]::IsWhiteSpace($t[$p])) { $p++ }
        if ($p -lt $len -and $t[$p] -eq [char]63) { $p++ }
        # chain continuation: "." with a legal identifier start after it (whitespace both sides)
        $r = $p
        while ($r -lt $len -and [char]::IsWhiteSpace($t[$r])) { $r++ }
        if ($r -lt $len -and $t[$r] -eq [char]46) {
            $s = $r + 1
            while ($s -lt $len -and [char]::IsWhiteSpace($t[$s])) { $s++ }
            if ($s -lt $len) {
                $e = $t[$s]
                if (($e -ge [char]65 -and $e -le [char]90) -or ($e -ge [char]97 -and $e -le [char]122) -or $e -eq [char]95) { $p = $s; continue }
            }
        }
        return [pscustomobject]@{ Segments = $segs; End = $p }
    }
}

# Function declarations: "fun" [typeparams] [receiver-chain] name "(" -- the receiver chain
# and type args are parsed structurally (see Get-KotlinPropertyChain), so receivers like
# "Map<String, Int>" or "String?" can never be truncated into a fake name, and the declared
# name must be immediately followed by "(" (tight syntax, optional whitespace).
function Test-KotlinFunDeclaration([string]$t, [string]$symbol) {
    $len = $t.Length
    foreach ($m in [regex]::Matches($t, "(?<![A-Za-z0-9_])fun(?![A-Za-z0-9_])")) {
        $p = $m.Index + 3
        while ($p -lt $len -and [char]::IsWhiteSpace($t[$p])) { $p++ }
        if ($p -lt $len -and $t[$p] -eq [char]60) {           # optional type-parameter list
            $depth = 0
            while ($p -lt $len) {
                $g = $t[$p]
                if ($g -eq [char]60) { $depth++ } elseif ($g -eq [char]62) { $depth-- }
                $p++
                if ($depth -eq 0) { break }
            }
            if ($depth -ne 0) { continue }
        }
        $chain = Get-KotlinPropertyChain $t $p
        if ($null -eq $chain -or $chain.Segments.Count -eq 0) { continue }
        $q = $chain.End
        while ($q -lt $len -and [char]::IsWhiteSpace($t[$q])) { $q++ }
        if ($q -ge $len -or $t[$q] -ne [char]40) { continue } # tight syntax: name "("
        if ($chain.Segments[$chain.Segments.Count - 1] -ne $symbol) { continue }
        $isReceiver = $false
        for ($k = 0; $k -lt $chain.Segments.Count - 1; $k++) {
            if ($chain.Segments[$k] -eq $symbol) { $isReceiver = $true; break }
        }
        if (-not $isReceiver) { return $true }
    }
    return $false
}

function Test-KotlinDeclaredSymbol([string]$text, [string]$symbol) {
    if ([string]::IsNullOrWhiteSpace($symbol)) { return $false }
    if (-not (Test-IdentifierSymbol $symbol)) { return $false }
    $rx = [regex]::Escape($symbol)
    $t = ConvertTo-LexicallyStripped $text $true
    if ([regex]::IsMatch($t, "\b(?:class|interface|object)\s+" + $rx + "\b")) { return $true }
    if (Test-KotlinFunDeclaration $t $symbol) { return $true }
    if ([regex]::IsMatch($t, "\btypealias\s+" + $rx + "\b")) { return $true }
    # Property chain: structurally parsed (Get-KotlinPropertyChain). The chain must start
    # right after "val"/"var"/"const val", so the FIRST segment can never be mistaken for a
    # longer name; the declared name is the LAST segment and receiver segments never declare.
    foreach ($kw in [regex]::Matches($t, "(?<![A-Za-z0-9_])(const\s+val|val|var)(?![A-Za-z0-9_])")) {
        $chain = Get-KotlinPropertyChain $t ($kw.Index + $kw.Length)
        if ($null -eq $chain -or $chain.Segments.Count -eq 0) { continue }
        if ($chain.Segments[$chain.Segments.Count - 1] -ne $symbol) { continue }
        $isReceiver = $false
        for ($k = 0; $k -lt $chain.Segments.Count - 1; $k++) {
            if ($chain.Segments[$k] -eq $symbol) { $isReceiver = $true; break }
        }
        if (-not $isReceiver) { return $true }
    }
    return $false
}

# --- frozen-baseline blob access (one bulk git cat-file --batch pass + cache) ---
$script:blobCache = @{}
$script:shaToPath = @{}

function Add-BlobRequest([string]$path) {
    if ([string]::IsNullOrEmpty($path)) { return }
    if ($script:blobCache.ContainsKey($path)) { return }
    if (-not $script:baselineTree.ContainsKey($path)) { return }   # membership failures reported separately
    $script:blobCache[$path] = $null                                # reserve; filled by bulk load
}

function Load-BaselineBlobs {
    if ($script:blobCache.Keys.Count -eq 0) { return }
    # resolve blob shas for the requested paths from the baseline tree (with SHAs)
    $ErrorActionPreference = "Continue"
    $treeFull = & git -C $script:RepoRoot -c core.quotepath=off ls-tree -r $script:bc 2>$null
    $ErrorActionPreference = "Stop"
    foreach ($line in $treeFull) {
        # ls-tree default line: "<mode> blob <sha> TAB <path>" (mode prefix included)
        if ($line -match "^[0-6]{6}\s+blob\s+([0-9a-f]{40})\t(.+)$") {
            $sha = $Matches[1]; $p = $Matches[2]
            if ($script:blobCache.ContainsKey($p)) { $script:shaToPath[$sha] = $p }
        }
    }
    $shas = @($script:shaToPath.Keys)
    if ($shas.Count -eq 0) { return }
    $ErrorActionPreference = "Continue"
    $raw = $shas | & git -C $script:RepoRoot cat-file --batch 2>$null
    $ErrorActionPreference = "Stop"
    $currentPath = $null
    $content = New-Object System.Collections.Generic.List[string]
    foreach ($line in $raw) {
        $s = "$line"
        if ($s -match "^([0-9a-f]{40})\s+blob\s+\d+$") {
            if ($null -ne $currentPath) { $script:blobCache[$currentPath] = ($content -join "`n") }
            $currentPath = $script:shaToPath[$Matches[1]]
            $content = New-Object System.Collections.Generic.List[string]
            continue
        }
        if ($null -ne $currentPath) { $content.Add($s) }
    }
    if ($null -ne $currentPath) { $script:blobCache[$currentPath] = ($content -join "`n") }
}

function Get-BaselineBlob([string]$path) {
    # A reserved entry ($null) means the bulk pass could not fill it (e.g. two paths sharing one
    # blob sha) -> fall through to a direct per-path read instead of returning null.
    if ($script:blobCache.ContainsKey($path) -and $null -ne $script:blobCache[$path]) { return $script:blobCache[$path] }
    $ErrorActionPreference = "Continue"
    $content = (& git -C $script:RepoRoot -c core.quotepath=off cat-file blob ($script:bc + ":" + $path) 2>$null) -join "`n"
    $ErrorActionPreference = "Stop"
    $script:blobCache[$path] = $content
    return $content
}

# pre-request every baseline blob this run will need: row sources + all anchor files
foreach ($p in $realPath.Values) { Add-BlobRequest $p }
foreach ($r in $rows) {
    if ($null -eq $r -or -not (Is-JsonObject $r)) { continue }
    foreach ($f in @("v5Anchors","testAnchors")) {
        if (-not (Has-Prop $r $f)) { continue }
        $arr = Prop-Value $r $f
        if (-not (Is-JsonArray $arr)) { continue }
        foreach ($a in @($arr)) {
            if (-not (Is-JsonObject $a)) { continue }
            $p = Prop-Value $a "path"
            if (Is-JsonString $p) { Add-BlobRequest $p }
        }
    }
}
Load-BaselineBlobs

# --- 4/4b/5/5b/6. per-row schema, taskbook check, frozen anchors, responsibility ---
$statusCount = @{}
$moduleCount = @{}
$rowIds = @{}
foreach ($r in $rows) {
    if ($null -eq $r -or -not (Is-JsonObject $r)) { continue }
    $tag = "[" + $r.id + " " + $r.module + "/" + $r.source + "]"

    # 4. required non-empty JSON strings (no coercion: 42 / true / null are failures)
    foreach ($f in @("id","module","source","v4Symbol","planTarget","status","responsibleTask","gapSummary","notes")) {
        if (-not (Has-Prop $r $f)) { Fail ($tag + " row-field-missing " + $f) }
        elseif (-not (Is-NonEmptyString (Prop-Value $r $f))) { Fail ($tag + " row-field-not-string " + $f) }
    }
    # id format + global uniqueness (only decidable for proper strings)
    if ((Has-Prop $r "id") -and (Is-JsonString (Prop-Value $r "id"))) {
        $idVal = Prop-Value $r "id"
        if ($idVal -notmatch "^[A-Z]{3}-[0-9]{3}$") { Fail ($tag + " bad-row-id: " + $idVal) }
        if ($rowIds.ContainsKey($idVal)) { Fail ($tag + " duplicate-row-id: " + $idVal) }
        else { $rowIds[$idVal] = $true }
    }
    # 4. required JSON arrays (null/scalar/object values are failures; empty lists legal)
    $depsArr = @()
    $v5tArr = @()
    $goldenArr = @()
    $v5aArr = @()
    $testaArr = @()
    $accaArr = @()
    foreach ($f in @("v5Targets","dependencies","v5Anchors","testAnchors","acceptanceAnchors","goldenSamples")) {
        if (-not (Has-Prop $r $f)) { Fail ($tag + " row-field-missing " + $f); continue }
        $fv = Prop-Value $r $f
        if (-not (Is-JsonArray $fv)) { Fail ($tag + " row-field-not-array " + $f); continue }
        switch ($f) {
            "dependencies"      { $depsArr = @($fv) }
            "v5Targets"         { $v5tArr = @($fv) }
            "goldenSamples"     { $goldenArr = @($fv) }
            "v5Anchors"         { $v5aArr = @($fv) }
            "testAnchors"       { $testaArr = @($fv) }
            "acceptanceAnchors" { $accaArr = @($fv) }
        }
    }

    if ($modules -notcontains $r.module) { Fail ($tag + " unknown-module: " + $r.module) }
    if ($allowedStatus -notcontains $r.status) { Fail ($tag + " unknown-status: " + $r.status) }

    # 4. v5Targets: >=1 entry, every entry a JSON string in the controlled vocabulary
    if ((Has-Prop $r "v5Targets") -and (Is-JsonArray (Prop-Value $r "v5Targets"))) {
        if ($v5tArr.Count -eq 0) { Fail ($tag + " v5-targets-empty") }
        foreach ($t in $v5tArr) {
            if (-not (Is-JsonString $t)) { Fail ($tag + " array-element-not-string v5Targets"); continue }
            if (-not $t.StartsWith("Unity(") -and $allowedV5Targets -notcontains $t) { Fail ($tag + " unknown-v5-target-module: " + $t) }
        }
    }

    # 4b. responsibility: real taskbook IDs; no self-dependency; no gate cycles
    $respTask = $null
    if ((Has-Prop $r "responsibleTask") -and (Is-JsonString (Prop-Value $r "responsibleTask"))) { $respTask = Prop-Value $r "responsibleTask" }
    if (-not (Is-NonEmptyString $respTask)) {
        Fail ($tag + " missing-responsible-task")
    } elseif (-not $taskIds.ContainsKey($respTask)) {
        Fail ($tag + " responsible-task-not-in-taskbook: " + $respTask)
    }
    foreach ($d in $depsArr) {
        if (-not (Is-NonEmptyString $d)) { Fail ($tag + " array-element-not-string dependencies"); continue }
        if (-not $taskIds.ContainsKey($d)) { Fail ($tag + " dependency-not-in-taskbook: " + $d); continue }
        if (-not (Is-NonEmptyString $respTask)) { continue }
        if ($d -eq $respTask) { Fail ($tag + " self-task-dependency: " + $d); continue }
        if ($d -match "^G[0-5]$") {
            $prereqs = $gatePrereqs[$d]
            if ($null -ne $prereqs -and $prereqs.Contains($respTask)) {
                Fail ($tag + " gate-dependency-cycle: " + $d + " itself requires " + $respTask)
            }
        }
    }

    $statusCount[$r.status] = 1 + ($statusCount[$r.status] | ForEach-Object { if ($_) { $_ } else { 0 } })
    $moduleCount[$r.module] = 1 + ($moduleCount[$r.module] | ForEach-Object { if ($_) { $_ } else { 0 } })

    # 5. v5Anchors: typed elements, canonical, category, IN BASELINE TREE, symbol in BASELINE BLOB
    foreach ($a in $v5aArr) {
        if (-not (Is-JsonObject $a)) { Fail ($tag + " anchor-element-not-object v5Anchors"); continue }
        if (-not (Has-Prop $a "path")) { Fail ($tag + " anchor-field-missing path"); continue }
        if (-not (Is-JsonString (Prop-Value $a "path"))) { Fail ($tag + " anchor-field-not-string path"); continue }
        $p = Prop-Value $a "path"
        if (-not (Test-CanonicalRepoPath $p)) { Fail ($tag + " anchor-path-not-canonical: " + $p); continue }
        if (-not $p.StartsWith("dotnet/src/")) { Fail ($tag + " v5-anchor-not-v5-source: " + $p) }
        elseif (-not $baselineTree.ContainsKey($p)) { Fail ($tag + " v5-anchor-not-in-baseline-tree: " + $p) }
        if (-not (Has-Prop $a "symbol")) { Fail ($tag + " v5-anchor-symbol-missing"); continue }
        if (-not (Is-NonEmptyString (Prop-Value $a "symbol"))) { Fail ($tag + " anchor-field-not-string symbol"); continue }
        $sym = Prop-Value $a "symbol"
        if (-not (Test-IdentifierSymbol $sym)) {
            Fail ($tag + " anchor-symbol-not-identifier: " + $sym)
        }
        elseif ($baselineTree.ContainsKey($p) -and -not (Test-AnchorSymbol $p $sym)) {
            Fail ($tag + " v5-anchor-symbol-not-declared-at-baseline: " + $sym + " in " + $p)
        }
    }
    # 5. testAnchors: typed elements, canonical, test category, IN BASELINE TREE, symbol in BASELINE BLOB
    foreach ($a in $testaArr) {
        if (-not (Is-JsonObject $a)) { Fail ($tag + " anchor-element-not-object testAnchors"); continue }
        if (-not (Has-Prop $a "path")) { Fail ($tag + " anchor-field-missing path"); continue }
        if (-not (Is-JsonString (Prop-Value $a "path"))) { Fail ($tag + " anchor-field-not-string path"); continue }
        $p = Prop-Value $a "path"
        if (-not (Test-CanonicalRepoPath $p)) { Fail ($tag + " anchor-path-not-canonical: " + $p); continue }
        if (-not ($p.Contains("/src/test/") -or $p.StartsWith("dotnet/tests/"))) { Fail ($tag + " test-anchor-not-test-source: " + $p) }
        elseif (-not $baselineTree.ContainsKey($p)) { Fail ($tag + " test-anchor-not-in-baseline-tree: " + $p) }
        if (-not (Has-Prop $a "symbol")) { Fail ($tag + " test-anchor-symbol-missing"); continue }
        if (-not (Is-NonEmptyString (Prop-Value $a "symbol"))) { Fail ($tag + " anchor-field-not-string symbol"); continue }
        $sym = Prop-Value $a "symbol"
        if (-not (Test-IdentifierSymbol $sym)) {
            Fail ($tag + " anchor-symbol-not-identifier: " + $sym)
        }
        elseif ($baselineTree.ContainsKey($p) -and -not (Test-AnchorSymbol $p $sym)) {
            Fail ($tag + " test-anchor-symbol-not-declared-at-baseline: " + $sym + " in " + $p)
        }
    }
    # 5. acceptanceAnchors: typed elements, doc under doc/ and in baseline tree, commit REAL evidence
    foreach ($a in $accaArr) {
        if (-not (Is-JsonObject $a)) { Fail ($tag + " anchor-element-not-object acceptanceAnchors"); continue }
        foreach ($sf in @("doc","commit")) {
            if (-not (Has-Prop $a $sf)) { Fail ($tag + " anchor-field-missing " + $sf) }
            elseif (-not (Is-NonEmptyString (Prop-Value $a $sf))) { Fail ($tag + " anchor-field-not-string " + $sf) }
        }
        if (-not (Has-Prop $a "doc") -or -not (Is-NonEmptyString (Prop-Value $a "doc"))) { continue }
        if (-not (Has-Prop $a "commit") -or -not (Is-NonEmptyString (Prop-Value $a "commit"))) { continue }
        $docPath = Prop-Value $a "doc"
        $commit = Prop-Value $a "commit"
        if (-not (Test-CanonicalRepoPath $docPath)) { Fail ($tag + " anchor-path-not-canonical: " + $docPath); continue }
        if (-not $docPath.StartsWith("doc/")) { Fail ($tag + " acceptance-doc-not-under-doc: " + $docPath) }
        if (-not ($commit -match "^[0-9a-f]{7,40}$")) { Fail ($tag + " acceptance-commit-bad: " + $commit); continue }
        # Doc membership at the baseline is checked with cat-file -e on "<bc>:<doc>" instead of a
        # tree lookup: doc paths may be non-ASCII, which ls-tree output cannot reliably round-trip
        # through the console encoding, while a direct object argument can.
        $ErrorActionPreference = "Continue"
        & git -C $RepoRoot cat-file -e ($bc + ":" + $docPath) 2>$null
        if ($LASTEXITCODE -ne 0) { Fail ($tag + " acceptance-doc-not-in-baseline-tree: " + $docPath) }
        & git -C $RepoRoot cat-file -e ($commit + "^{commit}") 2>$null
        if ($LASTEXITCODE -ne 0) {
            Fail ($tag + " acceptance-commit-not-in-repo: " + $commit)
        } else {
            & git -C $RepoRoot merge-base --is-ancestor $commit $bc 2>$null
            if ($LASTEXITCODE -ne 0) { Fail ($tag + " acceptance-commit-not-ancestor-of-baseline: " + $commit) }
            & git -C $RepoRoot cat-file -e ($commit + ":" + $docPath) 2>$null
            if ($LASTEXITCODE -ne 0) { Fail ($tag + " acceptance-doc-missing-at-commit: " + $docPath + "@" + $commit) }
        }
        $ErrorActionPreference = "Stop"
    }
    # 5. golden samples: controlled directory + IN BASELINE TREE
    foreach ($g in $goldenArr) {
        if (-not (Is-JsonString $g)) { Fail ($tag + " array-element-not-string goldenSamples"); continue }
        if (-not (Test-CanonicalRepoPath $g)) { Fail ($tag + " anchor-path-not-canonical: " + $g); continue }
        if (-not $g.StartsWith("tests/v5-golden/")) { Fail ($tag + " golden-sample-path-not-controlled: " + $g) }
        if (-not $baselineTree.ContainsKey($g)) { Fail ($tag + " golden-sample-not-in-baseline-tree: " + $g) }
    }

    # 5b. v4Symbol must be declared in the BASELINE BLOB of the row's own source file
    $srcKey = "$($r.module)|$($r.source)"
    if ($realPath.ContainsKey($srcKey)) {
        $srcFull = $realPath[$srcKey]
        if (-not (Test-IdentifierSymbol "$($r.v4Symbol)")) {
            Fail ($tag + " v4-symbol-not-identifier: " + $r.v4Symbol)
        }
        elseif (-not (Test-KotlinDeclaredSymbol (Get-BaselineBlob $srcFull) "$($r.v4Symbol)")) {
            Fail ($tag + " v4-symbol-not-declared-at-baseline: " + $r.v4Symbol + " in " + $srcFull)
        }
    }

    # 6. status-specific responsibility rules (uses the pre-extracted typed arrays)
    if ($r.status -eq "accepted-component") {
        if ($v5aArr.Count -eq 0 -or -not (Is-JsonObject $v5aArr[0])) {
            Fail ($tag + " accepted-component-without-v5-anchor")
        }
        if ($accaArr.Count -eq 0 -or -not (Is-JsonObject $accaArr[0])) {
            Fail ($tag + " accepted-component-without-acceptance-anchor")
        }
    } else {
        if (-not (Is-NonEmptyString (Prop-Value $r "gapSummary"))) { Fail ($tag + " status-needs-gap-summary: " + $r.status) }
        if (-not (Is-NonEmptyString $respTask)) { Fail ($tag + " status-needs-responsible-task: " + $r.status) }
    }
}

# --- 7. denominator + stats self-consistency ---
$denOk = $false
if ((Has-Prop $doc "denominator") -and (Is-JsonObject (Prop-Value $doc "denominator")) -and (Has-Prop (Prop-Value $doc "denominator") "productionSources") -and (Is-JsonNumber (Prop-Value (Prop-Value $doc "denominator") "productionSources"))) {
    $denOk = $true
    $denVal = Prop-Value (Prop-Value $doc "denominator") "productionSources"
    if ($denVal -ne $rows.Count) {
        Fail ("denominator-mismatch declared=" + $denVal + " rows=" + $rows.Count)
    }
}
if ($rows.Count -ne $realCount) {
    Fail ("coverage-mismatch rows=" + $rows.Count + " enumerated-tracked=" + $realCount)
}
if ((Has-Prop $doc "stats") -and (Is-JsonObject (Prop-Value $doc "stats"))) {
    $statsObj = Prop-Value $doc "stats"
    foreach ($st in $allowedStatus) {
        $declared = 0
        if ($null -ne $statsObj.byStatus -and $null -ne $statsObj.byStatus.PSObject.Properties[$st]) { $declared = $statsObj.byStatus.$st }
        $computed = 0
        if ($statusCount[$st]) { $computed = $statusCount[$st] }
        if ($declared -ne $computed) { Fail ("stats-by-status-mismatch " + $st + " declared=" + $declared + " computed=" + $computed) }
    }
    foreach ($m in $modules) {
        $declared = 0
        if ($null -ne $statsObj.byModule -and $null -ne $statsObj.byModule.PSObject.Properties[$m]) { $declared = $statsObj.byModule.$m }
        $computed = 0
        if ($moduleCount[$m]) { $computed = $moduleCount[$m] }
        if ($declared -ne $computed) { Fail ("stats-by-module-mismatch " + $m + " declared=" + $declared + " computed=" + $computed) }
    }
}

# --- header golden list: canonical, controlled, IN BASELINE TREE ---
if ((Has-Prop $doc "goldenSamplesInBaseline") -and (Is-JsonArray (Prop-Value $doc "goldenSamplesInBaseline"))) {
    foreach ($g in (Prop-Value $doc "goldenSamplesInBaseline")) {
        if (-not (Is-JsonString $g)) { continue }   # typed failure already reported
        if (-not (Test-CanonicalRepoPath $g)) { Fail ("anchor-path-not-canonical: " + $g); continue }
        if (-not $g.StartsWith("tests/v5-golden/")) { Fail ("golden-sample-path-not-controlled: " + $g) }
        if (-not $baselineTree.ContainsKey($g)) { Fail ("header-golden-sample-not-in-baseline-tree: " + $g) }
    }
}

# --- report ---
Note ("tracked-main-kt=" + $realCount + " (frozen baseline tree)")
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
