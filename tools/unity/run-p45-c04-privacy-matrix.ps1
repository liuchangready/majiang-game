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
  'Mahjong.Unity.Tests.PlayMode.C04PrivacyRoleSwitchPlayModeTests.RealRoom_ColdReopenReconcilesOriginalRequestWithoutDuplicate',
  'Mahjong.Unity.Tests.PlayMode.C04PrivacyRoleSwitchPlayModeTests.RealRoom_LegalSleeveAndFeeOwnershipAcrossFourSeatsAndSpectator',
  'Mahjong.Unity.Tests.PlayMode.C04PrivacyRoleSwitchPlayModeTests.RealRoom_ReleasedSeatAiAdvancesAfterPlayerCloseWithSpectatorOnly',
  'Mahjong.Unity.Tests.PlayMode.C04PrivacyRoleSwitchPlayModeTests.RealRoom_OpenDecisionDeadlineContinuesAfterPlayerClose',
  'Mahjong.Unity.Tests.PlayMode.C04PrivacyRoleSwitchPlayModeTests.RealRoom_ClosedPlayerToSpectatorToNewPlayerReceivePathRejectsOldRaw'
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

function Get-C04RequiredJsonProperty($Object, [string]$Name) {
  if ($null -eq $Object) { throw "Marker object is null; cannot read $Name." }
  $property = $Object.PSObject.Properties[$Name]
  if ($null -eq $property -or $null -eq $property.Value) { throw "Marker required field $Name is missing or null." }
  return $property.Value
}

function Get-C04RequiredMarkerString($Object, [string]$Name) {
  $value = [string](Get-C04RequiredJsonProperty $Object $Name)
  if ([string]::IsNullOrWhiteSpace($value)) { throw "Marker required string field $Name is empty." }
  return $value
}

function Get-C04RequiredMarkerLong($Object, [string]$Name) {
  $raw = Get-C04RequiredJsonProperty $Object $Name
  [long]$value = 0
  if (-not [long]::TryParse([string]$raw, [ref]$value)) { throw "Marker required integer field $Name is invalid: $raw" }
  return $value
}

function Get-C04RequiredMarkerInt($Object, [string]$Name) {
  $raw = Get-C04RequiredJsonProperty $Object $Name
  [int]$value = 0
  if (-not [int]::TryParse([string]$raw, [ref]$value)) { throw "Marker required integer field $Name is invalid: $raw" }
  return $value
}

function Assert-C04BuildMarker {
  param(
    [Parameter(Mandatory = $true)][string]$MarkerPath,
    [Parameter(Mandatory = $true)][string]$ExpectedSourceHead,
    [Parameter(Mandatory = $true)][string]$ExpectedBuildCommit
  )
  if (-not (Test-Path -LiteralPath $MarkerPath -PathType Leaf)) { throw "Player build marker is missing: $MarkerPath" }
  $marker = [IO.File]::ReadAllText($MarkerPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
  if ((Get-C04RequiredMarkerInt $marker 'schemaVersion') -ne 1) { throw 'Player marker schemaVersion is not 1.' }
  if ((Get-C04RequiredMarkerString $marker 'kind') -ne 'c04-privacy-player-build') { throw 'Player marker kind is not c04-privacy-player-build.' }
  $markerResult = Get-C04RequiredMarkerString $marker 'result'
  if ($markerResult -ne 'Succeeded') { throw "Player marker result=$markerResult, expected Succeeded." }
  if ((Get-C04RequiredMarkerLong $marker 'errors') -ne 0) { throw "Player marker errors=$((Get-C04RequiredMarkerLong $marker 'errors')), expected 0." }
  $markerTarget = Get-C04RequiredMarkerString $marker 'target'
  if ($markerTarget -ne 'StandaloneWindows64') { throw "Player marker target=$markerTarget, expected StandaloneWindows64." }
  $markerSource = Get-C04RequiredMarkerString $marker 'sourceHead'
  $markerCommit = Get-C04RequiredMarkerString $marker 'buildCommit'
  if (-not [string]::Equals($markerSource, $ExpectedSourceHead, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Player marker sourceHead=$markerSource, expected $ExpectedSourceHead."
  }
  if (-not [string]::Equals($markerCommit, $ExpectedBuildCommit, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Player marker buildCommit=$markerCommit, expected $ExpectedBuildCommit."
  }
  $errorProperty = $marker.PSObject.Properties['errorMessages']
  if ($null -eq $errorProperty -or $null -eq $errorProperty.Value) { throw 'Player marker required field errorMessages is missing or null.' }
  if (@($errorProperty.Value).Count -ne 0) { throw 'Player marker contains error messages.' }
  $markerOutput = Get-C04RequiredMarkerString $marker 'output'
  $output = [IO.Path]::GetFullPath($markerOutput)
  if (-not (Test-Path -LiteralPath $output -PathType Leaf)) { throw "Player marker output is missing: $output" }
  $actualPlayerBytes = (Get-Item -LiteralPath $output).Length
  $actualPlayerSha = Get-C04Sha256File $output
  $markerPlayerBytes = Get-C04RequiredMarkerLong $marker 'playerBytes'
  if ($markerPlayerBytes -ne $actualPlayerBytes) { throw "Player marker playerBytes=$markerPlayerBytes, actual=$actualPlayerBytes." }
  $markerPlayerSha = Get-C04RequiredMarkerString $marker 'playerSha256'
  if (-not [string]::Equals($markerPlayerSha, $actualPlayerSha, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Player marker playerSha256 does not match the actual executable.'
  }

  $sceneValue = Get-C04RequiredJsonProperty $marker 'scenes'
  $sceneNames = @($sceneValue)
  if ($sceneNames.Count -eq 0) { throw 'Player marker scenes is empty.' }
  foreach ($scene in $script:C04RequiredScenes) {
    if ($sceneNames -notcontains $scene) { throw "Player marker is missing required scene $scene." }
  }
  foreach ($scene in $sceneNames) {
    if ($script:C04RequiredScenes -notcontains $scene) { throw "Player marker contains unexpected scene $scene." }
  }

  $pluginRoot = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $output) 'Mahjong_Data/Managed'))
  $markerPlugins = Get-C04RequiredJsonProperty $marker 'plugins'
  foreach ($name in $script:C04RequiredPlugins) {
    $property = $markerPlugins.PSObject.Properties[$name]
    if ($null -eq $property -or $null -eq $property.Value -or [string]::IsNullOrWhiteSpace([string]$property.Value)) {
      throw "Player marker is missing plugin $name."
    }
    $candidate = Join-Path $pluginRoot $name
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { throw "Built player plugin is missing: $candidate" }
    $actual = Get-C04Sha256File $candidate
    if (-not [string]::Equals([string]$property.Value, $actual, [StringComparison]::OrdinalIgnoreCase)) {
      throw "Player marker plugin hash for $name does not match the built closure."
    }
  }

  $packageManifestPath = [IO.Path]::GetFullPath((Get-C04RequiredMarkerString $marker 'packageManifest'))
  $packageRoot = [IO.Path]::GetFullPath((Split-Path -Parent $output))
  $packageRootWithSeparator = $packageRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
  if (-not [string]::Equals([IO.Path]::GetDirectoryName($packageManifestPath), $packageRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Player package manifest is not inside the player package root.'
  }
  if (-not (Test-Path -LiteralPath $packageManifestPath -PathType Leaf)) { throw 'Player package manifest is missing.' }
  $manifestSha = Get-C04Sha256File $packageManifestPath
  $markerManifestSha = Get-C04RequiredMarkerString $marker 'packageManifestSha256'
  if (-not [string]::Equals($markerManifestSha, $manifestSha, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Player marker packageManifestSha256 does not match the manifest.'
  }
  $markerPackageCount = Get-C04RequiredMarkerLong $marker 'packageFileCount'
  $markerPackageBytes = Get-C04RequiredMarkerLong $marker 'packageBytes'
  $lines = @([IO.File]::ReadAllLines($packageManifestPath, [Text.Encoding]::UTF8) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
  if ($lines.Count -ne $markerPackageCount) { throw "Player package manifest count=$($lines.Count), marker=$markerPackageCount." }

  [long]$packageBytes = 0
  $manifestSet = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
  foreach ($line in $lines) {
    if ($line -notmatch '^([0-9a-fA-F]{64})  (.+)$') { throw "Malformed package manifest line: $line" }
    $expectedHash = $matches[1].ToLowerInvariant()
    $rawRelative = $matches[2]
    if ([IO.Path]::IsPathRooted($rawRelative)) { throw "Package manifest path is rooted: $rawRelative" }
    $relative = $rawRelative -replace '/', '\'
    if ($relative -match '(^|[\\/])\.\.([\\/]|$)') { throw "Package manifest path contains parent traversal: $rawRelative" }
    $normalizedRelative = $relative -replace '/', '\'
    if (-not $manifestSet.Add($normalizedRelative)) { throw "Duplicate package manifest entry: $rawRelative" }
    $full = [IO.Path]::GetFullPath((Join-Path $packageRoot $relative))
    if (-not $full.StartsWith($packageRootWithSeparator, [StringComparison]::OrdinalIgnoreCase)) {
      throw "Package manifest path escapes the output root: $rawRelative"
    }
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "Package manifest file is missing: $rawRelative" }
    $fileItem = Get-Item -LiteralPath $full
    if (($fileItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Package manifest file is a reparse point: $rawRelative" }
    $actual = Get-C04Sha256File $full
    if (-not [string]::Equals($expectedHash, $actual, [StringComparison]::OrdinalIgnoreCase)) { throw "Package file hash mismatch: $rawRelative" }
    $packageBytes += $fileItem.Length
  }
  if ($packageBytes -ne $markerPackageBytes) { throw "Player marker packageBytes=$markerPackageBytes, actual=$packageBytes." }

  $exeRelative = $output.Substring($packageRootWithSeparator.Length)
  if (-not $manifestSet.Contains($exeRelative)) { throw 'Player executable is not listed in the package manifest closure.' }
  foreach ($name in $script:C04RequiredPlugins) {
    $pluginRelative = 'Mahjong_Data\Managed\' + $name
    if (-not $manifestSet.Contains($pluginRelative)) { throw "Required player plugin $name is not listed in the package manifest closure." }
  }

  $actualSet = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
  [long]$actualPackageBytes = 0
  foreach ($file in (Get-ChildItem -LiteralPath $packageRoot -Recurse -File -Force)) {
    $fileFull = [IO.Path]::GetFullPath($file.FullName)
    if ([string]::Equals($fileFull, $packageManifestPath, [StringComparison]::OrdinalIgnoreCase)) { continue }
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Player package contains a reparse point: $($file.FullName)" }
    if (-not $fileFull.StartsWith($packageRootWithSeparator, [StringComparison]::OrdinalIgnoreCase)) {
      throw "Player package file escapes the output root: $($file.FullName)"
    }
    $relative = $fileFull.Substring($packageRootWithSeparator.Length)
    if (-not $manifestSet.Contains($relative)) { throw "Player package contains an unlisted file: $relative" }
    if (-not $actualSet.Add($relative)) { throw "Player package contains a duplicate path: $relative" }
    $actualPackageBytes += $file.Length
  }
  foreach ($relative in $manifestSet) {
    if (-not $actualSet.Contains($relative)) { throw "Player package manifest lists a missing file: $relative" }
  }
  if ($actualSet.Count -ne $manifestSet.Count) { throw 'Player package actual file set does not equal the manifest closure.' }
  if ($actualPackageBytes -ne $packageBytes) { throw "Player package actual bytes=$actualPackageBytes, manifest=$packageBytes." }

  return [pscustomobject]@{
    markerPath = [IO.Path]::GetFullPath($MarkerPath)
    output = $output
    playerBytes = $actualPlayerBytes
    playerSha256 = $actualPlayerSha
    packageFileCount = $lines.Count
    packageBytes = $packageBytes
    packageManifest = $packageManifestPath
    packageManifestSha256 = $manifestSha
    sourceHead = $markerSource
    buildCommit = $markerCommit
  }
}

function Get-C04ValidatedRawScenes([string]$PrivacyEvidence) {
  $required = @(
    [pscustomobject]@{ scene = 'role-switch'; path = (Join-Path $PrivacyEvidence 'role-switch-raw/raw-frames.json') },
    [pscustomobject]@{ scene = 'four-owner'; path = (Join-Path $PrivacyEvidence 'four-owner-raw/raw-frames.json') },
    [pscustomobject]@{ scene = 'closed-binding'; path = (Join-Path $PrivacyEvidence 'closed-binding-raw/raw-frames.json') },
    [pscustomobject]@{ scene = 'recovery'; path = (Join-Path $PrivacyEvidence 'recovery-raw/raw-frames.json') }
  )
  $results = New-Object Collections.Generic.List[object]
  foreach ($scene in $required) {
    if (-not (Test-Path -LiteralPath $scene.path -PathType Leaf)) {
      throw "Required raw scene '$($scene.scene)' did not seal $($scene.path)."
    }
    $raw = [IO.File]::ReadAllText($scene.path, [Text.Encoding]::UTF8) | ConvertFrom-Json
    if ([int]$raw.schemaVersion -ne 2 -or @($raw.frames).Count -lt 1) {
      throw "Raw scene '$($scene.scene)' schema is empty or unsupported."
    }
    $results.Add([pscustomobject]@{
      scene = $scene.scene
      path = $scene.path
      count = @($raw.frames).Count
      sha256 = Get-C04Sha256File $scene.path
    })
  }
  return $results.ToArray()
}

function Get-C04TestPlayerPackage {
  param(
    [Parameter(Mandatory = $true)][string]$StagingProject,
    [Parameter(Mandatory = $true)][string]$RunRoot,
    [Parameter(Mandatory = $true)][string]$SourceHead
  )
  $tempRoot = Join-Path $StagingProject 'Temp'
  if (-not (Test-Path -LiteralPath $tempRoot -PathType Container)) { throw 'Unity Test Player Temp directory is missing.' }
  $candidate = Get-ChildItem -LiteralPath $tempRoot -Recurse -Directory -Filter 'PlayerWithTests' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
  if ($null -eq $candidate) { throw 'Unity did not leave a PlayerWithTests package under the isolated staging Temp.' }
  $exe = Get-ChildItem -LiteralPath $candidate.FullName -Recurse -File -Filter '*.exe' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
  if ($null -eq $exe) { throw 'PlayerWithTests package has no executable.' }
  $packageRoot = [IO.Path]::GetFullPath($exe.DirectoryName)
  $packageRootWithSeparator = $packageRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
  $manifestPath = Join-Path $RunRoot 'test-player-package.sha256'
  if (Test-Path -LiteralPath $manifestPath) { throw 'Test Player package manifest already exists.' }
  $files = @(Get-ChildItem -LiteralPath $packageRoot -Recurse -File -Force | Sort-Object FullName)
  $lines = New-Object Collections.Generic.List[string]
  [long]$bytes = 0
  foreach ($file in $files) {
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Test Player package contains a reparse point: $($file.FullName)" }
    if (-not $file.FullName.StartsWith($packageRootWithSeparator, [StringComparison]::OrdinalIgnoreCase)) {
      throw "Test Player package file escapes the package root: $($file.FullName)"
    }
    $relative = $file.FullName.Substring($packageRootWithSeparator.Length) -replace '\\', '/'
    $lines.Add((Get-C04Sha256File $file.FullName) + '  ' + $relative)
    $bytes += $file.Length
  }
  Write-C04Utf8 $manifestPath (($lines -join "`n") + "`n")
  $manifestSha = Get-C04Sha256File $manifestPath
  $managed = Join-Path $packageRoot (([IO.Path]::GetFileNameWithoutExtension($exe.Name)) + '_Data/Managed')
  foreach ($name in $script:C04RequiredPlugins) {
    if (-not (Test-Path -LiteralPath (Join-Path $managed $name) -PathType Leaf)) {
      throw "Test Player package is missing required managed plugin $name."
    }
  }
  return [pscustomobject]@{
    kind = 'WINDOWS_TEST_PLAYER_PACKAGE'
    sourceHead = $SourceHead
    executable = $exe.FullName
    executableBytes = $exe.Length
    executableSha256 = Get-C04Sha256File $exe.FullName
    packageRoot = $packageRoot
    packageFileCount = $files.Count
    packageBytes = $bytes
    packageManifest = $manifestPath
    packageManifestSha256 = $manifestSha
    samePackageForP1P2 = $true
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

function Wait-C04TaskBounded($Task, [int]$Milliseconds, [string]$Label) {
  if ($null -eq $Task) { throw "$Label task is null." }
  if ($Task.IsCompleted) {
    $null = $Task.GetAwaiter().GetResult()
    return
  }
  $delay = [Threading.Tasks.Task]::Delay([Math]::Max(1, $Milliseconds))
  $completed = [Threading.Tasks.Task]::WhenAny($Task, $delay).GetAwaiter().GetResult()
  if (-not [object]::ReferenceEquals($completed, $Task)) { throw "$Label did not complete within $Milliseconds ms." }
  $null = $Task.GetAwaiter().GetResult()
}

function Get-C04OwnedDescendantProcessIds([int]$ParentId, [long]$ParentStartTicks) {
  $found = New-Object Collections.Generic.List[int]
  $queue = New-Object Collections.Generic.Queue[int]
  $queue.Enqueue($ParentId)
  $visited = New-Object 'System.Collections.Generic.HashSet[int]'
  $null = $visited.Add($ParentId)
  while ($queue.Count -gt 0) {
    $current = $queue.Dequeue()
    $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$current" -ErrorAction SilentlyContinue)
    foreach ($child in $children) {
      if ($null -eq $child.ProcessId -or $visited.Contains([int]$child.ProcessId)) { continue }
      try {
        $created = ([datetime]$child.CreationDate).ToUniversalTime().Ticks
      } catch { continue }
      if ($created -lt ($ParentStartTicks - [TimeSpan]::FromSeconds(2).Ticks)) { continue }
      $childId = [int]$child.ProcessId
      $null = $visited.Add($childId)
      $found.Add($childId)
      $queue.Enqueue($childId)
    }
  }
  return @($found)
}

function Stop-C04OwnedDescendants([int]$ParentId, [long]$ParentStartTicks, [int]$GraceSeconds) {
  $stopped = New-Object Collections.Generic.List[object]
  $alreadyExited = New-Object Collections.Generic.List[int]
  $failures = New-Object Collections.Generic.List[string]
  foreach ($childId in (Get-C04OwnedDescendantProcessIds -ParentId $ParentId -ParentStartTicks $ParentStartTicks)) {
    try {
      $child = Get-Process -Id $childId -ErrorAction SilentlyContinue
      if ($null -eq $child) { $alreadyExited.Add($childId); continue }
      $childTicks = $child.StartTime.ToUniversalTime().Ticks
      if ($childTicks -lt ($ParentStartTicks - [TimeSpan]::FromSeconds(2).Ticks)) { throw "descendant $childId start time predates the owned parent" }
      $result = Stop-C04OwnedProcess -Process $child -OwnedPid $childId -StartTicks $childTicks -GraceSeconds $GraceSeconds
      $stopped.Add([pscustomobject]@{ pid = $childId; normalCloseHonored = $result.normalCloseHonored; forceStopped = $result.forceStopped })
    } catch {
      $failures.Add("descendant ${childId}: $($_.Exception.Message)")
    }
  }
  return [pscustomobject]@{ stopped = $stopped.ToArray(); alreadyExited = $alreadyExited.ToArray(); failures = $failures.ToArray() }
}

function Stop-C04OwnedRecord($Record, [int]$GraceSeconds) {
  if ($null -eq $Record) { return }
  if (-not $Record.aliveAfter) { return }
  if (-not [string]::IsNullOrWhiteSpace([string]$Record.cleanupError)) {
    throw "Owned writer $($Record.pid) has a cleanup error; refusing to clear its alive state."
  }
  if ($Record.drainUnresolved) {
    $captured = @($Record.capturedDescendants)
    if ($captured.Count -eq 0) {
      $Record.cleanupError = 'unresolved output with no captured descendant identity'
      return
    }
    $stopResult = Stop-C04CapturedDescendants -Snapshots $captured -GraceSeconds $GraceSeconds
    if (@($stopResult.failures).Count -gt 0) {
      $Record.cleanupError = 'captured descendants could not be stopped: ' + ((@($stopResult.failures)) -join '; ')
      return
    }
    $Record.descendantsStopped = [int]$Record.descendantsStopped + @($stopResult.stopped).Count
    foreach ($snapshot in $captured) {
      $live = Get-Process -Id ([int]$snapshot.pid) -ErrorAction SilentlyContinue
      if ($null -ne $live -and $live.StartTime.ToUniversalTime().Ticks -eq [long]$snapshot.startTicks) {
        $Record.cleanupError = "captured descendant $($snapshot.pid) is still alive"
        return
      }
    }
    $Record.drainUnresolved = $false
  }
  foreach ($snapshot in @($Record.capturedDescendants)) {
    if ($null -eq $snapshot) { continue }
    $live = Get-Process -Id ([int]$snapshot.pid) -ErrorAction SilentlyContinue
    if ($null -ne $live -and $live.StartTime.ToUniversalTime().Ticks -eq [long]$snapshot.startTicks) {
      throw "Captured owned descendant $($snapshot.pid) is still alive; refusing to clear parent state."
    }
  }
  try {
    $process = Get-Process -Id ([int]$Record.pid) -ErrorAction SilentlyContinue
    if ($null -eq $process) {
      $Record.aliveAfter = $false
      return
    }
    $ticks = $process.StartTime.ToUniversalTime().Ticks
    if ($ticks -ne [long]$Record.startTicks) { throw "PID $($Record.pid) start identity changed" }
    $null = Stop-C04OwnedProcess -Process $process -OwnedPid ([int]$Record.pid) -StartTicks ([long]$Record.startTicks) -GraceSeconds $GraceSeconds
    $Record.aliveAfter = $false
  } catch {
    throw
  }
}

$script:C04JobSupportInitialized = $false
function Initialize-C04JobSupport {
  if ($script:C04JobSupportInitialized) { return }
  if (-not ('C04OwnedJob' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
public sealed class C04OwnedJob : IDisposable {
  [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_BASIC_LIMIT_INFORMATION {
    public long PerProcessUserTimeLimit;
    public long PerJobUserTimeLimit;
    public uint LimitFlags;
    public UIntPtr MinimumWorkingSetSize;
    public UIntPtr MaximumWorkingSetSize;
    public uint ActiveProcessLimit;
    public UIntPtr Affinity;
    public uint PriorityClass;
    public uint SchedulingClass;
  }
  [StructLayout(LayoutKind.Sequential)] private struct IO_COUNTERS {
    public ulong ReadOperationCount;
    public ulong WriteOperationCount;
    public ulong OtherOperationCount;
    public ulong ReadTransferCount;
    public ulong WriteTransferCount;
    public ulong OtherTransferCount;
  }
  [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION {
    public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
    public IO_COUNTERS IoInfo;
    public UIntPtr ProcessMemoryLimit;
    public UIntPtr JobMemoryLimit;
    public UIntPtr PeakProcessMemoryUsed;
    public UIntPtr PeakJobMemoryUsed;
  }
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
  private static extern IntPtr CreateJobObject(IntPtr security, string name);
  [DllImport("kernel32.dll")]
  private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
  [DllImport("kernel32.dll")]
  private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
  [DllImport("kernel32.dll")]
  private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
  [DllImport("kernel32.dll")]
  private static extern bool CloseHandle(IntPtr handle);
  private IntPtr _handle;
  private C04OwnedJob(IntPtr handle) { _handle = handle; }
  public static C04OwnedJob TryCreate(Process process) {
    IntPtr handle = CreateJobObject(IntPtr.Zero, null);
    if (handle == IntPtr.Zero) return null;
    var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
    info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
    int size = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
    IntPtr buffer = Marshal.AllocHGlobal(size);
    try {
      Marshal.StructureToPtr(info, buffer, false);
      if (!SetInformationJobObject(handle, 9, buffer, (uint)size)) { CloseHandle(handle); return null; }
    } finally { Marshal.FreeHGlobal(buffer); }
    try {
      if (!AssignProcessToJobObject(handle, process.Handle)) { CloseHandle(handle); return null; }
    } catch { CloseHandle(handle); return null; }
    return new C04OwnedJob(handle);
  }
  public void Terminate() { if (_handle != IntPtr.Zero) TerminateJobObject(_handle, 1); }
  public void Dispose() {
    if (_handle != IntPtr.Zero) { CloseHandle(_handle); _handle = IntPtr.Zero; }
  }
}
'@
  }
  $script:C04JobSupportInitialized = $true
}

function Get-C04CapturedDescendants([int]$ParentId, [long]$ParentStartTicks, [int]$Rounds = 16, [int]$DelayMs = 25) {
  $snapshots = New-Object Collections.Generic.List[object]
  $seen = New-Object 'System.Collections.Generic.HashSet[int]'
  for ($round = 0; $round -lt $Rounds; $round++) {
    $candidates = New-Object Collections.Generic.List[int]
    foreach ($childId in (Get-C04OwnedDescendantProcessIds -ParentId $ParentId -ParentStartTicks $ParentStartTicks)) {
      $candidates.Add([int]$childId)
    }
    foreach ($childId in $candidates) {
      if (-not $seen.Add($childId)) { continue }
      $child = Get-Process -Id $childId -ErrorAction SilentlyContinue
      if ($null -eq $child) { continue }
      try {
        $ticks = $child.StartTime.ToUniversalTime().Ticks
        if ($ticks -ge ($ParentStartTicks - [TimeSpan]::FromSeconds(2).Ticks)) {
          $snapshots.Add([pscustomobject]@{ pid = $childId; startTicks = $ticks })
        }
      } catch {
      }
    }
    Start-Sleep -Milliseconds $DelayMs
  }
  return $snapshots.ToArray()
}

function Stop-C04CapturedDescendants($Snapshots, [int]$GraceSeconds) {
  $stopped = New-Object Collections.Generic.List[object]
  $failures = New-Object Collections.Generic.List[string]
  foreach ($snapshot in @($Snapshots)) {
    if ($null -eq $snapshot -or [int]$snapshot.pid -eq $PID) { continue }
    try {
      $child = Get-Process -Id ([int]$snapshot.pid) -ErrorAction SilentlyContinue
      if ($null -eq $child) { continue }
      $ticks = $child.StartTime.ToUniversalTime().Ticks
      if ($ticks -ne [long]$snapshot.startTicks) { throw "captured descendant $($snapshot.pid) start identity changed" }
      $result = Stop-C04OwnedProcess -Process $child -OwnedPid ([int]$snapshot.pid) -StartTicks ([long]$snapshot.startTicks) -GraceSeconds $GraceSeconds
      $stopped.Add([pscustomobject]@{ pid = [int]$snapshot.pid; normalCloseHonored = $result.normalCloseHonored; forceStopped = $result.forceStopped })
    } catch {
      $failures.Add("captured descendant $($snapshot.pid): $($_.Exception.Message)")
    }
  }
  return [pscustomobject]@{ stopped = $stopped.ToArray(); failures = $failures.ToArray() }
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
    [int]$HeartbeatSeconds = 30,
    [System.Collections.IList]$OwnedRegistry = $null,
    [switch]$InjectPostStartFailure
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
  $job = $null
  try {
    Initialize-C04JobSupport
    $job = [C04OwnedJob]::TryCreate($process)
  } catch {
    $job = $null
  }
  $capturedDescendants = @()
  $state = [pscustomobject]@{
    name = $Name
    pid = $pidValue
    startTicks = $startTicks
    aliveAfter = $true
    phase = 'started'
    exitCode = $null
    timedOut = $false
    cancelled = $false
    forceStopped = $false
    drainTimedOut = $false
    drainUnresolved = $false
    elapsedSeconds = $null
    descendantsStopped = 0
    cleanupFailures = @()
    jobAssigned = ($null -ne $job)
    capturedDescendants = $capturedDescendants
    stdoutPath = $stdoutPath
    stderrPath = $stderrPath
    cleanupError = $null
  }
  if ($null -ne $OwnedRegistry) { $OwnedRegistry.Add($state) }

  $result = $null
  $stdoutStream = $null
  $stderrStream = $null
  $stdoutTask = $null
  $stderrTask = $null
  $timedOut = $false
  $drainTimedOut = $false
  $cancelled = $false
  $stopResult = $null
  $cleanupFailures = New-Object Collections.Generic.List[string]
  $descendantStop = $null
  $drainUnresolved = $false
  $exitCode = $null
  try {
    $capturedDescendants = @(Get-C04CapturedDescendants -ParentId $pidValue -ParentStartTicks $startTicks)
    $state.capturedDescendants = $capturedDescendants
    $stdoutStream = [IO.File]::Open($stdoutPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    $stderrStream = [IO.File]::Open($stderrPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    $stdoutTask = $process.StandardOutput.BaseStream.CopyToAsync($stdoutStream)
    $stderrTask = $process.StandardError.BaseStream.CopyToAsync($stderrStream)
    if ($InjectPostStartFailure) { throw 'INJECTED_POST_START_FAILURE' }
    $nextHeartbeat = [DateTime]::UtcNow.AddSeconds([Math]::Max(1, $HeartbeatSeconds))
    $totalBudgetMs = [Math]::Max(1000, ($TimeoutSeconds + $CloseGraceSeconds) * 1000)
    while (-not $process.WaitForExit(250)) {
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
      if ($null -eq $stopResult) {
        $stopResult = Stop-C04OwnedProcess -Process $process -OwnedPid $pidValue -StartTicks $startTicks -GraceSeconds $CloseGraceSeconds
      }
    }
    if (-not $process.WaitForExit(5000)) {
      throw "$Name parent process did not exit after the bounded stop path."
    }
    $exitCode = $process.ExitCode

    $drainBudgetMs = [Math]::Max(1000, $totalBudgetMs - [int]$sw.ElapsedMilliseconds)
    try {
      Wait-C04TaskBounded $stdoutTask $drainBudgetMs ($Name + ' stdout drain')
      Wait-C04TaskBounded $stderrTask $drainBudgetMs ($Name + ' stderr drain')
    } catch {
      $drainTimedOut = $true
      $timedOut = $true
      if ($null -ne $job) {
        try { $job.Terminate() } catch { $cleanupFailures.Add('owned job termination failed: ' + $_.Exception.Message) }
      }
      if ($null -eq $OwnedRegistry) {
        $capturedStop = Stop-C04CapturedDescendants -Snapshots $capturedDescendants -GraceSeconds 1
        if (@($capturedStop.failures).Count -gt 0) {
          $drainUnresolved = $true
          foreach ($failure in $capturedStop.failures) { $cleanupFailures.Add($failure) }
        }
      }
      $descendantStop = Stop-C04OwnedDescendants -ParentId $pidValue -ParentStartTicks $startTicks -GraceSeconds 1
      if (@($descendantStop.failures).Count -gt 0) {
        $drainUnresolved = $true
        foreach ($failure in $descendantStop.failures) { $cleanupFailures.Add($failure) }
      }
      if ($null -ne $OwnedRegistry -and @($capturedDescendants).Count -gt 0) {
        $drainUnresolved = $true
        $cleanupFailures.Add('output drain blocked by captured descendant; caller-owned cleanup required')
      } else {
        try {
          Wait-C04TaskBounded $stdoutTask 5000 ($Name + ' stdout drain after descendant stop')
          Wait-C04TaskBounded $stderrTask 5000 ($Name + ' stderr drain after descendant stop')
          $drainTimedOut = $true
        } catch {
          $drainUnresolved = $true
          $cleanupFailures.Add('output drain remained blocked: ' + $_.Exception.Message)
        }
      }
    }

    $sw.Stop()
    $normalHonored = if ($null -eq $stopResult) { $true } else { [bool]$stopResult.normalCloseHonored }
    $forceStopped = if ($null -eq $stopResult) { $false } else { [bool]$stopResult.forceStopped }
    $line = "nativeExit=$exitCode`nelapsedSeconds=$([Math]::Round($sw.Elapsed.TotalSeconds,3))`npid=$pidValue`nstartTicks=$startTicks`ntimedOut=$($timedOut.ToString().ToLowerInvariant())`ndrainTimedOut=$($drainTimedOut.ToString().ToLowerInvariant())`ncancelled=$($cancelled.ToString().ToLowerInvariant())`nnormalCloseHonored=$($normalHonored.ToString().ToLowerInvariant())`nforceStopped=$($forceStopped.ToString().ToLowerInvariant())`ndrainUnresolved=$($drainUnresolved.ToString().ToLowerInvariant())`n"
    Write-C04Utf8 $exitPath $line
    $aliveAfter = [bool]$drainUnresolved
    $state.aliveAfter = $aliveAfter
    $state.phase = 'finished'
    $state.exitCode = $exitCode
    $state.timedOut = $timedOut
    $state.cancelled = $cancelled
    $state.forceStopped = $forceStopped
    $state.drainTimedOut = $drainTimedOut
    $state.drainUnresolved = $drainUnresolved
    $state.elapsedSeconds = [Math]::Round($sw.Elapsed.TotalSeconds, 3)
    $state.descendantsStopped = $(if ($null -eq $descendantStop) { 0 } else { @($descendantStop.stopped).Count })
    $state.cleanupFailures = @($cleanupFailures)
    $result = [pscustomobject]@{
      name = $Name
      pid = $pidValue
      startTicks = $startTicks
      fileName = $FileName
      arguments = $Arguments
      workingDirectory = $psi.WorkingDirectory
      exitCode = $exitCode
      timedOut = $timedOut
      drainTimedOut = $drainTimedOut
      cancelled = $cancelled
      normalCloseHonored = $normalHonored
      forceStopped = $forceStopped
      aliveAfter = $aliveAfter
      drainUnresolved = $drainUnresolved
      descendantsStopped = $(if ($null -eq $descendantStop) { 0 } else { @($descendantStop.stopped).Count })
      cleanupFailures = @($cleanupFailures)
      elapsedSeconds = [Math]::Round($sw.Elapsed.TotalSeconds, 3)
      stdoutPath = $stdoutPath
      stderrPath = $stderrPath
      exitPath = $exitPath
    }
    return $result
  }
  finally {
    $cleanupError = $null
    if (-not $process.HasExited) {
      try {
        $null = Stop-C04OwnedProcess -Process $process -OwnedPid $pidValue -StartTicks $startTicks -GraceSeconds $CloseGraceSeconds
      } catch {
        $cleanupError = $_.Exception.Message
      }
    }
    if ($null -eq $OwnedRegistry) {
      $finalCapturedStop = Stop-C04CapturedDescendants -Snapshots $capturedDescendants -GraceSeconds 1
      if (@($finalCapturedStop.failures).Count -gt 0) {
        $drainUnresolved = $true
        foreach ($failure in $finalCapturedStop.failures) { $cleanupFailures.Add($failure) }
      }
    }
    if ($null -ne $stdoutTask) { try { $null = $stdoutTask.Wait(5000) } catch { } }
    if ($null -ne $stderrTask) { try { $null = $stderrTask.Wait(5000) } catch { } }
    if ($null -ne $job) {
      try {
        $job.Dispose()
      } catch {
        $cleanupFailures.Add('owned job dispose failed: ' + $_.Exception.Message)
      }
      $job = $null
    }
    if ($null -ne $stdoutStream) { $stdoutStream.Dispose() }
    if ($null -ne $stderrStream) { $stderrStream.Dispose() }
    $state.aliveAfter = (-not $process.HasExited) -or $drainUnresolved -or ($null -ne $cleanupError)
    $state.drainUnresolved = $drainUnresolved
    $state.cleanupFailures = @($cleanupFailures)
    if ($null -ne $result) {
      $result.aliveAfter = $state.aliveAfter
      $result.drainUnresolved = $drainUnresolved
      $result.descendantsStopped = $state.descendantsStopped
      $result.cleanupFailures = @($cleanupFailures)
    }
    if ($null -ne $cleanupError) {
      $state.cleanupError = $cleanupError
      $cleanupFailures.Add($cleanupError)
    }
    $process.Dispose()
    if ($null -ne $cleanupError) {
      throw "Owned process cleanup failed: $cleanupError"
    }
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
  try {
    $Handle.mutex.ReleaseMutex()
  } finally {
    $Handle.mutex.Dispose()
  }
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
  $result = Invoke-C04OwnedProcess -Name $Name -FileName $Exe -Arguments $Arguments -WorkingDirectory $Cwd -TimeoutSeconds $Timeout -CancelFile $MutexCancelFile -CloseGraceSeconds $ProcessCloseGraceSeconds -EvidenceDirectory $script:RunRoot -HeartbeatSeconds $MutexHeartbeatSeconds -OwnedRegistry $script:OwnedProcesses
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
  $finalizationFailures = New-Object Collections.Generic.List[string]
  do {
  try {
    Log "PRECHECK repo=$repo runRoot=$script:RunRoot dotnet=$DotnetPath unity=$UnityPath"
    $identity = Get-C04SourceIdentity -Repo $repo -ExpectedHead $ExpectedSourceHead
    $summary.source = $identity
    Log "SOURCE head=$($identity.head) clean=true"

    $sourceManifest = Get-C04SelectedManifest -Base $repo -RelativeRoots @('dotnet', 'tools/unity')
    $sourceClosurePath = Join-Path $script:RunRoot 'source-closure.sha256'
    Write-C04Utf8 $sourceClosurePath $sourceManifest.canonical
    $summary.inputClosure = [ordered]@{
      count = $sourceManifest.count
      bytes = $sourceManifest.bytes
      sha256 = $sourceManifest.sha256
      manifest = $sourceClosurePath
      manifestSha256 = (Get-C04Sha256File $sourceClosurePath)
    }
    Log "INPUT_CLOSURE count=$($sourceManifest.count) bytes=$($sourceManifest.bytes) sha256=$($sourceManifest.sha256)"

    if ($PreflightOnly) {
      $summary.status = 'PASS'
      Log 'PREFLIGHT_ONLY complete'
      break
    }

    $staging = Join-Path $script:RunRoot 'staging-project'
    $copied = Copy-C04UnityInputs -Repo $repo -Destination $staging
    $stagedClosurePath = Join-Path $script:RunRoot 'staged-input.sha256'
    Write-C04Utf8 $stagedClosurePath $copied.staged.canonical
    $summary.stagedInput = [ordered]@{
      count = $copied.staged.count
      bytes = $copied.staged.bytes
      sha256 = $copied.staged.sha256
      manifest = $stagedClosurePath
      manifestSha256 = (Get-C04Sha256File $stagedClosurePath)
    }
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
    $selfResult = Start-C04Owned 'privacy-verifier-selftest' $DotnetPath @($verifierDll, 'self-test', (Join-Path $script:RunRoot 'verifier-selftest')) $repo 60
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
      if ($WindowsPlayerTests) {
        $summary.testPlayerPackage = Get-C04TestPlayerPackage -StagingProject $staging -RunRoot $script:RunRoot -SourceHead $identity.head
        Log "TEST_PLAYER_PACKAGE exe=$($summary.testPlayerPackage.executable) files=$($summary.testPlayerPackage.packageFileCount) bytes=$($summary.testPlayerPackage.packageBytes) sha=$($summary.testPlayerPackage.packageManifestSha256)"
      }
      if ($WindowsPlayerTests -and -not (Test-Path -LiteralPath (Join-Path $privacyEvidence 'role-switch-raw/raw-frames.json'))) {
        throw 'Windows test Player did not seal role-switch raw channel frames under its isolated evidence root.'
      }
      $requiredRawScenes = @(Get-C04ValidatedRawScenes -PrivacyEvidence $privacyEvidence)
      $rawResults = New-Object Collections.Generic.List[object]
      foreach ($scene in $requiredRawScenes) {
        $rawResults.Add($scene)
        Log "RAW_SCENE scene=$($scene.scene) frames=$($scene.count) sha256=$($scene.sha256)"
        $matrixTests = Join-Path $repo 'tools/tests/C04PrivacyMatrix.Tests.ps1'
        $verifierEvidence = Join-Path $script:RunRoot ('verifier-tests/' + $scene.scene)
        $matrixResult = Start-C04Owned ('privacy-matrix-tests-' + $scene.scene) 'powershell.exe' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $matrixTests, '-DotnetPath', $DotnetPath, '-VerifierDll', $verifierDll, '-RawFramesPath', $scene.path, '-RunRoot', $verifierEvidence) $repo 240
        Assert-C04NativeSuccess $matrixResult ('privacy-matrix-tests-' + $scene.scene)
      }
      $summary.rawFrames = @($rawResults)
    }

    $summary.status = 'PASS'
    $sourceAfter = Get-C04SelectedManifest -Base $repo -RelativeRoots @('dotnet', 'tools/unity')
    Assert-C04ManifestEqual $sourceManifest $sourceAfter 'Frozen source input'
    $sourceFinalClosurePath = Join-Path $script:RunRoot 'source-closure-final.sha256'
    Write-C04Utf8 $sourceFinalClosurePath $sourceAfter.canonical
    $summary.inputClosure['finalSha256'] = $sourceAfter.sha256
    $summary.inputClosure['finalManifest'] = $sourceFinalClosurePath
    $summary.inputClosure['finalManifestSha256'] = (Get-C04Sha256File $sourceFinalClosurePath)
    $identityAfter = Get-C04SourceIdentity -Repo $repo -ExpectedHead $identity.head
    Log "SOURCE_FROZEN head=$($identityAfter.head) closure=$($sourceAfter.sha256)"
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
    foreach ($process in $script:OwnedProcesses.ToArray()) {
      if ($process.aliveAfter) {
        try {
          Stop-C04OwnedRecord -Record $process -GraceSeconds $ProcessCloseGraceSeconds
        } catch {
          $finalizationFailures.Add("owned process $($process.pid) cleanup failed: $($_.Exception.Message)")
        }
      }
    }
    $allStopped = $true
    foreach ($process in $script:OwnedProcesses) {
      if ($process.aliveAfter) { $allStopped = $false }
    }
    $skipSeal = -not $allStopped
    if ($skipSeal) {
      $summary.status = 'UNKNOWN'
      $summary.unknown = $true
      $finalizationFailures.Add('residual owned writer; normal seal withheld')
      Log 'RESIDUAL_OWNED_WRITER_SEAL_WITHHELD'
    }
    if ($script:MutexHandle) {
      if ($allStopped) {
        try {
          Release-C04Mutex $script:MutexHandle
          Log 'MUTEX_RELEASED'
        } catch {
          $finalizationFailures.Add('mutex release failed: ' + $_.Exception.Message)
        }
      } else {
        $summary.unknown = $true
        Log 'MUTEX_HELD_RESIDUAL_WRITER'
      }
      $script:MutexHandle = $null
    }
    if ($script:StageDrive) {
      if ($allStopped) {
        try {
          & subst.exe $script:StageDrive /D
          if ($LASTEXITCODE -ne 0) { throw "subst release exited $LASTEXITCODE" }
          Log "UNITY_STAGING_DRIVE_RELEASED drive=$($script:StageDrive)"
          $script:StageDrive = $null
        } catch {
          $finalizationFailures.Add('staging drive release failed: ' + $_.Exception.Message)
        }
      } else {
        $finalizationFailures.Add('staging drive retained because an owned writer is still alive')
        Log "UNITY_STAGING_DRIVE_RETAINED drive=$($script:StageDrive)"
      }
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
        $finalizationFailures.Add("env restore failed for ${key}: $($_.Exception.Message)")
        Log "ENV_RESTORE_FAIL key=$key message=$($_.Exception.Message)"
      }
    }
    if (-not $allStopped) {
      $summary.status = 'UNKNOWN'
      $summary.unknown = $true
      Log 'RESIDUAL_OWNED_WRITER_UNKNOWN'
    }
    Log 'FINALLY_ASSIGN_SUMMARY'
    $summary.finishedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    if ($finalizationFailures.Count -gt 0) {
      try {
        Write-C04Utf8 (Join-Path $script:RunRoot 'finalization-failure.txt') (($finalizationFailures -join "`n") + "`n")
      } catch {
        $finalizationFailures.Add('finalization failure record write failed: ' + $_.Exception.Message)
      }
      $summary.status = 'FAIL'
      $summary.error = ($finalizationFailures -join '; ')
      $summary.finalizationFailures = @($finalizationFailures)
    }
    if ($skipSeal) {
      $summary.status = 'UNKNOWN'
      $summary.unknown = $true
    }
    Log 'FINALLY_ASSIGN_PROCESSES'
    $summary['processes'] = $script:OwnedProcesses.ToArray()
    if ($skipSeal) {
      Write-C04Json (Join-Path $script:RunRoot 'summary.json') $summary
      Write-Host 'SEAL_WITHHELD_RESIDUAL_WRITER'
    } else {
      Log 'FINALLY_SEAL'
      try {
        $sealed = Write-C04SealedEvidence -RunRoot $script:RunRoot -Summary $summary
        Write-Host ("SEALED manifest=" + $sealed.manifestSha256 + " files=" + $sealed.manifest.count)
      } catch {
        $sealFailure = 'evidence seal failed: ' + $_.Exception.ToString()
        $finalizationFailures.Add($sealFailure)
        $summary.status = 'FAIL'
        $summary.error = ($finalizationFailures -join '; ')
        $summary.finalizationFailures = @($finalizationFailures)
        try {
          $summaryPath = Join-Path $script:RunRoot 'summary.json'
          if (Test-Path -LiteralPath $summaryPath) { Write-C04Json $summaryPath $summary }
          foreach ($sealFile in @('manifest.sha256', 'seal.json')) {
            $candidate = Join-Path $script:RunRoot $sealFile
            if (Test-Path -LiteralPath $candidate) { Remove-Item -LiteralPath $candidate -Force }
          }
          Write-C04Utf8 (Join-Path $script:RunRoot 'seal-failure.txt') ($sealFailure + "`n")
        } catch { }
        Write-Host ("SEAL_FAILED " + $_.Exception.ToString())
      }
    }
  }
  } while ($false)
  if ($null -ne $failure) { throw $failure }
  if ($null -ne $finalizationFailures -and $finalizationFailures.Count -gt 0) {
    throw ('Runner finalization failed: ' + ($finalizationFailures -join '; '))
  }
}

Invoke-C04RunnerMain
