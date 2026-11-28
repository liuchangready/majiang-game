using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Mahjong.Domain.Rules;
using Mahjong.TableRuntime;
using Mahjong.Ai;

namespace Mahjong.LocalHost
{
    /// <summary>
    /// Local AI/deadline driver. All reasoning happens outside the actor over the immutable
    /// per-seat observation; only a completed decision is admitted back for re-validation.
    /// Draws are direct authoritative commands because a draw has no choice.
    /// </summary>
    internal sealed class LocalTableDriver
    {
        private readonly TableActor _actor;
        private readonly LocalTableHostOptions _options;
        private readonly LocalTableStore _store;
        private readonly RequestScopeId[] _scopes;
        private readonly Func<bool> _isActive;
        private readonly bool[] _temporaryFuriten;
        private readonly bool[] _riichiFuriten;
        private readonly Func<int, bool>? _canOpenHumanDecision;
        private readonly SeatAiBinding[] _seatBindings = new SeatAiBinding[4];
        private readonly SeatAiBinding[] _takeoverBindings = new SeatAiBinding[4];
        private readonly bool[] _fallbackLogged = new bool[4];
        private readonly bool[] _takeoverFallbackLogged = new bool[4];
        private readonly ILocalAiDecisionObserver? _aiObserver;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly long[] _controlEpochs = new long[4];
        private readonly bool[] _aiSeats = new bool[4];
        private readonly bool[] _takeoverSeats = new bool[4];
        private readonly string[] _lastDecisionKeys = new string[4];
        private readonly object _iterationGateForTest = new object();
        private TaskCompletionSource<long> _iterationCompletedForTest = NewIterationSignal();
        private long _completedIterationsForTest;
        private long _requestCounter;
        private Task? _task;
        private string _lastLoopErrorForTest = string.Empty;
        private int _lastSeatForTest = -1;
        private string _lastPhaseForTest = string.Empty;
        private int _lastCandidateCountForTest = -1;
        private bool _lastAiForTest;
        private string _lastOpenResultForTest = string.Empty;

        /// <summary>Test-only gate after an AI decision is chosen and before it is submitted.</summary>
        internal Func<int, Mahjong.Ai.AiObservation, Task>? BeforeAiSubmitForTest { get; set; }

        /// <summary>Test-only gate after an AI cheat window is captured and before strategy scoring/submission runs.</summary>
        internal Func<int, TableAiCheatOpportunity, CancellationToken, Task>? BeforeAiCheatScoreOrSubmitForTest { get; set; }

        /// <summary>Opt-in evidence hook for a real AI choice, before its normal authority submission.</summary>
        internal Action<int, Mahjong.Ai.AiObservation, LegalActionOption>? AiDecisionChosenForTest { get; set; }

        /// <summary>Test-only callback with the authoritative result of a captured AI submission.</summary>
        internal Action<int, Mahjong.Ai.AiObservation, TableOperationResult>? AiSubmitCompletedForTest { get; set; }

        /// <summary>Test-only strategy override for driving exact legal candidate choices through LocalTableDriver.</summary>
        internal IAiStrategy? StrategyForTest { get; set; }

        /// <summary>Test-only accessor for the host-issued request scope bound to a seat.</summary>
        internal RequestScopeId ScopeForSeatForTest(int seat) => _scopes[seat];

        public LocalTableDriver(
            TableActor actor,
            LocalTableHostOptions options,
            LocalTableStore store,
            RequestScopeId[] scopes,
            Func<bool> isActive,
            bool[] temporaryFuriten,
            bool[] riichiFuriten,
            Func<int, bool>? canOpenHumanDecision = null)
        {
            _actor = actor;
            _options = options;
            _store = store;
            _scopes = scopes;
            _isActive = isActive;
            _temporaryFuriten = temporaryFuriten;
            _riichiFuriten = riichiFuriten;
            _canOpenHumanDecision = canOpenHumanDecision;
            LocalAiOptions ai = options.Ai ?? new LocalAiOptions();
            _aiObserver = options.AiDecisionObserver;
            CharacterAiProfile[] seatProfiles = ai.ResolveSeatProfiles();
            string fallbackStrategyId = ai.ResolveFallbackProfileId();
            for (int i = 0; i < 4; i++)
            {
                CharacterAiProfile seatProfile = seatProfiles[i];
                _seatBindings[i] = BuildSeatBinding(
                    seatProfile,
                    ai.Seed,
                    fallbackStrategyId,
                    seedSalt: i);

                CharacterAiProfile takeoverProfile;
                if (ai.TakeoverProfile != null)
                {
                    takeoverProfile = ai.TakeoverProfile;
                }
                else
                {
                    bool allowTakeoverSpend = seatProfile.CheatPolicy.Enabled &&
                                              seatProfile.CheatPolicy.TakeoverMaySpend;
                    ulong takeoverSeed = LocalAiOptions.DeriveSeatSeed(seatProfile.Seed ?? ai.Seed, i + 4);
                    AiCheatPolicy takeoverPolicy = allowTakeoverSpend
                        ? seatProfile.CheatPolicy
                        : new AiCheatPolicy(
                            enabled: false,
                            maxSpendPerRound: seatProfile.CheatPolicy.MaxSpendPerRound,
                            reservePoints: seatProfile.CheatPolicy.ReservePoints,
                            riskTolerance: seatProfile.CheatPolicy.RiskTolerance,
                            candidateLimit: seatProfile.CheatPolicy.CandidateLimit,
                            takeoverMaySpend: false,
                            capabilityToggles: seatProfile.CheatPolicy.CapabilityToggles);
                    // Preserve the seat's configured ordinary strategy (e.g. legacy-heuristic or baseline-basic)
                    // when no explicit TakeoverProfile is supplied, while enforcing non-spending takeover defaults
                    // and an isolated takeover seed.
                    takeoverProfile = new CharacterAiProfile(
                        seatProfile.ProfileId,
                        seatProfile.StrategyId,
                        seatProfile.Style,
                        takeoverPolicy,
                        seatProfile.ModelVersion,
                        seatProfile.FallbackStrategyId,
                        takeoverSeed);
                }

                _takeoverBindings[i] = BuildSeatBinding(
                    takeoverProfile,
                    ai.Seed,
                    fallbackStrategyId,
                    seedSalt: i + 4);
                _lastDecisionKeys[i] = string.Empty;
            }
        }

        internal string DescribeSeatBindingsForTest()
        {
            var parts = new string[4];
            for (int seat = 0; seat < 4; seat++)
            {
                SeatAiBinding b = _seatBindings[seat];
                SeatAiBinding t = _takeoverBindings[seat];
                parts[seat] = "s" + seat +
                    ":profile=" + b.Profile.ProfileId +
                    ",strategy=" + b.EffectiveStrategyId +
                    ",degraded=" + b.IsDegradedFallback +
                    ",cheats=" + b.EffectiveCheatPolicy.Enabled +
                    ",takeoverStrategy=" + t.EffectiveStrategyId +
                    ",takeoverSpend=" + t.EffectiveCheatPolicy.TakeoverMaySpend;
            }

            return string.Join(";", parts);
        }

        private SeatAiBinding GetActiveBinding(int seat) =>
            _takeoverSeats[seat] ? _takeoverBindings[seat] : _seatBindings[seat];

        private static SeatAiBinding BuildSeatBinding(
            CharacterAiProfile profile,
            ulong baseSeed,
            string hostFallbackStrategyId,
            int seedSalt)
        {
            ulong seatSeed = profile.Seed ?? LocalAiOptions.DeriveSeatSeed(baseSeed, seedSalt);
            string? requestedStrategy = profile.StrategyId;
            bool strategyKnown = !string.IsNullOrWhiteSpace(requestedStrategy) &&
                                 AiProfileIds.IsBuiltIn(requestedStrategy);
            bool modelCompatible = AiProfileIds.IsCompatibleHeuristicModelVersion(profile.ModelVersion);

            if (!strategyKnown || !modelCompatible)
            {
                string fallbackId = !string.IsNullOrWhiteSpace(profile.FallbackStrategyId) &&
                                    (string.Equals(profile.FallbackStrategyId, LocalAiProfiles.Basic, StringComparison.Ordinal) ||
                                     string.Equals(profile.FallbackStrategyId, LocalAiProfiles.LegacyHeuristic, StringComparison.Ordinal))
                    ? profile.FallbackStrategyId!
                    : hostFallbackStrategyId;
                IAiStrategy? fallbackOrdinary = string.Equals(fallbackId, LocalAiProfiles.LegacyHeuristic, StringComparison.Ordinal)
                    ? null
                    : new BasicAiStrategy();
                var disabledPolicy = new AiCheatPolicy(
                    enabled: false,
                    maxSpendPerRound: profile.CheatPolicy.MaxSpendPerRound,
                    reservePoints: profile.CheatPolicy.ReservePoints,
                    riskTolerance: profile.CheatPolicy.RiskTolerance,
                    candidateLimit: profile.CheatPolicy.CandidateLimit,
                    takeoverMaySpend: false,
                    capabilityToggles: profile.CheatPolicy.CapabilityToggles);
                string reason = !strategyKnown ? "STRATEGY_UNAVAILABLE" : "MODEL_VERSION_INCOMPATIBLE";
                return new SeatAiBinding(
                    profile,
                    fallbackOrdinary,
                    randomCheatStrategy: null,
                    scoredCheatStrategy: null,
                    disabledPolicy,
                    fallbackId,
                    isDegradedFallback: true,
                    fallbackReason: reason);
            }

            if (string.Equals(requestedStrategy, LocalAiProfiles.ScoredCheatBasic, StringComparison.Ordinal))
            {
                var scored = new ScoredCheatBasicAiStrategy(profile);
                return new SeatAiBinding(
                    profile,
                    scored,
                    randomCheatStrategy: null,
                    scoredCheatStrategy: scored,
                    profile.CheatPolicy,
                    LocalAiProfiles.ScoredCheatBasic,
                    isDegradedFallback: false,
                    fallbackReason: string.Empty);
            }

            if (string.Equals(requestedStrategy, LocalAiProfiles.RandomCheatBasic, StringComparison.Ordinal))
            {
                var randomStrategy = new RandomCheatBasicAiStrategy(
                    new SeededAiRandom(seatSeed),
                    profile.CheatPolicy);
                return new SeatAiBinding(
                    profile,
                    randomStrategy,
                    randomCheatStrategy: randomStrategy,
                    scoredCheatStrategy: null,
                    profile.CheatPolicy,
                    LocalAiProfiles.RandomCheatBasic,
                    isDegradedFallback: false,
                    fallbackReason: string.Empty);
            }

            if (string.Equals(requestedStrategy, LocalAiProfiles.Basic, StringComparison.Ordinal))
            {
                return new SeatAiBinding(
                    profile,
                    new BasicAiStrategy(),
                    randomCheatStrategy: null,
                    scoredCheatStrategy: null,
                    profile.CheatPolicy,
                    LocalAiProfiles.Basic,
                    isDegradedFallback: false,
                    fallbackReason: string.Empty);
            }

            return new SeatAiBinding(
                profile,
                ordinaryStrategy: null,
                randomCheatStrategy: null,
                scoredCheatStrategy: null,
                profile.CheatPolicy,
                LocalAiProfiles.LegacyHeuristic,
                isDegradedFallback: false,
                fallbackReason: string.Empty);
        }

        private sealed class SeatAiBinding
        {
            public SeatAiBinding(
                CharacterAiProfile profile,
                IAiStrategy? ordinaryStrategy,
                RandomCheatBasicAiStrategy? randomCheatStrategy,
                ScoredCheatBasicAiStrategy? scoredCheatStrategy,
                AiCheatPolicy effectiveCheatPolicy,
                string effectiveStrategyId,
                bool isDegradedFallback,
                string fallbackReason)
            {
                Profile = profile;
                OrdinaryStrategy = ordinaryStrategy;
                RandomCheatStrategy = randomCheatStrategy;
                ScoredCheatStrategy = scoredCheatStrategy;
                EffectiveCheatPolicy = effectiveCheatPolicy;
                EffectiveStrategyId = effectiveStrategyId;
                IsDegradedFallback = isDegradedFallback;
                FallbackReason = fallbackReason;
            }

            public CharacterAiProfile Profile { get; }
            public IAiStrategy? OrdinaryStrategy { get; }
            public RandomCheatBasicAiStrategy? RandomCheatStrategy { get; }
            public ScoredCheatBasicAiStrategy? ScoredCheatStrategy { get; }
            public AiCheatPolicy EffectiveCheatPolicy { get; }
            public string EffectiveStrategyId { get; }
            public bool IsDegradedFallback { get; }
            public string FallbackReason { get; }
            public bool HasCheatStrategy => RandomCheatStrategy != null || ScoredCheatStrategy != null;

            public void RestoreRoundSpend(string roundId, int seat, int spentPoints)
            {
                RandomCheatStrategy?.RestoreRoundSpend(roundId, seat, spentPoints);
                ScoredCheatStrategy?.RestoreRoundSpend(roundId, seat, spentPoints);
            }
        }

        public void Start()
        {
            if (_task != null)
            {
                return;
            }

            _task = Task.Run(DriveAsync);
        }

        public bool IsRunning => _task != null && !_task.IsCompleted;

        internal long CompletedIterationsForTest
        {
            get
            {
                lock (_iterationGateForTest)
                {
                    return _completedIterationsForTest;
                }
            }
        }

        internal Task<bool> WaitForIterationAfterForTest(long completedIterations, CancellationToken cancellationToken)
        {
            Task<long> changed;
            lock (_iterationGateForTest)
            {
                if (_completedIterationsForTest > completedIterations)
                {
                    return Task.FromResult(true);
                }

                changed = _iterationCompletedForTest.Task;
            }

            return WaitForIterationAsync(changed, cancellationToken);
        }

        public string DescribeDriverForTest()
        {
            return "running=" + IsRunning +
                   ";lastError=" + (_lastLoopErrorForTest.Length == 0 ? "-" : _lastLoopErrorForTest) +
                   ";lastSeat=" + _lastSeatForTest +
                   ";lastPhase=" + (_lastPhaseForTest.Length == 0 ? "-" : _lastPhaseForTest) +
                   ";lastAi=" + _lastAiForTest +
                   ";lastCandidateCount=" + _lastCandidateCountForTest +
                   ";lastOpen=" + (_lastOpenResultForTest.Length == 0 ? "-" : _lastOpenResultForTest) +
                   ";tempFuriten=" + string.Join(",", _temporaryFuriten.Select(value => value ? "1" : "0")) +
                   ";riichiFuriten=" + string.Join(",", _riichiFuriten.Select(value => value ? "1" : "0")) +
                   ";keys=" + string.Join(
                       "|",
                       _lastDecisionKeys.Select((value, seat) => seat + ":" + (value ?? "-")));
        }

        public async Task StopAsync()
        {
            try
            {
                _stop.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            Task? task = _task;
            if (task != null)
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch
                {
                }
            }
        }

        private async Task DriveAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                if (!_isActive())
                {
                    try
                    {
                        await Task.Delay(20, _stop.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    CompleteIterationForTest();
                    continue;
                }

                try
                {
                    TableSnapshot snapshot = await _actor.GetSnapshotAsync(_stop.Token).ConfigureAwait(false);
                    if (snapshot.IsFinished ||
                        snapshot.Lifecycle == TableActorLifecycle.Closed ||
                        snapshot.Lifecycle == TableActorLifecycle.Faulted)
                    {
                        return;
                    }

                    await _actor.TickAsync(TableTickRequest.Empty, _stop.Token).ConfigureAwait(false);
                    await DriveOneAsync(snapshot).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    _lastLoopErrorForTest = exception.GetType().Name + ":" + exception.Message;
                }

                CompleteIterationForTest();

                try
                {
                    await Task.Delay(15, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private static TaskCompletionSource<long> NewIterationSignal() =>
            new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);

        private static async Task<bool> WaitForIterationAsync(Task<long> changed, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => canceled.TrySetResult(true)))
            {
                Task completed = await Task.WhenAny(changed, canceled.Task).ConfigureAwait(false);
                if (completed != changed)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            await changed.ConfigureAwait(false);
            return true;
        }

        private void CompleteIterationForTest()
        {
            TaskCompletionSource<long> completed;
            long count;
            lock (_iterationGateForTest)
            {
                count = ++_completedIterationsForTest;
                completed = _iterationCompletedForTest;
                _iterationCompletedForTest = NewIterationSignal();
            }

            completed.TrySetResult(count);
        }

        private async Task DriveOneAsync(TableSnapshot snapshot)
        {
            TableControlSnapshot control = await _actor.GetControlSnapshotAsync(_stop.Token).ConfigureAwait(false);
            for (int i = 0; i < 4; i++)
            {
                _controlEpochs[i] = control.Seats[i].ControlEpoch;
                _takeoverSeats[i] = control.Seats[i].Kind == SeatControllerKind.Takeover;
                _aiSeats[i] = control.Seats[i].Kind == SeatControllerKind.Computer ||
                              _takeoverSeats[i];
            }

            // Authorized cheat candidates are captured and submitted inside the actor's serial
            // queue. At most one cheat action is admitted per driver iteration so an offer and
            // the following ordinary action remain separate, observable decisions.
            RoundStateMachine? currentRoundForCheat = null;
            for (int i = 0; i < 4; i++)
            {
                if (!_aiSeats[i])
                {
                    continue;
                }

                bool isTakeover = _takeoverSeats[i];
                SeatAiBinding binding = GetActiveBinding(i);
                if (binding.IsDegradedFallback)
                {
                    bool[] loggedFlags = isTakeover ? _takeoverFallbackLogged : _fallbackLogged;
                    if (!loggedFlags[i])
                    {
                        loggedFlags[i] = true;
                        Observe(
                            snapshot,
                            i,
                            "strategy-fallback",
                            binding.Profile.StrategyId ?? string.Empty,
                            "mahjong",
                            "fallback",
                            binding.FallbackReason,
                            "Degraded profile '" + binding.Profile.ProfileId + "' to '" + binding.EffectiveStrategyId + "'.",
                            binding.Profile.ProfileId,
                            binding.EffectiveStrategyId);
                    }
                }

                bool cheatsEnabled = !binding.IsDegradedFallback &&
                                     binding.HasCheatStrategy &&
                                     binding.EffectiveCheatPolicy.Enabled;
                bool hasPaidOffer = _actor.HasActiveCheatOffer(i);
                if (!cheatsEnabled && !hasPaidOffer)
                {
                    continue;
                }

                if (snapshot.Phase == RoundPhase.CheatWindow && i == snapshot.CurrentSeat)
                {
                    currentRoundForCheat ??= _actor.GetRoundStateForHost();
                    if (currentRoundForCheat.Spec.ConfirmsDraw(i) &&
                        currentRoundForCheat.IsRiichi(i) &&
                        _actor.GetConfirmedSleeveOfferForHost(i) != null)
                    {
                        continue;
                    }
                }

                // A reconstructed driver must continue each seat's current round budget from
                // the authoritative ledger inside TryAiCheatAsync's atomic capture.
                bool takeoverMaySpend = isTakeover &&
                    !binding.IsDegradedFallback &&
                    binding.EffectiveCheatPolicy.Enabled &&
                    binding.EffectiveCheatPolicy.TakeoverMaySpend;
                if (await TryAiCheatAsync(i, snapshot, binding, takeoverMaySpend).ConfigureAwait(false))
                {
                    return;
                }
            }

            int seat = snapshot.CurrentSeat;
            _lastSeatForTest = seat;
            _lastPhaseForTest = snapshot.Phase.ToString();
            _lastAiForTest = _aiSeats[seat];
            _lastCandidateCountForTest = -1;
            switch (snapshot.Phase)
            {
                case RoundPhase.Draw:
                case RoundPhase.KanDraw:
                    RoundStateMachine drawRound = _actor.GetRoundStateForHost();
                    if (drawRound.Tiles.WallRemaining == 0 && (_aiSeats[seat] || drawRound.Spec.ConfirmsDraw(seat)))
                    {
                        await SubmitExhaustiveDrawAsync(snapshot, seat).ConfigureAwait(false);
                    }
                    else if (_aiSeats[seat])
                    {
                        await SubmitDrawAsync(snapshot, seat).ConfigureAwait(false);
                    }
                    else if (drawRound.Spec.ConfirmsDraw(seat) && (_canOpenHumanDecision?.Invoke(seat) ?? true))
                    {
                        await OpenDecisionAsync(snapshot, seat, new[] { new LegalActionOption(LegalActionKind.Draw, seat) }, null)
                            .ConfigureAwait(false);
                    }

                    break;

                case RoundPhase.CheatWindow:
                case RoundPhase.Discard:
                {
                    RoundStateMachine round = _actor.GetRoundStateForHost();
                    if (await ResolveRiichiSleeveAsync(snapshot, round, seat).ConfigureAwait(false)) break;

                    if (!_aiSeats[seat] && _actor.HasActiveCheatOffer(seat))
                    {
                        break;
                    }

                    if (round.IsFinished)
                    {
                        return;
                    }

                    if (_actor.GetPendingDecisionId(seat) != null)
                    {
                        if (_aiSeats[seat])
                        {
                            await CompleteAiDecisionAsync(seat).ConfigureAwait(false);
                        }

                        break;
                    }

                    if (!_aiSeats[seat] && _canOpenHumanDecision != null && !_canOpenHumanDecision(seat)) break;

                    IReadOnlyList<LegalActionOption> candidates = LocalCandidateProvider.DeriveTurnCandidates(
                        round,
                        seat,
                        _temporaryFuriten,
                        _riichiFuriten);
                    _lastCandidateCountForTest = candidates.Count;

                    if (candidates.Count == 0)
                    {
                        // Domain facts yield no candidates; do not invent fallback Discard.
                        return;
                    }

                    TileId? drawnTile = round.Tiles.DrawnTile?.Seat == seat ? round.Tiles.DrawnTile?.Tile : null;
                    bool opened = await OpenDecisionAsync(snapshot, seat, candidates, drawnTile).ConfigureAwait(false);
                    if (opened && _aiSeats[seat])
                    {
                        await CompleteAiDecisionAsync(seat).ConfigureAwait(false);
                    }

                    break;
                }

                case RoundPhase.ResponseWindow:
                case RoundPhase.ChankanWindow:
                {
                    RoundStateMachine round = _actor.GetRoundStateForHost();
                    RoundResponseWindow? window = round.Window;
                    if (window == null)
                    {
                        break;
                    }

                    for (int i = 0; i < window.EligibleSeats.Count; i++)
                    {
                        int responseSeat = window.EligibleSeats[i];
                        if (window.HasResponded(responseSeat))
                        {
                            continue;
                        }

                        if (_actor.GetPendingDecisionId(responseSeat) != null)
                        {
                            if (_aiSeats[responseSeat])
                            {
                                await CompleteAiDecisionAsync(responseSeat).ConfigureAwait(false);
                            }

                            continue;
                        }

                        if (!_aiSeats[responseSeat] && _canOpenHumanDecision != null && !_canOpenHumanDecision(responseSeat)) continue;

                        IReadOnlyList<LegalActionOption> responseCandidates = LocalCandidateProvider.DeriveResponseCandidates(
                            round,
                            responseSeat,
                            window,
                            _temporaryFuriten,
                            _riichiFuriten);

                        if (responseCandidates.Count == 0)
                        {
                            // Domain facts yield no candidate; do not invent fallback Pass.
                            continue;
                        }

                        bool openedResponse = await OpenDecisionAsync(
                            snapshot,
                            responseSeat,
                            responseCandidates,
                            null).ConfigureAwait(false);

                        if (openedResponse && _aiSeats[responseSeat])
                        {
                            await CompleteAiDecisionAsync(responseSeat).ConfigureAwait(false);
                        }
                    }

                    break;
                }
            }
        }

        private async Task SubmitDrawAsync(TableSnapshot snapshot, int seat)
        {
            RoundCommand command = RoundCommand.Draw(snapshot.RoundId, seat);
            TableOperationResult result = await SubmitAsync(snapshot, seat, command, "AI_DRAW").ConfigureAwait(false);
            if (result.Status == TableOperationStatus.Rejected &&
                result.Detail != null &&
                result.Detail.IndexOf("WallExhausted", StringComparison.Ordinal) >= 0)
            {
                await SubmitExhaustiveDrawAsync(snapshot, seat).ConfigureAwait(false);
            }
        }

        private async Task<bool> ResolveRiichiSleeveAsync(TableSnapshot snapshot, RoundStateMachine round, int seat)
        {
            if (round.Phase != RoundPhase.CheatWindow || !round.Spec.ConfirmsDraw(seat) || !round.IsRiichi(seat))
                return false;

            SleeveBloomOffer? offer = _actor.GetConfirmedSleeveOfferForHost(seat);
            if (offer != null)
            {
                SleeveBloomKeep? keep = _actor.GetAutomaticRiichiSleeveChoiceForHost(seat);
                if (!keep.HasValue) return false;

                string requestId = "local-auto-sleeve-" + snapshot.StateVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var command = new RoundCheatCommand(round.RoundId, seat, RoundCheatAction.SubmitSleeveBloomChoice,
                    requestId, RoundCheatPayload.SleeveBloomChoice(offer.OfferId, keep.Value));
                var request = new TableCheatCommandEnvelope(command, new RequestId(requestId), snapshot.StateVersion,
                    _store.LeaseEpoch, new ControlEpoch(unchecked((ulong)ControlEpoch(snapshot, seat))), null,
                    _actor.RecoveryGeneration, _scopes[seat], "AUTO_SLEEVE_CHOICE",
                    TablePayloadSha256.Compute(System.Text.Encoding.UTF8.GetBytes(offer.OfferId + "|" + keep.Value)));
                await _actor.SubmitCheatAsync(request, _stop.Token).ConfigureAwait(false);
                return true;
            }

            if (round.Events.Count == 0 || round.Events[round.Events.Count - 1].Type != RoundEventType.CheatApplied ||
                round.Events[round.Events.Count - 1].Detail != "SubmitSleeveBloomChoice:OK")
                return false;

            IReadOnlyList<LegalActionOption> candidates = LocalCandidateProvider.DeriveTurnCandidates(
                round, seat, _temporaryFuriten, _riichiFuriten);
            if (candidates.Any(option => option.Kind == LegalActionKind.Tsumo))
            {
                TableOperationResult result = await SubmitSettlementAsync(snapshot, seat, TableSettlementAction.Tsumo, null).ConfigureAwait(false);
                _lastOpenResultForTest = result.Status + ":" + result.ReasonCode + ":" + result.Detail;
                return true;
            }

            if (candidates.Any(option => option.Kind == LegalActionKind.Kan) || !round.Tiles.DrawnTile.HasValue)
                return false;

            TileId discard = round.Tiles.DrawnTile.Value.Tile;
            IReadOnlyList<int> eligible = LocalCandidateProvider.DeriveEligibleSeatsForDiscard(
                round, seat, discard, _temporaryFuriten, _riichiFuriten);
            await SubmitAsync(snapshot, seat, RoundCommand.Discard(round.RoundId, seat, discard, eligible),
                "AUTO_SLEEVE_TSUMOGIRI").ConfigureAwait(false);
            return true;
        }

        private async Task<bool> OpenDecisionAsync(
            TableSnapshot snapshot,
            int seat,
            IReadOnlyList<LegalActionOption> candidates,
            TileId? drawnTile)
        {
            if (candidates.Count == 0)
            {
                return false;
            }

            if (_actor.GetPendingDecisionId(seat) != null)
            {
                _lastDecisionKeys[seat] = seat.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" +
                                          snapshot.StateVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" +
                                          snapshot.Phase.ToString();
                return true;
            }

            string key = seat.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" +
                         snapshot.StateVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" +
                         snapshot.Phase.ToString();
            if (string.Equals(_lastDecisionKeys[seat], key, StringComparison.Ordinal))
            {
                return false;
            }

            string prefix = _aiSeats[seat] ? "local-ai-" : "local-human-";
            string decisionId = prefix +
                                snapshot.StateVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" +
                                seat.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" +
                                snapshot.Phase.ToString() + "-" +
                                Guid.NewGuid().ToString("N");
            var pending = new PendingDecision(
                new DecisionKey(snapshot.RoundId, seat, decisionId),
                ControlEpoch(snapshot, seat),
                candidates,
                drawnTile);
            TableOperationResult opened = await _actor.OpenDecisionAsync(pending, _stop.Token).ConfigureAwait(false);
            _lastOpenResultForTest = opened.Status + ":" + opened.ReasonCode;
            if (opened.Status != TableOperationStatus.Configured)
            {
                return false;
            }

            _lastDecisionKeys[seat] = key;
            return true;
        }

        private async Task<bool> TryAiCheatAsync(
            int seat,
            TableSnapshot snapshot,
            SeatAiBinding binding,
            bool allowTakeoverPaidCheats)
        {
            TableAiCheatCaptureResult captured = await _actor
                .CaptureAiCheatOpportunityAsync(seat, allowTakeoverPaidCheats, _stop.Token)
                .ConfigureAwait(false);
            if (!captured.IsCaptured || captured.Opportunity == null)
            {
                return false;
            }

            if (BeforeAiCheatScoreOrSubmitForTest != null)
            {
                await BeforeAiCheatScoreOrSubmitForTest(seat, captured.Opportunity, _stop.Token).ConfigureAwait(false);
            }

            AiCheatOpportunity view = captured.Opportunity.View;
            if (view.Options.Count == 0)
            {
                return false;
            }

            // Rehydrate this seat's round budget from the same frozen mailbox capture
            // so budget checks and visible scoring facts share one consistent authority state.
            binding.RestoreRoundSpend(view.RoundId, seat, captured.Opportunity.SpentThisRound);

            bool canRunCheatStrategy = !binding.IsDegradedFallback &&
                                       binding.HasCheatStrategy &&
                                       (view.IsOfferChoice || binding.EffectiveCheatPolicy.Enabled);
            if (!view.IsOfferChoice && !canRunCheatStrategy)
            {
                return false;
            }

            string? optionId = null;
            if (canRunCheatStrategy)
            {
                if (binding.ScoredCheatStrategy != null)
                {
                    optionId = binding.ScoredCheatStrategy.ChooseCheat(view, captured.Opportunity.VisibleContext);
                }
                else if (binding.RandomCheatStrategy != null)
                {
                    optionId = binding.RandomCheatStrategy.ChooseCheat(view);
                }
            }

            if (optionId == null && view.IsOfferChoice)
            {
                for (int i = 0; i < view.Options.Count; i++)
                {
                    if (view.Options[i].IsSafeDefault)
                    {
                        optionId = view.Options[i].OptionId;
                        break;
                    }
                }
            }

            if (optionId == null)
            {
                return false;
            }

            TableAiCheatPlan? plan = captured.Opportunity.PlanFor(optionId);
            if (plan == null && view.IsOfferChoice)
            {
                for (int i = 0; i < view.Options.Count; i++)
                {
                    if (view.Options[i].IsSafeDefault)
                    {
                        plan = captured.Opportunity.PlanFor(view.Options[i].OptionId);
                        break;
                    }
                }
            }

            if (plan == null)
            {
                Observe(
                    snapshot,
                    seat,
                    "cheat-fallback",
                    optionId,
                    string.Empty,
                    "rejected",
                    "OPTION_NOT_FOUND",
                    "The selected option is not in the captured plan list.",
                    binding.Profile.ProfileId,
                    binding.EffectiveStrategyId);
                return false;
            }

            TableOperationResult result = await _actor
                .SubmitAiCheatAsync(plan, allowTakeoverPaidCheats, _stop.Token)
                .ConfigureAwait(false);
            bool accepted = result.Status == TableOperationStatus.Accepted ||
                            result.Status == TableOperationStatus.Replayed;
            Observe(
                snapshot,
                seat,
                accepted ? "cheat" : "cheat-fallback",
                plan.Option.OptionId,
                plan.Option.CapabilityId,
                accepted ? "accepted" : "rejected",
                result.ReasonCode,
                result.Detail ?? string.Empty,
                binding.Profile.ProfileId,
                binding.EffectiveStrategyId);
            return accepted;
        }

        private void Observe(
            TableSnapshot snapshot,
            int seat,
            string kind,
            string optionId,
            string capabilityId,
            string status,
            string reasonCode,
            string detail,
            string profileId = "",
            string strategyId = "")
        {
            Observe(
                snapshot.RoundId,
                seat,
                snapshot.Phase,
                snapshot.StateVersion.Value,
                kind,
                optionId,
                capabilityId,
                status,
                reasonCode,
                detail,
                profileId,
                strategyId);
        }

        private void Observe(
            string roundId,
            int seat,
            RoundPhase phase,
            ulong stateVersion,
            string kind,
            string optionId,
            string capabilityId,
            string status,
            string reasonCode,
            string detail,
            string profileId = "",
            string strategyId = "")
        {
            if (_aiObserver == null)
            {
                return;
            }

            _aiObserver.OnDecision(new LocalAiDecisionRecord(
                roundId,
                seat,
                phase.ToString(),
                kind,
                optionId,
                capabilityId,
                status,
                reasonCode ?? string.Empty,
                detail ?? string.Empty,
                checked((long)stateVersion),
                profileId,
                strategyId));
        }

        private async Task CompleteAiDecisionAsync(int seat)
        {
            TableAiObservationResult captured = await _actor.CaptureAiObservationAsync(seat, _stop.Token)
                .ConfigureAwait(false);
            if (!captured.IsCaptured || captured.Observation == null)
            {
                return;
            }

            Mahjong.Ai.AiObservation observation = captured.Observation;
            LegalActionOption? chosen = null;

            SeatAiBinding binding = GetActiveBinding(seat);
            IAiStrategy? strategy = StrategyForTest ?? binding.OrdinaryStrategy;
            string observedProfileId = binding.Profile.ProfileId;
            string observedStrategyId = StrategyForTest?.AlgorithmId ?? binding.EffectiveStrategyId;
            if (strategy != null)
            {
                try
                {
                    chosen = strategy.Decide(observation, _stop.Token).Action;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Observe(
                        observation.RoundId,
                        seat,
                        observation.Phase,
                        (ulong)observation.StateVersion,
                        "ordinary-fallback",
                        string.Empty,
                        "mahjong",
                        "fallback",
                        "BASIC_STRATEGY_FALLBACK",
                        exception.GetType().Name + ": " + exception.Message,
                        observedProfileId,
                        observedStrategyId);
                }
            }

            // Legacy heuristic remains the fallback for profiles that do not opt into the
            // Basic strategy and for a defensive fallback if a Basic context is unavailable.
            // Prioritize winning declarations (Ron, Tsumo)
            for (int i = 0; chosen == null && i < observation.LegalActions.Count; i++)
            {
                LegalActionOption option = observation.LegalActions[i];
                if (option.Kind == LegalActionKind.Ron || option.Kind == LegalActionKind.Tsumo)
                {
                    chosen = option;
                    break;
                }
            }

            // Next prioritize AddedKan (加杠)
            if (chosen == null)
            {
                for (int i = 0; i < observation.LegalActions.Count; i++)
                {
                    LegalActionOption option = observation.LegalActions[i];
                    if (option.Kind == LegalActionKind.Kan &&
                        option.OptionId != null &&
                        option.OptionId.StartsWith("added-kan", StringComparison.Ordinal))
                    {
                        chosen = option;
                        break;
                    }
                }
            }

            // Next prioritize Pon (碰) in response windows
            if (chosen == null)
            {
                for (int i = 0; i < observation.LegalActions.Count; i++)
                {
                    LegalActionOption option = observation.LegalActions[i];
                    if (option.Kind == LegalActionKind.Pon)
                    {
                        chosen = option;
                        break;
                    }
                }
            }

            // Next prefer discard (prefer tsumogiri discard if available)
            if (chosen == null && observation.MyDrawnTileId.HasValue)
            {
                for (int i = 0; i < observation.LegalActions.Count; i++)
                {
                    LegalActionOption option = observation.LegalActions[i];
                    if (option.Kind == LegalActionKind.Discard &&
                        option.Tile.HasValue &&
                        option.Tile.Value == observation.MyDrawnTileId.Value)
                    {
                        chosen = option;
                        break;
                    }
                }
            }

            if (chosen == null)
            {
                for (int i = 0; i < observation.LegalActions.Count; i++)
                {
                    LegalActionOption option = observation.LegalActions[i];
                    if (option.Kind == LegalActionKind.Discard)
                    {
                        chosen = option;
                        break;
                    }
                }
            }

            // Next prefer Pass
            if (chosen == null)
            {
                for (int i = 0; i < observation.LegalActions.Count; i++)
                {
                    LegalActionOption option = observation.LegalActions[i];
                    if (option.Kind == LegalActionKind.Pass)
                    {
                        chosen = option;
                        break;
                    }
                }
            }

            // Fallback to first legal action
            if (chosen == null && observation.LegalActions.Count > 0)
            {
                chosen = observation.LegalActions[0];
            }

            if (chosen == null)
            {
                return;
            }

            var intent = new Mahjong.Ai.CommandIntent(observation, chosen, "local-ai", 1);
            _ = new Mahjong.Ai.AiDecision(observation, intent, 1.0d);

            AiDecisionChosenForTest?.Invoke(seat, observation, chosen);
            Observe(
                observation.RoundId,
                seat,
                observation.Phase,
                (ulong)observation.StateVersion,
                "ordinary",
                chosen.OptionId ?? chosen.Kind.ToString(),
                "mahjong",
                "selected",
                string.Empty,
                string.Empty,
                observedProfileId,
                observedStrategyId);

            if (BeforeAiSubmitForTest != null)
            {
                await BeforeAiSubmitForTest(seat, observation).ConfigureAwait(false);
            }

            RoundStateMachine round = _actor.GetRoundStateForHost();
            string roundId = observation.RoundId;
            RoundCommand? command = null;
            TableOperationResult? submitResult = null;
            switch (chosen.Kind)
            {
                case LegalActionKind.Discard when chosen.Tile.HasValue:
                {
                    IReadOnlyList<int> eligible = LocalCandidateProvider.DeriveEligibleSeatsForDiscard(
                        round,
                        seat,
                        chosen.Tile.Value,
                        _temporaryFuriten,
                        _riichiFuriten);
                    command = RoundCommand.Discard(roundId, seat, chosen.Tile.Value, eligible);
                    break;
                }
                case LegalActionKind.Riichi when chosen.Tile.HasValue:
                {
                    IReadOnlyList<int> eligible = LocalCandidateProvider.DeriveEligibleSeatsForDiscard(
                        round,
                        seat,
                        chosen.Tile.Value,
                        _temporaryFuriten,
                        _riichiFuriten);
                    command = RoundCommand.Riichi(roundId, seat, chosen.Tile.Value, eligible);
                    break;
                }
                case LegalActionKind.Pass:
                    command = RoundCommand.Pass(roundId, seat);
                    break;
                case LegalActionKind.Draw:
                    command = RoundCommand.Draw(roundId, seat);
                    break;
                case LegalActionKind.Chi when chosen.Tiles.Count == 3:
                    command = RoundCommand.Chi(roundId, seat, chosen.Tiles[0], chosen.Tiles[1], chosen.Tiles[2]);
                    break;
                case LegalActionKind.Pon when chosen.Tiles.Count == 3:
                    command = RoundCommand.Pon(roundId, seat, chosen.Tiles[0], chosen.Tiles[1], chosen.Tiles[2]);
                    break;
                case LegalActionKind.Kan when chosen.OptionId != null &&
                    chosen.OptionId.StartsWith("closed-kan", StringComparison.Ordinal) &&
                    chosen.Tiles.Count == 4:
                    command = RoundCommand.ClosedKan(
                        roundId, seat, chosen.Tiles[0], chosen.Tiles[1], chosen.Tiles[2], chosen.Tiles[3]);
                    break;
                case LegalActionKind.Kan when chosen.OptionId != null &&
                    chosen.OptionId.StartsWith("open-kan", StringComparison.Ordinal) &&
                    chosen.Tiles.Count == 4:
                    command = RoundCommand.OpenKan(
                        roundId, seat, chosen.Tiles[0], chosen.Tiles[1], chosen.Tiles[2], chosen.Tiles[3]);
                    break;
                case LegalActionKind.Kan when chosen.OptionId != null &&
                    chosen.OptionId.StartsWith("added-kan", StringComparison.Ordinal) &&
                    chosen.Tile.HasValue:
                {
                    IReadOnlyList<int> eligible = LocalCandidateProvider.DeriveEligibleSeatsForAddedKan(
                        round,
                        seat,
                        chosen.Tile.Value,
                        _temporaryFuriten,
                        _riichiFuriten);
                    command = RoundCommand.AddedKan(roundId, seat, chosen.Tile.Value, eligible);
                    break;
                }
                case LegalActionKind.Tsumo:
                    submitResult = await SubmitCapturedSettlementAsync(captured, TableSettlementAction.Tsumo)
                        .ConfigureAwait(false);
                    break;
                case LegalActionKind.Ron:
                    submitResult = await SubmitCapturedSettlementAsync(captured, TableSettlementAction.Ron)
                        .ConfigureAwait(false);
                    break;
                case LegalActionKind.Other when observation.BasicContext != null &&
                    observation.BasicContext.TryGetFacts(chosen, out BasicCandidateFacts otherFacts) &&
                    otherFacts.AbortiveDraw == BasicAbortiveDrawKind.NineTerminals:
                    submitResult = await SubmitCapturedSettlementAsync(
                            captured,
                            TableSettlementAction.AbortiveDraw,
                            SettlementAbortiveDrawReason.NineTerminals)
                        .ConfigureAwait(false);
                    break;
            }

            if (command != null)
            {
                submitResult = await SubmitCapturedAiCommandAsync(
                        captured,
                        command,
                        CapturedCommandApi(chosen))
                    .ConfigureAwait(false);
            }

            if (submitResult != null)
            {
                AiSubmitCompletedForTest?.Invoke(seat, observation, submitResult);
                bool accepted = submitResult.Status == TableOperationStatus.Accepted ||
                                submitResult.Status == TableOperationStatus.Replayed;
                Observe(
                    observation.RoundId,
                    seat,
                    observation.Phase,
                    (ulong)observation.StateVersion,
                    accepted ? "ordinary" : "ordinary-rejected",
                    chosen.OptionId ?? chosen.Kind.ToString(),
                    "mahjong",
                    accepted ? "accepted" : "rejected",
                    submitResult.ReasonCode,
                    submitResult.Detail ?? string.Empty,
                    observedProfileId,
                    observedStrategyId);
            }

            _lastDecisionKeys[seat] = string.Empty;
        }

        private static string CapturedCommandApi(LegalActionOption chosen)
        {
            switch (chosen.Kind)
            {
                case LegalActionKind.Discard:
                    return "AI_DISCARD";
                case LegalActionKind.Riichi:
                    return "AI_RIICHI";
                case LegalActionKind.Pass:
                    return "AI_PASS";
                case LegalActionKind.Draw:
                    return "AI_DRAW";
                case LegalActionKind.Chi:
                    return "AI_CHI";
                case LegalActionKind.Pon:
                    return "AI_PON";
                case LegalActionKind.Kan when chosen.OptionId != null &&
                    chosen.OptionId.StartsWith("closed-kan", StringComparison.Ordinal):
                    return "AI_CLOSED_KAN";
                case LegalActionKind.Kan when chosen.OptionId != null &&
                    chosen.OptionId.StartsWith("open-kan", StringComparison.Ordinal):
                    return "AI_OPEN_KAN";
                case LegalActionKind.Kan when chosen.OptionId != null &&
                    chosen.OptionId.StartsWith("added-kan", StringComparison.Ordinal):
                    return "AI_ADDED_KAN";
                default:
                    return "AI_" + chosen.Kind.ToString().ToUpperInvariant();
            }
        }

        private async Task<TableOperationResult> SubmitCapturedAiCommandAsync(
            TableAiObservationResult captured,
            RoundCommand command,
            string api)
        {
            Mahjong.Ai.AiObservation observation = captured.Observation!;
            var decisionId = new DecisionId(observation.DecisionKey.DecisionId);
            string requestId = "local-ai-" + Interlocked.Increment(ref _requestCounter)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            byte[] business = System.Text.Encoding.UTF8.GetBytes(
                api + "|" + requestId + "|" + observation.RoundId + "|" + observation.StateVersion +
                "|" + observation.Seat + "|" + command.Action + "|" +
                (command.Tile?.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-") + "|" +
                (command.MeldTiles == null || command.MeldTiles.Count == 0
                    ? "-"
                    : string.Join(",", command.MeldTiles.Select(t => t.Value))));
            var envelope = new TableCommandEnvelope(
                command,
                new RequestId(requestId),
                new StateVersion((ulong)observation.StateVersion),
                captured.OwnerEpoch,
                new ControlEpoch(unchecked((ulong)observation.ControlEpoch)),
                decisionId,
                captured.RecoveryGeneration,
                _scopes[observation.Seat],
                api,
                TablePayloadSha256.Compute(business),
                observation.DecisionDeadlineMillis);
            return await _actor.SubmitAsync(envelope, _stop.Token).ConfigureAwait(false);
        }

        private async Task<TableOperationResult> SubmitCapturedSettlementAsync(
            TableAiObservationResult captured,
            TableSettlementAction action,
            SettlementAbortiveDrawReason? abortiveReason = null)
        {
            Mahjong.Ai.AiObservation observation = captured.Observation!;
            var decisionId = new DecisionId(observation.DecisionKey.DecisionId);
            string requestId = "local-ai-settle-" + Interlocked.Increment(ref _requestCounter)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            string api = action == TableSettlementAction.Tsumo
                ? "AI_TSUMO"
                : action == TableSettlementAction.Ron
                    ? "AI_RON"
                    : "AI_ABORTIVE_DRAW";
            byte[] business = System.Text.Encoding.UTF8.GetBytes(
                api + "|" + requestId + "|" + observation.RoundId + "|" + observation.StateVersion +
                "|" + observation.Seat + "|" + (abortiveReason?.ToString() ?? "-"));
            var request = new TableSettlementRequest(
                new TableSettlementIntent(observation.RoundId, action, observation.Seat, abortiveReason),
                new RequestId(requestId),
                new StateVersion((ulong)observation.StateVersion),
                captured.OwnerEpoch,
                new ControlEpoch(unchecked((ulong)observation.ControlEpoch)),
                decisionId,
                captured.RecoveryGeneration,
                _scopes[observation.Seat],
                api,
                TablePayloadSha256.Compute(business),
                observation.DecisionDeadlineMillis);
            return await _actor.SubmitSettlementAsync(request, _stop.Token).ConfigureAwait(false);
        }

        private async Task SubmitExhaustiveDrawAsync(TableSnapshot snapshot, int seat)
        {
            string requestId = "local-ai-settle-" +
                               Interlocked.Increment(ref _requestCounter).ToString(System.Globalization.CultureInfo.InvariantCulture);
            byte[] business = System.Text.Encoding.UTF8.GetBytes(
                "AI_EXHAUSTIVE_DRAW|" + requestId + "|" + snapshot.RoundId + "|" + snapshot.StateVersion.Value);
            var intent = new TableSettlementIntent(
                snapshot.RoundId,
                TableSettlementAction.ExhaustiveDraw,
                seat);
            var request = new TableSettlementRequest(
                intent,
                new RequestId(requestId),
                snapshot.StateVersion,
                _store.LeaseEpoch,
                new ControlEpoch(unchecked((ulong)ControlEpoch(snapshot, seat))),
                null,
                _actor.RecoveryGeneration,
                _scopes[seat],
                "AI_EXHAUSTIVE_DRAW",
                TablePayloadSha256.Compute(business));
            TableOperationResult result = await _actor.SubmitSettlementAsync(request, _stop.Token).ConfigureAwait(false);
            if (result.Status != TableOperationStatus.Accepted && result.Status != TableOperationStatus.Replayed)
            {
                _lastOpenResultForTest = "exhaust:" + result.Status + ":" + result.ReasonCode + ":" + result.Detail;
            }
        }

        private async Task<TableOperationResult> SubmitSettlementAsync(
            TableSnapshot snapshot,
            int seat,
            TableSettlementAction settlementAction,
            DecisionId? decisionId)
        {
            string requestId = "local-ai-settle-" +
                               Interlocked.Increment(ref _requestCounter).ToString(System.Globalization.CultureInfo.InvariantCulture);
            string api = settlementAction == TableSettlementAction.Tsumo ? "AI_TSUMO" : "AI_RON";
            byte[] business = System.Text.Encoding.UTF8.GetBytes(
                api + "|" + requestId + "|" + snapshot.RoundId + "|" + snapshot.StateVersion.Value);
            var intent = new TableSettlementIntent(
                snapshot.RoundId,
                settlementAction,
                seat);
            var request = new TableSettlementRequest(
                intent,
                new RequestId(requestId),
                snapshot.StateVersion,
                _store.LeaseEpoch,
                new ControlEpoch(unchecked((ulong)ControlEpoch(snapshot, seat))),
                decisionId,
                _actor.RecoveryGeneration,
                _scopes[seat],
                api,
                TablePayloadSha256.Compute(business));
            TableOperationResult result = await _actor.SubmitSettlementAsync(request, _stop.Token).ConfigureAwait(false);
            return result;
        }

        private async Task<TableOperationResult> SubmitAsync(
            TableSnapshot snapshot,
            int seat,
            RoundCommand command,
            string api,
            DecisionId? decisionId = null)
        {
            string requestId = "local-ai-" +
                               Interlocked.Increment(ref _requestCounter).ToString(System.Globalization.CultureInfo.InvariantCulture);
            byte[] business = System.Text.Encoding.UTF8.GetBytes(
                api + "|" + requestId + "|" + snapshot.RoundId + "|" + snapshot.StateVersion.Value +
                "|" + seat + "|" + command.Action);
            var envelope = new TableCommandEnvelope(
                command,
                new RequestId(requestId),
                snapshot.StateVersion,
                _store.LeaseEpoch,
                new ControlEpoch(unchecked((ulong)ControlEpoch(snapshot, seat))),
                decisionId,
                _actor.RecoveryGeneration,
                _scopes[seat],
                api,
                TablePayloadSha256.Compute(business));
            TableOperationResult result = await _actor.SubmitAsync(envelope, _stop.Token).ConfigureAwait(false);
            return result;
        }

        private long ControlEpoch(TableSnapshot snapshot, int seat)
        {
            return _controlEpochs[seat];
        }
    }
}
