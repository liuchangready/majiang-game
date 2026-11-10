using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.TableRuntime;

namespace Mahjong.LocalHost
{
    /// <summary>A local-only, public-safe view of a durably committed round history.</summary>
    public sealed class LocalRecordedRound
    {
        internal LocalRecordedRound(
            string id,
            string matchId,
            string roundId,
            DateTimeOffset recordedAt,
            string result,
            IReadOnlyList<LocalRecordedEvent> events)
        {
            Id = id;
            MatchId = matchId;
            RoundId = roundId;
            RecordedAt = recordedAt;
            Result = result;
            Events = events;
        }

        public string Id { get; }
        public string MatchId { get; }
        public string RoundId { get; }
        public DateTimeOffset RecordedAt { get; }
        public string Result { get; }
        public IReadOnlyList<LocalRecordedEvent> Events { get; }
    }

    /// <summary>Whitelisted public timeline facts; private payloads and event details are omitted.</summary>
    public sealed class LocalRecordedEvent
    {
        internal LocalRecordedEvent(ulong sequence, RoundEventType type, int seat, int? publicTile)
        {
            Sequence = sequence;
            TypeCode = type.ToString();
            Seat = seat;
            PublicTile = publicTile;
        }

        public ulong Sequence { get; }
        public string TypeCode { get; }
        public int Seat { get; }
        public int? PublicTile { get; }
    }

    /// <summary>Reads and validates local committed histories without opening or advancing a table.</summary>
    public static class LocalRecordedRounds
    {
        public static IReadOnlyList<LocalRecordedRound> List(
            string saveDirectory,
            string allowedMatchId,
            bool includeMatchRounds = false,
            int limit = 20)
        {
            if (limit < 1 || limit > 20) throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be 1..20.");
            if (!IsSafeId(allowedMatchId)) throw new ArgumentException("A trusted match scope is required.", nameof(allowedMatchId));

            string root = LocalTableHostPaths.ResolveSaveDirectory(saveDirectory);
            if (!Directory.Exists(root)) return Array.Empty<LocalRecordedRound>();
            var records = new Dictionary<string, ParsedRound>(StringComparer.Ordinal);
            foreach (string path in Directory.EnumerateFiles(root, "*.save", SearchOption.AllDirectories))
            {
                try
                {
                    LocalSave save = LocalSaveCodec.DecodeEnvelope(File.ReadAllBytes(path));
                    if (!RoomBelongsToScope(save.RoomId, allowedMatchId, includeMatchRounds)) continue;

                    TableCheckpoint checkpoint = LocalSaveCodec.DecodeCheckpoint(save.BaseCheckpointBlob);
                    if (!string.Equals(save.RoomId, checkpoint.RoomId, StringComparison.Ordinal))
                        throw new InvalidDataException("Local save and checkpoint room identities do not match.");
                    if (!TableCheckpointValidator.TryValidate(checkpoint, out _, out string checkpointDetail))
                        throw new InvalidDataException(checkpointDetail);

                    RoundRecoveryState baseState = RoundRecoveryCodec.Decode(checkpoint.StatePayload);
                    string roundId = baseState.Spec.RoundId;
                    if (!IsSafeId(roundId)) throw new InvalidDataException("Checkpoint round id is invalid.");
                    List<TableEventRecord> history = ValidateHistoryToHighWater(save, checkpoint, baseState);
                    if (history.Count == 0) continue;

                    var projected = new List<LocalRecordedEvent>();
                    foreach (TableEventRecord item in history)
                    {
                        LocalRecordedEvent? publicEvent = ProjectEvent(item);
                        if (publicEvent != null) projected.Add(publicEvent);
                    }
                    if (projected.Count == 0) continue;

                    var round = new ParsedRound(
                        StableId(allowedMatchId, roundId), allowedMatchId, roundId,
                        new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero),
                        save.TerminalState == LocalSaveTerminalState.RoundFinished || save.TerminalState == LocalSaveTerminalState.MatchFinished
                            ? "已结束" : "进行中",
                        projected.AsReadOnly(), history, save.CurrentState.StateVersion.Value);
                    MergeByRound(records, round);
                }
                catch (Exception exception) when (exception is IOException || exception is InvalidDataException ||
                                                   exception is ArgumentException || exception is UnauthorizedAccessException ||
                                                   exception is InvalidOperationException)
                {
                    System.Diagnostics.Trace.TraceWarning("LOCAL_RECORD_READ_FAILED: {0}: {1}", Path.GetFileName(path), exception.GetType().Name);
                    throw new InvalidDataException("A local recorded round could not be read.", exception);
                }
            }

            return records.Values
                .Select(value => value.ToPublic())
                .OrderByDescending(value => value.RecordedAt)
                .Take(limit)
                .ToArray();
        }

        private static List<TableEventRecord> ValidateHistoryToHighWater(
            LocalSave save,
            TableCheckpoint checkpoint,
            RoundRecoveryState checkpointState)
        {
            var history = checkpoint.CommittedHistoryPayload == null
                ? new List<TableEventRecord>()
                : new List<TableEventRecord>(TableCommittedHistoryCodec.Decode(checkpoint.CommittedHistoryPayload));
            ulong sequence = checkpoint.LastEventSeq.Value;
            ulong version = checkpoint.StateVersion.Value;
            byte[] currentStatePayload = checkpoint.StatePayload;
            var tailEvents = new List<TableEventRecord>();
            var tailJournals = new List<TableCommandJournalRecord>();

            foreach (LocalDurableOperation operation in save.Operations)
            {
                if (operation.ExpectedStateVersion < checkpoint.StateVersion.Value) continue;
                if (operation.Kind == LocalOperationKind.AppendTiming || operation.Kind == LocalOperationKind.AppendAudit) continue;
                if (operation.Kind != LocalOperationKind.Commit)
                    throw new InvalidDataException("A non-commit state operation follows the current checkpoint.");
                if (operation.ExpectedStateVersion != version)
                    throw new InvalidDataException("Durable operation state versions are not contiguous.");

                if (operation.Events.Count == 0)
                {
                    if (operation.Journal != null || operation.NextStatePayload != null)
                        throw new InvalidDataException("An eventless durable operation carries state-changing facts.");
                    continue;
                }

                if (version == ulong.MaxValue) throw new InvalidDataException("Durable command version overflowed.");
                ulong commandVersion = version + 1;
                if (operation.Journal == null ||
                    operation.Journal.CommandVersion.Value != commandVersion ||
                    operation.Journal.EvaluatedStateVersion.Value != version ||
                    TablePayloadSha256.Compute(operation.Journal.CommandPayload) != operation.Journal.CommandPayloadSha256 ||
                    (operation.Receipt != null &&
                     (!operation.Receipt.RequestId.Equals(operation.Journal.RequestId) ||
                      !operation.Receipt.ScopeId.Equals(operation.Journal.ScopeId) ||
                      !string.Equals(operation.Receipt.AccountId, operation.Journal.AccountId, StringComparison.Ordinal) ||
                      !string.Equals(operation.Receipt.Api, operation.Journal.Api, StringComparison.Ordinal) ||
                      operation.Receipt.PayloadSha256 != operation.Journal.PayloadSha256)) ||
                    (operation.Receipt == null && !operation.InternalCommand))
                    throw new InvalidDataException("Tail command journal does not bind its expected version and payload.");
                if (operation.NextStatePayload == null || operation.NextStatePayload.Length == 0)
                    throw new InvalidDataException("A committed tail command has no next-state payload.");

                RoundRecoveryState nextState = RoundRecoveryCodec.Decode(operation.NextStatePayload);
                if (nextState.Version != (long)commandVersion ||
                    !string.Equals(nextState.Spec.RoundId, checkpointState.Spec.RoundId, StringComparison.Ordinal) ||
                    nextState.SourceFingerprint != operation.NextStateFingerprint)
                    throw new InvalidDataException("Tail next-state identity/version/fingerprint is inconsistent.");

                for (int ordinal = 0; ordinal < operation.Events.Count; ordinal++)
                {
                    TableEventRecord item = operation.Events[ordinal];
                    if (sequence == ulong.MaxValue || item.Seq.Value != sequence + 1 ||
                        item.CommandVersion.Value != commandVersion || item.Ordinal != ordinal || item.EventSchema != 1)
                        throw new InvalidDataException("Tail event sequence/version/ordinal/schema is incomplete.");
                    if (item.Payload == null)
                        throw new InvalidDataException("Tail event is missing its command binding payload.");
                    TableEventFact fact = TableEventFactCodec.Decode(item.Payload);
                    if (fact.CommandVersion.Value != commandVersion || fact.Ordinal != ordinal ||
                        fact.SourceFingerprint != nextState.SourceFingerprint)
                        throw new InvalidDataException("Tail event does not match its independent command binding.");
                    history.Add(item);
                    tailEvents.Add(item);
                    sequence = item.Seq.Value;
                }

                version = commandVersion;
                currentStatePayload = operation.NextStatePayload;
                tailJournals.Add(operation.Journal);
            }

            if (!SameEventBytes(tailEvents, save.EventJournals) || !SameCommandJournals(tailJournals, save.CommandJournals))
                throw new InvalidDataException("Tail logs do not match the ordered durable commit operations.");
            if (sequence != save.CurrentState.LastEventSeq.Value || version != save.CurrentState.StateVersion.Value ||
                TablePayloadSha256.Compute(currentStatePayload) != save.CurrentState.StateHash)
                throw new InvalidDataException("Committed history does not reach the durable state high-water.");
            RoundRecoveryState finalState = RoundRecoveryCodec.Decode(currentStatePayload);
            if (finalState.SourceFingerprint != save.CurrentState.StateFingerprint ||
                finalState.Version != (long)save.CurrentState.StateVersion.Value ||
                !string.Equals(finalState.Spec.RoundId, checkpointState.Spec.RoundId, StringComparison.Ordinal))
                throw new InvalidDataException("Durable high-water state does not match the committed history round.");
            return history;
        }

        private static bool SameEventBytes(IReadOnlyList<TableEventRecord> left, IReadOnlyList<TableEventRecord> right) =>
            TableCommittedHistoryCodec.Encode(left).AsSpan().SequenceEqual(TableCommittedHistoryCodec.Encode(right));

        private static bool SameCommandJournals(
            IReadOnlyList<TableCommandJournalRecord> left,
            IReadOnlyList<TableCommandJournalRecord> right)
        {
            if (left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++)
            {
                TableCommandJournalRecord a = left[i];
                TableCommandJournalRecord b = right[i];
                if (a.CommandVersion != b.CommandVersion || a.EvaluatedStateVersion != b.EvaluatedStateVersion ||
                    !a.RequestId.Equals(b.RequestId) || !string.Equals(a.AccountId, b.AccountId, StringComparison.Ordinal) ||
                    !a.ScopeId.Equals(b.ScopeId) ||
                    a.Api != b.Api || a.PayloadSha256 != b.PayloadSha256 || a.SchemaVersion != b.SchemaVersion ||
                    !a.CommandPayload.AsSpan().SequenceEqual(b.CommandPayload)) return false;
            }
            return true;
        }

        private static void MergeByRound(Dictionary<string, ParsedRound> records, ParsedRound candidate)
        {
            string key = candidate.MatchId + "\0" + candidate.RoundId;
            if (!records.TryGetValue(key, out ParsedRound? existing))
            {
                records.Add(key, candidate);
                return;
            }

            int shared = Math.Min(existing.History.Count, candidate.History.Count);
            if (!SameEventBytes(existing.History.Take(shared).ToArray(), candidate.History.Take(shared).ToArray()))
                throw new InvalidDataException("Physical saves conflict for the same match/round identity.");
            ParsedRound preferred = candidate.History.Count > existing.History.Count ||
                (candidate.History.Count == existing.History.Count && candidate.StateVersion > existing.StateVersion)
                    ? candidate : existing;
            if (candidate.History.Count == existing.History.Count && candidate.StateVersion == existing.StateVersion &&
                !string.Equals(candidate.Result, existing.Result, StringComparison.Ordinal))
                throw new InvalidDataException("Equivalent match/round copies disagree about terminal result.");
            preferred.RecordedAt = candidate.RecordedAt > existing.RecordedAt ? candidate.RecordedAt : existing.RecordedAt;
            records[key] = preferred;
        }

        private static string StableId(string matchId, string roundId)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(matchId + "\0" + roundId));
                return Convert.ToBase64String(digest).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            }
        }

        private static bool RoomBelongsToScope(string roomId, string matchId, bool includeMatchRounds)
        {
            if (string.Equals(roomId, matchId, StringComparison.Ordinal)) return true;
            if (!includeMatchRounds || !roomId.StartsWith(matchId + "-r", StringComparison.Ordinal)) return false;
            string ordinal = roomId.Substring(matchId.Length + 2);
            if (ordinal.Length == 0) return false;
            for (int i = 0; i < ordinal.Length; i++)
                if (ordinal[i] < '0' || ordinal[i] > '9') return false;
            return true;
        }

        private static bool IsSafeId(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 128) return false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                      (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.')) return false;
            }
            return value != "." && value != "..";
        }

        internal static LocalRecordedEvent? ProjectEvent(TableEventRecord item)
        {
            bool tileIsPublic = item.Type == RoundEventType.DoraRevealed || item.Type == RoundEventType.TileDiscarded ||
                                item.Type == RoundEventType.ChiDeclared || item.Type == RoundEventType.PonDeclared;
            switch (item.Type)
            {
                case RoundEventType.RoundStarted:
                case RoundEventType.TilesDealt:
                case RoundEventType.DoraRevealed:
                case RoundEventType.TileDrawn:
                case RoundEventType.RinshanDrawn:
                case RoundEventType.TileDiscarded:
                case RoundEventType.ResponseWindowOpened:
                case RoundEventType.ChankanWindowOpened:
                case RoundEventType.ResponseClaimed:
                case RoundEventType.RiichiDeclared:
                case RoundEventType.ChiDeclared:
                case RoundEventType.PonDeclared:
                case RoundEventType.KanDeclared:
                case RoundEventType.TsumoWin:
                case RoundEventType.RonWin:
                case RoundEventType.ExhaustiveDraw:
                case RoundEventType.AbortiveDraw:
                case RoundEventType.RoundEnded:
                    return new LocalRecordedEvent(item.Seq.Value, item.Type, item.Seat,
                        tileIsPublic ? item.Tile?.Value : null);
                default:
                    return null;
            }
        }

        private sealed class ParsedRound
        {
            public ParsedRound(string id, string matchId, string roundId, DateTimeOffset recordedAt, string result,
                IReadOnlyList<LocalRecordedEvent> projected, List<TableEventRecord> history, ulong stateVersion)
            {
                Id = id;
                MatchId = matchId;
                RoundId = roundId;
                RecordedAt = recordedAt;
                Result = result;
                Projected = projected;
                History = history;
                StateVersion = stateVersion;
            }

            public string Id { get; }
            public string MatchId { get; }
            public string RoundId { get; }
            public DateTimeOffset RecordedAt { get; set; }
            public string Result { get; }
            public IReadOnlyList<LocalRecordedEvent> Projected { get; }
            public List<TableEventRecord> History { get; }
            public ulong StateVersion { get; }
            public LocalRecordedRound ToPublic() => new LocalRecordedRound(Id, MatchId, RoundId, RecordedAt, Result, Projected);
        }
    }
}
