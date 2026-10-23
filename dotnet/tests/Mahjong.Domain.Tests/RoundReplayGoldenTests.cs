using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.Domain.Tests
{
    /// <summary>
    /// Differential replay against the real V4-generated tests/v5-golden/round-v1.json.
    /// Every expected value here is read from that file; nothing is hand-computed. If the
    /// exporter has not run, this test fails with the concrete regeneration command rather
    /// than silently reporting alignment.
    /// </summary>
    public class RoundReplayGoldenTests
    {
        private const string RelativePath = "tests/v5-golden/round-v1.json";

        private static JsonDocument LoadGolden()
        {
            string path = FindGoldenPath();
            return JsonDocument.Parse(File.ReadAllText(path));
        }

        private static string FindGoldenPath([CallerFilePath] string sourceFile = "")
        {
            string? overrideRoot = Environment.GetEnvironmentVariable("MAHJONG_REPO_ROOT");
            if (!string.IsNullOrEmpty(overrideRoot))
            {
                string overridden = Path.Combine(overrideRoot!, RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(overridden))
                {
                    return overridden;
                }
            }

            foreach (string start in new[]
                     {
                         AppContext.BaseDirectory,
                         Directory.GetCurrentDirectory(),
                         Path.GetDirectoryName(sourceFile) ?? string.Empty
                     })
            {
                if (string.IsNullOrEmpty(start))
                {
                    continue;
                }

                DirectoryInfo? directory = new DirectoryInfo(start);
                while (directory != null)
                {
                    string candidate = Path.Combine(
                        directory.FullName,
                        RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }

                    directory = directory.Parent;
                }
            }

            throw new Xunit.Sdk.XunitException(
                "Missing real V4 golden file " + RelativePath + " from " + AppContext.BaseDirectory +
                " or " + Directory.GetCurrentDirectory() + ". Regenerate with: " +
                "$env:V5_ROUND_GOLDEN_EXPORT='1'; .\\gradlew.bat :engine:test --tests \"com.majiang.rules.V5RoundGoldenExportTest\" --console=plain");
        }

        [Fact]
        public void GoldenFileIsRealExportedEvidence()
        {
            using JsonDocument document = LoadGolden();
            JsonElement root = document.RootElement;

            Assert.Equal("v5-round-golden-v1", Str(root, "schema"));
            Assert.Equal("exported", Str(root, "extractionStatus"));
            Assert.Equal(1, Int(root, "rulesetVersion"));
            Assert.Matches("^[0-9a-f]{40}$", Str(root, "v4Commit"));
            Assert.True(Str(root, "generatedByCommand").Contains("V5RoundGoldenExportTest", StringComparison.Ordinal));
            Assert.True(root.GetProperty("unexportedSamples").GetArrayLength() > 0);
            Assert.Equal(2, Int(root.GetProperty("generationResult"), "roundLifecycleSamples"));
            Assert.Equal(16, Int(root.GetProperty("generationResult"), "matchProgressionCases"));
            Assert.Equal(6, Int(root.GetProperty("generationResult"), "decisionClockScenarios"));

            Assert.Equal("RG-001", Str(root.GetProperty("roundTerminal"), "sampleId"));
            Assert.Equal("RG-002", Str(root.GetProperty("chankanRon"), "sampleId"));
            Assert.Equal("exported", Str(root.GetProperty("roundTerminal"), "status"));
            Assert.Equal("exported", Str(root.GetProperty("chankanRon"), "status"));
            foreach (JsonElement scenario in root.GetProperty("decisionClock").EnumerateArray())
            {
                Assert.Equal("exported", Str(scenario, "status"));
            }
        }

        [Fact]
        public void RoundTerminalReplaysExactly()
        {
            using JsonDocument document = LoadGolden();
            JsonElement sample = document.RootElement.GetProperty("roundTerminal");
            JsonElement input = sample.GetProperty("input");
            JsonElement settlement = sample.GetProperty("settlement");
            JsonElement expected = sample.GetProperty("expected");

            string roundId = Str(input, "roundId");
            RoundStateMachine round = P13.Dealt(
                roundId,
                Int(input, "dealer"),
                LongArray(input.GetProperty("scores")),
                Int(input, "riichiSticks"),
                phase: RoundPhase.Discard);

            RoundSettlement value = new RoundSettlement(
                Str(settlement, "resultId"),
                RoundOutcome.Tsumo(Int(settlement, "winner")),
                LongArray(settlement.GetProperty("finalScores")),
                Int(settlement, "remainingSticks"),
                ParseUtc(Str(settlement, "completedAtUtc")),
                Long(settlement, "completedAtMonotonicMillis"));

            RoundTransition transition = round.Execute(RoundCommand.Tsumo(roundId, Int(settlement, "winner"), value));

            Assert.True(transition.IsAccepted, transition.Error?.ToString());
            Assert.Equal(Long(expected, "versionAfter"), transition.State!.Version);
            Assert.Equal(RoundPhase.RoundEnd, transition.State.Phase);
            Assert.Equal(RoundOutcomeKind.Tsumo, transition.Result!.Outcome.Kind);
            Assert.Equal(LongArray(expected.GetProperty("resultFinalScores")), transition.Result.FinalScores);
            Assert.Equal(LongArray(expected.GetProperty("resultDeltas")), transition.Result.TotalDeltas);
            Assert.Equal(Int(settlement, "remainingSticks"), transition.Result.RemainingRiichiSticks);
            Assert.Contains(transition.State.Events, e => e.Type == RoundEventType.TsumoWin);

            foreach (JsonElement rejected in expected.GetProperty("rejectedActions").EnumerateArray())
            {
                string action = rejected.GetString()!;
                RoundCommand command = action == "StartStacking"
                    ? RoundCommand.StartStacking(roundId)
                    : RoundCommand.Tsumo(roundId, Int(settlement, "winner"), value);
                RoundPreparation preparation = transition.State.Prepare(command);
                Assert.False(preparation.IsReady, action + " must be rejected after ROUND_END");
                Assert.Equal(RoundTransitionErrorCode.RoundFinished, preparation.Error!.Code);
            }

            Assert.Equal(Long(expected, "versionAfterRejections"), transition.State.Version);
        }

        [Fact]
        public void ChankanRonReplaysExactly()
        {
            using JsonDocument document = LoadGolden();
            JsonElement sample = document.RootElement.GetProperty("chankanRon");
            JsonElement input = sample.GetProperty("input");
            JsonElement expected = sample.GetProperty("expected");

            string roundId = Str(input, "roundId");
            int kanser = Int(input, "kanserSeat");
            int ronSeat = Int(input, "ronSeat");
            int addedTile = Int(input, "addedTileId");
            int[] eligible = IntArray(input.GetProperty("eligibleSeats"));
            RoundStateMachine round = P13.ChankanDealt(roundId);
            ulong hashBefore = round.Tiles.StateHash;

            RoundTransition window = round.Execute(RoundCommand.AddedKan(
                roundId,
                kanser,
                new TileId(addedTile),
                eligible));

            Assert.True(window.IsAccepted, window.Error?.ToString());
            Assert.Equal(RoundPhase.ChankanWindow, window.State!.Phase);
            Assert.Equal(hashBefore, window.State.Tiles.StateHash);
            Assert.Equal(Int(expected, "rinshanPointerAfterKan"), window.State.Tiles.RinshanPointer);
            Assert.Equal(Int(expected, "totalKanCountAfterKan"), window.State.Tiles.KanCount);
            Assert.Equal(3, window.State.Tiles.CountAt(TileZone.Meld, kanser));

            RoundOutcome outcome = RoundOutcome.Ron(Int(expected, "winner"), Int(expected, "loser"));
            RoundSettlement settlement = new RoundSettlement(
                Str(expected, "resultId"),
                outcome,
                LongArray(expected.GetProperty("finalScores")),
                0,
                ParseUtc("2026-10-23T00:00:00.0000000+00:00"),
                2000L);

            RoundTransition current = window;
            foreach (int seat in eligible)
            {
                if (seat == ronSeat || current.State!.Window == null || current.State.Window.HasResponded(seat))
                {
                    continue;
                }

                current = current.State.Execute(RoundCommand.Pass(roundId, seat));
                Assert.True(current.IsAccepted, current.Error?.ToString());
                if (current.Result != null)
                {
                    break;
                }
            }

            RoundTransition claim = current.State!.Execute(RoundCommand.Ron(roundId, ronSeat, settlement));
            Assert.True(claim.IsAccepted, claim.Error?.ToString());
            if (claim.Result == null)
            {
                foreach (int seat in eligible)
                {
                    if (claim.State!.Window == null || claim.State.Window.HasResponded(seat))
                    {
                        continue;
                    }

                    claim = claim.State.Execute(RoundCommand.Pass(roundId, seat));
                    Assert.True(claim.IsAccepted, claim.Error?.ToString());
                }
            }

            Assert.NotNull(claim.Result);
            Assert.Equal(RoundPhase.RoundEnd, claim.State!.Phase);
            Assert.Equal(hashBefore, claim.State.Tiles.StateHash);
            Assert.Equal(Int(expected, "rinshanPointerAfterRon"), claim.State.Tiles.RinshanPointer);
            Assert.Equal(Int(expected, "totalKanCountAfterRon"), claim.State.Tiles.KanCount);
            Assert.DoesNotContain(claim.State.Events, e => e.Type == RoundEventType.KanDeclared);
            Assert.DoesNotContain(claim.State.Events, e => e.Type == RoundEventType.RinshanDrawn);
            Assert.Equal(RoundOutcomeKind.Ron, claim.Result!.Outcome.Kind);
            Assert.Equal(Int(expected, "winner"), claim.Result.Outcome.WinnerSeat);
            Assert.Equal(Int(expected, "loser"), claim.Result.Outcome.LoserSeat);
            Assert.Equal(LongArray(expected.GetProperty("finalScores")), claim.Result.FinalScores);
            Assert.Equal(LongArray(expected.GetProperty("totalDeltas")), claim.Result.TotalDeltas);
            Assert.Equal(Long(expected, "versionAfterRon"), claim.State.Version);
        }

        [Fact]
        public void MatchProgressionMatrixReplaysExactly()
        {
            using JsonDocument document = LoadGolden();
            JsonElement cases = document.RootElement.GetProperty("matchProgression");
            Assert.Equal(16, cases.GetArrayLength());

            int replayed = 0;
            foreach (JsonElement sample in cases.EnumerateArray())
            {
                JsonElement configElement = sample.GetProperty("config");
                MatchRuleConfig config = new MatchRuleConfig(
                    length: Str(configElement, "length") == "TONPUUSEN" ? MatchLength.Tonpuusen : MatchLength.Hanchan,
                    targetScore: Long(configElement, "targetScore"),
                    allowWestExtension: Bool(configElement, "allowWestExtension"),
                    allowAgariYame: Bool(configElement, "allowAgariYame"),
                    allowBankruptcy: Bool(configElement, "allowBankruptcy"),
                    handsPerWind: Int(configElement, "handsPerWind"),
                    winds: IntArray(configElement.GetProperty("winds")),
                    bankruptcyThreshold: Long(configElement, "bankruptcyThreshold"),
                    bankruptcyInclusive: Bool(configElement, "bankruptcyInclusive"),
                    awardRemainingRiichiSticksToFirst: Bool(configElement, "awardRemainingRiichiSticksToFirst"),
                    tieBreakSeats: IntArray(configElement.GetProperty("tieBreakSeats")));

                JsonElement input = sample.GetProperty("input");
                RoundOutcome outcome = ParseOutcome(input.GetProperty("outcome"));
                var progressionInput = new ProgressionInput(
                    Int(input, "dealer"),
                    Int(input, "roundWind"),
                    Int(input, "roundNumber"),
                    Int(input, "honba"),
                    LongArray(input.GetProperty("scores")),
                    outcome,
                    Int(input, "remainingRiichiSticks"),
                    Bool(input, "dealerDeclaredAgariYame"));

                ProgressionResult result = new MatchProgression(config).NextRound(progressionInput);
                JsonElement expected = sample.GetProperty("expected");
                string type = Str(expected, "type");

                if (type == "NextRound")
                {
                    var next = result as ProgressionResult.NextRound;
                    Assert.NotNull(next);
                    Assert.Equal(Int(expected, "roundWind"), next!.RoundWind);
                    Assert.Equal(Int(expected, "roundNumber"), next.RoundNumber);
                    Assert.Equal(Int(expected, "dealer"), next.DealerSeat);
                    Assert.Equal(Int(expected, "honba"), next.HonbaSticks);
                    Assert.Equal(Bool(expected, "canDealerDeclareAgariYame"), next.CanDealerDeclareAgariYame);
                }
                else
                {
                    var finished = result as ProgressionResult.MatchFinished;
                    Assert.NotNull(finished);
                    Assert.Equal(LongArray(expected.GetProperty("finalScores")), finished!.FinalScores);
                    Assert.Equal(Str(expected, "reason"), ReasonCode(finished.Reason));
                    Assert.Equal(Int(expected, "transferSeat"), finished.RemainingRiichiSticksTransferredTo ?? -1);
                    JsonElement rankings = expected.GetProperty("rankings");
                    Assert.Equal(rankings.GetArrayLength(), finished.Rankings.Count);
                    int i = 0;
                    foreach (JsonElement ranking in rankings.EnumerateArray())
                    {
                        Assert.Equal(Int(ranking, "rank"), finished.Rankings[i].Rank);
                        Assert.Equal(Int(ranking, "seat"), finished.Rankings[i].Seat);
                        Assert.Equal(Long(ranking, "score"), finished.Rankings[i].Score);
                        i++;
                    }
                }

                replayed++;
            }

            Assert.Equal(16, replayed);
        }

        [Fact]
        public void DecisionClockTracesReplayExactly()
        {
            using JsonDocument document = LoadGolden();
            JsonElement scenarios = document.RootElement.GetProperty("decisionClock");
            Assert.Equal(6, scenarios.GetArrayLength());

            int replayedOperations = 0;
            foreach (JsonElement scenario in scenarios.EnumerateArray())
            {
                JsonElement configElement = scenario.GetProperty("config");
                var config = new DecisionClockConfig(
                    Long(configElement, "baseMillis"),
                    Long(configElement, "reserveMillis"),
                    Str(configElement, "scope") == "SESSION" ? ReserveScope.Session : ReserveScope.Round);
                var clock = new DecisionClock(config);

                foreach (JsonElement op in scenario.GetProperty("operations").EnumerateArray())
                {
                    string name = Str(op, "op");
                    switch (name)
                    {
                        case "beginRound":
                            clock.BeginRound(Str(op, "roundId"), Long(op, "now"));
                            break;
                        case "open":
                        {
                            var key = new DecisionKey(Str(op, "roundId"), Int(op, "seat"), Str(op, "decisionId"));
                            DecisionTiming timing = clock.Open(key, Long(op, "at"));
                            Assert.Equal(Long(op, "deadline"), timing.DeadlineMillis);
                            Assert.Equal(Long(op, "reserveAtOpen"), timing.ReserveAtOpenMillis);
                            Assert.Equal(Long(op, "generation"), timing.Generation);
                            break;
                        }
                        case "complete":
                        {
                            var key = new DecisionKey(Str(op, "roundId"), Int(op, "seat"), Str(op, "decisionId"));
                            Assert.Equal(Bool(op, "result"), clock.Complete(key, Long(op, "at")));
                            break;
                        }
                        case "expired":
                            Assert.Equal(Int(op, "count"), clock.Expired(Long(op, "at")).Count);
                            break;
                        case "expire":
                        {
                            var key = new DecisionKey(Str(op, "roundId"), Int(op, "seat"), Str(op, "decisionId"));
                            Assert.Equal(
                                Bool(op, "result"),
                                clock.Expire(key, Long(op, "generation"), Long(op, "at")));
                            break;
                        }
                        case "cancel":
                        {
                            var key = new DecisionKey(Str(op, "roundId"), Int(op, "seat"), Str(op, "decisionId"));
                            Assert.Equal(Bool(op, "result"), clock.Cancel(key, Long(op, "at")));
                            break;
                        }
                        case "pause":
                            clock.Pause(Long(op, "at"));
                            break;
                        case "resume":
                            clock.Resume(Long(op, "at"));
                            break;
                        case "remainingReserve":
                            Assert.Equal(Long(op, "value"), clock.RemainingReserve(Int(op, "seat")));
                            break;
                        case "timingDeadline":
                            Assert.Equal(Long(op, "value"), clock.Timing(Int(op, "seat"))!.DeadlineMillis);
                            break;
                        default:
                            throw new Xunit.Sdk.XunitException("Unknown golden clock op " + name);
                    }

                    replayedOperations++;
                }
            }

            Assert.True(replayedOperations >= 35, "too few exported clock operations were replayed: " + replayedOperations);
        }

        [Fact]
        public void ControlEpochOperationsReplayExactly()
        {
            using JsonDocument document = LoadGolden();
            JsonElement sample = document.RootElement.GetProperty("controlEpoch");
            var registry = new ControlRegistry();

            foreach (JsonElement op in sample.GetProperty("operations").EnumerateArray())
            {
                string name = Str(op, "op");
                switch (name)
                {
                    case "initial":
                        AssertEpochs(registry, op);
                        break;
                    case "setController":
                        registry.SetController(Int(op, "seat"), ParseControllerKind(Str(op, "kind")));
                        AssertEpochs(registry, op);
                        break;
                    case "initialize":
                    {
                        var kinds = op.GetProperty("kinds").EnumerateArray()
                            .Select(value => ParseControllerKind(value.GetString()!))
                            .ToArray();
                        registry.Initialize(kinds);
                        AssertEpochs(registry, op);
                        break;
                    }
                    case "validateEpoch":
                        Assert.Equal(
                            Bool(op, "result"),
                            registry.ValidateEpoch(Int(op, "seat"), Long(op, "expectedEpoch")).IsValid);
                        break;
                    default:
                        throw new Xunit.Sdk.XunitException("Unknown golden control op " + name);
                }
            }
        }

        private static void AssertEpochs(ControlRegistry registry, JsonElement op)
        {
            long[] epochs = LongArray(op.GetProperty("epochs"));
            string[] kinds = op.GetProperty("kinds").EnumerateArray().Select(e => e.GetString()!).ToArray();
            Assert.Equal(4, epochs.Length);
            Assert.Equal(4, kinds.Length);
            for (int seat = 0; seat < 4; seat++)
            {
                Assert.Equal(epochs[seat], registry.GetEpoch(seat));
                Assert.Equal(ParseControllerKind(kinds[seat]), registry.GetController(seat));
            }
        }

        private static SeatControllerKind ParseControllerKind(string value)
        {
            switch (value)
            {
                case "LocalHuman":
                    return SeatControllerKind.LocalHuman;
                case "RemoteHuman":
                    return SeatControllerKind.RemoteHuman;
                case "Computer":
                    return SeatControllerKind.Computer;
                case "Takeover":
                    return SeatControllerKind.Takeover;
                default:
                    throw new Xunit.Sdk.XunitException("Unknown controller kind " + value);
            }
        }

        private static RoundOutcome ParseOutcome(JsonElement element)
        {
            string kind = Str(element, "kind");
            int winner = Int(element, "winner");
            int loser = Int(element, "loser");
            switch (kind)
            {
                case "Tsumo":
                    return RoundOutcome.Tsumo(winner);
                case "Ron":
                    return RoundOutcome.Ron(winner, loser);
                case "ExhaustiveDraw":
                    return RoundOutcome.ExhaustiveDraw(IntArray(element.GetProperty("tenpai")));
                case "AbortiveDraw":
                    return RoundOutcome.AbortiveDraw(Str(element, "reason"));
                case "DealerWin":
                    return RoundOutcome.DealerWin(winner);
                case "NonDealerWin":
                    return RoundOutcome.NonDealerWin(winner);
                default:
                    throw new Xunit.Sdk.XunitException("Unknown golden outcome kind " + kind);
            }
        }

        private static string ReasonCode(MatchFinishReason reason)
        {
            switch (reason)
            {
                case MatchFinishReason.Bankruptcy:
                    return "BANKRUPTCY";
                case MatchFinishReason.NormalCompletion:
                    return "NORMAL_COMPLETION";
                case MatchFinishReason.WestCompletion:
                    return "WEST_COMPLETION";
                case MatchFinishReason.AgariYame:
                    return "AGARI_YAME";
                default:
                    throw new Xunit.Sdk.XunitException("Unknown finish reason " + reason);
            }
        }

        private static PersistedUtcTime ParseUtc(string value)
        {
            return new PersistedUtcTime(DateTimeOffset.Parse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind));
        }

        private static string Str(JsonElement element, string name)
        {
            JsonElement value = element.GetProperty(name);
            return value.GetString() ?? throw new Xunit.Sdk.XunitException("Null JSON string " + name);
        }

        private static int Int(JsonElement element, string name) => checked((int)Long(element, name));

        private static long Long(JsonElement element, string name) => element.GetProperty(name).GetInt64();

        private static bool Bool(JsonElement element, string name) => element.GetProperty(name).GetBoolean();

        private static long[] LongArray(JsonElement element)
        {
            return element.EnumerateArray().Select(value => value.GetInt64()).ToArray();
        }

        private static int[] IntArray(JsonElement element)
        {
            return element.EnumerateArray().Select(value => checked((int)value.GetInt64())).ToArray();
        }
    }
}
