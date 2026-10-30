using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Mahjong.TableRuntime;

namespace Mahjong.LocalHost
{
    /// <summary>
    /// One persisted confirmed request/receipt fact. Identity is the full
    /// account + scope + request composite; payloadSha256 is the real ToHex value of the
    /// committed payload digest, never a re-serialized approximation.
    /// </summary>
    public sealed class LocalTableRecoveryRequestFact
    {
        public LocalTableRecoveryRequestFact(
            string accountId,
            string scopeId,
            string requestId,
            string receiptId,
            bool confirmed,
            string status,
            string payloadSha256,
            string evaluatedStateVersion,
            string? commandVersion,
            string? eventSeq)
        {
            AccountId = accountId;
            ScopeId = scopeId;
            RequestId = requestId;
            ReceiptId = receiptId;
            Confirmed = confirmed;
            Status = status;
            PayloadSha256 = payloadSha256;
            EvaluatedStateVersion = evaluatedStateVersion;
            CommandVersion = commandVersion;
            EventSeq = eventSeq;
        }

        public string AccountId { get; }
        public string ScopeId { get; }
        public string RequestId { get; }
        public string ReceiptId { get; }
        public bool Confirmed { get; }
        public string Status { get; }
        public string PayloadSha256 { get; }
        public string EvaluatedStateVersion { get; }
        public string? CommandVersion { get; }
        public string? EventSeq { get; }
    }

    /// <summary>Durable cheat-ledger digest. Entries are the real committed ledger order.</summary>
    public sealed class LocalTableRecoveryLedgerFact
    {
        public LocalTableRecoveryLedgerFact(
            long points,
            int initialPoints,
            IReadOnlyList<int> balances,
            int entryCount,
            string entriesSha256,
            string balancesSha256,
            long totalVersion,
            long checkpointVersion,
            string source)
        {
            Points = points;
            InitialPoints = initialPoints;
            Balances = balances;
            EntryCount = entryCount;
            EntriesSha256 = entriesSha256;
            BalancesSha256 = balancesSha256;
            TotalVersion = totalVersion;
            CheckpointVersion = checkpointVersion;
            Source = source;
        }

        public long Points { get; }
        public int InitialPoints { get; }
        public IReadOnlyList<int> Balances { get; }
        public int EntryCount { get; }
        public string EntriesSha256 { get; }
        public string BalancesSha256 { get; }
        public long TotalVersion { get; }
        public long CheckpointVersion { get; }
        public string Source { get; }
    }

    /// <summary>136-entity physical digest, one exact position per entity.</summary>
    public sealed class LocalTableRecoveryPhysicalFact
    {
        public LocalTableRecoveryPhysicalFact(
            int tileCount,
            int uniqueCount,
            int wallCount,
            int deadWallCount,
            string tilesSha256)
        {
            TileCount = tileCount;
            UniqueCount = uniqueCount;
            WallCount = wallCount;
            DeadWallCount = deadWallCount;
            TilesSha256 = tilesSha256;
        }

        public int TileCount { get; }
        public int UniqueCount { get; }
        public int WallCount { get; }
        public int DeadWallCount { get; }
        public string TilesSha256 { get; }
    }

    /// <summary>
    /// Immutable facts derived only from one persisted local authority envelope. It never
    /// opens a host, takes a lease, advances a game or writes a save.
    /// </summary>
    public sealed class LocalTableRecoveryFacts
    {
        internal LocalTableRecoveryFacts(
            string authoritySaveSha256,
            int authoritySaveBytes,
            string roomId,
            string roundId,
            int rulesetVersion,
            string stateVersion,
            string eventSeq,
            string stateFingerprint,
            string stateHash,
            string controlVersion,
            IReadOnlyList<long> controlEpochs,
            string cheatVersion,
            string cheatFingerprint,
            IReadOnlyList<LocalTableRecoveryRequestFact> requests,
            LocalTableRecoveryLedgerFact ledger,
            LocalTableRecoveryPhysicalFact physical)
        {
            AuthoritySaveSha256 = authoritySaveSha256;
            AuthoritySaveBytes = authoritySaveBytes;
            RoomId = roomId;
            RoundId = roundId;
            RulesetVersion = rulesetVersion;
            StateVersion = stateVersion;
            EventSeq = eventSeq;
            StateFingerprint = stateFingerprint;
            StateHash = stateHash;
            ControlVersion = controlVersion;
            ControlEpochs = controlEpochs;
            CheatVersion = cheatVersion;
            CheatFingerprint = cheatFingerprint;
            Requests = requests;
            Ledger = ledger;
            Physical = physical;
        }

        public string AuthoritySaveSha256 { get; }
        public int AuthoritySaveBytes { get; }
        public string RoomId { get; }
        public string RoundId { get; }
        public int RulesetVersion { get; }
        public string StateVersion { get; }
        public string EventSeq { get; }
        public string StateFingerprint { get; }
        public string StateHash { get; }
        public string ControlVersion { get; }
        public IReadOnlyList<long> ControlEpochs { get; }
        public string CheatVersion { get; }
        public string CheatFingerprint { get; }
        public IReadOnlyList<LocalTableRecoveryRequestFact> Requests { get; }
        public LocalTableRecoveryLedgerFact Ledger { get; }
        public LocalTableRecoveryPhysicalFact Physical { get; }
    }

    /// <summary>Read-only snapshot captured from an already-open, committed LocalHost runtime.</summary>
    public sealed class LocalTableRecoveryCapture
    {
        internal LocalTableRecoveryCapture(byte[] authoritySaveBytes, LocalTableRecoveryFacts facts)
        {
            AuthoritySaveBytes = authoritySaveBytes ?? throw new ArgumentNullException(nameof(authoritySaveBytes));
            Facts = facts ?? throw new ArgumentNullException(nameof(facts));
        }

        /// <summary>Exact primary envelope bytes at the captured version. Private authority data.</summary>
        public byte[] AuthoritySaveBytes { get; }

        public LocalTableRecoveryFacts Facts { get; }
    }

    /// <summary>
    /// Strict read-only decoder for the persisted LocalHost envelope. It validates the
    /// envelope/checkpoint/round/control/cheat digests before deriving the comparison facts.
    /// </summary>
    public static class LocalTableRecoveryFactsReader
    {
        public static LocalTableRecoveryFacts ReadPersistedFacts(byte[] saveBytes)
        {
            if (saveBytes == null)
            {
                throw new ArgumentNullException(nameof(saveBytes));
            }

            if (saveBytes.Length == 0)
            {
                throw new InvalidDataException("Authority save is empty.");
            }

            string authoritySha256 = Sha256Hex(saveBytes);
            LocalSave save = LocalSaveCodec.DecodeEnvelope(saveBytes);
            if (save.BaseCheckpointBlob.Length == 0 || save.CheckpointSha256.IsEmpty)
            {
                throw new InvalidDataException("Authority save has no base checkpoint.");
            }

            TableCheckpoint checkpoint = LocalSaveCodec.DecodeCheckpoint(save.BaseCheckpointBlob);
            Require(checkpoint.RulesetVersion == save.RulesetVersion, "checkpoint ruleset version differs from the envelope.");
            Require(string.Equals(checkpoint.RoomId, save.RoomId, StringComparison.Ordinal), "checkpoint room id differs from the envelope.");

            // The envelope carries one base checkpoint plus the exact durable operations after
            // it. The current round/state bytes therefore come from the last accepted commit's
            // NextStatePayload, not from the older base checkpoint. Operations are applied in
            // their persisted order and are never mixed with a later version.
            byte[] currentStatePayload = checkpoint.StatePayload;
            ulong lastCommittedEventSeq = checkpoint.LastEventSeq.Value;
            for (int i = 0; i < save.Operations.Count; i++)
            {
                LocalDurableOperation operation = save.Operations[i];
                if (operation.Kind != LocalOperationKind.Commit || operation.Events.Count == 0)
                {
                    continue;
                }

                if (operation.NextStatePayload == null || operation.NextStatePayload.Length == 0)
                {
                    throw new InvalidDataException("A state-changing operation has no next state payload.");
                }

                currentStatePayload = operation.NextStatePayload;
                lastCommittedEventSeq = operation.Events[operation.Events.Count - 1].Seq.Value;
            }

            Require(lastCommittedEventSeq == save.CurrentState.LastEventSeq.Value, "latest operation event sequence differs from the saved state row.");

            RoundRecoveryState captured = RoundRecoveryCodec.Decode(currentStatePayload);
            RoundStateMachine round = RoundRecovery.Restore(captured);
            Require(round.RoundId == captured.Spec.RoundId, "restored round id differs from the captured spec.");
            Require(captured.Version == (long)save.CurrentState.StateVersion.Value, "current round version differs from the saved state row.");
            Require(TablePayloadSha256.Compute(currentStatePayload) == save.CurrentState.StateHash, "current round bytes do not match the saved state hash.");
            Require(captured.SourceFingerprint == save.CurrentState.StateFingerprint, "current round fingerprint differs from the saved state row.");

            TableControlCheckpoint control = TableControlCheckpointCodec.Decode(checkpoint.ControlPayload);
            Require(control.ControlVersion == save.CurrentState.ControlVersion, "control payload version differs from the saved state row.");

            if (checkpoint.CheatPayload == null || checkpoint.CheatPayload.Length == 0)
            {
                throw new InvalidDataException("Authority save has no committed cheat ledger payload.");
            }

            // The latest full checkpoint is the only persisted ledger snapshot. This exporter
            // does not replay cheat operations and does not claim otherwise; the facts carry the
            // checkpoint's own version/source so the verifier can compare them explicitly.
            TableCheatLedgerCheckpoint cheat = TableCheatLedgerCheckpointCodec.Decode(checkpoint.CheatPayload);
            cheat.RestoreCheatState();

            var controlEpochs = new long[4];
            for (int seat = 0; seat < 4; seat++)
            {
                controlEpochs[seat] = control.Snapshot.Seats[seat].ControlEpoch;
            }

            LocalTableRecoveryPhysicalFact physical = BuildPhysical(round);
            LocalTableRecoveryLedgerFact ledger = BuildLedger(cheat);
            IReadOnlyList<LocalTableRecoveryRequestFact> requests = BuildRequests(save);

            return new LocalTableRecoveryFacts(
                authoritySha256,
                saveBytes.Length,
                save.RoomId,
                round.RoundId,
                save.RulesetVersion,
                save.CurrentState.StateVersion.Value.ToString(CultureInfo.InvariantCulture),
                save.CurrentState.LastEventSeq.Value.ToString(CultureInfo.InvariantCulture),
                save.CurrentState.StateFingerprint.ToString(CultureInfo.InvariantCulture),
                save.CurrentState.StateHash.ToHex().ToUpperInvariant(),
                save.CurrentState.ControlVersion.ToString(CultureInfo.InvariantCulture),
                controlEpochs,
                save.CurrentState.CheatVersion.ToString(CultureInfo.InvariantCulture),
                save.CurrentState.CheatFingerprint.ToString(CultureInfo.InvariantCulture),
                requests,
                ledger,
                physical);
        }

        private static LocalTableRecoveryPhysicalFact BuildPhysical(RoundStateMachine round)
        {
            IReadOnlyList<TilePlacement> placements = round.Tiles.AsPlacements();
            if (placements.Count != TilePositionMap.TotalTileCount)
            {
                throw new InvalidDataException("Physical map does not contain exactly 136 entities.");
            }

            var seen = new HashSet<int>();
            var lines = new List<string>(TilePositionMap.TotalTileCount);
            int wall = 0;
            int deadWall = 0;
            for (int i = 0; i < placements.Count; i++)
            {
                TilePlacement placement = placements[i];
                if (!placement.Position.IsAssigned)
                {
                    throw new InvalidDataException("Entity " + placement.Tile.Value.ToString(CultureInfo.InvariantCulture) + " is unassigned.");
                }

                if (!seen.Add(placement.Tile.Value))
                {
                    throw new InvalidDataException("Physical map repeats entity " + placement.Tile.Value.ToString(CultureInfo.InvariantCulture) + ".");
                }

                TilePosition position = placement.Position;
                if (position.Zone == TileZone.Wall)
                {
                    wall++;
                }
                else if (position.Zone == TileZone.DeadWall)
                {
                    deadWall++;
                }

                lines.Add(
                    "tile|" +
                    placement.Tile.Value.ToString("D3", CultureInfo.InvariantCulture) + "|" +
                    position.Zone.ToString().ToUpperInvariant() + "|" +
                    position.Seat.ToString(CultureInfo.InvariantCulture) + "|" +
                    position.Index.ToString(CultureInfo.InvariantCulture));
            }

            if (seen.Count != TilePositionMap.TotalTileCount || wall + deadWall > TilePositionMap.TotalTileCount)
            {
                throw new InvalidDataException("Physical map is not a complete 136-entity set.");
            }

            lines.Sort(StringComparer.Ordinal);
            return new LocalTableRecoveryPhysicalFact(
                placements.Count,
                seen.Count,
                wall,
                deadWall,
                Sha256Hex(Utf8.GetBytes(string.Join("\n", lines))));
        }

        private static LocalTableRecoveryLedgerFact BuildLedger(TableCheatLedgerCheckpoint cheat)
        {
            var balances = new int[cheat.Balances.Count];
            long points = 0;
            for (int i = 0; i < balances.Length; i++)
            {
                balances[i] = cheat.Balances[i];
                points = checked(points + balances[i]);
            }

            var balanceText = string.Join(",", balances.Select(value => value.ToString(CultureInfo.InvariantCulture)));
            var lines = new List<string>
            {
                "initial=" + cheat.InitialPoints.ToString(CultureInfo.InvariantCulture),
                "balances=" + balanceText
            };

            var expectedBalances = new int[4];
            for (int seat = 0; seat < 4; seat++)
            {
                expectedBalances[seat] = cheat.InitialPoints;
            }

            for (int i = 0; i < cheat.Entries.Count; i++)
            {
                CheatLedgerEntry entry = cheat.Entries[i];
                expectedBalances[entry.Seat] = checked(expectedBalances[entry.Seat] + entry.Amount);
                if (entry.BalanceAfter != expectedBalances[entry.Seat])
                {
                    throw new InvalidDataException("Ledger entry " + entry.EntryId + " balanceAfter is inconsistent.");
                }

                lines.Add(
                    "entry|" + i.ToString(CultureInfo.InvariantCulture) + "|" +
                    entry.EntryId + "|" +
                    entry.Seat.ToString(CultureInfo.InvariantCulture) + "|" +
                    entry.Amount.ToString(CultureInfo.InvariantCulture) + "|" +
                    entry.BalanceAfter.ToString(CultureInfo.InvariantCulture) + "|" +
                    entry.Reason);
            }

            return new LocalTableRecoveryLedgerFact(
                points,
                cheat.InitialPoints,
                balances,
                cheat.Entries.Count,
                Sha256Hex(Utf8.GetBytes(string.Join("\n", lines))),
                Sha256Hex(Utf8.GetBytes("balances=" + balanceText)),
                cheat.TotalVersion,
                cheat.TotalVersion,
                "LATEST_PERSISTED_CHECKPOINT");
        }

        private static IReadOnlyList<LocalTableRecoveryRequestFact> BuildRequests(LocalSave save)
        {
            var requests = new List<LocalTableRecoveryRequestFact>(save.Receipts.Count);
            foreach (TableDurableReceipt receipt in save.Receipts.Values)
            {
                string identity = receipt.AccountId + "\u001f" + receipt.ScopeId.Value + "\u001f" + receipt.RequestId.Value;
                string receiptId = "rcpt-" + Sha256Hex(Utf8.GetBytes(identity));
                bool confirmed = receipt.Receipt.Status == ReceiptStatus.Applied ||
                                 receipt.Receipt.Status == ReceiptStatus.Recorded;
                requests.Add(new LocalTableRecoveryRequestFact(
                    receipt.AccountId,
                    receipt.ScopeId.Value,
                    receipt.RequestId.Value,
                    receiptId,
                    confirmed,
                    receipt.Receipt.Status.ToString().ToUpperInvariant(),
                    receipt.PayloadSha256.ToHex().ToUpperInvariant(),
                    receipt.Receipt.EvaluatedStateVersion.Value.ToString(CultureInfo.InvariantCulture),
                    receipt.Receipt.CommandVersion.HasValue
                        ? receipt.Receipt.CommandVersion.Value.Value.ToString(CultureInfo.InvariantCulture)
                        : null,
                    receipt.Receipt.EventSeq.HasValue
                        ? receipt.Receipt.EventSeq.Value.Value.ToString(CultureInfo.InvariantCulture)
                        : null));
            }

            requests.Sort((left, right) =>
            {
                int result = string.CompareOrdinal(left.AccountId, right.AccountId);
                if (result != 0)
                {
                    return result;
                }

                result = string.CompareOrdinal(left.ScopeId, right.ScopeId);
                return result != 0 ? result : string.CompareOrdinal(left.RequestId, right.RequestId);
            });
            return requests;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidDataException(message);
            }
        }

        private static string Sha256Hex(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return ToHex(sha.ComputeHash(bytes));
            }
        }

        private static string ToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
            {
                builder.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
    }
}
