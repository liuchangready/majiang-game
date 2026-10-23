using System;
using System.Collections.Generic;
using System.Reflection;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Xunit;

namespace Mahjong.Domain.Tests
{
    /// <summary>V5-0-VISIBILITY-MATRIX §2/§6: private cheat facts leave the state only through ViewFor(seat).</summary>
    public class CheatVisibilityTests
    {
        private static readonly TileId T0 = new TileId(0);
        private static readonly TileId T1 = new TileId(1);
        private static readonly TileId T2 = new TileId(2);
        private static readonly TileId T3 = new TileId(3);
        private static readonly TileId T4 = new TileId(4);
        private static readonly TileId T5 = new TileId(5);
        private static readonly TileId T6 = new TileId(6);
        private static readonly TileId T7 = new TileId(7);
        private static readonly TileId T8 = new TileId(8);
        private static readonly TileId T9 = new TileId(9);
        private static readonly TileId T10 = new TileId(10);
        private static readonly TileId T11 = new TileId(11);
        private static readonly TileId T12 = new TileId(12);

        /// <summary>
        /// A state where every seat owns a different private cheat fact. The optional
        /// arguments let the fingerprint test re-build the same shape with one source field
        /// changed at a time; defaults keep the original fixtures.
        /// </summary>
        private static CheatState RichState(
            string markGroupId = "g1",
            string immediateOfferId = "bloom-1-x",
            string? immediateWindowId = null,
            long immediateCreatedAt = 0,
            long immediateDeadline = 0,
            string offTurnOfferId = "bloom-offturn-2-y",
            string offTurnWindowId = "bloom-window-2-1-0",
            long offTurnDeadline = 11_000,
            long reservationCreatedAt = 0,
            string? reservationWindow = null,
            string beamOfferId = "beam-0-x",
            TileId? beamEligibleHand = null)
        {
            CheatState state = CheatTestBench.InitialState();

            TileMarkTransition marks = state.TileMarks.AddGroup(
                new TileMarkGroup(markGroupId, 1, new[] { T0, T1, T2, T3 }, CheatTestBench.RoundId, CheatTestBench.RoundOrdinal));
            Assert.True(marks.Success, marks.Error.ToString());
            state = state.WithTileMarks(marks.State);

            SleeveBloomState sleeve = state.SleeveBloom
                .WithImmediateOffer(new SleeveBloomOffer(
                    immediateOfferId, 1, SleeveBloomDrawKind.Normal, T4, T5, 1,
                    SleeveBloomOfferMode.Immediate, immediateWindowId, immediateDeadline, immediateCreatedAt))
                .WithOpenedWindow(2, 1, out string window2)
                .WithOffTurnOffer(new SleeveBloomOffer(
                    offTurnOfferId, 2, SleeveBloomDrawKind.Normal, T6, T7, 1,
                    SleeveBloomOfferMode.OffTurn, offTurnWindowId, offTurnDeadline, 1_000))
                .WithReservation(new SleeveBloomReservation(3, "bloom-offturn-3-z", T8, reservationWindow, reservationCreatedAt));
            state = state.WithSleeveBloom(sleeve);

            BeamSwapState beam = state.BeamSwap.WithOffer(new BeamSwapOffer(
                beamOfferId, 0, BeamSwapWindow.Wall,
                new[] { new BeamSwapCandidate(T9, BeamSwapSlot.Wall(54)) },
                new[] { beamEligibleHand ?? T10 },
                T11,
                1));
            return state.WithBeamSwap(beam);
        }

        [Fact]
        public void ViewFor_ExposesOnlyTheOwnerFacts()
        {
            CheatState state = RichState();

            CheatPrivateView seat0 = state.ViewFor(0);
            Assert.Equal(40, seat0.Balance);
            Assert.Empty(seat0.Entries);
            Assert.Empty(seat0.MarkGroups);
            Assert.Null(seat0.ImmediateOffer);
            Assert.Null(seat0.OffTurnOffer);
            Assert.Null(seat0.Reservation);
            Assert.Null(seat0.WindowId);
            Assert.NotNull(seat0.BeamSwapOffer);
            Assert.Equal("beam-0-x", seat0.BeamSwapOffer.OfferId);

            CheatPrivateView seat1 = state.ViewFor(1);
            Assert.Equal("g1", seat1.MarkGroups[0].GroupId);
            Assert.Equal(4, seat1.MarkGroups[0].TileIds.Count);
            Assert.Equal("bloom-1-x", seat1.ImmediateOffer!.OfferId);
            Assert.Null(seat1.BeamSwapOffer);
            Assert.Null(seat1.Reservation);

            CheatPrivateView seat2 = state.ViewFor(2);
            Assert.Equal("bloom-offturn-2-y", seat2.OffTurnOffer!.OfferId);
            Assert.Equal("bloom-window-2-1-0", seat2.WindowId);
            Assert.Null(seat2.ImmediateOffer);
            Assert.Empty(seat2.MarkGroups);

            CheatPrivateView seat3 = state.ViewFor(3);
            Assert.NotNull(seat3.Reservation);
            Assert.True(seat3.Reservation.Tile.Equals(T8));
            Assert.Null(seat3.WindowId);
            Assert.Null(seat3.OffTurnOffer);
        }

        [Fact]
        public void LedgerEntries_AreVisibleOnlyToTheirSeat()
        {
            CheatState state = CheatTestBench.InitialState();
            state = state.WithLedger(
                state.Ledger.Apply("a-0", 0, -10, "TILE_MARK").Ledger!
                    .Apply("a-2", 2, -5, "TILE_MARK").Ledger!);

            Assert.Single(state.ViewFor(0).Entries);
            Assert.Equal("a-0", state.ViewFor(0).Entries[0].EntryId);
            Assert.Single(state.ViewFor(2).Entries);
            Assert.Equal("a-2", state.ViewFor(2).Entries[0].EntryId);
            Assert.Empty(state.ViewFor(1).Entries);
            Assert.Empty(state.ViewFor(3).Entries);
            Assert.Equal(30, state.ViewFor(0).Balance);
            Assert.Equal(35, state.ViewFor(2).Balance);
        }

        [Fact]
        public void PublicSnapshot_CarriesNoCheatFactsAtAll()
        {
            CheatPublicSnapshot snapshot = RichState().PublicSnapshot;

            Assert.Empty(typeof(CheatPublicSnapshot).GetProperties(BindingFlags.Public | BindingFlags.Instance));
            Assert.Empty(typeof(CheatPublicSnapshot).GetFields(BindingFlags.Public | BindingFlags.Instance));
            Assert.NotNull(snapshot);
        }

        [Fact]
        public void Rejections_NeverCarryStateOrMap_AcrossSubsystems()
        {
            TilePositionMap map = CheatTestBench.CraftedOpening();
            CheatTurnContext context = CheatTestBench.Context(0, RoundPhase.CheatWindow, SleeveBloomDrawKind.DealExtra);
            CheatEconomyConfig economy = CheatTestBench.Economy();
            CheatState state = CheatTestBench.InitialState();
            var rejections = new List<CheatTransition>
            {
                // tile mark: every requested tile belongs to other seats' hands
                CheatSubmission.ApplyTileMark(
                    state, map, context, economy, 0, "r", "g",
                    new[]
                    {
                        map.TilesInZone(TileZone.Hand, 1)[0],
                        map.TilesInZone(TileZone.Hand, 2)[0],
                        map.TilesInZone(TileZone.Hand, 3)[0],
                        map.TilesInZone(TileZone.Hand, 1)[1]
                    }),
                // immediate sleeve: wrong seat
                CheatSubmission.ActivateSleeveBloom(state, map, context, economy, CheatTestBench.Sleeve(), 1, "r"),
                // off-turn sleeve: no window issued
                CheatSubmission.ActivateOffTurnSleeveBloom(
                    state, map, CheatTestBench.Context(1, RoundPhase.Draw, SleeveBloomDrawKind.DealExtra),
                    economy, CheatTestBench.Sleeve(), 1, "r", "bloom-window-1-1-0"),
                // beam swap: riichi locked
                CheatSubmission.ActivateBeamSwap(
                    state, map,
                    CheatTestBench.Context(0, RoundPhase.CheatWindow, SleeveBloomDrawKind.DealExtra, riichi: new[] { true, false, false, false }),
                    economy, CheatTestBench.Sleeve(), "r", 0),
                // delivery: no reservation
                CheatSubmission.DeliverSleeveBloomReservation(state, map, context, 0)
            };

            foreach (CheatTransition rejection in rejections)
            {
                Assert.False(rejection.Success);
                Assert.Null(rejection.State);
                Assert.Null(rejection.Map);
                Assert.Equal(40, state.Ledger.BalanceOf(0)); // the source state is never mutated anyway
            }
        }

        [Fact]
        public void VersionAndFingerprint_MoveOnlyWithContent()
        {
            CheatState state = CheatTestBench.InitialState();
            CheatState marked = state.WithTileMarks(state.TileMarks.AddGroup(
                new TileMarkGroup("g1", 1, new[] { T0, T1, T2, T3 }, CheatTestBench.RoundId, CheatTestBench.RoundOrdinal)).State);

            Assert.Equal(state.Version + 1, marked.Version);
            Assert.NotEqual(state.Fingerprint, marked.Fingerprint);

            // re-wrapping identical sub-states keeps the fingerprint stable
            Assert.Equal(marked.Fingerprint, marked.WithSleeveBloom(marked.SleeveBloom).Fingerprint);

            // a rejected change produces neither state nor map
            CheatTransition rejected = CheatSubmission.ApplyTileMark(
                marked, CheatTestBench.CraftedOpening(),
                CheatTestBench.Context(0, RoundPhase.CheatWindow, SleeveBloomDrawKind.DealExtra),
                CheatTestBench.Economy(), 0, "r", "g2", new[] { T1, T2, T3, T4 });
            Assert.False(rejected.Success);
            Assert.Null(rejected.State);
            Assert.Null(rejected.Map);
        }

        [Fact]
        public void SubStateGenerations_AdvanceOnSleeveAndBeamChangesWithoutLedgerOrMarks()
        {
            CheatState state = CheatTestBench.InitialState();
            long start = state.Version; // ledger 0 + marks 1 + sleeve 0 + beam 0

            // no-op re-wraps keep the aggregate generation stable
            Assert.Equal(start, state.WithSleeveBloom(state.SleeveBloom).Version);
            Assert.Equal(start, state.WithBeamSwap(state.BeamSwap).Version);

            SleeveBloomState sleeve = state.SleeveBloom;
            Assert.Equal(0, sleeve.Version);
            sleeve = sleeve.WithUsedOnTurn(1); // used-this-turn marker alone is a change
            Assert.Equal(1, sleeve.Version);
            Assert.Same(sleeve, sleeve.WithUsedOnTurn(1)); // same content replays un-versioned
            sleeve = sleeve.WithOpenedWindow(2, 1, out string window2); // window issuance
            Assert.Equal(2, sleeve.Version);
            sleeve = sleeve.WithOffTurnOffer(new SleeveBloomOffer(
                "bloom-offturn-2-x", 2, SleeveBloomDrawKind.Normal, T4, T5, 1,
                SleeveBloomOfferMode.OffTurn, window2, 11_000, 1_000));
            Assert.Equal(3, sleeve.Version);
            sleeve = sleeve.WithReservation(new SleeveBloomReservation(3, "bloom-offturn-3-x", T6, null, 0));
            Assert.Equal(4, sleeve.Version);
            Assert.True(sleeve.TryResolveOffTurnOffer(2, SleeveBloomKeep.First, out SleeveBloomState resolved, out _));
            Assert.Equal(5, resolved.Version); // choice converts the offer in one generation step
            SleeveBloomState cleared = resolved.Cleared(); // end-of-round expiry sweep
            Assert.Equal(6, cleared.Version);
            Assert.Same(cleared, cleared.Cleared());
            Assert.Equal(start + 6, state.WithSleeveBloom(cleared).Version);

            BeamSwapState beam = state.BeamSwap;
            Assert.Equal(0, beam.Version);
            var offer = new BeamSwapOffer(
                "beam-0-x", 0, BeamSwapWindow.Wall,
                new[] { new BeamSwapCandidate(T7, BeamSwapSlot.Wall(54)) },
                new[] { T8 }, T9, 1);
            beam = beam.WithOffer(offer);
            Assert.Equal(1, beam.Version);
            Assert.Same(beam, beam.WithOffer(offer)); // identical offer replays un-versioned
            beam = beam.WithoutOffer(); // keep-original/expiry resolution
            Assert.Equal(2, beam.Version);
            Assert.Same(beam, beam.WithoutOffer());
            // the fresh aggregate only carries the beam delta (+2); its sleeve is still the original
            Assert.Equal(start + 2, state.WithBeamSwap(beam).Version);
            Assert.Equal(start + 6 + 2, state.WithSleeveBloom(cleared).WithBeamSwap(beam).Version);
        }

        [Fact]
        public void CheatStateVersion_AdvancesThroughSubmissionsWithoutLedgerOrMarkChanges()
        {
            TilePositionMap map = CheatTestBench.OpeningAfterNormalDraw(1, out _, out _);
            CheatEconomyConfig economy = CheatTestBench.Economy();

            // immediate sleeve choice: only the sleeve generation moves
            CheatState activated = CheatSubmission.ActivateSleeveBloom(
                CheatTestBench.InitialState(), map,
                CheatTestBench.Context(1, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal),
                economy, CheatTestBench.Sleeve(), 1, "req-1").State!;
            CheatTransition chosen = CheatSubmission.ChooseSleeveBloom(
                activated, map,
                CheatTestBench.Context(1, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal),
                1, "bloom-1-req-1", SleeveBloomKeep.First);
            Assert.True(chosen.Success, chosen.Message);
            Assert.Equal(activated.Ledger, chosen.State!.Ledger); // no further charge
            Assert.Equal(activated.TileMarks, chosen.State.TileMarks);
            Assert.Equal(activated.Version + 1, chosen.State.Version);

            // beam expiry: only the beam generation moves
            CheatState beamed = CheatSubmission.ActivateBeamSwap(
                chosen.State, map,
                CheatTestBench.Context(1, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal, turn: 2),
                economy, CheatTestBench.Sleeve(), "req-2", 1).State!;
            CheatTransition expired = CheatSubmission.ExpireBeamSwap(
                beamed,
                CheatTestBench.Context(1, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal, turn: 2), 1);
            Assert.True(expired.Success);
            Assert.Equal(beamed.Ledger, expired.State!.Ledger);
            Assert.Equal(beamed.Version + 1, expired.State.Version);
        }

        [Fact]
        public void Fingerprint_MovesWithEveryKeySourceField()
        {
            CheatState rich = RichState();
            ulong baseline = rich.Fingerprint;

            // re-wrapping identical content keeps the fingerprint stable
            Assert.Equal(baseline, rich.WithSleeveBloom(rich.SleeveBloom).Fingerprint);
            Assert.Equal(baseline, rich.WithBeamSwap(rich.BeamSwap).Fingerprint);

            // ledger: entry id and reason are part of the source content
            Assert.NotEqual(baseline, RichState().WithLedger(
                RichState().Ledger.Apply("a-0-other", 0, -10, "TILE_MARK").Ledger!).Fingerprint);
            Assert.NotEqual(baseline, RichState().WithLedger(
                RichState().Ledger.Apply("a-0", 0, -10, "SLEEVE_BLOOM").Ledger!).Fingerprint);

            // marks: group id and creation round identity
            Assert.NotEqual(baseline, RichState(markGroupId: "g1-other").Fingerprint);

            // sleeve offers: ids, window ids and timestamps
            Assert.NotEqual(baseline, RichState(immediateOfferId: "bloom-1-other").Fingerprint);
            Assert.NotEqual(baseline, RichState(offTurnOfferId: "bloom-offturn-2-other").Fingerprint);
            Assert.NotEqual(baseline, RichState(offTurnWindowId: "bloom-window-2-9-9").Fingerprint);
            Assert.NotEqual(baseline, RichState(offTurnDeadline: 12_000).Fingerprint);
            Assert.NotEqual(baseline, RichState(reservationCreatedAt: 1).Fingerprint);
            Assert.NotEqual(baseline, RichState(reservationWindow: "bloom-window-3-1-1").Fingerprint);

            // beam offer: id and the frozen eligible hand set
            Assert.NotEqual(baseline, RichState(beamOfferId: "beam-0-other").Fingerprint);
            Assert.NotEqual(baseline, RichState(beamEligibleHand: T12).Fingerprint);

            // a per-seat window id disappearing is a source change too
            CheatState windowCleared = rich.WithSleeveBloom(rich.SleeveBloom.WithClearedWindow(2));
            Assert.NotEqual(baseline, windowCleared.Fingerprint);
        }

        [Fact]
        public void Fingerprint_MovesWithRoundRegistries()
        {
            // Round-3 复现探针：只换当前局 ID（发牌注册表内容），指纹必须跟随；
            // 两态代次数相同，排除「Version 代内容」的可能。裸态直接注册（无前一局）。
            CheatState plain = CheatState.Initialize(CheatTestBench.Economy());
            CheatState dealtA = plain.WithTileMarks(plain.TileMarks.RegisterDealtRound("round-A").State);
            CheatState dealtB = plain.WithTileMarks(plain.TileMarks.RegisterDealtRound("round-B").State);
            Assert.Equal(dealtA.Version, dealtB.Version);
            Assert.NotEqual(dealtA.Fingerprint, dealtB.Fingerprint);

            // 结算历史是另一个源字段：结算当前局后指纹移动
            CheatState settledA = dealtA.WithTileMarks(dealtA.TileMarks.CompleteRound("round-A").State);
            Assert.NotEqual(dealtA.Fingerprint, settledA.Fingerprint);

            // 更长的结算历史继续移动指纹
            CheatState extended = settledA.WithTileMarks(settledA.TileMarks.RegisterDealtRound("round-C").State);
            CheatState settledTwice = extended.WithTileMarks(extended.TileMarks.CompleteRound("round-C").State);
            Assert.NotEqual(settledA.Fingerprint, settledTwice.Fingerprint);
            Assert.NotEqual(extended.Fingerprint, settledTwice.Fingerprint);

            // 同内容重包裹（含注册表）保持稳定
            Assert.Equal(settledTwice.Fingerprint, settledTwice.WithTileMarks(settledTwice.TileMarks).Fingerprint);
            Assert.Equal(settledTwice.Fingerprint, settledTwice.WithSleeveBloom(settledTwice.SleeveBloom).Fingerprint);
        }

        [Fact]
        public void Fingerprint_MovesWithImmediateOfferMetadata()
        {
            // Round-4 探针复现：Immediate 构造器接受 window/timestamp 元数据且 Equals
            // 覆盖它们，指纹必须跟随每个字段（此前 only (A,10,1)/(B,20,2) 同指纹）。
            CheatState baseline = RichState();

            // 逐字段差异
            Assert.NotEqual(baseline.Fingerprint, RichState(immediateWindowId: "bloom-window-1-1-0").Fingerprint);
            Assert.NotEqual(baseline.Fingerprint, RichState(immediateCreatedAt: 2_000).Fingerprint);
            Assert.NotEqual(baseline.Fingerprint, RichState(immediateDeadline: 20_000).Fingerprint);

            // 三字段同变的探针对：代次数相同、指纹分叉
            CheatState a = RichState(immediateWindowId: "A", immediateDeadline: 10, immediateCreatedAt: 1);
            CheatState b = RichState(immediateWindowId: "B", immediateDeadline: 20, immediateCreatedAt: 2);
            Assert.Equal(a.Version, b.Version);
            Assert.NotEqual(a.Fingerprint, b.Fingerprint);

            // 同内容重包裹稳定
            Assert.Equal(a.Fingerprint, a.WithSleeveBloom(a.SleeveBloom).Fingerprint);
            Assert.Equal(a.Fingerprint, a.WithBeamSwap(a.BeamSwap).Fingerprint);
        }
    }
}
