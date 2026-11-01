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
                    Require(!result.TryGetProperty("binding", out _),
                        "REJECTED result must not publish a Ready/session binding");
                    Check("rejectedNoBinding");
                }
                ValidateAuthorityFiles(root, result);

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
            JsonElement prepare = ReadJson(Path.Combine(root, "prepare.json"));
            Require(Text(prepare, "api") == "SUBMIT_ACTION", "prepare API is not SUBMIT_ACTION");
            ValidatePreparePost(prepare, expected);

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
            Require(File.Exists(primary) && File.Exists(backup), "raw rejected save copies are missing");
            Require(Sha256File(primary) == Text(input, "primaryBeforeFaultSha256"),
                "rejected primary bytes changed");
            Require(Sha256File(backup) == Text(input, "backupBeforeFaultSha256"),
                "rejected backup bytes changed");
            List<OutboxEntry> entries = ReadOutbox(AuthorityPath(root, result, "authority/rejected-outbox.dat"));
            Require(entries.Count == 1 && entries[0].DigestSha256 == expected["canonicalDigest"],
                "rejected old outbox was not retained");
            Check("rejectedBoundary");
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
