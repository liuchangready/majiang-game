using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.TableRuntime;

namespace Mahjong.LocalHost
{
    /// <summary>Match-level local host options. Each round still uses its own durable room.</summary>
    public sealed class LocalMatchHostOptions
    {
        public string SaveDirectory { get; set; } = string.Empty;
        public string MatchId { get; set; } = string.Empty;
        public int RulesetVersion { get; set; } = 1;
        public string OwnerInstanceId { get; set; } = Guid.NewGuid().ToString("N");
        public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(10);
        public LocalHostEnvironment? Environment { get; set; }

        public MatchRuleConfig? Rules { get; set; }

        public bool DeclareAgariYameAtFinalRound { get; set; }

        /// <summary>
        /// Test-only deterministic round seam. Production leaves this null and uses the trusted
        /// environment wall; acceptance tests may inject a legal, repeatable wall while the
        /// coordinator still owns persistence, progression and score/stick carry-over.
        /// </summary>
        internal Func<LocalMatchState, LocalTableHostOptions, RoundStateMachine>? RoundFactory { get; set; }

        internal void Validate()
        {
            LocalTableHostPaths.ResolveSaveDirectory(SaveDirectory);
            if (string.IsNullOrWhiteSpace(MatchId) || MatchId.Length > 64)
            {
                throw new LocalTableException(LocalTableErrorKind.InvalidOptions, "MatchId must be 1..64 characters.");
            }

            for (int i = 0; i < MatchId.Length; i++)
            {
                char c = MatchId[i];
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ||
                      c == '-' || c == '_' || c == '.'))
                {
                    throw new LocalTableException(LocalTableErrorKind.InvalidOptions, "MatchId contains an unsafe character.");
                }
            }

            if (RulesetVersion < 1)
            {
                throw new LocalTableException(LocalTableErrorKind.InvalidOptions, "RulesetVersion must be positive.");
            }
        }
    }

    /// <summary>One match round handle. The caller owns the returned channel/session.</summary>
    public sealed class LocalMatchRound
    {
        internal LocalMatchRound(LocalTableChannel channel, string ticket, string roomId, string roundId, int ordinal)
        {
            Channel = channel;
            Ticket = ticket;
            RoomId = roomId;
            RoundId = roundId;
            Ordinal = ordinal;
        }

        public ITableChannel Channel { get; }
        public string Ticket { get; }
        public string RoomId { get; }
        public string RoundId { get; }
        public int Ordinal { get; }
    }

    /// <summary>Read-only match snapshot for UI/acceptance assertions.</summary>
    public sealed class LocalMatchSnapshot
    {
        public LocalMatchSnapshot(
            string matchId,
            int ordinal,
            string roundId,
            int dealerSeat,
            int roundWind,
            int roundNumber,
            int honba,
            IReadOnlyList<long> scores,
            IReadOnlyList<long> finalScores,
            int riichiSticks,
            bool finished,
            string finishReason,
            IReadOnlyList<RankingEntry> rankings,
            IReadOnlyList<LocalMatchRoundRecord> records)
        {
            MatchId = matchId;
            Ordinal = ordinal;
            RoundId = roundId;
            DealerSeat = dealerSeat;
            RoundWind = roundWind;
            RoundNumber = roundNumber;
            Honba = honba;
            Scores = scores;
            FinalScores = finalScores;
            RiichiSticks = riichiSticks;
            Finished = finished;
            FinishReason = finishReason;
            Rankings = rankings;
            Records = records;
        }

        public string MatchId { get; }
        public int Ordinal { get; }
        public string RoundId { get; }
        public int DealerSeat { get; }
        public int RoundWind { get; }
        public int RoundNumber { get; }
        public int Honba { get; }
        public IReadOnlyList<long> Scores { get; }
        public IReadOnlyList<long> FinalScores { get; }
        public int RiichiSticks { get; }
        public bool Finished { get; }
        public string FinishReason { get; }
        public IReadOnlyList<RankingEntry> Rankings { get; }
        public IReadOnlyList<LocalMatchRoundRecord> Records { get; }
    }

    public sealed class LocalMatchRoundRecord
    {
        public LocalMatchRoundRecord(
            string roundId,
            string resultId,
            RoundOutcomeKind outcomeKind,
            int winnerSeat,
            int loserSeat,
            IReadOnlyList<long> finalScores,
            int remainingRiichiSticks)
        {
            RoundId = roundId;
            ResultId = resultId;
            OutcomeKind = outcomeKind;
            WinnerSeat = winnerSeat;
            LoserSeat = loserSeat;
            FinalScores = finalScores;
            RemainingRiichiSticks = remainingRiichiSticks;
        }

        public string RoundId { get; }
        public string ResultId { get; }
        public RoundOutcomeKind OutcomeKind { get; }
        public int WinnerSeat { get; }
        public int LoserSeat { get; }
        public IReadOnlyList<long> FinalScores { get; }
        public int RemainingRiichiSticks { get; }
    }

    /// <summary>
    /// Persistent local match coordinator. It reuses the accepted Domain MatchProgression for
    /// next-round/score/honba/riichi/finish decisions and opens a completely new durable
    /// store/actor for every round, so a terminal round is never revived.
    /// </summary>
    public sealed class LocalMatchCoordinator : IAsyncDisposable
    {
        private readonly LocalMatchHostOptions _options;
        private readonly LocalSnapshotFacts _facts;
        private readonly ITableClock _clock;
        private readonly string _statePath;
        private readonly string _lockPath;
        private FileStream? _lockStream;
        private readonly object _gate = new object();

        private LocalMatchState _state;
        private LocalTableChannel? _channel;
        private string? _ticket;
        private bool _disposed;

        private LocalMatchCoordinator(
            LocalMatchHostOptions options,
            LocalSnapshotFacts facts,
            ITableClock clock,
            string statePath,
            string lockPath,
            FileStream lockStream,
            LocalMatchState state)
        {
            _options = options;
            _facts = facts;
            _clock = clock;
            _statePath = statePath;
            _lockPath = lockPath;
            _lockStream = lockStream;
            _state = state;
        }

        public static ValueTask<LocalMatchCoordinator> OpenAsync(
            LocalMatchHostOptions options,
            CancellationToken cancellationToken = default)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            options.Validate();
            cancellationToken.ThrowIfCancellationRequested();
            string root = LocalTableHostPaths.ResolveSaveDirectory(options.SaveDirectory);
            Directory.CreateDirectory(root);
            string statePath = Path.Combine(root, options.MatchId + ".match");
            string lockPath = statePath + ".lock";
            FileStream lockStream;
            try
            {
                lockStream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception)
            {
                throw new LocalTableException(
                    LocalTableErrorKind.LeaseConflict,
                    "The match is already open by another coordinator/process.",
                    exception);
            }

            try
            {
                LocalHostEnvironment environment = options.Environment ?? LocalHostEnvironment.System;
                var facts = new LocalSnapshotFacts(environment, environment.ResolveClock());
                LocalMatchState? loaded = LocalMatchStateCodec.Load(statePath);
                LocalMatchState state = loaded ?? LocalMatchState.Create(options.MatchId);
                if (loaded == null)
                {
                    LocalMatchStateCodec.Save(statePath, state, 0);
                }

                return new ValueTask<LocalMatchCoordinator>(
                    new LocalMatchCoordinator(
                        options,
                        facts,
                        environment.ResolveClock(),
                        statePath,
                        lockPath,
                        lockStream,
                        state));
            }
            catch
            {
                lockStream.Dispose();
                throw;
            }
        }

        public LocalMatchSnapshot Snapshot
        {
            get
            {
                lock (_gate)
                {
                    return _state.ToSnapshot();
                }
            }
        }

        public bool IsFinished => Snapshot.Finished;

        public async Task<LocalMatchRound> OpenRoundAsync(CancellationToken cancellationToken = default)
        {
            LocalMatchState state;
            LocalMatchRound? existing = null;
            lock (_gate)
            {
                ThrowIfDisposed();
                state = _state;
                if (state.Finished)
                {
                    throw new LocalTableException(LocalTableErrorKind.Unavailable, "The match is already finished.");
                }

                if (_channel != null && _ticket != null)
                {
                    existing = new LocalMatchRound(
                        _channel,
                        _ticket,
                        state.CurrentRoomId,
                        state.CurrentRoundId,
                        state.Ordinal);
                }
            }

            if (existing != null)
            {
                return existing;
            }

            string root = Path.Combine(
                LocalTableHostPaths.ResolveSaveDirectory(_options.SaveDirectory),
                _options.MatchId,
                "r" + state.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var roundOptions = new LocalTableHostOptions
            {
                SaveDirectory = root,
                RoomId = state.CurrentRoomId,
                RulesetVersion = _options.RulesetVersion,
                OwnerInstanceId = _options.OwnerInstanceId,
                LeaseDuration = _options.LeaseDuration,
                Environment = _options.Environment
            };
            RoundStateMachine initialRound = _options.RoundFactory != null
                ? _options.RoundFactory(state, roundOptions)
                : _facts.CreateRound(
                    new LocalTableHostOptions { RoomId = state.CurrentRoomId, SaveDirectory = root },
                    state.CurrentRoundId,
                    state.DealerSeat,
                    state.RoundWind,
                    state.RoundNumber,
                    state.Honba,
                    state.Scores,
                    state.RiichiSticks);
            if (!string.Equals(initialRound.RoundId, state.CurrentRoundId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The match round factory produced round '" + initialRound.RoundId +
                    "' but the coordinator expected '" + state.CurrentRoundId + "'.");
            }

            roundOptions.InitialRound = initialRound;
            var channel = (LocalTableChannel)await LocalTableHost.CreateChannelAsync(roundOptions, cancellationToken);
            string ticket = LocalTableHost.IssueTicket(roundOptions);
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_channel != null)
                {
                    throw new InvalidOperationException("A match round is already open.");
                }

                _channel = channel;
                _ticket = ticket;
            }

            return new LocalMatchRound(channel, ticket, state.CurrentRoomId, state.CurrentRoundId, state.Ordinal);
        }

        public Task<LocalMatchSnapshot> CompleteRoundAsync(CancellationToken cancellationToken = default)
        {
            LocalTableChannel channel;
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_channel == null)
                {
                    throw new InvalidOperationException("No match round is open.");
                }

                channel = _channel;
            }

            LocalTableRuntime runtime = channel.RuntimeForMatch
                ?? throw new InvalidOperationException("The round channel is not open.");
            RoundResult result = runtime.CurrentRoundResult
                ?? throw new InvalidOperationException("The round has not reached its authoritative terminal result.");

            LocalMatchState next;
            lock (_gate)
            {
                if (!string.Equals(_state.CurrentRoundId, result.RoundId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The terminal round does not belong to the current match round.");
                }

                next = _state.ApplyResult(result, _options.MatchId, _options.Rules, _options.DeclareAgariYameAtFinalRound);
                LocalMatchStateCodec.Save(_statePath, next, _state.Version);
                _state = next;
            }

            _ticket = null;
            _channel = null;
            channel.CloseAsync(new TableChannelCloseReason("MATCH_ROUND_DONE", true), cancellationToken)
                .GetAwaiter().GetResult();
            return Task.FromResult(next.ToSnapshot());
        }

        public async ValueTask DisposeAsync()
        {
            LocalTableChannel? channel;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                channel = _channel;
                _channel = null;
                _ticket = null;
            }

            if (channel != null)
            {
                try
                {
                    await channel.CloseAsync(
                        new TableChannelCloseReason("MATCH_DISPOSED", true),
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            }
            try
            {
                _lockStream?.Dispose();
            }
            catch
            {
            }
            finally
            {
                _lockStream = null;
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new LocalTableException(LocalTableErrorKind.ChannelClosed, "The match coordinator is disposed.");
            }
        }
    }

    internal sealed class LocalMatchState
    {
        public string MatchId { get; private set; } = string.Empty;
        public int Version { get; private set; }
        public int Ordinal { get; private set; }
        public string CurrentRoomId { get; private set; } = string.Empty;
        public string CurrentRoundId { get; private set; } = string.Empty;
        public int DealerSeat { get; private set; }
        public int RoundWind { get; private set; }
        public int RoundNumber { get; private set; }
        public int Honba { get; private set; }
        public int RiichiSticks { get; private set; }
        public long[] Scores { get; private set; } = Array.Empty<long>();
        public long[] FinalScores { get; private set; } = Array.Empty<long>();
        public bool Finished { get; private set; }
        public string FinishReason { get; private set; } = string.Empty;
        public List<RankingEntry> Rankings { get; private set; } = new List<RankingEntry>();
        public List<LocalMatchRoundRecord> Records { get; private set; } = new List<LocalMatchRoundRecord>();

        public static LocalMatchState Create(string matchId)
        {
            var state = new LocalMatchState
            {
                MatchId = matchId,
                Version = 1,
                Ordinal = 1,
                DealerSeat = 0,
                RoundWind = 1,
                RoundNumber = 0,
                Honba = 0,
                RiichiSticks = 0,
                Scores = new long[] { 25_000L, 25_000L, 25_000L, 25_000L }
            };
            state.CurrentRoomId = matchId + "-r1";
            state.CurrentRoundId = matchId + "-r1";
            return state;
        }

        public LocalMatchState ApplyResult(
            RoundResult result,
            string matchId,
            MatchRuleConfig? rules,
            bool declareAgariYameAtFinalRound)
        {
            if (Finished)
            {
                throw new InvalidOperationException("The match is already finished.");
            }

            var records = new List<LocalMatchRoundRecord>(Records)
            {
                new LocalMatchRoundRecord(
                    result.RoundId,
                    result.ResultId,
                    result.Outcome.Kind,
                    result.Outcome.WinnerSeat,
                    result.Outcome.LoserSeat,
                    result.FinalScores,
                    result.RemainingRiichiSticks)
            };

            MatchRuleConfig effectiveRules = rules ?? new MatchRuleConfig();
            var progression = new MatchProgression(effectiveRules);
            bool agariYame = declareAgariYameAtFinalRound &&
                            RoundWind == effectiveRules.Winds[effectiveRules.Winds.Count - 1] &&
                            RoundNumber == effectiveRules.HandsPerWind - 1;
            ProgressionResult next = progression.NextRound(new ProgressionInput(
                DealerSeat,
                RoundWind,
                RoundNumber,
                Honba,
                result.FinalScores,
                result.Outcome,
                result.RemainingRiichiSticks,
                agariYame));

            var state = new LocalMatchState
            {
                MatchId = matchId,
                Version = checked(Version + 1),
                Ordinal = Ordinal,
                CurrentRoomId = CurrentRoomId,
                CurrentRoundId = CurrentRoundId,
                DealerSeat = DealerSeat,
                RoundWind = RoundWind,
                RoundNumber = RoundNumber,
                Honba = Honba,
                RiichiSticks = result.RemainingRiichiSticks,
                Scores = Copy(result.FinalScores),
                FinalScores = Array.Empty<long>(),
                Records = records
            };

            if (next is ProgressionResult.NextRound nextRound)
            {
                state.Ordinal = checked(Ordinal + 1);
                state.DealerSeat = nextRound.DealerSeat;
                state.RoundWind = nextRound.RoundWind;
                state.RoundNumber = nextRound.RoundNumber;
                state.Honba = nextRound.HonbaSticks;
                state.CurrentRoomId = matchId + "-r" + state.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
                state.CurrentRoundId = state.CurrentRoomId;
                return state;
            }

            var finished = (ProgressionResult.MatchFinished)next;
            state.Finished = true;
            state.FinalScores = Copy(finished.FinalScores);
            state.Rankings = new List<RankingEntry>(finished.Rankings);
            state.FinishReason = finished.Reason.ToString();
            state.CurrentRoomId = string.Empty;
            state.CurrentRoundId = string.Empty;
            return state;
        }

        public LocalMatchSnapshot ToSnapshot()
        {
            return new LocalMatchSnapshot(
                MatchId,
                Ordinal,
                CurrentRoundId,
                DealerSeat,
                RoundWind,
                RoundNumber,
                Honba,
                Copy(Scores),
                Copy(FinalScores),
                RiichiSticks,
                Finished,
                FinishReason,
                new List<RankingEntry>(Rankings),
                new List<LocalMatchRoundRecord>(Records));
        }

        internal void Restore(
            string matchId,
            int version,
            int ordinal,
            string roomId,
            string roundId,
            int dealer,
            int wind,
            int number,
            int honba,
            int sticks,
            long[] scores,
            long[] finalScores,
            bool finished,
            string finishReason,
            List<RankingEntry> rankings,
            List<LocalMatchRoundRecord> records)
        {
            MatchId = matchId;
            Version = version;
            Ordinal = ordinal;
            CurrentRoomId = roomId;
            CurrentRoundId = roundId;
            DealerSeat = dealer;
            RoundWind = wind;
            RoundNumber = number;
            Honba = honba;
            RiichiSticks = sticks;
            Scores = scores;
            FinalScores = finalScores;
            Finished = finished;
            FinishReason = finishReason;
            Rankings = rankings;
            Records = records;
        }

        private static long[] Copy(IReadOnlyList<long> values)
        {
            var copy = new long[values.Count];
            for (int i = 0; i < values.Count; i++)
            {
                copy[i] = values[i];
            }

            return copy;
        }
    }

    internal static class LocalMatchStateCodec
    {
        private const int Magic = 0x4D415443; // MATC
        private const int EnvelopeVersion = 1;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        public static LocalMatchState? Load(string path)
        {
            bool primaryExists = File.Exists(path);
            string? primaryError = null;
            LocalMatchState? primary = null;
            if (primaryExists)
            {
                try
                {
                    primary = Decode(File.ReadAllBytes(path));
                }
                catch (Exception exception) when (
                    exception is InvalidDataException || exception is IOException ||
                    exception is UnauthorizedAccessException || exception is ArgumentException)
                {
                    primaryError = exception.Message;
                }
            }

            if (primary != null)
            {
                return primary;
            }

            string previous = path + ".prev";
            LocalMatchState? backup = null;
            if (File.Exists(previous))
            {
                try
                {
                    backup = Decode(File.ReadAllBytes(previous));
                }
                catch (Exception exception) when (
                    exception is InvalidDataException || exception is IOException ||
                    exception is UnauthorizedAccessException || exception is ArgumentException)
                {
                    primaryError = (primaryError ?? string.Empty) + " / prev=" + exception.Message;
                }
            }

            if (backup != null)
            {
                // A valid previous copy restores the last confirmed match state. Publish it
                // atomically through a temp file rather than using it in place.
                PublishRecovered(path, backup);
                return backup;
            }

            if (!primaryExists && !File.Exists(previous))
            {
                return null;
            }

            throw new LocalTableException(
                LocalTableErrorKind.CorruptState,
                "The match state and its previous copy are both unreadable: " + primaryError);
        }

        public static void Save(string path, LocalMatchState state, int expectedVersion)
        {
            LocalMatchState? current = File.Exists(path) ? Load(path) : null;
            if (current != null && current.Version != expectedVersion)
            {
                throw new LocalTableException(
                    LocalTableErrorKind.LeaseConflict,
                    "The match state advanced concurrently; refusing to overwrite it.");
            }

            SaveInternal(path, state, expectedVersion);
        }

        private static void PublishRecovered(string path, LocalMatchState state)
        {
            byte[] payload = Encode(state);
            byte[] envelope;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Utf8, true))
            {
                writer.Write(Magic);
                writer.Write(EnvelopeVersion);
                writer.Write(payload.Length);
                writer.Write(TablePayloadSha256.Compute(payload).ToArray());
                writer.Write(payload);
                writer.Flush();
                envelope = stream.ToArray();
            }

            string tmp = path + ".tmp." + Guid.NewGuid().ToString("N");
            string quarantine = path + ".corrupt." + Guid.NewGuid().ToString("N");
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(envelope, 0, envelope.Length);
                stream.Flush(true);
            }

            bool moved = false;
            try
            {
                if (File.Exists(path))
                {
                    File.Move(path, quarantine);
                    moved = true;
                }

                File.Move(tmp, path);
                if (moved)
                {
                    File.Delete(quarantine);
                }
            }
            catch
            {
                if (!File.Exists(path) && moved && File.Exists(quarantine))
                {
                    try
                    {
                        File.Move(quarantine, path);
                    }
                    catch
                    {
                    }
                }

                try
                {
                    if (File.Exists(tmp))
                    {
                        File.Delete(tmp);
                    }
                }
                catch
                {
                }

                throw new LocalTableException(
                    LocalTableErrorKind.Unavailable,
                    "The verified previous match state could not be published.");
            }
        }

        private static void SaveInternal(string path, LocalMatchState state, int? expectedVersion)
        {
            byte[] payload = Encode(state);
            byte[] envelope;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Utf8, true))
            {
                writer.Write(Magic);
                writer.Write(EnvelopeVersion);
                writer.Write(payload.Length);
                writer.Write(TablePayloadSha256.Compute(payload).ToArray());
                writer.Write(payload);
                writer.Flush();
                envelope = stream.ToArray();
            }

            string tmp = path + ".tmp." + Guid.NewGuid().ToString("N");
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(envelope, 0, envelope.Length);
                stream.Flush(true);
            }

            if (File.Exists(path))
            {
                File.Replace(tmp, path, path + ".prev", true);
            }
            else
            {
                File.Move(tmp, path);
                File.Copy(path, path + ".prev", true);
            }
        }

        private static byte[] Encode(LocalMatchState state)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Utf8, true))
            {
                writer.Write(state.MatchId);
                writer.Write(state.Version);
                writer.Write(state.Ordinal);
                writer.Write(state.CurrentRoomId);
                writer.Write(state.CurrentRoundId);
                writer.Write(state.DealerSeat);
                writer.Write(state.RoundWind);
                writer.Write(state.RoundNumber);
                writer.Write(state.Honba);
                writer.Write(state.RiichiSticks);
                WriteScores(writer, state.Scores);
                WriteScores(writer, state.FinalScores);
                writer.Write(state.Finished);
                writer.Write(state.FinishReason ?? string.Empty);
                writer.Write(state.Rankings.Count);
                for (int i = 0; i < state.Rankings.Count; i++)
                {
                    writer.Write(state.Rankings[i].Rank);
                    writer.Write(state.Rankings[i].Seat);
                    writer.Write(state.Rankings[i].Score);
                }

                writer.Write(state.Records.Count);
                for (int i = 0; i < state.Records.Count; i++)
                {
                    LocalMatchRoundRecord record = state.Records[i];
                    writer.Write(record.RoundId);
                    writer.Write(record.ResultId);
                    writer.Write((byte)record.OutcomeKind);
                    writer.Write(record.WinnerSeat);
                    writer.Write(record.LoserSeat);
                    WriteScores(writer, record.FinalScores);
                    writer.Write(record.RemainingRiichiSticks);
                }

                writer.Flush();
                return stream.ToArray();
            }
        }

        private static LocalMatchState Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 48)
            {
                throw new InvalidDataException("The match state is truncated.");
            }

            using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream, Utf8, true))
            {
                if (reader.ReadInt32() != Magic || reader.ReadInt32() != EnvelopeVersion)
                {
                    throw new InvalidDataException("The match state envelope is invalid.");
                }

                int length = reader.ReadInt32();
                if (length <= 0 || length > bytes.Length - 44)
                {
                    throw new InvalidDataException("The match state payload length is invalid.");
                }

                byte[] expected = reader.ReadBytes(32);
                byte[] payload = reader.ReadBytes(length);
                if (payload.Length != length ||
                    TablePayloadSha256.Compute(payload).ToArray().AsSpan().SequenceEqual(expected) == false)
                {
                    throw new InvalidDataException("The match state checksum does not match.");
                }

                using (var body = new MemoryStream(payload, false))
                using (var bodyReader = new BinaryReader(body, Utf8, true))
                {
                    string matchId = bodyReader.ReadString();
                    int version = bodyReader.ReadInt32();
                    int ordinal = bodyReader.ReadInt32();
                    string roomId = bodyReader.ReadString();
                    string roundId = bodyReader.ReadString();
                    int dealer = bodyReader.ReadInt32();
                    int wind = bodyReader.ReadInt32();
                    int number = bodyReader.ReadInt32();
                    int honba = bodyReader.ReadInt32();
                    int sticks = bodyReader.ReadInt32();
                    long[] scores = ReadScores(bodyReader);
                    long[] finalScores = ReadScores(bodyReader);
                    bool finished = bodyReader.ReadBoolean();
                    string reason = bodyReader.ReadString();
                    int rankCount = bodyReader.ReadInt32();
                    var rankings = new List<RankingEntry>(rankCount);
                    for (int i = 0; i < rankCount; i++)
                    {
                        rankings.Add(new RankingEntry(
                            bodyReader.ReadInt32(),
                            bodyReader.ReadInt32(),
                            bodyReader.ReadInt64()));
                    }

                    int recordCount = bodyReader.ReadInt32();
                    var records = new List<LocalMatchRoundRecord>(recordCount);
                    for (int i = 0; i < recordCount; i++)
                    {
                        string recordRound = bodyReader.ReadString();
                        string resultId = bodyReader.ReadString();
                        var kind = (RoundOutcomeKind)bodyReader.ReadByte();
                        int winner = bodyReader.ReadInt32();
                        int loser = bodyReader.ReadInt32();
                        long[] recordScores = ReadScores(bodyReader);
                        int recordSticks = bodyReader.ReadInt32();
                        records.Add(new LocalMatchRoundRecord(
                            recordRound,
                            resultId,
                            kind,
                            winner,
                            loser,
                            recordScores,
                            recordSticks));
                    }

                    if (scores.Length != 4 || finalScores.Length != 0 && finalScores.Length != 4)
                    {
                        throw new InvalidDataException("The match state score cardinality is invalid.");
                    }

                    long sum = 0;
                    for (int i = 0; i < scores.Length; i++)
                    {
                        sum += scores[i];
                    }

                    long tableTotal = checked(sum + ((long)sticks * 1000L));
                    if (tableTotal != 100_000L)
                    {
                        throw new InvalidDataException("The match state scores + riichi sticks violate zero-sum conservation.");
                    }

                    var state = new LocalMatchState();
                    state.Restore(
                        matchId,
                        version,
                        ordinal,
                        roomId,
                        roundId,
                        dealer,
                        wind,
                        number,
                        honba,
                        sticks,
                        scores,
                        finalScores,
                        finished,
                        reason,
                        rankings,
                        records);
                    return state;
                }
            }
        }

        private static void WriteScores(BinaryWriter writer, IReadOnlyList<long> scores)
        {
            writer.Write(scores.Count);
            for (int i = 0; i < scores.Count; i++)
            {
                writer.Write(scores[i]);
            }
        }

        private static long[] ReadScores(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > 4)
            {
                throw new InvalidDataException("The match score count is invalid.");
            }

            var values = new long[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = reader.ReadInt64();
            }

            return values;
        }
    }
}
