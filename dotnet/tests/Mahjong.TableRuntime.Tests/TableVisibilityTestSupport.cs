using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Mahjong.Ai;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;

namespace Mahjong.TableRuntime.Tests
{
    internal static class V32Fixtures
    {
        internal static CheatEconomyConfig Economy(int initialPoints = 1000)
        {
            var costs = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                [CheatEconomyConfig.TileMarkCostKey] = 5,
                [CheatEconomyConfig.SleeveBloomCostKey] = 10,
                [CheatEconomyConfig.BeamSwapCostKey] = 10
            };
            return new CheatEconomyConfig(true, initialPoints, costs);
        }

        internal static CheatState Cheat(int initialPoints = 1000)
        {
            return CheatState.Initialize(Economy(initialPoints));
        }

        internal static SleeveBloomConfig SleeveConfig(bool enabled = true)
        {
            return new SleeveBloomConfig(enabled, 5000L);
        }

        internal static CheatTurnContext CheatContext(
            RoundStateMachine round,
            int currentPlayer = 0,
            RoundPhase phase = RoundPhase.CheatWindow,
            SleeveBloomDrawKind drawKind = SleeveBloomDrawKind.DealExtra,
            int turn = 0,
            bool responseWindowOpen = false,
            int roundOrdinal = 1)
        {
            return new CheatTurnContext(
                currentPlayer,
                phase,
                responseWindowOpen,
                new[] { false, false, false, false },
                turn,
                round.RoundId,
                roundOrdinal,
                drawKind,
                1000L);
        }

        internal static CheatState WithRegisteredRound(CheatState state, string roundId)
        {
            TileMarkTransition transition = state.TileMarks.RegisterDealtRound(roundId);
            if (!transition.Success)
            {
                throw new InvalidOperationException("Fixture could not register dealt round: " + transition.Error);
            }

            return state.WithTileMarks(transition.State);
        }

        internal static CheatState WithLedger(CheatState state, params CheatLedgerEntry[] entries)
        {
            CheatState current = state;
            for (int i = 0; i < entries.Length; i++)
            {
                CheatLedgerEntry entry = entries[i];
                CheatLedgerApplyResult applied = current.Ledger.Apply(
                    entry.EntryId,
                    entry.Seat,
                    entry.Amount,
                    entry.Reason);
                if (!applied.Success || applied.Ledger == null)
                {
                    throw new InvalidOperationException("Fixture ledger entry was rejected: " + applied.Error);
                }

                current = current.WithLedger(applied.Ledger);
            }

            return current;
        }

        internal static TableProjectionContext Context(
            RoundStateMachine round,
            bool settlementBatch = false,
            bool revealUra = false,
            bool paused = false,
            IReadOnlyList<TableEventProjectionInput>? events = null,
            IReadOnlyList<TableSeatDecision>? decisions = null,
            IReadOnlyList<ClockView>? clocks = null,
            TableSessionProjection? session = null,
            IReadOnlyDictionary<int, IReadOnlyList<TableMeldFact>>? melds = null,
            IReadOnlyDictionary<int, IReadOnlyList<uint>>? discardHistories = null,
            bool[]? riichi = null,
            bool[]? furiten = null,
            bool[]? preparation = null,
            ulong? stateVersion = null,
            ulong? eventSeq = null,
            string? roundId = null,
            StreamCursor? cursor = null,
            IReadOnlyList<TableSeatPublicFacts>? publicFacts = null,
            IReadOnlyList<TableSeatPrivateFacts>? privateFacts = null)
        {
            ulong eventVersion = 0;
            if (events != null)
            {
                for (int i = 0; i < events.Count; i++)
                {
                    if (events[i].CommandVersion.Value > eventVersion)
                    {
                        eventVersion = events[i].CommandVersion.Value;
                    }
                }
            }

            ulong version = stateVersion ?? Math.Max(unchecked((ulong)round.Version), eventVersion);
            ulong lastSeq = eventSeq ?? (ulong)(events == null ? 0 : events.Count == 0 ? 0 : events[events.Count - 1].EventSeq.Value);
            var identity = new TableProjectionIdentity(
                new SessionId("session-1"),
                new RoundId(roundId ?? round.RoundId),
                new StateVersion(version),
                new EventSeq(lastSeq),
                cursor ?? new StreamCursor(new StreamVersion(version), new ProjectionSeq(1)),
                new SessionVersion(1));
            var policy = new TableProjectionPolicy(paused, settlementBatch, revealUra);
            return new TableProjectionContext(
                identity,
                policy,
                clocks,
                publicFacts ?? BuildPublicFacts(round, melds, riichi),
                privateFacts ?? BuildPrivateFacts(round, discardHistories, furiten, preparation),
                decisions,
                events,
                session);
        }

        internal static IReadOnlyList<TableSeatPublicFacts> BuildPublicFacts(
            RoundStateMachine round,
            IReadOnlyDictionary<int, IReadOnlyList<TableMeldFact>>? melds = null,
            bool[]? riichi = null)
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

                IReadOnlyList<TableMeldFact> seatMelds = Array.Empty<TableMeldFact>();
                if (melds != null && melds.TryGetValue(seat, out IReadOnlyList<TableMeldFact>? supplied))
                {
                    seatMelds = supplied;
                }

                result.Add(new TableSeatPublicFacts(
                    seat,
                    riichi != null && riichi[seat],
                    seatMelds,
                    discards));
            }

            return result;
        }

        internal static IReadOnlyList<TableSeatPrivateFacts> BuildPrivateFacts(
            RoundStateMachine round,
            IReadOnlyDictionary<int, IReadOnlyList<uint>>? discardHistories = null,
            bool[]? furiten = null,
            bool[]? preparation = null)
        {
            var result = new List<TableSeatPrivateFacts>(4);
            for (int seat = 0; seat < 4; seat++)
            {
                IReadOnlyList<uint> history = Array.Empty<uint>();
                if (discardHistories != null && discardHistories.TryGetValue(seat, out IReadOnlyList<uint>? supplied))
                {
                    history = supplied;
                }
                else
                {
                    IReadOnlyList<TileId> river = round.Tiles.TilesInZone(TileZone.Discard, seat);
                    var derived = new List<uint>(river.Count);
                    for (int i = 0; i < river.Count; i++)
                    {
                        derived.Add(unchecked((uint)river[i].Value));
                    }

                    history = derived;
                }

                result.Add(new TableSeatPrivateFacts(
                    seat,
                    furiten != null && furiten[seat],
                    preparation != null && preparation[seat],
                    history));
            }

            return result;
        }

        internal static TableEventProjectionInput Event(
            ProtocolEventType type,
            int seat = ProtocolConstants.NoSeat,
            uint[]? tiles = null,
            ulong seq = 1,
            ulong commandVersion = 1,
            uint ordinal = 0,
            bool ura = false,
            bool closedKan = false,
            bool chankan = false,
            long? cheatAmount = null,
            long? cheatBalance = null,
            string? reason = null)
        {
            return new TableEventProjectionInput(
                type,
                new EventSeq(seq),
                new StateVersion(commandVersion),
                ordinal,
                seat,
                tiles,
                ura,
                closedKan,
                chankan,
                cheatAmount,
                cheatBalance,
                reason);
        }

        internal static DecisionView Decision(string id = "d1", ulong controlEpoch = 1, params uint[] tiles)
        {
            var actions = new List<LegalActionView>();
            if (tiles.Length == 0)
            {
                actions.Add(new LegalActionView(ProtocolActionType.Pass, "pass", Array.Empty<uint>()));
            }
            else
            {
                for (int i = 0; i < tiles.Length; i++)
                {
                    actions.Add(new LegalActionView(
                        ProtocolActionType.DiscardTile,
                        "opt" + i.ToString(CultureInfo.InvariantCulture),
                        new[] { tiles[i] }));
                }
            }

            return new DecisionView(
                new DecisionId(id),
                new ControlEpoch(controlEpoch),
                500L,
                actions);
        }

        internal static TableCommandEnvelope Command(
            RoundCommand command,
            int expectedStateVersion,
            string requestId)
        {
            return new TableCommandEnvelope(
                command,
                new RequestId(requestId),
                new StateVersion(unchecked((ulong)expectedStateVersion)),
                new OwnerEpoch(1),
                new ControlEpoch(0));
        }

        internal static RoundStateMachine ApplyDiscard(RoundStateMachine round, int seat, out TableCommandEnvelope command)
        {
            TileId tile = round.Tiles.TilesInZone(TileZone.Hand, seat)[0];
            command = Command(RoundCommand.Discard(round.RoundId, seat, tile), (int)round.Version, "discard-" + seat);
            RoundTransition transition = round.Execute(command.Command);
            if (!transition.IsAccepted || transition.State == null)
            {
                throw new InvalidOperationException("Discard fixture failed: " + transition.Error);
            }

            return transition.State;
        }

        internal static RoundStateMachine ApplyTsumo(
            RoundStateMachine round,
            int winner,
            long[] finalScores)
        {
            var settlement = new RoundSettlement(
                "result-1",
                RoundOutcome.Tsumo(winner),
                finalScores,
                0,
                new PersistedUtcTime(new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero)),
                1234L);
            RoundTransition transition = round.Execute(RoundCommand.Tsumo(round.RoundId, winner, settlement));
            if (!transition.IsAccepted || transition.State == null)
            {
                throw new InvalidOperationException("Tsumo fixture failed: " + transition.Error);
            }

            return transition.State;
        }

        internal static IReadOnlyList<uint> HandValues(RoundStateMachine round, int seat)
        {
            IReadOnlyList<TileId> tiles = round.Tiles.TilesInZone(TileZone.Hand, seat);
            var values = new List<uint>(tiles.Count);
            for (int i = 0; i < tiles.Count; i++)
            {
                values.Add(unchecked((uint)tiles[i].Value));
            }

            return values;
        }

        internal static RoundStateMachine WithMeld(
            RoundStateMachine source,
            int seat,
            int tileCount,
            ProtocolMeldType type,
            out TableMeldFact fact)
        {
            TileId? drawn = source.Tiles.DrawnTile.HasValue && source.Tiles.DrawnTile.Value.Seat == seat
                ? source.Tiles.DrawnTile.Value.Tile
                : (TileId?)null;
            IReadOnlyList<TileId> hand = source.Tiles.TilesInZone(TileZone.Hand, seat);
            var chosen = new List<TileId>(tileCount);
            for (int i = 0; i < hand.Count && chosen.Count < tileCount; i++)
            {
                if (drawn.HasValue && hand[i] == drawn.Value)
                {
                    continue;
                }

                chosen.Add(hand[i]);
            }

            if (chosen.Count != tileCount)
            {
                throw new InvalidOperationException("Not enough closed hand tiles for the fixture meld.");
            }

            List<TilePlacement> placements = source.Tiles.AsPlacements().ToList();
            for (int i = 0; i < chosen.Count; i++)
            {
                int index = placements.FindIndex(item => item.Tile == chosen[i]);
                if (index < 0)
                {
                    throw new InvalidOperationException("Fixture meld tile disappeared.");
                }

                placements[index] = new TilePlacement(chosen[i], TilePosition.Meld(seat, i));
            }

            TilePositionMap map = TilePositionMap.FromPlacements(
                placements,
                source.Tiles.WallPointer,
                source.Tiles.WallEndExclusive,
                source.Tiles.RinshanPointer,
                source.Tiles.LastDiscard,
                source.Tiles.Revision,
                source.Tiles.LastRinshanDraw,
                source.Tiles.DoraIndicators,
                source.Tiles.KanCount,
                source.Tiles.DrawnTile);
            RoundStateMachine round = RoundStateMachine.CreateDealt(source.Spec, map, RoundPhase.CheatWindow);
            var values = new List<uint>(chosen.Count);
            for (int i = 0; i < chosen.Count; i++)
            {
                values.Add(unchecked((uint)chosen[i].Value));
            }

            fact = new TableMeldFact(type, values, null, ProtocolConstants.NoSeat);
            return round;
        }

        internal static TilePositionMap WithNormalDrawnTile(RoundStateMachine round, int seat)
        {
            TilePositionMap map = round.Tiles;
            if (!map.DrawnTile.HasValue || map.DrawnTile.Value.Seat != seat)
            {
                throw new InvalidOperationException("The fixture round has no drawn tile for seat " + seat + ".");
            }

            var evidence = new DrawnTileEvidence(map.DrawnTile.Value.Tile, seat, DrawSource.Wall);
            return TilePositionMap.FromPlacements(
                map.AsPlacements(),
                map.WallPointer,
                map.WallEndExclusive,
                map.RinshanPointer,
                map.LastDiscard,
                map.Revision,
                map.LastRinshanDraw,
                map.DoraIndicators,
                map.KanCount,
                evidence);
        }
    }

    internal static class VisibilitySurface
    {
        internal static string Dump(object? value)
        {
            var builder = new StringBuilder();
            DumpValue(value, builder, new HashSet<object>(ReferenceComparer.Instance), 0);
            return builder.ToString();
        }

        internal static bool HasField(string dump, string fieldName)
        {
            return System.Text.RegularExpressions.Regex.IsMatch(
                dump,
                "(?<![A-Za-z0-9_])" + System.Text.RegularExpressions.Regex.Escape(fieldName) + "=");
        }

        internal static IReadOnlyList<uint> PublicTileIds(PublicSnapshotView snapshot)
        {
            return PublicTileIds(snapshot.View);
        }

        internal static IReadOnlyList<uint> PublicTileIds(PublicMatchView view)
        {
            var result = new List<uint>();
            if (view.Board == null)
            {
                return result;
            }

            for (int i = 0; i < view.Board.DoraIndicators.Count; i++)
            {
                result.Add(view.Board.DoraIndicators[i]);
            }

            for (int seat = 0; seat < view.Board.Players.Count; seat++)
            {
                PlayerPublicView player = view.Board.Players[seat];
                for (int m = 0; m < player.Melds.Count; m++)
                {
                    for (int t = 0; t < player.Melds[m].TileIds.Count; t++)
                    {
                        result.Add(player.Melds[m].TileIds[t]);
                    }
                }

                for (int d = 0; d < player.Discards.Count; d++)
                {
                    result.Add(player.Discards[d].TileId);
                }
            }

            if (view.Board.LastDiscard != null)
            {
                result.Add(view.Board.LastDiscard.TileId);
            }

            return result;
        }

        internal static IReadOnlyList<uint> PlayerVisibleTileIds(PlayerSnapshotView snapshot)
        {
            var result = new List<uint>(PublicTileIds(snapshot.Public));
            for (int i = 0; i < snapshot.Private.ClosedTiles.Count; i++)
            {
                result.Add(snapshot.Private.ClosedTiles[i]);
            }

            if (snapshot.Private.DrawnTileId.HasValue)
            {
                result.Add(snapshot.Private.DrawnTileId.Value);
            }

            if (snapshot.Private.Decision != null)
            {
                for (int i = 0; i < snapshot.Private.Decision.Actions.Count; i++)
                {
                    for (int t = 0; t < snapshot.Private.Decision.Actions[i].TileIds.Count; t++)
                    {
                        result.Add(snapshot.Private.Decision.Actions[i].TileIds[t]);
                    }
                }
            }

            return result;
        }

        internal static string PayloadHex(TableProjectedEvent value)
        {
            byte[] body = value.EncodeBody();
            var builder = new StringBuilder(body.Length * 2);
            for (int i = 0; i < body.Length; i++)
            {
                builder.Append(body[i].ToString("X2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        private static void DumpValue(object? value, StringBuilder builder, HashSet<object> seen, int depth)
        {
            if (value == null)
            {
                builder.Append("null");
                return;
            }

            Type type = value.GetType();
            if (value is string text)
            {
                builder.Append('"').Append(text).Append('"');
                return;
            }

            if (type.IsEnum)
            {
                builder.Append(type.Name).Append('.').Append(value.ToString());
                return;
            }

            if (value is byte[] bytes)
            {
                builder.Append("bytes[");
                for (int i = 0; i < bytes.Length; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    builder.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
                }

                builder.Append(']');
                return;
            }

            if (type.IsPrimitive || value is decimal)
            {
                builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }

            if (value is IEnumerable sequence)
            {
                builder.Append(type.Name).Append('[');
                bool first = true;
                foreach (object? item in sequence)
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    DumpValue(item, builder, seen, depth + 1);
                }

                builder.Append(']');
                return;
            }

            if (!type.IsValueType)
            {
                if (!seen.Add(value))
                {
                    builder.Append("<ref>");
                    return;
                }
            }

            builder.Append(type.Name).Append('{');
            PropertyInfo[] properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(property => property.GetIndexParameters().Length == 0)
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .ToArray();
            for (int i = 0; i < properties.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append(properties[i].Name).Append('=');
                object? propertyValue;
                try
                {
                    propertyValue = properties[i].GetValue(value);
                }
                catch (TargetInvocationException)
                {
                    propertyValue = "<error>";
                }

                DumpValue(propertyValue, builder, seen, depth + 1);
            }

            builder.Append('}');
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();

            bool IEqualityComparer<object>.Equals(object? x, object? y) => ReferenceEquals(x, y);

            int IEqualityComparer<object>.GetHashCode(object obj) =>
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
