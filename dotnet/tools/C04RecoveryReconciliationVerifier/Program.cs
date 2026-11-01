using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.LocalHost;
using WireEnvelope = Mahjong.Contracts.Wire.Envelope;
using WireLookupStatus = Mahjong.Contracts.Wire.LookupStatus;
using WireReceiptStatus = Mahjong.Contracts.Wire.ReceiptStatus;

namespace C04RecoveryReconciliationVerifier
{
    internal static class Program
    {
        private static readonly HashSet<string> Findings = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> Checks = new HashSet<string>(StringComparer.Ordinal);

        private static int Main(string[] args)
        {
            var options = Parse(args);
            string resultPath = Required(options, "--result");
            string expectedPath = Required(options, "--expected");
            string inputPath = Required(options, "--input");
            string outputPath = Required(options, "--output");
            string root = Path.GetDirectoryName(Path.GetFullPath(resultPath)) ?? ".";
            try
            {
                JsonElement result = ReadJson(resultPath);
                JsonElement input = ReadJson(inputPath);
                Dictionary<string, string> expected = ReadExpected(expectedPath);
                string caseId = Text(result, "caseId");
                string fault = Text(result, "fault");
                string outcome = Text(result, "expectedOutcome");
                Require(Text(result, "kind") == "c04-recovery-result", "result kind");
                Require(caseId == Text(input, "caseId"), "caseId mismatch");
                Require(fault == Text(input, "fault"), "fault mismatch");
                Require(outcome == Text(input, "expectedOutcome"), "expectedOutcome mismatch");
                ValidateCasePolicy(caseId, fault, outcome, input);
                ValidateBuildProcess(result, input);
                ValidateRequest(result, expected);
                if (outcome != "REJECTED")
                {
                    ValidateBinding(result, expected);
                }
                else
                {
                    Require(!result.TryGetProperty("binding", out JsonElement refusedBinding) ||
                            refusedBinding.ValueKind == JsonValueKind.Null,
                        "REJECTED result must not publish a Ready/session binding");
                    Check("rejectedNoBinding");
                }
                ValidateAuthorityFiles(root, result);
                ValidateFaultBoundary(root, expected, input);

                List<Frame> frames = ReadFrames(result, root);
                if (outcome == "RECOVERED" || outcome == "RECOVERED_FROM_BACKUP")
                {
                    ValidateRecovered(root, result, input, expected, frames);
                }
                else if (outcome == "FRESH_START")
                {
                    ValidateFresh(root, result, input, expected, frames);
                }
                else if (outcome == "REJECTED")
                {
                    ValidateRejected(root, result, input, expected, frames);
                }
                else
                {
                    Require(false, "unsupported outcome " + outcome);
                }
            }
            catch (Exception exception)
            {
                Findings.Add(exception.GetType().Name + ": " + exception.Message);
            }

            var output = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["schemaVersion"] = 1,
                ["kind"] = "c04-recovery-verification",
                ["status"] = Findings.Count == 0 ? "PASS" : "FAIL",
                ["resultPath"] = Path.GetFullPath(resultPath),
                ["expectedPath"] = Path.GetFullPath(expectedPath),
                ["inputPath"] = Path.GetFullPath(inputPath),
                ["checks"] = Checks.OrderBy(c => c, StringComparer.Ordinal).ToArray(),
                ["errors"] = Findings.OrderBy(c => c, StringComparer.Ordinal).ToArray(),
                ["verifiedAt"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            File.WriteAllText(
                outputPath,
                JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }),
                new System.Text.UTF8Encoding(false));
            Console.WriteLine("C04_DOTNET_VERIFY " + output["status"] + " errors=" + Findings.Count);
            foreach (string finding in Findings)
            {
                Console.WriteLine("C04_DOTNET_VERIFY_ERROR " + finding);
            }

            return Findings.Count == 0 ? 0 : 1;
        }

        private static void ValidateCasePolicy(string caseId, string fault, string outcome, JsonElement input)
        {
            var policy = new Dictionary<string, (string Fault, string Outcome)>(StringComparer.Ordinal)
            {
                ["C04-G6-01"] = ("None", "RECOVERED"),
                ["C04-G6-02"] = ("None", "RECOVERED"),
                ["C04-G6-03"] = ("TruncatePrimary", "RECOVERED_FROM_BACKUP"),
                ["C04-G6-04"] = ("BadChecksumPrimary", "RECOVERED_FROM_BACKUP"),
                ["C04-G6-05"] = ("MissingPrimaryWithBackup", "RECOVERED_FROM_BACKUP"),
                ["C04-G6-06"] = ("BothMissing", "FRESH_START"),
                ["C04-G6-07"] = ("BothBad", "REJECTED"),
                ["C04-G6-08"] = ("IncompatiblePrimary", "REJECTED"),
            };
            Require(policy.TryGetValue(caseId, out (string Fault, string Outcome) expected),
                "unexpected case " + caseId);
            Require(expected.Fault == fault, caseId + " fault does not match the fixed policy");
            Require(expected.Outcome == outcome, caseId + " outcome does not match the fixed policy");
            if (caseId == "C04-G6-02")
            {
                Require(Bool(input, "force"), "C04-G6-02 must bind a force-killed prepare");
            }
            else
            {
                Require(!Bool(input, "force"), "non-Force row must not carry force=true");
            }

            Check("casePolicy");
        }

        private static void ValidateBuildProcess(JsonElement result, JsonElement input)
        {
            JsonElement build = Property(result, "build");
            Require(Text(build, "playerSha256") == Text(input, "playerSha256"), "player SHA mismatch");
            Require(Text(build, "packageSha256") == Text(input, "packageSha256"), "package SHA mismatch");
            Require(Text(build, "sourceHead") == Text(input, "sourceHead"), "sourceHead mismatch");
            Require(Text(build, "buildCommit") == Text(input, "buildCommit"), "buildCommit mismatch");
            JsonElement process = Property(result, "process");
            Require(Text(process, "sha256") == Text(input, "playerSha256"), "process SHA mismatch");
            Require(!string.IsNullOrWhiteSpace(Text(process, "path")) &&
                    Text(process, "path").EndsWith("Mahjong.exe", StringComparison.OrdinalIgnoreCase),
                "process path is not the bound Player exe");
            Require(Int(process, "pid") > 0, "process PID is missing");
            Require(!string.IsNullOrWhiteSpace(Text(process, "startUtc")), "process start is missing");
            Check("buildProcess");
        }

        private static void ValidateRequest(JsonElement result, Dictionary<string, string> expected)
        {
            JsonElement request = Property(result, "request");
            Require(Text(request, "requestId") == expected["requestId"], "requestId mismatch");
            Require(Text(request, "scopeId") == expected["scopeId"], "scopeId mismatch");
            Require(Text(request, "accountId") == expected["accountId"], "accountId mismatch");
            Require(Text(request, "payloadSha256") == expected["payloadSha256"], "payload SHA mismatch");
            Require(Text(result, "canonicalDigest") == expected["canonicalDigest"], "canonical digest mismatch");
            Check("requestIdentity");
        }

        private static void ValidateBinding(JsonElement result, Dictionary<string, string> expected)
        {
            JsonElement binding = Property(result, "binding");
            Require(Text(binding, "roomId") == "local-main", "binding room is not local-main");
            Require(Int(binding, "seat") == 0, "binding seat is not 0");
            Require(Text(binding, "role") == "Player", "binding role is not Player");
            Require(!string.IsNullOrWhiteSpace(Text(binding, "sessionId")), "binding session is empty");
            Require(!string.IsNullOrWhiteSpace(Text(binding, "scopeId")), "binding scope is empty");
            Require(UInt(binding, "bindingEpoch") > 0, "binding epoch is not positive");
            Require(Text(binding, "scopeId") != expected["scopeId"], "new binding reused the old scope");
            Check("binding");
        }

        private static void ValidateAuthorityFiles(string root, JsonElement result)
        {
            JsonElement files = Property(Property(result, "authority"), "files");
            int count = 0;
            foreach (JsonElement item in files.EnumerateArray())
            {
                string relative = Text(item, "path");
                string path = Path.Combine(root, relative);
                Require(File.Exists(path), "authority file is missing: " + relative);
                Require(Sha256File(path) == Text(item, "sha256"), "authority SHA mismatch: " + relative);
                Require(new FileInfo(path).Length == Long(item, "bytes"),
                    "authority byte length mismatch: " + relative);
                count++;
            }

            Require(count >= 2, "authority map has too few raw files");
            Check("authorityFiles");
        }

        private static List<Frame> ReadFrames(JsonElement result, string root)
        {
            var frames = new List<Frame>();
            foreach (JsonElement item in Property(result, "frames").EnumerateArray())
            {
                string relative = Text(item, "file");
                Require(!string.IsNullOrWhiteSpace(relative), "frame file is empty");
                string path = Path.Combine(root, relative);
                Require(File.Exists(path), "frame file is missing: " + relative);
                byte[] bytes = File.ReadAllBytes(path);
                Require(Sha256(bytes) == Text(item, "sha256"), "frame SHA mismatch: " + relative);
                WireEnvelope envelope = WireEnvelope.Parser.ParseFrom(bytes);
                var frame = new Frame(envelope, Text(item, "direction"), Text(item, "type"),
                    Text(item, "requestId"), Text(item, "scopeId"), Text(item, "status"),
                    Text(item, "correlationId"));
                Require(frame.Direction == "OUT" || frame.Direction == "IN", "invalid frame direction");
                Require(frame.Type == envelope.BodyCase.ToString(), "frame type metadata does not decode");
                Require(frame.RequestId == RequestIdOf(envelope), "frame request metadata does not decode");
                Require(frame.ScopeId == ScopeIdOf(envelope), "frame scope metadata does not decode");
                Require(frame.Status == StatusOf(envelope), "frame status metadata does not decode");
                Require(frame.CorrelationId == (envelope.CorrelationId ?? string.Empty),
                    "frame correlation metadata does not decode");
                frames.Add(frame);
            }

            Require(frames.Count > 0 || Text(result, "actualOutcome") == "REJECTED",
                "no raw frames were retained");
            Check("frameDecode");
            return frames;
        }

        private static void ValidateRecovered(
            string root,
            JsonElement result,
            JsonElement input,
            Dictionary<string, string> expected,
            List<Frame> frames)
        {
            Require(Text(result, "actualOutcome") == Text(result, "expectedOutcome"),
                "actual outcome is not the expected recovered outcome");
            ValidateWireIdentity(root, result, expected, frames);
            JsonElement prepare = ReadJson(Path.Combine(root, "prepare.json"));
            Require(Text(prepare, "api") == "SUBMIT_ACTION", "prepare API is not SUBMIT_ACTION");
            ValidatePreparePost(prepare, expected);
            ValidateSnapshotFacts(root, prepare, result, expected);

            string oldRequest = expected["requestId"];
            string oldScope = expected["scopeId"];
            List<Frame> oldQueries = frames.Where(f => f.Direction == "OUT" &&
                f.Type == "ReceiptQuery" && f.RequestId == oldRequest && f.ScopeId == oldScope).ToList();
            Require(oldQueries.Count == 1, "expected exactly one original receipt query");
            string correlation = oldQueries[0].CorrelationId;
            Require(!string.IsNullOrWhiteSpace(correlation), "receipt query correlation is empty");
            List<Frame> lookups = frames.Where(f => f.Direction == "IN" &&
                f.Type == "ReceiptLookup" && f.CorrelationId == correlation).ToList();
            Require(lookups.Count == 1, "expected exactly one correlated inbound Applied lookup");
            WireEnvelope lookupEnvelope = lookups[0].Envelope;
            var lookup = lookupEnvelope.ReceiptLookup;
            Require(lookup != null && lookup.Status == WireLookupStatus.Found,
                "inbound lookup is not Found");
            var receipt = lookup?.Receipt;
            Require(receipt != null && receipt.Status == WireReceiptStatus.Applied,
                "inbound lookup receipt is not Applied");
            Require(receipt != null && receipt.CanonicalPayloadSha256 == expected["canonicalDigest"],
                "inbound lookup receipt digest mismatch");
            Require(receipt != null &&
                    receipt.CommandVersion.ToString(CultureInfo.InvariantCulture) == expected["commandVersion"] &&
                    receipt.EventSeq.ToString(CultureInfo.InvariantCulture) == expected["eventSeq"] &&
                    receipt.EvaluatedStateVersion.ToString(CultureInfo.InvariantCulture) == expected["evaluatedStateVersion"],
                "inbound lookup receipt versions mismatch");
            Require(!frames.Any(f => f.Direction == "OUT" && f.Type == "Command" &&
                    f.RequestId == oldRequest), "the original command was resent");
            Require(!frames.Any(f => f.Direction == "IN" && f.Type == "Receipt" &&
                    f.RequestId == oldRequest), "the recovered session consumed an unverified natural receipt");

            string preparePost = AuthorityPath(root, result, "authority/prepare-post.save");
            string preparePostCheat = AuthorityPath(root, result, "authority/prepare-post-cheat.bin");
            string preparePrePath = AuthorityPath(root, result, "authority/prepare-pre.save");
            string preparePreCheat = AuthorityPath(root, result, "authority/prepare-pre-cheat.bin");
            string baseline = AuthorityPath(root, result, "authority/recover-baseline.save");
            string baselineCheat = AuthorityPath(root, result, "authority/recover-baseline-cheat.bin");
            string after = AuthorityPath(root, result, "authority/recover-after.save");
            string afterCheat = AuthorityPath(root, result, "authority/recover-after-cheat.bin");
            string repeat = AuthorityPath(root, result, "authority/recover-repeat.save");
            string repeatCheat = AuthorityPath(root, result, "authority/recover-repeat-cheat.bin");
            LocalTableRecoveryFacts post = ReadFacts(preparePost, preparePostCheat);
            LocalTableRecoveryFacts pre = ReadFacts(preparePrePath, preparePreCheat);
            LocalTableRecoveryFacts baselineFacts = ReadFacts(baseline, baselineCheat);
            LocalTableRecoveryFacts afterFacts = ReadFacts(after, afterCheat);
            LocalTableRecoveryFacts repeatFacts = ReadFacts(repeat, repeatCheat);

            RequireSameFacts(post, baselineFacts, "prepare-post vs recover-baseline");
            RequireSameFacts(baselineFacts, afterFacts, "recover-baseline vs recover-after");
            RequireSameFacts(afterFacts, repeatFacts, "recover-after vs recover-repeat");
            Require(pre.Ledger.EntryCount == post.Ledger.EntryCount && pre.Ledger.Points == post.Ledger.Points &&
                    pre.Ledger.EntriesSha256 == post.Ledger.EntriesSha256,
                "Discard changed the ledger");
            Require(post.Physical.TileCount == 136 && post.Physical.UniqueCount == 136,
                "post-Discard physical map is not 136/136");
            Require(afterFacts.Physical.TileCount == 136 && afterFacts.Physical.UniqueCount == 136,
                "recovered physical map is not 136/136");

            LocalTableRecoveryRequestFact? recoveredReceipt = afterFacts.Requests.FirstOrDefault(r =>
                r.RequestId == oldRequest && r.ScopeId == oldScope && r.AccountId == expected["accountId"]);
            Require(recoveredReceipt != null && recoveredReceipt.Status == "APPLIED",
                "raw save does not contain the original Applied receipt");
            Require(recoveredReceipt != null && recoveredReceipt.PayloadSha256 == expected["payloadSha256"],
                "raw save receipt payload SHA mismatch");
            Require(recoveredReceipt != null && recoveredReceipt.CommandVersion == expected["commandVersion"] &&
                    recoveredReceipt.EventSeq == expected["eventSeq"],
                "raw save receipt command/event mismatch");

            JsonElement state = Property(result, "state");
            Require(Text(state, "stateVersion") == afterFacts.StateVersion, "stateVersion claim mismatch");
            Require(Text(state, "eventSeq") == afterFacts.EventSeq, "eventSeq claim mismatch");
            Require(Text(state, "ledgerSha256") == afterFacts.Ledger.EntriesSha256, "ledger claim mismatch");
            Require(Int(state, "ledgerEntries") == afterFacts.Ledger.EntryCount, "ledger entry claim mismatch");
            Require(Long(state, "ledgerPoints") == afterFacts.Ledger.Points, "ledger points claim mismatch");
            Require(Text(state, "physicalSha256") == afterFacts.Physical.TilesSha256, "physical claim mismatch");
            Require(Int(state, "physicalCount") == 136 && Int(state, "physicalUnique") == 136,
                "physical count claim mismatch");
            Require(Text(state, "stateVersion") == expected["commandVersion"], "stateVersion is not commandVersion");
            Require(Text(state, "eventSeq") == expected["eventSeq"], "eventSeq mismatch");

            JsonElement preparePreClaims = Property(prepare, "pre");
            JsonElement preparePostElement = Property(prepare, "post");
            Require(Int(state, "handCount") == Int(preparePostElement, "handCount") &&
                    Int(state, "handCount") == Int(preparePreClaims, "handCount") - 1,
                "hand count effect is not exactly one");
            Require(Int(state, "riverCount") == Int(preparePostElement, "riverCount") &&
                    Int(state, "riverCount") == Int(preparePreClaims, "riverCount") + 1,
                "river count effect is not exactly one");
            Require(Bool(state, "tileInRiver"), "original tile is not in the river");
            Require(Int(state, "discardEvents") == 1, "not exactly one TileDiscarded event");
            Require(Int(state, "oldRequestCommands") == 0, "result claims an old command resend");
            Require(Int(state, "oldScopeQueries") == 1, "result claims the wrong query count");
            Require(Int(result, "oldOutboxUnresolved") == 1, "old outbox was not unresolved before reconciliation");

            string prepareOutbox = AuthorityPath(root, result, "authority/prepare-outbox.dat");
            string baselineOutbox = AuthorityPath(root, result, "authority/recover-baseline-outbox.dat");
            string afterOutbox = AuthorityPath(root, result, "authority/recover-after-outbox.dat");
            List<OutboxEntry> preEntries = ReadOutbox(prepareOutbox);
            List<OutboxEntry> baselineEntries = ReadOutbox(baselineOutbox);
            List<OutboxEntry> afterEntries = ReadOutbox(afterOutbox);
            Require(preEntries.Count == 1 && baselineEntries.Count == 1 && afterEntries.Count == 0,
                "outbox unresolved counts are not 1/1/0");
            Require(preEntries[0].RequestId.Value == oldRequest && preEntries[0].ScopeId.Value == oldScope &&
                    preEntries[0].DigestSha256 == expected["canonicalDigest"],
                "original outbox identity/digest mismatch");
            Require(baselineEntries[0].DigestSha256 == expected["canonicalDigest"],
                "baseline outbox digest mismatch");

            Require(Bool(Property(result, "checks"), "zeroDeltaAfterReconcile"),
                "result does not claim zero delta");
            Require(Bool(Property(result, "checks"), "repeatQuerySentNothing"),
                "result does not claim repeat query silence");
            Check("recoveredEffects");
        }

        private static void ValidateFresh(
            string root,
            JsonElement result,
            JsonElement input,
            Dictionary<string, string> expected,
            List<Frame> frames)
        {
            Require(Text(result, "actualOutcome") == "FRESH_START", "actual outcome is not FRESH_START");
            ValidateWireIdentity(root, result, expected, frames);
            Require(!frames.Any(f => f.RequestId == expected["requestId"] || f.ScopeId == expected["scopeId"]),
                "old scope/request appeared in FRESH_START traffic");
            Require(!frames.Any(f => f.Type == "Command" || f.Type == "ReceiptQuery"),
                "FRESH_START sent a command or receipt query");
            JsonElement prepare = ReadJson(Path.Combine(root, "prepare.json"));
            Require(Text(prepare, "api") == "SUBMIT_ACTION", "prepare API is not SUBMIT_ACTION");
            LocalTableRecoveryFacts preparePost = ReadFacts(
                AuthorityPath(root, result, "authority/prepare-post.save"),
                AuthorityPath(root, result, "authority/prepare-post-cheat.bin"));
            LocalTableRecoveryFacts preparePre = ReadFacts(
                AuthorityPath(root, result, "authority/prepare-pre.save"),
                AuthorityPath(root, result, "authority/prepare-pre-cheat.bin"));
            Require(preparePre.Ledger.EntryCount == preparePost.Ledger.EntryCount &&
                    preparePre.Ledger.Points == preparePost.Ledger.Points &&
                    preparePre.Ledger.EntriesSha256 == preparePost.Ledger.EntriesSha256,
                "prepare Discard changed the ledger");
            Require(preparePost.Physical.TileCount == 136 && preparePost.Physical.UniqueCount == 136,
                "prepare physical map is not 136/136");
            string prepareOutbox = AuthorityPath(root, result, "authority/prepare-outbox.dat");
            string baseline = AuthorityPath(root, result, "authority/recover-baseline.save");
            string baselineCheat = AuthorityPath(root, result, "authority/recover-baseline-cheat.bin");
            List<OutboxEntry> entries = ReadOutbox(prepareOutbox);
            Require(entries.Count == 1 && entries[0].DigestSha256 == expected["canonicalDigest"],
                "old outbox was not retained for FRESH_START");
            LocalTableRecoveryFacts facts = ReadFacts(baseline, baselineCheat);
            Require(facts.StateVersion == "0" && facts.EventSeq == "0" &&
                    facts.Requests.Count == 0, "fresh room is not stateVersion/eventSeq 0 with no receipts");
            JsonElement state = Property(result, "state");
            Require(Text(state, "stateVersion") == "0" && !Bool(state, "oldReceiptPresent") &&
                    Int(state, "oldOutboxUnresolved") == 1, "fresh result claims mismatch");
            Check("freshBoundary");
        }

        private static void ValidateRejected(
            string root,
            JsonElement result,
            JsonElement input,
            Dictionary<string, string> expected,
            List<Frame> frames)
        {
            Require(Text(result, "actualOutcome") == "REJECTED", "actual outcome is not REJECTED");
            Require(frames.Count == 0, "REJECTED sent or received a wire frame");
            Require(!Bool(Property(result, "state"), "ready"), "REJECTED result claims Ready");
            string primary = Path.Combine(root, "authority/rejected-primary.save");
            string backup = Path.Combine(root, "authority/rejected-backup.save");
            string beforePrimary = Path.Combine(root, "boundary/primary-after.save");
            string beforeBackup = Path.Combine(root, "boundary/backup-after.save");
            Require(File.Exists(primary) && File.Exists(backup), "raw rejected save copies are missing");
            Require(File.Exists(beforePrimary) && File.Exists(beforeBackup),
                "post-fault rejection boundary is missing");
            Require(Sha256File(primary) == Sha256File(beforePrimary),
                "rejected primary bytes changed after the fault boundary");
            Require(Sha256File(backup) == Sha256File(beforeBackup),
                "rejected backup bytes changed after the fault boundary");
            List<OutboxEntry> entries = ReadOutbox(AuthorityPath(root, result, "authority/rejected-outbox.dat"));
            Require(entries.Count == 1 && entries[0].DigestSha256 == expected["canonicalDigest"],
                "rejected old outbox was not retained");
            Check("rejectedBoundary");
        }

        private static void ValidateWireIdentity(
            string root,
            JsonElement result,
            Dictionary<string, string> expected,
            List<Frame> frames)
        {
            JsonElement binding = Property(result, "binding");
            string session = Text(binding, "sessionId");
            string room = Text(binding, "roomId");
            int seat = Int(binding, "seat");
            string role = Text(binding, "role");
            string bindingScope = Text(binding, "scopeId");
            Require(!string.IsNullOrWhiteSpace(session), "wire binding session is empty");
            Require(room == "local-main", "wire binding room is not local-main");
            Require(seat == 0 && role == "Player", "wire binding role/seat is not player seat 0");

            List<Frame> welcomes = frames.Where(f => f.Direction == "IN" && f.Envelope.Welcome != null).ToList();
            Require(welcomes.Count == 1, "expected exactly one inbound Welcome");
            if (welcomes.Count == 1)
            {
                var welcome = welcomes[0].Envelope.Welcome;
                Require(welcome.SessionId == session && welcome.RoomId == room &&
                        welcome.SeatId == seat && welcome.Role.ToString() == role &&
                        welcome.BindingEpoch == UInt(binding, "bindingEpoch"),
                    "Welcome identity differs from the result binding");
                Require(welcome.Scope != null &&
                        welcome.Scope.ScopeId == bindingScope &&
                        welcome.Scope.AccountId == expected["accountId"] &&
                        welcome.Scope.RoomId == room &&
                        welcome.Scope.Role.ToString() == role,
                    "Welcome scope differs from the result binding/account");
            }

            List<Frame> syncs = frames.Where(f => f.Direction == "IN" &&
                f.Envelope.SyncResponse != null && f.Envelope.SyncResponse.Player != null).ToList();
            Require(syncs.Count == 1, "expected exactly one inbound player SyncResponse");
            foreach (Frame frame in frames)
            {
                WireEnvelope envelope = frame.Envelope;
                if (!string.IsNullOrEmpty(envelope.SessionId))
                {
                    Require(envelope.SessionId == session, "frame session does not match the binding");
                }

                if (!string.IsNullOrEmpty(envelope.RoomId))
                {
                    Require(envelope.RoomId == room, "frame room does not match the binding");
                }

                if (envelope.Scope != null)
                {
                    Require(envelope.Scope.AccountId == expected["accountId"] &&
                            envelope.Scope.RoomId == room &&
                            envelope.Scope.Role.ToString() == role,
                        "frame scope account/role/room differs from the binding");
                    Require(envelope.Scope.ScopeId == bindingScope ||
                            envelope.Scope.ScopeId == expected["scopeId"],
                        "frame scope is neither the new binding nor the original scope");
                }
            }

            Check("wireIdentity");
        }

        private static void ValidateFaultBoundary(
            string root,
            Dictionary<string, string> expected,
            JsonElement input)
        {
            string beforePrimary = Path.Combine(root, "boundary/primary-before.save");
            string beforeBackup = Path.Combine(root, "boundary/backup-before.save");
            string afterPrimary = Path.Combine(root, "boundary/primary-after.save");
            string afterBackup = Path.Combine(root, "boundary/backup-after.save");
            string cheat = AuthorityPath(root, ReadJson(Path.Combine(root, "result.json")),
                "authority/prepare-post-cheat.bin");
            Require(File.Exists(beforePrimary) && File.Exists(beforeBackup),
                "pre-fault primary/backup boundary is missing");
            Require(TryFacts(beforePrimary, cheat) != null && TryFacts(beforeBackup, cheat) != null,
                "pre-fault primary/backup is not a valid save");

            string fault = Text(input, "fault");
            bool primaryValid = TryFacts(afterPrimary, cheat) != null;
            bool backupValid = TryFacts(afterBackup, cheat) != null;
            bool primaryExists = File.Exists(afterPrimary);
            bool backupExists = File.Exists(afterBackup);
            byte[] beforePrimaryBytes = File.ReadAllBytes(beforePrimary);
            byte[] beforeBackupBytes = File.ReadAllBytes(beforeBackup);
            switch (fault)
            {
                case "None":
                    Require(primaryExists && backupExists && primaryValid && backupValid,
                        "None fault does not have two valid post-fault saves");
                    Require(File.ReadAllBytes(afterPrimary).AsSpan().SequenceEqual(beforePrimaryBytes) &&
                            File.ReadAllBytes(afterBackup).AsSpan().SequenceEqual(beforeBackupBytes),
                        "None fault changed a save byte");
                    break;
                case "TruncatePrimary":
                    Require(primaryExists && !primaryValid && backupExists && backupValid,
                        "TruncatePrimary boundary is not invalid primary + valid backup");
                    Require(IsPrefix(beforePrimaryBytes, File.ReadAllBytes(afterPrimary), 2),
                        "TruncatePrimary did not retain exactly the half-length prefix");
                    Require(File.ReadAllBytes(afterBackup).AsSpan().SequenceEqual(beforeBackupBytes),
                        "TruncatePrimary changed the backup bytes");
                    break;
                case "BadChecksumPrimary":
                    Require(primaryExists && !primaryValid && backupExists && backupValid,
                        "BadChecksumPrimary boundary is not invalid primary + valid backup");
                    byte[] checksumBytes = File.ReadAllBytes(afterPrimary);
                    Require(checksumBytes.Length == beforePrimaryBytes.Length &&
                            DiffersOnlyAt(beforePrimaryBytes, checksumBytes, 44) &&
                            checksumBytes[44] == (byte)(beforePrimaryBytes[44] ^ 0x5A),
                        "BadChecksumPrimary did not preserve length/header and XOR byte 44");
                    Require(File.ReadAllBytes(afterBackup).AsSpan().SequenceEqual(beforeBackupBytes),
                        "BadChecksumPrimary changed the backup bytes");
                    break;
                case "MissingPrimaryWithBackup":
                    Require(!primaryExists && backupExists && backupValid,
                        "MissingPrimaryWithBackup boundary is not absent primary + valid backup");
                    Require(File.ReadAllBytes(afterBackup).AsSpan().SequenceEqual(beforeBackupBytes),
                        "MissingPrimaryWithBackup changed the backup bytes");
                    break;
                case "BothMissing":
                    Require(!primaryExists && !backupExists,
                        "BothMissing boundary still has a save");
                    break;
                case "BothBad":
                    Require(primaryExists && backupExists && !primaryValid && !backupValid,
                        "BothBad boundary is not two invalid saves");
                    Require(IsPrefix(beforePrimaryBytes, File.ReadAllBytes(afterPrimary), 3),
                        "BothBad primary is not the one-third prefix");
                    byte[] badBackup = File.ReadAllBytes(afterBackup);
                    Require(badBackup.Length == beforeBackupBytes.Length &&
                            DiffersOnlyAt(beforeBackupBytes, badBackup, 44) &&
                            badBackup[44] == (byte)(beforeBackupBytes[44] ^ 0xA5),
                        "BothBad backup did not preserve length and XOR byte 44");
                    break;
                case "IncompatiblePrimary":
                    Require(primaryExists && !primaryValid && backupExists && backupValid,
                        "IncompatiblePrimary boundary is not invalid primary + valid backup");
                    byte[] incompatible = File.ReadAllBytes(afterPrimary);
                    Require(incompatible.Length == beforePrimaryBytes.Length &&
                            DiffersOnlyAt(beforePrimaryBytes, incompatible, 4, 5, 6, 7) &&
                            incompatible[4] == 2 && incompatible[5] == 0 &&
                            incompatible[6] == 0 && incompatible[7] == 0,
                        "IncompatiblePrimary did not change only version bytes 4..7 to 2/0/0/0");
                    Require(File.ReadAllBytes(afterBackup).AsSpan().SequenceEqual(beforeBackupBytes),
                        "IncompatiblePrimary changed the backup bytes");
                    break;
                default:
                    Require(false, "unknown fault boundary " + fault);
                    break;
            }

            string launchPath = Path.Combine(root, "prepare-launch.json");
            Require(File.Exists(launchPath), "prepare launch observation is missing");
            JsonElement launch = ReadJson(launchPath);
            Require(launch.TryGetProperty("nativeExitCode", out _) &&
                    launch.TryGetProperty("timedOut", out _) &&
                    Text(launch, "probeMode") == "Prepare",
                "prepare launch has no complete process/native observation");
            if (Bool(input, "force"))
            {
                Require(Text(launch, "caseId") == "prepare-force" &&
                        Int(launch, "nativeExitCode") != 0 && !Bool(launch, "timedOut"),
                    "Force prepare did not record a non-zero/non-timeout process exit");
                string log = Path.Combine(root, "prepare-player.log");
                Require(File.Exists(log) &&
                        File.ReadAllText(log).Contains("FORCE_KILLED", StringComparison.Ordinal),
                    "Force prepare log has no FORCE_KILLED evidence");
            }
            else
            {
                Require(Text(launch, "caseId") == "prepare" &&
                        Int(launch, "nativeExitCode") == 0 && !Bool(launch, "timedOut"),
                    "normal prepare did not exit 0");
            }

            Check("faultBoundary");
        }

        private static bool IsPrefix(byte[] source, byte[] candidate, int divisor)
        {
            int expected = Math.Max(1, source.Length / divisor);
            if (candidate.Length != expected)
            {
                return false;
            }

            return candidate.AsSpan().SequenceEqual(source.AsSpan(0, expected));
        }

        private static bool DiffersOnlyAt(byte[] source, byte[] candidate, params int[] indexes)
        {
            if (source.Length != candidate.Length)
            {
                return false;
            }

            var allowed = new HashSet<int>(indexes);
            for (int i = 0; i < source.Length; i++)
            {
                if (source[i] != candidate[i] && !allowed.Contains(i))
                {
                    return false;
                }
            }

            return true;
        }

        private static LocalTableRecoveryFacts? TryFacts(string savePath, string cheatPath)
        {
            if (!File.Exists(savePath) || !File.Exists(cheatPath))
            {
                return null;
            }

            try
            {
                return LocalTableRecoveryFactsReader.ReadPersistedFacts(
                    File.ReadAllBytes(savePath),
                    File.ReadAllBytes(cheatPath));
            }
            catch
            {
                return null;
            }
        }

        private static void ValidatePreparePost(JsonElement prepare, Dictionary<string, string> expected)
        {
            JsonElement post = Property(prepare, "post");
            JsonElement pre = Property(prepare, "pre");
            Require(Text(prepare, "canonicalDigest") == expected["canonicalDigest"],
                "prepare canonical digest mismatch");
            Require(Int(post, "discardEvents") == 1, "prepare did not observe one TileDiscarded");
            Require(Int(post, "handCount") == Int(pre, "handCount") - 1, "prepare hand effect mismatch");
            Require(Int(post, "riverCount") == Int(pre, "riverCount") + 1, "prepare river effect mismatch");
            Require(Bool(post, "tileInRiver"), "prepare original tile is not in the river");
            Require(Int(post, "physicalCount") == 136 && Int(post, "physicalUnique") == 136,
                "prepare physical map is not 136/136");
        }

        private static void ValidateSnapshotFacts(
            string root,
            JsonElement prepare,
            JsonElement result,
            Dictionary<string, string> expected)
        {
            List<SnapshotFact> prepareSnaps = ReadSnapshots(Path.Combine(root, "prepare-frames"));
            Require(prepareSnaps.Count >= 2, "prepare did not retain pre/post player snapshots");
            SnapshotFact? pre = prepareSnaps.OrderBy(s => s.StateVersion).FirstOrDefault();
            SnapshotFact? post = prepareSnaps.OrderByDescending(s => s.StateVersion).FirstOrDefault();
            Require(pre != null && post != null, "prepare snapshot pair is missing");
            if (pre == null || post == null)
            {
                return;
            }

            uint tile = uint.Parse(expected["tileId"], CultureInfo.InvariantCulture);
            Require(post.StateVersion.ToString(CultureInfo.InvariantCulture) == expected["commandVersion"] &&
                    post.HandCount == pre.HandCount - 1 &&
                    post.RiverCount == pre.RiverCount + 1 &&
                    post.TileInRiver(tile) && post.DiscardEvents == 1,
                "raw prepare snapshots do not prove the single Discard effect");
            JsonElement preClaims = Property(prepare, "pre");
            JsonElement postClaims = Property(prepare, "post");
            Require(Int(preClaims, "handCount") == pre.HandCount &&
                    Int(preClaims, "riverCount") == pre.RiverCount &&
                    Int(postClaims, "handCount") == post.HandCount &&
                    Int(postClaims, "riverCount") == post.RiverCount &&
                    Int(postClaims, "discardEvents") == post.DiscardEvents,
                "prepare hand/river claims differ from raw snapshots");

            List<SnapshotFact> recoverSnaps = ReadSnapshots(Path.Combine(root, "frames"));
            SnapshotFact? recovered = recoverSnaps.OrderByDescending(s => s.StateVersion).FirstOrDefault();
            Require(recovered != null, "recover did not retain a player snapshot");
            if (recovered == null)
            {
                return;
            }

            JsonElement state = Property(result, "state");
            Require(recovered.StateVersion.ToString(CultureInfo.InvariantCulture) == expected["commandVersion"] &&
                    recovered.EventSeq.ToString(CultureInfo.InvariantCulture) == expected["eventSeq"] &&
                    recovered.HandCount == pre.HandCount - 1 &&
                    recovered.RiverCount == pre.RiverCount + 1 &&
                    recovered.TileInRiver(tile) &&
                    Int(state, "handCount") == recovered.HandCount &&
                    Int(state, "riverCount") == recovered.RiverCount &&
                    Bool(state, "tileInRiver"),
                "recovered hand/river/tile claims differ from the raw player snapshot");
            Check("rawSnapshots");
        }

        private static List<SnapshotFact> ReadSnapshots(string directory)
        {
            var snapshots = new List<SnapshotFact>();
            if (!Directory.Exists(directory))
            {
                return snapshots;
            }

            foreach (string path in Directory.GetFiles(directory, "*.bin").OrderBy(p => p, StringComparer.Ordinal))
            {
                try
                {
                    WireEnvelope envelope = WireEnvelope.Parser.ParseFrom(File.ReadAllBytes(path));
                    if (envelope.SyncResponse != null && envelope.SyncResponse.Player != null)
                    {
                        snapshots.Add(new SnapshotFact(envelope.SyncResponse.Player, 0));
                    }
                    else if (envelope.PlayerUpdate != null && envelope.PlayerUpdate.Snapshot != null)
                    {
                        snapshots.Add(new SnapshotFact(
                            envelope.PlayerUpdate.Snapshot,
                            envelope.PlayerUpdate.VisibleEvents.Count(e =>
                                e.EventCase == Mahjong.Contracts.Wire.EventEnvelope.EventOneofCase.TileDiscarded &&
                                e.TileDiscarded.Player == 0)));
                    }
                    else if (envelope.PlayerSnapshot != null)
                    {
                        snapshots.Add(new SnapshotFact(envelope.PlayerSnapshot, 0));
                    }
                }
                catch
                {
                    // Non-snapshot frames are decoded and checked elsewhere.
                }
            }

            return snapshots;
        }

        private static void RequireSameFacts(LocalTableRecoveryFacts left, LocalTableRecoveryFacts right, string name)
        {
            Require(left.StateVersion == right.StateVersion &&
                    left.EventSeq == right.EventSeq &&
                    left.StateHash == right.StateHash &&
                    left.Ledger.EntryCount == right.Ledger.EntryCount &&
                    left.Ledger.Points == right.Ledger.Points &&
                    left.Ledger.EntriesSha256 == right.Ledger.EntriesSha256 &&
                    left.Physical.TilesSha256 == right.Physical.TilesSha256,
                name + " raw facts differ");
        }

        private static LocalTableRecoveryFacts ReadFacts(string savePath, string cheatPath)
        {
            Require(File.Exists(savePath), "raw save is missing: " + savePath);
            Require(File.Exists(cheatPath), "raw cheat aggregate is missing: " + cheatPath);
            return LocalTableRecoveryFactsReader.ReadPersistedFacts(
                File.ReadAllBytes(savePath),
                File.ReadAllBytes(cheatPath));
        }

        private static List<OutboxEntry> ReadOutbox(string path)
        {
            Require(File.Exists(path), "raw outbox is missing: " + path);
            string temp = Path.Combine(Path.GetTempPath(), "c04-outbox-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                File.Copy(path, Path.Combine(temp, "outbox.dat"));
                var store = new FileOutboxStore(temp);
                return store.LoadUnresolvedAsync(System.Threading.CancellationToken.None)
                    .GetAwaiter().GetResult().ToList();
            }
            finally
            {
                try { Directory.Delete(temp, true); } catch { }
            }
        }

        private static string AuthorityPath(string root, JsonElement result, string relative)
        {
            JsonElement files = Property(Property(result, "authority"), "files");
            foreach (JsonElement item in files.EnumerateArray())
            {
                if (Text(item, "path") == relative)
                {
                    string path = Path.Combine(root, relative);
                    Require(File.Exists(path), "authority file is missing: " + relative);
                    Require(Sha256File(path) == Text(item, "sha256"), "authority SHA mismatch: " + relative);
                    Require(new FileInfo(path).Length == Long(item, "bytes"),
                        "authority byte length mismatch: " + relative);
                    return path;
                }
            }

            throw new InvalidDataException("authority map lacks " + relative);
        }

        private static JsonElement ReadJson(string path)
        {
            return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        }

        private static Dictionary<string, string> ReadExpected(string path)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in File.ReadAllLines(path))
            {
                int index = line.IndexOf('=');
                if (index > 0)
                {
                    values[line.Substring(0, index)] = line.Substring(index + 1);
                }
            }

            return values;
        }

        private static Dictionary<string, string> Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i + 1 < args.Length; i += 2)
            {
                values[args[i]] = args[i + 1];
            }

            return values;
        }

        private static string Required(Dictionary<string, string> values, string key)
        {
            return values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new ArgumentException("missing " + key);
        }

        private static JsonElement Property(JsonElement element, string name)
        {
            return element.TryGetProperty(name, out JsonElement value)
                ? value
                : throw new InvalidDataException("missing JSON property " + name);
        }

        private static string Text(JsonElement element, string name)
        {
            JsonElement value = Property(element, name);
            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
        }

        private static int Int(JsonElement element, string name)
        {
            JsonElement value = Property(element, name);
            return value.TryGetInt32(out int number) ? number : 0;
        }

        private static long Long(JsonElement element, string name)
        {
            JsonElement value = Property(element, name);
            return value.TryGetInt64(out long number) ? number : 0;
        }

        private static ulong UInt(JsonElement element, string name)
        {
            JsonElement value = Property(element, name);
            return value.TryGetUInt64(out ulong number) ? number : 0;
        }

        private static bool Bool(JsonElement element, string name)
        {
            JsonElement value = Property(element, name);
            return value.ValueKind == JsonValueKind.True;
        }

        private static string RequestIdOf(WireEnvelope envelope)
        {
            return envelope.BodyCase == WireEnvelope.BodyOneofCase.ReceiptQuery && envelope.ReceiptQuery != null
                ? envelope.ReceiptQuery.RequestId
                : envelope.RequestId ?? string.Empty;
        }

        private static string ScopeIdOf(WireEnvelope envelope)
        {
            return envelope.BodyCase == WireEnvelope.BodyOneofCase.ReceiptQuery && envelope.ReceiptQuery != null
                ? envelope.ReceiptQuery.RequestScopeId
                : envelope.Scope?.ScopeId ?? string.Empty;
        }

        private static string StatusOf(WireEnvelope envelope)
        {
            if (envelope.Receipt != null)
            {
                return envelope.Receipt.Status.ToString();
            }

            return envelope.ReceiptLookup != null ? envelope.ReceiptLookup.Status.ToString() : string.Empty;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                Findings.Add(message);
            }
        }

        private static void Check(string name)
        {
            Checks.Add(name);
        }

        private static string Sha256File(string path)
        {
            return Sha256(File.ReadAllBytes(path));
        }

        private static string Sha256(byte[] bytes)
        {
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(bytes);
            var builder = new System.Text.StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++)
            {
                builder.Append(hash[i].ToString("X2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        private sealed class SnapshotFact
        {
            private readonly Mahjong.Contracts.Wire.PlayerSnapshot _snapshot;

            public SnapshotFact(Mahjong.Contracts.Wire.PlayerSnapshot snapshot, int discardEvents)
            {
                _snapshot = snapshot;
                DiscardEvents = discardEvents;
            }

            public ulong StateVersion => _snapshot.StateVersion;
            public ulong EventSeq => _snapshot.EventSeq;
            public int HandCount => _snapshot.PrivateView.ClosedTiles.Count +
                (_snapshot.PrivateView.HasDrawnTileId ? 1 : 0);
            public int RiverCount => _snapshot.PublicView.Board.Players[0].Discards.Count;
            public int DiscardEvents { get; }

            public bool TileInRiver(uint tile)
            {
                return _snapshot.PublicView.Board.Players[0].Discards.Any(d => d.TileId == tile);
            }
        }

        private sealed class Frame
        {
            public Frame(WireEnvelope envelope, string direction, string type, string requestId,
                string scopeId, string status, string correlationId)
            {
                Envelope = envelope;
                Direction = direction;
                Type = type;
                RequestId = requestId;
                ScopeId = scopeId;
                Status = status;
                CorrelationId = correlationId;
            }

            public WireEnvelope Envelope { get; }
            public string Direction { get; }
            public string Type { get; }
            public string RequestId { get; }
            public string ScopeId { get; }
            public string Status { get; }
            public string CorrelationId { get; }
        }
    }
}
