#requires -Version 5.1
<#
.SYNOPSIS
  Mutation probe for the V5 movement golden binding (G-004..G-007).

.DESCRIPTION
  Builds the Domain test project in an isolated copy of HEAD (plus the current task
  files), runs the real TileMovementGoldenTests through the native xUnit runner, then
  applies one-field mutations to the copied fixture only:
    - INPUT negative: change one action seat
    - OUTPUT negative: change one expected wall pointer
    - STRUCTURE negative: duplicate one physical position
    - META negative: change the schema
    - SOURCE_ONLY positive: change v4TotalKanCount (must not affect the physical diff)
    - representation/order positive: reverse the placement array
    - representation/order positive: swap two hand slot indices
  The original worktree fixture is never modified. A -SelfNegative switch performs the
  baseline and then exits 1 deliberately, proving the probe's own failure path.

  Run:
    pwsh -NoProfile -File tools/v5-golden/probe-movement-binding.ps1

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

function Assert-Baseline($Run, [string]$Label) {
    if ($Run.Native.ExitCode -ne 0) {
        $script:Failures.Add($Label + ": native exit " + $Run.Native.ExitCode)
        return $false
    }

    if ($null -eq $Run.Summary) {
        $script:Failures.Add($Label + ": no xUnit summary")
        return $false
    }

    if ($Run.Summary.Total -lt 1) {
        $script:Failures.Add($Label + ": Total must be non-zero")
        return $false
    }

    if ($Run.Summary.Total -ne 10) {
        $script:Failures.Add($Label + ": expected 10 discovered tests, got " + $Run.Summary.Total)
        return $false
    }

    if ($Run.Summary.Failed -ne 0 -or $Run.Summary.Errors -ne 0 -or
        $Run.Summary.Skipped -ne 0 -or $Run.Summary.NotRun -ne 0) {
        $script:Failures.Add(
            $Label + ": expected 0 failed/errors/skipped/notRun, got " +
            $Run.Summary.Failed + "/" + $Run.Summary.Errors + "/" +
            $Run.Summary.Skipped + "/" + $Run.Summary.NotRun)
        return $false
    }

    return $true
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

    if ($Expected -eq "FAIL") {
        if ($native.ExitCode -eq 0) {
            $script:Failures.Add($Name + ": mutation unexpectedly passed (native exit 0)")
            return
        }

        if ($null -eq $summary) {
            $script:Failures.Add($Name + ": mutation failed without a real xUnit summary")
            return
        }

        if ($summary.Failed -lt 1) {
            $script:Failures.Add($Name + ": mutation exit was non-zero but Failed=0")
            return
        }

        Write-Step ("{0}: FAIL as expected (exit {1}, Total {2}, Failed {3})" -f $Name, $native.ExitCode, $summary.Total, $summary.Failed)
    }
    else {
        if ($native.ExitCode -ne 0) {
            $script:Failures.Add($Name + ": positive control failed with native exit " + $native.ExitCode)
            return
        }

        if ($null -eq $summary -or $summary.Failed -ne 0 -or $summary.Errors -ne 0) {
            $script:Failures.Add($Name + ": positive control did not report 0 failed/errors")
            return
        }

        Write-Step ("{0}: PASS as expected (exit 0, Total {1}, Failed 0)" -f $Name, $summary.Total)
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

    $baseline = Invoke-Baseline $Dotnet $dllPath $evidenceDir "baseline"
    if (-not (Assert-Baseline $baseline "baseline")) {
        $script:Results | Export-Csv -LiteralPath $summaryPath -NoTypeInformation -Encoding utf8
        Write-Step "FAIL: baseline"
        exit 1
    }

    Write-Step ("baseline: Total {0}, Failed 0, Errors 0, Skipped 0, Not Run 0" -f $baseline.Summary.Total)

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
            Name = "negative-structure-duplicate-position"
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
            Name = "negative-meta-schema"
            Kind = "META"
            Expected = "FAIL"
            Apply = {
                param($json)
                $json.schema = "not-v5-movement-golden-v1"
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
    if ($lineNative.ExitCode -ne 0 -or $null -eq $lineSummary -or
        $lineSummary.Failed -ne 0 -or $lineSummary.Errors -ne 0) {
        $script:Failures.Add("positive-line-ending-crlf: expected PASS after CRLF-only source checkout")
    }
    else {
        Write-Step ("positive-line-ending-crlf: PASS as expected (exit 0, Total {0}, Failed 0)" -f $lineSummary.Total)
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

    Write-Step ("PASS: baseline 10/0/0/0/0; 4 negative classes failed; 4 positive controls passed")
    Write-Step ("evidence: " + $evidenceDir)
    exit 0
}
finally {
    if (-not $KeepTemp -and (Test-Path -LiteralPath $tempRoot)) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
