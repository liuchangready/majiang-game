namespace CheatAiBatchRunner;

/// <summary>Validated paired outcomes and paired Student-t interval.</summary>
internal static class PairedStatistics
{
    internal sealed record Failure(string PairId, int PairIndex, int RotationIndex, string Leg,
        string RoundId, string Detail);
    internal sealed record PairOutcome(string PairId, string Status, long? CandidateScore,
        long? ControlScore, long? NetDifference, int CompleteRounds, int FailedRounds,
        int TimedOutRounds, int NotRunRounds, Failure? FirstFailure);
    internal sealed record Summary(int RequestedPairs, int CompletePairs, int FailedPairs,
        int TimedOutPairs, int NotRunPairs, int PartialPairs, int CompleteRounds, int FailedRounds,
        int TimedOutRounds, int NotRunRounds, Failure? FirstFailure, double? MeanDifference,
        double? Lower95, double? Upper95, string Method, PairOutcome[] Pairs);

    private sealed record LegOutcome(string Status, long Score, Failure? Failure);

    /// <summary>Production entry: all outcomes derive from the planned legs and their round records.</summary>
    internal static Summary Calculate(PairedRunPlan.Plan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Status != "plan" || plan.RequestedPairs < 1 || string.IsNullOrWhiteSpace(plan.CandidateProfileId) ||
            string.IsNullOrWhiteSpace(plan.ControlProfileId) || plan.Pairs == null ||
            plan.Pairs.Length != plan.RequestedPairs)
            throw new ArgumentException("Plan status or requested pair count is inconsistent.", nameof(plan));

        var pairIds = new HashSet<string>(StringComparer.Ordinal);
        var roundIds = new HashSet<string>(StringComparer.Ordinal);
        var outcomes = new List<PairOutcome>(plan.Pairs.Length);
        foreach (PairedRunPlan.Pair pair in plan.Pairs.OrderBy(item => item.PairIndex))
        {
            ValidatePairPlan(plan, pair, pairIds);
            outcomes.Add(ValidatePairEvidence(plan, pair, roundIds));
        }

        PairOutcome[] complete = outcomes.Where(pair => pair.Status == "complete").ToArray();
        double[] differences = complete.Select(pair =>
            (double)pair.CandidateScore!.Value - pair.ControlScore!.Value).ToArray();
        (double? mean, double? lower, double? upper, string method) = Interval(differences);
        Failure? firstFailure = outcomes.Select(pair => pair.FirstFailure).FirstOrDefault(failure => failure != null);
        return new Summary(plan.RequestedPairs,
            complete.Length,
            outcomes.Count(pair => pair.Status == "failed"),
            outcomes.Count(pair => pair.Status == "timeout"),
            outcomes.Count(pair => pair.Status == "not-run"),
            outcomes.Count(pair => pair.Status == "partial"),
            outcomes.Sum(pair => pair.CompleteRounds),
            outcomes.Sum(pair => pair.FailedRounds),
            outcomes.Sum(pair => pair.TimedOutRounds),
            outcomes.Sum(pair => pair.NotRunRounds),
            firstFailure, mean, lower, upper, method, outcomes.ToArray());
    }

    private static void ValidatePairPlan(PairedRunPlan.Plan plan, PairedRunPlan.Pair pair,
        HashSet<string> pairIds)
    {
        if (pair == null || string.IsNullOrWhiteSpace(pair.PairId) || !pairIds.Add(pair.PairId))
            throw new ArgumentException("Pair IDs must be non-empty and unique.", nameof(plan));
        if (pair.PairIndex < 1 || pair.PairIndex > plan.RequestedPairs ||
            pair.PairId != "pair-" + pair.PairIndex.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) ||
            pair.PairSeed != PairedRunPlan.DerivePairSeed(plan.MasterSeed, pair.PairIndex))
            throw new ArgumentException("Pair identity or deterministic seed does not match the plan.", nameof(plan));
        if (pair.Rotations == null || pair.Rotations.Length != 4 ||
            !pair.Rotations.Select(rotation => rotation.RotationIndex).OrderBy(index => index)
                .SequenceEqual(new[] { 0, 1, 2, 3 }))
            throw new ArgumentException("Each pair must contain exactly one rotation for each target seat 0..3.", nameof(plan));

        foreach (PairedRunPlan.Rotation rotation in pair.Rotations)
        {
            if (rotation.Seed != PairedRunPlan.DeriveRotationSeed(pair.PairSeed, rotation.RotationIndex) ||
                rotation.Candidate == null || rotation.Control == null ||
                rotation.Candidate.TargetSeat != rotation.RotationIndex ||
                rotation.Control.TargetSeat != rotation.RotationIndex ||
                rotation.Candidate.Seed != rotation.Seed || rotation.Control.Seed != rotation.Seed ||
                rotation.Candidate.ProfileId != plan.CandidateProfileId ||
                rotation.Control.ProfileId != plan.ControlProfileId ||
                string.IsNullOrWhiteSpace(rotation.Candidate.ProfileId) ||
                string.IsNullOrWhiteSpace(rotation.Control.ProfileId))
                throw new ArgumentException("Rotation seat, profile, or paired seed does not match the deterministic plan.", nameof(plan));
        }
    }

    private static PairOutcome ValidatePairEvidence(PairedRunPlan.Plan plan, PairedRunPlan.Pair pair,
        HashSet<string> roundIds)
    {
        var slots = new List<(int Rotation, string Leg, LegOutcome Outcome)>(8);
        foreach (PairedRunPlan.Rotation rotation in pair.Rotations.OrderBy(item => item.RotationIndex))
        {
            slots.Add((rotation.RotationIndex, "candidate",
                ValidateLeg(plan, pair, rotation, rotation.Candidate, "candidate", roundIds)));
            slots.Add((rotation.RotationIndex, "control",
                ValidateLeg(plan, pair, rotation, rotation.Control, "control", roundIds)));
        }

        int completed = slots.Count(slot => slot.Outcome.Status == "complete");
        int failed = slots.Count(slot => slot.Outcome.Status == "failed");
        int timedOut = slots.Count(slot => slot.Outcome.Status == "timeout");
        int notRun = slots.Count(slot => slot.Outcome.Status == "not-run");
        Failure? firstFailure = slots.Select(slot => slot.Outcome.Failure).FirstOrDefault(failure => failure != null);
        if (completed == 8)
        {
            long candidateScore = SumScores(slots.Where(slot => slot.Leg == "candidate"));
            long controlScore = SumScores(slots.Where(slot => slot.Leg == "control"));
            return new PairOutcome(pair.PairId, "complete", candidateScore, controlScore,
                checked(candidateScore - controlScore), 8, 0, 0, 0, null);
        }
        if (notRun == 8)
            return new PairOutcome(pair.PairId, "not-run", null, null, null, 0, 0, 0, 8, null);
        // Pair classification is deterministic: timeout takes precedence, then failure,
        // then a clean but incomplete partial batch. Counts retain every recorded leg.
        string status = timedOut > 0 ? "timeout" : failed > 0 ? "failed" : "partial";
        return new PairOutcome(pair.PairId, status, null, null, null, completed, failed, timedOut,
            notRun, firstFailure);
    }

    private static LegOutcome ValidateLeg(PairedRunPlan.Plan plan, PairedRunPlan.Pair pair,
        PairedRunPlan.Rotation rotation, PairedRunPlan.Leg leg, string legName, HashSet<string> roundIds)
    {
        if (leg.Rounds == null || leg.Status is not ("complete" or "failed" or "timeout" or "not-run"))
            throw new ArgumentException("Leg status or round evidence is missing/unsupported.", nameof(plan));
        if (leg.Status == "not-run")
        {
            if (leg.Rounds.Length != 0 || string.IsNullOrWhiteSpace(leg.NotRunReason) || leg.FirstFailure.Length != 0)
                throw new ArgumentException("Not-run legs need a reason and cannot contain round/failure evidence.", nameof(plan));
            return new LegOutcome("not-run", 0, null);
        }

        if (leg.Rounds.Length != 1 || !string.IsNullOrEmpty(leg.NotRunReason))
            throw new ArgumentException("An executed seat rotation must have exactly one round and no not-run reason.", nameof(plan));
        PairedRunPlan.RoundEvidence evidence = leg.Rounds[0];
        if (string.IsNullOrWhiteSpace(evidence.RoundId) || !roundIds.Add(evidence.RoundId) ||
            evidence.RotationIndex != rotation.RotationIndex || evidence.Seed != rotation.Seed ||
            evidence.TargetSeat != leg.TargetSeat)
            throw new ArgumentException("Round evidence has a duplicate ID or mismatched rotation, seed, or target seat.", nameof(plan));

        if (leg.Status == "complete")
        {
            if (leg.FirstFailure.Length != 0 || evidence.Status != "complete")
                throw new ArgumentException("Complete leg cannot carry failure status/detail.", nameof(plan));
            ValidateCompleteRound(evidence, plan);
            return new LegOutcome("complete", evidence.FinalScores![leg.TargetSeat], null);
        }

        if (evidence.Status != leg.Status || string.IsNullOrWhiteSpace(evidence.Error) ||
            leg.FirstFailure != evidence.Error)
            throw new ArgumentException("Failed/timeout leg must retain the exact non-empty first failing round detail.", nameof(plan));
        return new LegOutcome(leg.Status, 0,
            new Failure(pair.PairId, pair.PairIndex, rotation.RotationIndex, legName,
                evidence.RoundId, evidence.Error));
    }

    private static void ValidateCompleteRound(PairedRunPlan.RoundEvidence evidence, PairedRunPlan.Plan plan)
    {
        if (evidence.Terminal != true || evidence.Passed != true || evidence.FinalScores == null ||
            evidence.FinalScores.Length != 4 || evidence.TargetSeat is < 0 or > 3 ||
            evidence.RemainingRiichiSticks is null or < 0 || evidence.WallAssignedCount != 136 ||
            evidence.WallConserved != true || evidence.ConservationErrors == null ||
            evidence.ConservationErrors.Length != 0 || !string.IsNullOrEmpty(evidence.Error))
            throw new ArgumentException("Complete round lacks terminal, score, 136-tile, or conservation evidence.", nameof(plan));

        decimal scoreTotal = evidence.FinalScores.Sum(score => (decimal)score);
        if (scoreTotal + (decimal)evidence.RemainingRiichiSticks.Value * 1000m != 100_000m)
            throw new ArgumentException("Complete round violates authoritative point conservation.", nameof(plan));
    }

    private static long SumScores(IEnumerable<(int Rotation, string Leg, LegOutcome Outcome)> slots)
    {
        long sum = 0;
        foreach (var slot in slots) sum = checked(sum + slot.Outcome.Score);
        return sum;
    }

    private static (double? Mean, double? Lower, double? Upper, string Method) Interval(double[] values)
    {
        if (values.Length < 2)
            return (null, null, null, "paired Student-t interval; unavailable with fewer than 2 complete pairs");
        double mean = values.Average();
        double variance = values.Sum(value => (value - mean) * (value - mean)) / (values.Length - 1);
        double half = T95Critical(values.Length - 1) * Math.Sqrt(variance / values.Length);
        return (mean, mean - half, mean + half,
            "two-sided paired Student-t 95% CI; complete evidence-backed pair is unit; sample SD with n-1; exact tabulated t critical for df<=30, Cornish-Fisher approximation above df=30");
    }

    internal static double T95Critical(int df)
    {
        if (df < 1) throw new ArgumentOutOfRangeException(nameof(df), "Degrees of freedom must be positive.");
        ReadOnlySpan<double> exact =
        [
            0d, 12.7062047364, 4.30265272975, 3.18244630528, 2.77644510520,
            2.57058183564, 2.44691185114, 2.36462425101, 2.30600413520, 2.26215716285,
            2.22813885196, 2.20098516008, 2.17881282966, 2.16036865646, 2.14478668792,
            2.13144954556, 2.11990529922, 2.10981557783, 2.10092204024, 2.09302405441,
            2.08596344727, 2.07961384473, 2.07387306790, 2.06865761042, 2.06389856163,
            2.05953855275, 2.05552943864, 2.05183051648, 2.04840714180, 2.04522964213,
            2.04227245630,
        ];
        if (df < exact.Length) return exact[df];
        double z = 1.959963984540054;
        double v = df;
        double z2 = z * z;
        return z + (z * (z2 + 1)) / (4 * v) +
            (z * (5 * z2 * z2 + 16 * z2 + 3)) / (96 * v * v) +
            (z * (3 * z2 * z2 * z2 + 19 * z2 * z2 + 17 * z2 - 15)) / (384 * v * v * v);
    }
}
