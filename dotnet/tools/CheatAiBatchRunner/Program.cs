using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mahjong.Ai;
using Mahjong.Domain.Cheat;
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
        if (args.Length == 1 && args[0] == "--self-test-paired-statistics")
        {
            return RunPairedStatisticsSelfTest();
        }

        var options = RunnerOptions.Parse(args);
        if (options == null)
        {
            PrintUsage();
            return 2;
        }

        if (options.PlanPairs > 0)
        {
            return await RunPlanOnlyAsync(options).ConfigureAwait(false);
        }

        if (options.RunPairs > 0)
        {
            return await RunPairedBatchAsync(options).ConfigureAwait(false);
        }

        if (options.SeatProfiles.Count > 0)
        {
            string[] unavailable = options.SeatProfiles
                .Where(profile => !LocalAiProfiles.IsKnown(profile))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (unavailable.Length > 0)
            {
                Console.Error.WriteLine("NOT_RUN: unavailable profile(s): " + string.Join(",", unavailable) + ".");
                return 3;
            }

            if (options.SeatProfiles.Count != 4)
            {
                Console.Error.WriteLine(
                    "NOT_RUN: --seat-profiles=[" + string.Join(",", options.SeatProfiles) +
                    "]; four-seat LocalHost execution requires exactly 4 profiles.");
                return 3;
            }
        }

        return await RunSingleModeAsync(options).ConfigureAwait(false);
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine(
            "usage:\n" +
            "  CheatAiBatchRunner --rounds N [--seed N] [--output DIR] [--round-timeout-seconds N] [--overall-timeout-minutes N] [--seat-profiles P0,P1,P2,P3]\n" +
            "  CheatAiBatchRunner --plan-pairs N [--seed N] [--candidate-profile ID] [--control-profile ID] [--output DIR]\n" +
            "  CheatAiBatchRunner --run-pairs N [--seed N] [--candidate-profile ID] [--control-profile ID] [--opponent-profile ID] [--character-ids C0,C1,C2,C3] [--ruleset-version V] [--max-spend-per-round M] [--reserve-points R] [--round-timeout-seconds N | --round-timeout-ms MS] [--overall-timeout-minutes N | --overall-timeout-ms MS] [--preregistration-path FILE] [--preregistration-sha256 HEX] [--output DIR]\n" +
            "  CheatAiBatchRunner --self-test-paired-statistics");
    }

    private static async Task<int> RunPlanOnlyAsync(RunnerOptions options)
    {
        PairedRunPlan.Plan plan = PairedRunPlan.Create(
            options.PlanPairs,
            options.Seed,
            options.CandidateProfile,
            options.ControlProfile);
        PairedStatistics.Summary statistics = PairedStatistics.Calculate(plan);
        (string planPath, string pairedManifestPath) result;
        try
        {
            result = await WritePairedPlanExclusiveAsync(options.OutputDirectory, plan, statistics)
                .ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine("ERROR: paired output was not published: " + exception.Message);
            return 2;
        }
        catch (UnauthorizedAccessException exception)
        {
            Console.Error.WriteLine("ERROR: paired output was not published: " + exception.Message);
            return 2;
        }

        Console.WriteLine("NOT_RUN: paired plan generated; execution requires --run-pairs.");
        Console.WriteLine("requestedPairs=" + statistics.RequestedPairs + " plannedPairs=" + plan.Pairs.Length +
            " simulatedRounds=0 completePairs=" + statistics.CompletePairs +
            " failedPairs=" + statistics.FailedPairs + " timeoutPairs=" + statistics.TimedOutPairs +
            " notRunPairs=" + statistics.NotRunPairs + " partialPairs=" + statistics.PartialPairs + " mean=" +
            (statistics.MeanDifference?.ToString("R", CultureInfo.InvariantCulture) ?? "unavailable") +
            " ci=[" + (statistics.Lower95?.ToString("R", CultureInfo.InvariantCulture) ?? "unavailable") +
            "," + (statistics.Upper95?.ToString("R", CultureInfo.InvariantCulture) ?? "unavailable") + "]");
        Console.WriteLine("paired_plan=" + result.planPath);
        Console.WriteLine("manifest=" + result.pairedManifestPath);
        return 3;
    }

    private static async Task<int> RunPairedBatchAsync(RunnerOptions options)
    {
        string[] profilesToCheck = new[]
        {
            options.CandidateProfile,
            options.ControlProfile,
            options.OpponentProfile,
        };
        string[] unavailable = profilesToCheck
            .Where(profile => !LocalAiProfiles.IsKnown(profile))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (unavailable.Length > 0)
        {
            Console.Error.WriteLine("NOT_RUN: unavailable profile(s): " + string.Join(",", unavailable) + ".");
            return 3;
        }

        string fullOutput = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.OutputDirectory));
        string? parent = Path.GetDirectoryName(fullOutput);
        string leaf = Path.GetFileName(fullOutput);
        if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(leaf))
        {
            Console.Error.WriteLine("ERROR: paired output must name a new child directory: " + fullOutput);
            return 2;
        }

        Directory.CreateDirectory(parent);
        if (Directory.Exists(fullOutput) || File.Exists(fullOutput))
        {
            Console.Error.WriteLine("ERROR: paired output was not published: Output directory is already occupied: " + fullOutput);
            return 2;
        }

        string lockPath = Path.Combine(parent, "." + leaf + ".paired-run.lock");
        FileStream outputLock;
        try
        {
            outputLock = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine("ERROR: paired output lock failed: " + exception.Message);
            return 2;
        }
        catch (UnauthorizedAccessException exception)
        {
            Console.Error.WriteLine("ERROR: paired output lock failed: " + exception.Message);
            return 2;
        }

        string stagingDirectory = fullOutput + ".staging-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        try
        {
            if (Directory.Exists(fullOutput) || File.Exists(fullOutput))
            {
                Console.Error.WriteLine("ERROR: paired output was not published: Output directory became occupied: " + fullOutput);
                return 2;
            }

            Directory.CreateDirectory(stagingDirectory);
            string roundsDirectory = Path.Combine(stagingDirectory, "rounds");
            Directory.CreateDirectory(roundsDirectory);
            string decisionsPath = Path.Combine(stagingDirectory, "decisions.jsonl");
            string pairedRoundsPath = Path.Combine(stagingDirectory, "paired-rounds.jsonl");
            string pairedPlanPath = Path.Combine(stagingDirectory, "paired-plan.json");

            var stopwatch = Stopwatch.StartNew();
            DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
            var pairedPairs = new List<PairedRunPlan.Pair>(options.RunPairs);
            var telemetries = new List<PairedRoundTelemetry>(options.RunPairs * 8);
            int executedRounds = 0;
            int completedRounds = 0;
            int failedRounds = 0;
            int timedOutRounds = 0;
            int currentPairIndex = 0;
            int currentRotationIndex = 0;
            string currentLegName = "-";

            using var cancellation = new CancellationTokenSource(options.EffectiveOverallTimeout);
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
                        " executed=" + Volatile.Read(ref executedRounds) + "/" + (options.RunPairs * 8) +
                        " completed=" + Volatile.Read(ref completedRounds) +
                        " failed=" + Volatile.Read(ref failedRounds) +
                        " timeout=" + Volatile.Read(ref timedOutRounds) +
                        " currentPair=" + Volatile.Read(ref currentPairIndex) + "/" + options.RunPairs +
                        " rotation=" + Volatile.Read(ref currentRotationIndex) +
                        " leg=" + Volatile.Read(ref currentLegName));
                    Console.Out.Flush();
                },
                null,
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(30));

            await using (var decisionsWriter = new StreamWriter(
                new FileStream(decisionsPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false)))
            await using (var pairedRoundsWriter = new StreamWriter(
                new FileStream(pairedRoundsPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false)))
            {
                for (int pairIndex = 1; pairIndex <= options.RunPairs; pairIndex++)
                {
                    Volatile.Write(ref currentPairIndex, pairIndex);
                    ulong pairSeed = PairedRunPlan.DerivePairSeed(options.Seed, pairIndex);
                    string pairId = "pair-" + pairIndex.ToString("D4", CultureInfo.InvariantCulture);
                    var rotations = new List<PairedRunPlan.Rotation>(4);

                    for (int rotationIndex = 0; rotationIndex < 4; rotationIndex++)
                    {
                        Volatile.Write(ref currentRotationIndex, rotationIndex);
                        ulong rotationSeed = PairedRunPlan.DeriveRotationSeed(pairSeed, rotationIndex);

                        Volatile.Write(ref currentLegName, "candidate");
                        (PairedRunPlan.Leg candidateLeg, PairedRoundTelemetry? candidateTelemetry) =
                            await ExecutePairedLegAsync(
                                pairId,
                                pairIndex,
                                rotationIndex,
                                "candidate",
                                options.CandidateProfile,
                                rotationSeed,
                                options,
                                roundsDirectory,
                                decisionsWriter,
                                pairedRoundsWriter,
                                cancellation.Token).ConfigureAwait(false);
                        UpdateCounters(candidateLeg.Status, candidateTelemetry != null, ref executedRounds,
                            ref completedRounds, ref failedRounds, ref timedOutRounds);
                        if (candidateTelemetry != null) telemetries.Add(candidateTelemetry);

                        Volatile.Write(ref currentLegName, "control");
                        (PairedRunPlan.Leg controlLeg, PairedRoundTelemetry? controlTelemetry) =
                            await ExecutePairedLegAsync(
                                pairId,
                                pairIndex,
                                rotationIndex,
                                "control",
                                options.ControlProfile,
                                rotationSeed,
                                options,
                                roundsDirectory,
                                decisionsWriter,
                                pairedRoundsWriter,
                                cancellation.Token).ConfigureAwait(false);
                        UpdateCounters(controlLeg.Status, controlTelemetry != null, ref executedRounds,
                            ref completedRounds, ref failedRounds, ref timedOutRounds);
                        if (controlTelemetry != null) telemetries.Add(controlTelemetry);

                        rotations.Add(new PairedRunPlan.Rotation(rotationIndex, rotationSeed, candidateLeg, controlLeg));
                    }

                    pairedPairs.Add(new PairedRunPlan.Pair(pairId, pairIndex, pairSeed, rotations.ToArray()));
                }

                await decisionsWriter.FlushAsync().ConfigureAwait(false);
                await pairedRoundsWriter.FlushAsync().ConfigureAwait(false);
            }

            stopwatch.Stop();
            var plan = new PairedRunPlan.Plan(
                "plan",
                options.Seed,
                options.RunPairs,
                options.CandidateProfile,
                options.ControlProfile,
                pairedPairs.ToArray(),
                "Executed real LocalTableHost four-seat paired rotation batch. Same shuffle stream per rotation; independent shuffle and per-seat strategy streams.");
            PairedStatistics.Summary statistics = PairedStatistics.Calculate(plan);
            BehaviorAggregate behaviorAggregate = ComputeBehaviorAggregate(telemetries, options);
            ReconciliationAggregate reconciliationSummary = ComputeReconciliationAggregate(telemetries);

            var executionConfig = new
            {
                mode = "run-pairs",
                masterSeed = "0x" + options.Seed.ToString("X16", CultureInfo.InvariantCulture),
                masterSeedDecimal = options.Seed,
                requestedPairs = options.RunPairs,
                requestedRounds = options.RunPairs * 8,
                candidateProfileId = options.CandidateProfile,
                controlProfileId = options.ControlProfile,
                opponentProfileId = options.OpponentProfile,
                characterIds = options.CharacterIds,
                rulesetVersion = options.RulesetVersion,
                maxSpendPerRound = options.MaxSpendPerRound,
                reservePoints = options.ReservePoints,
                roundTimeoutMs = (long)options.EffectiveRoundTimeout.TotalMilliseconds,
                overallTimeoutMs = (long)options.EffectiveOverallTimeout.TotalMilliseconds,
                preregistrationPath = string.IsNullOrEmpty(options.PreregistrationPath) ? null : options.PreregistrationPath,
                preregistrationSha256 = string.IsNullOrEmpty(options.PreregistrationSha256) ? null : options.PreregistrationSha256,
                startedUtc,
                finishedUtc = DateTimeOffset.UtcNow,
                totalWallClockMilliseconds = (long)stopwatch.Elapsed.TotalMilliseconds,
                commandLine = Environment.CommandLine,
            };

            await using (var stream = new FileStream(pairedPlanPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new { executionConfig, plan, statistics, behaviorAggregate, reconciliationSummary },
                    JsonOptions).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            string stagedManifestPath = Path.Combine(stagingDirectory, "sha256.txt");
            string manifest = BuildManifest(stagingDirectory, stagedManifestPath);
            await using (var stream = new FileStream(stagedManifestPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 16 * 1024, useAsync: true))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                await writer.WriteAsync(manifest).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            Directory.Move(stagingDirectory, fullOutput);

            bool allCompleteAndReconciled =
                statistics.CompletePairs == options.RunPairs &&
                statistics.FailedPairs == 0 &&
                statistics.TimedOutPairs == 0 &&
                statistics.NotRunPairs == 0 &&
                statistics.PartialPairs == 0 &&
                reconciliationSummary.AllPassed;

            Console.WriteLine(
                "paired_run_done requestedPairs=" + statistics.RequestedPairs +
                " executedRounds=" + executedRounds +
                " completePairs=" + statistics.CompletePairs +
                " failedPairs=" + statistics.FailedPairs +
                " timeoutPairs=" + statistics.TimedOutPairs +
                " notRunPairs=" + statistics.NotRunPairs +
                " partialPairs=" + statistics.PartialPairs +
                " completeRounds=" + statistics.CompleteRounds +
                " failedRounds=" + statistics.FailedRounds +
                " timeoutRounds=" + statistics.TimedOutRounds +
                " notRunRounds=" + statistics.NotRunRounds +
                " reconciliationPassed=" + reconciliationSummary.AllPassed +
                " mean=" + (statistics.MeanDifference?.ToString("R", CultureInfo.InvariantCulture) ?? "unavailable") +
                " ci=[" + (statistics.Lower95?.ToString("R", CultureInfo.InvariantCulture) ?? "unavailable") +
                "," + (statistics.Upper95?.ToString("R", CultureInfo.InvariantCulture) ?? "unavailable") + "]" +
                " totalMs=" + stopwatch.ElapsedMilliseconds);
            Console.WriteLine("paired_plan=" + Path.Combine(fullOutput, "paired-plan.json"));
            Console.WriteLine("paired_rounds_jsonl=" + Path.Combine(fullOutput, "paired-rounds.jsonl"));
            Console.WriteLine("decisions_jsonl=" + Path.Combine(fullOutput, "decisions.jsonl"));
            Console.WriteLine("manifest=" + Path.Combine(fullOutput, "sha256.txt"));
            if (statistics.FirstFailure != null)
            {
                Console.Error.WriteLine(
                    "first_failure pair=" + statistics.FirstFailure.PairId +
                    " rotation=" + statistics.FirstFailure.RotationIndex +
                    " leg=" + statistics.FirstFailure.Leg +
                    " roundId=" + statistics.FirstFailure.RoundId +
                    " detail=" + statistics.FirstFailure.Detail);
            }

            return allCompleteAndReconciled ? 0 : 1;
        }
        finally
        {
            outputLock.Dispose();
            try { File.Delete(lockPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void UpdateCounters(
        string status,
        bool wasExecuted,
        ref int executedRounds,
        ref int completedRounds,
        ref int failedRounds,
        ref int timedOutRounds)
    {
        if (wasExecuted) Interlocked.Increment(ref executedRounds);
        if (status == "complete") Interlocked.Increment(ref completedRounds);
        else if (status == "failed") Interlocked.Increment(ref failedRounds);
        else if (status == "timeout") Interlocked.Increment(ref timedOutRounds);
    }

    private static async Task<(PairedRunPlan.Leg Leg, PairedRoundTelemetry? Telemetry)> ExecutePairedLegAsync(
        string pairId,
        int pairIndex,
        int rotationIndex,
        string legName,
        string targetProfileId,
        ulong rotationSeed,
        RunnerOptions options,
        string roundsDirectory,
        StreamWriter decisionsWriter,
        StreamWriter pairedRoundsWriter,
        CancellationToken overallToken)
    {
        string expectedRoundId = pairId + "-r" + rotationIndex.ToString(CultureInfo.InvariantCulture) + "-" + legName + "-r1";
        if (overallToken.IsCancellationRequested)
        {
            const string reason = "NOT_RUN: overall timeout or cancellation before leg execution.";
            return (new PairedRunPlan.Leg(targetProfileId, rotationIndex, rotationSeed, "not-run",
                reason, string.Empty, Array.Empty<PairedRunPlan.RoundEvidence>()), null);
        }

        ulong shuffleSeed = PairedRunPlan.DeriveShuffleSeed(rotationSeed);
        ulong strategyBaseSeed = PairedRunPlan.DeriveStrategyBaseSeed(rotationSeed);
        var seatSeeds = new ulong[4];
        var seatProfiles = new CharacterAiProfile[4];
        for (int seat = 0; seat < 4; seat++)
        {
            seatSeeds[seat] = PairedRunPlan.DeriveSeatStrategySeed(rotationSeed, seat);
            string seatProfileId = seat == rotationIndex ? targetProfileId : options.OpponentProfile;
            seatProfiles[seat] = BuildSeatProfile(seatProfileId, seatSeeds[seat], options.MaxSpendPerRound, options.ReservePoints);
        }

        string roomId = pairId + "-r" + rotationIndex.ToString(CultureInfo.InvariantCulture) + "-" + legName;
        string saveDirectory = Path.Combine(roundsDirectory, pairId, "r" + rotationIndex.ToString(CultureInfo.InvariantCulture) + "-" + legName);
        Directory.CreateDirectory(saveDirectory);

        var recorder = new DecisionRecorder();
        var environment = new DeterministicEnvironment(shuffleSeed);
        var hostOptions = new LocalTableHostOptions
        {
            SaveDirectory = saveDirectory,
            RoomId = roomId,
            RulesetVersion = options.RulesetVersion,
            OwnerInstanceId = "cheat-ai-paired-runner",
            LeaseDuration = TimeSpan.FromMinutes(10),
            AutoFlushInterval = TimeSpan.FromMilliseconds(250),
            Environment = environment.Create(),
            SeatPlan = LocalTableSeatPlan.FromHumanSeats(Array.Empty<int>()),
            CharacterIds = options.CharacterIds,
            Ai = new LocalAiOptions
            {
                Seed = strategyBaseSeed,
                SeatProfiles = seatProfiles,
            },
            AiDecisionObserver = recorder,
        };

        var roundWatch = Stopwatch.StartNew();
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        LocalRoundSummary? summary = null;
        string status = "failed";
        string error = string.Empty;

        try
        {
            await using (LocalTableRoom room = await LocalTableHost.OpenRoomAsync(hostOptions, overallToken).ConfigureAwait(false))
            {
                using var roundTimeout = CancellationTokenSource.CreateLinkedTokenSource(overallToken);
                roundTimeout.CancelAfter(options.EffectiveRoundTimeout);
                summary = await WaitForTerminalAsync(saveDirectory, roundTimeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!overallToken.IsCancellationRequested)
        {
            status = "timeout";
            error = "ROUND_TIMEOUT after " + (long)options.EffectiveRoundTimeout.TotalMilliseconds + "ms";
        }
        catch (OperationCanceledException)
        {
            status = "timeout";
            error = "OVERALL_CANCELLED_DURING_ROUND";
        }
        catch (Exception exception)
        {
            status = "failed";
            error = exception.GetType().Name + ": " + exception.Message;
        }

        roundWatch.Stop();
        DateTimeOffset finishedUtc = DateTimeOffset.UtcNow;
        DecisionRecord[] decisions = recorder.Snapshot();

        string roundId = summary != null && !string.IsNullOrWhiteSpace(summary.RoundId) ? summary.RoundId : expectedRoundId;
        bool? terminal = summary?.Finished;
        long[]? finalScores = summary?.FinalScores.ToArray();
        int? remainingRiichi = summary?.RemainingRiichiSticks;
        int? wallAssigned = summary?.WallAssignedCount;
        bool? wallConserved = summary?.WallConserved;
        string[]? conservationErrors = summary?.ConservationErrors.ToArray();
        int[] cheatBalances = summary?.CheatBalances.ToArray() ?? Array.Empty<int>();
        SeatCheatUsageRecord[] seatCheatUsage = summary?.SeatCheatUsage
            .Select(u => new SeatCheatUsageRecord { Seat = u.Seat, Reason = u.Reason, Count = u.Count, SpentPoints = u.SpentPoints })
            .ToArray() ?? Array.Empty<SeatCheatUsageRecord>();

        RoundReconciliation reconciliation = ReconcileRound(summary, decisions);
        bool passed = false;

        if (summary != null && string.IsNullOrEmpty(error))
        {
            bool conservationOk = summary.Finished &&
                summary.WallConserved &&
                summary.WallAssignedCount == 136 &&
                summary.ConservationErrors.Count == 0 &&
                summary.FinalScores.Count == 4 &&
                summary.RemainingRiichiSticks >= 0 &&
                summary.FinalScores.Sum(score => (decimal)score) + (decimal)summary.RemainingRiichiSticks * 1000m == 100_000m;
            if (!conservationOk)
            {
                status = "failed";
                error = "INVARIANT_FAILED: terminal=" + summary.Finished + " wall=" + summary.WallAssignedCount +
                    " conserved=" + summary.WallConserved + " errors=[" + string.Join("; ", summary.ConservationErrors) + "]";
            }
            else if (!reconciliation.Passed)
            {
                status = "failed";
                error = "RECONCILIATION_FAILED: " + reconciliation.Error;
            }
            else
            {
                status = "complete";
                passed = true;
            }
        }
        else if (string.IsNullOrEmpty(error))
        {
            status = "failed";
            error = "ROUND_SUMMARY_UNAVAILABLE";
        }

        for (int i = 0; i < decisions.Length; i++)
        {
            DecisionRecord d = decisions[i];
            await decisionsWriter.WriteLineAsync(JsonSerializer.Serialize(
                new
                {
                    pairId,
                    pairIndex,
                    rotationIndex,
                    leg = legName,
                    targetSeat = rotationIndex,
                    targetProfileId,
                    d.RoundId,
                    d.Seat,
                    d.Phase,
                    d.DecisionKind,
                    d.OptionId,
                    d.CapabilityId,
                    d.Status,
                    d.ReasonCode,
                    d.Detail,
                    d.StateVersion,
                    d.AuthoritativeStateVersionAfter,
                    d.ControlEpoch,
                    d.RecoveryGeneration,
                    d.DecisionId,
                    d.RequestId,
                    d.OfferId,
                    d.WindowId,
                    d.IsOfferChoice,
                    d.HasThirdCandidate,
                    d.SafeDefaultOptionId,
                    d.LegalOptionCount,
                    d.LegalCapabilities,
                    d.CheatPointsBefore,
                    d.SpentThisRoundBefore,
                    d.OptionCost,
                    d.EventSeq,
                    d.ReceiptStatus,
                    d.Replayed,
                    d.IsDefaultResolution,
                    d.ProfileId,
                    d.StrategyId,
                },
                JsonOptionsCompact)).ConfigureAwait(false);
        }

        await decisionsWriter.FlushAsync().ConfigureAwait(false);

        RoundBehaviorStats behaviorStats = ComputeRoundBehaviorStats(decisions, seatCheatUsage, rotationIndex);
        var telemetry = new PairedRoundTelemetry
        {
            PairId = pairId,
            PairIndex = pairIndex,
            RotationIndex = rotationIndex,
            Leg = legName,
            TargetSeat = rotationIndex,
            TargetProfileId = targetProfileId,
            OpponentProfileId = options.OpponentProfile,
            CharacterIds = options.CharacterIds,
            RulesetVersion = options.RulesetVersion,
            RotationSeedHex = "0x" + rotationSeed.ToString("X16", CultureInfo.InvariantCulture),
            ShuffleSeedHex = "0x" + shuffleSeed.ToString("X16", CultureInfo.InvariantCulture),
            SeatSeedHexes = seatSeeds.Select(s => "0x" + s.ToString("X16", CultureInfo.InvariantCulture)).ToArray(),
            RoomId = roomId,
            RoundId = roundId,
            Status = status,
            Terminal = terminal ?? false,
            Passed = passed,
            OutcomeCode = summary?.OutcomeCode ?? string.Empty,
            WinnerSeat = summary?.WinnerSeat ?? -1,
            LoserSeat = summary?.LoserSeat ?? -1,
            FinalScores = finalScores ?? Array.Empty<long>(),
            RemainingRiichiSticks = remainingRiichi ?? 0,
            WallAssignedCount = wallAssigned ?? 0,
            WallConserved = wallConserved ?? false,
            ConservationErrors = conservationErrors ?? Array.Empty<string>(),
            CheatBalances = cheatBalances,
            SeatCheatUsage = seatCheatUsage,
            StateVersion = summary?.StateVersion ?? 0L,
            DurationMilliseconds = roundWatch.ElapsedMilliseconds,
            StartedUtc = startedUtc,
            FinishedUtc = finishedUtc,
            Error = error,
            Reconciliation = reconciliation,
            BehaviorStats = behaviorStats,
        };

        await pairedRoundsWriter.WriteLineAsync(JsonSerializer.Serialize(telemetry, JsonOptionsCompact)).ConfigureAwait(false);
        await pairedRoundsWriter.FlushAsync().ConfigureAwait(false);

        var roundEvidence = new PairedRunPlan.RoundEvidence(
            roundId,
            rotationIndex,
            rotationSeed,
            rotationIndex,
            status,
            terminal,
            passed,
            finalScores,
            remainingRiichi,
            wallAssigned,
            wallConserved,
            conservationErrors,
            error);
        var leg = new PairedRunPlan.Leg(
            targetProfileId,
            rotationIndex,
            rotationSeed,
            status,
            string.Empty,
            error,
            new[] { roundEvidence });
        return (leg, telemetry);
    }

    private static CharacterAiProfile BuildSeatProfile(string profileId, ulong seatSeed, int maxSpend, int reservePoints)
    {
        return profileId switch
        {
            LocalAiProfiles.ScoredCheatBasic => CharacterAiProfile.CreateScoredBasic(
                profileId,
                CharacterStyle.Neutral,
                new AiCheatPolicy(enabled: true, maxSpendPerRound: maxSpend, reservePoints: reservePoints),
                seed: seatSeed),
            LocalAiProfiles.RandomCheatBasic => CharacterAiProfile.CreateRandomCheatBasic(
                profileId,
                new AiCheatPolicy(enabled: true, maxSpendPerRound: maxSpend, reservePoints: reservePoints),
                CharacterStyle.Neutral,
                seed: seatSeed),
            LocalAiProfiles.Basic => CharacterAiProfile.CreateBasic(
                profileId,
                CharacterStyle.Neutral,
                new AiCheatPolicy(enabled: false, maxSpendPerRound: 0, reservePoints: 0),
                seed: seatSeed),
            LocalAiProfiles.LegacyHeuristic => CharacterAiProfile.CreateLegacyHeuristic(
                profileId,
                new AiCheatPolicy(enabled: false, maxSpendPerRound: 0, reservePoints: 0),
                seed: seatSeed),
            _ => throw new ArgumentException("Unknown AI profile: " + profileId, nameof(profileId)),
        };
    }

    private static RoundReconciliation ReconcileRound(LocalRoundSummary? summary, DecisionRecord[] decisions)
    {
        if (summary == null)
        {
            return new RoundReconciliation { Passed = false, Error = "Summary missing." };
        }

        if (summary.CheatBalances.Count != 4)
        {
            return new RoundReconciliation { Passed = false, Error = "CheatBalances length != 4." };
        }

        // Verify per-seat ledger conservation: initial 120 - balance == sum(SeatCheatUsage) == sum(accepted cheat OptionCost).
        for (int seat = 0; seat < 4; seat++)
        {
            int ledgerSpent = 120 - summary.CheatBalances[seat];
            int usageSpent = summary.SeatCheatUsage.Where(u => u.Seat == seat).Sum(u => u.SpentPoints);
            if (ledgerSpent != usageSpent)
            {
                return new RoundReconciliation
                {
                    Passed = false,
                    Error = $"Seat {seat} ledger spent ({ledgerSpent}) != SeatCheatUsage ({usageSpent}).",
                };
            }

            int acceptedCost = decisions
                .Where(d => d.Seat == seat && d.DecisionKind == "cheat" && d.Status == "accepted")
                .Sum(d => d.OptionCost ?? 0);
            if (acceptedCost != ledgerSpent)
            {
                return new RoundReconciliation
                {
                    Passed = false,
                    Error = $"Seat {seat} accepted cheat OptionCost ({acceptedCost}) != ledger spent ({ledgerSpent}).",
                };
            }
        }

        // Verify correlation keys on every submitted decision and receipt.
        var ordinarySelectedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (DecisionRecord d in decisions)
        {
            if (d.DecisionKind == "ordinary" && d.Status == "selected")
            {
                if (string.IsNullOrWhiteSpace(d.DecisionId) || d.LegalOptionCount <= 0 || string.IsNullOrWhiteSpace(d.LegalCapabilities))
                {
                    return new RoundReconciliation { Passed = false, Error = "Ordinary selected record missing DecisionId or legal opportunity facts." };
                }

                ordinarySelectedKeys.Add($"{d.RoundId}|{d.Seat}|{d.DecisionId}|{d.StateVersion}");
            }
            else if ((d.DecisionKind == "ordinary" && d.Status == "accepted") || d.DecisionKind == "ordinary-rejected")
            {
                string key = $"{d.RoundId}|{d.Seat}|{d.DecisionId}|{d.StateVersion}";
                if (!ordinarySelectedKeys.Contains(key))
                {
                    return new RoundReconciliation { Passed = false, Error = $"Ordinary submission ({key}) lacks matching selected opportunity record." };
                }

                if (string.IsNullOrWhiteSpace(d.RequestId) || string.IsNullOrWhiteSpace(d.ReceiptStatus))
                {
                    return new RoundReconciliation { Passed = false, Error = $"Ordinary submission ({key}) missing RequestId or ReceiptStatus." };
                }

                if (d.Status == "accepted" && (!d.EventSeq.HasValue || !d.AuthoritativeStateVersionAfter.HasValue))
                {
                    return new RoundReconciliation { Passed = false, Error = $"Accepted ordinary submission ({key}) missing EventSeq or AuthoritativeStateVersionAfter." };
                }
            }
            else if (d.DecisionKind == "cheat" || d.DecisionKind == "cheat-fallback")
            {
                if (d.LegalOptionCount <= 0 || string.IsNullOrWhiteSpace(d.LegalCapabilities))
                {
                    return new RoundReconciliation { Passed = false, Error = "Cheat submission missing real legal opportunity counts/capabilities." };
                }

                if (d.DecisionKind == "cheat" &&
                    (string.IsNullOrWhiteSpace(d.RequestId) || string.IsNullOrWhiteSpace(d.ReceiptStatus) ||
                     !d.EventSeq.HasValue || !d.AuthoritativeStateVersionAfter.HasValue))
                {
                    return new RoundReconciliation { Passed = false, Error = "Accepted cheat record missing RequestId/ReceiptStatus/EventSeq/StateVersionAfter." };
                }
            }
            else if (d.DecisionKind == "cheat-skip")
            {
                if (d.LegalOptionCount <= 0 || string.IsNullOrWhiteSpace(d.LegalCapabilities))
                {
                    return new RoundReconciliation { Passed = false, Error = "Cheat-skip record missing real legal opportunity facts." };
                }
            }
        }

        return new RoundReconciliation
        {
            Passed = true,
            TotalDecisions = decisions.Length,
            OrdinarySelectedCount = ordinarySelectedKeys.Count,
            CheatOpportunityWindows = decisions.Count(d => d.DecisionKind is "cheat" or "cheat-skip" or "cheat-fallback"),
        };
    }

    private static RoundBehaviorStats ComputeRoundBehaviorStats(
        DecisionRecord[] decisions,
        SeatCheatUsageRecord[] seatCheatUsage,
        int targetSeat)
    {
        var stats = new RoundBehaviorStats();
        foreach (DecisionRecord d in decisions)
        {
            if (d.Seat != targetSeat)
            {
                continue;
            }

            if (d.DecisionKind == "ordinary" && d.Status == "selected")
            {
                stats.OrdinaryWindows++;
                if (HasCapability(d.LegalCapabilities, "Riichi")) stats.RiichiOpportunities++;
                if (HasCapability(d.LegalCapabilities, "Chi") ||
                    HasCapability(d.LegalCapabilities, "Pon") ||
                    HasCapability(d.LegalCapabilities, "Kan")) stats.MeldOpportunities++;
                if (HasCapability(d.LegalCapabilities, "Tsumo") ||
                    HasCapability(d.LegalCapabilities, "Ron")) stats.WinOpportunities++;
            }
            else if (d.DecisionKind == "ordinary" && d.Status == "accepted")
            {
                if (d.OptionId.StartsWith("riichi", StringComparison.OrdinalIgnoreCase)) stats.RiichiAccepted++;
                else if (d.OptionId.StartsWith("chi", StringComparison.OrdinalIgnoreCase) ||
                         d.OptionId.StartsWith("pon", StringComparison.OrdinalIgnoreCase) ||
                         d.OptionId.StartsWith("closed-kan", StringComparison.OrdinalIgnoreCase) ||
                         d.OptionId.StartsWith("open-kan", StringComparison.OrdinalIgnoreCase) ||
                         d.OptionId.StartsWith("added-kan", StringComparison.OrdinalIgnoreCase)) stats.MeldAccepted++;
                else if (string.Equals(d.OptionId, "tsumo", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(d.OptionId, "ron", StringComparison.OrdinalIgnoreCase)) stats.WinAccepted++;
            }
            else if (d.DecisionKind is "cheat" or "cheat-skip" or "cheat-fallback")
            {
                if (!d.IsOfferChoice)
                {
                    if (HasCapability(d.LegalCapabilities, "tile_mark")) stats.TileMarkOpportunities++;
                    if (HasCapability(d.LegalCapabilities, "sleeve_bloom")) stats.SleeveBloomOpportunities++;
                    if (HasCapability(d.LegalCapabilities, "beam_swap")) stats.BeamSwapOpportunities++;

                    if (d.DecisionKind == "cheat-skip") stats.CheatSkips++;
                    else if (d.DecisionKind == "cheat" && d.Status == "accepted")
                    {
                        if (d.CapabilityId == "tile_mark") stats.TileMarkActivationsAccepted++;
                        else if (d.CapabilityId == "sleeve_bloom") stats.SleeveBloomActivationsAccepted++;
                        else if (d.CapabilityId == "beam_swap") stats.BeamSwapActivationsAccepted++;
                    }
                    else if (d.DecisionKind == "cheat-fallback")
                    {
                        stats.CheatRejected++;
                    }
                }
                else
                {
                    stats.CheatOfferWindows++;
                    if (d.CapabilityId == "sleeve_bloom" && d.HasThirdCandidate) stats.ThirdCandidateAppearances++;
                    if (d.IsDefaultResolution) stats.SafeDefaultResolutions++;
                    if (d.DecisionKind == "cheat-fallback") stats.CheatRejected++;
                }
            }
            else if (d.DecisionKind == "timeout-default")
            {
                stats.TimeoutDefaults++;
            }
            else if (d.DecisionKind == "strategy-fallback")
            {
                stats.StrategyFallbacks++;
            }
        }

        stats.TargetSeatSpentPoints = seatCheatUsage.Where(u => u.Seat == targetSeat).Sum(u => u.SpentPoints);
        return stats;
    }

    private static bool HasCapability(string summary, string key)
    {
        if (string.IsNullOrEmpty(summary)) return false;
        string[] parts = summary.Split(',');
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            int colon = part.IndexOf(':');
            string name = colon >= 0 ? part[..colon] : part;
            if (string.Equals(name, key, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static BehaviorAggregate ComputeBehaviorAggregate(List<PairedRoundTelemetry> telemetries, RunnerOptions options)
    {
        return new BehaviorAggregate
        {
            Candidate = AggregateLegBehavior(telemetries.Where(t => t.Leg == "candidate" && t.Status == "complete"), options.CandidateProfile),
            Control = AggregateLegBehavior(telemetries.Where(t => t.Leg == "control" && t.Status == "complete"), options.ControlProfile),
        };
    }

    private static LegBehaviorAggregate AggregateLegBehavior(IEnumerable<PairedRoundTelemetry> items, string profileId)
    {
        var list = items.ToList();
        return new LegBehaviorAggregate
        {
            ProfileId = profileId,
            CompleteRounds = list.Count,
            TotalSpentPoints = list.Sum(t => t.BehaviorStats.TargetSeatSpentPoints),
            OrdinaryWindows = list.Sum(t => t.BehaviorStats.OrdinaryWindows),
            RiichiOpportunities = list.Sum(t => t.BehaviorStats.RiichiOpportunities),
            RiichiAccepted = list.Sum(t => t.BehaviorStats.RiichiAccepted),
            MeldOpportunities = list.Sum(t => t.BehaviorStats.MeldOpportunities),
            MeldAccepted = list.Sum(t => t.BehaviorStats.MeldAccepted),
            WinOpportunities = list.Sum(t => t.BehaviorStats.WinOpportunities),
            WinAccepted = list.Sum(t => t.BehaviorStats.WinAccepted),
            TileMarkOpportunities = list.Sum(t => t.BehaviorStats.TileMarkOpportunities),
            TileMarkActivationsAccepted = list.Sum(t => t.BehaviorStats.TileMarkActivationsAccepted),
            SleeveBloomOpportunities = list.Sum(t => t.BehaviorStats.SleeveBloomOpportunities),
            SleeveBloomActivationsAccepted = list.Sum(t => t.BehaviorStats.SleeveBloomActivationsAccepted),
            BeamSwapOpportunities = list.Sum(t => t.BehaviorStats.BeamSwapOpportunities),
            BeamSwapActivationsAccepted = list.Sum(t => t.BehaviorStats.BeamSwapActivationsAccepted),
            CheatSkips = list.Sum(t => t.BehaviorStats.CheatSkips),
            CheatOfferWindows = list.Sum(t => t.BehaviorStats.CheatOfferWindows),
            ThirdCandidateAppearances = list.Sum(t => t.BehaviorStats.ThirdCandidateAppearances),
            SafeDefaultResolutions = list.Sum(t => t.BehaviorStats.SafeDefaultResolutions),
            CheatRejected = list.Sum(t => t.BehaviorStats.CheatRejected),
            TimeoutDefaults = list.Sum(t => t.BehaviorStats.TimeoutDefaults),
            StrategyFallbacks = list.Sum(t => t.BehaviorStats.StrategyFallbacks),
        };
    }

    private static ReconciliationAggregate ComputeReconciliationAggregate(List<PairedRoundTelemetry> telemetries)
    {
        int failed = telemetries.Count(t => t.Status == "complete" && !t.Reconciliation.Passed);
        int checkedRounds = telemetries.Count(t => t.Status == "complete");
        return new ReconciliationAggregate
        {
            CheckedCompleteRounds = checkedRounds,
            PassedRounds = checkedRounds - failed,
            FailedRounds = failed,
            AllPassed = failed == 0,
        };
    }

    private static async Task<int> RunSingleModeAsync(RunnerOptions options)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        string decisionsPath = Path.Combine(options.OutputDirectory, "decisions.jsonl");
        string roundsDirectory = Path.Combine(options.OutputDirectory, "rounds");
        Directory.CreateDirectory(roundsDirectory);
        string runPath = Path.Combine(options.OutputDirectory, "run.json");

        string profileLabel = options.SeatProfiles.Count == 4
            ? string.Join(",", options.SeatProfiles)
            : DefaultProfile;

        var stopwatch = Stopwatch.StartNew();
        var results = new List<RoundRecord>();
        int completed = 0;
        int failed = 0;
        string firstFailure = string.Empty;
        using var cancellation = new CancellationTokenSource(options.EffectiveOverallTimeout);
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
                    ulong shuffleSeed = options.SeatProfiles.Count == 4
                        ? PairedRunPlan.DeriveShuffleSeed(roundSeed)
                        : roundSeed;
                    var environment = new DeterministicEnvironment(shuffleSeed);
                    LocalAiOptions aiOptions;
                    if (options.SeatProfiles.Count == 4)
                    {
                        var seatProfiles = new CharacterAiProfile[4];
                        for (int seat = 0; seat < 4; seat++)
                        {
                            ulong seatSeed = PairedRunPlan.DeriveSeatStrategySeed(roundSeed, seat);
                            seatProfiles[seat] = BuildSeatProfile(
                                options.SeatProfiles[seat],
                                seatSeed,
                                options.MaxSpendPerRound,
                                options.ReservePoints);
                        }

                        aiOptions = new LocalAiOptions
                        {
                            Seed = PairedRunPlan.DeriveStrategyBaseSeed(roundSeed),
                            SeatProfiles = seatProfiles,
                        };
                    }
                    else
                    {
                        aiOptions = new LocalAiOptions
                        {
                            ProfileId = DefaultProfile,
                            Seed = roundSeed,
                            AllowCheats = true,
                            MaxSpendPerRound = options.MaxSpendPerRound,
                            ReservePoints = options.ReservePoints,
                        };
                    }

                    var hostOptions = new LocalTableHostOptions
                    {
                        SaveDirectory = roundDirectory,
                        RoomId = roomId,
                        RulesetVersion = options.RulesetVersion,
                        OwnerInstanceId = "cheat-ai-runner",
                        LeaseDuration = TimeSpan.FromMinutes(10),
                        AutoFlushInterval = TimeSpan.FromMilliseconds(250),
                        Environment = environment.Create(),
                        SeatPlan = LocalTableSeatPlan.FromHumanSeats(Array.Empty<int>()),
                        CharacterIds = options.CharacterIds,
                        Ai = aiOptions,
                        AiDecisionObserver = recorder,
                    };

                    await using (LocalTableRoom room = await LocalTableHost.OpenRoomAsync(
                        hostOptions,
                        cancellation.Token).ConfigureAwait(false))
                    {
                        using var roundTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                            cancellation.Token);
                        roundTimeout.CancelAfter(options.EffectiveRoundTimeout);
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
                            decision.DecisionId,
                            decision.RequestId,
                            decision.LegalOptionCount,
                            decision.LegalCapabilities,
                            decision.ReceiptStatus,
                            decision.ProfileId,
                            decision.StrategyId,
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
            ProfileId = profileLabel,
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

    private static int RunPairedStatisticsSelfTest()
    {
        try
        {
            PairedRunPlan.Plan notRunPlan = PairedRunPlan.Create(2, 20261127, "candidate", "control");
            PairedStatistics.Summary notRun = PairedStatistics.Calculate(notRunPlan);
            if (notRun.CompletePairs != 0 || notRun.NotRunPairs != 2 || notRun.CompleteRounds != 0 ||
                notRun.MeanDifference != null || notRun.Lower95 != null || notRun.Upper95 != null ||
                notRun.FirstFailure != null)
                throw new InvalidOperationException("plan-only NOT_RUN statistics mismatch");

            PairedRunPlan.Plan completePlan = BuildCompleteStatisticsFixture();
            PairedStatistics.Summary sample = PairedStatistics.Calculate(completePlan);
            double expectedHalfWidth = PairedStatistics.T95Critical(1) * 40d;
            if (sample.RequestedPairs != 2 || sample.CompletePairs != 2 || sample.CompleteRounds != 16 ||
                sample.FailedPairs != 0 || sample.TimedOutPairs != 0 || sample.NotRunPairs != 0 ||
                sample.MeanDifference != 0d || sample.Lower95 != -expectedHalfWidth ||
                sample.Upper95 != expectedHalfWidth || sample.FirstFailure != null)
                throw new InvalidOperationException("two-pair evidence-backed mean/CI fixture mismatch");

            double df1 = PairedStatistics.T95Critical(1);
            double df3 = PairedStatistics.T95Critical(3);
            double df10 = PairedStatistics.T95Critical(10);
            if (Math.Abs(df1 - 12.7062047364) > 1e-9 ||
                Math.Abs(df3 - 3.18244630528) > 1e-9 ||
                Math.Abs(df10 - 2.22813885196) > 1e-9)
                throw new InvalidOperationException("Student-t critical value mismatch");

            PairedRunPlan.Plan oneComplete = SetPair(completePlan, 1, _ => CreateNotRunPair(completePlan, 1));
            PairedStatistics.Summary shortSample = PairedStatistics.Calculate(oneComplete);
            if (shortSample.MeanDifference != null || shortSample.Lower95 != null || shortSample.Upper95 != null ||
                shortSample.CompletePairs != 1 || shortSample.NotRunPairs != 1)
                throw new InvalidOperationException("insufficient sample must not report an estimate");

            PairedRunPlan.Plan failedPlan = CreateFailedPlan("failed", "failure at candidate rotation 0");
            PairedStatistics.Summary failed = PairedStatistics.Calculate(failedPlan);
            if (failed.FailedPairs != 1 || failed.TimedOutPairs != 0 || failed.NotRunPairs != 0 ||
                failed.CompletePairs != 0 || failed.FirstFailure == null || failed.FirstFailure.PairIndex != 1 ||
                failed.FirstFailure.RotationIndex != 0 || failed.FirstFailure.Leg != "candidate" ||
                failed.FirstFailure.RoundId != "pair-0001-candidate-r0" ||
                failed.FirstFailure.Detail != "failure at candidate rotation 0")
                throw new InvalidOperationException("failed pair/first failure evidence mismatch");

            PairedRunPlan.Plan timeoutPlan = CreateFailedPlan("timeout", "timeout at candidate rotation 0");
            PairedStatistics.Summary timedOut = PairedStatistics.Calculate(timeoutPlan);
            if (timedOut.TimedOutPairs != 1 || timedOut.FailedPairs != 0 || timedOut.FirstFailure == null ||
                timedOut.FirstFailure.Detail != "timeout at candidate rotation 0")
                throw new InvalidOperationException("timeout pair/first failure evidence mismatch");

            PairedStatistics.Summary partial = PairedStatistics.Calculate(CreatePartialPlan(mixed: false));
            if (partial.CompletePairs != 0 || partial.PartialPairs != 1 || partial.CompleteRounds != 1 ||
                partial.NotRunRounds != 7 || partial.FailedRounds != 0 || partial.TimedOutRounds != 0 ||
                partial.MeanDifference != null || partial.Lower95 != null || partial.Upper95 != null)
                throw new InvalidOperationException("partial cancellation count/classification mismatch");

            PairedStatistics.Summary mixed = PairedStatistics.Calculate(CreatePartialPlan(mixed: true));
            if (mixed.CompletePairs != 0 || mixed.TimedOutPairs != 1 || mixed.FailedPairs != 0 ||
                mixed.CompleteRounds != 1 || mixed.TimedOutRounds != 1 || mixed.FailedRounds != 1 ||
                mixed.NotRunRounds != 5 || mixed.MeanDifference != null || mixed.Lower95 != null ||
                mixed.Upper95 != null || mixed.FirstFailure == null || mixed.FirstFailure.PairId != "pair-0001" ||
                mixed.FirstFailure.RotationIndex != 0 || mixed.FirstFailure.Leg != "candidate" ||
                mixed.FirstFailure.RoundId != "pair-0001-candidate-r0" || mixed.FirstFailure.Detail != "first timeout")
                throw new InvalidOperationException("mixed partial batch counts/first failure mismatch");

            AssertPairedEvidenceRejected("candidate identity changed in one leg", SetLeg(completePlan, 1, 0, "candidate",
                leg => leg with { ProfileId = "different-candidate" }));
            AssertPairedEvidenceRejected("candidate identity changed across second pair", SetPair(completePlan, 1, pair => pair with
            {
                Rotations = pair.Rotations.Select(rotation => rotation with
                {
                    Candidate = rotation.Candidate with { ProfileId = "different-candidate" },
                }).ToArray(),
            }));
            AssertPairedEvidenceRejected("control identity changed across second pair", SetPair(completePlan, 1, pair => pair with
            {
                Rotations = pair.Rotations.Select(rotation => rotation with
                {
                    Control = rotation.Control with { ProfileId = "different-control" },
                }).ToArray(),
            }));

            AssertPairedEvidenceRejected("missing planned leg", SetPair(completePlan, 0, pair => pair with
            {
                Rotations = pair.Rotations.Select(rotation => rotation.RotationIndex == 0
                    ? rotation with { Control = null! } : rotation).ToArray(),
            }));
            AssertPairedEvidenceRejected("missing rotation", SetLeg(completePlan, 0, 0, "candidate",
                leg => leg with { Rounds = Array.Empty<PairedRunPlan.RoundEvidence>() }));
            AssertPairedEvidenceRejected("wrong seat", SetLeg(completePlan, 0, 0, "candidate", leg =>
                leg with { Rounds = new[] { leg.Rounds[0] with { TargetSeat = (leg.TargetSeat + 1) % 4 } } }));
            AssertPairedEvidenceRejected("wrong rotation", SetLeg(completePlan, 0, 0, "candidate", leg =>
                leg with { Rounds = new[] { leg.Rounds[0] with { RotationIndex = 1 } } }));
            AssertPairedEvidenceRejected("wrong seed", SetLeg(completePlan, 0, 0, "candidate", leg =>
                leg with { Rounds = new[] { leg.Rounds[0] with { Seed = leg.Seed + 1 } } }));
            AssertPairedEvidenceRejected("duplicate round evidence", SetLeg(SetLeg(completePlan, 0, 0, "control", leg =>
                leg with { Rounds = new[] { leg.Rounds[0] with { RoundId = "pair-0001-candidate-r0" } } }), 0, 1, "candidate",
                leg => leg with { Rounds = new[] { leg.Rounds[0] with { RoundId = "pair-0001-candidate-r0" } } }));
            AssertPairedEvidenceRejected("tile conservation", SetLeg(completePlan, 0, 0, "candidate", leg =>
                leg with { Rounds = new[] { leg.Rounds[0] with { WallAssignedCount = 135 } } }));
            AssertPairedEvidenceRejected("non-terminal round", SetLeg(completePlan, 0, 0, "candidate", leg =>
                leg with { Rounds = new[] { leg.Rounds[0] with { Terminal = false } } }));
            AssertPairedEvidenceRejected("passed false in complete leg", SetLeg(completePlan, 0, 0, "candidate", leg =>
                leg with { Rounds = new[] { leg.Rounds[0] with { Passed = false } } }));
            AssertPairedEvidenceRejected("wall conservation assertion", SetLeg(completePlan, 0, 0, "candidate", leg =>
                leg with { Rounds = new[] { leg.Rounds[0] with { WallConserved = false } } }));
            AssertPairedEvidenceRejected("conservation errors non-empty", SetLeg(completePlan, 0, 0, "candidate", leg =>
                leg with { Rounds = new[] { leg.Rounds[0] with { ConservationErrors = new[] { "tile 1 missing" } } } }));
            AssertPairedEvidenceRejected("negative riichi sticks", SetLeg(completePlan, 0, 0, "candidate", leg =>
                leg with { Rounds = new[] { leg.Rounds[0] with { RemainingRiichiSticks = -1 } } }));
            AssertPairedEvidenceRejected("point conservation", SetLeg(completePlan, 0, 0, "candidate", leg =>
                leg with { Rounds = new[] { leg.Rounds[0] with
                    { FinalScores = leg.Rounds[0].FinalScores!.Select((score, index) => index == 0 ? score + 1 : score).ToArray() } } }));
            AssertPairedEvidenceRejected("empty failure detail", SetLeg(failedPlan, 0, 0, "candidate", leg =>
                leg with { FirstFailure = string.Empty }));
            AssertPairedEvidenceRejected("timeout without failure evidence", SetLeg(timeoutPlan, 0, 0, "candidate", leg =>
                leg with
                {
                    FirstFailure = string.Empty,
                    Rounds = new[] { leg.Rounds[0] with { Error = string.Empty } },
                }));
            AssertPairedEvidenceRejected("wrong pair seed", SetPair(completePlan, 0, pair => pair with { PairSeed = pair.PairSeed + 1 }));

            if (notRunPlan.Pairs.Length != 2 || notRunPlan.Pairs.Any(pair => pair.Rotations.Length != 4 ||
                    pair.Rotations.Any(rotation => rotation.Candidate.Seed != rotation.Control.Seed ||
                        rotation.Candidate.TargetSeat != rotation.RotationIndex || rotation.Control.TargetSeat != rotation.RotationIndex ||
                        rotation.Candidate.Status != "not-run" || rotation.Control.Status != "not-run" ||
                        rotation.Candidate.Rounds.Length != 0 || rotation.Control.Rounds.Length != 0)))
                throw new InvalidOperationException("deterministic paired plan mismatch");

            PairedRunPlan.Plan samePlan = PairedRunPlan.Create(2, 20261127, "candidate", "control");
            if (!notRunPlan.Pairs.Select(pair => pair.PairSeed).SequenceEqual(samePlan.Pairs.Select(pair => pair.PairSeed)))
                throw new InvalidOperationException("paired plan seed derivation is not deterministic");

            // Verify shuffle & per-seat strategy seed isolation.
            ulong rSeed = PairedRunPlan.DeriveRotationSeed(PairedRunPlan.DerivePairSeed(20261128UL, 1), 0);
            ulong shuf = PairedRunPlan.DeriveShuffleSeed(rSeed);
            ulong stratBase = PairedRunPlan.DeriveStrategyBaseSeed(rSeed);
            ulong[] sSeeds = Enumerable.Range(0, 4).Select(seat => PairedRunPlan.DeriveSeatStrategySeed(rSeed, seat)).ToArray();
            if (new[] { rSeed, shuf, stratBase }.Concat(sSeeds).Distinct().Count() != 7)
                throw new InvalidOperationException("shuffle and per-seat strategy seeds collided");

            Console.WriteLine("PASS paired evidence tool fixtures: complete=2 failed=1 timeout=1 notRun=2; exact mean/95% CI; partial=1 complete/7 not-run; mixed=1 complete/1 timeout/1 failed/5 not-run with timeout first failure; profile identity mutations rejected; missing leg/round, wrong seat/rotation/seed, duplicate evidence, non-terminal, tile/point conservation errors and empty failure detail rejected; n<2 unavailable; df1/3/10 criticals; shuffle/seat seed isolation verified");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL paired statistics: " + exception);
            return 1;
        }
    }

    private static PairedRunPlan.Plan BuildCompleteStatisticsFixture()
    {
        PairedRunPlan.Plan plan = PairedRunPlan.Create(2, 20261127, "candidate", "control");
        PairedRunPlan.Pair[] pairs = plan.Pairs.Select(pair => pair with
        {
            Rotations = pair.Rotations.Select(rotation =>
            {
                long candidateTarget = pair.PairIndex == 1 ? 25_010L : 24_990L;
                PairedRunPlan.Leg candidate = MakeCompleteLeg(pair, rotation, rotation.Candidate,
                    "candidate", candidateTarget);
                PairedRunPlan.Leg control = MakeCompleteLeg(pair, rotation, rotation.Control,
                    "control", 25_000L);
                return rotation with { Candidate = candidate, Control = control };
            }).ToArray(),
        }).ToArray();
        return plan with { Pairs = pairs };
    }

    private static PairedRunPlan.Leg MakeCompleteLeg(PairedRunPlan.Pair pair,
        PairedRunPlan.Rotation rotation, PairedRunPlan.Leg leg, string role, long targetScore)
    {
        long[] scores = Enumerable.Repeat(25_000L, 4).ToArray();
        scores[leg.TargetSeat] = targetScore;
        scores[(leg.TargetSeat + 1) % 4] += 25_000L - targetScore;
        var evidence = new PairedRunPlan.RoundEvidence(
            pair.PairId + "-" + role + "-r" + rotation.RotationIndex,
            rotation.RotationIndex, rotation.Seed, leg.TargetSeat, "complete", true, true,
            scores, 0, 136, true, Array.Empty<string>(), string.Empty);
        return leg with
        {
            Status = "complete",
            NotRunReason = string.Empty,
            FirstFailure = string.Empty,
            Rounds = new[] { evidence },
        };
    }

    private static PairedRunPlan.Plan CreateFailedPlan(string status, string error)
    {
        PairedRunPlan.Plan plan = PairedRunPlan.Create(1, 20261127, "candidate", "control");
        PairedRunPlan.Pair pair = plan.Pairs[0];
        PairedRunPlan.Rotation[] rotations = pair.Rotations.Select(rotation => rotation with
        {
            Candidate = rotation.RotationIndex == 0
                ? rotation.Candidate with
                {
                    Status = status, NotRunReason = string.Empty, FirstFailure = error,
                    Rounds = new[] { new PairedRunPlan.RoundEvidence(
                        pair.PairId + "-candidate-r0", 0, rotation.Seed, rotation.Candidate.TargetSeat,
                        status, false, false, null, null, null, null, null, error) },
                }
                : rotation.Candidate,
        }).ToArray();
        return plan with { Pairs = new[] { pair with { Rotations = rotations } } };
    }

    private static PairedRunPlan.Plan CreatePartialPlan(bool mixed)
    {
        PairedRunPlan.Plan plan = PairedRunPlan.Create(1, 20261127, "candidate", "control");
        PairedRunPlan.Pair pair = plan.Pairs[0];
        PairedRunPlan.Rotation[] rotations = pair.Rotations.Select(rotation => rotation with
        {
            Candidate = mixed && rotation.RotationIndex == 1
                ? CreateFailureLeg(pair, rotation, rotation.Candidate, "failed", "later failure")
                : rotation.Candidate,
        }).ToArray();
        PairedRunPlan.Rotation first = rotations[0] with
        {
            Candidate = MakeCompleteLeg(pair, rotations[0], rotations[0].Candidate, "candidate", 25_000),
        };
        rotations[0] = first;
        if (!mixed)
        {
            rotations[0] = first with
            {
                Control = first.Control with { NotRunReason = "NOT_RUN: cancelled before execution." },
            };
        }
        else
        {
            rotations[0] = first with
            {
                Candidate = CreateFailureLeg(pair, first, first.Candidate, "timeout", "first timeout"),
                Control = MakeCompleteLeg(pair, first, first.Control, "control", 25_000),
            };
        }
        return plan with { Pairs = new[] { pair with { Rotations = rotations } } };
    }

    private static PairedRunPlan.Leg CreateFailureLeg(PairedRunPlan.Pair pair,
        PairedRunPlan.Rotation rotation, PairedRunPlan.Leg leg, string status, string error) => leg with
    {
        Status = status,
        NotRunReason = string.Empty,
        FirstFailure = error,
        Rounds = new[] { new PairedRunPlan.RoundEvidence(pair.PairId + "-" +
            (ReferenceEquals(leg, rotation.Candidate) ? "candidate" : "control") + "-r" + rotation.RotationIndex,
            rotation.RotationIndex, rotation.Seed, leg.TargetSeat, status, false, false,
            null, null, null, null, null, error) },
    };

    private static PairedRunPlan.Plan SetPair(PairedRunPlan.Plan plan, int pairArrayIndex,
        Func<PairedRunPlan.Pair, PairedRunPlan.Pair> update) => plan with
    {
        Pairs = plan.Pairs.Select((pair, index) => index == pairArrayIndex ? update(pair) : pair).ToArray(),
    };

    private static PairedRunPlan.Plan SetLeg(PairedRunPlan.Plan plan, int pairArrayIndex,
        int rotationIndex, string role, Func<PairedRunPlan.Leg, PairedRunPlan.Leg> update) =>
        SetPair(plan, pairArrayIndex, pair => pair with
        {
            Rotations = pair.Rotations.Select(rotation => rotation.RotationIndex != rotationIndex ? rotation :
                role == "candidate"
                    ? rotation with { Candidate = update(rotation.Candidate) }
                    : rotation with { Control = update(rotation.Control) }).ToArray(),
        });

    private static PairedRunPlan.Pair CreateNotRunPair(PairedRunPlan.Plan plan, int pairArrayIndex) =>
        PairedRunPlan.Create(plan.RequestedPairs, plan.MasterSeed, "candidate", "control").Pairs[pairArrayIndex];

    private static void AssertPairedEvidenceRejected(string name, PairedRunPlan.Plan plan)
    {
        try
        {
            _ = PairedStatistics.Calculate(plan);
        }
        catch (ArgumentException)
        {
            return;
        }
        throw new InvalidOperationException("invalid paired evidence was accepted: " + name);
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
        File.WriteAllText(manifestPath, BuildManifest(root, manifestPath), new UTF8Encoding(false));
    }

    private static async Task<(string planPath, string manifestPath)> WritePairedPlanExclusiveAsync(
        string outputDirectory, PairedRunPlan.Plan plan, PairedStatistics.Summary statistics)
    {
        string fullOutput = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        string parent = Path.GetDirectoryName(fullOutput) ?? Directory.GetCurrentDirectory();
        string leaf = Path.GetFileName(fullOutput);
        if (string.IsNullOrWhiteSpace(leaf)) throw new IOException("Output directory must name a new child directory.");
        Directory.CreateDirectory(parent);
        if (Directory.Exists(fullOutput) || File.Exists(fullOutput))
            throw new IOException("Output directory is already occupied: " + fullOutput);

        string lockPath = Path.Combine(parent, "." + leaf + ".paired-plan.lock");
        FileStream outputLock = new(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        string stagingDirectory = fullOutput + ".staging-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        try
        {
            if (Directory.Exists(fullOutput) || File.Exists(fullOutput))
                throw new IOException("Output directory became occupied before publication: " + fullOutput);
            Directory.CreateDirectory(stagingDirectory);
            string stagedPlanPath = Path.Combine(stagingDirectory, "paired-plan.json");
            await using (var stream = new FileStream(stagedPlanPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, new { plan, statistics }, JsonOptions)
                    .ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            string stagedManifestPath = Path.Combine(stagingDirectory, "sha256.txt");
            string manifest = BuildManifest(stagingDirectory, stagedManifestPath);
            await using (var stream = new FileStream(stagedManifestPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 16 * 1024, useAsync: true))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                await writer.WriteAsync(manifest).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            Directory.Move(stagingDirectory, fullOutput);
            return (Path.Combine(fullOutput, "paired-plan.json"), Path.Combine(fullOutput, "sha256.txt"));
        }
        finally
        {
            outputLock.Dispose();
            try { File.Delete(lockPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string BuildManifest(string root, string manifestPath)
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
                .Append("  ").Append(relative).Append('\n');
        }
        return builder.ToString();
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
                    ProfileId = record.ProfileId,
                    StrategyId = record.StrategyId,
                    ControlEpoch = record.ControlEpoch,
                    RecoveryGeneration = record.RecoveryGeneration,
                    DecisionId = record.DecisionId,
                    RequestId = record.RequestId,
                    OfferId = record.OfferId,
                    WindowId = record.WindowId,
                    IsOfferChoice = record.IsOfferChoice,
                    HasThirdCandidate = record.HasThirdCandidate,
                    SafeDefaultOptionId = record.SafeDefaultOptionId,
                    LegalOptionCount = record.LegalOptionCount,
                    LegalCapabilities = record.LegalCapabilities,
                    CheatPointsBefore = record.CheatPointsBefore,
                    SpentThisRoundBefore = record.SpentThisRoundBefore,
                    OptionCost = record.OptionCost,
                    AuthoritativeStateVersionAfter = record.AuthoritativeStateVersionAfter,
                    EventSeq = record.EventSeq,
                    ReceiptStatus = record.ReceiptStatus,
                    Replayed = record.Replayed,
                    IsDefaultResolution = record.IsDefaultResolution,
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
        public int RoundTimeoutMs { get; private set; }
        public int OverallTimeoutMinutes { get; private set; } = 120;
        public int OverallTimeoutMs { get; private set; }
        public List<string> SeatProfiles { get; } = new();
        public int PlanPairs { get; private set; }
        public int RunPairs { get; private set; }
        public string CandidateProfile { get; private set; } = "candidate";
        public string ControlProfile { get; private set; } = "control";
        public string OpponentProfile { get; private set; } = LocalAiProfiles.Basic;
        public string[] CharacterIds { get; private set; } =
            { PlayerCharacterIds.Octopus, PlayerCharacterIds.Octopus, PlayerCharacterIds.Octopus, PlayerCharacterIds.Octopus };
        public int RulesetVersion { get; private set; } = 1;
        public int MaxSpendPerRound { get; private set; } = 60;
        public int ReservePoints { get; private set; }
        public string PreregistrationPath { get; private set; } = string.Empty;
        public string PreregistrationSha256 { get; private set; } = string.Empty;

        public TimeSpan EffectiveRoundTimeout => RoundTimeoutMs > 0
            ? TimeSpan.FromMilliseconds(RoundTimeoutMs)
            : TimeSpan.FromSeconds(RoundTimeoutSeconds);

        public TimeSpan EffectiveOverallTimeout => OverallTimeoutMs > 0
            ? TimeSpan.FromMilliseconds(OverallTimeoutMs)
            : TimeSpan.FromMinutes(OverallTimeoutMinutes);

        private bool HasRoundsOption { get; set; }
        private bool HasSeatProfilesOption { get; set; }
        private bool HasCandidateProfileOption { get; set; }
        private bool HasControlProfileOption { get; set; }
        private bool HasOpponentProfileOption { get; set; }
        private bool HasCharacterIdsOption { get; set; }
        private bool HasRulesetVersionOption { get; set; }
        private bool HasMaxSpendOption { get; set; }
        private bool HasReservePointsOption { get; set; }
        private bool HasRoundTimeoutOption { get; set; }
        private bool HasOverallTimeoutOption { get; set; }
        private bool HasPreregOption { get; set; }

        public static RunnerOptions? Parse(string[] args)
        {
            var options = new RunnerOptions();
            var seenOptions = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < args.Length; i++)
            {
                string name = args[i];
                if (!seenOptions.Add(name)) return null;
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    return null;
                }

                string value = args[++i];
                switch (name)
                {
                    case "--rounds":
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rounds) || rounds < 1)
                            return null;
                        options.Rounds = rounds;
                        options.HasRoundsOption = true;
                        break;
                    case "--seed":
                        if (!ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong seed))
                            return null;
                        options.Seed = seed;
                        break;
                    case "--output":
                        options.OutputDirectory = Path.GetFullPath(value);
                        break;
                    case "--seat-profiles":
                        string[] profiles = value.Split(',', StringSplitOptions.None)
                            .Select(profile => profile.Trim()).ToArray();
                        if (profiles.Length < 2 || profiles.Length > 4 || profiles.Any(string.IsNullOrWhiteSpace))
                            return null;
                        options.SeatProfiles.AddRange(profiles);
                        options.HasSeatProfilesOption = true;
                        break;
                    case "--plan-pairs":
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int planPairs) || planPairs < 1)
                            return null;
                        options.PlanPairs = planPairs;
                        break;
                    case "--run-pairs":
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int runPairs) || runPairs < 1)
                            return null;
                        options.RunPairs = runPairs;
                        break;
                    case "--candidate-profile":
                        if (string.IsNullOrWhiteSpace(value)) return null;
                        options.CandidateProfile = value;
                        options.HasCandidateProfileOption = true;
                        break;
                    case "--control-profile":
                        if (string.IsNullOrWhiteSpace(value)) return null;
                        options.ControlProfile = value;
                        options.HasControlProfileOption = true;
                        break;
                    case "--opponent-profile":
                        if (string.IsNullOrWhiteSpace(value)) return null;
                        options.OpponentProfile = value;
                        options.HasOpponentProfileOption = true;
                        break;
                    case "--character-ids":
                        string[] chars = value.Split(',', StringSplitOptions.None)
                            .Select(c => c.Trim()).ToArray();
                        if (chars.Length != 4 || chars.Any(c => !PlayerCharacterIds.IsSupported(c)))
                            return null;
                        options.CharacterIds = chars;
                        options.HasCharacterIdsOption = true;
                        break;
                    case "--ruleset-version":
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ruleset) || ruleset < 1)
                            return null;
                        options.RulesetVersion = ruleset;
                        options.HasRulesetVersionOption = true;
                        break;
                    case "--max-spend-per-round":
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int maxSpend) || maxSpend < 0)
                            return null;
                        options.MaxSpendPerRound = maxSpend;
                        options.HasMaxSpendOption = true;
                        break;
                    case "--reserve-points":
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int reserve) || reserve < 0)
                            return null;
                        options.ReservePoints = reserve;
                        options.HasReservePointsOption = true;
                        break;
                    case "--round-timeout-seconds":
                        if (options.RoundTimeoutMs > 0 ||
                            !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int roundTimeout) || roundTimeout < 1)
                            return null;
                        options.RoundTimeoutSeconds = roundTimeout;
                        options.HasRoundTimeoutOption = true;
                        break;
                    case "--round-timeout-ms":
                        if (options.HasRoundTimeoutOption ||
                            !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int roundTimeoutMs) || roundTimeoutMs < 1)
                            return null;
                        options.RoundTimeoutMs = roundTimeoutMs;
                        options.HasRoundTimeoutOption = true;
                        break;
                    case "--overall-timeout-minutes":
                        if (options.OverallTimeoutMs > 0 ||
                            !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int overallTimeout) || overallTimeout < 1)
                            return null;
                        options.OverallTimeoutMinutes = overallTimeout;
                        options.HasOverallTimeoutOption = true;
                        break;
                    case "--overall-timeout-ms":
                        if (options.HasOverallTimeoutOption ||
                            !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int overallTimeoutMs) || overallTimeoutMs < 1)
                            return null;
                        options.OverallTimeoutMs = overallTimeoutMs;
                        options.HasOverallTimeoutOption = true;
                        break;
                    case "--preregistration-path":
                        if (string.IsNullOrWhiteSpace(value)) return null;
                        options.PreregistrationPath = Path.GetFullPath(value);
                        options.HasPreregOption = true;
                        break;
                    case "--preregistration-sha256":
                        if (string.IsNullOrWhiteSpace(value)) return null;
                        options.PreregistrationSha256 = value.Trim();
                        options.HasPreregOption = true;
                        break;
                    default:
                        return null;
                }
            }

            if (options.PlanPairs > 0 && options.RunPairs > 0) return null;

            if (options.PlanPairs > 0 &&
                (options.HasRoundsOption || options.HasSeatProfilesOption ||
                 options.HasOpponentProfileOption || options.HasCharacterIdsOption ||
                 options.HasRulesetVersionOption || options.HasMaxSpendOption ||
                 options.HasReservePointsOption || options.HasPreregOption ||
                 options.HasRoundTimeoutOption || options.HasOverallTimeoutOption))
            {
                return null;
            }

            if (options.RunPairs > 0)
            {
                if (options.HasRoundsOption || options.HasSeatProfilesOption)
                    return null;
                if (!options.HasCandidateProfileOption)
                    options.CandidateProfile = LocalAiProfiles.ScoredCheatBasic;
                if (!options.HasControlProfileOption)
                    options.ControlProfile = LocalAiProfiles.RandomCheatBasic;
            }
            else if (options.PlanPairs == 0)
            {
                if (options.HasCandidateProfileOption || options.HasControlProfileOption ||
                    options.HasOpponentProfileOption || options.HasPreregOption)
                    return null;
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

    private sealed class PairedRoundTelemetry
    {
        public string PairId { get; set; } = string.Empty;
        public int PairIndex { get; set; }
        public int RotationIndex { get; set; }
        public string Leg { get; set; } = string.Empty;
        public int TargetSeat { get; set; }
        public string TargetProfileId { get; set; } = string.Empty;
        public string OpponentProfileId { get; set; } = string.Empty;
        public string[] CharacterIds { get; set; } = Array.Empty<string>();
        public int RulesetVersion { get; set; }
        public string RotationSeedHex { get; set; } = string.Empty;
        public string ShuffleSeedHex { get; set; } = string.Empty;
        public string[] SeatSeedHexes { get; set; } = Array.Empty<string>();
        public string RoomId { get; set; } = string.Empty;
        public string RoundId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
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
        public SeatCheatUsageRecord[] SeatCheatUsage { get; set; } = Array.Empty<SeatCheatUsageRecord>();
        public long StateVersion { get; set; }
        public long DurationMilliseconds { get; set; }
        public DateTimeOffset StartedUtc { get; set; }
        public DateTimeOffset FinishedUtc { get; set; }
        public string Error { get; set; } = string.Empty;
        public RoundReconciliation Reconciliation { get; set; } = new();
        public RoundBehaviorStats BehaviorStats { get; set; } = new();
    }

    private sealed class SeatCheatUsageRecord
    {
        public int Seat { get; set; }
        public string Reason { get; set; } = string.Empty;
        public int Count { get; set; }
        public int SpentPoints { get; set; }
    }

    private sealed class RoundReconciliation
    {
        public bool Passed { get; set; }
        public string Error { get; set; } = string.Empty;
        public int TotalDecisions { get; set; }
        public int OrdinarySelectedCount { get; set; }
        public int CheatOpportunityWindows { get; set; }
    }

    private sealed class ReconciliationAggregate
    {
        public int CheckedCompleteRounds { get; set; }
        public int PassedRounds { get; set; }
        public int FailedRounds { get; set; }
        public bool AllPassed { get; set; }
    }

    private sealed class RoundBehaviorStats
    {
        public int TargetSeatSpentPoints { get; set; }
        public int OrdinaryWindows { get; set; }
        public int RiichiOpportunities { get; set; }
        public int RiichiAccepted { get; set; }
        public int MeldOpportunities { get; set; }
        public int MeldAccepted { get; set; }
        public int WinOpportunities { get; set; }
        public int WinAccepted { get; set; }
        public int TileMarkOpportunities { get; set; }
        public int TileMarkActivationsAccepted { get; set; }
        public int SleeveBloomOpportunities { get; set; }
        public int SleeveBloomActivationsAccepted { get; set; }
        public int BeamSwapOpportunities { get; set; }
        public int BeamSwapActivationsAccepted { get; set; }
        public int CheatSkips { get; set; }
        public int CheatOfferWindows { get; set; }
        public int ThirdCandidateAppearances { get; set; }
        public int SafeDefaultResolutions { get; set; }
        public int CheatRejected { get; set; }
        public int TimeoutDefaults { get; set; }
        public int StrategyFallbacks { get; set; }
    }

    private sealed class BehaviorAggregate
    {
        public LegBehaviorAggregate Candidate { get; set; } = new();
        public LegBehaviorAggregate Control { get; set; } = new();
    }

    private sealed class LegBehaviorAggregate
    {
        public string ProfileId { get; set; } = string.Empty;
        public int CompleteRounds { get; set; }
        public int TotalSpentPoints { get; set; }
        public int OrdinaryWindows { get; set; }
        public int RiichiOpportunities { get; set; }
        public int RiichiAccepted { get; set; }
        public int MeldOpportunities { get; set; }
        public int MeldAccepted { get; set; }
        public int WinOpportunities { get; set; }
        public int WinAccepted { get; set; }
        public int TileMarkOpportunities { get; set; }
        public int TileMarkActivationsAccepted { get; set; }
        public int SleeveBloomOpportunities { get; set; }
        public int SleeveBloomActivationsAccepted { get; set; }
        public int BeamSwapOpportunities { get; set; }
        public int BeamSwapActivationsAccepted { get; set; }
        public int CheatSkips { get; set; }
        public int CheatOfferWindows { get; set; }
        public int ThirdCandidateAppearances { get; set; }
        public int SafeDefaultResolutions { get; set; }
        public int CheatRejected { get; set; }
        public int TimeoutDefaults { get; set; }
        public int StrategyFallbacks { get; set; }
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
        public string ProfileId { get; set; } = string.Empty;
        public string StrategyId { get; set; } = string.Empty;
        public long ControlEpoch { get; set; }
        public long RecoveryGeneration { get; set; }
        public string DecisionId { get; set; } = string.Empty;
        public string RequestId { get; set; } = string.Empty;
        public string OfferId { get; set; } = string.Empty;
        public string WindowId { get; set; } = string.Empty;
        public bool IsOfferChoice { get; set; }
        public bool HasThirdCandidate { get; set; }
        public string SafeDefaultOptionId { get; set; } = string.Empty;
        public int LegalOptionCount { get; set; }
        public string LegalCapabilities { get; set; } = string.Empty;
        public int? CheatPointsBefore { get; set; }
        public int? SpentThisRoundBefore { get; set; }
        public int? OptionCost { get; set; }
        public long? AuthoritativeStateVersionAfter { get; set; }
        public long? EventSeq { get; set; }
        public string ReceiptStatus { get; set; } = string.Empty;
        public bool Replayed { get; set; }
        public bool IsDefaultResolution { get; set; }
    }
}
