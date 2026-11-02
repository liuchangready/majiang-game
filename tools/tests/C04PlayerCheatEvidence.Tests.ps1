[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$DotNetPath,
    [Parameter(Mandatory = $true)][string]$VerifierDll,
    [Parameter(Mandatory = $true)][string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$module = Join-Path $repo 'tools/unity/c04-player-cheats-evidence.psm1'
if (-not (Test-Path -LiteralPath $DotNetPath -PathType Leaf)) { throw "DOTNET_MISSING: $DotNetPath" }
if (-not (Test-Path -LiteralPath $VerifierDll -PathType Leaf)) { throw "VERIFIER_DLL_MISSING: $VerifierDll" }
if (Test-Path -LiteralPath $OutputRoot) { throw "REFUSE_OVERWRITE: $OutputRoot" }
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
Import-Module $module -Force

$head = '63206301378338d1360fce6d3a5783b121072051'
$artifact = Join-Path $OutputRoot 'player.bin'
[System.IO.File]::WriteAllBytes($artifact, [byte[]](1, 2, 3, 4))
$artifactHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $artifact).Hash
$package = Join-Path $OutputRoot 'package-manifest.json'
@{
    kind = 'c04-player-package-manifest'
    sourceHead = $head
    files = @(@{ path = 'player.bin'; bytes = 4; sha256 = $artifactHash })
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $package -Encoding UTF8

$failures = New-Object System.Collections.Generic.List[string]
$count = 0
$realRoot = Join-Path $OutputRoot 'localhost-authorized'
$captureOutput = @(& $DotNetPath $VerifierDll capture-localhost-authorized `
    --output-root $realRoot --source-head $head --package-manifest $package 2>&1)
$captureExit = $LASTEXITCODE
$count++
if ($captureExit -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $realRoot 'case-authorized.json') -PathType Leaf)) {
    $failures.Add("real LocalHost production-channel capture failed native=$captureExit output=$($captureOutput -join ' ')") | Out-Null
} else {
    $realPositive = Invoke-C04PlayerCheatEvidence -DotNetPath $DotNetPath -VerifierDll $VerifierDll `
        -Evidence (Join-Path $realRoot 'case-authorized.json') -Case authorized -SourceHead $head -PackageManifest $package
    $count++
    if ($realPositive.NativeExit -ne 0 -or $realPositive.Output -notmatch '"status":"PASS"' -or
        $realPositive.Output -notmatch '"physicalCount":136' -or $realPositive.Output -notmatch '"balance":99') {
        $failures.Add("actual LocalHost serialized command/receipt/publication/recovery originals failed verification: $($realPositive.Output)") | Out-Null
    }
}

$negativeRoot = Join-Path $OutputRoot 'localhost-negative'
$negativeCapture = @(& $DotNetPath $VerifierDll capture-localhost-negative `
    --output-root $negativeRoot --source-head $head --package-manifest $package 2>&1)
$negativeCaptureExit = $LASTEXITCODE
$count++
if ($negativeCaptureExit -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $negativeRoot 'case-negative.json') -PathType Leaf)) {
    $failures.Add("real LocalHost negative capture failed native=$negativeCaptureExit output=$($negativeCapture -join ' ')") | Out-Null
} else {
    $realNegative = Invoke-C04PlayerCheatEvidence -DotNetPath $DotNetPath -VerifierDll $VerifierDll `
        -Evidence (Join-Path $negativeRoot 'case-negative.json') -Case negative -SourceHead $head -PackageManifest $package
    $count++
    if ($realNegative.NativeExit -ne 0 -or $realNegative.Output -notmatch '"status":"PASS"' -or
        $realNegative.Output -notmatch 'DECISION_MISMATCH' -or $realNegative.Output -notmatch 'CHEAT_INSUFFICIENT_FUNDS') {
        $failures.Add("actual two-row LocalHost business rejection originals failed verification: $($realNegative.Output)") | Out-Null
    }
}

foreach ($case in @('authorized', 'negative')) {
    $caseRoot = Join-Path $OutputRoot $case
    New-Item -ItemType Directory -Force -Path $caseRoot | Out-Null
    # Historical Strict-style fields deliberately claim PASS with plausible hashes and no
    # original bytes. The new CLI must stop on the absent raw index for both case kinds.
    $caseJson = Join-Path $caseRoot ('case-' + $case + '.json')
    @{
        schemaVersion = 1
        layer = 'WINDOWS_PLAYER'
        caseName = $case
        sourceHead = $head
        status = 'PASS'
        EvidenceSource = 'GAME'
        rawPayloadSha256 = ('A' * 64)
        assertions = @(@{ id = 'G3-05-FINAL'; pass = $true; expected = '136'; actual = '136' })
        requests = @(@{ requestId = ('B' * 32); status = 'APPLIED'; payloadSha256 = ('C' * 64); rawPayloadSha256 = ('D' * 64) })
        rawEvidenceIndex = 'raw-index.json'
        rawEvidenceIndexSha256 = ('A' * 64)
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $caseJson -Encoding UTF8
    $result = Invoke-C04PlayerCheatEvidence -DotNetPath $DotNetPath -VerifierDll $VerifierDll `
        -Evidence $caseJson -Case $case -SourceHead $head -PackageManifest $package
    $count++
    if ($result.NativeExit -eq 0 -or $result.Output -notmatch 'CAPTURE_FILE_MISSING') {
        $failures.Add("$case accepted evidence without raw originals or returned no structured missing-file reason") | Out-Null
    }
}

$escapeRoot = Join-Path $OutputRoot 'escape'
New-Item -ItemType Directory -Force -Path $escapeRoot | Out-Null
$escapeCase = Join-Path $escapeRoot 'case-authorized.json'
@{ sourceHead = $head; rawEvidenceIndex = '../escape.json'; rawEvidenceIndexSha256 = ('A' * 64) } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $escapeCase -Encoding UTF8
$escape = Invoke-C04PlayerCheatEvidence -DotNetPath $DotNetPath -VerifierDll $VerifierDll `
    -Evidence $escapeCase -Case authorized -SourceHead $head -PackageManifest $package
$count++
if ($escape.NativeExit -eq 0 -or $escape.Output -notmatch 'CAPTURE_PATH_TRAVERSAL') {
    $failures.Add('parent traversal was accepted or did not return its structural error') | Out-Null
}

# A syntactically valid caller-selected SHA-256 is not evidence. Keep the index
# metadata hash current so the CLI must reach and reject the raw member itself.
$hashRoot = Join-Path $OutputRoot 'arbitrary-raw-hash'
Copy-Item -LiteralPath $realRoot -Destination $hashRoot -Recurse
$indexPath = Join-Path $hashRoot 'raw/index.json'
$index = Get-Content -LiteralPath $indexPath -Raw | ConvertFrom-Json
$index.actions[0].command.sha256 = 'A' * 64
$indexJson = $index | ConvertTo-Json -Depth 20
[System.IO.File]::WriteAllText($indexPath, $indexJson, [System.Text.UTF8Encoding]::new($false))
$casePath = Join-Path $hashRoot 'case-authorized.json'
$metadata = Get-Content -LiteralPath $casePath -Raw | ConvertFrom-Json
$metadata.rawEvidenceIndexSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $indexPath).Hash
[System.IO.File]::WriteAllText($casePath, ($metadata | ConvertTo-Json -Depth 10), [System.Text.UTF8Encoding]::new($false))
$arbitraryHash = Invoke-C04PlayerCheatEvidence -DotNetPath $DotNetPath -VerifierDll $VerifierDll `
    -Evidence $casePath -Case authorized -SourceHead $head -PackageManifest $package
$count++
if ($arbitraryHash.NativeExit -eq 0 -or $arbitraryHash.Output -notmatch 'RAW_FILE_SHA_MISMATCH') {
    $failures.Add('arbitrary valid 64-hex raw hash was accepted or not reported') | Out-Null
}

# Missing one exact accepted private publication must fail even when all the
# surrounding historical summary claims and other raw members remain intact.
$missingRoot = Join-Path $OutputRoot 'missing-publication'
Copy-Item -LiteralPath $realRoot -Destination $missingRoot -Recurse
$indexPath = Join-Path $missingRoot 'raw/index.json'
$index = Get-Content -LiteralPath $indexPath -Raw | ConvertFrom-Json
$index.actions[0].acceptedPrivatePublication.path = 'raw/does-not-exist.pb'
$indexJson = $index | ConvertTo-Json -Depth 20
[System.IO.File]::WriteAllText($indexPath, $indexJson, [System.Text.UTF8Encoding]::new($false))
$casePath = Join-Path $missingRoot 'case-authorized.json'
$metadata = Get-Content -LiteralPath $casePath -Raw | ConvertFrom-Json
$metadata.rawEvidenceIndexSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $indexPath).Hash
[System.IO.File]::WriteAllText($casePath, ($metadata | ConvertTo-Json -Depth 10), [System.Text.UTF8Encoding]::new($false))
$missingPublication = Invoke-C04PlayerCheatEvidence -DotNetPath $DotNetPath -VerifierDll $VerifierDll `
    -Evidence $casePath -Case authorized -SourceHead $head -PackageManifest $package
$count++
if ($missingPublication.NativeExit -eq 0 -or $missingPublication.Output -notmatch 'CAPTURE_FILE_MISSING') {
    $failures.Add('missing exact accepted private publication was accepted or not reported') | Out-Null
}

foreach ($control in @(
    [pscustomobject]@{ Name = 'missing-command'; Member = 'command' },
    [pscustomobject]@{ Name = 'missing-receipt'; Member = 'receipt' },
    [pscustomobject]@{ Name = 'missing-recovery'; Member = 'after.authoritySave' }
)) {
    $missingRoot = Join-Path $OutputRoot $control.Name
    Copy-Item -LiteralPath $realRoot -Destination $missingRoot -Recurse
    $indexPath = Join-Path $missingRoot 'raw/index.json'
    $index = Get-Content -LiteralPath $indexPath -Raw | ConvertFrom-Json
    if ($control.Member -eq 'after.authoritySave') {
        $index.actions[0].after.authoritySave.path = 'raw/missing-recovery.save'
    } else {
        $index.actions[0].$($control.Member).path = "raw/$($control.Name).pb"
    }
    [System.IO.File]::WriteAllText($indexPath, ($index | ConvertTo-Json -Depth 20), [System.Text.UTF8Encoding]::new($false))
    $casePath = Join-Path $missingRoot 'case-authorized.json'
    $metadata = Get-Content -LiteralPath $casePath -Raw | ConvertFrom-Json
    $metadata.rawEvidenceIndexSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $indexPath).Hash
    [System.IO.File]::WriteAllText($casePath, ($metadata | ConvertTo-Json -Depth 10), [System.Text.UTF8Encoding]::new($false))
    $missingOriginal = Invoke-C04PlayerCheatEvidence -DotNetPath $DotNetPath -VerifierDll $VerifierDll `
        -Evidence $casePath -Case authorized -SourceHead $head -PackageManifest $package
    $count++
    if ($missingOriginal.NativeExit -eq 0 -or $missingOriginal.Output -notmatch 'CAPTURE_FILE_MISSING') {
        $failures.Add("missing $($control.Member) original was accepted or did not return CAPTURE_FILE_MISSING") | Out-Null
    }
}

$badPackage = Join-Path $OutputRoot 'wrong-package.json'
@{ kind = 'c04-player-package-manifest'; sourceHead = ('F' * 40); files = @(@{ path = 'player.bin'; bytes = 4; sha256 = $artifactHash }) } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $badPackage -Encoding UTF8
$badPackageResult = Invoke-C04PlayerCheatEvidence -DotNetPath $DotNetPath -VerifierDll $VerifierDll `
    -Evidence (Join-Path $OutputRoot 'authorized/case-authorized.json') -Case authorized -SourceHead $head -PackageManifest $badPackage
$count++
if ($badPackageResult.NativeExit -eq 0 -or $badPackageResult.Output -notmatch 'PACKAGE_SOURCE_HEAD_MISMATCH') {
    $failures.Add('wrong source/package binding was accepted or not reported') | Out-Null
}

# Typed mutation matrix against fresh serialized production-channel captures.
$secondRealRoot = Join-Path $OutputRoot 'localhost-authorized-second-run'
$secondCapture = @(& $DotNetPath $VerifierDll capture-localhost-authorized `
    --output-root $secondRealRoot --source-head $head --package-manifest $package 2>&1)
$secondCaptureExit = $LASTEXITCODE
$count++
if ($secondCaptureExit -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $secondRealRoot 'case-authorized.json') -PathType Leaf)) {
    $failures.Add("second real LocalHost run for splice control failed native=$secondCaptureExit output=$($secondCapture -join ' ')") | Out-Null
} else {
    $matrixRoot = Join-Path $OutputRoot 'typed-mutation-matrix'
    New-Item -ItemType Directory -Path $matrixRoot | Out-Null
    $wireDll = Join-Path (Split-Path -Parent $VerifierDll) 'Mahjong.Contracts.Wire.dll'
    $protobufDll = Join-Path (Split-Path -Parent $VerifierDll) 'Google.Protobuf.dll'
    $mutatorProject = Join-Path $matrixRoot 'C04EvidenceMutator.csproj'
    @"
<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><LangVersion>10.0</LangVersion><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup><ItemGroup><Reference Include='Mahjong.Contracts.Wire'><HintPath>$wireDll</HintPath></Reference><Reference Include='Google.Protobuf'><HintPath>$protobufDll</HintPath></Reference></ItemGroup></Project>
"@ | Set-Content -LiteralPath $mutatorProject -Encoding UTF8
    @'
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Mahjong.Contracts.Wire;
static string H(byte[] b)=>Convert.ToHexString(SHA256.HashData(b));
static void CopyTree(string s,string d){Directory.CreateDirectory(d);foreach(var f in Directory.GetFiles(s))File.Copy(f,Path.Combine(d,Path.GetFileName(f)),true);foreach(var x in Directory.GetDirectories(s))CopyTree(x,Path.Combine(d,Path.GetFileName(x)));}
static JsonObject Index(string r)=>JsonNode.Parse(File.ReadAllBytes(Path.Combine(r,"raw/index.json")))!.AsObject();
static string PathOf(string r,JsonObject x)=>Path.Combine(r,x["path"]!.GetValue<string>().Replace('/',Path.DirectorySeparatorChar));
static void Edit(JsonObject row,string r,string key,Action<Envelope> fn,JsonObject? index=null){var x=row[key]!.AsObject();var p=PathOf(r,x);var old=File.ReadAllBytes(p);var e=Envelope.Parser.ParseFrom(old);fn(e);var b=e.ToByteArray();File.WriteAllBytes(p,b);x["sha256"]=H(b);if(index!=null){string set=key=="command"?"sentFrames":(key=="receipt"||key=="acceptedPrivatePublication")?"receivedFrames":"";if(set.Length>0)foreach(var frame in index[set]!.AsArray()){var f=frame!.AsObject();var fp=PathOf(r,f);var raw=File.ReadAllBytes(fp);if(raw.SequenceEqual(old)){File.WriteAllBytes(fp,b);f["sha256"]=H(b);}}}}
static void EditReceived(JsonObject index,string r,Func<Envelope,bool> match,Action<Envelope> edit){foreach(var frame in index["receivedFrames"]!.AsArray()){var f=frame!.AsObject();var p=PathOf(r,f);var old=File.ReadAllBytes(p);var e=Envelope.Parser.ParseFrom(old);if(!match(e))continue;edit(e);var b=e.ToByteArray();File.WriteAllBytes(p,b);f["sha256"]=H(b);return;}throw new InvalidDataException("RECEIVED_FRAME_NOT_FOUND");}
static void CopyRef(JsonObject x,string s,string d){string rel=x["path"]!.GetValue<string>();File.Copy(Path.Combine(s,rel.Replace('/',Path.DirectorySeparatorChar)),Path.Combine(d,rel.Replace('/',Path.DirectorySeparatorChar)),true);}
static void Save(string r,JsonObject i,string kind){var ip=Path.Combine(r,"raw/index.json");File.WriteAllText(ip,i.ToJsonString(new(){WriteIndented=true}));var cp=Path.Combine(r,"case-"+kind+".json");var c=JsonNode.Parse(File.ReadAllBytes(cp))!.AsObject();c["rawEvidenceIndexSha256"]=H(File.ReadAllBytes(ip));File.WriteAllText(cp,c.ToJsonString(new(){WriteIndented=true}));}
static int RunVerifier(string dotnet,string dll,string r,string kind,string head,string package,string result){var p=new ProcessStartInfo(dotnet){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var a in new[]{dll,"verify","--evidence",Path.Combine(r,"case-"+kind+".json"),"--case",kind,"--source-head",head,"--package-manifest",package})p.ArgumentList.Add(a);using var x=Process.Start(p)!;string o=x.StandardOutput.ReadToEnd(),e=x.StandardError.ReadToEnd();if(!x.WaitForExit(20000)){x.Kill(true);throw new TimeoutException("VERIFY_TIMEOUT");}File.WriteAllText(result,o+e);return x.ExitCode;}
string output=args[0],dll=args[1],dotnet=args[2],head=args[3],package=args[4],auth=args[5],negative=args[6],second=args[7];var log=new List<string>();int bad=0,rejectedCount=0,positiveCount=0;
var expected=new Dictionary<string,string>{
{"receipt-foreign-binding","RECEIPT_ENVELOPE_BINDING_MISMATCH"},{"receipt-scope-room","RECEIPT_ENVELOPE_BINDING_MISMATCH"},
{"role","COMMAND_SCOPE_NOT_PLAYER"},{"account","RECEIPT_IDENTITY_MISMATCH"},{"scope-id","RECEIPT_IDENTITY_MISMATCH"},
{"decision","CANONICAL_DECISION_MISMATCH"},{"round","CANONICAL_ROUND_MISMATCH"},{"epoch","COMMAND_CONTROL_EPOCH_MISMATCH"},{"version","COMMAND_BASELINE_VERSION_MISMATCH"},
{"canonical","RECEIPT_CANONICAL_DIGEST_MISMATCH"},{"receipt-evaluated-version","RECEIPT_EVALUATED_VERSION_MISMATCH"},{"receipt-command-version","PUBLICATION_COMMIT_HIGH_WATER_MISMATCH"},{"receipt-event-highwater","PUBLICATION_COMMIT_HIGH_WATER_MISMATCH"},
{"snapshot-session","PRIVATE_SNAPSHOT_IDENTITY_MISMATCH"},{"publication-highwater","PUBLICATION_COMMIT_HIGH_WATER_MISMATCH"},{"publication-owner-seat","WIRE_DECODE_MalformedBody"},{"publication-missing-event","PUBLICATION_MISSING_MATCHED_PRIVATE_EVENT"},
{"ledger","PRIVATE_LEDGER_ENTRIES_NOT_EXACT_RECOVERY"},{"event-payload","PRIVATE_CHEAT_EVENT_PAYLOAD_MISMATCH"},{"private-hand","PRIVATE_HAND_DOES_NOT_MATCH_EXACT_RECOVERY"},{"second-private-result","SECOND_PRIVATE_EVENT_PAYLOAD_MISMATCH"},{"beam-offer","SWAP_OFFER_ID_MISMATCH"},
{"stream-cursors","PUBLICATION_STREAM_VERSION_AHEAD_OF_SNAPSHOT"},{"projection-cursors","PLAYER_HISTORY_PROJECTION_SEQUENCE_GAP_OR_SHIFT"},
{"negative-room-session-seat","WELCOME_COMMAND_BINDING_MISMATCH"},{"forged-negative-reason","INSUFFICIENT_FUNDS_REASON_NOT_AUTHORITY_CHEAT_CODE"},{"rejection-missing-durable","RECOVERY_REQUEST_BINDING_MISSING_OR_MISMATCH"},
{"protocol-internal-rejection","PROTOCOL_ERROR_IS_NOT_BUSINESS_REJECTION"},{"protocol-malformed-rejection","PROTOCOL_ERROR_IS_NOT_BUSINESS_REJECTION"},{"same-negative-payload-different-requests","NEGATIVE_PAYLOADS_NOT_DISTINCT"},{"cross-run-first-row-splice","PRIOR_CAPTURED_REQUEST_NOT_IN_SAME_DURABLE_RUN"},
{"final-mark-groups-deleted","PRIVATE_MARK_GROUP_SET_MISMATCH"},{"mark-group-id-forged","PRIVATE_MARK_GROUP_SET_MISMATCH"},{"mark-round-forged","PRIVATE_MARK_GROUP_GENERATION_MISMATCH"},{"mark-location-forged","PRIVATE_MARK_ENTITY_REDACTION_MISMATCH"},
{"stale-sleeve-offer-after-second","PRIVATE_SLEEVE_IMMEDIATE_OFFER_PRESENCE_MISMATCH"},{"stale-beam-offer-after-swap","PRIVATE_BEAM_OFFER_PRESENCE_MISMATCH"},{"non-action-history-fromcursor-corrupt","PLAYER_HISTORY_FROM_CURSOR_PREDECESSOR_MISMATCH"},{"durable-negative-reason-disagrees","DURABLE_REJECTION_REASON_MISMATCH"}};
void Case(string name,bool neg,Action<string,JsonObject> mutate){string kind=neg?"negative":"authorized",src=neg?negative:auth,r=Path.Combine(output,"cases",name);CopyTree(src,r);var i=Index(r);mutate(r,i);Save(r,i,kind);int code=RunVerifier(dotnet,dll,r,kind,head,package,Path.Combine(r,"result.json"));var result=JsonNode.Parse(File.ReadAllBytes(Path.Combine(r,"result.json")))!.AsObject();string errors=result["errors"]!.ToJsonString();bool failed=code!=0&&result["status"]!.GetValue<string>()=="FAIL"&&result["errors"]!.AsArray().Count>0&&errors.Contains(expected[name],StringComparison.Ordinal);if(failed)rejectedCount++;else bad++;string row=$"{name} native={code} expected={expected[name]} rejected={failed} errors={errors}";log.Add(row);Console.WriteLine(row);}
void Positive(string name,Action<string,JsonObject> mutate){string r=Path.Combine(output,"cases",name);CopyTree(auth,r);var i=Index(r);mutate(r,i);Save(r,i,"authorized");int code=RunVerifier(dotnet,dll,r,"authorized",head,package,Path.Combine(r,"result.json"));var result=JsonNode.Parse(File.ReadAllBytes(Path.Combine(r,"result.json")))!.AsObject();bool passed=code==0&&result["status"]!.GetValue<string>()=="PASS"&&result["errors"]!.AsArray().Count==0;if(passed)positiveCount++;else bad++;string row=$"{name} native={code} positive={passed} errors={result["errors"]}";log.Add(row);Console.WriteLine(row);}
Positive("stream-version-lags-snapshot-within-commit-window",(r,i)=>{Edit(i["actions"]![0]!.AsObject(),r,"acceptedPrivatePublication",e=>{e.Cursor.StreamVersion=0;e.PlayerUpdate.Cursor.StreamVersion=0;e.PlayerUpdate.Snapshot.Cursor.StreamVersion=0;},i);var pub=Envelope.Parser.ParseFrom(File.ReadAllBytes(PathOf(r,i["actions"]![0]!["acceptedPrivatePublication"]!.AsObject())));var predecessor=pub.PlayerUpdate.Cursor.Clone();EditReceived(i,r,e=>e.MessageType==MessageType.PlayerUpdate&&e.PlayerUpdate.FromCursor.ProjectionSeq==predecessor.ProjectionSeq,e=>e.PlayerUpdate.FromCursor=predecessor.Clone());});
Positive("later-legitimate-recovery-retains-exact-commit",(r,i)=>i["actions"]![0]!["after"]=i["actions"]![1]!["before"]!.DeepClone());
Case("receipt-foreign-binding",false,(r,i)=>Edit(i["actions"]![0]!.AsObject(),r,"receipt",e=>{e.RoomId="foreign-room";e.SessionId="foreign-session";e.SeatId=3;}));
Case("receipt-scope-room",false,(r,i)=>Edit(i["actions"]![0]!.AsObject(),r,"receipt",e=>e.Scope!.RoomId="foreign-room"));
Case("role",false,(r,i)=>Edit(i["actions"]![0]!.AsObject(),r,"command",e=>e.Scope!.Role=Role.Spectator,i));
Case("account",false,(r,i)=>Edit(i["actions"]![0]!.AsObject(),r,"command",e=>e.Scope!.AccountId="foreign-account",i));
Case("scope-id",false,(r,i)=>Edit(i["actions"]![0]!.AsObject(),r,"command",e=>e.Scope!.ScopeId="foreign-scope",i));
Case("decision",false,(r,i)=>Edit(i["actions"]![0]!.AsObject(),r,"command",e=>e.Command.ExpectedDecisionId="foreign-decision",i));
Case("round",false,(r,i)=>Edit(i["actions"]![0]!.AsObject(),r,"command",e=>e.Command.ExpectedRoundId="foreign-round",i));
Case("epoch",false,(r,i)=>Edit(i["actions"]![0]!.AsObject(),r,"command",e=>e.ControlEpoch++,i));
Case("version",false,(r,i)=>Edit(i["actions"]![0]!.AsObject(),r,"command",e=>e.Command.ExpectedStateVersion++,i));
Case("canonical",false,(r,i)=>Edit(i["actions"]![0]!.AsObject(),r,"receipt",e=>e.Receipt.CanonicalPayloadSha256=new string('F',64),i));
Case("receipt-evaluated-version",false,(r,i)=>Edit(i["actions"]![0]!.AsObject(),r,"receipt",e=>e.Receipt.EvaluatedStateVersion++,i));
Case("receipt-command-version",false,(r,i)=>Edit(i["actions"]![0]!.AsObject(),r,"receipt",e=>e.Receipt.CommandVersion++,i));
Case("receipt-event-highwater",false,(r,i)=>Edit(i["actions"]![0]!.AsObject(),r,"receipt",e=>e.Receipt.EventSeq++,i));
Case("snapshot-session",false,(r,i)=>Edit(i["actions"]![0]!.AsObject(),r,"acceptedPrivatePublication",e=>e.PlayerUpdate.Snapshot.SessionId="foreign-session",i));
Case("publication-highwater",false,(r,i)=>Edit(i["actions"]![0]!.AsObject(),r,"acceptedPrivatePublication",e=>e.PlayerUpdate.Snapshot.EventSeq=999999,i));
Case("publication-owner-seat",false,(r,i)=>Edit(i["actions"]![0]!.AsObject(),r,"acceptedPrivatePublication",e=>e.PlayerUpdate.Snapshot.PrivateView.Seat=1,i));
Case("publication-missing-event",false,(r,i)=>Edit(i["actions"]![0]!.AsObject(),r,"acceptedPrivatePublication",e=>e.PlayerUpdate.VisibleEvents.Clear(),i));
Case("ledger",false,(r,i)=>Edit(i["actions"]![4]!.AsObject(),r,"acceptedPrivatePublication",e=>{var x=e.PlayerUpdate.Snapshot.PrivateView.Cheat.LedgerEntries[0];x.Amount=-999;x.BalanceAfter=0;x.ReasonCode="FORGED";},i));
Case("event-payload",false,(r,i)=>Edit(i["actions"]![0]!.AsObject(),r,"acceptedPrivatePublication",e=>e.PlayerUpdate.VisibleEvents.Single(x=>x.CheatPointsChanged!=null).CheatPointsChanged.Amount=-777,i));
Case("private-hand",false,(r,i)=>Edit(i["actions"]![0]!.AsObject(),r,"acceptedPrivatePublication",e=>e.PlayerUpdate.Snapshot.PrivateView.ClosedTiles.Clear(),i));
Case("second-private-result",false,(r,i)=>Edit(i["actions"]![2]!.AsObject(),r,"acceptedPrivatePublication",e=>e.PlayerUpdate.VisibleEvents.Single(x=>x.SleeveBloomResolved!=null).SleeveBloomResolved.ResultTileId++,i));
Case("beam-offer",false,(r,i)=>Edit(i["actions"]![3]!.AsObject(),r,"acceptedPrivatePublication",e=>e.PlayerUpdate.Snapshot.PrivateView.Cheat.BeamSwap.OfferId="wrong-offer",i));
Case("stream-cursors",false,(r,i)=>{for(int n=0;n<5;n++)Edit(i["actions"]![n]!.AsObject(),r,"acceptedPrivatePublication",e=>{e.Cursor.StreamVersion=999;e.PlayerUpdate.Cursor.StreamVersion=999;e.PlayerUpdate.Snapshot.Cursor.StreamVersion=999;},i);});
Case("projection-cursors",false,(r,i)=>{for(int n=0;n<5;n++)Edit(i["actions"]![n]!.AsObject(),r,"acceptedPrivatePublication",e=>{e.Cursor.ProjectionSeq+=100;e.PlayerUpdate.Cursor.ProjectionSeq+=100;e.PlayerUpdate.Snapshot.Cursor.ProjectionSeq+=100;},i);});
Case("final-mark-groups-deleted",false,(r,i)=>Edit(i["actions"]![4]!.AsObject(),r,"acceptedPrivatePublication",e=>e.PlayerUpdate.Snapshot.PrivateView.Cheat.TileMark.Groups.Clear(),i));
Case("mark-group-id-forged",false,(r,i)=>Edit(i["actions"]![0]!.AsObject(),r,"acceptedPrivatePublication",e=>e.PlayerUpdate.Snapshot.PrivateView.Cheat.TileMark.Groups[0].GroupId="foreign-group",i));
Case("mark-round-forged",false,(r,i)=>Edit(i["actions"]![0]!.AsObject(),r,"acceptedPrivatePublication",e=>e.PlayerUpdate.Snapshot.PrivateView.Cheat.TileMark.Groups[0].CreatedRoundId="foreign-round",i));
Case("mark-location-forged",false,(r,i)=>Edit(i["actions"]![0]!.AsObject(),r,"acceptedPrivatePublication",e=>e.PlayerUpdate.Snapshot.PrivateView.Cheat.TileMark.Groups[0].Tiles[0].Seat=3,i));
Case("stale-sleeve-offer-after-second",false,(r,i)=>{var p=PathOf(r,i["actions"]![1]!["acceptedPrivatePublication"]!.AsObject());var offer=Envelope.Parser.ParseFrom(File.ReadAllBytes(p)).PlayerUpdate.Snapshot.PrivateView.Cheat.SleeveBloom.Clone();Edit(i["actions"]![2]!.AsObject(),r,"acceptedPrivatePublication",e=>e.PlayerUpdate.Snapshot.PrivateView.Cheat.SleeveBloom=offer,i);});
Case("stale-beam-offer-after-swap",false,(r,i)=>{var p=PathOf(r,i["actions"]![3]!["acceptedPrivatePublication"]!.AsObject());var offer=Envelope.Parser.ParseFrom(File.ReadAllBytes(p)).PlayerUpdate.Snapshot.PrivateView.Cheat.BeamSwap.Clone();Edit(i["actions"]![4]!.AsObject(),r,"acceptedPrivatePublication",e=>e.PlayerUpdate.Snapshot.PrivateView.Cheat.BeamSwap=offer,i);});
Case("non-action-history-fromcursor-corrupt",false,(r,i)=>{foreach(var frame in i["receivedFrames"]!.AsArray()){var f=frame!.AsObject();var p=PathOf(r,f);var old=File.ReadAllBytes(p);var e=Envelope.Parser.ParseFrom(old);if(e.MessageType==MessageType.PlayerUpdate&&e.PlayerUpdate.Cursor.ProjectionSeq==3){e.PlayerUpdate.FromCursor.ProjectionSeq=999;var b=e.ToByteArray();File.WriteAllBytes(p,b);f["sha256"]=H(b);return;}}throw new InvalidDataException("NO_INTERMEDIATE_PLAYER_UPDATE");});
Case("durable-negative-reason-disagrees",true,(r,i)=>{var x=i["actions"]![0]!["after"]!["authoritySave"]!.AsObject();var p=PathOf(r,x);var b=File.ReadAllBytes(p);var from=Encoding.UTF8.GetBytes("DECISION_MISMATCH");var to=Encoding.UTF8.GetBytes("MALFORMED_MESSAGE");int found=0;for(int n=44;n<=b.Length-from.Length;n++)if(b.AsSpan(n,from.Length).SequenceEqual(from)){to.CopyTo(b,n);found++;}if(found==0)throw new InvalidDataException("NO_PERSISTED_REJECTION_REASON");SHA256.HashData(b.AsSpan(44)).CopyTo(b,12);File.WriteAllBytes(p,b);x["sha256"]=H(b);});
Case("negative-room-session-seat",true,(r,i)=>{var x=i["actions"]![1]!.AsObject();Edit(x,r,"command",e=>{e.RoomId="foreign";e.SessionId="foreign";e.SeatId=3;},i);Edit(x,r,"receipt",e=>{e.RoomId="foreign";e.SessionId="foreign";e.SeatId=3;},i);});
Case("forged-negative-reason",true,(r,i)=>Edit(i["actions"]![1]!.AsObject(),r,"receipt",e=>e.Receipt.ReasonCode="INSUFFICIENT_FUNDS",i));
Case("rejection-missing-durable",true,(r,i)=>{Edit(i["actions"]![1]!.AsObject(),r,"receipt",e=>e.Receipt.ReasonCode="INSUFFICIENT_FUNDS",i);for(int n=0;n<2;n++)i["actions"]![n]!["after"]=i["actions"]![n]!["before"]!.DeepClone();});
Case("protocol-internal-rejection",true,(r,i)=>Edit(i["actions"]![0]!.AsObject(),r,"receipt",e=>e.Receipt.ReasonCode="INTERNAL_ERROR",i));
Case("protocol-malformed-rejection",true,(r,i)=>Edit(i["actions"]![0]!.AsObject(),r,"receipt",e=>e.Receipt.ReasonCode="MALFORMED_MESSAGE",i));
Case("same-negative-payload-different-requests",true,(r,i)=>{var second=Envelope.Parser.ParseFrom(File.ReadAllBytes(PathOf(r,i["actions"]![1]!["command"]!.AsObject())));var first=i["actions"]![0]!.AsObject();Edit(first,r,"command",e=>{e.Command.Type=CommandType.SubmitSleeveBloom;e.Command.CanonicalPayload=second.Command.CanonicalPayload;e.Command.ExpectedDecisionId=second.Command.ExpectedDecisionId;},i);});
Case("cross-run-first-row-splice",false,(r,i)=>{var s=Index(second);var row=s["actions"]![0]!.AsObject();foreach(var key in new[]{"welcome","command","receipt","acceptedPrivatePublication"})CopyRef(row[key]!.AsObject(),second,r);foreach(var side in new[]{"before","after"})foreach(var pair in row[side]!.AsObject())CopyRef(pair.Value!.AsObject(),second,r);i["actions"]![0]=row.DeepClone();});
File.WriteAllLines(Path.Combine(output,"matrix-results.log"),log);string summary=$"TYPED_MATRIX total={log.Count} rejected={rejectedCount} acceptedPositive={positiveCount} unexpected={bad} skipped=0";File.WriteAllText(Path.Combine(output,"matrix-summary.txt"),summary);Console.WriteLine(summary);return bad==0&&rejectedCount==39&&positiveCount==2&&log.Count==41?0:1;
'@ | Set-Content -LiteralPath (Join-Path $matrixRoot 'Program.cs') -Encoding UTF8
    $mutatorProjectBuild = @(& $DotNetPath build $mutatorProject -v:q 2>&1)
    $mutatorBuildExit = $LASTEXITCODE
    $count++
    if ($mutatorBuildExit -ne 0) {
        $failures.Add("typed mutation harness build failed native=$mutatorBuildExit output=$($mutatorProjectBuild -join ' ')") | Out-Null
    } else {
        $mutatorDll = Join-Path $matrixRoot 'bin/Debug/net8.0/C04EvidenceMutator.dll'
        $matrixOutput = @(& $DotNetPath $mutatorDll $OutputRoot $VerifierDll $DotNetPath $head $package $realRoot $negativeRoot $secondRealRoot 2>&1)
        $matrixExit = $LASTEXITCODE
        $count++
        if ($matrixExit -ne 0 -or ($matrixOutput -join "`n") -notmatch 'TYPED_MATRIX total=41 rejected=39 acceptedPositive=2 unexpected=0 skipped=0') {
            $failures.Add("typed verifier mutation matrix failed native=$matrixExit output=$($matrixOutput -join ' ')") | Out-Null
        }
    }
}

Write-Host "C04PlayerCheatEvidence.Tests total=$count passed=$($count - $failures.Count) failed=$($failures.Count) layer=TOOL_FIXTURE"
foreach ($failure in $failures) { Write-Host "FAIL $failure" }
if ($failures.Count -gt 0) { exit 1 }
exit 0
