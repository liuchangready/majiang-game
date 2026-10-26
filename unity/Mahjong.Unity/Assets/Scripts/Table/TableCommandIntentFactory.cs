using System;
using System.Collections.Generic;
using System.Text;
using Mahjong.Client;
using Mahjong.Contracts;

namespace Mahjong.Unity.Table
{
    /// <summary>
    /// Caller/owner-supplied command identity. Request ids and scopes are never
    /// generated here: the existing owner must supply the original values.
    /// </summary>
    public sealed class TableCommandOwnerContext
    {
        public TableCommandOwnerContext(
            RequestId requestId,
            RequestScopeId scopeId,
            StateVersion? expectedStateVersion = null,
            OwnerEpoch? ownerEpoch = null)
        {
            if (requestId.IsEmpty)
            {
                throw new ArgumentException("A command intent needs an existing request id.", nameof(requestId));
            }

            if (scopeId.IsEmpty)
            {
                throw new ArgumentException("A side-effecting command needs the server-issued scope id.", nameof(scopeId));
            }

            RequestId = requestId;
            ScopeId = scopeId;
            ExpectedStateVersion = expectedStateVersion;
            OwnerEpoch = ownerEpoch;
        }

        public RequestId RequestId { get; }

        public RequestScopeId ScopeId { get; }

        public StateVersion? ExpectedStateVersion { get; }

        public OwnerEpoch? OwnerEpoch { get; }
    }

    /// <summary>
    /// Equality key for one user intent. Repeated clicks with the same logical
    /// selection produce the same key even when tile order differs.
    /// </summary>
    public sealed class TableIntentKey : IEquatable<TableIntentKey>
    {
        private readonly string _canonical;

        internal TableIntentKey(CommandKind kind, string roundId, string decisionId, string offerId, string windowId, string variant)
        {
            Kind = kind;
            _canonical = Build(kind, roundId, decisionId, offerId, windowId, variant);
        }

        public CommandKind Kind { get; }

        public bool Equals(TableIntentKey? other)
        {
            return other != null && string.Equals(_canonical, other._canonical, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as TableIntentKey);
        }

        public override int GetHashCode()
        {
            return StringComparer.Ordinal.GetHashCode(_canonical);
        }

        public override string ToString()
        {
            return _canonical;
        }

        private static string Build(CommandKind kind, string roundId, string decisionId, string offerId, string windowId, string variant)
        {
            var builder = new StringBuilder(160);
            Append(builder, ((int)kind).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(builder, roundId ?? string.Empty);
            Append(builder, decisionId ?? string.Empty);
            Append(builder, offerId ?? string.Empty);
            Append(builder, windowId ?? string.Empty);
            Append(builder, variant ?? string.Empty);
            return builder.ToString();
        }

        private static void Append(StringBuilder builder, string value)
        {
            builder.Append(value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(value);
            builder.Append(';');
        }
    }

    /// <summary>
    /// A fully-built command intent. It is returned to the existing owner; this
    /// assembly does not submit it or create a session, outbox or channel.
    /// </summary>
    public sealed class TableCommandIntent
    {
        public TableCommandIntent(
            CommandKind kind,
            ProtocolCommand command,
            TableIntentKey key,
            string roundId,
            string? decisionId,
            ulong? controlEpoch,
            string? offerId,
            string? windowId)
        {
            Kind = kind;
            Command = command ?? throw new ArgumentNullException(nameof(command));
            Key = key ?? throw new ArgumentNullException(nameof(key));
            RoundId = roundId ?? string.Empty;
            DecisionId = decisionId;
            ControlEpoch = controlEpoch;
            OfferId = offerId;
            WindowId = windowId;
        }

        public CommandKind Kind { get; }

        public ProtocolCommand Command { get; }

        public TableIntentKey Key { get; }

        public string RoundId { get; }

        public string? DecisionId { get; }

        public ulong? ControlEpoch { get; }

        public string? OfferId { get; }

        public string? WindowId { get; }
    }

    public sealed class TableIntentUnavailableException : Exception
    {
        public TableIntentUnavailableException(string code, string message)
            : base(message)
        {
            Code = code ?? string.Empty;
        }

        public string Code { get; }
    }

    /// <summary>
    /// Converts a confirmed view plus an explicit selection into the existing
    /// canonical ProtocolCommand. Every field is copied from the confirmed
    /// snapshot or from the user's selection inside that snapshot's candidate
    /// list; none is derived from a domain rule.
    /// </summary>
    public sealed class TableCommandIntentFactory
    {
        public const string ControlPayloadFieldCollision = "CONTROL_PAYLOAD_FIELD_COLLISION";

        private readonly ITableClientClock _clock;

        public TableCommandIntentFactory(ITableClientClock clock)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public long NowMonotonicMilliseconds => _clock.MonotonicMilliseconds;

        public TableCommandIntent CreateSubmitAction(
            PlayerTableViewState view,
            PlayerLegalActionState action,
            TableCommandOwnerContext owner)
        {
            RequireSubmittable(view, owner);
            if (action == null)
            {
                throw Unavailable("ACTION_REQUIRED", "A legal action selection is required.");
            }

            PlayerDecisionState decision = RequireDecision(view);
            RequireNotExpired(decision.Countdown, "DECISION_EXPIRED");
            if (action.Action == PlayerActionKind.Unknown ||
                action.RawActionType < (uint)ProtocolActionType.DrawTile ||
                action.RawActionType > (uint)ProtocolActionType.DeclareAgariYame)
            {
                throw Unavailable("ACTION_NOT_SUBMITTABLE", "The selected action is outside the player-submittable set.");
            }

            if (!SameAction(decision, action))
            {
                throw Unavailable("STALE_DECISION", "The selected action does not belong to the current confirmed decision.");
            }

            BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                view.Identity.RoundId,
                decision.DecisionId,
                decision.ControlEpoch,
                (ProtocolActionType)action.RawActionType,
                string.IsNullOrEmpty(action.OptionId) ? null : action.OptionId,
                action.TileIds.Count == 0 ? null : action.TileIds);

            ProtocolCommand command = CreateCommand(
                CommandKind.SubmitAction,
                payload,
                view,
                owner,
                view.Identity.RoundId,
                decision.DecisionId,
                decision.ControlEpoch);

            return new TableCommandIntent(
                CommandKind.SubmitAction,
                command,
                Key(CommandKind.SubmitAction, view, decision.DecisionId, null, null, ActionVariant(action)),
                view.Identity.RoundId,
                decision.DecisionId,
                decision.ControlEpoch,
                null,
                null);
        }

        public TableCommandIntent CreateSubmitTileMark(
            PlayerTableViewState view,
            IReadOnlyList<uint> tileIds,
            TableCommandOwnerContext owner)
        {
            RequireSubmittable(view, owner);
            PlayerDecisionState decision = RequireDecision(view);
            RequireNotExpired(decision.Countdown, "DECISION_EXPIRED");
            PlayerTileMarkState mark = view.Own!.Cheat?.TileMark
                ?? throw Unavailable("TILE_MARK_UNAVAILABLE", "The confirmed private view has no tile-mark selection.");

            if (!string.Equals(mark.RoundId, view.Identity.RoundId, StringComparison.Ordinal))
            {
                throw Unavailable("STALE_ROUND", "The tile-mark selection belongs to another round.");
            }

            uint[] selected = ValidateTileMarkSelection(mark, tileIds);
            BusinessPayload payload = CommandPayloadFactory.SubmitTileMark(
                view.Identity.RoundId,
                decision.DecisionId,
                decision.ControlEpoch,
                selected);

            ProtocolCommand command = CreateCommand(
                CommandKind.SubmitTileMark,
                payload,
                view,
                owner,
                view.Identity.RoundId,
                decision.DecisionId,
                decision.ControlEpoch);

            return new TableCommandIntent(
                CommandKind.SubmitTileMark,
                command,
                Key(CommandKind.SubmitTileMark, view, decision.DecisionId, null, null, TilesVariant(selected)),
                view.Identity.RoundId,
                decision.DecisionId,
                decision.ControlEpoch,
                null,
                null);
        }

        public TableCommandIntent CreateSubmitSleeveBloom(
            PlayerTableViewState view,
            TableCommandOwnerContext owner)
        {
            RequireSubmittable(view, owner);
            PlayerDecisionState decision = RequireDecision(view);
            RequireNotExpired(decision.Countdown, "DECISION_EXPIRED");
            PlayerCheatState cheat = view.Own!.Cheat
                ?? throw Unavailable("CHEAT_UNAVAILABLE", "The confirmed private view has no cheat state.");

            if (cheat.ImmediateSleeveBloom != null)
            {
                throw Unavailable("OFFER_ALREADY_OPEN", "An immediate sleeve-bloom offer already exists; choose it instead of activating again.");
            }

            BusinessPayload payload = CommandPayloadFactory.SubmitSleeveBloom(
                view.Identity.RoundId,
                decision.DecisionId,
                decision.ControlEpoch);
            ProtocolCommand command = CreateCommand(
                CommandKind.SubmitSleeveBloom,
                payload,
                view,
                owner,
                view.Identity.RoundId,
                decision.DecisionId,
                decision.ControlEpoch);

            return new TableCommandIntent(
                CommandKind.SubmitSleeveBloom,
                command,
                Key(CommandKind.SubmitSleeveBloom, view, decision.DecisionId, null, null, "activate"),
                view.Identity.RoundId,
                decision.DecisionId,
                decision.ControlEpoch,
                null,
                null);
        }

        public TableCommandIntent CreateSubmitSleeveBloomChoice(
            PlayerTableViewState view,
            bool offTurn,
            SleeveBloomChoice choice,
            TableCommandOwnerContext owner)
        {
            RequireSubmittable(view, owner);
            PlayerCheatState cheat = view.Own!.Cheat
                ?? throw Unavailable("CHEAT_UNAVAILABLE", "The confirmed private view has no cheat state.");
            PlayerSleeveBloomState? offer = offTurn ? cheat.OffTurnSleeveBloom : cheat.ImmediateSleeveBloom;
            if (offer == null)
            {
                throw Unavailable("OFFER_REQUIRED", offTurn ? "No off-turn sleeve-bloom offer is confirmed." : "No immediate sleeve-bloom offer is confirmed.");
            }

            if (!string.Equals(offer.RoundId, view.Identity.RoundId, StringComparison.Ordinal))
            {
                throw Unavailable("STALE_ROUND", "The sleeve-bloom offer belongs to another round.");
            }

            if (offer.Countdown != null)
            {
                RequireNotExpired(offer.Countdown, "OFFER_EXPIRED");
            }

            if (choice == SleeveBloomChoice.First && !offer.FirstTileId.HasValue)
            {
                throw Unavailable("CHOICE_NOT_AVAILABLE", "The first sleeve-bloom candidate is absent.");
            }

            if (choice == SleeveBloomChoice.Second && !offer.SecondTileId.HasValue)
            {
                throw Unavailable("CHOICE_NOT_AVAILABLE", "The second sleeve-bloom candidate is absent.");
            }

            if (choice != SleeveBloomChoice.First && choice != SleeveBloomChoice.Second)
            {
                throw Unavailable("CHOICE_NOT_AVAILABLE", "The sleeve-bloom choice must be FIRST or SECOND.");
            }

            CommandKind kind = offTurn
                ? CommandKind.SubmitSleeveBloomOffTurnChoice
                : CommandKind.SubmitSleeveBloomChoice;
            BusinessPayload payload = offTurn
                ? new CanonicalPayloadWriter((ushort)CommandKind.SubmitSleeveBloomOffTurnChoice)
                    .WriteString(CommandPayloadFields.OfferId, offer.OfferId)
                    .WriteUInt32(CommandPayloadFields.Choice, (uint)choice)
                    .Build()
                : CommandPayloadFactory.SubmitSleeveBloomChoice(offer.OfferId, choice);
            ProtocolCommand command = CreateCommand(
                kind,
                payload,
                view,
                owner,
                string.Empty,
                null,
                null);

            return new TableCommandIntent(
                kind,
                command,
                Key(kind, view, null, offer.OfferId, offer.WindowId, "choice/" + ((int)choice).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                view.Identity.RoundId,
                null,
                null,
                offer.OfferId,
                offer.WindowId);
        }

        public TableCommandIntent CreateSubmitSleeveBloomOffTurn(
            PlayerTableViewState view,
            TableCommandOwnerContext owner)
        {
            RequireSubmittable(view, owner);
            PlayerDecisionState decision = RequireDecision(view);
            RequireNotExpired(decision.Countdown, "DECISION_EXPIRED");
            PlayerCheatState cheat = view.Own!.Cheat
                ?? throw Unavailable("CHEAT_UNAVAILABLE", "The confirmed private view has no cheat state.");

            if (string.IsNullOrEmpty(cheat.WindowId))
            {
                throw Unavailable("WINDOW_REQUIRED", "No off-turn sleeve-bloom window is confirmed.");
            }

            if (cheat.OffTurnSleeveBloom != null || cheat.SleeveBloomReservation != null)
            {
                throw Unavailable("OFFER_ALREADY_OPEN", "The off-turn window already has a confirmed offer or reservation.");
            }

            BusinessPayload payload = new CanonicalPayloadWriter((ushort)CommandKind.SubmitSleeveBloomOffTurn)
                .WriteString(CommandPayloadFields.RoundIdField, view.Identity.RoundId)
                .WriteString(CommandPayloadFields.DecisionIdField, decision.DecisionId)
                .WriteUInt64(CommandPayloadFields.ControlEpochField, decision.ControlEpoch)
                .WriteString(CommandPayloadFields.WindowId, cheat.WindowId)
                .Build();

            ProtocolCommand command = CreateCommand(
                CommandKind.SubmitSleeveBloomOffTurn,
                payload,
                view,
                owner,
                view.Identity.RoundId,
                decision.DecisionId,
                decision.ControlEpoch);

            return new TableCommandIntent(
                CommandKind.SubmitSleeveBloomOffTurn,
                command,
                Key(CommandKind.SubmitSleeveBloomOffTurn, view, decision.DecisionId, null, cheat.WindowId, "activate"),
                view.Identity.RoundId,
                decision.DecisionId,
                decision.ControlEpoch,
                null,
                cheat.WindowId);
        }

        public TableCommandIntent CreateSubmitBeamSwap(
            PlayerTableViewState view,
            TableCommandOwnerContext owner)
        {
            RequireSubmittable(view, owner);
            PlayerDecisionState decision = RequireDecision(view);
            RequireNotExpired(decision.Countdown, "DECISION_EXPIRED");
            PlayerCheatState cheat = view.Own!.Cheat
                ?? throw Unavailable("CHEAT_UNAVAILABLE", "The confirmed private view has no cheat state.");

            if (cheat.BeamSwap != null)
            {
                throw Unavailable("OFFER_ALREADY_OPEN", "A beam-swap offer already exists; choose it instead of activating again.");
            }

            BusinessPayload payload = CommandPayloadFactory.SubmitBeamSwap(
                view.Identity.RoundId,
                decision.DecisionId,
                decision.ControlEpoch);
            ProtocolCommand command = CreateCommand(
                CommandKind.SubmitBeamSwap,
                payload,
                view,
                owner,
                view.Identity.RoundId,
                decision.DecisionId,
                decision.ControlEpoch);

            return new TableCommandIntent(
                CommandKind.SubmitBeamSwap,
                command,
                Key(CommandKind.SubmitBeamSwap, view, decision.DecisionId, null, null, "activate"),
                view.Identity.RoundId,
                decision.DecisionId,
                decision.ControlEpoch,
                null,
                null);
        }

        public TableCommandIntent CreateSubmitBeamSwapChoice(
            PlayerTableViewState view,
            BeamSwapChoice choice,
            uint? handTileId,
            PlayerBeamSwapCandidateState? target,
            TableCommandOwnerContext owner)
        {
            RequireSubmittable(view, owner);
            PlayerCheatState cheat = view.Own!.Cheat
                ?? throw Unavailable("CHEAT_UNAVAILABLE", "The confirmed private view has no cheat state.");
            PlayerBeamSwapState offer = cheat.BeamSwap
                ?? throw Unavailable("OFFER_REQUIRED", "No beam-swap offer is confirmed.");

            if (!string.Equals(offer.RoundId, view.Identity.RoundId, StringComparison.Ordinal))
            {
                throw Unavailable("STALE_ROUND", "The beam-swap offer belongs to another round.");
            }

            if (offer.Countdown != null)
            {
                RequireNotExpired(offer.Countdown, "OFFER_EXPIRED");
            }

            uint? targetSlot = null;
            if (choice == BeamSwapChoice.Swap)
            {
                if (!handTileId.HasValue || !Contains(offer.SelectableHandTileIds, handTileId.Value))
                {
                    throw Unavailable("HAND_TILE_REQUIRED", "A selectable hand tile is required for a beam swap.");
                }

                if (target == null || !SameCandidate(offer, target))
                {
                    throw Unavailable("STALE_SLOT", "The selected beam-swap slot is not in the current confirmed offer.");
                }

                targetSlot = target.Slot;
            }
            else if (choice != BeamSwapChoice.KeepOriginal)
            {
                throw Unavailable("CHOICE_NOT_AVAILABLE", "The beam-swap choice must be SWAP or KEEP_ORIGINAL.");
            }

            BusinessPayload payload = CommandPayloadFactory.SubmitBeamSwapChoice(
                offer.OfferId,
                choice,
                choice == BeamSwapChoice.Swap ? handTileId : null,
                choice == BeamSwapChoice.Swap ? targetSlot : null);
            ProtocolCommand command = CreateCommand(
                CommandKind.SubmitBeamSwapChoice,
                payload,
                view,
                owner,
                string.Empty,
                null,
                null);

            string variant = "choice/" + ((int)choice).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (choice == BeamSwapChoice.Swap)
            {
                variant += "/" + handTileId!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "/" + targetSlot!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "/" + ((int)target!.SlotKind).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "/" + target.SlotIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            return new TableCommandIntent(
                CommandKind.SubmitBeamSwapChoice,
                command,
                Key(CommandKind.SubmitBeamSwapChoice, view, null, offer.OfferId, null, variant),
                view.Identity.RoundId,
                null,
                null,
                offer.OfferId,
                null);
        }

        public TableCommandIntent CreateSubmitShufflePlan(
            PlayerTableViewState view,
            bool seeThrough,
            TableCommandOwnerContext owner)
        {
            RequireSubmittable(view, owner);
            PlayerPreparationState preparation = view.Own!.Cheat?.Preparation
                ?? throw Unavailable("PREPARATION_UNAVAILABLE", "The confirmed private view has no shuffle preparation.");
            if (string.IsNullOrEmpty(view.Identity.RoundId))
            {
                throw Unavailable("ROUND_ID_REQUIRED", "The confirmed snapshot has no round id.");
            }

            if (!string.Equals(preparation.RoundId, view.Identity.RoundId, StringComparison.Ordinal))
            {
                throw Unavailable("STALE_ROUND", "The shuffle preparation belongs to another round.");
            }

            if (preparation.Confirmed)
            {
                throw Unavailable("PREPARATION_ALREADY_CONFIRMED", "The confirmed snapshot already marks this preparation as confirmed.");
            }

            RequireNotExpired(preparation.Countdown, "PREPARATION_EXPIRED");
            if (seeThrough && !preparation.SeeThroughEnabled)
            {
                throw Unavailable("SEE_THROUGH_DISABLED", "See-through is not enabled in the confirmed preparation.");
            }

            uint target = preparation.Target;
            if (target > 6 && target != 255)
            {
                throw Unavailable("TARGET_NOT_REPRESENTABLE", "The confirmed shuffle target is outside the frozen target set.");
            }

            BusinessPayload payload = CommandPayloadFactory.SubmitShufflePlan(
                view.Identity.RoundId,
                target,
                seeThrough);
            ProtocolCommand command = CreateCommand(
                CommandKind.SubmitShufflePlan,
                payload,
                view,
                owner,
                view.Identity.RoundId,
                null,
                null);

            return new TableCommandIntent(
                CommandKind.SubmitShufflePlan,
                command,
                Key(
                    CommandKind.SubmitShufflePlan,
                    view,
                    null,
                    null,
                    null,
                    "target/" + target.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + "/see/" + (seeThrough ? "1" : "0")),
                view.Identity.RoundId,
                null,
                null,
                null,
                null);
        }

        public TableCommandIntent CreateNextRound(
            PlayerTableViewState view,
            TableCommandOwnerContext owner)
        {
            RequireSubmittable(view, owner);
            if (view.Board.Phase != PlayerGamePhase.RoundEnd)
            {
                throw Unavailable("NEXT_ROUND_NOT_AVAILABLE", "The confirmed phase is not ROUND_END.");
            }

            if (string.IsNullOrEmpty(view.Identity.RoundId))
            {
                throw Unavailable("ROUND_ID_REQUIRED", "The confirmed snapshot has no round id.");
            }

            BusinessPayload payload = new CanonicalPayloadWriter((ushort)CommandKind.NextRound)
                .WriteString(CommandPayloadFields.RoundIdField, view.Identity.RoundId)
                .Build();
            ProtocolCommand command = CreateCommand(
                CommandKind.NextRound,
                payload,
                view,
                owner,
                view.Identity.RoundId,
                null,
                null);

            return new TableCommandIntent(
                CommandKind.NextRound,
                command,
                Key(CommandKind.NextRound, view, null, null, null, "next"),
                view.Identity.RoundId,
                null,
                null,
                null,
                null);
        }

        /// <summary>
        /// Seat control is deliberately unavailable in this sub-range. The
        /// frozen ClaimControl/ReleaseControl validator requires roundId and
        /// expectedControlEpoch to both be canonical field 1, which cannot be
        /// encoded. No alternative or invalid payload is generated.
        /// </summary>
        public TableCommandIntent CreateClaimControl(
            PlayerTableViewState view,
            string roundId,
            ulong expectedControlEpoch,
            TableCommandOwnerContext owner)
        {
            throw ControlUnavailable(roundId, expectedControlEpoch, owner);
        }

        /// <summary>See <see cref="CreateClaimControl"/>: the same field collision applies.</summary>
        public TableCommandIntent CreateReleaseControl(
            PlayerTableViewState view,
            string roundId,
            ulong expectedControlEpoch,
            TableCommandOwnerContext owner)
        {
            throw ControlUnavailable(roundId, expectedControlEpoch, owner);
        }

        private static TableIntentUnavailableException ControlUnavailable(
            string roundId,
            ulong expectedControlEpoch,
            TableCommandOwnerContext owner)
        {
            if (owner == null)
            {
                throw new ArgumentNullException(nameof(owner));
            }

            return new TableIntentUnavailableException(
                ControlPayloadFieldCollision,
                "ClaimControl/ReleaseControl cannot be encoded: CommandPayloadFields.RoundIdField and "
                + "CommandPayloadFields.ExpectedControlEpoch are both canonical field 1, so no payload can satisfy "
                + "both required fields (roundId=" + (roundId ?? string.Empty)
                + ", expectedControlEpoch=" + expectedControlEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture) + ").");
        }

        private ProtocolCommand CreateCommand(
            CommandKind kind,
            BusinessPayload payload,
            PlayerTableViewState view,
            TableCommandOwnerContext owner,
            string roundId,
            string? decisionId,
            ulong? controlEpoch)
        {
            StateVersion expected = owner.ExpectedStateVersion ?? new StateVersion(view.Identity.StateVersion);
            OwnerEpoch ownerEpoch = owner.OwnerEpoch ?? new OwnerEpoch(view.Identity.OwnerEpoch);
            return new ProtocolCommand(
                kind,
                payload,
                owner.RequestId,
                owner.ScopeId,
                expected,
                string.IsNullOrEmpty(roundId) ? (RoundId?)null : new RoundId(roundId),
                string.IsNullOrEmpty(decisionId) ? (DecisionId?)null : new DecisionId(decisionId!),
                controlEpoch.HasValue ? new ControlEpoch(controlEpoch.Value) : (ControlEpoch?)null,
                ownerEpoch);
        }

        private static uint[] ValidateTileMarkSelection(PlayerTileMarkState mark, IReadOnlyList<uint> tileIds)
        {
            if (tileIds == null || tileIds.Count != 4)
            {
                throw Unavailable("TILE_MARK_REQUIRES_FOUR", "Tile mark requires exactly four distinct selected tiles.");
            }

            var result = new uint[4];
            var seen = new HashSet<uint>();
            for (int i = 0; i < 4; i++)
            {
                uint tile = tileIds[i];
                if (!seen.Add(tile))
                {
                    throw Unavailable("TILE_MARK_DUPLICATE", "Tile mark selection repeats a physical tile.");
                }

                if (!Contains(mark.SelectableTileIds, tile))
                {
                    throw Unavailable("TILE_MARK_NOT_SELECTABLE", "The selected tile is not in the confirmed tile-mark candidate list.");
                }

                result[i] = tile;
            }

            Array.Sort(result);
            return result;
        }

        private void RequireSubmittable(PlayerTableViewState view, TableCommandOwnerContext owner)
        {
            if (view == null)
            {
                throw new ArgumentNullException(nameof(view));
            }

            if (owner == null)
            {
                throw new ArgumentNullException(nameof(owner));
            }

            if (view.Own == null || !view.Identity.IsPrivateAuthorized)
            {
                throw Unavailable("NO_PRIVATE_VIEW", "No authorized player private view is available; a spectator or unbound view cannot submit.");
            }

            if (!view.CanSubmit)
            {
                throw Unavailable("NOT_SUBMITTABLE", "The confirmed view is not in a submittable ready/unpaused state.");
            }

            if (!string.Equals(owner.ScopeId.Value, view.Identity.ScopeId, StringComparison.Ordinal))
            {
                throw Unavailable("SCOPE_MISMATCH", "The owner-supplied scope does not match the confirmed binding scope.");
            }
        }

        private static PlayerDecisionState RequireDecision(PlayerTableViewState view)
        {
            PlayerDecisionState? decision = view.Own?.Decision;
            if (decision == null)
            {
                throw Unavailable("DECISION_REQUIRED", "The confirmed private view has no decision.");
            }

            if (string.IsNullOrEmpty(decision.RoundId))
            {
                throw Unavailable("ROUND_ID_REQUIRED", "The decision has no confirmed round id.");
            }

            if (!string.Equals(decision.RoundId, view.Identity.RoundId, StringComparison.Ordinal))
            {
                throw Unavailable("STALE_ROUND", "The decision belongs to another round.");
            }

            return decision;
        }

        private void RequireNotExpired(PlayerCountdown countdown, string code)
        {
            if (countdown.IsExpired(_clock.MonotonicMilliseconds))
            {
                throw Unavailable(code, "The server-relative deadline has expired.");
            }
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

        private static bool SameCandidate(PlayerBeamSwapState offer, PlayerBeamSwapCandidateState candidate)
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

        private static string ActionVariant(PlayerLegalActionState action)
        {
            var builder = new StringBuilder(48);
            builder.Append("action/");
            builder.Append(action.RawActionType.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append('/');
            builder.Append(action.OptionId);
            builder.Append('/');
            for (int i = 0; i < action.TileIds.Count; i++)
            {
                builder.Append(action.TileIds[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
                builder.Append(',');
            }

            return builder.ToString();
        }

        private static string TilesVariant(IReadOnlyList<uint> tiles)
        {
            var builder = new StringBuilder(24);
            for (int i = 0; i < tiles.Count; i++)
            {
                builder.Append(tiles[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
                builder.Append(',');
            }

            return builder.ToString();
        }

        private static TableIntentKey Key(
            CommandKind kind,
            PlayerTableViewState view,
            string? decisionId,
            string? offerId,
            string? windowId,
            string variant)
        {
            return new TableIntentKey(
                kind,
                view.Identity.RoundId,
                decisionId ?? string.Empty,
                offerId ?? string.Empty,
                windowId ?? string.Empty,
                variant);
        }

        private static TableIntentUnavailableException Unavailable(string code, string message)
        {
            return new TableIntentUnavailableException(code, message);
        }
    }
}
