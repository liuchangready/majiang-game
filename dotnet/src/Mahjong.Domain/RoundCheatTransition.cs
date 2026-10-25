using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Mahjong.Domain.Cheat;

namespace Mahjong.Domain
{
    /// <summary>
    /// The seven authoritative cheat command kinds. Values mirror the frozen protocol
    /// <c>CommandKind</c> order (SubmitTileMark..SubmitBeamSwapChoice) but are defined here so
    /// Domain never references Contracts. Values must not be reordered.
    /// </summary>
    public enum RoundCheatAction : byte
    {
        SubmitTileMark = 0,
        SubmitSleeveBloom = 1,
        SubmitSleeveBloomChoice = 2,
        SubmitSleeveBloomOffTurn = 3,
        SubmitSleeveBloomOffTurnChoice = 4,
        SubmitBeamSwap = 5,
        SubmitBeamSwapChoice = 6
    }

    /// <summary>Stable rejections added by the cheat execution bridge.</summary>
    public enum RoundCheatErrorCode : byte
    {
        None = 0,
        InvalidCommand = 1,
        RoundMismatch = 2,
        RoundFinished = 3,
        ContextMismatch = 4,
        NotConfigured = 5,
        CheatRejected = 6
    }

    /// <summary>
    /// Immutable, typed cheat payload. Exactly the fields admitted by the command action are
    /// present; every other field must be absent. There is no arbitrary dictionary/reducer
    /// surface and no caller-supplied CheatState, map, balance or candidate list.
    /// </summary>
    public sealed class RoundCheatPayload
    {
        private static readonly ReadOnlyCollection<TileId> NoTiles =
            new ReadOnlyCollection<TileId>(new List<TileId>());

        private RoundCheatPayload(
            string? groupId,
            IReadOnlyList<TileId>? tiles,
            string? offerId,
            SleeveBloomKeep? keep,
            string? windowId,
            BeamSwapAction? action,
            TileId? handTile,
            BeamSwapSlot? targetSlot)
        {
            GroupId = groupId;
            Tiles = tiles == null
                ? NoTiles
                : new ReadOnlyCollection<TileId>(new List<TileId>(tiles));
            OfferId = offerId;
            Keep = keep;
            WindowId = windowId;
            Action = action;
            HandTile = handTile;
            TargetSlot = targetSlot;
        }

        public string? GroupId { get; }

        public IReadOnlyList<TileId> Tiles { get; }

        public string? OfferId { get; }

        public SleeveBloomKeep? Keep { get; }

        public string? WindowId { get; }

        public BeamSwapAction? Action { get; }

        public TileId? HandTile { get; }

        public BeamSwapSlot? TargetSlot { get; }

        public static RoundCheatPayload TileMark(string groupId, IReadOnlyList<TileId> tiles)
        {
            if (string.IsNullOrWhiteSpace(groupId))
            {
                throw new ArgumentException("Tile mark group id cannot be blank.", nameof(groupId));
            }

            if (tiles == null || tiles.Count != 4)
            {
                throw new ArgumentException("Tile mark requires exactly four physical tiles.", nameof(tiles));
            }

            var seen = new bool[TileId.Count];
            var copy = new TileId[4];
            for (int i = 0; i < 4; i++)
            {
                TileId tile = tiles[i];
                if (seen[tile.Value])
                {
                    throw new ArgumentException("Tile mark contains a duplicate tile.", nameof(tiles));
                }

                seen[tile.Value] = true;
                copy[i] = tile;
            }

            return new RoundCheatPayload(groupId, copy, null, null, null, null, null, null);
        }

        public static RoundCheatPayload SleeveBloom() =>
            new RoundCheatPayload(null, null, null, null, null, null, null, null);

        public static RoundCheatPayload SleeveBloomChoice(string offerId, SleeveBloomKeep keep)
        {
            if (string.IsNullOrWhiteSpace(offerId))
            {
                throw new ArgumentException("Sleeve bloom choice requires an offer id.", nameof(offerId));
            }

            if (!Enum.IsDefined(typeof(SleeveBloomKeep), keep))
            {
                throw new ArgumentOutOfRangeException(nameof(keep), keep, "Unknown sleeve bloom keep value.");
            }

            return new RoundCheatPayload(null, null, offerId, keep, null, null, null, null);
        }

        public static RoundCheatPayload OffTurn(string windowId)
        {
            if (string.IsNullOrWhiteSpace(windowId))
            {
                throw new ArgumentException("Off-turn sleeve bloom requires a window id.", nameof(windowId));
            }

            return new RoundCheatPayload(null, null, null, null, windowId, null, null, null);
        }

        public static RoundCheatPayload OffTurnChoice(string offerId, SleeveBloomKeep keep)
        {
            RoundCheatPayload payload = SleeveBloomChoice(offerId, keep);
            return new RoundCheatPayload(
                null,
                null,
                payload.OfferId,
                payload.Keep,
                null,
                null,
                null,
                null);
        }

        public static RoundCheatPayload BeamSwap() =>
            new RoundCheatPayload(null, null, null, null, null, null, null, null);

        public static RoundCheatPayload BeamSwapChoice(
            string offerId,
            BeamSwapAction action,
            TileId? handTile,
            BeamSwapSlot? targetSlot)
        {
            if (string.IsNullOrWhiteSpace(offerId))
            {
                throw new ArgumentException("Beam swap choice requires an offer id.", nameof(offerId));
            }

            if (!Enum.IsDefined(typeof(BeamSwapAction), action))
            {
                throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown beam swap action.");
            }

            if (action == BeamSwapAction.Swap)
            {
                if (!handTile.HasValue || !targetSlot.HasValue)
                {
                    throw new ArgumentException("A SWAP choice requires a hand tile and target slot.");
                }
            }
            else if (handTile.HasValue || targetSlot.HasValue)
            {
                throw new ArgumentException("KEEP_ORIGINAL takes no hand tile or target slot.");
            }

            return new RoundCheatPayload(
                null,
                null,
                offerId,
                null,
                null,
                action,
                handTile,
                targetSlot);
        }

        internal void ValidateFor(RoundCheatAction action)
        {
            switch (action)
            {
                case RoundCheatAction.SubmitTileMark:
                    if (GroupId == null || Tiles.Count != 4 || OfferId != null || Keep.HasValue ||
                        WindowId != null || Action.HasValue || HandTile.HasValue || TargetSlot.HasValue)
                    {
                        throw new ArgumentException("The payload does not match SubmitTileMark.");
                    }

                    break;
                case RoundCheatAction.SubmitSleeveBloom:
                case RoundCheatAction.SubmitBeamSwap:
                    if (GroupId != null || Tiles.Count != 0 || OfferId != null || Keep.HasValue ||
                        WindowId != null || Action.HasValue || HandTile.HasValue || TargetSlot.HasValue)
                    {
                        throw new ArgumentException("The payload does not match an activation command.");
                    }

                    break;
                case RoundCheatAction.SubmitSleeveBloomChoice:
                case RoundCheatAction.SubmitSleeveBloomOffTurnChoice:
                    if (GroupId != null || Tiles.Count != 0 || string.IsNullOrWhiteSpace(OfferId) ||
                        !Keep.HasValue || WindowId != null || Action.HasValue ||
                        HandTile.HasValue || TargetSlot.HasValue)
                    {
                        throw new ArgumentException("The payload does not match a sleeve bloom choice.");
                    }

                    break;
                case RoundCheatAction.SubmitSleeveBloomOffTurn:
                    if (GroupId != null || Tiles.Count != 0 || OfferId != null || Keep.HasValue ||
                        string.IsNullOrWhiteSpace(WindowId) || Action.HasValue ||
                        HandTile.HasValue || TargetSlot.HasValue)
                    {
                        throw new ArgumentException("The payload does not match SubmitSleeveBloomOffTurn.");
                    }

                    break;
                case RoundCheatAction.SubmitBeamSwapChoice:
                    if (GroupId != null || Tiles.Count != 0 || string.IsNullOrWhiteSpace(OfferId) ||
                        Keep.HasValue || WindowId != null || !Action.HasValue)
                    {
                        throw new ArgumentException("The payload does not match SubmitBeamSwapChoice.");
                    }

                    if (Action.Value == BeamSwapAction.Swap &&
                        (!HandTile.HasValue || !TargetSlot.HasValue))
                    {
                        throw new ArgumentException("A SWAP choice requires a hand tile and target slot.");
                    }

                    if (Action.Value == BeamSwapAction.KeepOriginal &&
                        (HandTile.HasValue || TargetSlot.HasValue))
                    {
                        throw new ArgumentException("KEEP_ORIGINAL takes no hand tile or target slot.");
                    }

                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown cheat action.");
            }
        }
    }

    /// <summary>
    /// One typed cheat command. It carries only the business request and its stable request id;
    /// the caller cannot supply a round context, map, cheat state, balance or candidate result.
    /// </summary>
    public sealed class RoundCheatCommand
    {
        public RoundCheatCommand(
            string roundId,
            int seat,
            RoundCheatAction action,
            string requestId,
            RoundCheatPayload payload)
        {
            if (string.IsNullOrWhiteSpace(roundId))
            {
                throw new ArgumentException("RoundId cannot be blank.", nameof(roundId));
            }

            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat), seat, "Seat must be 0..3.");
            }

            if (!Enum.IsDefined(typeof(RoundCheatAction), action))
            {
                throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown cheat action.");
            }

            if (string.IsNullOrWhiteSpace(requestId))
            {
                throw new ArgumentException("Request id cannot be blank.", nameof(requestId));
            }

            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            payload.ValidateFor(action);
            RoundId = roundId;
            Seat = seat;
            Action = action;
            RequestId = requestId;
            Payload = payload;
        }

        public string RoundId { get; }

        public int Seat { get; }

        public RoundCheatAction Action { get; }

        public string RequestId { get; }

        public RoundCheatPayload Payload { get; }
    }

    /// <summary>One authoritative cheat event plus the concrete facts needed for its projection.</summary>
    public sealed class RoundCheatEvent
    {
        public RoundCheatEvent(
            CheatEvent value,
            string? offerId,
            SleeveBloomKeep? keep,
            BeamSwapAction? action,
            TileId? resultTile,
            TileId? handTile,
            BeamSwapSlot? targetSlot,
            int turnNumber)
        {
            Value = value ?? throw new ArgumentNullException(nameof(value));
            OfferId = offerId;
            Keep = keep;
            Action = action;
            ResultTile = resultTile;
            HandTile = handTile;
            TargetSlot = targetSlot;
            if (turnNumber < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(turnNumber), turnNumber, "Turn number must be non-negative.");
            }

            TurnNumber = turnNumber;
        }

        public CheatEvent Value { get; }

        public string? OfferId { get; }

        public SleeveBloomKeep? Keep { get; }

        public BeamSwapAction? Action { get; }

        public TileId? ResultTile { get; }

        public TileId? HandTile { get; }

        public BeamSwapSlot? TargetSlot { get; }

        public int TurnNumber { get; }
    }

    /// <summary>
    /// One pure cheat candidate: the exact next cheat aggregate, the next round (one version
    /// step through the Domain bridge) and the private events. A rejection with
    /// <see cref="CheatTransition.Success"/> false but <see cref="Changed"/> true still carries
    /// a candidate: the forced FIRST settlement must be persisted even though the caller is
    /// told the command was rejected.
    /// </summary>
    public sealed class RoundCheatChange
    {
        private readonly ReadOnlyCollection<RoundCheatEvent> _events;

        internal RoundCheatChange(
            RoundChange roundChange,
            CheatState cheatState,
            IReadOnlyList<RoundCheatEvent> events,
            bool accepted,
            CheatErrorCode error,
            string message)
        {
            RoundChange = roundChange;
            CheatState = cheatState;
            _events = new ReadOnlyCollection<RoundCheatEvent>(new List<RoundCheatEvent>(events));
            Accepted = accepted;
            Error = error;
            Message = message ?? string.Empty;
        }

        public RoundChange RoundChange { get; }

        public CheatState CheatState { get; }

        public IReadOnlyList<RoundCheatEvent> Events => _events;

        /// <summary>True only when the P1.4 transition reported Success.</summary>
        public bool Accepted { get; }

        /// <summary>Always true for a candidate; a plain no-op/rejection has no candidate.</summary>
        public bool Changed => true;

        public CheatErrorCode Error { get; }

        public string Message { get; }
    }

    /// <summary>Pure prepare result. A rejection without a change has no state and no version step.</summary>
    public sealed class RoundCheatPreparation
    {
        private RoundCheatPreparation(
            RoundCheatChange? change,
            RoundCheatErrorCode error,
            CheatErrorCode cheatError,
            string message)
        {
            Change = change;
            Error = error;
            CheatError = cheatError;
            Message = message ?? string.Empty;
        }

        public bool IsReady => Change != null;

        public RoundCheatChange? Change { get; }

        public RoundCheatErrorCode Error { get; }

        public CheatErrorCode CheatError { get; }

        public string Message { get; }

        internal static RoundCheatPreparation Ready(RoundCheatChange change) =>
            new RoundCheatPreparation(change, RoundCheatErrorCode.None, CheatErrorCode.None, string.Empty);

        internal static RoundCheatPreparation Rejected(
            RoundCheatErrorCode error,
            CheatErrorCode cheatError,
            string message) =>
            new RoundCheatPreparation(null, error, cheatError, message);
    }

    /// <summary>
    /// Durable replay input: the exact command plus the authoritative context values that the
    /// actor injected at execution time. The clock is never re-read during cold recovery.
    /// </summary>
    public sealed class RoundCheatJournal
    {
        public RoundCheatJournal(
            RoundCheatCommand command,
            int turnNumber,
            long nowMillis,
            int roundOrdinal,
            SleeveBloomDrawKind lastDrawKind)
        {
            Command = command ?? throw new ArgumentNullException(nameof(command));
            if (turnNumber < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(turnNumber), turnNumber, "Turn number must be non-negative.");
            }

            if (nowMillis < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(nowMillis), nowMillis, "Now must be non-negative.");
            }

            if (roundOrdinal < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(roundOrdinal), roundOrdinal, "Round ordinal must be non-negative.");
            }

            if (!Enum.IsDefined(typeof(SleeveBloomDrawKind), lastDrawKind))
            {
                throw new ArgumentOutOfRangeException(nameof(lastDrawKind), lastDrawKind, "Unknown draw kind.");
            }

            TurnNumber = turnNumber;
            NowMillis = nowMillis;
            RoundOrdinal = roundOrdinal;
            LastDrawKind = lastDrawKind;
        }

        public RoundCheatCommand Command { get; }

        public int TurnNumber { get; }

        public long NowMillis { get; }

        public int RoundOrdinal { get; }

        public SleeveBloomDrawKind LastDrawKind { get; }
    }

    /// <summary>
    /// Pure G1/N-1b bridge from the seven public cheat commands to the unique P1.4
    /// <see cref="CheatSubmission"/> transition surface. Every method is a total function of
    /// immutable inputs; no clock, random source, protocol type or mutable actor state is read.
    /// </summary>
    public static class RoundCheatTransition
    {
        public static RoundCheatPreparation Prepare(
            RoundStateMachine round,
            CheatState? cheat,
            RoundCheatCommand command,
            CheatTurnContext context,
            CheatEconomyConfig economy,
            SleeveBloomConfig sleeveConfig)
        {
            if (round == null) throw new ArgumentNullException(nameof(round));
            if (command == null) throw new ArgumentNullException(nameof(command));
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (economy == null) throw new ArgumentNullException(nameof(economy));
            if (sleeveConfig == null) throw new ArgumentNullException(nameof(sleeveConfig));

            RoundCheatPreparation? guard = Guard(round, cheat, command, context);
            if (guard != null)
            {
                return guard;
            }

            CheatState before = cheat!;
            CheatTransition transition;
            switch (command.Action)
            {
                case RoundCheatAction.SubmitTileMark:
                    transition = CheatSubmission.ApplyTileMark(
                        before,
                        round.Tiles,
                        context,
                        economy,
                        command.Seat,
                        command.RequestId,
                        command.Payload.GroupId!,
                        command.Payload.Tiles);
                    break;
                case RoundCheatAction.SubmitSleeveBloom:
                    transition = CheatSubmission.ActivateSleeveBloom(
                        before,
                        round.Tiles,
                        context,
                        economy,
                        sleeveConfig,
                        command.Seat,
                        command.RequestId);
                    break;
                case RoundCheatAction.SubmitSleeveBloomChoice:
                    transition = CheatSubmission.ChooseSleeveBloom(
                        before,
                        round.Tiles,
                        context,
                        command.Seat,
                        command.Payload.OfferId!,
                        command.Payload.Keep!.Value);
                    break;
                case RoundCheatAction.SubmitSleeveBloomOffTurn:
                    transition = CheatSubmission.ActivateOffTurnSleeveBloom(
                        before,
                        round.Tiles,
                        context,
                        economy,
                        sleeveConfig,
                        command.Seat,
                        command.RequestId,
                        command.Payload.WindowId!);
                    break;
                case RoundCheatAction.SubmitSleeveBloomOffTurnChoice:
                    transition = CheatSubmission.ChooseOffTurnSleeveBloom(
                        before,
                        round.Tiles,
                        context,
                        command.Seat,
                        command.Payload.OfferId!,
                        command.Payload.Keep!.Value);
                    break;
                case RoundCheatAction.SubmitBeamSwap:
                    transition = CheatSubmission.ActivateBeamSwap(
                        before,
                        round.Tiles,
                        context,
                        economy,
                        sleeveConfig,
                        command.RequestId,
                        command.Seat);
                    break;
                case RoundCheatAction.SubmitBeamSwapChoice:
                    transition = CheatSubmission.ChooseBeamSwap(
                        before,
                        round.Tiles,
                        context,
                        command.Seat,
                        command.Payload.OfferId!,
                        command.Payload.Action!.Value,
                        command.Payload.HandTile,
                        command.Payload.TargetSlot);
                    break;
                default:
                    return RoundCheatPreparation.Rejected(
                        RoundCheatErrorCode.InvalidCommand,
                        CheatErrorCode.None,
                        "Unsupported cheat action " + command.Action + ".");
            }

            return Materialize(round, before, command, context, transition);
        }

        /// <summary>
        /// Composes a normal discard with the delivery of the next seat's locked reservation.
        /// The round stays in DRAW long enough for the real P1.4 delivery to run, then the
        /// discard and the delivered TileDrawn event are committed as one version step. A late
        /// off-turn offer is first settled as FIRST (V4 settle-before-draw), and the discarder's
        /// new off-turn window is opened in the same candidate.
        /// </summary>
        public static RoundCheatPreparation PrepareDiscardDelivery(
            RoundStateMachine round,
            CheatState? cheat,
            RoundCommand discardCommand,
            long nowMillis)
        {
            if (round == null) throw new ArgumentNullException(nameof(round));
            if (discardCommand == null) throw new ArgumentNullException(nameof(discardCommand));
            if (discardCommand.Action != RoundActionKind.DiscardTile)
            {
                return RoundCheatPreparation.Rejected(
                    RoundCheatErrorCode.InvalidCommand,
                    CheatErrorCode.None,
                    "The discard-delivery bridge requires a DiscardTile command.");
            }

            if (cheat == null)
            {
                return RoundCheatPreparation.Rejected(
                    RoundCheatErrorCode.NotConfigured,
                    CheatErrorCode.None,
                    "Cheat execution is not configured.");
            }

            if (!discardCommand.Tile.HasValue)
            {
                return RoundCheatPreparation.Rejected(
                    RoundCheatErrorCode.InvalidCommand,
                    CheatErrorCode.None,
                    "The discard command has no physical tile.");
            }

            if (discardCommand.EligibleSeats != null && discardCommand.EligibleSeats.Count > 0)
            {
                return RoundCheatPreparation.Rejected(
                    RoundCheatErrorCode.InvalidCommand,
                    CheatErrorCode.None,
                    "Reservation delivery is only composed with a discard that opens no response window.");
            }

            RoundPreparation suppressed = round.PrepareDiscardSuppressed(discardCommand);
            if (!suppressed.IsReady)
            {
                return RoundCheatPreparation.Rejected(
                    RoundCheatErrorCode.ContextMismatch,
                    CheatErrorCode.None,
                    suppressed.Error!.Message);
            }

            RoundStateMachine drawState = suppressed.Change!.NextState;
            int nextSeat = drawState.CurrentSeat;
            CheatState current = cheat;
            var events = new List<RoundCheatEvent>();
            CheatTransition settled = CheatSubmission.SettleOffTurnForDraw(current, nextSeat);
            if (settled.State != null && settled.Changed)
            {
                current = settled.State;
                for (int i = 0; i < settled.Events.Count; i++)
                {
                    events.Add(Wrap(settled.Events[i], command: null, cheat, BuildDeliveryContext(
                        drawState,
                        round,
                        current,
                        nowMillis)));
                }
            }

            int turn = CountDrawTurns(round);
            CheatTurnContext deliveryContext = BuildDeliveryContext(drawState, round, current, nowMillis);
            CheatTransition delivery = CheatSubmission.DeliverSleeveBloomReservation(
                current,
                drawState.Tiles,
                deliveryContext,
                nextSeat);
            if (!delivery.Success || delivery.State == null || delivery.Map == null)
            {
                return RoundCheatPreparation.Rejected(
                    RoundCheatErrorCode.CheatRejected,
                    delivery.Error,
                    delivery.Message ?? "Reservation delivery failed.");
            }

            SleeveBloomReservation? reservation = cheat.SleeveBloom.ReservationFor(nextSeat);
            TileId delivered = reservation?.Tile ?? delivery.Map.DrawnTile!.Value.Tile;
            var added = new List<RoundEvent>(suppressed.Change.AddedEvents);
            added.Add(new RoundEvent(RoundEventType.TileDrawn, 0, nextSeat, delivered, null));
            RoundPreparation composed = round.PrepareCheatComposed(
                delivery.Map,
                RoundPhase.CheatWindow,
                nextSeat,
                null,
                added);
            if (!composed.IsReady)
            {
                return RoundCheatPreparation.Rejected(
                    RoundCheatErrorCode.ContextMismatch,
                    delivery.Error,
                    composed.Error!.Message);
            }

            for (int i = 0; i < delivery.Events.Count; i++)
            {
                events.Add(Wrap(delivery.Events[i], command: null, cheat, deliveryContext));
            }

            CheatState nextCheat = delivery.State;
            if (nextSeat != discardCommand.Seat)
            {
                nextCheat = nextCheat.WithSleeveBloom(
                    nextCheat.SleeveBloom.WithOpenedWindow(discardCommand.Seat, turn, out _));
            }

            return RoundCheatPreparation.Ready(new RoundCheatChange(
                composed.Change!,
                nextCheat,
                events,
                accepted: true,
                CheatErrorCode.None,
                string.Empty));
        }

        private static CheatTurnContext BuildDeliveryContext(
            RoundStateMachine drawState,
            RoundStateMachine source,
            CheatState cheat,
            long nowMillis)
        {
            return new CheatTurnContext(
                drawState.CurrentSeat,
                drawState.Phase,
                false,
                drawState.RiichiDeclared,
                CountDrawTurns(source),
                source.RoundId,
                cheat.TileMarks.DealtRoundOrdinal,
                SleeveBloomDrawKind.Normal,
                nowMillis);
        }

        private static int CountDrawTurns(RoundStateMachine round)
        {
            int turns = 0;
            for (int i = 0; i < round.Events.Count; i++)
            {
                RoundEventType type = round.Events[i].Type;
                if (type == RoundEventType.TileDrawn || type == RoundEventType.RinshanDrawn)
                {
                    turns++;
                }
            }

            return turns;
        }

        /// <summary>Pure next-normal-draw bridge. An outstanding off-turn offer is first settled as a
        /// FIRST reservation (V4 settleSleeveBloomOfferForDraw) and that reservation is then
        /// delivered as the real draw; both changes and the public TileDrawn event are one
        /// candidate. No charge is repeated at delivery.
        /// </summary>
        public static RoundCheatPreparation PrepareReservationDraw(
            RoundStateMachine round,
            CheatState? cheat,
            CheatTurnContext context,
            int seat)
        {
            if (round == null) throw new ArgumentNullException(nameof(round));
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (cheat == null)
            {
                return RoundCheatPreparation.Rejected(
                    RoundCheatErrorCode.NotConfigured,
                    CheatErrorCode.None,
                    "Cheat execution is not configured.");
            }

            if (round.IsFinished)
            {
                return RoundCheatPreparation.Rejected(
                    RoundCheatErrorCode.RoundFinished,
                    CheatErrorCode.None,
                    "The round is already finished.");
            }

            if (context.RoundId != round.RoundId ||
                context.CurrentPlayer != round.CurrentSeat ||
                context.Phase != round.Phase ||
                context.ResponseWindowOpen != (round.Window != null))
            {
                return RoundCheatPreparation.Rejected(
                    RoundCheatErrorCode.ContextMismatch,
                    CheatErrorCode.None,
                    "The cheat context does not match the authoritative round.");
            }

            CheatState current = cheat;
            var events = new List<RoundCheatEvent>();
            CheatTransition settled = CheatSubmission.SettleOffTurnForDraw(current, seat);
            if (settled.State != null && settled.Changed)
            {
                current = settled.State;
                for (int i = 0; i < settled.Events.Count; i++)
                {
                    events.Add(Wrap(settled.Events[i], command: null, cheat, context));
                }
            }

            CheatTransition delivery = CheatSubmission.DeliverSleeveBloomReservation(
                current,
                round.Tiles,
                context,
                seat);
            if (!delivery.Success || delivery.State == null || delivery.Map == null)
            {
                if (events.Count > 0)
                {
                    // The settle already changed the aggregate. Persist it as a rejected-with-
                    // state candidate rather than dropping it; the next draw can deliver.
                    RoundPreparation settledRound = round.PrepareCheatBridge(
                        round.Tiles,
                        round.Phase,
                        round.CurrentSeat,
                        round.Window,
                        RoundEventType.CheatApplied,
                        seat,
                        null,
                        "RESERVATION_SETTLE_ONLY:" + delivery.Error);
                    if (!settledRound.IsReady)
                    {
                        return RoundCheatPreparation.Rejected(
                            RoundCheatErrorCode.ContextMismatch,
                            delivery.Error,
                            settledRound.Error!.Message);
                    }

                    return RoundCheatPreparation.Ready(new RoundCheatChange(
                        settledRound.Change!,
                        current,
                        events,
                        accepted: false,
                        delivery.Error,
                        delivery.Message ?? string.Empty));
                }

                return RoundCheatPreparation.Rejected(
                    RoundCheatErrorCode.CheatRejected,
                    delivery.Error,
                    delivery.Message ?? "Reservation delivery failed.");
            }

            SleeveBloomReservation? reservation = cheat.SleeveBloom.ReservationFor(seat);
            TileId delivered = reservation?.Tile ?? delivery.Map.DrawnTile!.Value.Tile;
            RoundPreparation prepared = round.PrepareCheatBridge(
                delivery.Map,
                RoundPhase.CheatWindow,
                seat,
                null,
                RoundEventType.TileDrawn,
                seat,
                delivered,
                string.Empty);
            if (!prepared.IsReady)
            {
                return RoundCheatPreparation.Rejected(
                    RoundCheatErrorCode.ContextMismatch,
                    delivery.Error,
                    prepared.Error!.Message);
            }

            for (int i = 0; i < delivery.Events.Count; i++)
            {
                events.Add(Wrap(delivery.Events[i], command: null, current, context));
            }

            return RoundCheatPreparation.Ready(new RoundCheatChange(
                prepared.Change!,
                delivery.State,
                events,
                accepted: true,
                CheatErrorCode.None,
                string.Empty));
        }

        /// <summary>Authority sweep: settles every off-turn offer past its deadline as FIRST.</summary>
        public static RoundCheatPreparation ExpireOffTurn(
            RoundStateMachine round,
            CheatState? cheat,
            CheatTurnContext context)
        {
            if (round == null) throw new ArgumentNullException(nameof(round));
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (cheat == null)
            {
                return RoundCheatPreparation.Rejected(
                    RoundCheatErrorCode.NotConfigured,
                    CheatErrorCode.None,
                    "Cheat execution is not configured.");
            }

            CheatTransition transition = CheatSubmission.ExpireOffTurnSleeveBlooms(cheat, context);
            return Materialize(round, cheat, command: null, context, transition);
        }

        /// <summary>Timeout fallback for the immediate sleeve bloom offer: forced FIRST.</summary>
        public static RoundCheatPreparation ExpireImmediate(
            RoundStateMachine round,
            CheatState? cheat,
            CheatTurnContext context,
            int seat)
        {
            if (round == null) throw new ArgumentNullException(nameof(round));
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (cheat == null)
            {
                return RoundCheatPreparation.Rejected(
                    RoundCheatErrorCode.NotConfigured,
                    CheatErrorCode.None,
                    "Cheat execution is not configured.");
            }

            CheatTransition transition = CheatSubmission.ExpireSleeveBloomImmediate(cheat, context, seat);
            return Materialize(round, cheat, command: null, context, transition);
        }

        /// <summary>Timeout fallback for the beam swap offer: forced KEEP_ORIGINAL.</summary>
        public static RoundCheatPreparation ExpireBeamSwap(
            RoundStateMachine round,
            CheatState? cheat,
            CheatTurnContext context,
            int seat)
        {
            if (round == null) throw new ArgumentNullException(nameof(round));
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (cheat == null)
            {
                return RoundCheatPreparation.Rejected(
                    RoundCheatErrorCode.NotConfigured,
                    CheatErrorCode.None,
                    "Cheat execution is not configured.");
            }

            CheatTransition transition = CheatSubmission.ExpireBeamSwap(cheat, context, seat);
            return Materialize(round, cheat, command: null, context, transition);
        }

        private static RoundCheatPreparation? Guard(
            RoundStateMachine round,
            CheatState? cheat,
            RoundCheatCommand command,
            CheatTurnContext context)
        {
            if (cheat == null)
            {
                return RoundCheatPreparation.Rejected(
                    RoundCheatErrorCode.NotConfigured,
                    CheatErrorCode.None,
                    "Cheat execution is not configured.");
            }

            if (round.IsFinished)
            {
                return RoundCheatPreparation.Rejected(
                    RoundCheatErrorCode.RoundFinished,
                    CheatErrorCode.None,
                    "The round is already finished.");
            }

            if (!string.Equals(command.RoundId, round.RoundId, StringComparison.Ordinal))
            {
                return RoundCheatPreparation.Rejected(
                    RoundCheatErrorCode.RoundMismatch,
                    CheatErrorCode.None,
                    "Command round '" + command.RoundId + "' does not match '" + round.RoundId + "'.");
            }

            if (!string.Equals(context.RoundId, round.RoundId, StringComparison.Ordinal) ||
                context.CurrentPlayer != round.CurrentSeat ||
                context.Phase != round.Phase ||
                context.ResponseWindowOpen != (round.Window != null) ||
                context.RoundOrdinal != cheat.TileMarks.DealtRoundOrdinal)
            {
                return RoundCheatPreparation.Rejected(
                    RoundCheatErrorCode.ContextMismatch,
                    CheatErrorCode.None,
                    "The cheat context does not match the authoritative round/cheat state.");
            }

            for (int seat = 0; seat < 4; seat++)
            {
                if (context.RiichiDeclared(seat) != round.RiichiDeclared[seat])
                {
                    return RoundCheatPreparation.Rejected(
                        RoundCheatErrorCode.ContextMismatch,
                        CheatErrorCode.None,
                        "The cheat context riichi flags do not match the authoritative round.");
                }
            }

            return null;
        }

        private static RoundCheatPreparation Materialize(
            RoundStateMachine round,
            CheatState before,
            RoundCheatCommand? command,
            CheatTurnContext context,
            CheatTransition transition)
        {
            if (transition.State == null)
            {
                if (!transition.Success && !transition.Changed)
                {
                    return RoundCheatPreparation.Rejected(
                        RoundCheatErrorCode.CheatRejected,
                        transition.Error,
                        transition.Message ?? "Cheat transition rejected.");
                }

                return RoundCheatPreparation.Rejected(
                    RoundCheatErrorCode.CheatRejected,
                    transition.Error,
                    transition.Message ?? "Cheat transition produced no state.");
            }

            if (!transition.Changed)
            {
                // Exact Domain replay of the identical request: no version step, no new event,
                // no second charge. The actor returns the original durable receipt.
                return RoundCheatPreparation.Rejected(
                    RoundCheatErrorCode.CheatRejected,
                    CheatErrorCode.None,
                    "CHEAT_REPLAYED");
            }

            TileId? eventTile = null;
            string detail = command == null
                ? "CHEAT_SYSTEM:" + (transition.Success ? "OK" : transition.Error.ToString())
                : command.Action + ":" + (transition.Success ? "OK" : transition.Error.ToString());
            RoundPreparation prepared = round.PrepareCheatBridge(
                transition.Map ?? round.Tiles,
                round.Phase,
                round.CurrentSeat,
                round.Window,
                RoundEventType.CheatApplied,
                command?.Seat ?? -1,
                eventTile,
                detail);
            if (!prepared.IsReady)
            {
                return RoundCheatPreparation.Rejected(
                    RoundCheatErrorCode.ContextMismatch,
                    transition.Error,
                    prepared.Error!.Message);
            }

            var events = new List<RoundCheatEvent>(transition.Events.Count);
            for (int i = 0; i < transition.Events.Count; i++)
            {
                events.Add(Wrap(transition.Events[i], command, before, context));
            }

            return RoundCheatPreparation.Ready(new RoundCheatChange(
                prepared.Change!,
                transition.State,
                events,
                transition.Success,
                transition.Error,
                transition.Message ?? string.Empty));
        }

        private static RoundCheatEvent Wrap(
            CheatEvent value,
            RoundCheatCommand? command,
            CheatState before,
            CheatTurnContext context)
        {
            string? offerId = value.OfferId;
            SleeveBloomKeep? keep = value.Keep;
            BeamSwapAction? action = value.Action;
            TileId? resultTile = null;
            TileId? handTile = null;
            BeamSwapSlot? targetSlot = null;

            if (value.Kind == CheatEventKind.SleeveBloomResolved && offerId != null)
            {
                SleeveBloomOffer? offer = before.SleeveBloom.ImmediateOffer;
                if (offer == null || !string.Equals(offer.OfferId, offerId, StringComparison.Ordinal))
                {
                    for (int i = 0; i < before.SleeveBloom.OffTurnOffers.Count; i++)
                    {
                        if (string.Equals(before.SleeveBloom.OffTurnOffers[i].OfferId, offerId, StringComparison.Ordinal))
                        {
                            offer = before.SleeveBloom.OffTurnOffers[i];
                            break;
                        }
                    }
                }

                if (offer != null)
                {
                    resultTile = keep == SleeveBloomKeep.Second ? offer.SecondTile : offer.FirstTile;
                }
            }

            if (value.Kind == CheatEventKind.BeamSwapResolved && command != null)
            {
                handTile = command.Payload.HandTile;
                targetSlot = command.Payload.TargetSlot;
            }

            return new RoundCheatEvent(
                value,
                offerId,
                keep,
                action,
                resultTile,
                handTile,
                targetSlot,
                context.TurnNumber);
        }
    }
}
