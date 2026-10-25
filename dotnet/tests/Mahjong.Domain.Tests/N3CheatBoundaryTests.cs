using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Xunit;

namespace Mahjong.Domain.Tests
{
    /// <summary>
    /// N3 residual note obligations (shared ledger §14.1) that are not covered by the
    /// 81-note metadata adjudication:
    ///   B1  mv-swap-drawn-with-wall-backfill
    ///   B8  tm-capacity-tiles
    ///   B8  tm-decision-mismatch (domain context here; actor entry in TableRuntime tests)
    ///   B8  bm-activate-deadwall-empty
    ///
    /// These tests use only existing production APIs.  They do not add a V4-style hole
    /// backfill, a tile-mark group size other than four, a null-round port or a
    /// deadWallTileIdsInDeadWall filter.  Where the V4 input is not representable the
    /// test records the construction/contract rejection instead of inventing reachability.
    /// </summary>
    public class N3CheatBoundaryTests
    {
        private const int WallOffset = 53;

        // Frozen V4 exporter input, V5CheatGoldenExporterTest.kt:1091-1123 and the
        // cheat-v1.json tileMovement case.  Do not derive these from the note text.
        private const int V4DrawnTileId = 33;
        private const int V4DisplacedTileId = 29;
        private const int V4WallTailTileId = 25;
        private const int V4ResolvedIndex = 1;
        private const int V4BackfilledIndex = 0;
        private const int V4WallPointerAfterSwap = 1;

        [Fact]
        public void FrozenFixtureValuesAnchorTheFourCases()
        {
            JsonElement m1b = FrozenCase("tileMovement", "mv-swap-drawn-with-wall-backfill");
            Assert.Equal(V4DrawnTileId, m1b.GetProperty("drawnTileId").GetInt32());
            Assert.Equal(V4DisplacedTileId, m1b.GetProperty("displacedTileId").GetInt32());
            Assert.Equal(V4DisplacedTileId, m1b.GetProperty("backfilledTileId").GetInt32());
            Assert.Equal(V4ResolvedIndex, m1b.GetProperty("resolvedIndex").GetInt32());
            Assert.Equal(V4BackfilledIndex, m1b.GetProperty("backfilledIndex").GetInt32());
            Assert.Equal(0, m1b.GetProperty("wallPointerBeforeDraw").GetInt32());
            Assert.Equal(1, m1b.GetProperty("wallPointerAfterDraw").GetInt32());
            Assert.Equal(V4WallPointerAfterSwap, m1b.GetProperty("wallPointerAfterSwap").GetInt32());
            Assert.Equal(
                "after drawNormal the drawn tile keeps its old wall slot; swapping it backfills that slot with the displaced wall tile",
                m1b.GetProperty("note").GetString());
            Assert.Equal(
                "475d2d8b01b35d71007625463811d377d30b94b17d4b5fc5aafba8d28e7b5144",
                NoteSha(m1b.GetProperty("note").GetString()!));

            JsonElement capacity = FrozenCase("tileMarkHandler", "tm-capacity-tiles");
            Assert.Equal("8 marked + 4 > 12", capacity.GetProperty("note").GetString());
            Assert.Equal("Rejected", capacity.GetProperty("outcome").GetString());
            Assert.Equal("TILE_MARK_CAPACITY_EXCEEDED", capacity.GetProperty("reason").GetString());

            JsonElement decision = FrozenCase("tileMarkHandler", "tm-decision-mismatch");
            Assert.Equal("no active round", decision.GetProperty("note").GetString());
            Assert.Equal("Rejected", decision.GetProperty("outcome").GetString());
            Assert.Equal("DECISION_MISMATCH", decision.GetProperty("reason").GetString());

            JsonElement deadWall = FrozenCase("beamSwapHandler", "bm-activate-deadwall-empty");
            Assert.Equal("everything consumed", deadWall.GetProperty("note").GetString());
            Assert.Equal("Rejected", deadWall.GetProperty("outcome").GetString());
            Assert.Equal("BEAM_SWAP_NO_CANDIDATES", deadWall.GetProperty("reason").GetString());
        }

        // ------------------------------------------------------------------
        // B1: V4 hole backfill vs the canonical C# position map
        // ------------------------------------------------------------------

        [Fact]
        public void Backfill_V4AliasAndCanonicalPositionsAreBidirectionallyEquivalent()
        {
            // V4 exporter input: seat 0 has twelve closed tiles, wallPrefix is
            // [33, 29, 25], and drawNormal draws 33 from V4 slot 0 (C# Wall(53)).
            TilePositionMap initial = BuildTable(
                seat0Hand: new[] { 0, 4, 8, 12, 20, 24, 28, 32, 1, 5, 9, 13 },
                wallPrefix: new[] { V4DrawnTileId, V4DisplacedTileId, V4WallTailTileId },
                deadWallStart: 0,
                deadWallCount: 14,
                replacementCount: 0,
                wallPointer: WallOffset,
                wallEndExclusive: TilePositionMap.LiveWallSlotCount,
                doraSlots: new[] { 4 },
                drawnTile: null);

            Assert.Equal(136, initial.AsPlacements().Count);
            Assert.Equal(136, DistinctPositionCount(initial));
            Assert.Equal(69, initial.CountAt(TileZone.Wall));
            Assert.Equal(WallOffset + 0, initial.WallPointer);
            Assert.Equal(V4DrawnTileId, initial.TileAt(TilePosition.Wall(WallOffset + 0)).Value);
            Assert.Equal(V4DisplacedTileId, initial.TileAt(TilePosition.Wall(WallOffset + 1)).Value);
            Assert.Equal(V4WallTailTileId, initial.TileAt(TilePosition.Wall(WallOffset + 2)).Value);
            CheatTestBench.AssertConserved(initial);

            TransitionResult draw = initial.DrawFromWall(0);
            Assert.True(draw.IsSuccess, draw.Error?.ToString());
            TilePositionMap drawn = draw.RequireState();
            Assert.Equal(V4DrawnTileId, drawn.DrawnTile!.Value.Tile.Value);
            Assert.Equal(DrawSource.Wall, drawn.DrawnTile.Value.Source);
            Assert.Equal(WallOffset + V4WallPointerAfterSwap, drawn.WallPointer);

            // Canonical C# moves the drawn entity into the hand; the old wall slot is
            // empty.  This is the point where V4 materialises a backfill view.
            Assert.Equal(TileZone.Hand, drawn.PositionOf(new TileId(V4DrawnTileId)).Zone);
            Assert.True(drawn.IsPositionFree(TilePosition.Wall(WallOffset + V4BackfilledIndex)));
            Assert.Equal(136, DistinctPositionCount(drawn));
            CheatTestBench.AssertConserved(drawn);

            TransitionResult swappedResult = drawn.SwapHandWithWall(
                0, new TileId(V4DrawnTileId), WallOffset + V4ResolvedIndex);
            Assert.True(swappedResult.IsSuccess, swappedResult.Error?.ToString());
            Assert.Equal(V4DisplacedTileId, swappedResult.ResultTile!.Value.Value);
            TilePositionMap swapped = swappedResult.RequireState();

            // Value-level consumer: drawn identity, resolved slot, displaced entity,
            // the still-untouched wall tail, and the pointer/end pair.
            Assert.Equal(V4DisplacedTileId, swapped.DrawnTile!.Value.Tile.Value);
            Assert.Equal(TilePosition.Wall(WallOffset + V4ResolvedIndex),
                swapped.PositionOf(new TileId(V4DrawnTileId)));
            Assert.Equal(TilePosition.Hand(0, 12), swapped.PositionOf(new TileId(V4DisplacedTileId)));
            Assert.Equal(V4WallTailTileId, swapped.TileAt(TilePosition.Wall(WallOffset + 2)).Value);
            Assert.Equal(WallOffset + V4WallPointerAfterSwap, swapped.WallPointer);
            Assert.Equal(TilePositionMap.LiveWallSlotCount, swapped.WallEndExclusive);
            Assert.Equal(136, DistinctPositionCount(swapped));
            Assert.Equal(68, swapped.CountAt(TileZone.Wall));
            Assert.Equal(1, CountTile(swapped, V4DisplacedTileId));
            CheatTestBench.AssertConserved(swapped);

            // V4 fixture invariant: the exported backfilledTileId is the displaced tile
            // (29) and factsAfter places that one entity in the hand.  C# leaves the old
            // slot empty but the same entity is the drawn hand tile, so no physical tile
            // is duplicated.
            Assert.Equal(V4BackfilledIndex, V4ResolvedIndex - 1);
            Assert.Equal(V4DisplacedTileId, swapped.DrawnTile.Value.Tile.Value);
            Assert.Equal(TileZone.Hand, swapped.PositionOf(new TileId(V4DisplacedTileId)).Zone);
            Assert.True(swapped.IsPositionFree(TilePosition.Wall(WallOffset + V4BackfilledIndex)));

            // Negative with causal meaning: a real V4-style backfill would return 29 at
            // the old slot.  The canonical map must throw there, proving the alias did
            // not become a second physical entity.
            InvalidOperationException noTile = Assert.Throws<InvalidOperationException>(
                () => swapped.TileAt(TilePosition.Wall(WallOffset + V4BackfilledIndex)));
            Assert.Contains("No tile at position", noTile.Message, StringComparison.Ordinal);

            // Bidirectional next-draw equivalence: V4 wall[1] is the drawn tile and its
            // pointer is 1, so the next draw is 33; canonical C# skips the free hole and
            // draws 33 from Wall(54), leaving the tail 25 in place.
            TilePositionMap next = swapped.DrawFromWall(0).RequireState();
            Assert.Equal(V4DrawnTileId, next.DrawnTile!.Value.Tile.Value);
            Assert.Equal(WallOffset + V4ResolvedIndex + 1, next.WallPointer);
            Assert.Equal(V4WallTailTileId, next.TileAt(TilePosition.Wall(WallOffset + 2)).Value);
            Assert.Equal(136, DistinctPositionCount(next));
            CheatTestBench.AssertConserved(next);
        }

        [Fact]
        public void Backfill_MutatedResolvedSlotIsRejectedByTheFixtureValues()
        {
            TilePositionMap initial = BuildTable(
                seat0Hand: new[] { 0, 4, 8, 12, 20, 24, 28, 32, 1, 5, 9, 13 },
                wallPrefix: new[] { V4DrawnTileId, V4DisplacedTileId, V4WallTailTileId },
                deadWallStart: 0,
                deadWallCount: 14,
                replacementCount: 0,
                wallPointer: WallOffset,
                wallEndExclusive: TilePositionMap.LiveWallSlotCount,
                doraSlots: new[] { 4 },
                drawnTile: null);
            TilePositionMap drawn = initial.DrawFromWall(0).RequireState();

            // Mutate only the resolved wall slot: 2 instead of the exported 1.
            TransitionResult mutated = drawn.SwapHandWithWall(0, new TileId(V4DrawnTileId), WallOffset + 2);
            Assert.True(mutated.IsSuccess, mutated.Error?.ToString());
            Assert.Equal(V4WallTailTileId, mutated.ResultTile!.Value.Value);
            Assert.NotEqual(V4DisplacedTileId, mutated.ResultTile.Value.Value);
            Assert.Equal(TilePosition.Wall(WallOffset + 2),
                mutated.State!.PositionOf(new TileId(V4DrawnTileId)));
            Assert.NotEqual(
                TilePosition.Wall(WallOffset + V4ResolvedIndex),
                mutated.State.PositionOf(new TileId(V4DrawnTileId)));
            Assert.True(mutated.State.IsPositionFree(
                TilePosition.Wall(WallOffset + V4BackfilledIndex)));
            CheatTestBench.AssertConserved(mutated.State);
        }

        // ------------------------------------------------------------------
        // B8: tm-capacity-tiles — V4 groups=2 / 12 marked / 4 selected
        // ------------------------------------------------------------------

        [Fact]
        public void TileMark_TwoGroupTwelveMarkedStateCannotBeConstructed()
        {
            // V4's exact state is groups=2 with a 12-tile marked set.  C# TileMarkGroup
            // is exactly four tiles, so a 12-tile group is rejected at construction.
            ArgumentException groupError = Assert.Throws<ArgumentException>(
                () => new TileMarkGroup(
                    "v4-12-tile-group",
                    0,
                    Enumerable.Range(16, 12).Select(value => new TileId(value)).ToArray(),
                    CheatTestBench.RoundId,
                    CheatTestBench.RoundOrdinal));
            Assert.Contains("exactly four tiles", groupError.Message, StringComparison.Ordinal);
            Assert.Equal("tileIds", groupError.ParamName);

            // Two legal C# groups can carry only 8 marked tiles, never the V4 12.
            CheatState twoGroups = WithMarkedGroups(
                CheatTestBench.InitialState(),
                new[] { 16, 17, 18, 19 },
                new[] { 20, 21, 22, 23 });
            Assert.Equal(2, twoGroups.TileMarks.GroupsForSeat(0).Count);
            Assert.Equal(8, twoGroups.TileMarks.MarkedTilesForSeat(0).Count);

            // A third legal group reaches 12; a fourth group is rejected by the
            // three-group cap before any tile-limit check can run.
            CheatState threeGroups = WithMarkedGroups(
                twoGroups,
                new[] { 24, 25, 26, 27 });
            Assert.Equal(3, threeGroups.TileMarks.GroupsForSeat(0).Count);
            Assert.Equal(12, threeGroups.TileMarks.MarkedTilesForSeat(0).Count);

            TileMarkTransition fourth = threeGroups.TileMarks.AddGroup(
                new TileMarkGroup(
                    "v4-fourth-group",
                    0,
                    new[] { new TileId(0), new TileId(4), new TileId(8), new TileId(12) },
                    CheatTestBench.RoundId,
                    CheatTestBench.RoundOrdinal));
            Assert.False(fourth.Success);
            Assert.Equal(TileMarkError.GroupLimitExceeded, fourth.Error);
        }

        [Fact]
        public void TileMark_TwelvePlusFourIsRejectedWithZeroChargeAndNoStateChange()
        {
            CheatState before = WithMarkedGroups(
                CheatTestBench.InitialState(),
                new[] { 16, 17, 18, 19 },
                new[] { 20, 21, 22, 23 },
                new[] { 24, 25, 26, 27 });
            Assert.Equal(12, before.TileMarks.MarkedTilesForSeat(0).Count);
            Assert.Equal(40, before.Ledger.BalanceOf(0));
            Assert.Empty(before.Ledger.Entries);
            long version = before.Version;
            ulong fingerprint = before.Fingerprint;

            TileId[] selected =
            {
                new TileId(0), new TileId(4), new TileId(8), new TileId(12)
            };
            TilePositionMap map = BuildTable(
                seat0Hand: new[] { 0, 4, 8, 12, 32, 36, 40, 44, 48, 1, 5, 9, 13 },
                wallPrefix: Array.Empty<int>(),
                deadWallStart: 0,
                deadWallCount: 14,
                replacementCount: 0,
                wallPointer: WallOffset,
                wallEndExclusive: TilePositionMap.LiveWallSlotCount,
                doraSlots: new[] { 4 },
                drawnTile: null);

            CheatTransition result = CheatSubmission.ApplyTileMark(
                before,
                map,
                CheatTestBench.Context(0, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal),
                CheatTestBench.Economy(),
                0,
                "req-capacity-12-4",
                "g-capacity-12-4",
                selected);

            Assert.False(result.Success);
            Assert.Equal(CheatErrorCode.MarkCapacityExceeded, result.Error);
            Assert.Null(result.State);
            Assert.Null(result.Map);
            Assert.Empty(result.Events);

            // Zero fee / zero state mutation: the rejected transition never exposes the
            // charged intermediate state and the source aggregate is unchanged.
            Assert.Equal(40, before.Ledger.BalanceOf(0));
            Assert.Empty(before.Ledger.Entries);
            Assert.Equal(version, before.Version);
            Assert.Equal(fingerprint, before.Fingerprint);
            Assert.Equal(3, before.TileMarks.GroupsForSeat(0).Count);
            Assert.Equal(12, before.TileMarks.MarkedTilesForSeat(0).Count);
            CheatTestBench.AssertConserved(map);
        }

        [Fact]
        public void TileMark_EightPlusFourIsAllowedAndChargesOnce()
        {
            // The frozen note says "8 marked + 4 > 12", but 8 + 4 == 12.  This positive
            // control proves the old arithmetic must not become an acceptance assertion.
            CheatState before = WithMarkedGroups(
                CheatTestBench.InitialState(),
                new[] { 16, 17, 18, 19 },
                new[] { 20, 21, 22, 23 });
            Assert.Equal(8, before.TileMarks.MarkedTilesForSeat(0).Count);

            TileId[] selected =
            {
                new TileId(0), new TileId(4), new TileId(8), new TileId(12)
            };
            TilePositionMap map = BuildTable(
                seat0Hand: new[] { 0, 4, 8, 12, 32, 36, 40, 44, 48, 1, 5, 9, 13 },
                wallPrefix: Array.Empty<int>(),
                deadWallStart: 0,
                deadWallCount: 14,
                replacementCount: 0,
                wallPointer: WallOffset,
                wallEndExclusive: TilePositionMap.LiveWallSlotCount,
                doraSlots: new[] { 4 },
                drawnTile: null);

            CheatTransition result = CheatSubmission.ApplyTileMark(
                before,
                map,
                CheatTestBench.Context(0, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal),
                CheatTestBench.Economy(),
                0,
                "req-capacity-8-4",
                "g-capacity-8-4",
                selected);

            Assert.True(result.Success, result.Message);
            Assert.NotNull(result.State);
            Assert.Null(result.Map);
            Assert.Equal(2, result.Events.Count);
            Assert.Equal(30, result.State!.Ledger.BalanceOf(0));
            Assert.Single(result.State.Ledger.Entries);
            Assert.Equal(3, result.State.TileMarks.GroupsForSeat(0).Count);
            Assert.Equal(12, result.State.TileMarks.MarkedTilesForSeat(0).Count);
            Assert.Equal(8, before.TileMarks.MarkedTilesForSeat(0).Count);
            Assert.Equal(40, before.Ledger.BalanceOf(0));
        }

        // ------------------------------------------------------------------
        // B8: tm-decision-mismatch — the domain context has no null-round form
        // ------------------------------------------------------------------

        [Fact]
        public void TileMark_NoActiveRoundContextCannotBeConstructed()
        {
            bool[] riichi = { false, false, false, false };

            ArgumentException nullRound = Assert.Throws<ArgumentException>(
                () => new CheatTurnContext(
                    0,
                    RoundPhase.CheatWindow,
                    false,
                    riichi,
                    1,
                    null!,
                    1,
                    SleeveBloomDrawKind.Normal,
                    1_000L));
            Assert.Equal("roundId", nullRound.ParamName);

            ArgumentException blankRound = Assert.Throws<ArgumentException>(
                () => new CheatTurnContext(
                    0,
                    RoundPhase.CheatWindow,
                    false,
                    riichi,
                    1,
                    "   ",
                    1,
                    SleeveBloomDrawKind.Normal,
                    1_000L));
            Assert.Equal("roundId", blankRound.ParamName);
        }

        // ------------------------------------------------------------------
        // B8: bm-activate-deadwall-empty — V4 empty filter vs canonical positions
        // ------------------------------------------------------------------

        [Fact]
        public void BeamSwap_PointerFourCanonicalMapHasCandidatesAndChargesOnce()
        {
            TilePositionMap map = BuildDeadWallPointerFour(doraSlots: Array.Empty<int>());
            Assert.Equal(4, map.RinshanPointer);
            Assert.Equal(4, map.ReplacementCount);
            Assert.Equal(14, map.CountAt(TileZone.DeadWall) + map.CountAt(TileZone.DeadWallReplacement));
            Assert.Equal(14, map.TilesInZone(TileZone.DeadWall).Count +
                map.TilesInZone(TileZone.DeadWallReplacement).Count);

            CheatState before = CheatTestBench.InitialState();
            CheatTransition result = CheatSubmission.ActivateBeamSwap(
                before,
                map,
                CheatTestBench.Context(0, RoundPhase.CheatWindow, SleeveBloomDrawKind.Rinshan),
                CheatTestBench.Economy(),
                CheatTestBench.Sleeve(),
                "req-deadwall-pointer4",
                0);

            Assert.True(result.Success, result.Message);
            Assert.Null(result.Map);
            Assert.Equal(30, result.State!.Ledger.BalanceOf(0));
            Assert.Single(result.State.Ledger.Entries);
            BeamSwapState beam = result.State.BeamSwap;
            Assert.NotNull(beam.Offer);
            Assert.Equal(BeamSwapWindow.DeadWall, beam.Offer!.Window);
            Assert.Equal(14, beam.Offer.Candidates.Count);
            Assert.All(beam.Offer.Candidates, candidate =>
                Assert.True(
                    candidate.Slot.Kind == BeamSwapSlotKind.DeadWallSlot ||
                    candidate.Slot.Kind == BeamSwapSlotKind.ReplacementSlot,
                    candidate.Slot.ToString()));
        }

        [Fact]
        public void BeamSwap_FiveDoraIndicatorsStillLeaveNineCanonicalCandidates()
        {
            // A canonical pointer-4 map has 10 back-half dead-wall tiles plus four
            // replacement tiles.  Five dora indicators remove at most five back-half
            // candidates, leaving nine.  This is not the V4 dora-only filter case; it
            // proves the canonical candidate set cannot be empty.
            TilePositionMap map = BuildDeadWallPointerFour(doraSlots: new[] { 4, 6, 8, 10, 12 });
            Assert.Equal(5, map.DoraIndicators.Count);
            Assert.Equal(14, map.CountAt(TileZone.DeadWall) + map.CountAt(TileZone.DeadWallReplacement));

            CheatTransition result = CheatSubmission.ActivateBeamSwap(
                CheatTestBench.InitialState(),
                map,
                CheatTestBench.Context(0, RoundPhase.CheatWindow, SleeveBloomDrawKind.Rinshan),
                CheatTestBench.Economy(),
                CheatTestBench.Sleeve(),
                "req-deadwall-dora5",
                0);

            Assert.True(result.Success, result.Message);
            Assert.Equal(9, result.State!.BeamSwap.Offer!.Candidates.Count);
            Assert.DoesNotContain(
                result.State.BeamSwap.Offer.Candidates,
                candidate => map.DoraIndicators.Contains(candidate.Tile));
        }

        [Fact]
        public void BeamSwap_ThirteenEffectiveDeadWallTilesAreRejectedAtMapConstruction()
        {
            // V4's empty filter is not a physical map: the 14 dead-wall entities still
            // exist.  A map with only 13 effective dead-wall/replacement entities cannot
            // be constructed, so there is no canonical "empty candidate" map to replay.
            ConservationException error = Assert.Throws<ConservationException>(
                () => BuildDeadWallPointerFourThirteenEffective());
            Assert.Contains("Effective dead wall must contain 14 tiles", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void BeamSwap_WallExhaustedEmptyCandidatesHasZeroCostAndNoOffer()
        {
            // Reachable empty-candidate control, explicitly NOT the deadWallTileIdsInDeadWall
            // source case: a normal draw with no live-wall slot left rejects before charge.
            TilePositionMap map = BuildWallExhaustedNormalDraw();
            Assert.Equal(0, map.WallRemaining);
            Assert.True(map.WallPointer >= map.WallEndExclusive);

            CheatState before = CheatTestBench.InitialState();
            long version = before.Version;
            ulong fingerprint = before.Fingerprint;
            CheatTransition result = CheatSubmission.ActivateBeamSwap(
                before,
                map,
                CheatTestBench.Context(0, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal),
                CheatTestBench.Economy(),
                CheatTestBench.Sleeve(),
                "req-wall-empty-control",
                0);

            Assert.False(result.Success);
            Assert.Equal(CheatErrorCode.NoWallCandidate, result.Error);
            Assert.Contains("No live-wall slot remains", result.Message, StringComparison.Ordinal);
            Assert.Null(result.State);
            Assert.Null(result.Map);
            Assert.Empty(result.Events);
            Assert.Null(before.BeamSwap.Offer);
            Assert.Equal(40, before.Ledger.BalanceOf(0));
            Assert.Empty(before.Ledger.Entries);
            Assert.Equal(version, before.Version);
            Assert.Equal(fingerprint, before.Fingerprint);
            CheatTestBench.AssertConserved(map);
        }

        // ------------------------------------------------------------------
        // Fixture builders
        // ------------------------------------------------------------------

        private static TilePositionMap BuildDeadWallPointerFour(IReadOnlyList<int> doraSlots)
        {
            return BuildTable(
                seat0Hand: new[] { 0, 4, 8, 12, 20, 24, 28, 32, 1, 5, 9, 13, 16, 17 },
                wallPrefix: Array.Empty<int>(),
                deadWallStart: 4,
                deadWallCount: 10,
                replacementCount: 4,
                wallPointer: WallOffset,
                wallEndExclusive: TilePositionMap.LiveWallSlotCount,
                doraSlots: doraSlots,
                drawnTile: new DrawnTileEvidence(new TileId(17), 0, DrawSource.Rinshan));
        }

        private static TilePositionMap BuildDeadWallPointerFourThirteenEffective()
        {
            return BuildTable(
                seat0Hand: new[] { 0, 4, 8, 12, 20, 24, 28, 32, 1, 5, 9, 13, 16, 17 },
                wallPrefix: Array.Empty<int>(),
                deadWallStart: 4,
                deadWallCount: 9,
                replacementCount: 4,
                wallPointer: WallOffset,
                wallEndExclusive: TilePositionMap.LiveWallSlotCount,
                doraSlots: Array.Empty<int>(),
                drawnTile: new DrawnTileEvidence(new TileId(17), 0, DrawSource.Rinshan));
        }

        private static TilePositionMap BuildWallExhaustedNormalDraw()
        {
            return BuildTable(
                seat0Hand: new[] { 0, 4, 8, 12, 20, 24, 28, 32, 1, 5, 9, 13, 16, 17 },
                wallPrefix: Array.Empty<int>(),
                deadWallStart: 0,
                deadWallCount: 14,
                replacementCount: 0,
                wallPointer: TilePositionMap.LiveWallSlotCount,
                wallEndExclusive: TilePositionMap.LiveWallSlotCount,
                doraSlots: new[] { 4 },
                drawnTile: new DrawnTileEvidence(new TileId(17), 0, DrawSource.Wall),
                fillLiveWall: false);
        }

        private static TilePositionMap BuildTable(
            IReadOnlyList<int> seat0Hand,
            IReadOnlyList<int> wallPrefix,
            int deadWallStart,
            int deadWallCount,
            int replacementCount,
            int wallPointer,
            int wallEndExclusive,
            IReadOnlyList<int> doraSlots,
            DrawnTileEvidence? drawnTile,
            bool fillLiveWall = true)
        {
            var used = new bool[TileId.Count];
            var placements = new List<TilePlacement>(TileId.Count);
            var deadWallTiles = new Dictionary<int, int>();

            void Place(int tileValue, TilePosition position)
            {
                if (tileValue < 0 || tileValue >= TileId.Count)
                {
                    throw new InvalidOperationException("Fixture tile is outside 0..135: " + tileValue);
                }

                if (used[tileValue])
                {
                    throw new InvalidOperationException("Fixture duplicates tile " + tileValue);
                }

                used[tileValue] = true;
                placements.Add(new TilePlacement(new TileId(tileValue), position));
            }

            for (int seat = 0; seat < 4; seat++)
            {
                IReadOnlyList<int> hand = seat == 0 ? seat0Hand : Array.Empty<int>();
                for (int slot = 0; slot < hand.Count; slot++)
                {
                    Place(hand[slot], TilePosition.Hand(seat, slot));
                }
            }

            int next = 0;
            int NextUnused()
            {
                while (next < TileId.Count && used[next])
                {
                    next++;
                }

                if (next >= TileId.Count)
                {
                    throw new InvalidOperationException("Fixture exhausted the 136-tile set.");
                }

                int value = next;
                next++;
                return value;
            }

            for (int i = 0; i < wallPrefix.Count; i++)
            {
                Place(wallPrefix[i], TilePosition.Wall(WallOffset + i));
            }

            for (int i = 0; i < deadWallCount; i++)
            {
                int tileValue = NextUnused();
                int index = deadWallStart + i;
                Place(tileValue, TilePosition.DeadWall(index));
                deadWallTiles[index] = tileValue;
            }

            for (int i = 0; i < replacementCount; i++)
            {
                Place(NextUnused(), TilePosition.DeadWallReplacement(i));
            }

            // Fill non-actor hands with 13 deterministic tiles each after the explicit
            // V4 values are reserved, so no filler can steal the exported tile ids.
            for (int seat = 1; seat < 4; seat++)
            {
                for (int slot = 0; slot < 13; slot++)
                {
                    Place(NextUnused(), TilePosition.Hand(seat, slot));
                }
            }

            if (fillLiveWall)
            {
                int wallCount = wallEndExclusive - WallOffset;
                for (int i = wallPrefix.Count; i < wallCount; i++)
                {
                    Place(NextUnused(), TilePosition.Wall(WallOffset + i));
                }
            }

            int discardSlot = 0;
            while (placements.Count < TileId.Count)
            {
                Place(NextUnused(), TilePosition.Discard(0, discardSlot));
                discardSlot++;
            }

            var dora = new List<TileId>(doraSlots.Count);
            for (int i = 0; i < doraSlots.Count; i++)
            {
                if (!deadWallTiles.TryGetValue(doraSlots[i], out int tileValue))
                {
                    throw new InvalidOperationException("Dora slot " + doraSlots[i] + " is not a dead-wall slot.");
                }

                dora.Add(new TileId(tileValue));
            }

            return TilePositionMap.FromPlacements(
                placements,
                wallPointer,
                wallEndExclusive,
                replacementCount,
                doraIndicators: dora,
                drawnTile: drawnTile);
        }

        private static CheatState WithMarkedGroups(CheatState state, params int[][] groups)
        {
            CheatState current = state;
            for (int i = 0; i < groups.Length; i++)
            {
                var group = new TileMarkGroup(
                    "g-n3-" + groups[i][0],
                    0,
                    groups[i].Select(value => new TileId(value)).ToArray(),
                    CheatTestBench.RoundId,
                    CheatTestBench.RoundOrdinal);
                TileMarkTransition transition = current.TileMarks.AddGroup(group);
                Assert.True(transition.Success, transition.Error.ToString());
                current = current.WithTileMarks(transition.State);
            }

            return current;
        }

        private static JsonElement FrozenCase(string section, string caseId)
        {
            string? overridePath = Environment.GetEnvironmentVariable("V5_CHEAT_GOLDEN_PATH");
            string path;
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                path = overridePath!;
            }
            else
            {
                DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
                while (directory != null &&
                       !File.Exists(Path.Combine(directory.FullName, "tests", "v5-golden", "cheat-v1.json")))
                {
                    directory = directory.Parent;
                }

                if (directory == null)
                {
                    throw new InvalidOperationException(
                        "Cannot locate tests/v5-golden/cheat-v1.json from " + AppContext.BaseDirectory + ".");
                }

                path = Path.Combine(directory.FullName, "tests", "v5-golden", "cheat-v1.json");
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (JsonElement item in document.RootElement.GetProperty(section).EnumerateArray())
            {
                if (string.Equals(item.GetProperty("caseId").GetString(), caseId, StringComparison.Ordinal))
                {
                    return item.Clone();
                }
            }

            throw new InvalidOperationException("Fixture case not found: " + section + "/" + caseId + " in " + path);
        }

        private static string NoteSha(string note)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(note));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static int DistinctPositionCount(TilePositionMap map)
        {
            var seen = new HashSet<TilePosition>();
            foreach (TilePlacement placement in map.AsPlacements())
            {
                Assert.True(seen.Add(placement.Position), "duplicate position " + placement.Position);
            }

            return seen.Count;
        }

        private static int CountTile(TilePositionMap map, int tileValue)
        {
            int count = 0;
            foreach (TilePlacement placement in map.AsPlacements())
            {
                if (placement.Tile.Value == tileValue)
                {
                    count++;
                }
            }

            return count;
        }
    }
}
