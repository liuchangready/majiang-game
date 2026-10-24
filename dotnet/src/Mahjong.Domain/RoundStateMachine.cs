using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Mahjong.Domain.Rules;

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
        private readonly ReadOnlyCollection<bool> _riichiDeclared;
        private readonly ReadOnlyCollection<RoundMeld> _melds;
        private readonly ReadOnlyCollection<RoundEvent> _events;
        private readonly RoundResult? _result;
        private readonly int _riichiSticks;
        private readonly int _pendingKanDoraCount;

        internal RoundStateMachine(
            RoundSpec spec,
            long version,
            RoundPhase phase,
            int currentSeat,
            TilePositionMap tiles,
            RoundResponseWindow? window,
            RoundResult? result,
            IReadOnlyList<RoundEvent> events,
            RoundStateMachine? parentState,
            IReadOnlyList<long>? scores = null,
            int? riichiSticks = null,
            IReadOnlyList<bool>? riichiDeclared = null,
            IReadOnlyList<RoundMeld>? melds = null,
            int pendingKanDoraCount = 0)
        {
            Spec = spec;
            Version = version;
            Phase = phase;
            CurrentSeat = currentSeat;
            Tiles = tiles;
            Window = window;
            _result = result;
            _events = new ReadOnlyCollection<RoundEvent>(new List<RoundEvent>(events));
            _scores = new ReadOnlyCollection<long>(CopyScores(scores ?? spec.Scores));
            _riichiDeclared = new ReadOnlyCollection<bool>(CopyFlags(riichiDeclared));
            _melds = new ReadOnlyCollection<RoundMeld>(CopyMelds(melds, tiles));
            _riichiSticks = riichiSticks ?? spec.RiichiSticks;
            _pendingKanDoraCount = pendingKanDoraCount;
            ParentState = parentState;
            if ((phase == RoundPhase.RoundEnd) != (result != null))
            {
                throw new ArgumentException("ROUND_END and a result must be present together.");
            }

            if (_riichiSticks < 0 || _riichiSticks > spec.RiichiSticks + 4)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(riichiSticks),
                    _riichiSticks,
                    "Riichi sticks cannot be negative or exceed the four declarations a round can add.");
            }

            if (_pendingKanDoraCount < 0 || _pendingKanDoraCount > 5)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(pendingKanDoraCount),
                    _pendingKanDoraCount,
                    "Pending kan dora must be 0..5.");
            }

            long startingTotal = checked(SumScores(spec.Scores) + ((long)spec.RiichiSticks * 1000L));
            long currentTotal = checked(SumScores(_scores) + ((long)_riichiSticks * 1000L));
            if (startingTotal != currentTotal)
            {
                throw new ArgumentException(
                    "Round scores/sticks do not conserve the starting total: " +
                    currentTotal + " != " + startingTotal + ".");
            }

            for (int seat = 0; seat < 4; seat++)
            {
                if (_riichiDeclared[seat] && !IsMenzen(seat))
                {
                    throw new ArgumentException("Seat " + seat + " declared riichi with an open meld.");
                }
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

        /// <summary>Current riichi-stick pot (starting sticks plus declarations made this round).</summary>
        public int RiichiSticks => _riichiSticks;

        /// <summary>Per-seat riichi declaration flags; immutable copy.</summary>
        public IReadOnlyList<bool> RiichiDeclared => _riichiDeclared;

        /// <summary>Authoritative meld records added by N-1a decisions, in commit order.</summary>
        public IReadOnlyList<RoundMeld> Melds => _melds;

        /// <summary>
        /// Kan-dora indicators that are owed but not yet revealed. V4 defers open/added-kan
        /// dora to the following discard; closed kan reveals immediately.
        /// </summary>
        public int PendingKanDoraCount => _pendingKanDoraCount;

        public bool IsRiichi(int seat)
        {
            V5Guard.CheckSeat(seat);
            return _riichiDeclared[seat];
        }

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
                    case RoundActionKind.DeclareChi:
                        return PrepareChi(command);
                    case RoundActionKind.DeclarePon:
                        return PreparePon(command);
                    case RoundActionKind.DeclareOpenKan:
                        return PrepareOpenKan(command);
                    case RoundActionKind.DeclareClosedKan:
                        return PrepareClosedKan(command);
                    case RoundActionKind.DeclareRiichi:
                        return PrepareRiichi(command);
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

            if (_riichiDeclared[command.Seat])
            {
                DrawnTileEvidence? lockedDraw = Tiles.DrawnTile;
                if (!lockedDraw.HasValue || lockedDraw.Value.Seat != command.Seat)
                {
                    throw new RoundRuleViolation(
                        RoundTransitionErrorCode.RiichiDiscardLocked,
                        "A riichi player must discard the tile just drawn.");
                }

                if (lockedDraw.Value.Tile != command.Tile.Value)
                {
                    throw new RoundRuleViolation(
                        RoundTransitionErrorCode.RiichiDiscardLocked,
                        "Riichi is locked to tsumogiri " + lockedDraw.Value.Tile +
                        ", got " + command.Tile.Value + ".");
                }
            }

            IReadOnlyList<int> eligible = ValidateEligibleSeats(command.EligibleSeats, command.Seat);
            TransitionResult discarded = Tiles.Discard(command.Seat, command.Tile.Value);
            if (!discarded.IsSuccess)
            {
                throw FromTileError(discarded);
            }

            var events = new List<RoundEvent>();
            Add(events, RoundEventType.TileDiscarded, command.Seat, command.Tile.Value, null);
            TilePositionMap afterDora = RevealPendingKanDora(discarded.State!, events, out int pendingAfter);
            if (eligible.Count == 0)
            {
                return AdvanceToNextDraw(afterDora, command.Seat, events, pendingAfter);
            }

            var window = new RoundResponseWindow(
                command.Seat,
                command.Tile.Value,
                false,
                eligible,
                Array.Empty<RoundResponseClaim>());
            Add(events, RoundEventType.ResponseWindowOpened, command.Seat, command.Tile.Value, null);
            return Build(
                afterDora,
                RoundPhase.ResponseWindow,
                command.Seat,
                window,
                null,
                events,
                pendingKanDoraCount: pendingAfter);
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

        private RoundPreparation PrepareChi(RoundCommand command)
        {
            CheckSeat(command.Seat);
            RoundResponseWindow window = RequireWindow();
            if (window.IsChankan)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.MeldNotAllowed,
                    "Chi is not available in a chankan window.");
            }

            ValidateResponseSeat(window, command.Seat);
            if (command.Seat != (window.FromSeat + 1) % 4)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.MeldNotAllowed,
                    "Chi is only available to the discarder's shimocha (" +
                    ((window.FromSeat + 1) % 4) + ").");
            }

            if (_riichiDeclared[command.Seat])
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.MeldNotAllowed,
                    "A riichi player cannot chi.");
            }

            if (!command.Tile.HasValue || command.Tile.Value != window.TargetTile)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.InvalidTile,
                    "Chi must name the window target tile " + window.TargetTile + ".");
            }

            IReadOnlyList<TileId> handTiles = RequireMeldTiles(command, 2, "Chi");
            TransitionResult dryRun = Tiles.Chi(
                command.Seat,
                window.FromSeat,
                window.TargetTile,
                handTiles[0],
                handTiles[1]);
            if (!dryRun.IsSuccess)
            {
                throw FromTileError(dryRun);
            }

            RoundResponseClaim claim = RoundResponseClaim.Chi(command.Seat, handTiles[0], handTiles[1]);
            return RecordMeldClaim(window, claim, RoundEventType.ResponseClaimed, "CHI");
        }

        private RoundPreparation PreparePon(RoundCommand command)
        {
            CheckSeat(command.Seat);
            RoundResponseWindow window = RequireWindow();
            if (window.IsChankan)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.MeldNotAllowed,
                    "Pon is not available in a chankan window.");
            }

            ValidateResponseSeat(window, command.Seat);
            if (_riichiDeclared[command.Seat])
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.MeldNotAllowed,
                    "A riichi player cannot pon.");
            }

            if (!command.Tile.HasValue || command.Tile.Value != window.TargetTile)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.InvalidTile,
                    "Pon must name the window target tile " + window.TargetTile + ".");
            }

            IReadOnlyList<TileId> handTiles = RequireMeldTiles(command, 2, "Pon");
            TransitionResult dryRun = Tiles.Pon(
                command.Seat,
                window.FromSeat,
                window.TargetTile,
                handTiles[0],
                handTiles[1]);
            if (!dryRun.IsSuccess)
            {
                throw FromTileError(dryRun);
            }

            RoundResponseClaim claim = RoundResponseClaim.Pon(command.Seat, handTiles[0], handTiles[1]);
            return RecordMeldClaim(window, claim, RoundEventType.ResponseClaimed, "PON");
        }

        private RoundPreparation PrepareOpenKan(RoundCommand command)
        {
            CheckSeat(command.Seat);
            RoundResponseWindow window = RequireWindow();
            if (window.IsChankan)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.MeldNotAllowed,
                    "Open kan is not available in a chankan window.");
            }

            ValidateResponseSeat(window, command.Seat);
            if (_riichiDeclared[command.Seat])
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.MeldNotAllowed,
                    "A riichi player cannot call an open kan.");
            }

            if (!command.Tile.HasValue || command.Tile.Value != window.TargetTile)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.InvalidTile,
                    "Open kan must name the window target tile " + window.TargetTile + ".");
            }

            CheckKanAvailable();
            IReadOnlyList<TileId> handTiles = RequireMeldTiles(command, 3, "Open kan");
            TransitionResult dryRun = Tiles.OpenKan(
                command.Seat,
                window.FromSeat,
                window.TargetTile,
                handTiles[0],
                handTiles[1],
                handTiles[2]);
            if (!dryRun.IsSuccess)
            {
                throw FromTileError(dryRun);
            }

            RoundResponseClaim claim = RoundResponseClaim.OpenKan(
                command.Seat,
                handTiles[0],
                handTiles[1],
                handTiles[2]);
            return RecordMeldClaim(window, claim, RoundEventType.ResponseClaimed, "OPEN_KAN");
        }

        private RoundPreparation RecordMeldClaim(
            RoundResponseWindow window,
            RoundResponseClaim claim,
            RoundEventType eventType,
            string detail)
        {
            RoundResponseWindow nextWindow = window.WithClaim(claim);
            var events = new List<RoundEvent>();
            Add(events, eventType, claim.Seat, window.TargetTile, detail);
            if (!nextWindow.IsComplete)
            {
                return Build(
                    Tiles,
                    window.IsChankan ? RoundPhase.ChankanWindow : RoundPhase.ResponseWindow,
                    window.FromSeat,
                    nextWindow,
                    null,
                    events);
            }

            return ResolveCompletedWindow(nextWindow, events);
        }

        private RoundPreparation PrepareClosedKan(RoundCommand command)
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
                    "Closed kan is not legal in phase " + Phase + ".");
            }

            if (command.Seat != CurrentSeat)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.WrongPlayer,
                    "Seat " + command.Seat + " is not the current player (" + CurrentSeat + ").");
            }

            IReadOnlyList<TileId> tiles = RequireMeldTiles(command, 4, "Closed kan");
            if (_riichiDeclared[command.Seat] && !CanClosedKanAfterRiichi(command.Seat, tiles))
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.MeldNotAllowed,
                    "Closed kan after riichi must keep the exact waits and decomposition structure.");
            }

            CheckKanAvailable();
            TransitionResult dryRun = Tiles.ClosedKan(
                command.Seat,
                tiles[0],
                tiles[1],
                tiles[2],
                tiles[3]);
            if (!dryRun.IsSuccess)
            {
                throw FromTileError(dryRun);
            }

            var events = new List<RoundEvent>();
            TilePositionMap afterPending = RevealPendingKanDora(dryRun.State!, events, out _);
            TransitionResult dora = afterPending.RevealDoraIndicator();
            if (!dora.IsSuccess)
            {
                throw FromTileError(dora);
            }

            Add(events, RoundEventType.DoraRevealed, -1, dora.ResultTile, null);
            Add(events, RoundEventType.KanDeclared, command.Seat, tiles[0], "CLOSED_KAN");
            TransitionResult replacement = dora.State!.DrawReplacement(command.Seat);
            if (!replacement.IsSuccess)
            {
                throw FromTileError(replacement);
            }

            Add(events, RoundEventType.RinshanDrawn, command.Seat, replacement.ResultTile, null);
            IReadOnlyList<RoundMeld> melds = AppendMeld(new RoundMeld(
                RoundMeldKind.ClosedKan,
                command.Seat,
                command.Seat,
                tiles[0],
                tiles));
            return Build(
                replacement.State!,
                RoundPhase.CheatWindow,
                command.Seat,
                null,
                null,
                events,
                melds: melds,
                pendingKanDoraCount: 0);
        }

        private RoundPreparation PrepareRiichi(RoundCommand command)
        {
            CheckSeat(command.Seat);
            if (Window != null)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.WrongPhase,
                    "Cannot declare riichi while a response window is open.");
            }

            if (Phase != RoundPhase.CheatWindow && Phase != RoundPhase.Discard)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.WrongPhase,
                    "Riichi is not legal in phase " + Phase + ".");
            }

            if (command.Seat != CurrentSeat)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.WrongPlayer,
                    "Seat " + command.Seat + " is not the current player (" + CurrentSeat + ").");
            }

            if (_riichiDeclared[command.Seat])
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.RiichiAlreadyDeclared,
                    "Seat " + command.Seat + " already declared riichi.");
            }

            if (!IsMenzen(command.Seat))
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.RiichiNotMenzen,
                    "Riichi requires a closed hand.");
            }

            if (_scores[command.Seat] < 1000)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.RiichiInsufficientPoints,
                    "Riichi requires at least 1000 points, seat has " + _scores[command.Seat] + ".");
            }

            if (Tiles.WallRemaining < 4)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.RiichiWallTooShort,
                    "Riichi requires at least four drawable wall tiles, got " + Tiles.WallRemaining + ".");
            }

            if (!command.Tile.HasValue)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.InvalidTile,
                    "Riichi requires the physical discard tile.");
            }

            if (!CanRiichiWithTile(command.Seat, command.Tile.Value))
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.RiichiNotTenpai,
                    "Discarding " + command.Tile.Value + " leaves no live tenpai wait.");
            }

            var nextScores = new long[4];
            Array.Copy(CopyScores(_scores), nextScores, 4);
            nextScores[command.Seat] = checked(nextScores[command.Seat] - 1000L);
            var nextDeclared = CopyFlags(_riichiDeclared);
            nextDeclared[command.Seat] = true;

            IReadOnlyList<int> eligible = ValidateEligibleSeats(command.EligibleSeats, command.Seat);
            TransitionResult discarded = Tiles.Discard(command.Seat, command.Tile.Value);
            if (!discarded.IsSuccess)
            {
                throw FromTileError(discarded);
            }

            var events = new List<RoundEvent>();
            Add(events, RoundEventType.RiichiDeclared, command.Seat, command.Tile.Value, "RIICHI");
            Add(events, RoundEventType.TileDiscarded, command.Seat, command.Tile.Value, "RIICHI");
            TilePositionMap afterDora = RevealPendingKanDora(discarded.State!, events, out int pendingAfter);
            if (eligible.Count == 0)
            {
                return AdvanceToNextDraw(
                    afterDora,
                    command.Seat,
                    events,
                    pendingAfter,
                    nextScores,
                    _riichiSticks + 1,
                    nextDeclared);
            }

            var window = new RoundResponseWindow(
                command.Seat,
                command.Tile.Value,
                false,
                eligible,
                Array.Empty<RoundResponseClaim>());
            Add(events, RoundEventType.ResponseWindowOpened, command.Seat, command.Tile.Value, null);
            return Build(
                afterDora,
                RoundPhase.ResponseWindow,
                command.Seat,
                window,
                null,
                events,
                scores: nextScores,
                riichiSticks: _riichiSticks + 1,
                riichiDeclared: nextDeclared,
                pendingKanDoraCount: pendingAfter);
        }

        private void ValidateResponseSeat(RoundResponseWindow window, int seat)
        {
            if (seat == window.FromSeat)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.SeatNotEligible,
                    "The discarder/kanser cannot call its own tile.");
            }

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
        }

        private static IReadOnlyList<TileId> RequireMeldTiles(
            RoundCommand command,
            int expected,
            string action)
        {
            if (command.MeldTiles == null || command.MeldTiles.Count != expected)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.InvalidMeld,
                    action + " requires exactly " + expected + " physical hand tiles.");
            }

            var copy = new TileId[expected];
            var seen = new HashSet<int>();
            for (int i = 0; i < expected; i++)
            {
                copy[i] = command.MeldTiles[i];
                if (!seen.Add(copy[i].Value))
                {
                    throw new RoundRuleViolation(
                        RoundTransitionErrorCode.InvalidMeld,
                        action + " contains duplicate physical tile " + copy[i] + ".");
                }
            }

            return copy;
        }

        private void CheckKanAvailable()
        {
            if (Tiles.KanCount >= 4 ||
                Tiles.RinshanPointer >= 4 ||
                Tiles.WallRemaining <= 0)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.KanLimitReached,
                    "Kan requires totalKanCount<4, rinshanPointer<4 and a live wall tile.");
            }
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

            CheckKanAvailable();
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
            RoundResponseClaim? winner = SelectWinningClaim(window);
            if (winner == null)
            {
                if (window.IsChankan)
                {
                    TransitionResult kan = Tiles.AddedKan(window.FromSeat, window.TargetTile);
                    if (!kan.IsSuccess)
                    {
                        throw FromTileError(kan);
                    }

                    return CommitAddedKan(kan.State!, window.FromSeat, window.TargetTile, events);
                }

                return AdvanceToNextDraw(Tiles, window.FromSeat, events, _pendingKanDoraCount);
            }

            switch (winner.Kind)
            {
                case RoundClaimKind.Ron:
                    {
                        RoundSettlement settlement = winner.Settlement!;
                        ValidateSettlement(settlement, RoundOutcomeKind.Ron, winner.Seat, window.FromSeat);
                        Add(
                            events,
                            RoundEventType.RonWin,
                            winner.Seat,
                            window.TargetTile,
                            window.IsChankan ? "CHANKAN" : null);
                        return BuildTerminal(
                            settlement,
                            window.IsChankan ? RoundEndReason.ChankanRon : RoundEndReason.Ron,
                            events);
                    }

                case RoundClaimKind.Pon:
                    return CommitPon(window, winner, events);

                case RoundClaimKind.Chi:
                    return CommitChi(window, winner, events);

                case RoundClaimKind.OpenKan:
                    return CommitOpenKan(window, winner, events);

                default:
                    throw new RoundRuleViolation(
                        RoundTransitionErrorCode.MeldNotAllowed,
                        "Unsupported winning claim " + winner.Kind + ".");
            }
        }

        private static RoundResponseClaim? SelectWinningClaim(RoundResponseWindow window)
        {
            RoundResponseClaim? winner = null;
            int bestPriority = 0;
            int bestDistance = int.MaxValue;
            for (int i = 0; i < window.Claims.Count; i++)
            {
                RoundResponseClaim claim = window.Claims[i];
                int priority = ClaimPriority(claim.Kind);
                if (priority == 0)
                {
                    continue;
                }

                int distance = (claim.Seat - window.FromSeat + 4) % 4;
                if (distance == 0)
                {
                    distance = 4;
                }

                if (winner == null ||
                    priority > bestPriority ||
                    (priority == bestPriority && distance < bestDistance) ||
                    (priority == bestPriority && distance == bestDistance && claim.Seat < winner.Seat))
                {
                    winner = claim;
                    bestPriority = priority;
                    bestDistance = distance;
                }
            }

            return winner;
        }

        private static int ClaimPriority(RoundClaimKind kind)
        {
            switch (kind)
            {
                case RoundClaimKind.Ron:
                    return 3;
                case RoundClaimKind.Pon:
                case RoundClaimKind.OpenKan:
                    return 2;
                case RoundClaimKind.Chi:
                    return 1;
                default:
                    return 0;
            }
        }

        private RoundPreparation CommitPon(
            RoundResponseWindow window,
            RoundResponseClaim claim,
            List<RoundEvent> events)
        {
            TransitionResult pon = Tiles.Pon(
                claim.Seat,
                window.FromSeat,
                window.TargetTile,
                claim.MeldTiles[0],
                claim.MeldTiles[1]);
            if (!pon.IsSuccess)
            {
                throw FromTileError(pon);
            }

            IReadOnlyList<TileId> tiles = new[] { claim.MeldTiles[0], claim.MeldTiles[1], window.TargetTile };
            IReadOnlyList<RoundMeld> melds = AppendMeld(new RoundMeld(
                RoundMeldKind.Pon,
                claim.Seat,
                window.FromSeat,
                window.TargetTile,
                tiles));
            Add(events, RoundEventType.PonDeclared, claim.Seat, window.TargetTile, "PON/" + window.FromSeat);
            return Build(
                pon.State!,
                RoundPhase.Discard,
                claim.Seat,
                null,
                null,
                events,
                melds: melds);
        }

        private RoundPreparation CommitChi(
            RoundResponseWindow window,
            RoundResponseClaim claim,
            List<RoundEvent> events)
        {
            TransitionResult chi = Tiles.Chi(
                claim.Seat,
                window.FromSeat,
                window.TargetTile,
                claim.MeldTiles[0],
                claim.MeldTiles[1]);
            if (!chi.IsSuccess)
            {
                throw FromTileError(chi);
            }

            IReadOnlyList<TileId> tiles = new[] { claim.MeldTiles[0], claim.MeldTiles[1], window.TargetTile };
            IReadOnlyList<RoundMeld> melds = AppendMeld(new RoundMeld(
                RoundMeldKind.Chi,
                claim.Seat,
                window.FromSeat,
                window.TargetTile,
                tiles));
            Add(events, RoundEventType.ChiDeclared, claim.Seat, window.TargetTile, "CHI/" + window.FromSeat);
            return Build(
                chi.State!,
                RoundPhase.Discard,
                claim.Seat,
                null,
                null,
                events,
                melds: melds);
        }

        private RoundPreparation CommitOpenKan(
            RoundResponseWindow window,
            RoundResponseClaim claim,
            List<RoundEvent> events)
        {
            CheckKanAvailable();
            TransitionResult kan = Tiles.OpenKan(
                claim.Seat,
                window.FromSeat,
                window.TargetTile,
                claim.MeldTiles[0],
                claim.MeldTiles[1],
                claim.MeldTiles[2]);
            if (!kan.IsSuccess)
            {
                throw FromTileError(kan);
            }

            TilePositionMap afterPending = RevealPendingKanDora(kan.State!, events, out _);
            IReadOnlyList<TileId> tiles = new[]
            {
                claim.MeldTiles[0], claim.MeldTiles[1], claim.MeldTiles[2], window.TargetTile
            };
            IReadOnlyList<RoundMeld> melds = AppendMeld(new RoundMeld(
                RoundMeldKind.OpenKan,
                claim.Seat,
                window.FromSeat,
                window.TargetTile,
                tiles));
            Add(events, RoundEventType.KanDeclared, claim.Seat, window.TargetTile, "OPEN_KAN");
            TransitionResult replacement = afterPending.DrawReplacement(claim.Seat);
            if (!replacement.IsSuccess)
            {
                throw FromTileError(replacement);
            }

            Add(events, RoundEventType.RinshanDrawn, claim.Seat, replacement.ResultTile, null);
            return Build(
                replacement.State!,
                RoundPhase.CheatWindow,
                claim.Seat,
                null,
                null,
                events,
                melds: melds,
                pendingKanDoraCount: 1);
        }

        private RoundPreparation CommitAddedKan(
            TilePositionMap afterKan,
            int seat,
            TileId tile,
            List<RoundEvent> events)
        {
            TilePositionMap afterPending = RevealPendingKanDora(afterKan, events, out _);
            IReadOnlyList<RoundMeld> melds = MeldsWithAddedKan(seat, tile);
            Add(events, RoundEventType.KanDeclared, seat, tile, "ADDED_KAN");
            TransitionResult replacement = afterPending.DrawReplacement(seat);
            if (!replacement.IsSuccess)
            {
                throw FromTileError(replacement);
            }

            TileId? rinshan = replacement.ResultTile;
            Add(events, RoundEventType.RinshanDrawn, seat, rinshan, null);
            return Build(
                replacement.State!,
                RoundPhase.CheatWindow,
                seat,
                null,
                null,
                events,
                melds: melds,
                pendingKanDoraCount: 1);
        }

        private TilePositionMap RevealPendingKanDora(
            TilePositionMap map,
            List<RoundEvent> events,
            out int pendingAfter)
        {
            pendingAfter = 0;
            for (int i = 0; i < _pendingKanDoraCount; i++)
            {
                TransitionResult reveal = map.RevealDoraIndicator();
                if (!reveal.IsSuccess)
                {
                    throw FromTileError(reveal);
                }

                map = reveal.State!;
                Add(events, RoundEventType.DoraRevealed, -1, reveal.ResultTile, null);
            }

            return map;
        }

        private RoundPreparation AdvanceToNextDraw(
            TilePositionMap currentTiles,
            int fromSeat,
            List<RoundEvent> events,
            int pendingKanDoraCount = 0,
            IReadOnlyList<long>? scores = null,
            int? riichiSticks = null,
            IReadOnlyList<bool>? riichiDeclared = null)
        {
            int nextSeat = (fromSeat + 1) % 4;
            if (currentTiles.WallRemaining == 0)
            {
                Add(events, RoundEventType.WallExhausted, -1, null, null);
                return Build(
                    currentTiles,
                    RoundPhase.Draw,
                    nextSeat,
                    null,
                    null,
                    events,
                    scores: scores,
                    riichiSticks: riichiSticks,
                    riichiDeclared: riichiDeclared,
                    pendingKanDoraCount: pendingKanDoraCount);
            }

            TransitionResult drawn = currentTiles.DrawFromWall(nextSeat);
            if (!drawn.IsSuccess)
            {
                throw FromTileError(drawn);
            }

            Add(events, RoundEventType.TileDrawn, nextSeat, drawn.ResultTile, null);
            return Build(
                drawn.State!,
                RoundPhase.CheatWindow,
                nextSeat,
                null,
                null,
                events,
                scores: scores,
                riichiSticks: riichiSticks,
                riichiDeclared: riichiDeclared,
                pendingKanDoraCount: pendingKanDoraCount);
        }

        private RoundPreparation Build(
            TilePositionMap tiles,
            RoundPhase phase,
            int currentSeat,
            RoundResponseWindow? window,
            RoundResult? result,
            List<RoundEvent> addedEvents,
            IReadOnlyList<long>? scores = null,
            int? riichiSticks = null,
            IReadOnlyList<bool>? riichiDeclared = null,
            IReadOnlyList<RoundMeld>? melds = null,
            int? pendingKanDoraCount = null)
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
                this,
                scores ?? _scores,
                riichiSticks ?? _riichiSticks,
                riichiDeclared ?? _riichiDeclared,
                melds ?? _melds,
                pendingKanDoraCount ?? _pendingKanDoraCount);
            return RoundPreparation.Ready(new RoundChange(RoundId, Version, SourceFingerprint, next, committed, result));
        }

        private RoundPreparation BuildTerminal(RoundSettlement settlement, RoundEndReason reason, List<RoundEvent> events)
        {
            long startingTotal = checked(SumScores(Spec.Scores) + ((long)Spec.RiichiSticks * 1000L));
            long finalTotal = checked(SumScores(settlement.FinalScores) + ((long)settlement.RemainingRiichiSticks * 1000L));
            if (startingTotal != finalTotal)
            {
                throw new RoundRuleViolation(
                    RoundTransitionErrorCode.SettlementConservationViolation,
                    "Settlement breaks score/stick conservation: " + startingTotal + " != " + finalTotal + ".");
            }

            var deltas = new long[4];
            for (int i = 0; i < 4; i++)
            {
                deltas[i] = checked(settlement.FinalScores[i] - Spec.Scores[i]);
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
                new ReadOnlyCollection<long>(new List<long>(Spec.Scores)),
                settlement.FinalScores,
                new ReadOnlyCollection<long>(deltas),
                Spec.RiichiSticks,
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
                this,
                settlement.FinalScores,
                settlement.RemainingRiichiSticks,
                _riichiDeclared,
                _melds,
                0);
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

        /// <summary>
        /// Menzen is true only when every physical meld tile has a recorded non-open meld.
        /// A legacy/fixture map with an unrecorded three-tile group is treated as open rather
        /// than silently granting riichi over it.
        /// </summary>
        private bool IsMenzen(int seat)
        {
            for (int i = 0; i < _melds.Count; i++)
            {
                if (_melds[i].Seat == seat && _melds[i].IsOpen)
                {
                    return false;
                }
            }

            int physical = Tiles.CountAt(TileZone.Meld, seat);
            int recorded = 0;
            for (int i = 0; i < _melds.Count; i++)
            {
                if (_melds[i].Seat == seat)
                {
                    recorded += _melds[i].Tiles.Count;
                }
            }

            return physical == recorded;
        }

        private int MeldCount(int seat)
        {
            int count = 0;
            for (int i = 0; i < _melds.Count; i++)
            {
                if (_melds[i].Seat == seat)
                {
                    count++;
                }
            }

            return count;
        }

        private bool CanRiichiWithTile(int seat, TileId discardTile)
        {
            var hand = new List<TileId>(Tiles.TilesInZone(TileZone.Hand, seat));
            int remove = hand.IndexOf(discardTile);
            if (remove < 0)
            {
                return false;
            }

            hand.RemoveAt(remove);
            if (hand.Count % 3 != 1)
            {
                return false;
            }

            int[] closedCounts = TypeCounts(hand);
            int[] meldCounts = MeldTypeCounts(seat);
            foreach (int wait in HandDecomposer.CalculateWaits(hand))
            {
                if (closedCounts[wait] + meldCounts[wait] < 4)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Faithful port of V4 <c>RiichiKanValidator</c> (WRC 2022 8.9.1): the drawn tile must
        /// participate, the other three copies must be exactly held, and both the wait set and
        /// every regular decomposition structure must be unchanged by the kan.
        /// </summary>
        private bool CanClosedKanAfterRiichi(int seat, IReadOnlyList<TileId> kanTiles)
        {
            if (!IsMenzen(seat))
            {
                return false;
            }

            DrawnTileEvidence? drawnEvidence = Tiles.DrawnTile;
            if (!drawnEvidence.HasValue || drawnEvidence.Value.Seat != seat)
            {
                return false;
            }

            TileId drawn = drawnEvidence.Value.Tile;
            var hand = new List<TileId>(Tiles.TilesInZone(TileZone.Hand, seat));
            if (!hand.Remove(drawn))
            {
                return false;
            }

            var closed = hand;
            if (closed.Count != 13 - (MeldCount(seat) * 3))
            {
                return false;
            }

            var kan = new List<TileId>(kanTiles);
            kan.Sort();
            if (kan.Count != 4 ||
                kan.Distinct().Count() != 4 ||
                !kan.All(tile => tile.SameType(kan[0])) ||
                !kan.Contains(drawn))
            {
                return false;
            }

            var postClosed = new List<TileId>(closed);
            for (int i = 0; i < kan.Count; i++)
            {
                if (kan[i] == drawn)
                {
                    continue;
                }

                if (!postClosed.Remove(kan[i]))
                {
                    return false;
                }
            }

            var postMelds = new List<RoundMeld>(_melds)
            {
                new RoundMeld(
                    RoundMeldKind.ClosedKan,
                    seat,
                    seat,
                    kan[0],
                    kan)
            };
            IReadOnlyList<int> waitsOrig = CalculateWaitTypes(closed, _melds, seat);
            IReadOnlyList<int> waitsPost = CalculateWaitTypes(postClosed, postMelds, seat);
            if (waitsOrig.Count == 0 || !waitsOrig.SequenceEqual(waitsPost))
            {
                return false;
            }

            int targetType = kan[0].TypeIndex;
            for (int i = 0; i < waitsOrig.Count; i++)
            {
                TileId winTile = TileId.FromTypeIndex(waitsOrig[i], 0);
                var origTiles = new List<TileId>(closed) { winTile };
                var postTiles = new List<TileId>(postClosed) { winTile };
                IReadOnlyList<Decomposition> origDecomps = HandDecomposer.Decompose(origTiles);
                IReadOnlyList<Decomposition> postDecomps = HandDecomposer.Decompose(postTiles);
                if (origDecomps.Count == 0 || postDecomps.Count == 0)
                {
                    return false;
                }

                var postKeys = new List<string>(postDecomps.Count);
                for (int d = 0; d < postDecomps.Count; d++)
                {
                    if (postDecomps[d].Pattern != DecompPattern.Regular)
                    {
                        return false;
                    }

                    postKeys.Add(NormalizeDecomposition(postDecomps[d].Jantai, postDecomps[d].Mentsu));
                }

                var origKeys = new List<string>(origDecomps.Count);
                for (int d = 0; d < origDecomps.Count; d++)
                {
                    Decomposition decomp = origDecomps[d];
                    if (decomp.Pattern != DecompPattern.Regular)
                    {
                        return false;
                    }

                    int triplet = -1;
                    for (int m = 0; m < decomp.Mentsu.Count; m++)
                    {
                        MentsuGroup group = decomp.Mentsu[m];
                        if (group.IsTriple &&
                            group.TypeIndices.All(index => index == targetType))
                        {
                            triplet = m;
                            break;
                        }
                    }

                    if (triplet < 0)
                    {
                        return false;
                    }

                    var remaining = new List<MentsuGroup>(decomp.Mentsu.Count - 1);
                    for (int m = 0; m < decomp.Mentsu.Count; m++)
                    {
                        if (m != triplet)
                        {
                            remaining.Add(decomp.Mentsu[m]);
                        }
                    }

                    origKeys.Add(NormalizeDecomposition(decomp.Jantai, remaining));
                }

                origKeys.Sort(StringComparer.Ordinal);
                postKeys.Sort(StringComparer.Ordinal);
                if (!origKeys.SequenceEqual(postKeys))
                {
                    return false;
                }
            }

            return true;
        }

        private static IReadOnlyList<int> CalculateWaitTypes(
            IReadOnlyList<TileId> closed,
            IReadOnlyList<RoundMeld> melds,
            int seat)
        {
            int[] closedCounts = TypeCounts(closed);
            var meldCounts = new int[TileId.TypeCount];
            for (int i = 0; i < melds.Count; i++)
            {
                RoundMeld meld = melds[i];
                if (meld.Seat != seat)
                {
                    continue;
                }

                for (int t = 0; t < meld.Tiles.Count; t++)
                {
                    meldCounts[meld.Tiles[t].TypeIndex]++;
                }
            }

            var waits = new List<int>();
            foreach (int wait in HandDecomposer.CalculateWaits(closed))
            {
                if (closedCounts[wait] + meldCounts[wait] < 4)
                {
                    waits.Add(wait);
                }
            }

            waits.Sort();
            return waits;
        }

        private static int[] TypeCounts(IReadOnlyList<TileId> tiles)
        {
            var counts = new int[TileId.TypeCount];
            for (int i = 0; i < tiles.Count; i++)
            {
                counts[tiles[i].TypeIndex]++;
            }

            return counts;
        }

        private int[] MeldTypeCounts(int seat)
        {
            var counts = new int[TileId.TypeCount];
            for (int i = 0; i < _melds.Count; i++)
            {
                RoundMeld meld = _melds[i];
                if (meld.Seat != seat)
                {
                    continue;
                }

                for (int t = 0; t < meld.Tiles.Count; t++)
                {
                    counts[meld.Tiles[t].TypeIndex]++;
                }
            }

            return counts;
        }

        private static string NormalizeDecomposition(int jantai, IEnumerable<MentsuGroup> mentsu)
        {
            var parts = new List<string>();
            foreach (MentsuGroup group in mentsu)
            {
                var indices = new List<int>(group.TypeIndices);
                indices.Sort();
                parts.Add(
                    (group.IsSequence ? "S" : "T") +
                    string.Join(".", indices.Select(index => index.ToString(CultureInfo.InvariantCulture))));
            }

            parts.Sort(StringComparer.Ordinal);
            return jantai.ToString(CultureInfo.InvariantCulture) + "|" + string.Join(",", parts);
        }

        private IReadOnlyList<RoundMeld> AppendMeld(RoundMeld meld)
        {
            var next = new List<RoundMeld>(_melds.Count + 1);
            next.AddRange(_melds);
            next.Add(meld);
            return next;
        }

        private IReadOnlyList<RoundMeld> MeldsWithAddedKan(int seat, TileId tile)
        {
            var next = new List<RoundMeld>(_melds);
            for (int i = 0; i < next.Count; i++)
            {
                RoundMeld meld = next[i];
                if (meld.Seat == seat &&
                    meld.Kind == RoundMeldKind.Pon &&
                    meld.Tiles[0].SameType(tile))
                {
                    var tiles = new List<TileId>(meld.Tiles) { tile };
                    next[i] = new RoundMeld(
                        RoundMeldKind.AddedKan,
                        seat,
                        meld.FromSeat,
                        meld.CalledTile,
                        tiles);
                    return next;
                }
            }

            // Legacy/fixture maps may carry a physical pon without a record. The physical map
            // still proves the open meld; there is no reliable caller to invent here.
            return _melds;
        }

        private static long[] CopyScores(IReadOnlyList<long> scores)
        {
            if (scores == null || scores.Count != 4)
            {
                throw new ArgumentException("Scores must contain exactly four entries.", nameof(scores));
            }

            var copy = new long[4];
            for (int i = 0; i < 4; i++)
            {
                copy[i] = scores[i];
            }

            return copy;
        }

        private static bool[] CopyFlags(IReadOnlyList<bool>? flags)
        {
            var copy = new bool[4];
            if (flags == null)
            {
                return copy;
            }

            if (flags.Count != 4)
            {
                throw new ArgumentException("Riichi flags must contain exactly four entries.", nameof(flags));
            }

            for (int i = 0; i < 4; i++)
            {
                copy[i] = flags[i];
            }

            return copy;
        }

        private static RoundMeld[] CopyMelds(IReadOnlyList<RoundMeld>? melds, TilePositionMap tiles)
        {
            if (melds == null || melds.Count == 0)
            {
                return Array.Empty<RoundMeld>();
            }

            var copy = new RoundMeld[melds.Count];
            var seenTiles = new HashSet<int>();
            var perSeat = new int[4];
            for (int i = 0; i < melds.Count; i++)
            {
                RoundMeld meld = melds[i] ?? throw new ArgumentException("Meld cannot be null.", nameof(melds));
                if (++perSeat[meld.Seat] > 4)
                {
                    throw new ArgumentException("A seat cannot hold more than four melds.", nameof(melds));
                }

                for (int t = 0; t < meld.Tiles.Count; t++)
                {
                    TileId tile = meld.Tiles[t];
                    if (!seenTiles.Add(tile.Value))
                    {
                        throw new ArgumentException("A physical tile belongs to more than one meld.", nameof(melds));
                    }

                    TilePosition position = tiles[tile];
                    if (position.Zone != TileZone.Meld || position.Seat != meld.Seat)
                    {
                        throw new ArgumentException(
                            "Meld tile " + tile + " is at " + position + ", not in seat " +
                            meld.Seat + "'s meld zone.", nameof(melds));
                    }
                }

                copy[i] = meld;
            }

            return copy;
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
            MixInt(Spec.RiichiSticks);
            for (int i = 0; i < Spec.Scores.Count; i++)
            {
                MixLong(Spec.Scores[i]);
            }

            MixInt(_riichiSticks);
            MixInt(_pendingKanDoraCount);
            for (int i = 0; i < _scores.Count; i++)
            {
                MixLong(_scores[i]);
            }

            for (int i = 0; i < 4; i++)
            {
                MixInt(_riichiDeclared[i] ? 1 : 0);
            }

            MixInt(_melds.Count);
            for (int i = 0; i < _melds.Count; i++)
            {
                RoundMeld meld = _melds[i];
                MixInt((int)meld.Kind);
                MixInt(meld.Seat);
                MixInt(meld.FromSeat);
                MixInt(meld.CalledTile.Value);
                MixInt(meld.Tiles.Count);
                for (int t = 0; t < meld.Tiles.Count; t++)
                {
                    MixInt(meld.Tiles[t].Value);
                }
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
                    MixInt((int)claim.Kind);
                    MixInt(claim.MeldTiles.Count);
                    for (int t = 0; t < claim.MeldTiles.Count; t++)
                    {
                        MixInt(claim.MeldTiles[t].Value);
                    }

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
            RoundSettlement? settlement,
            IReadOnlyList<TileId>? meldTiles = null)
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
            MeldTiles = CopyMeldTiles(meldTiles);
        }

        public string RoundId { get; }

        public RoundActionKind Action { get; }

        public int Seat { get; }

        public TileId? Tile { get; }

        public IReadOnlyList<int>? EligibleSeats { get; }

        public IReadOnlyList<int>? ChankanEligibleSeats { get; }

        public RoundSettlement? Settlement { get; }

        /// <summary>
        /// Exact caller-owned physical tiles for a chi/pon/open-kan/closed-kan decision. Null for
        /// every other action. The called tile is <see cref="Tile"/> for response calls.
        /// </summary>
        public IReadOnlyList<TileId>? MeldTiles { get; }

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

        public static RoundCommand Chi(
            string roundId,
            int seat,
            TileId calledTile,
            TileId handTileA,
            TileId handTileB) =>
            new RoundCommand(
                roundId,
                RoundActionKind.DeclareChi,
                seat,
                calledTile,
                null,
                null,
                null,
                new[] { handTileA, handTileB });

        public static RoundCommand Pon(
            string roundId,
            int seat,
            TileId calledTile,
            TileId handTileA,
            TileId handTileB) =>
            new RoundCommand(
                roundId,
                RoundActionKind.DeclarePon,
                seat,
                calledTile,
                null,
                null,
                null,
                new[] { handTileA, handTileB });

        public static RoundCommand OpenKan(
            string roundId,
            int seat,
            TileId calledTile,
            TileId handTileA,
            TileId handTileB,
            TileId handTileC) =>
            new RoundCommand(
                roundId,
                RoundActionKind.DeclareOpenKan,
                seat,
                calledTile,
                null,
                null,
                null,
                new[] { handTileA, handTileB, handTileC });

        public static RoundCommand ClosedKan(
            string roundId,
            int seat,
            TileId tileA,
            TileId tileB,
            TileId tileC,
            TileId tileD) =>
            new RoundCommand(
                roundId,
                RoundActionKind.DeclareClosedKan,
                seat,
                tileA,
                null,
                null,
                null,
                new[] { tileA, tileB, tileC, tileD });

        public static RoundCommand Riichi(
            string roundId,
            int seat,
            TileId discardTile,
            IReadOnlyList<int>? eligibleSeats = null) =>
            new RoundCommand(
                roundId,
                RoundActionKind.DeclareRiichi,
                seat,
                discardTile,
                eligibleSeats,
                null,
                null);

        private static IReadOnlyList<TileId>? CopyMeldTiles(IReadOnlyList<TileId>? tiles)
        {
            if (tiles == null)
            {
                return null;
            }

            var copy = new TileId[tiles.Count];
            for (int i = 0; i < tiles.Count; i++)
            {
                copy[i] = tiles[i];
            }

            return new ReadOnlyCollection<TileId>(copy);
        }
    }
}
