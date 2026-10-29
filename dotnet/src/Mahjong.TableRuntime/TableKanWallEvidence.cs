using System;
using System.Collections.Generic;
using Mahjong.Contracts;
using Mahjong.Domain;

namespace Mahjong.TableRuntime
{
    /// <summary>
    /// Reconstructs the live-wall and dead-wall-replacement evidence for a committed event
    /// history using the Domain's actual physical model:
    ///
    /// - every normal draw moves one tile out of the live wall;
    /// - every kan replacement draw moves one tile out of the live wall into the dead wall and
    ///   then moves the dead-wall rinshan tile into the hand;
    /// - KanDeclared itself moves no wall tile (the kan's tiles come from the hand/meld, and
    ///   dora indicators are already dead-wall entities).
    ///
    /// Therefore walking the history backwards from the trusted final wall increments the
    /// counter for both TaskDrawn and RinshanDrawn. This is the evidence the previous inverse
    /// model omitted; it is never guessed from a wallRemaining default.
    /// </summary>
    internal sealed class TableKanWallEvidence
    {
        private TableKanWallEvidence(
            IReadOnlyDictionary<ulong, uint> wallRemainingBySeq,
            IReadOnlyDictionary<ulong, MeldView> kanMeldBySeq,
            int kanCount,
            int rinshanCount)
        {
            WallRemainingBySeq = wallRemainingBySeq;
            KanMeldBySeq = kanMeldBySeq;
            KanCount = kanCount;
            RinshanCount = rinshanCount;
        }

        internal IReadOnlyDictionary<ulong, uint> WallRemainingBySeq { get; }

        internal IReadOnlyDictionary<ulong, MeldView> KanMeldBySeq { get; }

        internal int KanCount { get; }

        internal int RinshanCount { get; }

        internal static TableKanWallEvidence Reconstruct(
            IReadOnlyList<TableEventRecord> history,
            RoundStateMachine finalRound)
        {
            if (history == null)
            {
                throw new ArgumentNullException(nameof(history));
            }

            if (finalRound == null)
            {
                throw new ArgumentNullException(nameof(finalRound));
            }

            ValidateHistoryOrder(history);

            var expectedWall = new Dictionary<ulong, uint>();
            long wall = finalRound.Tiles.WallRemaining;
            if (wall < 0 || wall > TilePositionMap.LiveWallSlotCount)
            {
                throw Invalid("The recovered live wall is outside the 0..122 physical range.");
            }

            for (int i = history.Count - 1; i >= 0; i--)
            {
                TableEventRecord record = history[i];
                switch (record.Type)
                {
                    case RoundEventType.TileDrawn:
                        if (wall < 0 || wall > TilePositionMap.LiveWallSlotCount)
                        {
                            throw Invalid(
                                "Recovered wall remaining is outside 0..122 at event " + record.Seq + ".");
                        }

                        expectedWall[record.Seq.Value] = unchecked((uint)wall);
                        wall++;
                        break;
                    case RoundEventType.RinshanDrawn:
                        // The replacement tile leaves the live-wall tail, so the live count also
                        // decreases by exactly one here.
                        wall++;
                        break;
                    case RoundEventType.KanDeclared:
                        // No live-wall movement in the declaration itself.
                        break;
                }

                if (wall > TilePositionMap.LiveWallSlotCount)
                {
                    throw Invalid("Recovered wall exceeded the physical live-wall slot count.");
                }
            }

            List<TableEventRecord> kanRecords = Filter(history, RoundEventType.KanDeclared);
            List<TableEventRecord> rinshanRecords = Filter(history, RoundEventType.RinshanDrawn);
            ValidateKanRinshanPairing(kanRecords, rinshanRecords);

            IReadOnlyList<RoundEvent> domainEvents = finalRound.Events;
            int domainKans = Count(domainEvents, RoundEventType.KanDeclared);
            int domainRinshan = Count(domainEvents, RoundEventType.RinshanDrawn);
            if (domainKans != finalRound.Tiles.KanCount)
            {
                throw Invalid(
                    "The recovered round's kan count does not match its committed kan events.");
            }

            if (domainRinshan != finalRound.Tiles.RinshanPointer ||
                finalRound.Tiles.ReplacementCount != finalRound.Tiles.RinshanPointer)
            {
                throw Invalid(
                    "The recovered round's rinshan/dead-wall replacement counters are inconsistent.");
            }

            bool hasDeal = Contains(domainEvents, RoundEventType.TilesDealt);
            if (hasDeal)
            {
                long totalDraws = finalRound.Events.Count == 0
                    ? 0L
                    : Count(domainEvents, RoundEventType.TileDrawn) + domainRinshan;
                if (finalRound.Tiles.WallRemaining + totalDraws !=
                    TilePositionMap.LiveWallSlotCount - TilePositionMap.InitialDealCount)
                {
                    throw Invalid(
                        "The recovered final wall plus its committed draw events does not equal " +
                        "the post-deal live-wall count.");
                }
            }

            bool fullHistory = history.Count == 0 || history[0].CommandVersion.Value == 1;
            if (fullHistory)
            {
                if (kanRecords.Count != domainKans || rinshanRecords.Count != domainRinshan)
                {
                    throw Invalid(
                        "The committed history does not cover every recovered kan/replacement event.");
                }

                // Every kan occupies a distinct 0..3 replacement slot and the counts are already
                // proven equal above; enforce the bound explicitly so a malformed final pointer
                // cannot pass through.
                if (rinshanRecords.Count > 4)
                {
                    throw Invalid("More than four replacement draws cannot fit the dead wall.");
                }
            }
            else
            {
                if (rinshanRecords.Count > domainRinshan || kanRecords.Count > domainKans)
                {
                    throw Invalid(
                        "A partial committed history claims more kan/replacement events than the round.");
                }
            }

            Dictionary<ulong, MeldView> kanMelds = MatchKanMelds(kanRecords, finalRound);
            return new TableKanWallEvidence(
                expectedWall,
                kanMelds,
                kanRecords.Count,
                rinshanRecords.Count);
        }

        internal static bool Matches(MeldView actual, MeldView expected)
        {
            if (actual == null || expected == null)
            {
                return actual == expected;
            }

            if (actual.Type != expected.Type ||
                actual.CalledTileId != expected.CalledTileId ||
                actual.FromSeat != expected.FromSeat ||
                actual.TileIds.Count != expected.TileIds.Count)
            {
                return false;
            }

            for (int i = 0; i < actual.TileIds.Count; i++)
            {
                if (actual.TileIds[i] != expected.TileIds[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static void ValidateHistoryOrder(IReadOnlyList<TableEventRecord> history)
        {
            ulong previousSeq = 0;
            ulong previousVersion = 0;
            int previousOrdinal = -1;
            for (int i = 0; i < history.Count; i++)
            {
                TableEventRecord record = history[i] ??
                    throw Invalid("Committed history contains a null event.");
                if (record.Seq.Value <= previousSeq)
                {
                    throw Invalid("Committed history is not strictly ordered by event_seq.");
                }

                if (record.CommandVersion.Value < previousVersion)
                {
                    throw Invalid("Committed history command versions are out of order.");
                }

                if (record.CommandVersion.Value == previousVersion)
                {
                    if (record.Ordinal != previousOrdinal + 1)
                    {
                        throw Invalid("Committed history ordinals are not contiguous within a command.");
                    }
                }
                else if (record.Ordinal != 0)
                {
                    throw Invalid("A new command must start at event ordinal zero.");
                }

                previousSeq = record.Seq.Value;
                previousVersion = record.CommandVersion.Value;
                previousOrdinal = record.Ordinal;
            }
        }

        private static void ValidateKanRinshanPairing(
            IReadOnlyList<TableEventRecord> kanRecords,
            IReadOnlyList<TableEventRecord> rinshanRecords)
        {
            var paired = new HashSet<ulong>();
            for (int i = 0; i < rinshanRecords.Count; i++)
            {
                TableEventRecord rinshan = rinshanRecords[i];
                TableEventRecord? match = null;
                for (int k = 0; k < kanRecords.Count; k++)
                {
                    TableEventRecord kan = kanRecords[k];
                    if (kan.CommandVersion != rinshan.CommandVersion ||
                        kan.Seat != rinshan.Seat ||
                        kan.Ordinal >= rinshan.Ordinal)
                    {
                        continue;
                    }

                    if (match == null || kan.Ordinal > match.Ordinal)
                    {
                        match = kan;
                    }
                }

                if (match == null)
                {
                    throw Invalid(
                        "RinshanDrawn at " + rinshan.Seq + " has no KanDeclared in its command.");
                }

                paired.Add(match.Seq.Value);
            }

            for (int i = 0; i < kanRecords.Count; i++)
            {
                if (!paired.Contains(kanRecords[i].Seq.Value))
                {
                    throw Invalid(
                        "KanDeclared at " + kanRecords[i].Seq + " has no replacement draw in its command.");
                }
            }
        }

        private static Dictionary<ulong, MeldView> MatchKanMelds(
            IReadOnlyList<TableEventRecord> kanRecords,
            RoundStateMachine finalRound)
        {
            var meldsBySeat = new Dictionary<int, List<RoundMeld>>();
            for (int i = 0; i < finalRound.Melds.Count; i++)
            {
                RoundMeld meld = finalRound.Melds[i];
                if (meld.Kind != RoundMeldKind.OpenKan &&
                    meld.Kind != RoundMeldKind.ClosedKan &&
                    meld.Kind != RoundMeldKind.AddedKan)
                {
                    continue;
                }

                if (!meldsBySeat.TryGetValue(meld.Seat, out List<RoundMeld>? list))
                {
                    list = new List<RoundMeld>();
                    meldsBySeat[meld.Seat] = list;
                }

                list.Add(meld);
            }

            var result = new Dictionary<ulong, MeldView>();
            var used = new Dictionary<int, HashSet<int>>();
            for (int i = 0; i < kanRecords.Count; i++)
            {
                TableEventRecord record = kanRecords[i];
                if (!meldsBySeat.TryGetValue(record.Seat, out List<RoundMeld>? seatMelds) ||
                    !record.Tile.HasValue)
                {
                    throw Invalid("KanDeclared at " + record.Seq + " has no recovered kan meld.");
                }

                if (!used.TryGetValue(record.Seat, out HashSet<int>? usedIndices))
                {
                    usedIndices = new HashSet<int>();
                    used[record.Seat] = usedIndices;
                }

                RoundMeld? match = null;
                int matchIndex = -1;
                for (int m = 0; m < seatMelds.Count; m++)
                {
                    if (usedIndices.Contains(m) ||
                        !KindMatchesDetail(seatMelds[m].Kind, record.Detail) ||
                        !ContainsTile(seatMelds[m], record.Tile.Value))
                    {
                        continue;
                    }

                    match = seatMelds[m];
                    matchIndex = m;
                    break;
                }

                if (match == null)
                {
                    throw Invalid(
                        "KanDeclared at " + record.Seq + " has no exact recovered Domain meld.");
                }

                usedIndices.Add(matchIndex);
                ProtocolMeldType type = match.Kind == RoundMeldKind.OpenKan
                    ? ProtocolMeldType.OpenKan
                    : match.Kind == RoundMeldKind.ClosedKan
                        ? ProtocolMeldType.ClosedKan
                        : ProtocolMeldType.AddedKan;
                bool closed = match.Kind == RoundMeldKind.ClosedKan;
                var tiles = new uint[match.Tiles.Count];
                for (int t = 0; t < tiles.Length; t++)
                {
                    tiles[t] = unchecked((uint)match.Tiles[t].Value);
                }

                result[record.Seq.Value] = new MeldView(
                    type,
                    tiles,
                    closed ? (uint?)null : unchecked((uint)match.CalledTile.Value),
                    closed ? ProtocolConstants.NoSeat : match.FromSeat);
            }

            int totalMelds = 0;
            foreach (KeyValuePair<int, List<RoundMeld>> entry in meldsBySeat)
            {
                totalMelds += entry.Value.Count;
            }

            if (totalMelds < kanRecords.Count)
            {
                throw Invalid("The committed history claims more kan records than recovered melds.");
            }

            return result;
        }

        private static bool KindMatchesDetail(RoundMeldKind kind, string? detail)
        {
            return kind == RoundMeldKind.OpenKan
                ? string.Equals(detail, "OPEN_KAN", StringComparison.Ordinal)
                : kind == RoundMeldKind.ClosedKan
                    ? string.Equals(detail, "CLOSED_KAN", StringComparison.Ordinal)
                    : kind == RoundMeldKind.AddedKan &&
                      string.Equals(detail, "ADDED_KAN", StringComparison.Ordinal);
        }

        private static bool ContainsTile(RoundMeld meld, TileId tile)
        {
            for (int i = 0; i < meld.Tiles.Count; i++)
            {
                if (meld.Tiles[i] == tile)
                {
                    return true;
                }
            }

            return false;
        }

        private static List<TableEventRecord> Filter(
            IReadOnlyList<TableEventRecord> history,
            RoundEventType type)
        {
            var result = new List<TableEventRecord>();
            for (int i = 0; i < history.Count; i++)
            {
                if (history[i].Type == type)
                {
                    result.Add(history[i]);
                }
            }

            return result;
        }

        private static int Count(IReadOnlyList<RoundEvent> events, RoundEventType type)
        {
            int count = 0;
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i].Type == type)
                {
                    count++;
                }
            }

            return count;
        }

        private static bool Contains(IReadOnlyList<RoundEvent> events, RoundEventType type)
        {
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i].Type == type)
                {
                    return true;
                }
            }

            return false;
        }

        private static TableProjectionException Invalid(string message)
        {
            return new TableProjectionException(TableProjectionErrorCode.InvalidContext, message);
        }
    }
}
