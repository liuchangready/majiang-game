# Shared definitions for tools/unity/build-shared.ps1 and
# tools/unity/check-shared-plugins.ps1.
#
# This file is dot-sourced by both entry points so the producer and the verifier
# use the exact same supported MSBuild input boundary, path normalisation and
# evidence schema. It performs no I/O on its own; callers decide what to write.
#
# Supported MSBuild input boundary (schema version 2)
# -----------------------------------------------------
# The provenance manifest covers:
#   * every file under the seven Unity-consumed shared project directories
#     (dotnet/src/Mahjong.{Domain,Contracts,Contracts.Wire,TableRuntime,Client,
#     LocalHost,Ai}) with a build-relevant extension, excluding bin/ and obj/;
#   * explicit Compile / Protobuf / EmbeddedResource includes declared by those
#     csproj files and by every supported .props/.targets file in the project,
#     dotnet/src/, dotnet/ and repository-root hierarchy. Explicit includes may
#     use $(MSBuildThisFileDirectory) / $(MSBuildThisFile) (and
#     $(MSBuildProjectDirectory) inside a project-owned file); other MSBuild
#     expressions are rejected fail-closed;
#   * explicit ProjectReference items declared by those csproj/props/targets
#     files, restricted to the seven supported projects;
#   * the shared build configuration (Directory.Build.props/targets/rsp at the
#     repository root, dotnet/ and dotnet/src/, Directory.Packages.props,
#     repository-level NuGet.config files, global.json, Mahjong.sln);
#   * the build tool itself and this shared definition file.
#
# Not covered / rejected fail-closed:
#   * custom <Import Project="..."> elements;
#   * <Compile Remove>, <Protobuf Remove>, <EmbeddedResource Remove>, item
#     Exclude attributes and custom DefaultItemExcludes;
#   * explicit <Reference>, <Analyzer> and <AdditionalFiles> items (their
#     external payload cannot be byte-pinned inside this boundary);
#   * explicit includes outside the repository, containing wildcards, or using
#     unsupported MSBuild property expressions;
#   * ProjectReference targets outside the seven supported projects;
#   * machine/user NuGet.config and NuGet global packages: repository-level
#     NuGet.config is hashed; package assemblies are captured by
#     id/version/relative-asset/SHA-256 and independently re-resolved by the
#     verifier from obj/project.assets.json, not by directory hash.
#
# The producer forces a real compile by removing the seven projects' selected
# bin/obj output directories before building; it never records a source hash
# against a DLL that the incremental build reused without compiling.
#
# Input identity is the raw byte SHA-256 of each file. Line-ending conversion is
# not applied because MSBuild consumes the bytes on disk; documentation and
# report files outside the extension allowlist are not inputs. Paths are stored
# relative to the repository root with forward slashes and compared
# case-insensitively, independent of enumeration order.

$script:SharedPluginProjects = @(
    'Mahjong.Domain',
    'Mahjong.Contracts',
    'Mahjong.Contracts.Wire',
    'Mahjong.TableRuntime',
    'Mahjong.Client',
    'Mahjong.LocalHost',
    'Mahjong.Ai'
)

# The seven own assemblies plus the real third-party runtime closure the Unity
# client consumes today. Package closure is also recorded per-assembly.
$script:SharedPluginRequiredAssemblies = @(
    'Mahjong.Domain.dll',
    'Mahjong.Contracts.dll',
    'Mahjong.Contracts.Wire.dll',
    'Mahjong.TableRuntime.dll',
    'Mahjong.Client.dll',
    'Mahjong.LocalHost.dll',
    'Mahjong.Ai.dll',
    'Google.Protobuf.dll'
)

$script:SharedPluginEvidenceSchemaVersion = 2
$script:SharedPluginEvidenceKind = 'mahjong-unity-shared-plugin-provenance'
$script:SharedPluginInputBoundaryVersion = 4

# Extensions that can alter a shared assembly or its resolved dependency graph.
# Documentation/report files are deliberately excluded so prose edits do not
# invalidate a provenance record.
$script:SharedPluginInputExtensions = @(
    '.cs',
    '.csproj',
    '.proto',
    '.resx',
    '.json',
    '.props',
    '.targets',
    '.config',
    '.ruleset',
    '.editorconfig',
    '.rsp'
)

function Resolve-SharedPluginFullPath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [string]$BasePath
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw 'Path must not be empty.'
    }

    $candidate = $Path
    if (-not [System.IO.Path]::IsPathRooted($candidate)) {
        $base = if ($BasePath) { $BasePath } else { (Get-Location).Path }
        $candidate = Join-Path $base $candidate
    }

    $full = [System.IO.Path]::GetFullPath($candidate)
    if ($full.Length -gt 3) {
        $full = $full.TrimEnd('\', '/')
    }
    return $full
}

function Get-SharedPluginRelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $rootFull = Resolve-SharedPluginFullPath $Root
    $pathFull = Resolve-SharedPluginFullPath $Path
    $rootPrefix = $rootFull.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not $pathFull.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the repository root: $pathFull (root: $rootFull)"
    }
    return $pathFull.Substring($rootPrefix.Length).Replace('\', '/')
}

# Evidence-friendly path: repository-relative when possible (stable across
# checkouts), otherwise an absolute path for scratch destinations outside the
# repo. Inputs themselves always have to live inside the repository.
function Get-SharedPluginDisplayPath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )
    $full = Resolve-SharedPluginFullPath $Path
    try {
        return Get-SharedPluginRelativePath -Root $Root -Path $full
    }
    catch {
        return $full
    }
}

function Get-SharedPluginFileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Get-SharedPluginToolIdentity {
    param([Parameter(Mandatory = $true)][string]$RepoRoot)

    $root = Resolve-SharedPluginFullPath $RepoRoot
    $items = @()
    foreach ($relative in @('tools/unity/build-shared.ps1', 'tools/unity/shared-plugin-provenance-lib.ps1')) {
        $full = Join-Path $root ($relative.Replace('/', '\'))
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
            throw "Provenance tool source missing: $relative"
        }
        $items += [pscustomobject]@{
            Path   = $relative
            Length = [long](Get-Item -LiteralPath $full).Length
            Sha256 = Get-SharedPluginFileSha256 $full
        }
    }
    return $items
}

function Test-SharedPluginPathWithin {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Root
    )
    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $fullRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    if ($fullPath -ieq $fullRoot) { return $true }
    return $fullPath.StartsWith($fullRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-SharedPluginNuGetCache {
    param([string]$DotnetPath)
    if ($env:NUGET_PACKAGES) {
        return (Resolve-SharedPluginFullPath $env:NUGET_PACKAGES)
    }
    if ($DotnetPath -and (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) {
        try {
            $localsOutput = & $DotnetPath nuget locals global-packages --list 2>$null
            if ($LASTEXITCODE -eq 0 -and $localsOutput -match 'global-packages:\s*(.+)$') {
                return (Resolve-SharedPluginFullPath $Matches[1].Trim())
            }
        }
        catch { }
    }
    return (Resolve-SharedPluginFullPath (Join-Path $env:USERPROFILE '.nuget\packages'))
}

# Resolve one declared MSBuild input to an absolute path. Only the explicitly
# supported property expressions are expanded; anything else fails closed rather
# than being silently omitted from the input closure.
function Resolve-SharedPluginMsBuildInput {
    param(
        [Parameter(Mandatory = $true)][string]$Include,
        [Parameter(Mandatory = $true)][string]$BaseDirectory,
        [Parameter(Mandatory = $true)][string]$ConfigPath,
        [string]$ProjectDirectory,
        [bool]$AllowProjectDirectory = $false,
        [Parameter(Mandatory = $true)][string]$Context
    )

    $value = $Include.Trim()
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "Empty MSBuild include in $Context"
    }
    if ($value -match '[*?]') {
        throw "Unsupported wildcard MSBuild include '$Include' in $Context"
    }

    $separator = [System.IO.Path]::DirectorySeparatorChar
    $baseWithSeparator = $BaseDirectory.TrimEnd('\', '/') + $separator
    $value = $value.Replace('$(MSBuildThisFileDirectory)', $baseWithSeparator)
    $value = $value.Replace('$(MSBuildThisFile)', $ConfigPath)
    if ($AllowProjectDirectory -and $ProjectDirectory) {
        $projectWithSeparator = $ProjectDirectory.TrimEnd('\', '/') + $separator
        $value = $value.Replace('$(MSBuildProjectDirectory)', $projectWithSeparator)
    }
    if ($value -match '\$\(') {
        throw "Unsupported MSBuild property expression '$Include' in $Context; the provenance boundary cannot resolve it"
    }
    if ($value -match '%\(') {
        throw "Unsupported MSBuild item metadata expression '$Include' in $Context"
    }

    $candidate = if ([System.IO.Path]::IsPathRooted($value)) { $value } else { Join-Path $BaseDirectory $value }
    $full = Resolve-SharedPluginFullPath $candidate
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "Declared MSBuild input does not exist: $Include ($Context)"
    }
    return $full
}

function Get-SharedPluginResolvedRuntimeClosure {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [string[]]$Projects = $script:SharedPluginProjects,
        [string]$DotnetPath
    )

    $root = Resolve-SharedPluginFullPath $RepoRoot
    $cache = Get-SharedPluginNuGetCache -DotnetPath $DotnetPath
    $assemblies = New-Object System.Collections.Generic.List[object]
    $errors = New-Object System.Collections.Generic.List[string]

    foreach ($project in $Projects) {
        $assetsPath = Join-Path $root "dotnet\src\$project\obj\project.assets.json"
        if (-not (Test-Path -LiteralPath $assetsPath -PathType Leaf)) {
            $errors.Add("Missing resolved assets for $project : $assetsPath") | Out-Null
            continue
        }
        try {
            $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
        }
        catch {
            $errors.Add("Unreadable resolved assets for $project : $($_.Exception.Message)") | Out-Null
            continue
        }

        $targetProp = $assets.targets.PSObject.Properties |
            Where-Object { $_.Name -ieq '.NETStandard,Version=v2.1' }
        if (-not $targetProp) {
            $errors.Add("$project was not restored for netstandard2.1") | Out-Null
            continue
        }

        foreach ($packageProp in $targetProp.Value.PSObject.Properties) {
            if ($packageProp.Name -notmatch '^(.+)/([^/]+)$') { continue }
            $library = $assets.libraries.PSObject.Properties[$packageProp.Name]
            if (-not $library -or $library.Value.type -ne 'package') { continue }
            $packageId = $Matches[1]
            $packageVersion = $Matches[2]
            $runtimeProp = $packageProp.Value.runtime
            if (-not $runtimeProp) { continue }

            foreach ($runtimeEntry in $runtimeProp.PSObject.Properties) {
                $relativePath = $runtimeEntry.Name
                $packageDll = Join-Path $cache "$($packageId.ToLower())\$packageVersion\$($relativePath -replace '/', '\')"
                if (-not (Test-Path -LiteralPath $packageDll -PathType Leaf)) {
                    $errors.Add("Resolved package assembly not found in the NuGet cache: $packageDll") | Out-Null
                    continue
                }
                $item = Get-Item -LiteralPath $packageDll
                $assemblies.Add([pscustomobject]@{
                    Name           = $item.Name
                    PackageId      = $packageId
                    PackageVersion = $packageVersion
                    AssetPath      = ($relativePath -replace '/', '\')
                    Length         = [long]$item.Length
                    Sha256         = Get-SharedPluginFileSha256 $packageDll
                    Path           = $packageDll
                }) | Out-Null
            }
        }
    }

    $conflicts = New-Object System.Collections.Generic.List[string]
    foreach ($group in @($assemblies | Group-Object -Property Name)) {
        $distinct = @($group.Group | ForEach-Object Sha256 | Select-Object -Unique)
        if ($distinct.Count -gt 1) {
            $conflicts.Add("Conflicting runtime copies of $($group.Name): $($distinct -join ', ')") | Out-Null
        }
    }

    return [pscustomobject]@{
        Ok         = (($errors.Count + $conflicts.Count) -eq 0)
        Errors     = @($errors + $conflicts)
        Assemblies = @($assemblies | Sort-Object -Property Name)
        NuGetCache = $cache
    }
}

# Reject MSBuild constructs that would pull in files the boundary cannot see.
# This is applied to every shared .props/.targets file, not only to the project
# files, so an Import/Remove added to Directory.Build.props cannot silently
# escape the recorded input set.
function Assert-SharedPluginMsBuildConfigBoundary {
    param([Parameter(Mandatory = $true)][string]$Path)

    $xml = New-Object System.Xml.XmlDocument
    $xml.Load($Path)
    $unsupported = $xml.SelectNodes('//*[local-name()="Import"] | //*[local-name()="DefaultItemExcludes"] | //*[local-name()="Compile" and @Remove] | //*[local-name()="Protobuf" and @Remove] | //*[local-name()="EmbeddedResource" and @Remove] | //*[local-name()="Reference"] | //*[local-name()="Analyzer"] | //*[local-name()="AdditionalFiles"] | //*[(local-name()="Compile" or local-name()="Protobuf" or local-name()="EmbeddedResource" or local-name()="ProjectReference") and @Exclude]')
    if ($unsupported -and $unsupported.Count -gt 0) {
        throw "Unsupported MSBuild import/removal/exclusion in '$Path'; the provenance boundary cannot cover arbitrary external inputs"
    }
}

# Add Compile/Protobuf/EmbeddedResource/ProjectReference items declared in one
# csproj/props/targets file. This is what makes a Directory.Build.props-level
# linked source part of the recorded closure instead of a silent extra input.
function Add-SharedPluginMsBuildDeclaredInputs {
    param(
        [Parameter(Mandatory = $true)][string]$ConfigPath,
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][scriptblock]$AddFile,
        [string]$ProjectDirectory,
        [bool]$AllowProjectDirectory = $false
    )

    $fullConfig = Resolve-SharedPluginFullPath $ConfigPath
    $baseDirectory = Split-Path -Parent $fullConfig
    $xml = New-Object System.Xml.XmlDocument
    $xml.Load($fullConfig)
    $items = $xml.SelectNodes('//*[local-name()="Compile" or local-name()="Protobuf" or local-name()="EmbeddedResource" or local-name()="ProjectReference"]')
    foreach ($node in $items) {
        $include = $node.GetAttribute('Include')
        if ([string]::IsNullOrWhiteSpace($include)) { continue }
        $resolved = Resolve-SharedPluginMsBuildInput -Include $include -BaseDirectory $baseDirectory -ConfigPath $fullConfig -ProjectDirectory $ProjectDirectory -AllowProjectDirectory $AllowProjectDirectory -Context $fullConfig

        if ($node.LocalName -eq 'ProjectReference') {
            $known = $false
            foreach ($knownProject in $script:SharedPluginProjects) {
                $knownCsproj = Resolve-SharedPluginFullPath (Join-Path $RepoRoot "dotnet\src\$knownProject\$knownProject.csproj")
                if ($resolved -ieq $knownCsproj) { $known = $true; break }
            }
            if (-not $known) {
                throw "ProjectReference outside the seven shared projects: $include ($fullConfig)"
            }
            continue
        }

        $null = Get-SharedPluginRelativePath -Root $RepoRoot -Path $resolved
        & $AddFile $resolved
    }
}

# Enumerates the supported input closure. Returns an array of
# @{ Path; Length; Sha256 } sorted by Path (ordinal, case-insensitive).
function Get-SharedPluginInputClosure {
    param([Parameter(Mandatory = $true)][string]$RepoRoot)

    $root = Resolve-SharedPluginFullPath $RepoRoot
    $entries = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([System.StringComparer]::OrdinalIgnoreCase)

    $addFile = {
        param([string]$filePath)
        $full = Resolve-SharedPluginFullPath $filePath
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
            throw "Declared MSBuild input does not exist: $full"
        }
        $relative = Get-SharedPluginRelativePath -Root $root -Path $full
        if ($entries.ContainsKey($relative)) { return }
        $item = Get-Item -LiteralPath $full
        $entries[$relative] = [pscustomobject]@{
            Path   = $relative
            Length = [long]$item.Length
            Sha256 = Get-SharedPluginFileSha256 $full
        }
    }

    foreach ($project in $script:SharedPluginProjects) {
        $projectDir = Join-Path $root "dotnet\src\$project"
        $csproj = Join-Path $projectDir "$project.csproj"
        if (-not (Test-Path -LiteralPath $csproj -PathType Leaf)) {
            throw "Supported shared project is missing its csproj: dotnet/src/$project/$project.csproj"
        }

        # Parse the project only to validate that it stays inside the supported
        # SDK-style default-glob boundary and to pick up explicit includes.
        $xml = New-Object System.Xml.XmlDocument
        $xml.Load($csproj)
        if ($xml.DocumentElement.LocalName -ne 'Project') {
            throw "Unsupported project XML root in $project.csproj"
        }
        $sdk = $xml.DocumentElement.GetAttribute('Sdk')
        if ($sdk -and $sdk -ne 'Microsoft.NET.Sdk') {
            throw "Unsupported MSBuild SDK '$sdk' in $project.csproj (only Microsoft.NET.Sdk is covered)"
        }

        $imports = $xml.SelectNodes('//*[local-name()="Import"]')
        if ($imports -and $imports.Count -gt 0) {
            throw "Unsupported custom MSBuild Import in $project.csproj; the provenance boundary cannot cover arbitrary external imports"
        }
        $unsupported = $xml.SelectNodes('//*[local-name()="Compile" and @Remove] | //*[local-name()="Protobuf" and @Remove] | //*[local-name()="EmbeddedResource" and @Remove] | //*[local-name()="DefaultItemExcludes"] | //*[local-name()="Reference"] | //*[local-name()="Analyzer"] | //*[local-name()="AdditionalFiles"]')
        if ($unsupported -and $unsupported.Count -gt 0) {
            throw "Unsupported MSBuild removal, external Reference/Analyzer/AdditionalFiles item in $project.csproj; declare a supported boundary instead"
        }

        Add-SharedPluginMsBuildDeclaredInputs -ConfigPath $csproj -RepoRoot $root -AddFile $addFile -ProjectDirectory $projectDir -AllowProjectDirectory $true

        $files = Get-ChildItem -LiteralPath $projectDir -Recurse -File -Force
        foreach ($file in $files) {
            $relativeToProject = $file.FullName.Substring($projectDir.Length)
            if ($relativeToProject -match '(^|[\\/])(bin|obj)([\\/]|$)') { continue }
            $extension = $file.Extension.ToLowerInvariant()
            if ($script:SharedPluginInputExtensions -notcontains $extension) { continue }
            if ($extension -eq '.props' -or $extension -eq '.targets') {
                Assert-SharedPluginMsBuildConfigBoundary $file.FullName
                Add-SharedPluginMsBuildDeclaredInputs -ConfigPath $file.FullName -RepoRoot $root -AddFile $addFile -ProjectDirectory $projectDir -AllowProjectDirectory $true
            }
            & $addFile $file.FullName
        }
    }

    foreach ($relative in @(
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
    )) {
        $full = Join-Path $root ($relative.Replace('/', '\'))
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { continue }
        if ($relative -match '\.(props|targets)$') {
            Assert-SharedPluginMsBuildConfigBoundary $full
            Add-SharedPluginMsBuildDeclaredInputs -ConfigPath $full -RepoRoot $root -AddFile $addFile -ProjectDirectory $null -AllowProjectDirectory $false
        }
        & $addFile $full
    }

    return @($entries.Values | Sort-Object -Property Path)
}

function Compare-SharedPluginInputClosure {
    param(
        [object[]]$Expected = @(),
        [object[]]$Actual = @()
    )

    $expectedMap = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @($Expected)) {
        if ($null -ne $entry) { $expectedMap[[string]$entry.Path] = $entry }
    }
    $actualMap = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @($Actual)) {
        if ($null -ne $entry) { $actualMap[[string]$entry.Path] = $entry }
    }

    $added = New-Object System.Collections.Generic.List[object]
    $removed = New-Object System.Collections.Generic.List[object]
    $modified = New-Object System.Collections.Generic.List[object]

    foreach ($path in $actualMap.Keys) {
        if (-not $expectedMap.ContainsKey($path)) {
            $added.Add($actualMap[$path]) | Out-Null
        }
    }
    foreach ($path in $expectedMap.Keys) {
        if (-not $actualMap.ContainsKey($path)) {
            $removed.Add($expectedMap[$path]) | Out-Null
        }
        else {
            $expectedEntry = $expectedMap[$path]
            $actualEntry = $actualMap[$path]
            if ($expectedEntry.Sha256 -ine $actualEntry.Sha256 -or [long]$expectedEntry.Length -ne [long]$actualEntry.Length) {
                $modified.Add([pscustomobject]@{
                    Path            = $path
                    ExpectedLength  = [long]$expectedEntry.Length
                    ActualLength    = [long]$actualEntry.Length
                    ExpectedSha256  = [string]$expectedEntry.Sha256
                    ActualSha256    = [string]$actualEntry.Sha256
                }) | Out-Null
            }
        }
    }

    return [pscustomobject]@{
        Added    = @($added | Sort-Object -Property Path)
        Removed  = @($removed | Sort-Object -Property Path)
        Modified = @($modified | Sort-Object -Property Path)
        IsEqual  = (($added.Count + $removed.Count + $modified.Count) -eq 0)
    }
}

function Read-SharedPluginProvenance {
    param([Parameter(Mandatory = $true)][string]$Path)

    $full = Resolve-SharedPluginFullPath $Path
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        return [pscustomobject]@{
            Ok      = $false
            Code    = 'EVIDENCE_MISSING'
            Message = "Provenance evidence not found: $full"
            Path    = $full
        }
    }

    try {
        $raw = [System.IO.File]::ReadAllText($full)
    }
    catch {
        return [pscustomobject]@{
            Ok      = $false
            Code    = 'EVIDENCE_UNREADABLE'
            Message = "Provenance evidence could not be read: $($_.Exception.Message)"
            Path    = $full
        }
    }

    if ([string]::IsNullOrWhiteSpace($raw)) {
        return [pscustomobject]@{
            Ok      = $false
            Code    = 'EVIDENCE_EMPTY'
            Message = "Provenance evidence is empty: $full"
            Path    = $full
        }
    }

    try {
        $data = $raw | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        return [pscustomobject]@{
            Ok      = $false
            Code    = 'EVIDENCE_MALFORMED'
            Message = "Provenance evidence is not valid JSON: $($_.Exception.Message)"
            Path    = $full
        }
    }

    if ($null -eq $data) {
        return [pscustomobject]@{
            Ok      = $false
            Code    = 'EVIDENCE_MALFORMED'
            Message = "Provenance evidence parsed to null: $full"
            Path    = $full
        }
    }

    return [pscustomobject]@{
        Ok        = $true
        Code      = 'OK'
        Message   = 'Provenance evidence parsed.'
        Path      = $full
        Data      = $data
        Sha256    = Get-SharedPluginFileSha256 $full
        RawLength = [long]$raw.Length
    }
}

function Write-SharedPluginProvenanceAtomic {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][object]$Data
    )

    $full = Resolve-SharedPluginFullPath $Path
    $directory = Split-Path -Parent $full
    if ($directory) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    $json = $Data | ConvertTo-Json -Depth 12
    $temp = "$full.tmp-$([guid]::NewGuid().ToString('N'))"
    $encoding = New-Object System.Text.UTF8Encoding($false)
    try {
        [System.IO.File]::WriteAllText($temp, $json, $encoding)
        if (Test-Path -LiteralPath $full -PathType Leaf) {
            try {
                [System.IO.File]::Replace($temp, $full, $null)
            }
            catch {
                Remove-Item -LiteralPath $full -Force
                [System.IO.File]::Move($temp, $full)
            }
        }
        else {
            [System.IO.File]::Move($temp, $full)
        }
    }
    finally {
        if (Test-Path -LiteralPath $temp -PathType Leaf) {
            Remove-Item -LiteralPath $temp -Force
        }
    }
    return $full
}
