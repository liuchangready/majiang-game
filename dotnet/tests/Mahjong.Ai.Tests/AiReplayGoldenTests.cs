using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.Ai.Tests
{
    /// <summary>
    /// Single read-only loader shared by every ai-v1 golden test. The optional
    /// MAHJONG_AI_GOLDEN override exists so the probe script can point the same compiled tests
    /// at an isolated TEMP copy instead of mutating the real fixture or the build output.
    /// A set-but-missing path is a hard failure, never a silent fallback.
    /// </summary>
    internal static class AiGoldenFixture
    {
        public const string ExpectedSchema = "v5-ai-golden-v1";
        public const string OverrideVariable = "MAHJONG_AI_GOLDEN";

        public static string ResolvePath()
        {
            string? overridden = Environment.GetEnvironmentVariable(OverrideVariable);
            if (!string.IsNullOrWhiteSpace(overridden))
            {
                string fullPath = Path.GetFullPath(overridden!);
                Assert.True(
                    File.Exists(fullPath),
                    OverrideVariable + " points to a missing fixture: " + fullPath);
                return fullPath;
            }

            return Path.Combine(AppContext.BaseDirectory, "ai-v1.json");
        }

        public static JsonDocument Load()
        {
            string path = ResolvePath();
            Assert.True(File.Exists(path), "Missing golden fixture: " + path);
            return JsonDocument.Parse(File.ReadAllText(path));
        }
    }

    /// <summary>
    /// Reads tests/v5-golden/ai-v1.json, which was produced by the V4 test-only exporter
    /// (engine/src/test/kotlin/com/majiang/state/V5AiGoldenExporterTest.kt) from actual
    /// TableAuthority.submitAiChoice calls.
    ///
    /// The fixture records the V4 receipt/reason and an explicit per-case V5 semantic mapping.
    /// This test does not re-run a heuristic and does not derive an expected value from C#:
    /// it builds the frozen observation, replays the recorded CommandIntent, and admits it
    /// against the mapped authority snapshot using only public Mahjong.Ai / Domain ports.
    ///
    /// It deliberately does not claim cross-language canonical-byte or SHA-256 equality with
    /// V4; no such V4 hash is present in the fixture.
    /// </summary>
    public class AiReplayGoldenTests
    {
        private const string ExpectedSchema = AiGoldenFixture.ExpectedSchema;
        private const string BaselineCommit = "205506f07f6a1ccfef6b32cf97390431e3349f8d";

        [Fact]
        public void GoldenFixtureHasProvenanceAndTheExpectedV4Cases()
        {
            using JsonDocument document = LoadFixture();
            JsonElement root = document.RootElement;

            Assert.Equal(ExpectedSchema, Text(root, "schema"));
            Assert.Equal(1L, Int64(root, "generatorVersion"));
            Assert.Equal(1L, Int64(root, "rulesetVersion"));
            Assert.Equal(BaselineCommit, Text(root, "v4Commit"));
            Assert.Equal(40, Text(root, "generatedAtHead").Length);

            // The exporter deliberately distinguishes V4 production dirt from this task's
            // test-only edits. Only the production half must be empty.
            Assert.False(Bool(root, "v4ProductionSourceDirty"));
            Assert.Equal(0, root.GetProperty("v4ProductionSourceDirtyPaths").GetArrayLength());
            foreach (JsonElement dirtyPath in root.GetProperty("v4TestOnlyTrackedDirtyPaths").EnumerateArray())
            {
                string path = dirtyPath.GetString()!;
                Assert.True(
                    path.StartsWith("engine/src/test/", StringComparison.Ordinal) ||
                    path.StartsWith("tests/v5-golden/", StringComparison.Ordinal) ||
                    path.StartsWith("dotnet/tests/Mahjong.Ai.Tests/", StringComparison.Ordinal) ||
                    path.StartsWith("doc/v5-重构/", StringComparison.Ordinal),
                    "Unexpected test-only dirty path: " + path);
            }

            JsonElement sourceFiles = root.GetProperty("sourceFileSha256");
            Assert.Equal(13, sourceFiles.GetArrayLength());
            foreach (JsonElement source in sourceFiles.EnumerateArray())
            {
                Assert.Equal(64, Text(source, "sha256").Length);
            }

            Assert.Equal("ROUND", Text(root.GetProperty("decisionClock"), "reserveScope"));
            Assert.Equal(1_000L, Int64(root.GetProperty("decisionClock"), "opensAtMillis"));
            Assert.Equal(24_000L, Int64(root.GetProperty("decisionClock"), "deadlineMillis"));
            Assert.False(Bool(root.GetProperty("decisionClock"), "expiredAtDeadlineMinus1"));
            Assert.True(Bool(root.GetProperty("decisionClock"), "expiredAtDeadline"));

            JsonElement recorded = root.GetProperty("recordedIntent");
            Assert.Equal("v4-golden-deterministic", Text(recorded, "algorithmId"));
            Assert.Equal(1L, Int64(recorded, "algorithmVersion"));
            Assert.Equal("L0", Text(recorded, "selectedLegalActionRef"));
            Assert.Equal("opt-0", Text(recorded, "selectedOptionId"));
            LegalActionOption recordedAction = ActionOptionFromJson(recorded.GetProperty("selectedAction"));
            AssertActionFields(recorded.GetProperty("selectedAction"), recordedAction, "recordedIntent");

            JsonElement cases = root.GetProperty("cases");
            Assert.Equal(7, cases.GetArrayLength());
            string[] ids = cases.EnumerateArray().Select(c => Text(c, "id")).ToArray();
            Assert.Equal(
                new[]
                {
                    "AI-002-old-round",
                    "AI-003-old-decision",
                    "AI-004-old-epoch",
                    "AI-005-wrong-seat",
                    "AI-006-candidate-not-legal",
                    "AI-007-exact-deadline",
                    "AI-001-legal-intent-before-deadline"
                },
                ids);
            string[] codes = cases.EnumerateArray()
                .Select(c => Text(c.GetProperty("v5").GetProperty("expected"), "rejectionCode"))
                .ToArray();
            Assert.Equal(
                new[] { "StaleRound", "StaleDecision", "StaleEpoch", "StaleDecision", "IllegalAction", "DeadlineExpired", "None" },
                codes);

            foreach (JsonElement caseElement in cases.EnumerateArray())
            {
                string kind = Text(caseElement, "kind");
                JsonElement mapping = caseElement.GetProperty("v5").GetProperty("authorityMapping");
                Assert.Equal(ExpectedAuthorityMode(kind), Text(mapping, "mode"));
                Assert.False(Bool(mapping, "isFreshV4Authority"));
                Assert.Equal(2, mapping.GetProperty("derivedFrom").GetArrayLength());
            }

            JsonElement oldEpoch = cases.EnumerateArray()
                .Single(c => Text(c, "id") == "AI-004-old-epoch");
            Assert.True(
                Int64(oldEpoch.GetProperty("v4Call"), "controlEpoch") <
                Int64(root.GetProperty("v5Observation"), "controlEpoch"),
                "AI-004 must submit a true older epoch, not a future epoch");
        }

        [Fact]
        public void GoldenCasesReplayTheRecordedIntentAndMatchTheV4SemanticMapping()
        {
            using JsonDocument document = LoadFixture();
            JsonElement root = document.RootElement;
            JsonElement observationElement = root.GetProperty("v5Observation");
            AiObservation observation = BuildObservation(
                observationElement,
                out Dictionary<string, LegalActionOption> actions,
                out List<string> legalRefs);

            Assert.Equal(14, observation.LegalActions.Count);
            Assert.Equal(observation.Seat, observationElement.GetProperty("seat").GetInt32());
            Assert.Equal(observation.RoundId, Text(observationElement, "roundId"));
            Assert.Equal(observation.DecisionId, Text(observationElement, "decisionId"));
            Assert.Equal(observation.ControlEpoch, Int64(observationElement, "controlEpoch"));
            Assert.Equal(observation.StateVersion, Int64(observationElement, "stateVersion"));
            Assert.Equal(observation.DecisionDeadlineMillis, Int64(observationElement, "deadlineMillis"));

            foreach (JsonElement caseElement in root.GetProperty("cases").EnumerateArray())
            {
                string id = Text(caseElement, "id");
                JsonElement v5 = caseElement.GetProperty("v5");
                JsonElement expected = v5.GetProperty("expected");
                JsonElement v4Call = caseElement.GetProperty("v4Call");
                JsonElement v4 = caseElement.GetProperty("v4");
                bool accepted = Bool(expected, "accepted");
                string code = Text(expected, "rejectionCode");

                CommandIntent intent = BuildIntent(root, observation, actions, caseElement);
                Assert.Equal(observation.RoundId, intent.RoundId);
                Assert.Equal(observation.Seat, intent.Seat);
                Assert.Equal(observation.DecisionId, intent.DecisionId);
                Assert.Equal(observation.ControlEpoch, intent.ControlEpoch);
                Assert.Equal(observation.StateVersion, intent.ObservationStateVersion);
                Assert.Equal(64, intent.AuthorityHash.Length);

                AssertV4CallBinding(caseElement, observation, intent);
                AssertV4Evidence(caseElement, intent, accepted);

                Assert.Equal(Int64(v5, "submittedAtMillis"), Int64(v4Call, "nowMillis"));
                Assert.Equal(
                    Bool(v4Call, "nowBeforeDeadline"),
                    Int64(v4Call, "nowMillis") < Int64(v4Call, "deadlineMillis"));

                // Replay injects the record; it never invokes a heuristic.
                AiDecision replay = AiDecisionService.Replay(intent, observation);
                Assert.Same(intent, replay.Intent);
                Assert.Equal(0d, replay.HeuristicScore);
                Assert.Equal(intent.AuthorityHash, replay.AuthorityHash);
                Assert.Same(intent.Action, replay.Intent.Action);

                AiAuthoritySnapshot authority = BuildAuthority(caseElement, observation, actions, legalRefs, intent);
                var service = new AiDecisionService(new ThrowingStrategy());
                long submittedAt = Int64(v5, "submittedAtMillis");
                AiSubmissionResult result = service.Submit(replay, authority, submittedAt);

                Assert.Equal(code, result.Code.ToString());
                Assert.Equal(accepted, result.IsAccepted);
                Assert.Same(intent, result.Intent);
                Assert.Equal(intent.AuthorityHash, result.RecordedIntent.AuthorityHash);

                if (accepted)
                {
                    Assert.NotNull(result.Submission);
                    AiDecisionSubmission submission = result.Submission!;
                    Assert.Equal(intent.DecisionKey, submission.Key);
                    Assert.Equal(intent.ControlEpoch, submission.ControlEpoch);
                    Assert.Same(intent.Action, submission.Action);
                    Assert.Equal(submittedAt, submission.SubmittedAtMillis);
                }
                else
                {
                    Assert.Null(result.Submission);
                }
            }
        }

        [Fact]
        public void AcceptedGoldenIntentFeedsTheDomainDeadlineArbiter()
        {
            using JsonDocument document = LoadFixture();
            JsonElement root = document.RootElement;
            JsonElement observationElement = root.GetProperty("v5Observation");
            AiObservation observation = BuildObservation(
                observationElement,
                out Dictionary<string, LegalActionOption> actions,
                out List<string> legalRefs);
            JsonElement acceptedCase = root.GetProperty("cases").EnumerateArray()
                .Single(c => Bool(c.GetProperty("v5").GetProperty("expected"), "accepted"));
            JsonElement v5 = acceptedCase.GetProperty("v5");
            CommandIntent intent = BuildIntent(root, observation, actions, acceptedCase);
            AiDecision replay = AiDecisionService.Replay(intent, observation);
            AiSubmissionResult result = new AiDecisionService(new ThrowingStrategy())
                .Submit(
                    replay,
                    BuildAuthority(acceptedCase, observation, actions, legalRefs, intent),
                    Int64(v5, "submittedAtMillis"));
            Assert.True(result.IsAccepted, result.Detail);
            Assert.NotNull(result.Submission);

            JsonElement clockElement = root.GetProperty("decisionClock");
            // V4 exports the enum as ROUND/SESSION; the C# Domain port uses Round/Session.
            ReserveScope reserveScope = Text(clockElement, "reserveScope") switch
            {
                "ROUND" => ReserveScope.Round,
                "SESSION" => ReserveScope.Session,
                string other => throw new InvalidOperationException("Unknown reserve scope: " + other)
            };
            var config = new DecisionClockConfig(
                Int64(clockElement, "baseMillis"),
                Int64(clockElement, "reserveMillis"),
                reserveScope);
            var clock = new DecisionClock(config);
            DecisionTiming timing = clock.Open(intent.DecisionKey, Int64(clockElement, "opensAtMillis"));
            Assert.Equal(Int64(clockElement, "deadlineMillis"), timing.DeadlineMillis);

            // V4 initialized seat 1 as Computer, which increments its ControlRegistry epoch to 1.
            var control = new ControlRegistry();
            control.Initialize(new[]
            {
                SeatControllerKind.RemoteHuman,
                SeatControllerKind.Computer,
                SeatControllerKind.RemoteHuman,
                SeatControllerKind.RemoteHuman
            });
            Assert.Equal(intent.ControlEpoch, control.GetEpoch(intent.Seat));
            Assert.True(control.IsAutomated(intent.Seat));

            var pending = new PendingDecision(
                intent.DecisionKey,
                intent.ControlEpoch,
                observation.LegalActions,
                observation.MyDrawnTileId);
            TickResult tick = new DeadlineArbiter(clock, control).ProcessTick(
                new TickInput(
                    observation.RoundId,
                    Int64(v5, "submittedAtMillis"),
                    new[] { pending },
                    new[] { result.Submission! }));

            Assert.Empty(tick.Timeouts);
            Assert.Empty(tick.RejectedAi);
            Assert.Single(tick.AcceptedAi);
            Assert.Same(intent.Action, tick.AcceptedAi[0].Action);
        }

        private static JsonDocument LoadFixture() => AiGoldenFixture.Load();

        internal static AiObservation BuildObservation(
            JsonElement observation,
            out Dictionary<string, LegalActionOption> actions,
            out List<string> legalActionRefs)
        {
            actions = new Dictionary<string, LegalActionOption>(StringComparer.Ordinal);
            legalActionRefs = new List<string>();
            var legalActions = new List<LegalActionOption>();
            foreach (JsonElement action in observation.GetProperty("legalActions").EnumerateArray())
            {
                var option = new LegalActionOption(
                    ParseDefinedEnum<LegalActionKind>(action, "kind"),
                    action.GetProperty("seat").GetInt32(),
                    TileIdOrNull(action, "tileId"),
                    TextOrNull(action, "optionId"));
                string actionRef = Text(action, "ref");
                actions.Add(actionRef, option);
                legalActionRefs.Add(actionRef);
                legalActions.Add(option);
            }

            var hand = new List<TileId>();
            foreach (JsonElement tile in observation.GetProperty("myHandTileIds").EnumerateArray())
            {
                hand.Add(new TileId(tile.GetInt32()));
            }

            var scores = new List<long>();
            foreach (JsonElement score in observation.GetProperty("scores").EnumerateArray())
            {
                scores.Add(score.GetInt64());
            }

            var dora = new List<TileId>();
            foreach (JsonElement tile in observation.GetProperty("visibleDoraIndicators").EnumerateArray())
            {
                dora.Add(new TileId(tile.GetInt32()));
            }

            return new AiObservation(
                Text(observation, "sessionId"),
                new DecisionKey(
                    Text(observation, "roundId"),
                    observation.GetProperty("seat").GetInt32(),
                    Text(observation, "decisionId")),
                Int64(observation, "controlEpoch"),
                Int64(observation, "stateVersion"),
                Int64(observation, "deadlineMillis"),
                ParseDefinedEnum<RoundPhase>(observation, "phase"),
                observation.GetProperty("dealerSeat").GetInt32(),
                observation.GetProperty("roundWind").GetInt32(),
                observation.GetProperty("roundNumber").GetInt32(),
                observation.GetProperty("honbaSticks").GetInt32(),
                observation.GetProperty("riichiSticks").GetInt32(),
                observation.GetProperty("remainingWallCount").GetInt32(),
                hand,
                TileIdOrNull(observation, "myDrawnTileId"),
                Melds(observation.GetProperty("myMelds")),
                Melds(observation.GetProperty("visibleMelds")),
                Discards(observation.GetProperty("visibleDiscards")),
                dora,
                scores,
                legalActions);
        }

        private static IReadOnlyList<AiVisibleMeld> Melds(JsonElement array)
        {
            var melds = new List<AiVisibleMeld>();
            foreach (JsonElement meld in array.EnumerateArray())
            {
                var tiles = new List<TileId>();
                foreach (JsonElement tile in meld.GetProperty("tiles").EnumerateArray())
                {
                    tiles.Add(new TileId(tile.GetInt32()));
                }

                melds.Add(new AiVisibleMeld(meld.GetProperty("seat").GetInt32(), tiles));
            }

            return melds;
        }

        private static IReadOnlyList<AiVisibleDiscard> Discards(JsonElement array)
        {
            var discards = new List<AiVisibleDiscard>();
            foreach (JsonElement discard in array.EnumerateArray())
            {
                discards.Add(new AiVisibleDiscard(
                    discard.GetProperty("seat").GetInt32(),
                    new TileId(discard.GetProperty("tileId").GetInt32()),
                    Bool(discard, "isRiichi"),
                    Bool(discard, "isClaimed")));
            }

            return discards;
        }

        internal static CommandIntent BuildIntent(
            JsonElement root,
            AiObservation observation,
            IReadOnlyDictionary<string, LegalActionOption> actions,
            JsonElement caseElement)
        {
            JsonElement recorded = root.GetProperty("recordedIntent");
            JsonElement v5 = caseElement.GetProperty("v5");
            string id = Text(caseElement, "id");
            string recordedRef = Text(recorded, "selectedLegalActionRef");
            string caseRef = Text(v5, "intentActionRef");

            // This binding is deliberate: a case cannot silently replay a different
            // (even legal) action while claiming to match the recorded V4 intent.
            Assert.Equal(recordedRef, caseRef);
            Assert.True(actions.ContainsKey(caseRef), id + ": unknown intent action ref " + caseRef);
            LegalActionOption action = actions[caseRef];
            Assert.NotNull(action.OptionId);
            Assert.Equal(Text(recorded, "selectedOptionId"), action.OptionId!);
            AssertActionFields(recorded.GetProperty("selectedAction"), action, id + " recordedIntent.selectedAction");

            return new CommandIntent(
                observation,
                action,
                Text(recorded, "algorithmId"),
                checked((int)Int64(recorded, "algorithmVersion")));
        }

        internal static AiAuthoritySnapshot BuildAuthority(
            JsonElement caseElement,
            AiObservation observation,
            IReadOnlyDictionary<string, LegalActionOption> actions,
            IReadOnlyList<string> allLegalRefs,
            CommandIntent intent)
        {
            JsonElement v5 = caseElement.GetProperty("v5");
            JsonElement mapping = v5.GetProperty("authorityMapping");
            JsonElement v4Call = caseElement.GetProperty("v4Call");
            string kind = Text(caseElement, "kind");
            string mode = Text(mapping, "mode");
            Assert.Equal(ExpectedAuthorityMode(kind), mode);
            Assert.False(Bool(mapping, "isFreshV4Authority"));
            Assert.False(string.IsNullOrWhiteSpace(Text(mapping, "basis")));
            Assert.Contains(
                mapping.GetProperty("derivedFrom").EnumerateArray().Select(e => e.GetString()),
                value => value == "v5Observation");
            Assert.Contains(
                mapping.GetProperty("derivedFrom").EnumerateArray().Select(e => e.GetString()),
                value => value == "v4Call");

            int seat = observation.Seat;
            string roundId = observation.RoundId;
            string decisionId = observation.DecisionId;
            long epoch = observation.ControlEpoch;
            List<string> legalRefs;
            switch (mode)
            {
                case "current":
                    legalRefs = allLegalRefs.ToList();
                    break;
                case "stale-round":
                    roundId = Text(v4Call, "roundId");
                    Assert.NotEqual(observation.RoundId, roundId);
                    legalRefs = allLegalRefs.ToList();
                    break;
                case "stale-decision":
                    decisionId = Text(v4Call, "decisionId");
                    Assert.NotEqual(observation.DecisionId, decisionId);
                    legalRefs = allLegalRefs.ToList();
                    break;
                case "stale-epoch":
                    epoch = Int64(v4Call, "controlEpoch");
                    Assert.True(
                        epoch < observation.ControlEpoch,
                        "stale-epoch must be a true older epoch, got " + epoch +
                        " against current " + observation.ControlEpoch);
                    legalRefs = allLegalRefs.ToList();
                    break;
                case "foreign-seat":
                    seat = Int(v4Call, "seat");
                    roundId = Text(v4Call, "roundId");
                    decisionId = Text(v4Call, "decisionId");
                    epoch = Int64(v4Call, "controlEpoch");
                    Assert.NotEqual(observation.Seat, seat);
                    legalRefs = new List<string>();
                    break;
                case "candidate-exclusion":
                    string intentRef = Text(v5, "intentActionRef");
                    legalRefs = allLegalRefs.Where(reference => reference != intentRef).ToList();
                    Assert.DoesNotContain(intentRef, legalRefs);
                    break;
                default:
                    throw new InvalidOperationException("Unknown authority mapping mode: " + mode);
            }

            long deadline = observation.DecisionDeadlineMillis;
            JsonElement authority = v5.GetProperty("authority");
            Assert.Equal(seat, Int(authority, "seat"));
            Assert.Equal(roundId, Text(authority, "roundId"));
            Assert.Equal(decisionId, Text(authority, "currentDecisionId"));
            Assert.Equal(epoch, Int64(authority, "controlEpoch"));
            Assert.Equal(deadline, Int64(authority, "deadlineMillis"));
            Assert.True(Bool(authority, "seatAutomated"));
            Assert.False(Bool(authority, "clockPaused"));
            string[] recordedRefs = authority.GetProperty("legalActionRefs")
                .EnumerateArray()
                .Select(element => element.GetString()!)
                .ToArray();
            Assert.Equal(legalRefs.ToArray(), recordedRefs);

            var legalActions = legalRefs.Select(reference =>
            {
                Assert.True(actions.ContainsKey(reference), "Unknown authority action ref: " + reference);
                return actions[reference];
            }).ToList();

            return new AiAuthoritySnapshot(
                seat,
                roundId,
                new DecisionKey(roundId, seat, decisionId),
                epoch,
                true,
                false,
                deadline,
                legalActions);
        }

        private static void AssertV4CallBinding(
            JsonElement caseElement,
            AiObservation observation,
            CommandIntent intent)
        {
            string id = Text(caseElement, "id");
            string kind = Text(caseElement, "kind");
            JsonElement v4Call = caseElement.GetProperty("v4Call");
            JsonElement v5 = caseElement.GetProperty("v5");
            int submittedSeat = Int(v4Call, "seat");

            if (kind == "non-own-seat")
            {
                Assert.NotEqual(observation.Seat, submittedSeat);
            }
            else
            {
                Assert.Equal(observation.Seat, submittedSeat);
            }

            Assert.Equal(observation.DecisionDeadlineMillis, Int64(v4Call, "deadlineMillis"));
            Assert.Equal(
                Bool(v4Call, "nowBeforeDeadline"),
                Int64(v4Call, "nowMillis") < Int64(v4Call, "deadlineMillis"));

            if (kind == "stale-round")
            {
                Assert.NotEqual(observation.RoundId, Text(v4Call, "roundId"));
            }
            else if (kind == "stale-decision")
            {
                Assert.NotEqual(observation.DecisionId, Text(v4Call, "decisionId"));
            }
            else if (kind == "stale-epoch")
            {
                Assert.True(Int64(v4Call, "controlEpoch") < observation.ControlEpoch);
            }
            else if (kind == "current")
            {
                Assert.Equal(observation.RoundId, Text(v4Call, "roundId"));
                Assert.Equal(observation.DecisionId, Text(v4Call, "decisionId"));
                Assert.Equal(observation.ControlEpoch, Int64(v4Call, "controlEpoch"));
            }

            if (kind == "illegal-candidate")
            {
                Assert.Equal("outside-options", Text(v4Call, "submittedActionRef"));
                JsonElement outside = v4Call.GetProperty("action");
                Assert.Equal("Pass", Text(outside, "kind"));
                Assert.Equal(observation.Seat, Int(outside, "player"));
                Assert.Null(TileIdOrNull(outside, "tileId"));
                Assert.Equal(JsonValueKind.Null, v4Call.GetProperty("optionId").ValueKind);
                Assert.Equal(JsonValueKind.Null, outside.GetProperty("optionId").ValueKind);
                Assert.NotEqual(intent.Action.Kind.ToString(), Text(outside, "kind"));
            }
            else
            {
                Assert.Equal(Text(v5, "intentActionRef"), Text(v4Call, "submittedActionRef"));
                Assert.Equal(intent.Action.OptionId, TextOrNull(v4Call, "optionId"));
                AssertActionFields(v4Call.GetProperty("action"), intent.Action, id + " v4Call.action");
            }
        }

        private static void AssertV4Evidence(
            JsonElement caseElement,
            CommandIntent intent,
            bool accepted)
        {
            string id = Text(caseElement, "id");
            JsonElement v4 = caseElement.GetProperty("v4");
            JsonElement events = v4.GetProperty("events");
            JsonElement before = v4.GetProperty("beforeState");
            JsonElement after = v4.GetProperty("afterState");

            Assert.Equal(accepted ? "APPLIED" : "REJECTED", Text(v4, "receiptStatus"));
            Assert.Equal(Int64(v4, "eventCount"), events.GetArrayLength());
            bool statesDiffer = !DeepEquals(before, after);
            Assert.True(
                Bool(v4, "stateChanged") == statesDiffer,
                id + ": stateChanged must match the actual before/after states");

            if (accepted)
            {
                Assert.Equal(JsonValueKind.Null, v4.GetProperty("reason").ValueKind);
                Assert.True(statesDiffer, id + ": accepted state must change");
                Assert.Equal(1, events.GetArrayLength());
                JsonElement discarded = events[0];
                Assert.True(Bool(discarded, "structured"), id + ": accepted event must carry structured fields");
                Assert.Equal("TileDiscarded", Text(discarded, "type"));
                Assert.Equal(intent.Action.Kind.ToString(), Text(discarded, "kind"));
                Assert.Equal(intent.Action.Seat, Int(discarded, "player"));
                Assert.NotNull(intent.Action.Tile);
                Assert.Equal(intent.Action.Tile!.Value.Value, Int(discarded, "tileId"));

                Assert.Equal(Text(before, "roundId"), Text(after, "roundId"));
                Assert.Equal(Int64(before, "roundVersion") + 1L, Int64(after, "roundVersion"));
                Assert.Equal("RESPONSE_WINDOW", Text(after, "phase"));
                Assert.Equal((long)intent.Action.Tile!.Value.Value, Int64(after, "lastDiscardTileId"));
                Assert.Equal((long)intent.Action.Seat, Int64(after, "lastDiscardPlayer"));

                JsonElement beforeSeat = SeatState(before, intent.Action.Seat);
                JsonElement afterSeat = SeatState(after, intent.Action.Seat);
                Assert.Equal(Int64(beforeSeat, "closedCount") - 1L, Int64(afterSeat, "closedCount"));
                Assert.Equal(Int64(beforeSeat, "discardCount") + 1L, Int64(afterSeat, "discardCount"));
                Assert.Equal(Int64(before, "wallPointer"), Int64(after, "wallPointer"));
                Assert.Equal(Int64(before, "wallRemaining"), Int64(after, "wallRemaining"));
            }
            else
            {
                Assert.Equal("STALE_AI_SUBMISSION", Text(v4, "reason"));
                Assert.Equal(0, events.GetArrayLength());
                Assert.False(Bool(v4, "stateChanged"));
                Assert.True(DeepEquals(before, after), id + ": rejected before/after states must be identical");
            }
        }

        private static string ExpectedAuthorityMode(string kind) => kind switch
        {
            "legal-intent" => "current",
            "exact-deadline" => "current",
            "stale-round" => "stale-round",
            "stale-decision" => "stale-decision",
            "stale-epoch" => "stale-epoch",
            "non-own-seat" => "foreign-seat",
            "illegal-candidate" => "candidate-exclusion",
            string other => throw new InvalidOperationException("Unknown case kind: " + other)
        };

        private static void AssertActionFields(JsonElement json, LegalActionOption action, string context)
        {
            Assert.True(
                string.Equals(action.Kind.ToString(), Text(json, "kind"), StringComparison.Ordinal),
                context + ".kind");
            Assert.True(action.Seat == Int(json, "player"), context + ".player");
            Assert.True(Nullable.Equals(action.Tile, TileIdOrNull(json, "tileId")), context + ".tileId");
            Assert.True(
                string.Equals(action.OptionId, TextOrNull(json, "optionId"), StringComparison.Ordinal),
                context + ".optionId");
        }

        private static LegalActionOption ActionOptionFromJson(JsonElement json) =>
            new LegalActionOption(
                ParseDefinedEnum<LegalActionKind>(json, "kind"),
                Int(json, "player"),
                TileIdOrNull(json, "tileId"),
                TextOrNull(json, "optionId"));

        /// <summary>
        /// Strict symbolic enum parse for fixture boundaries. Enum.Parse accepts a numeric
        /// string such as "254" for a byte-backed enum even though no such member exists; a
        /// fixture that feeds an undefined numeric value must fail instead of silently
        /// selecting an impossible action/phase.
        /// </summary>
        internal static TEnum ParseDefinedEnum<TEnum>(JsonElement element, string name)
            where TEnum : struct, Enum
        {
            JsonElement value = element.GetProperty(name);
            if (value.ValueKind != JsonValueKind.String)
            {
                throw new Xunit.Sdk.XunitException(
                    name + " must be a symbolic enum string, got " + value.ValueKind +
                    " raw=" + value.GetRawText());
            }

            string text = value.GetString()!;
            string[] names = Enum.GetNames(typeof(TEnum));
            if (Array.IndexOf(names, text) < 0)
            {
                throw new Xunit.Sdk.XunitException(
                    name + " is not a defined " + typeof(TEnum).Name + " member: " + text);
            }

            return (TEnum)Enum.Parse(typeof(TEnum), text, ignoreCase: false);
        }

        private static JsonElement SeatState(JsonElement state, int seat) =>
            state.GetProperty("seats").EnumerateArray()
                .Single(element => element.GetProperty("seat").GetInt32() == seat);

        internal static bool DeepEquals(JsonElement left, JsonElement right)
        {
            if (left.ValueKind != right.ValueKind)
            {
                return false;
            }

            switch (left.ValueKind)
            {
                case JsonValueKind.Object:
                    Dictionary<string, JsonElement> leftProperties = left.EnumerateObject()
                        .ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
                    Dictionary<string, JsonElement> rightProperties = right.EnumerateObject()
                        .ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
                    if (leftProperties.Count != rightProperties.Count)
                    {
                        return false;
                    }

                    foreach (KeyValuePair<string, JsonElement> property in leftProperties)
                    {
                        if (!rightProperties.TryGetValue(property.Key, out JsonElement other) ||
                            !DeepEquals(property.Value, other))
                        {
                            return false;
                        }
                    }

                    return true;
                case JsonValueKind.Array:
                    JsonElement[] leftItems = left.EnumerateArray().ToArray();
                    JsonElement[] rightItems = right.EnumerateArray().ToArray();
                    if (leftItems.Length != rightItems.Length)
                    {
                        return false;
                    }

                    for (int index = 0; index < leftItems.Length; index++)
                    {
                        if (!DeepEquals(leftItems[index], rightItems[index]))
                        {
                            return false;
                        }
                    }

                    return true;
                case JsonValueKind.String:
                    return string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal);
                case JsonValueKind.Number:
                    return string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal);
                case JsonValueKind.True:
                case JsonValueKind.False:
                case JsonValueKind.Null:
                    return true;
                default:
                    return string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal);
            }
        }

        private static int Int(JsonElement element, string name) => element.GetProperty(name).GetInt32();

        private static TileId? TileIdOrNull(JsonElement element, string name)
        {
            JsonElement value = element.GetProperty(name);
            if (value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            return new TileId(value.GetInt32());
        }

        private static string Text(JsonElement element, string name)
        {
            string? value = TextOrNull(element, name);
            Assert.False(string.IsNullOrWhiteSpace(value), "Missing string field: " + name);
            return value!;
        }

        private static string? TextOrNull(JsonElement element, string name)
        {
            JsonElement value = element.GetProperty(name);
            return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
        }

        private static long Int64(JsonElement element, string name) => element.GetProperty(name).GetInt64();

        private static bool Bool(JsonElement element, string name) => element.GetProperty(name).GetBoolean();

        private sealed class ThrowingStrategy : IAiStrategy
        {
            public string AlgorithmId => "must-not-run";

            public int AlgorithmVersion => 1;

            public AiStrategicChoice Decide(AiObservation observation, System.Threading.CancellationToken cancellationToken) =>
                throw new InvalidOperationException("Golden replay/admission must not run a strategy.");
        }
    }
}
