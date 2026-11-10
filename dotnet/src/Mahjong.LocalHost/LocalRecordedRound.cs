using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Mahjong.Domain;
using Mahjong.TableRuntime;

namespace Mahjong.LocalHost
{
    /// <summary>A local-only, public-safe view of a durably committed round history.</summary>
    public sealed class LocalRecordedRound
    {
        internal LocalRecordedRound(string id, string roundId, DateTimeOffset recordedAt, string result, IReadOnlyList<LocalRecordedEvent> events)
        {
            Id = id;
            RoundId = roundId;
            RecordedAt = recordedAt;
            Result = result;
            Events = events;
        }

        public string Id { get; }
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

    /// <summary>Reads committed local save history without opening a table or advancing authority.</summary>
    public static class LocalRecordedRounds
    {
        public static IReadOnlyList<LocalRecordedRound> List(string saveDirectory, int limit = 20)
        {
            if (limit < 1 || limit > 20) throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be 1..20.");
            string root = LocalTableHostPaths.ResolveSaveDirectory(saveDirectory);
            if (!Directory.Exists(root)) return Array.Empty<LocalRecordedRound>();
            var rows = new List<LocalRecordedRound>();
            foreach (string path in Directory.EnumerateFiles(root, "*.save", SearchOption.AllDirectories))
            {
                try
                {
                    LocalSave save = LocalSaveCodec.DecodeEnvelope(File.ReadAllBytes(path));
                    TableCheckpoint checkpoint = LocalSaveCodec.DecodeCheckpoint(save.BaseCheckpointBlob);
                    if (!string.Equals(save.RoomId, checkpoint.RoomId, StringComparison.Ordinal))
                        throw new InvalidDataException("Local save and checkpoint room identities do not match.");
                    // A checkpoint without the full history cannot produce a faithful player replay.
                    if (checkpoint.CommittedHistoryPayload == null && checkpoint.LastEventSeq.Value != 0) continue;
                    var events = new List<TableEventRecord>();
                    if (checkpoint.CommittedHistoryPayload != null)
                        events.AddRange(TableCommittedHistoryCodec.Decode(checkpoint.CommittedHistoryPayload));
                    foreach (TableEventRecord item in save.EventJournals)
                        if (checkpoint.CommittedHistoryPayload == null || item.Seq.Value > checkpoint.LastEventSeq.Value)
                            events.Add(item);
                    events.Sort((a, b) => a.Seq.Value.CompareTo(b.Seq.Value));
                    var projected = new List<LocalRecordedEvent>();
                    ulong previous = 0;
                    foreach (TableEventRecord item in events)
                    {
                        if (item.Seq.Value <= previous || (previous != 0 && item.Seq.Value != previous + 1))
                            throw new InvalidDataException("Committed event sequence is duplicated, incomplete, or out of order.");
                        previous = item.Seq.Value;
                        LocalRecordedEvent? publicEvent = ProjectEvent(item);
                        if (publicEvent != null) projected.Add(publicEvent);
                    }
                    if (projected.Count == 0) continue;
                    string relative = path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string id;
                    using (SHA256 sha = SHA256.Create())
                        id = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(relative)));
                    id = id.TrimEnd('=').Replace('+', '-').Replace('/', '_');
                    string result = save.TerminalState == LocalSaveTerminalState.RoundFinished || save.TerminalState == LocalSaveTerminalState.MatchFinished
                        ? "已结束"
                        : "进行中";
                    var record = new LocalRecordedRound(id, checkpoint.RoomId,
                        new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero), result, projected.AsReadOnly());
                    rows.Add(record);
                }
                catch (Exception exception) when (exception is IOException || exception is InvalidDataException ||
                                                   exception is ArgumentException || exception is UnauthorizedAccessException)
                {
                    System.Diagnostics.Trace.TraceWarning("LOCAL_RECORD_READ_FAILED: {0}: {1}", Path.GetFileName(path), exception.GetType().Name);
                    throw new InvalidDataException("A local recorded round could not be read.", exception);
                }
            }
            return rows.OrderByDescending(row => row.RecordedAt).Take(limit).ToArray();
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
    }
}
