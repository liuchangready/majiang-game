namespace CheatAiBatchRunner;

/// <summary>Deterministic paired design; evidence is populated only from actual host records.</summary>
internal static class PairedRunPlan
{
    private const ulong GoldenRatio = 0x9E3779B97F4A7C15UL;
    private const ulong ShuffleDomainTag = 0x53485546464C4501UL;
    private const ulong StrategyBaseDomainTag = 0x41495F4241534500UL;
    private const ulong SeatStrategyDomainTag = 0x534541545F534545UL;

    internal sealed record RoundEvidence(string RoundId, int RotationIndex, ulong Seed, int TargetSeat,
        string Status, bool? Terminal, bool? Passed, long[]? FinalScores, int? RemainingRiichiSticks,
        int? WallAssignedCount, bool? WallConserved, string[]? ConservationErrors, string Error);
    internal sealed record Leg(string ProfileId, int TargetSeat, ulong Seed, string Status,
        string NotRunReason, string FirstFailure, RoundEvidence[] Rounds);
    internal sealed record Rotation(int RotationIndex, ulong Seed, Leg Candidate, Leg Control);
    internal sealed record Pair(string PairId, int PairIndex, ulong PairSeed, Rotation[] Rotations);
    internal sealed record Plan(string Status, ulong MasterSeed, int RequestedPairs,
        string CandidateProfileId, string ControlProfileId, Pair[] Pairs, string Boundary);

    internal static Plan Create(int pairCount, ulong masterSeed, string candidateProfile, string controlProfile)
    {
        return Create(pairCount, masterSeed, candidateProfile, controlProfile,
            "NOT_RUN: plan-only mode (--plan-pairs); execution requires --run-pairs.");
    }

    internal static Plan Create(int pairCount, ulong masterSeed, string candidateProfile, string controlProfile,
        string defaultNotRunReason)
    {
        if (pairCount < 1) throw new ArgumentOutOfRangeException(nameof(pairCount));
        if (string.IsNullOrWhiteSpace(candidateProfile)) throw new ArgumentException("Candidate profile is required.", nameof(candidateProfile));
        if (string.IsNullOrWhiteSpace(controlProfile)) throw new ArgumentException("Control profile is required.", nameof(controlProfile));
        if (string.IsNullOrWhiteSpace(defaultNotRunReason)) throw new ArgumentException("Default not-run reason is required.", nameof(defaultNotRunReason));

        Pair[] pairs = Enumerable.Range(1, pairCount).Select(index =>
        {
            ulong pairSeed = DerivePairSeed(masterSeed, index);
            Rotation[] rotations = Enumerable.Range(0, 4).Select(rotation =>
            {
                ulong seed = DeriveRotationSeed(pairSeed, rotation);
                return new Rotation(rotation, seed,
                    new Leg(candidateProfile, rotation, seed, "not-run", defaultNotRunReason, string.Empty,
                        Array.Empty<RoundEvidence>()),
                    new Leg(controlProfile, rotation, seed, "not-run", defaultNotRunReason, string.Empty,
                        Array.Empty<RoundEvidence>()));
            }).ToArray();
            return new Pair("pair-" + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture),
                index, pairSeed, rotations);
        }).ToArray();

        return new Plan("plan", masterSeed, pairCount, candidateProfile, controlProfile, pairs,
            "Deterministic paired four-seat rotation plan. Candidate and control legs share the rotation shuffle stream while shuffle and each seat strategy stream remain independent.");
    }

    internal static ulong DerivePairSeed(ulong masterSeed, int pairIndex)
    {
        if (pairIndex < 1) throw new ArgumentOutOfRangeException(nameof(pairIndex));
        return Mix(unchecked(masterSeed + (ulong)pairIndex * GoldenRatio));
    }

    internal static ulong DeriveRotationSeed(ulong pairSeed, int rotationIndex)
    {
        if (rotationIndex < 0 || rotationIndex > 3) throw new ArgumentOutOfRangeException(nameof(rotationIndex));
        return Mix(unchecked(pairSeed + (ulong)rotationIndex * GoldenRatio));
    }

    /// <summary>Independent wall/deal shuffle stream shared by candidate and control in the same rotation.</summary>
    internal static ulong DeriveShuffleSeed(ulong rotationSeed)
    {
        return Mix(unchecked(rotationSeed ^ ShuffleDomainTag));
    }

    /// <summary>Independent base seed for AI strategy streams (never shared with shuffle).</summary>
    internal static ulong DeriveStrategyBaseSeed(ulong rotationSeed)
    {
        return Mix(unchecked(rotationSeed ^ StrategyBaseDomainTag));
    }

    /// <summary>Independent per-seat strategy seed for seat 0..3 within a rotation.</summary>
    internal static ulong DeriveSeatStrategySeed(ulong rotationSeed, int seat)
    {
        if (seat < 0 || seat > 3) throw new ArgumentOutOfRangeException(nameof(seat));
        return Mix(unchecked(rotationSeed + (ulong)(seat + 1) * GoldenRatio) ^ SeatStrategyDomainTag);
    }

    private static ulong Mix(ulong value)
    {
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }
}
