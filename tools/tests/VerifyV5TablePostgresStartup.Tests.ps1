#requires -Version 5.1
<#
.SYNOPSIS
    Self-test for tools\ci\verify-v5-table-postgres.ps1 startup and port
    preflight, including the real PostgreSQL 16.10 start/query/restart/cleanup
    path it owns.

.DESCRIPTION
    Context: push run 41140326888 (PR20 head 4271268) failed before any business
    test because PostgreSQL could not bind 127.0.0.1:55592:
        FATAL: could not create any TCP/IP sockets
    and the wrapper's old connect-only Test-PortInUse reported the port free.
    This self-test proves the replacement behavior with executable evidence, not
    source-text matching:

      S0  static contract: production script parses; required functions and
          parameter semantics exist; no machine-wide network/service mutation
          commands are present.
      S1  real bind-probe matrix: free port (Bindable), real loopback listener
          (InUse), and a real exclusive-address holder on 0.0.0.0 that accepts
          no connection but makes 127.0.0.1 bind fail with WSAEACCES 10013 -
          the exact class the connect-only probe missed. Also invalid/reserved.
      S2  production selector semantics: explicit port is exact and refuses
          (never substitutes) on AccessDenied/Invalid/lobby conflict; default
          mode moves to the next bindable port, skips the lobby port, and
          reports named candidate exhaustion. A narrow -Probe dependency
          injection is used only to prove deterministic exhaustion/partial
          selection; the real EACCES holder above is the primary evidence.
      S3  end-to-end production script refusals: a real listener and a real
          exclusive holder both produce BLOCKED exit 2 with no root, no
          pg_ctl start, and the foreign listener/holder survives untouched.
      S4  end-to-end explicit invalid/reserved/lobby-conflict refusals.
      S5  end-to-end default-mode candidate exhaustion with three real
          exclusive holders: BLOCKED, every candidate named, no fallback.
      S6  env restore + ownership non-regression: production
          Restore-ProcessEnvironment restores a present-nonempty variable and
          keeps an originally-absent variable absent (fresh child readback;
          Windows cannot represent a present-empty process variable), and a
          foreign ownership marker is refused while the foreign root is
          preserved.
      S7  REAL PostgreSQL 16.10 lifecycle through the production
          Initialize-Cluster/Restart-ClusterImmediate: isolated loopback-only
          start, 16.10 version query, data_directory ownership check, durable
          row across an immediate crash restart with recovery lines, then an
          ownership-checked stop and long-path-safe recursive delete of only
          this run's own root.
      S8  REAL pg_ctl startup failure (invalid server setting) through the full
          production script: FAIL exit 1, native pg_ctl exit recorded, original
          pg-start.log/postgres.log preserved, one attempt only (no blind
          retry), no tests.xml / tests stage, ownership-verified cleanup.
      S9  full production script with a real owned cluster and a fake dotnet
          that fails the fresh build: proves the finally block restores the
          caller's sentinel env values (recorded by value hash, never value)
          while the cluster is still stopped/deleted safely.

    Everything this self-test creates lives under the worktree's ignored
    build\ci-pg-startup-selftest\<stamp>\ tree, except the two short-lived
    failure clusters under artifacts\ci-pg-startup-... which the production
    wrapper itself creates/deletes under its existing artifacts guard. No
    machine excluded-port range, firewall rule, service or global setting is
    changed. No existing PostgreSQL process is stopped or attached to.

    Exit codes: 0 all checks passed; 1 one or more checks failed;
    2 the harness was BLOCKED before running (missing dotnet/PG zip/wrapper).
#>
[CmdletBinding()]
param(
    [string]$DotnetPath = 'D:\Unity\dotnet\dotnet.exe',
    [string]$ZipPath = 'D:\majiang-pg-tmp\cache\postgresql-16.10-1-windows-x64-binaries.zip',
    [int]$BasePort = 55740
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$wrapperScript = Join-Path $repoRoot 'tools\ci\verify-v5-table-postgres.ps1'
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
$evidenceRoot = Join-Path $repoRoot ('build\ci-pg-startup-selftest\' + $stamp + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$script:checks = 0
$script:passed = 0
$script:failed = 0
$script:failures = New-Object System.Collections.Generic.List[string]
$script:portCursor = $BasePort

function Write-CheckResult([bool]$Ok, [string]$Label) {
    $script:checks++
    if ($Ok) {
        $script:passed++
        Write-Host "  PASS  $Label"
    }
    else {
        $script:failed++
        $script:failures.Add($Label)
        Write-Host "  FAIL  $Label"
    }
}

function Assert-True([bool]$Condition, [string]$Label) {
    Write-CheckResult ([bool]$Condition) $Label
}

function Assert-Equal([string]$Expected, [string]$Actual, [string]$Label) {
    Write-CheckResult ($Expected -ceq $Actual) "$Label (expected '$Expected', actual '$Actual')"
}

function Assert-Match([string]$Text, [string]$Pattern, [string]$Label) {
    $ok = $null -ne $Text -and $Text -match $Pattern
    Write-CheckResult $ok "$Label (pattern '$Pattern' on '$($Text -replace '\s+', ' ')')"
}

function Assert-NotMatch([string]$Text, [string]$Pattern, [string]$Label) {
    $ok = -not ($null -ne $Text -and $Text -match $Pattern)
    Write-CheckResult $ok "$Label (forbidden pattern '$Pattern')"
}

# --- environment guards: BLOCKED (exit 2), never a skipped/zero-check green ---
if (-not (Test-Path -LiteralPath $DotnetPath)) {
    Write-Host "BLOCKED: dotnet not found at $DotnetPath"
    exit 2
}
if (-not (Test-Path -LiteralPath $ZipPath)) {
    Write-Host "BLOCKED: cached PostgreSQL 16.10 zip not found at $ZipPath"
    exit 2
}
if (-not (Test-Path -LiteralPath $wrapperScript)) {
    Write-Host "BLOCKED: production wrapper not found at $wrapperScript"
    exit 2
}
if ($BasePort -lt 2000 -or $BasePort -gt 64000) {
    Write-Host "BLOCKED: -BasePort must be in 2000..64000 (got $BasePort)"
    exit 2
}
$zipSha256 = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash
if ($zipSha256 -ne 'EBB3B6AF4FA69DEA9951B66855BC4D42DC04E56CCB9AA7024CE3C58BD89D6B0C') {
    Write-Host "BLOCKED: PostgreSQL zip SHA-256 mismatch: $zipSha256"
    exit 2
}
New-Item -ItemType Directory -Force -Path $evidenceRoot | Out-Null
$transcriptPath = Join-Path $evidenceRoot 'console.log'
try { Start-Transcript -LiteralPath $transcriptPath -Force | Out-Null } catch { }

Write-Host "=== VerifyV5TablePostgresStartup self-test ==="
Write-Host "repo=$repoRoot"
Write-Host "evidence=$evidenceRoot"
Write-Host "ps=$($PSVersionTable.PSVersion) os=$([Environment]::OSVersion.VersionString)"
Write-Host "dotnet=$DotnetPath"
Write-Host "pgzip=$ZipPath sha256=$zipSha256"
Write-Host "basePort=$BasePort"

# --- parse and import the production implementation (no copied logic) --------
$parseTokens = $null
$parseErrors = $null
$productionAst = [System.Management.Automation.Language.Parser]::ParseFile($wrapperScript, [ref]$parseTokens, [ref]$parseErrors)

$productionFunctionNames = @(
    'Write-Step', 'Add-Stage', 'Get-Sha256', 'Get-TextSha256',
    'Test-TcpListenerReachable', 'Get-PortProbe', 'Resolve-TablePort',
    'Get-LogTail', 'Restore-ProcessEnvironment', 'Invoke-Native',
    'Wait-PgReady', 'Get-PgScalar', 'Get-PgDataDirectory', 'Assert-OwnedCluster',
    'Initialize-Cluster', 'Restart-ClusterImmediate'
)
function Get-ProductionFunctionAst([string]$Name) {
    return $productionAst.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $Name
        }, $true) | Select-Object -First 1
}
foreach ($name in $productionFunctionNames) {
    $functionAst = Get-ProductionFunctionAst $name
    if ($null -eq $functionAst) { throw "production function '$name' not found" }
    # Define at this script's top level (not inside a helper function) so the
    # production implementation is the callable one for every scenario.
    Invoke-Expression $functionAst.Extent.Text
}

$script:RepoRoot = $repoRoot
$script:Stages = New-Object System.Collections.Generic.List[object]
$script:Lines = New-Object System.Collections.Generic.List[string]
$script:ClusterStarted = $false
$script:OwnedMarker = $null
$script:EnvRestoreRecords = @()

function Reset-ProductionState {
    $script:Stages = New-Object System.Collections.Generic.List[object]
    $script:Lines = New-Object System.Collections.Generic.List[string]
    $script:ClusterStarted = $false
    $script:OwnedMarker = $null
}

function Get-PortBlock([int]$Count) {
    while (($script:portCursor + $Count - 1) -le ($BasePort + 220)) {
        $start = $script:portCursor
        $ok = $true
        for ($i = 0; $i -lt $Count; $i++) {
            if ((Get-PortProbe -Value ($start + $i)).state -ne 'Bindable') { $ok = $false; break }
        }
        if ($ok) {
            $script:portCursor = $start + $Count
            return $start
        }
        $script:portCursor = $start + 1
    }
    throw "no free port block of $Count ports starting at $BasePort"
}

function New-ExclusiveHolder([int]$Port) {
    # Bound on 0.0.0.0 with SO_EXCLUSIVEADDRUSE and NOT listening. This is the
    # real local reproduction of the CI class: connect() fails, but PostgreSQL's
    # bind() on 127.0.0.1 fails with WSAEACCES 10013.
    $socket = [System.Net.Sockets.Socket]::new(
        [System.Net.Sockets.AddressFamily]::InterNetwork,
        [System.Net.Sockets.SocketType]::Stream,
        [System.Net.Sockets.ProtocolType]::Tcp)
    $socket.ExclusiveAddressUse = $true
    $socket.Bind([System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, $Port))
    return $socket
}

function New-LoopbackListener([int]$Port) {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
    $listener.Start()
    return $listener
}

function Test-TcpConnect([int]$Port) {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $task = $client.ConnectAsync('127.0.0.1', $Port)
        if (-not $task.Wait(400)) { return $false }
        return $client.Connected
    }
    catch { return $false }
    finally { $client.Dispose() }
}

function Remove-OwnedTree([string]$Path, [string]$GuardRoot) {
    # Guard: only a path strictly below this self-test's own evidence tree (or
    # the explicit artifacts failure-cluster argument) may be deleted. Long
    # paths use the \\?\ form so the extracted pgAdmin tree is removable.
    $full = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    $guard = [System.IO.Path]::GetFullPath($GuardRoot).TrimEnd('\') + [System.IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($guard, [StringComparison]::OrdinalIgnoreCase)) {
        throw "refusing recursive delete outside the owned guard root: $full"
    }
    if (-not (Test-Path -LiteralPath $full)) { return }
    $prefix = '\\?\' + $full
    foreach ($file in [System.IO.Directory]::EnumerateFiles($prefix, '*', [System.IO.SearchOption]::AllDirectories)) {
        [System.IO.File]::SetAttributes($file, [System.IO.FileAttributes]::Normal)
        [System.IO.File]::Delete($file)
    }
    $directories = [System.IO.Directory]::EnumerateDirectories($prefix, '*', [System.IO.SearchOption]::AllDirectories) |
        Sort-Object { ($_ -split '\\').Count } -Descending
    foreach ($directory in $directories) { [System.IO.Directory]::Delete($directory) }
    [System.IO.Directory]::Delete($prefix)
}

function Invoke-Wrapper([hashtable]$Arguments, [string]$EvidenceDir) {
    New-Item -ItemType Directory -Force -Path $EvidenceDir | Out-Null
    $hostExecutable = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    $argumentList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $wrapperScript)
    foreach ($key in ($Arguments.Keys | Sort-Object)) {
        $value = $Arguments[$key]
        if ($value -is [bool]) {
            if ($value) { $argumentList += "-$key" }
        }
        elseif ($null -ne $value -and "$value" -ne '') {
            $argumentList += @("-$key", "$value")
        }
    }
    $previousEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $hostExecutable @argumentList *>&1
        $exitCode = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousEap }
    $text = ($output | Out-String)
    Set-Content -LiteralPath (Join-Path $EvidenceDir 'console.log') -Value $text -Encoding utf8
    return [ordered]@{ exit = [int]$exitCode; text = $text; dir = $EvidenceDir; log = [string]$Arguments['LogDir'] }
}

function Read-Summary([string]$EvidenceDir) {
    foreach ($candidate in @(
            (Join-Path $EvidenceDir 'wrapper-summary.json'),
            (Join-Path $EvidenceDir 'log\wrapper-summary.json'))) {
        if (Test-Path -LiteralPath $candidate) {
            return (Get-Content -LiteralPath $candidate -Raw | ConvertFrom-Json)
        }
    }
    return $null
}

function Get-Stage($Summary, [string]$Name) {
    if ($null -eq $Summary) { return $null }
    return @($Summary.stages | Where-Object { $_.stage -eq $Name }) | Select-Object -First 1
}

function Get-PgZipCachePath {
    return $ZipPath
}

# =============================================================================
# S0  static contract
# =============================================================================
Write-Host ''
Write-Host '=== S0: static contract ==='
try {
    Assert-True ($parseErrors.Count -eq 0) 'S0 production script parses with zero syntax errors'
    foreach ($name in @('Get-PortProbe', 'Resolve-TablePort', 'Test-TcpListenerReachable', 'Restore-ProcessEnvironment')) {
        $found = $productionAst.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
            }, $true).Count -gt 0
        Assert-True $found "S0 production defines $name"
    }

    $parameters = @($productionAst.ParamBlock.Parameters)
    $portParameter = $parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'Port' } | Select-Object -First 1
    $defaultPortParameter = $parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'DefaultPort' } | Select-Object -First 1
    $limitParameter = $parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'PortCandidateLimit' } | Select-Object -First 1
    Assert-True ($null -ne $portParameter -and $null -eq $portParameter.DefaultValue) 'S0 -Port has no implicit default (explicit-only semantics)'
    Assert-True ($null -ne $defaultPortParameter -and $defaultPortParameter.DefaultValue.Extent.Text -eq '55592') 'S0 default candidate base is 55592'
    Assert-True ($null -ne $limitParameter -and $limitParameter.DefaultValue.Extent.Text -eq '16') 'S0 default candidate scan is bounded to 16'

    $forbiddenCommands = @('netsh', 'New-NetFirewallRule', 'Set-NetFirewallProfile', 'Set-NetIPInterface',
        'Stop-Service', 'Start-Service', 'Set-Service', 'New-Service', 'sc.exe', 'reg.exe')
    $commandNames = @($productionAst.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.CommandAst]
            }, $true) | ForEach-Object { $_.GetCommandName() } | Where-Object { $_ } | Select-Object -Unique)
    $hits = @($commandNames | Where-Object { $forbiddenCommands -contains $_ })
    Assert-True ($hits.Count -eq 0) "S0 no machine-wide network/service/firewall mutation command (hits: $($hits -join ','))"
}
catch {
    Assert-True $false "S0 threw: $($_.Exception.Message)"
}

# =============================================================================
# S1  real bind-probe matrix
# =============================================================================
Write-Host ''
Write-Host '=== S1: real bind-probe matrix ==='
$holder = $null
$listener = $null
try {
    $freePort = Get-PortBlock 3
    $probe = Get-PortProbe -Value $freePort
    Assert-Equal 'Bindable' $probe.state 'S1 free loopback port probes Bindable'

    $listener = New-LoopbackListener -Port $freePort
    $probe = Get-PortProbe -Value $freePort
    Assert-Equal 'InUse' $probe.state 'S1 real loopback listener probes InUse'
    Assert-True (Test-TcpConnect -Port $freePort) 'S1 the loopback listener is reachable (control)'
    $listener.Stop()
    $listener = $null
    Start-Sleep -Milliseconds 150

    $exclusivePort = $freePort + 1
    $holder = New-ExclusiveHolder -Port $exclusivePort
    Assert-True (-not (Test-TcpConnect -Port $exclusivePort)) 'S1 exclusive holder accepts no TCP connection (the exact CI blind spot)'
    $probe = Get-PortProbe -Value $exclusivePort
    Assert-Equal 'AccessDenied' $probe.state 'S1 exclusive holder makes the bind probe AccessDenied'
    Assert-Equal '10013' "$($probe.errorCode)" 'S1 AccessDenied carries socket error 10013 (WSAEACCES)'
    Assert-Match $probe.detail 'forbidden|access|permission' 'S1 AccessDenied detail is the real OS permission message'

    $holder.Dispose()
    $holder = $null
    Start-Sleep -Milliseconds 200
    Assert-Equal 'Bindable' (Get-PortProbe -Value $exclusivePort).state 'S1 probe released its socket; port is bindable again'
    Assert-True (-not (Test-TcpConnect -Port $freePort)) 'S1 reachability probe is false after listener stop (control)'

    Assert-Equal 'Reserved' (Get-PortProbe -Value 5432).state 'S1 reserved 5432 is rejected without probing'
    Assert-Equal 'Reserved' (Get-PortProbe -Value 55460).state 'S1 reserved lobby 55460 is rejected'
    Assert-Equal 'Invalid' (Get-PortProbe -Value 0).state 'S1 port 0 is invalid'
    Assert-Equal 'Invalid' (Get-PortProbe -Value 70000).state 'S1 port 70000 is invalid'
}
catch {
    Assert-True $false "S1 threw: $($_.Exception.Message)"
}
finally {
    if ($null -ne $holder) { $holder.Dispose() }
    if ($null -ne $listener) { try { $listener.Stop() } catch { } }
}

# =============================================================================
# S2  production selector semantics (explicit vs default)
# =============================================================================
Write-Host ''
Write-Host '=== S2: selector semantics ==='
try {
    $block = Get-PortBlock 4
    $explicit = Resolve-TablePort -RequestedPort $block -ExplicitPort
    Assert-Equal 'explicit' $explicit.mode 'S2 explicit mode is reported'
    Assert-Equal "$block" "$($explicit.port)" 'S2 explicit bindable port is used exactly'

    $defaultDecision = Resolve-TablePort -DefaultPort $block -CandidateLimit 2
    Assert-Equal 'default' $defaultDecision.mode 'S2 default mode is reported'
    Assert-Equal "$block" "$($defaultDecision.port)" 'S2 default mode takes the first bindable candidate'

    $skipsLobby = Resolve-TablePort -DefaultPort $block -CandidateLimit 3 -ExcludedPorts @($block)
    Assert-Equal "$($block + 1)" "$($skipsLobby.port)" 'S2 default mode skips the lobby port'
    Assert-Match ($skipsLobby.candidates -join ';') "$block=lobby-excluded" 'S2 lobby skip is named in candidates'

    $exclusivePort = $block + 2
    $holder = New-ExclusiveHolder -Port $exclusivePort
    try {
        $null = Resolve-TablePort -RequestedPort $exclusivePort -ExplicitPort
        Assert-True $false 'S2 explicit AccessDenied port must throw'
    }
    catch {
        Assert-Match $_.Exception.Message 'BLOCKED.*AccessDenied.*10013.*Refusing to substitute' 'S2 explicit AccessDenied refuses and never substitutes'
    }
    Assert-True (-not (Test-TcpConnect -Port $exclusivePort)) 'S2 explicit refusal did not disturb the exclusive holder'

    $moved = Resolve-TablePort -DefaultPort $exclusivePort -CandidateLimit 2
    Assert-Equal "$($exclusivePort + 1)" "$($moved.port)" 'S2 default mode moves off an AccessDenied candidate'
    Assert-Match ($moved.candidates -join ';') "$exclusivePort=AccessDenied" 'S2 AccessDenied candidate state is recorded'
    $holder.Dispose()
    $holder = $null
    Start-Sleep -Milliseconds 150

    try { $null = Resolve-TablePort -RequestedPort 0 -ExplicitPort; Assert-True $false 'S2 explicit invalid port must throw' }
    catch { Assert-Match $_.Exception.Message 'BLOCKED.*Invalid' 'S2 explicit invalid port is BLOCKED' }
    try { $null = Resolve-TablePort -RequestedPort 55460 -ExplicitPort; Assert-True $false 'S2 explicit reserved port must throw' }
    catch { Assert-Match $_.Exception.Message 'BLOCKED.*Reserved' 'S2 explicit reserved port is BLOCKED' }
    try { $null = Resolve-TablePort -RequestedPort $block -ExplicitPort -ExcludedPorts @($block); Assert-True $false 'S2 explicit lobby conflict must throw' }
    catch { Assert-Match $_.Exception.Message 'BLOCKED.*lobby port' 'S2 explicit lobby conflict is BLOCKED' }
    try { $null = Resolve-TablePort -DefaultPort $block -CandidateLimit 0; Assert-True $false 'S2 zero candidate limit must throw' }
    catch { Assert-Match $_.Exception.Message 'BLOCKED.*candidate limit' 'S2 zero candidate limit is BLOCKED' }

    # Dependency injection: deterministic candidate exhaustion and partial
    # selection on runtimes where a synthetic reservation cannot be created.
    # The real exclusive-holder cases above remain the primary evidence.
    $script:injectedAll = {
        param($Candidate, $Reserved)
        return [ordered]@{ port = $Candidate; address = '127.0.0.1'; state = 'AccessDenied'; errorCode = 10013; detail = 'injected WSAEACCES for deterministic exhaustion' }
    }
    try {
        $null = Resolve-TablePort -DefaultPort $block -CandidateLimit 2 -Probe $script:injectedAll
        Assert-True $false 'S2 injected all-rejected scan must throw'
    }
    catch {
        Assert-Match $_.Exception.Message "$block=AccessDenied.*Refusing to guess" 'S2 injected exhaustion names every candidate and refuses to guess'
    }
    $script:injectedSecond = {
        param($Candidate, $Reserved)
        if ($Candidate -eq $block) {
            return [ordered]@{ port = $Candidate; address = '127.0.0.1'; state = 'AccessDenied'; errorCode = 10013; detail = 'injected first-candidate reservation' }
        }
        return [ordered]@{ port = $Candidate; address = '127.0.0.1'; state = 'Bindable'; errorCode = 0; detail = '' }
    }
    $injectedSelection = Resolve-TablePort -DefaultPort $block -CandidateLimit 2 -Probe $script:injectedSecond
    Assert-Equal "$($block + 1)" "$($injectedSelection.port)" 'S2 injected partial selection moves to the next candidate (DI seam)'
}
catch {
    Assert-True $false "S2 threw: $($_.Exception.Message)"
}
finally {
    if ($null -ne $holder) { $holder.Dispose() }
}

# =============================================================================
# S3  end-to-end refusals: real listener and real exclusive holder
# =============================================================================
Write-Host ''
Write-Host '=== S3: end-to-end foreign-occupancy refusals ==='
$listener = $null
$holder = $null
try {
    $block = Get-PortBlock 2
    $listenerPort = $block
    $listener = New-LoopbackListener -Port $listenerPort
    $run = Invoke-Wrapper -Arguments @{
        Port = $listenerPort; DotnetPath = $DotnetPath
        Root = (Join-Path $evidenceRoot 's3-listener\root')
        LogDir = (Join-Path $evidenceRoot 's3-listener\log')
    } -EvidenceDir (Join-Path $evidenceRoot 's3-listener')
    Assert-Equal '2' "$($run.exit)" 'S3 real listener: production script exits BLOCKED 2'
    $summary = Read-Summary $run.dir
    $resultStage = Get-Stage $summary 'result'
    Assert-Equal 'BLOCKED' $resultStage.status 'S3 real listener: result stage is BLOCKED'
    Assert-Match $resultStage.detail 'InUse|active TCP listener' 'S3 real listener: failure names the active listener'
    Assert-True ($null -eq (Get-Stage $summary 'pg-start')) 'S3 real listener: pg_ctl was never started'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $evidenceRoot 's3-listener\root'))) 'S3 real listener: no cluster root was created'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $run.log 'pg-start.log'))) 'S3 real listener: no pg-start.log produced'
    Assert-True (Test-TcpConnect -Port $listenerPort) 'S3 real listener: foreign listener survived and still accepts'
    Assert-True ([bool]$summary.environmentRestored) 'S3 real listener: finally restored the process environment'
    Assert-Match $run.text 'P52-TABLE-POSTGRES-BLOCKED' 'S3 real listener: console reports BLOCKED, not PASS/FAIL'
    $listener.Stop()
    $listener = $null

    $exclusivePort = $block + 1
    $holder = New-ExclusiveHolder -Port $exclusivePort
    $run2 = Invoke-Wrapper -Arguments @{
        Port = $exclusivePort; DotnetPath = $DotnetPath
        Root = (Join-Path $evidenceRoot 's3-eacces\root')
        LogDir = (Join-Path $evidenceRoot 's3-eacces\log')
    } -EvidenceDir (Join-Path $evidenceRoot 's3-eacces')
    Assert-Equal '2' "$($run2.exit)" 'S3 exclusive holder: production script exits BLOCKED 2'
    $summary2 = Read-Summary $run2.dir
    $resultStage2 = Get-Stage $summary2 'result'
    Assert-Match $resultStage2.detail 'AccessDenied.*10013' 'S3 exclusive holder: failure is real WSAEACCES 10013'
    Assert-Match $resultStage2.detail 'Refusing to substitute' 'S3 exclusive holder: no silent port substitution'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $evidenceRoot 's3-eacces\root'))) 'S3 exclusive holder: no cluster root was created'
    Assert-True (-not (Test-TcpConnect -Port $exclusivePort)) 'S3 exclusive holder: holder still holds the port (not stopped/attached)'
    Assert-Equal "$exclusivePort" "$($summary2.port)" 'S3 exclusive holder: summary keeps the requested port, not a substitute'
}
catch {
    Assert-True $false "S3 threw: $($_.Exception.Message)"
}
finally {
    if ($null -ne $listener) { try { $listener.Stop() } catch { } }
    if ($null -ne $holder) { $holder.Dispose() }
}

# =============================================================================
# S4  end-to-end explicit invalid / reserved / lobby conflict
# =============================================================================
Write-Host ''
Write-Host '=== S4: explicit invalid, reserved and lobby conflict ==='
try {
    $block = Get-PortBlock 1
    $cases = @(
        @{ Name = 'invalid-zero'; Port = 0; Pattern = 'Invalid'; Lobby = 0; Unified = $false },
        @{ Name = 'invalid-high'; Port = 70000; Pattern = 'Invalid'; Lobby = 0; Unified = $false },
        @{ Name = 'reserved'; Port = 55460; Pattern = 'Reserved'; Lobby = 0; Unified = $false },
        @{ Name = 'lobby-conflict'; Port = $block; Pattern = 'lobby port'; Lobby = $block; Unified = $true }
    )
    foreach ($case in $cases) {
        $dir = Join-Path $evidenceRoot ('s4-' + $case.Name)
        $arguments = @{
            Port = $case.Port; DotnetPath = $DotnetPath
            Root = (Join-Path $dir 'root'); LogDir = (Join-Path $dir 'log')
        }
        if ($case.Unified) { $arguments['RunUnifiedVerification'] = $true; $arguments['LobbyPort'] = $case.Lobby }
        $run = Invoke-Wrapper -Arguments $arguments -EvidenceDir $dir
        $summary = Read-Summary $dir
        $resultStage = Get-Stage $summary 'result'
        Assert-Equal '2' "$($run.exit)" "S4 $($case.Name): exit 2 BLOCKED"
        Assert-Equal 'BLOCKED' $resultStage.status "S4 $($case.Name): result stage BLOCKED"
        Assert-Match $resultStage.detail $case.Pattern "S4 $($case.Name): detail names $($case.Pattern)"
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $dir 'root'))) "S4 $($case.Name): no cluster root"
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $dir 'log\tests.xml'))) "S4 $($case.Name): no tests were run"
    }
}
catch {
    Assert-True $false "S4 threw: $($_.Exception.Message)"
}

# =============================================================================
# S5  end-to-end default-mode candidate exhaustion
# =============================================================================
Write-Host ''
Write-Host '=== S5: default-mode candidate exhaustion ==='
$holders = New-Object System.Collections.Generic.List[object]
try {
    $block = Get-PortBlock 4
    for ($i = 0; $i -lt 3; $i++) { $holders.Add((New-ExclusiveHolder -Port ($block + $i))) }
    $dir = Join-Path $evidenceRoot 's5-exhaustion'
    $run = Invoke-Wrapper -Arguments @{
        DefaultPort = $block; PortCandidateLimit = 3; DotnetPath = $DotnetPath
        Root = (Join-Path $dir 'root'); LogDir = (Join-Path $dir 'log')
    } -EvidenceDir $dir
    $summary = Read-Summary $dir
    $resultStage = Get-Stage $summary 'result'
    Assert-Equal '2' "$($run.exit)" 'S5 exhausted default scan exits BLOCKED 2'
    Assert-Equal 'BLOCKED' $resultStage.status 'S5 exhausted default scan result stage BLOCKED'
    for ($i = 0; $i -lt 3; $i++) {
        Assert-Match $resultStage.detail "$($block + $i)=AccessDenied" "S5 candidate $($block + $i) is named AccessDenied"
    }
    Assert-Match $resultStage.detail 'Refusing to guess an unverified port' 'S5 exhaustion explicitly refuses an unverified fallback'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $dir 'root'))) 'S5 no cluster root was created'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $run.log 'pg-start.log'))) 'S5 pg_ctl was never started'
    for ($i = 0; $i -lt 3; $i++) {
        Assert-True (-not (Test-TcpConnect -Port ($block + $i))) "S5 holder $($block + $i) survived untouched"
    }
}
catch {
    Assert-True $false "S5 threw: $($_.Exception.Message)"
}
finally {
    foreach ($socket in $holders) { $socket.Dispose() }
}

# =============================================================================
# S6  env restore and ownership non-regression
# =============================================================================
Write-Host ''
Write-Host '=== S6: env restore and ownership guard ==='
try {
    # Windows cannot represent a present-empty process variable: assigning ''
    # deletes it (both the provider and .NET Environment). The meaningful
    # cases are present-nonempty and absent, tested here and by a fresh child.
    $names = @('MJ_SELFTEST_SET', 'MJ_SELFTEST_UNSET')
    $env:MJ_SELFTEST_SET = 'sentinel-set'
    Remove-Item Env:MJ_SELFTEST_UNSET -ErrorAction SilentlyContinue
    $snapshot = @{}
    foreach ($name in $names) { $snapshot[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }

    $env:MJ_SELFTEST_SET = 'mutated'
    $env:MJ_SELFTEST_UNSET = 'mutated-unset'
    $records = @(Restore-ProcessEnvironment -Snapshot $snapshot)
    Assert-Equal '2' "$($records.Count)" 'S6 production restore returned one record per variable'
    Assert-True (@($records | Where-Object { -not $_.restored }).Count -eq 0) 'S6 every variable reports restored=true'
    Assert-Equal 'sentinel-set' $env:MJ_SELFTEST_SET 'S6 set value restored exactly'
    Assert-True (-not (Test-Path Env:MJ_SELFTEST_UNSET)) 'S6 originally-unset variable is absent again'
    $recordSet = $records | Where-Object { $_.variable -eq 'MJ_SELFTEST_SET' }
    Assert-Equal (Get-TextSha256 'sentinel-set') $recordSet.restoredSha256 'S6 restored evidence is a value hash, not the value'
    $recordUnset = $records | Where-Object { $_.variable -eq 'MJ_SELFTEST_UNSET' }
    Assert-Equal '(unset)' $recordUnset.restoredSha256 'S6 unset evidence is recorded as (unset)'

    $childCommand = "Write-Output ('SET=' + `$env:MJ_SELFTEST_SET); Write-Output ('UNSET_EXISTS=' + (Test-Path Env:MJ_SELFTEST_UNSET))"
    $childOutput = (& powershell.exe -NoProfile -Command $childCommand | Out-String)
    Assert-Match $childOutput 'SET=sentinel-set' 'S6 fresh child sees the restored set value'
    Assert-Match $childOutput 'UNSET_EXISTS=False' 'S6 fresh child sees the unset variable as absent'
    foreach ($name in $names) { Remove-Item -LiteralPath ('Env:' + $name) -ErrorAction SilentlyContinue }

    # Ownership: a foreign marker must never authorize touching/deleting the root.
    $foreignRoot = Join-Path $evidenceRoot 's6-foreign-root'
    New-Item -ItemType Directory -Force $foreignRoot | Out-Null
    Set-Content -LiteralPath (Join-Path $foreignRoot 'sentinel.txt') -Value 'must-survive' -Encoding utf8
    [ordered]@{
        owner = 'someone-else'; root = $foreignRoot; port = ($BasePort + 100)
        database = 'mahjong_p52_test'; createdUtc = (Get-Date).ToUniversalTime().ToString('o')
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $foreignRoot 'pg-owner.json') -Encoding utf8
    $previousRoot = $Root
    $previousPort = $Port
    $previousMarker = $script:Marker
    $Root = $foreignRoot
    $Port = $BasePort + 100
    $script:Marker = Join-Path $foreignRoot 'pg-owner.json'
    try {
        $null = Assert-OwnedCluster
        Assert-True $false 'S6 foreign ownership marker must throw'
    }
    catch {
        Assert-Match $_.Exception.Message 'does not belong|ownership marker' 'S6 foreign ownership marker is refused'
    }
    Assert-True (Test-Path -LiteralPath (Join-Path $foreignRoot 'sentinel.txt')) 'S6 foreign root content was preserved'
    $Root = $previousRoot
    $Port = $previousPort
    $script:Marker = $previousMarker

    $dir = Join-Path $evidenceRoot 's6-foreign-run'
    $run = Invoke-Wrapper -Arguments @{
        Port = (Get-PortBlock 1); DotnetPath = $DotnetPath
        Root = $foreignRoot; LogDir = (Join-Path $dir 'log')
    } -EvidenceDir $dir
    $summary = Read-Summary $dir
    Assert-Equal '1' "$($run.exit)" 'S6 full script on a foreign root exits FAIL 1'
    Assert-Equal 'FAIL' (Get-Stage $summary 'cleanup').status 'S6 cleanup refuses the foreign root (FAIL stage)'
    Assert-True (Test-Path -LiteralPath (Join-Path $foreignRoot 'sentinel.txt')) 'S6 full script preserved the foreign root'
    Assert-Match ((@($summary.lines) -join ' ')) 'Refusing to touch' 'S6 full script names the ownership refusal'
    Remove-OwnedTree -Path $foreignRoot -GuardRoot $evidenceRoot
}
catch {
    Assert-True $false "S6 threw: $($_.Exception.Message)"
}
finally {
    foreach ($name in @('MJ_SELFTEST_SET', 'MJ_SELFTEST_UNSET')) {
        Remove-Item -LiteralPath ('Env:' + $name) -ErrorAction SilentlyContinue
    }
}

# =============================================================================
# S7  REAL PostgreSQL 16.10 start / query / immediate crash restart / cleanup
# =============================================================================
Write-Host ''
Write-Host '=== S7: real PostgreSQL 16.10 lifecycle ==='
$realRoot = Join-Path $evidenceRoot 'real-pg\root'
$realLog = Join-Path $evidenceRoot 'real-pg\logs'
$script:Root = $realRoot
$script:LogDir = $realLog
$script:PgBin = Join-Path $realRoot 'pgsql\bin'
$script:PgCtl = Join-Path $script:PgBin 'pg_ctl.exe'
$script:InitDb = Join-Path $script:PgBin 'initdb.exe'
$script:Psql = Join-Path $script:PgBin 'psql.exe'
$script:Createdb = Join-Path $script:PgBin 'createdb.exe'
$script:Dropdb = Join-Path $script:PgBin 'dropdb.exe'
$script:PgIsReady = Join-Path $script:PgBin 'pg_isready.exe'
$script:PgDump = Join-Path $script:PgBin 'pg_dump.exe'
$script:PgRestore = Join-Path $script:PgBin 'pg_restore.exe'
$script:DataDir = Join-Path $realRoot 'data'
$script:Marker = Join-Path $realRoot 'pg-owner.json'
$script:PgLog = Join-Path $realRoot 'logs\postgres.log'
$script:Database = 'mahjong_p52_test'
$script:RestoreDatabase = 'mahjong_p52_restore'
$script:PgZipSha256 = 'EBB3B6AF4FA69DEA9951B66855BC4D42DC04E56CCB9AA7024CE3C58BD89D6B0C'
$RunUnifiedVerification = $false
$PgZipPath = Get-PgZipCachePath
$script:Port = 0
try {
    New-Item -ItemType Directory -Force -Path $realLog | Out-Null
    Reset-ProductionState
    $block = Get-PortBlock 3
    $decision = Resolve-TablePort -DefaultPort $block -CandidateLimit 3
    Assert-Equal 'default' $decision.mode 'S7 real PG uses the production default-mode selector'
    Assert-Equal "$block" "$($decision.port)" 'S7 real PG default mode selects the first bindable candidate'
    $script:Port = [int]$decision.port

    Initialize-Cluster
    $serverVersion = Get-PgScalar -Database $script:Database -Sql 'SHOW server_version'
    $serverPort = Get-PgScalar -Database $script:Database -Sql 'SHOW port'
    $dataDirectory = Get-PgDataDirectory
    Assert-Equal '16.10' $serverVersion.Trim() 'S7 real PostgreSQL reports 16.10'
    Assert-Equal "$($script:Port)" $serverPort.Trim() 'S7 real PostgreSQL listens on the selected port'
    Assert-Equal ([System.IO.Path]::GetFullPath($script:DataDir)) ([System.IO.Path]::GetFullPath($dataDirectory)) 'S7 server data_directory is this run''s owned root'
    Assert-True (Test-Path -LiteralPath $script:Marker) 'S7 ownership marker exists'
    $marker = Get-Content -LiteralPath $script:Marker -Raw | ConvertFrom-Json
    Assert-Equal 'mahjong-p52-pg' $marker.owner 'S7 marker owner is the P5.2 wrapper'
    Assert-Equal ([System.IO.Path]::GetFullPath($realRoot)) ([System.IO.Path]::GetFullPath($marker.root)) 'S7 marker root matches this run'
    Assert-Equal "$($script:Port)" "$($marker.port)" 'S7 marker port matches this run'
    $listeners = @([System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() |
        Where-Object { $_.Port -eq $script:Port })
    Assert-True ($listeners.Count -gt 0) 'S7 PostgreSQL has a real TCP listener'
    Assert-True (@($listeners | Where-Object { -not [System.Net.IPAddress]::IsLoopback($_.Address) }).Count -eq 0) 'S7 listener is loopback-only'
    $startStage = Get-Stage ([ordered]@{ stages = $script:Stages }) 'pg-start'
    Assert-Equal 'PASS' $startStage.status 'S7 production pg-start stage is PASS'
    Assert-Equal '0' "$($startStage.exitCode)" 'S7 pg_ctl native exit is 0'

    $null = Get-PgScalar -Database $script:Database -Sql 'CREATE TABLE startup_probe(v integer); INSERT INTO startup_probe VALUES (42);'
    $restart = Restart-ClusterImmediate
    Assert-True ($restart.before -ne $restart.after) 'S7 postmaster start time changed across the immediate restart'
    Assert-True ($restart.recoveryLines.Count -gt 0) 'S7 immediate restart produced recovery log evidence'
    Assert-Equal '42' (Get-PgScalar -Database $script:Database -Sql 'SELECT v FROM startup_probe').Trim() 'S7 committed row survived the crash restart'

    $serverLog = Get-Content -LiteralPath $script:PgLog -Raw
    Assert-Match $serverLog 'starting PostgreSQL 16\.10' 'S7 server log names PostgreSQL 16.10'
    Assert-NotMatch $serverLog 'Permission denied|could not create any TCP/IP sockets' 'S7 no bind failure in the healthy run'

    $stopLog = Join-Path $realLog 'pg-selftest-stop.log'
    $stopCode = Invoke-Native -FilePath $script:PgCtl -Arguments @(
        '-D', $script:DataDir, '-w', '-t', '60', 'stop', '-m', 'fast'
    ) -LogPath $stopLog -TimeoutSeconds 90
    Assert-Equal '0' "$stopCode" 'S7 clean pg_ctl stop exits 0'
    $script:ClusterStarted = $false
    Start-Sleep -Milliseconds 400
    $remaining = @(Get-CimInstance Win32_Process -Filter "Name = 'postgres.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($script:DataDir, [StringComparison]::OrdinalIgnoreCase) -ge 0 })
    Assert-Equal '0' "$($remaining.Count)" 'S7 no postgres.exe process references this run''s data directory'

    # Preserve a stopped data directory for the S8 real startup-failure run.
    Copy-Item -LiteralPath $script:DataDir -Destination (Join-Path $evidenceRoot 's8-data') -Recurse -Force
    Assert-True (Test-Path -LiteralPath (Join-Path $evidenceRoot 's8-data\PG_VERSION')) 'S7 stopped data directory was preserved for S8'
    Remove-OwnedTree -Path $realRoot -GuardRoot $evidenceRoot
    Assert-True (-not (Test-Path -LiteralPath $realRoot)) 'S7 ownership-checked recursive cleanup removed only this run''s root'
}
catch {
    Assert-True $false "S7 threw: $($_.Exception.Message)"
}
finally {
    if (Test-Path -LiteralPath $realRoot) {
        try {
            if (Test-TcpListenerReachable -Value $script:Port) {
                $stopLog = Join-Path $realLog 'pg-selftest-finally-stop.log'
                $null = Invoke-Native -FilePath $script:PgCtl -Arguments @('-D', $script:DataDir, '-w', '-t', '60', 'stop', '-m', 'immediate') -LogPath $stopLog -TimeoutSeconds 90
            }
        }
        catch { }
        try { Remove-OwnedTree -Path $realRoot -GuardRoot $evidenceRoot } catch { }
    }
}

# =============================================================================
# S8  REAL pg_ctl startup failure through the full production script
# =============================================================================
Write-Host ''
Write-Host '=== S8: real native startup failure and log preservation ==='
$failRoot = $null
try {
    $savedData = Join-Path $evidenceRoot 's8-data'
    Assert-True (Test-Path -LiteralPath (Join-Path $savedData 'PG_VERSION')) 'S8 preconditions: stopped real data directory is available'
    $failPort = Get-PortBlock 1
    $failRoot = Join-Path $repoRoot ('artifacts\ci-pg-startup-fail-' + [guid]::NewGuid().ToString('N').Substring(0, 10))
    New-Item -ItemType Directory -Force -Path (Join-Path $failRoot 'data') | Out-Null
    Copy-Item -Path (Join-Path $savedData '*') -Destination (Join-Path $failRoot 'data') -Recurse -Force
    [ordered]@{
        owner = 'mahjong-p52-pg'; root = $failRoot; port = $failPort
        database = 'mahjong_p52_test'; createdUtc = (Get-Date).ToUniversalTime().ToString('o')
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $failRoot 'pg-owner.json') -Encoding utf8
    $confPath = Join-Path $failRoot 'data\postgresql.conf'
    $invalidSetting = [Environment]::NewLine + '# self-test: deterministic native startup failure' + [Environment]::NewLine +
        "port = $failPort" + [Environment]::NewLine +
        'shared_buffers = ''definitely-not-a-valid-size''' + [Environment]::NewLine
    [System.IO.File]::AppendAllText($confPath, $invalidSetting, (New-Object System.Text.UTF8Encoding($false)))

    $dir = Join-Path $evidenceRoot 's8-start-failure'
    $run = Invoke-Wrapper -Arguments @{
        Port = $failPort; Root = $failRoot; PgZipPath = $ZipPath; DotnetPath = $DotnetPath
        LogDir = (Join-Path $dir 'log')
    } -EvidenceDir $dir
    $summary = Read-Summary $dir
    $pgStart = Get-Stage $summary 'pg-start'
    Assert-Equal '1' "$($run.exit)" 'S8 real startup failure exits FAIL 1'
    Assert-Equal 'FAIL' $pgStart.status 'S8 pg-start stage is FAIL'
    Assert-Equal '1' "$($pgStart.exitCode)" 'S8 pg-start stage records the native pg_ctl exit 1'
    Assert-Match $pgStart.detail 'native exit=1' 'S8 stage detail names native exit 1'
    $wrapperLog = Get-Content -LiteralPath (Join-Path $dir 'log\wrapper.log') -Raw
    Assert-Match $wrapperLog 'pg_ctl start failed with native exit 1' 'S8 wrapper log records the native exit, not a generic failure'
    Assert-True ($null -eq (Get-Stage $summary 'tests')) 'S8 no tests stage (failure occurred before unified tests)'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $dir 'log\tests.xml'))) 'S8 no tests.xml; not a business-test failure'
    Assert-True (Test-Path -LiteralPath (Join-Path $dir 'log\pg-start.log')) 'S8 original pg-start.log is preserved'
    $pgStartLog = Get-Content -LiteralPath (Join-Path $dir 'log\pg-start.log') -Raw
    Assert-Match $pgStartLog 'pg_ctl' 'S8 preserved pg_ctl log is the real pg_ctl output'
    Assert-Match $pgStartLog 'pg_ctl' 'S8 preserved pg_ctl log is real pg_ctl output (failure text is OS-locale dependent)'
    Assert-True (Test-Path -LiteralPath (Join-Path $dir 'log\pg\postgres.log')) 'S8 original server postgres.log is preserved'
    $serverLog = Get-Content -LiteralPath (Join-Path $dir 'log\pg\postgres.log') -Raw
    Assert-Match $serverLog 'FATAL' 'S8 preserved server log contains the PostgreSQL FATAL'
    Assert-Match $serverLog 'invalid value for parameter|shared_buffers' 'S8 preserved server log shows the real native startup failure cause'
    Assert-True (Test-Path -LiteralPath (Join-Path $dir 'log\pg-start-diagnostics.txt')) 'S8 dedicated native-exit diagnostics file exists'
    $diagnostics = Get-Content -LiteralPath (Join-Path $dir 'log\pg-start-diagnostics.txt') -Raw
    Assert-Match $diagnostics 'pg_ctl native exit: 1' 'S8 diagnostics file records native exit 1'
    $attempts = ([regex]::Matches($serverLog, 'invalid value for parameter')).Count
    Assert-Equal '1' "$attempts" 'S8 exactly one failed startup attempt (no blind retry)'
    Assert-NotMatch $serverLog 'database system is ready' 'S8 no successful start or retry in the preserved server log'
    Assert-True (-not (Test-Path -LiteralPath $failRoot)) 'S8 ownership-verified cleanup removed the failed cluster root'
    Assert-Match $run.text 'P52-TABLE-POSTGRES-FAIL' 'S8 console reports FAIL, not PASS/BLOCKED'
}
catch {
    Assert-True $false "S8 threw: $($_.Exception.Message)"
}
finally {
    if ($null -ne $failRoot -and (Test-Path -LiteralPath $failRoot)) {
        try { Remove-OwnedTree -Path $failRoot -GuardRoot (Join-Path $repoRoot 'artifacts') } catch { }
    }
}

# =============================================================================
# S9  full script: finally restores caller env after a real owned start
# =============================================================================
Write-Host ''
Write-Host '=== S9: full-script env restore after a real owned start ==='
$envRoot = $null
try {
    $fakeDir = Join-Path $evidenceRoot 's9-fake-dotnet'
    New-Item -ItemType Directory -Force -Path $fakeDir | Out-Null
    $fakeExe = Join-Path $fakeDir 'fake-dotnet.exe'
    $fakeSource = @'
using System;
public class FakeDotnet {
  public static int Main(string[] args) {
    if (args.Length == 1 && args[0] == "--version") { Console.WriteLine("8.0.426"); return 0; }
    Console.Error.WriteLine("fake dotnet: build refused"); return 9;
  }
}
'@
    Add-Type -TypeDefinition $fakeSource -OutputAssembly $fakeExe -OutputType ConsoleApplication -ReferencedAssemblies 'System.dll'
    Assert-True (Test-Path -LiteralPath $fakeExe) 'S9 fake dotnet helper built'

    $envRoot = Join-Path $repoRoot ('artifacts\ci-pg-startup-env-' + [guid]::NewGuid().ToString('N').Substring(0, 10))
    $envPort = Get-PortBlock 1
    $sentinel = 'env-sentinel-' + [guid]::NewGuid().ToString('N')
    $previousSentinel = $env:MAHJONG_P52_TEST_POSTGRES
    $env:MAHJONG_P52_TEST_POSTGRES = $sentinel
    try {
        $dir = Join-Path $evidenceRoot 's9-env-restore'
        $run = Invoke-Wrapper -Arguments @{
            Port = $envPort; Root = $envRoot; PgZipPath = $ZipPath; DotnetPath = $fakeExe
            LogDir = (Join-Path $dir 'log')
        } -EvidenceDir $dir
    }
    finally {
        if ($null -eq $previousSentinel) { Remove-Item Env:MAHJONG_P52_TEST_POSTGRES -ErrorAction SilentlyContinue }
        else { $env:MAHJONG_P52_TEST_POSTGRES = $previousSentinel }
    }
    $summary = Read-Summary $dir
    Assert-Equal '1' "$($run.exit)" 'S9 fake build failure exits FAIL 1'
    Assert-Equal 'PASS' (Get-Stage $summary 'cluster').status 'S9 real cluster started before the build failure'
    Assert-Match (Get-Stage $summary 'result').detail 'Fresh test build failed with exit 9' 'S9 failure is the build native exit 9, not a PG failure'
    Assert-True ([bool]$summary.environmentRestored) 'S9 finally reports every process variable restored'
    $sentinelRecord = @($summary.environment | Where-Object { $_.variable -eq 'MAHJONG_P52_TEST_POSTGRES' }) | Select-Object -First 1
    Assert-True ($null -ne $sentinelRecord -and [bool]$sentinelRecord.wasSetBeforeRun) 'S9 sentinel was set before the run and recorded'
    Assert-Equal (Get-TextSha256 $sentinel) $sentinelRecord.restoredSha256 'S9 restored value matches the caller sentinel by hash'
    Assert-True (@($summary.environment | Where-Object { -not $_.restored }).Count -eq 0) 'S9 no variable failed restoration'
    Assert-True (-not (Test-Path -LiteralPath $envRoot)) 'S9 owned cluster was stopped and removed in finally'
}
catch {
    Assert-True $false "S9 threw: $($_.Exception.Message)"
}
finally {
    if ($null -ne $envRoot -and (Test-Path -LiteralPath $envRoot)) {
        try { Remove-OwnedTree -Path $envRoot -GuardRoot (Join-Path $repoRoot 'artifacts') } catch { }
    }
}

# =============================================================================
# Evidence, counts and exit code
# =============================================================================
$notRun = 0
$skip = 0
try { Stop-Transcript | Out-Null } catch { }
$summaryObject = [ordered]@{
    generatedUtc = (Get-Date).ToUniversalTime().ToString('o')
    psVersion = "$($PSVersionTable.PSVersion)"
    os = [Environment]::OSVersion.VersionString
    dotnet = $DotnetPath
    pgZip = $ZipPath
    pgZipSha256 = $zipSha256
    basePort = $BasePort
    checks = $script:checks
    passed = $script:passed
    failed = $script:failed
    skipped = $skip
    notRun = $notRun
    failures = $script:failures
}
$summaryObject | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'selftest-summary.json') -Encoding utf8
$manifest = @()
Get-ChildItem -LiteralPath $evidenceRoot -File -Recurse | Sort-Object FullName | ForEach-Object {
    $manifest += [ordered]@{
        path = $_.FullName.Substring($evidenceRoot.Length).TrimStart('\')
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        bytes = $_.Length
    }
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'manifest.sha256.json') -Encoding utf8

Write-Host ''
Write-Host "TOTAL=$($script:checks) PASS=$($script:passed) FAIL=$($script:failed) SKIP=$skip NOT_RUN=$notRun"
Write-Host "evidence=$evidenceRoot"
if ($script:failed -gt 0 -or $script:checks -eq 0) { exit 1 }
exit 0
