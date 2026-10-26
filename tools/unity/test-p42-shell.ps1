<#
.SYNOPSIS
    P4.2 Shell Complete Test & Verification Runner for Unity 6000.3.27f1
.DESCRIPTION
    Runs:
      1. Boundary policy enforcement & negative mutation matrix
      2. Unity EditMode tests via batchmode (-testPlatform EditMode)
      3. Unity PlayMode tests via batchmode (-testPlatform PlayMode)
      4. Windows 64-bit Standalone Player smoke test & screenshot verification
    Exits with code 0 on full success, non-zero on any failure.
#>
[CmdletBinding()]
param(
    [string]$UnityExe = "D:\Unity\Hub\Editor\6000.3.27f1\Editor\Unity.exe",
    [string]$ProjectPath = "unity/Mahjong.Unity",
    [string]$OutputDir = "build/p42",
    [string]$LogDir = "build/unity-logs",
    [switch]$SkipPlayer
)

$ErrorActionPreference = "Stop"
$swTotal = [System.Diagnostics.Stopwatch]::StartNew()

Write-Host "=========================================================="
Write-Host "🀄 P4.2 Platform Shell Automated Verification Runner"
Write-Host "Unity: $UnityExe"
Write-Host "Project: $ProjectPath"
Write-Host "=========================================================="

$resolvedUnity = $UnityExe
if (-not (Test-Path $resolvedUnity)) {
    throw "Unity executable not found: $resolvedUnity"
}

$resolvedProj = (Resolve-Path $ProjectPath).Path
$resolvedOutput = (Join-Path (Get-Location).Path $OutputDir)
$resolvedLogs = (Join-Path (Get-Location).Path $LogDir)

New-Item -ItemType Directory -Force -Path $resolvedOutput | Out-Null
New-Item -ItemType Directory -Force -Path $resolvedLogs | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $resolvedOutput "screenshots") | Out-Null

$results = @{
    BoundaryCheck = "NOT_RUN"
    BoundaryNegativeMatrix = "NOT_RUN"
    EditModeTests = "NOT_RUN"
    PlayModeTests = "NOT_RUN"
    PlayerBuild = "NOT_RUN"
    PlayerSmoke = "NOT_RUN"
}

# 1. Boundary checks
Write-Host "`n[STEP 1/5] Running Unity boundary checks..."
$boundaryScript = (Resolve-Path "tools/unity/check-unity-boundaries.ps1").Path
$bOut = & powershell.exe -ExecutionPolicy Bypass -File $boundaryScript
if ($LASTEXITCODE -ne 0) {
    Write-Host $bOut
    throw "Boundary check FAILED (ExitCode: $LASTEXITCODE)"
}
$results.BoundaryCheck = "PASS"
Write-Host "Boundary check: PASS"

Write-Host "`n[STEP 2/5] Running Boundary Negative Mutation Matrix (11 cases)..."
$bNegOut = & powershell.exe -ExecutionPolicy Bypass -File $boundaryScript -TestNegativeMatrix
if ($LASTEXITCODE -ne 0) {
    Write-Host $bNegOut
    throw "Boundary negative matrix FAILED (ExitCode: $LASTEXITCODE)"
}
$results.BoundaryNegativeMatrix = "PASS (11/11 blocked)"
Write-Host "Boundary negative matrix: PASS"

# 2. EditMode tests
Write-Host "`n[STEP 3/5] Running Unity EditMode Tests..."
$editXml = Join-Path $resolvedOutput "editmode-results.xml"
$editLog = Join-Path $resolvedLogs "editmode.log"
if (Test-Path $editXml) { Remove-Item $editXml -Force }

$psiEdit = New-Object System.Diagnostics.ProcessStartInfo
$psiEdit.FileName = $resolvedUnity
$psiEdit.Arguments = "-batchmode -runTests -testPlatform EditMode -projectPath `"$resolvedProj`" -testResults `"$editXml`" -logFile `"$editLog`""
$psiEdit.UseShellExecute = $false
$psiEdit.CreateNoWindow = $true

$procEdit = [System.Diagnostics.Process]::Start($psiEdit)
$cleanEdit = $procEdit.WaitForExit(300000)
if (-not $cleanEdit) {
    $procEdit.Kill()
    throw "EditMode test run timed out after 300 seconds"
}
if ($procEdit.ExitCode -ne 0 -or -not (Test-Path $editXml)) {
    throw "EditMode test run FAILED with exit code $($procEdit.ExitCode)"
}

[xml]$xmlEdit = Get-Content $editXml
$editTotal = [int]$xmlEdit.'test-run'.total
$editPassed = [int]$xmlEdit.'test-run'.passed
$editFailed = [int]$xmlEdit.'test-run'.failed
$editSkipped = [int]$xmlEdit.'test-run'.skipped
Write-Host "EditMode Tests: Total=$editTotal, Passed=$editPassed, Failed=$editFailed, Skipped=$editSkipped"
if ($editFailed -gt 0 -or $editSkipped -gt 0 -or $editTotal -eq 0 -or $editPassed -ne $editTotal) {
    throw "EditMode tests had $editFailed failures, $editSkipped skipped (Total: $editTotal)"
}
$results.EditModeTests = "PASS ($editPassed/$editTotal)"

# 3. PlayMode tests
Write-Host "`n[STEP 4/5] Running Unity PlayMode Tests..."
$playXml = Join-Path $resolvedOutput "playmode-results.xml"
$playLog = Join-Path $resolvedLogs "playmode.log"
if (Test-Path $playXml) { Remove-Item $playXml -Force }

$psiPlay = New-Object System.Diagnostics.ProcessStartInfo
$psiPlay.FileName = $resolvedUnity
$psiPlay.Arguments = "-batchmode -runTests -testPlatform PlayMode -projectPath `"$resolvedProj`" -testResults `"$playXml`" -logFile `"$playLog`""
$psiPlay.UseShellExecute = $false
$psiPlay.CreateNoWindow = $true

$procPlay = [System.Diagnostics.Process]::Start($psiPlay)
$cleanPlay = $procPlay.WaitForExit(300000)
if (-not $cleanPlay) {
    $procPlay.Kill()
    throw "PlayMode test run timed out after 300 seconds"
}
if ($procPlay.ExitCode -ne 0 -or -not (Test-Path $playXml)) {
    throw "PlayMode test run FAILED with exit code $($procPlay.ExitCode)"
}

[xml]$xmlPlay = Get-Content $playXml
$playTotal = [int]$xmlPlay.'test-run'.total
$playPassed = [int]$xmlPlay.'test-run'.passed
$playFailed = [int]$xmlPlay.'test-run'.failed
$playSkipped = [int]$xmlPlay.'test-run'.skipped
Write-Host "PlayMode Tests: Total=$playTotal, Passed=$playPassed, Failed=$playFailed, Skipped=$playSkipped"
if ($playFailed -gt 0 -or $playSkipped -gt 0 -or $playTotal -eq 0 -or $playPassed -ne $playTotal) {
    throw "PlayMode tests had $playFailed failures, $playSkipped skipped (Total: $playTotal)"
}
$results.PlayModeTests = "PASS ($playPassed/$playTotal)"

# 4. Post-compile compiled assemblies boundary check
Write-Host "Verifying boundary rules on compiled assemblies in Library/ScriptAssemblies..."
$bPostOut = & powershell.exe -ExecutionPolicy Bypass -File $boundaryScript
if ($LASTEXITCODE -ne 0) {
    Write-Host $bPostOut
    throw "Post-compile boundary check FAILED (ExitCode: $LASTEXITCODE)"
}
Write-Host "Post-compile boundary check: PASS"

# 5. Player Smoke Test
if (-not $SkipPlayer) {
    Write-Host "`n[STEP 5/5] Running Standalone Player Smoke Test & Screenshot Capture..."
    $playerDir = Join-Path $resolvedOutput "player"
    if (Test-Path $playerDir) { Remove-Item $playerDir -Recurse -Force }
    $playerExe = Join-Path $playerDir "Mahjong.exe"
    Write-Host "Building fresh player executable: $playerExe..."
    $playerLog = Join-Path $resolvedLogs "build-player.log"
    $psiBuild = New-Object System.Diagnostics.ProcessStartInfo
    $psiBuild.FileName = $resolvedUnity
    $psiBuild.Arguments = "-batchmode -quit -projectPath `"$resolvedProj`" -buildWindows64Player `"$playerExe`" -logFile `"$playerLog`""
    $psiBuild.UseShellExecute = $false
    $psiBuild.CreateNoWindow = $true
    $procBuild = [System.Diagnostics.Process]::Start($psiBuild)
    $cleanBuild = $procBuild.WaitForExit(300000)
    if (-not $cleanBuild) {
        $procBuild.Kill()
        throw "Player build timed out after 300 seconds"
    }
    if ($procBuild.ExitCode -ne 0 -or -not (Test-Path $playerExe)) {
        throw "Player build FAILED with exit code $($procBuild.ExitCode)"
    }
    $results.PlayerBuild = "PASS"

    $smokeScript = (Resolve-Path "tools/unity/run-player-smoke.ps1").Path
    $smokeOut = & powershell.exe -ExecutionPolicy Bypass -File $smokeScript -PlayerExe $playerExe -ScreenshotDir (Join-Path $resolvedOutput "screenshots")
    if ($LASTEXITCODE -ne 0) {
        Write-Host $smokeOut
        throw "Player smoke test FAILED (ExitCode: $LASTEXITCODE)"
    }
    $results.PlayerSmoke = "PASS"
    Write-Host "Player smoke test: PASS"
} else {
    $results.PlayerBuild = "SKIPPED"
    $results.PlayerSmoke = "SKIPPED"
}

$swTotal.Stop()
Write-Host "`n=========================================================="
Write-Host "VERIFICATION REPORT SUMMARY"
Write-Host "=========================================================="
foreach ($k in $results.Keys) {
    Write-Host ("{0,-26} : {1}" -f $k, $results[$k])
}
Write-Host ("{0,-26} : {1:N2}s" -f "Total Duration", $swTotal.Elapsed.TotalSeconds)
Write-Host "=========================================================="
Write-Host "ALL CHECKS PASSED (exit code 0)"
exit 0
