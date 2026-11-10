using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mahjong.LocalHost;

namespace CheatAiBatchRunner;

internal static class Program
{
    private const string DefaultProfile = LocalAiProfiles.RandomCheatBasic;

    private static int Main(string[] args)
    {
        return MainAsync(args).GetAwaiter().GetResult();
    }

    private static async Task<int> MainAsync(string[] args)
    {
        var options = RunnerOptions.Parse(args);
        if (options == null)
        {
            Console.Error.WriteLine(
                "usage: CheatAiBatchRunner --rounds N [--seed N] [--output DIR] " +
                "[--round-timeout-seconds N] [--overall-timeout-minutes N]");
            return 2;
        }

        Directory.CreateDirectory(options.OutputDirectory);
        string decisionsPath = Path.Combine(options.OutputDirectory, "decisions.jsonl");
        string roundsDirectory = Path.Combine(options.OutputDirectory, "rounds");
        Directory.CreateDirectory(roundsDirectory);
        string runPath = Path.Combine(options.OutputDirectory, "run.json");

        var stopwatch = Stopwatch.StartNew();
        var results = new List<RoundRecord>();
        int completed = 0;
        int failed = 0;
        string firstFailure = string.Empty;
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMinutes(options.OverallTimeoutMinutes));
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        using var heartbeat = new Timer(
            _ =>
            {
                Console.WriteLine(
                    "heartbeat elapsed=" + stopwatch.Elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture) +
                    " completed=" + Volatile.Read(ref completed) +
                    " failed=" + Volatile.Read(ref failed) +
                    " currentRound=" + Math.Min(options.Rounds, Volatile.Read(ref completed) + Volatile.Read(ref failed) + 1));
                Console.Out.Flush();
            },
            null,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30));

        await using (var decisions = new StreamWriter(
            new FileStream(decisionsPath, FileMode.Create, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(false)))
        {
            for (int index = 1; index <= options.Rounds; index++)
            {
                if (cancellation.IsCancellationRequested)
                {
                    firstFailure = string.IsNullOrEmpty(firstFailure)
                        ? "CANCELLED_BEFORE_ROUND_" + index.ToString(CultureInfo.InvariantCulture)
                        : firstFailure;
                    break;
                }

                ulong roundSeed = options.Seed + (ulong)index * 0x9E3779B97F4A7C15UL;
                var record = new RoundRecord
                {
                    RoundIndex = index,
                    Seed = roundSeed,
                    StartedUtc = DateTimeOffset.UtcNow,
                };
                var recorder = new DecisionRecorder();
                string roomId = "cheatai-" + index.ToString("D3", CultureInfo.InvariantCulture);
                string roundDirectory = Path.Combine(
                    roundsDirectory,
                    "round-" + index.ToString("D3", CultureInfo.InvariantCulture));

                Console.WriteLine(
                    "round_start index=" + index +
                    " seed=0x" + roundSeed.ToString("X16", CultureInfo.InvariantCulture) +
                    " room=" + roomId +
                    " dir=" + roundDirectory);
                Console.Out.Flush();

                try
                {
                    Directory.CreateDirectory(roundDirectory);
                    var environment = new DeterministicEnvironment(roundSeed);
                    var hostOptions = new LocalTableHostOptions
                    {
                        SaveDirectory = roundDirectory,
                        RoomId = roomId,
                        RulesetVersion = 1,
                        OwnerInstanceId = "cheat-ai-runner",
                        LeaseDuration = TimeSpan.FromMinutes(10),
                        AutoFlushInterval = TimeSpan.FromMilliseconds(250),
                        Environment = environment.Create(),
                        SeatPlan = LocalTableSeatPlan.FromHumanSeats(Array.Empty<int>()),
                        Ai = new LocalAiOptions
                        {
                            ProfileId = DefaultProfile,
                            Seed = roundSeed,
                            AllowCheats = true,
                            MaxSpendPerRound = 60,
                            ReservePoints = 0,
                        },
                        AiDecisionObserver = recorder,
                    };

                    await using (LocalTableRoom room = await LocalTableHost.OpenRoomAsync(
                        hostOptions,
                        cancellation.Token).ConfigureAwait(false))
                    {
                        using var roundTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                            cancellation.Token);
                        roundTimeout.CancelAfter(TimeSpan.FromSeconds(options.RoundTimeoutSeconds));
                        LocalRoundSummary summary = await WaitForTerminalAsync(
                            roundDirectory,
                            roundTimeout.Token).ConfigureAwait(false);

                        record.RoomId = roomId;
                        record.RoundId = summary.RoundId;
                        record.OutcomeCode = summary.OutcomeCode;
                        record.WinnerSeat = summary.WinnerSeat;
                        record.LoserSeat = summary.LoserSeat;
                        record.FinalScores = summary.FinalScores.ToArray();
                        record.RemainingRiichiSticks = summary.RemainingRiichiSticks;
                        record.WallAssignedCount = summary.WallAssignedCount;
                        record.WallConserved = summary.WallConserved;
                        record.ConservationErrors = summary.ConservationErrors.ToArray();
                        record.CheatBalances = summary.CheatBalances.ToArray();
                        record.CheatUsage = summary.CheatUsage
                            .Select(usage => new CheatUsageRecord
                            {
                                Reason = usage.Reason,
                                Count = usage.Count,
                                SpentPoints = usage.SpentPoints,
                            })
                            .ToArray();
                        record.Terminal = summary.Finished;
                        record.StateVersion = summary.StateVersion;
                    }
                }
                catch (Exception exception)
                {
                    record.Error = exception.ToString();
                    record.Terminal = false;
                }

                record.DurationMilliseconds = (long)stopwatch.Elapsed.TotalMilliseconds -
                    results.Sum(item => item.DurationMilliseconds);
                record.FinishedUtc = DateTimeOffset.UtcNow;
                record.Decisions = recorder.Snapshot();
                record.DecisionCount = record.Decisions.Length;
                record.OrdinaryDecisionCount = record.Decisions.Count(
                    item => string.Equals(item.DecisionKind, "ordinary", StringComparison.Ordinal));
                record.CheatAcceptedCount = record.Decisions.Count(
                    item => string.Equals(item.DecisionKind, "cheat", StringComparison.Ordinal));
                record.CheatChargeCount = record.CheatUsage.Sum(usage => usage.Count);
                record.CheatRejectedCount = record.Decisions.Count(
                    item => string.Equals(item.DecisionKind, "cheat-fallback", StringComparison.Ordinal));
                record.FallbackCount = record.Decisions.Count(
                    item => item.DecisionKind.EndsWith("-fallback", StringComparison.Ordinal));

                bool sane = record.Terminal &&
                    record.WallConserved &&
                    record.WallAssignedCount == 136 &&
                    record.FinalScores.Length == 4 &&
                    record.FinalScores.Sum() + record.RemainingRiichiSticks * 1000L == 100_000L;
                record.Passed = sane && string.IsNullOrEmpty(record.Error);
                results.Add(record);

                string roundPath = Path.Combine(
                    roundsDirectory,
                    "round-" + index.ToString("D3", CultureInfo.InvariantCulture) + ".json");
                await File.WriteAllTextAsync(
                    roundPath,
                    JsonSerializer.Serialize(record, JsonOptions),
                    new UTF8Encoding(false)).ConfigureAwait(false);

                for (int i = 0; i < record.Decisions.Length; i++)
                {
                    DecisionRecord decision = record.Decisions[i];
                    await decisions.WriteLineAsync(JsonSerializer.Serialize(
                        new
                        {
                            roundIndex = index,
                            seed = "0x" + roundSeed.ToString("X16", CultureInfo.InvariantCulture),
                            decision.RoundId,
                            decision.Seat,
                            decision.Phase,
                            decision.DecisionKind,
                            decision.OptionId,
                            decision.CapabilityId,
                            decision.Status,
                            decision.ReasonCode,
                            decision.Detail,
                            decision.StateVersion,
                        },
                        JsonOptionsCompact)).ConfigureAwait(false);
                }

                await decisions.FlushAsync().ConfigureAwait(false);

                Console.WriteLine(
                    "round_done index=" + index +
                    " passed=" + record.Passed +
                    " terminal=" + record.Terminal +
                    " outcome=" + (string.IsNullOrEmpty(record.OutcomeCode) ? "-" : record.OutcomeCode) +
                    " winner=" + record.WinnerSeat +
                    " scores=[" + string.Join(",", record.FinalScores) + "]" +
                    " durationMs=" + record.DurationMilliseconds +
                    " cheats=" + record.CheatChargeCount +
                    " wall=" + record.WallAssignedCount +
                    " conserved=" + record.WallConserved);
                Console.Out.Flush();

                if (!record.Passed)
                {
                    failed++;
                    firstFailure = string.IsNullOrEmpty(firstFailure)
                        ? "ROUND_" + index.ToString(CultureInfo.InvariantCulture) + ": " +
                          (string.IsNullOrEmpty(record.Error) ? "INVARIANT_FAILED" : record.Error)
                        : firstFailure;
                    break;
                }

                completed++;
            }
        }

        stopwatch.Stop();
        var run = new RunSummary
        {
            StartedUtc = results.Count == 0 ? DateTimeOffset.UtcNow : results[0].StartedUtc,
            FinishedUtc = DateTimeOffset.UtcNow,
            TotalWallClockMilliseconds = (long)stopwatch.Elapsed.TotalMilliseconds,
            RequestedRounds = options.Rounds,
            CompletedRounds = completed,
            FailedRounds = failed,
            NotRunRounds = options.Rounds - completed - failed,
            OverallPassed = completed == options.Rounds && failed == 0,
            FirstFailure = firstFailure,
            MasterSeed = options.Seed,
            ProfileId = DefaultProfile,
            CommandLine = Environment.CommandLine,
            TotalCheatCharges = results.Sum(item => item.CheatChargeCount),
            TotalCheatSpentPoints = results
                .SelectMany(item => item.CheatUsage)
                .Sum(usage => usage.SpentPoints),
            CheatUsageByReason = results
                .SelectMany(item => item.CheatUsage)
                .GroupBy(usage => usage.Reason, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => new CheatUsageRecord
                    {
                        Reason = group.Key,
                        Count = group.Sum(usage => usage.Count),
                        SpentPoints = group.Sum(usage => usage.SpentPoints),
                    },
                    StringComparer.Ordinal),
            OutcomeCounts = results
                .GroupBy(item => item.OutcomeCode, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            AverageRoundMilliseconds = results.Count == 0
                ? 0d
                : results.Average(item => item.DurationMilliseconds),
            Rounds = results.ToArray(),
        };

        await File.WriteAllTextAsync(
            runPath,
            JsonSerializer.Serialize(run, JsonOptions),
            new UTF8Encoding(false)).ConfigureAwait(false);

        string manifestPath = Path.Combine(options.OutputDirectory, "sha256.txt");
        WriteManifest(options.OutputDirectory, manifestPath);

        Console.WriteLine(
            "run_done requested=" + options.Rounds +
            " completed=" + completed +
            " failed=" + failed +
            " notRun=" + run.NotRunRounds +
            " totalMs=" + run.TotalWallClockMilliseconds +
            " passed=" + run.OverallPassed);
        Console.WriteLine("run_json=" + runPath);
        Console.WriteLine("decisions_jsonl=" + decisionsPath);
        Console.WriteLine("rounds_dir=" + roundsDirectory);
        Console.WriteLine("manifest=" + manifestPath);
        if (!run.OverallPassed)
        {
            Console.Error.WriteLine("first_failure=" + (string.IsNullOrEmpty(firstFailure) ? "UNKNOWN" : firstFailure));
        }

        return run.OverallPassed ? 0 : 1;
    }

    private static async Task<LocalRoundSummary> WaitForTerminalAsync(
        string saveDirectory,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LocalRoundSummary summary;
            try
            {
                summary = await LocalTableHost.GetRoundSummaryAsync(
                    saveDirectory,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (LocalTableException exception) when (exception.ErrorKind == LocalTableErrorKind.Unavailable)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (summary.Finished)
            {
                return summary;
            }

            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void WriteManifest(string root, string manifestPath)
    {
        var builder = new StringBuilder();
        string[] files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(path, manifestPath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (string file in files)
        {
            using FileStream stream = File.OpenRead(file);
            byte[] hash = SHA256.HashData(stream);
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            builder.Append(Convert.ToHexString(hash).ToLowerInvariant())
                .Append("  ")
                .Append(relative)
                .Append('\n');
        }

        File.WriteAllText(manifestPath, builder.ToString(), new UTF8Encoding(false));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions JsonOptionsCompact = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class DeterministicEnvironment
    {
        private readonly ulong _seed;
        private ulong _state;
        private int _id;

        public DeterministicEnvironment(ulong seed)
        {
            _seed = seed;
            _state = seed;
        }

        public LocalHostEnvironment Create()
        {
            return new LocalHostEnvironment
            {
                NextInt = NextInt,
                NextDouble = () => NextUInt64() / (double)ulong.MaxValue,
                NewId = () => "ai-id-" + (_seed.ToString("X16", CultureInfo.InvariantCulture)) + "-" +
                               Interlocked.Increment(ref _id).ToString(CultureInfo.InvariantCulture),
            };
        }

        private int NextInt(int bound)
        {
            if (bound <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bound));
            }

            return (int)(NextUInt64() % (ulong)bound);
        }

        private ulong NextUInt64()
        {
            _state += 0x9E3779B97F4A7C15UL;
            ulong z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    private sealed class DecisionRecorder : ILocalAiDecisionObserver
    {
        private readonly object _gate = new();
        private readonly List<DecisionRecord> _records = new();

        public void OnDecision(LocalAiDecisionRecord record)
        {
            lock (_gate)
            {
                _records.Add(new DecisionRecord
                {
                    RoundId = record.RoundId,
                    Seat = record.Seat,
                    Phase = record.Phase,
                    DecisionKind = record.DecisionKind,
                    OptionId = record.OptionId,
                    CapabilityId = record.CapabilityId,
                    Status = record.Status,
                    ReasonCode = record.ReasonCode,
                    Detail = record.Detail,
                    StateVersion = record.StateVersion,
                });
            }
        }

        public DecisionRecord[] Snapshot()
        {
            lock (_gate)
            {
                return _records.ToArray();
            }
        }
    }

    private sealed class RunnerOptions
    {
        public int Rounds { get; private set; } = 100;
        public ulong Seed { get; private set; } = 20261111UL;
        public string OutputDirectory { get; private set; } = string.Empty;
        public int RoundTimeoutSeconds { get; private set; } = 180;
        public int OverallTimeoutMinutes { get; private set; } = 120;

        public static RunnerOptions? Parse(string[] args)
        {
            var options = new RunnerOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string name = args[i];
                if (i + 1 >= args.Length)
                {
                    return null;
                }

                string value = args[++i];
                switch (name)
                {
                    case "--rounds":
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rounds) || rounds < 1)
                        {
                            return null;
                        }

                        options.Rounds = rounds;
                        break;
                    case "--seed":
                        if (!ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong seed))
                        {
                            return null;
                        }

                        options.Seed = seed;
                        break;
                    case "--output":
                        options.OutputDirectory = Path.GetFullPath(value);
                        break;
                    case "--round-timeout-seconds":
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int roundTimeout) || roundTimeout < 1)
                        {
                            return null;
                        }

                        options.RoundTimeoutSeconds = roundTimeout;
                        break;
                    case "--overall-timeout-minutes":
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int overallTimeout) || overallTimeout < 1)
                        {
                            return null;
                        }

                        options.OverallTimeoutMinutes = overallTimeout;
                        break;
                    default:
                        return null;
                }
            }

            if (string.IsNullOrEmpty(options.OutputDirectory))
            {
                options.OutputDirectory = Path.Combine(
                    Directory.GetCurrentDirectory(),
                    "build",
                    "cheat-ai-100",
                    Guid.NewGuid().ToString("N"));
            }

            return options;
        }
    }

    private sealed class RunSummary
    {
        public DateTimeOffset StartedUtc { get; set; }
        public DateTimeOffset FinishedUtc { get; set; }
        public long TotalWallClockMilliseconds { get; set; }
        public int RequestedRounds { get; set; }
        public int CompletedRounds { get; set; }
        public int FailedRounds { get; set; }
        public int NotRunRounds { get; set; }
        public bool OverallPassed { get; set; }
        public string FirstFailure { get; set; } = string.Empty;
        public ulong MasterSeed { get; set; }
        public string ProfileId { get; set; } = string.Empty;
        public string CommandLine { get; set; } = string.Empty;
        public int TotalCheatCharges { get; set; }
        public int TotalCheatSpentPoints { get; set; }
        public Dictionary<string, CheatUsageRecord> CheatUsageByReason { get; set; } = new();
        public Dictionary<string, int> OutcomeCounts { get; set; } = new();
        public double AverageRoundMilliseconds { get; set; }
        public RoundRecord[] Rounds { get; set; } = Array.Empty<RoundRecord>();
    }

    private sealed class RoundRecord
    {
        public int RoundIndex { get; set; }
        public ulong Seed { get; set; }
        public string RoomId { get; set; } = string.Empty;
        public string RoundId { get; set; } = string.Empty;
        public DateTimeOffset StartedUtc { get; set; }
        public DateTimeOffset FinishedUtc { get; set; }
        public long DurationMilliseconds { get; set; }
        public bool Terminal { get; set; }
        public bool Passed { get; set; }
        public string OutcomeCode { get; set; } = string.Empty;
        public int WinnerSeat { get; set; } = -1;
        public int LoserSeat { get; set; } = -1;
        public long[] FinalScores { get; set; } = Array.Empty<long>();
        public int RemainingRiichiSticks { get; set; }
        public int WallAssignedCount { get; set; }
        public bool WallConserved { get; set; }
        public string[] ConservationErrors { get; set; } = Array.Empty<string>();
        public int[] CheatBalances { get; set; } = Array.Empty<int>();
        public CheatUsageRecord[] CheatUsage { get; set; } = Array.Empty<CheatUsageRecord>();
        public long StateVersion { get; set; }
        public string Error { get; set; } = string.Empty;
        public DecisionRecord[] Decisions { get; set; } = Array.Empty<DecisionRecord>();
        public int DecisionCount { get; set; }
        public int OrdinaryDecisionCount { get; set; }
        public int CheatAcceptedCount { get; set; }
        public int CheatChargeCount { get; set; }
        public int CheatRejectedCount { get; set; }
        public int FallbackCount { get; set; }
    }

    private sealed class CheatUsageRecord
    {
        public string Reason { get; set; } = string.Empty;
        public int Count { get; set; }
        public int SpentPoints { get; set; }
    }

    private sealed class DecisionRecord
    {
        public string RoundId { get; set; } = string.Empty;
        public int Seat { get; set; }
        public string Phase { get; set; } = string.Empty;
        public string DecisionKind { get; set; } = string.Empty;
        public string OptionId { get; set; } = string.Empty;
        public string CapabilityId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string ReasonCode { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public long StateVersion { get; set; }
    }
}
