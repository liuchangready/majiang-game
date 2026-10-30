using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Xunit;
using ContractMeldView = Mahjong.Contracts.MeldView;
using ContractYakuView = Mahjong.Contracts.YakuView;
using DomainSleeveBloomDrawKind = Mahjong.Domain.Cheat.SleeveBloomDrawKind;

namespace Mahjong.TableRuntime.Tests
{
    /// <summary>
    /// The real host-side strict codec used by the joint tests. It maps the actor-frozen
    /// Contracts DTOs through the accepted ProjectionWireMapper and encodes/decodes only through
    /// WireCodec.Encode/Decode (never EncodeCompatibility or reflection).
    /// </summary>
    internal sealed class StrictWireStreamCodec : ITableStreamContractCodec
    {
        public int EncodeCount { get; private set; }

        public List<byte[]> Encoded { get; } = new List<byte[]>();

        public byte[] Encode(TableStreamContractFrame frame)
        {
            var envelope = new Envelope
            {
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion,
                MessageType = frame.Public != null ? MessageType.PublicUpdate : MessageType.PlayerUpdate,
                SessionId = frame.SessionId.Value,
                RoomId = frame.RoomId,
                SeatId = frame.Audience.Seat,
                BindingEpoch = frame.BindingEpoch.Value,
                Cursor = ProjectionWireMapper.MapCursor(frame.Cursor)
            };

            if (frame.Public != null)
            {
                envelope.PublicUpdate = ProjectionWireMapper.MapPublicUpdate(
                    frame.Public,
                    frame.Events,
                    frame.FromCursor,
                    frame.IsFullSnapshot);
            }
            else
            {
                envelope.PlayerUpdate = ProjectionWireMapper.MapPlayerUpdate(
                    frame.Player!,
                    frame.Events,
                    frame.FromCursor,
                    frame.IsFullSnapshot);
            }

            WireCodec.EnsureStrict(envelope);
            byte[] bytes = WireCodec.Encode(envelope);
            WireDecodeResult decoded = WireCodec.Decode(bytes);
            if (!decoded.IsOk || decoded.Envelope == null)
            {
                throw new WireProtocolException(
                    "The strict joint codec could not decode its own output: " + decoded.ErrorCode,
                    decoded.ErrorCode);
            }

            WireCodec.EnsureStrict(decoded.Envelope);
            byte[] reencoded = WireCodec.Encode(decoded.Envelope);
            if (!bytes.AsSpan().SequenceEqual(reencoded))
            {
                throw new WireMalformedException("Strict encode/decode/re-encode is not byte-stable.");
            }

            EncodeCount++;
            Encoded.Add(bytes);
            return bytes;
        }

        public static Envelope DecodeAndStrict(byte[] bytes)
        {
            WireDecodeResult decoded = WireCodec.Decode(bytes);
            Assert.True(decoded.IsOk, "Wire decode failed: " + decoded.ErrorCode);
            Assert.NotNull(decoded.Envelope);
            WireCodec.EnsureStrict(decoded.Envelope!);
            return decoded.Envelope!;
        }
    }

    internal sealed class WireEventFactOptions
    {
        public uint TurnNumber { get; set; } = 1;

        public bool RiichiDiscard { get; set; }

        public bool Tsumogiri { get; set; }

        /// <summary>Required for a KanDeclared event; the Domain only carries the called tile.</summary>
        public ContractMeldView? KanMeld { get; set; }

        /// <summary>Required for a win; the Domain does not model yaku/fu/base score.</summary>
        public IReadOnlyList<ContractYakuView>? WinYaku { get; set; }

        public uint WinHan { get; set; }

        public uint WinFu { get; set; }

        public long WinScore { get; set; }
    }

    /// <summary>
    /// Trusted fixture source for event facts. Domain-modelled values come from the request's
    /// immutable round/event records; unmodelled values are explicit options and a missing value
    /// throws instead of being defaulted.
    /// </summary>
    internal static class WireEventFacts
    {
        public static IReadOnlyList<TableProjectionEventFact> Build(
            TableProjectionContextRequest request,
            WireEventFactOptions? options = null)
        {
            WireEventFactOptions actual = options ?? new WireEventFactOptions();
            var wallBySeq = new Dictionary<ulong, uint>();
            long wall = request.Round.Tiles.WallRemaining;
            for (int i = request.CommittedEvents.Count - 1; i >= 0; i--)
            {
                TableEventRecord record = request.CommittedEvents[i];
                if (record.Type == RoundEventType.KanDeclared)
                {
                    continue;
                }

                if (record.Type == RoundEventType.RinshanDrawn)
                {
                    wall++;
                    continue;
                }

                if (record.Type == RoundEventType.TileDrawn)
                {
                    wallBySeq[record.Seq.Value] = unchecked((uint)wall);
                    wall++;
                }
            }

            var facts = new List<TableProjectionEventFact>();
            for (int i = 0; i < request.CommittedEvents.Count; i++)
            {
                TableEventRecord record = request.CommittedEvents[i];
                uint? wallRemaining = wallBySeq.TryGetValue(record.Seq.Value, out uint value)
                    ? value
                    : (uint?)null;
                TableProjectionEventFacts? factsValue = BuildOne(request, record, actual, wallRemaining);
                if (factsValue != null)
                {
                    facts.Add(new TableProjectionEventFact(
                        MapType(record.Type),
                        record.Seq,
                        record.CommandVersion,
                        unchecked((uint)record.Ordinal),
                        factsValue));
                }
            }

            return facts;
        }

        private static TableProjectionEventFacts? BuildOne(
            TableProjectionContextRequest request,
            TableEventRecord record,
            WireEventFactOptions options,
            uint? wallRemaining = null)
        {
            switch (record.Type)
            {
                case RoundEventType.TilesDealt:
                    return new TableProjectionEventFacts(turnNumber: options.TurnNumber);
                case RoundEventType.DoraRevealed:
                    return new TableProjectionEventFacts(turnNumber: options.TurnNumber);
                case RoundEventType.TileDrawn:
                    return new TableProjectionEventFacts(
                        turnNumber: options.TurnNumber,
                        wallRemaining: wallRemaining ??
                            unchecked((uint)request.Round.Tiles.WallRemaining));
                case RoundEventType.RinshanDrawn:
                    return new TableProjectionEventFacts(turnNumber: options.TurnNumber);
                case RoundEventType.TileDiscarded:
                    return new TableProjectionEventFacts(
                        turnNumber: options.TurnNumber,
                        riichiDiscard: options.RiichiDiscard,
                        tsumogiri: options.Tsumogiri);
                case RoundEventType.PonDeclared:
                case RoundEventType.ChiDeclared:
                    ContractMeldView call = TableCommittedCallFacts.RequireCallMeld(request.Round, record);
                    return new TableProjectionEventFacts(
                        turnNumber: TableCommittedCallFacts.RequireTurnNumber(request.Round, record),
                        meld: call,
                        fromSeat: call.FromSeat);
                case RoundEventType.RiichiDeclared:
                    if (!record.Tile.HasValue)
                        throw new InvalidOperationException("RiichiDeclared requires its committed discard tile.");
                    return new TableProjectionEventFacts(
                        turnNumber: TableCommittedCallFacts.RequireTurnNumber(request.Round, record),
                        discardTileId: unchecked((uint)record.Tile.Value.Value),
                        doubleRiichi: TableCommittedCallFacts.RequireDoubleRiichi(request.Round, record));
                case RoundEventType.KanDeclared:
                    if (options.KanMeld == null)
                    {
                        throw new InvalidOperationException(
                            "The joint fixture has no explicit kan meld fact; refusing to guess from the called tile.");
                    }

                    return new TableProjectionEventFacts(
                        turnNumber: options.TurnNumber,
                        meld: options.KanMeld);
                case RoundEventType.TsumoWin:
                case RoundEventType.RonWin:
                    if (options.WinYaku == null)
                    {
                        throw new InvalidOperationException(
                            "The joint fixture has no explicit win yaku/han/fu/score facts.");
                    }

                    RoundResult result = request.Round.Result ??
                        throw new InvalidOperationException("A win event requires the committed round result.");
                    return new TableProjectionEventFacts(
                        turnNumber: options.TurnNumber,
                        loserSeat: result.Outcome.LoserSeat >= 0 ? result.Outcome.LoserSeat : (int?)null,
                        yaku: options.WinYaku,
                        han: options.WinHan,
                        fu: options.WinFu,
                        score: options.WinScore);
                case RoundEventType.ExhaustiveDraw:
                case RoundEventType.AbortiveDraw:
                    RoundResult draw = request.Round.Result ??
                        throw new InvalidOperationException("A draw event requires the committed round result.");
                    return new TableProjectionEventFacts(
                        turnNumber: options.TurnNumber,
                        tenpaiPlayers: draw.Outcome.TenpaiSeats);
                case RoundEventType.RoundEnded:
                    return new TableProjectionEventFacts(
                        turnNumber: options.TurnNumber,
                        roundWind: unchecked((uint)request.Round.RoundWind),
                        roundNumber: unchecked((uint)request.Round.RoundNumber));
                default:
                    return null;
            }
        }

        internal static ProtocolEventType MapType(RoundEventType type)
        {
            switch (type)
            {
                case RoundEventType.TilesDealt: return ProtocolEventType.DealingComplete;
                case RoundEventType.DoraRevealed: return ProtocolEventType.DoraRevealed;
                case RoundEventType.TileDrawn: return ProtocolEventType.TileDrawn;
                case RoundEventType.RinshanDrawn: return ProtocolEventType.RinshanDraw;
                case RoundEventType.TileDiscarded: return ProtocolEventType.TileDiscarded;
                case RoundEventType.KanDeclared: return ProtocolEventType.KanDeclared;
                case RoundEventType.PonDeclared: return ProtocolEventType.PonDeclared;
                case RoundEventType.ChiDeclared: return ProtocolEventType.ChiDeclared;
                case RoundEventType.RiichiDeclared: return ProtocolEventType.RiichiDeclared;
                case RoundEventType.TsumoWin: return ProtocolEventType.TsumoWin;
                case RoundEventType.RonWin: return ProtocolEventType.RonWin;
                case RoundEventType.ExhaustiveDraw:
                case RoundEventType.AbortiveDraw: return ProtocolEventType.DrawGame;
                case RoundEventType.RoundEnded: return ProtocolEventType.RoundEnded;
                default:
                    throw new InvalidOperationException("No frozen protocol event type for " + type + ".");
            }
        }
    }

    internal static class WireCheat
    {
        /// <summary>
        /// Owner data with an absolute off-turn deadline 2000 at the actor clock 1000. The wire
        /// must carry remaining=1000, never the absolute value.
        /// </summary>
        public static CheatState Rich(RoundStateMachine round, int seat = 0)
        {
            CheatState state = V32Fixtures.WithRegisteredRound(V32Fixtures.Cheat(), round.RoundId);
            state = WithSeatContent(state, round, seat);
            return state;
        }

        /// <summary>Per-seat ledger/mark/window/off-turn/reservation plus seat-0 immediate/beam.</summary>
        public static CheatState ForAll(RoundStateMachine round)
        {
            CheatState state = V32Fixtures.WithRegisteredRound(V32Fixtures.Cheat(), round.RoundId);
            for (int seat = 0; seat < 4; seat++)
            {
                state = WithSeatContent(state, round, seat);
            }

            SleeveBloomState sleeve = state.SleeveBloom.WithImmediateOffer(new SleeveBloomOffer(
                "immediate-0",
                0,
                DomainSleeveBloomDrawKind.Normal,
                new TileId(4),
                new TileId(5),
                1,
                SleeveBloomOfferMode.Immediate));
            state = state.WithSleeveBloom(sleeve);

            var beam = new BeamSwapOffer(
                "beam-0",
                0,
                BeamSwapWindow.Wall,
                new[] { new BeamSwapCandidate(new TileId(8), BeamSwapSlot.Wall(54)) },
                new[] { new TileId(9) },
                new TileId(10),
                1);
            return state.WithBeamSwap(state.BeamSwap.WithOffer(beam));
        }

        private static CheatState WithSeatContent(CheatState state, RoundStateMachine round, int seat)
        {
            state = V32Fixtures.WithLedger(
                state,
                new CheatLedgerEntry("ledger-" + seat, seat, -5, 995, "TILE_MARK"));

            TileMarkTransition mark = state.TileMarks.AddGroup(new TileMarkGroup(
                "group-" + seat,
                seat,
                new[] { new TileId(0), new TileId(1), new TileId(2), new TileId(3) },
                round.RoundId,
                1));
            if (!mark.Success || mark.State == null)
            {
                throw new InvalidOperationException("Fixture mark group was rejected: " + mark.Error);
            }

            state = state.WithTileMarks(mark.State);
            SleeveBloomState sleeve = state.SleeveBloom;
            sleeve = sleeve.WithOpenedWindow(seat, 3, out string windowId);
            var offTurn = new SleeveBloomOffer(
                "offturn-" + seat,
                seat,
                DomainSleeveBloomDrawKind.Normal,
                new TileId(6),
                new TileId(7),
                3,
                SleeveBloomOfferMode.OffTurn,
                windowId,
                2000L,
                1000L);
            sleeve = sleeve.WithOffTurnOffer(offTurn);
            sleeve = sleeve.WithReservation(new SleeveBloomReservation(
                seat,
                offTurn.OfferId,
                new TileId(6),
                windowId,
                1000L));
            return state.WithSleeveBloom(sleeve);
        }
    }

    internal sealed class QueueClientChannel : ITableChannel
    {
        private readonly Queue<byte[]> _inbound = new Queue<byte[]>();

        public List<byte[]> Sent { get; } = new List<byte[]>();

        public bool IsOpen { get; private set; }

        public Task OpenAsync(TableChannelConnectRequest request, CancellationToken cancellationToken)
        {
            IsOpen = true;
            return Task.CompletedTask;
        }

        public Task SendAsync(ReadOnlyMemory<byte> wireEnvelope, CancellationToken cancellationToken)
        {
            Sent.Add(wireEnvelope.ToArray());
            return Task.CompletedTask;
        }

        public Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (_inbound.Count == 0)
            {
                throw new TableChannelClosedException("NO_INBOUND", false);
            }

            return Task.FromResult<ReadOnlyMemory<byte>>(_inbound.Dequeue());
        }

        public Task CloseAsync(TableChannelCloseReason reason, CancellationToken cancellationToken)
        {
            IsOpen = false;
            return Task.CompletedTask;
        }

        public void Enqueue(byte[] bytes)
        {
            _inbound.Enqueue(bytes);
        }

        public Envelope LastSent(MessageType type)
        {
            for (int i = Sent.Count - 1; i >= 0; i--)
            {
                WireDecodeResult decoded = WireCodec.Decode(Sent[i]);
                if (decoded.IsOk && decoded.Envelope != null && decoded.Envelope.MessageType == type)
                {
                    return decoded.Envelope;
                }
            }

            throw new InvalidOperationException("No sent envelope of type " + type + ".");
        }
    }
}
