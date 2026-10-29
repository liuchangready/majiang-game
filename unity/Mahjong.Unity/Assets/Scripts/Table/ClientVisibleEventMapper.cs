#nullable enable
using System;
using System.Collections.Generic;
using Mahjong.Client;
using Mahjong.Contracts;

namespace Mahjong.Unity.Table
{
    /// <summary>
    /// Why a confirmed, legal typed event deliberately did not become a visual
    /// animation. The event stays a state fact; the table model already applied
    /// the same accepted snapshot. This is never an error and never a fabricated
    /// tile/meld.
    /// </summary>
    public static class ClientVisibleEventStateOnlyCodes
    {
        public const string NoAnimation = "STATE_ONLY_NO_ANIMATION";
        public const string RedactedClosedKan = "STATE_ONLY_REDACTED_CLOSED_KAN";
        public const string UraNotAuthorized = "STATE_ONLY_URA_NOT_AUTHORIZED";
    }

    public sealed class ClientVisibleStateOnlyEvent
    {
        public ClientVisibleStateOnlyEvent(TableVisibleEvent source, string reasonCode)
        {
            Source = source ?? throw new ArgumentNullException(nameof(source));
            ReasonCode = reasonCode ?? string.Empty;
        }

        public TableVisibleEvent Source { get; }

        public ProtocolEventType Type => Source.Type;

        public ulong EventSeq => Source.EventSeq;

        public uint EventOrdinal => Source.EventOrdinal;

        public string ReasonCode { get; }
    }

    /// <summary>
    /// Result of mapping one already-accepted, immutable Client event batch into
    /// the existing confirmed animation vocabulary. On a baseline or legacy
    /// empty batch no event is playable, exactly matching the Client rule.
    /// </summary>
    public sealed class ClientVisibleEventMapping
    {
        private static readonly ConfirmedVisibleEvent[] NoAnimated = Array.Empty<ConfirmedVisibleEvent>();
        private static readonly ClientVisibleStateOnlyEvent[] NoStateOnly = Array.Empty<ClientVisibleStateOnlyEvent>();

        private ClientVisibleEventMapping(
            bool accepted,
            string code,
            bool isBaseline,
            bool isLegacyEmpty,
            ConfirmedAnimationContext? context,
            IReadOnlyList<ConfirmedVisibleEvent>? animated,
            IReadOnlyList<ClientVisibleStateOnlyEvent>? stateOnly)
        {
            Accepted = accepted;
            Code = code ?? string.Empty;
            IsBaseline = isBaseline;
            IsLegacyEmpty = isLegacyEmpty;
            Context = context;
            AnimatedEvents = animated ?? NoAnimated;
            StateOnlyEvents = stateOnly ?? NoStateOnly;
        }

        public bool Accepted { get; }

        public string Code { get; }

        /// <summary>True for the accepted full baseline/reset batch.</summary>
        public bool IsBaseline { get; }

        /// <summary>True for the compatibility empty batch of a legacy fake session.</summary>
        public bool IsLegacyEmpty { get; }

        /// <summary>Exact accepted identity of the batch; null for baseline/legacy/rejected.</summary>
        public ConfirmedAnimationContext? Context { get; }

        public IReadOnlyList<ConfirmedVisibleEvent> AnimatedEvents { get; }

        /// <summary>Legal confirmed events that carry state but intentionally no animation.</summary>
        public IReadOnlyList<ClientVisibleStateOnlyEvent> StateOnlyEvents { get; }

        internal static ClientVisibleEventMapping Reject(string code)
        {
            return new ClientVisibleEventMapping(false, code, false, false, null, null, null);
        }

        internal static ClientVisibleEventMapping LegacyEmpty()
        {
            return new ClientVisibleEventMapping(true, ClientVisibleEventMapper.LegacyEmptyCode, false, true, null, null, null);
        }

        internal static ClientVisibleEventMapping Baseline()
        {
            return new ClientVisibleEventMapping(true, ClientVisibleEventMapper.BaselineCode, true, false, null, null, null);
        }

        internal static ClientVisibleEventMapping AcceptedMapping(
            ConfirmedAnimationContext context,
            IReadOnlyList<ConfirmedVisibleEvent> animated,
            IReadOnlyList<ClientVisibleStateOnlyEvent> stateOnly)
        {
            return new ClientVisibleEventMapping(true, ClientVisibleEventMapper.AcceptedCode, false, false, context, animated, stateOnly);
        }
    }

    /// <summary>
    /// Pure presentation-side mapper. It consumes only an already-confirmed
    /// typed <see cref="TableVisibleEventBatch"/> and the exact projected table
    /// view of the same accepted snapshot. It never reads an Envelope, raw
    /// bytes, JSON, TableRuntime, Domain, server state or a later session
    /// binding. The batch identity must match the projected view exactly before
    /// any event is exposed to the animation queue.
    /// </summary>
    public sealed class ClientVisibleEventMapper
    {
        public const string AcceptedCode = "ACCEPTED";
        public const string BaselineCode = "BASELINE_RECONCILIATION_ONLY";
        public const string LegacyEmptyCode = "LEGACY_EMPTY_BATCH";
        public const string NoBatchCode = "NO_VISIBLE_EVENT_BATCH";
        public const string NoViewCode = "NO_CONFIRMED_VIEW";
        public const string ContextMismatchCode = "EVENT_VIEW_CONTEXT_MISMATCH";
        public const string NotReadyCode = "EVENT_VIEW_NOT_READY";
        public const string PrivateNotAuthorizedCode = "PRIVATE_EVENT_NOT_AUTHORIZED";
        public const string AuthorizationMismatchCode = "EVENT_AUTHORIZATION_MISMATCH";
        public const string UnsupportedEventCode = "UNSUPPORTED_VISIBLE_EVENT";
        public const string VisibilityMismatchCode = "EVENT_VISIBILITY_MISMATCH";
        public const string OwnerMismatchCode = "EVENT_OWNER_MISMATCH";
        public const string EventCoordinateMismatchCode = "EVENT_COORDINATE_MISMATCH";
        public const string EventSequenceMismatchCode = "EVENT_SEQUENCE_MISMATCH";
        public const string EventBodyMismatchCode = "EVENT_BODY_TYPE_MISMATCH";

        public ClientVisibleEventMapping Map(
            TableVisibleEventBatch? batch,
            PlayerTableViewState? view,
            long generation)
        {
            if (batch == null)
            {
                return ClientVisibleEventMapping.Reject(NoBatchCode);
            }

            if (batch.Kind == TableVisibleEventBatchKind.None)
            {
                return ClientVisibleEventMapping.LegacyEmpty();
            }

            if (batch.Kind == TableVisibleEventBatchKind.Baseline)
            {
                return ClientVisibleEventMapping.Baseline();
            }

            if (view == null)
            {
                return ClientVisibleEventMapping.Reject(NoViewCode);
            }

            if (generation < 0)
            {
                return ClientVisibleEventMapping.Reject(ContextMismatchCode);
            }

            if (view.ConnectionState != TableConnectionState.Ready)
            {
                return ClientVisibleEventMapping.Reject(NotReadyCode);
            }

            PlayerTableRole role = MapRole(batch.Role);
            if (!MatchesExactContext(batch, view, role))
            {
                return ClientVisibleEventMapping.Reject(ContextMismatchCode);
            }

            if (role == PlayerTableRole.Player)
            {
                if (!view.Identity.IsPrivateAuthorized)
                {
                    return ClientVisibleEventMapping.Reject(PrivateNotAuthorizedCode);
                }
            }
            else if (role == PlayerTableRole.Spectator)
            {
                if (view.Identity.IsPrivateAuthorized)
                {
                    return ClientVisibleEventMapping.Reject(AuthorizationMismatchCode);
                }
            }
            else
            {
                return ClientVisibleEventMapping.Reject(ContextMismatchCode);
            }

            var context = new ConfirmedAnimationContext(
                generation,
                batch.SessionId,
                batch.RoomId,
                batch.RoundId,
                batch.BindingEpoch,
                batch.StateVersion,
                batch.EventSeq,
                batch.StreamVersion,
                batch.ProjectionSeq,
                role,
                batch.Seat,
                batch.ScopeId,
                view.Identity.IsPrivateAuthorized,
                batch.OwnerEpoch,
                batch.ConnectionEpoch);

            var animated = new List<ConfirmedVisibleEvent>(batch.PlayableEvents.Count);
            var stateOnly = new List<ClientVisibleStateOnlyEvent>();
            for (int i = 0; i < batch.PlayableEvents.Count; i++)
            {
                TableVisibleEvent source = batch.PlayableEvents[i];
                if (source == null)
                {
                    return ClientVisibleEventMapping.Reject(UnsupportedEventCode);
                }

                if (source.Type == ProtocolEventType.Unspecified ||
                    source.Type != source.Payload.Type)
                {
                    return ClientVisibleEventMapping.Reject(EventBodyMismatchCode);
                }

                if (source.EventSeq == 0 ||
                    source.EventSeq > batch.EventSeq ||
                    source.CommandVersion == 0 ||
                    source.CommandVersion > batch.StateVersion)
                {
                    return ClientVisibleEventMapping.Reject(EventCoordinateMismatchCode);
                }

                bool isPrivateType = ProtocolEventPolicy.IsPrivateEventType(source.Type);
                if (isPrivateType)
                {
                    if (source.Visibility != ProtocolEventVisibility.Private)
                    {
                        return ClientVisibleEventMapping.Reject(VisibilityMismatchCode);
                    }

                    if (role != PlayerTableRole.Player ||
                        batch.Seat < 0 ||
                        source.OwnerSeat != batch.Seat ||
                        source.Seat != batch.Seat)
                    {
                        return ClientVisibleEventMapping.Reject(OwnerMismatchCode);
                    }
                }
                else
                {
                    if (source.Visibility != ProtocolEventVisibility.Public &&
                        source.Visibility != ProtocolEventVisibility.SettlementPublic)
                    {
                        return ClientVisibleEventMapping.Reject(VisibilityMismatchCode);
                    }

                    if (source.OwnerSeat != ProtocolConstants.NoSeat)
                    {
                        return ClientVisibleEventMapping.Reject(OwnerMismatchCode);
                    }

                    if ((source.Type == ProtocolEventType.TsumoWin || source.Type == ProtocolEventType.RonWin) &&
                        source.Visibility != ProtocolEventVisibility.SettlementPublic)
                    {
                        return ClientVisibleEventMapping.Reject(VisibilityMismatchCode);
                    }
                }

                switch (source.Type)
                {
                    case ProtocolEventType.StackingStarted:
                    case ProtocolEventType.WallBuilt:
                    case ProtocolEventType.DealingComplete:
                        stateOnly.Add(new ClientVisibleStateOnlyEvent(source, ClientVisibleEventStateOnlyCodes.NoAnimation));
                        continue;

                    case ProtocolEventType.KanDeclared:
                        if (IsRedactedClosedKan(source, batch.Seat))
                        {
                            stateOnly.Add(new ClientVisibleStateOnlyEvent(source, ClientVisibleEventStateOnlyCodes.RedactedClosedKan));
                            continue;
                        }

                        if (source.Seat < 0 || source.Seat > 3)
                        {
                            return ClientVisibleEventMapping.Reject(EventCoordinateMismatchCode);
                        }

                        break;

                    case ProtocolEventType.DoraRevealed:
                        if (source.Payload.IsUraDora && !source.Payload.UraAuthorized)
                        {
                            stateOnly.Add(new ClientVisibleStateOnlyEvent(source, ClientVisibleEventStateOnlyCodes.UraNotAuthorized));
                            continue;
                        }

                        break;
                }

                ConfirmedVisibleEventKind kind;
                if (!TryMapKind(source.Type, out kind))
                {
                    return ClientVisibleEventMapping.Reject(UnsupportedEventCode);
                }

                switch (source.Type)
                {
                    case ProtocolEventType.TileDiscarded:
                    case ProtocolEventType.ChiDeclared:
                    case ProtocolEventType.PonDeclared:
                    case ProtocolEventType.RiichiDeclared:
                        if (source.Seat < 0 || source.Seat > 3)
                        {
                            return ClientVisibleEventMapping.Reject(EventCoordinateMismatchCode);
                        }

                        break;
                }

                ConfirmedEventVisibility visibility = source.Visibility == ProtocolEventVisibility.Private
                    ? ConfirmedEventVisibility.Private
                    : ConfirmedEventVisibility.Public;

                animated.Add(new ConfirmedVisibleEvent(
                    source.EventSeq,
                    source.EventOrdinal,
                    kind,
                    visibility,
                    source.OwnerSeat,
                    source.Seat,
                    source.TileId));
            }

            return ClientVisibleEventMapping.AcceptedMapping(context, animated, stateOnly);
        }

        private static bool MatchesExactContext(
            TableVisibleEventBatch batch,
            PlayerTableViewState view,
            PlayerTableRole role)
        {
            PlayerTableIdentity identity = view.Identity;
            return role == identity.Role &&
                   string.Equals(batch.SessionId, identity.SessionId, StringComparison.Ordinal) &&
                   string.Equals(batch.RoomId, identity.RoomId, StringComparison.Ordinal) &&
                   string.Equals(batch.RoundId, identity.RoundId, StringComparison.Ordinal) &&
                   string.Equals(batch.ScopeId, identity.ScopeId, StringComparison.Ordinal) &&
                   batch.Seat == identity.Seat &&
                   batch.BindingEpoch == identity.BindingEpoch &&
                   batch.ConnectionEpoch == identity.ConnectionEpoch &&
                   batch.OwnerEpoch == identity.OwnerEpoch &&
                   batch.StateVersion == identity.StateVersion &&
                   batch.EventSeq == identity.EventSeq &&
                   batch.StreamVersion == identity.StreamVersion &&
                   batch.ProjectionSeq == identity.ProjectionSeq;
        }

        private static PlayerTableRole MapRole(ProtocolRole role)
        {
            switch (role)
            {
                case ProtocolRole.Player:
                    return PlayerTableRole.Player;
                case ProtocolRole.Spectator:
                    return PlayerTableRole.Spectator;
                default:
                    return PlayerTableRole.Unknown;
            }
        }

        private static bool IsRedactedClosedKan(TableVisibleEvent source, int ownSeat)
        {
            if (source.Payload.KanType != ProtocolMeldType.ClosedKan)
            {
                return false;
            }

            if (source.Seat == ownSeat &&
                source.Payload.Meld != null &&
                source.Payload.Meld.TileIds.Count == 4)
            {
                return false;
            }

            // A public opponent closed kan body is intentionally redacted. The
            // snapshot already carries the identity-free concealed meld, so no
            // tile can be invented here.
            return source.Payload.Meld == null ||
                   source.Payload.Meld.TileIds.Count == 0 ||
                   source.Seat != ownSeat;
        }

        private static bool TryMapKind(ProtocolEventType type, out ConfirmedVisibleEventKind kind)
        {
            switch (type)
            {
                case ProtocolEventType.TileDealt:
                    kind = ConfirmedVisibleEventKind.TileDealt;
                    return true;
                case ProtocolEventType.TileDrawn:
                    kind = ConfirmedVisibleEventKind.TileDrawn;
                    return true;
                case ProtocolEventType.TileDiscarded:
                    kind = ConfirmedVisibleEventKind.TileDiscarded;
                    return true;
                case ProtocolEventType.ChiDeclared:
                    kind = ConfirmedVisibleEventKind.ChiDeclared;
                    return true;
                case ProtocolEventType.PonDeclared:
                    kind = ConfirmedVisibleEventKind.PonDeclared;
                    return true;
                case ProtocolEventType.KanDeclared:
                    kind = ConfirmedVisibleEventKind.KanDeclared;
                    return true;
                case ProtocolEventType.RinshanDraw:
                    kind = ConfirmedVisibleEventKind.RinshanDraw;
                    return true;
                case ProtocolEventType.RiichiDeclared:
                    kind = ConfirmedVisibleEventKind.RiichiDeclared;
                    return true;
                case ProtocolEventType.TsumoWin:
                    kind = ConfirmedVisibleEventKind.TsumoWin;
                    return true;
                case ProtocolEventType.RonWin:
                    kind = ConfirmedVisibleEventKind.RonWin;
                    return true;
                case ProtocolEventType.DoraRevealed:
                    kind = ConfirmedVisibleEventKind.DoraRevealed;
                    return true;
                case ProtocolEventType.CheatPointsChanged:
                    kind = ConfirmedVisibleEventKind.CheatPointsChanged;
                    return true;
                case ProtocolEventType.DrawGame:
                    kind = ConfirmedVisibleEventKind.DrawGame;
                    return true;
                case ProtocolEventType.ScoreSettled:
                    kind = ConfirmedVisibleEventKind.ScoreSettled;
                    return true;
                case ProtocolEventType.RoundEnded:
                    kind = ConfirmedVisibleEventKind.RoundEnded;
                    return true;
                case ProtocolEventType.MatchEnded:
                    kind = ConfirmedVisibleEventKind.MatchEnded;
                    return true;
                case ProtocolEventType.SleeveBloomResolved:
                    kind = ConfirmedVisibleEventKind.SleeveBloomResolved;
                    return true;
                case ProtocolEventType.BeamSwapResolved:
                    kind = ConfirmedVisibleEventKind.BeamSwapResolved;
                    return true;
                default:
                    kind = ConfirmedVisibleEventKind.Unknown;
                    return false;
            }
        }
    }
}
