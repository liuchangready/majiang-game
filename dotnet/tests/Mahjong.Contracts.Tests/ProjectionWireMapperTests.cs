using System;
using System.Collections.Generic;
using System.Linq;
using Contracts = Mahjong.Contracts;
using Wire = Mahjong.Contracts.Wire;
using Xunit;

namespace Mahjong.Protocol.Tests
{
    public sealed class ProjectionWireMapperTests
    {
        [Fact]
        public void public_snapshot_round_trips_and_reencodes_identically()
        {
            Contracts.PublicSnapshotView source = PublicSnapshot(withSettlement: true);
            Wire.PublicSnapshot wire = Wire.ProjectionWireMapper.MapPublicSnapshot(source);
            Wire.Envelope envelope = Envelope(Wire.MessageType.PublicSnapshot, e => e.PublicSnapshot = wire);

            byte[] bytes = Wire.WireCodec.Encode(envelope);
            Wire.WireDecodeResult decoded = Wire.WireCodec.Decode(bytes);
            Assert.True(decoded.IsOk);
            Wire.WireCodec.EnsureStrict(decoded.Envelope!);
            Assert.Equal(bytes, Wire.WireCodec.Encode(decoded.Envelope!));

            Wire.PublicSnapshot actual = decoded.Envelope!.PublicSnapshot;
            Assert.Equal("session-1", actual.SessionId);
            Assert.Equal("round-1", actual.RoundId);
            Assert.Equal(7ul, actual.Cursor.StreamVersion);
            Assert.Equal(11ul, actual.Cursor.ProjectionSeq);
            Assert.Equal(7ul, actual.StateVersion);
            Assert.Equal(101ul, actual.EventSeq);
            Assert.Equal(4, actual.View.Board.Players.Count);
            Assert.Single(actual.View.Board.Players[3].ConcealedMelds);
            Assert.Equal(Wire.MeldType.ClosedKan, actual.View.Board.Players[3].ConcealedMelds[0].Type);
            Assert.Equal(4u, actual.View.Board.Players[3].ConcealedMelds[0].TileCount);
            Assert.Equal(-1, actual.View.Board.Players[3].ConcealedMelds[0].FromSeat);
            Assert.Single(actual.View.SettlementEvents);
            Assert.Equal(Wire.EventType.TsumoWin, actual.View.SettlementEvents[0].EventType);
        }

        [Fact]
        public void player_snapshot_round_trips_all_owner_payloads_and_four_seats()
        {
            for (int seat = 0; seat < 4; seat++)
            {
                Contracts.PlayerSnapshotView source = PlayerSnapshot(seat);
                Wire.PlayerSnapshot wire = Wire.ProjectionWireMapper.MapPlayerSnapshot(source);
                Wire.Envelope envelope = Envelope(Wire.MessageType.PlayerSnapshot, e => e.PlayerSnapshot = wire);
                envelope.SeatId = seat;

                byte[] bytes = Wire.WireCodec.Encode(envelope);
                Wire.WireDecodeResult decoded = Wire.WireCodec.Decode(bytes);
                Assert.True(decoded.IsOk);
                Wire.WireCodec.EnsureStrict(decoded.Envelope!);
                Assert.Equal(bytes, Wire.WireCodec.Encode(decoded.Envelope!));

                Wire.PlayerSnapshot actual = decoded.Envelope!.PlayerSnapshot;
                Assert.Equal(seat, actual.PrivateView.Seat);
                Assert.Equal(new uint[] { 80, 81, 82 }, actual.PrivateView.ClosedTiles.ToArray());
                Assert.Equal(83u, actual.PrivateView.DrawnTileId);
                Assert.Equal(new uint[] { 96, 97 }, actual.PrivateView.SleeveTiles.ToArray());
                Assert.Single(actual.PrivateView.OwnerMelds);
                Assert.Equal(Wire.MeldType.ClosedKan, actual.PrivateView.OwnerMelds[0].Type);
                Assert.Equal(4, actual.PrivateView.OwnerMelds[0].TileIds.Count);
                Assert.Equal(new uint[] { 98, 99, 100, 101 }, actual.PrivateView.OwnerMelds[0].TileIds.ToArray());
                Assert.Equal("offer-immediate", actual.PrivateView.Cheat.SleeveBloom.OfferId);
                Assert.Equal("IMMEDIATE", actual.PrivateView.Cheat.SleeveBloom.Mode);
                Assert.False(actual.PrivateView.Cheat.SleeveBloom.HasDeadline);
                Assert.Equal("OFF_TURN", actual.PrivateView.Cheat.OffTurnSleeveBloom.Mode);
                Assert.True(actual.PrivateView.Cheat.OffTurnSleeveBloom.HasDeadline);
                Assert.Equal(1234, actual.PrivateView.Cheat.OffTurnSleeveBloom.DeadlineRemainingMs);
                Assert.Equal("window-1", actual.PrivateView.Cheat.OffTurnSleeveBloom.WindowId);
                Assert.Equal(92u, actual.PrivateView.Cheat.SleeveBloomReservation.TileId);
                Assert.Single(actual.PrivateView.Cheat.TileMark.Groups);
                Assert.Equal("group-" + seat, actual.PrivateView.Cheat.TileMark.Groups[0].GroupId);
                Assert.Equal("round-1", actual.PrivateView.Cheat.TileMark.Groups[0].CreatedRoundId);
                Assert.Single(actual.PrivateView.Cheat.BeamSwap.Candidates);
                Assert.Equal(54u, actual.PrivateView.Cheat.BeamSwap.Candidates[0].SlotIndex);
                Assert.Equal(Wire.BeamSwapSlotKind.Wall, actual.PrivateView.Cheat.BeamSwap.Candidates[0].SlotKind);
                Assert.False(actual.PrivateView.Cheat.BeamSwap.HasDeadline);
                Assert.Equal("ledger-" + seat, actual.PrivateView.Cheat.LedgerEntries[0].EntryId);
                Assert.Equal(995, actual.PrivateView.Cheat.CheatPoints);
            }
        }

        [Fact]
        public void all_twenty_one_events_cross_encode_decode_and_reencode_identically()
        {
            Contracts.ProtocolEventRecord[] events = AllEvents();
            Assert.Equal(21, events.Length);

            foreach (Contracts.ProtocolEventRecord record in events)
            {
                if (Contracts.ProtocolEventPolicy.IsPrivateEventType(record.Type))
                {
                    Contracts.PlayerSnapshotView snapshot = PlayerSnapshot(1);
                    var update = new Wire.PlayerUpdate
                    {
                        Snapshot = Wire.ProjectionWireMapper.MapPlayerSnapshot(snapshot),
                        FromCursor = new Wire.StreamCursor { StreamVersion = 6, ProjectionSeq = 10 },
                        Cursor = new Wire.StreamCursor { StreamVersion = 7, ProjectionSeq = 11 },
                        IsFullSnapshot = true
                    };
                    update.VisibleEvents.Add(Wire.ProjectionWireMapper.MapEvent(record));
                    Wire.Envelope envelope = Envelope(Wire.MessageType.PlayerUpdate, e => e.PlayerUpdate = update);
                    envelope.SeatId = 1;

                    byte[] bytes = Wire.WireCodec.Encode(envelope);
                    Wire.WireDecodeResult decoded = Wire.WireCodec.Decode(bytes);
                    Assert.True(decoded.IsOk, record.Type + " failed to decode: " + decoded.ErrorCode);
                    Wire.WireCodec.EnsureStrict(decoded.Envelope!);
                    Assert.Equal(bytes, Wire.WireCodec.Encode(decoded.Envelope!));
                    Assert.Equal((Wire.EventType)(uint)record.Type, decoded.Envelope!.PlayerUpdate.VisibleEvents[0].EventType);
                }
                else
                {
                    Contracts.PublicSnapshotView snapshot = PublicSnapshot(withSettlement: false);
                    var update = new Wire.PublicUpdate
                    {
                        Snapshot = Wire.ProjectionWireMapper.MapPublicSnapshot(snapshot),
                        FromCursor = new Wire.StreamCursor { StreamVersion = 6, ProjectionSeq = 10 },
                        Cursor = new Wire.StreamCursor { StreamVersion = 7, ProjectionSeq = 11 },
                        IsFullSnapshot = true
                    };
                    update.VisibleEvents.Add(Wire.ProjectionWireMapper.MapPublicEvent(record));
                    Wire.Envelope envelope = Envelope(Wire.MessageType.PublicUpdate, e => e.PublicUpdate = update);

                    byte[] bytes = Wire.WireCodec.Encode(envelope);
                    Wire.WireDecodeResult decoded = Wire.WireCodec.Decode(bytes);
                    Assert.True(decoded.IsOk, record.Type + " failed to decode: " + decoded.ErrorCode);
                    Wire.WireCodec.EnsureStrict(decoded.Envelope!);
                    Assert.Equal(bytes, Wire.WireCodec.Encode(decoded.Envelope!));
                    Assert.Equal((Wire.EventType)(uint)record.Type, decoded.Envelope!.PublicUpdate.VisibleEvents[0].EventType);
                }
            }
        }

        [Fact]
        public void private_and_owner_facts_never_enter_the_public_snapshot()
        {
            Contracts.PublicSnapshotView source = PublicSnapshot(withSettlement: true);
            Wire.PublicSnapshot wire = Wire.ProjectionWireMapper.MapPublicSnapshot(source);
            byte[] bytes = Wire.WireCodec.Encode(Envelope(Wire.MessageType.PublicSnapshot, e => e.PublicSnapshot = wire));
            Wire.WireDecodeResult decoded = Wire.WireCodec.Decode(bytes);
            Assert.True(decoded.IsOk);

            Wire.PublicSnapshot actual = decoded.Envelope!.PublicSnapshot;
            Assert.Null(Wire.PublicSnapshot.Descriptor.FindFieldByName("private_view"));
            Assert.Null(Wire.PlayerPublicView.Descriptor.FindFieldByName("closed_tiles"));
            Assert.Null(Wire.PlayerPublicView.Descriptor.FindFieldByName("sleeve_tiles"));

            var publicTiles = new List<uint>();
            for (int p = 0; p < actual.View.Board.Players.Count; p++)
            {
                Wire.PlayerPublicView player = actual.View.Board.Players[p];
                for (int m = 0; m < player.Melds.Count; m++)
                {
                    publicTiles.AddRange(player.Melds[m].TileIds);
                }

                for (int d = 0; d < player.Discards.Count; d++)
                {
                    publicTiles.Add(player.Discards[d].TileId);
                }
            }

            Assert.DoesNotContain(publicTiles, tile => tile >= 80);
            Assert.DoesNotContain("offer-immediate", actual.ToString());
            Assert.DoesNotContain("ledger-0", actual.ToString());
            Assert.DoesNotContain("wall_seed", actual.ToString());
        }

        [Fact]
        public void dark_kan_is_public_as_a_placeholder_and_owner_only_as_identities()
        {
            Contracts.PublicSnapshotView publicSource = PublicSnapshot(withSettlement: false);
            Wire.PublicSnapshot publicWire = Wire.ProjectionWireMapper.MapPublicSnapshot(publicSource);
            Wire.PlayerPublicView publicPlayer = publicWire.View.Board.Players[3];
            Assert.Empty(publicPlayer.Melds);
            Assert.Single(publicPlayer.ConcealedMelds);

            Contracts.PlayerSnapshotView ownerSource = PlayerSnapshot(3);
            Wire.PlayerSnapshot ownerWire = Wire.ProjectionWireMapper.MapPlayerSnapshot(ownerSource);
            Assert.Single(ownerWire.PrivateView.OwnerMelds);
            Assert.Equal(4, ownerWire.PrivateView.OwnerMelds[0].TileIds.Count);
            Wire.WireCodec.EnsureStrict(Envelope(Wire.MessageType.PlayerSnapshot, e =>
            {
                e.PlayerSnapshot = ownerWire;
                e.SeatId = 3;
            }));

            var publicKan = new Contracts.ProtocolEventRecord[1];
            publicKan[0] = Typed(
                Contracts.ProtocolEventType.KanDeclared,
                Contracts.ProtocolEventVisibility.Public,
                Contracts.ProjectedEventPayload.KanDeclared(
                    4,
                    3,
                    null,
                    Contracts.ProtocolMeldType.ClosedKan));
            Wire.PublicEventEnvelope wirePublicKan = Wire.ProjectionWireMapper.MapPublicEvent(publicKan[0]);
            Assert.Equal(Wire.KanType.Closed, wirePublicKan.KanDeclared.KanType);
            Assert.Null(wirePublicKan.KanDeclared.Meld);
            Wire.WireCodec.EnsurePublicEvent(wirePublicKan);

            var privateKan = Typed(
                Contracts.ProtocolEventType.KanDeclared,
                Contracts.ProtocolEventVisibility.Public,
                Contracts.ProjectedEventPayload.KanDeclared(
                    4,
                    3,
                    new Contracts.MeldView(
                        Contracts.ProtocolMeldType.ClosedKan,
                        new uint[] { 98, 99, 100, 101 },
                        null,
                        Contracts.ProtocolConstants.NoSeat),
                    Contracts.ProtocolMeldType.ClosedKan));
            Wire.EventEnvelope wirePrivateKan = Wire.ProjectionWireMapper.MapEvent(privateKan);
            Assert.Equal(4, wirePrivateKan.KanDeclared.Meld.TileIds.Count);

            var seat0Update = new Wire.PlayerUpdate
            {
                Snapshot = Wire.ProjectionWireMapper.MapPlayerSnapshot(PlayerSnapshot(0)),
                Cursor = new Wire.StreamCursor { StreamVersion = 7, ProjectionSeq = 11 },
                FromCursor = new Wire.StreamCursor { StreamVersion = 6, ProjectionSeq = 10 },
                IsFullSnapshot = false
            };
            seat0Update.VisibleEvents.Add(Wire.ProjectionWireMapper.MapEvent(publicKan[0]));
            Wire.Envelope seat0Envelope = Envelope(Wire.MessageType.PlayerUpdate, e => e.PlayerUpdate = seat0Update);
            seat0Envelope.SeatId = 0;
            Wire.WireDecodeResult seat0Decoded = Wire.WireCodec.Decode(Wire.WireCodec.Encode(seat0Envelope));
            Assert.True(seat0Decoded.IsOk);
            Wire.WireCodec.EnsureStrict(seat0Decoded.Envelope!);

            var seat3Update = new Wire.PlayerUpdate
            {
                Snapshot = Wire.ProjectionWireMapper.MapPlayerSnapshot(PlayerSnapshot(3)),
                Cursor = new Wire.StreamCursor { StreamVersion = 7, ProjectionSeq = 11 },
                FromCursor = new Wire.StreamCursor { StreamVersion = 6, ProjectionSeq = 10 },
                IsFullSnapshot = false
            };
            seat3Update.VisibleEvents.Add(wirePrivateKan);
            Wire.Envelope seat3Envelope = Envelope(Wire.MessageType.PlayerUpdate, e => e.PlayerUpdate = seat3Update);
            seat3Envelope.SeatId = 3;
            Wire.WireDecodeResult seat3Decoded = Wire.WireCodec.Decode(Wire.WireCodec.Encode(seat3Envelope));
            Assert.True(seat3Decoded.IsOk);
            Wire.WireCodec.EnsureStrict(seat3Decoded.Envelope!);
        }

        [Fact]
        public void ura_reveal_requires_settlement_authorization_and_breakdown_is_checked()
        {
            Contracts.ProtocolEventRecord unauthorized = Typed(
                Contracts.ProtocolEventType.DoraRevealed,
                Contracts.ProtocolEventVisibility.Public,
                Contracts.ProjectedEventPayload.DoraRevealed(7, 50, true, false));
            Assert.Throws<Contracts.PublicProjectionException>(
                () => Wire.ProjectionWireMapper.MapPublicEvent(unauthorized));

            Assert.Throws<ArgumentException>(() => new Contracts.ScoreBreakdownView(
                3, 30, 3900, new[] { new Contracts.YakuView("RIICHI", 1) }, 0, 1,
                new uint[] { 51 }, new uint[] { 52 }, false));

            Contracts.ProtocolEventRecord authorized = Typed(
                Contracts.ProtocolEventType.DoraRevealed,
                Contracts.ProtocolEventVisibility.SettlementPublic,
                Contracts.ProjectedEventPayload.DoraRevealed(7, 50, true, true));
            Wire.PublicEventEnvelope wire = Wire.ProjectionWireMapper.MapPublicEvent(authorized);
            Assert.True(wire.DoraRevealed.IsUraDora);
            Assert.True(wire.DoraRevealed.UraAuthorized);
            Wire.WireCodec.EnsurePublicEvent(wire);
        }

        [Fact]
        public void unknown_event_oneof_mismatch_out_of_range_and_duplicate_tiles_are_rejected()
        {
            var unknown = new Wire.EventEnvelope
            {
                EventSchemaVersion = 1,
                EventType = (Wire.EventType)999,
                Visibility = Wire.EventVisibility.Public,
                UnknownPayload = Google.Protobuf.ByteString.CopyFrom(new byte[] { 1 })
            };
            Assert.Equal(Wire.WireEventDecodeStatus.Unknown, Wire.WireCodec.DecodeEvent(unknown).Status);

            var mismatch = new Wire.EventEnvelope
            {
                EventSchemaVersion = 1,
                EventType = Wire.EventType.TileDiscarded,
                Visibility = Wire.EventVisibility.Public,
                StackingStarted = new Wire.StackingStarted { TurnNumber = 1 }
            };
            Assert.Equal(Wire.WireEventDecodeStatus.Malformed, Wire.WireCodec.DecodeEvent(mismatch).Status);

            Wire.PublicSnapshot badTile = Wire.ProjectionWireMapper.MapPublicSnapshot(PublicSnapshot(withSettlement: false));
            badTile.View.Board.Players[0].Discards[0].TileId = 136;
            Assert.Throws<Contracts.PublicProjectionException>(() =>
                Wire.WireCodec.EnsureStrict(Envelope(Wire.MessageType.PublicSnapshot, e => e.PublicSnapshot = badTile)));

            Wire.PublicSnapshot duplicate = Wire.ProjectionWireMapper.MapPublicSnapshot(PublicSnapshot(withSettlement: false));
            duplicate.View.Board.Players[0].Melds[0].TileIds[1] = duplicate.View.Board.Players[0].Melds[0].TileIds[0];
            Assert.Throws<Contracts.PublicProjectionException>(() =>
                Wire.WireCodec.EnsureStrict(Envelope(Wire.MessageType.PublicSnapshot, e => e.PublicSnapshot = duplicate)));

            Wire.PlayerSnapshot wrongOwner = Wire.ProjectionWireMapper.MapPlayerSnapshot(PlayerSnapshot(0));
            Wire.Envelope wrongOwnerEnvelope = Envelope(Wire.MessageType.PlayerSnapshot, e =>
            {
                e.PlayerSnapshot = wrongOwner;
                e.SeatId = 1;
            });
            Assert.Throws<Contracts.PublicProjectionException>(() => Wire.WireCodec.EnsureStrict(wrongOwnerEnvelope));

            Contracts.ProtocolEventRecord privateEvent = Typed(
                Contracts.ProtocolEventType.TileDealt,
                Contracts.ProtocolEventVisibility.Public,
                Contracts.ProjectedEventPayload.TileDealt(1, 0, 1));
            Assert.Throws<Contracts.PublicProjectionException>(
                () => Wire.ProjectionWireMapper.MapPublicEvent(privateEvent));
        }

        [Fact]
        public void legacy_major_is_rejected_and_future_minor_optional_field_stays_permissive()
        {
            Wire.Envelope envelope = Envelope(Wire.MessageType.PublicSnapshot, e =>
                e.PublicSnapshot = Wire.ProjectionWireMapper.MapPublicSnapshot(PublicSnapshot(withSettlement: false)));
            byte[] bytes = Wire.WireCodec.Encode(envelope);

            byte[] legacy = AppendField(bytes, 1, 8);
            Wire.WireDecodeResult legacyResult = Wire.WireCodec.Decode(legacy);
            Assert.Equal(Wire.WireDecodeStatus.LegacyV4Rejected, legacyResult.Status);

            byte[] futureMinor = AppendField(bytes, 2, 1);
            Wire.WireDecodeResult minorResult = Wire.WireCodec.Decode(futureMinor);
            Assert.True(minorResult.IsOk);
            Assert.Equal(0, minorResult.UnknownFieldCount);

            Wire.Envelope future = Wire.WireCodec.Decode(futureMinor).Envelope!;
            future.ProtocolMinor = 1;
            Assert.Throws<Wire.WireProtocolException>(() => Wire.WireCodec.Encode(future));
        }

        [Fact]
        public void mapper_preserves_identity_and_does_not_allocate_cursors()
        {
            Contracts.StreamCursor cursor = new Contracts.StreamCursor(
                new Contracts.StreamVersion(77),
                new Contracts.ProjectionSeq(88));
            Contracts.PublicSnapshotView source = PublicSnapshot(withSettlement: false, cursor: cursor);
            Wire.PublicSnapshot wire = Wire.ProjectionWireMapper.MapPublicSnapshot(source);
            Assert.Equal(77ul, wire.Cursor.StreamVersion);
            Assert.Equal(88ul, wire.Cursor.ProjectionSeq);
            Assert.Equal(7ul, wire.StateVersion);
            Assert.Equal(101ul, wire.EventSeq);

            Contracts.PlayerSnapshotView player = PlayerSnapshot(0, cursor);
            Wire.PlayerSnapshot wirePlayer = Wire.ProjectionWireMapper.MapPlayerSnapshot(player);
            Assert.Equal(77ul, wirePlayer.Cursor.StreamVersion);
            Assert.Equal(88ul, wirePlayer.Cursor.ProjectionSeq);
        }

        [Fact]
        public void every_event_maps_on_public_and_four_player_streams_where_allowed()
        {
            Contracts.ProtocolEventRecord[] events = AllEvents();
            int publicMapped = 0;
            int playerMapped = 0;
            foreach (Contracts.ProtocolEventRecord record in events)
            {
                if (Contracts.ProtocolEventPolicy.IsPrivateEventType(record.Type))
                {
                    Assert.Throws<Contracts.PublicProjectionException>(
                        () => Wire.ProjectionWireMapper.MapPublicEvent(record));
                    Wire.EventEnvelope privateEvent = Wire.ProjectionWireMapper.MapEvent(record);
                    Assert.Equal(Wire.EventVisibility.Private, privateEvent.Visibility);
                    continue;
                }

                Wire.PublicEventEnvelope publicEvent = Wire.ProjectionWireMapper.MapPublicEvent(record);
                var publicUpdate = new Wire.PublicUpdate
                {
                    Snapshot = Wire.ProjectionWireMapper.MapPublicSnapshot(PublicSnapshot(withSettlement: false)),
                    FromCursor = new Wire.StreamCursor { StreamVersion = 6, ProjectionSeq = 10 },
                    Cursor = new Wire.StreamCursor { StreamVersion = 7, ProjectionSeq = 11 },
                    IsFullSnapshot = false
                };
                publicUpdate.VisibleEvents.Add(publicEvent);
                byte[] publicBytes = Wire.WireCodec.Encode(
                    Envelope(Wire.MessageType.PublicUpdate, e => e.PublicUpdate = publicUpdate));
                Assert.True(Wire.WireCodec.Decode(publicBytes).IsOk);
                publicMapped++;

                for (int seat = 0; seat < 4; seat++)
                {
                    var playerUpdate = new Wire.PlayerUpdate
                    {
                        Snapshot = Wire.ProjectionWireMapper.MapPlayerSnapshot(PlayerSnapshot(seat)),
                        FromCursor = new Wire.StreamCursor { StreamVersion = 6, ProjectionSeq = 10 },
                        Cursor = new Wire.StreamCursor { StreamVersion = 7, ProjectionSeq = 11 },
                        IsFullSnapshot = false
                    };
                    playerUpdate.VisibleEvents.Add(Wire.ProjectionWireMapper.MapEvent(record));
                    Wire.Envelope envelope = Envelope(
                        Wire.MessageType.PlayerUpdate,
                        e => e.PlayerUpdate = playerUpdate);
                    envelope.SeatId = seat;
                    byte[] playerBytes = Wire.WireCodec.Encode(envelope);
                    Wire.WireDecodeResult playerDecoded = Wire.WireCodec.Decode(playerBytes);
                    Assert.True(playerDecoded.IsOk, record.Type + " seat " + seat + ": " + playerDecoded.ErrorCode);
                    Wire.WireCodec.EnsureStrict(playerDecoded.Envelope!);
                    playerMapped++;
                }
            }

            Assert.Equal(15, publicMapped);
            Assert.Equal(60, playerMapped);
        }

        [Fact]
        public void identical_projection_maps_to_stable_bytes()
        {
            Contracts.PublicSnapshotView publicSource = PublicSnapshot(withSettlement: true);
            byte[] publicFirst = Wire.WireCodec.Encode(Envelope(
                Wire.MessageType.PublicSnapshot,
                e => e.PublicSnapshot = Wire.ProjectionWireMapper.MapPublicSnapshot(publicSource)));
            byte[] publicSecond = Wire.WireCodec.Encode(Envelope(
                Wire.MessageType.PublicSnapshot,
                e => e.PublicSnapshot = Wire.ProjectionWireMapper.MapPublicSnapshot(publicSource)));
            Assert.Equal(publicFirst, publicSecond);

            Contracts.PlayerSnapshotView playerSource = PlayerSnapshot(2);
            Wire.Envelope playerEnvelope = Envelope(Wire.MessageType.PlayerSnapshot, e =>
                e.PlayerSnapshot = Wire.ProjectionWireMapper.MapPlayerSnapshot(playerSource));
            playerEnvelope.SeatId = 2;
            byte[] playerFirst = Wire.WireCodec.Encode(playerEnvelope);
            byte[] playerSecond = Wire.WireCodec.Encode(playerEnvelope);
            Assert.Equal(playerFirst, playerSecond);
        }

        [Fact]
        public void metadata_only_settlement_events_are_rejected_instead_of_sent_as_empty_bodies()
        {
            Contracts.PublicSnapshotView typed = PublicSnapshot(withSettlement: true);
            Contracts.ProtocolEventRecord metadataOnly = Contracts.ProtocolEventRecord.Known(
                Contracts.ProtocolEventType.TsumoWin,
                Contracts.ProtocolEventVisibility.SettlementPublic,
                Contracts.ProtocolConstants.EventSchemaVersion,
                new Contracts.EventSeq(101),
                new Contracts.StateVersion(7),
                0,
                Array.Empty<byte>());
            var view = new Contracts.PublicMatchView(
                typed.View.Board,
                typed.View.Session,
                typed.View.Clocks,
                typed.View.Paused,
                typed.View.SettlementRoundId,
                new[] { metadataOnly });
            var source = new Contracts.PublicSnapshotView(
                typed.SessionId,
                typed.RoundId,
                typed.Cursor,
                typed.StateVersion,
                typed.EventSeq,
                typed.SessionVersion,
                typed.RoundVersion,
                view);
            Assert.Throws<Wire.WireProtocolException>(
                () => Wire.ProjectionWireMapper.MapPublicSnapshot(source));
        }

        [Fact]
        public void typed_payloads_reject_out_of_range_duplicate_and_empty_required_facts()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => Contracts.ProjectedEventPayload.TileDealt(1, 0, 136));
            Assert.Throws<ArgumentException>(
                () => new Contracts.MeldView(
                    Contracts.ProtocolMeldType.Chi,
                    new uint[] { 1, 1, 2 },
                    1,
                    0));
            Assert.Throws<ArgumentException>(
                () => Contracts.ProjectedEventPayload.DrawGame(1, "lowercase", Array.Empty<int>()));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new Contracts.SleeveBloomView(
                    "offer",
                    Contracts.ProjectionSleeveBloomMode.OffTurn,
                    1,
                    2,
                    true,
                    -1,
                    "window",
                    null,
                    1,
                    Contracts.ProjectionSleeveBloomDrawKind.Normal));
            Assert.Throws<ArgumentException>(
                () => new Contracts.BeamSwapView(
                    "beam",
                    Array.Empty<Contracts.BeamSwapCandidateView>(),
                    new uint[] { 3 },
                    false,
                    0,
                    4));
        }

        [Fact]
        public void public_closed_kan_identity_and_mistyped_placeholder_are_rejected_by_strict_wire()
        {
            Wire.PublicSnapshot snapshot = Wire.ProjectionWireMapper.MapPublicSnapshot(
                PublicSnapshot(withSettlement: false));
            Wire.MeldView closedKan = new Wire.MeldView
            {
                Type = Wire.MeldType.ClosedKan,
                FromSeat = -1
            };
            closedKan.TileIds.Add(98);
            closedKan.TileIds.Add(99);
            closedKan.TileIds.Add(100);
            closedKan.TileIds.Add(101);
            snapshot.View.Board.Players[0].Melds.Add(closedKan);
            Assert.Throws<Contracts.PublicProjectionException>(() =>
                Wire.WireCodec.EnsureStrict(Envelope(Wire.MessageType.PublicSnapshot, e => e.PublicSnapshot = snapshot)));

            Wire.PublicSnapshot wrongPlaceholder = Wire.ProjectionWireMapper.MapPublicSnapshot(
                PublicSnapshot(withSettlement: false));
            wrongPlaceholder.View.Board.Players[3].ConcealedMelds[0].Type = Wire.MeldType.Pon;
            Assert.Throws<Contracts.PublicProjectionException>(() =>
                Wire.WireCodec.EnsureStrict(Envelope(
                    Wire.MessageType.PublicSnapshot,
                    e => e.PublicSnapshot = wrongPlaceholder)));
        }

        [Fact]
        public void player_update_mapper_rejects_cross_seat_private_events_and_closed_kan_details()
        {
            Contracts.ProtocolEventRecord draw = Typed(
                Contracts.ProtocolEventType.TileDrawn,
                Contracts.ProtocolEventVisibility.Private,
                Contracts.ProjectedEventPayload.TileDrawn(1, 2, 6, 70));
            Contracts.StreamCursor from = new Contracts.StreamCursor(
                new Contracts.StreamVersion(0),
                new Contracts.ProjectionSeq(0));
            Assert.Throws<Contracts.PublicProjectionException>(() =>
                Wire.ProjectionWireMapper.MapPlayerUpdate(
                    PlayerSnapshot(0),
                    new[] { draw },
                    from,
                    false));
            Wire.PlayerUpdate matching = Wire.ProjectionWireMapper.MapPlayerUpdate(
                PlayerSnapshot(2),
                new[] { draw },
                from,
                false);
            Assert.Single(matching.VisibleEvents);

            Contracts.ProtocolEventRecord kan = Typed(
                Contracts.ProtocolEventType.KanDeclared,
                Contracts.ProtocolEventVisibility.Public,
                Contracts.ProjectedEventPayload.KanDeclared(
                    4,
                    2,
                    new Contracts.MeldView(
                        Contracts.ProtocolMeldType.ClosedKan,
                        new uint[] { 80, 81, 82, 83 },
                        null,
                        Contracts.ProtocolConstants.NoSeat),
                    Contracts.ProtocolMeldType.ClosedKan));
            Assert.Throws<Contracts.PublicProjectionException>(() =>
                Wire.ProjectionWireMapper.MapPlayerUpdate(
                    PlayerSnapshot(0),
                    new[] { kan },
                    from,
                    false));
            Wire.PlayerUpdate ownerKan = Wire.ProjectionWireMapper.MapPlayerUpdate(
                PlayerSnapshot(2),
                new[] { kan },
                from,
                false);
            Assert.Equal(4, ownerKan.VisibleEvents[0].KanDeclared.Meld.TileIds.Count);
        }

        private static Contracts.PlayerSnapshotView PlayerSnapshot(int seat, Contracts.StreamCursor? cursor = null)
        {
            Contracts.PublicSnapshotView publicSnapshot = PublicSnapshot(withSettlement: false, cursor: cursor);
            var cheat = new Contracts.PrivateCheatView(
                995,
                new[]
                {
                    new Contracts.CheatLedgerEntryView("ledger-" + seat, -5, 995, "TILE_MARK")
                },
                true,
                true,
                true,
                true,
                new Contracts.TileMarkView(
                    Array.Empty<uint>(),
                    new[]
                    {
                        new Contracts.TileMarkGroupView(
                            "group-" + seat,
                            new[] { new Contracts.MarkedTileView(10, string.Empty, -1, 0) },
                            "round-1",
                            1,
                            2)
                    }),
                new Contracts.SleeveBloomView(
                    "offer-immediate",
                    Contracts.ProjectionSleeveBloomMode.Immediate,
                    90,
                    91,
                    false,
                    0,
                    string.Empty,
                    null,
                    2,
                    Contracts.ProjectionSleeveBloomDrawKind.Normal),
                new Contracts.SleeveBloomView(
                    "offer-offturn",
                    Contracts.ProjectionSleeveBloomMode.OffTurn,
                    92,
                    93,
                    true,
                    1234,
                    "window-1",
                    null,
                    3,
                    Contracts.ProjectionSleeveBloomDrawKind.Normal),
                new Contracts.SleeveBloomReservationView("offer-offturn", 92, "window-1"),
                "window-1",
                new Contracts.BeamSwapView(
                    "beam-" + seat,
                    new[]
                    {
                        new Contracts.BeamSwapCandidateView(54u, Contracts.ProjectionBeamSwapSlotKind.Wall, 54u, 94)
                    },
                    new uint[] { 95 },
                    false,
                    0,
                    83),
                null);

            var decision = new Contracts.DecisionView(
                new Contracts.DecisionId("decision-1"),
                new Contracts.ControlEpoch(3),
                500,
                new[]
                {
                    new Contracts.LegalActionView(
                        Contracts.ProtocolActionType.DiscardTile,
                        "opt-1",
                        new uint[] { 80 })
                });

            var privateView = new Contracts.PrivatePlayerView(
                seat,
                new uint[] { 80, 81, 82 },
                83,
                true,
                new uint[] { (uint)(40 + seat) },
                decision,
                cheat,
                new uint[] { 96, 97 },
                new[]
                {
                    new Contracts.MeldView(
                        Contracts.ProtocolMeldType.ClosedKan,
                        new uint[] { 98, 99, 100, 101 },
                        null,
                        Contracts.ProtocolConstants.NoSeat)
                });

            return new Contracts.PlayerSnapshotView(
                publicSnapshot.SessionId,
                publicSnapshot.RoundId,
                seat,
                cursor ?? publicSnapshot.Cursor,
                publicSnapshot.StateVersion,
                publicSnapshot.EventSeq,
                publicSnapshot.SessionVersion,
                publicSnapshot.RoundVersion,
                publicSnapshot.View,
                privateView);
        }

        private static Contracts.PublicSnapshotView PublicSnapshot(
            bool withSettlement,
            Contracts.StreamCursor? cursor = null)
        {
            var players = new List<Contracts.PlayerPublicView>
            {
                new Contracts.PlayerPublicView(
                    0,
                    13,
                    new[]
                    {
                        new Contracts.MeldView(
                            Contracts.ProtocolMeldType.Chi,
                            new uint[] { 10, 11, 12 },
                            12,
                            0)
                    },
                    new[] { new Contracts.DiscardView(40, 1, false, false) },
                    false),
                new Contracts.PlayerPublicView(
                    1,
                    13,
                    new[]
                    {
                        new Contracts.MeldView(
                            Contracts.ProtocolMeldType.Pon,
                            new uint[] { 20, 21, 22 },
                            22,
                            2)
                    },
                    new[] { new Contracts.DiscardView(41, 1, false, false) },
                    false),
                new Contracts.PlayerPublicView(
                    2,
                    13,
                    new[]
                    {
                        new Contracts.MeldView(
                            Contracts.ProtocolMeldType.OpenKan,
                            new uint[] { 30, 31, 32, 33 },
                            33,
                            3)
                    },
                    new[] { new Contracts.DiscardView(42, 1, false, false) },
                    false),
                new Contracts.PlayerPublicView(
                    3,
                    13,
                    Array.Empty<Contracts.MeldView>(),
                    new[] { new Contracts.DiscardView(43, 1, false, false) },
                    false,
                    new[] { new Contracts.ConcealedMeldView(Contracts.ProtocolMeldType.ClosedKan, 4, -1) })
            };

            var board = new Contracts.PublicBoardView(
                Contracts.ProtocolGamePhase.Discard,
                1,
                1,
                0,
                0,
                0,
                1,
                70,
                new uint[] { 71 },
                new[]
                {
                    new Contracts.ScoreView(0, 25000),
                    new Contracts.ScoreView(1, 25000),
                    new Contracts.ScoreView(2, 25000),
                    new Contracts.ScoreView(3, 25000)
                },
                players,
                new Contracts.DiscardView(43, 1, false, false),
                false);

            var settlement = new List<Contracts.ProtocolEventRecord>();
            if (withSettlement)
            {
                settlement.Add(Typed(
                    Contracts.ProtocolEventType.TsumoWin,
                    Contracts.ProtocolEventVisibility.SettlementPublic,
                    Contracts.ProjectedEventPayload.TsumoWin(
                        8,
                        1,
                        44,
                        new[] { new Contracts.YakuView("TSUMO", 1) },
                        1,
                        30,
                        1000,
                        new Contracts.ScoreBreakdownView(
                            1,
                            30,
                            1000,
                            new[] { new Contracts.YakuView("TSUMO", 1) },
                            0,
                            0,
                            Array.Empty<uint>(),
                            Array.Empty<uint>(),
                            false))));
            }

            var view = new Contracts.PublicMatchView(
                board,
                new Contracts.PublicSessionSummary(false, string.Empty, Array.Empty<Contracts.ScoreView>(), Array.Empty<int>()),
                new[] { new Contracts.ClockView(1000, 5000, 10000, -1) },
                false,
                "round-1",
                settlement);

            Contracts.StreamCursor actualCursor = cursor ?? new Contracts.StreamCursor(
                new Contracts.StreamVersion(7),
                new Contracts.ProjectionSeq(11));
            return new Contracts.PublicSnapshotView(
                new Contracts.SessionId("session-1"),
                new Contracts.RoundId("round-1"),
                actualCursor,
                new Contracts.StateVersion(7),
                new Contracts.EventSeq(101),
                new Contracts.SessionVersion(1),
                new Contracts.RoundVersion(2),
                view);
        }

        private static Contracts.ProtocolEventRecord[] AllEvents()
        {
            var yaku = new[] { new Contracts.YakuView("TSUMO", 1) };
            var breakdown = new Contracts.ScoreBreakdownView(
                1, 30, 1000, yaku, 0, 0, Array.Empty<uint>(), Array.Empty<uint>(), false);
            return new[]
            {
                Typed(Contracts.ProtocolEventType.StackingStarted, Contracts.ProtocolEventVisibility.Public,
                    Contracts.ProjectedEventPayload.StackingStarted(1)),
                Typed(Contracts.ProtocolEventType.WallBuilt, Contracts.ProtocolEventVisibility.Public,
                    Contracts.ProjectedEventPayload.WallBuilt(1, 136)),
                Typed(Contracts.ProtocolEventType.TileDealt, Contracts.ProtocolEventVisibility.Private,
                    Contracts.ProjectedEventPayload.TileDealt(1, 1, 5)),
                Typed(Contracts.ProtocolEventType.DealingComplete, Contracts.ProtocolEventVisibility.Public,
                    Contracts.ProjectedEventPayload.DealingComplete(1)),
                Typed(Contracts.ProtocolEventType.TileDrawn, Contracts.ProtocolEventVisibility.Private,
                    Contracts.ProjectedEventPayload.TileDrawn(1, 1, 6, 70)),
                Typed(Contracts.ProtocolEventType.TileDiscarded, Contracts.ProtocolEventVisibility.Public,
                    Contracts.ProjectedEventPayload.TileDiscarded(1, 1, 7, true, false)),
                Typed(Contracts.ProtocolEventType.ChiDeclared, Contracts.ProtocolEventVisibility.Public,
                    Contracts.ProjectedEventPayload.ChiDeclared(1, 1,
                        new Contracts.MeldView(Contracts.ProtocolMeldType.Chi, new uint[] { 8, 9, 10 }, 10, 0), 0)),
                Typed(Contracts.ProtocolEventType.PonDeclared, Contracts.ProtocolEventVisibility.Public,
                    Contracts.ProjectedEventPayload.PonDeclared(1, 1,
                        new Contracts.MeldView(Contracts.ProtocolMeldType.Pon, new uint[] { 11, 12, 13 }, 13, 2), 2)),
                Typed(Contracts.ProtocolEventType.KanDeclared, Contracts.ProtocolEventVisibility.Public,
                    Contracts.ProjectedEventPayload.KanDeclared(1, 1,
                        new Contracts.MeldView(Contracts.ProtocolMeldType.OpenKan, new uint[] { 14, 15, 16, 17 }, 17, 3),
                        Contracts.ProtocolMeldType.OpenKan)),
                Typed(Contracts.ProtocolEventType.RinshanDraw, Contracts.ProtocolEventVisibility.Private,
                    Contracts.ProjectedEventPayload.RinshanDraw(1, 1, 18)),
                Typed(Contracts.ProtocolEventType.RiichiDeclared, Contracts.ProtocolEventVisibility.Public,
                    Contracts.ProjectedEventPayload.RiichiDeclared(1, 1, 19, true)),
                Typed(Contracts.ProtocolEventType.TsumoWin, Contracts.ProtocolEventVisibility.SettlementPublic,
                    Contracts.ProjectedEventPayload.TsumoWin(1, 1, 20, yaku, 1, 30, 1000, breakdown)),
                Typed(Contracts.ProtocolEventType.RonWin, Contracts.ProtocolEventVisibility.SettlementPublic,
                    Contracts.ProjectedEventPayload.RonWin(1, 1, 2, 21, yaku, 1, 30, 1000, breakdown, true)),
                Typed(Contracts.ProtocolEventType.DoraRevealed, Contracts.ProtocolEventVisibility.Public,
                    Contracts.ProjectedEventPayload.DoraRevealed(1, 22, false, false)),
                Typed(Contracts.ProtocolEventType.CheatPointsChanged, Contracts.ProtocolEventVisibility.Private,
                    Contracts.ProjectedEventPayload.CheatPointsChanged(1, 1, -5, 995)),
                Typed(Contracts.ProtocolEventType.DrawGame, Contracts.ProtocolEventVisibility.Public,
                    Contracts.ProjectedEventPayload.DrawGame(1, "EXHAUSTIVE_DRAW", new[] { 0, 1 })),
                Typed(Contracts.ProtocolEventType.ScoreSettled, Contracts.ProtocolEventVisibility.Public,
                    Contracts.ProjectedEventPayload.ScoreSettled(1,
                        new[] { new Contracts.PaymentView(1, 0, 1000) },
                        new[]
                        {
                            new Contracts.ScoreView(0, 24000),
                            new Contracts.ScoreView(1, 26000),
                            new Contracts.ScoreView(2, 25000),
                            new Contracts.ScoreView(3, 25000)
                        })),
                Typed(Contracts.ProtocolEventType.RoundEnded, Contracts.ProtocolEventVisibility.Public,
                    Contracts.ProjectedEventPayload.RoundEnded(1, 1, 1)),
                Typed(Contracts.ProtocolEventType.MatchEnded, Contracts.ProtocolEventVisibility.Public,
                    Contracts.ProjectedEventPayload.MatchEnded(1,
                        new[]
                        {
                            new Contracts.ScoreView(0, 24000),
                            new Contracts.ScoreView(1, 26000),
                            new Contracts.ScoreView(2, 25000),
                            new Contracts.ScoreView(3, 25000)
                        },
                        1)),
                Typed(Contracts.ProtocolEventType.SleeveBloomResolved, Contracts.ProtocolEventVisibility.Private,
                    Contracts.ProjectedEventPayload.SleeveBloomResolved(1, "offer-1", true, 23)),
                Typed(Contracts.ProtocolEventType.BeamSwapResolved, Contracts.ProtocolEventVisibility.Private,
                    Contracts.ProjectedEventPayload.BeamSwapResolved(1, "beam-1", Contracts.ProjectionBeamSwapResolution.Swap, 24, 54))
            };
        }

        private static Contracts.ProtocolEventRecord Typed(
            Contracts.ProtocolEventType type,
            Contracts.ProtocolEventVisibility visibility,
            Contracts.ProjectedEventPayload payload)
        {
            return Contracts.ProtocolEventRecord.KnownTyped(
                type,
                visibility,
                Contracts.ProtocolConstants.EventSchemaVersion,
                new Contracts.EventSeq(101),
                new Contracts.StateVersion(7),
                0,
                payload);
        }

        private static Wire.Envelope Envelope(Wire.MessageType type, Action<Wire.Envelope> assignBody)
        {
            var envelope = new Wire.Envelope
            {
                ProtocolMajor = Contracts.ProtocolConstants.Major,
                ProtocolMinor = Contracts.ProtocolConstants.Minor,
                RulesetVersion = Contracts.ProtocolConstants.RulesetVersion,
                ProjectionVersion = Contracts.ProtocolConstants.ProjectionVersion,
                MessageType = type,
                CorrelationId = "corr-1",
                RequestId = "request-1"
            };
            assignBody(envelope);
            return envelope;
        }

        private static byte[] AppendField(byte[] message, int fieldNumber, ulong value)
        {
            using (var stream = new System.IO.MemoryStream())
            {
                stream.Write(message, 0, message.Length);
                ulong tag = ((ulong)fieldNumber << 3) | 0;
                WriteVarint(stream, tag);
                WriteVarint(stream, value);
                return stream.ToArray();
            }
        }

        private static void WriteVarint(System.IO.Stream stream, ulong value)
        {
            while (value >= 0x80)
            {
                stream.WriteByte((byte)(value | 0x80));
                value >>= 7;
            }

            stream.WriteByte((byte)value);
        }
    }
}
