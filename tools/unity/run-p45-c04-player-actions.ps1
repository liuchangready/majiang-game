#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$RunRoot,
    [string]$UnityPath = 'D:\Unity\Hub\Editor\6000.3.27f1\Editor\Unity.exe',
    [string]$DotnetPath = 'D:\Unity\dotnet\dotnet.exe',
    [int]$TimeoutSeconds = 1800,
    [switch]$FixtureOnly
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$RunRoot = [IO.Path]::GetFullPath($RunRoot)
$allowed = [IO.Path]::GetFullPath((Join-Path $repo 'build')) + [IO.Path]::DirectorySeparatorChar
if (-not $RunRoot.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'RunRoot must be a fresh direct descendant of repository build/.' }
$leaf = Split-Path -Leaf $RunRoot
if ($leaf -notmatch '^[0-9a-fA-F]{32}$') { throw 'RunRoot leaf must be a GUID-N run id.' }
if (Test-Path -LiteralPath $RunRoot) { throw 'RunRoot already exists; refusing to overwrite evidence.' }
if (-not (Test-Path -LiteralPath $UnityPath -PathType Leaf)) { throw "Pinned Unity executable is missing: $UnityPath" }
if (-not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) { throw "Pinned .NET executable is missing: $DotnetPath" }

New-Item -ItemType Directory -Path $RunRoot | Out-Null
$runId = $leaf.ToLowerInvariant()
$source = Join-Path $RunRoot 'source'
$logs = Join-Path $RunRoot 'logs'
$playerDir = Join-Path $RunRoot 'player'
$caseRoot = Join-Path $RunRoot 'cases'
$dataRoot = Join-Path $RunRoot 'data'
$tempRoot = Join-Path $RunRoot 'temp'
$nugetRoot = Join-Path $RunRoot 'nuget'
$cliRoot = Join-Path $RunRoot 'dotnet-home'
$upmRoot = Join-Path $RunRoot 'upm-cache'
$upmNpmRoot = Join-Path $upmRoot 'npm'
$upmPackagesRoot = Join-Path $upmRoot 'packages'
foreach ($path in @($logs,$caseRoot,$dataRoot,$tempRoot,$nugetRoot,$cliRoot,$upmRoot,$upmNpmRoot,$upmPackagesRoot)) { New-Item -ItemType Directory -Force -Path $path | Out-Null }
$script:LogPath = Join-Path $logs 'runner.log'
$script:LogOpen = $true
$script:mutex = $null
$script:mutexHeld = $false
$script:C04VerifierPath = $null
$script:unityProjectRoot = $null
$script:stageRecords = New-Object System.Collections.Generic.List[object]
$gitHead = ''
$trx = Join-Path $RunRoot 'dotnet-results/LocalHost.xml'
$oldTemp = $env:TEMP; $oldTmp = $env:TMP; $oldNuget = $env:NUGET_PACKAGES; $oldCli = $env:DOTNET_CLI_HOME
$oldUpmRoot = $env:UPM_CACHE_ROOT; $oldUpmNpm = $env:UPM_NPM_CACHE_PATH; $oldUpmPackages = $env:UPM_CACHE_PATH
$env:TEMP = $tempRoot; $env:TMP = $tempRoot; $env:NUGET_PACKAGES = $nugetRoot; $env:DOTNET_CLI_HOME = $cliRoot
$env:UPM_CACHE_ROOT = $upmRoot; $env:UPM_NPM_CACHE_PATH = $upmNpmRoot; $env:UPM_CACHE_PATH = $upmPackagesRoot
$env:DOTNET_NOLOGO = '1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

function Write-RunLog([string]$message) {
    $line = '[' + [DateTimeOffset]::UtcNow.ToString('o') + '] ' + $message
    Write-Host $line
    if ($script:LogOpen) { Add-Content -LiteralPath $script:LogPath -Value $line -Encoding UTF8 }
}

function Get-Sha256([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }

function Assert-C04RunCacheIsolation([string]$root) {
    $rootPath = [IO.Path]::GetFullPath($root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $expected = @{
        UPM_CACHE_ROOT = Join-Path $root 'upm-cache'
        UPM_NPM_CACHE_PATH = Join-Path (Join-Path $root 'upm-cache') 'npm'
        UPM_CACHE_PATH = Join-Path (Join-Path $root 'upm-cache') 'packages'
    }
    foreach ($name in $expected.Keys) {
        $actualValue = [Environment]::GetEnvironmentVariable($name)
        if ([string]::IsNullOrWhiteSpace($actualValue) -or [IO.Path]::GetFullPath($actualValue) -cne [IO.Path]::GetFullPath($expected[$name]) -or
            -not [IO.Path]::GetFullPath($actualValue).StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Unity Package Manager cache $name is not isolated beneath the exclusive E-drive run root."
        }
    }
    return $true
}

function Get-C04StableArtifactFiles([string]$root) {
    $unityRoot = if ($script:unityProjectRoot) { $script:unityProjectRoot } else { Join-Path $root 'p' }
    $volatileRoots = @(
        (Join-Path $unityRoot 'Library'),
        (Join-Path $unityRoot 'Temp'),
        (Join-Path $unityRoot 'Logs'),
        (Join-Path $root 'temp'),
        (Join-Path $root 'nuget'),
        (Join-Path $root 'dotnet-home'),
        (Join-Path $root 'upm-cache')
    ) | ForEach-Object { [IO.Path]::GetFullPath($_).TrimEnd([IO.Path]::DirectorySeparatorChar) }
    $files = New-Object System.Collections.Generic.List[IO.FileInfo]
    $pending = New-Object System.Collections.Generic.Stack[string]
    $pending.Push([IO.Path]::GetFullPath($root))
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        $excluded = $false
        $dotnetSource = [IO.Path]::GetFullPath((Join-Path $root 'source/dotnet')).TrimEnd([IO.Path]::DirectorySeparatorChar)
        $directoryLeaf = Split-Path -Leaf $directory
        if ($directory.StartsWith($dotnetSource + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
            $directoryLeaf -in @('obj','bin')) {
            continue
        }
        foreach ($volatile in $volatileRoots) {
            if ($directory.Equals($volatile, [StringComparison]::OrdinalIgnoreCase) -or
                $directory.StartsWith($volatile + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                $excluded = $true
                break
            }
        }
        if ($excluded) { continue }
        foreach ($entry in Get-ChildItem -LiteralPath $directory -Force -ErrorAction Stop) {
            if ($entry.PSIsContainer) { $pending.Push($entry.FullName) }
            elseif ($entry.Name -ne 'manifest.json' -and $entry.Name -ne 'manifest.sha256') { $files.Add([IO.FileInfo]$entry) | Out-Null }
        }
    }
    return @($files | Sort-Object FullName)
}

function Get-C04SeparatedVolatileRoots([string]$root) {
    $unityRoot = if ($script:unityProjectRoot) { $script:unityProjectRoot } else { Join-Path $root 'p' }
    return @(
        [ordered]@{ path=$unityRoot; classification='EXTERNAL_GUID_STAGED_UNITY_PROJECT'; boundBy='unity-short-path-staging.json source/byte/SHA-256 equality record and Unity input closures' },
        [ordered]@{ path=(Join-Path $unityRoot 'Library'); classification='VOLATILE_UNITY_CACHE'; boundBy='unity prebuild/postbuild input closures and script-assembly hashes' },
        [ordered]@{ path=(Join-Path $unityRoot 'Temp'); classification='VOLATILE_UNITY_PROCESS_FILES'; boundBy='native stage process identity/exit and Unity log' },
        [ordered]@{ path=(Join-Path $unityRoot 'Logs'); classification='VOLATILE_UNITY_PROCESS_FILES'; boundBy='captured stage stdout/stderr and designated Unity log under logs/' },
        [ordered]@{ path='source/dotnet/**/obj'; classification='VOLATILE_DOTNET_INTERMEDIATES'; boundBy='fresh dotnet-build-artifacts-closure.json' },
        [ordered]@{ path='source/dotnet/**/bin'; classification='VOLATILE_DOTNET_INTERMEDIATES'; boundBy='fresh dotnet-build-artifacts-closure.json' },
        [ordered]@{ path='temp'; classification='VOLATILE_PROCESS_TEMP'; boundBy='not a build input or review artifact' },
        [ordered]@{ path='nuget'; classification='VOLATILE_PACKAGE_CACHE'; boundBy='resolved source closure and build output hashes' },
        [ordered]@{ path='dotnet-home'; classification='VOLATILE_TOOL_CACHE'; boundBy='pinned dotnet executable and native command record' },
        [ordered]@{ path='upm-cache'; classification='VOLATILE_UPM_DOWNLOAD_CACHE'; boundBy='per-run UPM_CACHE_ROOT plus resolved Library/PackageCache input closure' }
    )
}

function ConvertFrom-C04PlayerSummary([string]$path) {
    $raw = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $normalized = @{}
    foreach ($property in $raw.PSObject.Properties) {
        $name = $property.Name
        $key = $name.Substring(0,1).ToLowerInvariant() + $name.Substring(1)
        $normalized[$key] = $property.Value
    }
    return [pscustomobject]$normalized
}

function Invoke-OwnedNative {
    param([string]$Name,[string]$FilePath,[string]$Arguments,[string]$WorkingDirectory,[string]$StdoutPath,[string]$StderrPath,[int]$Timeout)
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath; $psi.Arguments = $Arguments; $psi.WorkingDirectory = $WorkingDirectory
    $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $psi.EnvironmentVariables['TEMP'] = $tempRoot; $psi.EnvironmentVariables['TMP'] = $tempRoot
    $psi.EnvironmentVariables['NUGET_PACKAGES'] = $nugetRoot; $psi.EnvironmentVariables['DOTNET_CLI_HOME'] = $cliRoot
    $psi.EnvironmentVariables['DOTNET_NOLOGO'] = '1'; $psi.EnvironmentVariables['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $process = New-Object System.Diagnostics.Process; $process.StartInfo = $psi
    $started = [DateTimeOffset]::UtcNow
    if (-not $process.Start()) { throw "$Name failed to start." }
    $pidOwned = $process.Id
    $startTimeOwned = $process.StartTime.ToUniversalTime().ToString('o')
    $stdoutTask = $process.StandardOutput.ReadToEndAsync(); $stderrTask = $process.StandardError.ReadToEndAsync()
    $exited = $process.WaitForExit($Timeout * 1000)
    $didTimeout = -not $exited
    $treeKillExit = $null
    if ($didTimeout) {
        # PID came directly from the Process object created above. /T is scoped
        # to that owned process tree; never enumerate/kill unrelated processes.
        $kill = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\taskkill.exe') -ArgumentList @('/PID',"$pidOwned",'/T','/F') -PassThru -WindowStyle Hidden -Wait
        $treeKillExit = $kill.ExitCode
        $exited = $process.WaitForExit(3000)
    }
    $drained = [System.Threading.Tasks.Task]::WaitAll([System.Threading.Tasks.Task[]]@($stdoutTask,$stderrTask),3000)
    $stdoutText = if ($drained) { $stdoutTask.Result } else { '[stdout drain exceeded bounded 3s wait]' }
    $stderrText = if ($drained) { $stderrTask.Result } else { '[stderr drain exceeded bounded 3s wait]' }
    [IO.File]::WriteAllText($StdoutPath, $stdoutText, (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText($StderrPath, $stderrText, (New-Object Text.UTF8Encoding($false)))
    $exit = if ($didTimeout -or -not $exited) { 124 } else { $process.ExitCode }
    $record = [ordered]@{ name=$Name; file=$FilePath; arguments=$Arguments; workingDirectory=$WorkingDirectory; pid=$pidOwned; processStartUtc=$startTimeOwned; startedUtc=$started.ToString('o'); endedUtc=[DateTimeOffset]::UtcNow.ToString('o'); exitCode=$exit; timedOut=$didTimeout; processExitedAfterKill=$exited; treeKillExit=$treeKillExit; outputDrained=$drained; stdout=$StdoutPath; stderr=$StderrPath; stdoutSha256=(Get-Sha256 $StdoutPath); stderrSha256=(Get-Sha256 $StderrPath) }
    $script:stageRecords.Add($record) | Out-Null
    Write-RunLog "$Name nativeExit=$exit timeout=$didTimeout"
    return $record
}

function Quote-Arg([string]$value) { '"' + $value.Replace('"','\"') + '"' }

function ConvertFrom-C04CanonicalPayload([byte[]]$bytes) {
    if ($bytes.Length -lt 8 -or [Text.Encoding]::ASCII.GetString($bytes,0,4) -cne 'MJC1') { throw 'Payload is not an MJC1 canonical command.' }
    if ([BitConverter]::ToUInt16($bytes,4) -ne 1 -or [BitConverter]::ToUInt16($bytes,6) -ne 10) { throw 'Payload is not canonical SubmitAction v1.' }
    $fields = @{}; $offset = 8; $last = 0
    while ($offset -lt $bytes.Length) {
        if ($bytes.Length - $offset -lt 7) { throw 'Truncated canonical payload field header.' }
        $number = [BitConverter]::ToUInt16($bytes,$offset); $type = [int]$bytes[$offset+2]; $length = [BitConverter]::ToUInt32($bytes,$offset+3); $offset += 7
        if ($number -le $last -or $number -eq 0 -or $length -gt ($bytes.Length - $offset)) { throw 'Canonical fields are unordered, duplicate, or truncated.' }
        $last = $number; $value = $null
        if ($number -in 1,2,5) {
            if ($type -ne 5) { throw "Canonical field $number is not String." }
            if ($length -lt 4) { throw "Canonical field $number is missing its inner UTF-8 byte length." }
            $innerLength = [BitConverter]::ToUInt32($bytes,$offset)
            if ($innerLength -ne ($length - 4)) { throw "Canonical field $number inner UTF-8 byte length is inconsistent." }
            $value = (New-Object Text.UTF8Encoding($false,$true)).GetString($bytes,$offset+4,[int]$innerLength)
        } elseif ($number -eq 3) {
            if ($type -ne 3 -or $length -ne 8) { throw 'Canonical controlEpoch is not UInt64.' }
            $value = [BitConverter]::ToUInt64($bytes,$offset)
        } elseif ($number -in 4,7) {
            if ($type -ne 2 -or $length -ne 4) { throw "Canonical field $number is not UInt32." }
            $value = [BitConverter]::ToUInt32($bytes,$offset)
        } elseif ($number -eq 6) {
            if ($type -ne 7 -or $length -lt 4) { throw 'Canonical tileIds is not RepeatedUInt32.' }
            $count = [BitConverter]::ToUInt32($bytes,$offset)
            if ($count -gt 14 -or $length -ne (4 + 4 * $count)) { throw 'Canonical tileIds count/length mismatch.' }
            $items = @(); for ($i=0; $i -lt $count; $i++) { $items += [BitConverter]::ToUInt32($bytes,$offset+4+4*$i) }; $value = $items
        } else { throw "Unexpected SubmitAction canonical field $number." }
        $fields[[int]$number] = $value; $offset += [int]$length
    }
    foreach ($required in 1,2,3,4) { if (-not $fields.ContainsKey($required)) { throw "Missing SubmitAction canonical field $required." } }
    return $fields
}

function Get-ClosureManifest([string]$path,[string]$output) {
    $files = Get-ChildItem -LiteralPath $path -Recurse -File | Sort-Object FullName
    $entries = New-Object System.Collections.Generic.List[object]
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($path.Length).TrimStart('\','/').Replace('\','/')
        $entries.Add([ordered]@{ path=$relative; bytes=[long]$file.Length; sha256=(Get-Sha256 $file.FullName) }) | Out-Null
    }
    $manifest = [ordered]@{ root=$path; fileCount=$entries.Count; entries=$entries }
    $json = $manifest | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($output, $json + "`n", (New-Object Text.UTF8Encoding($false)))
    return Get-Sha256 $output
}

function Assert-C04UnityProjectCopy([string]$sourceProject,[string]$stagedProject,[string]$evidencePath) {
    $sourceEntries = Get-ChildItem -LiteralPath $sourceProject -Recurse -File | Sort-Object { $_.FullName.Substring($sourceProject.Length).Replace('\','/').ToLowerInvariant() }
    $stagedEntries = Get-ChildItem -LiteralPath $stagedProject -Recurse -File | Sort-Object { $_.FullName.Substring($stagedProject.Length).Replace('\','/').ToLowerInvariant() }
    if ($sourceEntries.Count -eq 0 -or $sourceEntries.Count -ne $stagedEntries.Count) { throw 'Short-path staged Unity project file count differs from the frozen source project.' }
    $rows = New-Object System.Collections.Generic.List[object]
    for ($i=0; $i -lt $sourceEntries.Count; $i++) {
        $sourceRelative = $sourceEntries[$i].FullName.Substring($sourceProject.Length).TrimStart('\','/').Replace('\','/')
        $stagedRelative = $stagedEntries[$i].FullName.Substring($stagedProject.Length).TrimStart('\','/').Replace('\','/')
        $sourceHash = Get-Sha256 $sourceEntries[$i].FullName
        $stagedHash = Get-Sha256 $stagedEntries[$i].FullName
        if ($sourceRelative -cne $stagedRelative -or $sourceEntries[$i].Length -ne $stagedEntries[$i].Length -or $sourceHash -cne $stagedHash) {
            throw "Short-path Unity project staging differs from the frozen source file: $sourceRelative"
        }
        $rows.Add([ordered]@{ path=$sourceRelative; bytes=[long]$sourceEntries[$i].Length; sha256=$sourceHash }) | Out-Null
    }
    $maximumPath = @($stagedEntries | ForEach-Object { [IO.Path]::GetFullPath($_.FullName).Length } | Measure-Object -Maximum).Maximum
    if ($maximumPath -gt 259) { throw "Short-path staged Unity project still exceeds the supported legacy path boundary: $maximumPath" }
    $manifest = [ordered]@{ sourceProject='source/unity/Mahjong.Unity'; stagedProject='p'; files=$rows.Count; maximumAbsolutePathLength=[int]$maximumPath; windowsLegacyMaxPath=260; entries=$rows }
    [IO.File]::WriteAllText($evidencePath,(($manifest | ConvertTo-Json -Depth 8)+"`n"),(New-Object Text.UTF8Encoding($false)))
    return Get-Sha256 $evidencePath
}

function Get-EnabledScenes([string]$projectRoot) {
    $settings = Join-Path $projectRoot 'ProjectSettings/EditorBuildSettings.asset'
    if (-not (Test-Path -LiteralPath $settings -PathType Leaf)) { throw 'Frozen EditorBuildSettings.asset is missing.' }
    $lines = Get-Content -LiteralPath $settings -Encoding UTF8
    $scenes = New-Object System.Collections.Generic.List[string]
    for ($i=0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\s*-\s+enabled:\s*1\s*$') {
            if ($i + 1 -ge $lines.Count -or $lines[$i+1] -notmatch '^\s+path:\s*(.+?)\s*$') { throw 'Enabled scene entry is missing its path.' }
            $scenes.Add($Matches[1].Trim()) | Out-Null
        }
    }
    if ($scenes.Count -eq 0 -or @($scenes | Select-Object -Unique).Count -ne $scenes.Count) { throw 'Frozen enabled-scene list is empty or contains duplicates.' }
    return ,$scenes.ToArray()
}

function Get-UnityInputClosure([string]$projectRoot,[string]$output) {
    $rootNames = @('Assets','Packages','ProjectSettings','UserSettings','Library/PackageCache','Library/ScriptAssemblies')
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        $entries = New-Object System.Collections.Generic.List[object]
        $excludedPathRules = New-Object System.Collections.Generic.List[string]
        try {
            foreach ($rootName in $rootNames) {
                $root = Join-Path $projectRoot ($rootName.Replace('/','\'))
                if (-not (Test-Path -LiteralPath $root -PathType Container)) { if ($rootName -eq 'Library/ScriptAssemblies') { continue }; throw "Unity build input root is missing: $rootName" }
                $pending = New-Object System.Collections.Generic.Stack[string]
                $pending.Push($root)
                while ($pending.Count -gt 0) {
                    $directory = $pending.Pop()
                    if (-not (Test-Path -LiteralPath $directory -PathType Container)) { throw "Unity input tree changed during closure capture: $directory" }
                    foreach ($item in Get-ChildItem -LiteralPath $directory -Force -ErrorAction Stop) {
                        $relative = $item.FullName.Substring($projectRoot.Length).TrimStart('\','/').Replace('\','/')
                        if ($item.PSIsContainer) {
                            if ($rootName -eq 'Library/PackageCache' -and $item.Name.EndsWith('.bundle',[StringComparison]::OrdinalIgnoreCase)) {
                                $excludedPathRules.Add($relative + '/** (macOS bundle excluded from StandaloneWindows64 inputs)') | Out-Null
                                continue
                            }
                            $pending.Push($item.FullName)
                            continue
                        }
                        if (-not (Test-Path -LiteralPath $item.FullName -PathType Leaf)) { throw "Unity input file disappeared during closure capture: $relative" }
                        $length = [long](Get-Item -LiteralPath $item.FullName -ErrorAction Stop).Length
                        $digest = Get-Sha256 $item.FullName
                        if (-not (Test-Path -LiteralPath $item.FullName -PathType Leaf) -or (Get-Item -LiteralPath $item.FullName -ErrorAction Stop).Length -ne $length) { throw "Unity input changed while hashing: $relative" }
                        $entries.Add([ordered]@{ path=$relative; bytes=$length; sha256=$digest }) | Out-Null
                    }
                }
            }
            $manifest = [ordered]@{ projectRoot=$projectRoot; target='StandaloneWindows64'; roots=$rootNames; excludedPathRules=$excludedPathRules; fileCount=$entries.Count; entries=$entries }
            [IO.File]::WriteAllText($output,(($manifest | ConvertTo-Json -Depth 8)+"`n"),(New-Object Text.UTF8Encoding($false)))
            return Get-Sha256 $output
        } catch {
            if ($attempt -eq 3) { throw }
            Write-RunLog "UNITY_INPUT_CLOSURE_RESCAN attempt=$attempt reason=$($_.Exception.Message)"
            Start-Sleep -Seconds $attempt
        }
    }
    throw 'Unity input closure did not stabilize after three bounded scans.'
}

function Assert-C04PostBuildInputClosure([string]$beforePath,[string]$afterPath,[string]$changesPath) {
    $beforeManifest = Get-Content -LiteralPath $beforePath -Raw -Encoding UTF8 | ConvertFrom-Json
    $afterManifest = Get-Content -LiteralPath $afterPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $beforeByPath = @{}; foreach ($entry in $beforeManifest.entries) { $beforeByPath[[string]$entry.path] = $entry }
    $afterByPath = @{}; foreach ($entry in $afterManifest.entries) { $afterByPath[[string]$entry.path] = $entry }
    $changes = New-Object System.Collections.Generic.List[object]
    foreach ($path in @(@($beforeByPath.Keys) + @($afterByPath.Keys) | Sort-Object -Unique)) {
        $old = $beforeByPath[$path]; $new = $afterByPath[$path]
        if ($null -eq $old -or $null -eq $new -or $old.sha256 -cne $new.sha256) {
            $classification = if ($path -like 'Library/ScriptAssemblies/*') { 'UnityGenerated' } else { 'BuildInputChanged' }
            $changes.Add([ordered]@{ path=$path; classification=$classification; beforeSha256=if($old){$old.sha256}else{$null}; afterSha256=if($new){$new.sha256}else{$null}; afterBytes=if($new){$new.bytes}else{$null} }) | Out-Null
        }
    }
    [IO.File]::WriteAllText($changesPath,(($changes | ConvertTo-Json -Depth 6)+"`n"),(New-Object Text.UTF8Encoding($false)))
    $unexpected = @($changes | Where-Object { $_.classification -ne 'UnityGenerated' })
    if ($unexpected.Count -gt 0) { throw 'Unity build changed Assets, Packages, ProjectSettings, or resolved package cache inputs outside the declared Unity-generated ScriptAssemblies closure.' }
    return ,$changes.ToArray()
}

function Assert-C04FrozenSourceClean([int]$gitExit,[string]$statusText) {
    if ($gitExit -ne 0) { throw "git status failed with native exit $gitExit before source archive." }
    if (-not [string]::IsNullOrWhiteSpace($statusText)) { throw "Frozen source is dirty and cannot be archived as a candidate: $statusText" }
    return $true
}

function Assert-C04ArchiveHasNoGitMetadata([string]$sourceRoot) {
    if (Test-Path -LiteralPath (Join-Path $sourceRoot '.git')) { throw 'Frozen source archive/extract unexpectedly contains .git metadata.' }
    return $true
}

function Test-C04ExactPlayModeXml([xml]$xmlDocument) {
    if ($null -eq $xmlDocument) { throw 'PlayMode XML is missing.' }
    $expected = @('Pon_FixedPlayerActionIsAppliedAndVisible','Chi_FixedPlayerActionIsAppliedAndVisible','Riichi_FixedPlayerActionIsAppliedAndVisible','ClosedKan_FixedPlayerActionAndReplacementDrawAreApplied')
    $nodes = @($xmlDocument.SelectNodes('//test-case'))
    $names = @($nodes | ForEach-Object { $_.GetAttribute('name').Split('.')[-1] })
    if ($nodes.Count -ne 4 -or @($nodes | Where-Object { $_.GetAttribute('result') -cne 'Passed' }).Count -ne 0 -or
        (($names | Sort-Object) -join '|') -cne (($expected | Sort-Object) -join '|') -or @($names | Select-Object -Unique).Count -ne 4) {
        throw "PlayMode XML must contain the exact four unique C04 tests all Passed; count=$($nodes.Count) names=$($names -join ',')."
    }
    return ,$names
}

function Test-C04BuildMarker([string]$markerPath,[string]$executablePath,[string[]]$expectedScenes) {
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf) -or -not (Test-Path -LiteralPath $executablePath -PathType Leaf)) { throw 'Player build marker or executable is missing.' }
    $m = Get-Content -LiteralPath $markerPath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($key in @('result','target','errors','bytes','output','outputSha256','buildIdentity','scenes')) {
        if ($null -eq $m.PSObject.Properties[$key]) { throw "Player build marker is missing required field $key." }
    }
    foreach ($numeric in @(@{name='errors';value=$m.errors},@{name='bytes';value=$m.bytes})) {
        if ($numeric.value -is [bool] -or $numeric.value -isnot [ValueType] -or
            $numeric.value -isnot [byte] -and $numeric.value -isnot [sbyte] -and
            $numeric.value -isnot [int16] -and $numeric.value -isnot [uint16] -and
            $numeric.value -isnot [int32] -and $numeric.value -isnot [uint32] -and
            $numeric.value -isnot [int64] -and $numeric.value -isnot [uint64]) {
            throw "Player build marker field $($numeric.name) must be a JSON integer, not boolean, fractional, string, or null."
        }
    }
    if ([long]$m.errors -lt 0 -or [long]$m.bytes -le 0 -or
        $m.result -cne 'Succeeded' -or $m.target -cne 'StandaloneWindows64' -or [long]$m.errors -ne 0 -or
        [long]$m.bytes -ne (Get-Item -LiteralPath $executablePath).Length -or
        $m.output -cne [IO.Path]::GetFileName($executablePath)) { throw 'Player build marker fields do not describe the actual StandaloneWindows64 executable.' }
    if ($m.scenes -isnot [array] -or (($m.scenes -join '|') -cne ($expectedScenes -join '|'))) { throw 'Player build marker scene list differs from the exact frozen enabled scenes.' }
    $exeHash = Get-Sha256 $executablePath
    if ($m.outputSha256 -cne $exeHash -or $m.outputSha256 -notmatch '^[0-9a-f]{64}$') { throw 'Player marker executable hash does not match the built package.' }
    $identityInput = $m.target + "`n" + $exeHash + "`n" + ($expectedScenes -join "`n")
    $identityHash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($identityInput))).Replace('-','').ToLowerInvariant()
    if ($m.buildIdentity -cne $identityHash) { throw 'Player build identity is not bound to target, executable hash, and frozen scenes.' }
    return $m
}

function Test-C04CaseSummary {
    param($Summary,[string]$ExpectedCase)
    if ($null -eq $Summary) { throw 'C04 summary is missing.' }
    if ($Summary.result -ne 'PASS' -or $Summary.status -ne 'GAME') { throw 'C04 result/status is not PASS/GAME.' }
    if ($Summary.eventConfirmed -ne $true) { throw 'C04 event confirmation is absent; Applied receipt alone is insufficient.' }
    if ($Summary.nativeExitCode -ne 0 -or $Summary.nativeTimedOut -ne $false) { throw 'C04 native process failed or timed out.' }
    if ($Summary.caseId -ne $ExpectedCase) { throw 'C04 summary case identity mismatch.' }
    if ($Summary.seat -ne 0 -or $Summary.eventSeat -ne 0) { throw 'C04 result is not bound to seat 0.' }
    if ([string]::IsNullOrWhiteSpace($Summary.roomId) -or [string]::IsNullOrWhiteSpace($Summary.sessionId) -or [string]::IsNullOrWhiteSpace($Summary.roundId)) { throw 'C04 room/session/round binding is incomplete.' }
    if ($Summary.eventRoundId -ne $Summary.roundId) { throw 'C04 visible event round mismatch.' }
    if ([string]::IsNullOrWhiteSpace($Summary.requestId) -or [string]::IsNullOrWhiteSpace($Summary.scopeId)) { throw 'C04 request/scope identity is incomplete.' }
    if ($Summary.receiptRequestId -ne $Summary.requestId -or $Summary.receiptScopeId -ne $Summary.scopeId) { throw 'C04 Applied receipt is not bound to the exact request/scope identity.' }
    if ($Summary.payloadSha256 -notmatch '^[0-9a-f]{64}$') { throw 'C04 canonical payload hash is missing or malformed.' }
    if ([string]::IsNullOrWhiteSpace($Summary.canonicalPayloadBase64)) { throw 'C04 canonical payload bytes are missing.' }
    $payloadBytes = [Convert]::FromBase64String($Summary.canonicalPayloadBase64)
    $payloadHash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($payloadBytes)).Replace('-','').ToLowerInvariant()
    if ($payloadHash -ne $Summary.payloadSha256) { throw 'C04 canonical payload bytes do not match the recorded hash.' }
    $payload = ConvertFrom-C04CanonicalPayload $payloadBytes
    $expectedAction = @{ 'C04-G2-01'=6; 'C04-G2-02'=5; 'C04-G2-03'=8; 'C04-G2-04'=7 }[$ExpectedCase]
    $expectedEventTiles = @{ 'C04-G2-01'='0,1,2'; 'C04-G2-02'='0,4,8'; 'C04-G2-03'=''; 'C04-G2-04'='0,1,2,3' }[$ExpectedCase]
    if ($null -eq $expectedAction -or [UInt32]$payload[4] -ne $expectedAction -or [UInt32]$Summary.payloadActionType -ne $expectedAction) { throw 'Canonical SubmitAction type does not match its fixed case.' }
    if ($payload[1] -cne $Summary.roundId -or $Summary.payloadRoundId -cne $payload[1]) { throw 'Canonical round id is not bound to the case summary.' }
    if ($payload[2] -cne $Summary.decisionId -or $Summary.payloadDecisionId -cne $payload[2] -or [string]::IsNullOrWhiteSpace($Summary.decisionId)) { throw 'Canonical decision id does not match the submitted candidate.' }
    if ([UInt64]$payload[3] -ne [UInt64]$Summary.controlEpoch -or [UInt64]$Summary.payloadControlEpoch -ne [UInt64]$payload[3]) { throw 'Canonical control epoch is not bound to the candidate.' }
    $payloadOption = if ($payload.ContainsKey(5)) { [string]$payload[5] } else { '' }
    $expectedMeldType = if ($ExpectedCase -eq 'C04-G2-04') { 4 } else { 0 }
    $payloadMeldType = if ($payload.ContainsKey(7)) { [UInt32]$payload[7] } else { 0 }
    if ($payloadOption -cne [string]$Summary.candidateOptionId -or $payloadOption -cne [string]$Summary.payloadOptionId -or $payloadMeldType -ne $expectedMeldType -or [UInt32]$Summary.payloadMeldType -ne $expectedMeldType) { throw 'Canonical candidate option/meld type does not match the fixed candidate.' }
    $payloadTiles = if ($payload.ContainsKey(6)) { (@($payload[6]) | Sort-Object) -join ',' } else { '' }
    if (($payloadTiles -ne $expectedEventTiles) -or (($Summary.payloadTileIds -split ',' | Where-Object { $_ } | Sort-Object) -join ',') -ne $expectedEventTiles) { throw 'Canonical physical tile selection does not match its fixed case.' }
    if ($Summary.receiptStatus -ne 'Applied' -or [UInt64]$Summary.receiptCommandVersion -le [UInt64]$Summary.stateVersionBefore) { throw 'Applied receipt command version is not above the subscribed baseline.' }
    if ([UInt64]$Summary.eventCommandVersion -ne [UInt64]$Summary.receiptCommandVersion -or [UInt64]$Summary.eventCommandVersion -le [UInt64]$Summary.stateVersionBefore) { throw 'C04 event command version does not match the post-baseline Applied command.' }
    if ([UInt64]$Summary.stateVersionAfter -le [UInt64]$Summary.stateVersionBefore -or [UInt64]$Summary.stateVersionAfter -ne [UInt64]$Summary.receiptCommandVersion) { throw 'C04 authoritative state version does not equal the Applied command version above baseline.' }
    if ([UInt64]$Summary.eventSeqAfter -lt [UInt64]$Summary.eventSeq -or [UInt64]$Summary.eventSeq -le [UInt64]$Summary.eventSeqBefore -or [UInt64]$Summary.receiptEventSeq -ne [UInt64]$Summary.eventSeqAfter) { throw 'C04 event cursor/high-water is invalid or differs from the receipt.' }
    if ([UInt64]$Summary.bindingEpoch -eq 0 -or [UInt64]$Summary.ownerEpoch -eq 0 -or [UInt64]$Summary.controlEpoch -eq 0 -or [UInt64]$Summary.eventBindingEpoch -ne [UInt64]$Summary.bindingEpoch -or [UInt64]$Summary.eventConnectionEpoch -ne [UInt64]$Summary.connectionEpoch -or [UInt64]$Summary.eventOwnerEpoch -ne [UInt64]$Summary.ownerEpoch) { throw 'C04 epoch identity is incomplete or differs from the accepted batch.' }
    if ($Summary.eventScopeId -cne $Summary.scopeId -or $Summary.eventSessionId -cne $Summary.sessionId -or $Summary.eventRoomId -cne $Summary.roomId -or $Summary.eventTableId -cne $Summary.tableId -or $Summary.eventRole -cne 'Player') { throw 'C04 event batch identity does not match the request context.' }
    $cursorAdvanced = ([UInt64]$Summary.cursorStreamAfter -gt [UInt64]$Summary.cursorStreamBefore) -or (([UInt64]$Summary.cursorStreamAfter -eq [UInt64]$Summary.cursorStreamBefore) -and ([UInt64]$Summary.cursorAfter -gt [UInt64]$Summary.cursorBefore))
    if (-not $cursorAdvanced) { throw 'C04 structured stream cursor did not advance.' }
    $eventByCase = @{ 'C04-G2-01'='PonDeclared'; 'C04-G2-02'='ChiDeclared'; 'C04-G2-03'='RiichiDeclared'; 'C04-G2-04'='KanDeclared' }
    if (-not $eventByCase.ContainsKey($ExpectedCase) -or $Summary.eventType -ne $eventByCase[$ExpectedCase]) { throw 'C04 action event type does not match its fixed case.' }
    if (($Summary.eventTiles -split ',' | Where-Object { $_ } | Sort-Object) -join ',' -ne $expectedEventTiles) { throw 'Visible action event did not carry the fixed physical entities.' }
    switch ($ExpectedCase) {
        'C04-G2-01' { if (($Summary.meldTileIds -split ',' | Sort-Object) -join ',' -ne '0,1,2' -or $Summary.handTileIdsAfter -match '(^|,)(0|1|2)(,|$)') { throw 'Pon entity transfer to meld/hand is invalid.' } }
        'C04-G2-02' { if (($Summary.meldTileIds -split ',' | Sort-Object) -join ',' -ne '0,4,8' -or $Summary.handTileIdsAfter -match '(^|,)(0|4|8)(,|$)') { throw 'Chi entity transfer to meld/hand is invalid.' } }
        'C04-G2-03' { if ([UInt64]$Summary.seatScoreBefore -ne 25000 -or [UInt64]$Summary.seatScoreAfter -ne 24000 -or [UInt32]$Summary.riichiSticksAfter -ne ([UInt32]$Summary.riichiSticksBefore + 1)) { throw 'Riichi score/stick transfer is invalid.' } }
        'C04-G2-04' { if (($Summary.meldTileIds -split ',' | Sort-Object) -join ',' -ne '0,1,2,3' -or $Summary.handTileIdsAfter -match '(^|,)(0|1|2|3)(,|$)' -or [string]$Summary.replacementDrawTileId -ne '73') { throw 'Closed Kan/dead-wall replacement entity transfer is invalid.' } }
    }
    return $true
}

function Test-C04ActionEvidence {
    param($Summary,$Evidence,[string]$ExpectedCase,[string]$EvidencePathForVerifier)
    if ($null -eq $Evidence) { throw 'Raw Player action evidence is missing.' }
    if ($Evidence.caseId -cne $ExpectedCase -or $Evidence.requestId -cne $Summary.requestId -or $Evidence.scopeId -cne $Summary.scopeId) { throw 'Raw action evidence is not bound to the case/request/scope.' }
    if ($Evidence.payloadBase64 -cne $Summary.canonicalPayloadBase64 -or $Evidence.payloadSha256 -cne $Summary.payloadSha256) { throw 'Raw action evidence payload differs from the submitted summary payload.' }
    $rawPayload = [Convert]::FromBase64String($Evidence.payloadBase64)
    $rawHash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($rawPayload)).Replace('-','').ToLowerInvariant()
    if ($rawHash -cne $Evidence.payloadSha256) { throw 'Raw Player action payload hash is invalid.' }
    $parsed = ConvertFrom-C04CanonicalPayload $rawPayload
    if ([string]$parsed[1] -cne $Evidence.baselineRoundId -or [string]$parsed[2] -cne $Evidence.baselineDecisionId -or
        [UInt64]$parsed[3] -ne [UInt64]$Evidence.baselineControlEpoch) { throw 'Raw canonical action is not bound to its captured Player baseline.' }
    $actionByCase = @{ 'C04-G2-01'=6; 'C04-G2-02'=5; 'C04-G2-03'=8; 'C04-G2-04'=7 }
    $tilesByCase = @{ 'C04-G2-01'='0,1,2'; 'C04-G2-02'='0,4,8'; 'C04-G2-03'=''; 'C04-G2-04'='0,1,2,3' }
    $rawTiles = if ($parsed.ContainsKey(6)) { (@($parsed[6]) | Sort-Object) -join ',' } else { '' }
    if (-not $actionByCase.ContainsKey($ExpectedCase) -or [UInt32]$parsed[4] -ne $actionByCase[$ExpectedCase] -or $rawTiles -ne $tilesByCase[$ExpectedCase]) { throw 'Raw canonical action kind or physical selection differs from the frozen C04 case.' }
    $rawMeldType = if ($parsed.ContainsKey(7)) { [UInt32]$parsed[7] } else { 0 }
    if ($rawMeldType -ne $(if ($ExpectedCase -eq 'C04-G2-04') { 4 } else { 0 })) { throw 'Raw canonical meld type differs from the frozen action case.' }
    if ($Evidence.baselineRoomId -cne $Summary.roomId -or $Evidence.baselineSessionId -cne $Summary.sessionId -or
        $Evidence.baselineTableId -cne $Summary.tableId -or $Evidence.baselineRoundId -cne $Summary.roundId -or
        [int]$Evidence.baselineSeat -ne [int]$Summary.seat -or [UInt64]$Evidence.baselineBindingEpoch -ne [UInt64]$Summary.bindingEpoch -or
        [UInt64]$Evidence.baselineOwnerEpoch -ne [UInt64]$Summary.ownerEpoch -or [UInt64]$Evidence.baselineStateVersion -ne [UInt64]$Summary.stateVersionBefore -or
        [UInt64]$Evidence.baselineEventSeq -ne [UInt64]$Summary.eventSeqBefore) { throw 'Summary identity/epoch does not match the captured baseline publication.' }
    if ($Evidence.receiptDisposition -ne 'Applied' -or $Evidence.receiptStatus -ne 'Applied' -or
        $Evidence.receiptRequestId -cne $Evidence.requestId -or $Evidence.receiptScopeId -cne $Evidence.scopeId -or
        $Evidence.receiptDigest -cne $Evidence.payloadSha256 -or [UInt64]$Evidence.receiptCommandVersion -ne [UInt64]$Summary.receiptCommandVersion -or
        [UInt64]$Evidence.receiptEventSeq -ne [UInt64]$Summary.receiptEventSeq) { throw 'Typed Applied receipt does not match original request/scope/payload/version/event facts.' }
    if ($Evidence.eventType -cne $Summary.eventType -or $Evidence.eventRoundId -cne $Evidence.baselineRoundId -or
        $Evidence.eventBatchKind -cne 'Incremental' -or $Evidence.eventFullBaseline -ne $false -or $Evidence.eventRole -cne 'Player' -or
        [int]$Evidence.eventSeat -ne 0 -or [UInt64]$Evidence.eventBindingEpoch -ne [UInt64]$Evidence.baselineBindingEpoch -or
        [UInt64]$Evidence.eventConnectionEpoch -ne [UInt64]$Evidence.baselineConnectionEpoch -or
        [UInt64]$Evidence.eventOwnerEpoch -ne [UInt64]$Evidence.baselineOwnerEpoch -or
        $Evidence.eventScopeId -cne $Evidence.scopeId -or $Evidence.eventSessionId -cne $Evidence.baselineSessionId -or
        $Evidence.eventRoomId -cne $Evidence.baselineRoomId -or $Evidence.eventTableId -cne $Evidence.baselineTableId -or
        [UInt64]$Evidence.eventCommandVersion -ne [UInt64]$Evidence.receiptCommandVersion -or
        [UInt64]$Evidence.eventSeq -ne [UInt64]$Evidence.receiptEventSeq) { throw 'Captured incremental event does not match the typed receipt and baseline identity.' }
    if ($Evidence.afterRoomId -cne $Evidence.baselineRoomId -or $Evidence.afterSessionId -cne $Evidence.baselineSessionId -or
        $Evidence.afterTableId -cne $Evidence.baselineTableId -or $Evidence.afterRoundId -cne $Evidence.baselineRoundId -or
        [int]$Evidence.afterSeat -ne 0 -or [UInt64]$Evidence.afterBindingEpoch -ne [UInt64]$Evidence.baselineBindingEpoch -or
        [UInt64]$Evidence.afterConnectionEpoch -ne [UInt64]$Evidence.baselineConnectionEpoch -or
        [UInt64]$Evidence.afterOwnerEpoch -ne [UInt64]$Evidence.baselineOwnerEpoch -or
        [UInt64]$Evidence.afterStateVersion -ne [UInt64]$Evidence.receiptCommandVersion -or
        [UInt64]$Evidence.afterEventSeq -ne [UInt64]$Evidence.receiptEventSeq -or
        [UInt64]$Evidence.afterAcceptanceSequence -ne [UInt64]$Evidence.acceptanceSequence) { throw 'Captured same-publication snapshot is not bound to the confirmed incremental event.' }
    foreach ($field in @('baselinePlayerSnapshotBase64','afterPlayerSnapshotBase64','baselineAuthoritySaveBase64','baselineAuthorityCheatStateBase64','afterAuthoritySaveBase64','afterAuthorityCheatStateBase64')) {
        if ([string]::IsNullOrWhiteSpace([string]$Evidence.$field)) { throw "Raw typed Player/authority image is absent: $field" }
        try { if ([Convert]::FromBase64String([string]$Evidence.$field).Length -eq 0) { throw "Raw typed Player/authority image is empty: $field" } } catch { throw "Raw typed Player/authority image is invalid base64: $field" }
    }
    if ([int]$Evidence.baselinePhysicalTileCount -ne 136 -or [int]$Evidence.baselinePhysicalUniqueCount -ne 136 -or
        [int]$Evidence.afterPhysicalTileCount -ne 136 -or [int]$Evidence.afterPhysicalUniqueCount -ne 136 -or
        $Evidence.baselinePhysicalTilesSha256 -notmatch '^[0-9a-f]{64}$' -or $Evidence.afterPhysicalTilesSha256 -notmatch '^[0-9a-f]{64}$') { throw 'Durable LocalHost physical audit is not 136 unique entities.' }
    if ([string]$Evidence.handTileIds -cne [string]$Summary.handTileIdsAfter -or [string]$Evidence.meldTileIds -cne [string]$Summary.meldTileIds -or
        [string]$Evidence.drawnTileId -cne [string]$Summary.replacementDrawTileId -or [UInt64]$Evidence.seatScoreBefore -ne [UInt64]$Summary.seatScoreBefore -or
        [UInt64]$Evidence.seatScoreAfter -ne [UInt64]$Summary.seatScoreAfter -or [UInt32]$Evidence.riichiSticksBefore -ne [UInt32]$Summary.riichiSticksBefore -or
        [UInt32]$Evidence.riichiSticksAfter -ne [UInt32]$Summary.riichiSticksAfter) { throw 'Action effect summary differs from the actual accepted Player/publication state evidence.' }
    switch ($ExpectedCase) {
        'C04-G2-01' { if ($Evidence.eventTileIds -ne '0,1,2' -or $Evidence.meldKind -ne 'Pon' -or $Evidence.handTileIds -match '(^|,)(0|1|2)(,|$)') { throw 'Raw Pon entity transfer is invalid.' } }
        'C04-G2-02' { if ($Evidence.eventTileIds -ne '0,4,8' -or $Evidence.meldKind -ne 'Chi' -or $Evidence.handTileIds -match '(^|,)(0|4|8)(,|$)') { throw 'Raw Chi entity transfer is invalid.' } }
        'C04-G2-03' { if ([UInt64]$Evidence.seatScoreBefore -ne 25000 -or [UInt64]$Evidence.seatScoreAfter -ne 24000 -or [UInt32]$Evidence.riichiSticksAfter -ne ([UInt32]$Evidence.riichiSticksBefore + 1)) { throw 'Raw Riichi point/stick transfer is invalid.' } }
        'C04-G2-04' { if ($Evidence.eventTileIds -ne '0,1,2,3' -or $Evidence.meldKind -ne 'ClosedKan' -or $Evidence.handTileIds -match '(^|,)(0|1|2|3)(,|$)' -or $Evidence.drawnTileId -ne '73' -or [int]$Evidence.afterDeadWallCount -ne ([int]$Evidence.baselineDeadWallCount - 1)) { throw 'Raw Closed Kan/dead-wall replacement transfer is invalid.' } }
    }
    if (-not [string]::IsNullOrWhiteSpace([string]$script:C04VerifierPath)) {
        $verifyLog = Join-Path $logs ('protobuf-authority-verify-' + $ExpectedCase + '.stdout.txt')
        $verify = Invoke-OwnedNative -Name ('DecodeTypedProtobufAndAuthorityImage-' + $ExpectedCase) -FilePath $DotnetPath `
            -Arguments ('exec ' + (Quote-Arg $script:C04VerifierPath) + ' verify ' + (Quote-Arg $EvidencePathForVerifier)) `
            -WorkingDirectory $RunRoot -StdoutPath $verifyLog -StderrPath (Join-Path $logs ('protobuf-authority-verify-' + $ExpectedCase + '.stderr.txt')) -Timeout 30
        if ($verify.exitCode -ne 0 -or -not (Get-Content -LiteralPath $verify.stdout -Raw).Contains('C04_PROTOBUF_AND_AUTHORITY_IMAGE_PASS')) {
            throw "Generated protobuf/LocalHost authority image verifier rejected $ExpectedCase evidence; nativeExit=$($verify.exitCode)."
        }
    }
    return $true
}

if ($FixtureOnly) {
    Assert-C04RunCacheIsolation $RunRoot | Out-Null
    $ownedChildPath = Join-Path $RunRoot 'owned-timeout-child.ps1'
    $nestedPidPath = Join-Path $RunRoot 'owned-descendant.pid'
    $ownedScript = @'
$nested = Start-Process -FilePath $env:ComSpec -ArgumentList @('/c','ping -n 25 127.0.0.1 > nul') -PassThru -WindowStyle Hidden
[IO.File]::WriteAllText($env:C04_NESTED_PID_FILE,[string]$nested.Id)
Write-Output 'OWNED_TIMEOUT_CHILD_STARTED'
Start-Sleep -Seconds 25
'@
    [IO.File]::WriteAllText($ownedChildPath,$ownedScript,(New-Object Text.UTF8Encoding($false)))
    $env:C04_NESTED_PID_FILE = $nestedPidPath
    $timeoutRecord = Invoke-OwnedNative -Name 'FixtureOwnedTimeoutTree' -FilePath 'powershell.exe' -Arguments ('-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' + (Quote-Arg $ownedChildPath)) -WorkingDirectory $RunRoot -StdoutPath (Join-Path $logs 'owned-timeout.stdout.txt') -StderrPath (Join-Path $logs 'owned-timeout.stderr.txt') -Timeout 1
    Remove-Item Env:C04_NESTED_PID_FILE -ErrorAction SilentlyContinue
    if ($timeoutRecord.exitCode -ne 124 -or -not $timeoutRecord.timedOut -or -not $timeoutRecord.outputDrained -or
        (Get-Content -LiteralPath $timeoutRecord.stdout -Raw) -notmatch 'OWNED_TIMEOUT_CHILD_STARTED' -or
        -not (Test-Path -LiteralPath $nestedPidPath)) { throw 'Owned timeout/tree-kill/output-drain fixture did not retain its own process/output evidence.' }
    $descendantPid = [int](Get-Content -LiteralPath $nestedPidPath -Raw)
    $descendantAlive = $false
    try { $descendant = Get-Process -Id $descendantPid -ErrorAction Stop; $descendantAlive = $descendant.StartTime.ToUniversalTime() -gt [DateTime]::UtcNow.AddMinutes(-2) } catch { }
    if ($descendantAlive) { throw 'Owned timeout fixture descendant survived the scoped process-tree kill.' }
    Assert-C04FrozenSourceClean 0 '' | Out-Null
    foreach ($dirty in @(@{exit=0;status=' M tracked.cs'},@{exit=0;status='?? untracked.cs'},@{exit=1;status=''})) {
        $rejected = $false; try { Assert-C04FrozenSourceClean $dirty.exit $dirty.status | Out-Null } catch { $rejected = $true }
        if (-not $rejected) { throw 'Frozen source clean gate accepted a dirty tree or failed git status.' }
    }
    $archiveRepo = Join-Path $RunRoot 'archive-repo'
    $archiveFixture = Join-Path $RunRoot 'archive-fixture'
    New-Item -ItemType Directory -Path $archiveRepo,$archiveFixture | Out-Null
    $archiveInit = Invoke-OwnedNative -Name 'FixtureGitInit' -FilePath 'git.exe' -Arguments ('-C ' + (Quote-Arg $archiveRepo) + ' init') -WorkingDirectory $RunRoot -StdoutPath (Join-Path $logs 'fixture-git-init.stdout.txt') -StderrPath (Join-Path $logs 'fixture-git-init.stderr.txt') -Timeout 60
    if ($archiveInit.exitCode -ne 0) { throw 'Minimal source fixture git init failed.' }
    Invoke-OwnedNative -Name 'FixtureGitConfigName' -FilePath 'git.exe' -Arguments ('-C ' + (Quote-Arg $archiveRepo) + ' config user.name C04Fixture') -WorkingDirectory $RunRoot -StdoutPath (Join-Path $logs 'fixture-git-name.stdout.txt') -StderrPath (Join-Path $logs 'fixture-git-name.stderr.txt') -Timeout 60 | Out-Null
    Invoke-OwnedNative -Name 'FixtureGitConfigEmail' -FilePath 'git.exe' -Arguments ('-C ' + (Quote-Arg $archiveRepo) + ' config user.email c04-fixture@example.invalid') -WorkingDirectory $RunRoot -StdoutPath (Join-Path $logs 'fixture-git-email.stdout.txt') -StderrPath (Join-Path $logs 'fixture-git-email.stderr.txt') -Timeout 60 | Out-Null
    [IO.File]::WriteAllText((Join-Path $archiveRepo 'tracked.txt'),'frozen-source-fixture' + "`n",(New-Object Text.UTF8Encoding($false)))
    Invoke-OwnedNative -Name 'FixtureGitAdd' -FilePath 'git.exe' -Arguments ('-C ' + (Quote-Arg $archiveRepo) + ' add tracked.txt') -WorkingDirectory $RunRoot -StdoutPath (Join-Path $logs 'fixture-git-add.stdout.txt') -StderrPath (Join-Path $logs 'fixture-git-add.stderr.txt') -Timeout 60 | Out-Null
    Invoke-OwnedNative -Name 'FixtureGitCommit' -FilePath 'git.exe' -Arguments ('-C ' + (Quote-Arg $archiveRepo) + ' commit -m fixture') -WorkingDirectory $RunRoot -StdoutPath (Join-Path $logs 'fixture-git-commit.stdout.txt') -StderrPath (Join-Path $logs 'fixture-git-commit.stderr.txt') -Timeout 60 | Out-Null
    $archiveHead = (& git -C $archiveRepo rev-parse HEAD 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $archiveHead -notmatch '^[0-9a-f]{40}$') { throw 'Archive fixture could not resolve its minimal committed source.' }
    $archivePath = Join-Path $RunRoot 'archive-fixture.tar'
    $fixtureArchiveRecord = Invoke-OwnedNative -Name 'FixtureGitArchive' -FilePath 'git.exe' -Arguments ((@('-C',(Quote-Arg $archiveRepo),'archive','--format=tar','--output',(Quote-Arg $archivePath),$archiveHead)) -join ' ') -WorkingDirectory $RunRoot -StdoutPath (Join-Path $logs 'fixture-archive.stdout.txt') -StderrPath (Join-Path $logs 'fixture-archive.stderr.txt') -Timeout 120
    if ($fixtureArchiveRecord.exitCode -ne 0) { throw 'Fixture git archive failed.' }
    $fixtureExtract = Invoke-OwnedNative -Name 'FixtureArchiveExtract' -FilePath 'tar.exe' -Arguments ((@('-xf',(Quote-Arg $archivePath),'-C',(Quote-Arg $archiveFixture))) -join ' ') -WorkingDirectory $RunRoot -StdoutPath (Join-Path $logs 'fixture-extract.stdout.txt') -StderrPath (Join-Path $logs 'fixture-extract.stderr.txt') -Timeout 120
    if ($fixtureExtract.exitCode -ne 0) { throw 'Fixture archive extraction failed.' }
    Assert-C04ArchiveHasNoGitMetadata $archiveFixture | Out-Null
    $gitPointerFixture = Join-Path $RunRoot 'git-pointer-fixture'
    New-Item -ItemType Directory -Path $gitPointerFixture | Out-Null
    [IO.File]::WriteAllText((Join-Path $gitPointerFixture '.git'),'gitdir: E:/foreign/repository/.git' + "`n",(New-Object Text.UTF8Encoding($false)))
    $pointerRejected = $false; try { Assert-C04ArchiveHasNoGitMetadata $gitPointerFixture | Out-Null } catch { $pointerRejected = $true }
    if (-not $pointerRejected) { throw 'Archive gate accepted a .git pointer to a foreign worktree.' }
    $goodPlayXml = [xml]'<test-run><test-case name="A.Pon_FixedPlayerActionIsAppliedAndVisible" result="Passed"/><test-case name="A.Chi_FixedPlayerActionIsAppliedAndVisible" result="Passed"/><test-case name="A.Riichi_FixedPlayerActionIsAppliedAndVisible" result="Passed"/><test-case name="A.ClosedKan_FixedPlayerActionAndReplacementDrawAreApplied" result="Passed"/></test-run>'
    Test-C04ExactPlayModeXml $goodPlayXml | Out-Null
    foreach ($xmlCase in @(
        @{name='MissingPlayMode';xml=[xml]'<test-run><test-case name="A.Pon_FixedPlayerActionIsAppliedAndVisible" result="Passed"/></test-run>'},
        @{name='DuplicatePlayMode';xml=[xml]'<test-run><test-case name="A.Pon_FixedPlayerActionIsAppliedAndVisible" result="Passed"/><test-case name="B.Pon_FixedPlayerActionIsAppliedAndVisible" result="Passed"/><test-case name="A.Chi_FixedPlayerActionIsAppliedAndVisible" result="Passed"/><test-case name="A.Riichi_FixedPlayerActionIsAppliedAndVisible" result="Passed"/><test-case name="A.ClosedKan_FixedPlayerActionAndReplacementDrawAreApplied" result="Passed"/></test-run>'},
        @{name='SkippedPlayMode';xml=[xml]'<test-run><test-case name="A.Pon_FixedPlayerActionIsAppliedAndVisible" result="Passed"/><test-case name="A.Chi_FixedPlayerActionIsAppliedAndVisible" result="Passed"/><test-case name="A.Riichi_FixedPlayerActionIsAppliedAndVisible" result="Skipped"/><test-case name="A.ClosedKan_FixedPlayerActionAndReplacementDrawAreApplied" result="Passed"/></test-run>'}
    )) {
        $rejected = $false; try { Test-C04ExactPlayModeXml $xmlCase.xml | Out-Null } catch { $rejected = $true }
        if (-not $rejected) { throw "Exact PlayMode XML gate accepted invalid fixture: $($xmlCase.name)" }
    }
    $closureBeforePath = Join-Path $RunRoot 'fixture-input-before.json'; $closureAfterPath = Join-Path $RunRoot 'fixture-input-after.json'; $closureChangesPath = Join-Path $RunRoot 'fixture-input-changes.json'
    $closureBefore = [ordered]@{ entries=@([ordered]@{path='Assets/Scripts/App.cs';bytes=10;sha256=('a'*64)},[ordered]@{path='Packages/manifest.json';bytes=10;sha256=('b'*64)},[ordered]@{path='ProjectSettings/EditorBuildSettings.asset';bytes=10;sha256=('c'*64)},[ordered]@{path='Library/PackageCache/com.unity.test/package.dll';bytes=10;sha256=('d'*64)},[ordered]@{path='Library/ScriptAssemblies/App.dll';bytes=10;sha256=('e'*64)}) }
    $closureAfter = [ordered]@{ entries=@([ordered]@{path='Assets/Scripts/App.cs';bytes=10;sha256=('a'*64)},[ordered]@{path='Packages/manifest.json';bytes=10;sha256=('b'*64)},[ordered]@{path='ProjectSettings/EditorBuildSettings.asset';bytes=10;sha256=('c'*64)},[ordered]@{path='Library/PackageCache/com.unity.test/package.dll';bytes=10;sha256=('d'*64)},[ordered]@{path='Library/ScriptAssemblies/App.dll';bytes=11;sha256=('f'*64)}) }
    [IO.File]::WriteAllText($closureBeforePath,(($closureBefore|ConvertTo-Json -Depth 5)+"`n"),(New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText($closureAfterPath,(($closureAfter|ConvertTo-Json -Depth 5)+"`n"),(New-Object Text.UTF8Encoding($false)))
    $allowedChanges = Assert-C04PostBuildInputClosure $closureBeforePath $closureAfterPath $closureChangesPath
    if ($allowedChanges.Count -ne 1 -or $allowedChanges[0].classification -ne 'UnityGenerated') { throw 'Postbuild closure fixture did not retain the exact Unity-generated input hash change.' }
    foreach ($path in @('Assets/Scripts/App.cs','Packages/manifest.json','ProjectSettings/EditorBuildSettings.asset','Library/PackageCache/com.unity.test/package.dll')) {
        $badClosure = [ordered]@{ entries=@() }
        foreach ($entry in $closureBefore.entries) {
            if ($entry.path -eq $path) { $badClosure.entries += [ordered]@{path=$entry.path;bytes=$entry.bytes;sha256=('f'*64)} }
            else { $badClosure.entries += $entry }
        }
        [IO.File]::WriteAllText($closureAfterPath,(($badClosure|ConvertTo-Json -Depth 5)+"`n"),(New-Object Text.UTF8Encoding($false)))
        $rejected = $false; try { Assert-C04PostBuildInputClosure $closureBeforePath $closureAfterPath $closureChangesPath | Out-Null } catch { $rejected = $true }
        if (-not $rejected) { throw "Postbuild input closure gate accepted a source input mutation: $path" }
    }
    $packageFixtureProject = Join-Path $RunRoot 'package-closure-fixture'
    foreach ($directory in @('Assets','Packages','ProjectSettings','UserSettings','Library/PackageCache/Resolved','Library/PackageCache/MacPlugin.bundle/Contents','Library/ScriptAssemblies')) {
        New-Item -ItemType Directory -Force -Path (Join-Path $packageFixtureProject $directory) | Out-Null
    }
    foreach ($file in @('Assets/input.asset','Packages/manifest.json','ProjectSettings/EditorBuildSettings.asset','UserSettings/EditorUserSettings.asset','Library/PackageCache/Resolved/package.dll','Library/PackageCache/MacPlugin.bundle/Contents/ignored.dylib','Library/ScriptAssemblies/App.dll')) {
        [IO.File]::WriteAllText((Join-Path $packageFixtureProject $file),$file,(New-Object Text.UTF8Encoding($false)))
    }
    $packageClosurePath = Join-Path $RunRoot 'fixture-package-closure.json'
    Get-UnityInputClosure $packageFixtureProject $packageClosurePath | Out-Null
    $packageClosure = Get-Content $packageClosurePath -Raw -Encoding UTF8 | ConvertFrom-Json
    $packageClosurePaths = @($packageClosure.entries | ForEach-Object { [string]$_.path })
    if ($packageClosure.target -cne 'StandaloneWindows64' -or $packageClosurePaths -notcontains 'Library/PackageCache/Resolved/package.dll' -or
        @($packageClosurePaths | Where-Object { $_ -like 'Library/PackageCache/MacPlugin.bundle/*' }).Count -ne 0 -or
        @($packageClosure.excludedPathRules | Where-Object { $_ -like 'Library/PackageCache/MacPlugin.bundle/***StandaloneWindows64*' }).Count -ne 1) {
        throw 'Windows resolved-package closure did not bind target packages while excluding only the platform-specific macOS bundle.'
    }
    $stream = New-Object IO.MemoryStream
    $writer = New-Object IO.BinaryWriter($stream)
    $writer.Write([Text.Encoding]::ASCII.GetBytes('MJC1')); $writer.Write([UInt16]1); $writer.Write([UInt16]10)
    function Write-Field([IO.BinaryWriter]$Writer,[UInt16]$Number,[byte]$Type,[byte[]]$Value) { $Writer.Write($Number); $Writer.Write($Type); $Writer.Write([UInt32]$Value.Length); $Writer.Write($Value) }
    function Encode-CanonicalString([string]$Value) { $textBytes=[Text.Encoding]::UTF8.GetBytes($Value); $buffer=New-Object IO.MemoryStream; $stringWriter=New-Object IO.BinaryWriter($buffer); $stringWriter.Write([UInt32]$textBytes.Length); $stringWriter.Write($textBytes); $stringWriter.Flush(); return ,$buffer.ToArray() }
    Write-Field $writer 1 5 (Encode-CanonicalString 'fixture-round')
    Write-Field $writer 2 5 (Encode-CanonicalString 'fixture-decision')
    Write-Field $writer 3 3 ([BitConverter]::GetBytes([UInt64]3))
    Write-Field $writer 4 2 ([BitConverter]::GetBytes([UInt32]6))
    $tileBytes = New-Object IO.MemoryStream; $tileWriter = New-Object IO.BinaryWriter($tileBytes)
    $tileWriter.Write([UInt32]3); $tileWriter.Write([UInt32]0); $tileWriter.Write([UInt32]1); $tileWriter.Write([UInt32]2); $tileWriter.Flush()
    Write-Field $writer 6 7 $tileBytes.ToArray(); $writer.Flush(); $fixtureBytes=$stream.ToArray()
    $fixtureHash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($fixtureBytes)).Replace('-','').ToLowerInvariant()
    $validProtobufBytes = [byte[]](@(10,15) + [Text.Encoding]::UTF8.GetBytes('fixture-session') + @(18,13) + [Text.Encoding]::UTF8.GetBytes('fixture-round') + @(26,4,8,2,16,9,32,13,40,41))
    $validProtobufBase64 = [Convert]::ToBase64String($validProtobufBytes)
    $fixtureAuthorityBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('TOOL_FIXTURE_LOCALHOST_AUTHORITY_BYTES'))
    $valid = [pscustomobject]@{
        result='PASS'; status='GAME'; eventConfirmed=$true; nativeExitCode=0; nativeTimedOut=$false
        caseId='C04-G2-01'; seat=0; eventSeat=0; roomId='fixture-room'; sessionId='fixture-session'; roundId='fixture-round'
        eventRoundId='fixture-round'; requestId='fixture-request'; scopeId='fixture-scope'; receiptRequestId='fixture-request'; receiptScopeId='fixture-scope'; payloadSha256=$fixtureHash
        canonicalPayloadBase64=[Convert]::ToBase64String($fixtureBytes); receiptStatus='Applied'
        eventCommandVersion=12; receiptCommandVersion=12; receiptEventSeq=21; stateVersionBefore=11; stateVersionAfter=12; eventSeqBefore=20; eventSeqAfter=21; eventSeq=21
        bindingEpoch=1; eventBindingEpoch=1; connectionEpoch=1; eventConnectionEpoch=1; ownerEpoch=2; eventOwnerEpoch=2; controlEpoch=3; eventType='PonDeclared'
        decisionId='fixture-decision'; tableId='fixture-table'; eventScopeId='fixture-scope'; eventSessionId='fixture-session'; eventRoomId='fixture-room'; eventTableId='fixture-table'; eventRole='Player'
        cursorBefore=10; cursorAfter=11; cursorStreamBefore=1; cursorStreamAfter=1
        payloadActionType=6; payloadRoundId='fixture-round'; payloadDecisionId='fixture-decision'; payloadControlEpoch=3; candidateOptionId=''; payloadOptionId=''; payloadMeldType=0; payloadTileIds='0,1,2'
        eventTiles='0,1,2'; meldTileIds='0,1,2'; handTileIdsAfter='20,24'; seatScoreBefore=25000; seatScoreAfter=25000; riichiSticksBefore=0; riichiSticksAfter=0; replacementDrawTileId=''
    }
    Test-C04CaseSummary $valid 'C04-G2-01' | Out-Null
    $valid | Add-Member -NotePropertyName acceptanceSequence -NotePropertyValue 2 -Force
    $validEvidence = [pscustomobject]@{
        caseId='C04-G2-01'; requestId='fixture-request'; scopeId='fixture-scope'; payloadBase64=$valid.canonicalPayloadBase64; payloadSha256=$fixtureHash
        baselineRoomId='fixture-room'; baselineSessionId='fixture-session'; baselineTableId='fixture-table'; baselineRoundId='fixture-round'; baselineSeat=0
        baselineBindingEpoch=1; baselineConnectionEpoch=1; baselineOwnerEpoch=2; baselineControlEpoch=3; baselineDecisionId='fixture-decision'; baselineStateVersion=11; baselineEventSeq=20
        baselineCursorStream=1; baselineCursor=10; baselinePlayerSnapshotBase64=$validProtobufBase64; baselineAuthoritySaveBase64=$fixtureAuthorityBase64; baselineAuthorityCheatStateBase64=$fixtureAuthorityBase64; baselinePhysicalTileCount=136; baselinePhysicalUniqueCount=136; baselineDeadWallCount=14; baselinePhysicalTilesSha256=('a'*64)
        receiptDisposition='Applied'; receiptStatus='Applied'; receiptRequestId='fixture-request'; receiptScopeId='fixture-scope'; receiptDigest=$fixtureHash; receiptCommandVersion=12; receiptEventSeq=21
        eventType='PonDeclared'; eventRoundId='fixture-round'; eventScopeId='fixture-scope'; eventSessionId='fixture-session'; eventRoomId='fixture-room'; eventTableId='fixture-table'; eventSeat=0; eventBindingEpoch=1; eventConnectionEpoch=1; eventOwnerEpoch=2; eventRole='Player'; eventBatchKind='Incremental'; eventFullBaseline=$false; eventCommandVersion=12; eventSeq=21; eventTileIds='0,1,2'; acceptanceSequence=2
        afterRoomId='fixture-room'; afterSessionId='fixture-session'; afterTableId='fixture-table'; afterRoundId='fixture-round'; afterSeat=0; afterBindingEpoch=1; afterConnectionEpoch=1; afterOwnerEpoch=2
        afterStateVersion=12; afterEventSeq=21; afterCursorStream=1; afterCursor=11; afterAcceptanceSequence=2; afterPlayerSnapshotBase64=$validProtobufBase64; afterAuthoritySaveBase64=$fixtureAuthorityBase64; afterAuthorityCheatStateBase64=$fixtureAuthorityBase64
        afterPhysicalTileCount=136; afterPhysicalUniqueCount=136; afterDeadWallCount=14; afterPhysicalTilesSha256=('b'*64)
        handTileIds='20,24'; drawnTileId=''; meldKind='Pon'; meldTileIds='0,1,2'; seatScoreBefore=25000; seatScoreAfter=25000; riichiSticksBefore=0; riichiSticksAfter=0
    }
    Test-C04ActionEvidence $valid $validEvidence 'C04-G2-01' | Out-Null
    $negatives = @(
        @{ name='AppliedWithoutEvent'; update=@{ eventConfirmed=$false; eventType='' } },
        @{ name='WrongRound'; update=@{ eventRoundId='other-round' } },
        @{ name='WrongSeat'; update=@{ eventSeat=1 } },
        @{ name='WrongIdentity'; update=@{ receiptRequestId='foreign-request' } },
        @{ name='WrongEpoch'; update=@{ bindingEpoch=999 } },
        @{ name='NativeNonzero'; update=@{ nativeExitCode=17 } },
        @{ name='NativeTimeout'; update=@{ nativeTimedOut=$true } },
        @{ name='MissingCase'; update=@{ caseId='' } },
        @{ name='MissingPayload'; update=@{ canonicalPayloadBase64='' } },
        @{ name='HashMismatch'; update=@{ payloadSha256=('0' * 64) } },
        @{ name='CommandBeforeBaseline'; update=@{ receiptCommandVersion=1; eventCommandVersion=1 } },
        @{ name='CursorRollbackAndMissingDecision'; update=@{ cursorAfter=0; payloadDecisionId='' } },
        @{ name='WrongAction'; update=@{ payloadActionType=1 } },
        @{ name='WrongPhysicalEntities'; update=@{ eventTiles='0,1,3' } },
        @{ name='ForeignBatchIdentity'; update=@{ eventScopeId='foreign-scope' } },
        @{ name='EmptyEvidenceFacts'; update=@{ eventTiles=''; meldTileIds=''; roomId='' } },
        @{ name='WrongControlEpoch'; update=@{ controlEpoch=999; payloadControlEpoch=999 } },
        @{ name='WrongOwnerEpoch'; update=@{ ownerEpoch=999 } },
        @{ name='WrongEventCommandVersion'; update=@{ eventCommandVersion=11 } },
        @{ name='WrongMeldTransfer'; update=@{ meldTileIds='0,1,3' } }
        ,@{ name='PonClaimedTileStillInHand'; update=@{ handTileIdsAfter='2,20,24' } }
        ,@{ name='MissingPostHand'; update=@{ handTileIdsAfter='' } }
        ,@{ name='RiichiTwoSticks'; update=@{ riichiSticksAfter=2 } }
        ,@{ name='CoherentBindingEpochRewrite'; update=@{ bindingEpoch=999; eventBindingEpoch=999 } }
        ,@{ name='CoherentOwnerEpochRewrite'; update=@{ ownerEpoch=999; eventOwnerEpoch=999 } }
    )
    foreach ($negative in $negatives) {
        $candidate = $valid | Select-Object *
        foreach ($key in $negative.update.Keys) { $candidate.$key = $negative.update[$key] }
        $rejected = $false
        try { Test-C04CaseSummary $candidate 'C04-G2-01' | Out-Null; Test-C04ActionEvidence $candidate $validEvidence 'C04-G2-01' | Out-Null } catch { $rejected = $true }
        if (-not $rejected) { throw "Fixture guard accepted invalid case: $($negative.name)" }
    }
    $arbitrary = $valid | Select-Object *
    $arbitraryBytes = [Text.Encoding]::UTF8.GetBytes('SubmitAction ASCII with a recomputed self-consistent hash')
    $arbitrary.canonicalPayloadBase64 = [Convert]::ToBase64String($arbitraryBytes)
    $arbitrary.payloadSha256 = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($arbitraryBytes)).Replace('-','').ToLowerInvariant()
    $arbitraryRejected = $false
    try { Test-C04CaseSummary $arbitrary 'C04-G2-01' | Out-Null } catch { $arbitraryRejected = $true }
    if (-not $arbitraryRejected) { throw 'Fixture guard accepted arbitrary ASCII after recomputing its hash.' }
    $evidenceNegatives = @(
        @{ name='MissingRawReceipt'; update=@{ receiptCommandVersion=0 } },
        @{ name='MissingBaselineSnapshot'; update=@{ baselinePlayerSnapshotBase64='' } },
        @{ name='WrongRawBindingEpoch'; update=@{ eventBindingEpoch=999 } },
        @{ name='WrongPhysicalUniqueCount'; update=@{ afterPhysicalUniqueCount=135 } },
        @{ name='WrongPhysicalCount'; update=@{ afterPhysicalTileCount=135 } },
        @{ name='WrongSamePublication'; update=@{ afterStateVersion=13 } },
        @{ name='WrongRawActionEffect'; update=@{ handTileIds='2,20,24' } }
    )
    foreach ($negative in $evidenceNegatives) {
        $candidateEvidence = $validEvidence | Select-Object *
        foreach ($key in $negative.update.Keys) { $candidateEvidence.$key = $negative.update[$key] }
        $rejected = $false
        try { Test-C04ActionEvidence $valid $candidateEvidence 'C04-G2-01' | Out-Null } catch { $rejected = $true }
        if (-not $rejected) { throw "Fixture evidence guard accepted invalid action evidence: $($negative.name)" }
    }
    $fixtureExe = Join-Path $RunRoot 'fixture-player.exe'
    [IO.File]::WriteAllText($fixtureExe,'fixture-player-bytes',(New-Object Text.UTF8Encoding($false)))
    $fixtureScenes = @('Assets/Scenes/Boot.unity','Assets/Scenes/Menu.unity')
    $fixtureExeHash = Get-Sha256 $fixtureExe
    $fixtureIdentityInput = 'StandaloneWindows64' + "`n" + $fixtureExeHash + "`n" + ($fixtureScenes -join "`n")
    $fixtureIdentity = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($fixtureIdentityInput))).Replace('-','').ToLowerInvariant()
    $fixtureMarker = Join-Path $RunRoot 'fixture-player-marker.json'
    $markerObject = [ordered]@{ result='Succeeded'; target='StandaloneWindows64'; errors=0; bytes=(Get-Item $fixtureExe).Length; output='fixture-player.exe'; outputSha256=$fixtureExeHash; buildIdentity=$fixtureIdentity; scenes=$fixtureScenes }
    [IO.File]::WriteAllText($fixtureMarker,(($markerObject | ConvertTo-Json -Depth 5)+"`n"),(New-Object Text.UTF8Encoding($false)))
    Test-C04BuildMarker $fixtureMarker $fixtureExe $fixtureScenes | Out-Null
    $markerNegatives = @(
        @{ name='MissingErrors'; update=@{ errors=$null } },
        @{ name='WrongTarget'; update=@{ target='Windows_FAKE' } },
        @{ name='WrongScenes'; update=@{ scenes=@('Assets/FakeBoot.unity') } },
        @{ name='WrongOutputHash'; update=@{ outputSha256=('0'*64) } },
        @{ name='WrongResult'; update=@{ result='Failed' } },
        @{ name='BooleanErrors'; update=@{ errors=$false } },
        @{ name='FractionalErrors'; update=@{ errors=0.1 } },
        @{ name='FractionalBytes'; update=@{ bytes=([double]$markerObject.bytes + 0.1) } },
        @{ name='StringErrors'; update=@{ errors='0' } },
        @{ name='NegativeErrors'; update=@{ errors=-1 } },
        @{ name='BooleanBytes'; update=@{ bytes=$true } }
    )
    foreach ($negative in $markerNegatives) {
        $badMarker = [ordered]@{}
        foreach ($key in $markerObject.Keys) { $badMarker[$key] = $markerObject[$key] }
        foreach ($key in $negative.update.Keys) { $badMarker[$key] = $negative.update[$key] }
        $badPath = Join-Path $RunRoot ('marker-' + $negative.name + '.json')
        [IO.File]::WriteAllText($badPath,(($badMarker | ConvertTo-Json -Depth 5)+"`n"),(New-Object Text.UTF8Encoding($false)))
        $rejected = $false
        try { Test-C04BuildMarker $badPath $fixtureExe $fixtureScenes | Out-Null } catch { $rejected = $true }
        if (-not $rejected) { throw "Fixture marker gate accepted contradictory marker: $($negative.name)" }
    }
    [IO.File]::WriteAllText((Join-Path $RunRoot 'fixture-result.json'), (($valid | ConvertTo-Json -Depth 5) + "`n"), (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText((Join-Path $RunRoot 'foreign-sentinel.txt'), 'foreign-owner-preserved' + "`n", (New-Object Text.UTF8Encoding($false)))
    $stableFixtureFile = Join-Path $RunRoot 'logs/stable-review-log.txt'
    $volatileFixtureFile = Join-Path $RunRoot 'p/Library/PackageCache/cache-entry.tmp'
    $upmVolatileFixtureFile = Join-Path $RunRoot 'upm-cache/cache-entry.tmp'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $stableFixtureFile),(Split-Path -Parent $volatileFixtureFile),(Split-Path -Parent $upmVolatileFixtureFile) | Out-Null
    [IO.File]::WriteAllText($stableFixtureFile, 'stable-log-content' + "`n", (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText($volatileFixtureFile, 'volatile-cache-content' + "`n", (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText($upmVolatileFixtureFile, 'volatile-upm-cache-content' + "`n", (New-Object Text.UTF8Encoding($false)))
    $stableArtifacts = Get-C04StableArtifactFiles $RunRoot
    $stablePaths = @($stableArtifacts | ForEach-Object { $_.FullName.Substring($RunRoot.Length).TrimStart('\','/').Replace('\','/') })
    if ($stablePaths -notcontains 'logs/stable-review-log.txt' -or $stablePaths -contains 'p/Library/PackageCache/cache-entry.tmp' -or $stablePaths -contains 'upm-cache/cache-entry.tmp') {
        throw 'Stable artifact manifest did not retain logs or separate volatile Unity/UPM package caches.'
    }
    $verifierProject = Join-Path $repo 'dotnet/tools/C04PlayerActionVerifier/C04PlayerActionVerifier.csproj'
    $fixtureDotnetArtifacts = Join-Path $RunRoot 'verifier-dotnet-artifacts'
    $verifierBuildArgs = @('build',(Quote-Arg $verifierProject),'--configuration','Release','--artifacts-path',(Quote-Arg $fixtureDotnetArtifacts)) -join ' '
    $verifierBuild = Invoke-OwnedNative -Name 'FixtureFreshC04VerifierBuild' -FilePath $DotnetPath -Arguments $verifierBuildArgs -WorkingDirectory $repo -StdoutPath (Join-Path $logs 'fixture-verifier-build.stdout.txt') -StderrPath (Join-Path $logs 'fixture-verifier-build.stderr.txt') -Timeout 600
    if ($verifierBuild.exitCode -ne 0) { throw 'Fresh protobuf verifier fixture build failed.' }
    $fixtureVerifier = Join-Path $fixtureDotnetArtifacts 'bin/C04PlayerActionVerifier/release/Mahjong.C04PlayerActionVerifier.dll'
    $verifierFixture = Invoke-OwnedNative -Name 'FixtureGeneratedPlayerSnapshotParser' -FilePath $DotnetPath -Arguments ('exec ' + (Quote-Arg $fixtureVerifier) + ' selftest') -WorkingDirectory $RunRoot -StdoutPath (Join-Path $logs 'fixture-protobuf-parser.stdout.txt') -StderrPath (Join-Path $logs 'fixture-protobuf-parser.stderr.txt') -Timeout 30
    if ($verifierFixture.exitCode -ne 0 -or (Get-Content -LiteralPath $verifierFixture.stdout -Raw) -notmatch 'C04_PROTOBUF_FIXTURE_PASS valid=1 invalid=2') { throw 'Generated-code verifier did not accept a legal serialized snapshot and reject both malformed byte fixtures.' }
    $fixtureFiles = Get-ChildItem -LiteralPath $RunRoot -Recurse -File | Sort-Object FullName | ForEach-Object { [ordered]@{ path=$_.FullName.Substring($RunRoot.Length).TrimStart('\','/'); sha256=(Get-Sha256 $_.FullName); bytes=$_.Length } }
    [IO.File]::WriteAllText((Join-Path $RunRoot 'fixture-seal.json'), (($fixtureFiles | ConvertTo-Json -Depth 5) + "`n"), (New-Object Text.UTF8Encoding($false)))
    Write-Host "C04_TOOL_FIXTURES_PASS positive=1 negative=$($negatives.Count + $evidenceNegatives.Count + $markerNegatives.Count + 12) protobuf=PASS malformed=2 timeoutTree=PASS outputDrain=PASS outputFiles=$((Get-ChildItem -LiteralPath $RunRoot -Recurse -File).Count)"
    exit 0
}

try {
    $mutexName = 'Local\MahjongV5P45UnityValidation'
    $script:mutex = New-Object System.Threading.Mutex($false, $mutexName)
    $waitStart = [DateTimeOffset]::UtcNow
    Write-RunLog "WAIT_MUTEX name=$mutexName root=$RunRoot"
    try { $script:mutexHeld = $script:mutex.WaitOne([TimeSpan]::FromMinutes(60)) } catch [System.Threading.AbandonedMutexException] { $script:mutexHeld = $true }
    if (-not $script:mutexHeld) { throw 'Timed out waiting for the shared Unity validation mutex; no Unity stage ran.' }
    Write-RunLog ('MUTEX_ACQUIRED waitedSeconds=' + ([DateTimeOffset]::UtcNow - $waitStart).TotalSeconds.ToString('F3'))
    Assert-C04RunCacheIsolation $RunRoot | Out-Null
    Write-RunLog "RUN_CACHE_ROOTS upm=$upmRoot npm=$upmNpmRoot packages=$upmPackagesRoot"

    $gitHead = (& git -C $repo rev-parse HEAD 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $gitHead -notmatch '^[0-9a-f]{40}$') { throw 'Unable to resolve frozen candidate HEAD.' }
    $gitStatusLines = @(& git -C $repo status --short 2>&1)
    if ($LASTEXITCODE -ne 0) { throw 'git status failed before source staging.' }
    $gitStatus = ($gitStatusLines -join "`n").Trim()
    Assert-C04FrozenSourceClean $LASTEXITCODE $gitStatus | Out-Null
    $gitDiffCheck = & git -C $repo diff --check 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'Source diff --check failed before build.' }
    New-Item -ItemType Directory -Path $source | Out-Null
    $archive = Join-Path $RunRoot 'frozen-source.tar'
    $archiveArgs = @('-C',(Quote-Arg $repo),'archive','--format=tar','--output',(Quote-Arg $archive),$gitHead) -join ' '
    $archiveRecord = Invoke-OwnedNative -Name 'ArchiveFrozenCommit' -FilePath 'git.exe' -Arguments $archiveArgs -WorkingDirectory $repo -StdoutPath (Join-Path $logs 'archive.stdout.txt') -StderrPath (Join-Path $logs 'archive.stderr.txt') -Timeout 180
    if ($archiveRecord.exitCode -ne 0 -or -not (Test-Path -LiteralPath $archive)) { throw 'Frozen commit archive failed.' }
    $extractArgs = @('-xf',(Quote-Arg $archive),'-C',(Quote-Arg $source)) -join ' '
    $extract = Invoke-OwnedNative -Name 'ExtractFrozenCommit' -FilePath 'tar.exe' -Arguments $extractArgs -WorkingDirectory $RunRoot -StdoutPath (Join-Path $logs 'extract.stdout.txt') -StderrPath (Join-Path $logs 'extract.stderr.txt') -Timeout 300
    if ($extract.exitCode -ne 0) { throw 'Frozen source archive extraction failed.' }
    Assert-C04ArchiveHasNoGitMetadata $source | Out-Null
    $sourceClosureSha = Get-ClosureManifest $source (Join-Path $RunRoot 'source-prebuild-closure.json')
    $sourceProject = Join-Path $source 'unity/Mahjong.Unity'
    $projectStageRoot = Join-Path 'E:\majiang-review\c04-player-actions-projects' $runId
    if (Test-Path -LiteralPath $projectStageRoot) { throw 'Exclusive short-path Unity project staging root already exists; refusing to overwrite.' }
    New-Item -ItemType Directory -Path $projectStageRoot | Out-Null
    $project = Join-Path $projectStageRoot 'p'
    $script:unityProjectRoot = $project
    Copy-Item -LiteralPath $sourceProject -Destination $project -Recurse
    $shortPathProjectSha = Assert-C04UnityProjectCopy $sourceProject $project (Join-Path $RunRoot 'unity-short-path-staging.json')
    $userSettings = Join-Path $project 'UserSettings/EditorUserSettings.asset'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $userSettings) | Out-Null
    $isolatedImportSettings = @'
%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!162 &1
EditorUserSettings:
  m_ObjectHideFlags: 0
  serializedVersion: 5
  m_CompressAssetsOnImport: 1
'@
    [IO.File]::WriteAllText($userSettings, $isolatedImportSettings + "`n", (New-Object Text.UTF8Encoding($false)))
    $isolatedImportSettingsSha = Get-Sha256 $userSettings
    [IO.File]::WriteAllText((Join-Path $RunRoot 'unity-generated-inputs.json'), (([ordered]@{ path='p/UserSettings/EditorUserSettings.asset'; purpose='per-run project-scoped setting in the hash-verified short-path project copy; does not change machine preferences'; sha256=$isolatedImportSettingsSha; bytes=(Get-Item -LiteralPath $userSettings).Length } | ConvertTo-Json -Depth 4) + "`n"), (New-Object Text.UTF8Encoding($false)))

    $dotnetResults = Join-Path $RunRoot 'dotnet-results'
    $dotnetArtifacts = Join-Path $RunRoot 'dotnet-artifacts'
    New-Item -ItemType Directory -Path $dotnetResults,$dotnetArtifacts | Out-Null
    $dotnetProject = Join-Path $source 'dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj'
    $buildArgs = @('build',(Quote-Arg $dotnetProject),'--configuration','Release','--artifacts-path',(Quote-Arg $dotnetArtifacts)) -join ' '
    $localHostBuild = Invoke-OwnedNative -Name 'FreshLocalHostReleaseBuild' -FilePath $DotnetPath -Arguments $buildArgs -WorkingDirectory $source -StdoutPath (Join-Path $logs 'localhost-build.stdout.txt') -StderrPath (Join-Path $logs 'localhost-build.stderr.txt') -Timeout 1200
    if ($localHostBuild.exitCode -ne 0) { throw "Fresh LocalHost Release build failed; nativeExit=$($localHostBuild.exitCode)." }
    $testAssembly = Join-Path $dotnetArtifacts 'bin/Mahjong.LocalHost.Tests/release/Mahjong.LocalHost.Tests.dll'
    if (-not (Test-Path -LiteralPath $testAssembly)) { throw 'Fresh LocalHost build did not produce its test assembly.' }
    $dotnetXml = Join-Path $dotnetResults 'LocalHost.xml'
    $testArgs = @('exec',(Quote-Arg $testAssembly),'-noLogo','-automated','-failSkips','-result-xml',(Quote-Arg $dotnetXml)) -join ' '
    $localHostTests = Invoke-OwnedNative -Name 'FreshLocalHostFullTests' -FilePath $DotnetPath -Arguments $testArgs -WorkingDirectory $dotnetResults -StdoutPath (Join-Path $logs 'localhost.stdout.txt') -StderrPath (Join-Path $logs 'localhost.stderr.txt') -Timeout 1200
    if (-not (Test-Path -LiteralPath $dotnetXml)) { throw "Fresh LocalHost tests produced no native xUnit XML; nativeExit=$($localHostTests.exitCode)." }
    [xml]$trxXml = Get-Content -LiteralPath $dotnetXml -Raw
    $testAssemblies = @($trxXml.SelectNodes("//*[local-name()='assemblies']/*[local-name()='assembly']"))
    if ($testAssemblies.Count -ne 1) { throw "LocalHost XML must contain exactly one assembly result; count=$($testAssemblies.Count)." }
    $testAssemblyNode = $testAssemblies[0]
    $totalTests = [int]$testAssemblyNode.GetAttribute('total'); $passedTests = [int]$testAssemblyNode.GetAttribute('passed')
    $failedTests = [int]$testAssemblyNode.GetAttribute('failed'); $skippedTests = [int]$testAssemblyNode.GetAttribute('skipped')
    if ($totalTests -le 0 -or $passedTests + $failedTests + $skippedTests -ne $totalTests) { throw "LocalHost XML counts are incomplete; total=$totalTests passed=$passedTests failed=$failedTests skipped=$skippedTests." }
    if ($localHostTests.exitCode -ne 0 -or $passedTests -ne $totalTests -or $failedTests -ne 0 -or $skippedTests -ne 0) { throw "Fresh LocalHost tests did not fully pass; nativeExit=$($localHostTests.exitCode) total=$totalTests passed=$passedTests failed=$failedTests skipped=$skippedTests." }
    $runtimeAssembly = Join-Path $dotnetArtifacts 'bin/Mahjong.TableRuntime.Tests/release/Mahjong.TableRuntime.Tests.dll'
    if (-not (Test-Path -LiteralPath $runtimeAssembly -PathType Leaf)) { throw 'Fresh LocalHost build did not produce the affected TableRuntime test assembly.' }
    $runtimeXml = Join-Path $dotnetResults 'TableRuntime.xml'
    $runtimeArgs = @('exec',(Quote-Arg $runtimeAssembly),'-noLogo','-automated','-failSkips','-result-xml',(Quote-Arg $runtimeXml)) -join ' '
    $runtimeTests = Invoke-OwnedNative -Name 'FreshAffectedTableRuntimeTests' -FilePath $DotnetPath -Arguments $runtimeArgs -WorkingDirectory $dotnetResults -StdoutPath (Join-Path $logs 'table-runtime.stdout.txt') -StderrPath (Join-Path $logs 'table-runtime.stderr.txt') -Timeout 1200
    if (-not (Test-Path -LiteralPath $runtimeXml -PathType Leaf)) { throw "Fresh TableRuntime tests produced no native xUnit XML; nativeExit=$($runtimeTests.exitCode)." }
    [xml]$runtimeResultsXml = Get-Content -LiteralPath $runtimeXml -Raw -Encoding UTF8
    $runtimeAssemblies = @($runtimeResultsXml.SelectNodes("//*[local-name()='assemblies']/*[local-name()='assembly']"))
    if ($runtimeAssemblies.Count -ne 1) { throw "TableRuntime XML must contain exactly one assembly result; count=$($runtimeAssemblies.Count)." }
    $runtimeNode = $runtimeAssemblies[0]
    $runtimeTotal = [int]$runtimeNode.GetAttribute('total'); $runtimePassed = [int]$runtimeNode.GetAttribute('passed')
    $runtimeFailed = [int]$runtimeNode.GetAttribute('failed'); $runtimeSkipped = [int]$runtimeNode.GetAttribute('skipped')
    if ($runtimeTotal -le 0 -or $runtimePassed + $runtimeFailed + $runtimeSkipped -ne $runtimeTotal -or
        $runtimeTests.exitCode -ne 0 -or $runtimePassed -ne $runtimeTotal -or $runtimeFailed -ne 0 -or $runtimeSkipped -ne 0) {
        throw "Affected TableRuntime tests did not fully pass; nativeExit=$($runtimeTests.exitCode) total=$runtimeTotal passed=$runtimePassed failed=$runtimeFailed skipped=$runtimeSkipped."
    }
    $verifierProject = Join-Path $source 'dotnet/tools/C04PlayerActionVerifier/C04PlayerActionVerifier.csproj'
    $verifierBuildArgs = @('build',(Quote-Arg $verifierProject),'--configuration','Release','--artifacts-path',(Quote-Arg $dotnetArtifacts)) -join ' '
    $verifierBuild = Invoke-OwnedNative -Name 'FreshC04ActionEvidenceVerifierBuild' -FilePath $DotnetPath -Arguments $verifierBuildArgs -WorkingDirectory $source -StdoutPath (Join-Path $logs 'c04-verifier-build.stdout.txt') -StderrPath (Join-Path $logs 'c04-verifier-build.stderr.txt') -Timeout 600
    if ($verifierBuild.exitCode -ne 0) { throw 'Fresh generated-code verifier build failed.' }
    $script:C04VerifierPath = Join-Path $dotnetArtifacts 'bin/C04PlayerActionVerifier/release/Mahjong.C04PlayerActionVerifier.dll'
    if (-not (Test-Path -LiteralPath $script:C04VerifierPath -PathType Leaf)) { throw 'Generated-code verifier DLL is missing from the fresh artifact root.' }
    $verifierSelfTest = Invoke-OwnedNative -Name 'C04GeneratedProtobufVerifierSelfTest' -FilePath $DotnetPath -Arguments ('exec ' + (Quote-Arg $script:C04VerifierPath) + ' selftest') -WorkingDirectory $dotnetResults -StdoutPath (Join-Path $logs 'c04-verifier-selftest.stdout.txt') -StderrPath (Join-Path $logs 'c04-verifier-selftest.stderr.txt') -Timeout 30
    if ($verifierSelfTest.exitCode -ne 0 -or (Get-Content -LiteralPath $verifierSelfTest.stdout -Raw) -notmatch 'C04_PROTOBUF_FIXTURE_PASS valid=1 invalid=2') { throw 'Generated PlayerSnapshot.Parser positive/malformed-byte fixture failed.' }
    $dotnetClosureSha = Get-ClosureManifest $dotnetArtifacts (Join-Path $RunRoot 'dotnet-build-artifacts-closure.json')
    $localHostTestEvidence = [ordered]@{ buildCommand=$DotnetPath + ' ' + $buildArgs; buildNativeExitCode=$localHostBuild.exitCode; testCommand=$DotnetPath + ' ' + $testArgs; nativeExitCode=$localHostTests.exitCode; total=$totalTests; passed=$passedTests; failed=$failedTests; skipped=$skippedTests; xml='dotnet-results/LocalHost.xml'; xmlSha256=(Get-Sha256 $dotnetXml); affectedTableRuntime=[ordered]@{ command=$DotnetPath + ' ' + $runtimeArgs; nativeExitCode=$runtimeTests.exitCode; total=$runtimeTotal; passed=$runtimePassed; failed=$runtimeFailed; skipped=$runtimeSkipped; xml='dotnet-results/TableRuntime.xml'; xmlSha256=(Get-Sha256 $runtimeXml); stdoutSha256=$runtimeTests.stdoutSha256; stderrSha256=$runtimeTests.stderrSha256 }; buildArtifacts='dotnet-build-artifacts-closure.json'; buildArtifactsSha256=$dotnetClosureSha; buildStdoutSha256=$localHostBuild.stdoutSha256; buildStderrSha256=$localHostBuild.stderrSha256; stdoutSha256=$localHostTests.stdoutSha256; stderrSha256=$localHostTests.stderrSha256 }

    $projectPlugins = Join-Path $project 'Assets/Plugins/Mahjong'
    $frozenEnabledScenes = Get-EnabledScenes $project
    $sharedLog = Join-Path $logs 'build-shared.log'
    $sharedArgs = @('-NoLogo','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',(Quote-Arg (Join-Path $source 'tools/unity/build-shared.ps1')),'-Configuration','Release','-DotnetPath',(Quote-Arg $DotnetPath),'-PluginOutputDir',(Quote-Arg $projectPlugins)) -join ' '
    $shared = Invoke-OwnedNative -Name 'BuildSharedClosure' -FilePath 'powershell.exe' -Arguments $sharedArgs -WorkingDirectory $source -StdoutPath $sharedLog -StderrPath (Join-Path $logs 'build-shared.stderr.txt') -Timeout 900
    if ($shared.exitCode -ne 0) { throw 'Fresh shared plugin build failed; Unity stages were blocked.' }
    $sharedEvidence = Join-Path $projectPlugins 'shared-plugin-provenance.json'
    $guardArgs = @('-NoLogo','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',(Quote-Arg (Join-Path $source 'tools/unity/check-shared-plugins.ps1')),'-ProjectRoot',(Quote-Arg $source),'-PluginDir',(Quote-Arg $projectPlugins),'-EvidencePath',(Quote-Arg $sharedEvidence),'-Quiet') -join ' '
    $guard = Invoke-OwnedNative -Name 'VerifySharedPluginProvenance' -FilePath 'powershell.exe' -Arguments $guardArgs -WorkingDirectory $source -StdoutPath (Join-Path $logs 'shared-provenance.stdout.txt') -StderrPath (Join-Path $logs 'shared-provenance.stderr.txt') -Timeout 300
    if ($guard.exitCode -ne 0) { throw 'Fresh shared plugin provenance gate failed.' }
    $checkCopied = Invoke-OwnedNative -Name 'VerifyCopiedProjectPlugins' -FilePath 'powershell.exe' -Arguments $guardArgs -WorkingDirectory $source -StdoutPath (Join-Path $logs 'copied-plugin.stdout.txt') -StderrPath (Join-Path $logs 'copied-plugin.stderr.txt') -Timeout 300
    if ($checkCopied.exitCode -ne 0) { throw 'Freshly built Unity project plugin closure failed provenance validation.' }
    $pluginClosureSha = Get-ClosureManifest $projectPlugins (Join-Path $RunRoot 'shared-plugin-postbuild-closure.json')
    $provenanceSha = Get-Sha256 $sharedEvidence

    $warmupLog = Join-Path $logs 'unity-package-import-warmup.unity.log'
    $warmupArgs = @('-batchmode','-projectPath',(Quote-Arg $project),'-logFile',(Quote-Arg $warmupLog),'-quit') -join ' '
    $warmup = Invoke-OwnedNative -Name 'UnityResolvedPackageImportWarmup' -FilePath $UnityPath -Arguments $warmupArgs -WorkingDirectory $project -StdoutPath (Join-Path $logs 'unity-package-import-warmup.stdout.txt') -StderrPath (Join-Path $logs 'unity-package-import-warmup.stderr.txt') -Timeout 900
    if ($warmup.exitCode -ne 0) { throw 'Unity resolved-package import warmup returned a nonzero native exit.' }
    $resolvedPackageInputsSha = Get-UnityInputClosure $project (Join-Path $RunRoot 'unity-resolved-package-input-closure.json')

    $playXml = Join-Path $logs 'playmode.xml'; $playLog = Join-Path $logs 'playmode.unity.log'
    $unityTestArgs = @('-batchmode','-projectPath',(Quote-Arg $project),'-runTests','-testPlatform','PlayMode','-testFilter','Mahjong.Unity.Tests.PlayMode.C04PlayerActionScenarioTests','-testResults',(Quote-Arg $playXml),'-logFile',(Quote-Arg $playLog)) -join ' '
    $play = Invoke-OwnedNative -Name 'UnityPlayModeC04' -FilePath $UnityPath -Arguments $unityTestArgs -WorkingDirectory $project -StdoutPath (Join-Path $logs 'playmode.stdout.txt') -StderrPath (Join-Path $logs 'playmode.stderr.txt') -Timeout 900
    if ($play.exitCode -ne 0 -or -not (Test-Path -LiteralPath $playXml)) { throw 'Targeted Unity PlayMode run failed or produced no XML.' }
    [xml]$playResults = Get-Content -LiteralPath $playXml -Raw
    $testNodes = @($playResults.SelectNodes('//test-case'))
    $actualNames = Test-C04ExactPlayModeXml $playResults

    $preBuildInputsSha = Get-UnityInputClosure $project (Join-Path $RunRoot 'unity-prebuild-input-closure.json')
    New-Item -ItemType Directory -Path $playerDir | Out-Null
    $exe = Join-Path $playerDir 'Mahjong.Unity.exe'
    $buildMarker = Join-Path $logs 'player-build.json'
    $buildArgs = @('-batchmode','-projectPath',(Quote-Arg $project),'-executeMethod','C04PlayerActionBuild.Build','-c04-player-output',(Quote-Arg $exe),'-c04-player-marker',(Quote-Arg $buildMarker),'-logFile',(Quote-Arg (Join-Path $logs 'player-build.unity.log')),'-quit') -join ' '
    $build = Invoke-OwnedNative -Name 'BuildFreshWindowsPlayer' -FilePath $UnityPath -Arguments $buildArgs -WorkingDirectory $project -StdoutPath (Join-Path $logs 'player-build.stdout.txt') -StderrPath (Join-Path $logs 'player-build.stderr.txt') -Timeout 1200
    if ($build.exitCode -ne 0) { throw 'Fresh Windows Player build returned a nonzero native exit.' }
    $markerValue = Test-C04BuildMarker $buildMarker $exe $frozenEnabledScenes
    $postBuildInputsSha = Get-UnityInputClosure $project (Join-Path $RunRoot 'unity-postbuild-input-closure.json')
    $inputChanges = Assert-C04PostBuildInputClosure (Join-Path $RunRoot 'unity-prebuild-input-closure.json') `
        (Join-Path $RunRoot 'unity-postbuild-input-closure.json') (Join-Path $RunRoot 'unity-postbuild-input-changes.json')
    $postBuildClosureSha = Get-ClosureManifest (Join-Path $project 'Assets') (Join-Path $RunRoot 'source-postbuild-assets-closure.json')
    $scriptAssembliesSha = Get-ClosureManifest (Join-Path $project 'Library/ScriptAssemblies') (Join-Path $RunRoot 'unity-script-assemblies-closure.json')

    $playerFiles = New-Object System.Collections.Generic.List[object]
    foreach ($file in Get-ChildItem -LiteralPath $playerDir -Recurse -File | Sort-Object FullName) {
        $playerFiles.Add([ordered]@{ path=$file.FullName.Substring($playerDir.Length).TrimStart('\','/').Replace('\','/'); bytes=[long]$file.Length; sha256=(Get-Sha256 $file.FullName) }) | Out-Null
    }
    [IO.File]::WriteAllText((Join-Path $RunRoot 'player-package.json'), (($playerFiles | ConvertTo-Json -Depth 5) + "`n"), (New-Object Text.UTF8Encoding($false)))
    $upmCacheClosureSha = Get-ClosureManifest $upmRoot (Join-Path $RunRoot 'upm-cache-closure.json')

    $caseSpecs = @(
        @{ id='C04-G2-01'; action='pon'; event='PonDeclared' },
        @{ id='C04-G2-02'; action='chi'; event='ChiDeclared' },
        @{ id='C04-G2-03'; action='riichi'; event='RiichiDeclared' },
        @{ id='C04-G2-04'; action='closed-kan'; event='KanDeclared' }
    )
    $caseResults = New-Object System.Collections.Generic.List[object]
    foreach ($case in $caseSpecs) {
        $caseDir = Join-Path $caseRoot $case.id; $caseData = Join-Path $dataRoot $case.id
        New-Item -ItemType Directory -Path $caseDir,$caseData | Out-Null
        $nativeLog = Join-Path $caseDir 'player.log'
        $playerArgs = @('-batchmode','-runInBackground','-smoke-data',(Quote-Arg $caseData),'-logFile',(Quote-Arg $nativeLog),'-c04-player-actions',(Quote-Arg $caseDir),'-c04-player-action-case',$case.id,'-c04-player-action-run-id',$runId)
        $player = Invoke-OwnedNative -Name ('Player-' + $case.id) -FilePath $exe -Arguments ($playerArgs -join ' ') -WorkingDirectory $playerDir -StdoutPath (Join-Path $caseDir 'stdout.txt') -StderrPath (Join-Path $caseDir 'stderr.txt') -Timeout $TimeoutSeconds
        $summaryPath = Join-Path $caseDir 'summary.json'
        if ($player.exitCode -ne 0 -or -not (Test-Path -LiteralPath $summaryPath)) { throw "Player $($case.id) native exit $($player.exitCode), summary missing or nonzero." }
        $summary = ConvertFrom-C04PlayerSummary $summaryPath
        if ($summary.eventType -ne $case.event) { throw "Player $($case.id) visible event mismatch: $($summary.eventType)." }
        $actionEvidencePath = Join-Path $caseDir 'player-action.jsonl'
        if (-not (Test-Path -LiteralPath $actionEvidencePath -PathType Leaf)) { throw "Player $($case.id) raw typed action evidence is missing." }
        $actionLines = @(Get-Content -LiteralPath $actionEvidencePath -Encoding UTF8)
        if ($actionLines.Count -ne 1 -or [string]::IsNullOrWhiteSpace($actionLines[0])) { throw "Player $($case.id) evidence must contain exactly one stable action record." }
        $actionEvidence = $actionLines[0] | ConvertFrom-Json
        $summary | Add-Member -NotePropertyName nativeExitCode -NotePropertyValue $player.exitCode -Force
        $summary | Add-Member -NotePropertyName nativeTimedOut -NotePropertyValue $player.timedOut -Force
        $executionPath = Join-Path $caseDir 'execution.json'
        [IO.File]::WriteAllText($executionPath, (($summary | ConvertTo-Json -Depth 8) + "`n"), (New-Object Text.UTF8Encoding($false)))
        Test-C04CaseSummary $summary $case.id | Out-Null
        Test-C04ActionEvidence $summary $actionEvidence $case.id $actionEvidencePath | Out-Null
        if ($summary.runId -ne $runId) { throw "Player $($case.id) run identity mismatch." }
        $caseResults.Add([ordered]@{ caseId=$case.id; status='PASS'; layer='WINDOWS_PLAYER'; nativeExitCode=$player.exitCode; summary=$summaryPath; summarySha256=(Get-Sha256 $summaryPath); actionEvidence=$actionEvidencePath; actionEvidenceSha256=(Get-Sha256 $actionEvidencePath); executionEvidence=$executionPath; executionEvidenceSha256=(Get-Sha256 $executionPath); playerLog=$nativeLog; playerLogSha256=(Get-Sha256 $nativeLog); payloadSha256=$summary.payloadSha256 }) | Out-Null
    }

    $final = [ordered]@{ schemaVersion=1; task='G2-C04-D03'; status='PASS'; acceptedBase='8fd7c6cb701dfbb41c28938de4186749bdd1d6c7'; frozenCandidate=$gitHead; sourceTreeStatus=$gitStatus; sourceDiffCheck='PASS'; sourceClosureManifest='source-prebuild-closure.json'; sourceClosureManifestSha256=$sourceClosureSha; unityProjectRoot=$project; unityShortPathStaging='unity-short-path-staging.json'; unityShortPathStagingSha256=$shortPathProjectSha; generatedUnityInputs='unity-generated-inputs.json'; generatedUnityInputsSha256=(Get-Sha256 (Join-Path $RunRoot 'unity-generated-inputs.json')); unityResolvedPackageInputClosure='unity-resolved-package-input-closure.json'; unityResolvedPackageInputClosureSha256=$resolvedPackageInputsSha; unityPrebuildInputClosureSha256=$preBuildInputsSha; unityPostbuildInputClosureSha256=$postBuildInputsSha; unityPostbuildInputChanges='unity-postbuild-input-changes.json'; unityPostbuildInputChangesSha256=(Get-Sha256 (Join-Path $RunRoot 'unity-postbuild-input-changes.json')); upmDownloadCacheClosure='upm-cache-closure.json'; upmDownloadCacheClosureSha256=$upmCacheClosureSha; postBuildAssetsClosure='source-postbuild-assets-closure.json'; postBuildAssetsClosureSha256=$postBuildClosureSha; sharedPluginClosureSha256=$pluginClosureSha; sharedPluginProvenance=(Join-Path $projectPlugins 'shared-plugin-provenance.json'); sharedPluginProvenanceSha256=$provenanceSha; scriptAssembliesClosureSha256=$scriptAssembliesSha; localHostTests=$localHostTestEvidence; commands=$script:stageRecords; playMode=[ordered]@{ testCount=$testNodes.Count; passed=$testNodes.Count; failed=0; skipped=0; exactNames=$actualNames; xml=$playXml; xmlSha256=(Get-Sha256 $playXml) }; buildMarker=$buildMarker; buildMarkerSha256=(Get-Sha256 $buildMarker); playerPackageManifest='player-package.json'; cases=$caseResults; platforms=[ordered]@{ windowsPlayer='actual four fixed cases'; android='NOT_RUN (C05 owner)'; ios='NOT_RUN'; remoteTransport='NOT_IN_SCOPE' } }
    $summaryOut = Join-Path $RunRoot 'summary.json'
    [IO.File]::WriteAllText($summaryOut, (($final | ConvertTo-Json -Depth 12) + "`n"), (New-Object Text.UTF8Encoding($false)))
    Write-RunLog 'FINAL_SUMMARY_WRITTEN'
    $script:LogOpen = $false
    $artifactFiles = Get-C04StableArtifactFiles $RunRoot
    $artifacts = New-Object System.Collections.Generic.List[object]
    foreach ($file in $artifactFiles) {
        $artifacts.Add([ordered]@{ path=$file.FullName.Substring($RunRoot.Length).TrimStart('\','/').Replace('\','/'); bytes=[long]$file.Length; sha256=(Get-Sha256 $file.FullName) }) | Out-Null
    }
    $manifest = [ordered]@{ schemaVersion=1; task='G2-C04-D03'; root=$RunRoot; entryCount=$artifacts.Count; separatedVolatileRoots=(Get-C04SeparatedVolatileRoots $RunRoot); entries=$artifacts }
    $manifestPath = Join-Path $RunRoot 'manifest.json'
    [IO.File]::WriteAllText($manifestPath, (($manifest | ConvertTo-Json -Depth 10) + "`n"), (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText((Join-Path $RunRoot 'manifest.sha256'), ((Get-Sha256 $manifestPath) + "  manifest.json`n"), (New-Object Text.UTF8Encoding($false)))
    Write-Host "C04_PLAYER_ACTIONS_PASS cases=4 manifest=$(Get-Sha256 $manifestPath) root=$RunRoot"
}
catch {
    if ($script:LogOpen) { Write-RunLog ('FAIL ' + $_.Exception.GetType().Name + ': ' + $_.Exception.Message) }
    $failurePath = Join-Path $RunRoot 'failure.json'
    if (-not (Test-Path -LiteralPath $failurePath)) {
        $fixedIds = @('C04-G2-01','C04-G2-02','C04-G2-03','C04-G2-04')
        $caseMatrix = New-Object System.Collections.Generic.List[object]
        foreach ($caseId in $fixedIds) {
            $dotnetStatus = 'NOT_RUN'
            if ($trx -and (Test-Path -LiteralPath $trx)) {
                try {
                    [xml]$testXml = Get-Content -LiteralPath $trx -Raw
                    $needle = switch ($caseId) { 'C04-G2-01' { 'pon' } 'C04-G2-02' { 'chi' } 'C04-G2-03' { 'riichi' } 'C04-G2-04' { 'closed-kan' } }
                    $caseMarker = 'scenario: ' + [string][char]92 + [string][char]34 + $needle + [string][char]92 + [string][char]34
                    $nodes = @($testXml.SelectNodes("//*[local-name()='test']") | Where-Object { $_.name.IndexOf($caseMarker, [StringComparison]::OrdinalIgnoreCase) -ge 0 })
                    if ($nodes.Count -eq 1) { $dotnetStatus = if ($nodes[0].result -eq 'Pass') { 'PASS' } else { 'FAIL' } }
                    elseif ($nodes.Count -gt 1) { $dotnetStatus = 'FAIL_DUPLICATE_EVIDENCE' }
                } catch { $dotnetStatus = 'FAIL_UNPARSEABLE_XML' }
            }
            $caseMatrix.Add([ordered]@{ caseId=$caseId; dotnet=$dotnetStatus; playMode='NOT_RUN'; windowsPlayer='NOT_RUN' }) | Out-Null
        }
        $failure = [ordered]@{ status='FAIL'; task='G2-C04-D03'; acceptedBase='8fd7c6cb701dfbb41c28938de4186749bdd1d6c7'; frozenCandidate=$gitHead; error=$_.Exception.Message; fixedCaseMatrix=$caseMatrix; stageRecords=$script:stageRecords }
        [IO.File]::WriteAllText($failurePath, (($failure | ConvertTo-Json -Depth 8) + "`n"), (New-Object Text.UTF8Encoding($false)))
    }
    $script:LogOpen = $false
    $manifestPath = Join-Path $RunRoot 'manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        $failedArtifacts = Get-C04StableArtifactFiles $RunRoot
        $entries = New-Object System.Collections.Generic.List[object]
        foreach ($file in $failedArtifacts) {
            $entries.Add([ordered]@{ path=$file.FullName.Substring($RunRoot.Length).TrimStart('\','/').Replace('\','/'); bytes=[long]$file.Length; sha256=(Get-Sha256 $file.FullName) }) | Out-Null
        }
        $failedManifest = [ordered]@{ schemaVersion=1; task='G2-C04-D03'; status='FAILED_RUN'; root=$RunRoot; entryCount=$entries.Count; separatedVolatileRoots=(Get-C04SeparatedVolatileRoots $RunRoot); entries=$entries }
        [IO.File]::WriteAllText($manifestPath, (($failedManifest | ConvertTo-Json -Depth 10) + "`n"), (New-Object Text.UTF8Encoding($false)))
        [IO.File]::WriteAllText((Join-Path $RunRoot 'manifest.sha256'), ((Get-Sha256 $manifestPath) + "  manifest.json`n"), (New-Object Text.UTF8Encoding($false)))
    }
    throw
}
finally {
    if ($script:mutexHeld) { try { $script:mutex.ReleaseMutex() } catch {} }
    if ($script:mutex) { $script:mutex.Dispose() }
    $env:TEMP=$oldTemp; $env:TMP=$oldTmp; $env:NUGET_PACKAGES=$oldNuget; $env:DOTNET_CLI_HOME=$oldCli
    $env:UPM_CACHE_ROOT=$oldUpmRoot; $env:UPM_NPM_CACHE_PATH=$oldUpmNpm; $env:UPM_CACHE_PATH=$oldUpmPackages
}
