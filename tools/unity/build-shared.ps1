<#
.SYNOPSIS
    Builds the shared C# libraries and copies the complete Unity-consumed assembly
    closure into the Unity project.

.DESCRIPTION
    The shared assemblies target .NET Standard 2.1 / C# 9 so the exact same binaries
    load in Unity 6 and in the .NET server processes. Run this after changing any
    project under dotnet/src, then let Unity recompile the presentation assemblies.

    The copy set is the full runtime closure the Unity client consumes, not just the
    five historical modules:
      - every plugin project's own assembly (Domain, Contracts, Contracts.Wire,
        TableRuntime, Client, LocalHost, Ai)
      - third-party runtime dependencies resolved from each plugin project's
        project.assets.json against the NuGet global packages cache (today that is
        Google.Protobuf.dll for the Wire codec)
    Test projects, the outbox crash-worker helper, test frameworks (xunit,
    Microsoft.Testing.*), build tools (Grpc.Tools) and SDK payloads are never copied.

    Provenance:
      - on a successful build+copy to the default plugin directory, an atomic
        provenance manifest is written under the Unity project's ignored
        Library/SharedPluginProvenance/ directory. It records the concrete
        supported MSBuild input closure (relative paths + raw SHA-256), the
        build/tool identity and the byte hashes of every copied plugin file.
      - tools/unity/check-shared-plugins.ps1 (also invoked by the Editor-only
        SharedPluginProvenanceGuard) validates that record against the current
        inputs and plugin bytes and fails closed when anything drifted.
      - a scratch -PluginOutputDir gets a separate manifest inside that scratch
        directory; it never updates (or invalidates) the default synced record.
      - a failed build leaves no valid record: the previous record is removed
        before the build starts and a new one is only written after the build,
        copy, byte verification and a stable-input re-check have all succeeded.
      - if an input changes while the build is running, the build is rejected
        instead of recording a PASS for a mixed snapshot.

    Guards:
      - every plugin project must produce bin/<config>/netstandard2.1/<name>.dll
        (a shared assembly silently bumped above the Unity floor fails the build)
      - the effective LangVersion must stay <= 9.0 (csproj value, else
        dotnet/Directory.Build.props)
      - duplicate third-party assembly names are only accepted when every source
        copy has the identical SHA-256; conflicting versions abort the copy

.PARAMETER Configuration
    Debug or Release. Defaults to Release.

.PARAMETER DotnetPath
    Path to dotnet.exe. Defaults to DOTNET_ROOT, then D:\Unity\dotnet\dotnet.exe,
    then whatever dotnet is on PATH.

.PARAMETER PluginOutputDir
    Optional override for the copy destination. Defaults to
    unity\Mahjong.Unity\Assets\Plugins\Mahjong. Pass a scratch directory to inspect
    the copy result without touching the tracked plugin DLLs (used by CI and by
    tools\verify-v5.ps1; verification must never overwrite tracked Unity assets).
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$DotnetPath,

    [string]$PluginOutputDir
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$solution = Join-Path $root 'dotnet\Mahjong.sln'
$defaultPluginDir = Join-Path $root 'unity\Mahjong.Unity\Assets\Plugins\Mahjong'
$pluginDir = if ($PluginOutputDir) { $PluginOutputDir } else { $defaultPluginDir }

. (Join-Path $PSScriptRoot 'shared-plugin-provenance-lib.ps1')

# The default destination represents "the Unity project is synced" and owns the
# ignored Library record. A scratch destination gets its own local record so CI
# and tools\verify-v5.ps1 can inspect a copy without touching the synced state.
$pluginDirFull = Resolve-SharedPluginFullPath $pluginDir
$defaultPluginDirFull = Resolve-SharedPluginFullPath $defaultPluginDir
$isDefaultTarget = $pluginDirFull -ieq $defaultPluginDirFull
$unityProjectDir = Join-Path $root 'unity\Mahjong.Unity'
$defaultEvidencePath = Join-Path $unityProjectDir 'Library\SharedPluginProvenance\provenance.json'
$evidencePath = if ($isDefaultTarget) {
    $defaultEvidencePath
}
else {
    Join-Path $pluginDirFull 'shared-plugin-provenance.json'
}

# A record only describes a completed successful sync. Remove any previous
# record up front so a failed/aborted build can never leave an apparently green
# (but stale) PASS behind.
if (Test-Path -LiteralPath $evidencePath -PathType Leaf) {
    Remove-Item -LiteralPath $evidencePath -Force
}

$inputSnapshotBefore = @(Get-SharedPluginInputClosure -RepoRoot $root)
$toolIdentity = @(Get-SharedPluginToolIdentity -RepoRoot $root)
$buildStartedAtUtc = [DateTime]::UtcNow

# Assemblies the Unity client consumes (own plugin projects, dependency order).
$pluginProjects = @(
    'Mahjong.Domain',
    'Mahjong.Contracts',
    'Mahjong.Contracts.Wire',
    'Mahjong.TableRuntime',
    'Mahjong.Client',
    'Mahjong.LocalHost',
    'Mahjong.Ai'
)

# Never allow test / helper / tooling payloads into the Unity plugin folder.
$forbiddenPatterns = @(
    'Mahjong.*.Tests.*',
    'Mahjong.Outbox.CrashWorker.*',
    'xunit.*',
    'Microsoft.Testing.*',
    'Microsoft.ApplicationInsights.*',
    'Grpc.*',
    'Google.Protobuf.Tools.*'
)

if (-not $DotnetPath) {
    if ($env:DOTNET_ROOT) {
        $DotnetPath = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
    }
    elseif (Test-Path 'D:\Unity\dotnet\dotnet.exe') {
        $DotnetPath = 'D:\Unity\dotnet\dotnet.exe'
    }
    else {
        $DotnetPath = (Get-Command dotnet -ErrorAction Stop).Source
    }
}

if (-not (Test-Path $solution)) {
    throw "Solution not found: $solution"
}

function Test-AllowedFileName([string]$name) {
    foreach ($pattern in $forbiddenPatterns) {
        if ($name -like $pattern) { return $false }
    }
    return $true
}

# Effective LangVersion: csproj override wins, otherwise dotnet/Directory.Build.props.
function Get-EffectiveLangVersion([string]$csprojPath, [string]$propsPath) {
    $csprojValue = (Select-Xml -Path $csprojPath -XPath '//*[local-name()="LangVersion"]').Node.'#text'
    if ($csprojValue) { return $csprojValue.Trim() }
    $propsValue = (Select-Xml -Path $propsPath -XPath '//*[local-name()="LangVersion"]').Node.'#text'
    if ($propsValue) { return $propsValue.Trim() }
    return $null
}

function Assert-LangVersionFloor([string]$value, [string]$csprojPath) {
    # Shared assemblies must stay at or below the Unity 6 / netstandard2.1 floor (C# 9).
    if (-not $value) {
        throw "LangVersion not set for '$csprojPath' (neither in the csproj nor in dotnet/Directory.Build.props); the Unity floor would be left to toolchain defaults"
    }
    $numeric = $value -replace '^([0-9]+(\.[0-9]+)?).*$', '$1'
    if ($numeric -notmatch '^[0-9]+(\.[0-9]+)?$' -or [double]$numeric -gt 9.0) {
        throw "LangVersion '$value' in '$csprojPath' exceeds the shared-library C# 9 floor"
    }
}

Write-Host "dotnet   : $DotnetPath"
Write-Host "solution : $solution"
Write-Host "config   : $Configuration"
Write-Host "plugins  : $pluginDir"

$propsPath = Join-Path $root 'dotnet\Directory.Build.props'

foreach ($project in $pluginProjects) {
    $csproj = Join-Path $root "dotnet\src\$project\$project.csproj"
    if (-not (Test-Path $csproj)) {
        throw "Plugin project not found: $csproj"
    }
    $tfmInCsproj = (Select-Xml -Path $csproj -XPath '//*[local-name()="TargetFramework"]').Node.'#text'
    if ($tfmInCsproj -and $tfmInCsproj.Trim() -ne 'netstandard2.1') {
        throw "$project targets '$tfmInCsproj'; shared Unity assemblies must stay on netstandard2.1"
    }
    Assert-LangVersionFloor (Get-EffectiveLangVersion $csproj $propsPath) $csproj
}

# A successful dotnet build alone is not enough: MSBuild may reuse an old DLL
# when a source's timestamp was preserved. Remove this configuration's outputs
# for all seven projects so the recorded source set is actually compiled.
$cleanedOutputs = @()
foreach ($project in $pluginProjects) {
    foreach ($relative in @("dotnet\src\$project\bin\$Configuration", "dotnet\src\$project\obj\$Configuration")) {
        $full = Resolve-SharedPluginFullPath (Join-Path $root $relative)
        if (-not (Test-SharedPluginPathWithin -Path $full -Root (Join-Path $root 'dotnet'))) {
            throw "Refusing to clean output outside dotnet: $full"
        }
        if (Test-Path -LiteralPath $full -PathType Container) {
            Remove-Item -LiteralPath $full -Recurse -Force
            $cleanedOutputs += $relative.Replace('\', '/')
        }
    }
}

# Build from the repository root so an incidental Directory.Build.rsp in the
# caller's working directory cannot change the produced closure. The root rsp is
# part of the recorded input boundary when present.
Push-Location -LiteralPath $root
try {
    & $DotnetPath build $solution -c $Configuration --nologo -m:1 -nodeReuse:false
    $buildExitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}
if ($buildExitCode -ne 0) {
    throw "dotnet build failed with exit code $buildExitCode"
}

# --- collect the plugin assembly closure ---------------------------------------
# candidate map: file name -> list of @{ Source; Path; Sha256; Meta }
$candidates = @{}

function Add-Candidate([string]$source, [System.IO.FileInfo]$file, [hashtable]$meta) {
    if (-not (Test-AllowedFileName $file.Name)) { return }
    $entry = @{
        Source = $source
        Path   = $file.FullName
        Sha256 = (Get-FileHash -Algorithm SHA256 $file.FullName).Hash
        Meta   = $meta
    }
    if (-not $candidates.ContainsKey($file.Name)) {
        $candidates[$file.Name] = New-Object System.Collections.Generic.List[object]
    }
    $candidates[$file.Name].Add($entry) | Out-Null
}

foreach ($project in $pluginProjects) {
    $binDir = Join-Path $root "dotnet\src\$project\bin\$Configuration\netstandard2.1"
    $dll = Join-Path $binDir "$project.dll"
    if (-not (Test-Path $dll)) {
        throw "Missing build output: $dll (shared assemblies must target netstandard2.1)"
    }
    Add-Candidate $project (Get-Item $dll) @{
        SourceKind = 'project'
        Project    = $project
        AssetName  = "$project.dll"
    }
    $pdb = Join-Path $binDir "$project.pdb"
    if (Test-Path $pdb) {
        Add-Candidate $project (Get-Item $pdb) @{
            SourceKind = 'project'
            Project    = $project
            AssetName  = "$project.pdb"
        }
    }
}

# Third-party runtime closure from the resolved asset graph (e.g. Google.Protobuf
# for Contracts.Wire). Class libraries do not copy package assemblies to bin, so
# the DLLs are resolved against the NuGet global packages cache. The verifier
# re-resolves the same closure independently from obj/project.assets.json.
$runtimeClosure = Get-SharedPluginResolvedRuntimeClosure -RepoRoot $root -Projects $pluginProjects -DotnetPath $DotnetPath
if (-not $runtimeClosure.Ok) {
    throw "Could not resolve the third-party runtime closure: $($runtimeClosure.Errors -join '; ')"
}
$nugetCache = $runtimeClosure.NuGetCache
Write-Host "nuget    : $nugetCache"

foreach ($assembly in $runtimeClosure.Assemblies) {
    Add-Candidate "$($assembly.PackageId) $($assembly.PackageVersion)" (Get-Item -LiteralPath $assembly.Path) @{
        SourceKind     = 'package'
        PackageId      = $assembly.PackageId
        PackageVersion = $assembly.PackageVersion
        AssetName      = $assembly.AssetPath
    }
}

# Duplicate check: same file name from different sources must be byte-identical.
foreach ($name in $candidates.Keys | Sort-Object) {
    $entries = $candidates[$name]
    $distinct = $entries | ForEach-Object Sha256 | Select-Object -Unique
    if (@($distinct).Count -gt 1) {
        $details = ($entries | ForEach-Object { "$($_.Source)[$($_.Sha256.Substring(0,8))]" }) -join ', '
        throw "Conflicting copies of assembly '$name' in the plugin closure: $details"
    }
}

# --- copy ----------------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null

$copied = @()
foreach ($name in $candidates.Keys | Sort-Object) {
    $entry = $candidates[$name][0]
    Copy-Item -LiteralPath $entry.Path -Destination $pluginDir -Force
    $copied += $name
}

Write-Host "copied $($copied.Count) files:"
foreach ($name in $copied) {
    $file = Get-Item (Join-Path $pluginDir $name)
    Write-Host ("  {0,-28} {1,10} bytes  <- {2}" -f $name, $file.Length, $candidates[$name][0].Source)
}

# The Unity client needs at least the plugin assemblies and the Wire runtime.
foreach ($required in @($pluginProjects | ForEach-Object { "$($_).dll" }) + @('Google.Protobuf.dll')) {
    if (-not (Test-Path (Join-Path $pluginDir $required))) {
        throw "Plugin copy incomplete: missing $required in $pluginDir"
    }
}

# --- verify the copied bytes and freeze provenance ----------------------------
# The destination is only "synced" if every copied byte equals the byte that was
# just built; never record a manifest for a partial or altered copy.
foreach ($name in $copied) {
    $destination = Join-Path $pluginDirFull $name
    $expected = $candidates[$name][0]
    $destinationItem = Get-Item -LiteralPath $destination
    $destinationHash = Get-SharedPluginFileSha256 $destination
    if ($destinationHash -ine $expected.Sha256 -or [long]$destinationItem.Length -ne [long](Get-Item -LiteralPath $expected.Path).Length) {
        throw "Copied plugin file does not match the build output: $name (expected $($expected.Sha256), got $destinationHash)"
    }
}

# If a source changed while dotnet was building, the outputs are a mixed
# snapshot. Refuse to record a green provenance record for it.
$inputSnapshotAfter = @(Get-SharedPluginInputClosure -RepoRoot $root)
$inputDrift = Compare-SharedPluginInputClosure -Expected $inputSnapshotBefore -Actual $inputSnapshotAfter
if (-not $inputDrift.IsEqual) {
    $details = @()
    foreach ($entry in @($inputDrift.Added)) { $details += "ADDED $($entry.Path)" }
    foreach ($entry in @($inputDrift.Removed)) { $details += "REMOVED $($entry.Path)" }
    foreach ($entry in @($inputDrift.Modified)) { $details += "MODIFIED $($entry.Path)" }
    throw "Shared plugin inputs changed while the build was running; refusing to record provenance. $($details -join '; ')"
}

$dotnetVersion = 'unknown'
try {
    $versionOutput = & $DotnetPath --version 2>$null
    if ($LASTEXITCODE -eq 0 -and $versionOutput) {
        $dotnetVersion = ([string]($versionOutput | Select-Object -First 1)).Trim()
    }
}
catch {
    $dotnetVersion = 'unknown'
}

$outputs = @()
foreach ($name in $candidates.Keys | Sort-Object) {
    $entry = $candidates[$name][0]
    $sourceItem = Get-Item -LiteralPath $entry.Path
    $sourcePath = if ($entry.Meta.SourceKind -eq 'project') {
        Get-SharedPluginRelativePath -Root $root -Path $entry.Path
    }
    else {
        "nuget:$($entry.Meta.PackageId)/$($entry.Meta.PackageVersion)/$($entry.Meta.AssetName)"
    }
    $pluginRelative = Get-SharedPluginDisplayPath -Root $root -Path (Join-Path $pluginDirFull $name)
    $outputs += [ordered]@{
        Name       = $name
        Length     = [long]$sourceItem.Length
        Sha256     = $entry.Sha256
        SourceKind = $entry.Meta.SourceKind
        SourcePath = $sourcePath
        PluginPath = $pluginRelative
        Project    = if ($entry.Meta.SourceKind -eq 'project') { $entry.Meta.Project } else { $null }
    }
}

$packages = @()
foreach ($name in $candidates.Keys | Sort-Object) {
    $entry = $candidates[$name][0]
    if ($entry.Meta.SourceKind -ne 'package') { continue }
    $packages += [ordered]@{
        Id         = $entry.Meta.PackageId
        Version    = $entry.Meta.PackageVersion
        Assembly   = $name
        AssetPath  = $entry.Meta.AssetName
        Length     = [long](Get-Item -LiteralPath $entry.Path).Length
        Sha256     = $entry.Sha256
    }
}

$toolEntries = @{}
foreach ($item in $toolIdentity) { $toolEntries[$item.Path] = $item }

$manifest = [ordered]@{
    schemaVersion  = $script:SharedPluginEvidenceSchemaVersion
    kind           = $script:SharedPluginEvidenceKind
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    configuration  = $Configuration
    targetFramework = 'netstandard2.1'
    langVersionFloor = '9.0'
    project        = 'unity/Mahjong.Unity'
    evidenceScope  = if ($isDefaultTarget) { 'default-synced' } else { 'scratch' }
    pluginDir      = $pluginDirFull
    pluginDirRelative = Get-SharedPluginDisplayPath -Root $root -Path $pluginDirFull
    inputBoundary  = [ordered]@{
        version     = $script:SharedPluginInputBoundaryVersion
        description = 'SDK-style default globs for the seven shared projects plus explicit includes, ancestor/root MSBuild configuration and Directory.Build.rsp; raw byte SHA-256.'
        projects    = $script:SharedPluginProjects
        sharedFiles = @(
            'Directory.Build.props',
            'Directory.Build.targets',
            'Directory.Packages.props',
            'Directory.Build.rsp',
            'NuGet.config',
            'dotnet/Directory.Build.props',
            'dotnet/Directory.Build.targets',
            'dotnet/Directory.Packages.props',
            'dotnet/Directory.Build.rsp',
            'dotnet/NuGet.config',
            'dotnet/src/Directory.Build.props',
            'dotnet/src/Directory.Build.targets',
            'dotnet/src/Directory.Packages.props',
            'dotnet/src/Directory.Build.rsp',
            'dotnet/src/NuGet.config',
            'dotnet/global.json',
            'global.json',
            'dotnet/Mahjong.sln',
            'tools/unity/build-shared.ps1',
            'tools/unity/shared-plugin-provenance-lib.ps1'
        )
        excluded    = @('**/bin/**', '**/obj/**', 'tests', 'server-only services', 'documentation/reports', 'machine/user NuGet.config')
        unsupported = @('custom MSBuild Import', 'Compile/Protobuf/EmbeddedResource Remove', 'item Exclude attributes', 'custom DefaultItemExcludes', 'explicit Reference/Analyzer/AdditionalFiles', 'wildcard, out-of-repo or unresolved-property includes', 'ProjectReference outside the seven projects')
        lineEndings = 'raw-bytes-no-normalisation'
    }
    tool           = [ordered]@{
        buildShared      = $toolEntries['tools/unity/build-shared.ps1']
        sharedDefinition = $toolEntries['tools/unity/shared-plugin-provenance-lib.ps1']
        dotnetPath       = $DotnetPath
        dotnetVersion    = $dotnetVersion
        solution         = 'dotnet/Mahjong.sln'
    }
    build          = [ordered]@{
        startedAtUtc   = $buildStartedAtUtc.ToString('o')
        completedAtUtc = [DateTime]::UtcNow.ToString('o')
        elapsedSeconds = [math]::Round(([DateTime]::UtcNow - $buildStartedAtUtc).TotalSeconds, 3)
        exitCode       = 0
        mode           = 'forced-clean-outputs'
        forcedRebuild  = $true
        cleanedOutputs = @($cleanedOutputs | Sort-Object)
        copiedFileCount = $copied.Count
        inputCount     = $inputSnapshotAfter.Count
    }
    requiredOwnAssemblies = @($pluginProjects | ForEach-Object { "$($_).dll" })
    inputs         = @($inputSnapshotAfter | ForEach-Object {
        [ordered]@{ Path = $_.Path; Length = [long]$_.Length; Sha256 = $_.Sha256 }
    })
    packages       = @($packages | Sort-Object -Property Assembly)
    outputs        = @($outputs | Sort-Object -Property Name)
}

$writtenEvidence = Write-SharedPluginProvenanceAtomic -Path $evidencePath -Data $manifest
Write-Host "provenance: $writtenEvidence"
Write-Host "inputs    : $($inputSnapshotAfter.Count) files"
Write-Host "outputs   : $($outputs.Count) files"

Write-Host "Unity plugins ready: $pluginDir"
