<#
    SCRIPT_TOOL_FIXTURE compatibility/fail-closed matrix for the C04 runner.
    The old local Assert-C04Evidence/Strict semantic verifier is retired; these
    tests verify the remaining cheap guards and the D04-A CLI handoff contract.
    They do not run Unity, the Player or a game.
#>
$ErrorActionPreference = 'Stop'
$runner = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'tools\unity\run-p45-c04-player-cheats.ps1'
. $runner -SelfTest

$passed = 0
$failures = New-Object System.Collections.Generic.List[string]
$testRoot = Join-Path $root ('build\c04-cheats-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null

function Assert-Equal {
    param($Actual, $Expected, [string]$Label)
    if ($Actual -eq $Expected) { $script:passed++ }
    else { $script:failures.Add("$Label expected=[$Expected] actual=[$Actual]") | Out-Null }
}

function Assert-True {
    param($Condition, [string]$Label)
    if ($Condition) { $script:passed++ }
    else { $script:failures.Add("$Label condition was false") | Out-Null }
}

function Assert-Contains {
    param([object[]]$Values, [string]$Expected, [string]$Label)
    if (@($Values) -contains $Expected) { $script:passed++ }
    else { $script:failures.Add("$Label expected=$Expected actual=$(@($Values) -join ',')") | Out-Null }
}

# 1. The retired local verifier must not exist as a formal PASS fallback.
Assert-True ($null -eq (Get-Command Assert-C04EvidenceStrict -ErrorAction SilentlyContinue)) 'strict-removed'
Assert-True ($null -eq (Get-Command Assert-C04Evidence -ErrorAction SilentlyContinue)) 'basic-verifier-removed'

# 2. Fixed D04-A CLI contract.
$head = '0123456789abcdef0123456789abcdef01234567'
$verifierArguments = Get-C04VerifierArguments -Verifier 'C:\tools\C04PlayerCheatVerifier2.dll' -EvidenceJson 'C:\capture\case-authorized.json' -CaseName 'authorized' -ExpectedSourceHead $head -PackageManifest 'C:\pkg\c04-package-manifest.csv'
Assert-Equal ($verifierArguments -join ' ') ('C:\tools\C04PlayerCheatVerifier2.dll verify --evidence C:\capture\case-authorized.json --case authorized --source-head ' + $head + ' --package-manifest C:\pkg\c04-package-manifest.csv') 'verifier-fixed-cli'

# 3. Case JSON is only structurally checked here; semantics are delegated.
$caseDir = Join-Path $testRoot 'case'
New-Item -ItemType Directory -Force -Path $caseDir | Out-Null
Assert-Contains (Get-C04CaseStructureErrors -EvidenceJson (Join-Path $caseDir 'none.json')) 'MISSING_EVIDENCE_JSON' 'case-missing'
[System.IO.File]::WriteAllText((Join-Path $caseDir 'bad.json'), 'not-json')
Assert-Contains (Get-C04CaseStructureErrors -EvidenceJson (Join-Path $caseDir 'bad.json')) 'EVIDENCE_JSON_MALFORMED' 'case-malformed'
[System.IO.File]::WriteAllText((Join-Path $caseDir 'ok.json'), '{"schemaVersion":1}')
$okCaseErrors = @(Get-C04CaseStructureErrors -EvidenceJson (Join-Path $caseDir 'ok.json'))
Assert-Equal $okCaseErrors.Count 0 'case-structurally-ok'

# 4. BuildReport errors=3 is preserved verbatim and attributed to steps.
$exe = Join-Path $testRoot 'Mahjong.exe'
[System.IO.File]::WriteAllText($exe, 'fixture')
$report = [ordered]@{
    schema = 1
    result = 'Succeeded'
    errors = 3
    warnings = 200
    output = (Get-C04FullPath $exe)
    bytes = (Get-Item -LiteralPath $exe).Length
    outputSha256 = Get-C04Sha256 $exe
    startedUtc = '2026-11-01T10:17:15.0872355Z'
    finishedUtc = '2026-11-01T10:19:17.4294166Z'
    durationSeconds = 121.958
    scenes = @('Assets/Scenes/Boot.unity','Assets/Scenes/Menu.unity','Assets/Scenes/Lobby.unity','Assets/Scenes/Table.unity','Assets/Scenes/Spectator.unity','Assets/Scenes/Settlement.unity')
    plugins = @()
    steps = @([ordered]@{ name = 'Preprocess Player'; depth = 0; durationSeconds = 1; messages = @(
        [ordered]@{ type = 'Error'; content = 'historical-error-1' },
        [ordered]@{ type = 'Exception'; content = 'historical-error-2' },
        [ordered]@{ type = 'Error'; content = 'historical-error-3' }
    ) })
}
$reportPath = Join-Path $testRoot 'player-build.json'
Write-C04Json -Path $reportPath -Object $report
$check = Test-C04BuildReport -Path $reportPath -ExpectedOutput $exe
Assert-Contains $check.errors 'BUILD_REPORT_ERRORS:3' 'buildreport-preserves-3'
Assert-Contains $check.errors 'BUILD_REPORT_STEP_ERROR:step=Preprocess Player type=Error message=historical-error-1' 'buildreport-step-error'
Assert-Contains $check.errors 'BUILD_REPORT_STEP_ERROR:step=Preprocess Player type=Exception message=historical-error-2' 'buildreport-step-exception'
Assert-Equal ([int]$check.report.errors) 3 'buildreport-count-unchanged'

# 5. A report with errors but no Error/Exception step messages is not accepted.
$report.errors = 3
$report.steps = @([ordered]@{ name = 'Preprocess Player'; depth = 0; durationSeconds = 1; messages = @([ordered]@{ type = 'Log'; content = 'ok' }) })
$unattributedPath = Join-Path $testRoot 'unattributed.json'
Write-C04Json -Path $unattributedPath -Object $report
Assert-Contains (Test-C04BuildReport -Path $unattributedPath -ExpectedOutput $exe).errors 'BUILD_REPORT_UNATTRIBUTED_ERRORS' 'buildreport-unattributed'

# 6. Evidence root overwrite and outside-build guards are real runner refusals.
$existing = Join-Path $testRoot 'existing'
New-Item -ItemType Directory -Force -Path $existing | Out-Null
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -SourceHead $head -EvidenceRoot $existing -PlayerPath (Join-Path $testRoot 'missing.exe') -SkipBuild *> (Join-Path $testRoot 'overwrite.log')
Assert-Equal $LASTEXITCODE 2 'overwrite-exit-2'
Assert-True ((Get-Content -LiteralPath (Join-Path $testRoot 'overwrite.log') -Raw) -match 'REFUSE_OVERWRITE|overwrite') 'overwrite-message'

$outsideRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('c04-outside-' + [guid]::NewGuid().ToString('N'))
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -SourceHead $head -EvidenceRoot $outsideRoot -PlayerPath (Join-Path $testRoot 'missing.exe') -SkipBuild *> (Join-Path $testRoot 'outside.log')
Assert-Equal $LASTEXITCODE 2 'outside-exit-2'
Assert-True ((Get-Content -LiteralPath (Join-Path $testRoot 'outside.log') -Raw) -match 'EVIDENCE_ROOT_OUTSIDE_BUILD|outside') 'outside-message'

# 7. Missing SourceHead fails before any evidence root is created.
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -EvidenceRoot (Join-Path $testRoot 'no-head') -PlayerPath (Join-Path $testRoot 'missing.exe') -SkipBuild *> (Join-Path $testRoot 'nohead.log')
Assert-Equal $LASTEXITCODE 2 'no-head-exit-2'
Assert-True (-not (Test-Path -LiteralPath (Join-Path $testRoot 'no-head'))) 'no-head-no-evidence-root'

# 8. SkipBuild requires a sealed package, not just an existing exe.
$unsealed = Join-Path $testRoot 'unsealed'
New-Item -ItemType Directory -Force -Path $unsealed | Out-Null
[System.IO.File]::WriteAllText((Join-Path $unsealed 'Mahjong.exe'), 'x')
$skip = Test-C04PackageSeal -PlayerPath (Join-Path $unsealed 'Mahjong.exe') -ExpectedSourceHead $head
Assert-Contains $skip.errors 'PACKAGE_SEAL_MISSING' 'skip-build-requires-seal'

# 9. Quoting contract remains exact for space/quote paths.
Assert-Equal (ConvertTo-C04QuotedArgument 'C:\Program Files\Unity\Unity.exe') '"C:\Program Files\Unity\Unity.exe"' 'quoting-space'
Assert-Equal (ConvertTo-C04QuotedArgument 'a"b') '"a\"b"' 'quoting-quote'

Write-Host "C04PlayerCheats.Tests passed=$passed failed=$($failures.Count)"
foreach ($failure in $failures) { Write-Host "FAIL $failure" }
if ($failures.Count -gt 0) { exit 1 }
exit 0
