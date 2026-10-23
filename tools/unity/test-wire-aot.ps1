<#
.SYNOPSIS
    Reproducible P2.2 probe for the generated Wire codec: .NET, Unity Editor,
    Unity Roslyn compilation and optional Windows/Android IL2CPP build/runtime.

.DESCRIPTION
    This script is the single entry point and owns every helper it needs. The
    netstandard2.1 probe project, the net8.0 runner and the IL2CPP Editor build
    helper are generated under build\p22-aot\<stamp>\probe (or build\p22-aot\
    <stamp>\il2cpp-helper). Nothing outside build/ and a fresh OS temp directory
    is written, so the repository keeps only its six accepted P2.2 paths.

    .NET leg: builds and runs the generated probe project, which directly
    consumes Mahjong.Contracts.Wire without reflection-based serialization.

    Unity leg: copies the Unity project to a fresh temp directory, adds the
    generated assemblies there, and invokes:

        Unity.exe -batchmode -quit -nographics -projectPath <temp> ^
            -executeMethod Mahjong.Unity.Platform.WireAotSmoke.Run

    The real project's Assets/Plugins and ProjectSettings are never modified.
    The Unity script and the generated IL2CPP helper are also compiled with the
    Editor's bundled Roslyn against the installed Unity assemblies, which is a
    compile check only, not an Editor import.

    IL2CPP leg (-Il2cppTarget Windows|Android): inventories the actual host
    toolchain, then builds through BuildPlayerOptions with
    extraScriptingDefines = MAHJONG_WIRE_AOT_SMOKE, an explicit BuildTarget and
    an explicit IL2CPP scripting backend. No temporary define mutation and no
    reliance on an asynchronous Editor recompile. Missing prerequisites are
    reported as BLOCKED and a player/APK build is never reported as a runtime
    pass.

    Statuses: PASS, FAIL, BLOCKED, NOT_RUN. Exit 0 only when every requested
    check passed; 1 on FAIL; 2 on BLOCKED or NOT_RUN.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.PARAMETER SkipUnity
    Run only the .NET leg. Unity and IL2CPP checks are BLOCKED.

.PARAMETER BuildOnly
    Build the IL2CPP player/APK but skip the runtime round trip.

.PARAMETER Il2cppTarget
    None (default), Windows or Android. The toolchain inventory is always
    printed; a build is attempted only for the selected target.

.PARAMETER KeepTemp
    Keep the temporary Unity project for inspection.

.PARAMETER DotnetPath
    dotnet.exe path. Defaults to DOTNET_ROOT, D:\Unity\dotnet\dotnet.exe, then PATH.

.PARAMETER UnityPath
    Unity.exe path. Defaults to the newest editor under D:\Unity\Hub\Editor and
    C:\Program Files\Unity\Hub\Editor.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$SkipUnity,

    [switch]$BuildOnly,

    [ValidateSet('None', 'Windows', 'Android')]
    [string]$Il2cppTarget = 'None',

    [switch]$KeepTemp,

    [string]$DotnetPath,

    [string]$UnityPath
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$artifactRoot = Join-Path $repoRoot 'build\p22-aot'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$runDir = Join-Path $artifactRoot $stamp
New-Item -ItemType Directory -Force -Path $runDir | Out-Null

$script:results = New-Object System.Collections.Generic.List[object]
$script:failed = $false
$script:blocked = $false
$script:notRun = $false
$tempRoot = $null

function Add-Result {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Status,
        [string]$Detail
    )

    $script:results.Add([pscustomobject]@{ Name = $Name; Status = $Status; Detail = $Detail })
    $color = switch ($Status) {
        'PASS' { 'Green' }
        'FAIL' { 'Red' }
        'BLOCKED' { 'Yellow' }
        default { 'Gray' }
    }
    Write-Host ("{0,-8} {1,-24} {2}" -f $Status, $Name, $Detail) -ForegroundColor $color
    if ($Status -eq 'FAIL') { $script:failed = $true }
    elseif ($Status -eq 'BLOCKED') { $script:blocked = $true }
    elseif ($Status -eq 'NOT_RUN') { $script:notRun = $true }
}

function ConvertTo-NativeArgument {
    param([string]$Value)

    # Start-Process joins an argument array with spaces and does not quote.
    # Quote every argument that contains whitespace or a quote so UnityPath,
    # temp paths and log paths containing spaces survive intact.
    if ($Value -notmatch '[\s"]') {
        return $Value
    }

    return '"' + ($Value -replace '(\\*)"', '$1$1\"') + '"'
}

function Invoke-ExternalProcess {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [int]$TimeoutSeconds = 0
    )

    $argumentLine = ($Arguments | ForEach-Object { ConvertTo-NativeArgument $_ }) -join ' '
    $process = Start-Process -FilePath $FilePath -ArgumentList $argumentLine -WindowStyle Hidden -PassThru

    if ($TimeoutSeconds -gt 0) {
        $finished = $process.WaitForExit($TimeoutSeconds * 1000)
        if (-not $finished) {
            try { $process.Kill() } catch { }
            return [pscustomobject]@{ ExitCode = -1; TimedOut = $true; ArgumentLine = $argumentLine }
        }
    }
    else {
        $process.WaitForExit()
    }

    return [pscustomobject]@{ ExitCode = $process.ExitCode; TimedOut = $false; ArgumentLine = $argumentLine }
}

function Find-Unity {
    if ($UnityPath) {
        return $UnityPath
    }

    $roots = @('D:\Unity\Hub\Editor', 'C:\Program Files\Unity\Hub\Editor')
    $editors = @()
    foreach ($root in $roots) {
        if (Test-Path $root) {
            $editors += Get-ChildItem $root -Directory -ErrorAction SilentlyContinue |
                Where-Object { Test-Path (Join-Path $_.FullName 'Editor\Unity.exe') }
        }
    }

    $editor = $editors | Sort-Object Name -Descending | Select-Object -First 1
    if ($editor) {
        return (Join-Path $editor.FullName 'Editor\Unity.exe')
    }

    return $null
}

function Find-MsvcCl {
    $cl = Get-Command cl.exe -ErrorAction SilentlyContinue
    if ($cl) {
        return $cl.Source
    }

    $vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $vsPath = (& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2>$null | Select-Object -First 1)
        if ($vsPath) {
            $candidate = Get-ChildItem (Join-Path $vsPath 'VC\Tools\MSVC') -Directory -ErrorAction SilentlyContinue |
                Sort-Object Name -Descending |
                ForEach-Object { Join-Path $_.FullName 'bin\Hostx64\x64\cl.exe' } |
                Where-Object { Test-Path $_ } |
                Select-Object -First 1
            if ($candidate) {
                return $candidate
            }
        }
    }

    $roots = @(
        'C:\Program Files\Microsoft Visual Studio',
        'C:\Program Files (x86)\Microsoft Visual Studio'
    )
    foreach ($root in $roots) {
        if (-not (Test-Path $root)) { continue }
        $candidate = Get-ChildItem $root -Directory -ErrorAction SilentlyContinue |
            ForEach-Object { Get-ChildItem $_.FullName -Directory -ErrorAction SilentlyContinue } |
            ForEach-Object { Join-Path $_.FullName 'VC\Tools\MSVC' } |
            Where-Object { Test-Path $_ } |
            ForEach-Object { Get-ChildItem $_ -Directory -ErrorAction SilentlyContinue } |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName 'bin\Hostx64\x64\cl.exe' } |
            Where-Object { Test-Path $_ } |
            Select-Object -First 1
        if ($candidate) {
            return $candidate
        }
    }

    return $null
}

function Find-WindowsSdk {
    $roots = @('C:\Program Files (x86)\Windows Kits\10\Include', 'C:\Program Files\Windows Kits\10\Include')
    foreach ($root in $roots) {
        if (-not (Test-Path $root)) { continue }
        $candidate = Get-ChildItem $root -Directory -ErrorAction SilentlyContinue |
            Where-Object { Test-Path (Join-Path $_.FullName 'um\Windows.h') } |
            Sort-Object Name -Descending |
            Select-Object -First 1
        if ($candidate) {
            return $candidate.FullName
        }
    }

    return $null
}

function Find-AndroidSdk {
    $candidates = @(
        $env:ANDROID_HOME,
        $env:ANDROID_SDK_ROOT,
        (Join-Path $env:LOCALAPPDATA 'Android\Sdk'),
        'C:\Android\Sdk',
        'D:\Android\Sdk',
        $(if ($editorData) { Join-Path $editorData 'PlaybackEngines\AndroidPlayer\SDK' })
    ) | Where-Object { $_ }

    foreach ($candidate in $candidates) {
        if ((Test-Path (Join-Path $candidate 'platforms')) -or (Test-Path (Join-Path $candidate 'platform-tools'))) {
            return $candidate
        }
    }

    return $null
}

function Find-AndroidNdk {
    $roots = @(
        $env:ANDROID_NDK_HOME,
        $env:ANDROID_NDK_ROOT,
        $(if ($androidSdk) { Join-Path $androidSdk 'ndk' }),
        $(if ($editorData) { Join-Path $editorData 'PlaybackEngines\AndroidPlayer\NDK' })
    ) | Where-Object { $_ }

    foreach ($root in $roots) {
        if (-not (Test-Path $root)) { continue }
        if (Test-Path (Join-Path $root 'source.properties')) {
            return $root
        }
        $candidate = Get-ChildItem $root -Directory -ErrorAction SilentlyContinue |
            Where-Object { Test-Path (Join-Path $_.FullName 'source.properties') } |
            Sort-Object Name -Descending |
            Select-Object -First 1
        if ($candidate) {
            return $candidate.FullName
        }
    }

    return $null
}

function Find-Adb {
    $adb = Get-Command adb.exe -ErrorAction SilentlyContinue
    if ($adb) {
        return $(if ($adb.Source) { $adb.Source } else { $adb.FullName })
    }

    if ($androidSdk) {
        $candidate = Join-Path $androidSdk 'platform-tools\adb.exe'
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    return $null
}

function Find-Java {
    $candidates = @()
    $javaOnPath = Get-Command java.exe -ErrorAction SilentlyContinue
    if ($javaOnPath) { $candidates += $javaOnPath.Source }
    if ($env:JAVA_HOME) { $candidates += (Join-Path $env:JAVA_HOME 'bin\java.exe') }
    $candidates += 'C:\Program Files\Android\Android Studio\jbr\bin\java.exe'

    return ($candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1)
}

function Initialize-TempRoot {
    if (-not $tempRoot) {
        $script:tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('mahjong-wire-aot-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force -Path $script:tempRoot | Out-Null
    }

    return $script:tempRoot
}

function Remove-CheckedTree {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $resolvedRoot = (Resolve-Path -LiteralPath $tempRoot).Path.TrimEnd('\')
    $resolved = (Resolve-Path -LiteralPath $Path).Path.TrimEnd('\')
    if (-not $resolved.StartsWith($resolvedRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing recursive delete outside the temp project root: $resolved (root $resolvedRoot)"
    }

    Remove-Item -LiteralPath $resolved -Recurse -Force
}

function Remove-TempRoot {
    if (-not $tempRoot -or -not (Test-Path -LiteralPath $tempRoot)) {
        return
    }

    $resolved = (Resolve-Path -LiteralPath $tempRoot).Path.TrimEnd('\')
    $tempBase = ([System.IO.Path]::GetTempPath()).TrimEnd('\')
    $leaf = Split-Path -Leaf $resolved
    if (-not $resolved.StartsWith($tempBase + '\', [StringComparison]::OrdinalIgnoreCase) -or
        -not $leaf.StartsWith('mahjong-wire-aot-', [StringComparison]::Ordinal)) {
        throw "Refusing recursive delete of unverified temp root: $resolved"
    }

    Remove-Item -LiteralPath $resolved -Recurse -Force
}

if (-not $DotnetPath) {
    if ($env:DOTNET_ROOT -and (Test-Path (Join-Path $env:DOTNET_ROOT 'dotnet.exe'))) {
        $DotnetPath = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
    }
    elseif (Test-Path 'D:\Unity\dotnet\dotnet.exe') {
        $DotnetPath = 'D:\Unity\dotnet\dotnet.exe'
    }
    else {
        $DotnetPath = (Get-Command dotnet -ErrorAction Stop).Source
    }
}

$unity = Find-Unity
$editorData = if ($unity) { Join-Path (Split-Path $unity) 'Data' } else { $null }
$il2cppExe = if ($editorData) { Join-Path $editorData 'il2cpp\build\deploy\il2cpp.exe' } else { $null }
$androidPlayer = if ($editorData) { Join-Path $editorData 'PlaybackEngines\AndroidPlayer' } else { $null }
$winIl2cppModule = $null
if ($editorData) {
    $winSupport = Join-Path $editorData 'PlaybackEngines\windowsstandalonesupport'
    if (Test-Path $winSupport) {
        $winIl2cppModule = Get-ChildItem (Join-Path $winSupport 'Variations') -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like 'win64_*il2cpp*' } |
            Select-Object -First 1
    }
}
$msvcCl = Find-MsvcCl
$winSdk = Find-WindowsSdk
$androidSdk = Find-AndroidSdk
$androidNdk = Find-AndroidNdk
$adbPath = Find-Adb
$javaPath = Find-Java

Write-Host "repo     : $repoRoot"
Write-Host "dotnet   : $DotnetPath"
Write-Host "artifacts: $runDir"
Write-Host ""

# ---------------------------------------------------------------------------
# Generated probe project + runner (inside build/, never tracked)
# ---------------------------------------------------------------------------
$probeRoot = Join-Path $runDir 'probe'
$probeProjDir = Join-Path $probeRoot 'Mahjong.Contracts.Wire.AotSmoke'
$runnerProjDir = Join-Path $probeRoot 'Mahjong.Wire.AotSmoke.Runner'
New-Item -ItemType Directory -Force -Path $probeProjDir, $runnerProjDir | Out-Null

$wireProject = Join-Path $repoRoot 'dotnet\src\Mahjong.Contracts.Wire\Mahjong.Contracts.Wire.csproj'
$contractsProject = Join-Path $repoRoot 'dotnet\src\Mahjong.Contracts\Mahjong.Contracts.csproj'
$escapeXml = { param($value) [System.Security.SecurityElement]::Escape($value) }

$probeCsproj = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <AssemblyName>Mahjong.Contracts.Wire.AotSmoke</AssemblyName>
    <RootNamespace>Mahjong.Contracts.Wire.AotSmoke</RootNamespace>
    <TargetFramework>netstandard2.1</TargetFramework>
    <LangVersion>9.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Deterministic>true</Deterministic>
    <DebugType>portable</DebugType>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Google.Protobuf" Version="3.28.3" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="$(& $escapeXml $wireProject)" />
    <ProjectReference Include="$(& $escapeXml $contractsProject)" />
  </ItemGroup>
</Project>
"@
Set-Content -LiteralPath (Join-Path $probeProjDir 'Mahjong.Contracts.Wire.AotSmoke.csproj') -Value $probeCsproj -Encoding UTF8

$probeSource = @'
using System;
using System.Collections.Generic;
using System.Text;
using Google.Protobuf;
using Mahjong.Contracts;

namespace Mahjong.Contracts.Wire.AotSmoke
{
    /// <summary>
    /// Minimal direct consumer of the generated Wire codec. It is intentionally
    /// free of reflection-based serialization, dynamic code and JSON so the same
    /// code path can run in .NET, Unity Editor (Mono) and an IL2CPP player.
    /// </summary>
    public static class WireAotProbe
    {
        public const string PassPrefix = "PASS";

        public static string Run()
        {
            var envelopes = BuildEnvelopes();
            int totalBytes = 0;

            foreach (Envelope original in envelopes)
            {
                byte[] bytes = WireCodec.Encode(original);
                totalBytes += bytes.Length;

                WireDecodeResult decoded = WireCodec.Decode(bytes);
                if (!decoded.IsOk || decoded.Envelope == null)
                {
                    throw new InvalidOperationException(
                        "Decode failed for " + original.MessageType + ": " + decoded.Status + " " + decoded.ErrorCode);
                }

                if (decoded.Envelope.MessageType != original.MessageType ||
                    decoded.Envelope.BodyCase != original.BodyCase)
                {
                    throw new InvalidOperationException(
                        "Round trip changed envelope identity for " + original.MessageType + ".");
                }

                if (decoded.UnknownFieldCount != 0)
                {
                    throw new InvalidOperationException(
                        "Round trip reported unknown fields for " + original.MessageType + ".");
                }

                byte[] reencoded = WireCodec.Encode(decoded.Envelope);
                if (!BytesEqual(bytes, reencoded))
                {
                    throw new InvalidOperationException(
                        "Re-encode changed wire bytes for " + original.MessageType + ".");
                }
            }

            // The codec scans descriptors at runtime; this exercises unknown
            // field preservation without relying on reflection-based serializers.
            byte[] heartbeatBytes = WireCodec.Encode(Envelopes.Heartbeat());
            byte[] withUnknownField = AppendUnknownVarint(heartbeatBytes, 1000, 1);
            WireDecodeResult unknown = WireCodec.Decode(withUnknownField);
            if (!unknown.IsOk || unknown.UnknownFieldCount != 1)
            {
                throw new InvalidOperationException(
                    "Unknown-field scan returned " + unknown.Status + " / " + unknown.UnknownFieldCount + ".");
            }

            // V4 MJ-TCP/1 traffic must never be interpreted as V5 protobuf.
            byte[] v4Magic = Encoding.ASCII.GetBytes("MJTC");
            WireDecodeResult legacy = WireCodec.Decode(v4Magic);
            if (legacy.Status != WireDecodeStatus.LegacyV4Rejected)
            {
                throw new InvalidOperationException("V4 magic was not rejected: " + legacy.Status + ".");
            }

            return PassPrefix +
                   " codec=netstandard2.1" +
                   " messages=" + envelopes.Count +
                   " bytes=" + totalBytes +
                   " unknownFields=" + unknown.UnknownFieldCount +
                   " legacyV4=rejected" +
                   " assembly=" + typeof(WireCodec).Assembly.GetName().Name;
        }

        private static List<Envelope> BuildEnvelopes()
        {
            return new List<Envelope>
            {
                Envelopes.Hello(),
                Envelopes.Welcome(),
                Envelopes.Command(),
                Envelopes.Receipt(),
                Envelopes.PlayerSnapshot(),
                Envelopes.PlayerSnapshotQuery(),
                Envelopes.PublicSnapshot(),
                Envelopes.PublicSnapshotQuery(),
                Envelopes.RoomUpdate(),
                Envelopes.SyncRequest(),
                Envelopes.SyncResponse(),
                Envelopes.ReceiptQuery(),
                Envelopes.ReceiptLookup(),
                Envelopes.Heartbeat(),
                Envelopes.Error(),
                Envelopes.Close(),
                Envelopes.PlayerUpdate(),
                Envelopes.PublicUpdate()
            };
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static byte[] AppendUnknownVarint(byte[] source, int fieldNumber, ulong value)
        {
            var result = new byte[source.Length + 8];
            Buffer.BlockCopy(source, 0, result, 0, source.Length);
            int offset = source.Length;
            WriteVarint(result, ref offset, ((ulong)fieldNumber << 3) | 0);
            WriteVarint(result, ref offset, value);
            var trimmed = new byte[offset];
            Buffer.BlockCopy(result, 0, trimmed, 0, offset);
            return trimmed;
        }

        private static void WriteVarint(byte[] buffer, ref int offset, ulong value)
        {
            while (value >= 0x80)
            {
                buffer[offset++] = (byte)(value | 0x80);
                value >>= 7;
            }

            buffer[offset++] = (byte)value;
        }

        private static class Envelopes
        {
            public static Envelope Hello()
            {
                return Envelope(MessageType.Hello, e => e.Hello = new Hello
                {
                    Ticket = "ticket-1",
                    ProtocolMajor = ProtocolConstants.Major,
                    ProtocolMinor = ProtocolConstants.Minor,
                    RulesetVersion = ProtocolConstants.RulesetVersion,
                    ProjectionVersion = ProtocolConstants.ProjectionVersion,
                    ClientBuild = "aot-probe",
                    ResumeRequested = true,
                    ResumeSessionId = "session-1"
                });
            }

            public static Envelope Welcome()
            {
                return Envelope(MessageType.Welcome, e => e.Welcome = new Welcome
                {
                    SessionId = "session-1",
                    RoomId = "room-1",
                    TableId = "table-1",
                    Role = Role.Player,
                    SeatId = 0,
                    ConnectionEpoch = 1,
                    OwnerEpoch = 2,
                    BindingEpoch = 3,
                    Baseline = new StreamBaseline
                    {
                        StateVersion = 7,
                        EventSeq = 101,
                        Cursor = Cursor(7, 11)
                    },
                    Scope = Scope(),
                    Limits = new ServerLimits
                    {
                        MaxInboundMessageBytes = 1024 * 1024,
                        MaxCommandPayloadBytes = 64 * 1024,
                        MaxSnapshotBytes = 1024 * 1024,
                        MaxInFlightRequests = 64,
                        HeartbeatIntervalMs = 10000,
                        IdleDeadMs = 30000,
                        MaxEarlyBufferItems = 64,
                        MaxEarlyBufferBytes = 4 * 1024 * 1024
                    },
                    ProtocolMajor = ProtocolConstants.Major,
                    ProtocolMinor = ProtocolConstants.Minor,
                    RulesetVersion = ProtocolConstants.RulesetVersion,
                    ProjectionVersion = ProtocolConstants.ProjectionVersion
                });
            }

            public static Envelope Command()
            {
                BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                    "round-1",
                    "decision-1",
                    1,
                    ProtocolActionType.DiscardTile,
                    tileIds: new uint[] { 5 });

                return Envelope(MessageType.Command, e => e.Command = new Command
                {
                    Type = CommandType.SubmitAction,
                    CanonicalPayload = ByteString.CopyFrom(payload.CopyBytes()),
                    PayloadSchemaVersion = CanonicalPayloadDigest.FormatVersion,
                    ExpectedStateVersion = 4,
                    ExpectedRoundId = "round-1",
                    ExpectedDecisionId = "decision-1"
                });
            }

            public static Envelope Receipt()
            {
                return Envelope(MessageType.Receipt, e => e.Receipt = new Receipt
                {
                    Status = ReceiptStatus.Applied,
                    ReasonCode = string.Empty,
                    EvaluatedStateVersion = 1,
                    CommandVersion = 2,
                    EventSeq = 3,
                    CommittedAtMs = 1234,
                    CanonicalPayloadSha256 = new string('a', 64)
                });
            }

            public static Envelope PlayerSnapshot()
            {
                PlayerSnapshot snapshot = new PlayerSnapshot
                {
                    SessionId = "session-1",
                    RoundId = "round-1",
                    Cursor = Cursor(7, 11),
                    StateVersion = 7,
                    EventSeq = 101,
                    SessionVersion = 1,
                    RoundVersion = 2,
                    PublicView = PublicView(),
                    PrivateView = new PlayerPrivateView
                    {
                        Seat = 0,
                        Furiten = false
                    }
                };
                snapshot.PrivateView.ClosedTiles.Add(1);
                snapshot.PrivateView.ClosedTiles.Add(2);

                return Envelope(MessageType.PlayerSnapshot, e => e.PlayerSnapshot = snapshot);
            }

            public static Envelope PlayerSnapshotQuery()
            {
                return Envelope(MessageType.PlayerSnapshotQuery, e => e.PlayerSnapshotQuery = new PlayerSnapshotQuery
                {
                    SessionId = "session-1"
                });
            }

            public static Envelope PublicSnapshot()
            {
                return Envelope(MessageType.PublicSnapshot, e => e.PublicSnapshot = new PublicSnapshot
                {
                    SessionId = "session-1",
                    RoundId = "round-1",
                    Cursor = Cursor(7, 11),
                    StateVersion = 7,
                    EventSeq = 101,
                    SessionVersion = 1,
                    RoundVersion = 2,
                    View = PublicView()
                });
            }

            public static Envelope PublicSnapshotQuery()
            {
                return Envelope(MessageType.PublicSnapshotQuery, e => e.PublicSnapshotQuery = new PublicSnapshotQuery
                {
                    SessionId = "session-1"
                });
            }

            public static Envelope RoomUpdate()
            {
                return Envelope(MessageType.RoomUpdate, e => e.RoomUpdate = new RoomUpdate
                {
                    RoomId = "room-1",
                    SessionId = "session-1",
                    RoomVersion = 3,
                    Lifecycle = RoomLifecycle.Lobby,
                    Capacity = 4
                });
            }

            public static Envelope SyncRequest()
            {
                return Envelope(MessageType.SyncRequest, e => e.SyncRequest = new SyncRequest
                {
                    Target = SyncTarget.PlayerStream,
                    KnownCursor = Cursor(5, 10),
                    ReasonCode = ProtocolErrorCode.StreamGap,
                    RequestFullBaseline = true
                });
            }

            public static Envelope SyncResponse()
            {
                return Envelope(MessageType.SyncResponse, e => e.SyncResponse = new SyncResponse
                {
                    Target = SyncTarget.PublicStream,
                    IsFullSnapshot = true,
                    Public = new PublicSnapshot
                    {
                        SessionId = "session-1",
                        RoundId = "round-1",
                        Cursor = Cursor(7, 11),
                        StateVersion = 7,
                        EventSeq = 101,
                        SessionVersion = 1,
                        RoundVersion = 2,
                        View = PublicView()
                    },
                    StateVersion = 7,
                    EventSeq = 101
                });
            }

            public static Envelope ReceiptQuery()
            {
                return Envelope(MessageType.ReceiptQuery, e => e.ReceiptQuery = new ReceiptQuery
                {
                    RequestScopeId = "scope-1",
                    RequestId = "request-1"
                });
            }

            public static Envelope ReceiptLookup()
            {
                return Envelope(MessageType.ReceiptLookup, e => e.ReceiptLookup = new ReceiptLookup
                {
                    Status = LookupStatus.NotFound,
                    CanonicalPayloadSha256 = new string('b', 64)
                });
            }

            public static Envelope Heartbeat()
            {
                return Envelope(MessageType.Heartbeat, e => e.Heartbeat = new Heartbeat
                {
                    Ping = new Ping { Nonce = "0123456789abcdef" }
                });
            }

            public static Envelope Error()
            {
                return Envelope(MessageType.Error, e => e.Error = new ProtocolError
                {
                    Code = ProtocolErrorCode.ServerBusy,
                    Retryable = true,
                    FieldPath = "command",
                    DetailCode = "QUEUE_FULL",
                    RetryAfterMs = 250
                });
            }

            public static Envelope Close()
            {
                return Envelope(MessageType.Close, e => e.Close = new Close
                {
                    Code = ProtocolErrorCode.SlowConsumer,
                    Permanent = true,
                    WebsocketCloseCode = 1008
                });
            }

            public static Envelope PlayerUpdate()
            {
                return Envelope(MessageType.PlayerUpdate, e => e.PlayerUpdate = new PlayerUpdate
                {
                    Snapshot = PlayerSnapshot().PlayerSnapshot,
                    FromCursor = Cursor(5, 10),
                    Cursor = Cursor(7, 11),
                    IsFullSnapshot = false
                });
            }

            public static Envelope PublicUpdate()
            {
                return Envelope(MessageType.PublicUpdate, e => e.PublicUpdate = new PublicUpdate
                {
                    Snapshot = new PublicSnapshot
                    {
                        SessionId = "session-1",
                        RoundId = "round-1",
                        Cursor = Cursor(7, 11),
                        StateVersion = 7,
                        EventSeq = 101,
                        SessionVersion = 1,
                        RoundVersion = 2,
                        View = PublicView()
                    },
                    FromCursor = Cursor(5, 10),
                    Cursor = Cursor(7, 11),
                    IsFullSnapshot = false
                });
            }

            private static Envelope Envelope(MessageType type, Action<Envelope> assignBody)
            {
                var envelope = new Envelope
                {
                    ProtocolMajor = ProtocolConstants.Major,
                    ProtocolMinor = ProtocolConstants.Minor,
                    RulesetVersion = ProtocolConstants.RulesetVersion,
                    ProjectionVersion = ProtocolConstants.ProjectionVersion,
                    MessageType = type,
                    CorrelationId = "corr-1",
                    RequestId = "request-1"
                };
                assignBody(envelope);
                return envelope;
            }

            private static StreamCursor Cursor(ulong streamVersion, ulong projectionSeq)
            {
                return new StreamCursor { StreamVersion = streamVersion, ProjectionSeq = projectionSeq };
            }

            private static RequestScope Scope()
            {
                return new RequestScope
                {
                    ScopeId = "scope-1",
                    AccountId = "account-1",
                    Role = Role.Player,
                    RoomId = "room-1",
                    IssuedAtMs = 1000,
                    ExpiresAtMs = 2000
                };
            }

            private static PublicMatchView PublicView()
            {
                return new PublicMatchView
                {
                    Session = new PublicSessionSummary { Finished = false },
                    Paused = false,
                    SettlementRoundId = "round-1"
                };
            }
        }
    }
}
'@
Set-Content -LiteralPath (Join-Path $probeProjDir 'WireAotProbe.cs') -Value $probeSource -Encoding UTF8

$runnerCsproj = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <AssemblyName>Mahjong.Wire.AotSmoke.Runner</AssemblyName>
    <RootNamespace>Mahjong.Wire.AotSmoke.Runner</RootNamespace>
    <LangVersion>9.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Deterministic>true</Deterministic>
    <DebugType>portable</DebugType>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Mahjong.Contracts.Wire.AotSmoke\Mahjong.Contracts.Wire.AotSmoke.csproj" />
  </ItemGroup>
</Project>
'@
Set-Content -LiteralPath (Join-Path $runnerProjDir 'Mahjong.Wire.AotSmoke.Runner.csproj') -Value $runnerCsproj -Encoding UTF8

$runnerProgram = @'
using System;
using Mahjong.Contracts.Wire.AotSmoke;

namespace Mahjong.Wire.AotSmoke.Runner
{
    internal static class Program
    {
        private static int Main()
        {
            try
            {
                string result = WireAotProbe.Run();
                Console.WriteLine(result);
                return result.StartsWith(WireAotProbe.PassPrefix, StringComparison.Ordinal) ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL " + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
        }
    }
}
'@
Set-Content -LiteralPath (Join-Path $runnerProjDir 'Program.cs') -Value $runnerProgram -Encoding UTF8

$runnerProj = Join-Path $runnerProjDir 'Mahjong.Wire.AotSmoke.Runner.csproj'
$probeBin = Join-Path $probeProjDir "bin\$Configuration\netstandard2.1"
$probeDll = Join-Path $probeBin 'Mahjong.Contracts.Wire.AotSmoke.dll'

# ---------------------------------------------------------------------------
# .NET leg
# ---------------------------------------------------------------------------
try {
    $dotnetLog = Join-Path $runDir 'dotnet-probe.log'
    $dotnetOutput = & $DotnetPath run --project $runnerProj -c $Configuration 2>&1
    $dotnetExit = $LASTEXITCODE
    $dotnetOutput | Tee-Object -FilePath $dotnetLog
    $dotnetText = ($dotnetOutput | Out-String)
    $passLine = ($dotnetText -split "`r?`n" | Where-Object { $_ -match '^PASS ' } | Select-Object -First 1)

    if ($dotnetExit -eq 0 -and
        $dotnetText -match 'PASS codec=netstandard2\.1' -and
        $dotnetText -match 'messages=18' -and
        $dotnetText -match 'legacyV4=rejected') {
        Add-Result -Name '.NET AOT probe' -Status 'PASS' -Detail ("exit=0; " + $passLine)
    }
    else {
        Add-Result -Name '.NET AOT probe' -Status 'FAIL' -Detail "exit=$dotnetExit; log=$dotnetLog"
    }
}
catch {
    Add-Result -Name '.NET AOT probe' -Status 'FAIL' -Detail $_.Exception.Message
}

# ---------------------------------------------------------------------------
# IL2CPP helper (generated, then compiled against installed Unity API)
# ---------------------------------------------------------------------------
$helperDir = Join-Path $runDir 'il2cpp-helper'
New-Item -ItemType Directory -Force -Path $helperDir | Out-Null
$helperSourcePath = Join-Path $helperDir 'MahjongIl2cppSmokeBuild.cs'

$helperSource = @'
using System;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class MahjongIl2cppSmokeBuild
{
    private const string ProbeDefine = "MAHJONG_WIRE_AOT_SMOKE";

    public static void Build()
    {
        string target = Environment.GetEnvironmentVariable("MAHJONG_AOT_TARGET") ?? "Windows";
        string output = Environment.GetEnvironmentVariable("MAHJONG_AOT_OUTPUT");
        if (string.IsNullOrEmpty(output))
        {
            throw new Exception("MAHJONG_AOT_OUTPUT is required.");
        }

        BuildTarget buildTarget;
        BuildTargetGroup group;
        NamedBuildTarget namedTarget;
        if (string.Equals(target, "Android", StringComparison.Ordinal))
        {
            buildTarget = BuildTarget.Android;
            group = BuildTargetGroup.Android;
            namedTarget = NamedBuildTarget.Android;
            PlayerSettings.SetApplicationIdentifier(namedTarget, "com.mahjong.aotsmoke");
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            EditorUserBuildSettings.buildAppBundle = false;
        }
        else if (string.Equals(target, "Windows", StringComparison.Ordinal))
        {
            buildTarget = BuildTarget.StandaloneWindows64;
            group = BuildTargetGroup.Standalone;
            namedTarget = NamedBuildTarget.Standalone;
        }
        else
        {
            throw new Exception("Unsupported MAHJONG_AOT_TARGET: " + target);
        }

        // Target and backend are explicit. The define is supplied per build via
        // BuildPlayerOptions.extraScriptingDefines; project define symbols are
        // never mutated, so this does not depend on an async Editor recompile.
        PlayerSettings.SetScriptingBackend(namedTarget, ScriptingImplementation.IL2CPP);

        string[] scenes = CollectScenes();
        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = output,
            target = buildTarget,
            targetGroup = group,
            options = BuildOptions.None,
            extraScriptingDefines = new[] { ProbeDefine }
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        BuildSummary summary = report.summary;
        Console.WriteLine("[WireAotSmoke] build target=" + buildTarget + " backend=IL2CPP result=" + summary.result + " errors=" + summary.totalErrors + " output=" + output);
        if (summary.result != BuildResult.Succeeded)
        {
            throw new Exception("Build result " + summary.result + ", errors=" + summary.totalErrors);
        }
    }

    private static string[] CollectScenes()
    {
        var enabled = new System.Collections.Generic.List<string>();
        foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
        {
            if (scene.enabled)
            {
                enabled.Add(scene.path);
            }
        }

        if (enabled.Count == 0)
        {
            enabled.Add("Assets/Scenes/SampleScene.unity");
        }

        return enabled.ToArray();
    }
}
'@
Set-Content -LiteralPath $helperSourcePath -Value $helperSource -Encoding UTF8

$csc = if ($editorData) { Join-Path $editorData 'DotNetSdkRoslyn\csc.dll' } else { $null }
$unityEngineDll = if ($editorData) { Join-Path $editorData 'Managed\UnityEngine.dll' } else { $null }
$unityEditorDll = if ($editorData) { Join-Path $editorData 'Managed\UnityEditor.dll' } else { $null }
$netstandardDll = if ($editorData) { Join-Path $editorData 'NetStandard\ref\2.1.0\netstandard.dll' } else { $null }

function Invoke-UnityRoslynCompile {
    param(
        [Parameter(Mandatory)][string]$SourcePath,
        [Parameter(Mandatory)][string]$OutputPath,
        [string[]]$Defines = @(),
        [Parameter(Mandatory)][string]$LogPath
    )

    $arguments = @(
        $csc,
        '-nologo',
        '-nostdlib+',
        '-target:library',
        '-langversion:9.0',
        "-out:$OutputPath",
        "-r:$unityEngineDll",
        "-r:$unityEditorDll",
        "-r:$netstandardDll"
    )
    if ($Defines.Count -gt 0) {
        $arguments += "-define:$($Defines -join ';')"
    }
    $arguments += $SourcePath

    $output = & $DotnetPath @arguments 2>&1
    $exitCode = $LASTEXITCODE
    $output | Tee-Object -FilePath $LogPath | Out-Host
    if (-not (Test-Path $LogPath)) {
        Set-Content -LiteralPath $LogPath -Value "exit=$exitCode; no diagnostics" -Encoding UTF8
    }
    return $exitCode
}

if ($SkipUnity) {
    Add-Result -Name 'IL2CPP helper compile' -Status 'BLOCKED' -Detail 'skipped by -SkipUnity'
}
elseif (-not $unity) {
    Add-Result -Name 'IL2CPP helper compile' -Status 'BLOCKED' -Detail 'Unity.exe not found'
}
elseif (-not ((Test-Path $csc) -and (Test-Path $unityEngineDll) -and (Test-Path $unityEditorDll) -and (Test-Path $netstandardDll))) {
    Add-Result -Name 'IL2CPP helper compile' -Status 'BLOCKED' -Detail 'Unity Roslyn or reference assemblies not found'
}
else {
    try {
        $helperLog = Join-Path $runDir 'il2cpp-helper-compile.log'
        $helperOut = Join-Path $runDir 'MahjongIl2cppSmokeBuild.dll'
        $helperExit = Invoke-UnityRoslynCompile -SourcePath $helperSourcePath -OutputPath $helperOut -LogPath $helperLog
        if ($helperExit -eq 0) {
            Add-Result -Name 'IL2CPP helper compile' -Status 'PASS' -Detail "Unity Roslyn exit=0; extraScriptingDefines API accepted; log=$helperLog"
        }
        else {
            Add-Result -Name 'IL2CPP helper compile' -Status 'FAIL' -Detail "Unity Roslyn exit=$helperExit; log=$helperLog"
        }
    }
    catch {
        Add-Result -Name 'IL2CPP helper compile' -Status 'FAIL' -Detail $_.Exception.Message
    }
}

# ---------------------------------------------------------------------------
# Unity leg
# ---------------------------------------------------------------------------
function New-ProbeProject {
    param([Parameter(Mandatory)][string]$Leaf)

    $project = Join-Path $tempRoot $Leaf
    New-Item -ItemType Directory -Force -Path $project | Out-Null
    foreach ($part in @('Assets', 'Packages', 'ProjectSettings')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot "unity\Mahjong.Unity\$part") -Destination $project -Recurse
    }

    # Keep the probe project minimal: existing Domain/Client/... DLLs are not
    # part of this probe and could mask a stale Contracts dependency.
    $pluginDir = Join-Path $project 'Assets\Plugins\Mahjong'
    Remove-CheckedTree -Path $pluginDir
    New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
    foreach ($name in @('Mahjong.Contracts.dll', 'Mahjong.Contracts.Wire.dll', 'Mahjong.Contracts.Wire.AotSmoke.dll', 'Google.Protobuf.dll')) {
        $source = Join-Path $probeBin $name
        if (-not (Test-Path $source)) {
            throw "Missing probe assembly: $source"
        }
        Copy-Item -LiteralPath $source -Destination $pluginDir -Force
    }

    return $project
}

if ($SkipUnity) {
    Add-Result -Name 'Unity script compile' -Status 'BLOCKED' -Detail 'skipped by -SkipUnity'
    Add-Result -Name 'Unity Editor load' -Status 'BLOCKED' -Detail 'skipped by -SkipUnity'
}
elseif (-not $unity) {
    Add-Result -Name 'Unity script compile' -Status 'BLOCKED' -Detail 'Unity.exe not found'
    Add-Result -Name 'Unity Editor load' -Status 'BLOCKED' -Detail 'Unity.exe not found'
}
else {
    $smokeSource = Join-Path $repoRoot 'unity\Mahjong.Unity\Assets\Scripts\Platform\WireAotSmoke.cs'
    if (-not ((Test-Path $csc) -and (Test-Path $unityEngineDll) -and (Test-Path $unityEditorDll) -and (Test-Path $netstandardDll) -and (Test-Path $smokeSource))) {
        Add-Result -Name 'Unity script compile' -Status 'BLOCKED' -Detail 'Unity Roslyn or reference assemblies not found'
    }
    else {
        try {
            $editorCompileLog = Join-Path $runDir 'unity-smoke-compile.log'
            $playerCompileLog = Join-Path $runDir 'unity-smoke-player-compile.log'
            $editorOut = Join-Path $runDir 'WireAotSmoke.Editor.dll'
            $playerOut = Join-Path $runDir 'WireAotSmoke.Player.dll'

            $editorExit = Invoke-UnityRoslynCompile -SourcePath $smokeSource -OutputPath $editorOut -Defines @('UNITY_EDITOR') -LogPath $editorCompileLog
            $playerExit = Invoke-UnityRoslynCompile -SourcePath $smokeSource -OutputPath $playerOut -Defines @('MAHJONG_WIRE_AOT_SMOKE') -LogPath $playerCompileLog

            if ($editorExit -eq 0 -and $playerExit -eq 0) {
                Add-Result -Name 'Unity script compile' -Status 'PASS' -Detail "editor exit=0; player exit=0; logs=$editorCompileLog,$playerCompileLog"
            }
            else {
                Add-Result -Name 'Unity script compile' -Status 'FAIL' -Detail "editor exit=$editorExit; player exit=$playerExit; logs=$editorCompileLog,$playerCompileLog"
            }
        }
        catch {
            Add-Result -Name 'Unity script compile' -Status 'FAIL' -Detail $_.Exception.Message
        }
    }

    if (-not (Test-Path $probeDll)) {
        Add-Result -Name 'Unity Editor load' -Status 'BLOCKED' -Detail "probe assembly missing: $probeDll"
    }
    else {
        $tempRoot = Initialize-TempRoot
        try {
            $tempProject = New-ProbeProject -Leaf 'project'
            $logPath = Join-Path $runDir 'unity-wire-aot.log'

            Write-Host "Unity    : $unity"
            Write-Host "temp     : $tempProject"
            Write-Host "log      : $logPath"

            $process = Invoke-ExternalProcess -FilePath $unity -Arguments @(
                '-batchmode',
                '-quit',
                '-nographics',
                '-projectPath', $tempProject,
                '-executeMethod', 'Mahjong.Unity.Platform.WireAotSmoke.Run',
                '-logFile', $logPath
            ) -TimeoutSeconds 600

            $unityExit = $process.ExitCode
            $unityLog = if (Test-Path $logPath) { Get-Content $logPath -Raw } else { '' }
            $passLine = ($unityLog -split "`r?`n" | Where-Object { $_ -match '\[WireAotSmoke\] PASS' } | Select-Object -First 1)

            if ($process.TimedOut) {
                Add-Result -Name 'Unity Editor load' -Status 'FAIL' -Detail "timeout after 600s; log=$logPath"
            }
            elseif ($unityExit -eq 0 -and $passLine) {
                Add-Result -Name 'Unity Editor load' -Status 'PASS' -Detail ("exit=0; " + $passLine.Trim())
            }
            elseif ($unityLog -match 'No valid Unity Editor license found' -or $unityLog -match 'return code 198') {
                Add-Result -Name 'Unity Editor load' -Status 'BLOCKED' -Detail "Unity license unavailable; exit=$unityExit; log=$logPath"
            }
            elseif ($unityLog -match 'error CS[0-9]+' -or $unityLog -match 'Scripts have compiler errors') {
                Add-Result -Name 'Unity Editor load' -Status 'FAIL' -Detail "Unity compile error; exit=$unityExit; log=$logPath"
            }
            else {
                Add-Result -Name 'Unity Editor load' -Status 'FAIL' -Detail "exit=$unityExit; log=$logPath"
            }
        }
        catch {
            Add-Result -Name 'Unity Editor load' -Status 'FAIL' -Detail $_.Exception.Message
        }
    }
}

# ---------------------------------------------------------------------------
# IL2CPP toolchain inventory (read-only, always printed)
# ---------------------------------------------------------------------------
$licenseNote = 'batchmode seat validation is the only proof'
$licenseFiles = @()
foreach ($root in @("$env:ProgramData\Unity\licenses", "$env:LOCALAPPDATA\Unity\licenses")) {
    if (Test-Path $root) {
        $licenseFiles += Get-ChildItem $root -Recurse -File -Filter 'Unity*License*' -ErrorAction SilentlyContinue
    }
}
$ulf = @("$env:ProgramData\Unity\Unity_lic.ulf", "$env:LOCALAPPDATA\Unity\Unity_lic.ulf", "$env:APPDATA\Unity\Unity_lic.ulf") |
    Where-Object { Test-Path $_ } |
    Select-Object -First 1
if ($ulf) {
    $licenseNote = $ulf
}
elseif ($licenseFiles.Count -gt 0) {
    $licenseNote = "$($licenseFiles.Count) entitlement file(s) only; $licenseNote"
}

$inventory = [ordered]@{
    'Unity editor'          = $(if ($unity) { $unity } else { $null })
    'Unity IL2CPP compiler' = $(if ($il2cppExe -and (Test-Path $il2cppExe)) { $il2cppExe } else { $null })
    'Windows IL2CPP module' = $(if ($winIl2cppModule) { $winIl2cppModule.FullName } else { $null })
    'Android Player module' = $(if ($androidPlayer -and (Test-Path $androidPlayer)) { $androidPlayer } else { $null })
    'Android SDK'           = $androidSdk
    'Android NDK'           = $androidNdk
    'Android adb'           = $adbPath
    'JDK java.exe'          = $javaPath
    'MSVC cl.exe'           = $msvcCl
    'Windows SDK'           = $winSdk
}

Write-Host ""
Write-Host "--- IL2CPP toolchain inventory (read-only) ---"
foreach ($item in $inventory.GetEnumerator()) {
    $status = if ($item.Value) { 'PASS' } else { 'BLOCKED' }
    $color = if ($item.Value) { 'Green' } else { 'Yellow' }
    Write-Host ("{0,-8} {1,-24} {2}" -f $status, $item.Key, $(if ($item.Value) { $item.Value } else { 'not found' })) -ForegroundColor $color
}
Write-Host ("{0,-8} {1,-24} {2}" -f 'INFO', 'Unity license', $licenseNote)

$windowsMissing = @()
if (-not $unity) { $windowsMissing += 'Unity editor' }
if (-not ($il2cppExe -and (Test-Path $il2cppExe))) { $windowsMissing += 'Unity IL2CPP compiler' }
if (-not $winIl2cppModule) { $windowsMissing += 'Windows IL2CPP player module' }
if (-not $msvcCl) { $windowsMissing += 'MSVC cl.exe (Visual Studio C++ workload / Build Tools)' }
if (-not $winSdk) { $windowsMissing += 'Windows 10/11 SDK' }

$androidMissing = @()
if (-not $unity) { $androidMissing += 'Unity editor' }
if (-not ($il2cppExe -and (Test-Path $il2cppExe))) { $androidMissing += 'Unity IL2CPP compiler' }
if (-not ($androidPlayer -and (Test-Path $androidPlayer))) { $androidMissing += 'Unity Android Player module' }
if (-not $androidSdk) { $androidMissing += 'Android SDK' }
if (-not $androidNdk) { $androidMissing += 'Android NDK' }
if (-not $javaPath) { $androidMissing += 'JDK java.exe' }

function Invoke-Il2cppTarget {
    param([Parameter(Mandatory)][ValidateSet('Windows', 'Android')][string]$Target)

    $missing = if ($Target -eq 'Windows') { $windowsMissing } else { $androidMissing }
    $buildName = "$Target IL2CPP build"
    $runtimeName = "$Target IL2CPP runtime"

    if ($missing.Count -gt 0) {
        Add-Result -Name $buildName -Status 'BLOCKED' -Detail ("prerequisites missing: " + ($missing -join '; '))
        Add-Result -Name $runtimeName -Status 'NOT_RUN' -Detail 'build did not run; no player was produced'
        return
    }

    if ($SkipUnity) {
        Add-Result -Name $buildName -Status 'BLOCKED' -Detail 'skipped by -SkipUnity'
        Add-Result -Name $runtimeName -Status 'NOT_RUN' -Detail 'skipped by -SkipUnity'
        return
    }

    Initialize-TempRoot | Out-Null
    $tempProject = New-ProbeProject -Leaf "il2cpp-$($Target.ToLowerInvariant())"
    $editorDir = Join-Path $tempProject 'Assets\Editor'
    New-Item -ItemType Directory -Force -Path $editorDir | Out-Null
    Copy-Item -LiteralPath $helperSourcePath -Destination (Join-Path $editorDir 'MahjongIl2cppSmokeBuild.cs') -Force

    $outputPath = if ($Target -eq 'Windows') {
        Join-Path $runDir 'il2cpp\windows\MahjongAotSmoke.exe'
    }
    else {
        Join-Path $runDir 'il2cpp\android\MahjongAotSmoke.apk'
    }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $outputPath) | Out-Null
    $buildLog = Join-Path $runDir "unity-il2cpp-$($Target.ToLowerInvariant())-build.log"

    $oldTarget = $env:MAHJONG_AOT_TARGET
    $oldOutput = $env:MAHJONG_AOT_OUTPUT
    $env:MAHJONG_AOT_TARGET = $Target
    $env:MAHJONG_AOT_OUTPUT = $outputPath
    try {
        $process = Invoke-ExternalProcess -FilePath $unity -Arguments @(
            '-batchmode',
            '-quit',
            '-nographics',
            '-projectPath', $tempProject,
            '-executeMethod', 'MahjongIl2cppSmokeBuild.Build',
            '-logFile', $buildLog
        ) -TimeoutSeconds 3600
    }
    finally {
        $env:MAHJONG_AOT_TARGET = $oldTarget
        $env:MAHJONG_AOT_OUTPUT = $oldOutput
    }

    $buildLogText = if (Test-Path $buildLog) { Get-Content $buildLog -Raw } else { '' }
    if ($process.TimedOut) {
        Add-Result -Name $buildName -Status 'FAIL' -Detail "timeout after 3600s; log=$buildLog"
        Add-Result -Name $runtimeName -Status 'NOT_RUN' -Detail 'build timed out'
        return
    }
    if ($buildLogText -match 'No valid Unity Editor license found' -or $buildLogText -match 'return code 198') {
        Add-Result -Name $buildName -Status 'BLOCKED' -Detail "Unity license unavailable; exit=$($process.ExitCode); log=$buildLog"
        Add-Result -Name $runtimeName -Status 'NOT_RUN' -Detail 'build blocked by license'
        return
    }
    if ($process.ExitCode -ne 0 -or -not (Test-Path $outputPath)) {
        Add-Result -Name $buildName -Status 'FAIL' -Detail "exit=$($process.ExitCode); output missing; log=$buildLog"
        Add-Result -Name $runtimeName -Status 'NOT_RUN' -Detail 'build failed'
        return
    }

    Add-Result -Name $buildName -Status 'PASS' -Detail "IL2CPP build produced $outputPath; log=$buildLog"

    if ($BuildOnly) {
        Add-Result -Name $runtimeName -Status 'NOT_RUN' -Detail 'player/APK built but runtime skipped by -BuildOnly'
        return
    }

    if ($Target -eq 'Windows') {
        $playerLog = Join-Path $runDir 'il2cpp\windows\player.log'
        $player = Invoke-ExternalProcess -FilePath $outputPath -Arguments @(
            '-batchmode',
            '-nographics',
            '-logFile', $playerLog
        ) -TimeoutSeconds 300
        $playerText = if (Test-Path $playerLog) { Get-Content $playerLog -Raw } else { '' }
        if ($player.ExitCode -eq 0 -and $playerText -match '\[WireAotSmoke\] PASS') {
            Add-Result -Name $runtimeName -Status 'PASS' -Detail "player exit=0; log=$playerLog"
        }
        elseif ($player.TimedOut) {
            Add-Result -Name $runtimeName -Status 'FAIL' -Detail "player timeout; log=$playerLog"
        }
        else {
            Add-Result -Name $runtimeName -Status 'FAIL' -Detail "player exit=$($player.ExitCode); log=$playerLog"
        }
        return
    }

    if (-not $adbPath) {
        Add-Result -Name $runtimeName -Status 'BLOCKED' -Detail 'APK built, but adb is unavailable; runtime NOT_RUN'
        return
    }

    $devices = @((& $adbPath devices) -split "`r?`n" | Where-Object { $_ -match '\tdevice$' })
    if ($devices.Count -eq 0) {
        Add-Result -Name $runtimeName -Status 'BLOCKED' -Detail 'APK built, but no adb device is attached; runtime NOT_RUN'
        return
    }

    & $adbPath install -r $outputPath | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Add-Result -Name $runtimeName -Status 'FAIL' -Detail 'adb install failed'
        return
    }
    & $adbPath logcat -c | Out-Null
    & $adbPath shell am force-stop com.mahjong.aotsmoke | Out-Null
    & $adbPath shell am start -n com.mahjong.aotsmoke/com.unity3d.player.UnityPlayerActivity | Out-Null

    $logcatPath = Join-Path $runDir 'il2cpp\android\android-logcat.txt'
    $deadline = (Get-Date).AddSeconds(60)
    $marker = $null
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
        $logcat = & $adbPath logcat -d -s Unity 2>&1
        $marker = ($logcat | Where-Object { $_ -match '\[WireAotSmoke\] PASS' } | Select-Object -First 1)
        if ($marker) { break }
    }
    & $adbPath logcat -d -s Unity 2>&1 | Set-Content -LiteralPath $logcatPath
    & $adbPath uninstall com.mahjong.aotsmoke | Out-Null

    if ($marker) {
        Add-Result -Name $runtimeName -Status 'PASS' -Detail "logcat marker found; log=$logcatPath"
    }
    else {
        Add-Result -Name $runtimeName -Status 'FAIL' -Detail "no [WireAotSmoke] PASS marker; log=$logcatPath"
    }
}

if ($Il2cppTarget -eq 'None') {
    foreach ($pair in @(
        @{ Name = 'Windows IL2CPP build'; Missing = $windowsMissing; Runtime = 'Windows IL2CPP runtime' },
        @{ Name = 'Android IL2CPP build'; Missing = $androidMissing; Runtime = 'Android IL2CPP runtime' }
    )) {
        if ($pair.Missing.Count -gt 0) {
            Add-Result -Name $pair.Name -Status 'BLOCKED' -Detail ("not attempted; prerequisites missing: " + ($pair.Missing -join '; '))
        }
        else {
            Add-Result -Name $pair.Name -Status 'NOT_RUN' -Detail 'not requested; pass -Il2cppTarget Windows|Android'
        }
        Add-Result -Name $pair.Runtime -Status 'NOT_RUN' -Detail 'no IL2CPP player was built'
    }
}
else {
    $other = if ($Il2cppTarget -eq 'Windows') { 'Android' } else { 'Windows' }
    $otherMissing = if ($other -eq 'Windows') { $windowsMissing } else { $androidMissing }
    if ($otherMissing.Count -gt 0) {
        Add-Result -Name "$other IL2CPP build" -Status 'BLOCKED' -Detail ("not attempted; prerequisites missing: " + ($otherMissing -join '; '))
    }
    else {
        Add-Result -Name "$other IL2CPP build" -Status 'NOT_RUN' -Detail "not requested; selected target is $Il2cppTarget"
    }
    Add-Result -Name "$other IL2CPP runtime" -Status 'NOT_RUN' -Detail 'no IL2CPP player was built'

    try {
        Invoke-Il2cppTarget -Target $Il2cppTarget
    }
    catch {
        Add-Result -Name "$Il2cppTarget IL2CPP build" -Status 'FAIL' -Detail $_.Exception.Message
        Add-Result -Name "$Il2cppTarget IL2CPP runtime" -Status 'NOT_RUN' -Detail 'build failed before runtime'
    }
}

Add-Result -Name 'iOS IL2CPP' -Status 'BLOCKED' -Detail 'requires macOS/Xcode and a device; unavailable on Windows'

if ($tempRoot -and (Test-Path $tempRoot)) {
    if ($KeepTemp) {
        Write-Host "temp kept: $tempRoot"
    }
    else {
        try { Remove-TempRoot } catch { Write-Warning "temp cleanup skipped: $($_.Exception.Message)" }
    }
}

Write-Host ""
$resultsPath = Join-Path $runDir 'results.txt'
$script:results | Format-Table -AutoSize | Out-String | Tee-Object -FilePath $resultsPath
Write-Host "results  : $resultsPath"
Write-Host ("summary  : pass={0} fail={1} blocked={2} not_run={3}" -f `
    @($script:results | Where-Object { $_.Status -eq 'PASS' }).Count,
    @($script:results | Where-Object { $_.Status -eq 'FAIL' }).Count,
    @($script:results | Where-Object { $_.Status -eq 'BLOCKED' }).Count,
    @($script:results | Where-Object { $_.Status -eq 'NOT_RUN' }).Count)

if ($script:failed) {
    exit 1
}

if ($script:blocked -or $script:notRun) {
    exit 2
}

exit 0
