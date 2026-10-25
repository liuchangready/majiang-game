# V5-G1-N3 closeout ledger verifier. READ-ONLY: it reads the repository, the
# ledger TSV and the pinned frozen-census evidence, and never writes anything
# outside its own process. This is the N3 closeout ledger check, not the
# unified verifier and not a substitute for any other gate.
#
# Checks:
#   1. census evidence SHA pins (instances.tsv 049a097b..., rules.tsv 24959db1...),
#      1516 normalized paths, and a full re-derivation of the mapping-v5
#      adjudication (655145 units / 592 gap / 17 UNA / 27 mixed paths);
#   2. ledger set equality with the re-derived 592-instance gap set (unique
#      stable IDs, no leak, no duplicate, no unknown package or fixture);
#   3. the 14-package counts close on 592 with the pinned per-package vector;
#   4. bound/classified/pending mandatory columns: bound needs a resolvable
#      current C# anchor plus an accepted commit present in this history plus
#      evidence; classified must not be counted as full and needs full
#      reason/owner/revisit; pending needs owner plus a dated revisit;
#   5. incremental fixtures (movement-v1, shuffle-reversed-v1, visibility-v1)
#      never enter the ledger denominator.
#
# Exit codes: 0 all checks pass; 1 verification failure; 2 usage; 3 missing input.

param(
  [string]$RepoRoot = '',
  [string]$LedgerPath = '',
  [string]$EvidenceDir = 'D:/majiang-wt/codex-n3-r7-evidence-7816dc7072d04451862c965b0c0029d7'
)
$ErrorActionPreference = 'Stop'
$failures = 0
function Fail([string]$name, [string]$msg) {
  Write-Host ("CHECK-FAIL " + $name + ": " + $msg)
  Write-Host ("FAIL-TOKEN " + $name)
  $script:failures++
}
function Pass([string]$name, [string]$msg) { Write-Host ("CHECK-OK " + $name + ": " + $msg) }

$docDir = 'doc/v5-' + [char]0x91CD + [char]0x6784
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
if (-not $LedgerPath) { $LedgerPath = Join-Path $RepoRoot (Join-Path $docDir 'V5-G1-N3-CLOSEOUT-LEDGER.tsv') }
$instFile = Join-Path $EvidenceDir 'census/instances.tsv'
$rulesFile = Join-Path $EvidenceDir 'rules.tsv'
foreach ($f in @($LedgerPath, $instFile, $rulesFile)) {
  if (-not (Test-Path $f)) { Write-Host ("MISSING-INPUT " + $f); exit 3 }
}
foreach ($fx in @('physical-v1','scoring-v1','round-v1','cheat-v1','shuffle-v1','ai-v1')) {
  if (-not (Test-Path (Join-Path $EvidenceDir ('census/' + $fx + '-full-paths.txt')))) {
    Write-Host ("MISSING-INPUT census full-paths for " + $fx); exit 3
  }
}
Write-Host ("INPUTS ledger=" + $LedgerPath)
Write-Host ("INPUTS evidence=" + $EvidenceDir)

# ---------- pinned expectations ----------
$instShaExpected = '049a097b9d37ed01931835e18cbee6a9db5809826e090dc36ce51187787c6a12'
$rulesShaExpected = '24959db1f5bc463b2d61e0072e01c95d117c4398b6a929a1aa6b6c28a3e44a31'
$pkgTotal = @{
  'GAP:N3-B1'=24; 'GAP:N3-B2'=32; 'GAP:N3-B3'=21; 'GAP:N3-B4'=35; 'GAP:N3-B5'=10
  'GAP:N3-B6'=11; 'GAP:N3-B7'=1;  'GAP:N3-B8'=191;'GAP:N3-B9'=8;  'GAP:N3-B10'=28
  'GAP:N3-B11'=11;'GAP:N3-E1'=112;'GAP:N3-E2'=98; 'GAP:N3-E3'=10
}
$pkgVerdictWant = @{
  'GAP:N3-B1|bound'=18;      'GAP:N3-B1|pending'=6
  'GAP:N3-B2|bound'=32
  'GAP:N3-B3|bound'=21
  'GAP:N3-B4|bound'=35
  'GAP:N3-B5|bound'=10
  'GAP:N3-B6|pending'=11
  'GAP:N3-B7|bound'=1
  'GAP:N3-B8|bound'=108;     'GAP:N3-B8|classified'=8; 'GAP:N3-B8|pending'=75
  'GAP:N3-B9|classified'=1;  'GAP:N3-B9|pending'=7
  'GAP:N3-B10|bound'=28
  'GAP:N3-B11|bound'=11
  'GAP:N3-E1|bound'=48;      'GAP:N3-E1|classified'=64
  'GAP:N3-E2|bound'=98
  'GAP:N3-E3|bound'=10
}
$verdictTotalWant = @{ bound=420; classified=73; pending=99 }
$censusFixtures = @('physical-v1','scoring-v1','round-v1','cheat-v1','shuffle-v1','ai-v1')
$incrementalFixtures = @('movement-v1','shuffle-reversed-v1','visibility-v1')

# ---------- 1. census pins ----------
$h1 = (Get-FileHash -Algorithm SHA256 $instFile).Hash.ToLowerInvariant()
$h2 = (Get-FileHash -Algorithm SHA256 $rulesFile).Hash.ToLowerInvariant()
if ($h1 -ne $instShaExpected) { Fail 'census-pin' ("instances.tsv SHA " + $h1) } else { Pass 'census-pin' 'instances.tsv 049a097b...' }
if ($h2 -ne $rulesShaExpected) { Fail 'census-pin' ("rules.tsv SHA " + $h2) } else { Pass 'census-pin' 'rules.tsv 24959db1...' }

# ---------- 2. re-derive the v5 adjudication ----------
function Test-Match([string]$pattern, [string]$path) {
  if ($pattern -eq '$') { return ($path -eq '$') }
  if ($path -eq '$') { return $false }
  $p = $pattern.TrimStart('$').TrimStart('.').Split('.')
  $a = $path.TrimStart('$').TrimStart('.').Split('.')
  $ai = 0
  for ($pi = 0; $pi -lt $p.Count; $pi++) {
    if ($p[$pi] -eq '#') { return ($pi -eq $p.Count - 1) }
    if ($ai -ge $a.Count) { return $false }
    if ($p[$pi] -eq '*') { $ai++; continue }
    if ($p[$pi] -cne $a[$ai]) { return $false }
    $ai++
  }
  return ($ai -eq $a.Count)
}
function Parse-Scope([string]$scope) {
  if ($scope -eq '*') { return $null }
  if ($scope -notmatch '^case=[A-Za-z0-9._\[\]-]+(\|[A-Za-z0-9._\[\]-]+)*((;where=)[A-Za-z0-9_]+==[A-Za-z0-9._-]+(\|[A-Za-z0-9._-]+)*)?$') { return $null }
  $parts = $scope -split ';'
  $ids = $parts[0].Substring(5) -split '\|'
  $wf = ''; $wv = @()
  if ($parts.Count -gt 1) {
    $w = ($parts[1].Substring(6)) -split '=='
    $wf = $w[0]; $wv = $w[1] -split '\|'
  }
  return [pscustomobject]@{ ids = $ids; whereField = $wf; whereVals = $wv }
}
function Test-WhereStr([string]$parent, [string]$field, [string[]]$vals) {
  if (-not $parent -or $parent -eq '-') { return $false }
  $prefix = $field + '='
  foreach ($seg in $parent.Split(';')) {
    if ($seg.StartsWith($prefix, [StringComparison]::Ordinal)) {
      return ($vals -ccontains $seg.Substring($prefix.Length))
    }
  }
  return $false
}
$rules = New-Object System.Collections.Generic.List[object]
foreach ($line in (Get-Content -Encoding UTF8 $rulesFile)) {
  if ($line -notmatch '\S') { continue }
  if ($line.StartsWith('#')) { continue }
  $c = $line -split "`t"
  if ($c.Count -lt 7) { continue }
  if ($c[0].Length -gt 0 -and $c[0][0] -eq [char]0xFEFF) { $c[0] = $c[0].Substring(1) }
  if ($c[0] -eq 'fixture') { continue }
  $scope = '*'
  if ($c.Count -ge 8 -and $c[7]) { $scope = $c[7] }
  $rules.Add([pscustomobject]@{ fixture=$c[0]; pattern=$c[1]; cls=$c[3]; owner=$c[6]; scope=$scope })
}
if ($rules.Count -ne 542) { Fail 'rules-count' ("got " + $rules.Count + " want 542") } else { Pass 'rules-count' '542 rules' }

$pathOwner = @{}
$pathCount = 0
foreach ($fx in $censusFixtures) {
  $pathOwner[$fx] = @{}
  $fr = @($rules | Where-Object { $_.fixture -eq $fx })
  if ($fr.Count -eq 0) { Fail 'rules-coverage' ("no rules for " + $fx); continue }
  foreach ($line in Get-Content (Join-Path $EvidenceDir ('census/' + $fx + '-full-paths.txt'))) {
    $sp = $line -split ' '
    $owned = $null
    for ($i = 0; $i -lt $fr.Count; $i++) { if (Test-Match $fr[$i].pattern $sp[0]) { $owned = $fr[$i]; break } }
    if ($null -eq $owned) { Fail 'path-leak' ($fx + ' ' + $sp[0]); continue }
    $pathOwner[$fx][$sp[0]] = $owned.pattern
    $pathCount++
  }
}
if ($pathCount -ne 1516) { Fail 'paths-pin' ("got " + $pathCount + " want 1516") } else { Pass 'paths-pin' '1516 normalized paths' }

$famLines = @{}
$caseUniverse = @{}
$totalUnits = 0
foreach ($fx in $censusFixtures) { $caseUniverse[$fx] = @{} }
foreach ($line in [IO.File]::ReadLines($instFile)) {
  if (-not $line) { continue }
  $c = $line -split "`t"
  if ($c.Count -lt 6) { Fail 'instances-shape' 'short line'; continue }
  $fx = $c[0]
  if (-not $pathOwner.ContainsKey($fx)) { Fail 'instances-fixture' $fx; continue }
  $totalUnits++
  if ($c[2] -ne '-') { $caseUniverse[$fx][$c[2]] = $true }
  $pattern = $pathOwner[$fx][$c[1]]
  if ($null -eq $pattern) { Fail 'census-mismatch' ($fx + ' ' + $c[1]); continue }
  $gk = $fx + '|' + $pattern
  if (-not $famLines.ContainsKey($gk)) { $famLines[$gk] = New-Object System.Collections.Generic.List[string] }
  $famLines[$gk].Add($line)
}
if ($totalUnits -ne 655145) { Fail 'units-pin' ("got " + $totalUnits + " want 655145") } else { Pass 'units-pin' '655145 node instances' }

$derived = @{}     # key pkg|fixture|caseId|path|idx -> true
$derivedPkg = @{}
$unaCount = 0
$mixed = 0
$pathClasses = @{}
function Add-Derived([string]$fx, [string]$caseId, [string]$path, [string]$idx, [string]$cls, [string]$owner) {
  if ($owner -match '^GAP:') {
    $script:derived[$owner + '|' + $fx + '|' + $caseId + '|' + $path + '|' + $idx] = $true
    if (-not $script:derivedPkg.ContainsKey($owner)) { $script:derivedPkg[$owner] = 0 }
    $script:derivedPkg[$owner]++
  }
  if ($owner -match '^UNA:') { $script:unaCount++ }
  if (-not $script:pathClasses.ContainsKey($fx + '|' + $path)) { $script:pathClasses[$fx + '|' + $path] = @{} }
  $script:pathClasses[$fx + '|' + $path][$cls] = $true
}
foreach ($gk in $famLines.Keys) {
  $lines = $famLines[$gk]
  $fx = $gk.Substring(0, $gk.IndexOf('|'))
  $pattern = $gk.Substring($gk.IndexOf('|') + 1)
  $fr = @($rules | Where-Object { $_.fixture -eq $fx -and $_.pattern -eq $pattern })
  if ($fr.Count -eq 0) { continue }
  if ($fr[0].scope -eq '*') {
    foreach ($line in $lines) {
      $c = $line -split "`t"
      Add-Derived $fx $c[2] $c[1] $c[3] $fr[0].cls $fr[0].owner
    }
    continue
  }
  $parsed = @()
  foreach ($r in $fr) {
    $sc = Parse-Scope $r.scope
    if ($null -eq $sc) { Fail 'scope-format' ($gk + ' ' + $r.scope); continue }
    foreach ($id in $sc.ids) {
      if (-not $caseUniverse[$fx].ContainsKey($id)) { Fail 'scope-id-unknown' ($gk + ' ' + $id) }
    }
    $parsed += [pscustomobject]@{ row = $r; sc = $sc }
  }
  $owner = New-Object 'System.Collections.Generic.List[object]'
  for ($i = 0; $i -lt $lines.Count; $i++) { [void]$owner.Add([pscustomobject]@{ n = 0; cls = ''; own = '' }) }
  foreach ($pr in $parsed) {
    for ($i = 0; $i -lt $lines.Count; $i++) {
      $c = $lines[$i] -split "`t"
      if ($pr.sc.ids -cnotcontains $c[2]) { continue }
      if ($pr.sc.whereField -and -not (Test-WhereStr $c[5] $pr.sc.whereField $pr.sc.whereVals)) { continue }
      $owner[$i].n++
      if ($owner[$i].n -eq 1) { $owner[$i].cls = $pr.row.cls; $owner[$i].own = $pr.row.owner }
    }
  }
  for ($i = 0; $i -lt $lines.Count; $i++) {
    $c = $lines[$i] -split "`t"
    if ($owner[$i].n -ne 1) { Fail 'unit-coverage' ($gk + ' ' + $c[2] + ' owners=' + $owner[$i].n); continue }
    Add-Derived $fx $c[2] $c[1] $c[3] $owner[$i].cls $owner[$i].own
  }
}
foreach ($pp in $pathClasses.Keys) { if ($pathClasses[$pp].Keys.Count -gt 1) { $mixed++ } }
$gapTotal = 0
foreach ($k in $derivedPkg.Keys) { $gapTotal += $derivedPkg[$k] }
if ($gapTotal -ne 592) { Fail 'gap-pin' ("derived gap " + $gapTotal + " want 592") } else { Pass 'gap-pin' '592 gap instances re-derived' }
if ($unaCount -ne 17) { Fail 'una-pin' ("derived UNA " + $unaCount + " want 17") } else { Pass 'una-pin' '17 UNA instances excluded' }
if ($mixed -ne 27) { Fail 'mixed-pin' ("derived mixed " + $mixed + " want 27") } else { Pass 'mixed-pin' '27 fixture-qualified mixed paths' }
foreach ($pkg in $pkgTotal.Keys) {
  $n = if ($derivedPkg.ContainsKey($pkg)) { $derivedPkg[$pkg] } else { 0 }
  if ($n -ne $pkgTotal[$pkg]) { Fail 'pkg-vector' ($pkg + ' derived=' + $n + ' want=' + $pkgTotal[$pkg]) }
}
Pass 'pkg-vector' '14 package counts match the pinned CASE-DIST vector'

# ---------- 3. ledger parse ----------
$hdr = @('id','package','fixture','caseId','path','idx','kind','parent','histClass','verdict','csRead','assertion','fixCommit','evidence','reason','owner','revisit')
$ledger = New-Object System.Collections.Generic.List[object]
$first = $true
foreach ($line in [IO.File]::ReadLines($LedgerPath)) {
  if ($first) { $line = $line.TrimStart([char]0xFEFF); $first = $false }
  if (-not $line) { continue }
  $c = $line -split "`t"
  if ($first -eq $false -and $ledger.Count -eq 0) {
    if (($c -join "`t") -ne ($hdr -join "`t")) { Fail 'ledger-header' 'header mismatch'; break }
    $ledger.Add([pscustomobject]@{ header = $true })
    continue
  }
  $ledger.Add([pscustomobject]@{
    id=$c[0]; package=$c[1]; fixture=$c[2]; caseId=$c[3]; path=$c[4]; idx=$c[5]
    kind=$c[6]; parent=$c[7]; hist=$c[8]; verdict=$c[9]; csRead=$c[10]; asrt=$c[11]
    commit=$c[12]; ev=$c[13]; reason=$c[14]; owner=$c[15]; revisit=$c[16]
  })
}
if ($ledger.Count -lt 1 -or -not $ledger[0].header) { Fail 'ledger-header' 'header row missing' }
$rows = @($ledger | Where-Object { -not $_.header })
if ($rows.Count -ne 592) { Fail 'ledger-size' ("rows=" + $rows.Count + " want 592") } else { Pass 'ledger-size' '592 ledger rows' }

$ids = @{}
$seen = @{}
foreach ($r in $rows) {
  if ($ids.ContainsKey($r.id)) { Fail 'dup-id' $r.id }
  $ids[$r.id] = $true
  $wantId = $r.package.Substring(4) + '|' + $r.fixture + '|' + $r.caseId + '|' + $r.path + '|' + $r.idx
  if ($r.id -cne $wantId) { Fail 'id-shape' ($r.id + ' must be ' + $wantId) }
  $k = $r.package + '|' + $r.fixture + '|' + $r.caseId + '|' + $r.path + '|' + $r.idx
  if ($seen.ContainsKey($k)) { Fail 'dup-instance' $k }
  $seen[$k] = $true
}
if ($failures -eq 0) { Pass 'unique-set' 'stable IDs and (pkg,fixture,case,path,idx) tuples unique' }

# set equality with the re-derived gap set
$missing = 0; $extra = 0
foreach ($k in $derived.Keys) { if (-not $seen.ContainsKey($k)) { if ($missing -lt 5) { Fail 'ledger-leak' ('derived but absent from ledger: ' + $k) }; $missing++ } }
foreach ($k in $seen.Keys) { if (-not $derived.ContainsKey($k)) { if ($extra -lt 5) { Fail 'ledger-extra' ('in ledger but not in derived gap set: ' + $k) }; $extra++ } }
if ($missing -eq 0 -and $extra -eq 0) { Pass 'set-equality' 'ledger instance set equals the re-derived 592-instance gap set' }

# fixture discipline
$fxSeen = @{}
foreach ($r in $rows) { $fxSeen[$r.fixture] = $true }
foreach ($fx in $fxSeen.Keys) {
  if ($censusFixtures -cnotcontains $fx) { Fail 'fixture-unknown' $fx }
}
foreach ($fx in $incrementalFixtures) {
  if ($fxSeen.ContainsKey($fx)) { Fail 'incremental-fixture' ($fx + ' must not enter the closeout denominator') }
}
if ($failures -eq 0) { Pass 'fixture-discipline' 'fixtures restricted to the six census fixtures; incremental fixtures absent' }

# package totals and verdict vectors
$pkgSeen = @{}; $vSeen = @{}; $vTotal = @{ bound=0; classified=0; pending=0 }
foreach ($r in $rows) {
  if (-not $pkgTotal.ContainsKey($r.package)) { Fail 'package-unknown' $r.package; continue }
  $pkgSeen[$r.package] = 1 + $(if ($pkgSeen.ContainsKey($r.package)) { $pkgSeen[$r.package] } else { 0 })
  if (@('bound','classified','pending') -cnotcontains $r.verdict) { Fail 'verdict-enum' ($r.id + ' ' + $r.verdict); continue }
  $vk = $r.package + '|' + $r.verdict
  $vSeen[$vk] = 1 + $(if ($vSeen.ContainsKey($vk)) { $vSeen[$vk] } else { 0 })
  $vTotal[$r.verdict]++
}
foreach ($pkg in $pkgTotal.Keys) {
  $n = if ($pkgSeen.ContainsKey($pkg)) { $pkgSeen[$pkg] } else { 0 }
  if ($n -ne $pkgTotal[$pkg]) { Fail 'ledger-pkg' ($pkg + ' rows=' + $n + ' want=' + $pkgTotal[$pkg]) }
}
foreach ($k in $pkgVerdictWant.Keys) {
  $n = if ($vSeen.ContainsKey($k)) { $vSeen[$k] } else { 0 }
  if ($n -ne $pkgVerdictWant[$k]) { Fail 'verdict-vector' ($k + ' rows=' + $n + ' want=' + $pkgVerdictWant[$k]) }
}
foreach ($k in $vSeen.Keys) { if (-not $pkgVerdictWant.ContainsKey($k)) { Fail 'verdict-vector' ('unexpected verdict key ' + $k) } }
foreach ($v in $verdictTotalWant.Keys) {
  if ($vTotal[$v] -ne $verdictTotalWant[$v]) { Fail 'verdict-total' ($v + '=' + $vTotal[$v] + ' want=' + $verdictTotalWant[$v]) }
}
if ($failures -eq 0) { Pass 'closure' ('14 packages close on 592; bound=' + $vTotal.bound + ' classified=' + $vTotal.classified + ' pending=' + $vTotal.pending) }

# ---------- 4. per-verdict mandatory columns ----------
$lineCache = @{}
function File-Lines([string]$rel) {
  if (-not $script:lineCache.ContainsKey($rel)) {
    $full = Join-Path $script:RepoRoot ($rel -replace '/', '\')
    if (-not (Test-Path $full)) { $script:lineCache[$rel] = -1; return -1 }
    $script:lineCache[$rel] = @(Get-Content $full).Count
  }
  return $script:lineCache[$rel]
}
$commitOk = @{}
foreach ($r in $rows) {
  if ($r.verdict -eq 'bound') {
    if (-not $r.csRead -or $r.csRead -eq '-') { Fail 'bound-anchor' ($r.id + ' missing csRead'); continue }
    foreach ($tok in ($r.csRead -split ';')) {
      if ($tok -notmatch '^(.+?\.(?:cs|ps1))(?::(\d+(?:,\d+)*))?$') { Fail 'bound-anchor' ($r.id + ' unparseable ' + $tok); continue }
      $rel = $Matches[1]
      $n = File-Lines $rel
      if ($n -lt 0) { Fail 'bound-anchor' ($r.id + ' file not found: ' + $rel); continue }
      if ($Matches[2]) {
        foreach ($ln in ($Matches[2] -split ',')) {
          if ([int]$ln -lt 1 -or [int]$ln -gt $n) { Fail 'bound-anchor' ($r.id + ' line ' + $ln + ' out of range for ' + $rel + ' (' + $n + ' lines)') }
        }
      }
    }
    if ($r.commit -notmatch '^[0-9a-f]{7,40}([+][0-9a-f]{7,40})*$') { Fail 'bound-commit' ($r.id + ' fixCommit=' + $r.commit); continue }
    foreach ($sha in ($r.commit -split '[+]')) {
      if (-not $commitOk.ContainsKey($sha)) {
        $oldEap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $null = & git -C $RepoRoot cat-file -e ($sha + '^{commit}') 2>&1
        $ErrorActionPreference = $oldEap
        $commitOk[$sha] = ($LASTEXITCODE -eq 0)
      }
      if (-not $commitOk[$sha]) { Fail 'bound-commit' ($r.id + ' commit not in history: ' + $sha) }
    }
    if (-not $r.ev -or $r.ev.Length -lt 8) { Fail 'bound-evidence' $r.id }
  }
  elseif ($r.verdict -eq 'classified') {
    if (-not $r.reason -or $r.reason -eq '-') { Fail 'classified-reason' $r.id }
    if (-not $r.owner -or $r.owner -eq '-') { Fail 'classified-owner' $r.id }
    if (-not $r.revisit -or $r.revisit -eq '-') { Fail 'classified-revisit' $r.id }
    if ($r.asrt -match 'counted as full' -or $r.verdict -eq 'full') { Fail 'classified-not-full' $r.id }
  }
  else {
    if ($r.commit -ne '-') { Fail 'pending-commit' ($r.id + ' pending row carries fixCommit=' + $r.commit) }
    if (-not $r.owner -or $r.owner -eq '-') { Fail 'pending-owner' $r.id }
    if ($r.revisit -notmatch '20\d\d-\d\d-\d\d') { Fail 'pending-deadline' ($r.id + ' revisit lacks a dated deadline: ' + $r.revisit) }
  }
}
if ($failures -eq 0) {
  Pass 'bound-anchors' 'every bound row resolves to current files/lines and commits present in this history'
  Pass 'classified-discipline' 'classified rows carry full reason/owner/revisit and are never counted as full'
  Pass 'pending-discipline' 'pending rows carry owner, no fixCommit, and a dated revisit deadline'
}

# ---------- 5. the known classified blocked-case set ----------
$blockedWant = @('tm-capacity-tiles','tm-decision-mismatch','bm-activate-deadwall-empty','bm-activate-deadwall-dora-only')
$blockedSeen = @{}
foreach ($r in $rows) {
  if ($r.verdict -eq 'classified' -and $r.package -eq 'GAP:N3-B8') { $blockedSeen[$r.caseId] = $true }
}
foreach ($c in $blockedWant) { if (-not $blockedSeen.ContainsKey($c)) { Fail 'blocked-set' ('missing classified blocked case ' + $c) } }
foreach ($c in $blockedSeen.Keys) { if ($blockedWant -cnotcontains $c) { Fail 'blocked-set' ('unexpected classified blocked case ' + $c) } }
if ($failures -eq 0) { Pass 'blocked-set' 'B8 classified blocked cases are exactly the four still-blocked dependencies' }

if ($failures -gt 0) { Write-Host ("VERIFY-FAIL failures=" + $failures); exit 1 }
Write-Host 'VERIFY-OK n3-closeout-ledger 592=420bound+73classified+99pending, 14 packages, pins intact'
exit 0
