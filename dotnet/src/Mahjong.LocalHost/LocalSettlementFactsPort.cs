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
            for (int seat = 0; seat < 4; seat++)
            {
                seats.Add(new TableSettlementSeatHostFacts(seat));
            }

            return new TableSettlementHostFacts(source, seats);
        }
    }
}
