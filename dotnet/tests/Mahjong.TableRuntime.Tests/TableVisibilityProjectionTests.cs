using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class TableVisibilityProjectionTests
    {
        [Fact]
        public void public_snapshot_uses_explicit_public_fields_only()
        {
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            TableProjectionContext context = V32Fixtures.Context(round);

            PublicSnapshotView snapshot = TableVisibilityProjection.ProjectPublicSnapshot(round, context);

            PublicBoardView board = Assert.IsType<PublicBoardView>(snapshot.View.Board);
            Assert.Equal(4, board.Players.Count);
            Assert.Equal(4, board.Scores.Count);
            Assert.Equal((uint)round.Tiles.WallRemaining, board.WallRemaining);
            Assert.Single(board.DoraIndicators);
            Assert.Empty(snapshot.View.Clocks);
            Assert.All(board.Players, player => Assert.Equal((uint)round.Tiles.CountAt(TileZone.Hand, player.Seat), player.HandCount));

            string dump = VisibilitySurface.Dump(snapshot);
            Assert.False(VisibilitySurface.HasField(dump, "ClosedTiles"));
            Assert.False(VisibilitySurface.HasField(dump, "DrawnTileId"));
            Assert.False(VisibilitySurface.HasField(dump, "CheatPoints"));
            Assert.False(VisibilitySurface.HasField(dump, "Ledger"));
            Assert.False(VisibilitySurface.HasField(dump, "MarkGroups"));
            Assert.False(VisibilitySurface.HasField(dump, "SleeveTiles"));
            Assert.False(VisibilitySurface.HasField(dump, "BeamSwapOffer"));
            Assert.False(VisibilitySurface.HasField(dump, "StateFingerprint"));
            Assert.False(VisibilitySurface.HasField(dump, "Wall"));
            Assert.False(VisibilitySurface.HasField(dump, "DeadWall"));
            Assert.False(VisibilitySurface.HasField(dump, "Seed"));
        }

        [Fact]
        public void player_snapshot_keeps_own_hand_and_draw_private()
        {
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            CheatState cheat = V32Fixtures.Cheat();
            TableProjectionContext context = V32Fixtures.Context(round);

            PlayerSnapshotView seat0 = TableVisibilityProjection.ProjectPlayerSnapshot(round, cheat, 0, context);
            PlayerSnapshotView seat1 = TableVisibilityProjection.ProjectPlayerSnapshot(round, cheat, 1, context);

            Assert.Equal(13, seat0.Private.ClosedTiles.Count);
            Assert.True(seat0.Private.DrawnTileId.HasValue);
            Assert.Equal(13, seat1.Private.ClosedTiles.Count);
            Assert.False(seat1.Private.DrawnTileId.HasValue);
            Assert.Empty(seat0.Private.ClosedTiles.Intersect(seat1.Private.ClosedTiles));
            Assert.NotSame(seat0.Public, seat1.Public);

            IReadOnlyList<uint> publicTiles = VisibilitySurface.PublicTileIds(seat0.Public);
            Assert.DoesNotContain(seat0.Private.DrawnTileId!.Value, publicTiles);
            Assert.All(seat0.Private.ClosedTiles, tile => Assert.DoesNotContain(tile, publicTiles));

            IReadOnlyList<uint> seat0Visible = VisibilitySurface.PlayerVisibleTileIds(seat0);
            Assert.All(seat1.Private.ClosedTiles, tile => Assert.DoesNotContain(tile, seat0Visible));
        }

        [Fact]
        public void decision_is_visible_only_to_its_owner()
        {
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            CheatState cheat = V32Fixtures.Cheat();
            uint actionTile = V32Fixtures.HandValues(round, 2)[0];
            var decision = new TableSeatDecision(2, V32Fixtures.Decision("d2", 7, actionTile));
            TableProjectionContext context = V32Fixtures.Context(
                round,
                decisions: new[] { decision });

            PlayerSnapshotView seat0 = TableVisibilityProjection.ProjectPlayerSnapshot(round, cheat, 0, context);
            PlayerSnapshotView seat2 = TableVisibilityProjection.ProjectPlayerSnapshot(round, cheat, 2, context);
            PublicSnapshotView publicSnapshot = TableVisibilityProjection.ProjectPublicSnapshot(round, context);

            Assert.Null(seat0.Private.Decision);
            Assert.NotNull(seat2.Private.Decision);
            Assert.Equal("d2", seat2.Private.Decision!.DecisionId.Value);
            Assert.False(VisibilitySurface.HasField(VisibilitySurface.Dump(publicSnapshot), "Decision"));
            Assert.Contains("Decision=null", VisibilitySurface.Dump(seat0));
            Assert.Contains("d2", VisibilitySurface.Dump(seat2));
        }

        [Fact]
        public void private_events_reach_only_owner()
        {
            TableProjectionPolicy policy = new TableProjectionPolicy();
            var cases = new[]
            {
                V32Fixtures.Event(ProtocolEventType.TileDealt, 2, new[] { 3u }),
                V32Fixtures.Event(ProtocolEventType.TileDrawn, 2, new[] { 4u }),
                V32Fixtures.Event(ProtocolEventType.RinshanDraw, 2, new[] { 5u }),
                V32Fixtures.Event(ProtocolEventType.CheatPointsChanged, 2, cheatAmount: -5, cheatBalance: 995),
                V32Fixtures.Event(ProtocolEventType.SleeveBloomResolved, 2),
                V32Fixtures.Event(ProtocolEventType.BeamSwapResolved, 2)
            };

            foreach (TableEventProjectionInput input in cases)
            {
                for (int seat = 0; seat < 4; seat++)
                {
                    TableProjectedEvent? projected = TableEventProjection.Project(
                        input,
                        TableProjectionAudience.ForSeat(seat),
                        policy);
                    if (seat == 2)
                    {
                        Assert.NotNull(projected);
                        Assert.Equal(ProtocolEventVisibility.Private, projected!.Visibility);
                        Assert.Equal(input.EventSeq, projected.EventSeq);
                        Assert.Equal(input.CommandVersion, projected.CommandVersion);
                        Assert.Equal(input.EventOrdinal, projected.EventOrdinal);
                    }
                    else
                    {
                        Assert.Null(projected);
                    }
                }

                Assert.Null(TableEventProjection.Project(input, TableProjectionAudience.Spectator, policy));
            }

            TableProjectedEvent cheatPoints = TableEventProjection.Project(
                V32Fixtures.Event(ProtocolEventType.CheatPointsChanged, 2, cheatAmount: -5, cheatBalance: 995),
                TableProjectionAudience.ForSeat(2),
                policy)!;
            Assert.Equal(-5L, cheatPoints.CheatAmount);
            Assert.Equal(995L, cheatPoints.CheatBalance);
        }

        [Fact]
        public void public_events_reach_every_seat_and_spectator()
        {
            TableProjectionPolicy policy = new TableProjectionPolicy();
            var cases = new[]
            {
                V32Fixtures.Event(ProtocolEventType.StackingStarted),
                V32Fixtures.Event(ProtocolEventType.WallBuilt),
                V32Fixtures.Event(ProtocolEventType.DealingComplete),
                V32Fixtures.Event(ProtocolEventType.TileDiscarded, 0, new[] { 6u }),
                V32Fixtures.Event(ProtocolEventType.ChiDeclared, 1, new[] { 10u, 11u, 12u }),
                V32Fixtures.Event(ProtocolEventType.PonDeclared, 1, new[] { 13u, 14u, 15u }),
                V32Fixtures.Event(ProtocolEventType.KanDeclared, 1, new[] { 16u }),
                V32Fixtures.Event(ProtocolEventType.RiichiDeclared, 1),
                V32Fixtures.Event(ProtocolEventType.DoraRevealed, 0, new[] { 17u }),
                V32Fixtures.Event(ProtocolEventType.DrawGame, reason: "EXHAUSTIVE_DRAW"),
                V32Fixtures.Event(ProtocolEventType.ScoreSettled),
                V32Fixtures.Event(ProtocolEventType.RoundEnded),
                V32Fixtures.Event(ProtocolEventType.MatchEnded)
            };

            foreach (TableEventProjectionInput input in cases)
            {
                for (int seat = 0; seat < 4; seat++)
                {
                    TableProjectedEvent? projected = TableEventProjection.Project(
                        input,
                        TableProjectionAudience.ForSeat(seat),
                        policy);
                    Assert.NotNull(projected);
                    Assert.Equal(input.EventSeq, projected!.EventSeq);
                    Assert.Equal(input.CommandVersion, projected.CommandVersion);
                    Assert.Equal(input.EventOrdinal, projected.EventOrdinal);
                }

                Assert.NotNull(TableEventProjection.Project(input, TableProjectionAudience.Spectator, policy));
            }
        }

        [Fact]
        public void settlement_events_gate_only_spectator()
        {
            var tsumo = V32Fixtures.Event(ProtocolEventType.TsumoWin, 1, new[] { 18u });
            var ron = V32Fixtures.Event(ProtocolEventType.RonWin, 1, new[] { 19u }, chankan: true);

            TableProjectionPolicy notSettled = new TableProjectionPolicy(settlementBatch: false);
            TableProjectionPolicy settled = new TableProjectionPolicy(settlementBatch: true);

            foreach (TableEventProjectionInput input in new[] { tsumo, ron })
            {
                for (int seat = 0; seat < 4; seat++)
                {
                    Assert.NotNull(TableEventProjection.Project(input, TableProjectionAudience.ForSeat(seat), notSettled));
                }

                Assert.Null(TableEventProjection.Project(input, TableProjectionAudience.Spectator, notSettled));
                TableProjectedEvent? spectator = TableEventProjection.Project(
                    input,
                    TableProjectionAudience.Spectator,
                    settled);
                Assert.NotNull(spectator);
                Assert.Equal(ProtocolEventVisibility.SettlementPublic, spectator!.Visibility);
                Assert.Equal(input.IsChankan, spectator.IsChankan);
            }
        }

        [Fact]
        public void closed_kan_keeps_tile_identity_owner_only()
        {
            var kan = V32Fixtures.Event(
                ProtocolEventType.KanDeclared,
                2,
                new[] { 20u, 21u, 22u, 23u },
                closedKan: true);
            TableProjectionPolicy policy = new TableProjectionPolicy();

            TableProjectedEvent owner = TableEventProjection.Project(
                kan,
                TableProjectionAudience.ForSeat(2),
                policy)!;
            Assert.Equal(new uint[] { 20, 21, 22, 23 }, owner.TileIds.ToArray());
            Assert.True(owner.IsClosedKan);

            for (int seat = 0; seat < 4; seat++)
            {
                if (seat == 2)
                {
                    continue;
                }

                TableProjectedEvent other = TableEventProjection.Project(
                    kan,
                    TableProjectionAudience.ForSeat(seat),
                    policy)!;
                Assert.True(other.IsClosedKan);
                Assert.Empty(other.TileIds);
                Assert.Equal(0, BodyTileCount(other.EncodeBody()));
            }

            TableProjectedEvent spectator = TableEventProjection.Project(
                kan,
                TableProjectionAudience.Spectator,
                policy)!;
            Assert.Empty(spectator.TileIds);
            Assert.Equal(0, BodyTileCount(spectator.EncodeBody()));
        }

        [Fact]
        public void ura_dora_requires_settlement_authorization()
        {
            var tableDora = V32Fixtures.Event(ProtocolEventType.DoraRevealed, 0, new[] { 24u });
            var uraDora = V32Fixtures.Event(ProtocolEventType.DoraRevealed, 0, new[] { 25u }, ura: true);

            TableProjectionPolicy ordinary = new TableProjectionPolicy();
            TableProjectionPolicy authorized = new TableProjectionPolicy(settlementBatch: true, revealSettlementUra: true);
            TableProjectionPolicy batchOnly = new TableProjectionPolicy(settlementBatch: true, revealSettlementUra: false);

            Assert.NotNull(TableEventProjection.Project(tableDora, TableProjectionAudience.Spectator, ordinary));
            Assert.Null(TableEventProjection.Project(uraDora, TableProjectionAudience.Spectator, ordinary));
            Assert.Null(TableEventProjection.Project(uraDora, TableProjectionAudience.ForSeat(0), ordinary));
            Assert.Null(TableEventProjection.Project(uraDora, TableProjectionAudience.Spectator, batchOnly));

            TableProjectedEvent revealed = TableEventProjection.Project(
                uraDora,
                TableProjectionAudience.Spectator,
                authorized)!;
            Assert.Equal(ProtocolEventVisibility.SettlementPublic, revealed.Visibility);
            Assert.Equal(25u, Assert.Single(revealed.TileIds));
        }

        [Fact]
        public void unknown_and_unmapped_events_are_not_published()
        {
            TableProjectionPolicy policy = new TableProjectionPolicy();

            var unknown = Assert.Throws<TableProjectionException>(() =>
                TableEventProjection.Project(
                    V32Fixtures.Event((ProtocolEventType)999),
                    TableProjectionAudience.Spectator,
                    policy));
            Assert.Equal(TableProjectionErrorCode.UnknownEventType, unknown.ErrorCode);

            var unspecified = Assert.Throws<TableProjectionException>(() =>
                TableEventProjection.Project(
                    V32Fixtures.Event(ProtocolEventType.Unspecified),
                    TableProjectionAudience.ForSeat(0),
                    policy));
            Assert.Equal(TableProjectionErrorCode.UnknownEventType, unspecified.ErrorCode);

            var record = new TableEventRecord(
                new EventSeq(1),
                new StateVersion(1),
                0,
                RoundEventType.ResponseWindowOpened,
                0,
                new TileId(1),
                null);
            Assert.Null(TableEventProjection.FromRoundEvent(record));

            var wallExhausted = new TableEventRecord(
                new EventSeq(1),
                new StateVersion(1),
                0,
                RoundEventType.WallExhausted,
                -1,
                null,
                null);
            Assert.Null(TableEventProjection.FromRoundEvent(wallExhausted));

            var roundStarted = new TableEventRecord(
                new EventSeq(1),
                new StateVersion(1),
                0,
                RoundEventType.RoundStarted,
                0,
                null,
                "wind1/round0");
            Assert.Null(TableEventProjection.FromRoundEvent(roundStarted));
        }

        [Fact]
        public void context_validation_rejects_stale_or_mismatched_identity()
        {
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            RoundStateMachine afterDiscard = V32Fixtures.ApplyDiscard(round, 0, out _);
            Assert.True(afterDiscard.Version > round.Version);

            var wrongRound = Assert.Throws<TableProjectionException>(() =>
                TableVisibilityProjection.ProjectPublicSnapshot(
                    afterDiscard,
                    V32Fixtures.Context(afterDiscard, roundId: "round-2")));
            Assert.Equal(TableProjectionErrorCode.RoundIdMismatch, wrongRound.ErrorCode);

            var stale = Assert.Throws<TableProjectionException>(() =>
                TableVisibilityProjection.ProjectPublicSnapshot(
                    afterDiscard,
                    V32Fixtures.Context(
                        afterDiscard,
                        stateVersion: unchecked((ulong)afterDiscard.Version - 1))));
            Assert.Equal(TableProjectionErrorCode.StaleStateVersion, stale.ErrorCode);

            var future = Assert.Throws<TableProjectionException>(() =>
                TableVisibilityProjection.ProjectPublicSnapshot(
                    afterDiscard,
                    V32Fixtures.Context(
                        afterDiscard,
                        cursor: new StreamCursor(
                            new StreamVersion(unchecked((ulong)afterDiscard.Version + 1)),
                            new ProjectionSeq(1)))));
            Assert.Equal(TableProjectionErrorCode.FutureCursor, future.ErrorCode);

            var eventVersion = Assert.Throws<TableProjectionException>(() =>
                TableVisibilityProjection.ProjectPublicSnapshot(
                    afterDiscard,
                    V32Fixtures.Context(
                        afterDiscard,
                        events: new[] { V32Fixtures.Event(ProtocolEventType.RoundEnded, seq: 1, commandVersion: 99) },
                        eventSeq: 1,
                        stateVersion: unchecked((ulong)afterDiscard.Version))));
            Assert.Equal(TableProjectionErrorCode.EventVersionMismatch, eventVersion.ErrorCode);

            var eventWatermark = Assert.Throws<TableProjectionException>(() =>
                TableVisibilityProjection.ProjectPublicSnapshot(
                    afterDiscard,
                    V32Fixtures.Context(
                        afterDiscard,
                        events: new[] { V32Fixtures.Event(ProtocolEventType.RoundEnded, seq: 5) },
                        eventSeq: 4)));
            Assert.Equal(TableProjectionErrorCode.EventVersionMismatch, eventWatermark.ErrorCode);

            var order = Assert.Throws<TableProjectionException>(() =>
                TableVisibilityProjection.ProjectPublicSnapshot(
                    afterDiscard,
                    V32Fixtures.Context(
                        afterDiscard,
                        events: new[]
                        {
                            V32Fixtures.Event(ProtocolEventType.RoundEnded, seq: 2),
                            V32Fixtures.Event(ProtocolEventType.MatchEnded, seq: 1)
                        },
                        eventSeq: 2)));
            Assert.Equal(TableProjectionErrorCode.EventOrderMismatch, order.ErrorCode);
        }

        [Fact]
        public void context_validation_rejects_duplicate_seats_and_public_fact_mismatch()
        {
            RoundStateMachine round = V32Fixtures.ApplyDiscard(P31Fixtures.Dealt("round-1"), 0, out _);

            var duplicatePublic = new List<TableSeatPublicFacts>();
            for (int i = 0; i < 4; i++)
            {
                duplicatePublic.Add(new TableSeatPublicFacts(1, false, Array.Empty<TableMeldFact>(), Array.Empty<DiscardView>()));
            }

            var duplicate = Assert.Throws<TableProjectionException>(() =>
                TableVisibilityProjection.ProjectPublicSnapshot(
                    round,
                    V32Fixtures.Context(round, publicFacts: duplicatePublic)));
            Assert.Equal(TableProjectionErrorCode.DuplicateSeat, duplicate.ErrorCode);

            var duplicateDecisions = Assert.Throws<TableProjectionException>(() =>
                TableVisibilityProjection.ProjectPlayerSnapshot(
                    round,
                    V32Fixtures.Cheat(),
                    0,
                    V32Fixtures.Context(
                        round,
                        decisions: new[]
                        {
                            new TableSeatDecision(2, V32Fixtures.Decision("d1")),
                            new TableSeatDecision(2, V32Fixtures.Decision("d2"))
                        })));
            Assert.Equal(TableProjectionErrorCode.DuplicateSeat, duplicateDecisions.ErrorCode);

            List<TableSeatPublicFacts> missingFacts = V32Fixtures.BuildPublicFacts(round).ToList();
            missingFacts[0] = new TableSeatPublicFacts(0, false, Array.Empty<TableMeldFact>(), Array.Empty<DiscardView>());
            var mismatch = Assert.Throws<TableProjectionException>(() =>
                TableVisibilityProjection.ProjectPublicSnapshot(
                    round,
                    V32Fixtures.Context(round, publicFacts: missingFacts)));
            Assert.Equal(TableProjectionErrorCode.PublicFactsMismatch, mismatch.ErrorCode);

            uint unrelatedHandTile = unchecked((uint)round.Tiles.TilesInZone(TileZone.Hand, 0)[0].Value);
            List<TableSeatPrivateFacts> badHistory = V32Fixtures.BuildPrivateFacts(round).ToList();
            badHistory[0] = new TableSeatPrivateFacts(0, false, false, new[] { unrelatedHandTile });
            var privateMismatch = Assert.Throws<TableProjectionException>(() =>
                TableVisibilityProjection.ProjectPlayerSnapshot(
                    round,
                    V32Fixtures.Cheat(),
                    0,
                    V32Fixtures.Context(round, privateFacts: badHistory)));
            Assert.Equal(TableProjectionErrorCode.PrivateFactsMismatch, privateMismatch.ErrorCode);

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new TableSeatPrivateFacts(0, false, false, new[] { 999u }));
        }

        [Fact]
        public void outputs_are_read_only_and_do_not_share_public_view()
        {
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            CheatState cheat = V32Fixtures.Cheat();
            var mutablePublic = V32Fixtures.BuildPublicFacts(round).ToList();
            TableProjectionContext context = V32Fixtures.Context(round, publicFacts: mutablePublic);

            PlayerSnapshotView seat0 = TableVisibilityProjection.ProjectPlayerSnapshot(round, cheat, 0, context);
            PlayerSnapshotView seat1 = TableVisibilityProjection.ProjectPlayerSnapshot(round, cheat, 1, context);
            PublicSnapshotView publicSnapshot = TableVisibilityProjection.ProjectPublicSnapshot(round, context);

            mutablePublic.Clear();
            Assert.Equal(4, seat0.Public.Board!.Players.Count);
            Assert.Equal(4, seat1.Public.Board!.Players.Count);

            Assert.NotSame(seat0.Public, seat1.Public);
            Assert.NotSame(seat0.Private, seat1.Private);
            Assert.NotSame(publicSnapshot.View, seat0.Public);

            var dora = (IList<uint>)seat0.Public.Board!.DoraIndicators;
            Assert.Throws<NotSupportedException>(() => dora.Add(0u));
            var closed = (IList<uint>)seat0.Private.ClosedTiles;
            Assert.Throws<NotSupportedException>(() => closed.Add(0u));

            string before = VisibilitySurface.Dump(publicSnapshot);
            PlayerSnapshotView again = TableVisibilityProjection.ProjectPlayerSnapshot(round, cheat, 0, context);
            Assert.Equal(before, VisibilitySurface.Dump(publicSnapshot));
            Assert.Equal(VisibilitySurface.Dump(seat0), VisibilitySurface.Dump(again));
        }

        [Fact]
        public void different_hidden_states_produce_identical_public_snapshot()
        {
            RoundStateMachine roundA = P31Fixtures.Dealt("round-1");
            RoundStateMachine roundB = WithSwappedHiddenTiles(roundA);
            Assert.NotEqual(roundA.Tiles.StateHash, roundB.Tiles.StateHash);
            Assert.NotEqual(roundA.SourceFingerprint, roundB.SourceFingerprint);

            TableProjectionContext contextA = V32Fixtures.Context(roundA);
            TableProjectionContext contextB = V32Fixtures.Context(roundB);

            PublicSnapshotView publicA = TableVisibilityProjection.ProjectPublicSnapshot(roundA, contextA);
            PublicSnapshotView publicB = TableVisibilityProjection.ProjectPublicSnapshot(roundB, contextB);

            Assert.Equal(VisibilitySurface.Dump(publicA), VisibilitySurface.Dump(publicB));
            Assert.Equal(VisibilitySurface.PublicTileIds(publicA), VisibilitySurface.PublicTileIds(publicB));

            CheatState cheat = V32Fixtures.Cheat();
            PlayerSnapshotView seat0A = TableVisibilityProjection.ProjectPlayerSnapshot(roundA, cheat, 0, contextA);
            PlayerSnapshotView seat0B = TableVisibilityProjection.ProjectPlayerSnapshot(roundB, cheat, 0, contextB);
            Assert.Equal(VisibilitySurface.Dump(seat0A.Public), VisibilitySurface.Dump(seat0B.Public));

            PlayerSnapshotView seat1A = TableVisibilityProjection.ProjectPlayerSnapshot(roundA, cheat, 1, contextA);
            PlayerSnapshotView seat1B = TableVisibilityProjection.ProjectPlayerSnapshot(roundB, cheat, 1, contextB);
            Assert.NotEqual(
                string.Join(",", seat1A.Private.ClosedTiles),
                string.Join(",", seat1B.Private.ClosedTiles));
            Assert.Equal(VisibilitySurface.Dump(seat1A.Public), VisibilitySurface.Dump(seat1B.Public));
        }

        [Fact]
        public void public_snapshot_does_not_expose_hidden_wall_or_hand_tiles()
        {
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            PublicSnapshotView snapshot = TableVisibilityProjection.ProjectPublicSnapshot(
                round,
                V32Fixtures.Context(round));

            var publicTiles = VisibilitySurface.PublicTileIds(snapshot);
            IReadOnlyList<TileId> hiddenWall = round.Tiles.TilesInZone(TileZone.Wall);
            Assert.NotEmpty(hiddenWall);
            Assert.DoesNotContain(unchecked((uint)hiddenWall[0].Value), publicTiles);

            for (int seat = 0; seat < 4; seat++)
            {
                IReadOnlyList<TileId> hand = round.Tiles.TilesInZone(TileZone.Hand, seat);
                for (int i = 0; i < hand.Count; i++)
                {
                    Assert.DoesNotContain(unchecked((uint)hand[i].Value), publicTiles);
                }
            }

            string dump = VisibilitySurface.Dump(snapshot);
            Assert.False(VisibilitySurface.HasField(dump, "StateFingerprint"));
            Assert.False(VisibilitySurface.HasField(dump, "Seed"));
        }

        [Fact]
        public void event_bodies_preserve_source_versions_and_redact_hidden_payload()
        {
            var input = V32Fixtures.Event(
                ProtocolEventType.TileDiscarded,
                3,
                new[] { 30u },
                seq: 7,
                commandVersion: 4,
                ordinal: 2);
            TableProjectedEvent projected = TableEventProjection.Project(
                input,
                TableProjectionAudience.Spectator,
                new TableProjectionPolicy())!;

            Assert.Equal(new EventSeq(7), projected.EventSeq);
            Assert.Equal(new StateVersion(4), projected.CommandVersion);
            Assert.Equal(2u, projected.EventOrdinal);
            Assert.Equal(3, projected.Seat);
            Assert.Equal(new uint[] { 30 }, projected.TileIds.ToArray());
            Assert.Equal(1, BodyTileCount(projected.EncodeBody()));
            Assert.Equal(30u, BodyTileId(projected.EncodeBody(), 0));

            ProtocolEventRecord? contract = TableEventProjection.ProjectToContract(
                input,
                TableProjectionAudience.Spectator,
                new TableProjectionPolicy());
            Assert.NotNull(contract);
            ProtocolEventRecord record = contract!;
            Assert.Equal(ProtocolEventType.TileDiscarded, record.Type);
            Assert.Equal(ProtocolEventVisibility.Public, record.Visibility);
            Assert.Equal(7UL, record.EventSeq.Value);
            Assert.Equal(4UL, record.CommandVersion.Value);
            Assert.Equal(2u, record.EventOrdinal);
            Assert.Empty(record.CopyWirePayload());
        }

        [Fact]
        public void public_snapshot_projects_open_meld_and_omits_closed_kan_identity()
        {
            RoundStateMachine openRound = V32Fixtures.WithMeld(
                P31Fixtures.Dealt("round-1"),
                0,
                3,
                ProtocolMeldType.Chi,
                out TableMeldFact openMeld);
            var openFacts = new Dictionary<int, IReadOnlyList<TableMeldFact>>
            {
                [0] = new[] { openMeld }
            };
            PublicSnapshotView openPublic = TableVisibilityProjection.ProjectPublicSnapshot(
                openRound,
                V32Fixtures.Context(openRound, melds: openFacts));

            MeldView projectedMeld = Assert.Single(openPublic.View.Board!.Players[0].Melds);
            Assert.Equal(ProtocolMeldType.Chi, projectedMeld.Type);
            Assert.Equal(openMeld.TileIds.ToArray(), projectedMeld.TileIds.ToArray());
            Assert.Empty(openPublic.View.Board.Players[1].Melds);
            Assert.All(
                openMeld.TileIds,
                tile => Assert.Contains(tile, VisibilitySurface.PublicTileIds(openPublic)));

            RoundStateMachine kanRound = V32Fixtures.WithMeld(
                P31Fixtures.Dealt("round-1"),
                1,
                4,
                ProtocolMeldType.ClosedKan,
                out TableMeldFact concealedKan);
            var kanFacts = new Dictionary<int, IReadOnlyList<TableMeldFact>>
            {
                [1] = new[] { concealedKan }
            };
            TableProjectionContext kanContext = V32Fixtures.Context(kanRound, melds: kanFacts);
            PublicSnapshotView kanPublic = TableVisibilityProjection.ProjectPublicSnapshot(kanRound, kanContext);
            Assert.Empty(kanPublic.View.Board!.Players[1].Melds);
            Assert.All(
                concealedKan.TileIds,
                tile => Assert.DoesNotContain(tile, VisibilitySurface.PublicTileIds(kanPublic)));

            TableOwnerPrivateView owner = TableVisibilityProjection.ProjectOwnerPrivate(
                kanRound,
                V32Fixtures.Cheat(),
                1,
                kanContext);
            TableOwnerMeldView ownKan = Assert.Single(owner.Melds);
            Assert.True(ownKan.IsConcealed);
            Assert.Equal(concealedKan.TileIds.ToArray(), ownKan.TileIds.ToArray());
        }

        [Fact]
        public void invalid_event_payloads_and_audiences_are_rejected()
        {
            Assert.Throws<ArgumentException>(() =>
                V32Fixtures.Event(ProtocolEventType.RiichiDeclared, 1, new[] { 1u }));
            Assert.Throws<ArgumentException>(() =>
                V32Fixtures.Event(ProtocolEventType.KanDeclared, 1, new[] { 1u }, closedKan: true));
            Assert.Throws<ArgumentException>(() =>
                V32Fixtures.Event(ProtocolEventType.TsumoWin, 1, new[] { 1u, 2u }));
            Assert.Throws<ArgumentException>(() =>
                V32Fixtures.Event(ProtocolEventType.CheatPointsChanged, 1, cheatAmount: -5));
            Assert.Throws<ArgumentException>(() =>
                V32Fixtures.Event(ProtocolEventType.DrawGame, reason: "lowercase"));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                V32Fixtures.Event(ProtocolEventType.TileDrawn, 1, new[] { 999u }));
            Assert.Throws<ArgumentOutOfRangeException>(() => TableProjectionAudience.ForSeat(4));
        }

        [Fact]
        public void public_and_opponent_surfaces_scan_clean()
        {
            RoundStateMachine round = V32Fixtures.ApplyDiscard(P31Fixtures.Dealt("round-1"), 0, out _);
            CheatState state = V32Fixtures.WithRegisteredRound(V32Fixtures.Cheat(), round.RoundId);
            state = V32Fixtures.WithLedger(
                state,
                new CheatLedgerEntry("scan-0", 0, -5, 995, "TILE_MARK"),
                new CheatLedgerEntry("scan-1", 1, -10, 990, "BEAM_SWAP"));
            TileMarkTransition mark = state.TileMarks.AddGroup(new TileMarkGroup(
                "scan-group",
                0,
                new[] { new TileId(0), new TileId(1), new TileId(2), new TileId(3) },
                round.RoundId,
                1));
            Assert.True(mark.Success);
            state = state.WithTileMarks(mark.State);
            state = state.WithSleeveBloom(state.SleeveBloom.WithImmediateOffer(new SleeveBloomOffer(
                "scan-bloom",
                0,
                SleeveBloomDrawKind.Normal,
                new TileId(4),
                new TileId(5),
                1,
                SleeveBloomOfferMode.Immediate)));

            var decision = new TableSeatDecision(2, V32Fixtures.Decision("scan-d", 1));
            TableProjectionContext context = V32Fixtures.Context(
                round,
                decisions: new[] { decision });
            PublicSnapshotView publicSnapshot = TableVisibilityProjection.ProjectPublicSnapshot(round, context);

            string[] forbidden =
            {
                "ClosedTiles", "DrawnTileId", "CheatPoints", "Ledger", "MarkGroups",
                "SleeveTiles", "BeamSwapOffer", "ImmediateOffer", "Reservation",
                "Candidates", "EligibleHandTileIds", "StateFingerprint", "Seed", "Wall", "DeadWall"
            };
            string publicDump = VisibilitySurface.Dump(publicSnapshot);
            foreach (string field in forbidden)
            {
                Assert.False(VisibilitySurface.HasField(publicDump, field), "public field " + field);
            }

            IReadOnlyList<uint> publicTiles = VisibilitySurface.PublicTileIds(publicSnapshot);
            IReadOnlyList<TileId> hiddenWall = round.Tiles.TilesInZone(TileZone.Wall);
            Assert.DoesNotContain(unchecked((uint)hiddenWall[0].Value), publicTiles);

            for (int seat = 0; seat < 4; seat++)
            {
                PlayerSnapshotView snapshot = TableVisibilityProjection.ProjectPlayerSnapshot(
                    round,
                    state,
                    seat,
                    context);
                string opponentPublicDump = VisibilitySurface.Dump(snapshot.Public);
                foreach (string field in forbidden)
                {
                    Assert.False(VisibilitySurface.HasField(opponentPublicDump, field), "seat " + seat + " public " + field);
                }

                if (seat == 2)
                {
                    Assert.NotNull(snapshot.Private.Decision);
                }
                else
                {
                    Assert.Null(snapshot.Private.Decision);
                }

                for (int other = 0; other < 4; other++)
                {
                    if (other == seat)
                    {
                        continue;
                    }

                    IReadOnlyList<TileId> otherHand = round.Tiles.TilesInZone(TileZone.Hand, other);
                    for (int i = 0; i < otherHand.Count; i++)
                    {
                        Assert.DoesNotContain(
                            unchecked((uint)otherHand[i].Value),
                            snapshot.Private.ClosedTiles);
                    }
                }
            }
        }

        private static RoundStateMachine WithSwappedHiddenTiles(RoundStateMachine source)
        {
            TilePositionMap map = source.Tiles;
            List<TilePlacement> placements = map.AsPlacements().ToList();

            TilePlacement wall0 = placements.First(item => item.Position.Zone == TileZone.Wall);
            TilePlacement wall1 = placements.First(item => item.Position.Zone == TileZone.Wall && item.Position != wall0.Position);
            Swap(placements, wall0, wall1);

            TilePlacement hand0 = placements.First(item => item.Position.Zone == TileZone.Hand && item.Position.Seat == 1);
            TilePlacement hand1 = placements.First(item =>
                item.Position.Zone == TileZone.Hand &&
                item.Position.Seat == 1 &&
                item.Position != hand0.Position);
            Swap(placements, hand0, hand1);

            TilePositionMap swapped = TilePositionMap.FromPlacements(
                placements,
                map.WallPointer,
                map.WallEndExclusive,
                map.RinshanPointer,
                map.LastDiscard,
                map.Revision,
                map.LastRinshanDraw,
                map.DoraIndicators,
                map.KanCount,
                map.DrawnTile);
            return RoundStateMachine.CreateDealt(source.Spec, swapped, RoundPhase.CheatWindow);
        }

        private static void Swap(List<TilePlacement> placements, TilePlacement left, TilePlacement right)
        {
            int leftIndex = placements.IndexOf(left);
            int rightIndex = placements.IndexOf(right);
            placements[leftIndex] = new TilePlacement(right.Tile, left.Position);
            placements[rightIndex] = new TilePlacement(left.Tile, right.Position);
        }

        private static int BodyTileCount(byte[] body)
        {
            Assert.True(body.Length > 4 + 1 + 1 + 1 + 1 + 4 + 8 + 8 + 1);
            return body[4 + 1 + 1 + 1 + 1 + 4 + 8 + 8];
        }

        private static uint BodyTileId(byte[] body, int index)
        {
            int offset = 4 + 1 + 1 + 1 + 1 + 4 + 8 + 8 + 1 + index;
            Assert.True(body.Length > offset);
            return body[offset];
        }
    }
}
