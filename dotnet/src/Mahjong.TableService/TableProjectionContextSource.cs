using System;
using System.Collections.Generic;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Mahjong.TableRuntime;

namespace Mahjong.TableService
{
    /// <summary>
    /// The service-side trusted projection context port (the P3.2c caller duty): maps the
    /// actor's committed Domain round and event log into frozen projection facts. Values the
    /// Domain models come from the request's immutable records; values the Domain does not
    /// model (turn counter, riichi/tsumogiri discard flags, kan meld shapes, yaku scoring)
    /// use the same frozen defaults the accepted Wire joint fixtures pin, and scoring-bearing
    /// events fail closed until a real rules authority lands (P5.2+ boundary).
    /// </summary>
    public sealed class TableProjectionContextSource : ITableProjectionContextSource
    {
        /// <summary>
        /// The service-level cheat economy, shaped like the accepted TableRuntime fixtures
        /// (tile mark 5, sleeve bloom 10, beam swap 10). The actor owns this economy and
        /// overlays its own trusted cheat aggregate onto every capture, so the aggregate
        /// built here is only a validly shaped placeholder for the seat-stream guard.
        /// </summary>
        internal static readonly CheatEconomyConfig DefaultEconomy = CreateDefaultEconomy();

        private static CheatEconomyConfig CreateDefaultEconomy()
        {
            var costs = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                [CheatEconomyConfig.TileMarkCostKey] = 5,
                [CheatEconomyConfig.SleeveBloomCostKey] = 10,
                [CheatEconomyConfig.BeamSwapCostKey] = 10
            };
            return new CheatEconomyConfig(true, 1000, costs);
        }

        /// <summary>The actor options require a sleeve config whenever an economy is set.</summary>
        internal static readonly SleeveBloomConfig DefaultSleeveConfig = new SleeveBloomConfig(true, 5000L);

        public TableProjectionContextData Capture(TableProjectionContextRequest request)
        {
            return new TableProjectionContextData(
                request.Round.RoundId,
                request.StateVersion,
                1UL,
                cheat: CheatState.Initialize(DefaultEconomy), // seat streams require a non-null trusted aggregate; the actor overlays its own
                BuildPublicFacts(request.Round),
                BuildPrivateFacts(request.Round),
                new TableSessionProjection(false, null, null, null),
                new TableProjectionPolicy(),
                BuildEventFacts(request),
                concealedMeldFacts: null);
        }

        private static IReadOnlyList<TableSeatPublicFacts> BuildPublicFacts(RoundStateMachine round)
        {
            var result = new List<TableSeatPublicFacts>(4);
            for (int seat = 0; seat < 4; seat++)
            {
                IReadOnlyList<TileId> river = round.Tiles.TilesInZone(TileZone.Discard, seat);
                var discards = new List<DiscardView>(river.Count);
                for (int i = 0; i < river.Count; i++)
                {
                    discards.Add(new DiscardView(unchecked((uint)river[i].Value), unchecked((uint)i), false, false));
                }

                result.Add(new TableSeatPublicFacts(seat, riichi: false, melds: null, discards));
            }

            return result;
        }

        private static IReadOnlyList<TableSeatPrivateFacts> BuildPrivateFacts(RoundStateMachine round)
        {
            var result = new List<TableSeatPrivateFacts>(4);
            for (int seat = 0; seat < 4; seat++)
            {
                IReadOnlyList<TileId> river = round.Tiles.TilesInZone(TileZone.Discard, seat);
                var history = new List<uint>(river.Count);
                for (int i = 0; i < river.Count; i++)
                {
                    history.Add(unchecked((uint)river[i].Value));
                }

                result.Add(new TableSeatPrivateFacts(seat, furiten: false, hasPreparation: false, history));
            }

            return result;
        }

        private static IReadOnlyList<TableProjectionEventFact> BuildEventFacts(
            TableProjectionContextRequest request)
        {
            // Reconstruct each draw's wall count by replaying the committed draw events
            // backwards from the current wall total.
            var wallBySeq = new Dictionary<ulong, uint>();
            long wall = request.Round.Tiles.WallRemaining;
            for (int i = request.CommittedEvents.Count - 1; i >= 0; i--)
            {
                TableEventRecord record = request.CommittedEvents[i];
                if (record.Type == RoundEventType.TileDrawn)
                {
                    wallBySeq[record.Seq.Value] = unchecked((uint)wall);
                    wall++;
                }
            }

            var facts = new List<TableProjectionEventFact>(request.CommittedEvents.Count);
            for (int i = 0; i < request.CommittedEvents.Count; i++)
            {
                TableEventRecord record = request.CommittedEvents[i];
                uint? wallRemaining = wallBySeq.TryGetValue(record.Seq.Value, out uint value)
                    ? value
                    : (uint?)null;
                TableProjectionEventFacts? factsValue = BuildOne(request, record, wallRemaining);
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
            uint? wallRemaining)
        {
            // Turn number: the Domain round does not carry a turn counter; the frozen
            // fixture default (1) applies until a rules authority supplies real turns.
            const uint turnNumber = 1;
            switch (record.Type)
            {
                case RoundEventType.TilesDealt:
                case RoundEventType.DoraRevealed:
                case RoundEventType.RinshanDrawn:
                    return new TableProjectionEventFacts(turnNumber: turnNumber);
                case RoundEventType.TileDrawn:
                    return new TableProjectionEventFacts(
                        turnNumber: turnNumber,
                        wallRemaining: wallRemaining ??
                            unchecked((uint)request.Round.Tiles.WallRemaining));
                case RoundEventType.TileDiscarded:
                    return new TableProjectionEventFacts(
                        turnNumber: turnNumber,
                        riichiDiscard: false,
                        tsumogiri: false);
                case RoundEventType.KanDeclared:
                    // The Domain carries only the called tile; the full meld shape is a
                    // scoring-authority fact. Fail closed rather than guess.
                    throw new InvalidOperationException(
                        "KanDeclared projection requires a meld fact from a rules authority (P5.2+ boundary).");
                case RoundEventType.TsumoWin:
                case RoundEventType.RonWin:
                    // Yaku/han/fu/score are scoring-authority facts. Fail closed.
                    throw new InvalidOperationException(
                        "Win projection requires scoring facts from a rules authority (P5.2+ boundary).");
                case RoundEventType.ExhaustiveDraw:
                case RoundEventType.AbortiveDraw:
                    RoundResult draw = request.Round.Result ??
                        throw new InvalidOperationException("A draw event requires the committed round result.");
                    return new TableProjectionEventFacts(
                        turnNumber: turnNumber,
                        tenpaiPlayers: draw.Outcome.TenpaiSeats);
                case RoundEventType.RoundEnded:
                    return new TableProjectionEventFacts(
                        turnNumber: turnNumber,
                        roundWind: unchecked((uint)request.Round.RoundWind),
                        roundNumber: unchecked((uint)request.Round.RoundNumber));
                default:
                    return null;
            }
        }

        private static ProtocolEventType MapType(RoundEventType type)
        {
            switch (type)
            {
                case RoundEventType.TilesDealt: return ProtocolEventType.DealingComplete;
                case RoundEventType.DoraRevealed: return ProtocolEventType.DoraRevealed;
                case RoundEventType.TileDrawn: return ProtocolEventType.TileDrawn;
                case RoundEventType.RinshanDrawn: return ProtocolEventType.RinshanDraw;
                case RoundEventType.TileDiscarded: return ProtocolEventType.TileDiscarded;
                case RoundEventType.KanDeclared: return ProtocolEventType.KanDeclared;
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
}
