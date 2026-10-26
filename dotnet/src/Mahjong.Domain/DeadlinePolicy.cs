using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Mahjong.Domain
{
    public enum LegalActionKind : byte
    {
        Pass = 0,
        Discard = 1,
        Tsumo = 2,
        Ron = 3,
        Chi = 4,
        Pon = 5,
        Kan = 6,
        Riichi = 7,
        Cheat = 8,
        Other = 9
    }

    public sealed class LegalActionOption
    {
        public LegalActionOption(LegalActionKind kind, int seat, TileId? tile = null, string? optionId = null)
        {
            V5Guard.CheckSeat(seat);
            Kind = kind;
            Seat = seat;
            Tile = tile;
            OptionId = optionId;
        }

        public LegalActionKind Kind { get; }

        public int Seat { get; }

        public TileId? Tile { get; }

        public string? OptionId { get; }

        internal bool Matches(LegalActionOption other)
        {
            return Kind == other.Kind &&
                   Seat == other.Seat &&
                   Nullable.Equals(Tile, other.Tile) &&
                   string.Equals(OptionId, other.OptionId, StringComparison.Ordinal);
        }

        public override string ToString() => Kind + "/seat" + Seat + (Tile.HasValue ? "/" + Tile.Value : string.Empty);
    }

    /// <summary>A decision that is currently open for one seat, including its legal candidates.</summary>
    public sealed class PendingDecision
    {
        private readonly ReadOnlyCollection<LegalActionOption> _candidates;

        public PendingDecision(
            DecisionKey key,
            long controlEpoch,
            IReadOnlyList<LegalActionOption> candidates,
            TileId? drawnTile = null)
        {
            if (controlEpoch < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(controlEpoch), controlEpoch, "controlEpoch must be non-negative.");
            }

            if (candidates == null)
            {
                throw new ArgumentNullException(nameof(candidates));
            }

            var copy = new List<LegalActionOption>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++)
            {
                LegalActionOption candidate = candidates[i] ??
                    throw new ArgumentException("Candidate cannot be null.", nameof(candidates));
                if (candidate.Seat != key.Seat)
                {
                    throw new ArgumentException("Candidate seat must match the decision seat.", nameof(candidates));
                }

                copy.Add(candidate);
            }

            Key = key;
            ControlEpoch = controlEpoch;
            _candidates = new ReadOnlyCollection<LegalActionOption>(copy);
            DrawnTile = drawnTile;
        }

        public DecisionKey Key { get; }

        public long ControlEpoch { get; }

        public IReadOnlyList<LegalActionOption> Candidates => _candidates;

        public TileId? DrawnTile { get; }

        public bool Contains(LegalActionOption action)
        {
            for (int i = 0; i < _candidates.Count; i++)
            {
                if (_candidates[i].Matches(action))
                {
                    return true;
                }
            }

            return false;
        }
    }

    public sealed class AiDecisionSubmission
    {
        public AiDecisionSubmission(DecisionKey key, long controlEpoch, LegalActionOption action, long submittedAtMillis)
        {
            if (controlEpoch < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(controlEpoch), controlEpoch, "controlEpoch must be non-negative.");
            }

            if (submittedAtMillis < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(submittedAtMillis), submittedAtMillis, "Time must be non-negative.");
            }

            Key = key;
            ControlEpoch = controlEpoch;
            Action = action ?? throw new ArgumentNullException(nameof(action));
            SubmittedAtMillis = submittedAtMillis;
        }

        public DecisionKey Key { get; }

        public long ControlEpoch { get; }

        public LegalActionOption Action { get; }

        public long SubmittedAtMillis { get; }
    }

    public sealed class TickInput
    {
        public TickInput(
            string roundId,
            long nowMillis,
            IReadOnlyList<PendingDecision>? pendingDecisions = null,
            IReadOnlyList<AiDecisionSubmission>? aiSubmissions = null)
        {
            if (string.IsNullOrWhiteSpace(roundId))
            {
                throw new ArgumentException("roundId cannot be blank.", nameof(roundId));
            }

            if (nowMillis < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(nowMillis), nowMillis, "Time must be non-negative.");
            }

            RoundId = roundId;
            NowMillis = nowMillis;
            PendingDecisions = pendingDecisions == null
                ? new ReadOnlyCollection<PendingDecision>(new List<PendingDecision>())
                : new ReadOnlyCollection<PendingDecision>(new List<PendingDecision>(pendingDecisions));
            AiSubmissions = aiSubmissions == null
                ? new ReadOnlyCollection<AiDecisionSubmission>(new List<AiDecisionSubmission>())
                : new ReadOnlyCollection<AiDecisionSubmission>(new List<AiDecisionSubmission>(aiSubmissions));
        }

        public string RoundId { get; }

        public long NowMillis { get; }

        public IReadOnlyList<PendingDecision> PendingDecisions { get; }

        public IReadOnlyList<AiDecisionSubmission> AiSubmissions { get; }
    }

    public enum TickEntryKind : byte
    {
        Timeout = 0,
        AiAccepted = 1,
        AiRejected = 2
    }

    public enum TickRejectionCode : byte
    {
        None = 0,
        StaleRound = 1,
        StaleEpoch = 2,
        StaleDecision = 3,
        DeadlineExpired = 4,
        IllegalAction = 5,
        SeatNotAutomated = 6,
        NoDefaultAction = 7,
        InvalidSubmission = 8,
        ClockPaused = 9,
        StaleRecoveryGeneration = 10
    }

    /// <summary>
    /// One authoritative tick decision. Timeout entries always precede AI entries in
    /// <see cref="TickResult.Entries"/>; that ordering is the contract, not an accident.
    /// </summary>
    public sealed class TickEntry
    {
        internal TickEntry(
            TickEntryKind kind,
            int seat,
            string decisionId,
            LegalActionOption? action,
            TickRejectionCode rejectionCode,
            string? detail)
        {
            Kind = kind;
            Seat = seat;
            DecisionId = decisionId;
            Action = action;
            RejectionCode = rejectionCode;
            Detail = detail;
        }

        public TickEntryKind Kind { get; }

        public int Seat { get; }

        public string DecisionId { get; }

        public LegalActionOption? Action { get; }

        public TickRejectionCode RejectionCode { get; }

        public string? Detail { get; }

        public override string ToString() =>
            Kind + "/seat" + Seat + "/" + DecisionId + "/" +
            (Action == null ? "no-action" : Action.ToString()) + "/" + RejectionCode;
    }

    public sealed class TickResult
    {
        private readonly ReadOnlyCollection<TickEntry> _entries;

        internal TickResult(IReadOnlyList<TickEntry> entries)
        {
            _entries = new ReadOnlyCollection<TickEntry>(new List<TickEntry>(entries));
        }

        public IReadOnlyList<TickEntry> Entries => _entries;

        public IReadOnlyList<TickEntry> Timeouts => Filter(TickEntryKind.Timeout);

        public IReadOnlyList<TickEntry> AcceptedAi => Filter(TickEntryKind.AiAccepted);

        public IReadOnlyList<TickEntry> RejectedAi => Filter(TickEntryKind.AiRejected);

        private IReadOnlyList<TickEntry> Filter(TickEntryKind kind)
        {
            var list = new List<TickEntry>();
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Kind == kind)
                {
                    list.Add(_entries[i]);
                }
            }

            return new ReadOnlyCollection<TickEntry>(list);
        }
    }

    /// <summary>
    /// V4 timeout fallback. Never auto-wins, auto-kans, auto-riichis or auto-cheats:
    /// Pass &gt; tsumogiri &gt; lowest legal discard &gt; null.
    /// </summary>
    public static class TimeoutPolicy
    {
        public static LegalActionOption? Choose(PendingDecision decision)
        {
            if (decision == null)
            {
                throw new ArgumentNullException(nameof(decision));
            }

            LegalActionOption? pass = null;
            LegalActionOption? tsumogiri = null;
            LegalActionOption? lowestDiscard = null;

            for (int i = 0; i < decision.Candidates.Count; i++)
            {
                LegalActionOption candidate = decision.Candidates[i];
                if (candidate.Kind == LegalActionKind.Pass && pass == null)
                {
                    pass = candidate;
                    continue;
                }

                if (candidate.Kind == LegalActionKind.Discard && candidate.Tile.HasValue)
                {
                    if (decision.DrawnTile.HasValue && candidate.Tile.Value == decision.DrawnTile.Value)
                    {
                        if (tsumogiri == null)
                        {
                            tsumogiri = candidate;
                        }
                    }

                    if (lowestDiscard == null ||
                        candidate.Tile.Value.Value < lowestDiscard.Tile!.Value.Value)
                    {
                        lowestDiscard = candidate;
                    }
                }
            }

            if (pass != null)
            {
                return pass;
            }

            if (tsumogiri != null)
            {
                return tsumogiri;
            }

            return lowestDiscard;
        }
    }

    /// <summary>
    /// Processes one authoritative tick in a fixed order: (1) every due deadline is expired
    /// and its timeout action committed, (2) only then are same-tick AI results validated and
    /// applied. Therefore an AI result for a decision whose deadline has already passed loses
    /// to the timeout even when both are presented in the same tick.
    /// </summary>
    public sealed class DeadlineArbiter
    {
        private readonly DecisionClock _clock;
        private readonly ControlRegistry _control;

        public DeadlineArbiter(DecisionClock clock, ControlRegistry control)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _control = control ?? throw new ArgumentNullException(nameof(control));
        }

        public TickResult ProcessTick(TickInput input)
        {
            if (input == null)
            {
                throw new ArgumentNullException(nameof(input));
            }

            var entries = new List<TickEntry>();
            var pendingBySeat = new PendingDecision?[4];
            for (int i = 0; i < input.PendingDecisions.Count; i++)
            {
                PendingDecision pending = input.PendingDecisions[i] ??
                    throw new ArgumentException("Pending decision cannot be null.", nameof(input));
                if (pendingBySeat[pending.Key.Seat] != null)
                {
                    throw new ArgumentException(
                        "Seat " + pending.Key.Seat + " has more than one pending decision.",
                        nameof(input));
                }

                pendingBySeat[pending.Key.Seat] = pending;
            }

            var expiredSeats = new bool[4];
            bool clockForThisRound =
                string.Equals(_clock.ActiveRoundId, input.RoundId, StringComparison.Ordinal);

            // Phase 1: authoritative deadlines. These entries are appended first by design.
            if (clockForThisRound && !_clock.IsPausedState)
            {
                for (int seat = 0; seat < 4; seat++)
                {
                    DecisionTiming? timing = _clock.Timing(seat);
                    if (timing == null ||
                        !string.Equals(timing.RoundId, input.RoundId, StringComparison.Ordinal) ||
                        input.NowMillis < timing.DeadlineMillis)
                    {
                        continue;
                    }

                    PendingDecision? pending = pendingBySeat[seat];
                    LegalActionOption? fallback = pending == null ? null : TimeoutPolicy.Choose(pending);
                    bool expired = _clock.Expire(timing.Key, timing.Generation, input.NowMillis);
                    if (!expired)
                    {
                        continue;
                    }

                    expiredSeats[seat] = true;
                    entries.Add(new TickEntry(
                        TickEntryKind.Timeout,
                        seat,
                        timing.DecisionId,
                        fallback,
                        fallback == null ? TickRejectionCode.NoDefaultAction : TickRejectionCode.None,
                        fallback == null
                            ? "No legal timeout default; decision expired without an action."
                            : "Authoritative deadline reached; timeout action committed."));
                }
            }

            // Phase 2: same-tick AI results, in submission order.
            for (int i = 0; i < input.AiSubmissions.Count; i++)
            {
                AiDecisionSubmission submission = input.AiSubmissions[i] ??
                    throw new ArgumentException("AI submission cannot be null.", nameof(input));
                TickEntry rejection = ValidateAi(submission, input, pendingBySeat, expiredSeats, clockForThisRound);
                if (rejection.Kind == TickEntryKind.AiRejected)
                {
                    entries.Add(rejection);
                    continue;
                }

                DecisionTiming? timing = _clock.Timing(submission.Key.Seat);
                if (timing == null ||
                    timing.Key != submission.Key ||
                    !_clock.Complete(submission.Key, input.NowMillis))
                {
                    entries.Add(new TickEntry(
                        TickEntryKind.AiRejected,
                        submission.Key.Seat,
                        submission.Key.DecisionId,
                        submission.Action,
                        TickRejectionCode.StaleDecision,
                        "Decision closed before the AI submission could be applied."));
                    continue;
                }

                entries.Add(new TickEntry(
                    TickEntryKind.AiAccepted,
                    submission.Key.Seat,
                    submission.Key.DecisionId,
                    submission.Action,
                    TickRejectionCode.None,
                    "AI result accepted before its deadline."));
            }

            return new TickResult(entries);
        }

        private TickEntry ValidateAi(
            AiDecisionSubmission submission,
            TickInput input,
            PendingDecision?[] pendingBySeat,
            bool[] expiredSeats,
            bool clockForThisRound)
        {
            int seat = submission.Key.Seat;
            if (!string.Equals(submission.Key.RoundId, input.RoundId, StringComparison.Ordinal) ||
                !clockForThisRound)
            {
                return Reject(submission, TickRejectionCode.StaleRound, "Submission belongs to another round.");
            }

            ControlValidation control = _control.ValidateAutomatedAction(seat, submission.ControlEpoch);
            if (!control.IsValid)
            {
                TickRejectionCode code = control.Code == ControlValidationCode.EpochMismatch
                    ? TickRejectionCode.StaleEpoch
                    : TickRejectionCode.SeatNotAutomated;
                return Reject(submission, code, control.Message);
            }

            if (_clock.IsPausedState)
            {
                return Reject(submission, TickRejectionCode.ClockPaused, "Clock is paused.");
            }

            if (expiredSeats[seat])
            {
                return Reject(
                    submission,
                    TickRejectionCode.DeadlineExpired,
                    "The seat's deadline was already expired earlier in this tick.");
            }

            DecisionTiming? timing = _clock.Timing(seat);
            if (timing == null || timing.Key != submission.Key)
            {
                return Reject(submission, TickRejectionCode.StaleDecision, "No matching active decision.");
            }

            if (input.NowMillis >= timing.DeadlineMillis)
            {
                return Reject(
                    submission,
                    TickRejectionCode.DeadlineExpired,
                    "Submission arrived at or after the authoritative deadline.");
            }

            PendingDecision? pending = pendingBySeat[seat];
            if (pending == null ||
                pending.Key != submission.Key ||
                !pending.Contains(submission.Action))
            {
                return Reject(submission, TickRejectionCode.IllegalAction, "Submitted action is not a legal candidate.");
            }

            return new TickEntry(
                TickEntryKind.AiAccepted,
                seat,
                submission.Key.DecisionId,
                submission.Action,
                TickRejectionCode.None,
                null);
        }

        private static TickEntry Reject(
            AiDecisionSubmission submission,
            TickRejectionCode code,
            string detail)
        {
            return new TickEntry(
                TickEntryKind.AiRejected,
                submission.Key.Seat,
                submission.Key.DecisionId,
                submission.Action,
                code,
                detail);
        }
    }
}
