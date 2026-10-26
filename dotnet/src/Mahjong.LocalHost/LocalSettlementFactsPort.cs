using System;
using System.Collections.Generic;
using Mahjong.TableRuntime;

namespace Mahjong.LocalHost
{
    /// <summary>
    /// Real local N-2 host-fact source. The actor owns settlement calculation; this port only
    /// supplies the few trusted facts the frozen round model does not carry. Local single-table
    /// play has no external furiten/ippatsu source beyond the authoritative round, so the
    /// explicit facts are the local host's verified defaults for the current source binding.
    /// The source reference is echoed exactly; a stale/foreign binding is rejected by the actor.
    /// </summary>
    internal sealed class LocalSettlementFactsPort : ITableSettlementFactsPort
    {
        internal Func<Mahjong.Domain.RoundStateMachine?>? RoundProvider { get; set; }

        public TableSettlementHostFacts Resolve(TableSettlementSourceRef source, TableSettlementIntent intent)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (intent == null)
            {
                throw new ArgumentNullException(nameof(intent));
            }

            var seats = new List<TableSettlementSeatHostFacts>(4);
            Mahjong.Domain.RoundStateMachine? round = RoundProvider?.Invoke();
            for (int seat = 0; seat < 4; seat++)
            {
                bool ippatsu = false;
                bool temporaryFuriten = false;
                bool riichiFuriten = false;
                if (round != null)
                {
                    int lastDraw = -1;
                    int lastDiscard = -1;
                    int lastCallOrKan = -1;
                    int riichiAt = -1;
                    int drawsAfterRiichi = 0;
                    for (int i = 0; i < round.Events.Count; i++)
                    {
                        Mahjong.Domain.RoundEvent e = round.Events[i];
                        if (e.Type == Mahjong.Domain.RoundEventType.RiichiDeclared && e.Seat == seat)
                        {
                            riichiAt = i;
                            drawsAfterRiichi = 0;
                        }
                        else if (e.Seat == seat && e.Type == Mahjong.Domain.RoundEventType.TileDrawn)
                        {
                            lastDraw = i;
                            if (riichiAt >= 0)
                            {
                                drawsAfterRiichi++;
                            }
                        }
                        else if (e.Seat == seat && e.Type == Mahjong.Domain.RoundEventType.TileDiscarded)
                        {
                            lastDiscard = i;
                        }
                        else if (e.Type == Mahjong.Domain.RoundEventType.ResponseClaimed ||
                                 e.Type == Mahjong.Domain.RoundEventType.KanDeclared ||
                                 e.Type == Mahjong.Domain.RoundEventType.ChiDeclared ||
                                 e.Type == Mahjong.Domain.RoundEventType.PonDeclared)
                        {
                            lastCallOrKan = i;
                        }
                    }

                    temporaryFuriten = lastDiscard > lastDraw;
                    ippatsu = riichiAt >= 0 && lastCallOrKan < riichiAt &&
                              lastDiscard < riichiAt && drawsAfterRiichi <= 1;
                    riichiFuriten = riichiAt >= 0 && lastDiscard > riichiAt;
                }

                seats.Add(new TableSettlementSeatHostFacts(seat, ippatsu, temporaryFuriten, riichiFuriten));
            }

            return new TableSettlementHostFacts(source, seats);
        }
    }
}
