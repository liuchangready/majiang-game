#Requires -Version 5.1
[CmdletBinding()]
param(
  [string]$UnityPath = 'D:\Unity\Hub\Editor\6000.3.27f1\Editor\Unity.exe',
  [string]$OutputRoot,
  [string]$StagingRoot,
  [string]$PrivateToolchainRoot,
  [string]$RegistryCacheSource,
  [switch]$ReuseStaging,
  [string]$AndroidSdkRoot = 'D:\Android\Sdk',
  [string]$AvdHome = 'D:\Android\avd',
  [int]$MutexWaitSeconds = 600,
  [int]$UnityTimeoutSeconds = 7200,
  [int]$HeartbeatSeconds = 30,
  [int]$JobWorkerCount = 8,
  [int]$BeeBuildThreads = 0,
  [string]$CacheRoot = '',
  [switch]$ReuseBuildCache,
  [switch]$ProcessFixtureTest,
  [switch]$VerdictFixtureTest,
  [switch]$OutputFixtureTest,
  [switch]$SourceFixtureTest,
  [switch]$StagingGuardFixtureTest,
  [switch]$ArgumentFixtureTest,
  [switch]$PathBudgetFixtureTest
)
$ErrorActionPreference = 'Stop'
$script:AllowedPaths = @(
  'tools/unity/build-c05-android-emulator.ps1',
  'tools/unity/run-c05-android-emulator.ps1',
  'tools/unity/run-p45-c04-privacy-matrix.ps1',
  'tools/tests/C05AndroidEmulator.Tests.ps1',
  'unity/Mahjong.Unity/Assets/Editor/C05EmulatorAndroidBuild.cs',
  'unity/Mahjong.Unity/Assets/Editor/C05EmulatorAndroidBuild.cs.meta'
)
$script:RequiredScenes = @('Assets/Scenes/Boot.unity','Assets/Scenes/Menu.unity','Assets/Scenes/Lobby.unity',
  'Assets/Scenes/Table.unity','Assets/Scenes/Spectator.unity','Assets/Scenes/Settlement.unity')
$script:RequiredPlugins = @('Mahjong.Domain.dll','Mahjong.Contracts.dll','Mahjong.Contracts.Wire.dll',
  'Mahjong.TableRuntime.dll','Mahjong.Client.dll','Mahjong.LocalHost.dll','Mahjong.Ai.dll','Google.Protobuf.dll')

function Assert-C05EFreshOutputDirectory([string]$Path) {
  $full = [IO.Path]::GetFullPath($Path)
  if (Test-Path -LiteralPath $full) { throw 'Refusing to reuse an existing evidence/output directory; old APKs and failure logs are preserved.' }
  return $full
}
function ConvertTo-C05EWindowsArgument([string]$Value) {
  $builder = New-Object Text.StringBuilder; [void]$builder.Append('"'); $slashes = 0
  foreach ($ch in $Value.ToCharArray()) {
    if ($ch -eq [char]92) { $slashes++; continue }
    if ($ch -eq [char]34) { for ($i=0; $i -lt (2*$slashes+1); $i++) { [void]$builder.Append([char]92) }; [void]$builder.Append([char]34); $slashes = 0; continue }
    for ($i=0; $i -lt $slashes; $i++) { [void]$builder.Append([char]92) }
    $slashes = 0; [void]$builder.Append($ch)
  }
  for ($i=0; $i -lt (2*$slashes); $i++) { [void]$builder.Append([char]92) }
  [void]$builder.Append('"'); return $builder.ToString()
}
function Start-C05EHiddenProcess([string]$FilePath, [string[]]$Arguments, [string]$WorkingDirectory) {
  $line = (@($Arguments | ForEach-Object { ConvertTo-C05EWindowsArgument ([string]$_) }) -join ' ')
  return Start-Process -FilePath $FilePath -ArgumentList $line -WorkingDirectory $WorkingDirectory -WindowStyle Hidden -PassThru
}
function Get-C05EDescendantProcessIds([int]$RootId) {
  $all = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Select-Object ProcessId,ParentProcessId)
  $found = New-Object System.Collections.Generic.List[int]
  $frontier = @($RootId)
  while ($frontier.Count -gt 0) {
    $next = New-Object System.Collections.Generic.List[int]
    foreach ($p in $all) {
      $candidate = [int]$p.ProcessId
      if (($frontier -contains [int]$p.ParentProcessId) -and $candidate -ne $RootId -and -not $found.Contains($candidate)) {
        $found.Add($candidate); $next.Add($candidate)
      }
    }
    $frontier = $next.ToArray()
  }
  return $found.ToArray()
}
function Stop-C05EOwnedProcess([int]$ProcessId, [long]$ExpectedStartTicks) {
  $owned = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
  if (-not $owned -or $owned.StartTime.ToUniversalTime().Ticks -ne $ExpectedStartTicks) { return $false }
  $parentStart = $owned.StartTime
  $descendants = @(Get-C05EDescendantProcessIds $ProcessId)
  $verified = New-Object System.Collections.Generic.List[int]
  foreach ($id in $descendants) {
    $child = Get-Process -Id $id -ErrorAction SilentlyContinue
    if ($child -and $child.StartTime -ge $parentStart) { $verified.Add($id) }
  }
  for ($i = $verified.Count - 1; $i -ge 0; $i--) {
    $child = Get-Process -Id $verified[$i] -ErrorAction SilentlyContinue
    if ($child) { try { $child.Kill() } catch { } }
  }
  try { $owned.Kill(); $owned.WaitForExit() } catch { }
  $deadline = (Get-Date).AddSeconds(15)
  while ((Get-Date) -lt $deadline) {
    if (@($verified | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue }).Count -eq 0) { break }
    Start-Sleep -Milliseconds 250
  }
  return (@($verified | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue }).Count -eq 0)
}
function Invoke-C05ENative([string]$FilePath, [string[]]$Arguments, [int]$TimeoutSeconds = 120) {  $psi = New-Object Diagnostics.ProcessStartInfo
  $psi.FileName = $FilePath
  $psi.Arguments = (@($Arguments | ForEach-Object { ConvertTo-C05EWindowsArgument ([string]$_) }) -join ' ')
  $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true; $psi.WindowStyle = 'Hidden'
  $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
  $proc = New-Object Diagnostics.Process; $proc.StartInfo = $psi
  [void]$proc.Start(); $ticks = $proc.StartTime.ToUniversalTime().Ticks
  $outTask = $proc.StandardOutput.ReadToEndAsync(); $errTask = $proc.StandardError.ReadToEndAsync()
  $exited = $proc.WaitForExit($TimeoutSeconds * 1000); $killed = $false
  if (-not $exited) { $killed = Stop-C05EOwnedProcess $proc.Id $ticks; [void]$proc.WaitForExit(2000) }
  $drained = [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($outTask,$errTask), [TimeSpan]::FromSeconds(3))
  $stdout = if ($outTask.IsCompleted) { $outTask.Result } else { '' }
  $stderr = if ($errTask.IsCompleted) { $errTask.Result } else { '' }
  $exit = if ($exited -and $proc.HasExited) { $proc.ExitCode } else { $null }
  [pscustomobject]@{ file=$FilePath; args=$Arguments; exit=$exit; timedOut=(-not $exited); timeoutSeconds=$TimeoutSeconds;
    killedOwnedProcess=$killed; drainComplete=$drained; stdout=$stdout; stderr=$stderr }
}
function Invoke-C05EToFiles([string]$FilePath, [string[]]$Arguments, [int]$TimeoutSeconds, [string]$OutPath, [string]$ErrPath, [int]$HeartbeatSeconds = 30) {
  $line = (@($Arguments | ForEach-Object { ConvertTo-C05EWindowsArgument ([string]$_) }) -join ' ')
  $proc = Start-Process -FilePath $FilePath -ArgumentList $line -RedirectStandardOutput $OutPath -RedirectStandardError $ErrPath -PassThru -WindowStyle Hidden
  $ticks = $proc.StartTime.ToUniversalTime().Ticks
  $started = Get-Date; $deadline = $started.AddSeconds($TimeoutSeconds); $lastBeat = $started; $exited = $false; $killed = $false
  while ((Get-Date) -lt $deadline) {
    if ($proc.WaitForExit(2000)) { $exited = $true; break }
    if ($proc.HasExited) { $exited = $true; break }
    if (((Get-Date) - $lastBeat).TotalSeconds -ge $HeartbeatSeconds) {
      Write-Host ("HEARTBEAT {0:o} phase=child elapsed={1}s pid={2} file={3}" -f (Get-Date), [int]((Get-Date)-$started).TotalSeconds, $proc.Id, (Split-Path $FilePath -Leaf))
      $lastBeat = Get-Date
    }
  }
  if (-not $exited) { $killed = Stop-C05EOwnedProcess $proc.Id $ticks; [void]$proc.WaitForExit(2000) }
  $proc.Refresh()
  $stdout = if (Test-Path -LiteralPath $OutPath) { [string](Get-Content -LiteralPath $OutPath -Raw -ErrorAction SilentlyContinue) } else { '' }
  $stderr = if (Test-Path -LiteralPath $ErrPath) { [string](Get-Content -LiteralPath $ErrPath -Raw -ErrorAction SilentlyContinue) } else { '' }
  $exit = if ($exited -and $proc.HasExited) { [int]$proc.ExitCode } else { $null }
  return [pscustomobject]@{ file=$FilePath; args=$Arguments; exit=$exit; timedOut=(-not $exited); timeoutSeconds=$TimeoutSeconds;
    killedOwnedProcess=$killed; stdout=$stdout; stderr=$stderr }
}
function Invoke-C05EGit([string]$RepoRoot, [string[]]$GitArgs) {
  # Keep native Git path output ASCII-escaped so PowerShell 5.1's OEM decoder cannot corrupt UTF-8 path bytes.
  $git = (Get-Command git.exe -ErrorAction Stop).Source
  $native = Invoke-C05ENative $git (@('-C',$RepoRoot,'-c','core.quotepath=true') + $GitArgs) 30
  if ($native.timedOut -or -not $native.drainComplete -or $null -eq $native.exit) { throw "FAIL: git $($GitArgs -join ' ') timed out or did not drain completely." }
  if ($native.exit -ne 0) { throw "FAIL: git $($GitArgs -join ' ') exited $($native.exit): $($native.stderr)" }
  return @(([string]$native.stdout -split "`r?`n") | Where-Object { $_ -ne '' })
}
function ConvertFrom-C05EGitQuotedPath([string]$Line) {
  if (-not $Line.StartsWith('"')) { return $Line }
  if ($Line.Length -lt 2 -or -not $Line.EndsWith('"')) { throw 'FAIL: malformed Git C-quoted path.' }
  $bytes = New-Object System.Collections.Generic.List[byte]
  for ($i=1; $i -lt ($Line.Length-1); $i++) {
    $ch = $Line[$i]
    if ($ch -ne [char]92) {
      if ([int]$ch -gt 127) { throw 'FAIL: Git quoted path contained non-ASCII output.' }
      $bytes.Add([byte][int]$ch); continue
    }
    $i++; if ($i -ge ($Line.Length-1)) { throw 'FAIL: malformed Git C-quoted escape.' }
    $escaped = $Line[$i]
    if ($escaped -ge '0' -and $escaped -le '7') {
      if ($i+2 -ge ($Line.Length-1)) { throw 'FAIL: truncated Git octal path escape.' }
      $octal = [string]$escaped + [string]$Line[$i+1] + [string]$Line[$i+2]
      if ($octal -notmatch '^[0-7]{3}$') { throw 'FAIL: malformed Git octal path escape.' }
      $bytes.Add([Convert]::ToByte($octal,8)); $i += 2; continue
    }
    if ($escaped -eq [char]34) { $value=34 }
    elseif ($escaped -eq [char]92) { $value=92 }
    else { $value = switch ([string]$escaped) { 't' { 9 } 'n' { 10 } 'r' { 13 } 'b' { 8 } 'f' { 12 } 'v' { 11 } default { throw "FAIL: unsupported Git C-quoted escape: $escaped" } } }
    $bytes.Add([byte]$value)
  }
  try { return (New-Object System.Text.UTF8Encoding($false,$true)).GetString($bytes.ToArray()) }
  catch { throw "FAIL: Git path was not valid UTF-8: $($_.Exception.Message)" }
}
function Get-C05ESourceBoundary([string]$RepoRoot, [string[]]$AllowedPaths) {
  $headLines = Invoke-C05EGit $RepoRoot @('rev-parse','HEAD'); $head = ([string]($headLines | Select-Object -First 1)).Trim()
  $staged = @(Invoke-C05EGit $RepoRoot @('diff','--cached','--name-only') | ForEach-Object { ConvertFrom-C05EGitQuotedPath ([string]$_) }) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
  $unstaged = @(Invoke-C05EGit $RepoRoot @('diff','--name-only') | ForEach-Object { ConvertFrom-C05EGitQuotedPath ([string]$_) }) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
  $untracked = @(Invoke-C05EGit $RepoRoot @('ls-files','--others','--exclude-standard') | ForEach-Object { ConvertFrom-C05EGitQuotedPath ([string]$_) }) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
  # Unity/IL2CPP and the Gradle build create generated intermediates below the project
  # (e.g. .utmp, Library, Temp) that are not authored source and are not gitignored in
  # this repository. They are build outputs, not task inputs, so exclude them explicitly
  # while still rejecting any authored tracked/untracked source outside the task scope.
  $generatedPattern = '^unity/Mahjong\.Unity/(?:\.utmp|Library|Temp|obj|Logs|UserSettings)/'
  $untracked = @($untracked | Where-Object { $_ -notmatch $generatedPattern })
  $all = @(@($staged) + @($unstaged) + @($untracked) | Select-Object -Unique)
  $docDirectory = 'doc/v5-' + [string][char]0x91cd + [string][char]0x6784
  $expectedReports = @(
    "$docDirectory/V5-G2-C05-ANDROID-GEMINI38-DELIVERY-20261104.md",
    "$docDirectory/V5-G2-C05-ANDROID-GEMINI38-BINDING-20261104.json"
  )
  $unexpected = @($all | Where-Object { ($AllowedPaths -cnotcontains [string]$_) -and ($expectedReports -cnotcontains [string]$_) })
  if ($unexpected.Count) { throw ('FAIL: staged/unstaged/untracked inputs outside C05 emulator expectedFiles: ' + ($unexpected -join '; ')) }
  $hashes = @{}
  foreach ($path in $all) {
    $full = Join-Path $RepoRoot ($path.Replace('/', [IO.Path]::DirectorySeparatorChar))
    $hash = if (Test-Path -LiteralPath $full -PathType Leaf) { (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash } else { $null }
    $hashes[$path] = $hash
  }
  return [pscustomobject]@{ head=$head; staged=@($staged); unstaged=@($unstaged); untracked=@($untracked);
    dirtyPaths=@($all); unexpected=@(); inputHashes=$hashes }
}
function Repair-C05EPostBuildSource([string]$RepoRoot, [string[]]$BeforeDirty, [string]$RunDir) {
  # Unity import can silently rewrite tracked assets (font YAML, performance info,
  # solution file) even when the build fails. Preserve the diff, restore the exact
  # HEAD bytes, and report the rewrite as a build failure instead of letting it
  # contaminate the reviewed tree.
  $staged = @(Invoke-C05EGit $RepoRoot @('diff','--cached','--name-only')) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
  $unstaged = @(Invoke-C05EGit $RepoRoot @('diff','--name-only')) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
  $untracked = @(Invoke-C05EGit $RepoRoot @('ls-files','--others','--exclude-standard')) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
  # Unity/IL2CPP and the Gradle build create generated intermediates below the project
  # (e.g. .utmp, Library, Temp) that are not authored source and are not gitignored in
  # this repository. They are build outputs, not task inputs, so exclude them explicitly
  # while still rejecting any authored tracked/untracked source outside the task scope.
  $generatedPattern = '^unity/Mahjong\.Unity/(?:\.utmp|Library|Temp|obj|Logs|UserSettings)/'
  $untracked = @($untracked | Where-Object { $_ -notmatch $generatedPattern })
  $now = @(@($staged) + @($unstaged) + @($untracked) | Select-Object -Unique)
  $new = @($now | Where-Object { $BeforeDirty -notcontains [string]$_ })
  if ($new.Count -eq 0) { return @() }
  $diffPath = Join-Path $RunDir 'post-build-source-rewrite.diff'
  $diff = @(& git -C $RepoRoot -c core.quotepath=false diff --binary -- @($new) 2>&1)
  [IO.File]::WriteAllText($diffPath, ($diff -join "`n"), (New-Object Text.UTF8Encoding($false)))
  # Read-only detection: never checkout/restore. Authored source is immutable; a concurrent
  # user edit must be preserved and the build must fail, not be silently reverted.
  $report = [pscustomobject]@{ detectedUtc=(Get-Date).ToUniversalTime().ToString('o'); rewritten=@($new); restored=@();
    keptUntracked=@($new); diff=(Split-Path $diffPath -Leaf); diffSha256=(Get-FileHash $diffPath -Algorithm SHA256).Hash }
  Save-C05EJson (Join-Path $RunDir 'post-build-source-rewrite.json') $report
  return $new
}
function Test-C05EAllowedTransientRewrite([string]$Path) {
  # The forced shared-plugin build intentionally rewrites the tracked plugin DLLs/PDBs and
  # restores them in the finally block. Unity's importer/build preprocessors also reserialize
  # a small, fixed set of editor-generated files. They are restored to the exact HEAD bytes;
  # every other path is still a hard failure.
  if ($Path -like 'unity/Mahjong.Unity/Assets/Plugins/Mahjong/*') { return $true }
  $generated = @(
    'unity/Mahjong.Unity/Assets/Art/UI/Fonts/NotoSansSC-Regular SDF.asset',
    'unity/Mahjong.Unity/Assets/Resources/PerformanceTestRunInfo.json',
    'unity/Mahjong.Unity/Assets/Resources/PerformanceTestRunInfo.json.meta',
    'unity/Mahjong.Unity/Assets/Resources/PerformanceTestRunSettings.json',
    'unity/Mahjong.Unity/Assets/Resources/PerformanceTestRunSettings.json.meta',
    'unity/Mahjong.Unity/Mahjong.Unity.slnx',
    'unity/Mahjong.Unity/ProjectSettings/ProjectSettings.asset'
  )
  return ($generated -contains $Path)
}
function Get-C05ESourceClosure([string]$ProjectRoot) {
  $roots = @('Assets','Packages','ProjectSettings')
  $rows = New-Object System.Collections.Generic.List[object]
  foreach ($relRoot in $roots) {
    $dir = Join-Path $ProjectRoot $relRoot
    if (-not (Test-Path -LiteralPath $dir -PathType Container)) { continue }
    foreach ($file in Get-ChildItem -LiteralPath $dir -Recurse -File | Sort-Object FullName) {
      $rel = $file.FullName.Substring($ProjectRoot.Length + 1).Replace('\','/')
      $rows.Add([pscustomobject]@{ path=$rel; bytes=$file.Length; sha256=(Get-FileHash $file.FullName -Algorithm SHA256).Hash })
    }
  }
  return $rows.ToArray()
}
function Compare-C05ESourceClosure([object[]]$Before, [object[]]$After) {
  $b = @{}; foreach ($r in @($Before)) { $b[[string]$r.path] = ([string]$r.sha256) + '|' + [string]$r.bytes }
  $a = @{}; foreach ($r in @($After)) { $a[[string]$r.path] = ([string]$r.sha256) + '|' + [string]$r.bytes }
  $changed = New-Object System.Collections.Generic.List[string]
  foreach ($k in $b.Keys) { if (-not $a.ContainsKey($k) -or $a[$k] -ne $b[$k]) { $changed.Add($k) } }
  foreach ($k in $a.Keys) { if (-not $b.ContainsKey($k)) { $changed.Add($k) } }
  return @($changed | Sort-Object -Unique)
}
function Get-C05ESourceClosureDigest([object[]]$Rows) {
  $lines = @($Rows | Sort-Object path | ForEach-Object { ([string]$_.path) + '|' + [string]$_.bytes + '|' + [string]$_.sha256 })
  $text = $lines -join "`n"
  $sha = [Security.Cryptography.SHA256]::Create()
  try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text))).Replace('-','').ToUpperInvariant()) }
  finally { $sha.Dispose() }
}
function Test-C05ENoReparse([string]$Path) {
  $full = [IO.Path]::GetFullPath($Path)
  $current = $full
  while (-not [string]::IsNullOrWhiteSpace($current)) {
    if (Test-Path -LiteralPath $current) {
      $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
      if ($item -and (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) { return $false }
    }
    $parent = Split-Path -Parent $current
    if ($parent -eq $current -or [string]::IsNullOrWhiteSpace($parent)) { break }
    $current = $parent
  }
  return $true
}
function Test-C05EStagingDestination([string]$Path) {
  if ([string]::IsNullOrWhiteSpace($script:C05EAllowedStagingRoot)) { return $false }
  $full = [IO.Path]::GetFullPath($Path)
  $prefix = [IO.Path]::GetFullPath($script:C05EAllowedStagingRoot) + [IO.Path]::DirectorySeparatorChar
  if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { return $false }
  if (-not (Test-C05ENoReparse $full)) { return $false }
  return $true
}
function Resolve-C05EStagingRoot([string]$RepoRoot, [string]$Path, [bool]$Reuse) {
  if ([string]::IsNullOrWhiteSpace($Path)) { throw 'BLOCKED: StagingRoot is empty.' }
  if ($Path -match '(^|[\\/])\.{1,2}([\\/]|$)') { throw 'BLOCKED: StagingRoot traversal segments are forbidden.' }
  $full = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
  $repoBase = [IO.Path]::GetFullPath((Join-Path $RepoRoot 'build\c05-emulator-staging')).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
  $shortBase = if ($script:C05EShortStagingRoot) { [IO.Path]::GetFullPath($script:C05EShortStagingRoot) } else { [IO.Path]::GetFullPath('D:\c05s') }
  $repoPrefix = $repoBase + [IO.Path]::DirectorySeparatorChar
  $shortPrefix = $shortBase + [IO.Path]::DirectorySeparatorChar
  $external = $false
  if ($full.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) { $allowed = $repoBase }
  elseif ($full.StartsWith($shortPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    $external = $true
    $runId = $full.Substring($shortPrefix.Length)
    if ($runId -notmatch '^[0-9a-f]{32}$') { throw 'BLOCKED: external staging must be one direct 32-hex GUID child of the approved short base.' }
    if ($Reuse) { throw 'BLOCKED: external short staging is always fresh and cannot use -ReuseStaging.' }
    if (Test-Path -LiteralPath $full) { throw 'BLOCKED: external short staging already exists; it is never reused.' }
    $allowed = $full
  } else { throw "BLOCKED: StagingRoot '$full' is outside the repository staging root and approved short base." }
  if (-not $external -and (Test-Path -LiteralPath $full) -and -not $Reuse) { throw "BLOCKED: staging root already exists and must not be reused without -ReuseStaging: $full" }
  if (-not (Test-C05ENoReparse $full)) { throw "BLOCKED: StagingRoot '$full' traverses a reparse point." }
  $script:C05EAllowedStagingRoot = $allowed
  $script:C05EExternalStaging = $external
  return $full
}
function Get-C05ERegistryPackageInventory([string]$PackageRoot, [string]$ExpectedName, [string]$ExpectedVersion) {
  $manifestPath = Join-Path $PackageRoot 'package.json'
  if (-not (Test-C05ETreeNoReparse $PackageRoot)) { throw "package-tree-reparse:$ExpectedName" }
  if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "missing-package-json:$ExpectedName" }
  $manifest = Get-Content -Encoding UTF8 -LiteralPath $manifestPath -Raw | ConvertFrom-Json
  if ([string]$manifest.name -cne $ExpectedName -or [string]$manifest.version -cne $ExpectedVersion) { throw "package-identity-mismatch:${ExpectedName}:$($manifest.name):$($manifest.version)" }
  $files = @(Get-ChildItem -LiteralPath $PackageRoot -File -Recurse -Force -ErrorAction Stop | Sort-Object FullName)
  if ($files.Count -eq 0) { throw "empty-package-inventory:$ExpectedName" }
  $inventory = New-Object System.Collections.Generic.List[object]
  $canonical = New-Object Text.StringBuilder
  foreach ($file in $files) {
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "package-file-is-reparse:$($file.FullName)" }
    $relative = $file.FullName.Substring($PackageRoot.Length).TrimStart([char]92,[char]47).Replace('\','/')
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256 -ErrorAction Stop).Hash
    $inventory.Add([pscustomobject]@{ path=$relative; bytes=[long]$file.Length; sha256=$hash })
    [void]$canonical.Append($relative).Append("`0").Append([string]$file.Length).Append("`0").Append($hash).Append("`n")
  }
  return [pscustomobject]@{ name=$ExpectedName; version=$ExpectedVersion; packageJsonSha256=(Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash;
    fileCount=$inventory.Count; files=@($inventory.ToArray()); inventorySha256=(Get-C05ESha256Text $canonical.ToString()) }
}
function Get-C05ESha256Text([string]$Text) {
  $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes($Text)
  $sha = [Security.Cryptography.SHA256]::Create()
  try { return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-','') } finally { $sha.Dispose() }
}
function Copy-C05EVerifiedRegistryCache([string]$SourceCache, [string]$DestinationProject) {
  $source = [IO.Path]::GetFullPath($SourceCache); $destinationProject = [IO.Path]::GetFullPath($DestinationProject)
  $sourceProject = Split-Path (Split-Path $source -Parent) -Parent
  $sourceLock = Join-Path $sourceProject 'Packages\packages-lock.json'; $destinationLock = Join-Path $destinationProject 'Packages\packages-lock.json'
  if (-not (Test-Path -LiteralPath $source -PathType Container) -or -not (Test-Path -LiteralPath $sourceLock -PathType Leaf)) { throw 'BLOCKED: registry cache source or source lock is missing.' }
  if (-not (Test-Path -LiteralPath $destinationLock -PathType Leaf)) { throw 'BLOCKED: staged package lock is missing before registry cache preparation.' }
  $sourceLockHash = (Get-FileHash -LiteralPath $sourceLock -Algorithm SHA256).Hash
  $destinationLockHash = (Get-FileHash -LiteralPath $destinationLock -Algorithm SHA256).Hash
  if ($sourceLockHash -ne $destinationLockHash) { throw 'BLOCKED: registry cache source lock differs from frozen staged lock.' }
  $lock = Get-Content -Encoding UTF8 -LiteralPath $destinationLock -Raw | ConvertFrom-Json
  $registry = @($lock.dependencies.PSObject.Properties | Where-Object { [string]$_.Value.source -eq 'registry' })
  if ($registry.Count -eq 0) { throw 'BLOCKED: current lock contains no registry packages to prepare.' }
  $target = Join-Path $destinationProject 'Library\PackageCache'
  if (Test-Path -LiteralPath $target) { throw 'BLOCKED: refusing to merge a registry cache into an existing staging PackageCache.' }
  $copyRows = New-Object System.Collections.Generic.List[object]
  foreach ($dependency in $registry) {
    $name = [string]$dependency.Name; $version = [string]$dependency.Value.version
    $pattern = '^' + [regex]::Escape($name) + '@[0-9a-f]{12}$'
    $matches = @(Get-ChildItem -LiteralPath $source -Directory -ErrorAction Stop | Where-Object { $_.Name -match $pattern })
    if ($matches.Count -ne 1) { throw "BLOCKED: registry cache inventory for '$name' found $($matches.Count) exact package roots." }
    $sourceRoot = $matches[0].FullName
    $manifestPath = Join-Path $sourceRoot 'package.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "BLOCKED: registry cache package '$name' has no package.json." }
    $manifest = Get-Content -Encoding UTF8 -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ([string]$manifest.name -cne $name -or [string]$manifest.version -cne $version) { throw "BLOCKED: registry cache package identity/version mismatch for '$name'." }
    if (-not (Test-C05ETreeNoReparse $sourceRoot)) { throw "BLOCKED: registry cache package '$name' contains a reparse point." }
    $copyRows.Add([pscustomobject]@{ name=$name; version=$version; source=$sourceRoot; packageJsonSha256=(Get-FileHash $manifestPath -Algorithm SHA256).Hash })
  }
  New-Item -ItemType Directory -Path $target | Out-Null
  $verified = New-Object System.Collections.Generic.List[object]
  foreach ($row in $copyRows) {
    $destination = Join-Path $target (Split-Path $row.source -Leaf)
    $log = Join-Path $script:C05EActiveRun ('registry-cache-' + $row.name + '.log')
    $result = Invoke-C05EToFiles 'robocopy' @($row.source,$destination,'/E','/XJ','/NFL','/NDL','/NJH','/NJS','/NP','/R:1','/W:1') 1800 $log ($log + '.err')
    if ($result.timedOut -or $null -eq $result.exit -or $result.exit -ge 8) { throw "BLOCKED: registry cache copy failed for '$($row.name)' (native=$($result.exit) timeout=$($result.timedOut))." }
    $inventory = Get-C05ERegistryPackageInventory $destination $row.name $row.version
    if ($inventory.packageJsonSha256 -ne $row.packageJsonSha256) { throw "BLOCKED: copied package.json hash differs for '$($row.name)'." }
    $verified.Add([pscustomobject]@{ name=$row.name; version=$row.version; source=$row.source; sourcePackageJsonSha256=$row.packageJsonSha256;
      robocopyNative=$result.exit; destinationInventorySha256=$inventory.inventorySha256; destinationFileCount=$inventory.fileCount; files=$inventory.files })
  }
  return [pscustomobject]@{ status='COPIED_AND_HASHED'; source=$source; destination=$target; packageLockSha256=$destinationLockHash; packageCount=$verified.Count; packages=@($verified.ToArray()) }
}
function Get-C05EPathBudgetAudit([string]$ProjectPath, [string]$UnityPath, [string[]]$ProjectedPaths = @(), [string]$ExpectedConsumerRoot = '') {
  $fullProject = [IO.Path]::GetFullPath($ProjectPath)
  $consumerRoot = if ([string]::IsNullOrWhiteSpace($ExpectedConsumerRoot)) { $fullProject } else { [IO.Path]::GetFullPath($ExpectedConsumerRoot) }
  $incomplete = New-Object System.Collections.Generic.List[string]
  $overLimit = New-Object System.Collections.Generic.List[string]
  $inaccessible = New-Object System.Collections.Generic.List[string]
  $packageInventory = New-Object System.Collections.Generic.List[object]
  $observed = New-Object System.Collections.Generic.List[object]
  $lockPath = Join-Path $fullProject 'Packages\packages-lock.json'
  if (-not (Test-Path -LiteralPath $lockPath -PathType Leaf)) { $incomplete.Add("missing-lock:$lockPath") }
  else {
    $lock = $null
    try { $lock = Get-Content -Encoding UTF8 -LiteralPath $lockPath -Raw | ConvertFrom-Json }
    catch { $incomplete.Add("invalid-lock:${lockPath}:$($_.Exception.Message)") }
    if ($lock) {
      $packageCache = Join-Path $fullProject 'Library\PackageCache'
      $unityEditor = Split-Path ([IO.Path]::GetFullPath($UnityPath)) -Parent
      $builtinRoot = Join-Path $unityEditor 'Data\Resources\PackageManager\BuiltInPackages'
      foreach ($property in $lock.dependencies.PSObject.Properties) {
        $name = [string]$property.Name; $dependency = $property.Value; $source = [string]$dependency.source; $sourceRoot = $null
        if ($source -eq 'registry') {
          $pattern = '^' + [regex]::Escape($name) + '@[0-9a-f]{12}$'
          $matches = if (Test-Path -LiteralPath $packageCache -PathType Container) { @(Get-ChildItem -LiteralPath $packageCache -Directory -ErrorAction Stop | Where-Object { $_.Name -match $pattern }) } else { @() }
          if ($matches.Count -eq 1) {
            $sourceRoot = $matches[0].FullName
            try {
              $manifestPath = Join-Path $sourceRoot 'package.json'
              if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'missing-package-json' }
              $manifest = Get-Content -Encoding UTF8 -LiteralPath $manifestPath -Raw | ConvertFrom-Json
              if ([string]$manifest.name -cne $name -or [string]$manifest.version -cne [string]$dependency.version) { throw "package-identity-mismatch:$($manifest.name):$($manifest.version)" }
            } catch { $incomplete.Add("registry-package-metadata:${name}:$($_.Exception.Message)"); $sourceRoot=$null }
          } else { $incomplete.Add("registry-package-inventory:${name}:found=$($matches.Count)") }
        } elseif ($source -eq 'builtin') {
          $cacheMatches = if (Test-Path -LiteralPath $packageCache -PathType Container) { @(Get-ChildItem -LiteralPath $packageCache -Directory -ErrorAction Stop | Where-Object { $_.Name.StartsWith(($name + '@'), [StringComparison]::OrdinalIgnoreCase) }) } else { @() }
          if ($cacheMatches.Count -eq 1) { $sourceRoot = $cacheMatches[0].FullName }
          else { $candidate = Join-Path $builtinRoot $name; if (Test-Path -LiteralPath $candidate -PathType Container) { $sourceRoot = $candidate } else { $incomplete.Add("builtin-package-inventory:$name") } }
        } elseif ($source -eq 'embedded') {
          $candidate = Join-Path (Join-Path $fullProject 'Packages') $name
          if (Test-Path -LiteralPath $candidate -PathType Container) { $sourceRoot = $candidate } else { $incomplete.Add("embedded-package-inventory:$name") }
        } elseif ($source -eq 'local' -and ([string]$dependency.url).StartsWith('file:', [StringComparison]::OrdinalIgnoreCase)) {
          $local = ([string]$dependency.url).Substring(5).Replace('/', [IO.Path]::DirectorySeparatorChar)
          $candidate = if ([IO.Path]::IsPathRooted($local)) { $local } else { Join-Path $fullProject $local }
          $candidate = [IO.Path]::GetFullPath($candidate)
          if (Test-Path -LiteralPath $candidate -PathType Container) { $sourceRoot = $candidate } else { $incomplete.Add("local-package-inventory:$name") }
        } else { $incomplete.Add("unsupported-package-source:${name}:$source") }
        if ($sourceRoot) {
          try {
            $registryFiles = $null
            if ($source -eq 'registry') { $registryFiles = Get-C05ERegistryPackageInventory $sourceRoot $name ([string]$dependency.version); $sourceFiles = @($registryFiles.files) }
            else { $sourceFiles = @(Get-ChildItem -LiteralPath $sourceRoot -File -Recurse -Force -ErrorAction Stop) }
            if ($sourceFiles.Count -eq 0) { $incomplete.Add("empty-package-inventory:$name") }
            foreach ($file in $sourceFiles) {
              $sourceFilePath = if ($source -eq 'registry') { Join-Path $sourceRoot ([string]$file.path.Replace('/', [IO.Path]::DirectorySeparatorChar)) } else { $file.FullName }
              $observed.Add([pscustomobject]@{ path=$sourceFilePath; kind="package:$name"; exists=$true })
              $relative = if ($source -eq 'registry') { [string]$file.path } else { $file.FullName.Substring($sourceRoot.Length).TrimStart([char]92,[char]47) }
              $cacheLeaf = if ($source -eq 'registry' -or ($source -eq 'builtin' -and $sourceRoot.StartsWith($packageCache,[StringComparison]::OrdinalIgnoreCase))) { Split-Path $sourceRoot -Leaf } else { $name + '@000000000000' }
              $target = Join-Path (Join-Path (Join-Path $consumerRoot 'Library\PackageCache') $cacheLeaf) $relative
              $observed.Add([pscustomobject]@{ path=$target; kind="projected-package-cache:$name"; exists=$false })
            }
            $packageInventory.Add([pscustomobject]@{ name=$name; version=$(if($source -eq 'registry'){[string]$dependency.version}else{$null}); source=$source; root=$sourceRoot;
              fileCount=$sourceFiles.Count; packageJsonSha256=$(if($registryFiles){$registryFiles.packageJsonSha256}else{$null}); inventorySha256=$(if($registryFiles){$registryFiles.inventorySha256}else{$null}); files=$(if($registryFiles){$registryFiles.files}else{@()}) })
          } catch { $incomplete.Add("package-enumeration:${name}:$($_.Exception.Message)") }
        }
      }
    }
  }
  try {
    foreach ($file in @(Get-ChildItem -LiteralPath $fullProject -File -Recurse -Force -ErrorAction Stop)) {
      $observed.Add([pscustomobject]@{ path=$file.FullName; kind='staged-project'; exists=$true })
    }
  } catch { $inaccessible.Add("project-enumeration:$($_.Exception.Message)") }
  $futurePaths = @(
    (Join-Path $consumerRoot 'Library\PackageCache'),
    (Join-Path $consumerRoot 'Library\ScriptAssemblies\Assembly-CSharp.dll'),
    (Join-Path $consumerRoot 'Library\Bee\Android\Prj\IL2CPP\Gradle\unityLibrary\src\main\AndroidManifest.xml'),
    (Join-Path $consumerRoot 'Temp\StagingArea')
  ) + @($ProjectedPaths)
  foreach ($path in $futurePaths) { $observed.Add([pscustomobject]@{ path=[IO.Path]::GetFullPath($path); kind='future-output-budget'; exists=(Test-Path -LiteralPath $path) }) }
  $unique = @($observed | Sort-Object path,kind -Unique)
  $maxLength = 0
  foreach ($entry in $unique) {
    $length = $entry.path.Length; if ($length -gt $maxLength) { $maxLength = $length }
    if ($length -ge 260) { $overLimit.Add("$($entry.kind):${length}:$($entry.path)"); continue }
    if ($entry.exists -and -not [IO.File]::Exists($entry.path) -and -not [IO.Directory]::Exists($entry.path)) { $inaccessible.Add("ordinary-consumer-inaccessible:$($entry.path)") }
  }
  $complete = ($incomplete.Count -eq 0 -and $inaccessible.Count -eq 0)
  $status = if ($overLimit.Count) { 'FAIL_PATH_BUDGET' } elseif (-not $complete) { 'INCOMPLETE_INVENTORY' } else { 'PASS' }
  return [pscustomobject]@{ status=$status; completeInventory=$complete; project=$fullProject; packageLock=$lockPath; packageLockSha256=$(if(Test-Path -LiteralPath $lockPath){(Get-FileHash $lockPath -Algorithm SHA256).Hash}else{$null}); packageInventory=@($packageInventory.ToArray()); packagesMissing=@($incomplete.ToArray()); inaccessible=@($inaccessible.ToArray()); overLimit=@($overLimit.ToArray()); fileAndProjectedPathCount=$unique.Count; maxPathChars=$maxLength; ordinaryPathLimitExclusive=260; futureOutputRoots=$futurePaths }
}
function Assert-C05EBuildDestination([string]$Path, [string]$Kind) {
  $full = [IO.Path]::GetFullPath($Path)
  $buildRoot = [IO.Path]::GetFullPath((Join-Path $script:C05ERepoRoot 'build')) + [IO.Path]::DirectorySeparatorChar
  if (-not $full.StartsWith($buildRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "BLOCKED: $Kind '$full' is outside this worktree build directory." }
  if (-not (Test-C05ENoReparse $full)) { throw "BLOCKED: $Kind '$full' traverses a reparse/junction point." }
  $sourcePrefix = [IO.Path]::GetFullPath((Join-Path $script:C05ERepoRoot 'unity\Mahjong.Unity')) + [IO.Path]::DirectorySeparatorChar
  if ($full.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "BLOCKED: $Kind '$full' overlaps the authored source project." }
  return $full
}
function Test-C05ETreeNoReparse([string]$Path) {
  if (-not (Test-Path -LiteralPath $Path)) { return $true }
  $rootItem = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
  if ($rootItem -and (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) { return $false }
  $nested = @(Get-ChildItem -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue |
    Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 } | Select-Object -First 1)
  return ($nested.Count -eq 0)
}
function Copy-C05EStagingProject([string]$SourceProject, [string]$DestinationProject) {
  if (-not (Test-C05EStagingDestination $DestinationProject)) { $script:C05EStagingViolation = "destination '$DestinationProject' is outside the owned staging root or traverses a reparse point"; return $false }
  $map = @{ 'Assets'='Assets'; 'Packages'='Packages'; 'ProjectSettings'='ProjectSettings'; 'Library'='Library' }
  $trees = New-Object System.Collections.Generic.List[object]
  foreach ($name in $map.Keys) {
    $src = Join-Path $SourceProject $name
    $dst = Join-Path $DestinationProject $name
    if (-not (Test-Path -LiteralPath $src -PathType Container)) { continue }
    $trees.Add([pscustomobject]@{ name=$name; src=$src; dst=$dst })
  }
  # Validate every tree (including nested junctions) before creating or mirroring any tree.
  foreach ($tree in $trees) {
    if (-not (Test-C05ETreeNoReparse $tree.src) -or -not (Test-C05ETreeNoReparse $tree.dst)) {
      $script:C05EStagingViolation = "staging tree '$($tree.name)' contains a reparse/junction point; refusing any mirror"
      return $false
    }
  }
  $i = 0
  foreach ($tree in $trees) {
    New-Item -ItemType Directory -Force -Path $tree.dst | Out-Null
    $i++
    $log = Join-Path $script:C05EActiveRun "staging-copy-$i.log"
    $rc = Invoke-C05EToFiles 'robocopy' @($tree.src,$tree.dst,'/MIR','/NFL','/NDL','/NJH','/NJS','/NP','/R:1','/W:1') 1800 $log ($log + '.err')
    if ($rc.timedOut -or $rc.exit -ge 8) { throw "BLOCKED: staging copy failed for '$($tree.name)' (exit=$($rc.exit) timedOut=$($rc.timedOut))." }
  }
  return $true
}
function Sync-C05EStagingAuthored([string]$SourceProject, [string]$DestinationProject) {
  if (-not (Test-C05EStagingDestination $DestinationProject)) { $script:C05EStagingViolation = "destination '$DestinationProject' is outside the owned staging root or traverses a reparse point"; return $false }
  # Reused staging keeps its Library/import cache but its authored inputs must be reset to
  # the frozen source bytes: previous interrupted Unity runs may have reserialized settings.
  $trees = New-Object System.Collections.Generic.List[object]
  foreach ($name in @('Assets','Packages','ProjectSettings')) {
    $src = Join-Path $SourceProject $name
    $dst = Join-Path $DestinationProject $name
    if (-not (Test-Path -LiteralPath $src -PathType Container)) { continue }
    $trees.Add([pscustomobject]@{ name=$name; src=$src; dst=$dst })
  }
  foreach ($tree in $trees) {
    if (-not (Test-C05ETreeNoReparse $tree.src) -or -not (Test-C05ETreeNoReparse $tree.dst)) {
      $script:C05EStagingViolation = "staging tree '$($tree.name)' contains a reparse/junction point; refusing any mirror"
      return $false
    }
  }
  $i = 0
  foreach ($tree in $trees) {
    New-Item -ItemType Directory -Force -Path $tree.dst | Out-Null
    $i++
    $log = Join-Path $script:C05EActiveRun "staging-sync-$i.log"
    $rc = Invoke-C05EToFiles 'robocopy' @($tree.src,$tree.dst,'/MIR','/NFL','/NDL','/NJH','/NJS','/NP','/R:1','/W:1') 1800 $log ($log + '.err')
    if ($rc.timedOut -or $rc.exit -ge 8) { throw "BLOCKED: staging authored-input sync failed for '$($tree.name)' (exit=$($rc.exit) timedOut=$($rc.timedOut))." }
  }
  return $true
}
function Get-C05EProjectContract([string]$ProjectRoot) {
  $sceneFile = Join-Path $ProjectRoot 'ProjectSettings/EditorBuildSettings.asset'
  $settingsFile = Join-Path $ProjectRoot 'ProjectSettings/ProjectSettings.asset'
  if (-not (Test-Path -LiteralPath $sceneFile -PathType Leaf) -or -not (Test-Path -LiteralPath $settingsFile -PathType Leaf)) {
    throw 'FAIL: Unity scene/Android project settings are missing.'
  }
  $sceneText = Get-Content -LiteralPath $sceneFile -Raw
  $matches = [regex]::Matches($sceneText, '(?m)^\s*- enabled:\s*1\s*\r?\n\s*path:\s*(?<path>[^\r\n]+)')
  $enabled = @($matches | ForEach-Object { $_.Groups['path'].Value.Trim() })
  $settings = Get-Content -LiteralPath $settingsFile -Raw
  $min = [regex]::Match($settings, '(?m)^\s*AndroidMinSdkVersion:\s*(?<value>\d+)\s*$')
  $target = [regex]::Match($settings, '(?m)^\s*AndroidTargetSdkVersion:\s*(?<value>\d+)\s*$')
  if (-not $min.Success -or -not $target.Success) { throw 'FAIL: ProjectSettings lacks explicit Android SDK settings.' }
  return [pscustomobject]@{ enabledScenes=$enabled; minSdk=[int]$min.Groups['value'].Value; targetSdkSetting=[int]$target.Groups['value'].Value }
}
function Get-C05EBadgingContract([string]$Metadata) {
  $packageLines = [regex]::Matches($Metadata, "(?m)^package:\s+name='(?<value>[^']+)'(?:\s|$).*$")
  $minLines = [regex]::Matches($Metadata, "(?m)^minSdkVersion:'(?<value>\d+)'\s*$")
  $targetLines = [regex]::Matches($Metadata, "(?m)^targetSdkVersion:'(?<value>\d+)'\s*$")
  $abiLines = [regex]::Matches($Metadata, '(?m)^native-code:\s*(?<value>[^\r\n]*)\s*$')
  $activityLines = [regex]::Matches($Metadata, "(?m)^launchable-activity:\s+name='(?<value>[^']+)'(?:\s|$).*$")
  if ($packageLines.Count -ne 1 -or $minLines.Count -ne 1 -or $targetLines.Count -ne 1 -or $abiLines.Count -ne 1 -or $activityLines.Count -ne 1) {
    throw 'FAIL: APK badging package/SDK/native-code/launchable-activity records are missing or duplicated.'
  }
  $abiText = $abiLines[0].Groups['value'].Value
  if (-not [regex]::IsMatch($abiText, "^'[A-Za-z0-9_.-]+'(?:\s+'[A-Za-z0-9_.-]+')*$")) { throw 'FAIL: malformed APK native-code ABI record.' }
  $abis = @([regex]::Matches($abiText, "'(?<abi>[A-Za-z0-9_.-]+)'") | ForEach-Object { $_.Groups['abi'].Value })
  return [pscustomobject]@{ package=$packageLines[0].Groups['value'].Value; minSdk=[int]$minLines[0].Groups['value'].Value;
    targetSdk=[int]$targetLines[0].Groups['value'].Value; abis=$abis; activity=$activityLines[0].Groups['value'].Value }
}
function Get-C05EResolvedAutoTargetSdk([object]$Platforms) {
  $levels = @($Platforms | ForEach-Object { if ([string]$_ -match '^android-(?<api>\d+)$') { [int]$Matches['api'] } else { throw "FAIL: malformed accepted SDK platform entry '$_'." } })
  if (-not $levels.Count) { throw 'FAIL: Android SDK has no platform to resolve automatic targetSdk.' }
  return [int](($levels | Measure-Object -Maximum).Maximum)
}
function Get-C05EStrictInt([object]$Object, [string]$Name) {
  $property = if ($Object) { $Object.PSObject.Properties[$Name] } else { $null }
  if (-not $property -or $null -eq $property.Value) { throw "FAIL: BuildReport field '$Name' is missing or null." }
  $value = $property.Value
  if ($value -is [string] -or $value -is [bool]) { throw "FAIL: BuildReport field '$Name' has the wrong type." }
  if ($value -is [double] -or $value -is [single] -or $value -is [decimal]) {
    $asDouble = [double]$value
    if ([double]::IsNaN($asDouble) -or [double]::IsInfinity($asDouble) -or $asDouble -ne [math]::Truncate($asDouble)) {
      throw "FAIL: BuildReport field '$Name' is fractional or non-finite."
    }
  }
  try { $number = [long]$value } catch { throw "FAIL: BuildReport field '$Name' is not numeric." }
  if ($number -lt [int]::MinValue -or $number -gt [int]::MaxValue) { throw "FAIL: BuildReport field '$Name' overflows Int32." }
  return [int]$number
}
function Get-C05EStrictBool([object]$Object, [string]$Name) {
  $property = if ($Object) { $Object.PSObject.Properties[$Name] } else { $null }
  if (-not $property -or $null -eq $property.Value) { throw "FAIL: BuildReport field '$Name' is missing or null." }
  if ($property.Value -isnot [bool]) { throw "FAIL: BuildReport field '$Name' has a non-boolean type." }
  if (-not [bool]$property.Value) { throw "FAIL: BuildReport field '$Name' is not true." }
  return $true
}
function Assert-C05EBuildOutcome([object]$Report, [string]$ApkPath, [string]$Metadata, [datetime]$StartedAt, [string]$ProjectRoot, [int]$ResolvedAutoTargetSdk, [bool]$AllowCachedApk = $false) {
  if ($null -eq $Report) { throw 'FAIL: missing Unity BuildReport marker.' }
  if ((Get-C05EStrictInt $Report 'schema') -ne 2) { throw 'FAIL: Unity BuildReport schema is not 2.' }
  $minSdk = Get-C05EStrictInt $Report 'minSdkVersion'
  [void](Get-C05EStrictBool $Report 'tmpResources')
  [void](Get-C05EStrictBool $Report 'landscapeOnly')
  [void](Get-C05EStrictBool $Report 'fullscreen')
  [void](Get-C05EStrictBool $Report 'outsideSafeArea')
  if ($Report.result -ne 'Succeeded' -or (Get-C05EStrictInt $Report 'errors') -ne 0 -or $Report.target -ne 'Android' -or
      $Report.backend -ne 'IL2CPP' -or $Report.architectures -ne 'X86_64' -or $Report.variant -ne 'ANDROID_EMULATOR' -or
      $minSdk -lt 21) {
    throw 'FAIL: Unity BuildReport does not satisfy the emulator x86_64 IL2CPP full-game contract.'
  }
  foreach ($field in @('stepErrors','stepExceptions','failedAssertions','productionInputsRewritten','resolvedTargetSdk','sourceHead')) {
    if (-not $Report.PSObject.Properties[$field] -or $null -eq $Report.PSObject.Properties[$field].Value) {
      throw "FAIL: BuildReport is missing required field '$field'."
    }
  }
  if ((Get-C05EStrictInt $Report 'stepErrors') -ne 0 -or (Get-C05EStrictInt $Report 'stepExceptions') -ne 0 -or
      @($Report.failedAssertions).Count -ne 0 -or @($Report.productionInputsRewritten).Count -ne 0) {
    throw 'FAIL: BuildReport contains positive step errors/exceptions, failed assertions or rewritten inputs.'
  }
  if ((Get-C05EStrictInt $Report 'resolvedTargetSdk') -ne $ResolvedAutoTargetSdk) { throw 'FAIL: BuildReport resolvedTargetSdk differs from the SDK platform resolution.' }
  if ([string]$Report.sourceHead -notmatch '^[0-9a-fA-F]{40}$') { throw 'FAIL: BuildReport sourceHead is missing or malformed.' }
  $contract = Get-C05EProjectContract $ProjectRoot
  foreach ($scene in $script:RequiredScenes) {
    if ($contract.enabledScenes -notcontains $scene) { throw "FAIL: required production scene is not enabled in project input: $scene" }
  }
  $reported = @($Report.scenes)
  if ($reported.Count -ne $script:RequiredScenes.Count -or $contract.enabledScenes.Count -ne $script:RequiredScenes.Count) {
    throw 'FAIL: BuildReport scenes are empty or differ from the fixed production scene count.'
  }
  for ($i=0; $i -lt $reported.Count; $i++) {
    if ([string]$reported[$i] -cne [string]$script:RequiredScenes[$i] -or [string]$contract.enabledScenes[$i] -cne [string]$script:RequiredScenes[$i]) {
      throw 'FAIL: BuildReport scene order/content differs from the fixed production scene order.'
    }
  }
  if ([int]$Report.minSdkVersion -ne $contract.minSdk -or [int]$Report.targetSdkSetting -ne $contract.targetSdkSetting) {
    throw 'FAIL: BuildReport Android SDK settings differ from ProjectSettings.'
  }
  if (-not (Test-Path -LiteralPath $ApkPath -PathType Leaf)) { throw 'FAIL: fresh APK output missing.' }
  if (-not $AllowCachedApk -and (Get-Item -LiteralPath $ApkPath).LastWriteTimeUtc -lt $StartedAt.ToUniversalTime()) { throw 'FAIL: APK is stale and predates this build attempt.' }
  if ($Report.output -ne [IO.Path]::GetFullPath($ApkPath)) { throw 'FAIL: BuildReport output path mismatch.' }
  $badging = Get-C05EBadgingContract $Metadata
  $expectedTarget = if ($contract.targetSdkSetting -gt 0) { $contract.targetSdkSetting } else { $ResolvedAutoTargetSdk }
  if ($badging.package -cne [string]$Report.packageId -or $badging.abis.Count -ne 1 -or $badging.abis[0] -cne 'x86_64' -or
      $badging.minSdk -lt 21 -or $badging.minSdk -ne [int]$Report.minSdkVersion -or $badging.targetSdk -ne $expectedTarget -or
      $badging.targetSdk -lt $badging.minSdk -or [string]::IsNullOrWhiteSpace($badging.activity)) {
    throw 'FAIL: APK package, x86_64-only ABI, launchable activity or SDK metadata contradicts build/project settings.'
  }
  return $true
}
function Save-C05EJson([string]$Path, [object]$Value) { [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 12), (New-Object Text.UTF8Encoding($false))) }

# ----------------------------- deterministic fixtures -----------------------------
if ($VerdictFixtureTest) {
  $fixtureRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
  $testRoot = Join-Path $fixtureRoot ('build\c05-emulator-verdict-fixtures\' + [guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
  try {
    $apk = Join-Path $testRoot 'fixture.apk'; [IO.File]::WriteAllBytes($apk, [byte[]]@(1,2,3))
    $now = [DateTime]::UtcNow.AddSeconds(-1)
    $projectRoot = Join-Path $testRoot 'project'; $settingsDir = Join-Path $projectRoot 'ProjectSettings'
    New-Item -ItemType Directory -Force -Path $settingsDir | Out-Null
    $scenes = $script:RequiredScenes
    $sceneYaml = "EditorBuildSettings:`n  m_Scenes:`n" + (@($scenes | ForEach-Object { "  - enabled: 1`n    path: $_`n" }) -join '')
    [IO.File]::WriteAllText((Join-Path $settingsDir 'EditorBuildSettings.asset'), $sceneYaml)
    [IO.File]::WriteAllText((Join-Path $settingsDir 'ProjectSettings.asset'), "AndroidMinSdkVersion: 25`nAndroidTargetSdkVersion: 0`n")
    $report = [pscustomobject]@{ schema=2; cacheMode='CLEAN'; result='Succeeded'; errors=0; stepErrors=0; stepExceptions=0;
      stepErrorMessages=@(); failedAssertions=@(); productionInputsRewritten=@(); resolvedTargetSdk=36; sourceHead=('a'*40);
      target='Android'; backend='IL2CPP'; architectures='X86_64';
      variant='ANDROID_EMULATOR'; tmpResources=$true; minSdkVersion=25; targetSdkSetting=0; landscapeOnly=$true;
      fullscreen=$true; outsideSafeArea=$true; output=[IO.Path]::GetFullPath($apk); packageId='com.majiang.game'; scenes=$scenes }
    $metadata = "package: name='com.majiang.game' versionCode='1'`nminSdkVersion:'25'`ntargetSdkVersion:'36'`nnative-code: 'x86_64'`nlaunchable-activity: name='com.unity3d.player.UnityPlayerActivity'  label='' icon=''"
    $rejected = 0
    $caseNames = New-Object System.Collections.Generic.List[string]
    foreach ($bad in @($null, ($report | Select-Object *))) {
      if ($null -ne $bad) { $bad.result = 'Failed'; $bad.errors = 1 }
      try { Assert-C05EBuildOutcome $bad $apk $metadata $now $projectRoot 36 | Out-Null } catch { $rejected++; $caseNames.Add($(if ($null -eq $bad) { 'missing-marker' } else { 'failed-report' })) }
    }
    $cases = @(
      @{ name='backend-mono'; run={ $r=$report|Select-Object *; $r.backend='Mono'; Assert-C05EBuildOutcome $r $apk $metadata $now $projectRoot 36 } },
      @{ name='arch-arm64'; run={ $r=$report|Select-Object *; $r.architectures='ARM64'; Assert-C05EBuildOutcome $r $apk $metadata $now $projectRoot 36 } },
      @{ name='variant-wrong'; run={ $r=$report|Select-Object *; $r.variant='ANDROID_DEVICE'; Assert-C05EBuildOutcome $r $apk $metadata $now $projectRoot 36 } },
      @{ name='tmp-false'; run={ $r=$report|Select-Object *; $r.tmpResources=$false; Assert-C05EBuildOutcome $r $apk $metadata $now $projectRoot 36 } },
      @{ name='landscape-false'; run={ $r=$report|Select-Object *; $r.landscapeOnly=$false; Assert-C05EBuildOutcome $r $apk $metadata $now $projectRoot 36 } },
      @{ name='activity-missing'; run={ $m=$metadata -replace "(?m)^launchable-activity:.*\r?\n?",''; Assert-C05EBuildOutcome $report $apk $m $now $projectRoot 36 } },
      @{ name='abi-extra'; run={ $m=$metadata.Replace("native-code: 'x86_64'","native-code: 'x86_64' 'arm64-v8a'"); Assert-C05EBuildOutcome $report $apk $m $now $projectRoot 36 } },
      @{ name='abi-arm64'; run={ $m=$metadata.Replace("native-code: 'x86_64'","native-code: 'arm64-v8a'"); Assert-C05EBuildOutcome $report $apk $m $now $projectRoot 36 } },
      @{ name='abi-malformed'; run={ $m=$metadata.Replace("native-code: 'x86_64'","native-code: x86_64"); Assert-C05EBuildOutcome $report $apk $m $now $projectRoot 36 } },
      @{ name='abi-missing'; run={ $m=$metadata -replace "(?m)^native-code:.*\r?\n?",''; Assert-C05EBuildOutcome $report $apk $m $now $projectRoot 36 } },
      @{ name='abi-duplicate'; run={ $m=$metadata+"`nnative-code: 'x86_64'"; Assert-C05EBuildOutcome $report $apk $m $now $projectRoot 36 } },
      @{ name='package-mismatch'; run={ $m=$metadata.Replace("name='com.majiang.game'","name='com.other.game'"); Assert-C05EBuildOutcome $report $apk $m $now $projectRoot 36 } },
      @{ name='min-contradiction'; run={ $m=$metadata.Replace("minSdkVersion:'25'","minSdkVersion:'21'"); Assert-C05EBuildOutcome $report $apk $m $now $projectRoot 36 } },
      @{ name='target-contradiction'; run={ $m=$metadata.Replace("targetSdkVersion:'36'","targetSdkVersion:'35'"); Assert-C05EBuildOutcome $report $apk $m $now $projectRoot 36 } },
      @{ name='report-sdk-mismatch'; run={ $r=$report|Select-Object *; $r.minSdkVersion=26; Assert-C05EBuildOutcome $r $apk $metadata $now $projectRoot 36 } },
      @{ name='empty-scenes'; run={ $r=$report|Select-Object *; $r.scenes=@(); Assert-C05EBuildOutcome $r $apk $metadata $now $projectRoot 36 } },
      @{ name='missing-settlement'; run={ $r=$report|Select-Object *; $r.scenes=@($scenes|Where-Object {$_ -ne 'Assets/Scenes/Settlement.unity'}); Assert-C05EBuildOutcome $r $apk $metadata $now $projectRoot 36 } },
      @{ name='stale-apk'; run={ [IO.File]::SetLastWriteTimeUtc($apk,[DateTime]::UtcNow.AddDays(-1)); try { Assert-C05EBuildOutcome $report $apk $metadata $now $projectRoot 36 } finally { [IO.File]::SetLastWriteTimeUtc($apk,[DateTime]::UtcNow) } } },
      @{ name='output-mismatch'; run={ $r=$report|Select-Object *; $r.output=[IO.Path]::GetFullPath((Join-Path $testRoot 'other.apk')); Assert-C05EBuildOutcome $r $apk $metadata $now $projectRoot 36 } },
      @{ name='step-errors'; run={ $r=$report|Select-Object *; $r.stepErrors=1; Assert-C05EBuildOutcome $r $apk $metadata $now $projectRoot 36 } },
      @{ name='step-exceptions'; run={ $r=$report|Select-Object *; $r.stepExceptions=1; Assert-C05EBuildOutcome $r $apk $metadata $now $projectRoot 36 } },
      @{ name='failed-assertions'; run={ $r=$report|Select-Object *; $r.failedAssertions=@('CONFIRMED_FAILURE'); Assert-C05EBuildOutcome $r $apk $metadata $now $projectRoot 36 } },
      @{ name='rewritten-inputs'; run={ $r=$report|Select-Object *; $r.productionInputsRewritten=@('x'); Assert-C05EBuildOutcome $r $apk $metadata $now $projectRoot 36 } },
      @{ name='resolved-target-mismatch'; run={ $r=$report|Select-Object *; $r.resolvedTargetSdk=35; Assert-C05EBuildOutcome $r $apk $metadata $now $projectRoot 36 } }
    )
    foreach ($case in $cases) {
      try { & $case.run; throw 'case was accepted' } catch { if ($_.Exception.Message -eq 'case was accepted') { throw "negative case accepted: $($case.name)" }; $rejected++; $caseNames.Add($case.name) }
    }
    $badProject = Join-Path $testRoot 'bad-project'; New-Item -ItemType Directory -Force -Path (Join-Path $badProject 'ProjectSettings') | Out-Null
    [IO.File]::WriteAllText((Join-Path $badProject 'ProjectSettings\EditorBuildSettings.asset'), "EditorBuildSettings:`n  m_Scenes:`n  - enabled: 1`n    path: Assets/Scenes/Boot.unity`n")
    [IO.File]::WriteAllText((Join-Path $badProject 'ProjectSettings\ProjectSettings.asset'), "AndroidMinSdkVersion: 25`nAndroidTargetSdkVersion: 0`n")
    try { Assert-C05EBuildOutcome $report $apk $metadata $now $badProject 36 | Out-Null } catch { $rejected++; $caseNames.Add('project-scenes-incomplete') }
    if (-not (Assert-C05EBuildOutcome $report $apk $metadata $now $projectRoot 36)) { throw 'valid fixture artifact rejected' }
    if ($rejected -ne 27 -or $caseNames.Count -ne 27) { throw "verdict negative matrix rejected=$rejected cases=$($caseNames.Count) expected=27" }
    Write-Output 'VERDICT_FIXTURE PASS missing-marker failed-report backend-arm64-variant-tmp-orientation activity-abi-package-sdk-scene-stale-output step-errors-step-exceptions-failed-assertions-rewritten-resolved project-negative valid-accepted count=27'
    exit 0
  } finally {
    $full = [IO.Path]::GetFullPath($testRoot); $base = [IO.Path]::GetFullPath((Join-Path $fixtureRoot 'build')) + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($base,[StringComparison]::OrdinalIgnoreCase)) { throw 'unsafe verdict fixture cleanup' }
    [IO.Directory]::Delete($full,$true)
  }
}
if ($ProcessFixtureTest) {
  $child = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-Command','Start-Sleep -Seconds 90') -WindowStyle Hidden -PassThru
  try {
    $ticks = $child.StartTime.ToUniversalTime().Ticks
    $rejected = Stop-C05EOwnedProcess $child.Id ($ticks+1)
    $stillAlive = Get-Process -Id $child.Id -ErrorAction SilentlyContinue
    if ($rejected -or -not $stillAlive) { throw 'foreign/stale start-time identity was not preserved' }
    $stopped = Stop-C05EOwnedProcess $child.Id $ticks
    if (-not $stopped) { throw 'owned process exact PID+start-time did not stop' }
    Write-Output 'PROCESS_FIXTURE PASS stale-identity-preserved exact-owned-instance-stopped'
    exit 0
  } finally { if (Get-Process -Id $child.Id -ErrorAction SilentlyContinue) { [void](Stop-C05EOwnedProcess $child.Id $child.StartTime.ToUniversalTime().Ticks) } }
}
if ($OutputFixtureTest) {
  $fixtureRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
  $base = Join-Path $fixtureRoot ('build\c05-emulator-output-fixtures\' + [guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Force -Path $base | Out-Null
  try {
    [IO.File]::WriteAllBytes((Join-Path $base 'Mahjong.apk'), [byte[]]@(1,2,3))
    try { [void](Assert-C05EFreshOutputDirectory $base) } catch {
      if ($_.Exception.Message -like 'Refusing to reuse*') { Write-Output 'OUTPUT_FIXTURE PASS existing-apk-output-rejected'; exit 0 }
      throw
    }
    throw 'existing output was accepted'
  } finally {
    $full = [IO.Path]::GetFullPath($base); $allowed = [IO.Path]::GetFullPath((Join-Path $fixtureRoot 'build')) + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) { throw 'unsafe output fixture cleanup' }
    [IO.Directory]::Delete($full,$true)
  }
}
if ($SourceFixtureTest) {
  $fixtureRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
  $fixture = Join-Path $fixtureRoot ('build\c05-emulator-source-fixtures\' + [guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Force -Path $fixture | Out-Null
  try {
    & git -C $fixture init -q; if ($LASTEXITCODE -ne 0) { throw 'fixture git init failed' }
    & git -C $fixture config user.email fixture@invalid; & git -C $fixture config user.name C05Fixture
    $sceneDir = Join-Path $fixture 'unity\Mahjong.Unity\Assets\Scenes'; New-Item -ItemType Directory -Force -Path $sceneDir | Out-Null
    $menu = Join-Path $sceneDir 'Menu.unity'; $allowedFile = Join-Path $fixture 'allowed-input.txt'
    [IO.File]::WriteAllText($menu,'scene-base'); [IO.File]::WriteAllText($allowedFile,'allowed-base')
    & git -C $fixture add --all; & git -C $fixture commit -qm fixture
    $relativeScene = 'unity/Mahjong.Unity/Assets/Scenes/Menu.unity'; $allowed = @('allowed-input.txt'); $rejected = 0
    [IO.File]::WriteAllText($menu,'scene-staged'); & git -C $fixture add -- $relativeScene
    try { Get-C05ESourceBoundary $fixture $allowed | Out-Null } catch { if ($_.Exception.Message -like 'FAIL: staged/unstaged/untracked*') { $rejected++ } else { throw } }
    & git -C $fixture reset --hard -q HEAD
    [IO.File]::WriteAllText($menu,'scene-unstaged')
    try { Get-C05ESourceBoundary $fixture $allowed | Out-Null } catch { if ($_.Exception.Message -like 'FAIL: staged/unstaged/untracked*') { $rejected++ } else { throw } }
    [IO.File]::WriteAllText($menu,'scene-base'); [IO.File]::WriteAllText((Join-Path $fixture 'unexpected.txt'),'untracked')
    try { Get-C05ESourceBoundary $fixture $allowed | Out-Null } catch { if ($_.Exception.Message -like 'FAIL: staged/unstaged/untracked*') { $rejected++ } else { throw } }
    [IO.File]::Delete((Join-Path $fixture 'unexpected.txt'))
    [IO.File]::WriteAllText($allowedFile,'dirty-content')
    $result = Get-C05ESourceBoundary $fixture $allowed
    if ($rejected -ne 3 -or $result.dirtyPaths.Count -ne 1 -or $result.inputHashes['allowed-input.txt'] -ne (Get-FileHash $allowedFile -Algorithm SHA256).Hash) {
      throw 'source fixture failed to reject all states/bind permitted dirty bytes'
    }
    & git -C $fixture reset --hard -q HEAD
    $reportFolder = 'doc/v5-' + [string][char]0x91cd + [string][char]0x6784
    $deliveryRel = "$reportFolder/V5-G2-C05-ANDROID-GEMINI38-DELIVERY-20261104.md"
    $bindingRel = "$reportFolder/V5-G2-C05-ANDROID-GEMINI38-BINDING-20261104.json"
    $deliveryFile = Join-Path $fixture $deliveryRel; $bindingFile = Join-Path $fixture $bindingRel
    New-Item -ItemType Directory -Force -Path (Split-Path $deliveryFile -Parent) | Out-Null
    [IO.File]::WriteAllText((Join-Path $fixture "$reportFolder/V5-G2-C05-ANDROID-GEMINI38-DELIVERY-20261104.json"),'baseline-delivery-json')
    [IO.File]::WriteAllText((Join-Path $fixture "$reportFolder/V5-G2-C05-ANDROID-GEMINI38-BINDING-20261104.md"),'baseline-binding-md')
    & git -C $fixture add --all; & git -C $fixture commit -qm report-baseline
    [IO.File]::WriteAllText($deliveryFile,'approved-delivery-md'); & git -C $fixture add -- $deliveryRel
    [IO.File]::WriteAllText($bindingFile,'approved-binding-json')
    $reportCases = New-Object System.Collections.Generic.List[object]
    $approvedHash=@{}; $approvedHash[$deliveryRel]=(Get-FileHash $deliveryFile -Algorithm SHA256).Hash; $approvedHash[$bindingRel]=(Get-FileHash $bindingFile -Algorithm SHA256).Hash
    $savedConsoleEncoding = [Console]::OutputEncoding
    try {
      foreach ($mode in @(@{ name='gb2312'; encoding=[Text.Encoding]::GetEncoding(936) },@{ name='utf8'; encoding=(New-Object Text.UTF8Encoding($false)) })) {
        [Console]::OutputEncoding = $mode.encoding
        $approved = Get-C05ESourceBoundary $fixture $allowed
        if ($approved.inputHashes[$deliveryRel] -ne $approvedHash[$deliveryRel] -or $approved.inputHashes[$bindingRel] -ne $approvedHash[$bindingRel]) { throw "exact report paths failed $($mode.name) hash binding" }
        $reportCases.Add([pscustomobject]@{ case='staged-and-untracked-exact-paths'; environment=$mode.name; accepted=$true; deliverySha256=$approvedHash[$deliveryRel]; bindingSha256=$approvedHash[$bindingRel] })
      }
    } finally { [Console]::OutputEncoding = $savedConsoleEncoding }
    $reportCases.Add([pscustomobject]@{ path=$deliveryRel; state='staged'; accepted=$true; sha256=$approvedHash[$deliveryRel]; bytes=(Get-Item $deliveryFile).Length })
    $reportCases.Add([pscustomobject]@{ path=$bindingRel; state='untracked'; accepted=$true; sha256=$approvedHash[$bindingRel]; bytes=(Get-Item $bindingFile).Length })
    & git -C $fixture add -- $bindingRel; & git -C $fixture commit -qm exact-report-paths
    [IO.File]::WriteAllText($deliveryFile,'approved-delivery-unstaged'); [IO.File]::WriteAllText($bindingFile,'approved-binding-unstaged')
    $unstagedHashes = @{ $deliveryRel=(Get-FileHash $deliveryFile -Algorithm SHA256).Hash; $bindingRel=(Get-FileHash $bindingFile -Algorithm SHA256).Hash }
    $savedConsoleEncoding = [Console]::OutputEncoding
    try {
      foreach ($mode in @(@{ name='gb2312'; encoding=[Text.Encoding]::GetEncoding(936) },@{ name='utf8'; encoding=(New-Object Text.UTF8Encoding($false)) })) {
        [Console]::OutputEncoding = $mode.encoding
        $approvedUnstaged = Get-C05ESourceBoundary $fixture $allowed
        if ($approvedUnstaged.inputHashes[$deliveryRel] -ne $unstagedHashes[$deliveryRel] -or $approvedUnstaged.inputHashes[$bindingRel] -ne $unstagedHashes[$bindingRel]) { throw "exact unstaged report paths failed $($mode.name) hash binding" }
        $reportCases.Add([pscustomobject]@{ case='unstaged-exact-paths'; environment=$mode.name; accepted=$true; deliverySha256=$unstagedHashes[$deliveryRel]; bindingSha256=$unstagedHashes[$bindingRel] })
      }
    } finally { [Console]::OutputEncoding = $savedConsoleEncoding }
    $reportCases.Add([pscustomobject]@{ path=$deliveryRel; state='unstaged'; accepted=$true; sha256=(Get-FileHash $deliveryFile -Algorithm SHA256).Hash; bytes=(Get-Item $deliveryFile).Length })
    $reportCases.Add([pscustomobject]@{ path=$bindingRel; state='unstaged'; accepted=$true; sha256=(Get-FileHash $bindingFile -Algorithm SHA256).Hash; bytes=(Get-Item $bindingFile).Length })
    $reportRejected = 0; $reportStates = New-Object System.Collections.Generic.List[string]
    $rejectReportPath = {
      param([string]$Relative,[string]$Content,[string]$State)
      $target=Join-Path $fixture $Relative; New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
      [IO.File]::WriteAllText($target,$Content); $before=(Get-FileHash $target -Algorithm SHA256).Hash
      if ($State -eq 'staged') { & git -C $fixture add -- $Relative }
      if ($State -eq 'unstaged') { & git -C $fixture add -- $Relative; & git -C $fixture commit -qm bad-path-baseline; [IO.File]::WriteAllText($target,($Content+'-modified')) ; $before=(Get-FileHash $target -Algorithm SHA256).Hash }
      $didReject=$false; try { Get-C05ESourceBoundary $fixture $allowed | Out-Null } catch { if ($_.Exception.Message -like 'FAIL: staged/unstaged/untracked*') { $didReject=$true } else { throw } }
      if (-not $didReject) { throw "unauthorized report path accepted state=$State path=$Relative" }
      $after=(Get-FileHash $target -Algorithm SHA256).Hash
      if ($after -ne $before) { throw "guard modified rejected input state=$State path=$Relative" }
      $reportCases.Add([pscustomobject]@{ path=$Relative; state=$State; accepted=$false; sha256Before=$before; sha256After=$after; unchanged=($before -eq $after) })
      $reportStates.Add($State); $script:reportRejected++
      & git -C $fixture reset --hard -q HEAD
      if ($State -ne 'unstaged' -and (Test-Path -LiteralPath $target)) { Remove-Item -LiteralPath $target -Force }
    }
    $script:reportRejected=0
    & $rejectReportPath 'doc/other/V5-G2-C05-ANDROID-GEMINI38-DELIVERY-20261104.md' 'wrong-directory' 'staged'
    & $rejectReportPath "$reportFolder/V5-G2-C05-ANDROID-GEMINI38-DELIVERY-20261104.json" 'wrong-extension' 'unstaged'
    & $rejectReportPath "$reportFolder/V5-G2-C05-ANDROID-GEMINI38-BINDING-20261104.md" 'wrong-extension' 'untracked'
    & $rejectReportPath "$reportFolder/UNRELATED-NOTE.md" 'unrelated-doc' 'untracked'
    $reportRejected=$script:reportRejected
    if ($reportRejected -ne 4 -or ($reportStates -join ',') -ne 'staged,unstaged,untracked,untracked') { throw "exact report allowlist matrix rejected=$reportRejected states=$($reportStates -join ',')" }
    $nonGit = Join-Path $fixture 'missing-repo-path'; $gitFailed = $false
    try { Get-C05ESourceBoundary $nonGit $allowed | Out-Null } catch { $gitFailed = $true }
    if (-not $gitFailed) { throw 'git native failure was not rejected' }
    $nativeGitFailureRejected=$false
    try { [void](Invoke-C05EGit $fixture @('rev-parse','C05-NO-SUCH-REVISION')) } catch { if ($_.Exception.Message -match 'exited 128') { $nativeGitFailureRejected=$true } else { throw } }
    if (-not $nativeGitFailureRejected) { throw 'nonzero Git native result was treated as an empty successful path list' }
    $reportCases.Add([pscustomobject]@{ case='git-native-nonzero'; expected='REJECT'; actual='REJECT'; emptyListAccepted=$false })
    Write-Output ("SOURCE_FIXTURE PASS exact-two-report-paths both-console-encodings-native-failure-rejected wrong-dir-extension-unrelated rejected count={0} cases={1}" -f $reportRejected, (ConvertTo-Json -InputObject $reportCases.ToArray() -Compress -Depth 5))
    exit 0
  } finally {
    $full = [IO.Path]::GetFullPath($fixture); $base = [IO.Path]::GetFullPath((Join-Path $fixtureRoot 'build')) + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($base,[StringComparison]::OrdinalIgnoreCase)) { throw 'unsafe source fixture cleanup' }
    Get-ChildItem -LiteralPath $full -Force -Recurse -ErrorAction SilentlyContinue | ForEach-Object { $_.Attributes = [IO.FileAttributes]::Normal }
    [IO.Directory]::Delete($full,$true)
  }
}
if ($ArgumentFixtureTest) {
  $fixtureRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
  $dir = Join-Path $fixtureRoot ('build\c05-emulator-argv-fixtures\' + [guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  try {
    $script:stagingFixtureCase='setup'
    $exe = Join-Path $dir 'argv recorder.exe'; $record = Join-Path $dir 'args.json'; $env:C05E_ARGV_RECORD = $record
    $source = 'using System;using System.IO;using System.Linq;using System.Text;public static class R{public static int Main(){var a=Environment.GetCommandLineArgs().Skip(1).Select(x=>Convert.ToBase64String(Encoding.UTF8.GetBytes(x))).ToArray();File.WriteAllLines(Environment.GetEnvironmentVariable("C05E_ARGV_RECORD"),a);return 0;}}'
    $sourceFile = Join-Path $dir 'Recorder.cs'; [IO.File]::WriteAllText($sourceFile, $source, (New-Object Text.UTF8Encoding($false)))
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path -LiteralPath $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
    $compile = Invoke-C05ENative $csc @('/nologo','/target:exe',('/out:' + $exe),$sourceFile) 120
    if ($compile.exit -ne 0 -or -not (Test-Path -LiteralPath $exe)) { throw "argv fixture compile failed exit=$($compile.exit): $($compile.stderr)" }
    $expected = @('ordinary','D:\fixture space\Mahjong.apk','quote"inside','C:\tail\')
    $child = Start-C05EHiddenProcess $exe $expected $dir
    if (-not $child.WaitForExit(10000)) { [void](Stop-C05EOwnedProcess $child.Id $child.StartTime.ToUniversalTime().Ticks); throw 'argv fixture timed out' }
    if (-not (Test-Path $record)) { throw "argv fixture recorder did not run exit=$($child.ExitCode)" }
    $actual = @(Get-Content $record | ForEach-Object { [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($_)) })
    if ($child.ExitCode -ne 0 -or $actual.Count -ne $expected.Count) { throw "argv fixture count/exit mismatch exit=$($child.ExitCode) count=$($actual.Count)" }
    for ($i=0; $i -lt $expected.Count; $i++) { if ($actual[$i] -cne $expected[$i]) { throw "argv fixture mismatch at $i actual=[$($actual[$i])] expected=[$($expected[$i])]" } }
    Write-Output 'ARGUMENT_FIXTURE PASS spaces-quotes-trailing-separator-preserved'
    exit 0
  } finally {
    Remove-Item Env:C05E_ARGV_RECORD -ErrorAction SilentlyContinue
    $full = [IO.Path]::GetFullPath($dir); $base = [IO.Path]::GetFullPath((Join-Path $fixtureRoot 'build')) + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($base,[StringComparison]::OrdinalIgnoreCase)) { throw 'unsafe argv fixture cleanup' }
    [IO.Directory]::Delete($full,$true)
  }
}

if ($StagingGuardFixtureTest) {
  $fixtureRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
  $dir = Join-Path $fixtureRoot ('build\c05-staging-guard-fixtures\' + [guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  try {
    $script:C05ERepoRoot = $fixtureRoot
    $script:C05EAllowedStagingRoot = [IO.Path]::GetFullPath((Join-Path $fixtureRoot 'build\c05-emulator-staging'))
    $script:C05EShortStagingRoot = Join-Path $dir 'short-stage'
    New-Item -ItemType Directory -Force -Path $script:C05EShortStagingRoot | Out-Null
    $allowed = Join-Path $script:C05EAllowedStagingRoot ('owned-' + [guid]::NewGuid().ToString('N') + '\project')
    if (-not (Test-C05EStagingDestination $allowed)) { throw 'valid owned staging destination rejected' }
    $rejected = 0; $stagingCases = New-Object System.Collections.Generic.List[object]
    foreach ($bad in @('C:\c05-unapproved-target', $fixtureRoot, (Join-Path $fixtureRoot 'unity\Mahjong.Unity'), $script:C05EAllowedStagingRoot)) {
      if (Test-C05EStagingDestination $bad) { throw "foreign staging destination accepted: $bad" }
      $stagingCases.Add([pscustomobject]@{ path=$bad; expected='REJECT'; actual='REJECT' })
      $rejected++
    }
    $src = Join-Path $dir 'source'; $dst = Join-Path $dir 'foreign-project'
    New-Item -ItemType Directory -Force -Path (Join-Path $src 'Assets') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $dst 'Assets') | Out-Null
    [IO.File]::WriteAllText((Join-Path $src 'Assets\source.txt'), 'fixture-source')
    $sentinel = Join-Path $dst 'Assets\foreign-sentinel.txt'; [IO.File]::WriteAllText($sentinel, 'PRESERVE')
    $syncResult = Sync-C05EStagingAuthored $src $dst
    if ($syncResult -ne $false -or -not (Test-Path -LiteralPath $sentinel)) { throw 'foreign mirror destination was modified or not rejected' }
    $stagingCases.Add([pscustomobject]@{ path=$dst; operation='sync'; expected='REJECT'; actual='REJECT'; sentinelPreserved=$true })
    $rejected++
    $shortGuid = [guid]::NewGuid().ToString('N'); $shortCandidate = Join-Path $script:C05EShortStagingRoot $shortGuid
    $script:stagingFixtureCase='fresh-short-guid'
    $resolvedShort = Resolve-C05EStagingRoot $fixtureRoot $shortCandidate $false
    if ($resolvedShort -ne [IO.Path]::GetFullPath($shortCandidate) -or -not (Test-C05EStagingDestination (Join-Path $resolvedShort 'project'))) { throw 'fresh short GUID root was not accepted' }
    $stagingCases.Add([pscustomobject]@{ path=$shortCandidate; reuse=$false; expected='ACCEPT_FRESH_GUID'; actual='ACCEPT'; ownerRoot=$script:C05EAllowedStagingRoot })
    if (Test-C05EStagingDestination (Join-Path (Join-Path $script:C05EShortStagingRoot ([guid]::NewGuid().ToString('N'))) 'project')) { throw 'sibling external GUID root accepted by current owner boundary' }
    $stagingCases.Add([pscustomobject]@{ path='sibling-short-GUID'; expected='REJECT_OWNER_MISMATCH'; actual='REJECT' })
    $rejected++
    $shortAccepted = 1
    $invalidShort = @(
      $script:C05EShortStagingRoot,
      'D:\',
      ($script:C05EShortStagingRoot + '-adjacent\' + [guid]::NewGuid().ToString('N')),
      (Join-Path $script:C05EShortStagingRoot 'not-a-guid'),
      (Join-Path $shortCandidate 'child'),
      (Join-Path $script:C05EShortStagingRoot ('..\escape-' + [guid]::NewGuid().ToString('N'))),
      $fixtureRoot,
      (Join-Path $fixtureRoot 'unity\Mahjong.Unity')
    )
    foreach ($badShort in $invalidShort) {
      $script:stagingFixtureCase="invalid-short:$badShort"
      $rejected++
      $reason=$null
      try { [void](Resolve-C05EStagingRoot $fixtureRoot $badShort $false); throw "invalid short root accepted: $badShort" } catch { if ($_.Exception.Message -like 'invalid short root accepted:*') { throw }; $reason=$_.Exception.Message }
      $stagingCases.Add([pscustomobject]@{ path=$badShort; reuse=$false; expected='REJECT'; actual='REJECT'; reason=$reason })
    }
    $script:stagingFixtureCase='external-reuse'
    $reuseReason=$null; try { [void](Resolve-C05EStagingRoot $fixtureRoot $shortCandidate $true); throw 'external reuse accepted' } catch { if ($_.Exception.Message -eq 'external reuse accepted') { throw }; $reuseReason=$_.Exception.Message }
    $stagingCases.Add([pscustomobject]@{ path=$shortCandidate; reuse=$true; expected='REJECT_EXTERNAL_REUSE'; actual='REJECT'; reason=$reuseReason })
    $rejected++
    $existingShort = Join-Path $script:C05EShortStagingRoot ([guid]::NewGuid().ToString('N')); New-Item -ItemType Directory -Path $existingShort | Out-Null
    $sentinel = Join-Path $existingShort 'sentinel.bin'; [IO.File]::WriteAllBytes($sentinel,[byte[]]@(9,8,7,6)); $sentinelHash=(Get-FileHash $sentinel -Algorithm SHA256).Hash
    [IO.File]::WriteAllText((Join-Path $existingShort '.c05-owned-staging.json'),'{"owner":"FOREIGN"}')
    [IO.File]::WriteAllText((Join-Path $existingShort '.c05-active-writer.json'),'{"pid":999999,"startUtcTicks":1}')
    foreach ($reuse in @($false,$true)) {
      $script:stagingFixtureCase="existing-external-reuse:$reuse"
      $beforeHash=(Get-FileHash $sentinel -Algorithm SHA256).Hash; $reason=$null
      try { [void](Resolve-C05EStagingRoot $fixtureRoot $existingShort $reuse); throw "pre-existing external root accepted reuse=$reuse" } catch { if ($_.Exception.Message -like 'pre-existing external root accepted*') { throw }; $reason=$_.Exception.Message }
      $rejected++
      if ((Get-FileHash $sentinel -Algorithm SHA256).Hash -ne $sentinelHash) { throw 'pre-existing external sentinel changed' }
      $afterHash=(Get-FileHash $sentinel -Algorithm SHA256).Hash
      $stagingCases.Add([pscustomobject]@{ path=$existingShort; reuse=$reuse; expected='REJECT_PREEXISTING'; actual='REJECT'; reason=$reason; sentinelSha256Before=$beforeHash; sentinelSha256After=$afterHash; unchanged=($beforeHash -eq $afterHash) })
    }
    $script:stagingFixtureCase='reparse-short-root'
    $reparseBase='D:\game_dev_tools\unity\Editor\Data\PlaybackEngines\AndroidPlayer\OpenJDK'
    $reparseInfo=Get-Item -LiteralPath $reparseBase -Force -ErrorAction Stop
    if (($reparseInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) { throw 'known read-only Unity OpenJDK path is no longer a reparse point' }
    $previousShortRoot=$script:C05EShortStagingRoot; $script:C05EShortStagingRoot=$reparseBase
    $reparsePath=Join-Path $reparseBase ([guid]::NewGuid().ToString('N')); $reparseReason=$null
    try { [void](Resolve-C05EStagingRoot $fixtureRoot $reparsePath $false); throw 'reparse short root accepted' } catch { if ($_.Exception.Message -eq 'reparse short root accepted') { throw }; $reparseReason=$_.Exception.Message } finally { $script:C05EShortStagingRoot=$previousShortRoot }
    if ($reparseReason -notlike '*reparse point*') { throw "reparse short root did not fail on reparse guard: $reparseReason" }
    $stagingCases.Add([pscustomobject]@{ path=$reparsePath; expected='REJECT_REPARSE'; actual='REJECT'; reason=$reparseReason; createdOrWritten=$false })
    $rejected++
    $script:stagingFixtureCase='repo-reuse'
    $repoBase=[IO.Path]::GetFullPath((Join-Path $fixtureRoot 'build\c05-emulator-staging'))
    $repoReuse=Join-Path $repoBase ('owned-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $repoReuse | Out-Null
    if ((Resolve-C05EStagingRoot $fixtureRoot $repoReuse $true) -ne [IO.Path]::GetFullPath($repoReuse)) { throw 'repository reuse behavior changed' }
    $stagingCases.Add([pscustomobject]@{ path=$repoReuse; reuse=$true; expected='ACCEPT_REPOSITORY_REUSE'; actual='ACCEPT' })
    if ($shortAccepted -ne 1 -or $rejected -lt 16) { throw "short staging matrix accepted=$shortAccepted rejected=$rejected" }
    Write-Output ("STAGING_GUARD_FIXTURE PASS repo-default-preserved short-fresh-guid-accepted rejected count={0} cases={1}" -f $rejected,(ConvertTo-Json -InputObject $stagingCases.ToArray() -Compress -Depth 5))
    exit 0
  } catch {
    Write-Output "STAGING_GUARD_CASE_FAIL case=$script:stagingFixtureCase error=$($_.Exception.Message)"
    throw
  } finally {
    if (Test-Path -LiteralPath $dir) {
      $full = [IO.Path]::GetFullPath($dir); $base = [IO.Path]::GetFullPath((Join-Path $fixtureRoot 'build')) + [IO.Path]::DirectorySeparatorChar
      if (-not $full.StartsWith($base,[StringComparison]::OrdinalIgnoreCase)) { throw 'unsafe staging guard fixture cleanup' }
      [IO.Directory]::Delete($full,$true)
    }
  }
}
if ($PathBudgetFixtureTest) {
  $fixtureRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
  $dir = Join-Path $fixtureRoot ('build\b\' + [guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  try {
    $expectedRoot = 'D:\c05s\' + [guid]::NewGuid().ToString('N') + '\project'
    $pathCases=New-Object System.Collections.Generic.List[object]; $accepted=0; $rejected=0
    $project = Join-Path $dir 'single-project'; $cache = Join-Path $project 'Library\PackageCache\com.fixture.registry@0123456789ab'
    New-Item -ItemType Directory -Force -Path $cache,(Join-Path $project 'Packages') | Out-Null
    $singleLock='{"dependencies":{"com.fixture.registry":{"version":"1.0.0","source":"registry"}}}'
    [IO.File]::WriteAllText((Join-Path $project 'Packages\packages-lock.json'),$singleLock,(New-Object Text.UTF8Encoding($false)))
    $consumerFile = Join-Path $cache 'Readable.cs'; [IO.File]::WriteAllText($consumerFile,'class Fixture {}')
    [IO.File]::WriteAllText((Join-Path $cache 'package.json'),'{"name":"com.fixture.registry","version":"1.0.0"}',(New-Object Text.UTF8Encoding($false)))
    $valid = Get-C05EPathBudgetAudit $project 'C:\Unity\Editor\Unity.exe' @() $expectedRoot
    if ($valid.status -ne 'PASS' -or -not $valid.completeInventory -or -not [IO.File]::Exists($consumerFile) -or -not $valid.packageInventory[0].inventorySha256) { throw "short ordinary consumer path rejected: $($valid|ConvertTo-Json -Compress -Depth 5)" }
    $pathCases.Add([pscustomobject]@{ case='matching-package-metadata-and-hashed-file-inventory'; expected='PASS'; actual=$valid.status; completeInventory=$valid.completeInventory; ordinaryConsumerFileExists=[IO.File]::Exists($consumerFile); inventory=$valid.packageInventory; packageLockSha256=$valid.packageLockSha256 })
    $accepted++
    $longProjected = Join-Path (Join-Path $expectedRoot 'Library\PackageCache\com.fixture.registry@0123456789ab') ((('segment-' + ('x' * 180)) + '.asset'))
    $longAudit = Get-C05EPathBudgetAudit $project 'C:\Unity\Editor\Unity.exe' @($longProjected) $expectedRoot
    if ($longAudit.status -ne 'FAIL_PATH_BUDGET' -or $longAudit.overLimit.Count -eq 0) { throw 'over-limit prospective consumer path was not rejected' }
    $pathCases.Add([pscustomobject]@{ case='projected-over-limit-consumer-path'; expected='FAIL_PATH_BUDGET'; actual=$longAudit.status; completeInventory=$longAudit.completeInventory; overLimit=$longAudit.overLimit; maxPathChars=$longAudit.maxPathChars })
    $rejected++
    foreach ($case in @(@{name='missing-package-json'; json=$null},@{name='wrong-package-name'; json='{"name":"com.wrong.package","version":"1.0.0"}'},@{name='wrong-package-version'; json='{"name":"com.fixture.registry","version":"99.0.0"}'})) {
      $badProject = Join-Path $dir $case.name; $badCache = Join-Path $badProject 'Library\PackageCache\com.fixture.registry@0123456789ab'
      New-Item -ItemType Directory -Force -Path $badCache,(Join-Path $badProject 'Packages') | Out-Null
      [IO.File]::WriteAllText((Join-Path $badProject 'Packages\packages-lock.json'),$singleLock,(New-Object Text.UTF8Encoding($false)))
      [IO.File]::WriteAllText((Join-Path $badCache 'marker.txt'),'marker')
      if ($null -ne $case.json) { [IO.File]::WriteAllText((Join-Path $badCache 'package.json'),$case.json,(New-Object Text.UTF8Encoding($false))) }
      $audit = Get-C05EPathBudgetAudit $badProject 'C:\Unity\Editor\Unity.exe' @() $expectedRoot
      if ($audit.status -ne 'INCOMPLETE_INVENTORY' -or $audit.completeInventory) { throw "$($case.name) was treated as complete: $($audit|ConvertTo-Json -Compress -Depth 5)" }
      $pathCases.Add([pscustomobject]@{ case=$case.name; expected='INCOMPLETE_INVENTORY'; actual=$audit.status; completeInventory=$audit.completeInventory; packagesMissing=$audit.packagesMissing })
      $rejected++
    }
    $missingProject = Join-Path $dir 'missing-cache-project'; New-Item -ItemType Directory -Force -Path (Join-Path $missingProject 'Packages') | Out-Null
    [IO.File]::WriteAllText((Join-Path $missingProject 'Packages\packages-lock.json'),$singleLock,(New-Object Text.UTF8Encoding($false)))
    $missing = Get-C05EPathBudgetAudit $missingProject 'C:\Unity\Editor\Unity.exe' @() $expectedRoot
    if ($missing.status -ne 'INCOMPLETE_INVENTORY' -or $missing.completeInventory -or -not ($missing.packagesMissing -match 'registry-package-inventory')) { throw 'absent first-import PackageCache was treated as complete' }
    $pathCases.Add([pscustomobject]@{ case='first-import-packagecache-absent'; expected='INCOMPLETE_INVENTORY'; actual=$missing.status; completeInventory=$missing.completeInventory; packagesMissing=$missing.packagesMissing })
    $rejected++
    $liveLock = [IO.Path]::GetFullPath((Join-Path $fixtureRoot 'unity\Mahjong.Unity\Packages\packages-lock.json'))
    $live = Get-Content -Encoding UTF8 -LiteralPath $liveLock -Raw | ConvertFrom-Json
    $registry = @($live.dependencies.PSObject.Properties | Where-Object { [string]$_.Value.source -eq 'registry' })
    if ($registry.Count -lt 1) { throw "current packages-lock has no registry dependencies: $($registry.Count)" }
    $registryLock = [ordered]@{ dependencies=[ordered]@{} }
    foreach ($dependency in $registry) { $registryLock.dependencies[$dependency.Name]=$dependency.Value }
    $lockText = $registryLock | ConvertTo-Json -Depth 12
    $sourceProject = Join-Path $dir 's'; $targetProject = Join-Path $dir 't'
    foreach ($candidateProject in @($sourceProject,$targetProject)) { New-Item -ItemType Directory -Force -Path (Join-Path $candidateProject 'Packages') | Out-Null; [IO.File]::WriteAllText((Join-Path $candidateProject 'Packages\packages-lock.json'),$lockText,(New-Object Text.UTF8Encoding($false))) }
    $sourceCache = Join-Path $sourceProject 'Library\PackageCache'; New-Item -ItemType Directory -Force -Path $sourceCache | Out-Null
    foreach ($dependency in $registry) {
      $packageRoot = Join-Path $sourceCache ($dependency.Name + '@0123456789ab'); New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
      $manifest = [ordered]@{ name=$dependency.Name; version=[string]$dependency.Value.version }
      [IO.File]::WriteAllText((Join-Path $packageRoot 'package.json'),($manifest|ConvertTo-Json -Compress),(New-Object Text.UTF8Encoding($false)))
      [IO.File]::WriteAllText((Join-Path $packageRoot 'Fixture.txt'),('fixture:' + $dependency.Name),(New-Object Text.UTF8Encoding($false)))
    }
    $script:C05EActiveRun = Join-Path $dir 'evidence'; New-Item -ItemType Directory -Force -Path $script:C05EActiveRun | Out-Null
    $prepared = Copy-C05EVerifiedRegistryCache $sourceCache $targetProject
    $preparedAudit = Get-C05EPathBudgetAudit $targetProject 'C:\Unity\Editor\Unity.exe' @() $expectedRoot
    if ($prepared.packageCount -ne $registry.Count -or $preparedAudit.status -ne 'PASS' -or $preparedAudit.packageInventory.Count -ne $registry.Count -or -not $preparedAudit.completeInventory) { throw "current lock-package preparation failed: $($preparedAudit|ConvertTo-Json -Compress -Depth 5)" }
    $copyRows = New-Object System.Collections.Generic.List[object]
    foreach ($package in $preparedAudit.packageInventory) {
      $readable = [IO.File]::Exists((Join-Path $package.root 'Fixture.txt'))
      if (-not $readable) { throw "ordinary package consumer could not read copied package $($package.name)" }
      $copyRows.Add([pscustomobject]@{ name=$package.name; version=$package.version; fileCount=$package.fileCount; inventorySha256=$package.inventorySha256; ordinaryConsumerReadable=$readable })
    }
    $copyNativeCodes=@($prepared.packages | ForEach-Object { [int]$_.robocopyNative })
    if ($copyNativeCodes.Count -ne $registry.Count -or @($copyNativeCodes | Where-Object { $_ -lt 0 -or $_ -ge 8 }).Count) { throw "registry copy native result matches the current registry denominator and is in range: $($copyNativeCodes -join ',')" }
    $pathCases.Add([pscustomobject]@{ case='current-lock-19-registry-package-fresh-copy-fixture'; expected='PASS'; actual=$preparedAudit.status; lockRegistryCount=$registry.Count; copiedPackageCount=$prepared.packageCount; copyNativeCodes=$copyNativeCodes; inventory=$copyRows.ToArray(); packageLockSha256=$preparedAudit.packageLockSha256 })
    $accepted++
    $sentinelPackage = [string]$registry[0].Name; $sentinelVersion = [string]$registry[0].Value.version
    $cacheSentinel=Join-Path $targetProject ("Library\PackageCache\{0}@0123456789ab\Fixture.txt" -f $sentinelPackage); $cacheSentinelHash=(Get-FileHash $cacheSentinel -Algorithm SHA256).Hash
    $mergeRejected=$false; try { [void](Copy-C05EVerifiedRegistryCache $sourceCache $targetProject) } catch { if ($_.Exception.Message -like '*refusing to merge*') { $mergeRejected=$true } else { throw } }
    if (-not $mergeRejected -or (Get-FileHash $cacheSentinel -Algorithm SHA256).Hash -ne $cacheSentinelHash) { throw 'preexisting staging PackageCache was merged or modified' }
    $pathCases.Add([pscustomobject]@{ case='preexisting-staging-packagecache'; expected='REJECT_UNCHANGED'; actual='REJECT'; sentinelSha256Before=$cacheSentinelHash; sentinelSha256After=(Get-FileHash $cacheSentinel -Algorithm SHA256).Hash })
    $rejected++
    if ($accepted -ne 2 -or $rejected -ne 6) { throw "path budget counts accepted=$accepted rejected=$rejected expected=2/6" }
    Write-Output ("PATH_BUDGET_FIXTURE PASS matching-metadata-hashed-copy-19-lock-packages missing-metadata-name-version-absent-preexisting-and-overlimit-rejected count={0} cases={1}" -f $pathCases.Count,(ConvertTo-Json -InputObject $pathCases.ToArray() -Compress -Depth 7))
    exit 0
  } finally {
    if (Test-Path -LiteralPath $dir) {
      $full=[IO.Path]::GetFullPath($dir); $base=[IO.Path]::GetFullPath((Join-Path $fixtureRoot 'build'))+[IO.Path]::DirectorySeparatorChar
      if (-not $full.StartsWith($base,[StringComparison]::OrdinalIgnoreCase)) { throw 'unsafe path budget fixture cleanup' }
      [IO.Directory]::Delete($full,$true)
    }
  }
}

# ----------------------------- real emulator-variant build -----------------------------
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$sourceProject = Join-Path $root 'unity\Mahjong.Unity'
$project = $null
$unity = [IO.Path]::GetFullPath($UnityPath)
if (-not (Test-Path -LiteralPath $unity -PathType Leaf)) { throw "BLOCKED: Unity Editor missing: $unity" }
$editorData = Split-Path (Split-Path $unity -Parent) -Parent
$versionFile = Join-Path $editorData 'Editor\Data\Resources\UnityVersion.txt'
$unityVersion = if (Test-Path $versionFile) { (Get-Content $versionFile -Raw).Trim() } else { 'UNKNOWN' }
$androidPlayer = Join-Path (Split-Path $unity -Parent) 'Data\PlaybackEngines\AndroidPlayer'
foreach ($required in @('OpenJDK\bin\java.exe','SDK\platform-tools\adb.exe','NDK','Variations\il2cpp\Release\Libs\x86_64\libunity.so','Variations\il2cpp\Release\Libs\x86_64\libmain.so')) {
  if (-not (Test-Path -LiteralPath (Join-Path $androidPlayer $required))) { throw "BLOCKED: Unity AndroidPlayer component missing: $required" }
}
if (-not $OutputRoot) { $OutputRoot = Join-Path $root ('build\c05-android-emulator-apk\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8)) }
$run = Assert-C05EFreshOutputDirectory $OutputRoot
$script:C05EShortStagingRoot = [IO.Path]::GetFullPath('D:\c05s')
if (-not $StagingRoot) {
  # An automatically managed staging lives at a fixed short name so this call can
  # reuse it on the next run. The name must stay short: the JDK file API limits
  # paths to 260 characters, and launcher/build/intermediates/desugar_graph/...
  # already consumes most of that budget inside the staging tree.
  $StagingRoot = Join-Path $root 'build\c05-emulator-staging\a'
}
$guardRoot = if ($root) { $root } else { [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..')) }
$script:C05ERepoRoot = $guardRoot
if (-not $PSBoundParameters.ContainsKey('StagingRoot')) {
  # Caller did not name a staging: pick reuse vs. cold from what is on disk.
  $autoStaging = [IO.Path]::GetFullPath($StagingRoot)
  $autoProject = Join-Path $autoStaging 'project'
  $autoMarker = Join-Path $autoStaging '.c05-owned-staging.json'
  $autoDigest = Join-Path $autoStaging 'staging-source-closure.sha256'
  $autoIl2cpp = Join-Path $autoProject 'Library\Bee\Android\Prj\IL2CPP'
  $autoHasBuild = (Test-Path -LiteralPath $autoMarker -PathType Leaf) -and
                  (Test-Path -LiteralPath $autoDigest -PathType Leaf) -and
                  (Test-Path -LiteralPath $autoProject -PathType Container) -and
                  (Test-Path -LiteralPath $autoIl2cpp -PathType Container)
  if ($autoHasBuild) {
    $ReuseStaging = [switch]$true
    $ReuseBuildCache = [switch]$true
    Write-Host ("AUTO staging: reusing '{0}' (IL2CPP outputs present); pass -ReuseStaging:`$false for a cold build." -f $autoStaging)
  } else {
    if (Test-Path -LiteralPath $autoStaging) {
      # A staging without usable IL2CPP output is the residue of an interrupted
      # run. It cannot be reused, and a cold build refuses to start on top of an
      # existing staging, so drop it here instead of failing on the next guard.
      Write-Host ("AUTO staging: '{0}' exists without usable IL2CPP outputs; discarding it for a cold build." -f $autoStaging)
      $emptyForPurge = Join-Path $autoStaging '..\_purge-empty'
      New-Item -ItemType Directory -Force -Path $emptyForPurge | Out-Null
      & robocopy $emptyForPurge $autoStaging /MIR /NFL /NDL /NJH /NJS /NP /R:0 /W:0 2>&1 | Out-Null
      Remove-Item -LiteralPath $emptyForPurge -Recurse -Force -ErrorAction SilentlyContinue
      Remove-Item -LiteralPath $autoStaging -Recurse -Force -ErrorAction SilentlyContinue
    } else {
      Write-Host ("AUTO staging: '{0}' absent; building cold." -f $autoStaging)
    }
  }
}
$stagingRoot = Resolve-C05EStagingRoot $guardRoot $StagingRoot ([bool]$ReuseStaging)
if (-not (Test-C05ENoReparse $stagingRoot)) { throw "BLOCKED: StagingRoot '$stagingRoot' traverses a reparse/junction point." }
if (-not (Test-Path -LiteralPath $run)) { [void](Assert-C05EBuildDestination $run 'OutputRoot') }
if ((Test-Path -LiteralPath $stagingRoot) -and -not $ReuseStaging) { throw "BLOCKED: staging root already exists and must not be reused without -ReuseStaging: $stagingRoot" }
$stagingReused = [bool]($ReuseStaging -and (Test-Path -LiteralPath $stagingRoot))
$stagingSourceChanged = $false
if ($stagingReused) {
  $ownerMarker = Join-Path $stagingRoot '.c05-owned-staging.json'
  if (-not (Test-Path -LiteralPath $ownerMarker -PathType Leaf)) { throw "BLOCKED: reused staging '$stagingRoot' has no ownership marker." }
  $owner = Get-Content $ownerMarker -Raw | ConvertFrom-Json
  if ([string]$owner.owner -cne 'DSH-C05') { throw 'BLOCKED: reused staging ownership marker belongs to another owner.' }
  $writerPath = Join-Path $stagingRoot '.c05-active-writer.json'
  if (Test-Path -LiteralPath $writerPath -PathType Leaf) {
    $writer = Get-Content $writerPath -Raw | ConvertFrom-Json
    $wp = Get-Process -Id ([int]$writer.pid) -ErrorAction SilentlyContinue
    if ($wp -and $wp.StartTime.ToUniversalTime().Ticks -eq [long]$writer.startUtcTicks) { throw 'BLOCKED_RESOURCE_BUSY: reused staging still has an active writer.' }
  }
  # The path budget runs before Unity, but the AGP transform cache is redirected
  # only once the Gradle project is generated (C05EmulatorGradleBuildDirRedirect).
  # A staging built before that redirect still holds .transforms/<hex>/... at 262
  # characters, which the budget would reject. Gradle regenerates this module
  # output anyway, so drop it here; robocopy (not Remove-Item) is required because
  # those paths are already past the Win32 limit.
  $staleModuleBuild = Join-Path $stagingRoot 'project\Library\Bee\Android\Prj\IL2CPP\Gradle\unityLibrary\build'
  if (Test-Path -LiteralPath $staleModuleBuild -PathType Container) {
    $purgeEmpty = Join-Path $run 'purge-empty'
    New-Item -ItemType Directory -Force -Path $purgeEmpty | Out-Null
    & robocopy $purgeEmpty $staleModuleBuild /MIR /NFL /NDL /NJH /NJS /NP /R:0 /W:0 2>&1 | Out-Null
    Remove-Item -LiteralPath $staleModuleBuild -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $purgeEmpty -Recurse -Force -ErrorAction SilentlyContinue
  }
}
New-Item -ItemType Directory -Force -Path $run | Out-Null
$script:C05EActiveRun = $run
$project = Join-Path $stagingRoot 'project'
$apk = Join-Path $run 'Mahjong-emulator-x86_64.apk'
$marker = Join-Path $run 'build-report.json'
$editorLog = Join-Path $run 'build-report-detail.json'
$unityLog = Join-Path $run 'unity.log'
$sourceProjectSettingsPath = Join-Path $sourceProject 'ProjectSettings\ProjectSettings.asset'
$sourcePluginDir = Join-Path $sourceProject 'Assets\Plugins\Mahjong'
$sourcePluginBeforeHashes = @{}
Get-ChildItem -LiteralPath $sourcePluginDir -File | ForEach-Object { $sourcePluginBeforeHashes[$_.Name] = (Get-FileHash $_.FullName -Algorithm SHA256).Hash }

$head = $null; $native = $null; $held = $false; $mutex = $null; $sharedBuilt = $false; $oldTemp = $env:TEMP; $oldTmp = $env:TMP
$oldJava = $env:JAVA_HOME; $oldHome = $env:ANDROID_HOME; $oldRoot = $env:ANDROID_SDK_ROOT; $oldAvd = $env:ANDROID_AVD_HOME
$oldCli = $env:ANDROID_CLI_BIN; $oldPath = $env:PATH
$sourceState = $null
try {
  # Environment identity is probed before any expensive work.
  $probe = [ordered]@{}
  $probe.unity = [pscustomobject]@{ file=$unity; productVersion=(Get-Item $unity).VersionInfo.ProductVersion; sha256=(Get-FileHash $unity -Algorithm SHA256).Hash }
  $probe.java = Invoke-C05EToFiles (Join-Path $androidPlayer 'OpenJDK\bin\java.exe') @('-version') 60 (Join-Path $run 'probe-java.out') (Join-Path $run 'probe-java.err')
  $probe.adb = Invoke-C05EToFiles (Join-Path $AndroidSdkRoot 'platform-tools\adb.exe') @('version') 60 (Join-Path $run 'probe-adb.out') (Join-Path $run 'probe-adb.err')
  $probe.aapt2 = Invoke-C05EToFiles (Join-Path $AndroidSdkRoot 'build-tools\36.0.0\aapt2.exe') @('version') 60 (Join-Path $run 'probe-aapt2.out') (Join-Path $run 'probe-aapt2.err')
  $probe.emulator = Invoke-C05EToFiles (Join-Path $AndroidSdkRoot 'emulator\emulator.exe') @('-version') 60 (Join-Path $run 'probe-emulator.out') (Join-Path $run 'probe-emulator.err')
  $probe.accel = Invoke-C05EToFiles (Join-Path $AndroidSdkRoot 'emulator\emulator.exe') @('-accel-check') 120 (Join-Path $run 'probe-accel.out') (Join-Path $run 'probe-accel.err')
  $probe.avdList = Invoke-C05EToFiles (Join-Path $AndroidSdkRoot 'emulator\emulator.exe') @('-list-avds') 60 (Join-Path $run 'probe-avd-list.out') (Join-Path $run 'probe-avd-list.err')
  [IO.File]::WriteAllText((Join-Path $run 'environment-probe.json'), ($probe | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
  foreach ($key in @('adb','aapt2','emulator','avdList')) {
    if ($probe[$key].exit -ne 0) { throw "BLOCKED: environment probe '$key' native exit=$($probe[$key].exit); raw stdout/stderr saved." }
  }
  if ($probe.java.exit -ne 0) { throw "BLOCKED: bundled JDK probe native exit=$($probe.java.exit)." }

  # Process-scoped Android toolchain: the bundled sdkmanager/android-cli 23.0
  # exits 0xC0000409 on this host, and the SDK CMake lacks ninja.exe. Both tools
  # are provided from the task-exclusive build directory (no shared SDK or
  # global registry/EditorPrefs change). The CLI adapter only forwards read-only
  # discovery to the installed classic sdkmanager 16.0 and passes its exit
  # code through unchanged.
  $toolchainRoot = if ($PrivateToolchainRoot) { [IO.Path]::GetFullPath($PrivateToolchainRoot) } else { Join-Path $root 'build\android-toolchain\private' }
  $cliShim = Join-Path $toolchainRoot 'android-cli\android-cli-sdkmanager16.cmd'
  $ninjaExe = Join-Path $toolchainRoot 'ninja\1.13.2\ninja.exe'
  foreach ($requiredTool in @($cliShim, $ninjaExe)) {
    if (-not (Test-Path -LiteralPath $requiredTool -PathType Leaf)) {
      throw "BLOCKED: private Android toolchain missing '$requiredTool'; prepare it under build/android-toolchain/private before the build (no shared SDK change)."
    }
  }
  $ninjaVersion = ((& $ninjaExe --version 2>&1) -join ' ').Trim()
  if ($ninjaVersion -ne '1.13.2') { throw "BLOCKED: private ninja version '$ninjaVersion' is not 1.13.2." }
  $toolchain = [pscustomobject]@{
    root = (Split-Path $toolchainRoot -Leaf)
    cliShim = (Split-Path $cliShim -Leaf); cliShimSha256 = (Get-FileHash $cliShim -Algorithm SHA256).Hash
    ninja = (Split-Path $ninjaExe -Leaf); ninjaSha256 = (Get-FileHash $ninjaExe -Algorithm SHA256).Hash; ninjaVersion = $ninjaVersion
    androidCliMode = 'classic-sdkmanager-16-passthrough'
  }

  $platforms = @(Get-ChildItem (Join-Path $AndroidSdkRoot 'platforms') -Directory | ForEach-Object { $_.Name })
  $resolvedAutoTarget = Get-C05EResolvedAutoTargetSdk $platforms
  $diskE = (Get-Volume -DriveLetter E).SizeRemaining
  $diskD = (Get-Volume -DriveLetter D).SizeRemaining
  $budget = [pscustomobject]@{ measuredUtc=(Get-Date).ToUniversalTime().ToString('o'); eFreeBytes=[long]$diskE; dFreeBytes=[long]$diskD;
    minimumEBytes=16GB; minimumDBytes=8GB; estimatedPeakBytes=13800000000 }
  [IO.File]::WriteAllText((Join-Path $run 'storage-budget.json'), ($budget | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
  if ($diskE -lt 16GB) { throw "BLOCKED_STORAGE: E free=$diskE bytes is below the 16 GiB build/evidence floor." }
  if ($diskD -lt 8GB) { throw "BLOCKED_STORAGE: D free=$diskD bytes is below the 8 GiB AVD floor." }

  # Memory admission. Measured on this machine at -job-worker-count 8: the
  # build starts near 5.7 GB free physical, dips to ~1.09 GB free physical and
  # ~1.32 GB commit headroom while eight UnityShaderCompiler processes plus
  # clang++ (~760 MB) run, and still completes. The draw is dominated by
  # reclaimable page cache, so extrapolating a per-worker cost from it would
  # wrongly refuse a configuration that is known to work. What actually kills
  # the build is starting with no room for the first spike, so the gate is a
  # floor on the free headroom at launch. The historical failure
  # (D12_01 OPEN_ORDINARY_SHADER_BUILD_OOM) had a non-owned 4.26 GB QEMU
  # competing with the build.
  $osMemory = Get-CimInstance Win32_OperatingSystem
  $freePhysicalBytes = [long]$osMemory.FreePhysicalMemory * 1KB
  $commitHeadroomBytes = [long]$osMemory.FreeVirtualMemory * 1KB
  $requiredBytes = [long]2.5GB
  $memorySample = [pscustomobject]@{
    measuredUtc = (Get-Date).ToUniversalTime().ToString('o')
    jobWorkerCount = $JobWorkerCount
    freePhysicalBytes = $freePhysicalBytes
    commitHeadroomBytes = $commitHeadroomBytes
    requiredFreePhysicalBytes = $requiredBytes
    observedPeakFreePhysicalBytes = 1170161664
    observedPeakCommitHeadroomBytes = 1417670656
    status = $(if ($freePhysicalBytes -ge $requiredBytes -and $commitHeadroomBytes -ge $requiredBytes) { 'PASS' } else { 'BLOCKED_MEMORY' })
  }
  [IO.File]::WriteAllText((Join-Path $run 'memory-budget.json'), ($memorySample | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
  if ($memorySample.status -ne 'PASS') {
    throw ("BLOCKED_MEMORY: -job-worker-count {0} needs at least {1} bytes free physical and commit headroom; only {2} physical / {3} commit are free. Free memory first (an idle emulator holds ~4 GiB) or lower -JobWorkerCount." -f $JobWorkerCount, $requiredBytes, $freePhysicalBytes, $commitHeadroomBytes)
  }

  $sourceState = Get-C05ESourceBoundary $root $script:AllowedPaths
  $head = $sourceState.head
  $inputHashes = [ordered]@{ taskInputs=$sourceState.inputHashes; productionInputs=@{}; sharedPluginsBefore=@{} }
  foreach ($rel in @('unity/Mahjong.Unity/Assets/Editor/C05EmulatorAndroidBuild.cs','unity/Mahjong.Unity/Assets/Editor/C05EmulatorAndroidBuild.cs.meta',
      'unity/Mahjong.Unity/ProjectSettings/EditorBuildSettings.asset','unity/Mahjong.Unity/ProjectSettings/ProjectSettings.asset',
      'unity/Mahjong.Unity/Packages/manifest.json','unity/Mahjong.Unity/Packages/packages-lock.json')) {
    $inputHashes.productionInputs[$rel] = (Get-FileHash (Join-Path $root $rel) -Algorithm SHA256).Hash
  }
  foreach ($key in $sourcePluginBeforeHashes.Keys) { $inputHashes.sharedPluginsBefore[$key] = $sourcePluginBeforeHashes[$key] }

  $mutex = New-Object Threading.Mutex($false, 'Local\MahjongV5P45UnityValidation')
  $waited = 0
  while ($waited -lt $MutexWaitSeconds) {
    $held = $mutex.WaitOne([TimeSpan]::FromSeconds([Math]::Min($HeartbeatSeconds, $MutexWaitSeconds - $waited)))
    if ($held) { break }
    $waited += [Math]::Min($HeartbeatSeconds, $MutexWaitSeconds - $waited)
    Write-Host ("HEARTBEAT {0:o} mutex=Local\MahjongV5P45UnityValidation waited={1}s/{2}s" -f (Get-Date), $waited, $MutexWaitSeconds)
  }
  if (-not $held) { throw 'BLOCKED_RESOURCE_BUSY: Unity validation mutex not acquired within the bounded wait; no Unity process started.' }

  # Freeze the authored source closure under the lock, then build a task-local staging copy.
  # Source Assets/Plugins/ProjectSettings/Library are never written by this build.
  $sourceFreezeBefore = @(Get-C05ESourceClosure $sourceProject)
  $sourceFreezePath = Join-Path $run 'source-input-freeze-before.json'
  Save-C05EJson $sourceFreezePath ([pscustomobject]@{ schema=1; project=$sourceProject.Replace('\','/'); files=@($sourceFreezeBefore) })
  $sourceFreezeSha = (Get-FileHash $sourceFreezePath -Algorithm SHA256).Hash
  $sourceClosureDigest = Get-C05ESourceClosureDigest $sourceFreezeBefore
  $stagingDigestPath = Join-Path $stagingRoot 'staging-source-closure.sha256'
  if ($stagingReused) {
    if (-not (Test-Path -LiteralPath $stagingDigestPath -PathType Leaf)) { throw 'BLOCKED: reuse staging requested but its ownership digest is missing.' }
    if (-not (Test-Path -LiteralPath $project -PathType Container)) { throw 'BLOCKED: reuse staging project is missing.' }
    # Authored inputs are reset from the frozen source before any build; the post-sync
    # closure comparison below is the authority, not a possibly stale creation-time digest.
    $previousDigest = (Get-Content -LiteralPath $stagingDigestPath -Raw).Trim()
    $sourceChanged = -not [string]::Equals($previousDigest, $sourceClosureDigest, [StringComparison]::OrdinalIgnoreCase)
    if (-not (Sync-C05EStagingAuthored $sourceProject $project)) { throw "BLOCKED: $script:C05EStagingViolation" }
    $stagingClosure = @(Get-C05ESourceClosure $project)
    $stagingAuthoredDiff = @(Compare-C05ESourceClosure $sourceFreezeBefore $stagingClosure)
    if ($stagingAuthoredDiff.Count) { throw ('BLOCKED: reused staging authored inputs differ from source after sync: ' + ($stagingAuthoredDiff -join '; ')) }
    [IO.File]::WriteAllText($stagingDigestPath, $sourceClosureDigest, (New-Object Text.UTF8Encoding($false)))
    $stagingSourceChanged = [bool]$sourceChanged
    Write-Host ("STAGE reuse-staging {0:o} sourceFiles={1} digest={2} sourceChanged={3} staging={4}" -f (Get-Date), $sourceFreezeBefore.Count, $sourceClosureDigest, $sourceChanged, $stagingRoot)
  }
  else {
    Write-Host ("STAGE copy-staging {0:o} sourceFiles={1} staging={2}" -f (Get-Date), $sourceFreezeBefore.Count, $stagingRoot)
    New-Item -ItemType Directory -Force -Path $project | Out-Null
    if (-not (Copy-C05EStagingProject $sourceProject $project)) { throw "BLOCKED: $script:C05EStagingViolation" }
    [IO.File]::WriteAllText($stagingDigestPath, $sourceClosureDigest, (New-Object Text.UTF8Encoding($false)))
  }
  $packageCache = Join-Path $project 'Library\PackageCache'
  # The staging already carries a PackageCache whenever Copy-C05EStagingProject
  # copied the source project's Library, so a seed would find the target occupied
  # and refuse. Only seed when the staging genuinely has none.
  if ($RegistryCacheSource -and -not $stagingReused -and -not (Test-Path -LiteralPath $packageCache -PathType Container)) {
    $registryCacheReceipt = Copy-C05EVerifiedRegistryCache $RegistryCacheSource $project
    Save-C05EJson (Join-Path $run 'registry-cache-copy-receipt.json') $registryCacheReceipt
  } elseif (-not (Test-Path -LiteralPath $packageCache -PathType Container)) {
    throw 'BLOCKED: fresh staging has no materialized registry PackageCache; pass a lock-matched -RegistryCacheSource or provide a source-project Library cache.'
  }
  $pathBudgetAudit = Get-C05EPathBudgetAudit $project $unity
  $pathBudgetAuditPath = Join-Path $run 'staging-path-budget.json'
  Save-C05EJson $pathBudgetAuditPath $pathBudgetAudit
  if ($pathBudgetAudit.status -ne 'PASS') { throw "BLOCKED: staging path budget=$($pathBudgetAudit.status); see $pathBudgetAuditPath" }
  # The Editor guard walks up from Assets to the first tools/unity/check-shared-plugins.ps1.
  # Give the staging tree its own checker wrapper so it validates the staged plugin closure
  # (the source tree remains untouched and is validated separately by the frozen closure).
  $stagingTools = Join-Path $stagingRoot 'tools\unity'
  New-Item -ItemType Directory -Force -Path $stagingTools | Out-Null
  $stagingChecker = Join-Path $stagingTools 'check-shared-plugins.ps1'
  $realChecker = Join-Path $PSScriptRoot 'check-shared-plugins.ps1'
  $stagingPluginDir = Join-Path $project 'Assets\Plugins\Mahjong'
  $stagingManifest = Join-Path $stagingPluginDir 'shared-plugin-provenance.json'
  $wrapper = @"
param([string]`$ProjectRoot, [switch]`$Quiet)
& '$($realChecker.Replace("'","''"))' -ProjectRoot '$($root.Replace("'","''"))' -PluginDir '$($stagingPluginDir.Replace("'","''"))' -EvidencePath '$($stagingManifest.Replace("'","''"))' -Configuration Release -Quiet
exit `$LASTEXITCODE
"@
  [IO.File]::WriteAllText($stagingChecker, $wrapper, (New-Object Text.UTF8Encoding($false)))
  $projectSettingsPath = Join-Path $project 'ProjectSettings\ProjectSettings.asset'
  $projectSettingsBeforePath = Join-Path $run 'projectsettings-before.bin'
  $projectSettingsShaBefore = (Get-FileHash $projectSettingsPath -Algorithm SHA256).Hash
  [IO.File]::Copy($projectSettingsPath, $projectSettingsBeforePath, $true)
  $pluginDir = Join-Path $project 'Assets\Plugins\Mahjong'
  $pluginBeforeHashes = @{}
  Get-ChildItem -LiteralPath $pluginDir -File | ForEach-Object { $pluginBeforeHashes[$_.Name] = (Get-FileHash $_.FullName -Algorithm SHA256).Hash }
  $ownerMarker = Join-Path $stagingRoot '.c05-owned-staging.json'
  if (-not (Test-Path -LiteralPath $ownerMarker -PathType Leaf)) {
    Save-C05EJson $ownerMarker ([pscustomobject]@{ schema=1; owner='DSH-C05'; stagingRoot=$stagingRoot.Replace('\','/'); createdAt=(Get-Date).ToUniversalTime().ToString('o') })
  }
  $writerPath = Join-Path $stagingRoot '.c05-active-writer.json'
  $self = Get-Process -Id $PID
  Save-C05EJson $writerPath ([pscustomobject]@{ pid=$PID; startUtcTicks=$self.StartTime.ToUniversalTime().Ticks; startedUtc=(Get-Date).ToUniversalTime().ToString('o') })
  Write-Host ("STAGE staging-ready {0:o} project={1}" -f (Get-Date), $project)

  $env:TEMP = Join-Path $run 'temp'; $env:TMP = $env:TEMP
  New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null
  $env:JAVA_HOME = Join-Path $androidPlayer 'OpenJDK'
  $env:ANDROID_HOME = $AndroidSdkRoot
  $env:ANDROID_SDK_ROOT = $AndroidSdkRoot
  $env:ANDROID_AVD_HOME = $AvdHome
  $env:ANDROID_CLI_BIN = $cliShim
  $env:ANDROID_CLI_SHIM_LOG = Join-Path $run 'android-cli-shim.log'
  $env:PATH = (Split-Path $ninjaExe -Parent) + ';' + (Join-Path $androidPlayer 'OpenJDK\bin') + ';' + (Join-Path $AndroidSdkRoot 'platform-tools') + ';' + $oldPath

  # Toolchain caches hold no project code and are safe to keep across runs, so
  # they live outside the staging tree under one predictable root. GRADLE_USER_HOME
  # avoids re-resolving the Android plugin graph on every cold staging.
  $oldGradleHome = $env:GRADLE_USER_HOME
  $oldGradleBuildRoot = $env:C05_GRADLE_BUILD_ROOT
  $cacheRoot = if ($CacheRoot) { $CacheRoot } else { 'D:\dev_cache' }
  $gradleHome = Join-Path $cacheRoot 'android\gradle'
  $gradleBuildRoot = (Join-Path $cacheRoot 'android\gc').Replace('\','/')
  New-Item -ItemType Directory -Force -Path $gradleHome | Out-Null
  New-Item -ItemType Directory -Force -Path (Join-Path $cacheRoot 'android\gc') | Out-Null
  $env:GRADLE_USER_HOME = $gradleHome
  $env:C05_GRADLE_BUILD_ROOT = $gradleBuildRoot
  Write-Host ("CACHE android-gradle {0}" -f $gradleHome)
  Write-Host ("CACHE android-gradle-build {0}" -f $gradleBuildRoot)

  $sharedLog = Join-Path $run 'shared-build.log'
  $sharedRun = Invoke-C05EToFiles 'powershell.exe' @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $PSScriptRoot 'build-shared.ps1'),'-Configuration','Release','-PluginOutputDir',$pluginDir) 1200 (Join-Path $run 'shared-build.out') (Join-Path $run 'shared-build.err')
  [IO.File]::WriteAllText($sharedLog, ([string]$sharedRun.stdout + "`n[stderr]`n" + [string]$sharedRun.stderr), (New-Object Text.UTF8Encoding($false)))
  if ($sharedRun.timedOut -or $sharedRun.exit -ne 0) { throw "BLOCKED: shared plugin forced build failed exit=$($sharedRun.exit) timedOut=$($sharedRun.timedOut) log=$sharedLog" }
  $sharedBuilt = $true
  $provenanceLog = Join-Path $run 'plugin-provenance.log'
  $provenancePath = Join-Path $pluginDir 'shared-plugin-provenance.json'
  $provRun = Invoke-C05EToFiles 'powershell.exe' @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $PSScriptRoot 'check-shared-plugins.ps1'),'-ProjectRoot',$root,'-PluginDir',$pluginDir,'-EvidencePath',$provenancePath,'-Configuration','Release') 300 (Join-Path $run 'provenance.out') (Join-Path $run 'provenance.err')
  [IO.File]::WriteAllText($provenanceLog, ([string]$provRun.stdout + "`n[stderr]`n" + [string]$provRun.stderr), (New-Object Text.UTF8Encoding($false)))
  if ($provRun.timedOut -or $provRun.exit -ne 0) { throw "BLOCKED: shared plugin provenance verification failed exit=$($provRun.exit) timedOut=$($provRun.timedOut)" }
  $pluginsAfter = @{}
  Get-ChildItem -LiteralPath $pluginDir -File | ForEach-Object { $pluginsAfter[$_.Name] = (Get-FileHash $_.FullName -Algorithm SHA256).Hash }
  # Copy the complete shared-plugin provenance before any cleanup, and bind it in evidence.
  $provenanceDir = Join-Path $run 'shared-provenance'
  New-Item -ItemType Directory -Force -Path $provenanceDir | Out-Null
  $provenanceCopies = New-Object System.Collections.Generic.List[object]
  foreach ($candidate in @($provenancePath, (Join-Path $project 'Library\SharedPluginProvenance\provenance.json'))) {
    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
      $name = if ((Split-Path $candidate -Leaf) -eq 'provenance.json') { 'library-provenance.json' } else { 'plugin-provenance.json' }
      $dest = Join-Path $provenanceDir $name
      [IO.File]::Copy($candidate, $dest, $true)
      $provenanceCopies.Add([pscustomobject]@{ source=$candidate.Replace('\','/'); copy=$name; bytes=(Get-Item $dest).Length; sha256=(Get-FileHash $dest -Algorithm SHA256).Hash })
    }
  }
  if (-not $provenanceCopies.Count) { throw 'FAIL: no shared-plugin provenance record was produced; cannot bind the APK inputs.' }
  Save-C05EJson (Join-Path $provenanceDir 'provenance-manifest.json') ([pscustomobject]@{ schema=1; records=$provenanceCopies.ToArray() })

  $arguments = @('-batchmode','-quit','-job-worker-count',[string]$JobWorkerCount,'-projectPath',$project,'-buildTarget','Android',
    '-executeMethod','C05EmulatorAndroidBuild.Build','-c05EmuOutput',$apk,'-c05EmuMarker',$marker,
    '-c05EmuEditorLog',$editorLog,'-c05EmuSourceRoot',$sourceProject,'-c05EmuSourceHead',$head,
    '-c05EmuResolvedTargetSdk',[string]$resolvedAutoTarget,'-logFile',$unityLog)
  if ($ReuseBuildCache) { $arguments += '-c05EmuReuseCache' }
  # Bee (the build backend behind IL2CPP) reads its worker count from
  # BEE_BUILD_THREADS; Unity has no command-line switch for it. The variable is
  # set only for this child and restored immediately, so a caller's environment
  # is never mutated.
  $previousBeeThreads = $env:BEE_BUILD_THREADS
  if ($BeeBuildThreads -gt 0) { $env:BEE_BUILD_THREADS = [string]$BeeBuildThreads }
  try {
    $start = Get-Date
    $proc = Start-C05EHiddenProcess $unity $arguments $project
  } finally {
    $env:BEE_BUILD_THREADS = $previousBeeThreads
  }
  $ownedPid = $proc.Id; $ownedStart = $proc.StartTime.ToUniversalTime().Ticks
  $lastBeat = Get-Date
  while (-not $proc.HasExited) {
    if (((Get-Date) - $start).TotalSeconds -ge $UnityTimeoutSeconds) {
      $killed = Stop-C05EOwnedProcess $ownedPid $ownedStart
      [pscustomobject]@{ status='TIMEOUT'; pid=$ownedPid; startUtcTicks=$ownedStart; timeoutSeconds=$UnityTimeoutSeconds; killAttempted=$killed } |
        ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $run 'timeout.json')
      throw "Unity timeout after $UnityTimeoutSeconds seconds; only the exact launched PID/start-time instance was eligible for termination."
    }
    if (((Get-Date) - $lastBeat).TotalSeconds -ge $HeartbeatSeconds) {
      $logBytes = if (Test-Path $unityLog) { (Get-Item $unityLog).Length } else { 0 }
      Write-Host ("HEARTBEAT {0:o} phase=unity elapsed={1}s logBytes={2} pid={3} alive={4}" -f (Get-Date), [int](((Get-Date)-$start).TotalSeconds), $logBytes, $ownedPid, (-not $proc.HasExited))
      $lastBeat = Get-Date
    }
    Start-Sleep -Seconds 2
  }
  $native = $proc.ExitCode
  $sourceFreezeAfter = @(Get-C05ESourceClosure $sourceProject)
  $sourceFreezeAfterPath = Join-Path $run 'source-input-freeze-after.json'
  Save-C05EJson $sourceFreezeAfterPath ([pscustomobject]@{ schema=1; project=$sourceProject.Replace('\','/'); files=@($sourceFreezeAfter) })
  $sourceChanged = @(Compare-C05ESourceClosure $sourceFreezeBefore $sourceFreezeAfter)
  $postRewrites = @(Repair-C05EPostBuildSource $root @($sourceState.dirtyPaths) $run)
  if ($native -ne 0) { throw "Unity exited $native; see $unityLog" }
  if ($sourceChanged.Count) { throw ('FAIL: authored source closure changed during the build (no restore performed): ' + ($sourceChanged -join '; ')) }
  if ($postRewrites.Count) { throw ('FAIL: authored source paths changed during the build (no restore performed): ' + ($postRewrites -join '; ')) }
  $projectSettingsShaAfter = (Get-FileHash $projectSettingsPath -Algorithm SHA256).Hash
  $stagingSettingsRewritten = $projectSettingsShaAfter -ne $projectSettingsShaBefore
  if ($stagingSettingsRewritten) { [IO.File]::Copy($projectSettingsBeforePath, $projectSettingsPath, $true) }
  if ((Get-FileHash $projectSettingsPath -Algorithm SHA256).Hash -ne $projectSettingsShaBefore) {
    throw 'FAIL: staging ProjectSettings.asset could not be restored to its pre-build bytes.'
  }
  $sourcePluginAfterHashes = @{}
  Get-ChildItem -LiteralPath $sourcePluginDir -File | ForEach-Object { $sourcePluginAfterHashes[$_.Name] = (Get-FileHash $_.FullName -Algorithm SHA256).Hash }
  $sourcePluginsUnchanged = $true
  foreach ($key in $sourcePluginBeforeHashes.Keys) {
    if ($sourcePluginAfterHashes[$key] -ne $sourcePluginBeforeHashes[$key]) { $sourcePluginsUnchanged = $false }
  }
  if (-not $sourcePluginsUnchanged) { throw 'FAIL: authored source plugin bytes changed during the staged build.' }
  if (-not (Test-Path $marker)) { throw 'Unity returned success without a fresh BuildReport marker.' }
  $report = Get-Content $marker -Raw | ConvertFrom-Json
  $reportStepErrors = [int]$report.stepErrors
  $reportStepExceptions = [int]$report.stepExceptions
  $reportFailed = @($report.failedAssertions)
  $reportRewritten = @($report.productionInputsRewritten)
  if ($report.result -ne 'Succeeded' -or [int]$report.errors -ne 0 -or $reportStepErrors -ne 0 -or $reportStepExceptions -ne 0 -or
      $reportFailed.Count -ne 0 -or $reportRewritten.Count -ne 0) {
    throw "Unity BuildReport rejected result=$($report.result) errors=$($report.errors) stepErrors=$reportStepErrors stepExceptions=$reportStepExceptions failed=$($reportFailed.Count) rewritten=$($reportRewritten.Count)."
  }
  if ([string]$report.sourceHead -cne $head) { throw 'FAIL: BuildReport sourceHead differs from the frozen worktree HEAD.' }
  if (-not (Test-Path $apk)) { throw 'Unity BuildReport succeeded but fresh APK output is missing.' }

  $aapt2 = Join-Path $AndroidSdkRoot 'build-tools\36.0.0\aapt2.exe'
  $badging = Invoke-C05ENative $aapt2 @('dump','badging',$apk) 180
  $metaPath = Join-Path $run 'apk-metadata.txt'
  [IO.File]::WriteAllText($metaPath, ([string]$badging.stdout + "`n[stderr]`n" + [string]$badging.stderr), (New-Object Text.UTF8Encoding($false)))
  if ($badging.exit -ne 0) { throw "FAIL: aapt2 dump badging exited $($badging.exit)." }
  [void](Assert-C05EBuildOutcome $report $apk $badging.stdout $start $project $resolvedAutoTarget ([bool]$ReuseBuildCache))

  $hash = (Get-FileHash $apk -Algorithm SHA256).Hash; $apkInfo = Get-Item $apk
  $reportSha = (Get-FileHash $marker -Algorithm SHA256).Hash
  $metadataSha = (Get-FileHash $metaPath -Algorithm SHA256).Hash
  # Finalize only after all owned writers stopped, source closure verified, and provenance copied.
  $summary = [pscustomobject]@{
    status='PASS'; variant='ANDROID_EMULATOR'; abi='x86_64'; sourceHead=$head; dirtyPaths=@($sourceState.dirtyPaths);
    buildCacheMode=$(if ($ReuseBuildCache) { 'REUSE' } else { 'CLEAN' });
    sourceProject=$sourceProject.Replace('\','/'); stagingProject=$project.Replace('\','/');
    stagingRoot=$(if ($stagingRoot.StartsWith(([IO.Path]::GetFullPath($root) + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) { $stagingRoot.Substring($root.Length + 1).Replace('\','/') } else { $stagingRoot.Replace('\','/') });
    sourceInputFreezeSha256=$sourceFreezeSha; sourceInputFileCount=$sourceFreezeBefore.Count;
    sourceInputsUnchanged=$true; sourcePluginsUnchanged=$true; sourcePluginBeforeHashes=$sourcePluginBeforeHashes;
    stagingReused=$stagingReused; stagingSourceChanged=$stagingSourceChanged; stagingAuthoredInputsSynced=$stagingReused; stagingAuthoredClosureMatchesSource=$true;
    inputSha256=$inputHashes; sharedPluginsAfterBuild=$pluginsAfter;
    sharedProvenance=@($provenanceCopies.ToArray());
    unityPath=$unity; unityVersion=$unityVersion; projectVersion=(Get-Content (Join-Path $project 'ProjectSettings\ProjectVersion.txt') -Raw).Trim();
    androidPlayer=[pscustomobject]@{ path=$androidPlayer; jdkVersion='17.0.20.1'; ndkVersion=(Get-Content (Join-Path $androidPlayer 'NDK\source.properties') -Raw).Trim();
      x86_64Il2cppLibs=$true }; androidSdkRoot=$AndroidSdkRoot; androidAvdHome=$AvdHome;
    adbPath=(Join-Path $AndroidSdkRoot 'platform-tools\adb.exe'); aapt2Path=$aapt2; emulatorPath=(Join-Path $AndroidSdkRoot 'emulator\emulator.exe');
    storageBudget=$budget; unityJobWorkerCount=$JobWorkerCount; beeBuildThreads=$BeeBuildThreads; apk=(Split-Path $apk -Leaf); apkBytes=$apkInfo.Length; apkSha256=$hash; nativeExit=$native;
    privateToolchain=$toolchain;
    buildReport=$report; buildReportSha256=$reportSha; metadataSha256=$metadataSha;
    activity=(Get-C05EBadgingContract ([string]$badging.stdout)).activity;
    projectSettingsSha256Before=$projectSettingsShaBefore; projectSettingsSha256After=(Get-FileHash $projectSettingsPath -Algorithm SHA256).Hash;
    stagingProjectSettingsTransientWriteRestored=$stagingSettingsRewritten;
    sourceInputFreezeAfterSha256=(Get-FileHash $sourceFreezeAfterPath -Algorithm SHA256).Hash;
    postBuildRewriteDiffSha256=$(if (Test-Path (Join-Path $run 'post-build-source-rewrite.diff')) { (Get-FileHash (Join-Path $run 'post-build-source-rewrite.diff') -Algorithm SHA256).Hash } else { $null });
    elapsedSeconds=[math]::Round(((Get-Date)-$start).TotalSeconds,1);
    evidenceRoot=$run.Substring($root.Length + 1).Replace('\','/')
  }
  $summaryPath = Join-Path $run 'summary.json'
  $summaryTmp = Join-Path $run ('summary.json.tmp-' + [guid]::NewGuid().ToString('N'))
  Save-C05EJson $summaryTmp $summary
  Move-Item -LiteralPath $summaryTmp -Destination $summaryPath -Force
  Write-Host "PASS variant=ANDROID_EMULATOR ABI=x86_64 APK bytes=$($apkInfo.Length) SHA256=$hash evidence=$run"
}
catch {
  $reportLine = $null
  if (Test-Path $unityLog) { $reportLine = Select-String -Path $unityLog -Pattern 'C05_EMULATOR_BUILD_REPORT: (\{.*\})' | Select-Object -Last 1 }
  $status = if ($_.Exception.Message -like 'BLOCKED_STORAGE*') { 'BLOCKED_STORAGE' }
    elseif ($_.Exception.Message -like 'BLOCKED_RESOURCE_BUSY*') { 'BLOCKED_RESOURCE_BUSY' }
    elseif ($_.Exception.Message -like 'BLOCKED*') { (($_.Exception.Message -split ':')[0]).Trim() }
    elseif ($_.Exception.Message -like 'NOT_RUN:*') { 'NOT_RUN' }
    else { 'FAIL' }
  $failure = [pscustomobject]@{ status=$status; variant='ANDROID_EMULATOR'; sourceHead=$head; dirtyPaths=@($(if ($sourceState) { $sourceState.dirtyPaths } else { @() }));
    error=$_.Exception.Message; nativeExit=$native; apkExists=(Test-Path $apk); apkSha256=$(if (Test-Path $apk) { (Get-FileHash $apk -Algorithm SHA256).Hash } else { $null });
    buildReportLine=$(if ($reportLine) { $reportLine.Matches[0].Groups[1].Value } else { $null });
    projectSettingsSha256Before=$projectSettingsShaBefore; postBuildSourceRewrites=@($(if ($postRewrites) { $postRewrites } else { @() }));
    privateToolchain=$(if ($toolchain) { $toolchain } else { $null }); evidenceRoot=$run }
  Save-C05EJson (Join-Path $run 'failure-summary.json') $failure
  throw
}
finally {
  # Authored source and its plugin bytes are never restored/checked out here; the staged
  # build must leave them untouched. Only process-scoped environment and the mutex are released.
  if ($stagingRoot) {
    $writerPath = Join-Path $stagingRoot '.c05-active-writer.json'
    if (Test-Path -LiteralPath $writerPath -PathType Leaf) {
      try { $writer = Get-Content $writerPath -Raw | ConvertFrom-Json; if ([int]$writer.pid -eq $PID) { Remove-Item -LiteralPath $writerPath -Force } } catch { }
    }
  }
  $env:TEMP = $oldTemp; $env:TMP = $oldTmp; $env:JAVA_HOME = $oldJava
  $env:ANDROID_HOME = $oldHome; $env:ANDROID_SDK_ROOT = $oldRoot; $env:ANDROID_AVD_HOME = $oldAvd
  $env:ANDROID_CLI_BIN = $oldCli; $env:PATH = $oldPath
  $env:GRADLE_USER_HOME = $oldGradleHome
  $env:C05_GRADLE_BUILD_ROOT = $oldGradleBuildRoot
  if ($held -and $null -ne $mutex) { try { $mutex.ReleaseMutex() } catch { } }
  if ($null -ne $mutex) { $mutex.Dispose() }
}
