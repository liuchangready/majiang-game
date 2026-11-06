#Requires -Version 5.1
[CmdletBinding()]
param([string]$OutputRoot)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not $OutputRoot) {
    $OutputRoot = Join-Path $root ('build\fast-build-tests\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8))
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

$script:assertions = 0
$script:failures = New-Object System.Collections.Generic.List[string]

function Check([string]$Name, [bool]$Condition, [string]$Detail = '') {
    $script:assertions++
    if (-not $Condition) {
        $script:failures.Add("$Name $Detail".Trim())
        Write-Host "FAIL $Name $Detail" -ForegroundColor Red
    } else {
        Write-Host "PASS $Name" -ForegroundColor Green
    }
}

Write-Host "=== Fast Build Optimization Test Suite ==="

# -----------------------------------------------------------------------------
# 1. Shared Plugin Builder & Provenance Contracts
# -----------------------------------------------------------------------------
$buildShared = Join-Path $root 'tools/unity/build-shared.ps1'
$checkShared = Join-Path $root 'tools/unity/check-shared-plugins.ps1'
$scratchPluginDir = Join-Path $OutputRoot 'scratch-plugins'
New-Item -ItemType Directory -Force -Path $scratchPluginDir | Out-Null

# 1a. Fast initial build creates baseline evidence
& $buildShared -PluginOutputDir $scratchPluginDir -Fast
$exit1 = $LASTEXITCODE
Check "shared-fast-build-native0" ($exit1 -eq 0) "exit=$exit1"

$scratchEvidence = Join-Path $scratchPluginDir 'shared-plugin-provenance.json'
Check "scratch-evidence-exists" (Test-Path -LiteralPath $scratchEvidence -PathType Leaf)
$evidence1 = Get-Content -LiteralPath $scratchEvidence -Raw | ConvertFrom-Json
Check "scratch-evidence-first-not-forced" ($evidence1.build.forcedRebuild -eq $false)

# 1b. Fast second build reuses previous evidence
& $buildShared -PluginOutputDir $scratchPluginDir -Fast
$exitReuse = $LASTEXITCODE
Check "shared-fast-reuse-native0" ($exitReuse -eq 0) "exit=$exitReuse"
$evidenceReuse = Get-Content -LiteralPath $scratchEvidence -Raw | ConvertFrom-Json
Check "scratch-evidence-reused" ($evidenceReuse.build.reused -eq $true)
Check "scratch-evidence-reused-not-forced" ($evidenceReuse.build.forcedRebuild -eq $false)

# 1c. Checker default accepts reused record
& $checkShared -ProjectRoot $root -PluginDir $scratchPluginDir -EvidencePath $scratchEvidence -Configuration Release
$checkExit1 = $LASTEXITCODE
Check "checker-accepts-reused" ($checkExit1 -eq 0) "exit=$checkExit1"

# 1d. Checker with -RequireForcedRebuild rejects reused record
& $checkShared -ProjectRoot $root -PluginDir $scratchPluginDir -EvidencePath $scratchEvidence -Configuration Release -RequireForcedRebuild 2>&1 | Out-Null
$checkExitForced = $LASTEXITCODE
Check "checker-rejects-reused-when-forced-required" ($checkExitForced -ne 0) "exit=$checkExitForced"

# 1d. Forced build produces forcedRebuild=true record
& $buildShared -PluginOutputDir $scratchPluginDir -ForcedRebuild
$exit2 = $LASTEXITCODE
Check "shared-forced-build-native0" ($exit2 -eq 0) "exit=$exit2"
$evidence2 = Get-Content -LiteralPath $scratchEvidence -Raw | ConvertFrom-Json
Check "scratch-evidence-forced" ($evidence2.build.forcedRebuild -eq $true)
Check "scratch-evidence-reused-false" ($evidence2.build.reused -eq $false)

# 1e. Checker with -RequireForcedRebuild accepts forced record
& $checkShared -ProjectRoot $root -PluginDir $scratchPluginDir -EvidencePath $scratchEvidence -Configuration Release -RequireForcedRebuild
$checkExitForcedPass = $LASTEXITCODE
Check "checker-accepts-forced" ($checkExitForcedPass -eq 0) "exit=$checkExitForcedPass"

# 1f. Byte identity preserves mtime
$targetDll = Join-Path $scratchPluginDir 'Mahjong.Domain.dll'
$mtimeBefore = (Get-Item -LiteralPath $targetDll).LastWriteTimeUtc
Start-Sleep -Milliseconds 1100
& $buildShared -PluginOutputDir $scratchPluginDir -Fast
$mtimeAfter = (Get-Item -LiteralPath $targetDll).LastWriteTimeUtc
Check "mtime-preserved-on-reuse" ($mtimeBefore -eq $mtimeAfter) "before=$mtimeBefore after=$mtimeAfter"

# -----------------------------------------------------------------------------
# 2. Android Staging Guard Boundaries
# -----------------------------------------------------------------------------
$buildAndroid = Join-Path $root 'tools/unity/build-c05-android-emulator.ps1'
$androidStagingFixtureOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $buildAndroid -StagingGuardFixtureTest 2>&1
$androidStagingExit = $LASTEXITCODE
$androidStagingText = $androidStagingFixtureOutput -join ' '
Check "android-staging-guard-native0" ($androidStagingExit -eq 0) "exit=$androidStagingExit"
Check "android-staging-guard-marker" ($androidStagingText -match 'STAGING_GUARD_FIXTURE PASS')
Check "android-staging-guard-18-rejected" ($androidStagingText -match 'rejected count=18')
Check "android-staging-guard-accept-external" ($androidStagingText -match 'ACCEPT_EXTERNAL_REUSE')

# -----------------------------------------------------------------------------
# 3. Shader & URP Asset Trimming
# -----------------------------------------------------------------------------
$urpAsset = Join-Path $root 'unity/Mahjong.Unity/Assets/Settings/UniversalRP.asset'
$urpText = Get-Content -LiteralPath $urpAsset -Raw
Check "urp-terrain-holes-trimmed" ($urpText -match 'm_SupportsTerrainHoles:\s*0')

$urpGlobal = Join-Path $root 'unity/Mahjong.Unity/Assets/Settings/UniversalRenderPipelineGlobalSettings.asset'
$urpGlobalText = Get-Content -LiteralPath $urpGlobal -Raw
Check "urp-terrain-shaders-trimmed" ($urpGlobalText -match 'm_IncludeTerrainShaders:\s*0')

$graphicsAsset = Join-Path $root 'unity/Mahjong.Unity/ProjectSettings/GraphicsSettings.asset'
$graphicsText = Get-Content -LiteralPath $graphicsAsset -Raw
Check "graphics-specular-trimmed" ($graphicsText -notmatch '15104')
Check "graphics-diffuse-trimmed" ($graphicsText -notmatch '15105')
Check "graphics-retains-ui-default" ($graphicsText -match '10753')
Check "graphics-retains-sprites-default" ($graphicsText -match '10770')
Check "graphics-retains-sprites-mask" ($graphicsText -match '10783')

# -----------------------------------------------------------------------------
# 4. Fast Profile Script Contracts
# -----------------------------------------------------------------------------
$c05EditorCs = Get-Content -LiteralPath (Join-Path $root 'unity/Mahjong.Unity/Assets/Editor/C05EmulatorAndroidBuild.cs') -Raw
Check "c05-editor-fast-profile" ($c05EditorCs -match '-c05EmuFastProfile')
Check "c05-editor-optimize-size" ($c05EditorCs -match 'Il2CppCodeGeneration\.OptimizeSize')
Check "c05-editor-lz4" ($c05EditorCs -match 'CompressWithLz4')

$c04EditorCs = Get-Content -LiteralPath (Join-Path $root 'unity/Mahjong.Unity/Assets/Editor/C04PrivacyBuild.cs') -Raw
Check "c04-editor-fast-profile" ($c04EditorCs -match '-c04PrivacyFastProfile')
Check "c04-editor-logs-fast-profile" ($c04EditorCs -match 'C04_PRIVACY_FAST_PROFILE: profile=FAST scriptingBackend=Mono')

$c04Runner = Get-Content -LiteralPath (Join-Path $root 'tools/unity/run-p45-c04-privacy-matrix.ps1') -Raw
Check "c04-runner-has-fast-param" ($c04Runner -match '\[switch\]\$Fast')
Check "c04-runner-has-sync-authored" ($c04Runner -match 'function Sync-C04StagingAuthored')
Check "c04-runner-passes-fast-to-build-shared" ($c04Runner -match 'if \(\$Fast\) \{ \$sharedArgs \+= ''-Fast'' \}')

$c05Builder = Get-Content -LiteralPath (Join-Path $root 'tools/unity/build-c05-android-emulator.ps1') -Raw
Check "c05-builder-has-fast-param" ($c05Builder -match '\[switch\]\$Fast')
Check "c05-builder-passes-fast-to-build-shared" ($c05Builder -match 'if \(\$Fast\) \{ \$sharedArgs \+= ''-Fast'' \}')

# -----------------------------------------------------------------------------
# Summary
# -----------------------------------------------------------------------------
$summary = [pscustomobject]@{
    schema = 1
    status = if ($script:failures.Count -eq 0) { 'PASS' } else { 'FAIL' }
    assertions = $script:assertions
    failures = $script:failures.ToArray()
    generatedUtc = (Get-Date).ToUniversalTime().ToString('o')
}
[IO.File]::WriteAllText((Join-Path $OutputRoot 'fast-build-test-summary.json'), ($summary | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))

if ($script:failures.Count -gt 0) {
    Write-Host "FAST BUILD TESTS FAILED assertions=$($script:assertions) failures=$($script:failures.Count)" -ForegroundColor Red
    exit 1
}

Write-Host "FAST BUILD TESTS PASSED assertions=$($script:assertions)" -ForegroundColor Green
exit 0
