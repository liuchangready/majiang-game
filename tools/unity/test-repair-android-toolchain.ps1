<#
.SYNOPSIS
    Focused tests for the P2.2 Android toolchain repair and recheck wrapper.

.DESCRIPTION
    The tests exercise the real functions from:
      tools/unity/test-wire-aot-platform-recheck.ps1
      tools/unity/repair-android-toolchain.ps1
    rather than a re-implementation. They cover native exit propagation, bounded
    timeout, paths with spaces, marker/APK negative cases, exclusive run
    isolation, private-path rejection, shared-tool fingerprint comparison, a real
    CMake+Ninja before/after configure/build, the real check-only toolchain
    verification, and (when supplied) the real APK produced by this run.

    All fixtures are written below build/android-toolchain-tests/<stamp>-<guid>.
    No shared Unity/SDK/JDK file is modified.

.PARAMETER ApkPath
    Optional real APK from the current run. When present it is parsed and must
    be a valid com.mahjong.aotsmoke APK.

.NOTES
    Exit: 0 all cases PASS; 1 any FAIL. A missing real APK is NOT_RUN, not PASS.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string]$PrivateRoot,
    [string]$ApkPath,
    [int]$TimeoutCaseSeconds = 2
)

$ErrorActionPreference = 'Stop'
if (-not $RepoRoot) {
    $RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
}
$RepoRoot = [System.IO.Path]::GetFullPath($RepoRoot)
if (-not $PrivateRoot) {
    $PrivateRoot = Join-Path $RepoRoot 'build\android-toolchain\private'
}
$PrivateRoot = [System.IO.Path]::GetFullPath($PrivateRoot)

$testRoot = Join-Path $RepoRoot 'build\android-toolchain-tests'
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
$runDir = $null
for ($attempt = 0; $attempt -lt 10; $attempt++) {
    $candidate = Join-Path $testRoot ("{0}-{1}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), [guid]::NewGuid().ToString('N').Substring(0, 12))
    if (Test-Path -LiteralPath $candidate) { continue }
    try {
        New-Item -ItemType Directory -Path $candidate -ErrorAction Stop | Out-Null
        $runDir = $candidate
        break
    }
    catch {
        if (Test-Path -LiteralPath $candidate) { continue }
        throw
    }
}
if (-not $runDir) { throw "Could not allocate a fresh test directory under $testRoot" }

$script:Results = New-Object System.Collections.Generic.List[object]
function Write-Utf8NoBom {
    param([Parameter(Mandatory)][string]$Path, [AllowEmptyString()][string]$Text)
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($false)))
}
function Get-Sha256 {
    param([string]$Path)
    if (-not $Path -or -not (Test-Path -LiteralPath $Path)) { return $null }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}
function Add-Case {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][ValidateSet('PASS', 'FAIL', 'NOT_RUN')][string]$Status, [string]$Detail)
    $script:Results.Add([pscustomobject]@{ name = $Name; status = $Status; detail = $Detail })
    $color = if ($Status -eq 'PASS') { 'Green' } elseif ($Status -eq 'FAIL') { 'Red' } else { 'Yellow' }
    Write-Host ("[{0}] {1}: {2}" -f $Status, $Name, $Detail) -ForegroundColor $color
}
function Assert-Case {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][bool]$Condition, [string]$Detail)
    Add-Case -Name $Name -Status $(if ($Condition) { 'PASS' } else { 'FAIL' }) -Detail $Detail
}
function Get-AstFunctionText {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Name)
    $tokens = $null
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
    if ($errors.Count -gt 0) { throw "Parse errors in $Path" }
    $functionAst = $ast.Find({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $Name
        }, $true)
    if (-not $functionAst) { throw "Function $Name not found in $Path" }
    return $functionAst.Extent.Text
}
function Get-ChildProcessCount {
    param([string]$Marker)
    return @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($Marker) }).Count
}

$wrapperPath = Join-Path $RepoRoot 'tools\unity\test-wire-aot-platform-recheck.ps1'
$repairPath = Join-Path $RepoRoot 'tools\unity\repair-android-toolchain.ps1'
Start-Transcript -Path (Join-Path $runDir 'tests.log') -Force | Out-Null

try {
    # Load the actual wrapper functions. Invoke-NativeProcess depends on the
    # argument converter, a writer and the owned-process tree stopper.
    Invoke-Expression (Get-AstFunctionText -Path $wrapperPath -Name 'ConvertTo-NativeArgument')
    function Stop-OwnedProcessTree {
        param([System.Diagnostics.Process]$Process, [hashtable]$TrackedInstances = @{}, [object]$RootCreationDate = $null, [string]$EvidencePath)
        if ($Process -and -not $Process.HasExited) {
            & taskkill.exe /PID $Process.Id /T /F 2>&1 | Out-Null
        }
    }
    Invoke-Expression (Get-AstFunctionText -Path $wrapperPath -Name 'Invoke-NativeProcess')
    Invoke-Expression (Get-AstFunctionText -Path $wrapperPath -Name 'Get-AndroidBuildVerdict')
    Invoke-Expression (Get-AstFunctionText -Path $wrapperPath -Name 'New-ExclusiveRunDirectory')
    Invoke-Expression (Get-AstFunctionText -Path $wrapperPath -Name 'Test-AndroidApk')
    Invoke-Expression (Get-AstFunctionText -Path $repairPath -Name 'Assert-PrivateToolPath')
    Invoke-Expression (Get-AstFunctionText -Path $repairPath -Name 'Compare-SharedFingerprint')

    # -----------------------------------------------------------------------
    # Native process behavior
    # -----------------------------------------------------------------------
    $normal = Invoke-NativeProcess -FilePath 'cmd.exe' -Arguments @('/d', '/c', 'echo NORMAL_OK') -TimeoutSeconds 20 `
        -LogPrefix (Join-Path $runDir 'native-normal')
    Assert-Case -Name 'native normal exit' -Condition ($normal.ExitCode -eq 0 -and -not $normal.TimedOut) -Detail "exit=$($normal.ExitCode) timedOut=$($normal.TimedOut)"
    Assert-Case -Name 'native normal stdout' -Condition ($normal.Stdout -match 'NORMAL_OK') -Detail "stdout=$($normal.Stdout.Trim())"

    $nonzero = Invoke-NativeProcess -FilePath 'cmd.exe' -Arguments @('/d', '/c', 'exit 7') -TimeoutSeconds 20 `
        -LogPrefix (Join-Path $runDir 'native-nonzero')
    Assert-Case -Name 'native nonzero exit' -Condition ($nonzero.ExitCode -eq 7 -and -not $nonzero.TimedOut) -Detail "exit=$($nonzero.ExitCode) timedOut=$($nonzero.TimedOut)"

    $missingPath = Join-Path $runDir ("missing-{0}.exe" -f [guid]::NewGuid().ToString('N'))
    $missingThrew = $false
    try {
        $null = Invoke-NativeProcess -FilePath $missingPath -Arguments @() -TimeoutSeconds 5 `
            -LogPrefix (Join-Path $runDir 'native-missing')
    }
    catch { $missingThrew = $true }
    Assert-Case -Name 'native missing executable throws' -Condition $missingThrew -Detail "path=$missingPath"

    $timeoutScript = Join-Path $runDir 'timeout-child.ps1'
    Write-Utf8NoBom -Path $timeoutScript -Text ("`$host.UI.RawUI.WindowTitle = 'DSH_TIMEOUT_CHILD'`r`nStart-Sleep -Seconds 30`r`n")
    $timedOut = Invoke-NativeProcess -FilePath 'powershell.exe' -Arguments @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $timeoutScript) `
        -TimeoutSeconds $TimeoutCaseSeconds -LogPrefix (Join-Path $runDir 'native-timeout')
    Start-Sleep -Milliseconds 500
    $leftover = Get-ChildProcessCount -Marker $timeoutScript
    Assert-Case -Name 'native timeout is bounded' -Condition ($timedOut.TimedOut -and $timedOut.ExitCode -eq -1 -and $timedOut.DurationSec -lt ($TimeoutCaseSeconds + 10)) `
        -Detail "timedOut=$($timedOut.TimedOut) exit=$($timedOut.ExitCode) elapsed=$($timedOut.DurationSec)s"
    Assert-Case -Name 'native timeout leaves no owned child' -Condition ($leftover -eq 0) -Detail "children=$leftover"

    $spaceDir = Join-Path $runDir 'dir with spaces'
    New-Item -ItemType Directory -Force -Path $spaceDir | Out-Null
    $spaceScript = Join-Path $spaceDir 'probe script.ps1'
    Write-Utf8NoBom -Path $spaceScript -Text ("Write-Output 'SPACE_OK'`r`nexit 9`r`n")
    $spaceRun = Invoke-NativeProcess -FilePath 'powershell.exe' -Arguments @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $spaceScript) `
        -TimeoutSeconds 20 -LogPrefix (Join-Path $runDir 'native-spaces')
    Assert-Case -Name 'native path with spaces' -Condition ($spaceRun.ExitCode -eq 9 -and $spaceRun.Stdout -match 'SPACE_OK') `
        -Detail "exit=$($spaceRun.ExitCode) stdout=$($spaceRun.Stdout.Trim())"

    # -----------------------------------------------------------------------
    # Android build verdict negatives
    # -----------------------------------------------------------------------
    $good = @{
        TimedOut = $false; ExitCode = 0; ApkExists = $true; ApkFresh = $true; ApkValid = $true
        MarkerFound = $true; MarkerTarget = 'Android'; MarkerBackend = 'IL2CPP'; MarkerResult = 'Succeeded'
        MarkerErrors = 0; MarkerOutputMatches = $true
    }
    $goodVerdict = Get-AndroidBuildVerdict @good
    Assert-Case -Name 'verdict accepts exact success' -Condition ($goodVerdict.Pass) -Detail "reasons=$($goodVerdict.Reasons -join ',')"

    $cases = @(
        @{ name = 'verdict rejects timeout'; patch = @{ TimedOut = $true; ExitCode = -1 } },
        @{ name = 'verdict rejects nonzero exit'; patch = @{ ExitCode = 3 } },
        @{ name = 'verdict rejects missing apk'; patch = @{ ApkExists = $false } },
        @{ name = 'verdict rejects stale apk'; patch = @{ ApkFresh = $false } },
        @{ name = 'verdict rejects invalid apk'; patch = @{ ApkValid = $false } },
        @{ name = 'verdict rejects missing marker'; patch = @{ MarkerFound = $false } },
        @{ name = 'verdict rejects Failed marker'; patch = @{ MarkerResult = 'Failed' } },
        @{ name = 'verdict rejects errors=1'; patch = @{ MarkerErrors = 1 } },
        @{ name = 'verdict rejects wrong backend'; patch = @{ MarkerBackend = 'Mono2x' } },
        @{ name = 'verdict rejects wrong target'; patch = @{ MarkerTarget = 'StandaloneWindows64' } },
        @{ name = 'verdict rejects output mismatch'; patch = @{ MarkerOutputMatches = $false } }
    )
    foreach ($case in $cases) {
        $input = @{}
        foreach ($key in $good.Keys) { $input[$key] = $good[$key] }
        foreach ($key in $case.patch.Keys) { $input[$key] = $case.patch[$key] }
        $verdict = Get-AndroidBuildVerdict @input
        Assert-Case -Name $case.name -Condition (-not $verdict.Pass) -Detail "reasons=$($verdict.Reasons -join ',')"
    }

    # -----------------------------------------------------------------------
    # Exclusive run allocation
    # -----------------------------------------------------------------------
    $isolatedRoot = Join-Path $runDir 'runs'
    $first = New-ExclusiveRunDirectory -Root $isolatedRoot -Stamp '20261027-000000'
    $second = New-ExclusiveRunDirectory -Root $isolatedRoot -Stamp '20261027-000000'
    Write-Utf8NoBom -Path (Join-Path $first 'marker.txt') -Text "FIRST_ONLY`r`n"
    Assert-Case -Name 'run isolation uses fresh directories' -Condition ($first -ne $second) -Detail "first=$first second=$second"
    Assert-Case -Name 'run isolation hides stale marker' -Condition (-not (Test-Path -LiteralPath (Join-Path $second 'marker.txt'))) -Detail "secondMarkerExists=$(Test-Path -LiteralPath (Join-Path $second 'marker.txt'))"

    # -----------------------------------------------------------------------
    # Private path guard and shared fingerprint comparison
    # -----------------------------------------------------------------------
    $privateOk = $false
    try {
        $null = Assert-PrivateToolPath -Path (Join-Path $PrivateRoot 'ninja\1.13.2\ninja.exe') -PrivateRoot $PrivateRoot
        $privateOk = $true
    }
    catch { $privateOk = $false }
    Assert-Case -Name 'private path accepts private tool' -Condition $privateOk -Detail $PrivateRoot

    $sharedRejected = $false
    try {
        $null = Assert-PrivateToolPath -Path (Join-Path $RepoRoot 'tools\unity\test-wire-aot.ps1') -PrivateRoot $PrivateRoot
    }
    catch { $sharedRejected = $true }
    Assert-Case -Name 'private path rejects shared tracked file' -Condition $sharedRejected -Detail (Join-Path $RepoRoot 'tools\unity\test-wire-aot.ps1')

    $absoluteRejected = $false
    try {
        $null = Assert-PrivateToolPath -Path 'C:\Windows\System32\cmd.exe' -PrivateRoot $PrivateRoot
    }
    catch { $absoluteRejected = $true }
    Assert-Case -Name 'private path rejects outside absolute path' -Condition $absoluteRejected -Detail 'C:\Windows\System32\cmd.exe'

    $before = @(
        [pscustomobject]@{ role = 'a'; sha256 = 'AA'; bytes = 1 },
        [pscustomobject]@{ role = 'b'; sha256 = 'BB'; bytes = 2 }
    )
    $same = @(
        [pscustomobject]@{ role = 'a'; sha256 = 'AA'; bytes = 1 },
        [pscustomobject]@{ role = 'b'; sha256 = 'BB'; bytes = 2 }
    )
    $changed = @(
        [pscustomobject]@{ role = 'a'; sha256 = 'AA'; bytes = 1 },
        [pscustomobject]@{ role = 'b'; sha256 = 'CC'; bytes = 2 }
    )
    $sameDiff = @(Compare-SharedFingerprint -Before $before -After $same)
    $changedDiff = @(Compare-SharedFingerprint -Before $before -After $changed)
    Assert-Case -Name 'shared fingerprint accepts identical' -Condition ($sameDiff.Count -eq 0) -Detail "changes=$($sameDiff -join ';')"
    Assert-Case -Name 'shared fingerprint detects mutation' -Condition ($changedDiff.Count -eq 1) -Detail "changes=$($changedDiff -join ';')"

    # -----------------------------------------------------------------------
    # Real CMake + Ninja before/after (private ninja only on PATH)
    # -----------------------------------------------------------------------
    $manifestPath = Join-Path $PrivateRoot 'toolchain.json'
    $manifest = $null
    if (Test-Path -LiteralPath $manifestPath) {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    }
    Assert-Case -Name 'toolchain manifest exists' -Condition ([bool]$manifest) -Detail $manifestPath

    if ($manifest) {
        $ninjaExe = [string]$manifest.ninjaExe
        $cmakeExe = [string]$manifest.cmake
        $ninjaDir = [string]$manifest.ninjaDir
        $ninjaVersion = ((& $ninjaExe --version 2>&1 | Out-String).Trim() -split "`r?`n" | Select-Object -First 1)
        Assert-Case -Name 'private ninja version and hash' -Condition ($ninjaVersion -eq [string]$manifest.ninjaVersion -and (Get-Sha256 -Path $ninjaExe) -eq [string]$manifest.ninjaSha256) `
            -Detail "version=$ninjaVersion hash=$(Get-Sha256 -Path $ninjaExe)"

        $cmakeProject = Join-Path $runDir 'cmake-project'
        New-Item -ItemType Directory -Force -Path $cmakeProject | Out-Null
        Write-Utf8NoBom -Path (Join-Path $cmakeProject 'CMakeLists.txt') -Text @"
cmake_minimum_required(VERSION 3.20)
project(Tiny NONE)
add_custom_target(hello ALL COMMAND `${CMAKE_COMMAND} -E echo NINJA_OK)
"@
        $systemPath = "$env:SystemRoot\System32;$env:SystemRoot"
        $beforeBuild = Join-Path $runDir 'cmake-before'
        $beforeRun = Invoke-NativeProcess -FilePath $cmakeExe -Arguments @('-S', $cmakeProject, '-B', $beforeBuild, '-G', 'Ninja') -TimeoutSeconds 60 `
            -LogPrefix (Join-Path $runDir 'cmake-before') -Environment @{ PATH = $systemPath }
        Assert-Case -Name 'cmake without ninja fails' -Condition ($beforeRun.ExitCode -ne 0) -Detail "exit=$($beforeRun.ExitCode) stderr=$((($beforeRun.Stderr + $beforeRun.Stdout) -split "`r?`n" | Where-Object { $_ -match 'ninja|CMAKE_MAKE_PROGRAM' } | Select-Object -First 1))"

        $afterBuild = Join-Path $runDir 'cmake-after'
        $afterRun = Invoke-NativeProcess -FilePath $cmakeExe -Arguments @('-S', $cmakeProject, '-B', $afterBuild, '-G', 'Ninja') -TimeoutSeconds 60 `
            -LogPrefix (Join-Path $runDir 'cmake-after') -Environment @{ PATH = "$ninjaDir;$systemPath" }
        Assert-Case -Name 'cmake with private ninja configures' -Condition ($afterRun.ExitCode -eq 0) -Detail "exit=$($afterRun.ExitCode)"
        $buildRun = Invoke-NativeProcess -FilePath $cmakeExe -Arguments @('--build', $afterBuild) -TimeoutSeconds 60 `
            -LogPrefix (Join-Path $runDir 'cmake-build') -Environment @{ PATH = "$ninjaDir;$systemPath" }
        Assert-Case -Name 'private ninja builds real target' -Condition ($buildRun.ExitCode -eq 0 -and ($buildRun.Stdout + $buildRun.Stderr) -match 'NINJA_OK') `
            -Detail "exit=$($buildRun.ExitCode) output=$((($buildRun.Stdout + $buildRun.Stderr) -split "`r?`n" | Where-Object { $_ -match 'NINJA_OK' } | Select-Object -First 1))"

        $shimPath = [string]$manifest.androidCliBin
        $shimHash = Get-Sha256 -Path $shimPath
        $shimText = if (Test-Path -LiteralPath $shimPath) { Get-Content -LiteralPath $shimPath -Raw } else { '' }
        Assert-Case -Name 'private CLI adapter is real passthrough' -Condition ($shimHash -and $shimText -match 'sdkmanager\.bat' -and $shimText -match 'sdk list' -and $shimText -match 'refused') `
            -Detail "path=$shimPath sha256=$shimHash"
    }

    # -----------------------------------------------------------------------
    # Real repair check-only run and shared tools unchanged
    # -----------------------------------------------------------------------
    $checkDir = Join-Path $runDir 'check-only'
    New-Item -ItemType Directory -Force -Path $checkDir | Out-Null
    $checkLog = Join-Path $runDir 'check-only.stdout.log'
    $checkErr = Join-Path $runDir 'check-only.stderr.log'
    $checkPsi = New-Object System.Diagnostics.ProcessStartInfo
    $checkPsi.FileName = 'powershell.exe'
    $checkPsi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$repairPath`" -CheckOnly"
    $checkPsi.UseShellExecute = $false
    $checkPsi.CreateNoWindow = $true
    $checkPsi.RedirectStandardOutput = $true
    $checkPsi.RedirectStandardError = $true
    $checkProc = New-Object System.Diagnostics.Process
    $checkProc.StartInfo = $checkPsi
    $null = $checkProc.Start()
    $checkOut = $checkProc.StandardOutput.ReadToEnd()
    $checkErrText = $checkProc.StandardError.ReadToEnd()
    $checkProc.WaitForExit()
    Write-Utf8NoBom -Path $checkLog -Text $checkOut
    Write-Utf8NoBom -Path $checkErr -Text $checkErrText
    Assert-Case -Name 'repair check-only exits 0' -Condition ($checkProc.ExitCode -eq 0) -Detail "exit=$($checkProc.ExitCode) log=$checkLog"

    $checkEvidence = $null
    $evidenceMatch = [regex]::Match($checkOut, 'evidence=(.+)')
    if ($evidenceMatch.Success) { $checkEvidence = $evidenceMatch.Groups[1].Value.Trim() }
    $checkSummary = $null
    if ($checkEvidence -and (Test-Path -LiteralPath (Join-Path $checkEvidence 'summary.json'))) {
        $checkSummary = Get-Content -LiteralPath (Join-Path $checkEvidence 'summary.json') -Raw | ConvertFrom-Json
    }
    Assert-Case -Name 'repair check-only records shared tools unchanged' `
        -Condition ([bool]$checkSummary -and @($checkSummary.sharedToolsChanged).Count -eq 0 -and [string]$checkSummary.exitCode -eq '0') `
        -Detail "evidence=$checkEvidence changed=$(@($checkSummary.sharedToolsChanged).Count)"

    # -----------------------------------------------------------------------
    # Real APK parse (required for a PASS delivery)
    # -----------------------------------------------------------------------
    if ($ApkPath -and (Test-Path -LiteralPath $ApkPath)) {
        $apk = Test-AndroidApk -Path $ApkPath -SdkRoot ([string]$manifest.sdkRoot) -LogPrefix (Join-Path $runDir 'real-apk-badging')
        Assert-Case -Name 'real APK parses as com.mahjong.aotsmoke' -Condition ($apk.Valid -and $apk.Package -eq 'com.mahjong.aotsmoke') `
            -Detail "valid=$($apk.Valid) package=$($apk.Package) bytes=$($apk.Bytes) sha256=$($apk.Sha256) reason=$($apk.Reason)"
    }
    else {
        Add-Case -Name 'real APK parses as com.mahjong.aotsmoke' -Status NOT_RUN -Detail "ApkPath not supplied or missing: $ApkPath"
    }
}
catch {
    Add-Case -Name 'test harness' -Status FAIL -Detail $_.Exception.Message
    Write-Host "STACK: $($_.ScriptStackTrace)"
}

$pass = @($script:Results | Where-Object { $_.status -eq 'PASS' }).Count
$fail = @($script:Results | Where-Object { $_.status -eq 'FAIL' }).Count
$notRun = @($script:Results | Where-Object { $_.status -eq 'NOT_RUN' }).Count
$total = $script:Results.Count
$exitCode = if ($fail -gt 0) { 1 } else { 0 }
$summary = [ordered]@{
    startedAt = (Get-Date).ToString('o')
    repoRoot = $RepoRoot
    privateRoot = $PrivateRoot
    apkPath = $ApkPath
    total = $total
    pass = $pass
    fail = $fail
    notRun = $notRun
    skipped = 0
    exitCode = $exitCode
    results = $script:Results.ToArray()
}
Write-Utf8NoBom -Path (Join-Path $runDir 'summary.json') -Text ($summary | ConvertTo-Json -Depth 8)
Write-Utf8NoBom -Path (Join-Path $runDir 'results.txt') -Text (
    ("total={0} pass={1} fail={2} notRun={3} skipped=0 exit={4}`r`n" -f $total, $pass, $fail, $notRun, $exitCode) +
    (($script:Results | ForEach-Object { "{0} {1}: {2}" -f $_.status, $_.name, $_.detail }) -join "`r`n") + "`r`n"
)
Write-Host ''
Write-Host ("SUMMARY total={0} pass={1} fail={2} notRun={3} skipped=0 exit={4}" -f $total, $pass, $fail, $notRun, $exitCode)
Write-Host "evidence=$runDir"
try { Stop-Transcript | Out-Null } catch { }
exit $exitCode
