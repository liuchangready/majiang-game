using System;
using System.Collections.Generic;
using Google.Protobuf;
using Contracts = Mahjong.Contracts;
using Wire = Mahjong.Contracts.Wire;
using Xunit;

namespace Mahjong.Protocol.Tests
{
    /// <summary>
    /// Regression probes for the public/private Wire security boundary. They intentionally build
    /// generated messages that a malicious or buggy producer could emit and assert the real
    /// Encode/Decode/EnsureStrict entry points, not only the mapper.
    /// </summary>
    public sealed class WireSecurityBoundaryTests
    {
        [Fact]
        public void public_closed_kan_identities_are_rejected_at_every_public_entry_point()
        {
            Wire.PublicEventEnvelope kan = ClosedKanPublicEvent();
            Assert.Throws<Contracts.PublicProjectionException>(() => Wire.WireCodec.EnsurePublicEvent(kan));

            Wire.PublicUpdate update = PublicUpdateWith(kan);
            Assert.Throws<Contracts.PublicProjectionException>(() => Wire.WireCodec.Encode(
                Envelope(Wire.MessageType.PublicUpdate, e => e.PublicUpdate = update)));
            Assert.NotEqual(Wire.WireDecodeStatus.Ok, Wire.WireCodec.Decode(
                Envelope(Wire.MessageType.PublicUpdate, e => e.PublicUpdate = update).ToByteArray()).Status);
            Assert.Throws<Contracts.PublicProjectionException>(() => Wire.WireCodec.EnsureStrict(
                Envelope(Wire.MessageType.PublicUpdate, e => e.PublicUpdate = update)));

            Wire.PublicSnapshot settlementSnapshot = PublicSnapshot();
            settlementSnapshot.View.SettlementEvents.Add(kan);
            Assert.Throws<Contracts.PublicProjectionException>(() => Wire.WireCodec.Encode(
                Envelope(Wire.MessageType.PublicSnapshot, e => e.PublicSnapshot = settlementSnapshot)));
            Assert.NotEqual(Wire.WireDecodeStatus.Ok, Wire.WireCodec.Decode(
                Envelope(Wire.MessageType.PublicSnapshot, e => e.PublicSnapshot = settlementSnapshot).ToByteArray()).Status);

            Wire.PlayerSnapshot playerSnapshot = PlayerSnapshot(0);
            playerSnapshot.PublicView.SettlementEvents.Add(kan);
            Assert.Throws<Contracts.PublicProjectionException>(() => Wire.WireCodec.Encode(
                Envelope(Wire.MessageType.PlayerSnapshot, e =>
                {
                    e.PlayerSnapshot = playerSnapshot;
                    e.SeatId = 0;
                })));
            Assert.NotEqual(Wire.WireDecodeStatus.Ok, Wire.WireCodec.Decode(
                Envelope(Wire.MessageType.PlayerSnapshot, e =>
                {
                    e.PlayerSnapshot = playerSnapshot;
                    e.SeatId = 0;
                }).ToByteArray()).Status);

            Wire.SyncResponse sync = new Wire.SyncResponse
            {
                Target = Wire.SyncTarget.PublicStream,
                IsFullSnapshot = true,
                Public = settlementSnapshot
            };
            Assert.Throws<Contracts.PublicProjectionException>(() => Wire.WireCodec.Encode(
                Envelope(Wire.MessageType.SyncResponse, e => e.SyncResponse = sync)));
            Assert.NotEqual(Wire.WireDecodeStatus.Ok, Wire.WireCodec.Decode(
                Envelope(Wire.MessageType.SyncResponse, e => e.SyncResponse = sync).ToByteArray()).Status);

            // Empty tile_ids are not a bypass: any closed-kan MeldView or called_tile_id is
            // rejected. The only defined public shape is no meld at all.
            Wire.PublicEventEnvelope calledTile = ClosedKanPublicEvent();
            calledTile.KanDeclared.Meld.TileIds.Clear();
            calledTile.KanDeclared.Meld.CalledTileId = 80;
            Assert.Throws<Contracts.PublicProjectionException>(
                () => Wire.WireCodec.EnsurePublicEvent(calledTile));

            Wire.PublicEventEnvelope emptyMeld = ClosedKanPublicEvent();
            emptyMeld.KanDeclared.Meld.TileIds.Clear();
            Assert.Throws<Contracts.PublicProjectionException>(
                () => Wire.WireCodec.EnsurePublicEvent(emptyMeld));

            Wire.PublicEventEnvelope safeRedacted = ClosedKanPublicEvent();
            safeRedacted.KanDeclared = new Wire.KanDeclared
            {
                Player = 2,
                KanType = Wire.KanType.Closed
            };
            Wire.WireCodec.EnsurePublicEvent(safeRedacted);
        }

        [Fact]
        public void public_win_visibility_is_rejected_at_every_public_entry_point()
        {
            foreach (bool ron in new[] { false, true })
            {
                Wire.PublicEventEnvelope win = PublicWin(ron, Wire.EventVisibility.Public);
                Assert.Throws<Contracts.PublicProjectionException>(() => Wire.WireCodec.EnsurePublicEvent(win));

                Wire.PublicUpdate update = PublicUpdateWith(win);
                Assert.Throws<Contracts.PublicProjectionException>(() => Wire.WireCodec.Encode(
                    Envelope(Wire.MessageType.PublicUpdate, e => e.PublicUpdate = update)));
                Assert.NotEqual(Wire.WireDecodeStatus.Ok, Wire.WireCodec.Decode(
                    Envelope(Wire.MessageType.PublicUpdate, e => e.PublicUpdate = update).ToByteArray()).Status);
                Assert.Throws<Contracts.PublicProjectionException>(() => Wire.WireCodec.EnsureStrict(
                    Envelope(Wire.MessageType.PublicUpdate, e => e.PublicUpdate = update)));

                Wire.PublicEventEnvelope settlement = PublicWin(ron, Wire.EventVisibility.SettlementPublic);
                Wire.WireCodec.EnsurePublicEvent(settlement);
            }
        }

        [Fact]
        public void player_snapshot_owner_binding_is_enforced_by_encode_decode_and_strict()
        {
            Wire.Envelope wrongOwner = Envelope(Wire.MessageType.PlayerSnapshot, e =>
                e.PlayerSnapshot = PlayerSnapshot(1));
            wrongOwner.SeatId = 0;
            Assert.Throws<Contracts.PublicProjectionException>(() => Wire.WireCodec.Encode(wrongOwner));
            Assert.NotEqual(Wire.WireDecodeStatus.Ok, Wire.WireCodec.Decode(wrongOwner.ToByteArray()).Status);
            Assert.Throws<Contracts.PublicProjectionException>(() => Wire.WireCodec.EnsureStrict(wrongOwner));

            Wire.Envelope unknownSeat = Envelope(Wire.MessageType.PlayerSnapshot, e =>
                e.PlayerSnapshot = PlayerSnapshot(0));
            unknownSeat.SeatId = -1;
            Assert.Throws<Wire.WireProtocolException>(() => Wire.WireCodec.Encode(unknownSeat));
            Assert.NotEqual(Wire.WireDecodeStatus.Ok, Wire.WireCodec.Decode(unknownSeat.ToByteArray()).Status);

            Wire.Envelope outOfRange = Envelope(Wire.MessageType.PlayerSnapshot, e =>
                e.PlayerSnapshot = PlayerSnapshot(0));
            outOfRange.SeatId = 4;
            Assert.Throws<Contracts.PublicProjectionException>(() => Wire.WireCodec.Encode(outOfRange));

            Wire.Envelope valid = Envelope(Wire.MessageType.PlayerSnapshot, e =>
                e.PlayerSnapshot = PlayerSnapshot(0));
            valid.SeatId = 0;
            byte[] bytes = Wire.WireCodec.Encode(valid);
            Wire.WireDecodeResult decoded = Wire.WireCodec.Decode(bytes);
            Assert.True(decoded.IsOk);
            Wire.WireCodec.EnsureStrict(decoded.Envelope!);

            // Compatibility-only path can still produce the malicious bytes for a downstream
            // consumer test, but the strict decode/encode path refuses them.
            byte[] compatibility = Wire.WireCodec.EncodeCompatibility(wrongOwner);
            Assert.NotEqual(Wire.WireDecodeStatus.Ok, Wire.WireCodec.Decode(compatibility).Status);
        }

        [Fact]
        public void private_event_owner_matrix_rejects_other_seats_and_missing_provenance()
        {
            for (int receiver = 0; receiver < 4; receiver++)
            {
                for (int owner = 0; owner < 4; owner++)
                {
                    Wire.EventEnvelope[] events =
                    {
                        TileDealt(owner),
                        TileDrawn(owner),
                        RinshanDraw(owner),
                        CheatPoints(owner),
                        SleeveResolved(owner),
                        BeamResolved(owner)
                    };

                    for (int i = 0; i < events.Length; i++)
                    {
                        Wire.Envelope envelope = Envelope(Wire.MessageType.PlayerUpdate, e =>
                            e.PlayerUpdate = PlayerUpdate(receiver, events[i]));
                        envelope.SeatId = receiver;

                        if (receiver == owner)
                        {
                            byte[] bytes = Wire.WireCodec.Encode(envelope);
                            Wire.WireDecodeResult decoded = Wire.WireCodec.Decode(bytes);
                            Assert.True(decoded.IsOk, "receiver " + receiver + " event " + i + ": " + decoded.ErrorCode);
                            Wire.WireCodec.EnsureStrict(decoded.Envelope!);
                        }
                        else
                        {
                            Assert.Throws<Contracts.PublicProjectionException>(
                                () => Wire.WireCodec.Encode(envelope));
                            Assert.NotEqual(
                                Wire.WireDecodeStatus.Ok,
                                Wire.WireCodec.Decode(envelope.ToByteArray()).Status);
                            Assert.Throws<Contracts.PublicProjectionException>(
                                () => Wire.WireCodec.EnsureStrict(envelope));
                        }
                    }
                }
            }

            // Missing resolved owner provenance fails closed even on the matching seat.
            Wire.EventEnvelope unresolvedSleeve = SleeveResolved(0);
            unresolvedSleeve.SleeveBloomResolved.ClearOwnerSeat();
            Assert.Throws<Contracts.PublicProjectionException>(() => Wire.WireCodec.EnsureStrict(
                PlayerUpdateEnvelope(0, unresolvedSleeve)));
            Assert.Throws<Contracts.PublicProjectionException>(() => Wire.WireCodec.Encode(
                PlayerUpdateEnvelope(0, unresolvedSleeve)));

            Wire.EventEnvelope unresolvedBeam = BeamResolved(0);
            unresolvedBeam.BeamSwapResolved.ClearOwnerSeat();
            Assert.Throws<Contracts.PublicProjectionException>(() => Wire.WireCodec.EnsureStrict(
                PlayerUpdateEnvelope(0, unresolvedBeam)));
            Assert.Throws<Contracts.PublicProjectionException>(() => Wire.WireCodec.Encode(
                PlayerUpdateEnvelope(0, unresolvedBeam)));
        }

        [Fact]
        public void closed_kan_identities_are_owner_only_in_player_streams()
        {
            Wire.EventEnvelope full = ClosedKanEvent(2);
            Assert.Throws<Contracts.PublicProjectionException>(() => Wire.WireCodec.Encode(
                PlayerUpdateEnvelope(0, full)));
            Assert.NotEqual(
                Wire.WireDecodeStatus.Ok,
                Wire.WireCodec.Decode(PlayerUpdateEnvelope(0, full).ToByteArray()).Status);
            Assert.Throws<Contracts.PublicProjectionException>(() => Wire.WireCodec.EnsureStrict(
                PlayerUpdateEnvelope(0, full)));

            Wire.Envelope owner = PlayerUpdateEnvelope(2, full);
            Wire.WireDecodeResult ownerDecoded = Wire.WireCodec.Decode(Wire.WireCodec.Encode(owner));
            Assert.True(ownerDecoded.IsOk);
            Wire.WireCodec.EnsureStrict(ownerDecoded.Envelope!);

            Wire.EventEnvelope redacted = ClosedKanEvent(2);
            redacted.KanDeclared.Meld = null;
            Wire.WireCodec.EnsureStrict(PlayerUpdateEnvelope(0, redacted));

            Wire.EventEnvelope calledTile = ClosedKanEvent(2);
            calledTile.KanDeclared.Meld.TileIds.Clear();
            calledTile.KanDeclared.Meld.CalledTileId = 80;
            Assert.Throws<Contracts.PublicProjectionException>(() => Wire.WireCodec.Encode(
                PlayerUpdateEnvelope(2, calledTile)));
        }

        [Fact]
        public void future_minor_unknown_optional_fields_still_decode_for_player_bodies()
        {
            Wire.Envelope snapshot = Envelope(Wire.MessageType.PlayerSnapshot, e =>
                e.PlayerSnapshot = PlayerSnapshot(0));
            snapshot.SeatId = 0;
            Wire.WireDecodeResult snapshotDecoded = Wire.WireCodec.Decode(
                TestWire.AppendVarintField(snapshot.ToByteArray(), 900, 1));
            Assert.True(snapshotDecoded.IsOk);
            Assert.Equal(1, snapshotDecoded.UnknownFieldCount);

            Wire.Envelope update = PlayerUpdateEnvelope(0, TileDrawn(0));
            Wire.WireDecodeResult updateDecoded = Wire.WireCodec.Decode(
                TestWire.AppendVarintField(update.ToByteArray(), 901, 1));
            Assert.True(updateDecoded.IsOk);
            Assert.Equal(1, updateDecoded.UnknownFieldCount);
        }

        private static Wire.PublicEventEnvelope ClosedKanPublicEvent()
        {
            var meld = new Wire.MeldView { Type = Wire.MeldType.ClosedKan, FromSeat = -1 };
            meld.TileIds.Add(80);
            meld.TileIds.Add(81);
            meld.TileIds.Add(82);
            meld.TileIds.Add(83);
            return new Wire.PublicEventEnvelope
            {
                EventSchemaVersion = 1,
                EventType = Wire.EventType.KanDeclared,
                Visibility = Wire.EventVisibility.Public,
                KanDeclared = new Wire.KanDeclared
                {
                    Player = 2,
                    KanType = Wire.KanType.Closed,
                    Meld = meld
                }
            };
        }

        private static Wire.PublicEventEnvelope PublicWin(bool ron, Wire.EventVisibility visibility)
        {
            var win = new Wire.PublicEventEnvelope
            {
                EventSchemaVersion = 1,
                Visibility = visibility
            };
            if (ron)
            {
                win.EventType = Wire.EventType.RonWin;
                win.RonWin = new Wire.RonWin { Winner = 0, Loser = 1, WinTileId = 10 };
            }
            else
            {
                win.EventType = Wire.EventType.TsumoWin;
                win.TsumoWin = new Wire.TsumoWin { Player = 0, WinTileId = 10 };
            }

            return win;
        }

        private static Wire.PublicUpdate PublicUpdateWith(Wire.PublicEventEnvelope wireEvent)
        {
            var update = new Wire.PublicUpdate
            {
                Snapshot = PublicSnapshot(),
                Cursor = new Wire.StreamCursor { StreamVersion = 1, ProjectionSeq = 1 },
                FromCursor = new Wire.StreamCursor { StreamVersion = 0, ProjectionSeq = 0 },
                IsFullSnapshot = false
            };
            update.VisibleEvents.Add(wireEvent);
            return update;
        }

        private static Wire.PublicSnapshot PublicSnapshot()
        {
            return new Wire.PublicSnapshot
            {
                SessionId = "session-1",
                RoundId = "round-1",
                Cursor = new Wire.StreamCursor { StreamVersion = 1, ProjectionSeq = 1 },
                View = new Wire.PublicMatchView()
            };
        }

        private static Wire.PlayerSnapshot PlayerSnapshot(int seat)
        {
            return new Wire.PlayerSnapshot
            {
                SessionId = "session-1",
                RoundId = "round-1",
                Cursor = new Wire.StreamCursor { StreamVersion = 1, ProjectionSeq = 1 },
                PublicView = new Wire.PublicMatchView(),
                PrivateView = new Wire.PlayerPrivateView { Seat = seat }
            };
        }

        private static Wire.PlayerUpdate PlayerUpdate(int receiver, Wire.EventEnvelope wireEvent)
        {
            var update = new Wire.PlayerUpdate
            {
                Snapshot = PlayerSnapshot(receiver),
                Cursor = new Wire.StreamCursor { StreamVersion = 1, ProjectionSeq = 1 },
                FromCursor = new Wire.StreamCursor { StreamVersion = 0, ProjectionSeq = 0 },
                IsFullSnapshot = false
            };
            update.VisibleEvents.Add(wireEvent);
            return update;
        }

        private static Wire.Envelope PlayerUpdateEnvelope(int receiver, Wire.EventEnvelope wireEvent)
        {
            Wire.Envelope envelope = Envelope(
                Wire.MessageType.PlayerUpdate,
                e => e.PlayerUpdate = PlayerUpdate(receiver, wireEvent));
            envelope.SeatId = receiver;
            return envelope;
        }

        private static Wire.EventEnvelope BasePrivate(int owner)
        {
            return new Wire.EventEnvelope
            {
                EventSchemaVersion = 1,
                Visibility = Wire.EventVisibility.Private
            };
        }

        private static Wire.EventEnvelope TileDealt(int owner)
        {
            Wire.EventEnvelope e = BasePrivate(owner);
            e.EventType = Wire.EventType.TileDealt;
            e.TileDealt = new Wire.TileDealt { Player = owner, TileId = 5 };
            return e;
        }

        private static Wire.EventEnvelope TileDrawn(int owner)
        {
            Wire.EventEnvelope e = BasePrivate(owner);
            e.EventType = Wire.EventType.TileDrawn;
            e.TileDrawn = new Wire.TileDrawn { Player = owner, TileId = 6 };
            return e;
        }

        private static Wire.EventEnvelope RinshanDraw(int owner)
        {
            Wire.EventEnvelope e = BasePrivate(owner);
            e.EventType = Wire.EventType.RinshanDraw;
            e.RinshanDraw = new Wire.RinshanDraw { Player = owner, TileId = 7 };
            return e;
        }

        private static Wire.EventEnvelope CheatPoints(int owner)
        {
            Wire.EventEnvelope e = BasePrivate(owner);
            e.EventType = Wire.EventType.CheatPointsChanged;
            e.CheatPointsChanged = new Wire.CheatPointsChanged
            {
                Seat = owner,
                Amount = -5,
                NewBalance = 995
            };
            return e;
        }

        private static Wire.EventEnvelope SleeveResolved(int owner)
        {
            Wire.EventEnvelope e = BasePrivate(owner);
            e.EventType = Wire.EventType.SleeveBloomResolved;
            e.SleeveBloomResolved = new Wire.SleeveBloomResolved
            {
                OfferId = "offer-" + owner,
                KeptFirst = true,
                OwnerSeat = owner
            };
            return e;
        }

        private static Wire.EventEnvelope BeamResolved(int owner)
        {
            Wire.EventEnvelope e = BasePrivate(owner);
            e.EventType = Wire.EventType.BeamSwapResolved;
            e.BeamSwapResolved = new Wire.BeamSwapResolved
            {
                OfferId = "beam-" + owner,
                Action = "SWAP",
                OwnerSeat = owner
            };
            return e;
        }

        private static Wire.EventEnvelope ClosedKanEvent(int player)
        {
            var meld = new Wire.MeldView { Type = Wire.MeldType.ClosedKan, FromSeat = -1 };
            meld.TileIds.Add(80);
            meld.TileIds.Add(81);
            meld.TileIds.Add(82);
            meld.TileIds.Add(83);
            return new Wire.EventEnvelope
            {
                EventSchemaVersion = 1,
                Visibility = Wire.EventVisibility.Public,
                EventType = Wire.EventType.KanDeclared,
                KanDeclared = new Wire.KanDeclared
                {
                    Player = player,
                    KanType = Wire.KanType.Closed,
                    Meld = meld
                }
            };
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
    }
}
