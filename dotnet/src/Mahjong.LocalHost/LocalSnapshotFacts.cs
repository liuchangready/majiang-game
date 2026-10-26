using System;
using System.Collections.Generic;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Mahjong.TableRuntime;

namespace Mahjong.LocalHost
{
    /// <summary>
    /// Trusted local composition facts. Domain state supplies every modelled value; only the
    /// explicitly unmodelled projection facts are derived here from the committed round/events.
    /// The source never reads the wall order into a projection and never invents a cheat value.
    /// </summary>
    internal sealed class LocalSnapshotFacts : ITableProjectionContextSource
    {
        internal static readonly CheatEconomyConfig CheatEconomy = new CheatEconomyConfig(
            true,
            120,
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                { CheatEconomyConfig.TileMarkCostKey, 1 },
                { CheatEconomyConfig.SleeveBloomCostKey, 10 },
                { CheatEconomyConfig.BeamSwapCostKey, 10 }
            });

        internal static readonly SleeveBloomConfig SleeveBloom = new SleeveBloomConfig(true, 5_000L);

        internal static readonly CheatState InitialCheatState = CheatState.Initialize(CheatEconomy);

        private readonly LocalHostEnvironment _environment;
        private readonly ITableClock _clock;

        internal Func<Mahjong.Domain.Rules.RoundSettlementCalculation?>? SettlementProvider { get; set; }

        internal TableProjectionContextData? RecoveredFacts { get; set; }

        public LocalSnapshotFacts(LocalHostEnvironment environment, ITableClock clock)
        {
            _environment = environment;
            _clock = clock;
        }

        public RoundStateMachine CreateInitialRound(LocalTableHostOptions options)
        {
            return CreateRound(
                options,
                options.RoomId + "-r1",
                0,
                1,
                0,
                0,
                new long[] { 25_000L, 25_000L, 25_000L, 25_000L },
                0);
        }

        public RoundStateMachine CreateRound(
            LocalTableHostOptions options,
            string roundId,
            int dealerSeat,
            int roundWind,
            int roundNumber,
            int honba,
            IReadOnlyList<long> scores,
            int riichiSticks)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            var order = new List<TileId>(TileId.Count);
            for (int i = 0; i < TileId.Count; i++)
            {
                order.Add(new TileId(i));
            }

            for (int i = order.Count - 1; i > 0; i--)
            {
                int j = _environment.ResolveInt(i + 1);
                TileId swap = order[i];
                order[i] = order[j];
                order[j] = swap;
            }

            RecordedRandomDraw wall = RecordedRandomDraw.Create(roundId + "-wall", order);
            TilePositionMap dealt = TilePositionMap.CreateDealt(wall, dealerSeat);
            var spec = new RoundSpec(
                roundId,
                dealerSeat,
                roundWind,
                roundNumber,
                honba,
                scores,
                riichiSticks);
            return RoundStateMachine.CreateDealt(spec, dealt, RoundPhase.CheatWindow);
        }

        public TableCheckpoint CreateInitialCheckpoint(LocalTableHostOptions options, RoundStateMachine round)
        {
            var controllers = new Dictionary<int, SeatControllerKind>
            {
                { 0, SeatControllerKind.LocalHuman },
                { 1, SeatControllerKind.Computer },
                { 2, SeatControllerKind.Computer },
                { 3, SeatControllerKind.Computer }
            };
            var registry = new ControlRegistry(new DisconnectPolicy(), controllers);
            var clock = new DecisionClock(new DecisionClockConfig(20_000L, 3_000L, ReserveScope.Round));
            clock.BeginRound(round.RoundId, 0L);
            ulong utc = NowUtc();
            string boot = _environment.ResolveId();
            TableTimingCheckpoint timing = TableCheckpointFactory.CaptureTiming(
                clock,
                Array.Empty<PendingDecision>(),
                0L,
                utc,
                boot);

            CheatState cheat = InitialCheatState;
            TileMarkTransition registration = cheat.TileMarks.RegisterDealtRound(round.RoundId);
            if (!registration.Success)
            {
                throw new LocalTableException(
                    LocalTableErrorKind.Unavailable,
                    "The initial cheat state cannot register the round: " + registration.Error);
            }

            cheat = cheat.WithTileMarks(registration.State);
            TableProjectionContextData facts = Capture(new TableProjectionContextRequest(
                options.RoomId,
                round,
                new StateVersion(0),
                new EventSeq(0),
                new OwnerEpoch(1),
                0L,
                0L,
                0L,
                (long)utc,
                Array.Empty<TableEventRecord>()));
            byte[] factsPayload = TableProjectionFactsCodec.Encode(facts);

            return TableCheckpointFactory.Create(
                options.RoomId,
                round,
                new StateVersion(0),
                new EventSeq(0),
                new TableControlCheckpoint(registry.Capture(), 0L),
                timing,
                options.RulesetVersion,
                boot,
                utc,
                TableCheatLedgerCheckpoint.FromCheatState(cheat),
                factsPayload,
                1L,
                0L,
                Array.Empty<TableEventRecord>(),
                Array.Empty<TableCommandBinding>());
        }

        public TableProjectionContextData Capture(TableProjectionContextRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            RoundStateMachine round = request.Round;
            if (RecoveredFacts != null &&
                string.Equals(RecoveredFacts.RoundId, round.RoundId, StringComparison.Ordinal) &&
                RecoveredFacts.StateVersion == request.StateVersion)
            {
                return RecoveredFacts;
            }
            var publicFacts = new List<TableSeatPublicFacts>(4);
            var privateFacts = new List<TableSeatPrivateFacts>(4);
            int[] discardCounters = new int[4];
            for (int seat = 0; seat < 4; seat++)
            {
                var discards = new List<DiscardView>();
                TileId? seatDraw = null;
                bool riichiPending = false;
                for (int i = 0; i < request.CommittedEvents.Count; i++)
                {
                    TableEventRecord record = request.CommittedEvents[i];
                    if (record.Seat != seat)
                    {
                        continue;
                    }

                    if (record.Type == RoundEventType.RiichiDeclared)
                    {
                        riichiPending = true;
                        continue;
                    }

                    if (record.Type == RoundEventType.TileDrawn || record.Type == RoundEventType.RinshanDrawn)
                    {
                        seatDraw = record.Tile;
                        continue;
                    }

                    if (record.Type != RoundEventType.TileDiscarded || !record.Tile.HasValue)
                    {
                        continue;
                    }

                    discardCounters[seat]++;
                    bool tsumogiri = seatDraw.HasValue && seatDraw.Value == record.Tile.Value;
                    discards.Add(new DiscardView(
                        unchecked((uint)record.Tile.Value.Value),
                        unchecked((uint)discardCounters[seat]),
                        riichiPending,
                        tsumogiri));
                    riichiPending = false;
                    seatDraw = null;
                }

                var melds = new List<TableMeldFact>();
                for (int i = 0; i < round.Melds.Count; i++)
                {
                    RoundMeld meld = round.Melds[i];
                    if (meld.Seat != seat)
                    {
                        continue;
                    }

                    var tiles = new List<uint>(meld.Tiles.Count);
                    for (int t = 0; t < meld.Tiles.Count; t++)
                    {
                        tiles.Add(unchecked((uint)meld.Tiles[t].Value));
                    }

                    melds.Add(new TableMeldFact(
                        MapMeld(meld.Kind),
                        tiles,
                        unchecked((uint)meld.CalledTile.Value),
                        meld.FromSeat));
                }

                publicFacts.Add(new TableSeatPublicFacts(seat, round.IsRiichi(seat), melds, discards));
                var history = new List<uint>(discards.Count);
                for (int i = 0; i < discards.Count; i++)
                {
                    history.Add(discards[i].TileId);
                }

                privateFacts.Add(new TableSeatPrivateFacts(seat, false, false, history));
            }

            TableSessionProjection? session = null;
            if (round.IsFinished && round.GetResult() != null)
            {
                RoundResult result = round.GetResult()!;
                session = new TableSessionProjection(
                    false,
                    string.Empty,
                    result.FinalScores,
                    null,
                    result.RoundId);
            }

            return new TableProjectionContextData(
                round.RoundId,
                request.StateVersion,
                1UL,
                InitialCheatState,
                publicFacts,
                privateFacts,
                session,
                new TableProjectionPolicy(false, round.IsFinished, round.IsFinished),
                BuildEventFacts(request));
        }

        private IReadOnlyList<TableProjectionEventFact> BuildEventFacts(TableProjectionContextRequest request)
        {
            var facts = new List<TableProjectionEventFact>();
            RoundStateMachine round = request.Round;
            var wallBySeq = new Dictionary<ulong, uint>();
            long wall = round.Tiles.WallRemaining;
            for (int i = request.CommittedEvents.Count - 1; i >= 0; i--)
            {
                TableEventRecord record = request.CommittedEvents[i];
                if (record.Type == RoundEventType.TileDrawn)
                {
                    wallBySeq[record.Seq.Value] = unchecked((uint)Math.Max(0L, wall));
                    wall++;
                }
            }

            int discardOrdinal = 0;
            for (int i = 0; i < request.CommittedEvents.Count; i++)
            {
                TableEventRecord record = request.CommittedEvents[i];
                ProtocolEventType type;
                TableProjectionEventFacts? value = null;
                switch (record.Type)
                {
                    case RoundEventType.TilesDealt:
                    case RoundEventType.DoraRevealed:
                        type = record.Type == RoundEventType.TilesDealt
                            ? ProtocolEventType.DealingComplete
                            : ProtocolEventType.DoraRevealed;
                        value = new TableProjectionEventFacts(turnNumber: 1U);
                        break;
                    case RoundEventType.TileDrawn:
                        type = ProtocolEventType.TileDrawn;
                        value = new TableProjectionEventFacts(
                            turnNumber: unchecked((uint)(i + 1)),
                            wallRemaining: wallBySeq.TryGetValue(record.Seq.Value, out uint wallRemaining)
                                ? wallRemaining
                                : unchecked((uint)Math.Max(0L, round.Tiles.WallRemaining)));
                        break;
                    case RoundEventType.RinshanDrawn:
                        type = ProtocolEventType.RinshanDraw;
                        value = new TableProjectionEventFacts(turnNumber: unchecked((uint)(i + 1)));
                        break;
                    case RoundEventType.TileDiscarded:
                        discardOrdinal++;
                        type = ProtocolEventType.TileDiscarded;
                        value = new TableProjectionEventFacts(
                            turnNumber: unchecked((uint)discardOrdinal),
                            riichiDiscard: false,
                            tsumogiri: false,
                            discardTileId: record.Tile.HasValue
                                ? unchecked((uint)record.Tile.Value.Value)
                                : (uint?)null);
                        break;
                    case RoundEventType.KanDeclared:
                        type = ProtocolEventType.KanDeclared;
                        value = new TableProjectionEventFacts(turnNumber: unchecked((uint)(i + 1)));
                        break;
                    case RoundEventType.TsumoWin:
                    case RoundEventType.RonWin:
                        type = record.Type == RoundEventType.TsumoWin
                            ? ProtocolEventType.TsumoWin
                            : ProtocolEventType.RonWin;
                        RoundResult? win = round.GetResult();
                        if (win == null)
                        {
                            continue;
                        }

                        Mahjong.Domain.Rules.RoundSettlementCalculation? calculation = SettlementProvider?.Invoke();
                        IReadOnlyList<YakuView>? yaku = null;
                        uint? han = null;
                        uint? fu = null;
                        long? score = null;
                        if (calculation != null &&
                            string.Equals(calculation.SourceRoundId, round.RoundId, StringComparison.Ordinal) &&
                            calculation.Breakdown != null)
                        {
                            var views = new List<YakuView>();
                            for (int y = 0; y < calculation.Breakdown.Yaku.Count; y++)
                            {
                                Mahjong.Domain.Rules.YakuHit hit = calculation.Breakdown.Yaku[y];
                                if (hit.Han > 0)
                                {
                                    views.Add(new YakuView(hit.Code, unchecked((uint)hit.Han)));
                                }
                            }

                            yaku = views;
                            han = unchecked((uint)calculation.Breakdown.TotalYakuHan);
                            fu = unchecked((uint)calculation.Breakdown.Fu.FinalFu);
                            score = calculation.Breakdown.TotalPoints;
                        }

                        value = new TableProjectionEventFacts(
                            turnNumber: unchecked((uint)(i + 1)),
                            loserSeat: win.Outcome.LoserSeat >= 0 ? win.Outcome.LoserSeat : (int?)null,
                            yaku: yaku,
                            han: han,
                            fu: fu,
                            score: score,
                            finalScores: Scores(win.FinalScores));
                        break;
                    case RoundEventType.ExhaustiveDraw:
                    case RoundEventType.AbortiveDraw:
                        type = ProtocolEventType.DrawGame;
                        RoundResult? draw = round.GetResult();
                        if (draw == null)
                        {
                            continue;
                        }

                        value = new TableProjectionEventFacts(
                            turnNumber: unchecked((uint)(i + 1)),
                            tenpaiPlayers: draw.Outcome.TenpaiSeats,
                            finalScores: Scores(draw.FinalScores));
                        break;
                    case RoundEventType.RoundEnded:
                        type = ProtocolEventType.RoundEnded;
                        value = new TableProjectionEventFacts(
                            turnNumber: unchecked((uint)(i + 1)),
                            roundWind: unchecked((uint)round.RoundWind),
                            roundNumber: unchecked((uint)round.RoundNumber));
                        break;
                    default:
                        continue;
                }

                if (value != null)
                {
                    facts.Add(new TableProjectionEventFact(
                        type,
                        record.Seq,
                        record.CommandVersion,
                        unchecked((uint)record.Ordinal),
                        value));
                }
            }

            return facts;
        }

        private static IReadOnlyList<ScoreView> Scores(IReadOnlyList<long> values)
        {
            var list = new List<ScoreView>(values.Count);
            for (int i = 0; i < values.Count; i++)
            {
                list.Add(new ScoreView(i, values[i]));
            }

            return list;
        }

        private static ProtocolMeldType MapMeld(RoundMeldKind kind)
        {
            switch (kind)
            {
                case RoundMeldKind.Chi: return ProtocolMeldType.Chi;
                case RoundMeldKind.Pon: return ProtocolMeldType.Pon;
                case RoundMeldKind.OpenKan: return ProtocolMeldType.OpenKan;
                case RoundMeldKind.AddedKan: return ProtocolMeldType.AddedKan;
                case RoundMeldKind.ClosedKan: return ProtocolMeldType.ClosedKan;
                default: throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown meld kind.");
            }
        }

        private ulong NowUtc()
        {
            long value = _clock.UtcNowMs;
            return value < 0 ? 0UL : (ulong)value;
        }
    }
}
