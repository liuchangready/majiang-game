#requires -Version 5.1
<#
    No-Pester regression tests for tools/unity/run-p45-full-match.ps1 -Mode Verify.
    Every negative case must return a non-zero native exit. Fixtures are written
    only under the ignored build/p45-full-match-driver/tool-tests root.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$tool = Join-Path $root 'tools\unity\run-p45-full-match.ps1'
$outRoot = Join-Path $root 'build\p45-full-match-driver\tool-tests'
if (Test-Path -LiteralPath $outRoot) {
    Remove-Item -LiteralPath $outRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $outRoot | Out-Null
$script:Total = 0
$script:Passed = 0
$script:Failed = 0

# Load the production ownership helpers without executing the tool's main body.
$ast = [System.Management.Automation.Language.Parser]::ParseFile($tool, [ref]$null, [ref]$null)
$ownershipFunctions = @('Get-ProcessRecord', 'Test-OwnedPlayerProcess', 'Stop-OwnedPlayerProcess', 'Quote-ProcessArgument', 'Join-ProcessArguments')
foreach ($name in $ownershipFunctions) {
    $fn = $ast.Find({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true)
    if ($null -eq $fn) { throw "Production ownership function not found: $name" }
    Invoke-Expression $fn.Extent.Text
}

function Write-ToolLog {
    param([string]$Message)
}

function Assert-True {
    param([bool]$Condition, [string]$Name)
    $script:Total++
    if ($Condition) { $script:Passed++; Write-Host ('PASS ' + $Name) }
    else { $script:Failed++; Write-Host ('FAIL ' + $Name) }
}

function New-Fixture {
    param([string]$Name)

    $run = Join-Path $outRoot $Name
    New-Item -ItemType Directory -Force -Path (Join-Path $run 'logs') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $run 'driver') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $run 'evidence') | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $run 'logs\tool.log'), "control`n")
    [System.IO.File]::WriteAllText((Join-Path $run 'driver\summary.json'), '{"outcome":"FAIL"}')
    [System.IO.File]::WriteAllText((Join-Path $run 'driver\event.jsonl'), "{`"kind`":`"x`"}`n")
    return $run
}

function Write-ComputedManifest {
    param([string]$Run, [string]$Kind = 'p45-full-match-evidence', [string]$RootOverride)

    $entries = New-Object System.Collections.Generic.List[object]
    [long]$total = 0
    Get-ChildItem -LiteralPath $Run -Recurse -File | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($Run.Length).TrimStart('\', '/').Replace('\', '/')
        if ($relative -eq 'evidence/artifacts.json' -or $relative -eq 'evidence/artifacts.json.sha256.txt' -or $relative -eq 'logs/tool.log') {
        }
        else {
            $entries.Add([ordered]@{
                path = $relative
                bytes = [long]$_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
                classification = 'evidence'
            }) | Out-Null
            $total += $_.Length
        }
    }
    $manifest = [ordered]@{
        schemaVersion = 1
        kind = $Kind
        root = if ($RootOverride) { $RootOverride } else { (Resolve-Path $Run).Path }
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        entryCount = $entries.Count
        totalBytes = $total
        controlLogs = @('logs/tool.log')
        entries = $entries
    }
    $path = Join-Path $Run 'evidence\artifacts.json'
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding UTF8
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    [System.IO.File]::WriteAllText(
        (Join-Path $Run 'evidence\artifacts.json.sha256.txt'),
        ($hash + '  evidence/artifacts.json' + "`n"),
        (New-Object System.Text.UTF8Encoding($false)))
    return $path
}

function Invoke-Verify {
    param([string]$Run)
    $p = Start-Process -FilePath 'powershell.exe' -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $tool,
        '-Mode', 'Verify', '-RunDir', $Run
    ) -PassThru -WindowStyle Hidden -Wait
    return $p.ExitCode
}

# 1) A complete fixture verifies twice.
$runA = New-Fixture 'valid'
Write-ComputedManifest -Run $runA | Out-Null
$first = Invoke-Verify -Run $runA
$second = Invoke-Verify -Run $runA
Assert-True ($first -eq 0) 'valid manifest first Verify'
Assert-True ($second -eq 0) 'valid manifest second Verify'

# 1b) Collect seals a fresh final sample, then Verify twice.
$runCollect = New-Fixture 'collect'
[System.IO.File]::WriteAllText((Join-Path $runCollect 'driver\required.txt'), 'required')
$collect = Start-Process -FilePath 'powershell.exe' -ArgumentList @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $tool,
    '-Mode', 'Collect', '-RunDir', $runCollect
) -PassThru -WindowStyle Hidden -Wait
$collectVerify1 = Invoke-Verify -Run $runCollect
$collectVerify2 = Invoke-Verify -Run $runCollect
Assert-True ($collect.ExitCode -eq 0) 'Collect creates final manifest'
Assert-True ($collectVerify1 -eq 0) 'Collect manifest Verify 1'
Assert-True ($collectVerify2 -eq 0) 'Collect manifest Verify 2'

# 2) Empty entries.
$runB = New-Fixture 'empty'
Write-ComputedManifest -Run $runB | Out-Null
$m = Get-Content -LiteralPath (Join-Path $runB 'evidence\artifacts.json') -Raw | ConvertFrom-Json
$m.entries = @()
$m.entryCount = 0
$m.totalBytes = 0
$m | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runB 'evidence\artifacts.json') -Encoding UTF8
$h = (Get-FileHash -LiteralPath (Join-Path $runB 'evidence\artifacts.json') -Algorithm SHA256).Hash
[System.IO.File]::WriteAllText((Join-Path $runB 'evidence\artifacts.json.sha256.txt'), ($h + "  x`n"))
Assert-True ((Invoke-Verify -Run $runB) -ne 0) 'empty entries rejected'

# 3) Missing file.
$runC = New-Fixture 'missing'
Write-ComputedManifest -Run $runC | Out-Null
Remove-Item -LiteralPath (Join-Path $runC 'driver\event.jsonl') -Force
Assert-True ((Invoke-Verify -Run $runC) -ne 0) 'missing file rejected'

# 4) Extra file.
$runD = New-Fixture 'extra'
Write-ComputedManifest -Run $runD | Out-Null
[System.IO.File]::WriteAllText((Join-Path $runD 'driver\extra.txt'), 'extra')
Assert-True ((Invoke-Verify -Run $runD) -ne 0) 'extra file rejected'

# 5) Tampered length/hash.
$runE = New-Fixture 'tampered'
Write-ComputedManifest -Run $runE | Out-Null
[System.IO.File]::AppendAllText((Join-Path $runE 'driver\event.jsonl'), 'tamper')
Assert-True ((Invoke-Verify -Run $runE) -ne 0) 'tampered bytes rejected'

# 6) Wrong root.
$runF = New-Fixture 'wrongroot'
Write-ComputedManifest -Run $runF -RootOverride (Join-Path $outRoot 'elsewhere') | Out-Null
Assert-True ((Invoke-Verify -Run $runF) -ne 0) 'wrong root rejected'

# 7) Missing sidecar.
$runG = New-Fixture 'nosidecar'
Write-ComputedManifest -Run $runG | Out-Null
Remove-Item -LiteralPath (Join-Path $runG 'evidence\artifacts.json.sha256.txt') -Force
Assert-True ((Invoke-Verify -Run $runG) -ne 0) 'missing sidecar rejected'

# 8) Wrong sidecar.
$runH = New-Fixture 'badsidecar'
Write-ComputedManifest -Run $runH | Out-Null
[System.IO.File]::WriteAllText((Join-Path $runH 'evidence\artifacts.json.sha256.txt'), ('0' * 64 + "  x`n"))
Assert-True ((Invoke-Verify -Run $runH) -ne 0) 'wrong sidecar rejected'

# 9) Missing manifest.
$runI = New-Fixture 'nomanifest'
Assert-True ((Invoke-Verify -Run $runI) -ne 0) 'missing manifest rejected'

# 10) Player manifest references a missing whole-package file.
$runJ = New-Fixture 'badplayer'
New-Item -ItemType Directory -Force -Path (Join-Path $runJ 'player') | Out-Null
[System.IO.File]::WriteAllText((Join-Path $runJ 'player\Mahjong.exe'), 'exe')
$playerManifest = [ordered]@{
    schemaVersion = 1
    kind = 'p45-full-match-player-files'
    root = (Resolve-Path (Join-Path $runJ 'player')).Path
    fileCount = 2
    totalBytes = 10
    files = @(
        [ordered]@{ path = 'Mahjong.exe'; bytes = 3; sha256 = (Get-FileHash (Join-Path $runJ 'player\Mahjong.exe') -Algorithm SHA256).Hash },
        [ordered]@{ path = 'Mahjong_Data\missing.dll'; bytes = 7; sha256 = ('0' * 64) }
    )
}
$playerManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runJ 'player-files.json') -Encoding UTF8
Write-ComputedManifest -Run $runJ | Out-Null
Assert-True ((Invoke-Verify -Run $runJ) -ne 0) 'whole Player manifest missing file rejected'

# 10b) A manifest root redirected to a different existing run is rejected.
$runK = New-Fixture 'wrong-existing-root'
$runOther = New-Fixture 'other-existing-root'
Write-ComputedManifest -Run $runK -RootOverride (Resolve-Path $runOther).Path | Out-Null
Assert-True ((Invoke-Verify -Run $runK) -ne 0) 'manifest root redirected to another existing run rejected'

# 10c) A required file cannot exempt itself by adding it to controlLogs.
$runL = New-Fixture 'omitted-control'
[System.IO.File]::WriteAllText((Join-Path $runL 'driver\required.txt'), 'required')
Write-ComputedManifest -Run $runL | Out-Null
$manifestL = Get-Content -LiteralPath (Join-Path $runL 'evidence\artifacts.json') -Raw | ConvertFrom-Json
$requiredEntry = @($manifestL.entries | Where-Object { $_.path -eq 'driver/required.txt' })
$manifestL.entries = @($manifestL.entries | Where-Object { $_.path -ne 'driver/required.txt' })
$manifestL.entryCount = @($manifestL.entries).Count
$manifestL.controlLogs = @('logs/tool.log', 'driver/required.txt')
$manifestL.totalBytes = [long]($manifestL.totalBytes - [long]$requiredEntry[0].bytes)
$manifestL | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runL 'evidence\artifacts.json') -Encoding UTF8
$hashL = (Get-FileHash -LiteralPath (Join-Path $runL 'evidence\artifacts.json') -Algorithm SHA256).Hash
[System.IO.File]::WriteAllText((Join-Path $runL 'evidence\artifacts.json.sha256.txt'), ($hashL + "  x`n"))
Assert-True ((Invoke-Verify -Run $runL) -ne 0) 'required file cannot be self-exempted via controlLogs'

# 10d) Whole-player root must equal the expected player directory.
$runM = New-Fixture 'wrong-player-root'
New-Item -ItemType Directory -Force -Path (Join-Path $runM 'player') | Out-Null
[System.IO.File]::WriteAllText((Join-Path $runM 'player\Mahjong.exe'), 'exe')
$wrongPlayerRoot = Join-Path $outRoot 'another-player-root'
New-Item -ItemType Directory -Force -Path $wrongPlayerRoot | Out-Null
$playerM = [ordered]@{
    schemaVersion = 1
    kind = 'p45-full-match-player-files'
    root = $wrongPlayerRoot
    fileCount = 1
    totalBytes = 3
    files = @([ordered]@{ path = 'Mahjong.exe'; bytes = 3; sha256 = (Get-FileHash (Join-Path $runM 'player\Mahjong.exe') -Algorithm SHA256).Hash })
}
$playerM | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runM 'player-files.json') -Encoding UTF8
Write-ComputedManifest -Run $runM | Out-Null
Assert-True ((Invoke-Verify -Run $runM) -ne 0) 'whole Player manifest wrong root rejected'

# 11) Production ownership identity: a same-process record must be accepted, a
# wrong hash must be refused without killing, and the correct record must stop
# only the test-owned child process.
$probe = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-Command', 'Start-Sleep -Seconds 120') -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 800
try {
    $current = Get-ProcessRecord -Process $probe
    $good = [ordered]@{
        pid = $current.pid
        startTimeUtc = $current.startTimeUtc
        path = $current.path
        sha256 = $current.sha256
    }
    $bad = [ordered]@{
        pid = $good.pid
        startTimeUtc = $good.startTimeUtc
        path = $good.path
        sha256 = ('0' * 64)
    }
    Assert-True (Test-OwnedPlayerProcess -Process $probe -Record $good) 'same owned process record accepted'
    Assert-True (-not (Test-OwnedPlayerProcess -Process $probe -Record $bad)) 'wrong hash record refused'
    $refused = Stop-OwnedPlayerProcess -Process $probe -Record $bad -Reason 'TEST_WRONG_ID'
    Assert-True ($refused -eq 'NOT_OWNED_OR_EXITED') 'wrong identity does not kill'
    Assert-True (-not $probe.HasExited) 'test child still alive after refused stop'
    $stopped = Stop-OwnedPlayerProcess -Process $probe -Record $good -Reason 'TEST_OWNED'
    $probe.WaitForExit(10000) | Out-Null
    Assert-True ($stopped -eq 'KILLED' -and $probe.HasExited) 'correct identity stops owned child'
}
finally {
    if (-not $probe.HasExited) {
        $probe.Kill()
        $probe.WaitForExit(10000) | Out-Null
    }
}

$toolText = Get-Content -LiteralPath $tool -Raw

# 12) Windows argument quoting: spaces and a trailing backslash survive exactly.
$spaceDir = Join-Path $outRoot 'space dir'
New-Item -ItemType Directory -Force -Path $spaceDir | Out-Null
$probeScript = Join-Path $spaceDir 'args probe.ps1'
$probeOut = Join-Path $spaceDir 'args out.txt'
[System.IO.File]::WriteAllText($probeScript, '$args -join "|"' + "`n")
$argLine = Join-ProcessArguments -Arguments @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $probeScript,
    'arg with spaces', 'plain', 'trailing\'
)
$argProcess = Start-Process -FilePath 'powershell.exe' -ArgumentList $argLine -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput $probeOut -Wait
$received = (Get-Content -LiteralPath $probeOut -Raw).Trim()
Assert-True ($argProcess.ExitCode -eq 0 -and $received -eq 'arg with spaces|plain|trailing\') 'Windows argument quoting preserves spaces and trailing backslash'

Assert-True ($toolText -match '\$record\.path\s*=\s*\$record\.exe' -and $toolText -match '\$record\.sha256\s*=\s*\$record\.exeSha256') 'player record uses unified path/sha fields'
Assert-True ($toolText -match 'UNITY_FINALLY' -and $toolText -match 'Stop-OwnedPlayerProcess\s+-Process\s+\$process\s+-Record\s+\$unityRecord') 'Unity timeout/finally uses owned identity stop'
Assert-True ($toolText -match 'executions\\' -and $toolText -match '\-full-match-run-id' -and $toolText -match 'driverSummaryRunIdMatches') 'run uses a fresh execution id bound to summary'
Assert-True ($toolText -match 'Test-WholePlayerManifest' -and $toolText -match 'source\.dirty\.patch' -and $toolText -match 'source\.untracked\.json') 'whole-package and source-closure checks exist'

Write-Host ('TOTAL=' + $script:Total + ' PASSED=' + $script:Passed + ' FAILED=' + $script:Failed)
if ($script:Failed -ne 0) { exit 1 }
exit 0
