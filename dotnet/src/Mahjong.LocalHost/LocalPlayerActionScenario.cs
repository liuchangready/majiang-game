using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Mahjong.Domain;
using Mahjong.TableRuntime;

namespace Mahjong.LocalHost
{
    /// <summary>
    /// Deterministic, opt-in C04 fixtures. They build a legal 136-entity deal
    /// and advance only through accepted domain commands. They never synthesize
    /// a receipt, event record, or result for the Player's requested action.
    /// </summary>
    public static class LocalPlayerActionScenario
    {
        public const string Pon = "pon";
        public const string Chi = "chi";
        public const string Riichi = "riichi";
        public const string ClosedKan = "closed-kan";

        public static LocalTableHostOptions ConfigureHostOptions(LocalTableHostOptions options, string scenarioId)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            options.InitialRound = CreateInitialRound(scenarioId);
            options.AutoPlay = true;
            return options;
        }

        public static RoundStateMachine CreateInitialRound(string scenarioId)
        {
            if (string.Equals(scenarioId, Pon, StringComparison.Ordinal)) return BuildCallWindow(Pon);
            if (string.Equals(scenarioId, Chi, StringComparison.Ordinal)) return BuildCallWindow(Chi);
            if (string.Equals(scenarioId, Riichi, StringComparison.Ordinal)) return BuildRiichiTurn();
            if (string.Equals(scenarioId, ClosedKan, StringComparison.Ordinal)) return BuildClosedKanTurn();
            throw new ArgumentOutOfRangeException(nameof(scenarioId), scenarioId, "Unknown fixed C04 Player action scenario.");
        }

        /// <summary>
        /// Validate the closed scenario set and all physical-deal invariants without
        /// exposing the Domain RoundStateMachine through the Unity composition API.
        /// </summary>
        public static void ValidateScenario(string scenarioId)
        {
            RoundStateMachine round = CreateInitialRound(scenarioId);
            if (TilePositionMap.Validate(round.Tiles).AssignedCount != TileId.Count)
                throw new InvalidOperationException("Frozen scenario does not conserve all physical tiles.");
        }

        /// <summary>
        /// Read-only C04 audit view derived from the exact persisted LocalHost envelope.
        /// The returned canonical lines contain each physical entity and its authoritative
        /// zone/seat/index; callers must compare this with the same envelope's recovery facts.
        /// </summary>
        public static string ReadPhysicalAudit(byte[] authoritySaveBytes, byte[] cheatStatePayload)
        {
            if (authoritySaveBytes == null) throw new ArgumentNullException(nameof(authoritySaveBytes));
            if (cheatStatePayload == null) throw new ArgumentNullException(nameof(cheatStatePayload));
            LocalTableRecoveryFacts facts = LocalTableRecoveryFactsReader.ReadPersistedFacts(authoritySaveBytes, cheatStatePayload);
            LocalSave save = LocalSaveCodec.DecodeEnvelope(authoritySaveBytes);
            if (save.BaseCheckpointBlob.Length == 0) throw new InvalidOperationException("C04 authority save has no checkpoint.");
            TableCheckpoint checkpoint = LocalSaveCodec.DecodeCheckpoint(save.BaseCheckpointBlob);
            byte[] statePayload = checkpoint.StatePayload;
            for (int i = 0; i < save.Operations.Count; i++)
            {
                LocalDurableOperation operation = save.Operations[i];
                if (operation.ExpectedStateVersion < checkpoint.StateVersion.Value) continue;
                if (operation.Kind == LocalOperationKind.SaveCheckpoint ||
                    (operation.Kind == LocalOperationKind.CommitControl && operation.CheckpointBlob != null))
                {
                    checkpoint = LocalSaveCodec.DecodeCheckpoint(operation.CheckpointBlob!);
                    statePayload = checkpoint.StatePayload;
                }
                else if (operation.Kind == LocalOperationKind.Commit && operation.Events.Count > 0)
                {
                    if (operation.NextStatePayload == null || operation.NextStatePayload.Length == 0)
                        throw new InvalidOperationException("C04 authority commit has no state payload.");
                    statePayload = operation.NextStatePayload;
                }
            }

            RoundRecoveryState recovered = RoundRecoveryCodec.Decode(statePayload);
            RoundStateMachine round = RoundRecovery.Restore(recovered);
            IReadOnlyList<TilePlacement> placements = round.Tiles.AsPlacements();
            if (placements.Count != 136 || facts.Physical.TileCount != 136 || facts.Physical.UniqueCount != 136)
                throw new InvalidOperationException("C04 authority physical audit is not exactly 136 entities.");
            var lines = new List<string>(placements.Count);
            var ids = new HashSet<int>();
            foreach (TilePlacement placement in placements)
            {
                if (!placement.Position.IsAssigned || !ids.Add(placement.Tile.Value))
                    throw new InvalidOperationException("C04 authority physical audit has unassigned or duplicate entities.");
                lines.Add("tile|" + placement.Tile.Value.ToString("D3", CultureInfo.InvariantCulture) + "|" +
                    placement.Position.Zone.ToString().ToUpperInvariant() + "|" +
                    placement.Position.Seat.ToString(CultureInfo.InvariantCulture) + "|" +
                    placement.Position.Index.ToString(CultureInfo.InvariantCulture));
            }
            if (ids.Count != 136) throw new InvalidOperationException("C04 authority physical audit omitted an entity.");
            lines.Sort(StringComparer.Ordinal);
            string canonical = string.Join("\n", lines);
            using (SHA256 sha = SHA256.Create())
            {
                string digest = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical))).Replace("-", string.Empty);
                if (!string.Equals(digest, facts.Physical.TilesSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("C04 authority physical placements differ from recovery facts.");
            }
            return canonical;
        }

        private static RoundStateMachine BuildCallWindow(string action)
        {
            bool pon = string.Equals(action, Pon, StringComparison.Ordinal);
            var fixedOrder = new Dictionary<int, int>();
            int[] seat0 = pon
                ? new[] { 0, 1, 20, 24, 28, 32, 36, 40, 44, 48, 52, 72, 76, 132 }
                : new[] { 0, 8, 20, 24, 28, 32, 36, 40, 44, 48, 52, 72, 76, 132 };
            int[] seat0Slots = { 0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48, 52 };
            for (int i = 0; i < seat0.Length; i++) fixedOrder.Add(seat0Slots[i], seat0[i]);

            int target = pon ? 2 : 4;
            fixedOrder.Add(4, 80); // deterministic seat 1 prelude discard
            fixedOrder.Add(8, 84); // deterministic seat 2 prelude discard
            fixedOrder.Add(12, target); // seat 3 supplies the fixed call tile

            RoundStateMachine round = BuildDealt("c04-" + action + "-seed-440" + (pon ? "1" : "2"), fixedOrder);
            round = Accepted(round, RoundCommand.Discard(round.RoundId, 0, new TileId(132), new[] { 1, 2, 3 }));
            round = PassAll(round);
            round = Accepted(round, RoundCommand.Discard(round.RoundId, 1, new TileId(80), new[] { 0, 2, 3 }));
            round = PassAll(round);
            round = Accepted(round, RoundCommand.Discard(round.RoundId, 2, new TileId(84), new[] { 0, 1, 3 }));
            round = PassAll(round);
            round = Accepted(round, RoundCommand.Discard(round.RoundId, 3, new TileId(target), new[] { 0, 1, 2 }));
            if (round.Window == null || round.Window.FromSeat != 3 || !round.Window.EligibleSeats.Contains(0))
                throw new InvalidOperationException("Frozen call fixture did not create the seat-0 response window.");

            // Freeze the other responders as accepted domain prelude actions. This
            // leaves only seat 0 pending when LocalHost composes, so no background AI
            // command can race the subscribed request baseline or outrank the target.
            int[] otherResponders = round.Window.EligibleSeats.Where(seat => seat != 0).ToArray();
            for (int i = 0; i < otherResponders.Length; i++)
            {
                int seat = otherResponders[i];
                if (round.Window?.HasResponded(seat) == false)
                    round = Accepted(round, RoundCommand.Pass(round.RoundId, seat));
            }
            return round;
        }

        private static RoundStateMachine PassAll(RoundStateMachine round)
        {
            while (round.Window != null)
            {
                int pendingSeat = -1;
                for (int i = 0; i < round.Window.EligibleSeats.Count; i++)
                {
                    int seat = round.Window.EligibleSeats[i];
                    if (!round.Window.HasResponded(seat)) { pendingSeat = seat; break; }
                }
                if (pendingSeat < 0) break;
                round = Accepted(round, RoundCommand.Pass(round.RoundId, pendingSeat));
            }
            return round;
        }

        private static RoundStateMachine BuildRiichiTurn()
        {
            var fixedOrder = new Dictionary<int, int>();
            int[] hand = { 0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 37, 72, 76, 124 };
            int[] slots = { 0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48, 52 };
            for (int i = 0; i < hand.Length; i++) fixedOrder.Add(slots[i], hand[i]);
            return BuildDealt("c04-riichi-seed-4403", fixedOrder);
        }

        private static RoundStateMachine BuildClosedKanTurn()
        {
            var fixedOrder = new Dictionary<int, int>();
            int[] hand = { 0, 1, 2, 3, 36, 40, 44, 48, 52, 56, 60, 64, 68, 72 };
            int[] slots = { 0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48, 52 };
            for (int i = 0; i < hand.Length; i++) fixedOrder.Add(slots[i], hand[i]);
            fixedOrder.Add(122, 73); // dead-wall replacement draw physical entity
            return BuildDealt("c04-closed-kan-seed-4404", fixedOrder);
        }

        private static RoundStateMachine BuildDealt(string roundId, IDictionary<int, int> fixedOrder)
        {
            if (fixedOrder.Keys.Any(slot => slot < 0 || slot >= TileId.Count) ||
                fixedOrder.Values.Any(tile => tile < 0 || tile >= TileId.Count) ||
                fixedOrder.Values.Distinct().Count() != fixedOrder.Count)
                throw new InvalidOperationException("Frozen scenario contains an invalid or duplicated tile/slot.");

            var used = new HashSet<int>(fixedOrder.Values);
            var remaining = new Queue<int>(Enumerable.Range(0, TileId.Count).Where(id => !used.Contains(id)));
            var order = new int[TileId.Count];
            for (int slot = 0; slot < TileId.Count; slot++)
                order[slot] = fixedOrder.TryGetValue(slot, out int tile) ? tile : remaining.Dequeue();

            var tiles = order.Select(id => new TileId(id)).ToArray();
            RecordedRandomDraw wall = RecordedRandomDraw.Create(roundId + "-fixed-wall", tiles);
            TilePositionMap dealt = TilePositionMap.CreateDealt(wall, 0);
            if (TilePositionMap.Validate(dealt).AssignedCount != TileId.Count)
                throw new InvalidOperationException("Frozen scenario did not conserve all 136 physical entities.");
            return RoundStateMachine.CreateDealt(
                new RoundSpec(roundId, 0, 1, 0, 0, new long[] { 25000, 25000, 25000, 25000 }, 0),
                dealt, RoundPhase.CheatWindow);
        }

        private static RoundStateMachine Accepted(RoundStateMachine state, RoundCommand command)
        {
            RoundTransition transition = state.Execute(command);
            if (!transition.IsAccepted || transition.State == null)
                throw new InvalidOperationException("Frozen scenario prelude was rejected: " + transition.Error);
            if (TilePositionMap.Validate(transition.State.Tiles).AssignedCount != TileId.Count)
                throw new InvalidOperationException("Frozen scenario prelude violated 136-tile conservation.");
            return transition.State;
        }
    }
}
