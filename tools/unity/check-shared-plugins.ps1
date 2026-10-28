<#
.SYNOPSIS
    Read-only verification of the Unity shared-plugin provenance record.

.DESCRIPTION
    Validates, without building or copying anything, that the shared plugin
    closure currently present in the Unity project was produced from the current
    supported MSBuild input closure.

    A PASS requires all of the following:
      * a structurally valid provenance record under the Unity project's
        ignored Library/SharedPluginProvenance/ directory (or an explicitly
        supplied scratch record);
      * the recorded input closure is byte-identical to the current input
        closure (no modified, added or removed sources, project/build config,
        proto or dependency-lock files);
      * the build tool source matches the recorded tool identity;
      * every recorded plugin output exists with the recorded length/SHA-256;
      * the seven own assemblies plus the real third-party runtime closure are
        present, and no unexpected DLL was left in the plugin directory;
      * when the build output directory still exists, the plugin bytes still
        match the most recent build output (detects "rebuilt but not synced").

    Any drift exits non-zero and prints the exact paths and hash prefixes.
    Missing/malformed/empty evidence is a failure, not a silent pass. This
    script never builds, never copies, never edits the record, and never uses
    optional-path tolerance to turn an unknown state into PASS.

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
        $lower.EndsWith('.ruleset') -or $lower.EndsWith('.editorconfig')) {
        if ($Kind -eq 'added') { return 'BUILD_CONFIG_ADDED' }
        if ($Kind -eq 'removed') { return 'BUILD_CONFIG_REMOVED' }
        return 'BUILD_CONFIG_CHANGED'
    }
    if ($Kind -eq 'added') { return 'INPUT_ADDED' }
    if ($Kind -eq 'removed') { return 'INPUT_REMOVED' }
    return 'INPUT_CHANGED'
}

$root = if ($ProjectRoot) {
    Resolve-SharedPluginFullPath $ProjectRoot
}
else {
    Resolve-SharedPluginFullPath (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
}
$unityProjectDir = Join-Path $root 'unity\Mahjong.Unity'
$pluginDirFull = if ($PluginDir) {
    Resolve-SharedPluginFullPath $PluginDir
}
else {
    Resolve-SharedPluginFullPath (Join-Path $unityProjectDir 'Assets\Plugins\Mahjong')
}
$evidenceFull = if ($EvidencePath) {
    Resolve-SharedPluginFullPath $EvidencePath
}
else {
    Resolve-SharedPluginFullPath (Join-Path $unityProjectDir 'Library\SharedPluginProvenance\provenance.json')
}

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

        $schemaVersion = Get-ManifestProperty $manifest 'schemaVersion'
        $kind = [string](Get-ManifestProperty $manifest 'kind')
        $configuration = [string](Get-ManifestProperty $manifest 'configuration')
        $project = [string](Get-ManifestProperty $manifest 'project')
        $pluginDirRecorded = [string](Get-ManifestProperty $manifest 'pluginDir')
        $generatedAtUtc = [string](Get-ManifestProperty $manifest 'generatedAtUtc')

        if ($null -eq $schemaVersion) {
            Add-Failure 'EVIDENCE_SCHEMA_MISSING' 'Provenance record has no schemaVersion.'
        }
        elseif ([int]$schemaVersion -ne $script:SharedPluginEvidenceSchemaVersion) {
            Add-Failure 'EVIDENCE_SCHEMA_UNSUPPORTED' "Unsupported provenance schemaVersion '$schemaVersion' (expected $($script:SharedPluginEvidenceSchemaVersion))."
        }
        if ($kind -ne $script:SharedPluginEvidenceKind) {
            Add-Failure 'EVIDENCE_KIND_INVALID' "Provenance record kind '$kind' is not '$($script:SharedPluginEvidenceKind)'."
        }
        if ($configuration -ine $Configuration) {
            Add-Failure 'CONFIGURATION_MISMATCH' "Provenance record was built for '$configuration' but '$Configuration' is required."
        }
        if ($project -ne 'unity/Mahjong.Unity') {
            Add-Failure 'EVIDENCE_PROJECT_INVALID' "Provenance record project '$project' is not 'unity/Mahjong.Unity'."
        }
        if ([string]::IsNullOrWhiteSpace($pluginDirRecorded)) {
            Add-Failure 'EVIDENCE_TARGET_MISSING' 'Provenance record has no pluginDir.'
        }
        else {
            $recordedFull = Resolve-SharedPluginFullPath $pluginDirRecorded
            if ($recordedFull -ine $pluginDirFull) {
                Add-Failure 'EVIDENCE_TARGET_MISMATCH' "Provenance record describes '$recordedFull' but the inspected plugin directory is '$pluginDirFull'."
            }
        }

        $boundary = Get-ManifestProperty $manifest 'inputBoundary'
        $boundaryVersion = Get-ManifestProperty $boundary 'version'
        if ($null -eq $boundaryVersion -or [int]$boundaryVersion -ne $script:SharedPluginInputBoundaryVersion) {
            Add-Failure 'EVIDENCE_BOUNDARY_UNSUPPORTED' "Provenance record input boundary version '$boundaryVersion' is not supported."
        }

        # Build tool identity is part of the record, not something the checker
        # may silently refresh.
        $tool = Get-ManifestProperty $manifest 'tool'
        $buildTool = Get-ManifestProperty $tool 'buildShared'
        $recordedToolSha = [string](Get-ManifestProperty $buildTool 'Sha256')
        try {
            $currentTools = @(Get-SharedPluginToolIdentity -RepoRoot $root)
            $currentBuildTool = $currentTools | Where-Object { $_.Path -eq 'tools/unity/build-shared.ps1' } | Select-Object -First 1
            if (-not $recordedToolSha -or $currentBuildTool.Sha256 -ine $recordedToolSha) {
                Add-Failure 'TOOL_CHANGED' "build-shared.ps1 does not match the recorded tool identity (recorded $recordedToolSha, current $($currentBuildTool.Sha256))."
            }
        }
        catch {
            Add-Failure 'TOOL_UNREADABLE' "Could not read the current build tool identity: $($_.Exception.Message)"
        }

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
            if ($sha -notmatch '^[0-9A-Fa-f]{64}$') {
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
            if ([string]::IsNullOrWhiteSpace($name) -or $name -match '[\\/]' -or $name -eq '.' -or $name -eq '..') {
                Add-Failure 'EVIDENCE_OUTPUT_NAME_INVALID' "Invalid output name in provenance record: '$name'."
                continue
            }
            if ($sha -notmatch '^[0-9A-Fa-f]{64}$') {
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
            $expectedOutputs += [pscustomobject]@{
                Name       = $name
                Length     = [long]$length
                Sha256     = $sha.ToUpperInvariant()
                SourceKind = $sourceKind
                Project    = [string](Get-ManifestProperty $entry 'Project')
            }
        }
        $outputCount = $expectedOutputs.Count

        foreach ($required in $script:SharedPluginRequiredAssemblies) {
            if (-not $seenOutputs.Contains($required)) {
                Add-Failure 'EVIDENCE_REQUIRED_OUTPUT_MISSING' "Provenance record does not list required assembly '$required'."
            }
        }

        $recordedPackages = @(Get-ManifestProperty $manifest 'packages')
        if ($recordedPackages.Count -eq 0) {
            Add-Failure 'EVIDENCE_PACKAGES_EMPTY' 'Provenance record has no resolved package closure.'
        }
        $packageAssemblies = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($package in $recordedPackages) {
            $packageId = [string](Get-ManifestProperty $package 'Id')
            $packageVersion = [string](Get-ManifestProperty $package 'Version')
            $assembly = [string](Get-ManifestProperty $package 'Assembly')
            $packageSha = [string](Get-ManifestProperty $package 'Sha256')
            if ([string]::IsNullOrWhiteSpace($packageId) -or [string]::IsNullOrWhiteSpace($packageVersion) -or
                [string]::IsNullOrWhiteSpace($assembly) -or $packageSha -notmatch '^[0-9A-Fa-f]{64}$') {
                Add-Failure 'EVIDENCE_PACKAGE_INVALID' 'Provenance record contains an invalid package entry.'
                continue
            }
            $packageAssemblies.Add($assembly) | Out-Null
            $matchingOutput = $expectedOutputs | Where-Object { $_.Name -ieq $assembly } | Select-Object -First 1
            if (-not $matchingOutput -or $matchingOutput.Sha256 -ine $packageSha.ToUpperInvariant()) {
                Add-Failure 'EVIDENCE_PACKAGE_HASH_MISMATCH' "Package '$packageId $packageVersion' assembly '$assembly' does not match the recorded output bytes."
            }
        }
        if (-not $packageAssemblies.Contains('Google.Protobuf.dll')) {
            Add-Failure 'EVIDENCE_REQUIRED_PACKAGE_MISSING' "Provenance record does not include the required Google.Protobuf.dll package closure."
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
        # If bin/<Configuration>/netstandard2.1 still exists, the plugin bytes
        # must equal the most recent build output too. This catches "rebuilt but
        # never synced" even when the source bytes were reverted afterwards.
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
