using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Mahjong.Domain
{
    /// <summary>
    /// Immutable single-round lifecycle.
    ///
    /// V4 parity points:
    /// - <c>ROUND_END</c> is final: no command can revive a finished round;
    /// - a new round is a new instance with a caller-supplied new <see cref="RoundId"/> and
    ///   version 0 (MatchSession enforces uniqueness across a match);
    /// - <c>prepare</c> builds a candidate and never mutates authority; <c>install</c> rejects
    ///   a stale or foreign candidate, preserving the V4 sourceVersion guard;
    /// - added kan opens <c>CHANKAN_WINDOW</c> before any meld/rinshan mutation; a ron leaves
    ///   the pon and rinshan pointer untouched;
    /// - every accepted command advances the version exactly once and emits at least one event.
    ///
    /// Scoring/legality are not re-implemented here. Tsumo/Ron/draw commands carry a
    /// <see cref="RoundSettlement"/> computed by the frozen rules layer; this class verifies
    /// outcome identity and score/stick conservation before committing the terminal state.
    /// </summary>
    public sealed class RoundStateMachine
    {
        private readonly ReadOnlyCollection<long> _scores;
        private readonly ReadOnlyCollection<RoundEvent> _events;
        private readonly RoundResult? _result;

        internal RoundStateMachine(
            RoundSpec spec,
            long version,
            RoundPhase phase,
            int currentSeat,
            TilePositionMap tiles,
            RoundResponseWindow? window,
            RoundResult? result,
            IReadOnlyList<RoundEvent> events,
            RoundStateMachine? parentState)
        {
            Spec = spec;
            Version = version;
            Phase = phase;
            CurrentSeat = currentSeat;
            Tiles = tiles;
            Window = window;
            _result = result;
            _events = new ReadOnlyCollection<RoundEvent>(new List<RoundEvent>(events));
            _scores = new ReadOnlyCollection<long>(new List<long>(spec.Scores));
            ParentState = parentState;
            if ((phase == RoundPhase.RoundEnd) != (result != null))
            {
                throw new ArgumentException("ROUND_END and a result must be present together.");
            }

            SourceFingerprint = ComputeSourceFingerprint();
        }

        /// <summary>
        /// Exact predecessor in the immutable transition chain; null for a factory-created
        /// round. MatchSession uses this chain to verify that a candidate really descends from
        /// its current active round rather than merely sharing roundId/version.
        /// </summary>
        internal RoundStateMachine? ParentState { get; }

        public RoundSpec Spec { get; }

        public string RoundId => Spec.RoundId;

        public long Version { get; }

        /// <summary>
        /// Stable 64-bit fingerprint of this exact authoritative state (phase, seat, scores,
        /// sticks, 136-tile hash, window/claims, events and result). It binds a prepared change
        /// to its source and is not a substitute for the tile-only <see cref="TilePositionMap.StateHash"/>.
        /// </summary>
        public ulong SourceFingerprint { get; }

        public RoundPhase Phase { get; }

        public int DealerSeat => Spec.DealerSeat;

        public int RoundWind => Spec.RoundWind;

        public int RoundNumber => Spec.RoundNumber;

        public int HonbaSticks => Spec.HonbaSticks;

        public int CurrentSeat { get; }

        public IReadOnlyList<long> Scores => _scores;

        public int RiichiSticks => Spec.RiichiSticks;

        public TilePositionMap Tiles { get; }

        /// <summary>Open discard-call or chankan window; null when no response is pending.</summary>
        public RoundResponseWindow? Window { get; }

        public IReadOnlyList<RoundEvent> Events => _events;

        public bool IsFinished => Phase == RoundPhase.RoundEnd;

        public RoundResult? Result => _result;

        public RoundResult? GetResult() => _result;

        /// <summary>Creates a fresh, undealt round. The caller supplies the complete pre-deal wall.</summary>
        public static RoundStateMachine CreateUnstarted(RoundSpec spec, TilePositionMap wall)
        {
            if (spec == null)
            {
                throw new ArgumentNullException(nameof(spec));
            }

            if (wall == null)
            {
                throw new ArgumentNullException(nameof(wall));
            }

            if (wall.CountAt(TileZone.Hand) != 0 ||
                wall.CountAt(TileZone.Discard) != 0 ||
                wall.CountAt(TileZone.Meld) != 0 ||
                wall.CountAt(TileZone.Sleeve) != 0 ||
                wall.CountAt(TileZone.DeadWall) != TilePositionMap.DeadWallSlotCount)
            {
                throw new ArgumentException("An unstarted round requires a pre-deal wall with 14 dead-wall tiles and no player tiles.", nameof(wall));
            }

            return new RoundStateMachine(
                spec,
                0,
                RoundPhase.Stacking,
                spec.DealerSeat,
                wall,
                null,
                null,
                Array.Empty<RoundEvent>(),
                null);
        }

        /// <summary>
        /// Creates a round from an already dealt map (recorded replay/fixture path). The phase
        /// must be non-terminal; the normal opening phase is CHEAT_WINDOW.
        /// </summary>
        public static RoundStateMachine CreateDealt(RoundSpec spec, TilePositionMap dealt, RoundPhase phase = RoundPhase.CheatWindow)
        {
            if (spec == null)
            {
                throw new ArgumentNullException(nameof(spec));
            }

            if (dealt == null)
            {
                throw new ArgumentNullException(nameof(dealt));
            }

            if (phase == RoundPhase.RoundEnd || phase == RoundPhase.Stacking)
            {
                throw new ArgumentOutOfRangeException(nameof(phase), phase, "A dealt round must start in a non-terminal, non-STACKING phase.");
            }

            if (dealt.CountAt(TileZone.Hand) == 0)
            {
                throw new ArgumentException("A dealt round requires at least one hand tile.", nameof(dealt));
            }

            return new RoundStateMachine(
                spec,
                0,
                phase,
                spec.DealerSeat,
                dealt,
                null,
                null,
                Array.Empty<RoundEvent>(),
                null);
        }

        /// <summary>
        /// Builds a candidate transition without touching this instance. A rejection returns a
        /// value with no candidate and no event; the source round is byte-for-byte unchanged.
        /// </summary>
        public RoundPreparation Prepare(RoundCommand command)
        {
            if (command == null)
            {
                throw new ArgumentNullException(nameof(command));
            }

            if (IsFinished || _result != null)
            {
                return RoundPreparation.Rejected(
                    RoundTransitionErrorCode.RoundFinished,
                    "Round " + RoundId + " is already finished and cannot be revived.");
            }

            if (!string.Equals(command.RoundId, RoundId, StringComparison.Ordinal))
            {
                return RoundPreparation.Rejected(
                    RoundTransitionErrorCode.RoundIdMismatch,
                    "Command roundId '" + command.RoundId + "' does not match '" + RoundId + "'.");
            }

            try
            {
                switch (command.Action)
                {
                    case RoundActionKind.StartStacking:
                        return PrepareStartStacking();
                    case RoundActionKind.DrawTile:
                        return PrepareDraw(command.Seat);
                    case RoundActionKind.DiscardTile:
                        return PrepareDiscard(command);
                    case RoundActionKind.DeclareTsumo:
                        return PrepareTsumo(command);
                    case RoundActionKind.DeclareRon:
                        return PrepareRon(command);
                    case RoundActionKind.Pass:
                        return PreparePass(command.Seat);
                    case RoundActionKind.DeclareAddedKan:
                        return PrepareAddedKan(command);
                    case RoundActionKind.DeclareExhaustiveDraw:
                        return PrepareExhaustiveDraw(command);
                    case RoundActionKind.DeclareAbortiveDraw:
                        return PrepareAbortiveDraw(command);
                    default:
                        return RoundPreparation.Rejected(
                            RoundTransitionErrorCode.WrongPhase,
                            "Unsupported action " + command.Action + ".");
                }
            }
            catch (RoundRuleViolation violation)
            {
                return RoundPreparation.Rejected(violation.Code, violation.Message);
            }
        }

        /// <summary>Applies a prepared candidate, rejecting stale/foreign changes without mutation.</summary>
        public RoundTransition Install(RoundChange change)
        {
            if (change == null)
            {
                throw new ArgumentNullException(nameof(change));
            }

            if (IsFinished)
            {
                return RoundTransition.Rejected(
                    RoundTransitionErrorCode.RoundFinished,
                    "Round " + RoundId + " is already finished.");
            }

            if (!string.Equals(change.RoundId, RoundId, StringComparison.Ordinal))
            {
                return RoundTransition.Rejected(
                    RoundTransitionErrorCode.RoundIdMismatch,
                    "Change belongs to round '" + change.RoundId + "', not '" + RoundId + "'.");
            }

            if (change.SourceVersion != Version)
            {
                return RoundTransition.Rejected(
                    RoundTransitionErrorCode.StaleSourceVersion,
                    "Expected source version " + Version + ", got " + change.SourceVersion + ".");
            }

            if (change.SourceFingerprint != SourceFingerprint)
            {
                return RoundTransition.Rejected(
                    RoundTransitionErrorCode.ForeignSourceState,
                    "Candidate was prepared from a different authoritative state with the same roundId/version.");
            }

            if (!ReferenceEquals(change.NextState.ParentState, this))
            {
                return RoundTransition.Rejected(
                    RoundTransitionErrorCode.ForeignSourceState,
                    "Candidate is not a direct child of this exact round state.");
            }

            if (change.NextState.Version != Version + 1 ||
                !string.Equals(change.NextState.RoundId, RoundId, StringComparison.Ordinal))
            {
                return RoundTransition.Rejected(
                    RoundTransitionErrorCode.StaleSourceVersion,
                    "Candidate is not the immediate successor of this round.");
            }

            return RoundTransition.Accepted(change);
        }

        /// <summary>Prepare + install convenience entry used by rule/authority adapters.</summary>
        public RoundTransition Execute(RoundCommand command)
        {
            RoundPreparation prepared = Prepare(command);
            if (!prepared.IsReady)
            {
                RoundTransitionError error = prepared.Error!;
                return RoundTransition.Rejected(error.Code, error.Message);
            }

            return Install(prepared.Change!);
        }

        private RoundPreparation PrepareStartStacking()
        {
            if (Phase != RoundPhase.Stacking)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.WrongPhase,
                    "StartStacking is only legal on a fresh undealt round.");
            }

            if (Version != 0 || Tiles.CountAt(TileZone.Hand) != 0 ||
                Tiles.CountAt(TileZone.Discard) != 0 || Tiles.CountAt(TileZone.Meld) != 0)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.WrongPhase,
                    "StartStacking is only legal before the first deal.");
            }

            TransitionResult dealt = Tiles.DealInitial(DealerSeat);
            if (!dealt.IsSuccess)
            {
                throw FromTileError(dealt);
            }

            var events = new List<RoundEvent>();
            Add(events, RoundEventType.RoundStarted, DealerSeat, null, "wind" + RoundWind + "/round" + RoundNumber);
            Add(events, RoundEventType.TilesDealt, DealerSeat, null, "dealer14");
            for (int i = 0; i < dealt.State!.DoraIndicators.Count; i++)
            {
                Add(events, RoundEventType.DoraRevealed, -1, dealt.State.DoraIndicators[i], null);
            }

            return Build(dealt.State, RoundPhase.CheatWindow, DealerSeat, null, null, events);
        }

        private RoundPreparation PrepareDraw(int seat)
        {
            CheckSeat(seat);
            if (Phase != RoundPhase.Draw || Window != null)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.WrongPhase,
                    "DrawTile is only legal in DRAW without an open window.");
            }

            if (seat != CurrentSeat)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.WrongPlayer,
                    "Seat " + seat + " is not the current player (" + CurrentSeat + ").");
            }

            TransitionResult drawn = Tiles.DrawFromWall(seat);
            if (!drawn.IsSuccess)
            {
                throw FromTileError(drawn);
            }

            var events = new List<RoundEvent>();
            Add(events, RoundEventType.TileDrawn, seat, drawn.ResultTile, null);
            return Build(drawn.State!, RoundPhase.CheatWindow, seat, null, null, events);
        }

        private RoundPreparation PrepareDiscard(RoundCommand command)
        {
            CheckSeat(command.Seat);
            if (Phase != RoundPhase.CheatWindow && Phase != RoundPhase.Discard)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.WrongPhase,
                    "DiscardTile is only legal in CHEAT_WINDOW/DISCARD.");
            }

            if (command.Seat != CurrentSeat)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.WrongPlayer,
                    "Seat " + command.Seat + " is not the current player (" + CurrentSeat + ").");
            }

            if (!command.Tile.HasValue)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.InvalidTile,
                    "DiscardTile requires a physical tile.");
            }

            IReadOnlyList<int> eligible = ValidateEligibleSeats(command.EligibleSeats, command.Seat);
            TransitionResult discarded = Tiles.Discard(command.Seat, command.Tile.Value);
            if (!discarded.IsSuccess)
            {
                throw FromTileError(discarded);
            }

            var events = new List<RoundEvent>();
            Add(events, RoundEventType.TileDiscarded, command.Seat, command.Tile.Value, null);
            if (eligible.Count == 0)
            {
                return AdvanceToNextDraw(discarded.State!, command.Seat, events);
            }

            var window = new RoundResponseWindow(
                command.Seat,
                command.Tile.Value,
                false,
                eligible,
                Array.Empty<RoundResponseClaim>());
            Add(events, RoundEventType.ResponseWindowOpened, command.Seat, command.Tile.Value, null);
            return Build(discarded.State!, RoundPhase.ResponseWindow, command.Seat, window, null, events);
        }

        private RoundPreparation PrepareTsumo(RoundCommand command)
        {
            CheckSeat(command.Seat);
            if (Phase != RoundPhase.Draw &&
                Phase != RoundPhase.CheatWindow &&
                Phase != RoundPhase.Discard &&
                Phase != RoundPhase.KanDraw)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.WrongPhase,
                    "DeclareTsumo is not legal in phase " + Phase + ".");
            }

            if (command.Seat != CurrentSeat)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.WrongPlayer,
                    "Seat " + command.Seat + " is not the current player (" + CurrentSeat + ").");
            }

            DrawnTileEvidence? drawn = Tiles.DrawnTile;
            if (!drawn.HasValue || drawn.Value.Seat != command.Seat)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.NoDrawnTile,
                    "Seat " + command.Seat + " has no tracked drawn tile.");
            }

            RoundSettlement settlement = RequireSettlement(command);
            ValidateSettlement(settlement, RoundOutcomeKind.Tsumo, command.Seat, -1);
            var events = new List<RoundEvent>();
            Add(events, RoundEventType.TsumoWin, command.Seat, drawn.Value.Tile, null);
            return BuildTerminal(settlement, RoundEndReason.Tsumo, events);
        }

        private RoundPreparation PrepareRon(RoundCommand command)
        {
            CheckSeat(command.Seat);
            RoundResponseWindow window = RequireWindow();
            if (command.Seat == window.FromSeat)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.SeatNotEligible,
                    "The discarder/kanser cannot call Ron on its own tile.");
            }

            if (!ContainsSeat(window.EligibleSeats, command.Seat))
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.SeatNotEligible,
                    "Seat " + command.Seat + " is not eligible in this window.");
            }

            if (window.HasResponded(command.Seat))
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.DuplicateResponse,
                    "Seat " + command.Seat + " already responded.");
            }

            RoundSettlement settlement = RequireSettlement(command);
            ValidateSettlement(
                settlement,
                RoundOutcomeKind.Ron,
                command.Seat,
                window.FromSeat,
                checkConservation: false);

            RoundResponseWindow nextWindow = window.WithClaim(RoundResponseClaim.Ron(command.Seat, settlement));
            var events = new List<RoundEvent>();
            Add(events, RoundEventType.ResponseClaimed, command.Seat, window.TargetTile, window.IsChankan ? "RON_CHANKAN" : "RON");
            if (!nextWindow.IsComplete)
            {
                return Build(Tiles, window.IsChankan ? RoundPhase.ChankanWindow : RoundPhase.ResponseWindow, window.FromSeat, nextWindow, null, events);
            }

            return ResolveCompletedWindow(nextWindow, events);
        }

        private RoundPreparation PreparePass(int seat)
        {
            CheckSeat(seat);
            RoundResponseWindow window = RequireWindow();
            if (!ContainsSeat(window.EligibleSeats, seat))
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.SeatNotEligible,
                    "Seat " + seat + " is not eligible in this window.");
            }

            if (window.HasResponded(seat))
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.DuplicateResponse,
                    "Seat " + seat + " already responded.");
            }

            RoundResponseWindow nextWindow = window.WithClaim(RoundResponseClaim.Pass(seat));
            var events = new List<RoundEvent>();
            Add(events, RoundEventType.ResponseClaimed, seat, window.TargetTile, "PASS");
            if (!nextWindow.IsComplete)
            {
                return Build(Tiles, window.IsChankan ? RoundPhase.ChankanWindow : RoundPhase.ResponseWindow, window.FromSeat, nextWindow, null, events);
            }

            return ResolveCompletedWindow(nextWindow, events);
        }

        private RoundPreparation PrepareAddedKan(RoundCommand command)
        {
            CheckSeat(command.Seat);
            if (Window != null)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.WrongPhase,
                    "Cannot declare a kan while a response window is open.");
            }

            if (Phase != RoundPhase.CheatWindow && Phase != RoundPhase.Discard)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.WrongPhase,
                    "Added kan is not legal in phase " + Phase + ".");
            }

            if (command.Seat != CurrentSeat)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.WrongPlayer,
                    "Seat " + command.Seat + " is not the current player (" + CurrentSeat + ").");
            }

            if (!command.Tile.HasValue)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.InvalidTile,
                    "Added kan requires the fourth physical tile.");
            }

            TransitionResult dryRun = Tiles.AddedKan(command.Seat, command.Tile.Value);
            if (!dryRun.IsSuccess)
            {
                throw FromTileError(dryRun);
            }

            IReadOnlyList<int> eligible = ValidateEligibleSeats(command.ChankanEligibleSeats, command.Seat);
            var events = new List<RoundEvent>();
            if (eligible.Count == 0)
            {
                return CommitAddedKan(dryRun.State!, command.Seat, command.Tile.Value, events);
            }

            var window = new RoundResponseWindow(
                command.Seat,
                command.Tile.Value,
                true,
                eligible,
                Array.Empty<RoundResponseClaim>());
            Add(events, RoundEventType.ChankanWindowOpened, command.Seat, command.Tile.Value, null);
            return Build(Tiles, RoundPhase.ChankanWindow, command.Seat, window, null, events);
        }

        private RoundPreparation PrepareExhaustiveDraw(RoundCommand command)
        {
            if (Phase != RoundPhase.Draw || Window != null)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.WrongPhase,
                    "An exhaustive draw is only legal when DRAW has no tiles left.");
            }

            if (Tiles.WallRemaining != 0)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.WallNotExhausted,
                    "Wall still has " + Tiles.WallRemaining + " tiles.");
            }

            RoundSettlement settlement = RequireSettlement(command);
            RoundOutcome outcome = settlement.Outcome;
            if (outcome.Kind != RoundOutcomeKind.ExhaustiveDraw)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.SettlementOutcomeMismatch,
                    "Exhaustive draw requires an ExhaustiveDraw settlement.");
            }

            var events = new List<RoundEvent>();
            Add(events, RoundEventType.ExhaustiveDraw, -1, null, "tenpai:" + JoinSeats(outcome.TenpaiSeats));
            return BuildTerminal(settlement, RoundEndReason.ExhaustiveDraw, events);
        }

        private RoundPreparation PrepareAbortiveDraw(RoundCommand command)
        {
            RoundSettlement settlement = RequireSettlement(command);
            if (settlement.Outcome.Kind != RoundOutcomeKind.AbortiveDraw)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.SettlementOutcomeMismatch,
                    "Abortive draw requires an AbortiveDraw settlement.");
            }

            var events = new List<RoundEvent>();
            Add(events, RoundEventType.AbortiveDraw, -1, null, settlement.Outcome.Reason);
            return BuildTerminal(settlement, RoundEndReason.AbortiveDraw, events);
        }

        private RoundPreparation ResolveCompletedWindow(RoundResponseWindow window, List<RoundEvent> events)
        {
            RoundResponseClaim? winner = null;
            int bestDistance = int.MaxValue;
            for (int i = 0; i < window.Claims.Count; i++)
            {
                RoundResponseClaim claim = window.Claims[i];
                if (!claim.IsRon)
                {
                    continue;
                }

                int distance = (claim.Seat - window.FromSeat + 4) % 4;
                if (winner == null || distance < bestDistance ||
                    (distance == bestDistance && claim.Seat < winner.Seat))
                {
                    winner = claim;
                    bestDistance = distance;
                }
            }

            if (winner != null)
            {
                RoundSettlement settlement = winner.Settlement!;
                ValidateSettlement(settlement, RoundOutcomeKind.Ron, winner.Seat, window.FromSeat);
                Add(events, RoundEventType.RonWin, winner.Seat, window.TargetTile, window.IsChankan ? "CHANKAN" : null);
                return BuildTerminal(
                    settlement,
                    window.IsChankan ? RoundEndReason.ChankanRon : RoundEndReason.Ron,
                    events);
            }

            if (window.IsChankan)
            {
                TransitionResult kan = Tiles.AddedKan(window.FromSeat, window.TargetTile);
                if (!kan.IsSuccess)
                {
                    throw FromTileError(kan);
                }

                return CommitAddedKan(kan.State!, window.FromSeat, window.TargetTile, events);
            }

            return AdvanceToNextDraw(Tiles, window.FromSeat, events);
        }

        private RoundPreparation CommitAddedKan(
            TilePositionMap afterKan,
            int seat,
            TileId tile,
            List<RoundEvent> events)
        {
            TransitionResult replacement = afterKan.DrawReplacement(seat);
            if (!replacement.IsSuccess)
            {
                throw FromTileError(replacement);
            }

            TileId? rinshan = replacement.ResultTile;
            Add(events, RoundEventType.KanDeclared, seat, tile, "ADDED_KAN");
            Add(events, RoundEventType.RinshanDrawn, seat, rinshan, null);
            return Build(replacement.State!, RoundPhase.CheatWindow, seat, null, null, events);
        }

        private RoundPreparation AdvanceToNextDraw(
            TilePositionMap currentTiles,
            int fromSeat,
            List<RoundEvent> events)
        {
            int nextSeat = (fromSeat + 1) % 4;
            if (currentTiles.WallRemaining == 0)
            {
                Add(events, RoundEventType.WallExhausted, -1, null, null);
                return Build(currentTiles, RoundPhase.Draw, nextSeat, null, null, events);
            }

            TransitionResult drawn = currentTiles.DrawFromWall(nextSeat);
            if (!drawn.IsSuccess)
            {
                throw FromTileError(drawn);
            }

            Add(events, RoundEventType.TileDrawn, nextSeat, drawn.ResultTile, null);
            return Build(drawn.State!, RoundPhase.CheatWindow, nextSeat, null, null, events);
        }

        private RoundPreparation Build(
            TilePositionMap tiles,
            RoundPhase phase,
            int currentSeat,
            RoundResponseWindow? window,
            RoundResult? result,
            List<RoundEvent> addedEvents)
        {
            long nextVersion = checked(Version + 1);
            var allEvents = new List<RoundEvent>(_events.Count + addedEvents.Count);
            allEvents.AddRange(_events);
            var committed = new List<RoundEvent>(addedEvents.Count);
            for (int i = 0; i < addedEvents.Count; i++)
            {
                RoundEvent raw = addedEvents[i];
                var committedEvent = new RoundEvent(raw.Type, _events.Count + i, raw.Seat, raw.Tile, raw.Detail);
                allEvents.Add(committedEvent);
                committed.Add(committedEvent);
            }

            var next = new RoundStateMachine(
                Spec,
                nextVersion,
                phase,
                currentSeat,
                tiles,
                window,
                result,
                allEvents,
                this);
            return RoundPreparation.Ready(new RoundChange(RoundId, Version, SourceFingerprint, next, committed, result));
        }

        private RoundPreparation BuildTerminal(RoundSettlement settlement, RoundEndReason reason, List<RoundEvent> events)
        {
            long startingTotal = checked(SumScores(_scores) + (long)RiichiSticks * 1000L);
            long finalTotal = checked(SumScores(settlement.FinalScores) + (long)settlement.RemainingRiichiSticks * 1000L);
            if (startingTotal != finalTotal)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.SettlementConservationViolation,
                    "Settlement breaks score/stick conservation: " + startingTotal + " != " + finalTotal + ".");
            }

            var deltas = new long[4];
            for (int i = 0; i < 4; i++)
            {
                deltas[i] = checked(settlement.FinalScores[i] - _scores[i]);
            }

            var allEvents = new List<RoundEvent>(_events.Count + events.Count);
            allEvents.AddRange(_events);
            var committed = new List<RoundEvent>(events.Count);
            for (int i = 0; i < events.Count; i++)
            {
                RoundEvent raw = events[i];
                var committedEvent = new RoundEvent(raw.Type, _events.Count + i, raw.Seat, raw.Tile, raw.Detail);
                allEvents.Add(committedEvent);
                committed.Add(committedEvent);
            }

            Add(allEvents, RoundEventType.RoundEnded, -1, null, reason.ToString());
            committed.Add(allEvents[allEvents.Count - 1]);

            var result = new RoundResult(
                settlement.ResultId,
                RoundId,
                settlement.Outcome,
                new ReadOnlyCollection<long>(new List<long>(_scores)),
                settlement.FinalScores,
                new ReadOnlyCollection<long>(deltas),
                RiichiSticks,
                settlement.RemainingRiichiSticks,
                new ReadOnlyCollection<RoundEvent>(new List<RoundEvent>(allEvents)),
                reason,
                settlement.CompletedAtUtc,
                settlement.CompletedAtMonotonicMillis);

            long nextVersion = checked(Version + 1);
            var next = new RoundStateMachine(
                Spec,
                nextVersion,
                RoundPhase.RoundEnd,
                CurrentSeat,
                Tiles,
                null,
                result,
                allEvents,
                this);
            var change = new RoundChange(RoundId, Version, SourceFingerprint, next, committed, result);
            return RoundPreparation.Ready(change);
        }

        private void ValidateSettlement(
            RoundSettlement settlement,
            RoundOutcomeKind expectedKind,
            int expectedWinner,
            int expectedLoser,
            bool checkConservation = true)
        {
            RoundOutcome outcome = settlement.Outcome;
            if (outcome.Kind != expectedKind || outcome.WinnerSeat != expectedWinner)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.SettlementOutcomeMismatch,
                    "Settlement " + outcome.Kind + "/winner" + outcome.WinnerSeat +
                    " does not match " + expectedKind + "/winner" + expectedWinner + ".");
            }

            if (expectedLoser >= 0 && outcome.LoserSeat != expectedLoser)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.SettlementOutcomeMismatch,
                    "Settlement loser " + outcome.LoserSeat + " does not match " + expectedLoser + ".");
            }

            if (checkConservation)
            {
                long startingTotal = checked(SumScores(_scores) + (long)RiichiSticks * 1000L);
                long finalTotal = checked(SumScores(settlement.FinalScores) + (long)settlement.RemainingRiichiSticks * 1000L);
                if (startingTotal != finalTotal)
                {
                    throw new RoundRuleViolation(
                        RoundTransitionErrorCode.SettlementConservationViolation,
                        "Settlement breaks score/stick conservation: " + startingTotal + " != " + finalTotal + ".");
                }
            }
        }

        private RoundResponseWindow RequireWindow()
        {
            if (Window == null ||
                (Phase != RoundPhase.ResponseWindow && Phase != RoundPhase.ChankanWindow))
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.NoResponseWindow,
                    "No response window is open.");
            }

            return Window;
        }

        private RoundSettlement RequireSettlement(RoundCommand command)
        {
            return command.Settlement ??
                   throw new RoundRuleViolation(
                       RoundTransitionErrorCode.SettlementOutcomeMismatch,
                       "This command requires a scoring-layer settlement.");
        }

        private static IReadOnlyList<int> ValidateEligibleSeats(IReadOnlyList<int>? seats, int fromSeat)
        {
            if (seats == null)
            {
                return new ReadOnlyCollection<int>(new List<int>());
            }

            var copy = new List<int>(seats.Count);
            var seen = new bool[4];
            for (int i = 0; i < seats.Count; i++)
            {
                int seat = seats[i];
                CheckSeat(seat);
                if (seat == fromSeat)
                {
                    throw new RoundRuleViolation(
                        RoundTransitionErrorCode.InvalidEligibleSeats,
                        "The acting seat cannot be eligible to call its own tile.");
                }

                if (seen[seat])
                {
                    throw new RoundRuleViolation(
                        RoundTransitionErrorCode.InvalidEligibleSeats,
                        "Eligible seat " + seat + " is duplicated.");
                }

                seen[seat] = true;
                copy.Add(seat);
            }

            copy.Sort();
            return new ReadOnlyCollection<int>(copy);
        }

        private static bool ContainsSeat(IReadOnlyList<int> seats, int seat)
        {
            for (int i = 0; i < seats.Count; i++)
            {
                if (seats[i] == seat)
                {
                    return true;
                }
            }

            return false;
        }

        private static void CheckSeat(int seat)
        {
            if (seat < 0 || seat > 3)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.InvalidSeat,
                    "Seat must be 0..3, got " + seat + ".");
            }
        }

        private static long SumScores(IReadOnlyList<long> scores)
        {
            long sum = 0;
            for (int i = 0; i < scores.Count; i++)
            {
                sum = checked(sum + scores[i]);
            }

            return sum;
        }

        private static string JoinSeats(IReadOnlyList<int> seats)
        {
            var parts = new string[seats.Count];
            for (int i = 0; i < seats.Count; i++)
            {
                parts[i] = seats[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            return string.Join(",", parts);
        }

        private ulong ComputeSourceFingerprint()
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offset;

            void Mix(ulong value)
            {
                hash ^= value;
                hash *= prime;
            }

            void MixInt(int value) => Mix(unchecked((ulong)(uint)value));
            void MixLong(long value) => Mix(unchecked((ulong)value));
            void MixString(string? value)
            {
                if (value == null)
                {
                    MixInt(-1);
                    return;
                }

                MixInt(value.Length);
                for (int i = 0; i < value.Length; i++)
                {
                    MixInt(value[i]);
                }
            }

            MixString(RoundId);
            MixLong(Version);
            MixInt((int)Phase);
            MixInt(CurrentSeat);
            MixInt(DealerSeat);
            MixInt(RoundWind);
            MixInt(RoundNumber);
            MixInt(HonbaSticks);
            MixInt(RiichiSticks);
            for (int i = 0; i < _scores.Count; i++)
            {
                MixLong(_scores[i]);
            }

            MixLong(unchecked((long)Tiles.StateHash));
            MixInt(Window == null ? 0 : 1);
            if (Window != null)
            {
                MixInt(Window.FromSeat);
                MixInt(Window.TargetTile.Value);
                MixInt(Window.IsChankan ? 1 : 0);
                MixInt(Window.EligibleSeats.Count);
                for (int i = 0; i < Window.EligibleSeats.Count; i++)
                {
                    MixInt(Window.EligibleSeats[i]);
                }

                MixInt(Window.Claims.Count);
                for (int i = 0; i < Window.Claims.Count; i++)
                {
                    RoundResponseClaim claim = Window.Claims[i];
                    MixInt(claim.Seat);
                    MixInt(claim.IsRon ? 1 : 0);
                    if (claim.Settlement != null)
                    {
                        MixSettlement(claim.Settlement);
                    }
                }
            }

            MixInt(_events.Count);
            for (int i = 0; i < _events.Count; i++)
            {
                RoundEvent e = _events[i];
                MixInt((int)e.Type);
                MixInt(e.Ordinal);
                MixInt(e.Seat);
                MixInt(e.Tile.HasValue ? e.Tile.Value.Value : -1);
                MixString(e.Detail);
            }

            MixInt(_result == null ? 0 : 1);
            if (_result != null)
            {
                MixString(_result.ResultId);
                MixInt((int)_result.Outcome.Kind);
                MixInt(_result.Outcome.WinnerSeat);
                MixInt(_result.Outcome.LoserSeat);
                MixInt(_result.Outcome.TenpaiSeats.Count);
                for (int i = 0; i < _result.Outcome.TenpaiSeats.Count; i++)
                {
                    MixInt(_result.Outcome.TenpaiSeats[i]);
                }

                for (int i = 0; i < _result.FinalScores.Count; i++)
                {
                    MixLong(_result.FinalScores[i]);
                }

                MixInt(_result.RemainingRiichiSticks);
                MixInt((int)_result.EndReason);
                MixLong(_result.CompletedAtUtc.Value.UtcDateTime.Ticks);
                MixLong(_result.CompletedAtMonotonicMillis);
            }

            return hash == 0 ? 1UL : hash;

            void MixSettlement(RoundSettlement settlement)
            {
                MixString(settlement.ResultId);
                MixInt((int)settlement.Outcome.Kind);
                MixInt(settlement.Outcome.WinnerSeat);
                MixInt(settlement.Outcome.LoserSeat);
                MixInt(settlement.Outcome.TenpaiSeats.Count);
                for (int i = 0; i < settlement.Outcome.TenpaiSeats.Count; i++)
                {
                    MixInt(settlement.Outcome.TenpaiSeats[i]);
                }

                for (int i = 0; i < settlement.FinalScores.Count; i++)
                {
                    MixLong(settlement.FinalScores[i]);
                }

                MixInt(settlement.RemainingRiichiSticks);
                MixLong(settlement.CompletedAtUtc.Value.UtcDateTime.Ticks);
                MixLong(settlement.CompletedAtMonotonicMillis);
            }
        }

        private static void Add(
            List<RoundEvent> events,
            RoundEventType type,
            int seat,
            TileId? tile,
            string? detail)
        {
            events.Add(new RoundEvent(type, events.Count, seat, tile, detail));
        }

        private static RoundRuleViolation FromTileError(TransitionResult result)
        {
            TransitionError? error = result.Error;
            return new RoundRuleViolation(
                RoundTransitionErrorCode.TileTransitionRejected,
                error == null ? "Tile transition failed." : error.ToString());
        }

        private sealed class RoundRuleViolation : Exception
        {
            internal RoundRuleViolation(RoundTransitionErrorCode code, string message)
                : base(message)
            {
                Code = code;
            }

            internal RoundTransitionErrorCode Code { get; }
        }
    }

    /// <summary>Caller-supplied, already-authorized round command.</summary>
    public sealed class RoundCommand
    {
        private RoundCommand(
            string roundId,
            RoundActionKind action,
            int seat,
            TileId? tile,
            IReadOnlyList<int>? eligibleSeats,
            IReadOnlyList<int>? chankanEligibleSeats,
            RoundSettlement? settlement)
        {
            RoundId = string.IsNullOrWhiteSpace(roundId)
                ? throw new ArgumentException("roundId cannot be blank.", nameof(roundId))
                : roundId;
            V5Guard.CheckSeat(seat);
            Action = action;
            Seat = seat;
            Tile = tile;
            EligibleSeats = eligibleSeats;
            ChankanEligibleSeats = chankanEligibleSeats;
            Settlement = settlement;
        }

        public string RoundId { get; }

        public RoundActionKind Action { get; }

        public int Seat { get; }

        public TileId? Tile { get; }

        public IReadOnlyList<int>? EligibleSeats { get; }

        public IReadOnlyList<int>? ChankanEligibleSeats { get; }

        public RoundSettlement? Settlement { get; }

        public static RoundCommand StartStacking(string roundId) =>
            new RoundCommand(roundId, RoundActionKind.StartStacking, 0, null, null, null, null);

        public static RoundCommand Draw(string roundId, int seat) =>
            new RoundCommand(roundId, RoundActionKind.DrawTile, seat, null, null, null, null);

        public static RoundCommand Discard(
            string roundId,
            int seat,
            TileId tile,
            IReadOnlyList<int>? eligibleSeats = null) =>
            new RoundCommand(roundId, RoundActionKind.DiscardTile, seat, tile, eligibleSeats, null, null);

        public static RoundCommand Tsumo(string roundId, int seat, RoundSettlement settlement) =>
            new RoundCommand(roundId, RoundActionKind.DeclareTsumo, seat, null, null, null, settlement);

        public static RoundCommand Ron(string roundId, int seat, RoundSettlement settlement) =>
            new RoundCommand(roundId, RoundActionKind.DeclareRon, seat, null, null, null, settlement);

        public static RoundCommand Pass(string roundId, int seat) =>
            new RoundCommand(roundId, RoundActionKind.Pass, seat, null, null, null, null);

        public static RoundCommand AddedKan(
            string roundId,
            int seat,
            TileId tile,
            IReadOnlyList<int>? chankanEligibleSeats = null) =>
            new RoundCommand(
                roundId,
                RoundActionKind.DeclareAddedKan,
                seat,
                tile,
                null,
                chankanEligibleSeats,
                null);

        public static RoundCommand ExhaustiveDraw(string roundId, RoundSettlement settlement) =>
            new RoundCommand(roundId, RoundActionKind.DeclareExhaustiveDraw, 0, null, null, null, settlement);

        public static RoundCommand AbortiveDraw(string roundId, RoundSettlement settlement) =>
            new RoundCommand(roundId, RoundActionKind.DeclareAbortiveDraw, 0, null, null, null, settlement);
    }
}
