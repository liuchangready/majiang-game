using System;
using System.Collections.Generic;
using System.Linq;
using Mahjong.Domain.Cheat;
using Xunit;

namespace Mahjong.Domain.Tests
{
    public sealed class FourSeatCharacterTests
    {
        private static string FindRequest(string roundId, int seat, int turnNumber, bool hit)
        {
            for (int i = 0; i < 10_000; i++)
            {
                string request = "m102-dom-" + seat + "-" + turnNumber + "-" + i;
                bool actual = PlayerCharacterRules.GrantsThirdSleeveCandidate(
                    PlayerCharacterIds.Octopus,
                    PlayerCharacterIds.CurrentTraitsVersion,
                    roundId,
                    seat,
                    turnNumber,
                    request);
                if (actual == hit)
                {
                    return request;
                }
            }

            throw new InvalidOperationException("Could not find a deterministic SleeveBloom hit/miss request.");
        }

        private static CheatState FourSeatState(params string[] characterIds) =>
            CheatTestBench.InitialState().WithCharacters(characterIds);

        // M1T-01: four-seat construction, defensive freeze, validation, fingerprint sensitivity & prefix compatibility.
        [Fact]
        public void M1T01_FourSeatCheatState_FreezesCopiesValidatesAndSeparatesFingerprints()
        {
            string[] mutableIds =
            {
                PlayerCharacterIds.Player,
                PlayerCharacterIds.Octopus,
                PlayerCharacterIds.Player,
                PlayerCharacterIds.Player
            };
            int[] mutableVersions = { 1, 1, 1, 1 };

            CheatState state = CheatState.Initialize(CheatTestBench.Economy(), mutableIds, mutableVersions);
            mutableIds[1] = PlayerCharacterIds.Player;
            mutableIds[2] = PlayerCharacterIds.Octopus;
            mutableVersions[0] = 99;

            Assert.Equal(
                new[]
                {
                    PlayerCharacterIds.Player,
                    PlayerCharacterIds.Octopus,
                    PlayerCharacterIds.Player,
                    PlayerCharacterIds.Player
                },
                state.CharacterIds);
            Assert.Equal(new[] { 1, 1, 1, 1 }, state.CharacterTraitsVersions);
            Assert.Equal(PlayerCharacterIds.Player, state.CharacterIdOf(0));
            Assert.Equal(PlayerCharacterIds.Octopus, state.CharacterIdOf(1));

            Assert.Throws<ArgumentException>(() =>
                CheatState.Initialize(CheatTestBench.Economy(), new[] { PlayerCharacterIds.Player, PlayerCharacterIds.Octopus }));
            Assert.Throws<ArgumentException>(() =>
                CheatState.Initialize(CheatTestBench.Economy(), mutableIds, new[] { 1, 1 }));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                CheatState.Initialize(CheatTestBench.Economy(), new[] { "player", "unknown", "player", "player" }));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                CheatState.Initialize(CheatTestBench.Economy(), new[] { "player", "octopus", "player", "player" }, new[] { 1, 2, 1, 1 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => state.CharacterIdOf(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => state.CharacterIdOf(4));
            Assert.Throws<ArgumentOutOfRangeException>(() => state.WithCharacter(4, PlayerCharacterIds.Octopus));

            // Legacy prefix compatibility: seat 0 only matches old single-seat fingerprint.
            CheatState defaultAllPlayer = CheatTestBench.InitialState();
            CheatState explicitAllPlayer = CheatTestBench.InitialState().WithCharacters(
                new[] { "player", "player", "player", "player" },
                new[] { 1, 1, 1, 1 });
            Assert.Equal(defaultAllPlayer.Fingerprint, explicitAllPlayer.Fingerprint);

            CheatState legacySeat0Octopus = CheatTestBench.InitialState().WithPlayerCharacter(PlayerCharacterIds.Octopus);
            CheatState explicitSeat0Octopus = CheatTestBench.InitialState().WithCharacters(
                new[] { "octopus", "player", "player", "player" },
                new[] { 1, 1, 1, 1 });
            Assert.Equal(legacySeat0Octopus.Fingerprint, explicitSeat0Octopus.Fingerprint);

            // Every single seat toggle changes the fingerprint independently, even when another seat is already Octopus.
            var seen = new HashSet<ulong>();
            for (int mask = 0; mask < 16; mask++)
            {
                string[] combo = new string[4];
                for (int seat = 0; seat < 4; seat++)
                {
                    combo[seat] = ((mask >> seat) & 1) == 1 ? PlayerCharacterIds.Octopus : PlayerCharacterIds.Player;
                }

                ulong fp = CheatTestBench.InitialState().WithCharacters(combo).Fingerprint;
                Assert.True(seen.Add(fp), "Duplicate fingerprint for seat character mask " + mask);
            }
        }

        // M1T-01: cross-trick BeamSwapOffer / UsedOnTurn guards refuse immediate SleeveBloom with 0 charge.
        [Fact]
        public void M1T01_ImmediateSleeveBloom_RefusesWhenBeamSwapOfferOrTurnGuardActive()
        {
            TilePositionMap map = CheatTestBench.OpeningAfterNormalDraw(2, out TileId drawn, out _);
            CheatTurnContext context = CheatTestBench.Context(2, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal);
            CheatState baseOctopus = FourSeatState("player", "player", "octopus", "player");

            TileId eligible = map.TilesInZone(TileZone.Hand, 2).First(t => !t.Equals(drawn));
            CheatState withBeamOffer = baseOctopus.WithBeamSwap(baseOctopus.BeamSwap.WithOffer(new BeamSwapOffer(
                "beam-2-active",
                2,
                BeamSwapWindow.Wall,
                new[] { new BeamSwapCandidate(map.TileAt(TilePosition.Wall(map.WallPointer)), BeamSwapSlot.Wall(map.WallPointer)) },
                new[] { eligible },
                drawn,
                context.TurnNumber)));
            CheatTransition blockedOffer = CheatSubmission.ActivateSleeveBloom(
                withBeamOffer, map, context, CheatTestBench.Economy(), CheatTestBench.Sleeve(), 2, "req-beam-offer");
            Assert.False(blockedOffer.Success);
            Assert.Equal(CheatErrorCode.OtherOfferPending, blockedOffer.Error);
            Assert.Null(blockedOffer.State);

            CheatState withBeamUsed = baseOctopus.WithBeamSwap(baseOctopus.BeamSwap.WithUsedOnTurn(context.TurnNumber));
            CheatTransition blockedTurn = CheatSubmission.ActivateSleeveBloom(
                withBeamUsed, map, context, CheatTestBench.Economy(), CheatTestBench.Sleeve(), 2, "req-beam-turn");
            Assert.False(blockedTurn.Success);
            Assert.Equal(CheatErrorCode.AlreadyUsedThisTurn, blockedTurn.Error);
            Assert.Null(blockedTurn.State);
        }

        // M1T-02 + M1T-03: non-zero seats 1/2/3 Octopus on regular normal draw, 0/1/2 candidate boundary & 2-choice Third rejection.
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void M1T02_M1T03_NonZeroSeatOctopus_RegularNormalDraw_RespectsCandidateBoundaries(int seat)
        {
            TilePositionMap map = CheatTestBench.OpeningAfterNormalDraw(seat, out TileId drawn, out TileId second);
            CheatTurnContext context = CheatTestBench.Context(seat, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal);
            string hitRequest = FindRequest(CheatTestBench.RoundId, seat, context.TurnNumber, hit: true);

            string[] ids = { "player", "player", "player", "player" };
            ids[seat] = PlayerCharacterIds.Octopus;
            CheatState octopus = FourSeatState(ids);
            CheatState allPlayer = FourSeatState("player", "player", "player", "player");

            // >= 2 candidates + Octopus hit -> 3 choices, isolated to acting seat's ViewFor, Third moves once.
            CheatTransition hit = CheatSubmission.ActivateSleeveBloom(
                octopus, map, context, CheatTestBench.Economy(), CheatTestBench.Sleeve(), seat, hitRequest);
            Assert.True(hit.Success, hit.Message);
            SleeveBloomOffer offer = hit.State!.SleeveBloom.ImmediateOffer!;
            Assert.Equal(drawn, offer.FirstTile);
            Assert.Equal(second, offer.SecondTile);
            Assert.True(offer.ThirdTile.HasValue);
            Assert.Equal(30, hit.State.Ledger.BalanceOf(seat));
            for (int other = 0; other < 4; other++)
            {
                if (other == seat)
                {
                    Assert.Equal(offer.ThirdTile, hit.State.ViewFor(other).ImmediateOffer!.ThirdTile);
                }
                else
                {
                    Assert.Null(hit.State.ViewFor(other).ImmediateOffer);
                }
            }

            CheatTransition choseThird = CheatSubmission.ChooseSleeveBloom(
                hit.State, map, context, seat, offer.OfferId, SleeveBloomKeep.Third);
            Assert.True(choseThird.Success, choseThird.Message);
            Assert.Equal(offer.ThirdTile.Value, choseThird.Map!.DrawnTile!.Value.Tile);
            Assert.Equal(30, choseThird.State!.Ledger.BalanceOf(seat));
            Assert.Single(choseThird.State.Ledger.Entries);
            CheatTestBench.AssertConserved(choseThird.Map!);

            // Same seat as "player" with same hit request -> 2-choice offer, Third rejected zero side effects.
            CheatTransition playerAct = CheatSubmission.ActivateSleeveBloom(
                allPlayer, map, context, CheatTestBench.Economy(), CheatTestBench.Sleeve(), seat, hitRequest);
            Assert.True(playerAct.Success);
            SleeveBloomOffer playerOffer = playerAct.State!.SleeveBloom.ImmediateOffer!;
            Assert.Null(playerOffer.ThirdTile);
            CheatTransition rejectPlayerThird = CheatSubmission.ChooseSleeveBloom(
                playerAct.State, map, context, seat, playerOffer.OfferId, SleeveBloomKeep.Third);
            Assert.False(rejectPlayerThird.Success);
            Assert.Equal(CheatErrorCode.InvalidSelection, rejectPlayerThird.Error);
            Assert.Null(rejectPlayerThird.State);
            Assert.Null(rejectPlayerThird.Map);
            Assert.Equal(playerOffer.OfferId, playerAct.State.SleeveBloom.ImmediateOffer!.OfferId);

            // available.Count == 1 -> succeeds with 2-choice offer (ThirdTile == null) even for Octopus hit, then rejects Third.
            TilePositionMap exactOne = TilePositionMap.CreateDealt(
                TestTables.Crafted(
                    (53, TileId.FromTypeIndex(4, 0)),
                    (54, TileId.FromTypeIndex(5, 0)),
                    (55, TileId.FromTypeIndex(6, 0))),
                seat);
            while (exactOne.WallRemaining > 1)
            {
                int cur = exactOne.DrawnTile!.Value.Seat;
                TileId d = exactOne.DrawnTile.Value.Tile;
                exactOne = exactOne.Discard(cur, d).RequireState();
                exactOne = exactOne.DrawFromWall((cur + 1) % 4).RequireState();
            }
            Assert.Equal(seat, exactOne.DrawnTile!.Value.Seat);
            Assert.Equal(1, exactOne.WallRemaining);

            CheatTurnContext oneCtx = CheatTestBench.Context(seat, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal, turn: 5);
            string oneHit = FindRequest(CheatTestBench.RoundId, seat, oneCtx.TurnNumber, hit: true);
            CheatTransition oneAct = CheatSubmission.ActivateSleeveBloom(
                octopus, exactOne, oneCtx, CheatTestBench.Economy(), CheatTestBench.Sleeve(), seat, oneHit);
            Assert.True(oneAct.Success, oneAct.Message);
            SleeveBloomOffer oneOffer = oneAct.State!.SleeveBloom.ImmediateOffer!;
            Assert.Null(oneOffer.ThirdTile);
            Assert.Equal(30, oneAct.State.Ledger.BalanceOf(seat));

            CheatTransition rejectOneThird = CheatSubmission.ChooseSleeveBloom(
                oneAct.State, exactOne, oneCtx, seat, oneOffer.OfferId, SleeveBloomKeep.Third);
            Assert.False(rejectOneThird.Success);
            Assert.Equal(CheatErrorCode.InvalidSelection, rejectOneThird.Error);
            Assert.Null(rejectOneThird.State);

            // available.Count == 0 -> rejected NoWallCandidate with 0 charge on target seat.
            TilePositionMap zeroWall = TilePositionMap.CreateDealt(
                TestTables.Crafted(
                    (53, TileId.FromTypeIndex(4, 0)),
                    (54, TileId.FromTypeIndex(5, 0)),
                    (55, TileId.FromTypeIndex(6, 0))),
                (seat + 3) % 4);
            while (zeroWall.WallRemaining > 0)
            {
                int cur = zeroWall.DrawnTile!.Value.Seat;
                TileId d = zeroWall.DrawnTile.Value.Tile;
                zeroWall = zeroWall.Discard(cur, d).RequireState();
                zeroWall = zeroWall.DrawFromWall((cur + 1) % 4).RequireState();
            }
            Assert.Equal(seat, zeroWall.DrawnTile!.Value.Seat);
            Assert.Equal(0, zeroWall.WallRemaining);
            CheatTurnContext zeroCtx = CheatTestBench.Context(seat, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal, turn: 6);
            CheatTransition zeroAct = CheatSubmission.ActivateSleeveBloom(
                octopus, zeroWall, zeroCtx, CheatTestBench.Economy(), CheatTestBench.Sleeve(), seat,
                FindRequest(CheatTestBench.RoundId, seat, zeroCtx.TurnNumber, hit: true));
            Assert.False(zeroAct.Success);
            Assert.Equal(CheatErrorCode.NoWallCandidate, zeroAct.Error);
            Assert.Null(zeroAct.State);
            Assert.Equal(40, octopus.Ledger.BalanceOf(seat));
        }

        // M1T-02 (O-01 + pure Domain confirmedDraw): regular non-confirmed rinshan never produces Third;
        // pure Domain confirmedDraw=true on seats 1..3 evaluates state.CharacterIdOf(seat).
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void M1T02_RinshanO01AndPureDomainConfirmedDraw_FollowSeatCharacter(int seat)
        {
            TilePositionMap map = CheatTestBench.OpeningAfterRinshanDraw(seat, out _, out _);
            CheatTurnContext ctx = CheatTestBench.Context(seat, RoundPhase.CheatWindow, SleeveBloomDrawKind.Rinshan);
            string[] ids = { "player", "player", "player", "player" };
            ids[seat] = PlayerCharacterIds.Octopus;
            CheatState octopus = FourSeatState(ids);

            // O-01: non-confirmed rinshan does NOT grant ThirdTile even for Octopus.
            CheatTransition nonConfirmed = CheatSubmission.ActivateSleeveBloomWithFrozenInput(
                octopus,
                map,
                ctx,
                CheatTestBench.Economy(),
                CheatTestBench.Sleeve(),
                seat,
                FindRequest(CheatTestBench.RoundId, seat, ctx.TurnNumber, hit: true),
                SleeveBloomActivationInput.Unspecified,
                protectOffTurnThird: true,
                confirmedDraw: false);
            Assert.True(nonConfirmed.Success, nonConfirmed.Message);
            Assert.Null(nonConfirmed.State!.SleeveBloom.ImmediateOffer!.ThirdTile);

            // Pure Domain confirmedDraw=true uses StateHash and grants ThirdTile on Octopus hit for seat 1..3.
            TilePositionMap? hitMap = null;
            for (int seed = 0; seed < 200; seed++)
            {
                int[] order = Enumerable.Range(0, TileId.Count).ToArray();
                var rng = new Random(seed);
                for (int i = order.Length - 1; i > 4; i--)
                {
                    int j = rng.Next(4, i + 1);
                    (order[i], order[j]) = (order[j], order[i]);
                }

                TilePositionMap candidate = TilePositionMap.CreateDealt(
                    RecordedRandomDraw.Create("dom-conf-" + seed, order.Select(id => new TileId(id)).ToArray()),
                    0);
                candidate = candidate.Discard(0, candidate.DrawnTile!.Value.Tile).RequireState();
                candidate = candidate.DrawReplacement(seat).RequireState();
                if (PlayerCharacterRules.GrantsThirdSleeveCandidate(
                    PlayerCharacterIds.Octopus,
                    PlayerCharacterIds.CurrentTraitsVersion,
                    ctx.RoundId,
                    seat,
                    ctx.TurnNumber,
                    TileStateHash.Format(candidate.StateHash)))
                {
                    hitMap = candidate;
                    break;
                }
            }

            Assert.NotNull(hitMap);
            CheatTransition confirmed = CheatSubmission.ActivateSleeveBloomWithFrozenInput(
                octopus,
                hitMap!,
                ctx,
                CheatTestBench.Economy(),
                CheatTestBench.Sleeve(),
                seat,
                "any-request-ignored-by-confirmed",
                SleeveBloomActivationInput.Unspecified,
                protectOffTurnThird: true,
                confirmedDraw: true);
            Assert.True(confirmed.Success, confirmed.Message);
            Assert.True(confirmed.State!.SleeveBloom.ImmediateOffer!.ThirdTile.HasValue);

            CheatState player = FourSeatState("player", "player", "player", "player");
            CheatTransition confirmedPlayer = CheatSubmission.ActivateSleeveBloomWithFrozenInput(
                player,
                hitMap!,
                ctx,
                CheatTestBench.Economy(),
                CheatTestBench.Sleeve(),
                seat,
                "any-request-ignored-by-confirmed",
                SleeveBloomActivationInput.Unspecified,
                protectOffTurnThird: true,
                confirmedDraw: true);
            Assert.True(confirmedPlayer.Success);
            Assert.Null(confirmedPlayer.State!.SleeveBloom.ImmediateOffer!.ThirdTile);
        }

        // M1T-02 + M1T-03: off-turn activation & rejection semantics for non-zero seats.
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void M1T02_M1T03_NonZeroSeatOffTurn_GrantsThirdOnOctopusAndKeepsGoldenRejections(int seat)
        {
            TilePositionMap map = CheatTestBench.CraftedOpening();
            string[] ids = { "player", "player", "player", "player" };
            ids[seat] = PlayerCharacterIds.Octopus;
            CheatState octopus = FourSeatState(ids);
            octopus = octopus.WithSleeveBloom(octopus.SleeveBloom.WithOpenedWindow(seat, 1, out string windowId));

            int otherSeat = (seat + 1) % 4;
            CheatTurnContext context = CheatTestBench.Context(otherSeat, RoundPhase.Discard, SleeveBloomDrawKind.DealExtra, now: 1_000);
            string hitRequest = FindRequest(CheatTestBench.RoundId, seat, context.TurnNumber, hit: true);

            CheatTransition activated = CheatSubmission.ActivateOffTurnSleeveBloom(
                octopus, map, context, CheatTestBench.Economy(), CheatTestBench.Sleeve(), seat, hitRequest, windowId);
            Assert.True(activated.Success, activated.Message);
            SleeveBloomOffer offer = activated.State!.SleeveBloom.OffTurnOfferFor(seat)!;
            Assert.True(offer.ThirdTile.HasValue);

            // Stale drift before deadline: plain rejection preserves pending offer.
            TilePositionMap drifted = map.Discard(0, map.DrawnTile!.Value.Tile).RequireState().DrawFromWall(1).RequireState();
            CheatTransition stale = CheatSubmission.ChooseOffTurnSleeveBloom(
                activated.State, drifted, context, seat, offer.OfferId, SleeveBloomKeep.Third);
            Assert.False(stale.Success);
            Assert.Equal(CheatErrorCode.StaleOffer, stale.Error);
            Assert.Null(stale.State);

            // Expired choice: RejectedWithState settles First reservation without extra charge.
            CheatTurnContext expiredCtx = CheatTestBench.Context(
                otherSeat, RoundPhase.Discard, SleeveBloomDrawKind.DealExtra, now: offer.DeadlineMillis);
            CheatTransition expired = CheatSubmission.ChooseOffTurnSleeveBloom(
                activated.State, map, expiredCtx, seat, offer.OfferId, SleeveBloomKeep.Third);
            Assert.False(expired.Success);
            Assert.Equal(CheatErrorCode.DeadlineExpired, expired.Error);
            Assert.NotNull(expired.State);
            Assert.Equal(offer.FirstTile, expired.State!.SleeveBloom.ReservationFor(seat)!.Tile);
            Assert.Equal(30, expired.State.Ledger.BalanceOf(seat));
        }
    }
}
