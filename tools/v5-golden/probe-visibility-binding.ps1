#requires -Version 5.1
<#
.SYNOPSIS
  Mutation probe for the V5 visibility golden binding (G-057a..G-057d).

.DESCRIPTION
  Builds an isolated copy of HEAD (plus the current task fixture and C# test) under a
  short, exclusive D:-drive temp directory, runs the real VisibilityGoldenTests through
  the native xUnit runner, then applies one-field mutations to the copied fixture only.

  Covered negatives:
    - INPUT: a hand placement tile and an event owner seat
    - OUTPUT: snapshot score/hand count, board dora, event tile/dropped event,
      wall remaining and last discard
    - PRIVACY: opponent hand id relabelled public, opponent id injected into public dora,
      ura id injected into dora, private cheat balance exposed
    - STRUCTURE: duplicate/removed case, extra task path
    - META: schema, source hash, render hash, baseline commit
  Covered positives:
    - SOURCE_ONLY fields (legal actions text, dead-wall list) do not affect the binding
    - placement order is representation-only
    - scope note is meta-only
    - CRLF fixture checkout still passes (LF-normalized render hash)

  Every native run passes one exact count gate (Total=8, Errors=0, Skipped=0, Not Run=0;
  PASS exit 0/Failed 0, FAIL exit 1/Failed>=1). The gate has its own negative self-check.
  The original worktree fixture is never modified and its hash before/after is compared.
  A -SelfNegative switch runs the green baseline and then exits 1 deliberately.

  Run:
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/v5-golden/probe-visibility-binding.ps1

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

$script:ExpectedTotal = 8

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
        @{ Name = "pass-total-7"; Expected = "PASS"; Exit = 0; Total = 7; Errors = 0; Failed = 0; Skipped = 0; NotRun = 0 },
        @{ Name = "pass-skipped-1"; Expected = "PASS"; Exit = 0; Total = 8; Errors = 0; Failed = 0; Skipped = 1; NotRun = 0 },
        @{ Name = "pass-notrun-1"; Expected = "PASS"; Exit = 0; Total = 8; Errors = 0; Failed = 0; Skipped = 0; NotRun = 1 },
        @{ Name = "pass-errors-1"; Expected = "PASS"; Exit = 0; Total = 8; Errors = 1; Failed = 0; Skipped = 0; NotRun = 0 },
        @{ Name = "pass-failed-1"; Expected = "PASS"; Exit = 0; Total = 8; Errors = 0; Failed = 1; Skipped = 0; NotRun = 0 },
        @{ Name = "fail-exit-0"; Expected = "FAIL"; Exit = 0; Total = 8; Errors = 0; Failed = 1; Skipped = 0; NotRun = 0 },
        @{ Name = "fail-total-7"; Expected = "FAIL"; Exit = 1; Total = 7; Errors = 0; Failed = 1; Skipped = 0; NotRun = 0 },
        @{ Name = "fail-errors-1"; Expected = "FAIL"; Exit = 1; Total = 8; Errors = 1; Failed = 1; Skipped = 0; NotRun = 0 },
        @{ Name = "fail-skipped-1"; Expected = "FAIL"; Exit = 1; Total = 8; Errors = 0; Failed = 1; Skipped = 1; NotRun = 0 },
        @{ Name = "fail-notrun-1"; Expected = "FAIL"; Exit = 1; Total = 8; Errors = 0; Failed = 1; Skipped = 0; NotRun = 1 },
        @{ Name = "fail-exit-2"; Expected = "FAIL"; Exit = 2; Total = 8; Errors = 0; Failed = 1; Skipped = 0; NotRun = 0 }
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

    $goodPass = [pscustomobject]@{ Total = 8; Errors = 0; Failed = 0; Skipped = 0; NotRun = 0 }
    $goodFail = [pscustomobject]@{ Total = 8; Errors = 0; Failed = 2; Skipped = 0; NotRun = 0 }
    if (-not (Test-CountGate "PASS" 0 $goodPass)) {
        $script:Failures.Add("gate self-check rejected the valid PASS case")
    }
    if (-not (Test-CountGate "FAIL" 1 $goodFail)) {
        $script:Failures.Add("gate self-check rejected the valid FAIL case")
    }
}

function Read-Fixture([string]$Path) {
    return (Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json)
}

function Get-NormalizedJson([object]$Document) {
    $Document.renderSha256LfNormalized = $null
    $text = ($Document | ConvertTo-Json -Depth 100) + "`n"
    return $text.Replace("`r`n", "`n")
}

function Get-Sha256Text([string]$Text) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
        return ([System.BitConverter]::ToString($sha.ComputeHash($bytes))).Replace("-", "").ToLowerInvariant()
    } finally {
        $sha.Dispose()
    }
}

function Write-Fixture([object]$Document, [string]$Path) {
    $text = ($Document | ConvertTo-Json -Depth 100) + "`n"
    [System.IO.File]::WriteAllText($Path, $text, [System.Text.UTF8Encoding]::new($false))
}

function Update-RenderHash([object]$Document, [string]$Path) {
    # Write the exact document with a placeholder, then hash the exact file text with
    # the placeholder replaced by null (identical to the C# verifier's normalization).
    $placeholder = "0" * 64
    $Document.renderSha256LfNormalized = $placeholder
    Write-Fixture $Document $Path
    $text = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8).Replace("`r`n", "`n")
    $pattern = '"renderSha256LfNormalized":\s*"' + $placeholder + '"'
    $normalized = [regex]::Replace($text, $pattern, '"renderSha256LfNormalized": null', 1)
    $hash = Get-Sha256Text $normalized
    $final = [regex]::Replace($text, $pattern, ('"renderSha256LfNormalized": "' + $hash + '"'), 1)
    [System.IO.File]::WriteAllText($Path, $final, [System.Text.UTF8Encoding]::new($false))
}

function Invoke-VisibilityRun(
    [string]$DotnetPath,
    [string]$DllPath,
    [string]$EvidenceDir,
    [string]$Label
) {
    $log = Join-Path $EvidenceDir ($Label + ".log")
    $native = Invoke-Native $DotnetPath @(
        $DllPath,
        "-class", "Mahjong.TableRuntime.Tests.VisibilityGoldenTests",
        "-noColor",
        "-noLogo"
    ) $log
    $summary = Get-Summary $native.Text
    Add-Result $Label "run" "PASS" $native.ExitCode $summary $log
    return [pscustomobject]@{
        Native = $native
        Summary = $summary
    }
}

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$runId = (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmssZ") + "-" + $PID
$evidenceDir = Join-Path $repo ("build/visibility-binding-probe/" + $runId)
$tempRoot = Join-Path $evidenceDir "tmp"
$tempRepo = Join-Path $tempRoot "repo"
New-Item -ItemType Directory -Force -Path $evidenceDir | Out-Null
New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null

$summaryPath = Join-Path $evidenceDir "results.csv"
$originalFixture = Join-Path $repo "tests/v5-golden/visibility-v1.json"
$baselineFixture = Join-Path $tempRoot "visibility-v1.baseline.json"
$fixturePath = Join-Path $tempRepo "tests/v5-golden/visibility-v1.json"
$dllPath = Join-Path $tempRepo ("dotnet/tests/Mahjong.TableRuntime.Tests/bin/" + $Configuration + "/net8.0/Mahjong.TableRuntime.Tests.dll")
$originalHashBefore = Get-FixtureHash $originalFixture

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

    # All build/run temp state is isolated under this run's own evidence directory.
    $env:TEMP = $tempRoot
    $env:TMP = $tempRoot
    $env:DOTNET_CLI_HOME = Join-Path $tempRoot "dotnet-home"
    New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME | Out-Null

    $zip = Join-Path $tempRoot "head.zip"
    Write-Step ("isolated D: temp copy: " + $tempRepo)
    $archive = Invoke-Native "git" @("-C", $repo, "archive", "--format=zip", "--output=$zip", "HEAD") (Join-Path $evidenceDir "git-archive.log")
    if ($archive.ExitCode -ne 0) {
        Write-Step ("BLOCKED: git archive failed with exit " + $archive.ExitCode)
        exit 2
    }

    Expand-Archive -LiteralPath $zip -DestinationPath $tempRepo -Force
    foreach ($relative in @(
        "tests/v5-golden/visibility-v1.json",
        "dotnet/tests/Mahjong.TableRuntime.Tests/VisibilityGoldenTests.cs",
        "engine/src/test/kotlin/com/majiang/projection/V5VisibilityGoldenExporterTest.kt"
    )) {
        $source = Join-Path $repo $relative
        if (-not (Test-Path -LiteralPath $source)) {
            Write-Step ("BLOCKED: missing task file " + $relative)
            exit 2
        }

        $destination = Join-Path $tempRepo $relative
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination -Force
    }

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $fixturePath) | Out-Null
    Copy-Item -LiteralPath $fixturePath -Destination $baselineFixture -Force

    $project = Join-Path $tempRepo "dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj"
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

    $baseline = Invoke-VisibilityRun $Dotnet $dllPath $evidenceDir "baseline"
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
            Name = "negative-input-placement-tile"
            Kind = "INPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = {
                param($json)
                $json.cases[0].input.state.placements[0][0] = 2
            }
        },
        @{
            Name = "negative-input-event-owner-seat"
            Kind = "INPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = {
                param($json)
                $json.cases[1].input.events[1].seat = 2
            }
        },
        @{
            Name = "negative-output-snapshot-score"
            Kind = "OUTPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = {
                param($json)
                $json.cases[0].expected.snapshot.scores[0] = 24999
            }
        },
        @{
            Name = "negative-output-hand-count"
            Kind = "OUTPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = {
                param($json)
                $json.cases[0].expected.snapshot.handTileCounts[0] = 13
            }
        },
        @{
            Name = "negative-output-board-dora"
            Kind = "OUTPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = {
                param($json)
                $json.cases[2].expected.board.doraIndicators[0] = 31
            }
        },
        @{
            Name = "negative-output-event-tile"
            Kind = "OUTPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = {
                param($json)
                $json.cases[1].expected.visibility.unauthorized.'0'[1].tileIds[0] = 34
            }
        },
        @{
            Name = "negative-output-event-dropped"
            Kind = "OUTPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = {
                param($json)
                $json.cases[1].expected.visibility.unauthorized.'0' = @($json.cases[1].expected.visibility.unauthorized.'0' | Select-Object -Skip 1)
            }
        },
        @{
            Name = "negative-output-wall-remaining"
            Kind = "OUTPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = {
                param($json)
                $json.cases[3].expected.spectatorBoard.wallRemaining = $json.cases[3].expected.spectatorBoard.wallRemaining + 1
            }
        },
        @{
            Name = "negative-output-last-discard"
            Kind = "OUTPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = {
                param($json)
                $json.cases[3].expected.seatViews.'0'.lastDiscard = 40
            }
        },
        @{
            Name = "negative-privacy-opponent-id-public"
            Kind = "PRIVACY"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = {
                param($json)
                $json.cases[3].expected.hiddenTileIds.opponentClosedFromSeat0[0] = 37
            }
        },
        @{
            Name = "negative-privacy-public-dora-injection"
            Kind = "PRIVACY"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = {
                param($json)
                $json.cases[3].expected.spectatorBoard.doraIndicators = @(57, 14)
            }
        },
        @{
            Name = "negative-privacy-ura-injection"
            Kind = "PRIVACY"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = {
                param($json)
                $json.cases[2].expected.board.doraIndicators = @(29, 30)
            }
        },
        @{
            Name = "negative-privacy-cheat-balance"
            Kind = "PRIVACY"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = {
                param($json)
                $json.cases[3].expected.hiddenTileIds.privateCheatBalances = @(1000)
            }
        },
        @{
            Name = "negative-structure-duplicate-case"
            Kind = "STRUCTURE"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = {
                param($json)
                $json.cases = @($json.cases) + @($json.cases[0])
            }
        },
        @{
            Name = "negative-structure-remove-case"
            Kind = "STRUCTURE"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = {
                param($json)
                $json.cases = @($json.cases | Select-Object -First 3)
            }
        },
        @{
            Name = "negative-structure-extra-task-path"
            Kind = "STRUCTURE"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = {
                param($json)
                $json.taskPaths = @($json.taskPaths) + @("engine/src/main/kotlin/com/majiang/session/PlayerView.kt")
            }
        },
        @{
            Name = "negative-meta-schema"
            Kind = "META"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = {
                param($json)
                $json.schema = "v5-visibility-golden-v2"
            }
        },
        @{
            Name = "negative-meta-source-hash"
            Kind = "META"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = {
                param($json)
                $value = [string]$json.sourceHashes[0].sha256LfNormalized
                $json.sourceHashes[0].sha256LfNormalized = "0" + $value.Substring(1)
            }
        },
        @{
            Name = "negative-meta-render-hash"
            Kind = "META"
            Expected = "FAIL"
            RefreshHash = $false
            RawHashFlip = $true
            Apply = {
                param($json)
            }
        },
        @{
            Name = "negative-meta-baseline-commit"
            Kind = "META"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = {
                param($json)
                $json.v5BaselineCommit = "0232d775526de921f2cde3aba283236fc3b8e8c1"
            }
        },
        @{
            Name = "negative-input-current-player"
            Kind = "INPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = {
                param($json)
                $json.cases[0].input.state.currentPlayer = 2
            }
        },
        @{
            Name = "negative-output-meld-fromseat"
            Kind = "OUTPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = {
                param($json)
                $json.cases[0].expected.snapshot.melds[1][0].fromSeat = 2
            }
        },
        @{
            Name = "negative-output-meld-called"
            Kind = "OUTPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = {
                param($json)
                $json.cases[0].expected.snapshot.melds[1][0].calledTileId = 4
            }
        },
        @{
            Name = "negative-input-meld-fromseat"
            Kind = "INPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = {
                param($json)
                $json.cases[0].input.state.melds[1][0].fromSeat = 2
            }
        },
        @{
            Name = "negative-input-meld-called"
            Kind = "INPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = {
                param($json)
                $json.cases[0].input.state.melds[1][0].calledTileId = 4
            }
        },
        @{
            Name = "negative-input-meld-tile"
            Kind = "INPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = {
                param($json)
                $json.cases[0].input.state.melds[1][0].tileIds[0] = 1
            }
        },
        @{
            Name = "negative-input-discard-tile"
            Kind = "INPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = {
                param($json)
                $json.cases[0].input.state.discards[0][0] = 135
            }
        },
        @{
            Name = "negative-input-hand-tile"
            Kind = "INPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = {
                param($json)
                $json.cases[3].input.state.hands[1].closedTileIds[0] = 135
            }
        },
        @{
            Name = "negative-output-event-turn"
            Kind = "OUTPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = {
                param($json)
                $json.cases[1].expected.visibility.unauthorized.'0'[0].turnNumber = 999
            }
        },
        @{
            Name = "negative-output-event-fromseat"
            Kind = "OUTPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = {
                param($json)
                $json.cases[1].expected.visibility.unauthorized.'0'[3].fromSeat = 2
            }
        },
        @{
            Name = "negative-input-event-turn"
            Kind = "INPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = {
                param($json)
                $json.cases[1].input.events[0].turnNumber = 999
            }
        },
        @{
            Name = "negative-input-clock-deadline"
            Kind = "INPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = {
                param($json)
                $json.cases[2].input.clock.deadline = 999
            }
        },
        @{
            Name = "negative-output-copy-clocks"
            Kind = "OUTPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = {
                param($json)
                $json.cases[2].expected.copyProbe.clocksAfterMutation = 0
            }
        },
        @{
            Name = "negative-input-cheat-points"
            Kind = "INPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = {
                param($json)
                $json.cases[3].input.state.privateCheat.points[1] = 2000
            }
        },
        @{
            Name = "negative-input-cheat-reason"
            Kind = "INPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = {
                param($json)
                $json.cases[3].input.state.privateCheat.entries[0].reason = "CHANGED"
            }
        },
        @{
            Name = "negative-input-placement-swap"
            Kind = "INPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = {
                param($json)
                $json.cases[0].input.state.placements[0][0] = 1
                $json.cases[0].input.state.placements[1][0] = 0
            }
        },
        @{
            Name = "negative-input-breakdown-ura"
            Kind = "INPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = {
                param($json)
                $json.cases[2].input.events[8].breakdown.uraTiles[0] = 31
            }
        },
        @{
            Name = "negative-output-breakdown-ura"
            Kind = "OUTPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = {
                param($json)
                $json.cases[2].expected.tsumoBreakdown.authorized.uraTiles[0] = 35
            }
        },
        @{
            Name = "negative-c-input-event-yaku"
            Kind = "INPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = { param($json) $json.cases[2].input.events[8].yaku[0].code = "CHANGED" }
        },
        @{
            Name = "negative-c-input-breakdown-yaku"
            Kind = "INPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = { param($json) $json.cases[2].input.events[8].breakdown.yaku[0].code = "CHANGED" }
        },
        @{
            Name = "negative-c-input-breakdown-han"
            Kind = "INPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = { param($json) $json.cases[2].input.events[8].breakdown.han = 9 }
        },
        @{
            Name = "negative-c-input-breakdown-fu"
            Kind = "INPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = { param($json) $json.cases[2].input.events[8].breakdown.fu = 40 }
        },
        @{
            Name = "negative-c-output-breakdown-yaku"
            Kind = "OUTPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = { param($json) $json.cases[2].expected.tsumoBreakdown.authorized.yaku[0].code = "CHANGED" }
        },
        @{
            Name = "negative-c-output-breakdown-han"
            Kind = "OUTPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = { param($json) $json.cases[2].expected.tsumoBreakdown.authorized.han = 9 }
        },
        @{
            Name = "negative-c-output-breakdown-fu"
            Kind = "OUTPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = { param($json) $json.cases[2].expected.tsumoBreakdown.authorized.fu = 40 }
        },
        @{
            Name = "negative-c-output-unauth-private-ura"
            Kind = "PRIVACY"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = { param($json) $json.cases[2].expected.tsumoBreakdown.unauthorized.uraTiles = @(34) }
        },
        @{
            Name = "negative-c-output-unauth-private-urahits"
            Kind = "PRIVACY"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = { param($json) $json.cases[2].expected.tsumoBreakdown.unauthorized.uraHitTiles = @(35) }
        },
        @{
            Name = "negative-c-output-event-score"
            Kind = "OUTPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = { param($json) $json.cases[2].expected.settlement.authorized[4].score = 4100 }
        },
        @{
            Name = "negative-c-output-event-han"
            Kind = "OUTPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = { param($json) $json.cases[2].expected.settlement.authorized[4].han = 9 }
        },
        @{
            Name = "negative-c-output-event-fu"
            Kind = "OUTPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = { param($json) $json.cases[2].expected.settlement.authorized[4].fu = 40 }
        },
        @{
            Name = "negative-c-output-event-yaku"
            Kind = "OUTPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = { param($json) $json.cases[2].expected.settlement.authorized[4].yaku[0].code = "CHANGED" }
        },
        @{
            Name = "negative-c-output-event-turn"
            Kind = "OUTPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = { param($json) $json.cases[2].expected.settlement.authorized[4].turnNumber = 99 }
        },
        @{
            Name = "negative-c-input-event-turn"
            Kind = "INPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = { param($json) $json.cases[2].input.events[8].turnNumber = 10 }
        },
        @{
            Name = "negative-c-output-event-fromseat"
            Kind = "OUTPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = { param($json) $json.cases[2].expected.settlement.authorized[1].fromSeat = 2 }
        },
        @{
            Name = "negative-c-output-event-called"
            Kind = "OUTPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = { param($json) $json.cases[2].expected.settlement.authorized[1].calledTileId = 40 }
        },
        @{
            Name = "negative-c-output-event-riichi"
            Kind = "OUTPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = { param($json) $json.cases[2].expected.settlement.authorized[0].riichiDiscard = $true }
        },
        @{
            Name = "negative-c-output-event-wind"
            Kind = "OUTPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = { param($json) $json.cases[2].expected.settlement.authorized[5].roundWind = 2 }
        },
        @{
            Name = "negative-c-input-event-fromseat"
            Kind = "INPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = { param($json) $json.cases[2].input.events[5].fromSeat = 2 }
        },
        @{
            Name = "negative-c-output-event-breakdown-ura"
            Kind = "OUTPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = { param($json) $json.cases[2].expected.settlement.authorized[4].breakdown.uraTiles[0] = 35 }
        },
        @{
            Name = "negative-c-input-breakdown-authorization"
            Kind = "INPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = { param($json) $json.cases[2].input.events[8].breakdown.uraAuthorized = $false }
        },
        @{
            Name = "negative-c-output-breakdown-authorization"
            Kind = "OUTPUT"
            Expected = "FAIL"
            RefreshHash = $true
            Apply = { param($json) $json.cases[2].expected.tsumoBreakdown.authorized.uraAuthorized = $false }
        },
        @{
            Name = "positive-source-only-legal-actions"
            Kind = "SOURCE_ONLY"
            Expected = "PASS"
            RefreshHash = $true
            Apply = {
                param($json)
                $json.cases[0].sourceOnly.legalActions = "mutated but intentionally not compared by C#"
            }
        },
        @{
            Name = "positive-source-only-dead-wall"
            Kind = "SOURCE_ONLY"
            Expected = "PASS"
            RefreshHash = $true
            Apply = {
                param($json)
                $json.cases[3].sourceOnly.v4DeadWall[0] = 135
            }
        },
        @{
            Name = "positive-placement-order"
            Kind = "STRUCTURE"
            Expected = "PASS"
            RefreshHash = $true
            Apply = {
                param($json)
                $reversed = @($json.cases[3].input.state.placements)
                [array]::Reverse($reversed)
                $json.cases[3].input.state.placements = $reversed
            }
        },
        @{
            Name = "positive-scope-note"
            Kind = "META"
            Expected = "PASS"
            RefreshHash = $true
            Apply = {
                param($json)
                $json.generationResult.fixtureLineEndings = "LF-normalized"
            }
        }
    )

    foreach ($mutation in $mutations) {
        Copy-Item -LiteralPath $baselineFixture -Destination $fixturePath -Force
        if ($mutation.ContainsKey("RawHashFlip") -and $mutation.RawHashFlip) {
            $raw = [System.IO.File]::ReadAllText($fixturePath, [System.Text.Encoding]::UTF8)
            $raw = [regex]::Replace(
                $raw,
                '"renderSha256LfNormalized":\s*"([0-9a-f])',
                '"renderSha256LfNormalized": "0',
                1)
            [System.IO.File]::WriteAllText($fixturePath, $raw, [System.Text.UTF8Encoding]::new($false))
        } else {
            $json = Read-Fixture $fixturePath
            & $mutation.Apply $json
            if ($mutation.RefreshHash) {
                Update-RenderHash $json $fixturePath
            } else {
                Write-Fixture $json $fixturePath
            }
        }

        $result = Invoke-VisibilityRun $Dotnet $dllPath $evidenceDir $mutation.Name
        if (-not (Assert-CountGate $mutation.Expected $result.Native.ExitCode $result.Summary $mutation.Name)) {
            Write-Step ("FAIL: " + $mutation.Name + " expected " + $mutation.Expected + " / " + (Format-Summary $result.Summary))
        } elseif ($mutation.Expected -eq "FAIL") {
            Write-Step ("{0}: FAIL as expected (exit 1, {1})" -f $mutation.Name, (Format-Summary $result.Summary))
        } else {
            Write-Step ("{0}: PASS as expected (exit 0, {1})" -f $mutation.Name, (Format-Summary $result.Summary))
        }
    }

    # Representation positive: a CRLF checkout of the same fixture must still pass.
    Copy-Item -LiteralPath $baselineFixture -Destination $fixturePath -Force
    $lf = [System.IO.File]::ReadAllText($fixturePath, [System.Text.Encoding]::UTF8)
    $crlf = $lf.Replace("`r`n", "`n").Replace("`n", "`r`n")
    [System.IO.File]::WriteAllText($fixturePath, $crlf, [System.Text.UTF8Encoding]::new($false))
    $crlfResult = Invoke-VisibilityRun $Dotnet $dllPath $evidenceDir "positive-crlf-fixture"
    if (-not (Assert-CountGate "PASS" $crlfResult.Native.ExitCode $crlfResult.Summary "positive-crlf-fixture")) {
        Write-Step ("FAIL: positive-crlf-fixture / " + (Format-Summary $crlfResult.Summary))
    } else {
        Write-Step ("positive-crlf-fixture: PASS as expected (exit 0, {0})" -f (Format-Summary $crlfResult.Summary))
    }

    $expectedRuns = 1 + $mutations.Count + 1
    if ($script:Results.Count -ne $expectedRuns) {
        $script:Failures.Add("probe ran " + $script:Results.Count + " native runs, expected " + $expectedRuns)
    }

    $duplicateNames = $script:Results | Group-Object name | Where-Object Count -gt 1
    if ($null -ne $duplicateNames) {
        $script:Failures.Add("probe produced duplicate run names")
    }

    $script:Results | Export-Csv -LiteralPath $summaryPath -NoTypeInformation -Encoding utf8
    $originalHashAfter = Get-FixtureHash $originalFixture
    if ($originalHashBefore -ne $originalHashAfter) {
        $script:Failures.Add("original worktree fixture changed: " + $originalHashBefore + " -> " + $originalHashAfter)
    }

    if ($script:Failures.Count -gt 0) {
        foreach ($failure in $script:Failures) {
            Write-Step ("FAIL: " + $failure)
        }
        Write-Step ("FAIL: evidence=" + $evidenceDir)
        exit 1
    }

    Write-Step ("PASS: {0} native runs, baseline Total={1}, original fixture unchanged ({2})" -f `
        $script:Results.Count, $script:ExpectedTotal, $originalHashAfter)
    Write-Step ("evidence=" + $evidenceDir)
    exit 0
} catch {
    $script:Failures.Add("probe exception: " + $_.Exception.Message)
} finally {
    foreach ($failure in $script:Failures) {
        Write-Step ("FAIL: " + $failure)
    }

    if (-not $KeepTemp -and (Test-Path -LiteralPath $tempRoot)) {
        $resolved = (Resolve-Path -LiteralPath $tempRoot).Path
        $expectedPrefix = (Resolve-Path -LiteralPath $evidenceDir).Path + [System.IO.Path]::DirectorySeparatorChar + "tmp"
        if ($resolved -eq $expectedPrefix) {
            if (Test-Path -LiteralPath $Dotnet) {
                & $Dotnet build-server shutdown *> $null
            }

            for ($attempt = 0; $attempt -lt 3; $attempt++) {
                Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
                if (-not (Test-Path -LiteralPath $resolved)) {
                    break
                }

                Start-Sleep -Milliseconds 300
            }

            if (Test-Path -LiteralPath $resolved) {
                # Long analyzer-loader paths can exceed MAX_PATH even though the files are
                # no longer locked; use the extended-length prefix for the final attempt.
                try {
                    [System.IO.Directory]::Delete("\\?\" + $resolved, $true)
                } catch {
                    Write-Step ("WARN: own temp path is still locked: " + $resolved)
                }
            }
        } else {
            Write-Step ("WARN: refusing to delete unexpected temp path " + $resolved)
        }
    }
}

if ($script:Failures.Count -gt 0) {
    exit 1
}

Write-Step "PASS: probe completed"
exit 0
