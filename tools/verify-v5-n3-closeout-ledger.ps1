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
#      stable IDs, no leak, no duplicate, no unknown package or fixture), plus
#      history fidelity: census kind/parent/histClass must survive verbatim;
#   3. the 14-package counts close on 592 with the pinned per-package vector;
#   4. per-row adjudication digests (verdict/fixCommit/anchor/assertion/evidence
#      per identity) so same-count swaps and silent re-adjudication fail;
#   5. bound/classified/pending mandatory columns: bound needs a resolvable
#      current C# anchor whose cited lines are real code (never blank, comment,
#      using or a bare brace), assertion/reason/owner/revisit obligations, a
#      fixCommit that is an ancestor of HEAD, and evidence citing only the
#      runtime-hash-verified shared registry IDs; classified must not be counted
#      as full and needs full reason/owner/revisit plus registry evidence;
#      pending needs owner, no fixCommit and a dated revisit;
#   6. incremental fixtures (movement-v1, shuffle-reversed-v1, visibility-v1)
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
  'GAP:N3-E3|bound'=1;       'GAP:N3-E3|classified'=9
}
$verdictTotalWant = @{ bound=411; classified=82; pending=99 }

# ---------- shared evidence registry (runtime-verified) ----------
# Every ledger evidence cell cites only these IDs; each file is hashed at
# verification time and must equal the pinned SHA-256 below. Re-adjudicating
# the ledger in a later round must regenerate these pins together with the
# per-package adjudication digests in the same commit.
$docDirName = 'doc/v5-' + [char]0x91CD + [char]0x6784
$reviewDir = 'artifacts/TEMP/codex-review-20261025'
$evidenceDir2 = 'D:/majiang-wt/codex-n3-r7-evidence-7816dc7072d04451862c965b0c0029d7'
$evidenceCatalog = @{
  'EV-AUDIT'      = @(($docDirName + '/V5-G1-N3-GOLDEN-FIELD-AUDIT.md'), '5a440c58902c260d607016546a37b243daaa75dcfe99401d3f5de7b202cb80e9')
  'EV-ACCEPT'     = @(($docDirName + '/CODEX-V5-G1-N3-FIELD-AUDIT-ACCEPTANCE.md'), '8d72f1dfb071e6cec56046b65abe78217d1f877f930e9ac1cb87ac88d7e68d71')
  'EV-CENSUS'     = @(($evidenceDir2 + '/census/instances.tsv'), '049a097b9d37ed01931835e18cbee6a9db5809826e090dc36ce51187787c6a12')
  'EV-RULES'      = @(($evidenceDir2 + '/rules.tsv'), '24959db1f5bc463b2d61e0072e01c95d117c4398b6a929a1aa6b6c28a3e44a31')
  'EV-CHEAT-DOC'  = @(($docDirName + '/V5-N3-CHEAT-GOLDEN-BINDING.md'), 'a9daf5f5ccd705f031af98b7aa7825452a263e97ba3df55df0a01e4e9c9d9abe')
  'EV-CHEAT-ACC'  = @(($docDirName + '/CODEX-V5-N3-CHEAT-BINDING-ACCEPTANCE.md'), 'a0660e19dc6777ce41fdbd734bec9cf61e5a3cf5c5275658f4a97c025a6574e5')
  'EV-ROUND-DOC'  = @(($docDirName + '/V5-N3-ROUND-GOLDEN-BINDING.md'), '9fa4953c787ef2bb5c924d5bdcabfc2ad7208209fba59b3a6ba2405a33d115db')
  'EV-ROUND-ACC'  = @(($docDirName + '/CODEX-V5-N3-ROUND-ACCEPTANCE.md'), '92c26f45e5e8a8bcda9d1c55f673400baf754038e7c02a6fa1b16c00582cda1e')
  'EV-SCORE-DOC'  = @(($docDirName + '/V5-N3-SCORING-GOLDEN-BINDING.md'), 'f6e7f4abf5c90f753fea5e338fb4ad8ed10346458b945483ae6c059e1cd7fe3a')
  'EV-PHYS-DOC'   = @(($docDirName + '/V5-N3-PHYSICAL-GOLDEN-BINDING.md'), '641fab2bbf81d55825fc42bed73e9d5def6a301afad561847e9c82f7eb5541ab')
  'EV-PHYS-ACC'   = @(($docDirName + '/CODEX-V5-N3-PHYSICAL-ACCEPTANCE.md'), 'e4621aee129d86a31c51e82e147022b1633f6ccd33184b1410dea2affd2d9b87')
  'EV-SHUF-DOC'   = @(($docDirName + '/V5-N3-SHUFFLE-GOLDEN-BINDING.md'), '2179ad99ee3139f28785d24ae2a3e9299b0caff17f9987f6807468785cce2842')
  'EV-SHUF-ACC'   = @(($docDirName + '/CODEX-V5-N3-SHUFFLE-ACCEPTANCE.md'), '9343395dab91501e751dad6934ad87faa1a6c474d85e827cdf9871f2a33f47db')
  'EV-AI-DOC'     = @(($docDirName + '/V5-N3-AI-REPLAY-BINDING.md'), 'dd94ef8f32d9759fd1fbe2b6974d728cbdc27414c7f3f31842e692a3b74ecb1a')
  'EV-AI-ACC'     = @(($docDirName + '/CODEX-V5-N3-AI-R2-ACCEPTANCE.md'), '0b48be12d87c1a4e43ef1e5b575b64c3ae85fd8711530014a152c365533f24b1')
  'EV-TABLE-DOC'  = @(($docDirName + '/V5-N3-TABLE-CHEAT-GOLDEN-ADMISSION.md'), 'add5cb6dea7747fd7e781ee97e9d7414469baa0df10cd2834e676387c97f4d08')
  'EV-TABLE-ACC'  = @(($docDirName + '/CODEX-V5-N3-TABLE-CHEAT-ACCEPTANCE.md'), 'e401de2d63fb65e6abab99136edd4e42c3c97fdb1e9bb969b264de6b025fe701')
  'EV-SLEEVE-DOC' = @(($docDirName + '/V5-N3-SLEEVE-FROZEN-INPUT.md'), '181bc19e84aa83d08334e5419d63402b4f20c61ac0f651a393f1cad93a742b38')
  'EV-SLEEVE-ACC' = @(($docDirName + '/CODEX-V5-N3-SLEEVE-FROZEN-INPUT-ACCEPTANCE.md'), '5c648e9824201a005743dd8d96912b8be19a188ad8553347e6fded7b99d61d91')
  'EV-R1-REVIEW'  = @(($reviewDir + '/findings.md'), 'e520991daa3b2c253053431a9bef3db032a802344c9ddea1a8c6002b5087f33e')
  'EV-R1-ANCHORS' = @(($reviewDir + '/anchors.tsv'), '3f881f63060a15e2a1404cb5e39956e2e6e13956a945941a3a87495be74b5539')
  'EV-R1-BASE'    = @(($reviewDir + '/baseline.log'), 'a1c919861c9c3065777703262826f055532e73f71682621f2d03bcf90d643718')
  'EV-R1-DOMAIN'  = @(($reviewDir + '/domain.log'), 'd3ce821392555fce4ea056049d30306c12a60882c1718a06100a43263ff81aad')
  'EV-R1-TABLE'   = @(($reviewDir + '/table.log'), '574549978cf1ba28fbab03713140f6240c8f22685ed4f299773ffbdb2fd7e2df')
  'EV-R1-PHYSHASH' = @(($reviewDir + '/physical-hash-verification-value.log'), '1692254931aecf482485f68c150d771c390f621942f47d5c71fefd7f64cb327e')
  'EV-R1-PHYSV4'  = @(($reviewDir + '/physical-v4-reason-value.log'), '98497fcee6ce6707b33d3325eb9e9c1c0ccb2e019426138d013c0acb163bcf42')
}
$evidenceOk = @{}
foreach ($evId in $evidenceCatalog.Keys) {
  $evPath = $evidenceCatalog[$evId][0]
  if (-not [IO.Path]::IsPathRooted($evPath)) { $evPath = Join-Path $RepoRoot ($evPath -replace '/', '\') }
  if (-not (Test-Path $evPath)) { Fail 'evidence-catalog' ($evId + ' file missing: ' + $evPath); continue }
  $evSha = (Get-FileHash -Algorithm SHA256 $evPath).Hash.ToLowerInvariant()
  if ($evSha -ne $evidenceCatalog[$evId][1]) { Fail 'evidence-catalog' ($evId + ' SHA drift: ' + $evSha); continue }
  $evidenceOk[$evId] = $true
}
if ($failures -eq 0) { Pass 'evidence-catalog' ('all ' + $evidenceCatalog.Count + ' registry files hash-match their pinned SHA-256') }

# ---------- per-package adjudication digests ----------
# SHA-256 over the sorted canonical rows (id|verdict|fixCommit|csRead|assertion|
# evidence, LF-joined, UTF-8) of each package. Catches same-count verdict swaps
# and any silent per-row adjudication change; regenerate together with the
# evidence registry when the ledger is legitimately re-adjudicated.
$adjudicationPin = @{
  'GAP:N3-B1'='72f5d50b71a1e94a5f0ee7a17052d2eac7f8a762892968f29a4b477656dc8069'
  'GAP:N3-B10'='6b348e74d74cdf136d8dd24a16ff0f8282122cacd2b3dac623183b4191d55cdb'
  'GAP:N3-B11'='763625e1c6a7cacaffda2e51b95071ad4371af4b5b943f03ffdd9f1d969a4826'
  'GAP:N3-B2'='1d371bc1fc76f58edee4f42f8d3cb1a9d09d200e0b42500c8661120c3c679010'
  'GAP:N3-B3'='bbe8661eb7a45b7898f30629511ada7a256a3e1e1bef714249dcea4085ae71dc'
  'GAP:N3-B4'='21d83e73028bdcffa4ad22875f457bb56c50cffdfdc07b65c39f57ddfde8cfbf'
  'GAP:N3-B5'='8c3805d9752854ab57023a96e312e35ca341ce0c17850ea8ab7044d0a7ac7be7'
  'GAP:N3-B6'='77b6a764b01df03167f38bd4d99a55c0c3d2bb5113f1347626901fc344d12305'
  'GAP:N3-B7'='3a7386f9f839df90421fee6614d4653236d92e822a677bac3aa1318803f07bb8'
  'GAP:N3-B8'='becfff903f9fc123a92cb97ec4f46f556163b04e1b2e0d76a7abc2e9c65cfc18'
  'GAP:N3-B9'='abd4e9f8f03670fa45c48f17186ea8883b39115ea6858e217a9e938b46f75f4a'
  'GAP:N3-E1'='3967694903f0934bc21caeac122a39bfdd5b62371a19781aa9444cb7bfbaaa3d'
  'GAP:N3-E2'='64bc076514ae919e94412d4921203416b1a8f13dd60248b1d5c6dad7875d53a0'
  'GAP:N3-E3'='6754375c49db62ad23053cc21d10ebd8c7ffc8a0efa95f9fc85ad1be9defeee5'
}
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

$derived = @{}     # key pkg|fixture|caseId|path|idx -> census truth record
$derivedPkg = @{}
$unaCount = 0
$mixed = 0
$pathClasses = @{}
function Add-Derived([string]$fx, [string]$caseId, [string]$path, [string]$idx, [string]$kind, [string]$parent, [string]$cls, [string]$owner) {
  if ($owner -match '^GAP:') {
    $script:derived[$owner + '|' + $fx + '|' + $caseId + '|' + $path + '|' + $idx] =
      @{ fixture = $fx; caseId = $caseId; path = $path; idx = $idx; kind = $kind; parent = $parent; hist = $cls }
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
      Add-Derived $fx $c[2] $c[1] $c[3] $c[4] $c[5] $fr[0].cls $fr[0].owner
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
    Add-Derived $fx $c[2] $c[1] $c[3] $c[4] $c[5] $owner[$i].cls $owner[$i].own
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
$scriptRowsBad = 0
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
  if ($c.Count -ne 17) { if ($scriptRowsBad -lt 5) { Fail 'ledger-schema' ('row ' + ($c[0]) + ' has ' + $c.Count + ' columns, want 17') }; $scriptRowsBad++ }
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
if ($scriptRowsBad -eq 0) { Pass 'ledger-schema' 'every row has exactly 17 columns' }

# history fidelity: the frozen census truth (fixture/caseId/path/idx/kind/parent/
# histClass) must survive verbatim in every ledger row, not just the identity key.
$histBad = 0
foreach ($r in $rows) {
  $k = $r.package + '|' + $r.fixture + '|' + $r.caseId + '|' + $r.path + '|' + $r.idx
  $truth = $derived[$k]
  if ($null -eq $truth) { continue }
  if ($r.kind -cne $truth.kind -or $r.parent -cne $truth.parent -or $r.hist -cne $truth.hist) {
    if ($histBad -lt 5) { Fail 'history-fidelity' ($r.id + ' census truth altered: kind=' + $r.kind + '/' + $truth.kind + ' parent=' + $r.parent + '/' + $truth.parent + ' histClass=' + $r.hist + '/' + $truth.hist) }
    $histBad++
  }
}
if ($histBad -eq 0) { Pass 'history-fidelity' 'kind/parent/histClass match the frozen census for all 592 instances' }

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
function File-Text([string]$rel) {
  if (-not $script:lineCache.ContainsKey($rel)) {
    $full = Join-Path $script:RepoRoot ($rel -replace '/', '\')
    if (-not (Test-Path $full)) { $script:lineCache[$rel] = $null; return $null }
    $script:lineCache[$rel] = @(Get-Content $full)
  }
  return $script:lineCache[$rel]
}
function Test-CodeLine([string]$id, [string]$rel, [string[]]$text, [int]$ln) {
  # Anchor CONTENT rule: each file:lines token must resolve to >=1 contentful
  # code line (non-blank, non-comment, non-using, non-bare-brace). Structural
  # lines inside a multi-line check block are fine as long as real assertions
  # are pinned too; a token that pins ONLY vacuous lines is an empty anchor.
  if ($ln -lt 1 -or $ln -gt $text.Count) { Fail 'bound-anchor' ($id + ' line ' + $ln + ' out of range for ' + $rel + ' (' + $text.Count + ' lines)'); return $false }
  $t = $text[$ln - 1].Trim()
  if ($t -eq '') { return 'blank' }
  if ($t.StartsWith('//')) { return 'comment' }
  if ($t -match '^using\b') { return 'using' }
  if ($t -eq '}' -or $t -eq '{') { return 'brace' }
  return $true
}
$commitOk = @{}
$ancestorOk = @{}
$boundAnchorBad = 0; $boundOblBad = 0; $boundEvBad = 0; $clsEvBad = 0
foreach ($r in $rows) {
  if ($r.verdict -eq 'bound') {
    if (-not $r.csRead -or $r.csRead -eq '-') { Fail 'bound-anchor' ($r.id + ' missing csRead'); continue }
    foreach ($tok in ($r.csRead -split ';')) {
      if ($tok -notmatch '^(.+?\.(?:cs|ps1))(?::(\d+(?:,\d+)*))?$') { Fail 'bound-anchor' ($r.id + ' unparseable ' + $tok); continue }
      $rel = $Matches[1]
      $text = File-Text $rel
      if ($null -eq $text) { Fail 'bound-anchor' ($r.id + ' file not found: ' + $rel); continue }
      if ($Matches[2]) {
        $anyCode = $false; $weakKind = ''
        foreach ($lnStr in ($Matches[2] -split ',')) {
          $k = Test-CodeLine $r.id $rel $text ([int]$lnStr)
          if ($k -eq $false) { continue }
          if ($k -eq $true) { $anyCode = $true; continue }
          if (-not $weakKind) { $weakKind = $k }
        }
        if (-not $anyCode) { Fail 'bound-anchor-content' ($r.id + ' anchor token ' + $tok + ' resolves to no contentful code line (first weak: ' + $weakKind + ')') }
      }
    }
    if ($r.commit -notmatch '^[0-9a-f]{7,40}([+][0-9a-f]{7,40})*$') { Fail 'bound-commit' ($r.id + ' fixCommit=' + $r.commit) }
    else {
      foreach ($sha in ($r.commit -split '[+]')) {
        if (-not $commitOk.ContainsKey($sha)) {
          $oldEap = $ErrorActionPreference
          $ErrorActionPreference = 'Continue'
          $null = & git -C $RepoRoot cat-file -e ($sha + '^{commit}') 2>&1
          $commitOk[$sha] = ($LASTEXITCODE -eq 0)
          & git -C $RepoRoot merge-base --is-ancestor $sha HEAD 2>&1 | Out-Null
          $ancestorOk[$sha] = ($LASTEXITCODE -eq 0)
          $ErrorActionPreference = $oldEap
        }
        if (-not $commitOk[$sha]) { Fail 'bound-commit' ($r.id + ' commit not in history: ' + $sha) }
        if (-not $ancestorOk[$sha]) { Fail 'bound-commit-ancestor' ($r.id + ' commit exists but is not an ancestor of HEAD: ' + $sha) }
      }
    }
    if (-not $r.asrt -or $r.asrt -eq '-' -or $r.asrt.Trim().Length -lt 4) { Fail 'bound-obligation' ($r.id + ' bound row lacks a value-level assertion'); $boundOblBad++ }
    if (-not $r.reason -or $r.reason -eq '-') { Fail 'bound-obligation' ($r.id + ' bound row lacks reason'); $boundOblBad++ }
    if (-not $r.owner -or $r.owner -eq '-') { Fail 'bound-obligation' ($r.id + ' bound row lacks owner'); $boundOblBad++ }
    if (-not $r.revisit -or $r.revisit -eq '-') { Fail 'bound-obligation' ($r.id + ' bound row lacks revisit'); $boundOblBad++ }
    if ($r.ev -notmatch '^(EV-[A-Z0-9-]+)(;EV-[A-Z0-9-]+)*$') { Fail 'bound-evidence' ($r.id + ' evidence cell is not a registry-ID list: ' + $r.ev); $boundEvBad++; }
    else {
      foreach ($evId in ($r.ev -split ';')) { if (-not $evidenceOk.ContainsKey($evId)) { Fail 'bound-evidence' ($r.id + ' evidence ID failed runtime hash: ' + $evId); $boundEvBad++ } }
    }
  }
  elseif ($r.verdict -eq 'classified') {
    if (-not $r.reason -or $r.reason -eq '-') { Fail 'classified-reason' $r.id }
    if (-not $r.owner -or $r.owner -eq '-') { Fail 'classified-owner' $r.id }
    if (-not $r.revisit -or $r.revisit -eq '-') { Fail 'classified-revisit' $r.id }
    if ($r.asrt -match 'counted as full' -or $r.verdict -eq 'full') { Fail 'classified-not-full' $r.id }
    if ($r.ev -notmatch '^(EV-[A-Z0-9-]+)(;EV-[A-Z0-9-]+)*$') { Fail 'classified-evidence' ($r.id + ' evidence cell is not a registry-ID list: ' + $r.ev); $clsEvBad++ }
    else {
      foreach ($evId in ($r.ev -split ';')) { if (-not $evidenceOk.ContainsKey($evId)) { Fail 'classified-evidence' ($r.id + ' evidence ID failed runtime hash: ' + $evId); $clsEvBad++ } }
    }
  }
  else {
    if ($r.commit -ne '-') { Fail 'pending-commit' ($r.id + ' pending row carries fixCommit=' + $r.commit) }
    if (-not $r.owner -or $r.owner -eq '-') { Fail 'pending-owner' $r.id }
    if ($r.revisit -notmatch '20\d\d-\d\d-\d\d') { Fail 'pending-deadline' ($r.id + ' revisit lacks a dated deadline: ' + $r.revisit) }
  }
}
if ($boundAnchorBad -eq 0 -and $failures -eq 0) {
  Pass 'bound-anchors' 'every bound row resolves to current code lines (no blank/comment/using anchors) with commits that are ancestors of HEAD'
}
if ($boundOblBad -eq 0) { Pass 'bound-obligation' 'every bound row carries assertion/reason/owner/revisit obligations' }
if ($boundEvBad -eq 0 -and $clsEvBad -eq 0) { Pass 'evidence-resolution' 'every bound/classified evidence cell cites only runtime-hash-verified registry IDs' }
if ($failures -eq 0) {
  Pass 'classified-discipline' 'classified rows carry full reason/owner/revisit and are never counted as full'
  Pass 'pending-discipline' 'pending rows carry owner, no fixCommit, and a dated revisit deadline'
}

# ---------- 4b. per-package adjudication digests ----------
$sha256 = [Security.Cryptography.SHA256]::Create()
$adjBad = 0
foreach ($pkg in $adjudicationPin.Keys) {
  $canon = @($rows | Where-Object { $_.package -eq $pkg } |
    ForEach-Object { ($_.id + '|' + $_.verdict + '|' + $_.commit + '|' + $_.csRead + '|' + $_.asrt + '|' + $_.ev) } |
    Sort-Object)
  $bytes = [Text.Encoding]::UTF8.GetBytes(($canon -join "`n"))
  $digest = ([BitConverter]::ToString($sha256.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
  if ($digest -ne $adjudicationPin[$pkg]) {
    Fail 'adjudication-pin' ($pkg + ' adjudication digest drift: ' + $digest); $adjBad++
  }
}
if ($adjBad -eq 0) { Pass 'adjudication-pin' ('all ' + $adjudicationPin.Count + ' per-package adjudication digests match (same-count swaps impossible)') }

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
Write-Host 'VERIFY-OK n3-closeout-ledger 592=411bound+82classified+99pending, 14 packages, pins/digests/registry intact'
exit 0
