using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Mahjong.Contracts;
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
        public const string DealerTsumo = "dealer-tsumo";

        public static LocalTableHostOptions ConfigureHostOptions(LocalTableHostOptions options, string scenarioId)
        {
            return ConfigureHostOptions(options, scenarioId, null);
        }

        public static LocalTableHostOptions ConfigureHostOptions(
            LocalTableHostOptions options,
            string scenarioId,
            string? roundId)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            options.InitialRound = CreateInitialRound(scenarioId, roundId);
            options.AutoPlay = true;
            return options;
        }

        public static RoundStateMachine CreateInitialRound(string scenarioId)
        {
            return CreateInitialRound(scenarioId, null);
        }

        /// <summary>Builds the same accepted fixture under a caller-owned isolated round id.</summary>
        public static RoundStateMachine CreateInitialRound(string scenarioId, string? roundId)
        {
            if (string.Equals(scenarioId, Pon, StringComparison.Ordinal)) return BuildCallWindow(Pon, roundId);
            if (string.Equals(scenarioId, Chi, StringComparison.Ordinal)) return BuildCallWindow(Chi, roundId);
            if (string.Equals(scenarioId, Riichi, StringComparison.Ordinal)) return BuildRiichiTurn(roundId);
            if (string.Equals(scenarioId, ClosedKan, StringComparison.Ordinal)) return BuildClosedKanTurn(roundId);
            if (string.Equals(scenarioId, DealerTsumo, StringComparison.Ordinal)) return BuildDealerTsumoTurn(roundId);
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

        /// <summary>
        /// Reads the physical image from the durable operation that committed one accepted
        /// Player action. The save envelope may already contain later autonomous commands, so
        /// this deliberately selects and validates the exact request/version/event operation
        /// instead of treating the envelope's newest state as the action's post-state.
        /// </summary>
        public static string ReadCommittedActionPhysicalAudit(
            byte[] authoritySaveBytes,
            string requestId,
            string scopeId,
            string roundId,
            ulong baselineVersion,
            ulong commandVersion,
            ulong actionEventSeq,
            string actionEventType,
            int actionSeat,
            string? canonicalPayloadSha256 = null)
        {
            if (authoritySaveBytes == null) throw new ArgumentNullException(nameof(authoritySaveBytes));
            if (string.IsNullOrWhiteSpace(requestId)) throw new ArgumentException("Request id is required.", nameof(requestId));
            if (string.IsNullOrWhiteSpace(scopeId)) throw new ArgumentException("Scope id is required.", nameof(scopeId));
            if (string.IsNullOrWhiteSpace(roundId)) throw new ArgumentException("Round id is required.", nameof(roundId));
            if (commandVersion != checked(baselineVersion + 1))
                throw new InvalidOperationException("The accepted action command version does not immediately follow its baseline.");

            LocalSave save = LocalSaveCodec.DecodeEnvelope(authoritySaveBytes);
            var matches = new List<LocalDurableOperation>();
            for (int i = 0; i < save.Operations.Count; i++)
            {
                LocalDurableOperation operation = save.Operations[i];
                TableDurableReceipt? receipt = operation.Receipt;
                TableCommandJournalRecord? journal = operation.Journal;
                if (operation.Kind != LocalOperationKind.Commit || receipt == null || journal == null ||
                    receipt.Receipt.Status != ReceiptStatus.Applied ||
                    !string.Equals(receipt.RequestId.Value, requestId, StringComparison.Ordinal) ||
                    !string.Equals(receipt.ScopeId.Value, scopeId, StringComparison.Ordinal) ||
                    !string.Equals(journal.RequestId.Value, requestId, StringComparison.Ordinal) ||
                    !string.Equals(journal.ScopeId.Value, scopeId, StringComparison.Ordinal) ||
                    !string.Equals(receipt.Api, journal.Api, StringComparison.Ordinal) ||
                    journal.EvaluatedStateVersion.Value != baselineVersion ||
                    journal.CommandVersion.Value != commandVersion ||
                    receipt.Receipt.EvaluatedStateVersion.Value != baselineVersion ||
                    receipt.Receipt.CommandVersion?.Value != commandVersion ||
                    !receipt.Receipt.EventSeq.HasValue || receipt.Receipt.EventSeq.Value.Value < actionEventSeq)
                    continue;

                bool eventMatches = operation.Events.Any(value =>
                    value.CommandVersion.Value == commandVersion &&
                    value.Seq.Value == actionEventSeq &&
                    string.Equals(value.Type.ToString(), actionEventType, StringComparison.Ordinal) &&
                    value.Seat == actionSeat);
                if (eventMatches) matches.Add(operation);
            }

            byte[]? acceptedStatePayload = null;
            ulong acceptedFingerprint = 0;
            if (matches.Count == 1)
            {
                LocalDurableOperation acceptedOperation = matches[0];
                if (acceptedOperation.ExpectedStateVersion != baselineVersion ||
                    acceptedOperation.NextStatePayload == null || acceptedOperation.NextStatePayload.Length == 0 ||
                    acceptedOperation.NextStateFingerprint == 0)
                    throw new InvalidOperationException("The exact accepted action operation has no valid post-action authority state.");
                acceptedStatePayload = acceptedOperation.NextStatePayload;
                acceptedFingerprint = acceptedOperation.NextStateFingerprint;
            }
            else if (matches.Count == 0 && string.Equals(actionEventType, "TsumoWin", StringComparison.Ordinal))
            {
                // A terminal action may atomically checkpoint and compact its operation journal.
                // In that case, bind the event to the retained durable receipt and the exact
                // terminal checkpoint at the same command version instead of requiring a journal
                // record that the production store intentionally removed.
                TableDurableReceipt[] receipts = save.Receipts.Values.Where(value =>
                    string.Equals(value.RequestId.Value, requestId, StringComparison.Ordinal) &&
                    string.Equals(value.ScopeId.Value, scopeId, StringComparison.Ordinal) &&
                    value.Receipt.Status == ReceiptStatus.Applied &&
                    value.Receipt.EvaluatedStateVersion.Value == baselineVersion &&
                    value.Receipt.CommandVersion?.Value == commandVersion &&
                    value.Receipt.EventSeq.HasValue && value.Receipt.EventSeq.Value.Value >= actionEventSeq).ToArray();
                if (receipts.Length != 1 || save.CurrentState.StateVersion.Value != commandVersion ||
                    save.CurrentState.LastEventSeq.Value < actionEventSeq ||
                    save.TerminalState != LocalSaveTerminalState.RoundFinished)
                    throw new InvalidOperationException("The terminal checkpoint has no unique retained applied receipt for the confirmed Tsumo action.");

                TableDurableReceipt receipt = receipts[0];
                string receiptKey = receipt.AccountId + "\u001f" + receipt.ScopeId.Value + "\u001f" + receipt.RequestId.Value;
                if (string.IsNullOrWhiteSpace(canonicalPayloadSha256) ||
                    !save.ReceiptDigests.TryGetValue(receiptKey, out string? persistedDigest) ||
                    !string.Equals(persistedDigest, canonicalPayloadSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The terminal checkpoint receipt has no matching persisted canonical request digest.");

                TableCheckpoint checkpoint = LocalSaveCodec.DecodeCheckpoint(save.BaseCheckpointBlob);
                if (checkpoint.StateVersion.Value != commandVersion || checkpoint.LastEventSeq.Value < actionEventSeq ||
                    checkpoint.StateFingerprint == 0 || checkpoint.StatePayload.Length == 0)
                    throw new InvalidOperationException("The terminal checkpoint does not match the accepted Tsumo command version/event.");
                acceptedStatePayload = checkpoint.StatePayload;
                acceptedFingerprint = checkpoint.StateFingerprint;
            }
            else
            {
                LocalDurableOperation? candidate = save.Operations.FirstOrDefault(operation =>
                    operation.Kind == LocalOperationKind.Commit && operation.Receipt != null && operation.Journal != null &&
                    string.Equals(operation.Receipt.RequestId.Value, requestId, StringComparison.Ordinal) &&
                    string.Equals(operation.Receipt.ScopeId.Value, scopeId, StringComparison.Ordinal));
                string diagnostic = candidate == null ? "no request/scope commit" :
                    "candidateVersion=" + candidate.Journal!.CommandVersion.Value.ToString(CultureInfo.InvariantCulture) +
                    "; expectedVersion=" + commandVersion.ToString(CultureInfo.InvariantCulture) +
                    "; apiMatch=" + string.Equals(candidate.Receipt!.Api, candidate.Journal.Api, StringComparison.Ordinal) +
                    "; eventRecords=" + string.Join(",", candidate.Events.Select(value => value.Seq.Value + ":" + value.Type + ":" + value.Seat));
                throw new InvalidOperationException("The persisted envelope does not contain exactly one durable commit bound to the confirmed Player action (" + diagnostic + ").");
            }

            RoundRecoveryState recovered = RoundRecoveryCodec.Decode(acceptedStatePayload!);
            if (recovered.Version != checked((long)commandVersion) || recovered.Spec.RoundId != roundId ||
                recovered.SourceFingerprint != acceptedFingerprint)
                throw new InvalidOperationException("The durable action state payload differs from its committed version, round, or fingerprint.");

            RoundStateMachine round = RoundRecovery.Restore(recovered);
            if (!string.Equals(round.RoundId, roundId, StringComparison.Ordinal))
                throw new InvalidOperationException("The restored durable action state belongs to another round.");
            if (string.Equals(actionEventType, "TsumoWin", StringComparison.Ordinal) &&
                (!round.IsFinished || round.Result?.Outcome.Kind != RoundOutcomeKind.Tsumo ||
                 round.Result.Outcome.WinnerSeat != actionSeat))
                throw new InvalidOperationException("The terminal authority checkpoint does not contain the confirmed Tsumo winner.");
            ConservationReport physical = TilePositionMap.Validate(round.Tiles);
            IReadOnlyList<TilePlacement> placements = round.Tiles.AsPlacements();
            if (!physical.IsValid || physical.AssignedCount != TileId.Count || placements.Count != TileId.Count)
                throw new InvalidOperationException("The exact accepted action state does not conserve all 136 physical entities.");

            var lines = new List<string>(placements.Count);
            var ids = new HashSet<int>();
            foreach (TilePlacement placement in placements)
            {
                if (!placement.Position.IsAssigned || placement.Tile.Value < 0 || placement.Tile.Value >= TileId.Count ||
                    !ids.Add(placement.Tile.Value))
                    throw new InvalidOperationException("The exact accepted action state contains an unassigned or duplicate physical entity.");
                lines.Add("tile|" + placement.Tile.Value.ToString("D3", CultureInfo.InvariantCulture) + "|" +
                    placement.Position.Zone.ToString().ToUpperInvariant() + "|" +
                    placement.Position.Seat.ToString(CultureInfo.InvariantCulture) + "|" +
                    placement.Position.Index.ToString(CultureInfo.InvariantCulture));
            }
            if (ids.Count != TileId.Count)
                throw new InvalidOperationException("The exact accepted action state omits a physical entity.");
            lines.Sort(StringComparer.Ordinal);
            return string.Join("\n", lines);
        }

        private static RoundStateMachine BuildCallWindow(string action, string? requestedRoundId)
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

            RoundStateMachine round = BuildDealt(requestedRoundId ?? "c04-" + action + "-seed-440" + (pon ? "1" : "2"), fixedOrder);
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

        private static RoundStateMachine BuildRiichiTurn(string? requestedRoundId)
        {
            var fixedOrder = new Dictionary<int, int>();
            int[] hand = { 0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 37, 72, 76, 124 };
            int[] slots = { 0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48, 52 };
            for (int i = 0; i < hand.Length; i++) fixedOrder.Add(slots[i], hand[i]);
            return BuildDealt(requestedRoundId ?? "c04-riichi-seed-4403", fixedOrder);
        }

        private static RoundStateMachine BuildClosedKanTurn(string? requestedRoundId)
        {
            var fixedOrder = new Dictionary<int, int>();
            int[] hand = { 0, 1, 2, 3, 36, 40, 44, 48, 52, 56, 60, 64, 68, 72 };
            int[] slots = { 0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48, 52 };
            for (int i = 0; i < hand.Length; i++) fixedOrder.Add(slots[i], hand[i]);
            fixedOrder.Add(122, 73); // dead-wall replacement draw physical entity
            return BuildDealt(requestedRoundId ?? "c04-closed-kan-seed-4404", fixedOrder);
        }

        private static RoundStateMachine BuildDealerTsumoTurn(string? requestedRoundId)
        {
            // Seat 0's thirteen tiles plus drawn 4m complete a Haku triplet,
            // 23m/4m, 345p, 678s and 55m. This fixture is used only by explicit
            // C04 validation and still enters LocalHost through its real host API.
            int[] hand = { 124, 125, 126, 4, 8, 48, 52, 56, 92, 96, 100, 16, 17 };
            int[] wallSlots = { 0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48 };
            var fixedOrder = new Dictionary<int, int>();
            for (int i = 0; i < hand.Length; i++) fixedOrder.Add(wallSlots[i], hand[i]);
            fixedOrder.Add(52, 12);
            return BuildDealt(requestedRoundId ?? "c04-local-dealer-tsumo-seed-4405", fixedOrder);
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
