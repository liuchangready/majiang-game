#requires -Version 5.1
[CmdletBinding()]
param(
  [string]$RunRoot = '',
  [string]$UnityPath = 'D:\Unity\Hub\Editor\6000.3.27f1\Editor\Unity.exe',
  [string]$DotnetPath = 'D:\Unity\dotnet\dotnet.exe',
  [int]$TimeoutSeconds = 900,
  [int]$MutexTimeoutSeconds = 600,
  [int]$MutexHeartbeatSeconds = 30,
  [int]$ProcessCloseGraceSeconds = 15,
  [string]$MutexCancelFile = '',
  [string]$ExpectedSourceHead = '',
  [string]$PlayerBuildMarker = '',
  [switch]$PreflightOnly,
  [switch]$BuildPlayer,
  [switch]$WindowsPlayerTests,
  [switch]$LoadFunctionsOnly
)

$ErrorActionPreference = 'Stop'

$script:C04MutexName = 'Local\MahjongV5P45UnityValidation'
$script:C04ExpectedTests = @(
  'Mahjong.Unity.Tests.PlayMode.C04PrivacyRoleSwitchPlayModeTests.RealRoom_FourPlayersSpectatorAndRoleSwitchUseIndependentSessions',
  'Mahjong.Unity.Tests.PlayMode.C04PrivacyRoleSwitchPlayModeTests.RealRoom_DelayedOldRawReceiveIsRejectedByNewBinding',
  'Mahjong.Unity.Tests.PlayMode.C04PrivacyRoleSwitchPlayModeTests.RealRoom_ColdReopenReconcilesOriginalRequestWithoutDuplicate'
)
$script:C04RequiredScenes = @(
  'Assets/Scenes/Boot.unity',
  'Assets/Scenes/Menu.unity',
  'Assets/Scenes/Lobby.unity',
  'Assets/Scenes/Table.unity',
  'Assets/Scenes/Spectator.unity',
  'Assets/Scenes/Settlement.unity'
)
$script:C04RequiredPlugins = @(
  'Mahjong.Domain.dll',
  'Mahjong.Contracts.dll',
  'Mahjong.Contracts.Wire.dll',
  'Mahjong.TableRuntime.dll',
  'Mahjong.Client.dll',
  'Mahjong.LocalHost.dll',
  'Mahjong.Ai.dll',
  'Google.Protobuf.dll'
)

function Write-C04Utf8([string]$Path, [string]$Text) {
  $directory = Split-Path -Parent ([IO.Path]::GetFullPath($Path))
  if ($directory) { [IO.Directory]::CreateDirectory($directory) | Out-Null }
  [IO.File]::WriteAllText($Path, $Text, (New-Object Text.UTF8Encoding($false)))
}

function Write-C04Json([string]$Path, $Value) {
  Write-C04Utf8 $Path (($Value | ConvertTo-Json -Depth 12) + "`n")
}

function Get-C04Sha256Bytes([byte[]]$Bytes) {
  if ($null -eq $Bytes) { throw 'Cannot hash a null byte array.' }
  $sha = [Security.Cryptography.SHA256]::Create()
  try { return ([BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant() }
  finally { $sha.Dispose() }
}

function Get-C04Sha256File([string]$Path) {
  if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "File is missing for hashing: $Path" }
  $sha = [Security.Cryptography.SHA256]::Create()
  $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
  try { return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
  finally { $stream.Dispose(); $sha.Dispose() }
}

function Get-C04FileManifest {
  param(
    [Parameter(Mandatory = $true)][string]$Root,
    [string[]]$ExcludeDirectoryNames = @('.git', 'Library', 'Temp', 'obj', 'bin', 'Logs', 'UserSettings'),
    [string[]]$ExcludeFileNames = @()
  )
  $rootFull = [IO.Path]::GetFullPath($Root)
  if (-not (Test-Path -LiteralPath $rootFull -PathType Container)) { throw "Manifest root is missing: $rootFull" }
  $excludedDirs = @{}
  foreach ($name in $ExcludeDirectoryNames) { $excludedDirs[$name.ToLowerInvariant()] = $true }
  $excludedFiles = @{}
  foreach ($name in $ExcludeFileNames) { $excludedFiles[$name.ToLowerInvariant()] = $true }

  $rows = New-Object Collections.Generic.List[object]
  $all = Get-ChildItem -LiteralPath $rootFull -Recurse -File -Force
  foreach ($file in $all) {
    $relative = $file.FullName.Substring($rootFull.Length).TrimStart('\', '/')
    $parts = $relative -split '[\\/]'
    $skip = $false
    for ($i = 0; $i -lt ($parts.Length - 1); $i++) {
      if ($excludedDirs.ContainsKey($parts[$i].ToLowerInvariant())) { $skip = $true; break }
    }
    if ($skip) { continue }
    if ($excludedFiles.ContainsKey($parts[$parts.Length - 1].ToLowerInvariant())) { continue }
    $rows.Add([pscustomobject]@{
      path = ($relative -replace '\\', '/')
      length = [long]$file.Length
      sha256 = (Get-C04Sha256File $file.FullName)
    })
  }
  $sorted = @($rows | Sort-Object path)
  $lines = New-Object Collections.Generic.List[string]
  [long]$bytes = 0
  foreach ($row in $sorted) {
    $lines.Add($row.sha256 + '  ' + $row.path)
    $bytes += $row.length
  }
  $canonical = ($lines -join "`n") + "`n"
  return [pscustomobject]@{
    root = $rootFull
    count = [int]$sorted.Count
    bytes = $bytes
    sha256 = (Get-C04Sha256Bytes ([Text.Encoding]::UTF8.GetBytes($canonical)))
    canonical = $canonical
    entries = $sorted
  }
}

function Assert-C04ManifestEqual($Expected, $Actual, [string]$Label) {
  if ($null -eq $Expected -or $null -eq $Actual) { throw "$Label manifest is missing." }
  if ($Expected.sha256 -ne $Actual.sha256 -or $Expected.count -ne $Actual.count -or $Expected.bytes -ne $Actual.bytes) {
    $expectedMap = @{}
    foreach ($entry in $Expected.entries) { $expectedMap[$entry.path] = $entry }
    $actualMap = @{}
    foreach ($entry in $Actual.entries) { $actualMap[$entry.path] = $entry }
    $differences = New-Object Collections.Generic.List[string]
    foreach ($path in $expectedMap.Keys) {
      if (-not $actualMap.ContainsKey($path)) { $differences.Add("missing:$path"); continue }
      if ($expectedMap[$path].sha256 -ne $actualMap[$path].sha256) { $differences.Add("changed:$path") }
    }
    foreach ($path in $actualMap.Keys) {
      if (-not $expectedMap.ContainsKey($path)) { $differences.Add("extra:$path") }
    }
    $sample = ($differences | Select-Object -First 12) -join ', '
    throw "$Label input closure mismatch: expected $($Expected.count)/$($Expected.sha256), actual $($Actual.count)/$($Actual.sha256). $sample"
  }
}

function Assert-C04SourceIdentity {
  param(
    [Parameter(Mandatory = $true)][string]$Head,
    [string[]]$DirtyEntries = @(),
    [int]$DiffCheckExit = 0,
    [string]$ExpectedHead = ''
  )
  if ([string]::IsNullOrWhiteSpace($Head)) { throw 'HEAD is empty; candidate source is not frozen.' }
  if ([string]$Head -notmatch '^[0-9a-f]{40}$') { throw "HEAD is not a 40-character lowercase commit: $Head" }
  if (@($DirtyEntries).Count -ne 0) { throw "Source tree is dirty: $(@($DirtyEntries) -join '; ')" }
  if ($DiffCheckExit -ne 0) { throw "git diff --check failed with exit $DiffCheckExit." }
  if (-not [string]::IsNullOrWhiteSpace($ExpectedHead) -and
      -not [string]::Equals($Head, $ExpectedHead, [StringComparison]::OrdinalIgnoreCase)) {
    throw "HEAD $Head does not match the frozen candidate $ExpectedHead."
  }
  return [pscustomobject]@{ head = $Head.ToLowerInvariant(); clean = $true; diffCheckExit = 0 }
}

function Get-C04SourceIdentity {
  param([Parameter(Mandatory = $true)][string]$Repo, [string]$ExpectedHead = '')
  $head = (& git -C $Repo rev-parse HEAD).Trim()
  if ($LASTEXITCODE -ne 0) { throw 'git rev-parse HEAD failed.' }
  $status = @(& git -C $Repo status --porcelain=v1 -uall)
  if ($LASTEXITCODE -ne 0) { throw 'git status failed.' }
  & git -C $Repo diff --check | Out-Null
  $diffExit = $LASTEXITCODE
  $identity = Assert-C04SourceIdentity -Head $head -DirtyEntries $status -DiffCheckExit $diffExit -ExpectedHead $ExpectedHead
  $identity | Add-Member -NotePropertyName dirtyEntries -NotePropertyValue $status
  return $identity
}

function Get-C04SelectedManifest {
  param(
    [Parameter(Mandatory = $true)][string]$Base,
    [Parameter(Mandatory = $true)][string[]]$RelativeRoots,
    [string[]]$ExcludeDirectoryNames = @('.git', 'Library', 'Temp', 'obj', 'bin', 'Logs', 'UserSettings')
  )
  $baseFull = [IO.Path]::GetFullPath($Base)
  if (-not (Test-Path -LiteralPath $baseFull -PathType Container)) { throw "Selected-manifest base is missing: $baseFull" }
  $excludedDirs = @{}
  foreach ($name in $ExcludeDirectoryNames) { $excludedDirs[$name.ToLowerInvariant()] = $true }
  $rows = New-Object Collections.Generic.List[object]
  foreach ($relativeRoot in $RelativeRoots) {
    $candidate = Join-Path $baseFull $relativeRoot
    if (-not (Test-Path -LiteralPath $candidate -PathType Container)) { throw "Selected-manifest root is missing: $candidate" }
    foreach ($file in (Get-ChildItem -LiteralPath $candidate -Recurse -File -Force)) {
      $relative = $file.FullName.Substring($baseFull.Length).TrimStart('\', '/')
      $parts = $relative -split '[\\/]'
      $skip = $false
      for ($i = 0; $i -lt ($parts.Length - 1); $i++) {
        if ($excludedDirs.ContainsKey($parts[$i].ToLowerInvariant())) { $skip = $true; break }
      }
      if ($skip) { continue }
      $rows.Add([pscustomobject]@{
        path = ($relative -replace '\\', '/')
        length = [long]$file.Length
        sha256 = (Get-C04Sha256File $file.FullName)
      })
    }
  }
  $sorted = @($rows | Sort-Object path)
  $lines = New-Object Collections.Generic.List[string]
  [long]$bytes = 0
  foreach ($row in $sorted) {
    $lines.Add($row.sha256 + '  ' + $row.path)
    $bytes += $row.length
  }
  $canonical = ($lines -join "`n") + "`n"
  return [pscustomobject]@{
    root = $baseFull
    relativeRoots = $RelativeRoots
    count = [int]$sorted.Count
    bytes = $bytes
    sha256 = (Get-C04Sha256Bytes ([Text.Encoding]::UTF8.GetBytes($canonical)))
    canonical = $canonical
    entries = $sorted
  }
}

function Copy-C04DirectoryContents([string]$Source, [string]$Destination) {
  if (-not (Test-Path -LiteralPath $Source -PathType Container)) { throw "Copy source is missing: $Source" }
  [IO.Directory]::CreateDirectory($Destination) | Out-Null
  foreach ($item in (Get-ChildItem -LiteralPath $Source -Force)) {
    $target = Join-Path $Destination $item.Name
    if ($item.PSIsContainer) {
      Copy-C04DirectoryContents $item.FullName $target
    } else {
      [IO.File]::Copy($item.FullName, $target, $true)
    }
  }
}

function Copy-C04UnityInputs {
  param(
    [Parameter(Mandatory = $true)][string]$Repo,
    [Parameter(Mandatory = $true)][string]$Destination
  )
  $unityRoot = Join-Path $Repo 'unity/Mahjong.Unity'
  $relativeRoots = @('Assets', 'Packages', 'ProjectSettings')
  $before = Get-C04SelectedManifest -Base $unityRoot -RelativeRoots $relativeRoots
  [IO.Directory]::CreateDirectory($Destination) | Out-Null
  foreach ($name in $relativeRoots) { Copy-C04DirectoryContents (Join-Path $unityRoot $name) (Join-Path $Destination $name) }
  $after = Get-C04SelectedManifest -Base $Destination -RelativeRoots $relativeRoots
  Assert-C04ManifestEqual $before $after 'Unity staged input'
  return [pscustomobject]@{ source = $before; staged = $after }
}

function Assert-C04UnityXml {
  param(
    [Parameter(Mandatory = $true)][string]$XmlPath,
    [Parameter(Mandatory = $true)][string[]]$ExpectedTests
  )
  if (-not (Test-Path -LiteralPath $XmlPath -PathType Leaf)) { throw "Unity XML is missing: $XmlPath" }
  [xml]$document = [IO.File]::ReadAllText($XmlPath, [Text.Encoding]::UTF8)
  $root = $document.'test-run'
  if ($null -eq $root) { throw 'Unity XML has no test-run root.' }
  $expected = @($ExpectedTests)
  if ($expected.Count -lt 1) { throw 'No expected Unity test names were supplied.' }

  function Read-Attr($node, [string]$name) {
    $attribute = $node.Attributes[$name]
    if ($null -eq $attribute) { return $null }
    return $attribute.Value
  }
  function Read-Int($node, [string]$name, [int]$default) {
    $value = Read-Attr $node $name
    if ([string]::IsNullOrWhiteSpace($value)) { return $default }
    $parsed = 0
    if (-not [int]::TryParse($value, [ref]$parsed)) { throw "XML attribute $name is not an integer: $value" }
    return $parsed
  }

  $total = Read-Int $root 'total' -1
  $passed = Read-Int $root 'passed' -1
  $failed = Read-Int $root 'failed' -1
  $skipped = Read-Int $root 'skipped' -1
  $inconclusive = Read-Int $root 'inconclusive' 0
  $errors = Read-Int $root 'errors' 0
  $notRun = Read-Int $root 'not-run' 0
  $result = [string](Read-Attr $root 'result')
  if ($total -ne $expected.Count) { throw "Unity XML total=$total, expected $($expected.Count)." }
  if ($passed -ne $expected.Count) { throw "Unity XML passed=$passed, expected $($expected.Count)." }
  if ($failed -ne 0) { throw "Unity XML failed=$failed, expected 0." }
  if ($skipped -ne 0) { throw "Unity XML skipped=$skipped, expected 0." }
  if ($inconclusive -ne 0) { throw "Unity XML inconclusive=$inconclusive, expected 0." }
  if ($errors -ne 0) { throw "Unity XML errors=$errors, expected 0." }
  if ($notRun -ne 0) { throw "Unity XML not-run=$notRun, expected 0." }
  if ($result -ne 'Passed') { throw "Unity XML result=$result, expected Passed." }

  $cases = @($document.SelectNodes('//test-case'))
  if ($cases.Count -ne $expected.Count) { throw "Unity XML test-case nodes=$($cases.Count), expected $($expected.Count)." }
  $actualNames = New-Object Collections.Generic.List[string]
  foreach ($case in $cases) {
    $name = [string](Read-Attr $case 'fullname')
    if ([string]::IsNullOrWhiteSpace($name)) { $name = [string](Read-Attr $case 'name') }
    $caseResult = [string](Read-Attr $case 'result')
    if ($caseResult -ne 'Passed') { throw "Unity test case $name result=$caseResult." }
    if ($case.SelectSingleNode('.//failure') -ne $null -or $case.SelectSingleNode('.//error') -ne $null) {
      throw "Unity test case $name contains a failure/error node."
    }
    $actualNames.Add($name)
  }
  foreach ($caseName in $expected) {
    if (-not $actualNames.Contains($caseName)) { throw "Unity XML is missing expected case $caseName." }
  }
  foreach ($caseName in $actualNames) {
    if ($expected -notcontains $caseName) { throw "Unity XML contains unexpected case $caseName." }
  }
  foreach ($suite in @($document.SelectNodes('//test-suite'))) {
    $suiteResult = [string](Read-Attr $suite 'result')
    if ($suiteResult -eq 'Failed' -or $suiteResult -eq 'Skipped') { throw "Unity test suite reported $suiteResult." }
    if ((Read-Int $suite 'failed' 0) -ne 0) { throw 'Unity test suite reported a failed count.' }
    if ((Read-Int $suite 'skipped' 0) -ne 0) { throw 'Unity test suite reported a skipped count.' }
  }
  return [pscustomobject]@{
    total = $total; passed = $passed; failed = $failed; skipped = $skipped
    inconclusive = $inconclusive; errors = $errors; notRun = $notRun; result = $result
    cases = @($actualNames)
  }
}

function Assert-C04BuildMarker {
  param(
    [Parameter(Mandatory = $true)][string]$MarkerPath,
    [Parameter(Mandatory = $true)][string]$ExpectedSourceHead,
    [Parameter(Mandatory = $true)][string]$ExpectedBuildCommit
  )
  if (-not (Test-Path -LiteralPath $MarkerPath -PathType Leaf)) { throw "Player build marker is missing: $MarkerPath" }
  $marker = [IO.File]::ReadAllText($MarkerPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
  if ([int]$marker.schemaVersion -ne 1) { throw 'Player marker schemaVersion is not 1.' }
  if ([string]$marker.kind -ne 'c04-privacy-player-build') { throw 'Player marker kind is not c04-privacy-player-build.' }
  if ([string]$marker.result -ne 'Succeeded') { throw "Player marker result=$($marker.result), expected Succeeded." }
  if ([int]$marker.errors -ne 0) { throw "Player marker errors=$($marker.errors), expected 0." }
  if ([string]$marker.target -ne 'StandaloneWindows64') { throw "Player marker target=$($marker.target), expected StandaloneWindows64." }
  if (-not [string]::Equals([string]$marker.sourceHead, $ExpectedSourceHead, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Player marker sourceHead=$($marker.sourceHead), expected $ExpectedSourceHead."
  }
  if (-not [string]::Equals([string]$marker.buildCommit, $ExpectedBuildCommit, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Player marker buildCommit=$($marker.buildCommit), expected $ExpectedBuildCommit."
  }
  if (@($marker.errorMessages).Count -ne 0) { throw 'Player marker contains error messages.' }
  if ([string]::IsNullOrWhiteSpace([string]$marker.output)) { throw 'Player marker output is empty.' }
  $output = [IO.Path]::GetFullPath([string]$marker.output)
  if (-not (Test-Path -LiteralPath $output -PathType Leaf)) { throw "Player marker output is missing: $output" }
  $actualPlayerBytes = (Get-Item -LiteralPath $output).Length
  $actualPlayerSha = Get-C04Sha256File $output
  if ([long]$marker.playerBytes -ne $actualPlayerBytes) { throw "Player marker playerBytes=$($marker.playerBytes), actual=$actualPlayerBytes." }
  if (-not [string]::Equals([string]$marker.playerSha256, $actualPlayerSha, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Player marker playerSha256 does not match the actual executable.'
  }

  $sceneNames = @($marker.scenes)
  foreach ($scene in $script:C04RequiredScenes) {
    if ($sceneNames -notcontains $scene) { throw "Player marker is missing required scene $scene." }
  }
  foreach ($scene in $sceneNames) {
    if ($script:C04RequiredScenes -notcontains $scene) { throw "Player marker contains unexpected scene $scene." }
  }

  $pluginRoot = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $output) 'Mahjong_Data/Managed'))
  $markerPlugins = $marker.plugins
  if ($null -eq $markerPlugins) { throw 'Player marker has no plugin identity map.' }
  foreach ($name in $script:C04RequiredPlugins) {
    $property = $markerPlugins.PSObject.Properties[$name]
    if ($null -eq $property) { throw "Player marker is missing plugin $name." }
    $candidate = Join-Path $pluginRoot $name
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { throw "Built player plugin is missing: $candidate" }
    $actual = Get-C04Sha256File $candidate
    if (-not [string]::Equals([string]$property.Value, $actual, [StringComparison]::OrdinalIgnoreCase)) {
      throw "Player marker plugin hash for $name does not match the built closure."
    }
  }

  $packageManifestPath = [string]$marker.packageManifest
  if ([string]::IsNullOrWhiteSpace($packageManifestPath)) { throw 'Player marker packageManifest is empty.' }
  $packageManifestPath = [IO.Path]::GetFullPath($packageManifestPath)
  if (-not (Test-Path -LiteralPath $packageManifestPath -PathType Leaf)) { throw 'Player package manifest is missing.' }
  $manifestSha = Get-C04Sha256File $packageManifestPath
  if (-not [string]::Equals([string]$marker.packageManifestSha256, $manifestSha, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Player marker packageManifestSha256 does not match the manifest.'
  }
  $lines = @([IO.File]::ReadAllLines($packageManifestPath, [Text.Encoding]::UTF8) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
  if ($lines.Count -ne [int]$marker.packageFileCount) { throw "Player package manifest count=$($lines.Count), marker=$($marker.packageFileCount)." }
  $packageRoot = [IO.Path]::GetDirectoryName($output)
  [long]$packageBytes = 0
  $seen = @{}
  foreach ($line in $lines) {
    if ($line -notmatch '^([0-9a-fA-F]{64})  (.+)$') { throw "Malformed package manifest line: $line" }
    $expectedHash = $matches[1].ToLowerInvariant()
    $relative = $matches[2] -replace '/', '\'
    if ($seen.ContainsKey($relative)) { throw "Duplicate package manifest entry: $relative" }
    $seen[$relative] = $true
    $full = [IO.Path]::GetFullPath((Join-Path $packageRoot $relative))
    if (-not $full.StartsWith($packageRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "Package manifest path escapes the output: $relative" }
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "Package manifest file is missing: $relative" }
    $actual = Get-C04Sha256File $full
    if (-not [string]::Equals($expectedHash, $actual, [StringComparison]::OrdinalIgnoreCase)) { throw "Package file hash mismatch: $relative" }
    $packageBytes += (Get-Item -LiteralPath $full).Length
  }
  if ($packageBytes -ne [long]$marker.packageBytes) { throw "Player marker packageBytes=$($marker.packageBytes), actual=$packageBytes." }

  return [pscustomobject]@{
    markerPath = [IO.Path]::GetFullPath($MarkerPath)
    output = $output
    playerBytes = $actualPlayerBytes
    playerSha256 = $actualPlayerSha
    packageFileCount = $lines.Count
    packageBytes = $packageBytes
    packageManifest = $packageManifestPath
    packageManifestSha256 = $manifestSha
    sourceHead = [string]$marker.sourceHead
    buildCommit = [string]$marker.buildCommit
  }
}

function Stop-C04OwnedProcess {
  param(
    [Parameter(Mandatory = $true)]$Process,
    [Parameter(Mandatory = $true)][int]$OwnedPid,
    [Parameter(Mandatory = $true)][long]$StartTicks,
    [int]$GraceSeconds = 15
  )
  if ($null -eq $Process) { throw 'Owned process handle is null.' }
  if ($Process.Id -ne $OwnedPid) { throw "Refusing to stop PID $($Process.Id); expected owned PID $OwnedPid." }
  if ($Process.HasExited) { return [pscustomobject]@{ stopped = $true; normalCloseHonored = $true; forceStopped = $false; alive = $false } }
  $actualTicks = $Process.StartTime.ToUniversalTime().Ticks
  if ($actualTicks -ne $StartTicks) { throw "Refusing to stop PID $OwnedPid; process start identity changed." }

  $normalRequested = $false
  try { $normalRequested = $Process.CloseMainWindow() } catch { $normalRequested = $false }
  if ($Process.WaitForExit([Math]::Max(1, $GraceSeconds) * 1000)) {
    return [pscustomobject]@{ stopped = $true; normalCloseHonored = $true; forceStopped = $false; alive = $false }
  }

  if ($Process.HasExited) {
    return [pscustomobject]@{ stopped = $true; normalCloseHonored = $true; forceStopped = $false; alive = $false }
  }
  $actualTicks = $Process.StartTime.ToUniversalTime().Ticks
  if ($actualTicks -ne $StartTicks -or $Process.Id -ne $OwnedPid) {
    throw "Refusing to terminate PID $OwnedPid after identity changed; residual process must be reported UNKNOWN."
  }
  try { $Process.Kill() } catch { throw "Owned process $OwnedPid did not honor normal close and Kill failed: $($_.Exception.Message)" }
  if (-not $Process.WaitForExit(10000)) {
    throw "Owned process $OwnedPid is still alive after owned termination; resources must not be released."
  }
  return [pscustomobject]@{ stopped = $true; normalCloseHonored = $false; forceStopped = $true; alive = $false; normalCloseRequested = $normalRequested }
}

function Invoke-C04OwnedProcess {
  param(
    [Parameter(Mandatory = $true)][string]$Name,
    [Parameter(Mandatory = $true)][string]$FileName,
    [string[]]$Arguments = @(),
    [Parameter(Mandatory = $true)][string]$WorkingDirectory,
    [Parameter(Mandatory = $true)][int]$TimeoutSeconds,
    [string]$CancelFile = '',
    [int]$CloseGraceSeconds = 15,
    [string]$EvidenceDirectory = '',
    [int]$HeartbeatSeconds = 30
  )
  if ([string]::IsNullOrWhiteSpace($EvidenceDirectory)) { $EvidenceDirectory = $WorkingDirectory }
  [IO.Directory]::CreateDirectory($EvidenceDirectory) | Out-Null
  $stdoutPath = Join-Path $EvidenceDirectory ($Name + '.stdout.log')
  $stderrPath = Join-Path $EvidenceDirectory ($Name + '.stderr.log')
  $exitPath = Join-Path $EvidenceDirectory ($Name + '.exit')
  $timeoutPath = Join-Path $EvidenceDirectory ($Name + '.timeout')
  $cancelPath = Join-Path $EvidenceDirectory ($Name + '.cancelled')
  foreach ($path in @($stdoutPath, $stderrPath, $exitPath, $timeoutPath, $cancelPath)) {
    if (Test-Path -LiteralPath $path) { throw "Process evidence already exists: $path" }
  }
  $psi = New-Object Diagnostics.ProcessStartInfo
  $psi.FileName = $FileName
  $psi.Arguments = (($Arguments | ForEach-Object { '"' + ([string]$_).Replace('"', '\"') + '"' }) -join ' ')
  $psi.WorkingDirectory = [IO.Path]::GetFullPath($WorkingDirectory)
  $psi.UseShellExecute = $false
  $psi.CreateNoWindow = $true
  $psi.RedirectStandardOutput = $true
  $psi.RedirectStandardError = $true
  $process = New-Object Diagnostics.Process
  $process.StartInfo = $psi
  $sw = [Diagnostics.Stopwatch]::StartNew()
  if (-not $process.Start()) { throw "$Name did not start." }
  $pidValue = $process.Id
  $startTicks = $process.StartTime.ToUniversalTime().Ticks
  $stdoutStream = [IO.File]::Open($stdoutPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
  $stderrStream = [IO.File]::Open($stderrPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
  $stdoutTask = $process.StandardOutput.BaseStream.CopyToAsync($stdoutStream)
  $stderrTask = $process.StandardError.BaseStream.CopyToAsync($stderrStream)
  $timedOut = $false
  $cancelled = $false
  $stopResult = $null
  $nextHeartbeat = [DateTime]::UtcNow.AddSeconds([Math]::Max(1, $HeartbeatSeconds))
  try {
    while (-not $process.WaitForExit(1000)) {
      if (-not [string]::IsNullOrWhiteSpace($CancelFile) -and (Test-Path -LiteralPath $CancelFile)) {
        $cancelled = $true
        Write-C04Utf8 $cancelPath ("cancel-file observed at " + [DateTimeOffset]::UtcNow.ToString('o') + "`n")
        $stopResult = Stop-C04OwnedProcess -Process $process -OwnedPid $pidValue -StartTicks $startTicks -GraceSeconds $CloseGraceSeconds
        break
      }
      if ($sw.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
        $timedOut = $true
        Write-C04Utf8 $timeoutPath ("timeoutSeconds=" + $TimeoutSeconds + "`n")
        $stopResult = Stop-C04OwnedProcess -Process $process -OwnedPid $pidValue -StartTicks $startTicks -GraceSeconds $CloseGraceSeconds
        break
      }
      if ([DateTime]::UtcNow -ge $nextHeartbeat) {
        Write-Host ("[" + [DateTimeOffset]::UtcNow.ToString('o') + "] HEARTBEAT name=$Name pid=$pidValue elapsed=$([int]$sw.Elapsed.TotalSeconds)s")
        $nextHeartbeat = [DateTime]::UtcNow.AddSeconds([Math]::Max(1, $HeartbeatSeconds))
      }
    }
    if (-not $process.HasExited) {
      throw "$Name is still alive after the bounded stop path."
    }
    $process.WaitForExit()
    $stdoutTask.GetAwaiter().GetResult()
    $stderrTask.GetAwaiter().GetResult()
    $sw.Stop()
    $exitCode = $process.ExitCode
    $normalHonored = if ($null -eq $stopResult) { $true } else { [bool]$stopResult.normalCloseHonored }
    $forceStopped = if ($null -eq $stopResult) { $false } else { [bool]$stopResult.forceStopped }
    $line = "nativeExit=$exitCode`nelapsedSeconds=$([Math]::Round($sw.Elapsed.TotalSeconds,3))`npid=$pidValue`nstartTicks=$startTicks`ntimedOut=$($timedOut.ToString().ToLowerInvariant())`ncancelled=$($cancelled.ToString().ToLowerInvariant())`nnormalCloseHonored=$($normalHonored.ToString().ToLowerInvariant())`nforceStopped=$($forceStopped.ToString().ToLowerInvariant())`n"
    Write-C04Utf8 $exitPath $line
    return [pscustomobject]@{
      name = $Name
      pid = $pidValue
      startTicks = $startTicks
      fileName = $FileName
      arguments = $Arguments
      workingDirectory = $psi.WorkingDirectory
      exitCode = $exitCode
      timedOut = $timedOut
      cancelled = $cancelled
      normalCloseHonored = $normalHonored
      forceStopped = $forceStopped
      aliveAfter = (-not $process.HasExited)
      elapsedSeconds = [Math]::Round($sw.Elapsed.TotalSeconds, 3)
      stdoutPath = $stdoutPath
      stderrPath = $stderrPath
      exitPath = $exitPath
    }
  }
  finally {
    if (-not $process.HasExited) {
      try { $stopResult = Stop-C04OwnedProcess -Process $process -OwnedPid $pidValue -StartTicks $startTicks -GraceSeconds $CloseGraceSeconds } catch { }
    }
    try { if ($null -ne $stdoutTask -and $stdoutTask.IsCompleted) { $null = $stdoutTask.GetAwaiter().GetResult() } } catch { }
    try { if ($null -ne $stderrTask -and $stderrTask.IsCompleted) { $null = $stderrTask.GetAwaiter().GetResult() } } catch { }
    $stdoutStream.Dispose()
    $stderrStream.Dispose()
    $process.Dispose()
  }
}

function Assert-C04NativeSuccess($Result, [string]$Label) {
  if ($null -eq $Result) { throw "$Label produced no process result." }
  if ($Result.timedOut) { throw "$Label timed out after $($Result.elapsedSeconds)s." }
  if ($Result.cancelled) { throw "$Label was cancelled before normal completion." }
  if ($Result.aliveAfter) { throw "$Label left a live owned writer; result is UNKNOWN." }
  if ([int]$Result.exitCode -ne 0) { throw "$Label failed with native exit $($Result.exitCode)." }
}

function Acquire-C04Mutex {
  param(
    [Parameter(Mandatory = $true)][string]$Name,
    [Parameter(Mandatory = $true)][int]$TimeoutSeconds,
    [int]$HeartbeatSeconds = 30,
    [string]$CancelFile = ''
  )
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while ($true) {
    if (-not [string]::IsNullOrWhiteSpace($CancelFile) -and (Test-Path -LiteralPath $CancelFile)) {
      throw 'Cancelled while waiting for the Unity validation mutex.'
    }
    $created = $false
    $mutex = New-Object Threading.Mutex($false, $Name, [ref]$created)
    $held = $false
    try { $held = $mutex.WaitOne(1000) } catch [Threading.AbandonedMutexException] { $held = $true }
    if ($held) {
      return [pscustomobject]@{ mutex = $mutex; name = $Name; waitedSeconds = [int]$sw.Elapsed.TotalSeconds; acquired = $true }
    }
    $mutex.Dispose()
    if ([int]$sw.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
      throw "Unity mutex wait exceeded the configured bound of $TimeoutSeconds seconds."
    }
    if ([int]$sw.Elapsed.TotalSeconds -gt 0 -and ([int]$sw.Elapsed.TotalSeconds % [Math]::Max(1, $HeartbeatSeconds)) -eq 0) {
      Write-Host ("[" + [DateTimeOffset]::UtcNow.ToString('o') + "] HEARTBEAT mutex_wait=$([int]$sw.Elapsed.TotalSeconds)s")
    }
    Start-Sleep -Milliseconds 250
  }
}

function Release-C04Mutex($Handle) {
  if ($null -eq $Handle -or $null -eq $Handle.mutex) { return }
  try { $Handle.mutex.ReleaseMutex() } catch { }
  $Handle.mutex.Dispose()
}

function Write-C04SealedEvidence {
  param(
    [Parameter(Mandatory = $true)][string]$RunRoot,
    [Parameter(Mandatory = $true)]$Summary
  )
  $root = [IO.Path]::GetFullPath($RunRoot)
  $summaryPath = Join-Path $root 'summary.json'
  if (Test-Path -LiteralPath $summaryPath) { throw 'Evidence is already sealed; refusing to overwrite summary.json.' }
  Write-C04Json $summaryPath $Summary
  $manifest = Get-C04FileManifest -Root $root -ExcludeDirectoryNames @('.git', 'Library', 'Temp', 'obj', 'bin', 'Logs', 'UserSettings') -ExcludeFileNames @('manifest.sha256', 'seal.json')
  $manifestPath = Join-Path $root 'manifest.sha256'
  Write-C04Utf8 $manifestPath $manifest.canonical
  $manifestSha = Get-C04Sha256File $manifestPath
  $seal = [ordered]@{
    schemaVersion = 1
    result = [string]$Summary.status
    runRoot = $root
    manifest = $manifestPath
    manifestSha256 = $manifestSha
    fileCount = $manifest.count
    bytes = $manifest.bytes
    sealedUtc = [DateTimeOffset]::UtcNow.ToString('o')
  }
  Write-C04Json (Join-Path $root 'seal.json') $seal
  return [pscustomobject]@{ manifest = $manifest; manifestSha256 = $manifestSha; seal = $seal }
}

# ---------------------------------------------------------------------------
# Main runner. Dot-sourcing with -LoadFunctionsOnly stops before this block.
# ---------------------------------------------------------------------------
if ($LoadFunctionsOnly) { return }

$script:LogPath = ''
$script:OwnedProcesses = New-Object Collections.Generic.List[object]
$script:MutexHandle = $null
$script:StageDrive = $null
$script:OldEnvironment = @{}

function Log([string]$Text) {
  $line = '[' + [DateTimeOffset]::UtcNow.ToString('o') + '] ' + $Text
  Write-Host $line
  if (-not [string]::IsNullOrWhiteSpace($script:LogPath)) {
    [IO.File]::AppendAllText($script:LogPath, $line + "`n", (New-Object Text.UTF8Encoding($false)))
  }
}

function Start-C04Owned([string]$Name, [string]$Exe, [string[]]$Arguments, [string]$Cwd, [int]$Timeout) {
  $result = Invoke-C04OwnedProcess -Name $Name -FileName $Exe -Arguments $Arguments -WorkingDirectory $Cwd -TimeoutSeconds $Timeout -CancelFile $MutexCancelFile -CloseGraceSeconds $ProcessCloseGraceSeconds -EvidenceDirectory $script:RunRoot -HeartbeatSeconds $MutexHeartbeatSeconds
  $script:OwnedProcesses.Add($result)
  Log "PROCESS $Name pid=$($result.pid) exit=$($result.exitCode) elapsed=$($result.elapsedSeconds)s timedOut=$($result.timedOut) cancelled=$($result.cancelled) forceStopped=$($result.forceStopped)"
  return $result
}

function Invoke-C04RunnerMain {
  if ($BuildPlayer -and $WindowsPlayerTests) { throw 'Choose either the standalone Player build or Windows Player test run.' }
  if ([string]::IsNullOrWhiteSpace($RunRoot)) { throw 'RunRoot is required.' }
  $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
  $script:RunRoot = [IO.Path]::GetFullPath($RunRoot)
  $allowed = [IO.Path]::GetFullPath((Join-Path $repo 'build')) + [IO.Path]::DirectorySeparatorChar
  if (-not $script:RunRoot.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'RunRoot must be a fresh child of repository build/.' }
  if ((Split-Path -Leaf $script:RunRoot) -notmatch '^[0-9a-fA-F]{32}$') { throw 'RunRoot must end in a GUID-N run id.' }
  if (Test-Path -LiteralPath $script:RunRoot) { throw 'RunRoot exists; refusing to overwrite evidence.' }
  if (-not (Test-Path -LiteralPath $UnityPath -PathType Leaf) -or -not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) {
    throw 'Pinned Unity or dotnet executable is missing.'
  }

  New-Item -ItemType Directory -Path $script:RunRoot | Out-Null
  $script:LogPath = Join-Path $script:RunRoot 'runner.log'
  $temp = Join-Path $script:RunRoot 'temp'
  $upm = Join-Path $script:RunRoot 'upm-cache'
  $nuget = Join-Path $script:RunRoot 'nuget'
  $cli = Join-Path $script:RunRoot 'dotnet-home'
  foreach ($path in @($temp, $upm, (Join-Path $upm 'npm'), (Join-Path $upm 'packages'), $nuget, $cli)) {
    [IO.Directory]::CreateDirectory($path) | Out-Null
  }
  $script:OldEnvironment = @{
    TEMP = $env:TEMP; TMP = $env:TMP; NUGET_PACKAGES = $env:NUGET_PACKAGES; DOTNET_CLI_HOME = $env:DOTNET_CLI_HOME
    UPM_CACHE_ROOT = $env:UPM_CACHE_ROOT; UPM_NPM_CACHE_PATH = $env:UPM_NPM_CACHE_PATH; UPM_CACHE_PATH = $env:UPM_CACHE_PATH
    MAHJONG_C04_PRIVACY_EVIDENCE = $env:MAHJONG_C04_PRIVACY_EVIDENCE
  }
  $env:TEMP = $temp; $env:TMP = $temp; $env:NUGET_PACKAGES = $nuget; $env:DOTNET_CLI_HOME = $cli
  $env:UPM_CACHE_ROOT = $upm; $env:UPM_NPM_CACHE_PATH = Join-Path $upm 'npm'; $env:UPM_CACHE_PATH = Join-Path $upm 'packages'
  $env:DOTNET_NOLOGO = '1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

  $summary = [ordered]@{
    schemaVersion = 1
    runner = 'run-p45-c04-privacy-matrix.ps1'
    status = 'FAIL'
    mode = $(if ($BuildPlayer) { 'player-build' } elseif ($WindowsPlayerTests) { 'windows-player-tests' } else { 'playmode' })
    startedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    finishedUtc = $null
    runRoot = $script:RunRoot
    repo = $repo
    unity = [ordered]@{ path = $UnityPath; version = (Get-Item -LiteralPath $UnityPath).VersionInfo.FileVersion }
    dotnet = [ordered]@{ path = $DotnetPath; version = (Get-Item -LiteralPath $DotnetPath).VersionInfo.FileVersion; sdk = (& $DotnetPath --version).Trim() }
    source = $null
    inputClosure = $null
    stagedInput = $null
    processes = @()
    mutex = $null
    testXml = $null
    marker = $null
    error = $null
    firstFailure = $null
    unknown = $false
    preflightOnly = [bool]$PreflightOnly
  }
  $failure = $null
  try {
    Log "PRECHECK repo=$repo runRoot=$script:RunRoot dotnet=$DotnetPath unity=$UnityPath"
    $identity = Get-C04SourceIdentity -Repo $repo -ExpectedHead $ExpectedSourceHead
    $summary.source = $identity
    Log "SOURCE head=$($identity.head) clean=true"

    $sourceManifest = Get-C04SelectedManifest -Base $repo -RelativeRoots @('dotnet', 'tools/unity')
    $summary.inputClosure = [ordered]@{ count = $sourceManifest.count; bytes = $sourceManifest.bytes; sha256 = $sourceManifest.sha256 }
    Log "INPUT_CLOSURE count=$($sourceManifest.count) bytes=$($sourceManifest.bytes) sha256=$($sourceManifest.sha256)"

    if ($PreflightOnly) {
      $summary.status = 'PASS'
      Log 'PREFLIGHT_ONLY complete'
      return
    }

    $staging = Join-Path $script:RunRoot 'staging-project'
    $copied = Copy-C04UnityInputs -Repo $repo -Destination $staging
    $summary.stagedInput = [ordered]@{ count = $copied.staged.count; bytes = $copied.staged.bytes; sha256 = $copied.staged.sha256 }
    Log "STAGED_INPUT count=$($copied.staged.count) sha256=$($copied.staged.sha256)"
    $occupied = @(Get-PSDrive -PSProvider FileSystem | ForEach-Object { $_.Name.ToUpperInvariant() })
    foreach ($letter in @('R', 'S', 'T', 'U', 'V', 'W', 'X', 'Y', 'Z')) {
      if ($occupied -notcontains $letter) {
        $drive = "${letter}:"
        & subst.exe $drive $script:RunRoot
        if ($LASTEXITCODE -ne 0) { throw "Could not map the isolated run root to a short Unity staging drive (subst exit $LASTEXITCODE)." }
        $script:StageDrive = $drive
        break
      }
    }
    if (-not $script:StageDrive) { throw 'No unused drive letter is available for the short Unity staging path.' }
    $unityProject = Join-Path ($script:StageDrive + '\') 'staging-project'
    if (-not (Test-Path -LiteralPath (Join-Path $staging 'Assets/Scripts/App/AppComposition.cs') -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $staging 'Assets/Tests/PlayMode/C04PrivacyRoleSwitchPlayModeTests.cs') -PathType Leaf)) {
      throw 'Staged Unity project layout lacks AppComposition or the C04 PlayMode test under Assets/.'
    }

    $pluginDir = Join-Path $unityProject 'Assets/Plugins/Mahjong'
    [IO.Directory]::CreateDirectory($pluginDir) | Out-Null
    $wrapperDir = Join-Path $staging 'tools/unity'
    [IO.Directory]::CreateDirectory($wrapperDir) | Out-Null
    $checker = Join-Path $repo 'tools/unity/check-shared-plugins.ps1'
    $manifestPath = Join-Path $pluginDir 'shared-plugin-provenance.json'
    $wrapper = @"
param([string]`$ProjectRoot,[switch]`$Quiet)
`$ErrorActionPreference='Stop'
& '$checker' -ProjectRoot '$repo' -PluginDir '$pluginDir' -EvidencePath '$manifestPath' -Configuration Release -Quiet:`$Quiet
exit `$LASTEXITCODE
"@
    Write-C04Utf8 (Join-Path $wrapperDir 'check-shared-plugins.ps1') $wrapper
    $verifierProject = Join-Path $repo 'dotnet/tools/C04PrivacyMatrixVerifier/C04PrivacyMatrixVerifier.csproj'
    $verifierResult = Start-C04Owned 'privacy-verifier-build' $DotnetPath @('build', $verifierProject, '-c', 'Release', '--nologo', '-nr:false') $repo 300
    Assert-C04NativeSuccess $verifierResult 'privacy-verifier-build'
    $verifierDll = Join-Path $repo 'dotnet/tools/C04PrivacyMatrixVerifier/bin/Release/net8.0/C04PrivacyMatrixVerifier.dll'
    $selfResult = Start-C04Owned 'privacy-verifier-selftest' $DotnetPath @($verifierDll, 'self-test', 'unused.json') $repo 60
    Assert-C04NativeSuccess $selfResult 'privacy-verifier-selftest'

    $summary.mutex = [ordered]@{ name = $script:C04MutexName; acquired = $false; waitedSeconds = $null }
    $script:MutexHandle = Acquire-C04Mutex -Name $script:C04MutexName -TimeoutSeconds $MutexTimeoutSeconds -HeartbeatSeconds $MutexHeartbeatSeconds -CancelFile $MutexCancelFile
    $summary.mutex.acquired = $true
    $summary.mutex.waitedSeconds = $script:MutexHandle.waitedSeconds
    Log "MUTEX_ACQUIRED name=$($script:C04MutexName) waited=$($script:MutexHandle.waitedSeconds)s"

    $sharedBuilder = Join-Path $repo 'tools/unity/build-shared.ps1'
    $sharedResult = Start-C04Owned 'shared-plugin-build-and-provenance' 'powershell.exe' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $sharedBuilder, '-Configuration', 'Release', '-DotnetPath', $DotnetPath, '-PluginOutputDir', $pluginDir) $repo 1200
    Assert-C04NativeSuccess $sharedResult 'shared-plugin-build-and-provenance'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Fresh shared plugin provenance manifest is missing.' }
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $script:RunRoot 'shared-plugin-provenance.json') -Force

    $unityLog = Join-Path $script:RunRoot 'unity-editor.log'
    $testXml = Join-Path $script:RunRoot 'playmode-results.xml'
    $privacyEvidence = Join-Path $script:RunRoot 'privacy-evidence'
    $marker = Join-Path $script:RunRoot 'player-build.json'
    $player = Join-Path $script:RunRoot 'player/Mahjong.exe'
    $buildLog = Join-Path $script:RunRoot 'player-build.log'
    [IO.Directory]::CreateDirectory((Split-Path -Parent $player)) | Out-Null
    if ($BuildPlayer) {
      $unityArgs = @('-batchmode', '-quit', '-projectPath', $unityProject, '-executeMethod', 'C04PrivacyBuild.Build', '-logFile', $unityLog,
        '-c04PrivacyOutput', $player, '-c04PrivacyMarker', $marker, '-c04PrivacyBuildLog', $buildLog,
        '-c04PrivacySourceHead', $identity.head, '-c04PrivacyBuildCommit', $identity.head, '-upmCachePath', $upm)
      $unityResult = Start-C04Owned 'unity-player-build' $UnityPath $unityArgs $unityProject $TimeoutSeconds
      Assert-C04NativeSuccess $unityResult 'unity-player-build'
      $summary.marker = Assert-C04BuildMarker -MarkerPath $marker -ExpectedSourceHead $identity.head -ExpectedBuildCommit $identity.head
    } else {
      if ($WindowsPlayerTests) { $testXml = Join-Path $script:RunRoot 'windows-player-results.xml' }
      if (-not [string]::IsNullOrWhiteSpace($PlayerBuildMarker)) {
        $summary.marker = Assert-C04BuildMarker -MarkerPath $PlayerBuildMarker -ExpectedSourceHead $identity.head -ExpectedBuildCommit $identity.head
        Log "PLAYER_MARKER packageFiles=$($summary.marker.packageFileCount) packageBytes=$($summary.marker.packageBytes)"
      }
      if ($WindowsPlayerTests) {
        $env:MAHJONG_C04_PRIVACY_EVIDENCE = $privacyEvidence
        $unityArgs = @('-batchmode', '-projectPath', $unityProject, '-runTests', '-testPlatform', 'StandaloneWindows64',
          '-testFilter', 'Mahjong.Unity.Tests.PlayMode.C04PrivacyRoleSwitchPlayModeTests',
          '-testResults', $testXml, '-logFile', $unityLog, '-upmCachePath', $upm)
      } else {
        $unityArgs = @('-batchmode', '-projectPath', $unityProject, '-runTests', '-testPlatform', 'PlayMode',
          '-testFilter', 'Mahjong.Unity.Tests.PlayMode.C04PrivacyRoleSwitchPlayModeTests',
          '-testResults', $testXml, '-logFile', $unityLog, '-c04PrivacyEvidence', $privacyEvidence, '-upmCachePath', $upm)
      }
      $stageName = if ($WindowsPlayerTests) { 'unity-windows-player-tests' } else { 'unity-playmode' }
      $unityResult = Start-C04Owned $stageName $UnityPath $unityArgs $unityProject $TimeoutSeconds
      Assert-C04NativeSuccess $unityResult $stageName
      $summary.testXml = Assert-C04UnityXml -XmlPath $testXml -ExpectedTests $script:C04ExpectedTests
      if ($WindowsPlayerTests -and -not (Test-Path -LiteralPath (Join-Path $privacyEvidence 'raw-frames.json'))) {
        throw 'Windows test Player did not seal real raw channel frames under its isolated evidence root.'
      }
      $rawFrames = Join-Path $privacyEvidence 'raw-frames.json'
      if (-not (Test-Path -LiteralPath $rawFrames -PathType Leaf)) { throw 'PlayMode did not seal raw frames.' }
      $raw = [IO.File]::ReadAllText($rawFrames, [Text.Encoding]::UTF8) | ConvertFrom-Json
      if ([int]$raw.schemaVersion -ne 1 -or @($raw.frames).Count -lt 1) { throw 'Raw capture schema is empty or unsupported.' }
      $rawSha = Get-C04Sha256File $rawFrames
      $summary.rawFrames = [ordered]@{ path = $rawFrames; count = @($raw.frames).Count; sha256 = $rawSha }
      $matrixTests = Join-Path $repo 'tools/tests/C04PrivacyMatrix.Tests.ps1'
      $verifierEvidence = Join-Path $script:RunRoot 'verifier-tests'
      $matrixResult = Start-C04Owned 'privacy-matrix-tests' 'powershell.exe' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $matrixTests, '-DotnetPath', $DotnetPath, '-VerifierDll', $verifierDll, '-RawFramesPath', $rawFrames, '-RunRoot', $verifierEvidence) $repo 240
      Assert-C04NativeSuccess $matrixResult 'privacy-matrix-tests'
    }

    $summary.status = 'PASS'
    Log 'RUNNER_STAGE_PASS'
  }
  catch {
    $failure = $_.Exception.ToString()
    $summary.error = $_.Exception.Message
    $summary.firstFailure = $failure
    $summary.status = 'FAIL'
    Write-C04Utf8 (Join-Path $script:RunRoot 'failure.txt') ($failure + "`n")
    Log "FAIL $($_.Exception.Message)"
  }
  finally {
    Log 'FINALLY_START'
    $allStopped = $true
    foreach ($process in $script:OwnedProcesses) {
      if ($process.aliveAfter) { $allStopped = $false }
    }
    if ($script:MutexHandle) {
      if ($allStopped) {
        Release-C04Mutex $script:MutexHandle
        Log 'MUTEX_RELEASED'
      } else {
        $summary.unknown = $true
        Log 'MUTEX_HELD_RESIDUAL_WRITER'
      }
      $script:MutexHandle = $null
    }
    if ($script:StageDrive) {
      & subst.exe $script:StageDrive /D
      Log "UNITY_STAGING_DRIVE_RELEASED drive=$($script:StageDrive)"
      $script:StageDrive = $null
    }
    Log 'FINALLY_ENV_RESTORE'
    foreach ($key in $script:OldEnvironment.Keys) {
      $value = $script:OldEnvironment[$key]
      try {
        if ($null -eq $value) {
          Remove-Item -Path ('Env:' + $key) -ErrorAction SilentlyContinue
        } else {
          [Environment]::SetEnvironmentVariable([string]$key, [string]$value)
        }
      } catch {
        Log "ENV_RESTORE_FAIL key=$key valueType=$(if ($null -eq $value) { 'null' } else { $value.GetType().FullName }) message=$($_.Exception.Message)"
        throw
      }
    }
    if (-not $allStopped) {
      $summary.status = 'UNKNOWN'
      $summary.unknown = $true
      Log 'RESIDUAL_OWNED_WRITER_UNKNOWN'
    }
    Log 'FINALLY_ASSIGN_SUMMARY'
    $summary.finishedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    Log 'FINALLY_ASSIGN_PROCESSES'
    $summary['processes'] = $script:OwnedProcesses.ToArray()
    Log 'FINALLY_SEAL'
    try {
      $sealed = Write-C04SealedEvidence -RunRoot $script:RunRoot -Summary $summary
      Log "SEALED manifest=$($sealed.manifestSha256) files=$($sealed.manifest.count)"
    } catch {
      Write-Host ("SEAL_FAILED " + $_.Exception.ToString())
    }
  }
  if ($null -ne $failure) { throw $failure }
}

Invoke-C04RunnerMain
