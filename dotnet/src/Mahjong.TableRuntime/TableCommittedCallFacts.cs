using System;
using System.Collections.Generic;
using System.Globalization;
using Mahjong.Contracts;
using Mahjong.Domain;

namespace Mahjong.TableRuntime
{
    /// <summary>
    /// Rebuilds public call/riichi event facts from the immutable committed round history.
    /// The journal event's physical called tile is the join key; current meld order is never
    /// used as an event identity.
    /// </summary>
    public static class TableCommittedCallFacts
    {
        public static MeldView RequireCallMeld(RoundStateMachine round, TableEventRecord record)
        {
            if (round == null) throw new ArgumentNullException(nameof(round));
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (!record.Tile.HasValue || (record.Type != RoundEventType.PonDeclared && record.Type != RoundEventType.ChiDeclared))
                throw new InvalidOperationException("A committed Pon/Chi fact requires its source event and called physical tile.");

            RoundEvent source = RequireRoundEvent(round, record);
            int fromSeat = ParseFromSeat(source, record.Type);
            RoundMeldKind required = record.Type == RoundEventType.PonDeclared ? RoundMeldKind.Pon : RoundMeldKind.Chi;
            RoundMeld? match = null;
            bool historicalPonUpgraded = false;
            foreach (RoundMeld candidate in round.Melds)
            {
                if (candidate.Seat != record.Seat || candidate.CalledTile != record.Tile.Value || candidate.FromSeat != fromSeat)
                    continue;

                if (candidate.Kind == required)
                {
                    if (match != null) throw new InvalidOperationException("A committed call matches multiple current meld records.");
                    match = candidate;
                    continue;
                }

                if (required == RoundMeldKind.Pon && candidate.Kind == RoundMeldKind.AddedKan &&
                    IsLaterAddedKan(round, source, candidate))
                {
                    if (match != null) throw new InvalidOperationException("A committed Pon matches multiple current meld records.");
                    match = candidate;
                    historicalPonUpgraded = true;
                }
            }

            if (match == null)
                throw new InvalidOperationException("The committed " + record.Type + " event has no matching authoritative meld.");

            var tileIds = new List<uint>(3);
            foreach (TileId tile in match.Tiles)
            {
                if (historicalPonUpgraded && IsAddedKanTile(round, source, match, tile)) continue;
                tileIds.Add(unchecked((uint)tile.Value));
            }

            if (tileIds.Count != 3 || !tileIds.Contains(unchecked((uint)record.Tile.Value.Value)))
                throw new InvalidOperationException("The committed " + record.Type + " event cannot be reconstructed as its exact three-tile meld.");

            ProtocolMeldType meldType = required == RoundMeldKind.Pon ? ProtocolMeldType.Pon : ProtocolMeldType.Chi;
            return new MeldView(meldType, tileIds, unchecked((uint)record.Tile.Value.Value), fromSeat);
        }

        public static uint RequireTurnNumber(RoundStateMachine round, TableEventRecord record)
        {
            RoundEvent source = RequireRoundEvent(round, record);
            uint turn = 1;
            bool sawDiscard = false;
            IReadOnlyList<RoundEvent> events = round.Events;
            for (int i = 0; i < events.Count; i++)
            {
                if (ReferenceEquals(events[i], source)) return turn;
                if (events[i].Type == RoundEventType.TileDrawn || events[i].Type == RoundEventType.RinshanDrawn)
                {
                    if (sawDiscard) turn = checked(turn + 1);
                }
                else if (events[i].Type == RoundEventType.TileDiscarded)
                {
                    sawDiscard = true;
                }
            }

            throw new InvalidOperationException("The committed event is not present in its round history.");
        }

        public static bool RequireDoubleRiichi(RoundStateMachine round, TableEventRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (record.Type != RoundEventType.RiichiDeclared || !record.Tile.HasValue)
                throw new InvalidOperationException("A RiichiDeclared fact requires its committed discard tile.");

            RoundEvent source = RequireRoundEvent(round, record);
            int position = IndexOf(round, source);
            bool ownPriorDiscard = false;
            bool interrupted = false;
            IReadOnlyList<RoundEvent> events = round.Events;
            for (int i = 0; i < position; i++)
            {
                RoundEvent prior = events[i];
                if (prior.Type == RoundEventType.TileDiscarded && prior.Seat == record.Seat) ownPriorDiscard = true;
                if (prior.Type == RoundEventType.PonDeclared || prior.Type == RoundEventType.ChiDeclared || prior.Type == RoundEventType.KanDeclared)
                    interrupted = true;
            }

            // Double-riichi is a first-discard / uninterrupted-opening rule. The global
            // turn projection counter advances after every discard and therefore is already
            // 2 for the first draw by the non-dealer seats; it is not a valid condition here.
            return !ownPriorDiscard && !interrupted;
        }

        private static RoundEvent RequireRoundEvent(RoundStateMachine round, TableEventRecord record)
        {
            if (round == null) throw new ArgumentNullException(nameof(round));
            if (record == null) throw new ArgumentNullException(nameof(record));
            RoundEvent? found = null;
            foreach (RoundEvent candidate in round.Events)
            {
                if (candidate.Type == record.Type && candidate.Seat == record.Seat && candidate.Tile == record.Tile &&
                    string.Equals(candidate.Detail, record.Detail, StringComparison.Ordinal))
                {
                    if (found != null) throw new InvalidOperationException("A committed event identity is ambiguous in its round history.");
                    found = candidate;
                }
            }

            return found ?? throw new InvalidOperationException("The committed event does not belong to the captured round history.");
        }

        private static int IndexOf(RoundStateMachine round, RoundEvent value)
        {
            for (int i = 0; i < round.Events.Count; i++) if (ReferenceEquals(round.Events[i], value)) return i;
            throw new InvalidOperationException("The committed event is not present in its round history.");
        }

        private static int ParseFromSeat(RoundEvent source, RoundEventType type)
        {
            string prefix = type == RoundEventType.PonDeclared ? "PON/" : "CHI/";
            if (source.Detail == null || !source.Detail.StartsWith(prefix, StringComparison.Ordinal) ||
                !int.TryParse(source.Detail.Substring(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int seat) ||
                seat < 0 || seat > 3 || seat == source.Seat)
                throw new InvalidOperationException("The committed " + type + " event has invalid fromSeat provenance.");
            return seat;
        }

        private static bool IsLaterAddedKan(RoundStateMachine round, RoundEvent source, RoundMeld meld)
        {
            int sourceIndex = IndexOf(round, source);
            for (int i = sourceIndex + 1; i < round.Events.Count; i++)
            {
                RoundEvent value = round.Events[i];
                if (value.Type == RoundEventType.KanDeclared && value.Seat == meld.Seat &&
                    string.Equals(value.Detail, "ADDED_KAN", StringComparison.Ordinal) && value.Tile.HasValue &&
                    Contains(meld, value.Tile.Value)) return true;
            }
            return false;
        }

        private static bool IsAddedKanTile(RoundStateMachine round, RoundEvent source, RoundMeld meld, TileId tile)
        {
            int sourceIndex = IndexOf(round, source);
            for (int i = sourceIndex + 1; i < round.Events.Count; i++)
            {
                RoundEvent value = round.Events[i];
                if (value.Type == RoundEventType.KanDeclared && value.Seat == meld.Seat &&
                    string.Equals(value.Detail, "ADDED_KAN", StringComparison.Ordinal) && value.Tile.HasValue &&
                    Contains(meld, value.Tile.Value)) return value.Tile.Value == tile;
            }
            return false;
        }

        private static bool Contains(RoundMeld meld, TileId tile)
        {
            for (int i = 0; i < meld.Tiles.Count; i++) if (meld.Tiles[i] == tile) return true;
            return false;
        }
    }
}
