<#
.SYNOPSIS
    Read-only verification of the Unity shared-plugin provenance record.

.DESCRIPTION
    Validates, without building or copying anything, that the shared plugin
    closure currently present in the Unity project was produced from the current
    supported MSBuild input closure and a successful forced build.

    A PASS requires all of the following:
      * a structurally valid schema-2 provenance record with every required
        build field and a successful exitCode=0 forced build;
      * the recorded requested/recorded configuration and evidence scope match
        the inspected destination;
      * the recorded input closure is byte-identical to the current input
        closure (modified/added/removed sources, project props/targets, rsp,
        repository NuGet.config and dependency-lock files all fail);
      * both producer tool files match the recorded identity;
      * every recorded plugin output exists with the recorded length/SHA-256;
      * the seven own assemblies are project outputs and package outputs match
        package entries bidirectionally;
      * the real runtime closure is independently re-resolved from
        obj/project.assets.json and the NuGet cache, so deleting an omitted
        dependency from both the record and the plugin directory cannot pass;
      * no unexpected DLL was left in the plugin directory;
      * when bin/<requested-config>/netstandard2.1 still exists, it matches the
        synced bytes (detects "rebuilt but not synced").

    Any drift exits non-zero and prints the exact paths and hash prefixes.
    Missing/malformed/empty evidence is a failure, not a silent pass. This
    script never builds, never copies and never edits the record. Input
    enumeration fails closed on MSBuild constructs the supported boundary
    cannot resolve.

.PARAMETER ProjectRoot
    Repository root. Defaults to the parent of tools/unity.

.PARAMETER PluginDir
    Plugin destination to inspect. Defaults to
    unity/Mahjong.Unity/Assets/Plugins/Mahjong.

.PARAMETER EvidencePath
    Provenance record to inspect. Defaults to
    unity/Mahjong.Unity/Library/SharedPluginProvenance/provenance.json.

.PARAMETER Configuration
    Build configuration the record must describe. Defaults to Release (the
    configuration used for Unity player builds).

.PARAMETER Json
    Emit a machine-readable JSON result on stdout.

.PARAMETER Quiet
    Print nothing on PASS. Failures are always printed.

.PARAMETER MaxDifferences
    Maximum number of individual differences printed per failure category.
#>
[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$PluginDir,
    [string]$EvidencePath,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$Json,
    [switch]$Quiet,
    [int]$MaxDifferences = 40
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'shared-plugin-provenance-lib.ps1')

function Get-ManifestProperty {
    param($Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-SharedPluginDifferenceCode {
    param([string]$Kind, [string]$Path)
    $lower = $Path.ToLowerInvariant()
    if ($lower -eq 'tools/unity/build-shared.ps1' -or $lower -eq 'tools/unity/shared-plugin-provenance-lib.ps1') {
        return 'TOOL_CHANGED'
    }
    if ($lower.EndsWith('.cs')) {
        if ($Kind -eq 'added') { return 'SOURCE_ADDED' }
        if ($Kind -eq 'removed') { return 'SOURCE_REMOVED' }
        return 'SOURCE_CHANGED'
    }
    if ($lower.EndsWith('.proto')) {
        if ($Kind -eq 'added') { return 'PROTO_ADDED' }
        if ($Kind -eq 'removed') { return 'PROTO_REMOVED' }
        return 'PROTO_CHANGED'
    }
    if ($lower.EndsWith('packages.lock.json')) {
        if ($Kind -eq 'added') { return 'DEPENDENCY_ADDED' }
        if ($Kind -eq 'removed') { return 'DEPENDENCY_REMOVED' }
        return 'DEPENDENCY_CHANGED'
    }
    if ($lower.EndsWith('.csproj') -or $lower.EndsWith('.props') -or $lower.EndsWith('.targets') -or
        $lower.EndsWith('.sln') -or $lower.EndsWith('nuget.config') -or $lower.EndsWith('global.json') -or
        $lower.EndsWith('.ruleset') -or $lower.EndsWith('.editorconfig') -or $lower.EndsWith('.rsp')) {
        if ($Kind -eq 'added') { return 'BUILD_CONFIG_ADDED' }
        if ($Kind -eq 'removed') { return 'BUILD_CONFIG_REMOVED' }
        return 'BUILD_CONFIG_CHANGED'
    }
    if ($Kind -eq 'added') { return 'INPUT_ADDED' }
    if ($Kind -eq 'removed') { return 'INPUT_REMOVED' }
    return 'INPUT_CHANGED'
}

function Test-Sha256Text {
    param([string]$Value)
    return ($Value -match '^[0-9A-Fa-f]{64}$')
}

function Read-IsoTimestamp {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
    try {
        return [DateTime]::Parse($Value, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind)
    }
    catch {
        return $null
    }
}

$root = if ($ProjectRoot) {
    Resolve-SharedPluginFullPath $ProjectRoot
}
else {
    Resolve-SharedPluginFullPath (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
}
$unityProjectDir = Join-Path $root 'unity\Mahjong.Unity'
$defaultPluginDirFull = Resolve-SharedPluginFullPath (Join-Path $unityProjectDir 'Assets\Plugins\Mahjong')
$pluginDirFull = if ($PluginDir) {
    Resolve-SharedPluginFullPath $PluginDir
}
else {
    $defaultPluginDirFull
}
$evidenceFull = if ($EvidencePath) {
    Resolve-SharedPluginFullPath $EvidencePath
}
else {
    Resolve-SharedPluginFullPath (Join-Path $unityProjectDir 'Library\SharedPluginProvenance\provenance.json')
}
$isDefaultTarget = ($pluginDirFull -ieq $defaultPluginDirFull)
$failures = New-Object System.Collections.Generic.List[object]
$internalError = $false
$manifest = $null
$expectedInputs = @()
$expectedOutputs = @()
$inputCount = 0
$outputCount = 0
$evidenceSha = $null
$generatedAtUtc = $null

function Add-Failure {
    param([string]$Code, [string]$Message)
    $failures.Add([pscustomobject]@{ Code = $Code; Message = $Message }) | Out-Null
}

try {
    if (-not (Test-Path -LiteralPath $unityProjectDir -PathType Container)) {
        Add-Failure 'PROJECT_MISSING' "Unity project directory not found: $unityProjectDir"
    }
    if (-not (Test-Path -LiteralPath $pluginDirFull -PathType Container)) {
        Add-Failure 'PLUGIN_DIR_MISSING' "Unity plugin directory not found: $pluginDirFull"
    }

    $read = Read-SharedPluginProvenance -Path $evidenceFull
    if (-not $read.Ok) {
        Add-Failure $read.Code $read.Message
    }
    else {
        $evidenceSha = $read.Sha256
        $manifest = $read.Data

        # --- schema and top-level identity -----------------------------------
        $schemaVersion = Get-ManifestProperty $manifest 'schemaVersion'
        $kind = [string](Get-ManifestProperty $manifest 'kind')
        $recordedConfiguration = [string](Get-ManifestProperty $manifest 'configuration')
        $project = [string](Get-ManifestProperty $manifest 'project')
        $pluginDirRecorded = [string](Get-ManifestProperty $manifest 'pluginDir')
        $pluginDirRecordedRelative = [string](Get-ManifestProperty $manifest 'pluginDirRelative')
        $generatedAtUtc = [string](Get-ManifestProperty $manifest 'generatedAtUtc')
        $targetFramework = [string](Get-ManifestProperty $manifest 'targetFramework')
        $langVersionFloor = [string](Get-ManifestProperty $manifest 'langVersionFloor')
        $evidenceScope = [string](Get-ManifestProperty $manifest 'evidenceScope')
        $recordedIsDefault = ($pluginDirRecordedRelative -eq 'unity/Mahjong.Unity/Assets/Plugins/Mahjong')

        if ($null -eq $schemaVersion -or ([string]$schemaVersion -notmatch '^\d+$')) {
            Add-Failure 'EVIDENCE_SCHEMA_MISSING' 'Provenance record has no numeric schemaVersion.'
        }
        elseif ([int]$schemaVersion -ne $script:SharedPluginEvidenceSchemaVersion) {
            Add-Failure 'EVIDENCE_SCHEMA_UNSUPPORTED' "Unsupported provenance schemaVersion '$schemaVersion' (expected $($script:SharedPluginEvidenceSchemaVersion))."
        }
        if ($kind -ne $script:SharedPluginEvidenceKind) {
            Add-Failure 'EVIDENCE_KIND_INVALID' "Provenance record kind '$kind' is not '$($script:SharedPluginEvidenceKind)'."
        }
        if ($recordedConfiguration -notin @('Debug', 'Release')) {
            Add-Failure 'EVIDENCE_CONFIGURATION_INVALID' "Provenance record configuration '$recordedConfiguration' is not Debug/Release."
        }
        elseif ($recordedConfiguration -ine $Configuration) {
            Add-Failure 'CONFIGURATION_MISMATCH' "Provenance record was built for '$recordedConfiguration' but '$Configuration' was requested."
        }
        if ($project -ne 'unity/Mahjong.Unity') {
            Add-Failure 'EVIDENCE_PROJECT_INVALID' "Provenance record project '$project' is not 'unity/Mahjong.Unity'."
        }
        if ($targetFramework -ne 'netstandard2.1') {
            Add-Failure 'EVIDENCE_TFM_INVALID' "Provenance record targetFramework '$targetFramework' is not netstandard2.1."
        }
        if ($langVersionFloor -ne '9.0') {
            Add-Failure 'EVIDENCE_LANGVERSION_INVALID' "Provenance record langVersionFloor '$langVersionFloor' is not 9.0."
        }
        if ($null -eq (Read-IsoTimestamp $generatedAtUtc)) {
            Add-Failure 'EVIDENCE_GENERATED_AT_INVALID' "Provenance record generatedAtUtc '$generatedAtUtc' is not a valid timestamp."
        }
        if ($evidenceScope -notin @('default-synced', 'scratch')) {
            Add-Failure 'EVIDENCE_SCOPE_INVALID' "Provenance record evidenceScope '$evidenceScope' is invalid."
        }
        elseif ($recordedIsDefault -and $evidenceScope -ne 'default-synced') {
            Add-Failure 'EVIDENCE_SCOPE_MISMATCH' "Scratch evidence cannot describe the default synced plugin directory ($pluginDirFull)."
        }
        elseif (-not $recordedIsDefault -and $evidenceScope -ne 'scratch') {
            Add-Failure 'EVIDENCE_SCOPE_MISMATCH' "Default-synced evidence cannot describe a scratch plugin directory ($pluginDirFull)."
        }
        if ([string]::IsNullOrWhiteSpace($pluginDirRecorded)) {
            Add-Failure 'EVIDENCE_TARGET_MISSING' 'Provenance record has no pluginDir.'
        }
        else {
            if ([string]::IsNullOrWhiteSpace($pluginDirRecordedRelative)) {
                Add-Failure 'EVIDENCE_RELATIVE_TARGET_MISSING' 'Provenance record has no pluginDirRelative.'
            }
            $recordedFull = Resolve-SharedPluginFullPath $pluginDirRecorded
            if ($recordedFull -ine $pluginDirFull) {
                Add-Failure 'EVIDENCE_TARGET_MISMATCH' "Provenance record describes '$recordedFull' but the inspected plugin directory is '$pluginDirFull'."
            }
        }

        $boundary = Get-ManifestProperty $manifest 'inputBoundary'
        $boundaryVersion = Get-ManifestProperty $boundary 'version'
        if ($null -eq $boundaryVersion -or ([string]$boundaryVersion -notmatch '^\d+$') -or [int]$boundaryVersion -ne $script:SharedPluginInputBoundaryVersion) {
            Add-Failure 'EVIDENCE_BOUNDARY_UNSUPPORTED' "Provenance record input boundary version '$boundaryVersion' is not supported."
        }

        # --- successful forced build metadata --------------------------------
        $build = Get-ManifestProperty $manifest 'build'
        if ($null -eq $build) {
            Add-Failure 'EVIDENCE_BUILD_MISSING' 'Provenance record has no build metadata.'
        }
        else {
            $buildExit = Get-ManifestProperty $build 'exitCode'
            $buildMode = [string](Get-ManifestProperty $build 'mode')
            $forcedRebuild = Get-ManifestProperty $build 'forcedRebuild'
            $copiedCount = Get-ManifestProperty $build 'copiedFileCount'
            $buildInputCount = Get-ManifestProperty $build 'inputCount'
            $startedAt = Read-IsoTimestamp ([string](Get-ManifestProperty $build 'startedAtUtc'))
            $completedAt = Read-IsoTimestamp ([string](Get-ManifestProperty $build 'completedAtUtc'))
            $elapsedSeconds = Get-ManifestProperty $build 'elapsedSeconds'

            if ($null -eq $buildExit -or ([string]$buildExit -notmatch '^-?\d+$')) {
                Add-Failure 'EVIDENCE_BUILD_EXIT_MISSING' 'Provenance record build.exitCode is missing or not numeric.'
            }
            elseif ([int]$buildExit -ne 0) {
                Add-Failure 'EVIDENCE_BUILD_FAILED' "Provenance record describes a failed build (exitCode=$buildExit)."
            }
            if ($buildMode -ne 'forced-clean-outputs') {
                Add-Failure 'EVIDENCE_BUILD_MODE_INVALID' "Provenance record build.mode '$buildMode' is not forced-clean-outputs."
            }
            if ($forcedRebuild -ne $true) {
                Add-Failure 'EVIDENCE_BUILD_NOT_FORCED' 'Provenance record does not prove a forced build.'
            }
            if ($null -eq $startedAt -or $null -eq $completedAt) {
                Add-Failure 'EVIDENCE_BUILD_TIME_INVALID' 'Provenance record build start/complete timestamps are missing or invalid.'
            }
            elseif ($completedAt -lt $startedAt) {
                Add-Failure 'EVIDENCE_BUILD_TIME_INVALID' 'Provenance record build completed before it started.'
            }
            if ($null -eq $elapsedSeconds -or [double]$elapsedSeconds -lt 0) {
                Add-Failure 'EVIDENCE_BUILD_ELAPSED_INVALID' 'Provenance record build.elapsedSeconds is missing or negative.'
            }
            if ($null -eq $copiedCount -or ([string]$copiedCount -notmatch '^\d+$')) {
                Add-Failure 'EVIDENCE_BUILD_COPIED_COUNT_INVALID' 'Provenance record build.copiedFileCount is missing or not numeric.'
            }
            if ($null -eq $buildInputCount -or ([string]$buildInputCount -notmatch '^\d+$')) {
                Add-Failure 'EVIDENCE_BUILD_INPUT_COUNT_INVALID' 'Provenance record build.inputCount is missing or not numeric.'
            }
        }

        # --- tool identity ----------------------------------------------------
        $tool = Get-ManifestProperty $manifest 'tool'
        $buildTool = Get-ManifestProperty $tool 'buildShared'
        $sharedTool = Get-ManifestProperty $tool 'sharedDefinition'
        $recordedBuildToolSha = [string](Get-ManifestProperty $buildTool 'Sha256')
        $recordedSharedToolSha = [string](Get-ManifestProperty $sharedTool 'Sha256')
        try {
            $currentTools = @(Get-SharedPluginToolIdentity -RepoRoot $root)
            $currentBuildTool = $currentTools | Where-Object { $_.Path -eq 'tools/unity/build-shared.ps1' } | Select-Object -First 1
            $currentSharedTool = $currentTools | Where-Object { $_.Path -eq 'tools/unity/shared-plugin-provenance-lib.ps1' } | Select-Object -First 1
            if (-not $recordedBuildToolSha -or $currentBuildTool.Sha256 -ine $recordedBuildToolSha) {
                Add-Failure 'TOOL_CHANGED' "build-shared.ps1 does not match the recorded tool identity (recorded $recordedBuildToolSha, current $($currentBuildTool.Sha256))."
            }
            if (-not $recordedSharedToolSha -or $currentSharedTool.Sha256 -ine $recordedSharedToolSha) {
                Add-Failure 'TOOL_CHANGED' "shared-plugin-provenance-lib.ps1 does not match the recorded tool identity (recorded $recordedSharedToolSha, current $($currentSharedTool.Sha256))."
            }
        }
        catch {
            Add-Failure 'TOOL_UNREADABLE' "Could not read the current build tool identity: $($_.Exception.Message)"
        }

        # --- inputs -----------------------------------------------------------
        $recordedInputs = @(Get-ManifestProperty $manifest 'inputs')
        if ($recordedInputs.Count -eq 0) {
            Add-Failure 'EVIDENCE_INPUTS_EMPTY' 'Provenance record has no inputs.'
        }
        $seenInputs = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $recordedInputs) {
            $path = [string](Get-ManifestProperty $entry 'Path')
            $sha = [string](Get-ManifestProperty $entry 'Sha256')
            $length = Get-ManifestProperty $entry 'Length'
            if ([string]::IsNullOrWhiteSpace($path) -or [System.IO.Path]::IsPathRooted($path) -or $path -match '(^|/)\.\.(/|$)') {
                Add-Failure 'EVIDENCE_INPUT_PATH_INVALID' "Invalid input path in provenance record: '$path'."
                continue
            }
            $normalized = $path.Replace('\', '/')
            if (-not (Test-Sha256Text $sha)) {
                Add-Failure 'EVIDENCE_INPUT_HASH_INVALID' "Invalid SHA-256 for recorded input '$normalized'."
                continue
            }
            if ($null -eq $length -or [long]$length -lt 0) {
                Add-Failure 'EVIDENCE_INPUT_LENGTH_INVALID' "Invalid length for recorded input '$normalized'."
                continue
            }
            if (-not $seenInputs.Add($normalized)) {
                Add-Failure 'EVIDENCE_INPUT_DUPLICATE' "Duplicate input path in provenance record: '$normalized'."
                continue
            }
            $expectedInputs += [pscustomobject]@{
                Path   = $normalized
                Length = [long]$length
                Sha256 = $sha.ToUpperInvariant()
            }
        }
        $inputCount = $expectedInputs.Count
        if ($null -ne $build) {
            $buildInputCount = Get-ManifestProperty $build 'inputCount'
            if ($null -ne $buildInputCount -and [int]$buildInputCount -ne $inputCount) {
                Add-Failure 'EVIDENCE_BUILD_INPUT_COUNT_MISMATCH' "build.inputCount=$buildInputCount but the record lists $inputCount inputs."
            }
        }

        # --- outputs ----------------------------------------------------------
        $recordedOutputs = @(Get-ManifestProperty $manifest 'outputs')
        if ($recordedOutputs.Count -eq 0) {
            Add-Failure 'EVIDENCE_OUTPUTS_EMPTY' 'Provenance record has no outputs.'
        }
        $seenOutputs = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $recordedOutputs) {
            $name = [string](Get-ManifestProperty $entry 'Name')
            $sha = [string](Get-ManifestProperty $entry 'Sha256')
            $length = Get-ManifestProperty $entry 'Length'
            $sourceKind = [string](Get-ManifestProperty $entry 'SourceKind')
            $entryProject = [string](Get-ManifestProperty $entry 'Project')
            if ([string]::IsNullOrWhiteSpace($name) -or $name -match '[\\/]' -or $name -eq '.' -or $name -eq '..') {
                Add-Failure 'EVIDENCE_OUTPUT_NAME_INVALID' "Invalid output name in provenance record: '$name'."
                continue
            }
            if (-not (Test-Sha256Text $sha)) {
                Add-Failure 'EVIDENCE_OUTPUT_HASH_INVALID' "Invalid SHA-256 for recorded output '$name'."
                continue
            }
            if ($null -eq $length -or [long]$length -lt 0) {
                Add-Failure 'EVIDENCE_OUTPUT_LENGTH_INVALID' "Invalid length for recorded output '$name'."
                continue
            }
            if (-not $seenOutputs.Add($name)) {
                Add-Failure 'EVIDENCE_OUTPUT_DUPLICATE' "Duplicate output in provenance record: '$name'."
                continue
            }
            if ($sourceKind -notin @('project', 'package')) {
                Add-Failure 'EVIDENCE_OUTPUT_SOURCE_INVALID' "Output '$name' has invalid SourceKind '$sourceKind'."
                continue
            }
            $isPdb = $name.EndsWith('.pdb', [System.StringComparison]::OrdinalIgnoreCase)
            $isDll = $name.EndsWith('.dll', [System.StringComparison]::OrdinalIgnoreCase)
            if (-not $isPdb -and -not $isDll) {
                Add-Failure 'EVIDENCE_OUTPUT_KIND_INVALID' "Output '$name' is neither DLL nor PDB."
                continue
            }
            if ($sourceKind -eq 'project') {
                if ($entryProject -notin $script:SharedPluginProjects) {
                    Add-Failure 'EVIDENCE_OUTPUT_PROJECT_INVALID' "Project output '$name' names unknown project '$entryProject'."
                    continue
                }
                $expectedName = if ($isPdb) { "$entryProject.pdb" } else { "$entryProject.dll" }
                if ($name -ine $expectedName) {
                    Add-Failure 'EVIDENCE_OUTPUT_PROJECT_NAME_MISMATCH' "Project output '$name' does not match project '$entryProject'."
                    continue
                }
            }
            $expectedOutputs += [pscustomobject]@{
                Name       = $name
                Length     = [long]$length
                Sha256     = $sha.ToUpperInvariant()
                SourceKind = $sourceKind
                Project    = $entryProject
            }
        }
        $outputCount = $expectedOutputs.Count
        if ($null -ne $build) {
            $copiedCount = Get-ManifestProperty $build 'copiedFileCount'
            if ($null -ne $copiedCount -and [int]$copiedCount -ne $outputCount) {
                Add-Failure 'EVIDENCE_BUILD_COPIED_COUNT_MISMATCH' "build.copiedFileCount=$copiedCount but the record lists $outputCount outputs."
            }
        }

        foreach ($project in $script:SharedPluginProjects) {
            $dllName = "$project.dll"
            $match = $expectedOutputs | Where-Object { $_.Name -ieq $dllName -and $_.SourceKind -eq 'project' -and $_.Project -ieq $project } | Select-Object -First 1
            if (-not $match) {
                Add-Failure 'EVIDENCE_OWN_OUTPUT_MISSING' "Provenance record does not list own project output '$dllName' for '$project'."
            }
        }
        foreach ($required in $script:SharedPluginRequiredAssemblies) {
            if (-not (Test-Path -LiteralPath (Join-Path $pluginDirFull $required) -PathType Leaf)) {
                Add-Failure 'PLUGIN_REQUIRED_MISSING' "Required plugin assembly is missing: $required."
            }
        }

        # --- packages and independent runtime closure -------------------------
        $recordedPackages = @(Get-ManifestProperty $manifest 'packages')
        if ($recordedPackages.Count -eq 0) {
            Add-Failure 'EVIDENCE_PACKAGES_EMPTY' 'Provenance record has no resolved package closure.'
        }
        $packageMap = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($package in $recordedPackages) {
            $packageId = [string](Get-ManifestProperty $package 'Id')
            $packageVersion = [string](Get-ManifestProperty $package 'Version')
            $assembly = [string](Get-ManifestProperty $package 'Assembly')
            $assetPath = [string](Get-ManifestProperty $package 'AssetPath')
            $packageSha = [string](Get-ManifestProperty $package 'Sha256')
            $packageLength = Get-ManifestProperty $package 'Length'
            if ([string]::IsNullOrWhiteSpace($packageId) -or [string]::IsNullOrWhiteSpace($packageVersion) -or
                [string]::IsNullOrWhiteSpace($assembly) -or [string]::IsNullOrWhiteSpace($assetPath) -or
                -not (Test-Sha256Text $packageSha) -or $null -eq $packageLength -or [long]$packageLength -lt 0) {
                Add-Failure 'EVIDENCE_PACKAGE_INVALID' 'Provenance record contains an invalid package entry.'
                continue
            }
            if ($packageMap.ContainsKey($assembly)) {
                Add-Failure 'EVIDENCE_PACKAGE_DUPLICATE' "Duplicate package assembly in provenance record: $assembly."
                continue
            }
            $packageMap[$assembly] = $package
            $matchingOutput = $expectedOutputs | Where-Object { $_.Name -ieq $assembly } | Select-Object -First 1
            if (-not $matchingOutput -or $matchingOutput.SourceKind -ne 'package' -or
                $matchingOutput.Sha256 -ine $packageSha.ToUpperInvariant() -or [long]$matchingOutput.Length -ne [long]$packageLength) {
                Add-Failure 'EVIDENCE_PACKAGE_HASH_MISMATCH' "Package '$packageId $packageVersion' assembly '$assembly' does not match a package output with identical bytes."
            }
        }
        foreach ($output in $expectedOutputs) {
            if ($output.SourceKind -eq 'package' -and -not $packageMap.ContainsKey($output.Name)) {
                Add-Failure 'EVIDENCE_PACKAGE_OUTPUT_UNBOUND' "Package output '$($output.Name)' has no package entry."
            }
        }
        if (-not $packageMap.ContainsKey('Google.Protobuf.dll')) {
            Add-Failure 'EVIDENCE_REQUIRED_PACKAGE_MISSING' 'Provenance record does not include the required Google.Protobuf.dll package closure.'
        }

        # Independently re-resolve the real runtime closure. The record cannot
        # hide a dependency by deleting both its entry and its plugin file.
        $runtime = Get-SharedPluginResolvedRuntimeClosure -RepoRoot $root -Projects $script:SharedPluginProjects
        if (-not $runtime.Ok) {
            Add-Failure 'RUNTIME_CLOSURE_UNRESOLVED' "Could not independently resolve the runtime closure: $($runtime.Errors -join '; ')"
        }
        else {
            $closureMap = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([System.StringComparer]::OrdinalIgnoreCase)
            foreach ($assembly in @($runtime.Assemblies)) {
                $closureMap[$assembly.Name] = $assembly
                if (-not $packageMap.ContainsKey($assembly.Name)) {
                    Add-Failure 'RUNTIME_CLOSURE_MISSING' "Resolved runtime dependency '$($assembly.Name)' '$($assembly.PackageId) $($assembly.PackageVersion)' is missing from the provenance record."
                    continue
                }
                $package = $packageMap[$assembly.Name]
                if ([string](Get-ManifestProperty $package 'Sha256') -ine $assembly.Sha256 -or
                    [long](Get-ManifestProperty $package 'Length') -ne [long]$assembly.Length) {
                    Add-Failure 'RUNTIME_CLOSURE_MISMATCH' "Resolved runtime dependency '$($assembly.Name)' does not match the recorded package bytes."
                }
            }
            foreach ($name in $packageMap.Keys) {
                if (-not $closureMap.ContainsKey($name)) {
                    Add-Failure 'RUNTIME_CLOSURE_EXTRA' "Recorded package assembly '$name' is not part of the independently resolved runtime closure."
                }
            }
        }

        # --- input drift ------------------------------------------------------
        try {
            $actualInputs = @(Get-SharedPluginInputClosure -RepoRoot $root)
            if ($expectedInputs.Count -gt 0) {
                $diff = Compare-SharedPluginInputClosure -Expected $expectedInputs -Actual $actualInputs
                foreach ($entry in @($diff.Added)) {
                    $code = Get-SharedPluginDifferenceCode 'added' $entry.Path
                    Add-Failure $code "Added input: $($entry.Path) ($($entry.Length) bytes, $($entry.Sha256.Substring(0, 12))...)."
                }
                foreach ($entry in @($diff.Removed)) {
                    $code = Get-SharedPluginDifferenceCode 'removed' $entry.Path
                    Add-Failure $code "Removed input: $($entry.Path)."
                }
                foreach ($entry in @($diff.Modified)) {
                    $code = Get-SharedPluginDifferenceCode 'modified' $entry.Path
                    Add-Failure $code ("Changed input: {0} (length {1} -> {2}, SHA-256 {3}... -> {4}...)." -f `
                        $entry.Path, $entry.ExpectedLength, $entry.ActualLength, $entry.ExpectedSha256.Substring(0, 12), $entry.ActualSha256.Substring(0, 12))
                }
            }
        }
        catch {
            Add-Failure 'UNSUPPORTED_INPUT_BOUNDARY' "Current MSBuild inputs cannot be enumerated within the supported boundary: $($_.Exception.Message)"
        }

        # --- plugin output bytes ---------------------------------------------
        if (Test-Path -LiteralPath $pluginDirFull -PathType Container) {
            foreach ($entry in $expectedOutputs) {
                $destination = Join-Path $pluginDirFull $entry.Name
                if (-not (Test-Path -LiteralPath $destination -PathType Leaf)) {
                    Add-Failure 'PLUGIN_MISSING' "Plugin file is missing: $($entry.Name) at $destination."
                    continue
                }
                $item = Get-Item -LiteralPath $destination
                $actualSha = Get-SharedPluginFileSha256 $destination
                if ([long]$item.Length -ne [long]$entry.Length -or $actualSha -ine $entry.Sha256) {
                    Add-Failure 'PLUGIN_CORRUPT' ("Plugin file does not match provenance: {0} (length {1} != {2}, SHA-256 {3}... != {4}...)." -f `
                        $entry.Name, $item.Length, $entry.Length, $actualSha.Substring(0, 12), $entry.Sha256.Substring(0, 12))
                }
            }

            foreach ($file in @(Get-ChildItem -LiteralPath $pluginDirFull -File -Filter '*.dll' -ErrorAction SilentlyContinue)) {
                if (-not $seenOutputs.Contains($file.Name)) {
                    Add-Failure 'PLUGIN_UNEXPECTED' "Unexpected DLL in the plugin directory (not part of the recorded closure): $($file.Name)."
                }
            }
        }

        # --- current build output drift --------------------------------------
        foreach ($entry in $expectedOutputs) {
            if ($entry.SourceKind -ne 'project' -or [string]::IsNullOrWhiteSpace($entry.Project)) { continue }
            $bin = Join-Path $root ("dotnet\src\{0}\bin\{1}\netstandard2.1\{2}" -f $entry.Project, $Configuration, $entry.Name)
            if (-not (Test-Path -LiteralPath $bin -PathType Leaf)) { continue }
            $binSha = Get-SharedPluginFileSha256 $bin
            if ($binSha -ine $entry.Sha256) {
                Add-Failure 'BUILD_OUTPUT_DRIFT' "Build output is newer/different than the synced plugin: $bin (build $binSha, record $($entry.Sha256)). Run build-shared.ps1."
            }
        }
    }
}
catch {
    $internalError = $true
    Add-Failure 'INTERNAL_ERROR' "Unhandled verifier error: $($_.Exception.Message)"
}

$ok = ($failures.Count -eq 0)
$firstCode = if ($ok) { 'OK' } else { [string]$failures[0].Code }
$summary = [ordered]@{
    Ok               = $ok
    Code             = $firstCode
    EvidencePath     = $evidenceFull
    EvidenceSha256   = $evidenceSha
    PluginDir        = $pluginDirFull
    Configuration    = $Configuration
    SchemaVersion    = $script:SharedPluginEvidenceSchemaVersion
    GeneratedAtUtc   = $generatedAtUtc
    InputCount       = $inputCount
    OutputCount      = $outputCount
    RequiredAssemblies = $script:SharedPluginRequiredAssemblies
    FailureCount     = $failures.Count
    Failures         = $failures.ToArray()
}

if ($Json) {
    $summary | ConvertTo-Json -Depth 8
}

if (-not $Json) {
    if ($ok) {
        if (-not $Quiet) {
            Write-Host 'SHARED_PLUGIN_PROVENANCE: PASS'
            Write-Host "evidence  : $evidenceFull"
            Write-Host "pluginDir : $pluginDirFull"
            Write-Host "inputs    : $inputCount files (schema $($script:SharedPluginEvidenceSchemaVersion), boundary $($script:SharedPluginInputBoundaryVersion))"
            Write-Host "outputs   : $outputCount files ($($script:SharedPluginRequiredAssemblies.Count) required assemblies + PDBs/packages)"
        }
    }
    else {
        Write-Host 'SHARED_PLUGIN_PROVENANCE: FAIL'
        Write-Host "evidence  : $evidenceFull"
        Write-Host "pluginDir : $pluginDirFull"
        Write-Host "failures  : $($failures.Count)"
        $printed = 0
        foreach ($failure in $failures) {
            if ($printed -ge $MaxDifferences) {
                Write-Host "  ... $($failures.Count - $printed) more failure(s) omitted from console output"
                break
            }
            Write-Host ("  [{0}] {1}" -f $failure.Code, $failure.Message)
            $printed++
        }
        Write-Host "Fix: run powershell -NoProfile -ExecutionPolicy Bypass -File tools\unity\build-shared.ps1 -Configuration $Configuration"
    }
}
elseif (-not $ok) {
    [Console]::Error.WriteLine("SHARED_PLUGIN_PROVENANCE: FAIL ($firstCode)")
}

if ($ok) {
    exit 0
}
if ($internalError) {
    exit 2
}
exit 1
