#requires -Version 5.1
<#
.SYNOPSIS
  Mutation probe for the V5 movement golden binding (G-004..G-007).

.DESCRIPTION
  Builds the Domain test project in an isolated copy of HEAD (plus the current task
  files), runs the real TileMovementGoldenTests through the native xUnit runner, then
  applies one-field mutations to the copied fixture only:
    - INPUT negative: change one action seat
    - OUTPUT negative: wall/rinshan pointer, nullable result both directions, rejection
      result, evidence, hands/rivers/dead-wall/replacements/dora arrays
    - STRUCTURE negative: invalid/duplicate hand slots, wrong meld group, before/after
    - META negative: schema, required source set, source hashes, command enable/comment/
      selector, G-row mapping
    - SOURCE_ONLY positive: change v4TotalKanCount (must not affect the physical diff)
    - representation/order positive: reversed placements, swapped hand slots, swapped
      meld group-internal offsets, CRLF-only exporter checkout, supported POSIX command
  Every native run passes one exact count gate (Total=10, Errors/Skipped/NotRun=0;
  PASS exit0/Failed0, FAIL exit1/Failed>=1). The gate has its own negative self-check.
  The original worktree fixture is never modified. A -SelfNegative switch performs the
  baseline and then exits 1 deliberately, proving the probe's own failure path.

  Run:
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/v5-golden/probe-movement-binding.ps1

  Exit codes:
    0  PASS (baseline green, every negative failed, every positive passed)
    1  FAIL
    2  BLOCKED (missing tool/tree/build)
#>
[CmdletBinding()]
param(
    [string]$Dotnet = "D:/Unity/dotnet/dotnet.exe",
    [string]$Configuration = "Release",
    [switch]$SelfNegative,
    [switch]$KeepTemp
)

$ErrorActionPreference = "Stop"
$script:Failures = New-Object System.Collections.Generic.List[string]
$script:Results = New-Object System.Collections.Generic.List[object]

function Write-Step([string]$Message) {
    Write-Host ("[probe] " + $Message)
}

function Add-Result(
    [string]$Name,
    [string]$Kind,
    [string]$Expected,
    [int]$ExitCode,
    $Summary,
    [string]$LogPath
) {
    $total = if ($null -eq $Summary) { $null } else { $Summary.Total }
    $failed = if ($null -eq $Summary) { $null } else { $Summary.Failed }
    $errors = if ($null -eq $Summary) { $null } else { $Summary.Errors }
    $skipped = if ($null -eq $Summary) { $null } else { $Summary.Skipped }
    $notRun = if ($null -eq $Summary) { $null } else { $Summary.NotRun }
    $script:Results.Add([pscustomobject]@{
        name = $Name
        kind = $Kind
        expected = $Expected
        nativeExit = $ExitCode
        total = $total
        failed = $failed
        errors = $errors
        skipped = $skipped
        notRun = $notRun
        log = $LogPath
    })
}

function Invoke-Native([string]$FilePath, [string[]]$Arguments, [string]$LogPath) {
    $output = & $FilePath @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    $text = ($output | ForEach-Object { $_.ToString() }) -join "`n"
    [System.IO.File]::WriteAllText($LogPath, $text, [System.Text.UTF8Encoding]::new($false))
    return [pscustomobject]@{
        ExitCode = $exitCode
        Text = $text
    }
}

function Get-Summary([string]$Text) {
    $match = [regex]::Match(
        $Text,
        'Total:\s*(\d+),\s*Errors:\s*(\d+),\s*Failed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Not Run:\s*(\d+)')
    if (-not $match.Success) {
        return $null
    }

    return [pscustomobject]@{
        Total = [int]$match.Groups[1].Value
        Errors = [int]$match.Groups[2].Value
        Failed = [int]$match.Groups[3].Value
        Skipped = [int]$match.Groups[4].Value
        NotRun = [int]$match.Groups[5].Value
    }
}

function Get-FixtureHash([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return "MISSING"
    }

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

$script:ExpectedTotal = 10

function Test-CountGate([string]$Expected, [int]$ExitCode, $Summary) {
    if ($null -eq $Summary) {
        return $false
    }

    if ($Summary.Total -ne $script:ExpectedTotal -or
        $Summary.Errors -ne 0 -or
        $Summary.Skipped -ne 0 -or
        $Summary.NotRun -ne 0) {
        return $false
    }

    if ($Expected -eq "PASS") {
        return ($ExitCode -eq 0 -and $Summary.Failed -eq 0)
    }

    if ($Expected -eq "FAIL") {
        return ($ExitCode -eq 1 -and $Summary.Failed -ge 1)
    }

    return $false
}

function Format-Summary($Summary) {
    if ($null -eq $Summary) {
        return "<no summary>"
    }

    return ("Total={0}, Errors={1}, Failed={2}, Skipped={3}, NotRun={4}" -f `
        $Summary.Total, $Summary.Errors, $Summary.Failed, $Summary.Skipped, $Summary.NotRun)
}

function Assert-CountGate([string]$Expected, [int]$ExitCode, $Summary, [string]$Label) {
    if (Test-CountGate $Expected $ExitCode $Summary) {
        return $true
    }

    $script:Failures.Add(
        $Label + ": count gate expected " + $Expected + " exit " +
        $(if ($Expected -eq "PASS") { "0" } else { "1" }) +
        ", got exit " + $ExitCode + " / " + (Format-Summary $Summary))
    return $false
}

function Invoke-GateSelfCheck {
    $bad = @(
        @{ Name = "pass-total-0"; Expected = "PASS"; Exit = 0; Total = 0; Errors = 0; Failed = 0; Skipped = 0; NotRun = 0 },
        @{ Name = "pass-total-9"; Expected = "PASS"; Exit = 0; Total = 9; Errors = 0; Failed = 0; Skipped = 0; NotRun = 0 },
        @{ Name = "pass-skipped-1"; Expected = "PASS"; Exit = 0; Total = 10; Errors = 0; Failed = 0; Skipped = 1; NotRun = 0 },
        @{ Name = "pass-notrun-1"; Expected = "PASS"; Exit = 0; Total = 10; Errors = 0; Failed = 0; Skipped = 0; NotRun = 1 },
        @{ Name = "pass-errors-1"; Expected = "PASS"; Exit = 0; Total = 10; Errors = 1; Failed = 0; Skipped = 0; NotRun = 0 },
        @{ Name = "pass-failed-1"; Expected = "PASS"; Exit = 0; Total = 10; Errors = 0; Failed = 1; Skipped = 0; NotRun = 0 },
        @{ Name = "fail-exit-0"; Expected = "FAIL"; Exit = 0; Total = 10; Errors = 0; Failed = 1; Skipped = 0; NotRun = 0 },
        @{ Name = "fail-total-9"; Expected = "FAIL"; Exit = 1; Total = 9; Errors = 0; Failed = 1; Skipped = 0; NotRun = 0 },
        @{ Name = "fail-errors-1"; Expected = "FAIL"; Exit = 1; Total = 10; Errors = 1; Failed = 1; Skipped = 0; NotRun = 0 },
        @{ Name = "fail-skipped-1"; Expected = "FAIL"; Exit = 1; Total = 10; Errors = 0; Failed = 1; Skipped = 1; NotRun = 0 },
        @{ Name = "fail-notrun-1"; Expected = "FAIL"; Exit = 1; Total = 10; Errors = 0; Failed = 1; Skipped = 0; NotRun = 1 },
        @{ Name = "fail-exit-2"; Expected = "FAIL"; Exit = 2; Total = 10; Errors = 0; Failed = 1; Skipped = 0; NotRun = 0 }
    )

    foreach ($case in $bad) {
        $summary = [pscustomobject]@{
            Total = $case.Total
            Errors = $case.Errors
            Failed = $case.Failed
            Skipped = $case.Skipped
            NotRun = $case.NotRun
        }
        if (Test-CountGate $case.Expected $case.Exit $summary) {
            $script:Failures.Add("gate self-check accepted invalid case " + $case.Name)
        }
    }

    $goodPass = [pscustomobject]@{ Total = 10; Errors = 0; Failed = 0; Skipped = 0; NotRun = 0 }
    $goodFail = [pscustomobject]@{ Total = 10; Errors = 0; Failed = 2; Skipped = 0; NotRun = 0 }
    if (-not (Test-CountGate "PASS" 0 $goodPass)) {
        $script:Failures.Add("gate self-check rejected the valid PASS case")
    }
    if (-not (Test-CountGate "FAIL" 1 $goodFail)) {
        $script:Failures.Add("gate self-check rejected the valid FAIL case")
    }
}

function Invoke-Baseline(
    [string]$DotnetPath,
    [string]$DllPath,
    [string]$EvidenceDir,
    [string]$Label
) {
    $log = Join-Path $EvidenceDir ($Label + ".log")
    $native = Invoke-Native $DotnetPath @(
        $DllPath,
        "-class", "Mahjong.Domain.Tests.TileMovementGoldenTests",
        "-noColor",
        "-noLogo"
    ) $log
    $summary = Get-Summary $native.Text
    Add-Result $Label "baseline" "PASS" $native.ExitCode $summary $log
    return [pscustomobject]@{
        Native = $native
        Summary = $summary
        Log = $log
    }
}

function Invoke-Mutation(
    [string]$Name,
    [string]$Kind,
    [string]$Expected,
    [string]$BaselineFixture,
    [string]$FixturePath,
    [string]$DotnetPath,
    [string]$DllPath,
    [string]$EvidenceDir,
    [scriptblock]$Apply
) {
    Copy-Item -LiteralPath $BaselineFixture -Destination $FixturePath -Force
    $json = Get-Content -LiteralPath $FixturePath -Raw -Encoding UTF8 | ConvertFrom-Json
    & $Apply $json
    $rendered = $json | ConvertTo-Json -Depth 100
    [System.IO.File]::WriteAllText(
        $FixturePath,
        $rendered,
        [System.Text.UTF8Encoding]::new($false))

    $log = Join-Path $EvidenceDir ($Name + ".log")
    $native = Invoke-Native $DotnetPath @(
        $DllPath,
        "-class", "Mahjong.Domain.Tests.TileMovementGoldenTests",
        "-noColor",
        "-noLogo"
    ) $log
    $summary = Get-Summary $native.Text
    Add-Result $Name $Kind $Expected $native.ExitCode $summary $log

    if (-not (Assert-CountGate $Expected $native.ExitCode $summary $Name)) {
        return
    }

    if ($Expected -eq "FAIL") {
        Write-Step ("{0}: FAIL as expected (exit 1, {1})" -f $Name, (Format-Summary $summary))
    }
    else {
        Write-Step ("{0}: PASS as expected (exit 0, {1})" -f $Name, (Format-Summary $summary))
    }
}

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$runId = (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmssZ") + "-" + $PID
$evidenceDir = Join-Path $repo ("build/movement-binding-probe/" + $runId)
New-Item -ItemType Directory -Force -Path $evidenceDir | Out-Null
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("movement-binding-probe-" + [guid]::NewGuid().ToString("N"))
$tempRepo = Join-Path $tempRoot "repo"

$summaryPath = Join-Path $evidenceDir "results.csv"
$originalFixture = Join-Path $repo "tests/v5-golden/movement-v1.json"
$originalHashBefore = Get-FixtureHash $originalFixture
$baselineFixture = Join-Path $tempRoot "movement-v1.baseline.json"
$fixturePath = Join-Path $tempRepo "tests/v5-golden/movement-v1.json"
$dllPath = Join-Path $tempRepo "dotnet/tests/Mahjong.Domain.Tests/bin/$Configuration/net8.0/Mahjong.Domain.Tests.dll"

try {
    if (-not (Test-Path -LiteralPath $Dotnet)) {
        Write-Step ("BLOCKED: missing dotnet " + $Dotnet)
        exit 2
    }

    if (-not (Test-Path -LiteralPath $originalFixture)) {
        Write-Step ("BLOCKED: missing fixture " + $originalFixture)
        exit 2
    }

    $git = Get-Command git -ErrorAction SilentlyContinue
    if ($null -eq $git) {
        Write-Step "BLOCKED: git not found"
        exit 2
    }

    New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
    $zip = Join-Path $tempRoot "head.zip"
    Write-Step ("isolated TEMP copy: " + $tempRepo)
    $archive = Invoke-Native "git" @("-C", $repo, "archive", "--format=zip", "--output=$zip", "HEAD") (Join-Path $evidenceDir "git-archive.log")
    if ($archive.ExitCode -ne 0) {
        Write-Step ("BLOCKED: git archive failed with exit " + $archive.ExitCode)
        exit 2
    }

    Expand-Archive -LiteralPath $zip -DestinationPath $tempRepo -Force
    foreach ($relative in @(
        "tests/v5-golden/movement-v1.json",
        "dotnet/tests/Mahjong.Domain.Tests/TileMovementGoldenTests.cs",
        "engine/src/test/kotlin/com/majiang/model/V5TileMovementGoldenExporterTest.kt"
    )) {
        $source = Join-Path $repo $relative
        if (Test-Path -LiteralPath $source) {
            $destination = Join-Path $tempRepo $relative
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
            Copy-Item -LiteralPath $source -Destination $destination -Force
        }
    }

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $fixturePath) | Out-Null
    Copy-Item -LiteralPath $fixturePath -Destination $baselineFixture -Force

    $project = Join-Path $tempRepo "dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj"
    $buildLog = Join-Path $evidenceDir "build.log"
    $build = Invoke-Native $Dotnet @(
        "build", $project,
        "-c", $Configuration,
        "--nologo",
        "-v:minimal"
    ) $buildLog
    if ($build.ExitCode -ne 0) {
        Write-Step ("BLOCKED: build failed with exit " + $build.ExitCode)
        $script:Failures.Add("build failed with exit " + $build.ExitCode)
        exit 2
    }

    Invoke-GateSelfCheck
    if ($script:Failures.Count -gt 0) {
        foreach ($failure in $script:Failures) {
            Write-Step ("FAIL: " + $failure)
        }
        Write-Step "FAIL: count-gate self-check"
        exit 1
    }
    Write-Step "count-gate self-check: rejected Total0/short/skip/notRun/error/exit drift cases"

    $baseline = Invoke-Baseline $Dotnet $dllPath $evidenceDir "baseline"
    if (-not (Assert-CountGate "PASS" $baseline.Native.ExitCode $baseline.Summary "baseline")) {
        $script:Results | Export-Csv -LiteralPath $summaryPath -NoTypeInformation -Encoding utf8
        Write-Step "FAIL: baseline"
        exit 1
    }

    Write-Step ("baseline: " + (Format-Summary $baseline.Summary))

    if ($SelfNegative) {
        Write-Step "SELF-NEGATIVE: deliberate exit 1 after a green baseline"
        $script:Results | Export-Csv -LiteralPath $summaryPath -NoTypeInformation -Encoding utf8
        exit 1
    }

    $mutations = @(
        @{
            Name = "negative-input-action-seat"
            Kind = "INPUT"
            Expected = "FAIL"
            Apply = {
                param($json)
                $json.cases[0].steps[0].action.seat = 1
            }
        },
        @{
            Name = "negative-output-wall-pointer"
            Kind = "OUTPUT"
            Expected = "FAIL"
            Apply = {
                param($json)
                $json.cases[0].steps[1].after.pointers.wallPointer = 2
            }
        },
        @{
            Name = "negative-output-draw-result-null"
            Kind = "OUTPUT"
            Expected = "FAIL"
            Apply = {
                param($json)
                $json.cases[0].steps[0].resultTileId = $null
            }
        },
        @{
            Name = "negative-output-draw-result-wrong"
            Kind = "OUTPUT"
            Expected = "FAIL"
            Apply = {
                param($json)
                $json.cases[0].steps[0].resultTileId = 65
            }
        },
        @{
            Name = "negative-output-discard-result-null"
            Kind = "OUTPUT"
            Expected = "FAIL"
            Apply = {
                param($json)
                $json.cases[0].steps[1].resultTileId = $null
            }
        },
        @{
            Name = "negative-output-rinshan-result-null"
            Kind = "OUTPUT"
            Expected = "FAIL"
            Apply = {
                param($json)
                $json.cases[3].steps[2].resultTileId = $null
            }
        },
        @{
            Name = "negative-output-null-to-value"
            Kind = "OUTPUT"
            Expected = "FAIL"
            Apply = {
                param($json)
                $json.cases[1].steps[0].resultTileId = 1
            }
        },
        @{
            Name = "negative-output-rejection-result"
            Kind = "OUTPUT"
            Expected = "FAIL"
            Apply = {
                param($json)
                $json.cases[6].steps[0].resultTileId = 1
            }
        },
        @{
            Name = "negative-output-lastdiscard-tile"
            Kind = "OUTPUT"
            Expected = "FAIL"
            Apply = {
                param($json)
                $json.cases[0].steps[1].after.lastDiscard.tileId = 67
            }
        },
        @{
            Name = "negative-output-drawntile-tile"
            Kind = "OUTPUT"
            Expected = "FAIL"
            Apply = {
                param($json)
                $json.cases[0].steps[0].after.drawnTile.tileId = 63
            }
        },
        @{
            Name = "negative-output-rinshan-slot"
            Kind = "OUTPUT"
            Expected = "FAIL"
            Apply = {
                param($json)
                $json.cases[3].steps[2].after.rinshanDraw.slot = 1
            }
        },
        @{
            Name = "negative-output-hands-array"
            Kind = "OUTPUT"
            Expected = "FAIL"
            Apply = {
                param($json)
                $json.cases[0].steps[0].after.hands[0].tiles[0] = 99
            }
        },
        @{
            Name = "negative-output-rivers-array"
            Kind = "OUTPUT"
            Expected = "FAIL"
            Apply = {
                param($json)
                $json.cases[0].steps[1].after.rivers[0].tiles[0] = 67
            }
        },
        @{
            Name = "negative-output-deadwall-array"
            Kind = "OUTPUT"
            Expected = "FAIL"
            Apply = {
                param($json)
                $json.cases[0].initialState.deadWallBySlot[0] = 99
            }
        },
        @{
            Name = "negative-output-replacements-array"
            Kind = "OUTPUT"
            Expected = "FAIL"
            Apply = {
                param($json)
                $json.cases[3].steps[2].after.replacements[0] = 99
            }
        },
        @{
            Name = "negative-output-dora-array"
            Kind = "OUTPUT"
            Expected = "FAIL"
            Apply = {
                param($json)
                $json.cases[3].initialState.doraIndicators[0] = 99
            }
        },
        @{
            Name = "negative-output-rinshan-pointer"
            Kind = "OUTPUT"
            Expected = "FAIL"
            Apply = {
                param($json)
                $json.cases[7].steps[6].after.pointers.rinshanPointer = 3
            }
        },
        @{
            Name = "negative-raw-initial-wall-seat"
            Kind = "STRUCTURE"
            Expected = "FAIL"
            Apply = {
                param($json)
                ($json.cases[0].initialState.placements | Where-Object zone -eq WALL | Select-Object -First 1).seat = 99
            }
        },
        @{
            Name = "negative-raw-before-wall-seat"
            Kind = "STRUCTURE"
            Expected = "FAIL"
            Apply = {
                param($json)
                ($json.cases[0].steps[0].before.placements | Where-Object zone -eq WALL | Select-Object -First 1).seat = 99
            }
        },
        @{
            Name = "negative-raw-after-wall-seat"
            Kind = "STRUCTURE"
            Expected = "FAIL"
            Apply = {
                param($json)
                ($json.cases[0].steps[0].after.placements | Where-Object zone -eq WALL | Select-Object -First 1).seat = 99
            }
        },
        @{
            Name = "negative-raw-initial-deadwall-seat"
            Kind = "STRUCTURE"
            Expected = "FAIL"
            Apply = {
                param($json)
                ($json.cases[0].initialState.placements | Where-Object zone -eq DEAD_WALL | Select-Object -First 1).seat = 99
            }
        },
        @{
            Name = "negative-raw-before-deadwall-seat"
            Kind = "STRUCTURE"
            Expected = "FAIL"
            Apply = {
                param($json)
                ($json.cases[0].steps[0].before.placements | Where-Object zone -eq DEAD_WALL | Select-Object -First 1).seat = 99
            }
        },
        @{
            Name = "negative-raw-after-deadwall-seat"
            Kind = "STRUCTURE"
            Expected = "FAIL"
            Apply = {
                param($json)
                ($json.cases[0].steps[0].after.placements | Where-Object zone -eq DEAD_WALL | Select-Object -First 1).seat = 99
            }
        },
        @{
            Name = "negative-raw-before-replacement-seat"
            Kind = "STRUCTURE"
            Expected = "FAIL"
            Apply = {
                param($json)
                ($json.cases[7].steps[1].before.placements | Where-Object zone -eq DEAD_WALL_REPLACEMENT | Select-Object -First 1).seat = 99
            }
        },
        @{
            Name = "negative-raw-after-replacement-seat"
            Kind = "STRUCTURE"
            Expected = "FAIL"
            Apply = {
                param($json)
                ($json.cases[3].steps[2].after.placements | Where-Object zone -eq DEAD_WALL_REPLACEMENT | Select-Object -First 1).seat = 99
            }
        },
        @{
            Name = "negative-structure-initial-duplicate-position"
            Kind = "STRUCTURE"
            Expected = "FAIL"
            Apply = {
                param($json)
                $placements = @($json.cases[0].initialState.placements)
                $placements[1].zone = $placements[0].zone
                $placements[1].seat = $placements[0].seat
                $placements[1].index = $placements[0].index
            }
        },
        @{
            Name = "negative-structure-output-hand-index"
            Kind = "STRUCTURE"
            Expected = "FAIL"
            Apply = {
                param($json)
                $hand = @($json.cases[0].steps[0].after.placements | Where-Object { $_.zone -eq "HAND" })
                $hand[0].index = -1
            }
        },
        @{
            Name = "negative-structure-output-duplicate-position"
            Kind = "STRUCTURE"
            Expected = "FAIL"
            Apply = {
                param($json)
                $hand = @($json.cases[0].steps[0].after.placements | Where-Object { $_.zone -eq "HAND" })
                $hand[1].index = $hand[0].index
            }
        },
        @{
            Name = "negative-structure-meld-wrong-group"
            Kind = "STRUCTURE"
            Expected = "FAIL"
            Apply = {
                param($json)
                $meld = @($json.cases[1].steps[0].after.placements | Where-Object { $_.zone -eq "MELD" })
                $meld[0].index = 15
            }
        },
        @{
            Name = "negative-structure-before-hand-index"
            Kind = "STRUCTURE"
            Expected = "FAIL"
            Apply = {
                param($json)
                $hand = @($json.cases[0].steps[1].before.placements | Where-Object { $_.zone -eq "HAND" })
                $hand[0].index = -1
            }
        },
        @{
            Name = "negative-meta-schema"
            Kind = "META"
            Expected = "FAIL"
            Apply = {
                param($json)
                $json.schema = "not-v5-movement-golden-v1"
            }
        },
        @{
            Name = "negative-meta-source-hashes-empty"
            Kind = "META"
            Expected = "FAIL"
            Apply = {
                param($json)
                $json.sourceHashes = @()
            }
        },
        @{
            Name = "negative-meta-source-omitted"
            Kind = "META"
            Expected = "FAIL"
            Apply = {
                param($json)
                $json.sourceHashes = @($json.sourceHashes | Select-Object -Skip 1)
            }
        },
        @{
            Name = "negative-meta-source-duplicate"
            Kind = "META"
            Expected = "FAIL"
            Apply = {
                param($json)
                $json.sourceHashes = @($json.sourceHashes) + @($json.sourceHashes[0])
            }
        },
        @{
            Name = "negative-meta-source-unknown"
            Kind = "META"
            Expected = "FAIL"
            Apply = {
                param($json)
                $json.sourceHashes[0].file = "unknown/source.kt"
            }
        },
        @{
            Name = "negative-meta-source-hash-wrong"
            Kind = "META"
            Expected = "FAIL"
            Apply = {
                param($json)
                $json.sourceHashes[0].sha256LfNormalized = ("0" * 64)
            }
        },
        @{
            Name = "negative-meta-command-bogus"
            Kind = "META"
            Expected = "FAIL"
            Apply = {
                param($json)
                $json.generatedByCommand = "bogus"
            }
        },
        @{
            Name = "negative-meta-grows-wrong"
            Kind = "META"
            Expected = "FAIL"
            Apply = {
                param($json)
                $json.cases[0].gRows = @("G-007")
            }
        },
        @{
            Name = "negative-meta-export-disabled"
            Kind = "META"
            Expected = "FAIL"
            Apply = {
                param($json)
                $json.generatedByCommand = $json.generatedByCommand.Replace("EXPORT='1'", "EXPORT='0'")
            }
        },
        @{
            Name = "negative-meta-export-comment-only"
            Kind = "META"
            Expected = "FAIL"
            Apply = {
                param($json)
                $json.generatedByCommand = "# " + $json.generatedByCommand
            }
        },
        @{
            Name = "negative-meta-wrong-selector"
            Kind = "META"
            Expected = "FAIL"
            Apply = {
                param($json)
                $json.generatedByCommand = $json.generatedByCommand.Replace(
                    '--tests "com.majiang.model.V5TileMovementGoldenExporterTest"',
                    '--tests "com.majiang.model.TileMovementContractTest" # V5TileMovementGoldenExporterTest')
            }
        },
        @{
            Name = "positive-source-only-kan-count"
            Kind = "SOURCE_ONLY"
            Expected = "PASS"
            Apply = {
                param($json)
                $json.cases[0].steps[0].after.v4TotalKanCount = 99
            }
        },
        @{
            Name = "positive-order-reversed-placements"
            Kind = "ORDER"
            Expected = "PASS"
            Apply = {
                param($json)
                $placements = @($json.cases[0].initialState.placements)
                [array]::Reverse($placements)
                $json.cases[0].initialState.placements = $placements
            }
        },
        @{
            Name = "positive-order-swapped-hand-slots"
            Kind = "ORDER"
            Expected = "PASS"
            Apply = {
                param($json)
                $hand = @($json.cases[0].initialState.placements | Where-Object {
                    $_.zone -eq "HAND" -and $_.seat -eq 0
                })
                $first = $hand[0].index
                $hand[0].index = $hand[1].index
                $hand[1].index = $first
            }
        },
        @{
            Name = "positive-order-meld-offsets"
            Kind = "ORDER"
            Expected = "PASS"
            Apply = {
                param($json)
                $meld = @($json.cases[1].steps[0].after.placements | Where-Object { $_.zone -eq "MELD" })
                $first = $meld[0].index
                $meld[0].index = $meld[1].index
                $meld[1].index = $first
            }
        },
        @{
            Name = "positive-meta-posix-command"
            Kind = "META_COMMAND"
            Expected = "PASS"
            Apply = {
                param($json)
                $json.generatedByCommand = 'V5_MOVEMENT_GOLDEN_EXPORT=1 ./gradlew :engine:test --tests "com.majiang.model.V5TileMovementGoldenExporterTest" --rerun-tasks --console=plain'
            }
        }
    )

    foreach ($mutation in $mutations) {
        Invoke-Mutation `
            -Name $mutation.Name `
            -Kind $mutation.Kind `
            -Expected $mutation.Expected `
            -BaselineFixture $baselineFixture `
            -FixturePath $fixturePath `
            -DotnetPath $Dotnet `
            -DllPath $dllPath `
            -EvidenceDir $evidenceDir `
            -Apply $mutation.Apply
    }

    # Positive line-ending control: the exporter source is LF in the fixture. Convert
    # the isolated copy to CRLF; the LF-normalized content hash must stay identical and
    # the C# test must use its explicit raw-bytes-differ branch, not fail.
    Copy-Item -LiteralPath $baselineFixture -Destination $fixturePath -Force
    $exporterSource = Join-Path $tempRepo "engine/src/test/kotlin/com/majiang/model/V5TileMovementGoldenExporterTest.kt"
    $exporterText = [System.IO.File]::ReadAllText($exporterSource, [System.Text.UTF8Encoding]::new($false))
    $exporterLf = $exporterText.Replace("`r`n", "`n")
    $exporterCrlf = $exporterLf.Replace("`n", "`r`n")
    [System.IO.File]::WriteAllText($exporterSource, $exporterCrlf, [System.Text.UTF8Encoding]::new($false))
    $lineLog = Join-Path $evidenceDir "positive-line-ending-crlf.log"
    $lineNative = Invoke-Native $Dotnet @(
        $dllPath,
        "-class", "Mahjong.Domain.Tests.TileMovementGoldenTests",
        "-noColor",
        "-noLogo"
    ) $lineLog
    $lineSummary = Get-Summary $lineNative.Text
    Add-Result "positive-line-ending-crlf" "LINE_ENDING" "PASS" $lineNative.ExitCode $lineSummary $lineLog
    if (Assert-CountGate "PASS" $lineNative.ExitCode $lineSummary "positive-line-ending-crlf") {
        Write-Step ("positive-line-ending-crlf: PASS as expected (exit 0, {0})" -f (Format-Summary $lineSummary))
    }

    $script:Results | Export-Csv -LiteralPath $summaryPath -NoTypeInformation -Encoding utf8
    $originalHashAfter = Get-FixtureHash $originalFixture
    if ($originalHashBefore -ne $originalHashAfter) {
        $script:Failures.Add("original fixture hash changed during probe")
    }

    [System.IO.File]::WriteAllText(
        (Join-Path $evidenceDir "summary.txt"),
        ("originalFixtureSha256=" + $originalHashBefore + "`n" +
         "originalFixtureHashAfter=" + $originalHashAfter + "`n" +
         "results=" + $script:Results.Count + "`n" +
         "failures=" + $script:Failures.Count + "`n"),
        [System.Text.UTF8Encoding]::new($false))

    if ($script:Failures.Count -gt 0) {
        foreach ($failure in $script:Failures) {
            Write-Step ("FAIL: " + $failure)
        }
        Write-Step ("evidence: " + $evidenceDir)
        exit 1
    }

    Write-Step ("PASS: baseline 10/0/0/0/0; 41 negative cases failed; 6 positive controls passed")
    Write-Step ("evidence: " + $evidenceDir)
    exit 0
}
finally {
    $resolvedTemp = [System.IO.Path]::GetFullPath($tempRoot)
    $resolvedBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    $leaf = Split-Path -Leaf $resolvedTemp
    if (-not $KeepTemp -and
        $resolvedTemp.StartsWith($resolvedBase, [System.StringComparison]::OrdinalIgnoreCase) -and
        $leaf.StartsWith("movement-binding-probe-", [System.StringComparison]::Ordinal) -and
        (Test-Path -LiteralPath $resolvedTemp)) {
        Remove-Item -LiteralPath $resolvedTemp -Recurse -Force -ErrorAction SilentlyContinue
    }
    elseif ($KeepTemp) {
        Write-Step ("kept TEMP copy: " + $resolvedTemp)
    }
}
