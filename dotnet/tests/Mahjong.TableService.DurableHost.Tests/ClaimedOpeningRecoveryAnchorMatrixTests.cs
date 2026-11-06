using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Mahjong.TableRuntime;
using Mahjong.TableService;
using Mahjong.TableService.Tests;
using Xunit;

namespace Mahjong.TableService.DurableHost.Tests
{
    /// <summary>
    /// D-R2-01, pure-domain matrix: a legal chi/pon/open-kan may remove the deal-extra
    /// seat's opening discard from the current river. Every such checkpoint must still
    /// recover with the exact persisted tsumogiri truth (opening tsumogiri AND opening hand
    /// discard), for an empty river and for a river with later discards, from BOTH:
    ///  - checkpoint-0 + committed event tail (the pending deal-extra evidence), and
    ///  - a checkpoint captured after the claim (the persisted event fact).
    /// The negative half tampers the persisted provenance and must fail closed: no guessed
    /// value is ever accepted.
    /// </summary>
    public sealed class ClaimedOpeningRecoveryAnchorMatrixTests
    {
        private const string Room = "p54b-claim-matrix";

        public static IEnumerable<object[]> PositiveCases()
        {
            foreach (string claim in new[] { "Chi", "Pon", "OpenKan" })
            {
                foreach (string opening in new[] { "Tsumogiri", "Hand" })
                {
                    foreach (string river in new[] { "Empty", "Later" })
                    {
                        yield return new object[] { claim, opening, river, "checkpoint0+tail" };
                        yield return new object[] { claim, opening, river, "post-claim-checkpoint" };
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(PositiveCases))]
        public void ClaimedOpening_RecoveryAnchor_UsesPersistedHistory(
            string claim,
            string opening,
            string river,
            string checkpointForm)
        {
            Scenario scenario = Build(claim, opening, river);

            TableCheckpoint checkpoint = checkpointForm == "post-claim-checkpoint"
                ? BuildPostClaimCheckpoint(scenario)
                : BuildCheckpoint0(scenario);

            TableProjectionContextSource recovered = InvokeAnchor(checkpoint);
            TableProjectionContextData facts = Capture(recovered, scenario);

            TableEventRecord openingRecord = scenario.Records.First(record =>
                record.Type == RoundEventType.TileDiscarded &&
                record.Seat == 0 &&
                record.Tile.HasValue &&
                record.Tile.Value.Value == scenario.Target);
            TableProjectionEventFact openingFact = facts.EventFacts.Single(fact =>
                fact.EventSeq == openingRecord.Seq);
            Assert.Equal(scenario.ExpectedTsumogiri, openingFact.Facts.Tsumogiri);
            Assert.Equal((uint)scenario.Target, openingFact.Facts.DiscardTileId);

            // The claimed tile lives in the claimant's meld, never in the opening seat's
            // current river. A later river entry is a different physical discard.
            Assert.Equal(scenario.LaterDiscard ? 1 : 0, facts.PublicFacts[0].Discards.Count);
            IReadOnlyList<TableMeldFact>? claimantMelds = facts.PublicFacts[1].Melds;
            Assert.NotNull(claimantMelds);
            Assert.Single(claimantMelds!);
            Assert.Equal(
                claim == "Chi" ? ProtocolMeldType.Chi
                    : claim == "Pon" ? ProtocolMeldType.Pon
                    : ProtocolMeldType.OpenKan,
                claimantMelds![0].Type);
            Assert.Equal(136, CountAllTiles(scenario.FinalRound.Tiles));
            Assert.Equal(0, scenario.FinalRound.Tiles.CountAt(TileZone.Unassigned));
        }

        [Fact]
        public void TamperedOpeningFact_WrongTile_FailsClosed()
        {
            Scenario scenario = Build("Pon", "Tsumogiri", "Empty");
            TableCheckpoint checkpoint = BuildPostClaimCheckpoint(scenario, tamper: Tamper.WrongTile);
            Assert.Throws<TargetInvocationException>(() => InvokeAnchor(checkpoint));
        }

        [Fact]
        public void TamperedOpeningFact_MissingSequence_FailsClosed()
        {
            Scenario scenario = Build("Pon", "Tsumogiri", "Empty");
            TableCheckpoint checkpoint = BuildPostClaimCheckpoint(scenario, tamper: Tamper.RemoveOpeningFact);
            Assert.Throws<TargetInvocationException>(() => InvokeAnchor(checkpoint));
        }

        [Fact]
        public void TamperedOpeningFact_DuplicateSequence_FailsClosed()
        {
            Scenario scenario = Build("Pon", "Tsumogiri", "Empty");
            TableCheckpoint checkpoint = BuildPostClaimCheckpoint(scenario, tamper: Tamper.DuplicateOpeningFact);
            Assert.Throws<TargetInvocationException>(() => InvokeAnchor(checkpoint));
        }

        [Fact]
        public void ClaimedAwayOpeningDiscard_WithoutAnyPersistedFact_NeverGuesses()
        {
            Scenario scenario = Build("Pon", "Tsumogiri", "Empty");
            // The probe's synthetic shape: facts with no event-fact window at all and no
            // history. Construction stays structural, but the projection must refuse the
            // first time the missing verdict would be published — never guess.
            TableProjectionContextData facts = Capture(
                new TableProjectionContextSource(null, scenario.InitialRound),
                scenario,
                committedEventsOverride: Array.Empty<TableEventRecord>());
            byte[] factsPayload = TableProjectionFactsCodec.Encode(facts);
            TableCheckpoint evidenceFree = BuildCheckpoint(scenario, factsPayload, includeHistory: false);
            TableProjectionContextSource source = InvokeAnchor(evidenceFree);
            Assert.Throws<InvalidOperationException>(() => Capture(source, scenario));
        }

        private enum Tamper
        {
            None,
            WrongTile,
            RemoveOpeningFact,
            DuplicateOpeningFact
        }

        private sealed class Scenario
        {
            public RoundStateMachine InitialRound = null!;
            public RoundStateMachine FinalRound = null!;
            public IReadOnlyList<TableEventRecord> Records = Array.Empty<TableEventRecord>();
            public IReadOnlyList<TableCommandBinding> Bindings = Array.Empty<TableCommandBinding>();
            public int Target;
            public bool ExpectedTsumogiri;
            public bool LaterDiscard;
            public StateVersion CheckpointVersion;
        }

        private static Scenario Build(string claim, string opening, string river)
        {
            int target = claim == "Chi" ? 8 : 127;
            int[] pair = claim == "Chi"
                ? new[] { 0, 4 }
                : claim == "OpenKan"
                    ? new[] { 124, 125, 126 }
                    : new[] { 124, 125 };
            bool tsumogiri = opening == "Tsumogiri";
            bool later = river == "Later";
            int drawn = tsumogiri ? target : claim == "Chi" ? 127 : 8;
            IEnumerable<int> hand = tsumogiri
                ? Enumerable.Range(108, 13)
                : new[] { target }.Concat(Enumerable.Range(108, 12));

            var fixture = new N1aTable { Dealer = 0 };
            fixture.Hand(0, hand.Select(value => new TileId(value)).ToArray());
            fixture.Drawn(0, new TileId(drawn));
            fixture.Hand(1, pair.Select(value => new TileId(value)).ToArray());
            string roundId = Room + "-r1";
            RoundStateMachine initial = fixture.Build(roundId);

            var records = new List<TableEventRecord>();
            var bindings = new List<TableCommandBinding>();
            int seq = 0;
            ulong version = 0;
            RoundStateMachine current = initial;

            RoundStateMachine Apply(RoundCommand command)
            {
                RoundTransition transition = current.Execute(command);
                Assert.True(transition.IsAccepted, transition.Error?.ToString());
                version++;
                for (int i = 0; i < transition.Events.Count; i++)
                {
                    RoundEvent roundEvent = transition.Events[i];
                    seq++;
                    records.Add(new TableEventRecord(
                        new EventSeq((ulong)seq),
                        new StateVersion(version),
                        i,
                        roundEvent.Type,
                        roundEvent.Seat,
                        roundEvent.Tile,
                        roundEvent.Detail,
                        1,
                        TableEventFactCodec.Encode(
                            new StateVersion(version),
                            i,
                            transition.State!.SourceFingerprint)));
                }

                bindings.Add(new TableCommandBinding(
                    version,
                    transition.Events.Count,
                    transition.State!.SourceFingerprint));
                current = transition.State!;
                return current;
            }

            Apply(RoundCommand.Discard(roundId, 0, new TileId(target), new[] { 1 }));
            RoundCommand call = claim == "Chi"
                ? RoundCommand.Chi(roundId, 1, new TileId(target), new TileId(pair[0]), new TileId(pair[1]))
                : claim == "Pon"
                    ? RoundCommand.Pon(roundId, 1, new TileId(target), new TileId(pair[0]), new TileId(pair[1]))
                    : RoundCommand.OpenKan(
                        roundId, 1, new TileId(target), new TileId(pair[0]), new TileId(pair[1]), new TileId(pair[2]));
            Apply(call);

            if (later)
            {
                // Four real turns: the claimant discards, seats 2/3 discard, then seat 0's
                // next turn discards a DIFFERENT physical tile. The opening discard is gone
                // from the river while a later entry exists.
                for (int seat = 1; seat <= 3; seat++)
                {
                    Apply(RoundCommand.Discard(roundId, seat, current.Tiles.TilesInZone(TileZone.Hand, seat)[0]));
                }

                Apply(RoundCommand.Discard(roundId, 0, current.Tiles.TilesInZone(TileZone.Hand, 0)[0]));
            }

            return new Scenario
            {
                InitialRound = initial,
                FinalRound = current,
                Records = records,
                Bindings = bindings,
                Target = target,
                ExpectedTsumogiri = tsumogiri,
                LaterDiscard = later,
                CheckpointVersion = new StateVersion(version)
            };
        }

        private static TableCheckpoint BuildCheckpoint0(Scenario scenario)
        {
            return RoomHost.CreateInitialCheckpoint(
                Room,
                scenario.InitialRound,
                CheatLedger.Initialize(DefaultEconomy()),
                new OwnerEpoch(1),
                "claim-matrix",
                1);
        }

        private static TableCheckpoint BuildPostClaimCheckpoint(Scenario scenario, Tamper tamper = Tamper.None)
        {
            TableProjectionContextData facts = Capture(
                new TableProjectionContextSource(null, scenario.InitialRound), scenario);
            if (tamper != Tamper.None)
            {
                var eventFacts = new List<TableProjectionEventFact>();
                TableEventRecord openingRecord = scenario.Records.First(record =>
                    record.Type == RoundEventType.TileDiscarded &&
                    record.Seat == 0 &&
                    record.Tile.HasValue &&
                    record.Tile.Value.Value == scenario.Target);
                foreach (TableProjectionEventFact fact in facts.EventFacts)
                {
                    if (fact.EventSeq != openingRecord.Seq)
                    {
                        eventFacts.Add(fact);
                        continue;
                    }

                    if (tamper == Tamper.RemoveOpeningFact)
                    {
                        continue;
                    }

                    TableProjectionEventFacts changed = new TableProjectionEventFacts(
                        turnNumber: fact.Facts.TurnNumber,
                        wallSize: fact.Facts.WallSize,
                        wallRemaining: fact.Facts.WallRemaining,
                        meld: fact.Facts.Meld,
                        fromSeat: fact.Facts.FromSeat,
                        riichiDiscard: fact.Facts.RiichiDiscard,
                        tsumogiri: fact.Facts.Tsumogiri,
                        discardTileId: tamper == Tamper.WrongTile
                            ? fact.Facts.DiscardTileId.GetValueOrDefault() + 1
                            : fact.Facts.DiscardTileId,
                        doubleRiichi: fact.Facts.DoubleRiichi,
                        loserSeat: fact.Facts.LoserSeat,
                        yaku: fact.Facts.Yaku,
                        han: fact.Facts.Han,
                        fu: fact.Facts.Fu,
                        score: fact.Facts.Score,
                        breakdown: fact.Facts.Breakdown,
                        tenpaiPlayers: fact.Facts.TenpaiPlayers,
                        payments: fact.Facts.Payments,
                        newScores: fact.Facts.NewScores,
                        finalScores: fact.Facts.FinalScores,
                        winnerSeat: fact.Facts.WinnerSeat,
                        roundWind: fact.Facts.RoundWind,
                        roundNumber: fact.Facts.RoundNumber,
                        offerId: fact.Facts.OfferId,
                        keptFirst: fact.Facts.KeptFirst,
                        resultTileId: fact.Facts.ResultTileId,
                        beamSwapResolution: fact.Facts.BeamSwapResolution,
                        handTileId: fact.Facts.HandTileId,
                        targetSlot: fact.Facts.TargetSlot);
                    eventFacts.Add(new TableProjectionEventFact(
                        fact.Type, fact.EventSeq, fact.CommandVersion, fact.EventOrdinal, changed));
                    if (tamper == Tamper.DuplicateOpeningFact)
                    {
                        eventFacts.Add(new TableProjectionEventFact(
                            fact.Type, fact.EventSeq, fact.CommandVersion, fact.EventOrdinal, changed));
                    }
                }

                facts = new TableProjectionContextData(
                    facts.RoundId,
                    facts.StateVersion,
                    facts.SessionVersion,
                    facts.Cheat,
                    facts.PublicFacts,
                    facts.PrivateFacts,
                    facts.Session,
                    facts.Policy,
                    eventFacts,
                    facts.ConcealedMeldFacts);
            }

            return BuildCheckpoint(scenario, TableProjectionFactsCodec.Encode(facts), includeHistory: true);
        }

        private static TableCheckpoint BuildCheckpoint(
            Scenario scenario,
            byte[] factsPayload,
            bool includeHistory)
        {
            TableCheckpoint initial = BuildCheckpoint0(scenario);
            TableControlCheckpoint control = TableControlCheckpointCodec.Decode(initial.ControlPayload);
            TableTimingCheckpoint timing = TableTimingCheckpointCodec.Decode(initial.TimingPayload);
            return TableCheckpointFactory.Create(
                Room,
                scenario.FinalRound,
                scenario.CheckpointVersion,
                new EventSeq((ulong)scenario.Records.Count),
                control,
                timing,
                initial.RulesetVersion,
                "claim-matrix",
                1,
                null,
                factsPayload,
                1,
                0,
                includeHistory ? scenario.Records : null,
                includeHistory ? scenario.Bindings : null);
        }

        private static TableProjectionContextData Capture(
            TableProjectionContextSource source,
            Scenario scenario,
            IReadOnlyList<TableEventRecord>? committedEventsOverride = null)
        {
            return source.Capture(new TableProjectionContextRequest(
                Room,
                scenario.FinalRound,
                scenario.CheckpointVersion,
                new EventSeq((ulong)scenario.Records.Count),
                new OwnerEpoch(1),
                0,
                0,
                0,
                1,
                committedEventsOverride ?? scenario.Records));
        }

        private static TableProjectionContextSource InvokeAnchor(TableCheckpoint checkpoint)
        {
            MethodInfo method = typeof(PostgresTableHostRegistry).GetMethod(
                "BuildRecoveryProjectionSource",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("the recovery anchor seam is missing");
            return (TableProjectionContextSource)method.Invoke(
                null,
                new object[] { checkpoint, new TableRoomDecisionState() })!;
        }

        private static CheatEconomyConfig DefaultEconomy()
        {
            var costs = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                [CheatEconomyConfig.TileMarkCostKey] = 5,
                [CheatEconomyConfig.SleeveBloomCostKey] = 10,
                [CheatEconomyConfig.BeamSwapCostKey] = 10
            };
            return new CheatEconomyConfig(true, 1000, costs);
        }

        private static int CountAllTiles(TilePositionMap tiles)
        {
            int total = tiles.CountAt(TileZone.Wall) +
                        tiles.CountAt(TileZone.DeadWall) +
                        tiles.CountAt(TileZone.DeadWallReplacement);
            for (int seat = 0; seat < 4; seat++)
            {
                total += tiles.CountAt(TileZone.Hand, seat);
                total += tiles.CountAt(TileZone.Discard, seat);
                total += tiles.CountAt(TileZone.Meld, seat);
                total += tiles.CountAt(TileZone.Sleeve, seat);
            }

            return total;
        }
    }
}
