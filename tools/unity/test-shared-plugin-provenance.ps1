<#
.SYNOPSIS
    End-to-end test matrix for the Unity shared-plugin provenance guard.

.DESCRIPTION
    Exercises the production entry points, never a private mirror:
      * tools/unity/build-shared.ps1 (real Release build + sync),
      * tools/unity/check-shared-plugins.ps1 (the read-only verifier),
      * the real Unity Editor guard via -buildWindows64Player and Play mode.

    Positive cases:
      * fresh Release build, 7/7 own assemblies match the fresh build output,
      * the real third-party runtime closure is recorded and present,
      * scratch destination is isolated from the default synced record,
      * a documentation-only file in a project directory is not an input,
      * a repository path containing spaces works,
      * after a valid sync, Unity Play mode actually enters,
      * a fresh repository copy builds and validates with relative evidence.

    Negative cases:
      * missing / empty / malformed / unsupported-schema evidence,
      * tampered recorded input hashes,
      * missing / corrupt plugin DLL,
      * changed source, added source, changed dependency and build config,
      * unsupported MSBuild Import in shared props,
      * native build failure leaves no PASS,
      * source changing during the build is rejected without a record,
      * real -buildWindows64Player is blocked with stale/missing provenance,
      * real -buildWindows64Player rejects a drifted DLL byte even when the
        provenance record itself is structurally valid,
      * real Play mode is cancelled by the guard with missing provenance.

    Fault injection only writes under this tree's ignored evidence root; every
    mutated tracked file is backed up first and restored byte-for-byte, with a
    final inventory comparison. No other receiver tree is touched.

.PARAMETER UnityExe
    Unity Editor executable. Defaults to the locally accepted 6000.3.27f1 path.

.PARAMETER EvidenceRoot
    Ignored directory for logs, XML, backups and scratch output. Defaults to a
    new GUID directory under build/.

.PARAMETER SkipUnity
    Skip the real Editor/P layer guard cases (the script reports them NOT_RUN).
    Not used for the accepted run.

.PARAMETER SkipFixtureBuild
    Skip the isolated fixture-repository build-drift cases.

.PARAMETER UnityTimeoutSeconds
    Bound for each Unity invocation. Defaults to 600 seconds.

.PARAMETER BuildTimeoutSeconds
    Bound for each build-shared invocation. Defaults to 900 seconds.
#>
[CmdletBinding()]
param(
    [string]$UnityExe = 'D:\Unity\Hub\Editor\6000.3.27f1\Editor\Unity.exe',
    [string]$EvidenceRoot,
    [switch]$SkipUnity,
    [switch]$SkipFixtureBuild,
    [int]$UnityTimeoutSeconds = 600,
    [int]$BuildTimeoutSeconds = 900
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $PSScriptRoot 'shared-plugin-provenance-lib.ps1')

$unityProject = Join-Path $repoRoot 'unity\Mahjong.Unity'
$pluginDir = Join-Path $unityProject 'Assets\Plugins\Mahjong'
$defaultEvidence = Join-Path $unityProject 'Library\SharedPluginProvenance\provenance.json'
$checker = Join-Path $PSScriptRoot 'check-shared-plugins.ps1'
$builder = Join-Path $PSScriptRoot 'build-shared.ps1'
$powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'

if (-not (Test-Path -LiteralPath $powershell -PathType Leaf)) {
    throw "Windows PowerShell 5.1 not found: $powershell"
}
if (-not (Test-Path -LiteralPath $UnityExe -PathType Leaf) -and -not $SkipUnity) {
    throw "Unity executable not found: $UnityExe"
}

if (-not $EvidenceRoot) {
    $suffix = [guid]::NewGuid().ToString('N').Substring(0, 16)
    $EvidenceRoot = Join-Path $repoRoot "build\shared-plugin-provenance-ds-20261028-$suffix"
}
$EvidenceRoot = Resolve-SharedPluginFullPath $EvidenceRoot
New-Item -ItemType Directory -Force -Path $EvidenceRoot | Out-Null
$logsDir = Join-Path $EvidenceRoot 'logs'
$backupDir = Join-Path $EvidenceRoot 'backups'
$preDir = Join-Path $EvidenceRoot 'pre'
New-Item -ItemType Directory -Force -Path $logsDir, $backupDir, $preDir | Out-Null

$script:cases = New-Object System.Collections.Generic.List[object]
$script:overallFail = $false
$script:overallStart = [DateTime]::UtcNow

function Add-TestCase {
    param(
        [string]$Id,
        [string]$Name,
        [string]$Status,
        [string]$Detail,
        [string]$LogPath,
        [double]$Seconds
    )
    $script:cases.Add([pscustomobject]@{
        Id      = $Id
        Name    = $Name
        Status  = $Status
        Detail  = $Detail
        LogPath = $LogPath
        Seconds = [math]::Round($Seconds, 3)
    }) | Out-Null
    if ($Status -eq 'FAIL') { $script:overallFail = $true }
    Write-Host ("[{0}] {1} - {2} ({3:N2}s)" -f $Status, $Id, $Name, $Seconds)
    if ($Status -eq 'FAIL') { Write-Host ("       {0}" -f $Detail) }
}

function Quote-Arg {
    param([string]$Value)
    if ($null -eq $Value) { return '""' }
    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append('"')
    $backslashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq [char]92) {
            $backslashes++
            continue
        }
        if ($character -eq [char]34) {
            [void]$builder.Append([char]92, ($backslashes * 2) + 1)
            [void]$builder.Append([char]34)
            $backslashes = 0
            continue
        }
        if ($backslashes -gt 0) {
            [void]$builder.Append([char]92, $backslashes)
            $backslashes = 0
        }
        [void]$builder.Append($character)
    }
    if ($backslashes -gt 0) {
        [void]$builder.Append([char]92, $backslashes * 2)
    }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Invoke-CapturedProcess {
    param(
        [string]$FilePath,
        [string[]]$Arguments,
        [string]$LogPath,
        [int]$TimeoutSeconds
    )

    $argumentString = (@($Arguments | ForEach-Object { Quote-Arg $_ }) -join ' ')
    $logDirectory = Split-Path -Parent $LogPath
    if ($logDirectory) { New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null }

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath
    $psi.Arguments = $argumentString
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.WorkingDirectory = $repoRoot

    $startedAt = Get-Date
    $timedOut = $false
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $psi
    if (-not $process.Start()) {
        throw "Failed to start process: $FilePath"
    }
    # Read both streams concurrently to avoid pipe-buffer deadlocks.
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $timedOut = $true
        try { $process.Kill() } catch { }
        try { [void]$process.WaitForExit(5000) } catch { }
    }
    else {
        $process.WaitForExit()
    }
    $exitCode = if ($timedOut) { 'TIMEOUT' } else { $process.ExitCode }
    $combined = $stdoutTask.Result + $stderrTask.Result
    $elapsed = ((Get-Date) - $startedAt).TotalSeconds

    $header = "COMMAND: $FilePath $argumentString`r`nNATIVE_EXIT: $exitCode`r`nELAPSED_SECONDS: $([math]::Round($elapsed, 3))`r`n`r`n"
    [System.IO.File]::WriteAllText($LogPath, $header + $combined, (New-Object System.Text.UTF8Encoding($false)))
    [System.IO.File]::WriteAllText("$LogPath.exit.txt", [string]$exitCode, (New-Object System.Text.UTF8Encoding($false)))

    return [pscustomobject]@{
        ExitCode = $exitCode
        Output   = $combined
        TimedOut = $timedOut
        Seconds  = $elapsed
        LogPath  = $LogPath
    }
}

function Invoke-Checker {
    param(
        [string]$Id,
        [string[]]$ExtraArguments = @()
    )
    $arguments = @(
        '-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
        '-File', $checker, '-ProjectRoot', $repoRoot
    ) + $ExtraArguments
    return Invoke-CapturedProcess -FilePath $powershell -Arguments $arguments -LogPath (Join-Path $logsDir "$Id.log") -TimeoutSeconds 300
}

function Invoke-Builder {
    param(
        [string]$Id,
        [string[]]$ExtraArguments = @(),
        [string]$Root = $repoRoot
    )
    $scriptPath = if ($Root -eq $repoRoot) { $builder } else { Join-Path $Root 'tools\unity\build-shared.ps1' }
    $arguments = @(
        '-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
        '-File', $scriptPath, '-Configuration', 'Release'
    ) + $ExtraArguments
    return Invoke-CapturedProcess -FilePath $powershell -Arguments $arguments -LogPath (Join-Path $logsDir "$Id.log") -TimeoutSeconds $BuildTimeoutSeconds
}

function Get-FileInventory {
    param([string[]]$Paths)
    $items = @()
    foreach ($path in $Paths) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        $item = Get-Item -LiteralPath $path
        $items += [pscustomobject]@{
            Path   = Get-SharedPluginRelativePath -Root $repoRoot -Path $item.FullName
            Length = [long]$item.Length
            Sha256 = Get-SharedPluginFileSha256 $item.FullName
        }
    }
    return @($items | Sort-Object -Property Path)
}

function Backup-File {
    param([string]$Path)
    $relative = Get-SharedPluginRelativePath -Root $repoRoot -Path $Path
    $destination = Join-Path $backupDir $relative
    $directory = Split-Path -Parent $destination
    if ($directory) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
    Copy-Item -LiteralPath $Path -Destination $destination -Force
    $hash = Get-SharedPluginFileSha256 $Path
    [System.IO.File]::WriteAllText("$destination.sha256", $hash, (New-Object System.Text.UTF8Encoding($false)))
}

function Restore-File {
    param([string]$Path)
    $relative = Get-SharedPluginRelativePath -Root $repoRoot -Path $Path
    $source = Join-Path $backupDir $relative
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Backup missing for $relative"
    }
    Copy-Item -LiteralPath $source -Destination $Path -Force
    $expected = [System.IO.File]::ReadAllText("$source.sha256").Trim()
    $actual = Get-SharedPluginFileSha256 $Path
    if ($actual -ine $expected) {
        throw "Restore hash mismatch for $relative (expected $expected, got $actual)"
    }
}

function Assert-CheckContains {
    param($Run, [string]$Code)
    return (($Run.ExitCode -ne 0) -and (-not $Run.TimedOut) -and ($Run.Output -match [regex]::Escape($Code)))
}

function Write-JUnitXml {
    param([string]$Path)
    $builderXml = New-Object System.Text.StringBuilder
    [void]$builderXml.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
    $failed = @($script:cases | Where-Object { $_.Status -eq 'FAIL' }).Count
    $elapsed = ([DateTime]::UtcNow - $script:overallStart).TotalSeconds
    [void]$builderXml.AppendLine(('<testsuite name="shared-plugin-provenance" tests="{0}" failures="{1}" skipped="0" time="{2}">' -f $script:cases.Count, $failed, [math]::Round($elapsed, 3)))
    foreach ($case in $script:cases) {
        $name = [System.Security.SecurityElement]::Escape("$($case.Id) $($case.Name)")
        [void]$builderXml.AppendLine(('  <testcase classname="shared-plugin-provenance" name="{0}" time="{1}">' -f $name, $case.Seconds))
        if ($case.Status -eq 'FAIL') {
            $message = [System.Security.SecurityElement]::Escape($case.Detail)
            [void]$builderXml.AppendLine(('    <failure message="{0}" />' -f $message))
        }
        [void]$builderXml.AppendLine('  </testcase>')
    }
    [void]$builderXml.AppendLine('</testsuite>')
    [System.IO.File]::WriteAllText($Path, $builderXml.ToString(), (New-Object System.Text.UTF8Encoding($false)))
}

# ---------------------------------------------------------------------------
# Back up every tracked file this script may mutate.
# ---------------------------------------------------------------------------
$mutableFiles = @(
    (Join-Path $repoRoot 'dotnet\src\Mahjong.Client\TableSession.cs'),
    (Join-Path $repoRoot 'dotnet\src\Mahjong.Ai\AiDecision.cs'),
    (Join-Path $repoRoot 'dotnet\src\Mahjong.Contracts.Wire\Mahjong.Contracts.Wire.csproj'),
    (Join-Path $repoRoot 'dotnet\Directory.Build.props')
)
if (Test-Path -LiteralPath $defaultEvidence -PathType Leaf) {
    $mutableFiles += $defaultEvidence
}
$pluginFiles = @(Get-ChildItem -LiteralPath $pluginDir -File -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
$mutableFiles += $pluginFiles
$mutableFiles = @($mutableFiles | Select-Object -Unique)
$inventoryFiles = @($mutableFiles | Where-Object { $_ -ne $defaultEvidence })
foreach ($file in $mutableFiles) {
    if (Test-Path -LiteralPath $file -PathType Leaf) { Backup-File $file }
}

$cleanupJunction = $null
$fixtureRepo = $null
$exitCode = 0

try {
    # -----------------------------------------------------------------------
    # P01 - real fresh Release build and sync
    # -----------------------------------------------------------------------
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $build = Invoke-Builder -Id 'P01-build-shared'
    $watch.Stop()
    $buildOk = ($build.ExitCode -eq 0) -and (Test-Path -LiteralPath $defaultEvidence -PathType Leaf)
    $detail = "exit=$($build.ExitCode); evidence=$(Test-Path -LiteralPath $defaultEvidence -PathType Leaf)"
    if ($buildOk) {
        $manifest = Get-Content -LiteralPath $defaultEvidence -Raw | ConvertFrom-Json
        $buildOk = ($manifest.outputs.Count -eq 19) -and ($manifest.inputs.Count -gt 100) -and ($manifest.packages.Count -ge 1)
        $detail += "; inputs=$($manifest.inputs.Count); outputs=$($manifest.outputs.Count); packages=$($manifest.packages.Count)"
    }
    Add-TestCase -Id 'P01' -Name 'fresh Release build-shared + atomic provenance record' -Status $(if ($buildOk) { 'PASS' } else { 'FAIL' }) -Detail $detail -LogPath $build.LogPath -Seconds $watch.Elapsed.TotalSeconds

    # The post-build bytes are the baseline all later mutations restore to.
    $pluginFiles = @(Get-ChildItem -LiteralPath $pluginDir -File -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
    foreach ($file in $pluginFiles) {
        Backup-File $file
    }
    Backup-File $defaultEvidence
    $mutableFiles = @($mutableFiles | Select-Object -Unique)
    $inventoryFiles = @($mutableFiles | Where-Object { $_ -ne $defaultEvidence })
    $preInventory = @(Get-FileInventory -Paths $inventoryFiles)
    $preInventory | Export-Csv -NoTypeInformation -Path (Join-Path $preDir 'mutable-inventory.csv')
    $postBuildInventory = @(Get-FileInventory -Paths (@($pluginFiles) + @($defaultEvidence)))
    $postBuildInventory | Export-Csv -NoTypeInformation -Path (Join-Path $preDir 'post-build-inventory.csv')

    # -----------------------------------------------------------------------
    # P02 - production checker PASS + 7/7 own assemblies equal fresh build
    # -----------------------------------------------------------------------
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $check = Invoke-Checker -Id 'P02-check-pass' -ExtraArguments @('-Json')
    $watch.Stop()
    $checkPass = ($check.ExitCode -eq 0)
    $parsed = $null
    if ($check.Output.Trim().StartsWith('{')) {
        try { $parsed = $check.Output | ConvertFrom-Json } catch { $parsed = $null }
    }
    $checkPass = $checkPass -and ($null -ne $parsed) -and ($parsed.Ok -eq $true) -and ($parsed.InputCount -gt 100) -and ($parsed.OutputCount -eq 19)
    $ownMatches = 0
    $ownDetails = @()
    foreach ($project in $script:SharedPluginProjects) {
        $pluginDll = Join-Path $pluginDir "$project.dll"
        $freshDll = Join-Path $repoRoot "dotnet\src\$project\bin\Release\netstandard2.1\$project.dll"
        if ((Test-Path $pluginDll) -and (Test-Path $freshDll)) {
            $pluginHash = Get-SharedPluginFileSha256 $pluginDll
            $freshHash = Get-SharedPluginFileSha256 $freshDll
            if ($pluginHash -eq $freshHash) { $ownMatches++ }
            $ownDetails += "$project=$($pluginHash.Substring(0,12))/$($freshHash.Substring(0,12))"
        }
    }
    $checkPass = $checkPass -and ($ownMatches -eq 7)
    Add-TestCase -Id 'P02' -Name 'checker PASS, structure valid, 7/7 own assemblies equal fresh output' -Status $(if ($checkPass) { 'PASS' } else { 'FAIL' }) -Detail "exit=$($check.ExitCode); ownMatches=$ownMatches/7; $($ownDetails -join '; ')" -LogPath $check.LogPath -Seconds $watch.Elapsed.TotalSeconds

    # -----------------------------------------------------------------------
    # P03 - missing evidence
    # -----------------------------------------------------------------------
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    Move-Item -LiteralPath $defaultEvidence -Destination "$defaultEvidence.hidden" -Force
    try {
        $run = Invoke-Checker -Id 'P03-missing-evidence'
        $ok = Assert-CheckContains $run 'EVIDENCE_MISSING'
    }
    finally {
        Move-Item -LiteralPath "$defaultEvidence.hidden" -Destination $defaultEvidence -Force
    }
    $watch.Stop()
    Add-TestCase -Id 'P03' -Name 'missing evidence fails closed (EVIDENCE_MISSING)' -Status $(if ($ok) { 'PASS' } else { 'FAIL' }) -Detail "exit=$($run.ExitCode); output=$($run.Output.Trim())" -LogPath $run.LogPath -Seconds $watch.Elapsed.TotalSeconds

    # -----------------------------------------------------------------------
    # P04 - empty and malformed evidence
    # -----------------------------------------------------------------------
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($defaultEvidence, '', $utf8)
    $empty = Invoke-Checker -Id 'P04a-empty-evidence'
    $emptyOk = Assert-CheckContains $empty 'EVIDENCE_EMPTY'
    [System.IO.File]::WriteAllText($defaultEvidence, '{ not valid json', $utf8)
    $malformed = Invoke-Checker -Id 'P04b-malformed-evidence'
    $malformedOk = Assert-CheckContains $malformed 'EVIDENCE_MALFORMED'
    Restore-File $defaultEvidence
    $watch.Stop()
    Add-TestCase -Id 'P04' -Name 'empty and malformed evidence fail closed' -Status $(if ($emptyOk -and $malformedOk) { 'PASS' } else { 'FAIL' }) -Detail "empty_exit=$($empty.ExitCode); malformed_exit=$($malformed.ExitCode); log=$($malformed.LogPath)" -LogPath $malformed.LogPath -Seconds $watch.Elapsed.TotalSeconds

    # -----------------------------------------------------------------------
    # P05 - unsupported schema / tampered record
    # -----------------------------------------------------------------------
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $record = Get-Content -LiteralPath $defaultEvidence -Raw | ConvertFrom-Json
    $record.schemaVersion = 99
    [System.IO.File]::WriteAllText($defaultEvidence, ($record | ConvertTo-Json -Depth 12), $utf8)
    $schemaRun = Invoke-Checker -Id 'P05a-schema-unsupported'
    $schemaOk = Assert-CheckContains $schemaRun 'EVIDENCE_SCHEMA_UNSUPPORTED'
    $record = Get-Content -LiteralPath (Join-Path $backupDir (Get-SharedPluginRelativePath -Root $repoRoot -Path $defaultEvidence)) -Raw | ConvertFrom-Json
    $record.pluginDir = 'D:\somewhere-else'
    [System.IO.File]::WriteAllText($defaultEvidence, ($record | ConvertTo-Json -Depth 12), $utf8)
    $targetRun = Invoke-Checker -Id 'P05b-target-mismatch'
    $targetOk = Assert-CheckContains $targetRun 'EVIDENCE_TARGET_MISMATCH'
    Restore-File $defaultEvidence
    $watch.Stop()
    Add-TestCase -Id 'P05' -Name 'unsupported schema and wrong target fail closed' -Status $(if ($schemaOk -and $targetOk) { 'PASS' } else { 'FAIL' }) -Detail "schema_exit=$($schemaRun.ExitCode); target_exit=$($targetRun.ExitCode)" -LogPath $targetRun.LogPath -Seconds $watch.Elapsed.TotalSeconds

    # -----------------------------------------------------------------------
    # P06 - missing DLL
    # -----------------------------------------------------------------------
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $victim = Join-Path $pluginDir 'Mahjong.Client.dll'
    $victimHashBefore = Get-SharedPluginFileSha256 $victim
    Move-Item -LiteralPath $victim -Destination "$victim.hidden" -Force
    try {
        $run = Invoke-Checker -Id 'P06-missing-dll'
        $ok = Assert-CheckContains $run 'PLUGIN_MISSING'
    }
    finally {
        Move-Item -LiteralPath "$victim.hidden" -Destination $victim -Force
    }
    $victimHashAfter = Get-SharedPluginFileSha256 $victim
    $ok = $ok -and ($victimHashBefore -eq $victimHashAfter)
    $watch.Stop()
    Add-TestCase -Id 'P06' -Name 'missing own DLL fails closed and is restored exactly' -Status $(if ($ok) { 'PASS' } else { 'FAIL' }) -Detail "exit=$($run.ExitCode); restored=$($victimHashBefore -eq $victimHashAfter)" -LogPath $run.LogPath -Seconds $watch.Elapsed.TotalSeconds

    # -----------------------------------------------------------------------
    # P07 - corrupt DLL (same length, one byte flipped)
    # -----------------------------------------------------------------------
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $bytes = [System.IO.File]::ReadAllBytes($victim)
    $bytes[200] = [byte](($bytes[200] + 1) % 256)
    [System.IO.File]::WriteAllBytes($victim, $bytes)
    $run = Invoke-Checker -Id 'P07-corrupt-dll'
    $ok = Assert-CheckContains $run 'PLUGIN_CORRUPT'
    Restore-File $victim
    $ok = $ok -and ((Get-SharedPluginFileSha256 $victim) -eq $victimHashBefore)
    $watch.Stop()
    Add-TestCase -Id 'P07' -Name 'corrupt DLL fails closed and is restored exactly' -Status $(if ($ok) { 'PASS' } else { 'FAIL' }) -Detail "exit=$($run.ExitCode); restored=$((Get-SharedPluginFileSha256 $victim) -eq $victimHashBefore)" -LogPath $run.LogPath -Seconds $watch.Elapsed.TotalSeconds

    # -----------------------------------------------------------------------
    # P08 - stale/modified source
    # -----------------------------------------------------------------------
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $sourceFile = Join-Path $repoRoot 'dotnet\src\Mahjong.Client\TableSession.cs'
    Add-Content -LiteralPath $sourceFile -Value '// provenance-stale-source-injection' -Encoding UTF8
    $run = Invoke-Checker -Id 'P08-source-changed'
    $ok = Assert-CheckContains $run 'SOURCE_CHANGED'
    Restore-File $sourceFile
    $sourceRestored = (Get-SharedPluginFileSha256 $sourceFile) -eq ([System.IO.File]::ReadAllText((Join-Path $backupDir 'dotnet\src\Mahjong.Client\TableSession.cs.sha256')).Trim())
    $ok = $ok -and $sourceRestored
    $watch.Stop()
    Add-TestCase -Id 'P08' -Name 'changed source fails closed' -Status $(if ($ok) { 'PASS' } else { 'FAIL' }) -Detail "exit=$($run.ExitCode); restored=$sourceRestored" -LogPath $run.LogPath -Seconds $watch.Elapsed.TotalSeconds

    # -----------------------------------------------------------------------
    # P09 - newly added compile input
    # -----------------------------------------------------------------------
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $newSource = Join-Path $repoRoot 'dotnet\src\Mahjong.Ai\ProvenanceInjectedCompileInput.cs'
    [System.IO.File]::WriteAllText($newSource, "namespace Mahjong.Ai { internal static class ProvenanceInjectedCompileInput { } }`r`n", $utf8)
    try {
        $run = Invoke-Checker -Id 'P09-source-added'
        $ok = Assert-CheckContains $run 'SOURCE_ADDED'
    }
    finally {
        if (Test-Path -LiteralPath $newSource) { Remove-Item -LiteralPath $newSource -Force }
    }
    $watch.Stop()
    Add-TestCase -Id 'P09' -Name 'new MSBuild .cs input fails closed' -Status $(if ($ok) { 'PASS' } else { 'FAIL' }) -Detail "exit=$($run.ExitCode); removed=$(-not (Test-Path -LiteralPath $newSource))" -LogPath $run.LogPath -Seconds $watch.Elapsed.TotalSeconds

    # -----------------------------------------------------------------------
    # P10 - dependency change
    # -----------------------------------------------------------------------
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $csproj = Join-Path $repoRoot 'dotnet\src\Mahjong.Contracts.Wire\Mahjong.Contracts.Wire.csproj'
    $csprojText = [System.IO.File]::ReadAllText($csproj)
    [System.IO.File]::WriteAllText($csproj, $csprojText.Replace('Version="3.28.3"', 'Version="3.28.2"'), $utf8)
    try {
        $run = Invoke-Checker -Id 'P10-dependency-changed'
        $ok = (($run.ExitCode -ne 0) -and (($run.Output -match 'BUILD_CONFIG_CHANGED') -or ($run.Output -match 'DEPENDENCY_CHANGED')))
    }
    finally {
        Restore-File $csproj
    }
    $watch.Stop()
    Add-TestCase -Id 'P10' -Name 'dependency version change fails closed' -Status $(if ($ok) { 'PASS' } else { 'FAIL' }) -Detail "exit=$($run.ExitCode)" -LogPath $run.LogPath -Seconds $watch.Elapsed.TotalSeconds

    # -----------------------------------------------------------------------
    # P11 - shared build configuration change
    # -----------------------------------------------------------------------
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $props = Join-Path $repoRoot 'dotnet\Directory.Build.props'
    Add-Content -LiteralPath $props -Value '<!-- provenance-build-config-injection -->' -Encoding UTF8
    $run = Invoke-Checker -Id 'P11-build-config-changed'
    $ok = Assert-CheckContains $run 'BUILD_CONFIG_CHANGED'
    Restore-File $props
    $watch.Stop()
    Add-TestCase -Id 'P11' -Name 'shared build config change fails closed' -Status $(if ($ok) { 'PASS' } else { 'FAIL' }) -Detail "exit=$($run.ExitCode)" -LogPath $run.LogPath -Seconds $watch.Elapsed.TotalSeconds

    # -----------------------------------------------------------------------
    # P21 - an unsupported MSBuild Import added to shared configuration is
    # rejected instead of silently escaping the recorded input boundary.
    # -----------------------------------------------------------------------
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $props = Join-Path $repoRoot 'dotnet\Directory.Build.props'
    $propsText = [System.IO.File]::ReadAllText($props)
    $injected = $propsText.Replace('</Project>', '  <Import Project="provenance-external.props" />' + "`r`n" + '</Project>')
    [System.IO.File]::WriteAllText($props, $injected, $utf8)
    try {
        $run = Invoke-Checker -Id 'P21-unsupported-import'
        $ok = (($run.ExitCode -ne 0) -and ($run.Output -match 'UNSUPPORTED_INPUT_BOUNDARY'))
    }
    finally {
        Restore-File $props
    }
    $watch.Stop()
    Add-TestCase -Id 'P21' -Name 'unsupported MSBuild Import in shared props fails closed' -Status $(if ($ok) { 'PASS' } else { 'FAIL' }) -Detail "exit=$($run.ExitCode)" -LogPath $run.LogPath -Seconds $watch.Elapsed.TotalSeconds

    # -----------------------------------------------------------------------
    # P12 - scratch destination cannot update the default synced state
    # -----------------------------------------------------------------------
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $defaultEvidenceBefore = Get-SharedPluginFileSha256 $defaultEvidence
    $defaultPluginBefore = @(Get-ChildItem -LiteralPath $pluginDir -File | Sort-Object Name | ForEach-Object { "$($_.Name)=$(Get-SharedPluginFileSha256 $_.FullName)" })
    $scratch = Join-Path $EvidenceRoot 'scratch-plugins'
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
    $scratchBuild = Invoke-Builder -Id 'P12-scratch-build' -ExtraArguments @('-PluginOutputDir', $scratch)
    $scratchEvidence = Join-Path $scratch 'shared-plugin-provenance.json'
    $scratchCheck = Invoke-Checker -Id 'P12-scratch-check' -ExtraArguments @('-PluginDir', $scratch, '-EvidencePath', $scratchEvidence)
    $copyMismatch = Invoke-Checker -Id 'P12-scratch-evidence-on-default' -ExtraArguments @('-EvidencePath', $scratchEvidence)
    $defaultEvidenceAfter = Get-SharedPluginFileSha256 $defaultEvidence
    $defaultPluginAfter = @(Get-ChildItem -LiteralPath $pluginDir -File | Sort-Object Name | ForEach-Object { "$($_.Name)=$(Get-SharedPluginFileSha256 $_.FullName)" })
    $defaultUnchanged = ($defaultEvidenceBefore -eq $defaultEvidenceAfter) -and (([string]::Join('|', $defaultPluginBefore)) -eq ([string]::Join('|', $defaultPluginAfter)))
    $ok = ($scratchBuild.ExitCode -eq 0) -and ($scratchCheck.ExitCode -eq 0) -and (Test-Path -LiteralPath $scratchEvidence) -and
          ($copyMismatch.ExitCode -ne 0) -and ($copyMismatch.Output -match 'EVIDENCE_TARGET_MISMATCH') -and $defaultUnchanged
    $watch.Stop()
    Add-TestCase -Id 'P12' -Name 'scratch copy has separate evidence and never updates default sync state' -Status $(if ($ok) { 'PASS' } else { 'FAIL' }) -Detail "build=$($scratchBuild.ExitCode); scratchCheck=$($scratchCheck.ExitCode); copiedEvidenceRejected=$($copyMismatch.ExitCode); defaultUnchanged=$defaultUnchanged" -LogPath $scratchBuild.LogPath -Seconds $watch.Elapsed.TotalSeconds

    # -----------------------------------------------------------------------
    # P13 - repository path containing spaces
    # -----------------------------------------------------------------------
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $junction = Join-Path $EvidenceRoot 'repo with spaces'
    if (Test-Path -LiteralPath $junction) { Remove-Item -LiteralPath $junction -Force }
    $mklinkCommand = 'mklink /J "' + $junction + '" "' + $repoRoot + '"'
    $null = & "$env:SystemRoot\System32\cmd.exe" /c $mklinkCommand 2>&1
    if (-not (Test-Path -LiteralPath $junction -PathType Container)) {
        $ok = $false
        $run = [pscustomobject]@{ ExitCode = 'JUNCTION_FAILED'; Output = 'mklink /J failed'; LogPath = $null }
    }
    else {
        $cleanupJunction = $junction
        $run = Invoke-CapturedProcess -FilePath $powershell -Arguments @(
            '-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
            '-File', (Join-Path $junction 'tools\unity\check-shared-plugins.ps1'),
            '-ProjectRoot', $junction,
            '-PluginDir', $pluginDir,
            '-EvidencePath', $defaultEvidence,
            '-Json'
        ) -LogPath (Join-Path $logsDir 'P13-path-with-spaces.log') -TimeoutSeconds 300
        $ok = ($run.ExitCode -eq 0) -and ($run.Output -match '"Ok":\s*true')
    }
    $watch.Stop()
    Add-TestCase -Id 'P13' -Name 'checker works through a repository path containing spaces' -Status $(if ($ok) { 'PASS' } else { 'FAIL' }) -Detail "exit=$($run.ExitCode)" -LogPath $run.LogPath -Seconds $watch.Elapsed.TotalSeconds

    # -----------------------------------------------------------------------
    # P19 - documentation-only change inside a project directory is not an input
    # -----------------------------------------------------------------------
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $docFile = Join-Path $repoRoot 'dotnet\src\Mahjong.Client\ProvenanceDocOnly.md'
    [System.IO.File]::WriteAllText($docFile, "# documentation only: must not invalidate the build inputs`r`n", $utf8)
    try {
        $run = Invoke-Checker -Id 'P19-doc-only'
        $ok = ($run.ExitCode -eq 0) -and ($run.Output -match 'SHARED_PLUGIN_PROVENANCE: PASS')
    }
    finally {
        if (Test-Path -LiteralPath $docFile) { Remove-Item -LiteralPath $docFile -Force }
    }
    $watch.Stop()
    Add-TestCase -Id 'P19' -Name 'documentation-only file in a project dir does not invalidate provenance' -Status $(if ($ok) { 'PASS' } else { 'FAIL' }) -Detail "exit=$($run.ExitCode)" -LogPath $run.LogPath -Seconds $watch.Elapsed.TotalSeconds

    # -----------------------------------------------------------------------
    # P20 - tampered recorded input hash fails closed (the checker really uses
    # the recorded hashes instead of trusting the record's structure alone)
    # -----------------------------------------------------------------------
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $record = Get-Content -LiteralPath $defaultEvidence -Raw | ConvertFrom-Json
    $tamperedInput = @($record.inputs | Where-Object { ([string]$_.Path).EndsWith('.cs') } | Select-Object -First 1)
    if ($tamperedInput.Count -eq 1) {
        $tamperedInput[0].Sha256 = ('0' * 64)
        [System.IO.File]::WriteAllText($defaultEvidence, ($record | ConvertTo-Json -Depth 12), $utf8)
        $run = Invoke-Checker -Id 'P20-tampered-input-hash'
        $ok = (($run.ExitCode -ne 0) -and (($run.Output -match 'SOURCE_CHANGED') -or ($run.Output -match 'INPUT_CHANGED')))
    }
    else {
        $ok = $false
        $run = [pscustomobject]@{ ExitCode = 'NO_CS_INPUT'; Output = ''; LogPath = $null }
    }
    Restore-File $defaultEvidence
    $watch.Stop()
    Add-TestCase -Id 'P20' -Name 'tampered recorded input hash fails closed' -Status $(if ($ok) { 'PASS' } else { 'FAIL' }) -Detail "exit=$($run.ExitCode)" -LogPath $run.LogPath -Seconds $watch.Elapsed.TotalSeconds

    # -----------------------------------------------------------------------
    # P14 - isolated fixture: build failure leaves no PASS, mid-build drift rejected
    # -----------------------------------------------------------------------
    if (-not $SkipFixtureBuild) {
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        $fixtureRepo = Join-Path $EvidenceRoot 'fixture-repo'
        if (Test-Path -LiteralPath $fixtureRepo) { Remove-Item -LiteralPath $fixtureRepo -Recurse -Force }
        New-Item -ItemType Directory -Force -Path (Join-Path $fixtureRepo 'tools\unity'), (Join-Path $fixtureRepo 'unity\Mahjong.Unity\Assets\Plugins\Mahjong') | Out-Null
        foreach ($file in @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'dotnet') -Recurse -File)) {
            $relative = $file.FullName.Substring((Join-Path $repoRoot 'dotnet').Length).TrimStart('\')
            if ($relative -match '(^|\\)(bin|obj)(\\|$)') { continue }
            $destination = Join-Path (Join-Path $fixtureRepo 'dotnet') $relative
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
        }
        foreach ($file in @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'tests\v5-golden') -Recurse -File)) {
            $relative = $file.FullName.Substring((Join-Path $repoRoot 'tests\v5-golden').Length).TrimStart('\')
            $destination = Join-Path (Join-Path $fixtureRepo 'tests\v5-golden') $relative
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
        }
        Copy-Item -LiteralPath (Join-Path $repoRoot 'global.json') -Destination $fixtureRepo -Force
        Copy-Item -LiteralPath $builder -Destination (Join-Path $fixtureRepo 'tools\unity') -Force
        Copy-Item -LiteralPath $checker -Destination (Join-Path $fixtureRepo 'tools\unity') -Force
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'shared-plugin-provenance-lib.ps1') -Destination (Join-Path $fixtureRepo 'tools\unity') -Force

        $fixtureBuild = Invoke-CapturedProcess -FilePath $powershell -Arguments @(
            '-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
            '-File', (Join-Path $fixtureRepo 'tools\unity\build-shared.ps1'),
            '-Configuration', 'Release', '-DotnetPath', 'D:\Unity\dotnet\dotnet.exe'
        ) -LogPath (Join-Path $logsDir 'P14a-fixture-build.log') -TimeoutSeconds $BuildTimeoutSeconds
        $fixtureEvidence = Join-Path $fixtureRepo 'unity\Mahjong.Unity\Library\SharedPluginProvenance\provenance.json'
        $fixtureCheck = Invoke-CapturedProcess -FilePath $powershell -Arguments @(
            '-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
            '-File', (Join-Path $fixtureRepo 'tools\unity\check-shared-plugins.ps1'),
            '-ProjectRoot', $fixtureRepo
        ) -LogPath (Join-Path $logsDir 'P14b-fixture-check.log') -TimeoutSeconds 300
        $fixturePositive = ($fixtureBuild.ExitCode -eq 0) -and ($fixtureCheck.ExitCode -eq 0) -and (Test-Path -LiteralPath $fixtureEvidence)

        # Mid-build drift: the wrapper changes a real compile input, then builds.
        $driftWrapper = Join-Path $EvidenceRoot 'fake-dotnet-drift.cmd'
        $driftSource = Join-Path $fixtureRepo 'dotnet\src\Mahjong.Ai\AiDecision.cs'
        $wrapperText = "@echo off`r`n>>`"$driftSource`" echo // provenance-drift-injected`r`n`"D:\Unity\dotnet\dotnet.exe`" %*`r`nexit /b %ERRORLEVEL%`r`n"
        [System.IO.File]::WriteAllText($driftWrapper, $wrapperText, (New-Object System.Text.UTF8Encoding($false)))
        $driftBuild = Invoke-CapturedProcess -FilePath $powershell -Arguments @(
            '-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
            '-File', (Join-Path $fixtureRepo 'tools\unity\build-shared.ps1'),
            '-Configuration', 'Release', '-DotnetPath', $driftWrapper
        ) -LogPath (Join-Path $logsDir 'P14c-fixture-drift-build.log') -TimeoutSeconds $BuildTimeoutSeconds
        $driftRejected = ($driftBuild.ExitCode -ne 0) -and ($driftBuild.Output -match 'changed while the build was running')
        $driftNoEvidence = -not (Test-Path -LiteralPath $fixtureEvidence)
        $driftCheck = Invoke-CapturedProcess -FilePath $powershell -Arguments @(
            '-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
            '-File', (Join-Path $fixtureRepo 'tools\unity\check-shared-plugins.ps1'),
            '-ProjectRoot', $fixtureRepo
        ) -LogPath (Join-Path $logsDir 'P14d-fixture-drift-check.log') -TimeoutSeconds 300
        $driftCheckFails = ($driftCheck.ExitCode -ne 0) -and ($driftCheck.Output -match 'EVIDENCE_MISSING')

        # Native failure: a dotnet that fails before producing anything.
        $failWrapper = Join-Path $EvidenceRoot 'fake-dotnet-fail.cmd'
        [System.IO.File]::WriteAllText($failWrapper, "@echo off`r`nexit /b 42`r`n", (New-Object System.Text.UTF8Encoding($false)))
        $failBuild = Invoke-CapturedProcess -FilePath $powershell -Arguments @(
            '-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
            '-File', (Join-Path $fixtureRepo 'tools\unity\build-shared.ps1'),
            '-Configuration', 'Release', '-DotnetPath', $failWrapper
        ) -LogPath (Join-Path $logsDir 'P14e-fixture-native-fail.log') -TimeoutSeconds 300
        $failNoEvidence = -not (Test-Path -LiteralPath $fixtureEvidence)

        $watch.Stop()
        $ok = $fixturePositive -and $driftRejected -and $driftNoEvidence -and $driftCheckFails -and ($failBuild.ExitCode -ne 0) -and $failNoEvidence
        Add-TestCase -Id 'P14' -Name 'fixture build failure leaves no PASS; mid-build input drift is rejected' -Status $(if ($ok) { 'PASS' } else { 'FAIL' }) -Detail "fixtureBuild=$($fixtureBuild.ExitCode); fixtureCheck=$($fixtureCheck.ExitCode); driftExit=$($driftBuild.ExitCode); driftRejected=$driftRejected; driftNoEvidence=$driftNoEvidence; driftCheck=$($driftCheck.ExitCode); failExit=$($failBuild.ExitCode); failNoEvidence=$failNoEvidence" -LogPath $driftBuild.LogPath -Seconds $watch.Elapsed.TotalSeconds
    }
    else {
        Add-TestCase -Id 'P14' -Name 'fixture build failure / mid-build drift' -Status 'NOT_RUN' -Detail 'skipped by -SkipFixtureBuild' -LogPath $null -Seconds 0
    }

    # -----------------------------------------------------------------------
    # P15 - real Unity BuildPlayer blocked when provenance is missing
    # -----------------------------------------------------------------------
    if (-not $SkipUnity) {
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        $blockedDir = Join-Path $EvidenceRoot 'blocked-player'
        if (Test-Path -LiteralPath $blockedDir) { Remove-Item -LiteralPath $blockedDir -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $blockedDir | Out-Null
        $blockedExe = Join-Path $blockedDir 'Mahjong.exe'
        Move-Item -LiteralPath $defaultEvidence -Destination "$defaultEvidence.hidden" -Force
        try {
            $run = Invoke-CapturedProcess -FilePath $UnityExe -Arguments @(
                '-batchmode', '-quit', '-projectPath', $unityProject,
                '-buildWindows64Player', $blockedExe,
                '-logFile', (Join-Path $logsDir 'P15-unity-build-blocked.log')
            ) -LogPath (Join-Path $logsDir 'P15-unity-build-blocked-wrapper.log') -TimeoutSeconds $UnityTimeoutSeconds
        }
        finally {
            Move-Item -LiteralPath "$defaultEvidence.hidden" -Destination $defaultEvidence -Force
        }
        $logText = if (Test-Path (Join-Path $logsDir 'P15-unity-build-blocked.log')) { Get-Content (Join-Path $logsDir 'P15-unity-build-blocked.log') -Raw } else { '' }
        # The block must come from the production checker rejecting the missing
        # provenance record, not from a wrong repository root / missing checker.
        $checkerRan = ($logText -match 'EVIDENCE_MISSING') -and
                      ($logText -notmatch 'provenance checker not found') -and
                      ($logText -notmatch 'could not locate tools\\unity\\check-shared-plugins\.ps1')
        $ok = ($run.ExitCode -ne 0) -and ($run.ExitCode -ne 'TIMEOUT') -and
              ($logText -match 'SHARED_PLUGIN_PROVENANCE_BUILD_BLOCKED') -and
              $checkerRan -and
              (-not (Test-Path -LiteralPath $blockedExe))
        $watch.Stop()
        Add-TestCase -Id 'P15' -Name 'real Unity -buildWindows64Player is blocked without provenance' -Status $(if ($ok) { 'PASS' } else { 'FAIL' }) -Detail "unityExit=$($run.ExitCode); marker=$([bool]($logText -match 'SHARED_PLUGIN_PROVENANCE_BUILD_BLOCKED')); checkerRan=$checkerRan; exeExists=$(Test-Path -LiteralPath $blockedExe)" -LogPath (Join-Path $logsDir 'P15-unity-build-blocked.log') -Seconds $watch.Elapsed.TotalSeconds

        # -------------------------------------------------------------------
        # P15b - real BuildPlayer rejected because the synced DLL bytes drifted,
        # even though a structurally valid provenance record is present.
        # -------------------------------------------------------------------
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        $corruptTarget = Join-Path $pluginDir 'Mahjong.Ai.dll'
        $corruptHash = Get-SharedPluginFileSha256 $corruptTarget
        $bytes = [System.IO.File]::ReadAllBytes($corruptTarget)
        $bytes[200] = [byte](($bytes[200] + 1) % 256)
        [System.IO.File]::WriteAllBytes($corruptTarget, $bytes)
        $blockedDir2 = Join-Path $EvidenceRoot 'blocked-player-corrupt'
        New-Item -ItemType Directory -Force -Path $blockedDir2 | Out-Null
        $blockedExe2 = Join-Path $blockedDir2 'Mahjong.exe'
        try {
            $run2 = Invoke-CapturedProcess -FilePath $UnityExe -Arguments @(
                '-batchmode', '-quit', '-projectPath', $unityProject,
                '-buildWindows64Player', $blockedExe2,
                '-logFile', (Join-Path $logsDir 'P15b-unity-build-corrupt.log')
            ) -LogPath (Join-Path $logsDir 'P15b-unity-build-corrupt-wrapper.log') -TimeoutSeconds $UnityTimeoutSeconds
        }
        finally {
            Restore-File $corruptTarget
        }
        $restored = ((Get-SharedPluginFileSha256 $corruptTarget) -eq $corruptHash)
        $logText2 = if (Test-Path (Join-Path $logsDir 'P15b-unity-build-corrupt.log')) { Get-Content (Join-Path $logsDir 'P15b-unity-build-corrupt.log') -Raw } else { '' }
        $ok = ($run2.ExitCode -ne 0) -and ($run2.ExitCode -ne 'TIMEOUT') -and
              ($logText2 -match 'SHARED_PLUGIN_PROVENANCE_BUILD_BLOCKED') -and
              ($logText2 -match 'PLUGIN_CORRUPT') -and
              (-not (Test-Path -LiteralPath $blockedExe2)) -and $restored
        $watch.Stop()
        Add-TestCase -Id 'P15b' -Name 'real BuildPlayer rejects a drifted DLL with valid evidence (PLUGIN_CORRUPT)' -Status $(if ($ok) { 'PASS' } else { 'FAIL' }) -Detail "unityExit=$($run2.ExitCode); marker=$([bool]($logText2 -match 'SHARED_PLUGIN_PROVENANCE_BUILD_BLOCKED')); corrupt=$([bool]($logText2 -match 'PLUGIN_CORRUPT')); restored=$restored; exeExists=$(Test-Path -LiteralPath $blockedExe2)" -LogPath (Join-Path $logsDir 'P15b-unity-build-corrupt.log') -Seconds $watch.Elapsed.TotalSeconds

        # -------------------------------------------------------------------
        # P16 - real Editor Play blocked, then P17 allowed after a valid sync
        # -------------------------------------------------------------------
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        $probeJson = Join-Path $EvidenceRoot 'play-blocked.json'
        Move-Item -LiteralPath $defaultEvidence -Destination "$defaultEvidence.hidden" -Force
        try {
            $run = Invoke-CapturedProcess -FilePath $UnityExe -Arguments @(
                '-projectPath', $unityProject,
                '-executeMethod', 'Mahjong.Unity.Editor.SharedPluginProvenancePlayProbe.Run',
                '-provenancePlayProbeResult', $probeJson,
                '-logFile', (Join-Path $logsDir 'P16-unity-play-blocked.log')
            ) -LogPath (Join-Path $logsDir 'P16-unity-play-blocked-wrapper.log') -TimeoutSeconds ([math]::Min($UnityTimeoutSeconds, 300))
        }
        finally {
            Move-Item -LiteralPath "$defaultEvidence.hidden" -Destination $defaultEvidence -Force
        }
        $logText = if (Test-Path (Join-Path $logsDir 'P16-unity-play-blocked.log')) { Get-Content (Join-Path $logsDir 'P16-unity-play-blocked.log') -Raw } else { '' }
        $probe = if (Test-Path -LiteralPath $probeJson) { Get-Content -LiteralPath $probeJson -Raw | ConvertFrom-Json } else { $null }
        $ok = ($run.ExitCode -eq 42) -and ($null -ne $probe) -and ($probe.blocked -eq $true) -and ($probe.enteredPlay -eq $false) -and
              ($logText -match 'SHARED_PLUGIN_PROVENANCE_PLAY_BLOCKED')
        $watch.Stop()
        Add-TestCase -Id 'P16' -Name 'real Editor Play is cancelled with missing provenance' -Status $(if ($ok) { 'PASS' } else { 'FAIL' }) -Detail "unityExit=$($run.ExitCode); probe=$($probeJson); blocked=$($probe.blocked); entered=$($probe.enteredPlay)" -LogPath (Join-Path $logsDir 'P16-unity-play-blocked.log') -Seconds $watch.Elapsed.TotalSeconds

        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        $probeJsonGood = Join-Path $EvidenceRoot 'play-allowed.json'
        $run = Invoke-CapturedProcess -FilePath $UnityExe -Arguments @(
            '-projectPath', $unityProject,
            '-executeMethod', 'Mahjong.Unity.Editor.SharedPluginProvenancePlayProbe.Run',
            '-provenancePlayProbeResult', $probeJsonGood,
            '-logFile', (Join-Path $logsDir 'P17-unity-play-allowed.log')
        ) -LogPath (Join-Path $logsDir 'P17-unity-play-allowed-wrapper.log') -TimeoutSeconds ([math]::Min($UnityTimeoutSeconds, 300))
        $logText = if (Test-Path (Join-Path $logsDir 'P17-unity-play-allowed.log')) { Get-Content (Join-Path $logsDir 'P17-unity-play-allowed.log') -Raw } else { '' }
        $probe = if (Test-Path -LiteralPath $probeJsonGood) { Get-Content -LiteralPath $probeJsonGood -Raw | ConvertFrom-Json } else { $null }
        $ok = ($run.ExitCode -eq 0) -and ($null -ne $probe) -and ($probe.enteredPlay -eq $true) -and ($probe.blocked -eq $false) -and
              ($logText -match 'PLAY_PROBE_ENTERED_PLAY')
        $watch.Stop()
        Add-TestCase -Id 'P17' -Name 'real Editor Play enters after a valid sync' -Status $(if ($ok) { 'PASS' } else { 'FAIL' }) -Detail "unityExit=$($run.ExitCode); entered=$($probe.enteredPlay); blocked=$($probe.blocked)" -LogPath (Join-Path $logsDir 'P17-unity-play-allowed.log') -Seconds $watch.Elapsed.TotalSeconds
    }
    else {
        Add-TestCase -Id 'P15' -Name 'real Unity BuildPlayer blocked' -Status 'NOT_RUN' -Detail 'skipped by -SkipUnity' -LogPath $null -Seconds 0
        Add-TestCase -Id 'P16' -Name 'real Editor Play blocked' -Status 'NOT_RUN' -Detail 'skipped by -SkipUnity' -LogPath $null -Seconds 0
        Add-TestCase -Id 'P17' -Name 'real Editor Play allowed' -Status 'NOT_RUN' -Detail 'skipped by -SkipUnity' -LogPath $null -Seconds 0
    }
}
catch {
    Add-TestCase -Id 'P00' -Name 'test harness completed without an unhandled error' -Status 'FAIL' -Detail $_.Exception.Message -LogPath $null -Seconds 0
}
finally {
    # Restore every mutated tracked file, byte-for-byte, and verify.
    $restoreErrors = New-Object System.Collections.Generic.List[string]
    foreach ($file in $mutableFiles) {
        try {
            if (Test-Path -LiteralPath $file -PathType Leaf) { Restore-File $file }
        }
        catch {
            $restoreErrors.Add("$file : $($_.Exception.Message)") | Out-Null
        }
    }
    $postInventory = @(Get-FileInventory -Paths $inventoryFiles)
    $postInventory | Export-Csv -NoTypeInformation -Path (Join-Path $EvidenceRoot 'mutable-inventory-restored.csv')
    if ($restoreErrors.Count -gt 0) {
        Add-TestCase -Id 'P18' -Name 'exact restore of mutated tracked files' -Status 'FAIL' -Detail ($restoreErrors -join '; ') -LogPath $null -Seconds 0
    }
    else {
        $preText = (Get-Content -LiteralPath (Join-Path $preDir 'mutable-inventory.csv') -Raw)
        $postText = (Get-Content -LiteralPath (Join-Path $EvidenceRoot 'mutable-inventory-restored.csv') -Raw)
        $same = ($preText -eq $postText)
        Add-TestCase -Id 'P18' -Name 'exact restore of mutated tracked files' -Status $(if ($same) { 'PASS' } else { 'FAIL' }) -Detail "inventoryMatch=$same" -LogPath $null -Seconds 0
    }

    if ($cleanupJunction -and (Test-Path -LiteralPath $cleanupJunction)) {
        try { Remove-Item -LiteralPath $cleanupJunction -Force } catch { }
    }
    if ($fixtureRepo -and (Test-Path -LiteralPath $fixtureRepo)) {
        $resolvedFixture = Resolve-SharedPluginFullPath $fixtureRepo
        if ($resolvedFixture.StartsWith($EvidenceRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            # Long MSBuild paths can defeat Remove-Item; rd handles them and is best-effort.
            $null = & "$env:SystemRoot\System32\cmd.exe" /c ('rd /s /q "\\?\' + $resolvedFixture + '" 2>nul')
            if (Test-Path -LiteralPath $resolvedFixture) {
                try { Remove-Item -LiteralPath $resolvedFixture -Recurse -Force -ErrorAction SilentlyContinue } catch { }
            }
        }
    }
}

Write-JUnitXml -Path (Join-Path $EvidenceRoot 'test-results.xml')
$summaryPath = Join-Path $EvidenceRoot 'test-summary.json'
[ordered]@{
    StartedAtUtc = $script:overallStart.ToString('o')
    FinishedAtUtc = [DateTime]::UtcNow.ToString('o')
    EvidenceRoot = $EvidenceRoot
    RepoRoot = $repoRoot
    Total = $script:cases.Count
    Passed = @($script:cases | Where-Object { $_.Status -eq 'PASS' }).Count
    Failed = @($script:cases | Where-Object { $_.Status -eq 'FAIL' }).Count
    NotRun = @($script:cases | Where-Object { $_.Status -eq 'NOT_RUN' }).Count
    OverallFail = $script:overallFail
    Cases = $script:cases.ToArray()
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding UTF8

Write-Host ''
Write-Host ("SUMMARY total={0} pass={1} fail={2} not_run={3} evidence={4}" -f $script:cases.Count,
    @($script:cases | Where-Object { $_.Status -eq 'PASS' }).Count,
    @($script:cases | Where-Object { $_.Status -eq 'FAIL' }).Count,
    @($script:cases | Where-Object { $_.Status -eq 'NOT_RUN' }).Count,
    $EvidenceRoot)
Write-Host "XML: $(Join-Path $EvidenceRoot 'test-results.xml')"

if ($script:overallFail) { exit 1 }
exit 0
