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
        private const string ExpectedSchema = "v5-ai-golden-v1";
        private const string BaselineCommit = "205506f07f6a1ccfef6b32cf97390431e3349f8d";

        private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "ai-v1.json");

        [Fact]
        public void GoldenFixtureHasProvenanceAndTheExpectedV4Cases()
        {
            using JsonDocument document = LoadFixture();
            JsonElement root = document.RootElement;

            Assert.Equal(ExpectedSchema, Text(root, "schema"));
            Assert.Equal(1L, Int64(root, "generatorVersion"));
            Assert.Equal(1L, Int64(root, "rulesetVersion"));
            Assert.Equal(BaselineCommit, Text(root, "v4Commit"));

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

            Assert.Equal("v4-golden-deterministic", Text(root.GetProperty("recordedIntent"), "algorithmId"));
            Assert.Equal(1L, Int64(root.GetProperty("recordedIntent"), "algorithmVersion"));
            Assert.Equal("L0", Text(root.GetProperty("recordedIntent"), "selectedLegalActionRef"));

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
        }

        [Fact]
        public void GoldenCasesReplayTheRecordedIntentAndMatchTheV4SemanticMapping()
        {
            using JsonDocument document = LoadFixture();
            JsonElement root = document.RootElement;
            JsonElement observationElement = root.GetProperty("v5Observation");
            AiObservation observation = BuildObservation(observationElement, out Dictionary<string, LegalActionOption> actions);

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
                JsonElement v4 = caseElement.GetProperty("v4");
                bool accepted = Bool(expected, "accepted");
                string code = Text(expected, "rejectionCode");

                // The expected C# outcome must agree with the recorded actual V4 result.
                Assert.Equal(accepted ? "APPLIED" : "REJECTED", Text(v4, "receiptStatus"));
                if (accepted)
                {
                    Assert.Equal(JsonValueKind.Null, v4.GetProperty("reason").ValueKind);
                    Assert.True(Bool(v4, "stateChanged"), id + ": V4 accepted call must change state");
                    Assert.True(Int64(v4, "eventCount") >= 1L, id + ": V4 accepted call must emit events");
                }
                else
                {
                    Assert.Equal("STALE_AI_SUBMISSION", Text(v4, "reason"));
                    Assert.False(Bool(v4, "stateChanged"), id + ": V4 rejected call must not change state");
                    Assert.Equal(0L, Int64(v4, "eventCount"));
                    Assert.NotEqual("None", code);
                }

                CommandIntent intent = BuildIntent(root, observation, actions, v5);
                Assert.Equal(observation.RoundId, intent.RoundId);
                Assert.Equal(observation.Seat, intent.Seat);
                Assert.Equal(observation.DecisionId, intent.DecisionId);
                Assert.Equal(observation.ControlEpoch, intent.ControlEpoch);
                Assert.Equal(observation.StateVersion, intent.ObservationStateVersion);
                Assert.Equal(64, intent.AuthorityHash.Length);

                // Replay injects the record; it never invokes a heuristic.
                AiDecision replay = AiDecisionService.Replay(intent, observation);
                Assert.Same(intent, replay.Intent);
                Assert.Equal(0d, replay.HeuristicScore);
                Assert.Equal(intent.AuthorityHash, replay.AuthorityHash);
                Assert.Same(intent.Action, replay.Intent.Action);

                AiAuthoritySnapshot authority = BuildAuthority(caseElement, actions);
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
            AiObservation observation = BuildObservation(observationElement, out Dictionary<string, LegalActionOption> actions);
            JsonElement acceptedCase = root.GetProperty("cases").EnumerateArray()
                .Single(c => Bool(c.GetProperty("v5").GetProperty("expected"), "accepted"));
            JsonElement v5 = acceptedCase.GetProperty("v5");
            CommandIntent intent = BuildIntent(root, observation, actions, v5);
            AiDecision replay = AiDecisionService.Replay(intent, observation);
            AiSubmissionResult result = new AiDecisionService(new ThrowingStrategy())
                .Submit(replay, BuildAuthority(acceptedCase, actions), Int64(v5, "submittedAtMillis"));
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

        private static JsonDocument LoadFixture()
        {
            Assert.True(File.Exists(FixturePath), "Missing golden fixture: " + FixturePath);
            return JsonDocument.Parse(File.ReadAllText(FixturePath));
        }

        private static AiObservation BuildObservation(
            JsonElement observation,
            out Dictionary<string, LegalActionOption> actions)
        {
            actions = new Dictionary<string, LegalActionOption>(StringComparer.Ordinal);
            var legalActions = new List<LegalActionOption>();
            foreach (JsonElement action in observation.GetProperty("legalActions").EnumerateArray())
            {
                var option = new LegalActionOption(
                    Enum.Parse<LegalActionKind>(Text(action, "kind"), ignoreCase: false),
                    action.GetProperty("seat").GetInt32(),
                    TileIdOrNull(action, "tileId"),
                    TextOrNull(action, "optionId"));
                actions.Add(Text(action, "ref"), option);
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
                Enum.Parse<RoundPhase>(Text(observation, "phase"), ignoreCase: false),
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

        private static CommandIntent BuildIntent(
            JsonElement root,
            AiObservation observation,
            IReadOnlyDictionary<string, LegalActionOption> actions,
            JsonElement v5)
        {
            JsonElement recorded = root.GetProperty("recordedIntent");
            string actionRef = Text(v5, "intentActionRef");
            Assert.True(actions.ContainsKey(actionRef), "Unknown intent action ref: " + actionRef);
            return new CommandIntent(
                observation,
                actions[actionRef],
                Text(recorded, "algorithmId"),
                checked((int)Int64(recorded, "algorithmVersion")));
        }

        private static AiAuthoritySnapshot BuildAuthority(
            JsonElement caseElement,
            IReadOnlyDictionary<string, LegalActionOption> actions)
        {
            JsonElement authority = caseElement.GetProperty("v5").GetProperty("authority");
            int seat = authority.GetProperty("seat").GetInt32();
            string roundId = Text(authority, "roundId");
            var currentKey = new DecisionKey(roundId, seat, Text(authority, "currentDecisionId"));
            var legalActions = new List<LegalActionOption>();
            foreach (JsonElement reference in authority.GetProperty("legalActionRefs").EnumerateArray())
            {
                string actionRef = reference.GetString()!;
                Assert.True(actions.ContainsKey(actionRef), "Unknown authority action ref: " + actionRef);
                legalActions.Add(actions[actionRef]);
            }

            return new AiAuthoritySnapshot(
                seat,
                roundId,
                currentKey,
                Int64(authority, "controlEpoch"),
                Bool(authority, "seatAutomated"),
                Bool(authority, "clockPaused"),
                Int64(authority, "deadlineMillis"),
                legalActions);
        }

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
