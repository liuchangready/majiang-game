#requires -Version 5.1
[CmdletBinding()]
param(
  [ValidateSet('All','Windows','Android')][string]$Platform = 'All',
  [ValidateSet('All','Baseline','Candidate')][string]$Version = 'All',
  [string]$CellFilter = '',
  [switch]$SkipSmoke
)

$ErrorActionPreference = 'Stop'

$script:BenchmarkRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$script:BaselineSrc = 'D:\c05s\b_src_66f31612'
$script:BaselineCsharpSrc = 'D:\c05s\b_src_csharp'
$script:CandidateSrc = $script:BenchmarkRoot
$script:CandidateCsharpSrc = 'D:\c05s\c_src_csharp'

$script:UnityPath = 'D:\game_dev_tools\unity\Editor\Unity.exe'
$script:DotnetPath = 'D:\game_dev_tools\dotnet\dotnet.exe'
$script:AndroidSdkRoot = 'D:\Android\Sdk'
$script:AvdHome = 'D:\Android\avd'
$script:PrivateToolchainRoot = 'E:\mj\c05-luna-20261106\build\c05luna\aa82604ee7a547628cf981557e408f32\toolchain\private'
$script:RegistryCacheSource = 'D:\c05s\aa82604ee7a547628cf981557e408f32\project\Library\PackageCache'

# Persistent short staging paths (32-hex GUIDs for Android guard compliance)
$script:CandidateWinStaging = 'D:\c05s\w05ecandidate20261107win00000001\staging-project'
$script:CandidateAndStaging = 'D:\c05s\d05ec05e202611070000000000000001'
$script:BaselineAndStaging = 'D:\c05s\d05eba5e202611070000000000000001'

$script:EvidenceBase = Join-Path $script:BenchmarkRoot 'build\unified-matrix-20261107'
[IO.Directory]::CreateDirectory($script:EvidenceBase) | Out-Null

function Get-C05ResourceSample {
  $os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
  return [pscustomobject]@{
    sampleUtc = [DateTimeOffset]::UtcNow.ToString('o')
    freePhysicalBytes = [long]($os.FreePhysicalMemory * 1024)
    totalPhysicalBytes = [long]($os.TotalVisibleMemorySize * 1024)
    freeVirtualBytes = [long]($os.FreeVirtualMemory * 1024)
    totalVirtualBytes = [long]($os.TotalVirtualMemorySize * 1024)
  }
}

function Format-DurationSeconds([double]$Seconds) {
  if ([double]::IsNaN($Seconds) -or [double]::IsInfinity($Seconds) -or $Seconds -lt 0) { return 'N/A' }
  return ("{0:F2}s" -f $Seconds)
}

function Invoke-CellWindows {
  param(
    [string]$CellId,
    [string]$Version,
    [string]$Scenario,
    [string]$RepoPath,
    [switch]$IsCandidate,
    [switch]$ReuseStaging
  )
  Write-Host "`n========================================================" -ForegroundColor Cyan
  Write-Host "[START] $CellId (Version=$Version Platform=Windows64 Scenario=$Scenario)" -ForegroundColor Cyan
  Write-Host "========================================================" -ForegroundColor Cyan

  $cellDir = Join-Path $script:EvidenceBase $CellId
  $runGuid = [guid]::NewGuid().ToString('N')
  $runRoot = Join-Path $cellDir $runGuid
  New-Item -ItemType Directory -Force -Path $runRoot | Out-Null

  $resBefore = Get-C05ResourceSample
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $status = 'UNKNOWN'
  $errorMsg = ''
  $summary = $null

  $args = @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass',
    '-File', (Join-Path $RepoPath 'tools\unity\run-p45-c04-privacy-matrix.ps1'),
    '-UnityPath', $script:UnityPath,
    '-DotnetPath', $script:DotnetPath,
    '-BuildPlayer',
    '-RunRoot', $runRoot
  )
  if ($IsCandidate) {
    $args += '-Fast'
    $args += @('-StagingRoot', $script:CandidateWinStaging)
    if ($ReuseStaging) {
      $args += '-ReuseStaging'
    }
  }

  $runnerLog = Join-Path $runRoot 'runner-stdout.log'
  try {
    Write-Host "Running Windows build in $RepoPath..."
    $proc = Start-Process -FilePath 'powershell.exe' -ArgumentList $args -RedirectStandardOutput $runnerLog -RedirectStandardError (Join-Path $runRoot 'runner-stderr.log') -PassThru -WindowStyle Hidden
    
    $lastBeat = [Diagnostics.Stopwatch]::StartNew()
    while (-not $proc.HasExited) {
      Start-Sleep -Seconds 5
      if ($lastBeat.Elapsed.TotalSeconds -ge 30) {
        Write-Host "HEARTBEAT [$CellId] elapsed=$([int]$sw.Elapsed.TotalSeconds)s procId=$($proc.Id)"
        $lastBeat.Restart()
      }
      if ($sw.Elapsed.TotalSeconds -gt 3600) {
        $proc.Kill()
        throw "Windows build timed out after 3600s"
      }
    }
    $nativeExit = $proc.ExitCode
    $sw.Stop()

    $summaryPath = Join-Path $runRoot 'summary.json'
    if (Test-Path -LiteralPath $summaryPath) {
      $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
      $status = [string]$summary.status
    } else {
      $status = if ($nativeExit -eq 0) { 'PASS' } else { 'FAIL' }
      if ($nativeExit -ne 0) {
        $errContent = Get-Content -LiteralPath (Join-Path $runRoot 'runner-stderr.log') -Raw -ErrorAction SilentlyContinue
        $errorMsg = "Process exited with code $nativeExit: $errContent"
      }
    }
  } catch {
    $sw.Stop()
    $status = 'FAIL'
    $errorMsg = $_.Exception.Message
    Write-Host "ERROR: $errorMsg" -ForegroundColor Red
  }

  $resAfter = Get-C05ResourceSample

  # Breakdown times extraction
  $lockWait = 0.0
  $inputSync = 0.0
  $sharedDll = 0.0
  $unityImportCompile = 0.0
  $csharp = 0.0
  $shader = 0.0
  $packageSign = 0.0
  $seal = 0.0

  if ($summary -and $summary.processes) {
    foreach ($p in $summary.processes) {
      if ($p.name -match 'shared-plugin|build-shared') {
        $sharedDll += [double]$p.durationSeconds
      } elseif ($p.name -eq 'unity-build-player') {
        $unityImportCompile += [double]$p.durationSeconds
      }
    }
  }
  if ($summary -and $summary.mutex) {
    $lockWait = [double]$summary.mutex.waitedSeconds
  }

  $playerExe = Join-Path $runRoot 'player\Mahjong.exe'
  $playerSha = ''
  $playerBytes = 0L
  if (Test-Path -LiteralPath $playerExe) {
    $playerSha = (Get-FileHash -LiteralPath $playerExe -Algorithm SHA256).Hash
    $playerBytes = (Get-Item -LiteralPath $playerExe).Length
  }

  Write-Host "[FINISH] $CellId status=$status wall=$([math]::Round($sw.Elapsed.TotalSeconds,2))s playerBytes=$playerBytes" -ForegroundColor $(if ($status -eq 'PASS') { 'Green' } else { 'Yellow' })

  return [pscustomobject]@{
    cellId = $CellId
    version = $Version
    platform = 'Windows64'
    scenario = $Scenario
    status = $status
    nativeExit = $nativeExit
    wallClockSeconds = [math]::Round($sw.Elapsed.TotalSeconds, 2)
    lockWaitSeconds = [math]::Round($lockWait, 2)
    inputSyncSeconds = [math]::Round($inputSync, 2)
    sharedDllSeconds = [math]::Round($sharedDll, 2)
    unityImportCompileSeconds = [math]::Round($unityImportCompile, 2)
    csharpSeconds = [math]::Round($csharp, 2)
    shaderSeconds = [math]::Round($shader, 2)
    il2cppSeconds = 'N/A'
    gradleSeconds = 'N/A'
    packageSignSeconds = [math]::Round($packageSign, 2)
    sealSeconds = [math]::Round($seal, 2)
    artifactPath = $playerExe
    artifactBytes = $playerBytes
    artifactSha256 = $playerSha
    cacheMode = if ($IsCandidate) { (if ($ReuseStaging) { 'FAST_REUSE' } else { 'FAST_COLD' }) } else { 'FORMAL_COLD' }
    resBefore = $resBefore
    resAfter = $resAfter
    runRoot = $runRoot
    error = $errorMsg
  }
}

function Invoke-CellAndroid {
  param(
    [string]$CellId,
    [string]$Version,
    [string]$Scenario,
    [string]$RepoPath,
    [switch]$IsCandidate,
    [switch]$ReuseStaging,
    [switch]$ReuseBuildCache
  )
  Write-Host "`n========================================================" -ForegroundColor Cyan
  Write-Host "[START] $CellId (Version=$Version Platform=Android-x86_64 Scenario=$Scenario)" -ForegroundColor Cyan
  Write-Host "========================================================" -ForegroundColor Cyan

  $cellDir = Join-Path $script:EvidenceBase $CellId
  $runGuid = [guid]::NewGuid().ToString('N')
  $runRoot = Join-Path $cellDir $runGuid

  $stagingRoot = if ($IsCandidate) { $script:CandidateAndStaging } else { $script:BaselineAndStaging }

  $resBefore = Get-C05ResourceSample
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $status = 'UNKNOWN'
  $errorMsg = ''
  $nativeExit = -1

  $args = @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass',
    '-File', (Join-Path $RepoPath 'tools\unity\build-c05-android-emulator.ps1'),
    '-UnityPath', $script:UnityPath,
    '-OutputRoot', $runRoot,
    '-StagingRoot', $stagingRoot,
    '-PrivateToolchainRoot', $script:PrivateToolchainRoot,
    '-RegistryCacheSource', $script:RegistryCacheSource,
    '-AndroidSdkRoot', $script:AndroidSdkRoot,
    '-AvdHome', $script:AvdHome
  )
  if ($IsCandidate) {
    $args += '-Fast'
    if ($ReuseStaging) { $args += '-ReuseStaging' }
    if ($ReuseBuildCache) { $args += '-ReuseBuildCache' }
  } else {
    # Baseline invocation: in baseline no-op/csharp scenarios, passing -ReuseStaging or reusing external staging
    # tests baseline's actual behavior (which throws BLOCKED)
    if ($ReuseStaging) { $args += '-ReuseStaging' }
    if ($ReuseBuildCache) { $args += '-ReuseBuildCache' }
  }

  $runnerLog = Join-Path $runRoot 'builder-stdout.log'
  [IO.Directory]::CreateDirectory($cellDir) | Out-Null

  try {
    Write-Host "Running Android build in $RepoPath..."
    $proc = Start-Process -FilePath 'powershell.exe' -ArgumentList $args -RedirectStandardOutput $runnerLog -RedirectStandardError (Join-Path $cellDir "$runGuid-stderr.log") -PassThru -WindowStyle Hidden

    $lastBeat = [Diagnostics.Stopwatch]::StartNew()
    while (-not $proc.HasExited) {
      Start-Sleep -Seconds 5
      if ($lastBeat.Elapsed.TotalSeconds -ge 30) {
        Write-Host "HEARTBEAT [$CellId] elapsed=$([int]$sw.Elapsed.TotalSeconds)s procId=$($proc.Id)"
        $lastBeat.Restart()
      }
      if ($sw.Elapsed.TotalSeconds -gt 7200) {
        $proc.Kill()
        throw "Android build timed out after 7200s"
      }
    }
    $nativeExit = $proc.ExitCode
    $sw.Stop()

    $buildReportJson = Join-Path $runRoot 'build-report.json'
    if (Test-Path -LiteralPath $buildReportJson) {
      $report = Get-Content -LiteralPath $buildReportJson -Raw | ConvertFrom-Json
      $status = if ($report.result -eq 'Succeeded') { 'PASS' } else { 'FAIL' }
    } else {
      if ($nativeExit -eq 0) {
        $status = 'PASS'
      } else {
        $errContent = Get-Content -LiteralPath (Join-Path $cellDir "$runGuid-stderr.log") -Raw -ErrorAction SilentlyContinue
        if ($errContent -match 'BLOCKED') {
          $status = 'BLOCKED'
          $errorMsg = ($errContent -split "`r?`n" | Where-Object { $_ -match 'BLOCKED' }) -join '; '
        } else {
          $status = 'FAIL'
          $errorMsg = "Process exited with code $nativeExit: $errContent"
        }
      }
    }
  } catch {
    $sw.Stop()
    $status = 'FAIL'
    $errorMsg = $_.Exception.Message
    Write-Host "ERROR: $errorMsg" -ForegroundColor Red
  }

  $resAfter = Get-C05ResourceSample

  # Breakdown times extraction
  $lockWait = 0.0
  $inputSync = 0.0
  $sharedDll = 0.0
  $unityImportCompile = 0.0
  $csharp = 0.0
  $shader = 0.0
  $il2cpp = 0.0
  $gradle = 0.0
  $packageSign = 0.0
  $seal = 0.0

  $detailJson = Join-Path $runRoot 'build-report-detail.json'
  if (Test-Path -LiteralPath $detailJson) {
    try {
      $detail = Get-Content -LiteralPath $detailJson -Raw | ConvertFrom-Json
      if ($detail.steps) {
        foreach ($st in $detail.steps) {
          $name = [string]$st.name
          $dur = [double]$st.durationSeconds
          if ($name -match 'Compile scripts') { $csharp += $dur }
          elseif ($name -match 'IL2CPP|il2cpp') { $il2cpp += $dur }
          elseif ($name -match 'Gradle|gradle') { $gradle += $dur }
          elseif ($name -match 'ProcessResources|Shader') { $shader += $dur }
        }
      }
    } catch { }
  }

  $apkPath = Join-Path $runRoot 'Mahjong-emulator-x86_64.apk'
  $apkSha = ''
  $apkBytes = 0L
  if (Test-Path -LiteralPath $apkPath) {
    $apkSha = (Get-FileHash -LiteralPath $apkPath -Algorithm SHA256).Hash
    $apkBytes = (Get-Item -LiteralPath $apkPath).Length
  }

  Write-Host "[FINISH] $CellId status=$status wall=$([math]::Round($sw.Elapsed.TotalSeconds,2))s apkBytes=$apkBytes" -ForegroundColor $(if ($status -eq 'PASS') { 'Green' } elseif ($status -eq 'BLOCKED') { 'DarkYellow' } else { 'Red' })

  return [pscustomobject]@{
    cellId = $CellId
    version = $Version
    platform = 'Android-x86_64'
    scenario = $Scenario
    status = $status
    nativeExit = $nativeExit
    wallClockSeconds = [math]::Round($sw.Elapsed.TotalSeconds, 2)
    lockWaitSeconds = [math]::Round($lockWait, 2)
    inputSyncSeconds = [math]::Round($inputSync, 2)
    sharedDllSeconds = [math]::Round($sharedDll, 2)
    unityImportCompileSeconds = [math]::Round($unityImportCompile, 2)
    csharpSeconds = [math]::Round($csharp, 2)
    shaderSeconds = [math]::Round($shader, 2)
    il2cppSeconds = [math]::Round($il2cpp, 2)
    gradleSeconds = [math]::Round($gradle, 2)
    packageSignSeconds = [math]::Round($packageSign, 2)
    sealSeconds = [math]::Round($seal, 2)
    artifactPath = $apkPath
    artifactBytes = $apkBytes
    artifactSha256 = $apkSha
    cacheMode = if ($IsCandidate) { (if ($ReuseStaging) { 'FAST_REUSE' } else { 'FAST_COLD' }) } else { 'FORMAL_COLD' }
    resBefore = $resBefore
    resAfter = $resAfter
    runRoot = $runRoot
    error = $errorMsg
  }
}

# ----------------- Execution Driver -----------------
$results = New-Object Collections.Generic.List[object]

# Prepare short staging clean state
if ($Platform -in @('All', 'Windows') -and $Version -in @('All', 'Candidate')) {
  if (Test-Path -LiteralPath (Split-Path $script:CandidateWinStaging -Parent)) {
    Remove-Item -Recurse -Force (Split-Path $script:CandidateWinStaging -Parent) -ErrorAction SilentlyContinue
  }
}
if ($Platform -in @('All', 'Android') -and $Version -in @('All', 'Candidate')) {
  if (Test-Path -LiteralPath $script:CandidateAndStaging) {
    Remove-Item -Recurse -Force $script:CandidateAndStaging -ErrorAction SilentlyContinue
  }
}
if ($Platform -in @('All', 'Android') -and $Version -in @('All', 'Baseline')) {
  if (Test-Path -LiteralPath $script:BaselineAndStaging) {
    Remove-Item -Recurse -Force $script:BaselineAndStaging -ErrorAction SilentlyContinue
  }
}

# 1. WINDOWS BASELINE
if ($Platform -in @('All', 'Windows') -and $Version -in @('All', 'Baseline')) {
  if (-not $CellFilter -or $CellFilter -eq 'B-WIN-COLD') {
    $r = Invoke-CellWindows -CellId 'B-WIN-COLD' -Version 'Baseline' -Scenario 'cold' -RepoPath $script:BaselineSrc -IsCandidate:$false
    $results.Add($r)
  }
  if (-not $CellFilter -or $CellFilter -eq 'B-WIN-NOOP') {
    $r = Invoke-CellWindows -CellId 'B-WIN-NOOP' -Version 'Baseline' -Scenario 'noop' -RepoPath $script:BaselineSrc -IsCandidate:$false
    $results.Add($r)
  }
  if (-not $CellFilter -or $CellFilter -eq 'B-WIN-CSHARP') {
    $r = Invoke-CellWindows -CellId 'B-WIN-CSHARP' -Version 'Baseline' -Scenario 'csharp' -RepoPath $script:BaselineCsharpSrc -IsCandidate:$false
    $results.Add($r)
  }
}

# 2. WINDOWS CANDIDATE
if ($Platform -in @('All', 'Windows') -and $Version -in @('All', 'Candidate')) {
  if (-not $CellFilter -or $CellFilter -eq 'C-WIN-COLD') {
    $r = Invoke-CellWindows -CellId 'C-WIN-COLD' -Version 'Candidate' -Scenario 'cold' -RepoPath $script:CandidateSrc -IsCandidate:$true -ReuseStaging:$false
    $results.Add($r)
  }
  if (-not $CellFilter -or $CellFilter -eq 'C-WIN-NOOP') {
    $r = Invoke-CellWindows -CellId 'C-WIN-NOOP' -Version 'Candidate' -Scenario 'noop' -RepoPath $script:CandidateSrc -IsCandidate:$true -ReuseStaging:$true
    $results.Add($r)
  }
  if (-not $CellFilter -or $CellFilter -eq 'C-WIN-CSHARP') {
    $r = Invoke-CellWindows -CellId 'C-WIN-CSHARP' -Version 'Candidate' -Scenario 'csharp' -RepoPath $script:CandidateCsharpSrc -IsCandidate:$true -ReuseStaging:$true
    $results.Add($r)
  }
}

# 3. ANDROID BASELINE
if ($Platform -in @('All', 'Android') -and $Version -in @('All', 'Baseline')) {
  if (-not $CellFilter -or $CellFilter -eq 'B-AND-COLD') {
    $r = Invoke-CellAndroid -CellId 'B-AND-COLD' -Version 'Baseline' -Scenario 'cold' -RepoPath $script:BaselineSrc -IsCandidate:$false -ReuseStaging:$false
    $results.Add($r)
  }
  if (-not $CellFilter -or $CellFilter -eq 'B-AND-NOOP') {
    $r = Invoke-CellAndroid -CellId 'B-AND-NOOP' -Version 'Baseline' -Scenario 'noop' -RepoPath $script:BaselineSrc -IsCandidate:$false -ReuseStaging:$true
    $results.Add($r)
  }
  if (-not $CellFilter -or $CellFilter -eq 'B-AND-CSHARP') {
    $r = Invoke-CellAndroid -CellId 'B-AND-CSHARP' -Version 'Baseline' -Scenario 'csharp' -RepoPath $script:BaselineCsharpSrc -IsCandidate:$false -ReuseStaging:$true
    $results.Add($r)
  }
}

# 4. ANDROID CANDIDATE
if ($Platform -in @('All', 'Android') -and $Version -in @('All', 'Candidate')) {
  if (-not $CellFilter -or $CellFilter -eq 'C-AND-COLD') {
    $r = Invoke-CellAndroid -CellId 'C-AND-COLD' -Version 'Candidate' -Scenario 'cold' -RepoPath $script:CandidateSrc -IsCandidate:$true -ReuseStaging:$false -ReuseBuildCache:$false
    $results.Add($r)
  }
  if (-not $CellFilter -or $CellFilter -eq 'C-AND-NOOP') {
    $r = Invoke-CellAndroid -CellId 'C-AND-NOOP' -Version 'Candidate' -Scenario 'noop' -RepoPath $script:CandidateSrc -IsCandidate:$true -ReuseStaging:$true -ReuseBuildCache:$true
    $results.Add($r)
  }
  if (-not $CellFilter -or $CellFilter -eq 'C-AND-CSHARP') {
    $r = Invoke-CellAndroid -CellId 'C-AND-CSHARP' -Version 'Candidate' -Scenario 'csharp' -RepoPath $script:CandidateCsharpSrc -IsCandidate:$true -ReuseStaging:$true -ReuseBuildCache:$true
    $results.Add($r)
  }
}

# Output Results JSON
$bindingOutput = [ordered]@{
  schemaVersion = 1
  kind = 'g2-fast-build-unified-matrix'
  generatedUtc = [DateTimeOffset]::UtcNow.ToString('o')
  taskCandidateHead = (& git -C $script:CandidateSrc rev-parse HEAD).Trim()
  baselineHead = '66f31612f23baf9e00e8eb242d0259c5ec2c9d9e'
  tools = [ordered]@{
    unity = $script:UnityPath
    dotnet = $script:DotnetPath
    androidSdk = $script:AndroidSdkRoot
    avd = $script:AvdHome
  }
  cells = @($results)
}

$bindingJsonPath = Join-Path $script:BenchmarkRoot 'doc\v5-重构\G2-FAST-BUILD-BINDING-20261107.json'
[IO.File]::WriteAllText($bindingJsonPath, ($bindingOutput | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
Write-Host "`nSaved Matrix Binding JSON to: $bindingJsonPath" -ForegroundColor Green

# Print Summary Table
Write-Host "`n======================= 12-CELL TIMING MATRIX SUMMARY =======================" -ForegroundColor Cyan
$results | Format-Table -Property cellId, version, platform, scenario, status, wallClockSeconds, sharedDllSeconds, unityImportCompileSeconds, csharpSeconds, il2cppSeconds, gradleSeconds, artifactBytes
