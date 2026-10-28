# Shared definitions for tools/unity/build-shared.ps1 and
# tools/unity/check-shared-plugins.ps1.
#
# This file is dot-sourced by both entry points so the producer and the verifier
# use the exact same supported MSBuild input boundary, path normalisation and
# evidence schema. It performs no I/O on its own; callers decide what to write.
#
# Supported MSBuild input boundary (schema version 1)
# -----------------------------------------------------
# The provenance manifest covers:
#   * every file under the seven Unity-consumed shared project directories
#     (dotnet/src/Mahjong.{Domain,Contracts,Contracts.Wire,TableRuntime,Client,
#     LocalHost,Ai}) with a build-relevant extension, excluding bin/ and obj/;
#   * explicit Compile / Protobuf / EmbeddedResource includes declared by those
#     csproj files, when they resolve inside the repository;
#   * the shared build configuration (Directory.Build.props/targets and
#     Directory.Build.rsp at the repository root, dotnet/ and dotnet/src/,
#     Directory.Packages.props, NuGet.config, global.json, Mahjong.sln);
#   * the build tool itself and this shared definition file.
#
# Not covered / rejected fail-closed:
#   * custom <Import Project="..."> elements;
#   * <Compile Remove>, <Protobuf Remove>, <EmbeddedResource Remove> and custom
#     DefaultItemExcludes;
#   * explicit <Reference>, <Analyzer> and <AdditionalFiles> items (their
#     external payload cannot be byte-pinned inside this boundary);
#   * explicit includes outside the repository or containing wildcards;
#   * ProjectReference targets outside the seven supported projects;
#   * the NuGet global packages cache: package assemblies are captured by
#     id/version/relative-asset/SHA-256 in the manifest, not by directory hash.
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

$script:SharedPluginEvidenceSchemaVersion = 1
$script:SharedPluginEvidenceKind = 'mahjong-unity-shared-plugin-provenance'
$script:SharedPluginInputBoundaryVersion = 3

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

# Reject MSBuild constructs that would pull in files the boundary cannot see.
# This is applied to every shared .props/.targets file, not only to the project
# files, so an Import/Remove added to Directory.Build.props cannot silently
# escape the recorded input set.
function Assert-SharedPluginMsBuildConfigBoundary {
    param([Parameter(Mandatory = $true)][string]$Path)

    $xml = New-Object System.Xml.XmlDocument
    $xml.Load($Path)
    $unsupported = $xml.SelectNodes('//*[local-name()="Import"] | //*[local-name()="DefaultItemExcludes"] | //*[local-name()="Compile" and @Remove] | //*[local-name()="Protobuf" and @Remove] | //*[local-name()="EmbeddedResource" and @Remove] | //*[local-name()="Reference"] | //*[local-name()="Analyzer"] | //*[local-name()="AdditionalFiles"]')
    if ($unsupported -and $unsupported.Count -gt 0) {
        throw "Unsupported MSBuild import/removal in shared configuration '$Path'; the provenance boundary cannot cover arbitrary external inputs"
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
        $unsupported = $xml.SelectNodes('//*[local-name()="Compile" and @Remove] | //*[local-name()="Protobuf" and @Remove] | //*[local-name()="DefaultItemExcludes"] | //*[local-name()="Reference"] | //*[local-name()="Analyzer"] | //*[local-name()="AdditionalFiles"]')
        if ($unsupported -and $unsupported.Count -gt 0) {
            throw "Unsupported MSBuild removal, external Reference/Analyzer/AdditionalFiles item in $project.csproj; declare a supported boundary instead"
        }

        $explicitItems = $xml.SelectNodes('//*[local-name()="Compile" or local-name()="Protobuf" or local-name()="EmbeddedResource"]')
        foreach ($node in $explicitItems) {
            $include = $node.GetAttribute('Include')
            if ([string]::IsNullOrWhiteSpace($include)) { continue }
            if ($include -match '[*?]') {
                throw "Unsupported wildcard MSBuild include '$include' in $project.csproj"
            }
            $candidate = if ([System.IO.Path]::IsPathRooted($include)) { $include } else { Join-Path $projectDir $include }
            $full = Resolve-SharedPluginFullPath $candidate
            if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
                throw "Explicit MSBuild input not found: $include ($project.csproj)"
            }
            # Cannot point outside the repository: that would be an uncovered input.
            $null = Get-SharedPluginRelativePath -Root $root -Path $full
            & $addFile $full
        }

        $projectReferences = $xml.SelectNodes('//*[local-name()="ProjectReference"]')
        foreach ($node in $projectReferences) {
            $include = $node.GetAttribute('Include')
            if ([string]::IsNullOrWhiteSpace($include)) { continue }
            if ($include -match '[*?]') {
                throw "Unsupported wildcard ProjectReference '$include' in $project.csproj"
            }
            $referenceFull = Resolve-SharedPluginFullPath (Join-Path $projectDir $include)
            $known = $false
            foreach ($knownProject in $script:SharedPluginProjects) {
                $knownCsproj = Resolve-SharedPluginFullPath (Join-Path $root "dotnet\src\$knownProject\$knownProject.csproj")
                if ($referenceFull -ieq $knownCsproj) { $known = $true; break }
            }
            if (-not $known) {
                throw "ProjectReference outside the seven shared projects: $include ($project.csproj)"
            }
        }

        $files = Get-ChildItem -LiteralPath $projectDir -Recurse -File -Force
        foreach ($file in $files) {
            $relativeToProject = $file.FullName.Substring($projectDir.Length)
            if ($relativeToProject -match '(^|[\\/])(bin|obj)([\\/]|$)') { continue }
            $extension = $file.Extension.ToLowerInvariant()
            if ($script:SharedPluginInputExtensions -notcontains $extension) { continue }
            if ($extension -eq '.props' -or $extension -eq '.targets') {
                Assert-SharedPluginMsBuildConfigBoundary $file.FullName
            }
            & $addFile $file.FullName
        }
    }

    foreach ($relative in @(
        'Directory.Build.props',
        'Directory.Build.targets',
        'Directory.Packages.props',
        'Directory.Build.rsp',
        'dotnet/Directory.Build.props',
        'dotnet/Directory.Build.targets',
        'dotnet/Directory.Packages.props',
        'dotnet/Directory.Build.rsp',
        'dotnet/src/Directory.Build.props',
        'dotnet/src/Directory.Build.targets',
        'dotnet/src/Directory.Packages.props',
        'dotnet/NuGet.config',
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
