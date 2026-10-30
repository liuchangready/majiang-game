#Requires -Version 5.1
# Shared, dependency-free helpers for the P4.5 Windows Player E2E tooling.
# This file is dot-sourced by run-p45-e2e.ps1 and by the verification tests.
# It deliberately does not use Pester, external modules, shell string evaluation,
# or product-test backdoors.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$script:P45ToolVersion = 'P4.5-E2E/1'
$script:P45SchemaVersion = 1
$script:P45Utf8 = New-Object System.Text.UTF8Encoding($false)
$script:P45Utf8Bom = New-Object System.Text.UTF8Encoding($true)
$script:P45RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$script:P45Views = @('BEFORE_RECOVERY', 'AFTER_RECOVERY', 'UNKNOWN')
$script:P45ObservationSources = @(
    'PLAYER_STRUCTURED_EXPORT',
    'TOOL_SELF_TEST',
    'SAVE_HEADER_EXTRACT',
    'MANUAL_TRANSCRIPTION'
)
$script:P45BuildProvenances = @('UNKNOWN', 'USER_DECLARED', 'BUILD_LOG_ATTACHED')
# Fail-closed log redaction policy. Keys are matched quoted or unquoted and
# case-insensitively; nested JSON under a sensitive key is never published.
$script:P45SensitiveKeyAlternation = 'set-cookie|cookie|authorization|bearer|tickets?|access[_-]?token|refresh[_-]?token|id[_-]?token|token|opaque|secret|password|passwd|api[_-]?key|apikey|credential|signature'
$script:P45RedactionPolicy = 'P45-LOG-REDACTION/2-FAIL-CLOSED'

function Get-P45ExitCodeMap {
    @{
        OK = 0
        USAGE = 2
        RUN_EXISTS = 3
        OWNERSHIP_OR_IDENTITY = 4
        NOT_FOUND = 5
        PLAYER_NONZERO_EXIT = 10
        TIMEOUT = 11
        LAUNCH_FAILED = 12
        STOP_FAILED = 13
        EVIDENCE_INVALID = 14
        MANIFEST_INVALID = 15
        COMPARE_MISMATCH = 16
        COMPARE_BLOCKED = 17
        OBSERVATION_INVALID = 18
        OBSERVATION_IDENTITY = 19
        TOOL_SELF_TEST_ONLY = 30
        INTERNAL_ERROR = 99
    }
}

function Throw-P45 {
    param(
        [int]$Code,
        [string]$Status,
        [string]$Message
    )

    $safe = (($Message -replace '[\r\n|]+', ' ').Trim())
    throw ('P45ERR|' + $Code.ToString([Globalization.CultureInfo]::InvariantCulture) + '|' + $Status + '|' + $safe)
}

function Get-P45ErrorMessage {
    param([object]$ErrorRecord)

    if ($null -eq $ErrorRecord) { return 'unknown error' }
    $text = [string]$ErrorRecord
    if ($ErrorRecord -is [System.Management.Automation.ErrorRecord]) {
        $text = [string]$ErrorRecord.Exception.Message
    }
    return ($text -replace '[\r\n]+', ' ').Trim()
}

function Get-P45UtcNowText {
    ([DateTime]::UtcNow).ToString('o', [Globalization.CultureInfo]::InvariantCulture)
}

function Get-P45TimeContext {
    $now = [DateTimeOffset]::UtcNow
    $tz = $null
    try {
        $tz = [TimeZoneInfo]::FindSystemTimeZoneById('China Standard Time')
    }
    catch {
        $tz = [TimeZoneInfo]::Local
    }

    $local = [TimeZoneInfo]::ConvertTime($now, $tz)
    $offset = $local.ToString('zzz', [Globalization.CultureInfo]::InvariantCulture)
    [pscustomobject]@{
        utc = $now.ToString('o', [Globalization.CultureInfo]::InvariantCulture)
        local = $local.ToString('yyyy-MM-ddTHH:mm:ss.fffzzz', [Globalization.CultureInfo]::InvariantCulture)
        label = if ($tz.Id -eq 'China Standard Time') { 'Asia/Shanghai' } else { $tz.Id }
        windowsTimeZoneId = $tz.Id
        utcOffset = $offset
        localDate = $local.ToString('yyyyMMdd-HHmmss', [Globalization.CultureInfo]::InvariantCulture)
    }
}

function Get-P45FullPath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [string]$BaseDirectory = (Get-Location).Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        Throw-P45 2 'USAGE' 'Path must not be empty.'
    }

    $candidate = $Path
    if (-not [IO.Path]::IsPathRooted($candidate)) {
        $candidate = Join-Path $BaseDirectory $candidate
    }

    try {
        return [IO.Path]::GetFullPath($candidate)
    }
    catch {
        Throw-P45 2 'USAGE' ('Path is not valid: ' + $Path)
    }
}

function Assert-P45UnderDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Directory
    )

    $full = Get-P45FullPath -Path $Path
    $root = (Get-P45FullPath -Path $Directory).TrimEnd('\', '/')
    if (-not $full.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
        -not [string]::Equals($full, $root, [StringComparison]::OrdinalIgnoreCase)) {
        Throw-P45 4 'OWNERSHIP_OR_IDENTITY' ('Path escapes the owned directory: ' + $Path)
    }
    return $full
}

function Get-P45RelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$BaseDirectory,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $base = (Get-P45FullPath -Path $BaseDirectory).TrimEnd('\', '/')
    $full = Get-P45FullPath -Path $Path
    if ($full.StartsWith($base + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        return $full.Substring($base.Length + 1)
    }
    if ([string]::Equals($full, $base, [StringComparison]::OrdinalIgnoreCase)) {
        return '.'
    }
    return $full
}

function Get-P45Sha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not [IO.File]::Exists($Path)) {
        Throw-P45 5 'NOT_FOUND' ('File not found: ' + $Path)
    }
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        try {
            return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
        }
        catch {
            if ($attempt -eq 19) { throw }
            Start-Sleep -Milliseconds 100
        }
    }
}

function Get-P45TextSha256 {
    param([AllowEmptyString()][string]$Text)

    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash($script:P45Utf8.GetBytes($Text)))).Replace('-', '').ToUpperInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Get-P45BytesSha256 {
    param([byte[]]$Bytes)

    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-', '').ToUpperInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Write-P45JsonAtomic {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][object]$Object
    )

    $full = Get-P45FullPath -Path $Path
    $dir = Split-Path -Parent $full
    if (-not [IO.Directory]::Exists($dir)) {
        [IO.Directory]::CreateDirectory($dir) | Out-Null
    }

    $json = ($Object | ConvertTo-Json -Depth 30) + [Environment]::NewLine
    $tmp = $full + '.tmp.' + [Guid]::NewGuid().ToString('N')
    try {
        [IO.File]::WriteAllText($tmp, $json, $script:P45Utf8)
        if ([IO.File]::Exists($full)) {
            [IO.File]::Replace($tmp, $full, [NullString]::Value)
        }
        else {
            [IO.File]::Move($tmp, $full)
        }
    }
    finally {
        if ([IO.File]::Exists($tmp)) {
            [IO.File]::Delete($tmp)
        }
    }
}

function Write-P45TextAtomic {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text
    )

    $full = Get-P45FullPath -Path $Path
    $dir = Split-Path -Parent $full
    if (-not [IO.Directory]::Exists($dir)) {
        [IO.Directory]::CreateDirectory($dir) | Out-Null
    }
    $tmp = $full + '.tmp.' + [Guid]::NewGuid().ToString('N')
    try {
        [IO.File]::WriteAllText($tmp, $Text, $script:P45Utf8)
        if ([IO.File]::Exists($full)) {
            [IO.File]::Replace($tmp, $full, [NullString]::Value)
        }
        else {
            [IO.File]::Move($tmp, $full)
        }
    }
    finally {
        if ([IO.File]::Exists($tmp)) {
            [IO.File]::Delete($tmp)
        }
    }
}

function Read-P45Json {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not [IO.File]::Exists($Path)) {
        Throw-P45 5 'NOT_FOUND' ('JSON file not found: ' + $Path)
    }
    $text = $null
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        try {
            $text = [IO.File]::ReadAllText($Path, $script:P45Utf8)
            break
        }
        catch {
            if ($attempt -eq 19) {
                Throw-P45 14 'EVIDENCE_INVALID' ('Could not read JSON in ' + $Path + ': ' + (Get-P45ErrorMessage $_))
            }
            Start-Sleep -Milliseconds 100
        }
    }
    try {
        if ([string]::IsNullOrWhiteSpace($text)) {
            Throw-P45 14 'EVIDENCE_INVALID' ('JSON file is empty: ' + $Path)
        }
        return ($text | ConvertFrom-Json)
    }
    catch {
        if ([string]$_.Exception.Message -like 'P45ERR|*') { throw }
        Throw-P45 14 'EVIDENCE_INVALID' ('Invalid JSON in ' + $Path + ': ' + (Get-P45ErrorMessage $_))
    }
}

function Get-P45Property {
    param(
        [object]$Object,
        [string]$Name,
        [object]$Default = $null
    )

    if ($null -eq $Object) { return $Default }
    if ($Object -is [System.Collections.IDictionary]) {
        if ($Object.Contains($Name)) { return $Object[$Name] }
        return $Default
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $Default }
    return $property.Value
}

function ConvertTo-P45InvariantString {
    param([object]$Value)

    if ($null -eq $Value) { return '' }
    if ($Value -is [bool]) { return $(if ($Value) { 'true' } else { 'false' }) }
    if ($Value -is [DateTime]) {
        return $Value.ToUniversalTime().ToString('o', [Globalization.CultureInfo]::InvariantCulture)
    }
    return [Convert]::ToString($Value, [Globalization.CultureInfo]::InvariantCulture)
}

function Test-P45Sha256Text {
    param([object]$Value)

    return ($Value -is [string] -and $Value -match '^[A-F0-9]{64}$')
}

function Test-P45CommitText {
    param([object]$Value)

    return ($Value -is [string] -and ($Value -eq 'UNKNOWN' -or $Value -match '^[0-9a-fA-F]{40}$'))
}

function Get-P45SourceSnapshot {
    param([string]$RepoRoot = $script:P45RepoRoot)

    $git = Get-Command git.exe -ErrorAction SilentlyContinue
    if ($null -eq $git) {
        return [pscustomobject]@{
            head = 'UNKNOWN'
            branch = 'UNKNOWN'
            dirty = $null
            dirtyKnown = $false
            dirtyFileCount = 0
            statusPorcelain = ''
            statusSha256 = Get-P45TextSha256 ''
            diffStat = ''
            diffStatSha256 = Get-P45TextSha256 ''
        }
    }

    $head = (& git.exe -C $RepoRoot rev-parse HEAD 2>$null)
    $headExit = $LASTEXITCODE
    $branch = (& git.exe -C $RepoRoot rev-parse --abbrev-ref HEAD 2>$null)
    $branchExit = $LASTEXITCODE
    $statusLines = @(& git.exe -C $RepoRoot status --porcelain=v1 --untracked-files=all 2>$null)
    $statusExit = $LASTEXITCODE
    $diffStatLines = @(& git.exe -C $RepoRoot diff --stat HEAD 2>$null)
    $diffStatExit = $LASTEXITCODE

    if ($headExit -ne 0 -or $branchExit -ne 0 -or $statusExit -ne 0 -or $diffStatExit -ne 0) {
        return [pscustomobject]@{
            head = 'UNKNOWN'
            branch = 'UNKNOWN'
            dirty = $null
            dirtyKnown = $false
            dirtyFileCount = 0
            statusPorcelain = ''
            statusSha256 = Get-P45TextSha256 ''
            diffStat = ''
            diffStatSha256 = Get-P45TextSha256 ''
        }
    }

    $statusText = ($statusLines -join "`n")
    $diffStatText = ($diffStatLines -join "`n")
    $nonEmpty = @($statusLines | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    [pscustomobject]@{
        head = ([string]$head).Trim()
        branch = ([string]$branch).Trim()
        dirty = ($nonEmpty.Count -gt 0)
        dirtyKnown = $true
        dirtyFileCount = $nonEmpty.Count
        statusPorcelain = $statusText
        statusSha256 = Get-P45TextSha256 $statusText
        diffStat = $diffStatText
        diffStatSha256 = Get-P45TextSha256 $diffStatText
    }
}

function Get-P45FileRecord {
    param([Parameter(Mandatory = $true)][string]$Path)

    $full = Get-P45FullPath -Path $Path
    if (-not [IO.File]::Exists($full)) {
        Throw-P45 5 'NOT_FOUND' ('File not found: ' + $full)
    }
    $item = Get-Item -LiteralPath $full
    [pscustomobject]@{
        path = $full
        bytes = [long]$item.Length
        sha256 = Get-P45Sha256 -Path $full
        lastWriteUtc = $item.LastWriteTimeUtc.ToString('o', [Globalization.CultureInfo]::InvariantCulture)
    }
}

function Get-P45PlayerRecord {
    param(
        [Parameter(Mandatory = $true)][string]$PlayerPath,
        [string]$BuildProvenance = 'UNKNOWN',
        [string]$BuildCommit = 'UNKNOWN',
        [string]$BuildSourceNote = '',
        [string]$BuildLog = '',
        [string]$ExpectedPlayerSha256 = ''
    )

    $full = Get-P45FullPath -Path $PlayerPath
    if (-not [IO.File]::Exists($full)) {
        Throw-P45 5 'NOT_FOUND' ('Player executable not found: ' + $full)
    }
    if ([IO.Path]::GetExtension($full) -ine '.exe') {
        Throw-P45 2 'USAGE' 'The Player path must point to a Windows .exe file.'
    }

    if ([string]::IsNullOrWhiteSpace($BuildProvenance)) { $BuildProvenance = 'UNKNOWN' }
    if ($script:P45BuildProvenances -notcontains $BuildProvenance) {
        Throw-P45 2 'USAGE' ('BuildProvenance must be one of: ' + ($script:P45BuildProvenances -join ', '))
    }
    if ([string]::IsNullOrWhiteSpace($BuildCommit)) { $BuildCommit = 'UNKNOWN' }
    if (-not (Test-P45CommitText $BuildCommit)) {
        Throw-P45 2 'USAGE' 'BuildCommit must be UNKNOWN or a 40-character hexadecimal commit.'
    }
    if ($BuildProvenance -eq 'UNKNOWN' -and $BuildCommit -ne 'UNKNOWN') {
        Throw-P45 2 'USAGE' 'A known BuildCommit requires explicit BuildProvenance USER_DECLARED or BUILD_LOG_ATTACHED.'
    }

    $record = Get-P45FileRecord -Path $full
    if (-not [string]::IsNullOrWhiteSpace($ExpectedPlayerSha256) -and
        $ExpectedPlayerSha256.ToUpperInvariant() -ne $record.sha256) {
        Throw-P45 4 'OWNERSHIP_OR_IDENTITY' ('Player SHA-256 mismatch: expected ' + $ExpectedPlayerSha256 + ', actual ' + $record.sha256)
    }

    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($full)
    $dataDirectory = Join-Path (Split-Path -Parent $full) ([IO.Path]::GetFileNameWithoutExtension($full) + '_Data')
    $appInfo = $null
    $appInfoPath = Join-Path $dataDirectory 'app.info'
    if ([IO.File]::Exists($appInfoPath)) {
        $lines = @([IO.File]::ReadAllLines($appInfoPath, $script:P45Utf8) | Select-Object -First 4)
        $appInfo = [pscustomobject]@{
            path = $appInfoPath
            sha256 = Get-P45Sha256 -Path $appInfoPath
            lines = $lines
        }
    }

    $bootConfig = $null
    $bootPath = Join-Path $dataDirectory 'boot.config'
    if ([IO.File]::Exists($bootPath)) {
        $bootConfig = Get-P45FileRecord -Path $bootPath
    }

    $buildLogRecord = $null
    if (-not [string]::IsNullOrWhiteSpace($BuildLog)) {
        $buildLogRecord = Get-P45FileRecord -Path (Get-P45FullPath -Path $BuildLog)
    }

    [pscustomobject]@{
        path = $record.path
        bytes = $record.bytes
        sha256 = $record.sha256
        lastWriteUtc = $record.lastWriteUtc
        fileVersion = [string]$version.FileVersion
        productVersion = [string]$version.ProductVersion
        productName = [string]$version.ProductName
        companyName = [string]$version.CompanyName
        dataDirectory = $dataDirectory
        dataDirectoryExists = [IO.Directory]::Exists($dataDirectory)
        appInfo = $appInfo
        bootConfig = $bootConfig
        knownBuildSource = $BuildSourceNote
        buildProvenance = $BuildProvenance
        buildCommit = if ($BuildCommit -eq 'UNKNOWN') { 'UNKNOWN' } else { $BuildCommit.ToLowerInvariant() }
        buildLog = $buildLogRecord
        buildCommitMatchesSourceHead = $false
    }
}

function New-P45Run {
    param(
        [string]$RunRoot = '',
        [string]$RunDir = '',
        [Parameter(Mandatory = $true)][string]$PlayerPath,
        [ValidateSet('GAME', 'TOOL_SELF_TEST')][string]$EvidenceClass = 'GAME',
        [ValidateSet('LocalSmoke', 'Manual', 'ToolSelfTest')][string]$Mode = 'LocalSmoke',
        [switch]$Visible,
        [string]$BuildProvenance = 'UNKNOWN',
        [string]$BuildCommit = 'UNKNOWN',
        [string]$BuildSourceNote = '',
        [string]$BuildLog = '',
        [string]$ExpectedPlayerSha256 = ''
    )

    if ($EvidenceClass -eq 'TOOL_SELF_TEST' -and $Mode -ne 'ToolSelfTest') {
        Throw-P45 2 'USAGE' 'EvidenceClass TOOL_SELF_TEST requires Mode ToolSelfTest.'
    }
    if ($EvidenceClass -eq 'GAME' -and $Mode -eq 'ToolSelfTest') {
        Throw-P45 2 'USAGE' 'Mode ToolSelfTest is only available with EvidenceClass TOOL_SELF_TEST.'
    }

    if ([string]::IsNullOrWhiteSpace($RunDir)) {
        $base = if ([string]::IsNullOrWhiteSpace($RunRoot)) {
            Join-Path $script:P45RepoRoot 'build/p45-e2e-tooling/runs'
        }
        else {
            Get-P45FullPath -Path $RunRoot
        }
        $stamp = (Get-P45TimeContext).localDate
        $RunDir = Join-Path $base ($stamp + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    }
    else {
        $RunDir = Get-P45FullPath -Path $RunDir
    }

    if ([IO.Directory]::Exists($RunDir) -or [IO.File]::Exists($RunDir)) {
        Throw-P45 3 'RUN_EXISTS' ('Refusing to overwrite an existing run path: ' + $RunDir)
    }

    $source = Get-P45SourceSnapshot
    $player = Get-P45PlayerRecord -PlayerPath $PlayerPath -BuildProvenance $BuildProvenance `
        -BuildCommit $BuildCommit -BuildSourceNote $BuildSourceNote -BuildLog $BuildLog `
        -ExpectedPlayerSha256 $ExpectedPlayerSha256
    $player.buildCommitMatchesSourceHead = ($player.buildCommit -ne 'UNKNOWN' -and
        $player.buildCommit -eq $source.head.ToLowerInvariant())

    $paths = [pscustomobject]@{
        root = $RunDir
        save = Join-Path $RunDir 'local'
        outbox = Join-Path $RunDir 'client\outbox'
        log = Join-Path $RunDir 'log'
        screenshots = Join-Path $RunDir 'screenshots'
        evidence = Join-Path $RunDir 'evidence'
        attempts = Join-Path $RunDir 'attempts'
        observations = Join-Path $RunDir 'observations'
        control = Join-Path $RunDir 'control'
        localSave = Join-Path $RunDir 'local'
        clientOutbox = Join-Path $RunDir 'client\outbox'
    }

    foreach ($directory in @($paths.save, $paths.outbox, $paths.log, $paths.screenshots, $paths.evidence, $paths.attempts, $paths.observations, $paths.control)) {
        [IO.Directory]::CreateDirectory($directory) | Out-Null
    }

    $runId = 'p45-' + (Get-P45TimeContext).localDate + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $ownerToken = [Guid]::NewGuid().ToString('N')
    $time = Get-P45TimeContext
    $markerPath = Join-Path $RunDir 'ownership.json'
    $marker = [pscustomobject]@{
        schemaVersion = $script:P45SchemaVersion
        kind = 'p45-e2e-ownership'
        runId = $runId
        ownerToken = $ownerToken
        createdAt = $time.utc
        timeZone = $time.label
        sourceHead = $source.head
        playerSha256 = $player.sha256
    }
    Write-P45JsonAtomic -Path $markerPath -Object $marker

    $scenarioNames = @(
        'menu_create_local_room',
        'stack_and_deal',
        'full_round_tsumo_or_ron',
        'repeat_round_or_next_round',
        'match_finish_and_ranking',
        'draw_tsumo_discard_call_kan_riichi',
        'tile_mark',
        'sleeve_bloom',
        'beam_swap',
        'ai_takeover_and_reclaim',
        'exhaustive_draw',
        'normal_exit_cold_recovery',
        'forced_kill_cold_recovery',
        'corrupt_save',
        'previous_backup_recovery',
        'spectator_privacy',
        'local_result_isolation'
    )
    $scenarios = @(
        foreach ($name in $scenarioNames) {
            [pscustomobject]@{
                name = $name
                status = 'NOT_RUN'
                evidence = @()
                note = 'Initialized by Prepare. This tool does not execute a full match or accept recovery without real structured observations.'
            }
        }
    )

    $platform = [pscustomobject]@{
        os = [Environment]::OSVersion.VersionString
        is64BitOperatingSystem = [Environment]::Is64BitOperatingSystem
        is64BitProcess = [Environment]::Is64BitProcess
        processorCount = [Environment]::ProcessorCount
        powershellVersion = $PSVersionTable.PSVersion.ToString()
        powershellEdition = if ($PSVersionTable.ContainsKey('PSEdition')) { [string]$PSVersionTable.PSEdition } else { 'Desktop' }
        culture = [Globalization.CultureInfo]::CurrentCulture.Name
    }

    $run = [pscustomobject]@{
        schemaVersion = $script:P45SchemaVersion
        kind = 'p45-e2e-run'
        runId = $runId
        toolVersion = $script:P45ToolVersion
        createdAt = $time.utc
        time = $time
        platform = $platform
        evidenceClass = $EvidenceClass
        status = 'PREPARED'
        source = $source
        player = $player
        paths = $paths
        ownership = [pscustomobject]@{
            marker = $markerPath
            markerSha256 = Get-P45Sha256 -Path $markerPath
        }
        launch = [pscustomobject]@{
            mode = $Mode
            visible = [bool]$Visible
            hiddenDefault = -not [bool]$Visible
            argumentsRecorded = $true
        }
        attemptCount = 0
        scenarios = $scenarios
        collections = @()
        notes = @(
            'The Windows Player smoke path only proves menu/lobby/local-table start, one legal action, one visible event, return to menu and clean exit.',
            'It does not prove a full match, every round transition, recovery consistency, spectator privacy, or any Android/iOS/IL2CPP result.',
            'Structured recovery observations require a Player export hook or a future explicit importer; save headers alone are not enough for PASS.'
        )
    }

    Write-P45JsonAtomic -Path (Join-Path $RunDir 'run.json') -Object $run
    Write-P45JsonAtomic -Path (Join-Path $RunDir 'state.json') -Object ([pscustomobject]@{
        schemaVersion = $script:P45SchemaVersion
        kind = 'p45-e2e-state'
        runId = $runId
        updatedAt = (Get-P45UtcNowText)
        activeProcess = $null
        status = 'PREPARED'
    })

    return [pscustomobject]@{
        Root = $RunDir
        Run = $run
        Marker = $marker
    }
}

function Get-P45Run {
    param([Parameter(Mandatory = $true)][string]$RunDir)

    $root = Get-P45FullPath -Path $RunDir
    if (-not [IO.Directory]::Exists($root)) {
        Throw-P45 5 'NOT_FOUND' ('Run directory not found: ' + $root)
    }

    $runPath = Join-Path $root 'run.json'
    $markerPath = Join-Path $root 'ownership.json'
    if (-not [IO.File]::Exists($runPath) -or -not [IO.File]::Exists($markerPath)) {
        Throw-P45 4 'OWNERSHIP_OR_IDENTITY' ('Run ownership files are missing under: ' + $root)
    }

    $run = Read-P45Json -Path $runPath
    $marker = Read-P45Json -Path $markerPath
    if ((Get-P45Property $run 'kind') -ne 'p45-e2e-run' -or (Get-P45Property $run 'schemaVersion') -ne $script:P45SchemaVersion) {
        Throw-P45 4 'OWNERSHIP_OR_IDENTITY' 'run.json is not a supported P4.5 run record.'
    }
    if ((Get-P45Property $marker 'kind') -ne 'p45-e2e-ownership' -or
        (Get-P45Property $marker 'runId') -ne (Get-P45Property $run 'runId') -or
        [string]::IsNullOrWhiteSpace([string](Get-P45Property $marker 'ownerToken'))) {
        Throw-P45 4 'OWNERSHIP_OR_IDENTITY' 'Ownership marker does not belong to this run.'
    }

    $expectedMarkerHash = [string](Get-P45Property (Get-P45Property $run 'ownership') 'markerSha256')
    $actualMarkerHash = Get-P45Sha256 -Path $markerPath
    if (-not [string]::Equals($expectedMarkerHash, $actualMarkerHash, [StringComparison]::OrdinalIgnoreCase)) {
        Throw-P45 4 'OWNERSHIP_OR_IDENTITY' 'Ownership marker hash no longer matches run.json.'
    }

    $runPaths = Get-P45Property $run 'paths'
    $runRoot = [string](Get-P45Property $runPaths 'root')
    if (-not [string]::Equals((Get-P45FullPath $runRoot), $root, [StringComparison]::OrdinalIgnoreCase)) {
        Throw-P45 4 'OWNERSHIP_OR_IDENTITY' 'run.json paths.root does not match the opened run directory.'
    }

    [pscustomobject]@{
        Root = $root
        RunPath = $runPath
        MarkerPath = $markerPath
        Run = $run
        Marker = $marker
    }
}

function Save-P45Run {
    param([Parameter(Mandatory = $true)][object]$Context)

    Write-P45JsonAtomic -Path $Context.RunPath -Object $Context.Run
}

function Get-P45State {
    param([Parameter(Mandatory = $true)][object]$Context)

    $path = Join-Path $Context.Root 'state.json'
    if (-not [IO.File]::Exists($path)) {
        return [pscustomobject]@{
            schemaVersion = $script:P45SchemaVersion
            kind = 'p45-e2e-state'
            runId = $Context.Run.runId
            updatedAt = (Get-P45UtcNowText)
            activeProcess = $null
            status = 'UNKNOWN'
        }
    }
    $state = Read-P45Json -Path $path
    if ((Get-P45Property $state 'runId') -ne $Context.Run.runId) {
        Throw-P45 4 'OWNERSHIP_OR_IDENTITY' 'state.json belongs to a different run.'
    }
    return $state
}

function Save-P45State {
    param(
        [Parameter(Mandatory = $true)][object]$Context,
        [Parameter(Mandatory = $true)][object]$State
    )

    $State.updatedAt = Get-P45UtcNowText
    Write-P45JsonAtomic -Path (Join-Path $Context.Root 'state.json') -Object $State
}

function Get-P45AttemptDirectories {
    param([Parameter(Mandatory = $true)][object]$Context)

    $attemptsRoot = Join-Path $Context.Root 'attempts'
    if (-not [IO.Directory]::Exists($attemptsRoot)) { return @() }
    @(Get-ChildItem -LiteralPath $attemptsRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d{4}$' } |
        Sort-Object Name)
}

function Get-P45NextAttemptNumber {
    param([Parameter(Mandatory = $true)][object]$Context)

    $dirs = @(Get-P45AttemptDirectories -Context $Context)
    if ($dirs.Count -eq 0) { return 1 }
    $max = 0
    foreach ($dir in $dirs) {
        $n = [int]$dir.Name
        if ($n -gt $max) { $max = $n }
    }
    return $max + 1
}

function Get-P45ProcessStartUtc {
    param([Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process)

    for ($i = 0; $i -lt 20; $i++) {
        try {
            return $Process.StartTime.ToUniversalTime().ToString('o', [Globalization.CultureInfo]::InvariantCulture)
        }
        catch {
            Start-Sleep -Milliseconds 50
            try { $Process.Refresh() } catch { return $null }
            if ($Process.HasExited) { return $null }
        }
    }
    return $null
}

function Get-P45ProcessByIdentity {
    param(
        [int]$ProcessId,
        [string]$ExpectedPath,
        [string]$ExpectedSha256,
        [string]$ExpectedStartUtc
    )

    try {
        $process = Get-Process -Id $ProcessId -ErrorAction Stop
    }
    catch {
        return [pscustomobject]@{ Exists = $false; Reason = 'PROCESS_NOT_FOUND'; Process = $null }
    }

    $actualPath = $null
    try { $actualPath = $process.Path } catch { $actualPath = $null }
    if ([string]::IsNullOrWhiteSpace($actualPath)) {
        return [pscustomobject]@{ Exists = $false; Reason = 'PROCESS_PATH_UNAVAILABLE'; Process = $process }
    }

    if (-not [string]::Equals((Get-P45FullPath $actualPath), (Get-P45FullPath $ExpectedPath), [StringComparison]::OrdinalIgnoreCase)) {
        return [pscustomobject]@{ Exists = $false; Reason = 'PROCESS_PATH_MISMATCH'; Process = $process }
    }

    $start = $null
    try { $start = $process.StartTime.ToUniversalTime().ToString('o', [Globalization.CultureInfo]::InvariantCulture) } catch { $start = $null }
    if ([string]::IsNullOrWhiteSpace($start) -or -not [string]::Equals($start, $ExpectedStartUtc, [StringComparison]::Ordinal)) {
        return [pscustomobject]@{ Exists = $false; Reason = 'PROCESS_START_TIME_MISMATCH'; Process = $process }
    }

    if (-not [string]::IsNullOrWhiteSpace($ExpectedSha256)) {
        $actualHash = Get-P45Sha256 -Path $actualPath
        if (-not [string]::Equals($actualHash, $ExpectedSha256, [StringComparison]::OrdinalIgnoreCase)) {
            return [pscustomobject]@{ Exists = $false; Reason = 'PROCESS_EXE_HASH_MISMATCH'; Process = $process }
        }
    }

    [pscustomobject]@{ Exists = $true; Reason = 'MATCH'; Process = $process }
}

function Get-P45ProcessCommandLine {
    param([Parameter(Mandatory = $true)][int]$ProcessId)

    try {
        $process = Get-CimInstance -ClassName Win32_Process -Filter ('ProcessId=' + $ProcessId) -ErrorAction Stop
        if ($null -eq $process) { return $null }
        return [string]$process.CommandLine
    }
    catch {
        return $null
    }
}

function Test-P45ProcessRunBinding {
    param(
        [Parameter(Mandatory = $true)][int]$ProcessId,
        [Parameter(Mandatory = $true)][object]$Context,
        [Parameter(Mandatory = $true)][string]$Mode
    )

    $commandLine = Get-P45ProcessCommandLine -ProcessId $ProcessId
    if ([string]::IsNullOrWhiteSpace($commandLine)) { return 'COMMAND_LINE_UNAVAILABLE' }
    $needle = if ($Mode -eq 'ToolSelfTest') {
        [string]$Context.Run.paths.save
    }
    else {
        [string]$Context.Run.paths.root
    }
    if ($commandLine.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        return 'RUN_BINDING_MISMATCH'
    }
    return 'MATCH'
}

function Get-P45ProcessAlive {
    param([int]$ProcessId)
    try {
        $p = Get-Process -Id $ProcessId -ErrorAction Stop
        return (-not $p.HasExited)
    }
    catch {
        return $false
    }
}

function Get-P45NativeExitCode {
    param([Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process)

    try {
        $Process.Refresh()
        if ($Process.HasExited) { return [int]$Process.ExitCode }
    }
    catch { }
    return $null
}

function Quote-P45Argument {
    param([AllowEmptyString()][string]$Argument)

    if ($null -eq $Argument) { return '""' }
    if ($Argument.Length -eq 0) { return '""' }
    if ($Argument -notmatch '[\s"]') { return $Argument }

    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('"')
    $slashes = 0
    foreach ($ch in $Argument.ToCharArray()) {
        if ($ch -eq '\') {
            $slashes++
            continue
        }
        if ($ch -eq '"') {
            [void]$sb.Append('\' * (($slashes * 2) + 1))
            [void]$sb.Append('"')
            $slashes = 0
            continue
        }
        if ($slashes -gt 0) {
            [void]$sb.Append('\' * $slashes)
            $slashes = 0
        }
        [void]$sb.Append($ch)
    }
    if ($slashes -gt 0) {
        [void]$sb.Append('\' * ($slashes * 2))
    }
    [void]$sb.Append('"')
    return $sb.ToString()
}

function Join-P45Arguments {
    param([string[]]$Arguments)

    $parts = @()
    foreach ($arg in $Arguments) {
        $parts += (Quote-P45Argument -Argument ([string]$arg))
    }
    return ($parts -join ' ')
}

function Hide-P45ProcessWindow {
    param([Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process)

    try {
        if (-not ([System.Management.Automation.PSTypeName]'P45.NativeMethods').Type) {
            Add-Type -Namespace P45 -Name NativeMethods -MemberDefinition @'
[DllImport("user32.dll")]
public static extern bool ShowWindow(System.IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")]
public static extern bool IsWindowVisible(System.IntPtr hWnd);
'@ | Out-Null
        }
        for ($i = 0; $i -lt 10; $i++) {
            if ($Process.HasExited) { return }
            $handle = $Process.MainWindowHandle
            if ($handle -ne [IntPtr]::Zero) {
                [void][P45.NativeMethods]::ShowWindow($handle, 0)
                return
            }
            Start-Sleep -Milliseconds 100
        }
    }
    catch {
        # Hiding is best effort only. It never changes process ownership checks.
    }
}

function New-P45Attempt {
    param(
        [Parameter(Mandatory = $true)][object]$Context,
        [ValidateSet('LocalSmoke', 'Manual', 'ToolSelfTest')][string]$Mode,
        [switch]$Visible,
        [switch]$Wait,
        [int]$TimeoutSeconds = 60,
        [ValidateSet('hold', 'autoexit', 'exit0', 'exit7')][string]$SelfTestMode = 'hold',
        [int]$SelfTestHoldSeconds = 60
    )

    if ($TimeoutSeconds -lt 1 -or $TimeoutSeconds -gt 86400) {
        Throw-P45 2 'USAGE' 'TimeoutSeconds must be between 1 and 86400.'
    }
    if ($Context.Run.evidenceClass -eq 'TOOL_SELF_TEST' -and $Mode -ne 'ToolSelfTest') {
        Throw-P45 2 'USAGE' 'TOOL_SELF_TEST runs can only launch with Mode ToolSelfTest.'
    }
    if ($Context.Run.evidenceClass -eq 'GAME' -and $Mode -eq 'ToolSelfTest') {
        Throw-P45 2 'USAGE' 'Mode ToolSelfTest is forbidden for GAME evidence.'
    }
    if ($Mode -eq 'Manual' -and -not $Visible) {
        Throw-P45 2 'USAGE' 'Manual player mode must be explicitly visible. Re-run with -Visible.'
    }

    $state = Get-P45State -Context $Context
    $active = Get-P45Property $state 'activeProcess'
    if ($null -ne $active) {
        $identity = Get-P45ProcessByIdentity -ProcessId ([int]$active.pid) -ExpectedPath ([string]$active.path) `
            -ExpectedSha256 ([string]$active.sha256) -ExpectedStartUtc ([string]$active.startTimeUtc)
        if ($identity.Exists) {
            $binding = Test-P45ProcessRunBinding -ProcessId ([int]$active.pid) -Context $Context -Mode ([string]$active.mode)
            if ($binding -ne 'MATCH') {
                Throw-P45 4 'OWNERSHIP_OR_IDENTITY' ('Refusing to launch: active PID binding check returned ' + $binding + '.')
            }
            Throw-P45 4 'OWNERSHIP_OR_IDENTITY' ('Refusing to launch while this run still owns PID ' + $active.pid + '.')
        }
        if ($identity.Reason -ne 'PROCESS_NOT_FOUND') {
            Throw-P45 4 'OWNERSHIP_OR_IDENTITY' ('Refusing to launch while state points at a mismatched live PID: ' + $identity.Reason)
        }
        $state.activeProcess = $null
        $state.status = 'STOPPED'
        Save-P45State -Context $Context -State $state
    }

    $attemptNumber = Get-P45NextAttemptNumber -Context $Context
    $attemptName = $attemptNumber.ToString('0000', [Globalization.CultureInfo]::InvariantCulture)
    $attemptDir = Join-Path $Context.Root ('attempts\' + $attemptName)
    [IO.Directory]::CreateDirectory($attemptDir) | Out-Null
    $logFile = Join-Path $Context.Root ('log\attempt-' + $attemptName + '.log')
    [IO.Directory]::CreateDirectory((Split-Path -Parent $logFile)) | Out-Null
    $screenshotDir = Join-Path $Context.Root ('screenshots\attempt-' + $attemptName)
    [IO.Directory]::CreateDirectory($screenshotDir) | Out-Null
    $attemptJson = Join-Path $attemptDir 'launch.json'
    $exitJson = Join-Path $attemptDir 'exit.json'
    $stopFile = Join-Path $Context.Root 'control\stop.request'
    # A stop request belongs to one launched process. Remove an old marker so a
    # restart cannot be mistaken for an immediate stop; this is control state,
    # never game/save data.
    if ([IO.File]::Exists($stopFile)) { [IO.File]::Delete($stopFile) }

    $player = $Context.Run.player
    $playerPath = [string]$player.path
    $playerHash = [string]$player.sha256
    $hidden = -not [bool]$Visible

    $arguments = @()
    if ($Mode -eq 'ToolSelfTest') {
        $arguments = @(
            '--mode', $SelfTestMode,
            '--data-dir', [string]$Context.Run.paths.save,
            '--outbox-dir', [string]$Context.Run.paths.outbox,
            '--log-dir', (Join-Path $Context.Root 'log'),
            '--log-file', $logFile,
            '--args-out', (Join-Path $attemptDir 'helper-args.txt'),
            '--stop-file', $stopFile,
            '--run-id', ([string]$Context.Run.runId),
            '--attempt', $attemptName,
            '--hold-seconds', ([string]$SelfTestHoldSeconds)
        )
    }
    else {
        $arguments = @(
            '-screen-width', '1280',
            '-screen-height', '720',
            '-screen-fullscreen', '0',
            '-logfile', $logFile,
            '-smoke-data', $Context.Root
        )
        if ($Mode -eq 'LocalSmoke') {
            $arguments += @('-smoke-localhost', $screenshotDir)
        }
    }

    $argumentText = Join-P45Arguments -Arguments $arguments
    $time = Get-P45TimeContext
    $launch = [pscustomobject]@{
        schemaVersion = $script:P45SchemaVersion
        kind = 'p45-e2e-attempt'
        runId = [string]$Context.Run.runId
        attempt = $attemptNumber
        evidenceClass = [string]$Context.Run.evidenceClass
        mode = $Mode
        visible = [bool]$Visible
        hidden = $hidden
        status = 'STARTING'
        startedAt = $time.utc
        time = $time
        player = [pscustomobject]@{
            path = $playerPath
            sha256 = $playerHash
            bytes = [long]$player.bytes
            productVersion = [string]$player.productVersion
            buildCommit = [string]$player.buildCommit
            buildProvenance = [string]$player.buildProvenance
        }
        command = [pscustomobject]@{
            executable = $playerPath
            arguments = $arguments
            argumentsText = $argumentText
            workingDirectory = (Split-Path -Parent $playerPath)
        }
        logFile = $logFile
        stdoutFile = (Join-Path $attemptDir 'stdout.log')
        stderrFile = (Join-Path $attemptDir 'stderr.log')
        screenshotDirectory = $screenshotDir
        stopFile = $stopFile
        pid = $null
        startTimeUtc = $null
        nativeExitCode = $null
        disposition = $null
        toolExitCode = $null
        toolStatus = $null
    }
    Write-P45JsonAtomic -Path $attemptJson -Object $launch

    # Start-Process gives the child real file handles for stdout/stderr. Keeping
    # ProcessStartInfo pipes here would let a no-wait child inherit the caller's
    # captured console and make an outer test/CI capture wait for the game.
    $oldRunId = $env:P45_E2E_RUN_ID
    $oldAttempt = $env:P45_E2E_ATTEMPT
    $oldEvidenceClass = $env:P45_E2E_EVIDENCE_CLASS
    $env:P45_E2E_RUN_ID = [string]$Context.Run.runId
    $env:P45_E2E_ATTEMPT = $attemptName
    $env:P45_E2E_EVIDENCE_CLASS = [string]$Context.Run.evidenceClass
    $windowStyle = if ($hidden) { 'Hidden' } else { 'Normal' }
    [IO.File]::WriteAllText([string]$launch.stdoutFile, '', $script:P45Utf8)
    [IO.File]::WriteAllText([string]$launch.stderrFile, '', $script:P45Utf8)
    $process = $null
    try {
        if ($Wait) {
            # Windows PowerShell 5.1 Start-Process reports ExitCode reliably only
            # when the cmdlet itself waits. The child inherits the caller console,
            # so a wait-mode invocation must be run detached by a test/CI harness
            # that redirects the *tool* output, not by a no-wait launch.
            $process = Start-Process -FilePath $playerPath -ArgumentList $argumentText `
                -WorkingDirectory (Split-Path -Parent $playerPath) -WindowStyle $windowStyle `
                -PassThru -ErrorAction Stop
        }
        else {
            # No-wait launches redirect the child to files. This prevents the child
            # from keeping the caller's captured stdout/stderr pipe open.
            $process = Start-Process -FilePath $playerPath -ArgumentList $argumentText `
                -WorkingDirectory (Split-Path -Parent $playerPath) -WindowStyle $windowStyle `
                -RedirectStandardOutput ([string]$launch.stdoutFile) `
                -RedirectStandardError ([string]$launch.stderrFile) -PassThru -ErrorAction Stop
        }
    }
    catch {
        $env:P45_E2E_RUN_ID = $oldRunId
        $env:P45_E2E_ATTEMPT = $oldAttempt
        $env:P45_E2E_EVIDENCE_CLASS = $oldEvidenceClass
        $launch.status = 'START_FAILED'
        $launch.disposition = 'LAUNCH_FAILED'
        $launch.toolExitCode = 12
        $launch.toolStatus = 'LAUNCH_FAILED'
        Write-P45JsonAtomic -Path $attemptJson -Object $launch
        $state.status = 'LAUNCH_FAILED'
        Save-P45State -Context $Context -State $state
        Throw-P45 12 'LAUNCH_FAILED' ('Could not start the process: ' + (Get-P45ErrorMessage $_))
    }
    $env:P45_E2E_RUN_ID = $oldRunId
    $env:P45_E2E_ATTEMPT = $oldAttempt
    $env:P45_E2E_EVIDENCE_CLASS = $oldEvidenceClass
    if ($null -eq $process) {
        Throw-P45 12 'LAUNCH_FAILED' 'Start-Process returned no process handle.'
    }

    Start-Sleep -Milliseconds 100
    $startTime = Get-P45ProcessStartUtc -Process $process
    if ([string]::IsNullOrWhiteSpace($startTime)) {
        if ($process.HasExited) {
            $native = Get-P45NativeExitCode -Process $process
            $launch.pid = [int]$process.Id
            $launch.startTimeUtc = $null
            $launch.status = 'EXITED'
            $launch.nativeExitCode = $native
            $launch.disposition = if ($null -eq $native) { 'EXITED_UNOBSERVED' } elseif ($native -eq 0) { 'EXITED_OK' } else { 'EXITED_NONZERO' }
            $launch.toolExitCode = if ($null -ne $native -and $native -eq 0) { 0 } else { 10 }
            $launch.toolStatus = if ($null -eq $native) { 'EXITED_UNOBSERVED' } elseif ($native -eq 0) { 'EXITED_OK' } else { 'PLAYER_NONZERO_EXIT' }
            Write-P45JsonAtomic -Path $attemptJson -Object $launch
            Write-P45JsonAtomic -Path $exitJson -Object ([pscustomobject]@{
                schemaVersion = $script:P45SchemaVersion
                kind = 'p45-e2e-exit'
                runId = $Context.Run.runId
                attempt = $attemptNumber
                disposition = $launch.disposition
                nativeExitCode = $native
                toolExitCode = $launch.toolExitCode
                requested = $false
                observedAt = (Get-P45UtcNowText)
            })
            $state.activeProcess = $null
            $state.status = 'EXITED'
            Save-P45State -Context $Context -State $state
            $Context.Run.status = if ($launch.disposition -eq 'EXITED_OK') { 'ATTEMPT_EXITED_OK' } elseif ($launch.disposition -eq 'EXITED_UNOBSERVED') { 'ATTEMPT_EXITED_UNOBSERVED' } else { 'ATTEMPT_EXITED_NONZERO' }
            $Context.Run.attemptCount = [Math]::Max([int]$Context.Run.attemptCount, $attemptNumber)
            Save-P45Run -Context $Context
            return [pscustomobject]@{
                Attempt = $attemptNumber
                AttemptDirectory = $attemptDir
                Launch = $launch
                ExitCode = $launch.toolExitCode
                Status = $launch.toolStatus
                NativeExitCode = $native
                Process = $process
            }
        }
        Throw-P45 4 'OWNERSHIP_OR_IDENTITY' 'Could not read a stable process start time for a live process.'
    }

    if ($hidden) {
        Hide-P45ProcessWindow -Process $process
    }

    $launch.pid = [int]$process.Id
    $launch.startTimeUtc = $startTime
    $launch.status = 'RUNNING'
    Write-P45JsonAtomic -Path $attemptJson -Object $launch

    $state.activeProcess = [pscustomobject]@{
        ownerRunId = [string]$Context.Run.runId
        attempt = $attemptNumber
        pid = [int]$process.Id
        startTimeUtc = $startTime
        path = $playerPath
        sha256 = $playerHash
        logFile = $logFile
        mode = $Mode
        visible = [bool]$Visible
    }
    $state.status = 'RUNNING'
    Save-P45State -Context $Context -State $state
    $Context.Run.attemptCount = [Math]::Max([int]$Context.Run.attemptCount, $attemptNumber)
    $Context.Run.status = 'RUNNING'
    $Context.Run.launch.mode = $Mode
    $Context.Run.launch.visible = [bool]$Visible
    Save-P45Run -Context $Context

    if (-not $Wait) {
        $launch.toolExitCode = 0
        $launch.toolStatus = 'RUNNING'
        Write-P45JsonAtomic -Path $attemptJson -Object $launch
        return [pscustomobject]@{
            Attempt = $attemptNumber
            AttemptDirectory = $attemptDir
            Launch = $launch
            ExitCode = 0
            Status = 'RUNNING'
            NativeExitCode = $null
            Process = $process
        }
    }

    $exited = $false
    try { $exited = $process.WaitForExit($TimeoutSeconds * 1000) } catch { $exited = $false }
    if ($exited) {
        $native = Get-P45NativeExitCode -Process $process
        $launch.status = 'EXITED'
        $launch.nativeExitCode = $native
        $launch.disposition = if ($null -eq $native) { 'EXITED_UNOBSERVED' } elseif ($native -eq 0) { 'EXITED_OK' } else { 'EXITED_NONZERO' }
        $launch.toolExitCode = if ($null -ne $native -and $native -eq 0) { 0 } else { 10 }
        $launch.toolStatus = if ($null -eq $native) { 'EXITED_UNOBSERVED' } elseif ($native -eq 0) { 'EXITED_OK' } else { 'PLAYER_NONZERO_EXIT' }
        Write-P45JsonAtomic -Path $attemptJson -Object $launch
        Write-P45JsonAtomic -Path $exitJson -Object ([pscustomobject]@{
            schemaVersion = $script:P45SchemaVersion
            kind = 'p45-e2e-exit'
            runId = $Context.Run.runId
            attempt = $attemptNumber
            disposition = $launch.disposition
            nativeExitCode = $native
            toolExitCode = $launch.toolExitCode
            requested = $false
            observedAt = (Get-P45UtcNowText)
        })
        $state.activeProcess = $null
        $state.status = 'EXITED'
        Save-P45State -Context $Context -State $state
        $Context.Run.status = if ($launch.disposition -eq 'EXITED_OK') { 'ATTEMPT_EXITED_OK' } elseif ($launch.disposition -eq 'EXITED_UNOBSERVED') { 'ATTEMPT_EXITED_UNOBSERVED' } else { 'ATTEMPT_EXITED_NONZERO' }
        Save-P45Run -Context $Context
        return [pscustomobject]@{
            Attempt = $attemptNumber
            AttemptDirectory = $attemptDir
            Launch = $launch
            ExitCode = $launch.toolExitCode
            Status = $launch.toolStatus
            NativeExitCode = $native
            Process = $process
        }
    }

    # The tool owns termination only because it just started this process and
    # re-verifies the same PID/start/path/hash before killing it.
    $identity = Get-P45ProcessByIdentity -ProcessId $process.Id -ExpectedPath $playerPath `
        -ExpectedSha256 $playerHash -ExpectedStartUtc $startTime
    $native = $null
    if ($identity.Exists) {
        $binding = Test-P45ProcessRunBinding -ProcessId $process.Id -Context $Context -Mode $Mode
        if ($binding -ne 'MATCH') {
            Throw-P45 4 'OWNERSHIP_OR_IDENTITY' ('Timeout kill refused: process binding check returned ' + $binding + '.')
        }
        try { $process.Kill() } catch { }
        try { [void]$process.WaitForExit(5000) } catch { }
        $native = Get-P45NativeExitCode -Process $process
    }
    $launch.status = 'TIMED_OUT'
    $launch.nativeExitCode = $native
    $launch.disposition = 'TIMED_OUT'
    $launch.toolExitCode = 11
    $launch.toolStatus = 'TIMEOUT'
    Write-P45JsonAtomic -Path $attemptJson -Object $launch
    Write-P45JsonAtomic -Path $exitJson -Object ([pscustomobject]@{
        schemaVersion = $script:P45SchemaVersion
        kind = 'p45-e2e-exit'
        runId = $Context.Run.runId
        attempt = $attemptNumber
        disposition = 'TIMED_OUT'
        nativeExitCode = $native
        toolExitCode = 11
        requested = $true
        killReason = 'TOOL_TIMEOUT'
        observedAt = (Get-P45UtcNowText)
    })
    $state.activeProcess = $null
    $state.status = 'TIMED_OUT'
    Save-P45State -Context $Context -State $state
    $Context.Run.status = 'ATTEMPT_TIMED_OUT'
    Save-P45Run -Context $Context
    return [pscustomobject]@{
        Attempt = $attemptNumber
        AttemptDirectory = $attemptDir
        Launch = $launch
        ExitCode = 11
        Status = 'TIMEOUT'
        NativeExitCode = $native
        Process = $process
    }
}

function Stop-P45OwnedProcess {
    param(
        [Parameter(Mandatory = $true)][object]$Context,
        [int]$GraceSeconds = 5,
        [switch]$Force
    )

    if ($GraceSeconds -lt 0 -or $GraceSeconds -gt 3600) {
        Throw-P45 2 'USAGE' 'GraceSeconds must be between 0 and 3600.'
    }

    $state = Get-P45State -Context $Context
    $active = Get-P45Property $state 'activeProcess'
    if ($null -eq $active) {
        return [pscustomobject]@{
            Status = 'ALREADY_EXITED'
            ExitCode = 0
            NativeExitCode = $null
            Disposition = 'ALREADY_EXITED'
            Attempt = $null
        }
    }

    if ([string](Get-P45Property $active 'ownerRunId') -ne [string]$Context.Run.runId) {
        Throw-P45 4 'OWNERSHIP_OR_IDENTITY' 'Active process is owned by a different run.'
    }

    $pidValue = [int](Get-P45Property $active 'pid')
    $identity = Get-P45ProcessByIdentity -ProcessId $pidValue -ExpectedPath ([string]$active.path) `
        -ExpectedSha256 ([string]$active.sha256) -ExpectedStartUtc ([string]$active.startTimeUtc)
    if (-not $identity.Exists) {
        if ($identity.Reason -ne 'PROCESS_NOT_FOUND') {
            # A live PID with a different path/start/hash is a reused or foreign
            # process. Never record it as our exit and never touch it.
            Throw-P45 4 'OWNERSHIP_OR_IDENTITY' ('Refusing to stop a process whose identity no longer matches: ' + $identity.Reason)
        }

        # The process disappeared between Launch and Stop. Record that observed
        # fact without inventing a native exit code or claiming a PASS.
        $attempt = [int]$active.attempt
        $attemptName = $attempt.ToString('0000', [Globalization.CultureInfo]::InvariantCulture)
        $attemptDir = Join-Path $Context.Root ('attempts\' + $attemptName)
        $launchPath = Join-Path $attemptDir 'launch.json'
        $exitPath = Join-Path $attemptDir 'exit.json'
        if ([IO.File]::Exists($launchPath)) {
            $launch = Read-P45Json -Path $launchPath
            $launch.status = 'EXITED'
            $launch.disposition = 'EXITED_UNOBSERVED'
            $launch.toolExitCode = 0
            $launch.toolStatus = 'EXITED_UNOBSERVED'
            Write-P45JsonAtomic -Path $launchPath -Object $launch
            if (-not [IO.File]::Exists($exitPath)) {
                Write-P45JsonAtomic -Path $exitPath -Object ([pscustomobject]@{
                    schemaVersion = $script:P45SchemaVersion
                    kind = 'p45-e2e-exit'
                    runId = $Context.Run.runId
                    attempt = $attempt
                    disposition = 'EXITED_UNOBSERVED'
                    nativeExitCode = $null
                    toolExitCode = 0
                    requested = $false
                    killReason = $null
                    observedAt = (Get-P45UtcNowText)
                })
            }
        }
        $state.activeProcess = $null
        $state.status = 'STOPPED'
        Save-P45State -Context $Context -State $state
        return [pscustomobject]@{
            Status = 'ALREADY_EXITED'
            ExitCode = 0
            NativeExitCode = $null
            Disposition = 'EXITED_UNOBSERVED'
            Attempt = $attempt
            Reason = $identity.Reason
        }
    }

    $process = $identity.Process
    $binding = Test-P45ProcessRunBinding -ProcessId $pidValue -Context $Context -Mode ([string]$active.mode)
    if ($binding -ne 'MATCH') {
        Throw-P45 4 'OWNERSHIP_OR_IDENTITY' ('Refusing to stop: process binding check returned ' + $binding + '.')
    }
    $attempt = [int]$active.attempt
    $attemptName = $attempt.ToString('0000', [Globalization.CultureInfo]::InvariantCulture)
    $attemptDir = Join-Path $Context.Root ('attempts\' + $attemptName)
    $launchPath = Join-Path $attemptDir 'launch.json'
    $exitPath = Join-Path $attemptDir 'exit.json'
    if (-not [IO.File]::Exists($launchPath)) {
        Throw-P45 4 'OWNERSHIP_OR_IDENTITY' 'The active attempt has no launch record; refusing to terminate it.'
    }

    $disposition = 'GRACEFUL_STOPPED'
    $killReason = $null
    if ($Force) {
        $verifyAgain = Get-P45ProcessByIdentity -ProcessId $pidValue -ExpectedPath ([string]$active.path) `
            -ExpectedSha256 ([string]$active.sha256) -ExpectedStartUtc ([string]$active.startTimeUtc)
        if (-not $verifyAgain.Exists) {
            Throw-P45 4 'OWNERSHIP_OR_IDENTITY' ('Refusing to kill after identity re-check failed: ' + $verifyAgain.Reason)
        }
        $bindingAgain = Test-P45ProcessRunBinding -ProcessId $pidValue -Context $Context -Mode ([string]$active.mode)
        if ($bindingAgain -ne 'MATCH') {
            Throw-P45 4 'OWNERSHIP_OR_IDENTITY' ('Refusing to kill after binding re-check: ' + $bindingAgain)
        }
        try { $process.Kill() } catch {
            Throw-P45 13 'STOP_FAILED' ('Could not kill the owned process: ' + (Get-P45ErrorMessage $_))
        }
        try { [void]$process.WaitForExit(5000) } catch { }
        $disposition = 'FORCE_KILLED'
        $killReason = 'TOOL_FORCE'
    }
    else {
        $stopRequest = Join-Path $Context.Root 'control\stop.request'
        Write-P45TextAtomic -Path $stopRequest -Text ((Get-P45UtcNowText) + [Environment]::NewLine)
        try { [void]$process.CloseMainWindow() } catch { }
        $exited = $false
        try { $exited = $process.WaitForExit([Math]::Max(0, $GraceSeconds) * 1000) } catch { $exited = $false }
        if (-not $exited) {
            $state.status = 'STILL_RUNNING'
            Save-P45State -Context $Context -State $state
            Throw-P45 13 'STOP_FAILED' ('Process did not exit within ' + $GraceSeconds + 's; refusing to kill without -Force.')
        }
    }

    $native = Get-P45NativeExitCode -Process $process
    $launch = Read-P45Json -Path $launchPath
    $launch.status = 'STOPPED'
    $launch.nativeExitCode = $native
    $launch.disposition = $disposition
    $launch.toolExitCode = 0
    $launch.toolStatus = $disposition
    Write-P45JsonAtomic -Path $launchPath -Object $launch
    Write-P45JsonAtomic -Path $exitPath -Object ([pscustomobject]@{
        schemaVersion = $script:P45SchemaVersion
        kind = 'p45-e2e-exit'
        runId = $Context.Run.runId
        attempt = $attempt
        disposition = $disposition
        nativeExitCode = $native
        toolExitCode = 0
        requested = $true
        killReason = $killReason
        observedAt = (Get-P45UtcNowText)
    })
    $state.activeProcess = $null
    $state.status = 'STOPPED'
    Save-P45State -Context $Context -State $state
    $Context.Run.status = 'STOPPED'
    Save-P45Run -Context $Context

    [pscustomobject]@{
        Status = $disposition
        ExitCode = 0
        NativeExitCode = $native
        Disposition = $disposition
        Attempt = $attempt
        KillReason = $killReason
    }
}

function Test-P45SensitiveText {
    param([AllowEmptyString()][string]$Text)

    if ([string]::IsNullOrEmpty($Text)) { return $false }
    $keys = $script:P45SensitiveKeyAlternation
    $keyPattern = '(?:"(?:' + $keys + ')"|''(?:' + $keys + ')''|\b(?:' + $keys + ')\b)'
    if ([Text.RegularExpressions.Regex]::IsMatch($Text, ('(?is)' + $keyPattern + '\s*[:=]\s*\S'))) { return $true }
    if ([Text.RegularExpressions.Regex]::IsMatch($Text, '(?i)\bBearer\s+\S')) { return $true }
    return $false
}

function Get-P45JsonSensitivity {
    param([object]$Node, [int]$Depth = 0)

    $state = [pscustomobject]@{
        HasSensitive = $false
        HasNested = $false
        HasUnredacted = $false
    }
    Add-P45JsonSensitivityState -Node $Node -State $state -Depth $Depth
    return $state
}

function Add-P45JsonSensitivityState {
    param([object]$Node, [object]$State, [int]$Depth = 0)

    if ($null -eq $Node) { return }
    $keyRegex = '(?i)^(?:' + $script:P45SensitiveKeyAlternation + ')$'

    if ($Node -is [string]) {
        if ($Depth -gt 8) {
            $State.HasNested = $true
            return
        }
        $text = [string]$Node
        $inner = Get-P45EmbeddedJsonRedaction -Text $text -Depth ($Depth + 1)
        if (@($inner.Problems).Count -gt 0) {
            $State.HasNested = $true
            return
        }
        if (-not [string]::Equals([string]$inner.Text, $text, [StringComparison]::Ordinal)) {
            $State.HasUnredacted = $true
            return
        }
        if (Test-P45SensitiveText $text) {
            $State.HasUnredacted = $true
            return
        }
        try {
            $decoded = [Text.RegularExpressions.Regex]::Unescape($text)
        }
        catch {
            $decoded = $text
        }
        if (-not [string]::Equals($decoded, $text, [StringComparison]::Ordinal)) {
            $innerDecoded = Get-P45EmbeddedJsonRedaction -Text $decoded -Depth ($Depth + 1)
            if (@($innerDecoded.Problems).Count -gt 0) {
                $State.HasNested = $true
            }
            elseif (-not [string]::Equals([string]$innerDecoded.Text, $decoded, [StringComparison]::Ordinal)) {
                $State.HasUnredacted = $true
            }
            elseif (Test-P45SensitiveText $decoded) {
                $State.HasUnredacted = $true
            }
        }
        return
    }

    if ($Node -is [System.Collections.IEnumerable] -and
        -not ($Node -is [System.Collections.IDictionary]) -and
        -not ($Node -is [pscustomobject])) {
        foreach ($item in $Node) {
            Add-P45JsonSensitivityState -Node $item -State $State -Depth ($Depth + 1)
        }
        return
    }

    if ($Node -is [System.Collections.IDictionary]) {
        foreach ($key in $Node.Keys) {
            $name = [string]$key
            $value = $Node[$key]
            if ($name -match $keyRegex) {
                $State.HasSensitive = $true
                if ($value -is [pscustomobject] -or
                    ($value -is [System.Collections.IEnumerable] -and -not ($value -is [string]) -and $null -ne $value)) {
                    $State.HasNested = $true
                }
                elseif ($null -ne $value -and -not ($value -is [string] -and $value -eq '<REDACTED>')) {
                    $State.HasUnredacted = $true
                }
            }
            Add-P45JsonSensitivityState -Node $value -State $State -Depth ($Depth + 1)
        }
        return
    }

    if ($Node -is [pscustomobject]) {
        foreach ($property in $Node.PSObject.Properties) {
            $name = [string]$property.Name
            $value = $property.Value
            if ($name -match $keyRegex) {
                $State.HasSensitive = $true
                if ($value -is [pscustomobject] -or
                    ($value -is [System.Collections.IEnumerable] -and -not ($value -is [string]) -and $null -ne $value)) {
                    $State.HasNested = $true
                }
                elseif ($null -ne $value -and -not ($value -is [string] -and $value -eq '<REDACTED>')) {
                    $State.HasUnredacted = $true
                }
            }
            if ($value -is [string] -or $value -is [pscustomobject] -or
                ($value -is [System.Collections.IEnumerable] -and -not ($value -is [string]) -and $null -ne $value)) {
                Add-P45JsonSensitivityState -Node $value -State $State -Depth ($Depth + 1)
            }
        }
    }
}

function Get-P45EmbeddedJsonRedaction {
    param([AllowEmptyString()][string]$Text, [int]$Depth = 0)

    $builder = New-Object System.Text.StringBuilder
    $problems = New-Object System.Collections.Generic.List[string]
    if ($Depth -gt 8) {
        return [pscustomobject]@{
            Text = $Text
            Problems = @('nested JSON depth exceeded; refusing to publish')
        }
    }
    if ($null -eq $Text -or $Text.Length -eq 0) {
        return [pscustomobject]@{ Text = ''; Problems = @() }
    }

    $i = 0
    while ($i -lt $Text.Length) {
        $char = $Text[$i]
        if ($char -ne '{' -and $char -ne '[') {
            [void]$builder.Append($char)
            $i++
            continue
        }

        $next = $i + 1
        while ($next -lt $Text.Length -and [char]::IsWhiteSpace($Text[$next])) { $next++ }
        $candidatePossible = $false
        if ($next -lt $Text.Length) {
            $nextChar = $Text[$next]
            if ($char -eq '{') {
                $candidatePossible = ($nextChar -eq '"' -or $nextChar -eq '}' -or $nextChar -eq '\')
            }
            else {
                $candidatePossible = ($nextChar -eq '{' -or $nextChar -eq '[' -or $nextChar -eq '"' -or
                    $nextChar -eq ']' -or $nextChar -eq 't' -or $nextChar -eq 'f' -or $nextChar -eq 'n' -or
                    $nextChar -eq '-' -or [char]::IsDigit($nextChar))
            }
        }
        if (-not $candidatePossible) {
            [void]$builder.Append($char)
            $i++
            continue
        }

        $depth = 0
        $j = $i
        $inString = $false
        $escape = $false
        $closed = $false
        while ($j -lt $Text.Length) {
            $current = $Text[$j]
            if ($inString) {
                if ($escape) { $escape = $false }
                elseif ($current -eq '\') { $escape = $true }
                elseif ($current -eq '"') { $inString = $false }
            }
            else {
                if ($current -eq '"') { $inString = $true }
                elseif ($current -eq '{' -or $current -eq '[') { $depth++ }
                elseif ($current -eq '}' -or $current -eq ']') {
                    $depth--
                    if ($depth -eq 0) { $closed = $true; break }
                }
            }
            $j++
        }

        if (-not $closed) {
            $problems.Add('unterminated JSON-like value at ' + $i) | Out-Null
            [void]$builder.Append($Text.Substring($i))
            break
        }

        $candidate = $Text.Substring($i, $j - $i + 1)
        try {
            $node = $candidate | ConvertFrom-Json
        }
        catch {
            $problems.Add('JSON-like value at ' + $i + ' is not parseable') | Out-Null
            [void]$builder.Append($candidate)
            $i = $j + 1
            continue
        }

        $sensitivity = Get-P45JsonSensitivity -Node $node -Depth $Depth
        if ($sensitivity.HasNested) {
            $problems.Add('nested JSON/array under a sensitive key at ' + $i) | Out-Null
            [void]$builder.Append($candidate)
        }
        elseif ($sensitivity.HasUnredacted) {
            # Covers escaped keys such as "\u0074icket", embedded JSON after a
            # log prefix, and valid JSON whose key the plain regex cannot see.
            # Remove the whole value rather than trusting a partial field match.
            [void]$builder.Append('<REDACTED-JSON>')
        }
        else {
            [void]$builder.Append($candidate)
        }
        $i = $j + 1
    }

    [pscustomobject]@{
        Text = $builder.ToString()
        Problems = @($problems.ToArray())
    }
}

function Get-P45RedactedLog {
    param([AllowEmptyString()][string]$Text)

    if ($null -eq $Text -or $Text.Length -eq 0) { return '' }
    $keys = $script:P45SensitiveKeyAlternation
    $keyPattern = '(?:"(?:' + $keys + ')"|''(?:' + $keys + ')''|\b(?:' + $keys + ')\b)'
    $quotedValue = '(?<vquote>["''])(?:\\.|(?!\k<vquote>).)*\k<vquote>'
    $redacted = [Text.RegularExpressions.Regex]::Replace(
        $Text,
        ('(?is)(?<prefix>' + $keyPattern + '\s*[:=]\s*)' + $quotedValue),
        '${prefix}${vquote}<REDACTED>${vquote}')
    # For unquoted values (headers, key=value, URL query) redact the complete
    # remainder of the line. Quoted values were already handled above.
    $redacted = [Text.RegularExpressions.Regex]::Replace(
        $redacted,
        ('(?im)(?<prefix>' + $keyPattern + '\s*[:=]\s*)(?!["''])[^\r\n]+'),
        '${prefix}<REDACTED>')
    $redacted = [Text.RegularExpressions.Regex]::Replace(
        $redacted,
        '(?i)(?<prefix>\bBearer\s+)(?!<REDACTED>)[A-Za-z0-9._~+/=-]+',
        '${prefix}<REDACTED>')
    # Detect JSON values anywhere in the line, including log-prefixed JSON and
    # escaped object keys that the plain regex cannot match. Sensitive scalar
    # JSON is replaced as a whole; nested JSON remains a hard unsafe case.
    $embedded = Get-P45EmbeddedJsonRedaction -Text $redacted
    return $embedded.Text
}

function Find-P45SensitiveJsonNode {
    param(
        [object]$Node,
        [string]$Path,
        [object]$Reasons
    )

    if ($null -eq $Node) { return }
    $keyRegex = '(?i)^(?:' + $script:P45SensitiveKeyAlternation + ')$'

    if ($Node -is [System.Collections.IEnumerable] -and
        -not ($Node -is [string]) -and
        -not ($Node -is [System.Collections.IDictionary]) -and
        -not ($Node -is [pscustomobject])) {
        $index = 0
        foreach ($item in $Node) {
            Find-P45SensitiveJsonNode -Node $item -Path ($Path + '[' + $index + ']') -Reasons $Reasons
            $index++
        }
        return
    }

    if ($Node -is [System.Collections.IDictionary]) {
        foreach ($key in $Node.Keys) {
            $value = $Node[$key]
            $name = [string]$key
            if ($name -match $keyRegex) {
                if ($null -ne $value -and -not ($value -is [string] -and $value -eq '<REDACTED>')) {
                    $Reasons.Add('sensitive JSON key is not exactly redacted: ' + $Path + '.' + $name) | Out-Null
                }
            }
            Find-P45SensitiveJsonNode -Node $value -Path ($Path + '.' + $name) -Reasons $Reasons
        }
        return
    }

    if ($Node -is [pscustomobject]) {
        foreach ($property in $Node.PSObject.Properties) {
            $name = [string]$property.Name
            $value = $property.Value
            if ($name -match $keyRegex) {
                if ($value -is [pscustomobject] -or
                    ($value -is [System.Collections.IEnumerable] -and -not ($value -is [string]) -and $null -ne $value)) {
                    $Reasons.Add('nested JSON under sensitive key is not publishable: ' + $Path + '.' + $name) | Out-Null
                }
                elseif ($null -ne $value -and -not ($value -is [string] -and $value -eq '<REDACTED>')) {
                    $Reasons.Add('sensitive JSON key is not exactly redacted: ' + $Path + '.' + $name) | Out-Null
                }
            }
            if ($value -is [pscustomobject] -or
                ($value -is [System.Collections.IEnumerable] -and -not ($value -is [string]) -and $null -ne $value)) {
                Find-P45SensitiveJsonNode -Node $value -Path ($Path + '.' + $name) -Reasons $Reasons
            }
        }
    }
}

function Get-P45LogRedactionAssessment {
    param([AllowEmptyString()][string]$Text)

    $redacted = Get-P45RedactedLog -Text $Text
    $reasons = New-Object System.Collections.Generic.List[string]

    if ($Text.IndexOf([char]0) -ge 0) {
        $reasons.Add('log contains NUL/binary content and cannot be published') | Out-Null
    }

    $embedded = Get-P45EmbeddedJsonRedaction -Text $redacted
    foreach ($problem in @($embedded.Problems)) {
        $reasons.Add($problem) | Out-Null
    }
    $redacted = $embedded.Text

    $keys = $script:P45SensitiveKeyAlternation
    $keyPattern = '(?:"(?:' + $keys + ')"|''(?:' + $keys + ')''|\b(?:' + $keys + ')\b)'
    $residual = '(?is)(?<prefix>' + $keyPattern + '\s*[:=]\s*)(?<value>"(?:[^"\\]|\\.)*"|''(?:[^''\\]|\\.)*''|[^\s,;}\]\)]+)'
    foreach ($match in [Text.RegularExpressions.Regex]::Matches($redacted, $residual)) {
        $value = $match.Groups['value'].Value.Trim()
        $safeValues = @('<REDACTED>', '"<REDACTED>"', "'<REDACTED>'", '""', "''", 'null', 'NULL')
        if ($safeValues -notcontains $value) {
            $reasons.Add('sensitive key still has a non-redacted value at ' + $match.Index) | Out-Null
        }
        if ($value.StartsWith('{') -or $value.StartsWith('[')) {
            $reasons.Add('sensitive key has nested JSON/array at ' + $match.Index) | Out-Null
        }
    }

    if ([Text.RegularExpressions.Regex]::IsMatch($redacted, '(?i)\bBearer\s+(?!<REDACTED>)[^\s,;}\]\)]+')) {
        $reasons.Add('unredacted Bearer value remains') | Out-Null
    }

    foreach ($line in ($redacted -split "\r?\n")) {
        $trimmed = $line.Trim()
        $jsonLike = $trimmed.StartsWith('{')
        if (-not $jsonLike -and $trimmed.StartsWith('[')) {
            # Do not mistake Unity log prefixes such as "[Physics::Module]"
            # for JSON arrays. Only array-looking value starts are parsed.
            $jsonLike = ($trimmed -match '^\[\s*(?:\{|\[|"|''|true\b|false\b|null\b|-?\d|$)')
        }
        if (-not $jsonLike) { continue }
        try {
            $node = $trimmed | ConvertFrom-Json
        }
        catch {
            $reasons.Add('JSON-like log line is not parseable after redaction') | Out-Null
            continue
        }
        Find-P45SensitiveJsonNode -Node $node -Path '$' -Reasons $reasons
    }

    [pscustomobject]@{
        Policy = $script:P45RedactionPolicy
        Text = $redacted
        Safe = ($reasons.Count -eq 0)
        Reasons = @($reasons.ToArray() | Select-Object -Unique)
    }
}

function Copy-P45EvidenceFile {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$Kind,
        [Parameter(Mandatory = $true)][string]$Sensitivity,
        [Parameter(Mandatory = $true)][string]$CollectionDirectory,
        [int]$Attempt = 0,
        [string]$SourceRelative = '',
        [string]$RedactedFrom = ''
    )

    $fullSource = Get-P45FullPath -Path $Source
    if (-not [IO.File]::Exists($fullSource)) {
        Throw-P45 5 'NOT_FOUND' ('Evidence source disappeared: ' + $fullSource)
    }
    $fullDestination = Get-P45FullPath -Path $Destination
    [IO.Directory]::CreateDirectory((Split-Path -Parent $fullDestination)) | Out-Null
    [IO.File]::Copy($fullSource, $fullDestination, $false)
    $record = Get-P45FileRecord -Path $fullDestination
    [pscustomobject]@{
        artifactPath = (Get-P45RelativePath -BaseDirectory $CollectionDirectory -Path $fullDestination)
        sourcePath = $SourceRelative
        kind = $Kind
        attempt = $Attempt
        bytes = $record.bytes
        sha256 = $record.sha256
        sensitivity = $Sensitivity
        redactedFrom = $RedactedFrom
    }
}

function New-P45Collection {
    param([Parameter(Mandatory = $true)][object]$Context)

    $state = Get-P45State -Context $Context
    if ($null -ne (Get-P45Property $state 'activeProcess')) {
        Throw-P45 13 'STOP_FAILED' 'Collect refuses to archive a live run. Stop the owned process first so evidence bytes are stable.'
    }

    $attempts = @(Get-P45AttemptDirectories -Context $Context)
    $observationRoot = Join-Path $Context.Root 'observations'
    $observationFiles = @(Get-ChildItem -LiteralPath $observationRoot -Recurse -File -Filter '*.json' -ErrorAction SilentlyContinue)
    if ($attempts.Count -eq 0 -and $observationFiles.Count -eq 0) {
        Throw-P45 14 'EVIDENCE_INVALID' 'No attempt and no imported observation exists; status remains NOT_RUN instead of collecting an empty manifest.'
    }

    $collectionsRoot = Join-Path $Context.Root 'evidence\collections'
    [IO.Directory]::CreateDirectory($collectionsRoot) | Out-Null
    $existing = @(Get-ChildItem -LiteralPath $collectionsRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d{4}$' })
    $next = 1
    if ($existing.Count -gt 0) {
        $next = (($existing | ForEach-Object { [int]$_.Name } | Measure-Object -Maximum).Maximum + 1)
    }
    $collectionName = ([int]$next).ToString('0000', [Globalization.CultureInfo]::InvariantCulture)
    $collectionDir = Join-Path $collectionsRoot $collectionName
    [IO.Directory]::CreateDirectory($collectionDir) | Out-Null
    $manifestPath = Join-Path $collectionDir 'manifest.json'
    if ([IO.File]::Exists($manifestPath)) {
        Throw-P45 3 'RUN_EXISTS' 'Refusing to overwrite an existing evidence manifest.'
    }

    $entries = New-Object System.Collections.Generic.List[object]
    $notes = New-Object System.Collections.Generic.List[string]

    foreach ($attemptDir in $attempts) {
        $attemptNumber = [int]$attemptDir.Name
        foreach ($file in @(Get-ChildItem -LiteralPath $attemptDir.FullName -File -ErrorAction SilentlyContinue)) {
            $sensitivity = if ($file.Name -in @('stdout.log', 'stderr.log')) { 'private-do-not-publish' } else { 'public' }
            $dest = Join-Path $collectionDir ('attempts\' + $attemptDir.Name + '\' + $file.Name)
            $entries.Add((Copy-P45EvidenceFile -Source $file.FullName -Destination $dest -Kind 'attempt' `
                -Sensitivity $sensitivity -CollectionDirectory $collectionDir -Attempt $attemptNumber `
                -SourceRelative (Get-P45RelativePath $Context.Root $file.FullName))) | Out-Null
        }
    }

    $statePath = Join-Path $Context.Root 'state.json'
    if ([IO.File]::Exists($statePath)) {
        $entries.Add((Copy-P45EvidenceFile -Source $statePath -Destination (Join-Path $collectionDir 'attempts\state-snapshot.json') `
            -Kind 'state' -Sensitivity 'public' -CollectionDirectory $collectionDir -SourceRelative 'state.json')) | Out-Null
    }

    $logRoot = Join-Path $Context.Root 'log'
    if ([IO.Directory]::Exists($logRoot)) {
        foreach ($file in @(Get-ChildItem -LiteralPath $logRoot -Recurse -File -ErrorAction SilentlyContinue)) {
            $relative = Get-P45RelativePath $logRoot $file.FullName
            $rawDest = Join-Path $collectionDir ('private\logs\' + $relative)
            $entries.Add((Copy-P45EvidenceFile -Source $file.FullName -Destination $rawDest -Kind 'log-raw' `
                -Sensitivity 'private-do-not-publish' -CollectionDirectory $collectionDir `
                -SourceRelative (Get-P45RelativePath $Context.Root $file.FullName))) | Out-Null
            $text = [IO.File]::ReadAllText($file.FullName, $script:P45Utf8)
            $assessment = Get-P45LogRedactionAssessment -Text $text
            if ($assessment.Safe) {
                $publicDest = Join-Path $collectionDir ('logs\' + $relative)
                [IO.Directory]::CreateDirectory((Split-Path -Parent $publicDest)) | Out-Null
                [IO.File]::WriteAllText($publicDest, $assessment.Text, $script:P45Utf8)
                $publicRecord = Get-P45FileRecord -Path $publicDest
                $entries.Add([pscustomobject]@{
                    artifactPath = (Get-P45RelativePath -BaseDirectory $collectionDir -Path $publicDest)
                    sourcePath = (Get-P45RelativePath $Context.Root $file.FullName)
                    kind = 'log-redacted'
                    attempt = 0
                    bytes = $publicRecord.bytes
                    sha256 = $publicRecord.sha256
                    sensitivity = 'public'
                    redactedFrom = (Get-P45RelativePath $collectionDir $rawDest)
                }) | Out-Null
            }
            else {
                $notes.Add('Public redacted copy withheld for ' + $relative + '; ' + ($assessment.Reasons -join '; ')) | Out-Null
            }
        }
    }

    $screenshotRoot = Join-Path $Context.Root 'screenshots'
    $screenshotHashes = New-Object System.Collections.Generic.List[string]
    if ([IO.Directory]::Exists($screenshotRoot)) {
        foreach ($file in @(Get-ChildItem -LiteralPath $screenshotRoot -Recurse -File -ErrorAction SilentlyContinue |
                Where-Object { $_.Extension -ieq '.png' })) {
            $relative = Get-P45RelativePath $screenshotRoot $file.FullName
            $dest = Join-Path $collectionDir ('screenshots\' + $relative)
            $entry = Copy-P45EvidenceFile -Source $file.FullName -Destination $dest -Kind 'screenshot' `
                -Sensitivity 'public' -CollectionDirectory $collectionDir `
                -SourceRelative (Get-P45RelativePath $Context.Root $file.FullName)
            $entries.Add($entry) | Out-Null
            $screenshotHashes.Add([string]$entry.sha256) | Out-Null
        }
    }
    if ($screenshotHashes.Count -gt 1) {
        $uniqueScreenshots = @($screenshotHashes | Sort-Object -Unique)
        if ($uniqueScreenshots.Count -lt $screenshotHashes.Count) {
            $notes.Add('Screenshots are not distinct (hash duplicates). Treat them as transport/archival evidence only, not visual UI proof; hidden windows can produce blank frames.')
        }
    }

    $saveRoot = [string]$Context.Run.paths.save
    $normalizedDir = Join-Path $collectionDir 'normalized'
    if ([IO.Directory]::Exists($saveRoot)) {
        foreach ($file in @(Get-ChildItem -LiteralPath $saveRoot -Recurse -File -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -notlike '*.lock' -and $_.Name -notlike '*.tmp.*' })) {
            $relative = Get-P45RelativePath $saveRoot $file.FullName
            $rawDest = Join-Path $collectionDir ('private\save\' + $relative)
            $entries.Add((Copy-P45EvidenceFile -Source $file.FullName -Destination $rawDest -Kind 'save-raw' `
                -Sensitivity 'private-do-not-publish' -CollectionDirectory $collectionDir `
                -SourceRelative (Get-P45RelativePath $Context.Root $file.FullName))) | Out-Null

            if ($file.Extension -ieq '.save' -or $file.Name -like '*.save.prev') {
                $header = Get-P45SaveHeader -Path $file.FullName
                $observation = New-P45SaveObservation -Context $Context -Header $header -SourceFile $file.FullName
                $normalizedPath = Join-Path $normalizedDir ($relative + '.observation.json')
                Write-P45JsonAtomic -Path $normalizedPath -Object $observation
                $normalizedRecord = Get-P45FileRecord -Path $normalizedPath
                $entries.Add([pscustomobject]@{
                    artifactPath = (Get-P45RelativePath -BaseDirectory $collectionDir -Path $normalizedPath)
                    sourcePath = (Get-P45RelativePath $Context.Root $file.FullName)
                    kind = 'save-header-observation'
                    attempt = 0
                    bytes = $normalizedRecord.bytes
                    sha256 = $normalizedRecord.sha256
                    sensitivity = 'public'
                    redactedFrom = ''
                }) | Out-Null
                if (-not $header.valid) {
                    $notes.Add('Save header parse failed for ' + $relative + ': ' + $header.error)
                }
            }
        }
    }

    if ([IO.Directory]::Exists($observationRoot)) {
        foreach ($file in @($observationFiles)) {
            $relative = Get-P45RelativePath $observationRoot $file.FullName
            $dest = Join-Path $collectionDir ('observations\' + $relative)
            $entries.Add((Copy-P45EvidenceFile -Source $file.FullName -Destination $dest -Kind 'observation' `
                -Sensitivity 'public' -CollectionDirectory $collectionDir `
                -SourceRelative (Get-P45RelativePath $Context.Root $file.FullName))) | Out-Null
        }
    }

    if ($entries.Count -eq 0) {
        Throw-P45 14 'EVIDENCE_INVALID' 'No evidence files were produced; status remains NOT_RUN instead of writing an empty PASS manifest.'
    }

    $manifest = [pscustomobject]@{
        schemaVersion = $script:P45SchemaVersion
        kind = 'p45-e2e-evidence-manifest'
        manifestId = 'p45-manifest-' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
        runId = [string]$Context.Run.runId
        evidenceClass = [string]$Context.Run.evidenceClass
        collection = $collectionName
        generatedAt = (Get-P45UtcNowText)
        status = if ($Context.Run.evidenceClass -eq 'TOOL_SELF_TEST') {
            'TOOL_SELF_TEST_EVIDENCE_COLLECTED'
        }
        elseif ($attempts.Count -gt 0) {
            'GAME_EVIDENCE_COLLECTED_PARTIAL'
        }
        else {
            'NOT_RUN'
        }
        redactionPolicy = $script:P45RedactionPolicy
        entries = @($entries.ToArray())
        counts = [pscustomobject]@{
            artifacts = $entries.Count
            attempts = $attempts.Count
            publicArtifacts = @($entries | Where-Object { $_.sensitivity -eq 'public' }).Count
            privateArtifacts = @($entries | Where-Object { $_.sensitivity -ne 'public' }).Count
        }
        notes = @($notes.ToArray())
        exclusions = @('manifest.json', 'manifest.sha256.txt', '*.tmp.*', '*.lock', 'run.json (not archived; contains no raw log bytes)')
    }

    # Never include this manifest or its sidecar in its own entries.
    $manifest.entries = @($manifest.entries | Where-Object {
        $_.artifactPath -notin @('manifest.json', 'manifest.sha256.txt')
    })
    Write-P45JsonAtomic -Path $manifestPath -Object $manifest
    $manifestHash = Get-P45Sha256 -Path $manifestPath
    Write-P45TextAtomic -Path (Join-Path $collectionDir 'manifest.sha256.txt') -Text ($manifestHash + '  manifest.json' + [Environment]::NewLine)

    $collectionRecord = [pscustomobject]@{
        collection = $collectionName
        manifest = $manifestPath
        manifestSha256 = $manifestHash
        manifestEntries = @($manifest.entries).Count
        generatedAt = $manifest.generatedAt
        status = $manifest.status
    }
    $collections = @($Context.Run.collections)
    $collections += $collectionRecord
    $Context.Run | Add-Member -NotePropertyName collections -NotePropertyValue $collections -Force
    Save-P45Run -Context $Context

    [pscustomobject]@{
        Collection = $collectionName
        Directory = $collectionDir
        ManifestPath = $manifestPath
        ManifestSha256 = $manifestHash
        EntryCount = @($manifest.entries).Count
        Status = $manifest.status
    }
}

function Test-P45Manifest {
    param([Parameter(Mandatory = $true)][string]$ManifestPath)

    $full = Get-P45FullPath -Path $ManifestPath
    if (-not [IO.File]::Exists($full)) {
        Throw-P45 5 'NOT_FOUND' ('Manifest not found: ' + $full)
    }
    $manifest = Read-P45Json -Path $full
    if ((Get-P45Property $manifest 'kind') -ne 'p45-e2e-evidence-manifest') {
        Throw-P45 15 'MANIFEST_INVALID' 'Not a P4.5 evidence manifest.'
    }
    $collectionDir = Split-Path -Parent $full
    $sidecarPath = Join-Path $collectionDir 'manifest.sha256.txt'
    if (-not [IO.File]::Exists($sidecarPath)) {
        Throw-P45 15 'MANIFEST_INVALID' 'manifest.sha256.txt is missing; manifest hash cannot be independently checked.'
    }
    $sidecar = ([IO.File]::ReadAllText($sidecarPath, $script:P45Utf8)).Trim()
    $expected = ($sidecar -split '\s+')[0]
    $actual = Get-P45Sha256 -Path $full
    if (-not [string]::Equals($expected, $actual, [StringComparison]::OrdinalIgnoreCase)) {
        Throw-P45 15 'MANIFEST_INVALID' ('Manifest sidecar mismatch: expected ' + $expected + ', actual ' + $actual)
    }

    $seen = @{}
    $count = 0
    $entriesProperty = $manifest.PSObject.Properties['entries']
    if ($null -eq $entriesProperty -or $null -eq $entriesProperty.Value) {
        Throw-P45 15 'MANIFEST_INVALID' 'Manifest has no entries array.'
    }
    foreach ($entry in @($entriesProperty.Value)) {
        $relative = [string](Get-P45Property $entry 'artifactPath')
        if ([string]::IsNullOrWhiteSpace($relative) -or
            $relative -in @('manifest.json', 'manifest.sha256.txt') -or
            [IO.Path]::IsPathRooted($relative)) {
            Throw-P45 15 'MANIFEST_INVALID' ('Illegal or self-referencing artifact path: ' + $relative)
        }
        if ($seen.ContainsKey($relative)) {
            Throw-P45 15 'MANIFEST_INVALID' ('Duplicate artifact path: ' + $relative)
        }
        $seen[$relative] = $true
        $artifact = Get-P45FullPath -Path (Join-Path $collectionDir $relative)
        if (-not $artifact.StartsWith($collectionDir.TrimEnd('\', '/') + '\', [StringComparison]::OrdinalIgnoreCase)) {
            Throw-P45 15 'MANIFEST_INVALID' ('Artifact escapes the collection: ' + $relative)
        }
        if (-not [IO.File]::Exists($artifact)) {
            Throw-P45 15 'MANIFEST_INVALID' ('Artifact is missing: ' + $relative)
        }
        $size = (Get-Item -LiteralPath $artifact).Length
        if ($size -ne [long](Get-P45Property $entry 'bytes')) {
            Throw-P45 15 'MANIFEST_INVALID' ('Artifact size mismatch: ' + $relative)
        }
        $hash = Get-P45Sha256 -Path $artifact
        if (-not [string]::Equals($hash, [string](Get-P45Property $entry 'sha256'), [StringComparison]::OrdinalIgnoreCase)) {
            Throw-P45 15 'MANIFEST_INVALID' ('Artifact SHA-256 mismatch: ' + $relative)
        }
        $count++
    }

    [pscustomobject]@{
        ManifestPath = $full
        ManifestSha256 = $actual
        EntryCount = $count
        Status = [string](Get-P45Property $manifest 'status')
        Collection = [string](Get-P45Property $manifest 'collection')
    }
}

function Read-P45SevenBitInt {
    param(
        [Parameter(Mandatory = $true)][byte[]]$Bytes,
        [Parameter(Mandatory = $true)][ref]$Offset
    )

    $result = 0
    $shift = 0
    for ($i = 0; $i -lt 5; $i++) {
        if ($Offset.Value -ge $Bytes.Length) { throw 'Unexpected end of string length.' }
        $b = [int]$Bytes[$Offset.Value]
        $Offset.Value++
        $result = $result -bor (($b -band 0x7F) -shl $shift)
        if (($b -band 0x80) -eq 0) { return $result }
        $shift += 7
    }
    throw 'Invalid 7-bit encoded string length.'
}

function Read-P45SaveString {
    param(
        [Parameter(Mandatory = $true)][byte[]]$Bytes,
        [Parameter(Mandatory = $true)][ref]$Offset
    )

    # LocalSaveCodec.WriteString writes a little-endian Int32 byte length, not
    # BinaryWriter's 7-bit string prefix.
    $length = Read-P45SaveInt32 -Bytes $Bytes -Offset $Offset
    if ($length -lt 0 -or $length -gt 1048576 -or ($Offset.Value + $length) -gt $Bytes.Length) {
        throw 'Save string length is out of bounds.'
    }
    $value = $script:P45Utf8.GetString($Bytes, $Offset.Value, $length)
    $Offset.Value += $length
    return $value
}

function Read-P45SaveInt32 {
    param([byte[]]$Bytes, [ref]$Offset)
    if (($Offset.Value + 4) -gt $Bytes.Length) { throw 'Save is truncated.' }
    $value = [BitConverter]::ToInt32($Bytes, $Offset.Value)
    $Offset.Value += 4
    return $value
}

function Read-P45SaveInt64 {
    param([byte[]]$Bytes, [ref]$Offset)
    if (($Offset.Value + 8) -gt $Bytes.Length) { throw 'Save is truncated.' }
    $value = [BitConverter]::ToInt64($Bytes, $Offset.Value)
    $Offset.Value += 8
    return $value
}

function Read-P45SaveUInt64 {
    param([byte[]]$Bytes, [ref]$Offset)
    if (($Offset.Value + 8) -gt $Bytes.Length) { throw 'Save is truncated.' }
    $value = [BitConverter]::ToUInt64($Bytes, $Offset.Value)
    $Offset.Value += 8
    return $value
}

function Read-P45SaveBool {
    param([byte[]]$Bytes, [ref]$Offset)
    if ($Offset.Value -ge $Bytes.Length) { throw 'Save is truncated.' }
    $value = ($Bytes[$Offset.Value] -ne 0)
    $Offset.Value++
    return $value
}

function Get-P45SaveHeader {
    param([Parameter(Mandatory = $true)][string]$Path)

    try {
        $full = Get-P45FullPath -Path $Path
        if (-not [IO.File]::Exists($full)) { throw 'File not found.' }
        $bytes = [IO.File]::ReadAllBytes($full)
        if ($bytes.Length -lt 44) { throw 'Save is shorter than the envelope header.' }
        $magic = $script:P45Utf8.GetString($bytes, 0, 4)
        if ($magic -ne 'P4HL') { throw 'Save magic is not P4HL.' }
        $offset = 4
        $envelopeVersion = Read-P45SaveInt32 -Bytes $bytes -Offset ([ref]$offset)
        $payloadLength = Read-P45SaveInt32 -Bytes $bytes -Offset ([ref]$offset)
        if ($payloadLength -le 0 -or (44 + $payloadLength) -gt $bytes.Length) { throw 'Save payload length is invalid.' }
        $expectedPayloadHash = [BitConverter]::ToString($bytes, $offset, 32).Replace('-', '').ToUpperInvariant()
        $offset += 32
        $payload = New-Object byte[] $payloadLength
        [Array]::Copy($bytes, 44, $payload, 0, $payloadLength)
        $actualPayloadHash = Get-P45BytesSha256 -Bytes $payload
        if ($expectedPayloadHash -ne $actualPayloadHash) { throw 'Save envelope SHA-256 does not match its payload.' }

        $p = 0
        $fileFormatVersion = Read-P45SaveInt32 -Bytes $payload -Offset ([ref]$p)
        $roomId = Read-P45SaveString -Bytes $payload -Offset ([ref]$p)
        if ($p -ge $payload.Length) { throw 'Save is truncated before origin.' }
        $originValue = [int]$payload[$p]
        $p++
        $origin = if ($originValue -eq 0) { 'Local' } elseif ($originValue -eq 1) { 'Remote' } else { 'UNKNOWN' }
        $rulesetVersion = Read-P45SaveInt32 -Bytes $payload -Offset ([ref]$p)
        $stateVersion = Read-P45SaveUInt64 -Bytes $payload -Offset ([ref]$p)
        $lastEventSeq = Read-P45SaveUInt64 -Bytes $payload -Offset ([ref]$p)
        $stateFingerprint = Read-P45SaveUInt64 -Bytes $payload -Offset ([ref]$p)
        $stateHashLength = Read-P45SaveInt32 -Bytes $payload -Offset ([ref]$p)
        if ($stateHashLength -ne 32 -or ($p + $stateHashLength) -gt $payload.Length) { throw 'Save state hash length is invalid.' }
        $stateHash = [BitConverter]::ToString($payload, $p, $stateHashLength).Replace('-', '').ToUpperInvariant()
        $p += $stateHashLength
        $controlVersion = Read-P45SaveInt64 -Bytes $payload -Offset ([ref]$p)
        $controlEpoch = Read-P45SaveUInt64 -Bytes $payload -Offset ([ref]$p)
        $timingVersion = Read-P45SaveInt64 -Bytes $payload -Offset ([ref]$p)
        $cheatVersion = Read-P45SaveInt64 -Bytes $payload -Offset ([ref]$p)
        $cheatFingerprint = Read-P45SaveUInt64 -Bytes $payload -Offset ([ref]$p)
        $cheatBound = Read-P45SaveBool -Bytes $payload -Offset ([ref]$p)

        [pscustomobject]@{
            valid = $true
            error = ''
            path = $full
            bytes = [long]$bytes.Length
            sha256 = Get-P45Sha256 -Path $full
            envelopeVersion = $envelopeVersion
            payloadLength = $payloadLength
            payloadSha256 = $actualPayloadHash
            fileFormatVersion = $fileFormatVersion
            roomId = $roomId
            origin = $origin
            rulesetVersion = $rulesetVersion
            stateVersion = $stateVersion.ToString([Globalization.CultureInfo]::InvariantCulture)
            lastEventSeq = $lastEventSeq.ToString([Globalization.CultureInfo]::InvariantCulture)
            stateFingerprint = $stateFingerprint.ToString([Globalization.CultureInfo]::InvariantCulture)
            stateHash = $stateHash
            controlVersion = $controlVersion
            controlEpoch = $controlEpoch.ToString([Globalization.CultureInfo]::InvariantCulture)
            timingVersion = $timingVersion
            cheatVersion = $cheatVersion
            cheatFingerprint = $cheatFingerprint.ToString([Globalization.CultureInfo]::InvariantCulture)
            cheatBound = $cheatBound
            availableFields = @('stateVersion', 'lastEventSeq', 'stateFingerprint', 'stateHash', 'controlEpoch')
            missingRecoveryFields = @('roundId', 'confirmedRequests', 'ledger', 'physicalTileDigest', 'structuredPlayerExport')
        }
    }
    catch {
        [pscustomobject]@{
            valid = $false
            error = (Get-P45ErrorMessage $_)
            path = (Get-P45FullPath -Path $Path)
            bytes = if ([IO.File]::Exists($Path)) { (Get-Item -LiteralPath $Path).Length } else { 0 }
            sha256 = if ([IO.File]::Exists($Path)) { Get-P45Sha256 -Path $Path } else { '' }
            availableFields = @()
            missingRecoveryFields = @('all')
        }
    }
}

function New-P45SaveObservation {
    param(
        [Parameter(Mandatory = $true)][object]$Context,
        [Parameter(Mandatory = $true)][object]$Header,
        [Parameter(Mandatory = $true)][string]$SourceFile
    )

    $headerSafe = $Header | Select-Object -Property * -ExcludeProperty path,sha256,payloadSha256
    $observation = [pscustomobject]@{
        schemaVersion = $script:P45SchemaVersion
        kind = 'p45-e2e-observation'
        observationId = 'save-' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
        source = 'SAVE_HEADER_EXTRACT'
        phase = 'UNKNOWN'
        capturedAt = (Get-P45TimeContext).local
        runBinding = [pscustomobject]@{
            runId = [string]$Context.Run.runId
            playerSha256 = [string]$Context.Run.player.sha256
            sourceHead = [string]$Context.Run.source.head
            buildCommit = [string]$Context.Run.player.buildCommit
            buildProvenance = [string]$Context.Run.player.buildProvenance
            rulesetVersion = if ($Header.valid) { [int]$Header.rulesetVersion } else { 1 }
        }
        availability = [pscustomobject]@{
            roundId = $false
            stateVersion = [bool]$Header.valid
            eventSeq = [bool]$Header.valid
            controlEpoch = [bool]$Header.valid
            requests = $false
            ledger = $false
            physical = $false
            structuredPlayerExport = $false
        }
        roundId = $null
        stateVersion = if ($Header.valid) { [string]$Header.stateVersion } else { $null }
        eventSeq = if ($Header.valid) { [string]$Header.lastEventSeq } else { $null }
        controlEpoch = if ($Header.valid) { [string]$Header.controlEpoch } else { $null }
        requests = $null
        ledger = $null
        physical = $null
        reconnect = $null
        save = [pscustomobject]@{
            sourceFile = (Get-P45RelativePath -BaseDirectory $Context.Root -Path $SourceFile)
            header = $headerSafe
        }
        integrity = [pscustomobject]@{
            algorithm = 'SHA-256'
            canonicalSha256 = ''
        }
    }
    $observation.integrity.canonicalSha256 = Get-P45ObservationDigest -Observation $observation
    return $observation
}

function Get-P45ObservationDigest {
    param(
        [Parameter(Mandatory = $true)][object]$Observation,
        [switch]$ReturnLines
    )

    $runBinding = Get-P45Property $Observation 'runBinding'
    $ledger = Get-P45Property $Observation 'ledger'
    $physical = Get-P45Property $Observation 'physical'
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('schemaVersion=' + (ConvertTo-P45InvariantString (Get-P45Property $Observation 'schemaVersion')))
    $lines.Add('kind=' + (ConvertTo-P45InvariantString (Get-P45Property $Observation 'kind')))
    $lines.Add('observationId=' + (ConvertTo-P45InvariantString (Get-P45Property $Observation 'observationId')))
    $lines.Add('source=' + (ConvertTo-P45InvariantString (Get-P45Property $Observation 'source')))
    $lines.Add('phase=' + (ConvertTo-P45InvariantString (Get-P45Property $Observation 'phase')))
    $lines.Add('runId=' + (ConvertTo-P45InvariantString (Get-P45Property $runBinding 'runId')))
    $lines.Add('playerSha256=' + (ConvertTo-P45InvariantString (Get-P45Property $runBinding 'playerSha256')))
    $lines.Add('sourceHead=' + (ConvertTo-P45InvariantString (Get-P45Property $runBinding 'sourceHead')))
    $lines.Add('buildCommit=' + (ConvertTo-P45InvariantString (Get-P45Property $runBinding 'buildCommit')))
    $lines.Add('buildProvenance=' + (ConvertTo-P45InvariantString (Get-P45Property $runBinding 'buildProvenance')))
    $lines.Add('rulesetVersion=' + (ConvertTo-P45InvariantString (Get-P45Property $runBinding 'rulesetVersion')))
    $availability = Get-P45Property $Observation 'availability'
    foreach ($field in @('roundId', 'stateVersion', 'eventSeq', 'controlEpoch', 'requests', 'ledger', 'physical', 'structuredPlayerExport')) {
        $lines.Add('availability.' + $field + '=' + (ConvertTo-P45InvariantString (Get-P45Property $availability $field)))
    }
    $lines.Add('roundId=' + (ConvertTo-P45InvariantString (Get-P45Property $Observation 'roundId')))
    $lines.Add('stateVersion=' + (ConvertTo-P45InvariantString (Get-P45Property $Observation 'stateVersion')))
    $lines.Add('eventSeq=' + (ConvertTo-P45InvariantString (Get-P45Property $Observation 'eventSeq')))
    $lines.Add('controlEpoch=' + (ConvertTo-P45InvariantString (Get-P45Property $Observation 'controlEpoch')))
    $lines.Add('ledger.points=' + (ConvertTo-P45InvariantString (Get-P45Property $ledger 'points')))
    $lines.Add('ledger.entryCount=' + (ConvertTo-P45InvariantString (Get-P45Property $ledger 'entryCount')))
    $lines.Add('ledger.entriesSha256=' + (ConvertTo-P45InvariantString (Get-P45Property $ledger 'entriesSha256')))
    $lines.Add('physical.tileCount=' + (ConvertTo-P45InvariantString (Get-P45Property $physical 'tileCount')))
    $lines.Add('physical.tilesSha256=' + (ConvertTo-P45InvariantString (Get-P45Property $physical 'tilesSha256')))

    $requests = @(Get-P45Property $Observation 'requests' @())
    $requestLines = @()
    foreach ($request in $requests) {
        $requestLines += ('request=' +
            (ConvertTo-P45InvariantString (Get-P45Property $request 'requestId')) + '|' +
            (ConvertTo-P45InvariantString (Get-P45Property $request 'receiptId')) + '|' +
            (ConvertTo-P45InvariantString (Get-P45Property $request 'confirmed')) + '|' +
            (ConvertTo-P45InvariantString (Get-P45Property $request 'payloadSha256')))
    }
    [Array]::Sort($requestLines, [StringComparer]::Ordinal)
    foreach ($line in $requestLines) { $lines.Add($line) }
    if ($ReturnLines) { return @($lines.ToArray()) }
    return Get-P45TextSha256 -Text ($lines -join "`n")
}

function Test-P45Observation {
    param(
        [Parameter(Mandatory = $true)][object]$Observation,
        [Parameter(Mandatory = $true)][object]$Context,
        [Parameter(Mandatory = $true)][ValidateSet('Before', 'After')][string]$ExpectedPhase
    )

    $errors = New-Object System.Collections.Generic.List[string]
    $identityErrors = New-Object System.Collections.Generic.List[string]

    if ((Get-P45Property $Observation 'kind') -ne 'p45-e2e-observation') { $errors.Add('kind must be p45-e2e-observation') }
    if ((Get-P45Property $Observation 'schemaVersion') -ne $script:P45SchemaVersion) { $errors.Add('schemaVersion must be 1') }
    $observationId = [string](Get-P45Property $Observation 'observationId')
    if ([string]::IsNullOrWhiteSpace($observationId)) { $errors.Add('observationId is required') }
    $source = [string](Get-P45Property $Observation 'source')
    if ($script:P45ObservationSources -notcontains $source) { $errors.Add('source is not recognized') }
    $phase = [string](Get-P45Property $Observation 'phase')
    $expectedPhaseText = if ($ExpectedPhase -eq 'Before') { 'BEFORE_RECOVERY' } else { 'AFTER_RECOVERY' }
    if ($script:P45Views -notcontains $phase) { $errors.Add('phase is not recognized') }
    if (-not [string]::Equals($phase, $expectedPhaseText, [StringComparison]::Ordinal)) {
        $errors.Add('phase must be exactly ' + $expectedPhaseText + '; UNKNOWN or the other side cannot be used for recovery Compare')
    }

    $binding = Get-P45Property $Observation 'runBinding'
    if ($null -eq $binding) {
        $errors.Add('runBinding is required')
        $binding = [pscustomobject]@{}
    }
    if (-not [string]::Equals([string](Get-P45Property $binding 'runId'), [string]$Context.Run.runId, [StringComparison]::Ordinal)) {
        $identityErrors.Add('observation runId does not match this run')
    }
    if (-not [string]::Equals([string](Get-P45Property $binding 'playerSha256'), [string]$Context.Run.player.sha256, [StringComparison]::OrdinalIgnoreCase)) {
        $identityErrors.Add('observation playerSha256 does not match this run Player')
    }
    if (-not [string]::Equals([string](Get-P45Property $binding 'sourceHead'), [string]$Context.Run.source.head, [StringComparison]::OrdinalIgnoreCase)) {
        $identityErrors.Add('observation sourceHead does not match this run source')
    }
    $buildCommit = [string](Get-P45Property $binding 'buildCommit')
    if (-not (Test-P45CommitText $buildCommit)) { $identityErrors.Add('observation buildCommit must be UNKNOWN or 40 hex characters') }
    $buildProvenance = [string](Get-P45Property $binding 'buildProvenance')
    if ($script:P45BuildProvenances -notcontains $buildProvenance) { $errors.Add('observation buildProvenance is not recognized') }
    $runPlayer = Get-P45Property $Context.Run 'player'
    $runBuildCommit = [string](Get-P45Property $runPlayer 'buildCommit')
    $runBuildProvenance = [string](Get-P45Property $runPlayer 'buildProvenance')
    if (-not [string]::Equals($buildCommit, $runBuildCommit, [StringComparison]::OrdinalIgnoreCase)) {
        $identityErrors.Add('observation buildCommit does not match the Player build recorded by Prepare')
    }
    if (-not [string]::Equals($buildProvenance, $runBuildProvenance, [StringComparison]::Ordinal)) {
        $identityErrors.Add('observation buildProvenance does not match the Player provenance recorded by Prepare')
    }
    $ruleset = Get-P45Property $binding 'rulesetVersion'
    if ($null -eq $ruleset -or [int]$ruleset -lt 1) { $errors.Add('observation rulesetVersion must be a positive integer') }

    if ([string]$Context.Run.evidenceClass -eq 'TOOL_SELF_TEST' -and $source -ne 'TOOL_SELF_TEST') {
        $errors.Add('TOOL_SELF_TEST runs cannot contain observations that claim source ' + $source)
    }

    $availability = Get-P45Property $Observation 'availability'
    if ($null -eq $availability) {
        $errors.Add('availability is required')
        $availability = [pscustomobject]@{}
    }
    foreach ($field in @('roundId', 'stateVersion', 'eventSeq', 'controlEpoch', 'requests', 'ledger', 'physical', 'structuredPlayerExport')) {
        if ($null -eq (Get-P45Property $availability $field)) { $errors.Add('availability.' + $field + ' is required') }
    }

    foreach ($field in @('stateVersion', 'eventSeq', 'controlEpoch')) {
        $value = Get-P45Property $Observation $field
        if ($null -ne $value -and ([string]$value -notmatch '^\d+$')) { $errors.Add($field + ' must be a decimal string when present') }
    }

    $requests = Get-P45Property $Observation 'requests'
    if ($null -ne $requests) {
        $requestIds = @{}
        foreach ($request in @($requests)) {
            $requestId = [string](Get-P45Property $request 'requestId')
            $receiptId = [string](Get-P45Property $request 'receiptId')
            $confirmed = Get-P45Property $request 'confirmed'
            $payloadHash = [string](Get-P45Property $request 'payloadSha256')
            if ([string]::IsNullOrWhiteSpace($requestId)) { $errors.Add('requestId is required') }
            if ([string]::IsNullOrWhiteSpace($receiptId)) { $errors.Add('receiptId is required for request ' + $requestId) }
            if ($confirmed -isnot [bool]) { $errors.Add('confirmed must be boolean for request ' + $requestId) }
            if (-not (Test-P45Sha256Text $payloadHash)) { $errors.Add('payloadSha256 is invalid for request ' + $requestId) }
            if ($requestIds.ContainsKey($requestId)) { $errors.Add('duplicate requestId ' + $requestId) }
            $requestIds[$requestId] = $true
        }
    }

    $ledger = Get-P45Property $Observation 'ledger'
    if ($null -ne $ledger) {
        if ([string](Get-P45Property $ledger 'points') -notmatch '^-?\d+$') { $errors.Add('ledger.points must be a decimal string') }
        $entryCount = Get-P45Property $ledger 'entryCount'
        if ($null -eq $entryCount -or [int]$entryCount -lt 0) { $errors.Add('ledger.entryCount must be a non-negative integer') }
        if (-not (Test-P45Sha256Text ([string](Get-P45Property $ledger 'entriesSha256')))) { $errors.Add('ledger.entriesSha256 is invalid') }
    }

    $physical = Get-P45Property $Observation 'physical'
    if ($null -ne $physical) {
        $tileCount = Get-P45Property $physical 'tileCount'
        if ($null -eq $tileCount -or [int]$tileCount -lt 0 -or [int]$tileCount -gt 136) { $errors.Add('physical.tileCount must be 0..136') }
        if (-not (Test-P45Sha256Text ([string](Get-P45Property $physical 'tilesSha256')))) { $errors.Add('physical.tilesSha256 is invalid') }
    }

    $integrity = Get-P45Property $Observation 'integrity'
    if ($null -eq $integrity) {
        $errors.Add('integrity is required')
    }
    else {
        if ((Get-P45Property $integrity 'algorithm') -ne 'SHA-256') { $errors.Add('integrity.algorithm must be SHA-256') }
        $declared = [string](Get-P45Property $integrity 'canonicalSha256')
        if (-not (Test-P45Sha256Text $declared)) {
            $errors.Add('integrity.canonicalSha256 is invalid')
        }
        else {
            try {
                $actual = Get-P45ObservationDigest -Observation $Observation
                if (-not [string]::Equals($declared, $actual, [StringComparison]::OrdinalIgnoreCase)) {
                    $errors.Add('integrity.canonicalSha256 does not match canonical observation fields')
                }
            }
            catch {
                $errors.Add('canonical digest could not be computed: ' + (Get-P45ErrorMessage $_))
            }
        }
    }

    $eligible = $true
    $reasons = New-Object System.Collections.Generic.List[string]
    if ([string]$Context.Run.evidenceClass -ne 'GAME') {
        $eligible = $false
        $reasons.Add('Prepare evidenceClass is ' + [string]$Context.Run.evidenceClass + ', not GAME')
    }
    if ($source -ne 'PLAYER_STRUCTURED_EXPORT') {
        $eligible = $false
        $reasons.Add('source is ' + $source + ', not a real Player structured export')
    }
    foreach ($field in @('roundId', 'stateVersion', 'eventSeq', 'requests', 'ledger', 'physical', 'structuredPlayerExport')) {
        if ((Get-P45Property $availability $field) -ne $true) {
            $eligible = $false
            $reasons.Add('availability.' + $field + ' is false')
        }
    }
    if ($null -eq (Get-P45Property $Observation 'roundId') -or [string]::IsNullOrWhiteSpace([string](Get-P45Property $Observation 'roundId'))) {
        $eligible = $false; $reasons.Add('roundId is missing')
    }
    if ($null -eq $requests) { $eligible = $false; $reasons.Add('confirmed requests are missing') }
    if ($null -eq $ledger) { $eligible = $false; $reasons.Add('ledger is missing') }
    if ($null -eq $physical) { $eligible = $false; $reasons.Add('physical digest is missing') }
    elseif ($null -ne $physical -and [int](Get-P45Property $physical 'tileCount') -ne 136) {
        $eligible = $false; $reasons.Add('physical.tileCount is not 136')
    }
    if ($runBuildCommit -eq 'UNKNOWN' -or $buildCommit -eq 'UNKNOWN') {
        $eligible = $false; $reasons.Add('build identity is UNKNOWN')
    }
    if ($runBuildProvenance -eq 'UNKNOWN' -or $buildProvenance -eq 'UNKNOWN') {
        $eligible = $false; $reasons.Add('build provenance is UNKNOWN')
    }
    if ($runBuildProvenance -ne 'BUILD_LOG_ATTACHED') {
        $eligible = $false; $reasons.Add('build provenance is not a verifiable BUILD_LOG_ATTACHED claim')
    }
    if ($null -eq (Get-P45Property $runPlayer 'buildLog')) {
        $eligible = $false; $reasons.Add('build log artifact is missing from the prepared Player record')
    }
    if (-not [string]::Equals($buildCommit, [string]$Context.Run.source.head, [StringComparison]::OrdinalIgnoreCase)) {
        $eligible = $false; $reasons.Add('Player build commit does not match the prepared source head')
    }
    if ([bool]$Context.Run.source.dirty) {
        $eligible = $false; $reasons.Add('prepared source tree was dirty, so no build can be tied to an immutable clean commit')
    }
    if ($source -eq 'PLAYER_STRUCTURED_EXPORT') {
        $eligible = $false
        $reasons.Add('no verified P45 Player structured exporter is registered in this tool version; self-asserted source and a recomputable SHA-256 are not provenance')
    }

    [pscustomobject]@{
        Valid = ($errors.Count -eq 0)
        IdentityValid = ($identityErrors.Count -eq 0)
        Errors = @($errors.ToArray())
        IdentityErrors = @($identityErrors.ToArray())
        Eligible = ($eligible -and $errors.Count -eq 0 -and $identityErrors.Count -eq 0)
        EligibilityReasons = @($reasons.ToArray())
        Source = $source
        IsToolSelfTest = ($source -eq 'TOOL_SELF_TEST' -or [string]$Context.Run.evidenceClass -eq 'TOOL_SELF_TEST')
        Phase = $phase
    }
}

function Import-P45Observation {
    param(
        [Parameter(Mandatory = $true)][object]$Context,
        [Parameter(Mandatory = $true)][string]$ObservationPath,
        [Parameter(Mandatory = $true)][ValidateSet('Before', 'After')][string]$Phase
    )

    $source = Get-P45FullPath -Path $ObservationPath
    $observation = Read-P45Json -Path $source
    $validation = Test-P45Observation -Observation $observation -Context $Context -ExpectedPhase $Phase
    if (-not $validation.IdentityValid) {
        Throw-P45 19 'OBSERVATION_IDENTITY' ('Observation identity rejected: ' + ($validation.IdentityErrors -join '; '))
    }
    if (-not $validation.Valid) {
        Throw-P45 18 'OBSERVATION_INVALID' ('Observation rejected: ' + ($validation.Errors -join '; '))
    }

    $stamp = (Get-P45TimeContext).localDate
    $hashPrefix = (Get-P45Sha256 -Path $source).Substring(0, 12)
    $destination = Join-Path $Context.Root ('observations\' + $Phase.ToLowerInvariant() + '-' + $stamp + '-' + $hashPrefix + '.json')
    if ([IO.File]::Exists($destination)) {
        Throw-P45 3 'RUN_EXISTS' 'Refusing to overwrite an existing imported observation.'
    }
    [IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
    [IO.File]::Copy($source, $destination, $false)

    [pscustomobject]@{
        Phase = $Phase
        Destination = $destination
        Sha256 = Get-P45Sha256 -Path $destination
        Source = $validation.Source
        Eligible = $validation.Eligible
        EligibilityReasons = $validation.EligibilityReasons
    }
}

function Get-P45LatestObservation {
    param(
        [Parameter(Mandatory = $true)][object]$Context,
        [Parameter(Mandatory = $true)][ValidateSet('Before', 'After')][string]$Phase
    )

    $root = Join-Path $Context.Root 'observations'
    if (-not [IO.Directory]::Exists($root)) { return $null }
    $prefix = $Phase.ToLowerInvariant() + '-'
    $files = @(Get-ChildItem -LiteralPath $root -File -Filter ($prefix + '*.json') -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending)
    if ($files.Count -eq 0) { return $null }
    return $files[0].FullName
}

function Compare-P45Observations {
    param(
        [Parameter(Mandatory = $true)][object]$Context,
        [Parameter(Mandatory = $true)][string]$BeforePath,
        [Parameter(Mandatory = $true)][string]$AfterPath
    )

    $before = Read-P45Json -Path $BeforePath
    $after = Read-P45Json -Path $AfterPath
    $beforeValidation = Test-P45Observation -Observation $before -Context $Context -ExpectedPhase 'Before'
    $afterValidation = Test-P45Observation -Observation $after -Context $Context -ExpectedPhase 'After'
    if (-not $beforeValidation.IdentityValid -or -not $afterValidation.IdentityValid) {
        Throw-P45 19 'OBSERVATION_IDENTITY' ('Observation identity rejected: ' +
            (($beforeValidation.IdentityErrors + $afterValidation.IdentityErrors) -join '; '))
    }
    if (-not $beforeValidation.Valid -or -not $afterValidation.Valid) {
        Throw-P45 18 'OBSERVATION_INVALID' ('Observation rejected: ' +
            (($beforeValidation.Errors + $afterValidation.Errors) -join '; '))
    }

    $checks = New-Object System.Collections.Generic.List[object]
    $addCheck = {
        param([string]$Name, [object]$Left, [object]$Right, [bool]$Required)
        $leftText = ConvertTo-P45InvariantString $Left
        $rightText = ConvertTo-P45InvariantString $Right
        $equal = [string]::Equals($leftText, $rightText, [StringComparison]::Ordinal)
        $checks.Add([pscustomobject]@{
            name = $Name
            before = $leftText
            after = $rightText
            equal = $equal
            required = $Required
        }) | Out-Null
        return $equal
    }

    [void](& $addCheck 'roundId' (Get-P45Property $before 'roundId') (Get-P45Property $after 'roundId') $true)
    [void](& $addCheck 'stateVersion' (Get-P45Property $before 'stateVersion') (Get-P45Property $after 'stateVersion') $true)
    [void](& $addCheck 'eventSeq' (Get-P45Property $before 'eventSeq') (Get-P45Property $after 'eventSeq') $true)
    [void](& $addCheck 'controlEpoch' (Get-P45Property $before 'controlEpoch') (Get-P45Property $after 'controlEpoch') $false)

    $beforeRequests = @(Get-P45Property $before 'requests' @())
    $afterRequests = @(Get-P45Property $after 'requests' @())
    $requestLines = {
        param([object[]]$Requests)
        $lines = @()
        foreach ($request in $Requests) {
            $lines += ((ConvertTo-P45InvariantString (Get-P45Property $request 'requestId')) + '|' +
                (ConvertTo-P45InvariantString (Get-P45Property $request 'receiptId')) + '|' +
                (ConvertTo-P45InvariantString (Get-P45Property $request 'confirmed')) + '|' +
                (ConvertTo-P45InvariantString (Get-P45Property $request 'payloadSha256')))
        }
        [Array]::Sort($lines, [StringComparer]::Ordinal)
        return ($lines -join "`n")
    }
    [void](& $addCheck 'confirmedRequests' (& $requestLines $beforeRequests) (& $requestLines $afterRequests) $true)

    $beforeLedger = Get-P45Property $before 'ledger'
    $afterLedger = Get-P45Property $after 'ledger'
    [void](& $addCheck 'ledger.points' (Get-P45Property $beforeLedger 'points') (Get-P45Property $afterLedger 'points') $true)
    [void](& $addCheck 'ledger.entryCount' (Get-P45Property $beforeLedger 'entryCount') (Get-P45Property $afterLedger 'entryCount') $true)
    [void](& $addCheck 'ledger.entriesSha256' (Get-P45Property $beforeLedger 'entriesSha256') (Get-P45Property $afterLedger 'entriesSha256') $true)

    $beforePhysical = Get-P45Property $before 'physical'
    $afterPhysical = Get-P45Property $after 'physical'
    [void](& $addCheck 'physical.tileCount' (Get-P45Property $beforePhysical 'tileCount') (Get-P45Property $afterPhysical 'tileCount') $true)
    [void](& $addCheck 'physical.tilesSha256' (Get-P45Property $beforePhysical 'tilesSha256') (Get-P45Property $afterPhysical 'tilesSha256') $true)

    [void](& $addCheck 'runId' (Get-P45Property (Get-P45Property $before 'runBinding') 'runId') (Get-P45Property (Get-P45Property $after 'runBinding') 'runId') $true)
    [void](& $addCheck 'playerSha256' (Get-P45Property (Get-P45Property $before 'runBinding') 'playerSha256') (Get-P45Property (Get-P45Property $after 'runBinding') 'playerSha256') $true)
    [void](& $addCheck 'sourceHead' (Get-P45Property (Get-P45Property $before 'runBinding') 'sourceHead') (Get-P45Property (Get-P45Property $after 'runBinding') 'sourceHead') $true)
    [void](& $addCheck 'buildCommit' (Get-P45Property (Get-P45Property $before 'runBinding') 'buildCommit') (Get-P45Property (Get-P45Property $after 'runBinding') 'buildCommit') $true)
    [void](& $addCheck 'rulesetVersion' (Get-P45Property (Get-P45Property $before 'runBinding') 'rulesetVersion') (Get-P45Property (Get-P45Property $after 'runBinding') 'rulesetVersion') $true)

    $beforeReconnect = Get-P45Property $before 'reconnect'
    $afterReconnect = Get-P45Property $after 'reconnect'
    $reconnectBeforeText = ConvertTo-P45InvariantString (Get-P45Property $beforeReconnect 'sessionId') + '|' + (ConvertTo-P45InvariantString (Get-P45Property $beforeReconnect 'cursor'))
    $reconnectAfterText = ConvertTo-P45InvariantString (Get-P45Property $afterReconnect 'sessionId') + '|' + (ConvertTo-P45InvariantString (Get-P45Property $afterReconnect 'cursor'))

    $mismatches = @($checks | Where-Object { $_.required -and -not $_.equal })
    $requiredEqual = ($mismatches.Count -eq 0)
    $toolOnly = ([string]$Context.Run.evidenceClass -eq 'TOOL_SELF_TEST' -or
        $beforeValidation.IsToolSelfTest -or $afterValidation.IsToolSelfTest)
    $eligible = ($beforeValidation.Eligible -and $afterValidation.Eligible)

    $incompleteReasons = New-Object System.Collections.Generic.List[string]
    foreach ($pair in @(
            @{ Name = 'before'; Observation = $before },
            @{ Name = 'after'; Observation = $after })) {
        $availability = Get-P45Property $pair.Observation 'availability'
        foreach ($field in @('roundId', 'stateVersion', 'eventSeq', 'requests', 'ledger', 'physical', 'structuredPlayerExport')) {
            if ((Get-P45Property $availability $field) -ne $true) {
                $incompleteReasons.Add($pair.Name + ' availability.' + $field + ' is false')
            }
        }
        foreach ($field in @('roundId', 'stateVersion', 'eventSeq')) {
            if ($null -eq (Get-P45Property $pair.Observation $field)) {
                $incompleteReasons.Add($pair.Name + ' ' + $field + ' is missing')
            }
        }
        foreach ($field in @('requests', 'ledger', 'physical')) {
            $property = $pair.Observation.PSObject.Properties[$field]
            if ($null -eq $property -or $null -eq $property.Value) {
                $incompleteReasons.Add($pair.Name + ' ' + $field + ' missing')
            }
        }
    }
    $incomplete = ($incompleteReasons.Count -gt 0)

    $outcome = 'BLOCKED'
    $exitCode = 17
    $pass = $false
    $rationale = ''
    if ($incomplete) {
        $outcome = 'BLOCKED'
        $exitCode = 17
        $rationale = 'Required recovery fields are missing/not available: ' + ($incompleteReasons -join '; ')
    }
    elseif (-not $requiredEqual) {
        $outcome = 'FAIL'
        $exitCode = 16
        $rationale = 'One or more required recovery fields differ.'
    }
    elseif ($toolOnly) {
        $outcome = 'TOOL_SELF_TEST_ONLY'
        $exitCode = 30
        $rationale = 'The comparison logic equaled, but at least one observation is TOOL_SELF_TEST evidence and cannot represent a real game recovery.'
    }
    elseif (-not $eligible) {
        $outcome = 'BLOCKED'
        $exitCode = 17
        $rationale = 'Required fields equal, but real Player export eligibility is not established: ' +
            ((@($beforeValidation.EligibilityReasons) + @($afterValidation.EligibilityReasons)) -join '; ')
    }
    else {
        $outcome = 'PASS'
        $exitCode = 0
        $pass = $true
        $rationale = 'Both observations are complete real Player structured exports from the same run/build; required recovery fields are equal.'
    }

    $result = [pscustomobject]@{
        schemaVersion = $script:P45SchemaVersion
        kind = 'p45-e2e-compare'
        compareId = 'p45-compare-' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
        runId = [string]$Context.Run.runId
        evidenceClass = [string]$Context.Run.evidenceClass
        createdAt = (Get-P45TimeContext).local
        before = [pscustomobject]@{
            path = (Get-P45RelativePath -BaseDirectory $Context.Root -Path $BeforePath)
            sha256 = Get-P45Sha256 -Path $BeforePath
            observationId = [string](Get-P45Property $before 'observationId')
            source = [string](Get-P45Property $before 'source')
            phase = [string](Get-P45Property $before 'phase')
        }
        after = [pscustomobject]@{
            path = (Get-P45RelativePath -BaseDirectory $Context.Root -Path $AfterPath)
            sha256 = Get-P45Sha256 -Path $AfterPath
            observationId = [string](Get-P45Property $after 'observationId')
            source = [string](Get-P45Property $after 'source')
            phase = [string](Get-P45Property $after 'phase')
        }
        checks = @($checks.ToArray())
        mismatches = @($mismatches)
        requiredFieldsEqual = $requiredEqual
        eligibleForRealPass = $eligible
        realPassGate = 'NO_VERIFIED_PLAYER_STRUCTURED_EXPORTER_IN_THIS_TOOL_VERSION'
        pass = $pass
        outcome = $outcome
        rationale = $rationale
        mutableFields = [pscustomobject]@{
            reconnectSessionAndCursor = [pscustomobject]@{
                before = $reconnectBeforeText
                after = $reconnectAfterText
                compared = $false
            }
            controlEpoch = [pscustomobject]@{
                before = (ConvertTo-P45InvariantString (Get-P45Property $before 'controlEpoch'))
                after = (ConvertTo-P45InvariantString (Get-P45Property $after 'controlEpoch'))
                compared = $false
            }
        }
    }

    $resultPath = Join-Path $Context.Root ('evidence\compare-' + (Get-P45TimeContext).localDate + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6) + '.json')
    Write-P45JsonAtomic -Path $resultPath -Object $result
    [pscustomobject]@{
        Result = $result
        ResultPath = $resultPath
        ExitCode = $exitCode
    }
}
