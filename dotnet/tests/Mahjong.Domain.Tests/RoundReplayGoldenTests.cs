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
            AssertExactProperties(
                sample,
                "roundTerminal",
                "sampleId",
                "kind",
                "status",
                "source",
                "input",
                "settlement",
                "expected");
            Assert.Equal("RG-001", Str(sample, "sampleId"));
            Assert.Equal("round-terminal", Str(sample, "kind"));
            Assert.Equal("exported", Str(sample, "status"));
            JsonElement source = sample.GetProperty("source");
            AssertExactProperties(source, "roundTerminal.source", "file", "testClass", "testMethod");
            RequireNonEmpty(source, "file");
            RequireNonEmpty(source, "testClass");
            RequireNonEmpty(source, "testMethod");

            JsonElement input = sample.GetProperty("input");
            JsonElement settlement = sample.GetProperty("settlement");
            JsonElement expected = sample.GetProperty("expected");
            AssertExactProperties(
                input,
                "roundTerminal.input",
                "roundId",
                "dealer",
                "scores",
                "riichiSticks",
                "phaseBefore",
                "versionBefore");
            AssertExactProperties(
                settlement,
                "roundTerminal.settlement",
                "resultId",
                "outcome",
                "winner",
                "finalScores",
                "totalDeltas",
                "remainingSticks",
                "completedAtUtc",
                "completedAtMonotonicMillis");
            AssertExactProperties(
                expected,
                "roundTerminal.expected",
                "versionAfter",
                "phaseAfter",
                "eventTypes",
                "resultOutcome",
                "resultFinalScores",
                "resultDeltas",
                "rejectedActions",
                "versionAfterRejections");

            // N3-RG-001.input.roundId/dealer/scores/riichiSticks
            string roundId = Str(input, "roundId");
            int dealer = Int(input, "dealer");
            long[] startingScores = LongArray(input.GetProperty("scores"));
            int startingSticks = Int(input, "riichiSticks");
            RoundPhase phaseBefore = ParsePhase(Str(input, "phaseBefore"));
            long versionBefore = Long(input, "versionBefore");

            // The exporter's opening observation is DISCARD at version 0. It is a fixed
            // construction, not a phase read back from the fixture: the fixture must match
            // the real pre-execution round state rather than define it.
            Assert.Equal(RoundPhase.Discard, phaseBefore);
            Assert.Equal(0L, versionBefore);
            RoundStateMachine round = P13.Dealt(
                roundId,
                dealer,
                startingScores,
                startingSticks,
                phase: RoundPhase.Discard);
            Assert.Equal(roundId, round.RoundId);
            Assert.Equal(dealer, round.DealerSeat);
            Assert.Equal(startingScores, round.Scores);
            Assert.Equal(startingSticks, round.RiichiSticks);
            Assert.Equal(phaseBefore, round.Phase);
            Assert.Equal(versionBefore, round.Version);
            Assert.Null(round.Result);

            // N3-RG-001.settlement.resultId/outcome/winner/finalScores/totalDeltas/
            // remainingSticks/completedAtUtc/completedAtMonotonicMillis
            RoundOutcome settlementOutcome = ParseTsumoOutcome(settlement);
            RoundSettlement value = new RoundSettlement(
                Str(settlement, "resultId"),
                settlementOutcome,
                LongArray(settlement.GetProperty("finalScores")),
                Int(settlement, "remainingSticks"),
                ParseUtc(Str(settlement, "completedAtUtc")),
                Long(settlement, "completedAtMonotonicMillis"));

            RoundTransition transition = round.Execute(
                RoundCommand.Tsumo(roundId, Int(settlement, "winner"), value));

            Assert.True(transition.IsAccepted, transition.Error?.ToString());
            Assert.NotNull(transition.State);
            Assert.NotNull(transition.Result);
            RoundResult actual = transition.Result!;

            Assert.Equal(Str(settlement, "resultId"), actual.ResultId);
            Assert.Equal(roundId, actual.RoundId);
            Assert.Equal(RoundOutcomeKind.Tsumo, actual.Outcome.Kind);
            Assert.Equal(Str(settlement, "outcome"), OutcomeName(actual.Outcome.Kind));
            Assert.Equal(Int(settlement, "winner"), actual.Outcome.WinnerSeat);
            Assert.Equal(LongArray(settlement.GetProperty("finalScores")), actual.FinalScores);
            Assert.Equal(LongArray(settlement.GetProperty("totalDeltas")), actual.TotalDeltas);
            Assert.Equal(Int(settlement, "remainingSticks"), actual.RemainingRiichiSticks);
            Assert.Equal(startingSticks, actual.StartingRiichiSticks);
            Assert.Equal(startingScores, actual.StartingScores);
            Assert.Equal(ParseUtc(Str(settlement, "completedAtUtc")).Value, actual.CompletedAtUtc.Value);
            Assert.Equal(Long(settlement, "completedAtMonotonicMillis"), actual.CompletedAtMonotonicMillis);
            Assert.Equal(4, actual.FinalScores.Count);
            Assert.Equal(4, actual.TotalDeltas.Count);
            for (int seat = 0; seat < 4; seat++)
            {
                Assert.Equal(
                    checked(actual.FinalScores[seat] - actual.StartingScores[seat]),
                    actual.TotalDeltas[seat]);
            }

            // N3-RG-001.expected.versionAfter/phaseAfter/resultOutcome/
            // resultFinalScores/resultDeltas
            Assert.Equal(Long(expected, "versionAfter"), transition.State.Version);
            RoundPhase phaseAfter = ParsePhase(Str(expected, "phaseAfter"));
            Assert.Equal(phaseAfter, transition.State.Phase);
            Assert.Equal(RoundPhase.RoundEnd, transition.State.Phase);
            Assert.Equal(
                ParseOutcomeKind(Str(expected, "resultOutcome")),
                actual.Outcome.Kind);
            Assert.Equal(LongArray(expected.GetProperty("resultFinalScores")), actual.FinalScores);
            Assert.Equal(LongArray(expected.GetProperty("resultDeltas")), actual.TotalDeltas);

            // N3-RG-001.expected.eventTypes. C# has no native ScoreSettled event; the
            // semantic slot is the non-null settled RoundResult itself (scores/deltas/sticks
            // are asserted above). The committed terminal events must be exactly
            // TsumoWin + RoundEnded and the projected V4 lifecycle is
            // TsumoWin, ScoreSettled, RoundEnded.
            AssertTerminalLifecycle(
                actual,
                "TsumoWin",
                RoundEndReason.Tsumo,
                Array.Empty<string>(),
                expected.GetProperty("eventTypes"));
            Assert.Equal(
                new[] { "TsumoWin", "RoundEnded" },
                transition.Events.Select(EventName).ToArray());
            Assert.Equal(
                new[] { "TsumoWin", "RoundEnded" },
                transition.State.Events.Select(EventName).ToArray());
            Assert.Equal(
                transition.State.Events.Select(EventName).ToArray(),
                actual.Events.Select(EventName).ToArray());
            Assert.Equal(1, transition.State.Events.Count(e => e.Type == RoundEventType.TsumoWin));
            Assert.Equal(1, transition.State.Events.Count(e => e.Type == RoundEventType.RoundEnded));

            // N3-RG-001.expected.rejectedActions/versionAfterRejections
            string[] rejectedActions = expected.GetProperty("rejectedActions")
                .EnumerateArray()
                .Select(value => value.GetString()
                    ?? throw new Xunit.Sdk.XunitException("Null rejected action in roundTerminal"))
                .ToArray();
            Assert.Equal(
                new[] { "StartStacking", "DeclareTsumo", "DeclareAgariYame" },
                rejectedActions);
            long versionBeforeRejections = transition.State.Version;
            foreach (string action in rejectedActions)
            {
                switch (action)
                {
                    case "StartStacking":
                        AssertRoundFinishedRejection(
                            transition.State,
                            RoundCommand.StartStacking(roundId),
                            action);
                        break;
                    case "DeclareTsumo":
                        AssertRoundFinishedRejection(
                            transition.State,
                            RoundCommand.Tsumo(roundId, Int(settlement, "winner"), value),
                            action);
                        break;
                    case "DeclareAgariYame":
                        AssertAgariYameRejectedAfterRoundEnd(
                            transition.State,
                            ParseUtc(Str(settlement, "completedAtUtc")),
                            Long(settlement, "completedAtMonotonicMillis"));
                        break;
                    default:
                        throw new Xunit.Sdk.XunitException(
                            "Unknown rejected action " + action + " in roundTerminal");
                }

                Assert.Equal(versionBeforeRejections, transition.State.Version);
                Assert.Equal(RoundPhase.RoundEnd, transition.State.Phase);
                Assert.NotNull(transition.State.Result);
            }

            Assert.Equal(Long(expected, "versionAfterRejections"), transition.State.Version);
        }

        [Fact]
        public void ChankanRonReplaysExactly()
        {
            using JsonDocument document = LoadGolden();
            JsonElement sample = document.RootElement.GetProperty("chankanRon");
            AssertExactProperties(
                sample,
                "chankanRon",
                "sampleId",
                "kind",
                "status",
                "source",
                "input",
                "expected");
            Assert.Equal("RG-002", Str(sample, "sampleId"));
            Assert.Equal("round-chankan-ron", Str(sample, "kind"));
            Assert.Equal("exported", Str(sample, "status"));
            JsonElement source = sample.GetProperty("source");
            AssertExactProperties(source, "chankanRon.source", "file", "testClass", "testMethod");
            RequireNonEmpty(source, "file");
            RequireNonEmpty(source, "testClass");
            RequireNonEmpty(source, "testMethod");

            JsonElement input = sample.GetProperty("input");
            JsonElement expected = sample.GetProperty("expected");
            AssertExactProperties(
                input,
                "chankanRon.input",
                "roundId",
                "kanserSeat",
                "addedTileId",
                "ronSeat",
                "eligibleSeats",
                "meldTiles");
            AssertExactProperties(
                expected,
                "chankanRon.expected",
                "phaseAfterKan",
                "meldTypeAfterKan",
                "rinshanPointerAfterKan",
                "totalKanCountAfterKan",
                "legalRonSeat1",
                "phaseAfterRon",
                "rinshanPointerAfterRon",
                "totalKanCountAfterRon",
                "eventTypes",
                "hasKanDeclared",
                "hasRinshanDraw",
                "resultOutcome",
                "winner",
                "loser",
                "finalScores",
                "totalDeltas",
                "resultId",
                "versionAfterRon");

            // N3-RG-002.input.roundId/kanserSeat/addedTileId/ronSeat/eligibleSeats/meldTiles
            string roundId = Str(input, "roundId");
            int kanser = Int(input, "kanserSeat");
            int ronSeat = Int(input, "ronSeat");
            int addedTileValue = Int(input, "addedTileId");
            int[] eligible = IntArray(input.GetProperty("eligibleSeats"));
            int[] meldTileValues = IntArray(input.GetProperty("meldTiles"));
            TileId addedTile = new TileId(addedTileValue);

            // The exported V4 chankan window has exactly seat 1 eligible and seat 1 is the
            // ron seat; these are contract facts of RG-002, not values re-read from the
            // C# window after the test constructed it from the same fixture array.
            Assert.Equal(new[] { 1 }, eligible);
            Assert.Equal(1, ronSeat);

            RoundStateMachine round = P13.ChankanDealt(roundId);
            Assert.Equal(roundId, round.RoundId);
            Assert.Equal(0L, round.Version);
            ConservationReport conservation = TilePositionMap.Validate(round.Tiles);
            Assert.True(conservation.IsValid);
            Assert.Equal(136, conservation.AssignedCount);

            // P13.ChankanDealt seeds the pon physically but carries no RoundMeld record;
            // the exact physical slots are therefore the authoritative fixture binding.
            Assert.Empty(round.Melds);
            Assert.Equal(3, meldTileValues.Length);
            Assert.Equal(3, round.Tiles.CountAt(TileZone.Meld, kanser));
            for (int index = 0; index < meldTileValues.Length; index++)
            {
                var tile = new TileId(meldTileValues[index]);
                Assert.Equal(TilePosition.Meld(kanser, index), round.Tiles.PositionOf(tile));
                Assert.Equal(tile, round.Tiles.TileAt(TilePosition.Meld(kanser, index)));
                Assert.True(tile.SameType(addedTile),
                    "meld tile " + tile + " is not the same type as added tile " + addedTile);
            }

            Assert.Equal(TilePosition.Hand(kanser, 13), round.Tiles.PositionOf(addedTile));
            Assert.True(round.Tiles.DrawnTile.HasValue);
            Assert.Equal(addedTile, round.Tiles.DrawnTile!.Value.Tile);
            Assert.Equal(kanser, round.Tiles.DrawnTile.Value.Seat);
            Assert.Equal(
                meldTileValues.Select(value => new TileId(value)).ToArray(),
                round.Tiles.TilesInZone(TileZone.Meld, kanser).ToArray());

            ulong hashBefore = round.Tiles.StateHash;
            int handCountBefore = round.Tiles.CountAt(TileZone.Hand, kanser);
            RoundTransition window = round.Execute(RoundCommand.AddedKan(
                roundId,
                kanser,
                addedTile,
                eligible));

            Assert.True(window.IsAccepted, window.Error?.ToString());
            Assert.NotNull(window.State);
            Assert.Equal(ParsePhase(Str(expected, "phaseAfterKan")), window.State!.Phase);
            Assert.Equal(RoundPhase.ChankanWindow, window.State.Phase);
            Assert.Equal(hashBefore, window.State.Tiles.StateHash);
            Assert.Equal(Int(expected, "rinshanPointerAfterKan"), window.State.Tiles.RinshanPointer);
            Assert.Equal(Int(expected, "totalKanCountAfterKan"), window.State.Tiles.KanCount);
            Assert.Equal(3, window.State.Tiles.CountAt(TileZone.Meld, kanser));
            Assert.Equal(handCountBefore, window.State.Tiles.CountAt(TileZone.Hand, kanser));

            // N3-RG-002.expected.meldTypeAfterKan. Three same-type physical tiles in the
            // meld zone and no fourth committed tile is the real V4 PON-after-added-kan
            // signature (a chi would have three different types; a committed kan would have 4).
            TileId[] actualMeldTiles = window.State.Tiles
                .TilesInZone(TileZone.Meld, kanser)
                .ToArray();
            string actualMeldType = actualMeldTiles.Length == 3 &&
                                    actualMeldTiles.All(value => value.SameType(addedTile))
                ? "PON"
                : throw new Xunit.Sdk.XunitException(
                    "physical meld is not the expected three-tile PON after added kan");
            Assert.Equal(Str(expected, "meldTypeAfterKan"), actualMeldType);
            Assert.Equal(RoundMeldKind.Pon, ParseMeldKind(Str(expected, "meldTypeAfterKan")));

            RoundResponseWindow? chankanWindow = window.State.Window;
            Assert.NotNull(chankanWindow);
            Assert.True(chankanWindow!.IsChankan);
            Assert.Equal(kanser, chankanWindow.FromSeat);
            Assert.Equal(addedTile, chankanWindow.TargetTile);
            Assert.Equal(eligible, chankanWindow.EligibleSeats.ToArray());
            Assert.Contains(ronSeat, chankanWindow.EligibleSeats);
            Assert.False(chankanWindow.HasResponded(ronSeat));

            // N3-RG-002.expected.legalRonSeat1: the fixture flag is the real eligibility
            // of seat 1 in the chankan window, and the accepted Ron below proves it.
            bool actualLegalRonSeat1 =
                chankanWindow.IsChankan &&
                chankanWindow.FromSeat == kanser &&
                chankanWindow.TargetTile == addedTile &&
                chankanWindow.EligibleSeats.Contains(1);
            Assert.Equal(Bool(expected, "legalRonSeat1"), actualLegalRonSeat1);

            RoundOutcome outcome = ParseRonOutcome(expected);
            Assert.Equal(ronSeat, outcome.WinnerSeat);
            Assert.Equal(kanser, outcome.LoserSeat);
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
                if (seat == ronSeat ||
                    current.State!.Window == null ||
                    current.State.Window.HasResponded(seat))
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

            RoundTransition claim = current.State!.Execute(
                RoundCommand.Ron(roundId, ronSeat, settlement));
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
            Assert.NotNull(claim.State);
            RoundResult actual = claim.Result!;
            Assert.Equal(ParsePhase(Str(expected, "phaseAfterRon")), claim.State!.Phase);
            Assert.Equal(RoundPhase.RoundEnd, claim.State.Phase);
            Assert.Null(claim.State.Window);
            Assert.Equal(hashBefore, claim.State.Tiles.StateHash);
            Assert.Equal(Int(expected, "rinshanPointerAfterRon"), claim.State.Tiles.RinshanPointer);
            Assert.Equal(Int(expected, "totalKanCountAfterRon"), claim.State.Tiles.KanCount);
            Assert.Equal(3, claim.State.Tiles.CountAt(TileZone.Meld, kanser));
            Assert.Equal(handCountBefore, claim.State.Tiles.CountAt(TileZone.Hand, kanser));
            Assert.Equal(TilePosition.Hand(kanser, 13), claim.State.Tiles.PositionOf(addedTile));

            // N3-RG-002.expected.hasKanDeclared/hasRinshanDraw. V4 names the latter
            // RinshanDraw; C# names the same terminal event RinshanDrawn. No state flag is
            // consulted: the booleans are the real result-event Contains semantics.
            bool actualHasKanDeclared = actual.Events.Any(e => e.Type == RoundEventType.KanDeclared);
            bool actualHasRinshanDraw = actual.Events.Any(e => e.Type == RoundEventType.RinshanDrawn);
            Assert.Equal(Bool(expected, "hasKanDeclared"), actualHasKanDeclared);
            Assert.Equal(Bool(expected, "hasRinshanDraw"), actualHasRinshanDraw);
            Assert.Equal(
                actualHasKanDeclared,
                claim.State.Events.Any(e => e.Type == RoundEventType.KanDeclared));
            Assert.Equal(
                actualHasRinshanDraw,
                claim.State.Events.Any(e => e.Type == RoundEventType.RinshanDrawn));

            // N3-RG-002.expected.resultOutcome/winner/loser/finalScores/totalDeltas/
            // resultId/versionAfterRon
            Assert.Equal(RoundOutcomeKind.Ron, actual.Outcome.Kind);
            Assert.Equal(Str(expected, "resultOutcome"), OutcomeName(actual.Outcome.Kind));
            Assert.Equal(Int(expected, "winner"), actual.Outcome.WinnerSeat);
            Assert.Equal(Int(expected, "loser"), actual.Outcome.LoserSeat);
            Assert.Equal(LongArray(expected.GetProperty("finalScores")), actual.FinalScores);
            Assert.Equal(LongArray(expected.GetProperty("totalDeltas")), actual.TotalDeltas);
            Assert.Equal(Str(expected, "resultId"), actual.ResultId);
            Assert.Equal(roundId, actual.RoundId);
            Assert.Equal(Long(expected, "versionAfterRon"), claim.State.Version);
            Assert.Equal(4, actual.FinalScores.Count);
            Assert.Equal(4, actual.TotalDeltas.Count);
            for (int seat = 0; seat < 4; seat++)
            {
                Assert.Equal(
                    checked(actual.FinalScores[seat] - actual.StartingScores[seat]),
                    actual.TotalDeltas[seat]);
            }

            // N3-RG-002.expected.eventTypes. The window/claim prefix is real C# history and
            // must be exact; only the terminal lifecycle tail maps to V4 result.events:
            // RonWin, ScoreSettled (the non-null settled result), RoundEnded.
            AssertTerminalLifecycle(
                actual,
                "RonWin",
                RoundEndReason.ChankanRon,
                new[] { "ChankanWindowOpened", "ResponseClaimed" },
                expected.GetProperty("eventTypes"));
            Assert.Equal(
                new[] { "ResponseClaimed", "RonWin", "RoundEnded" },
                claim.Events.Select(EventName).ToArray());
            Assert.Equal(
                new[] { "ChankanWindowOpened", "ResponseClaimed", "RonWin", "RoundEnded" },
                claim.State.Events.Select(EventName).ToArray());
            Assert.Equal(
                claim.State.Events.Select(EventName).ToArray(),
                actual.Events.Select(EventName).ToArray());
            Assert.Equal(1, claim.State.Events.Count(e => e.Type == RoundEventType.RonWin));
            Assert.Equal(1, claim.State.Events.Count(e => e.Type == RoundEventType.RoundEnded));
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

        private static void AssertTerminalLifecycle(
            RoundResult result,
            string winEventName,
            RoundEndReason expectedReason,
            string[] expectedPrefix,
            JsonElement fixtureEventTypes)
        {
            if (result == null)
            {
                throw new Xunit.Sdk.XunitException("A terminal lifecycle requires a settled RoundResult.");
            }

            Assert.Equal(expectedReason, result.EndReason);
            Assert.NotNull(result.Outcome);

            string[] actual = result.Events.Select(EventName).ToArray();

            // The prefix is real committed history (for example a chankan window and its
            // response claim); it must equal the exact expected C# prefix, not be filtered
            // away after the fact. After it, the terminal tail is exactly win + RoundEnded.
            Assert.Equal(expectedPrefix.Length + 2, actual.Length);
            for (int index = 0; index < expectedPrefix.Length; index++)
            {
                Assert.Equal(expectedPrefix[index], actual[index]);
            }

            Assert.Equal(winEventName, actual[expectedPrefix.Length]);
            Assert.Equal("RoundEnded", actual[actual.Length - 1]);
            Assert.Equal(1, actual.Count(name => name == winEventName));
            Assert.Equal(1, actual.Count(name => name == "RoundEnded"));
            Assert.Equal(0, actual.Count(name => name == "MatchEnded"));

            // C# RoundEventType has no ScoreSettled member. The non-null settled
            // RoundResult (result id, final scores, deltas, sticks) is the real semantic
            // evidence for that V4 slot; it is not a deleted fixture field.
            Assert.False(string.IsNullOrWhiteSpace(result.ResultId));
            Assert.Equal(4, result.FinalScores.Count);
            Assert.Equal(4, result.TotalDeltas.Count);
            Assert.True(result.RemainingRiichiSticks >= 0);

            string[] fixture = fixtureEventTypes.EnumerateArray()
                .Select(value => value.GetString()
                    ?? throw new Xunit.Sdk.XunitException("Null event type in round golden"))
                .ToArray();
            Assert.Equal(1, fixture.Count(name => name == "ScoreSettled"));
            Assert.Equal(1, fixture.Count(name => name == "RoundEnded"));
            Assert.Equal(1, fixture.Count(name => name == winEventName));
            Assert.Equal(0, fixture.Count(name => name == "MatchEnded"));
            Assert.Equal(new[] { winEventName, "ScoreSettled", "RoundEnded" }, fixture);
        }

        private static void AssertRoundFinishedRejection(
            RoundStateMachine state,
            RoundCommand command,
            string action)
        {
            RoundPreparation preparation = state.Prepare(command);
            Assert.False(preparation.IsReady, action + " must be rejected after ROUND_END");
            Assert.NotNull(preparation.Error);
            Assert.Equal(RoundTransitionErrorCode.RoundFinished, preparation.Error!.Code);
        }

        private static void AssertAgariYameRejectedAfterRoundEnd(
            RoundStateMachine terminal,
            PersistedUtcTime completedAtUtc,
            long completedAtMonotonicMillis)
        {
            Assert.True(terminal.IsFinished);
            Assert.Equal(RoundPhase.RoundEnd, terminal.Phase);
            Assert.NotNull(terminal.Result);
            long terminalVersion = terminal.Version;
            RoundResult terminalResult = terminal.Result!;

            // Exercise the real match-level Agari-Yame API against a real ROUND_END, not a
            // synthetic session. The fresh round uses the same frozen spec/settlement so the
            // match reaches a genuine finished active round first.
            string roundId = "rg-001-terminal-agari-yame";
            RoundStateMachine active = P13.Dealt(
                roundId,
                terminal.DealerSeat,
                terminalResult.StartingScores.ToArray(),
                terminalResult.StartingRiichiSticks,
                phase: RoundPhase.Discard);
            var settlement = new RoundSettlement(
                terminalResult.ResultId,
                terminalResult.Outcome,
                terminalResult.FinalScores,
                terminalResult.RemainingRiichiSticks,
                completedAtUtc,
                completedAtMonotonicMillis);
            RoundTransition finished = active.Execute(
                RoundCommand.Tsumo(roundId, terminalResult.Outcome.WinnerSeat, settlement));
            Assert.True(finished.IsAccepted, finished.Error?.ToString());
            Assert.NotNull(finished.State);
            Assert.True(finished.State!.IsFinished);

            MatchSession session = MatchSession.Create(roundId);
            MatchTransition started = session.StartSession(active);
            Assert.True(started.IsAccepted, started.Error?.ToString());
            Assert.NotNull(started.State);
            session = started.State!;
            MatchTransition accepted = session.AcceptRoundResult(
                finished.State,
                completedAtUtc,
                completedAtMonotonicMillis);
            Assert.True(accepted.IsAccepted, accepted.Error?.ToString());
            Assert.NotNull(accepted.State);
            session = accepted.State!;
            Assert.Equal(SessionLifecycle.WaitingReady, session.Lifecycle);
            Assert.False(session.CanDealerDeclareAgariYame);
            long sessionVersion = session.Version;

            MatchTransition choice = session.ChooseDealerEnd(
                true,
                completedAtUtc,
                completedAtMonotonicMillis);
            Assert.False(choice.IsAccepted);
            Assert.NotNull(choice.Error);
            Assert.Equal(MatchTransitionErrorCode.DealerChoiceUnavailable, choice.Error!.Code);
            Assert.Null(choice.State);
            Assert.Equal(sessionVersion, session.Version);
            Assert.Equal(terminalVersion, finished.State.Version);
            Assert.Equal(RoundPhase.RoundEnd, finished.State.Phase);
            Assert.NotNull(finished.State.Result);

            Assert.Equal(terminalVersion, terminal.Version);
            Assert.Equal(RoundPhase.RoundEnd, terminal.Phase);
            Assert.NotNull(terminal.Result);
        }

        private static string EventName(RoundEvent value) => value.Type.ToString();

        private static string OutcomeName(RoundOutcomeKind kind)
        {
            switch (kind)
            {
                case RoundOutcomeKind.DealerWin:
                    return "DealerWin";
                case RoundOutcomeKind.NonDealerWin:
                    return "NonDealerWin";
                case RoundOutcomeKind.Ron:
                    return "Ron";
                case RoundOutcomeKind.Tsumo:
                    return "Tsumo";
                case RoundOutcomeKind.ExhaustiveDraw:
                    return "ExhaustiveDraw";
                case RoundOutcomeKind.AbortiveDraw:
                    return "AbortiveDraw";
                default:
                    throw new Xunit.Sdk.XunitException("Unknown terminal outcome kind " + kind);
            }
        }

        private static RoundOutcomeKind ParseOutcomeKind(string value)
        {
            switch (value)
            {
                case "Tsumo":
                    return RoundOutcomeKind.Tsumo;
                case "Ron":
                    return RoundOutcomeKind.Ron;
                case "ExhaustiveDraw":
                    return RoundOutcomeKind.ExhaustiveDraw;
                case "AbortiveDraw":
                    return RoundOutcomeKind.AbortiveDraw;
                case "DealerWin":
                    return RoundOutcomeKind.DealerWin;
                case "NonDealerWin":
                    return RoundOutcomeKind.NonDealerWin;
                default:
                    throw new Xunit.Sdk.XunitException("Unknown golden outcome kind " + value);
            }
        }

        private static RoundOutcome ParseTsumoOutcome(JsonElement settlement)
        {
            string outcome = Str(settlement, "outcome");
            if (outcome != "Tsumo")
            {
                throw new Xunit.Sdk.XunitException(
                    "roundTerminal settlement must be Tsumo, was " + outcome);
            }

            return RoundOutcome.Tsumo(Int(settlement, "winner"));
        }

        private static RoundOutcome ParseRonOutcome(JsonElement expected)
        {
            string outcome = Str(expected, "resultOutcome");
            if (outcome != "Ron")
            {
                throw new Xunit.Sdk.XunitException(
                    "chankanRon resultOutcome must be Ron, was " + outcome);
            }

            return RoundOutcome.Ron(Int(expected, "winner"), Int(expected, "loser"));
        }

        private static RoundPhase ParsePhase(string value)
        {
            switch (value)
            {
                case "STACKING":
                    return RoundPhase.Stacking;
                case "DEALING":
                    return RoundPhase.Dealing;
                case "DRAW":
                    return RoundPhase.Draw;
                case "CHEAT_WINDOW":
                    return RoundPhase.CheatWindow;
                case "DISCARD":
                    return RoundPhase.Discard;
                case "RESPONSE_WINDOW":
                    return RoundPhase.ResponseWindow;
                case "CHANKAN_WINDOW":
                    return RoundPhase.ChankanWindow;
                case "KAN_DRAW":
                    return RoundPhase.KanDraw;
                case "ROUND_END":
                    return RoundPhase.RoundEnd;
                default:
                    throw new Xunit.Sdk.XunitException("Unknown golden round phase " + value);
            }
        }

        private static RoundMeldKind ParseMeldKind(string value)
        {
            switch (value)
            {
                case "CHI":
                    return RoundMeldKind.Chi;
                case "PON":
                    return RoundMeldKind.Pon;
                case "OPEN_KAN":
                    return RoundMeldKind.OpenKan;
                case "CLOSED_KAN":
                    return RoundMeldKind.ClosedKan;
                case "ADDED_KAN":
                    return RoundMeldKind.AddedKan;
                default:
                    throw new Xunit.Sdk.XunitException("Unknown golden meld kind " + value);
            }
        }

        private static void AssertExactProperties(
            JsonElement element,
            string context,
            params string[] expected)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new Xunit.Sdk.XunitException(context + " must be a JSON object");
            }

            string[] actual = element.EnumerateObject().Select(property => property.Name).ToArray();
            string[] missing = expected.Except(actual).ToArray();
            string[] unknown = actual.Except(expected).ToArray();
            if (missing.Length != 0 || unknown.Length != 0 || actual.Length != expected.Length)
            {
                throw new Xunit.Sdk.XunitException(
                    context + " field set mismatch. missing=[" + string.Join(",", missing) +
                    "] unknown=[" + string.Join(",", unknown) +
                    "] actual=[" + string.Join(",", actual) + "]");
            }
        }

        private static void RequireNonEmpty(JsonElement element, string name)
        {
            if (string.IsNullOrWhiteSpace(Str(element, name)))
            {
                throw new Xunit.Sdk.XunitException("Empty JSON string " + name);
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
