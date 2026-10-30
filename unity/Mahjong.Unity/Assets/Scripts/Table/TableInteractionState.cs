using System;
using System.Collections.Generic;
using Mahjong.Contracts;

namespace Mahjong.Unity.Table
{
    public enum TableIntentKind
    {
        SubmitAction = 0,
        SubmitTileMark = 1,
        SubmitSleeveBloom = 2,
        SubmitSleeveBloomChoice = 3,
        SubmitSleeveBloomOffTurn = 4,
        SubmitSleeveBloomOffTurnChoice = 5,
        SubmitBeamSwap = 6,
        SubmitBeamSwapChoice = 7,
        SubmitShufflePlan = 8,
        NextRound = 9,
        ClaimControl = 10,
        ReleaseControl = 11
    }

    public enum TableSelectionKind
    {
        None = 0,
        Action = 1,
        TileMark = 2,
        SleeveBloom = 3,
        BeamSwap = 4,
        Preparation = 5
    }

    public enum TablePendingOutcome
    {
        Applied = 1,
        Recorded = 2,
        Rejected = 3,
        Unknown = 4,
        Abandoned = 5
    }

    public static class TableInteractionCodes
    {
        public const string Applied = "APPLIED";
        public const string SelectionCleared = "SELECTION_CLEARED";
        public const string NoConfirmedView = "NO_CONFIRMED_VIEW";
        public const string NoPrivateView = "NO_PRIVATE_VIEW";
        public const string NotSubmittable = "NOT_SUBMITTABLE";
        public const string SelectionRequired = "SELECTION_REQUIRED";
        public const string SelectionExpired = "SELECTION_EXPIRED";
        public const string NotACandidate = "NOT_A_CANDIDATE";
        public const string StaleSelection = "STALE_SELECTION";
        public const string AlreadyPending = "ALREADY_PENDING";
        public const string PendingConflict = "PENDING_CONFLICT";
        public const string NoPending = "NO_PENDING";
        public const string PendingCleared = "PENDING_CLEARED";
        public const string InvalidIntentKind = "INTENT_KIND_UNSUPPORTED";
        public const string InvalidConfirmedData = "INVALID_CONFIRMED_DATA";
        public const string StaleGeneration = "STALE_GENERATION";
        public const string StaleBinding = "STALE_BINDING";
        public const string StaleState = "STALE_STATE";
        public const string IdentityMismatch = "IDENTITY_MISMATCH";
    }

    public sealed class TableSelection
    {
        internal TableSelection(
            TableSelectionKind kind,
            PlayerLegalActionState? action,
            IReadOnlyList<uint>? tileMarkTileIds,
            bool? sleeveOffTurn,
            SleeveBloomChoice? sleeveChoice,
            BeamSwapChoice? beamChoice,
            uint? handTileId,
            PlayerBeamSwapCandidateState? beamTarget,
            bool? seeThrough,
            string sourceRoundId,
            string sourceFingerprint)
        {
            Kind = kind;
            Action = action;
            TileMarkTileIds = PlayerTableCopy.UInt32List(tileMarkTileIds);
            SleeveOffTurn = sleeveOffTurn;
            SleeveChoice = sleeveChoice;
            BeamChoice = beamChoice;
            HandTileId = handTileId;
            BeamTarget = beamTarget;
            SeeThrough = seeThrough;
            SourceRoundId = sourceRoundId ?? string.Empty;
            SourceFingerprint = sourceFingerprint ?? string.Empty;
        }

        public TableSelectionKind Kind { get; }

        public PlayerLegalActionState? Action { get; }

        public IReadOnlyList<uint> TileMarkTileIds { get; }

        public bool? SleeveOffTurn { get; }

        public SleeveBloomChoice? SleeveChoice { get; }

        public BeamSwapChoice? BeamChoice { get; }

        public uint? HandTileId { get; }

        public PlayerBeamSwapCandidateState? BeamTarget { get; }

        public bool? SeeThrough { get; }

        public string SourceRoundId { get; }

        internal string SourceFingerprint { get; }

        public bool HasSelection => Kind != TableSelectionKind.None;

        internal static readonly TableSelection None = new TableSelection(
            TableSelectionKind.None,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            string.Empty,
            string.Empty);
    }

    public sealed class TablePendingIntent
    {
        public TablePendingIntent(
            TableCommandIntent intent,
            TablePendingOutcome? lastOutcome,
            bool needsReconciliation,
            long createdAtMonotonicMs)
        {
            Intent = intent ?? throw new ArgumentNullException(nameof(intent));
            LastOutcome = lastOutcome;
            NeedsReconciliation = needsReconciliation;
            CreatedAtMonotonicMs = createdAtMonotonicMs;
        }

        public TableCommandIntent Intent { get; }

        public TablePendingOutcome? LastOutcome { get; }

        /// <summary>
        /// True after an UNKNOWN outcome. The UI must not resubmit or rebuild a
        /// new request; only the existing Client reconciliation may resolve it.
        /// </summary>
        public bool NeedsReconciliation { get; }

        public long CreatedAtMonotonicMs { get; }
    }

    public sealed class TableInteractionOutcome
    {
        private TableInteractionOutcome(bool accepted, string code, string? message, TableCommandIntent? intent)
        {
            Accepted = accepted;
            Code = code;
            Message = message;
            Intent = intent;
        }

        public bool Accepted { get; }

        public string Code { get; }

        public string? Message { get; }

        public TableCommandIntent? Intent { get; }

        public static TableInteractionOutcome Ok(string code, TableCommandIntent? intent = null)
        {
            return new TableInteractionOutcome(true, code, null, intent);
        }

        public static TableInteractionOutcome Rejected(string code, string? message = null)
        {
            return new TableInteractionOutcome(false, code, message, null);
        }
    }

    /// <summary>
    /// UI-side interaction gate over one confirmed table view. It records the
    /// user's explicit selection, one pending intent at a time, and clears old
    /// selections on focus/pause/identity/decision/offer/baseline changes.
    ///
    /// Completion of an animation never resolves a pending intent. Only an
    /// explicit Client receipt/reconciliation outcome does. A new decision or
    /// offer clears the old selection but does not invent a new request id.
    /// </summary>
    public sealed class TableInteractionState
    {
        private readonly TableCommandIntentFactory _factory;
        private TableSelection _selection = TableSelection.None;
        private TablePendingIntent? _pending;
        private PlayerTableViewState? _view;
        private PlayerTableIdentity? _requiredIdentity;
        private long _requiredGeneration = -1;
        private ulong _requiredStateVersion;
        private long _revision;
        // Last trustworthy owner-seat authority. A snapshot that omits the
        // optional control projection does not erase this memory: missing data
        // is not proof that the authority changed or the request was resolved.
        private bool _hasKnownControlAuthority;
        private ulong _knownControlEpoch;
        private bool _knownControlledByComputer;

        public TableInteractionState(TableCommandIntentFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public PlayerTableViewState? View => _view;

        public TableSelection Selection => _selection;

        public TablePendingIntent? Pending => _pending;

        public long Revision => _revision;

        public TableInteractionOutcome ApplyConfirmedView(PlayerTableViewState view)
        {
            if (view == null)
            {
                throw new ArgumentNullException(nameof(view));
            }

            string? rejection = EvaluateViewFence(view);
            if (rejection != null)
            {
                return Reject(rejection, "The confirmed view does not satisfy the accepted interaction identity fence.");
            }

            bool bindingChanged = _view != null && !SameBindingIdentity(_view.Identity, view.Identity);
            bool privateDisappeared = view.Own == null;
            PlayerControlState? incomingControl = view.Control;
            bool controlAuthorityChanged = _hasKnownControlAuthority &&
                                           incomingControl != null &&
                                           (_knownControlEpoch != incomingControl.ControlEpoch ||
                                            _knownControlledByComputer != incomingControl.ControlledByComputer);
            if (incomingControl != null)
            {
                _hasKnownControlAuthority = true;
                _knownControlEpoch = incomingControl.ControlEpoch;
                _knownControlledByComputer = incomingControl.ControlledByComputer;
            }

            // A missing optional control projection is not a control change:
            // the old request may still be in flight or awaiting reconciliation,
            // so it must keep its pending protection until a trustworthy
            // authority transition or an explicit lifecycle/receipt resolves it.
            if (bindingChanged)
            {
                _pending = null;
                ClearSelectionCore();
            }

            if (controlAuthorityChanged)
            {
                // A control transition invalidates any action selected under the
                // old controller and any control request already superseded by a
                // confirmed state. The receipt path still reports its own outcome.
                _pending = null;
                ClearSelectionCore();
            }

            if (privateDisappeared || view.Paused)
            {
                ClearSelectionCore();
            }
            else
            {
                ClearStaleSelection(view);
            }

            // A reset/identity switch is satisfied by the first view that
            // passes the fence. The monotonic generation floor remains, so an
            // older view can never roll the interaction back afterwards.
            _requiredIdentity = null;
            _requiredGeneration = Math.Max(_requiredGeneration, view.Identity.Generation);
            _requiredStateVersion = view.Identity.StateVersion;
            _view = view;
            _revision++;
            return TableInteractionOutcome.Ok(bindingChanged ? TableInteractionCodes.PendingCleared : TableInteractionCodes.Applied);
        }

        public TableInteractionOutcome SelectAction(PlayerLegalActionState action)
        {
            if (action == null)
            {
                return Reject(TableInteractionCodes.NotACandidate, "A legal action is required.");
            }

            PlayerTableViewState? view = _view;
            if (!Selectable(view, out TableInteractionOutcome? failure))
            {
                return failure!;
            }

            PlayerDecisionState? currentDecision = view!.Own!.Decision;
            if (currentDecision == null)
            {
                return Reject(TableInteractionCodes.SelectionRequired, "The confirmed view has no decision.");
            }

            PlayerDecisionState decision = currentDecision;
            if (decision.Countdown.IsExpired(_factory.NowMonotonicMilliseconds))
            {
                ClearSelectionCore();
                _revision++;
                return Reject(TableInteractionCodes.SelectionExpired, "The decision deadline has expired.");
            }

            if (action.Index < 0 || action.Index >= decision.Candidates.Count || !SameAction(decision, action))
            {
                return Reject(TableInteractionCodes.StaleSelection, "The action does not belong to the current confirmed decision.");
            }

            if (action.Action == PlayerActionKind.Unknown || action.Action == PlayerActionKind.StartStacking || action.Action == PlayerActionKind.DealComplete)
            {
                return Reject(TableInteractionCodes.NotACandidate, "The action is outside the player-submittable set.");
            }

            _selection = new TableSelection(
                TableSelectionKind.Action,
                action,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                view.Identity.RoundId,
                DecisionFingerprint(view));
            _revision++;
            return TableInteractionOutcome.Ok(TableInteractionCodes.Applied);
        }

        public TableInteractionOutcome SelectDiscardTile(uint tileId)
        {
            PlayerTableViewState? view = _view;
            if (!Selectable(view, out TableInteractionOutcome? failure))
            {
                return failure!;
            }

            PlayerDecisionState? currentDecision = view!.Own!.Decision;
            if (currentDecision == null)
            {
                return Reject(TableInteractionCodes.SelectionRequired, "The confirmed view has no decision.");
            }

            PlayerDecisionState decision = currentDecision;
            if (decision.Countdown.IsExpired(_factory.NowMonotonicMilliseconds))
            {
                ClearSelectionCore();
                _revision++;
                return Reject(TableInteractionCodes.SelectionExpired, "The decision deadline has expired.");
            }

            PlayerLegalActionState? found = null;
            for (int i = 0; i < decision.Candidates.Count; i++)
            {
                PlayerLegalActionState candidate = decision.Candidates[i];
                if (candidate.Action == PlayerActionKind.DiscardTile && Contains(candidate.TileIds, tileId))
                {
                    if (found != null)
                    {
                        return Reject(TableInteractionCodes.StaleSelection, "The discard tile maps to more than one candidate.");
                    }

                    found = candidate;
                }
            }

            if (found == null)
            {
                return Reject(TableInteractionCodes.NotACandidate, "The tile is not a confirmed discard candidate.");
            }

            return SelectAction(found);
        }

        public TableInteractionOutcome SelectTileMark(IReadOnlyList<uint> tileIds)
        {
            PlayerTableViewState? view = _view;
            if (!Selectable(view, out TableInteractionOutcome? failure))
            {
                return failure!;
            }

            PlayerDecisionState? currentDecision = view!.Own!.Decision;
            if (currentDecision == null)
            {
                return Reject(TableInteractionCodes.SelectionRequired, "The confirmed view has no decision.");
            }

            PlayerDecisionState decision = currentDecision;
            if (decision.Countdown.IsExpired(_factory.NowMonotonicMilliseconds))
            {
                ClearSelectionCore();
                _revision++;
                return Reject(TableInteractionCodes.SelectionExpired, "The decision deadline has expired.");
            }

            PlayerTileMarkState? mark = view.Own!.Cheat?.TileMark;
            if (mark == null)
            {
                return Reject(TableInteractionCodes.SelectionRequired, "No confirmed tile-mark selection is available.");
            }

            if (tileIds == null || tileIds.Count != 4)
            {
                return Reject(TableInteractionCodes.StaleSelection, "Tile mark requires exactly four selected tiles.");
            }

            var seen = new HashSet<uint>();
            for (int i = 0; i < tileIds.Count; i++)
            {
                uint tile = tileIds[i];
                if (!seen.Add(tile) || !Contains(mark.SelectableTileIds, tile))
                {
                    return Reject(TableInteractionCodes.StaleSelection, "Tile mark selection must be four distinct confirmed candidates.");
                }
            }

            _selection = new TableSelection(
                TableSelectionKind.TileMark,
                null,
                tileIds,
                null,
                null,
                null,
                null,
                null,
                null,
                view.Identity.RoundId,
                TileMarkFingerprint(view));
            _revision++;
            return TableInteractionOutcome.Ok(TableInteractionCodes.Applied);
        }

        public TableInteractionOutcome SelectSleeveBloom(bool offTurn, SleeveBloomChoice choice)
        {
            PlayerTableViewState? view = _view;
            if (!Selectable(view, out TableInteractionOutcome? failure))
            {
                return failure!;
            }

            PlayerCheatState? cheat = view!.Own!.Cheat;
            PlayerSleeveBloomState? offer = offTurn ? cheat?.OffTurnSleeveBloom : cheat?.ImmediateSleeveBloom;
            if (offer == null)
            {
                return Reject(TableInteractionCodes.SelectionRequired, "No confirmed sleeve-bloom offer is available.");
            }

            if (offer.Countdown != null && offer.Countdown.IsExpired(_factory.NowMonotonicMilliseconds))
            {
                ClearSelectionCore();
                _revision++;
                return Reject(TableInteractionCodes.SelectionExpired, "The sleeve-bloom offer deadline has expired.");
            }

            if (choice != SleeveBloomChoice.First && choice != SleeveBloomChoice.Second)
            {
                return Reject(TableInteractionCodes.NotACandidate, "The sleeve-bloom choice must be FIRST or SECOND.");
            }

            if (choice == SleeveBloomChoice.First && !offer.FirstTileId.HasValue)
            {
                return Reject(TableInteractionCodes.NotACandidate, "The first sleeve-bloom candidate is absent.");
            }

            if (choice == SleeveBloomChoice.Second && !offer.SecondTileId.HasValue)
            {
                return Reject(TableInteractionCodes.NotACandidate, "The second sleeve-bloom candidate is absent.");
            }

            _selection = new TableSelection(
                TableSelectionKind.SleeveBloom,
                null,
                null,
                offTurn,
                choice,
                null,
                null,
                null,
                null,
                view.Identity.RoundId,
                SleeveFingerprint(view));
            _revision++;
            return TableInteractionOutcome.Ok(TableInteractionCodes.Applied);
        }

        public TableInteractionOutcome SelectBeamSwap(
            BeamSwapChoice choice,
            uint? handTileId,
            PlayerBeamSwapCandidateState? target)
        {
            PlayerTableViewState? view = _view;
            if (!Selectable(view, out TableInteractionOutcome? failure))
            {
                return failure!;
            }

            PlayerBeamSwapState? offer = view!.Own!.Cheat?.BeamSwap;
            if (offer == null)
            {
                return Reject(TableInteractionCodes.SelectionRequired, "No confirmed beam-swap offer is available.");
            }

            if (offer.Countdown != null && offer.Countdown.IsExpired(_factory.NowMonotonicMilliseconds))
            {
                ClearSelectionCore();
                _revision++;
                return Reject(TableInteractionCodes.SelectionExpired, "The beam-swap offer deadline has expired.");
            }

            if (choice == BeamSwapChoice.Swap)
            {
                if (!handTileId.HasValue || !Contains(offer.SelectableHandTileIds, handTileId.Value))
                {
                    return Reject(TableInteractionCodes.NotACandidate, "The hand tile is not a confirmed beam-swap candidate.");
                }

                if (target == null || !ContainsCandidate(offer, target))
                {
                    return Reject(TableInteractionCodes.NotACandidate, "The target slot is not a confirmed beam-swap candidate.");
                }
            }
            else if (choice != BeamSwapChoice.KeepOriginal)
            {
                return Reject(TableInteractionCodes.NotACandidate, "The beam-swap choice must be SWAP or KEEP_ORIGINAL.");
            }

            _selection = new TableSelection(
                TableSelectionKind.BeamSwap,
                null,
                null,
                null,
                null,
                choice,
                choice == BeamSwapChoice.Swap ? handTileId : null,
                choice == BeamSwapChoice.Swap ? target : null,
                null,
                view.Identity.RoundId,
                BeamFingerprint(view));
            _revision++;
            return TableInteractionOutcome.Ok(TableInteractionCodes.Applied);
        }

        public TableInteractionOutcome SelectPreparation(bool seeThrough)
        {
            PlayerTableViewState? view = _view;
            if (!Selectable(view, out TableInteractionOutcome? failure))
            {
                return failure!;
            }

            PlayerPreparationState? preparation = view!.Own!.Cheat?.Preparation;
            if (preparation == null)
            {
                return Reject(TableInteractionCodes.SelectionRequired, "No confirmed shuffle preparation is available.");
            }

            if (preparation.Confirmed)
            {
                return Reject(TableInteractionCodes.NotSubmittable, "The confirmed preparation is already confirmed.");
            }

            if (preparation.Countdown.IsExpired(_factory.NowMonotonicMilliseconds))
            {
                ClearSelectionCore();
                _revision++;
                return Reject(TableInteractionCodes.SelectionExpired, "The preparation deadline has expired.");
            }

            if (seeThrough && !preparation.SeeThroughEnabled)
            {
                return Reject(TableInteractionCodes.NotACandidate, "See-through is not enabled in the confirmed preparation.");
            }

            _selection = new TableSelection(
                TableSelectionKind.Preparation,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                seeThrough,
                view.Identity.RoundId,
                PreparationFingerprint(view));
            _revision++;
            return TableInteractionOutcome.Ok(TableInteractionCodes.Applied);
        }

        public TableInteractionOutcome ClearSelection()
        {
            if (!_selection.HasSelection)
            {
                return TableInteractionOutcome.Ok(TableInteractionCodes.SelectionCleared);
            }

            ClearSelectionCore();
            _revision++;
            return TableInteractionOutcome.Ok(TableInteractionCodes.SelectionCleared);
        }

        public TableInteractionOutcome OnFocusLost()
        {
            ClearSelectionCore();
            _revision++;
            return TableInteractionOutcome.Ok(TableInteractionCodes.SelectionCleared);
        }

        public TableInteractionOutcome OnPause(bool paused)
        {
            if (paused)
            {
                ClearSelectionCore();
            }

            _revision++;
            return TableInteractionOutcome.Ok(TableInteractionCodes.SelectionCleared);
        }

        public TableInteractionOutcome OnBaselineReset(long generation)
        {
            if (generation < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(generation));
            }

            PlayerTableIdentity? retained = _view?.Identity ?? _requiredIdentity;
            ulong retainedState = _view?.Identity.StateVersion ?? _requiredStateVersion;
            if (retained != null && generation <= retained.Generation)
            {
                // A same-generation reset must not forget which binding/state
                // was already accepted: the next view still has to satisfy the
                // retained binding and state floor.
                _requiredIdentity = retained;
                _requiredStateVersion = retainedState;
                _requiredGeneration = retained.Generation;
            }
            else
            {
                _requiredIdentity = null;
                _requiredStateVersion = 0;
                _requiredGeneration = Math.Max(_requiredGeneration, generation);
            }

            _pending = null;
            _view = null;
            _hasKnownControlAuthority = false;
            ClearSelectionCore();
            _revision++;
            return TableInteractionOutcome.Ok(TableInteractionCodes.PendingCleared);
        }

        public TableInteractionOutcome OnIdentityChanged(PlayerTableIdentity identity)
        {
            if (identity == null)
            {
                throw new ArgumentNullException(nameof(identity));
            }

            _requiredGeneration = Math.Max(_requiredGeneration, identity.Generation);
            _requiredIdentity = identity;
            _requiredStateVersion = identity.StateVersion;
            _pending = null;
            _view = null;
            _hasKnownControlAuthority = false;
            ClearSelectionCore();
            _revision++;
            return TableInteractionOutcome.Ok(TableInteractionCodes.PendingCleared);
        }

        public TableInteractionOutcome BuildIntent(TableIntentKind kind, TableCommandOwnerContext owner)
        {
            if (owner == null)
            {
                throw new ArgumentNullException(nameof(owner));
            }

            PlayerTableViewState? view = _view;
            if (view == null)
            {
                return Reject(TableInteractionCodes.NoConfirmedView, "No confirmed table view has been applied.");
            }

            try
            {
                TableCommandIntent intent = Build(kind, view, owner);
                if (_pending != null)
                {
                    if (_pending.Intent.Key.Equals(intent.Key))
                    {
                        return TableInteractionOutcome.Ok(TableInteractionCodes.AlreadyPending);
                    }

                    return Reject(TableInteractionCodes.PendingConflict, "Another intent is already pending; resolve or reconcile it first.");
                }

                _pending = new TablePendingIntent(intent, null, false, _factory.NowMonotonicMilliseconds);
                _revision++;
                return TableInteractionOutcome.Ok(TableInteractionCodes.Applied, intent);
            }
            catch (TableIntentUnavailableException exception)
            {
                if (exception.Code == "DECISION_EXPIRED" ||
                    exception.Code == "OFFER_EXPIRED" ||
                    exception.Code == "PREPARATION_EXPIRED")
                {
                    ClearSelectionCore();
                    _revision++;
                }

                return TableInteractionOutcome.Rejected(exception.Code, exception.Message);
            }
            catch (ArgumentException exception)
            {
                return TableInteractionOutcome.Rejected(TableInteractionCodes.InvalidConfirmedData, exception.Message);
            }
            catch (CommandValidationException exception)
            {
                return TableInteractionOutcome.Rejected(TableInteractionCodes.InvalidConfirmedData, exception.Message);
            }
        }

        public TableInteractionOutcome MarkDeliveryUnknown()
        {
            if (_pending == null)
            {
                return TableInteractionOutcome.Rejected(TableInteractionCodes.NoPending, "There is no pending intent.");
            }

            _pending = new TablePendingIntent(
                _pending.Intent,
                TablePendingOutcome.Unknown,
                true,
                _pending.CreatedAtMonotonicMs);
            _revision++;
            return TableInteractionOutcome.Ok(TableInteractionCodes.Applied);
        }

        public TableInteractionOutcome ApplyClientOutcome(TablePendingOutcome outcome)
        {
            if (_pending == null)
            {
                return TableInteractionOutcome.Rejected(TableInteractionCodes.NoPending, "There is no pending intent.");
            }

            if (outcome == TablePendingOutcome.Unknown)
            {
                _pending = new TablePendingIntent(
                    _pending.Intent,
                    TablePendingOutcome.Unknown,
                    true,
                    _pending.CreatedAtMonotonicMs);
                _revision++;
                return TableInteractionOutcome.Ok(TableInteractionCodes.Applied);
            }

            if ((outcome == TablePendingOutcome.Applied || outcome == TablePendingOutcome.Recorded) &&
                IsControlIntent(_pending.Intent.Kind))
            {
                // A control receipt is authoritative for the command result, but
                // the visible authority only changes with a confirmed snapshot.
                // Keep the request pending so the old control buttons cannot be
                // pressed again against the pre-change epoch.
                _pending = new TablePendingIntent(
                    _pending.Intent,
                    outcome,
                    false,
                    _pending.CreatedAtMonotonicMs);
                _revision++;
                return TableInteractionOutcome.Ok(TableInteractionCodes.Applied);
            }

            if (outcome == TablePendingOutcome.Applied ||
                outcome == TablePendingOutcome.Recorded ||
                outcome == TablePendingOutcome.Rejected ||
                outcome == TablePendingOutcome.Abandoned)
            {
                _pending = null;
                ClearSelectionCore();
                _revision++;
                return TableInteractionOutcome.Ok(TableInteractionCodes.Applied);
            }

            return TableInteractionOutcome.Rejected(TableInteractionCodes.InvalidConfirmedData, "Unknown pending outcome.");
        }

        private TableCommandIntent Build(TableIntentKind kind, PlayerTableViewState view, TableCommandOwnerContext owner)
        {
            switch (kind)
            {
                case TableIntentKind.SubmitAction:
                    RequireSelection(TableSelectionKind.Action);
                    return _factory.CreateSubmitAction(view, _selection.Action!, owner);
                case TableIntentKind.SubmitTileMark:
                    RequireSelection(TableSelectionKind.TileMark);
                    return _factory.CreateSubmitTileMark(view, _selection.TileMarkTileIds, owner);
                case TableIntentKind.SubmitSleeveBloom:
                    return _factory.CreateSubmitSleeveBloom(view, owner);
                case TableIntentKind.SubmitSleeveBloomChoice:
                    RequireSelection(TableSelectionKind.SleeveBloom);
                    if (_selection.SleeveOffTurn!.Value)
                    {
                        throw new TableIntentUnavailableException(
                            TableInteractionCodes.InvalidIntentKind,
                            "The selected sleeve-bloom offer is off-turn; use SubmitSleeveBloomOffTurnChoice.");
                    }

                    return _factory.CreateSubmitSleeveBloomChoice(view, false, _selection.SleeveChoice!.Value, owner);
                case TableIntentKind.SubmitSleeveBloomOffTurn:
                    return _factory.CreateSubmitSleeveBloomOffTurn(view, owner);
                case TableIntentKind.SubmitSleeveBloomOffTurnChoice:
                    RequireSelection(TableSelectionKind.SleeveBloom);
                    if (!_selection.SleeveOffTurn!.Value)
                    {
                        throw new TableIntentUnavailableException(
                            TableInteractionCodes.InvalidIntentKind,
                            "The selected sleeve-bloom offer is immediate; use SubmitSleeveBloomChoice.");
                    }

                    return _factory.CreateSubmitSleeveBloomChoice(view, true, _selection.SleeveChoice!.Value, owner);
                case TableIntentKind.SubmitBeamSwap:
                    return _factory.CreateSubmitBeamSwap(view, owner);
                case TableIntentKind.SubmitBeamSwapChoice:
                    RequireSelection(TableSelectionKind.BeamSwap);
                    return _factory.CreateSubmitBeamSwapChoice(
                        view,
                        _selection.BeamChoice!.Value,
                        _selection.HandTileId,
                        _selection.BeamTarget,
                        owner);
                case TableIntentKind.SubmitShufflePlan:
                    RequireSelection(TableSelectionKind.Preparation);
                    return _factory.CreateSubmitShufflePlan(view, _selection.SeeThrough!.Value, owner);
                case TableIntentKind.NextRound:
                    return _factory.CreateNextRound(view, owner);
                case TableIntentKind.ClaimControl:
                    return _factory.CreateClaimControl(view, owner);
                case TableIntentKind.ReleaseControl:
                    return _factory.CreateReleaseControl(view, owner);
                default:
                    throw new TableIntentUnavailableException(
                        TableInteractionCodes.InvalidIntentKind,
                        "Unsupported table intent kind.");
            }
        }

        private void RequireSelection(TableSelectionKind expected)
        {
            if (_selection.Kind != expected || !_selection.HasSelection)
            {
                throw new TableIntentUnavailableException(
                    TableInteractionCodes.SelectionRequired,
                    "A confirmed " + expected + " selection is required.");
            }
        }

        private bool Selectable(PlayerTableViewState? view, out TableInteractionOutcome? failure)
        {
            if (view == null)
            {
                failure = Reject(TableInteractionCodes.NoConfirmedView, "No confirmed table view has been applied.");
                return false;
            }

            if (view.Own == null || !view.Identity.IsPrivateAuthorized)
            {
                failure = Reject(TableInteractionCodes.NoPrivateView, "No authorized player private view is available.");
                return false;
            }

            if (!view.CanSubmit)
            {
                failure = Reject(TableInteractionCodes.NotSubmittable, "The confirmed view is not submittable.");
                return false;
            }

            if (view.Own.Control == null)
            {
                failure = Reject(
                    TableCommandIntentFactory.ControlStateUnavailable,
                    "The confirmed view has no authoritative owner control state.");
                return false;
            }

            if (view.Own.Control.ControlledByComputer)
            {
                failure = Reject(
                    TableCommandIntentFactory.ControlAutomated,
                    "The computer currently controls this seat.");
                return false;
            }

            failure = null;
            return true;
        }

        private void ClearStaleSelection(PlayerTableViewState view)
        {
            if (!_selection.HasSelection)
            {
                return;
            }

            if (!string.Equals(_selection.SourceRoundId, view.Identity.RoundId, StringComparison.Ordinal))
            {
                ClearSelectionCore();
                return;
            }

            string current = _selection.Kind switch
            {
                TableSelectionKind.Action => DecisionFingerprint(view),
                TableSelectionKind.TileMark => TileMarkFingerprint(view),
                TableSelectionKind.SleeveBloom => SleeveFingerprint(view),
                TableSelectionKind.BeamSwap => BeamFingerprint(view),
                TableSelectionKind.Preparation => PreparationFingerprint(view),
                _ => string.Empty
            };

            if (!string.Equals(current, _selection.SourceFingerprint, StringComparison.Ordinal))
            {
                ClearSelectionCore();
            }
        }

        private string? EvaluateViewFence(PlayerTableViewState view)
        {
            if (_requiredGeneration >= 0 && view.Identity.Generation < _requiredGeneration)
            {
                return TableInteractionCodes.StaleGeneration;
            }

            if (_requiredIdentity != null)
            {
                if (!SameBindingIdentity(view.Identity, _requiredIdentity))
                {
                    return TableInteractionCodes.IdentityMismatch;
                }

                if (view.Identity.Generation < _requiredIdentity.Generation)
                {
                    return TableInteractionCodes.StaleGeneration;
                }

                if (view.Identity.Generation == _requiredIdentity.Generation &&
                    view.Identity.StateVersion < _requiredStateVersion)
                {
                    return TableInteractionCodes.StaleState;
                }

                return null;
            }

            if (_view == null)
            {
                return null;
            }

            PlayerTableIdentity current = _view.Identity;
            if (view.Identity.Generation < current.Generation)
            {
                return TableInteractionCodes.StaleGeneration;
            }

            if (view.Identity.Generation == current.Generation)
            {
                if (view.Identity.BindingEpoch < current.BindingEpoch)
                {
                    return TableInteractionCodes.StaleBinding;
                }

                if (view.Identity.BindingEpoch == current.BindingEpoch)
                {
                    if (!SameBindingIdentity(view.Identity, current))
                    {
                        return TableInteractionCodes.IdentityMismatch;
                    }

                    if (view.Identity.StateVersion < current.StateVersion)
                    {
                        return TableInteractionCodes.StaleState;
                    }

                    if (!string.Equals(view.Identity.RoundId, current.RoundId, StringComparison.Ordinal) &&
                        view.Identity.StateVersion == current.StateVersion)
                    {
                        return TableInteractionCodes.StaleState;
                    }
                }
            }

            return null;
        }

        private void ClearSelectionCore()
        {
            _selection = TableSelection.None;
        }

        private static bool IsControlIntent(CommandKind kind)
        {
            return kind == CommandKind.ClaimControl || kind == CommandKind.ReleaseControl;
        }

        private static bool SameBindingIdentity(PlayerTableIdentity left, PlayerTableIdentity right)
        {
            return left.Generation == right.Generation &&
                   string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal) &&
                   string.Equals(left.RoomId, right.RoomId, StringComparison.Ordinal) &&
                   left.Role == right.Role &&
                   left.Seat == right.Seat &&
                   left.BindingEpoch == right.BindingEpoch &&
                   string.Equals(left.ScopeId, right.ScopeId, StringComparison.Ordinal) &&
                   left.ConnectionEpoch == right.ConnectionEpoch &&
                   left.OwnerEpoch == right.OwnerEpoch;
        }

        private static bool SameAction(PlayerDecisionState decision, PlayerLegalActionState action)
        {
            if (action.Index < 0 || action.Index >= decision.Candidates.Count)
            {
                return false;
            }

            PlayerLegalActionState current = decision.Candidates[action.Index];
            return current.RawActionType == action.RawActionType &&
                   string.Equals(current.OptionId, action.OptionId, StringComparison.Ordinal) &&
                   TilesEqual(current.TileIds, action.TileIds) &&
                   string.Equals(action.DecisionId, decision.DecisionId, StringComparison.Ordinal) &&
                   action.ControlEpoch.HasValue &&
                   action.ControlEpoch.Value == decision.ControlEpoch &&
                   string.Equals(action.RoundId, decision.RoundId, StringComparison.Ordinal);
        }

        private static bool Contains(IReadOnlyList<uint> values, uint value)
        {
            for (int i = 0; i < values.Count; i++)
            {
                if (values[i] == value)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsCandidate(PlayerBeamSwapState offer, PlayerBeamSwapCandidateState candidate)
        {
            for (int i = 0; i < offer.Candidates.Count; i++)
            {
                PlayerBeamSwapCandidateState current = offer.Candidates[i];
                if (current.Slot == candidate.Slot &&
                    current.SlotIndex == candidate.SlotIndex &&
                    current.SlotKind == candidate.SlotKind &&
                    current.TileId == candidate.TileId)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TilesEqual(IReadOnlyList<uint> left, IReadOnlyList<uint> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            for (int i = 0; i < left.Count; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static string DecisionFingerprint(PlayerTableViewState view)
        {
            PlayerDecisionState? decision = view.Own?.Decision;
            if (decision == null)
            {
                return string.Empty;
            }

            return view.Identity.RoundId + "|" + decision.DecisionId + "|"
                + decision.ControlEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|"
                + decision.Candidates.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string TileMarkFingerprint(PlayerTableViewState view)
        {
            PlayerTileMarkState? mark = view.Own?.Cheat?.TileMark;
            if (mark == null)
            {
                return string.Empty;
            }

            var values = new List<uint>(mark.SelectableTileIds);
            values.Sort();
            return view.Identity.RoundId + "|" + DecisionFingerprint(view) + "|" + Join(values);
        }

        private static string SleeveFingerprint(PlayerTableViewState view)
        {
            PlayerCheatState? cheat = view.Own?.Cheat;
            if (cheat == null)
            {
                return string.Empty;
            }

            return view.Identity.RoundId + "|"
                + (cheat.ImmediateSleeveBloom?.OfferId ?? string.Empty) + "|"
                + (cheat.ImmediateSleeveBloom?.WindowId ?? string.Empty) + "|"
                + (cheat.OffTurnSleeveBloom?.OfferId ?? string.Empty) + "|"
                + (cheat.OffTurnSleeveBloom?.WindowId ?? string.Empty) + "|"
                + cheat.WindowId;
        }

        private static string BeamFingerprint(PlayerTableViewState view)
        {
            PlayerBeamSwapState? beam = view.Own?.Cheat?.BeamSwap;
            if (beam == null)
            {
                return string.Empty;
            }

            string key = view.Identity.RoundId + "|" + beam.OfferId;
            for (int i = 0; i < beam.Candidates.Count; i++)
            {
                PlayerBeamSwapCandidateState candidate = beam.Candidates[i];
                key += "|" + candidate.Slot.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "/" + candidate.SlotIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "/" + ((int)candidate.SlotKind).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "/" + (candidate.TileId.HasValue ? candidate.TileId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-");
            }

            return key;
        }

        private static string PreparationFingerprint(PlayerTableViewState view)
        {
            PlayerPreparationState? preparation = view.Own?.Cheat?.Preparation;
            if (preparation == null)
            {
                return string.Empty;
            }

            return view.Identity.RoundId + "|"
                + preparation.Target.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|"
                + (preparation.Confirmed ? "1" : "0") + "|"
                + (preparation.SeeThroughEnabled ? "1" : "0");
        }

        private static string Join(IReadOnlyList<uint> values)
        {
            var result = string.Empty;
            for (int i = 0; i < values.Count; i++)
            {
                result += values[i].ToString(System.Globalization.CultureInfo.InvariantCulture) + ",";
            }

            return result;
        }

        private static TableInteractionOutcome Reject(string code, string? message)
        {
            return TableInteractionOutcome.Rejected(code, message);
        }
    }
}
