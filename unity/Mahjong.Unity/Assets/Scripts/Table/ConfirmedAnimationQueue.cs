using System;
using System.Collections.Generic;

namespace Mahjong.Unity.Table
{
    public static class TableAnimationCodes
    {
        public const string Accepted = "ACCEPTED";
        public const string BaselineReset = "BASELINE_RESET";
        public const string Duplicate = "DUPLICATE";
        public const string NoBaseline = "NO_CONFIRMED_BASELINE";
        public const string ContextMismatch = "CONFIRMATION_CONTEXT_MISMATCH";
        public const string StaleGeneration = "STALE_GENERATION";
        public const string StaleStateVersion = "STALE_STATE_VERSION";
        public const string StaleEventSeq = "STALE_EVENT_SEQ";
        public const string EventSeqRegression = "EVENT_SEQ_REGRESSION";
        public const string NonMonotonicSnapshot = "NON_MONOTONIC_SNAPSHOT";
        public const string PrivateNotAuthorized = "PRIVATE_EVENT_NOT_AUTHORIZED";
        public const string PrivateOwnerMissing = "PRIVATE_EVENT_OWNER_MISSING";
        public const string EventVisibilityMismatch = "EVENT_VISIBILITY_MISMATCH";
        public const string AudienceMismatch = "ANIMATION_AUDIENCE_MISMATCH";
        public const string UnsupportedEvent = "UNSUPPORTED_VISIBLE_EVENT";
        public const string NoPreviousSnapshot = "NO_PREVIOUS_SNAPSHOT";
        public const string StalePrevious = "STALE_PREVIOUS_SNAPSHOT";
    }

    public enum ConfirmedEventVisibility
    {
        Unknown = 0,
        Public = 1,
        Private = 2
    }

    public enum ConfirmedVisibleEventKind
    {
        Unknown = 0,
        TileDealt = 1,
        TileDrawn = 2,
        TileDiscarded = 3,
        ChiDeclared = 4,
        PonDeclared = 5,
        KanDeclared = 6,
        RinshanDraw = 7,
        RiichiDeclared = 8,
        TsumoWin = 9,
        RonWin = 10,
        DoraRevealed = 11,
        CheatPointsChanged = 12,
        DrawGame = 13,
        ScoreSettled = 14,
        RoundEnded = 15,
        MatchEnded = 16,
        SleeveBloomResolved = 17,
        BeamSwapResolved = 18
    }

    public enum TableAnimationKind
    {
        Unknown = 0,
        Discard = 1,
        Meld = 2,
        OwnDraw = 3,
        Deal = 4,
        Draw = 5,
        Riichi = 6,
        Win = 7,
        Dora = 8,
        CheatPoints = 9,
        RoundEnd = 10,
        MatchEnd = 11,
        Settlement = 12,
        SleeveBloomResolved = 13,
        BeamSwapResolved = 14
    }

    public enum TableAnimationSource
    {
        SnapshotDifference = 1,
        VisibleEvent = 2
    }

    public sealed class ConfirmedAnimationContext
    {
        public ConfirmedAnimationContext(
            long generation,
            string sessionId,
            string roomId,
            string roundId,
            ulong bindingEpoch,
            ulong stateVersion,
            ulong eventSeq,
            ulong streamVersion,
            ulong projectionSeq,
            PlayerTableRole role = PlayerTableRole.Unknown,
            int seat = -1,
            string? scopeId = null,
            bool isPrivateAuthorized = false,
            ulong ownerEpoch = 0,
            ulong connectionEpoch = 0)
        {
            if (generation < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(generation));
            }

            if (seat < -1 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat));
            }

            Generation = generation;
            SessionId = sessionId ?? string.Empty;
            RoomId = roomId ?? string.Empty;
            RoundId = roundId ?? string.Empty;
            BindingEpoch = bindingEpoch;
            StateVersion = stateVersion;
            EventSeq = eventSeq;
            StreamVersion = streamVersion;
            ProjectionSeq = projectionSeq;
            Role = role;
            Seat = seat;
            ScopeId = scopeId ?? string.Empty;
            IsPrivateAuthorized = isPrivateAuthorized;
            OwnerEpoch = ownerEpoch;
            ConnectionEpoch = connectionEpoch;
        }

        public long Generation { get; }

        public string SessionId { get; }

        public string RoomId { get; }

        public string RoundId { get; }

        public ulong BindingEpoch { get; }

        /// <summary>Table state version, never a stream cursor and never an event count.</summary>
        public ulong StateVersion { get; }

        /// <summary>Event log sequence, independent of state version and stream version.</summary>
        public ulong EventSeq { get; }

        /// <summary>Per-stream projection version. It may repeat or lag; it is not a fence.</summary>
        public ulong StreamVersion { get; }

        /// <summary>Per-stream emission sequence, separate from every version above.</summary>
        public ulong ProjectionSeq { get; }

        /// <summary>Confirmed baseline role; events cannot override it.</summary>
        public PlayerTableRole Role { get; }

        /// <summary>Confirmed baseline seat; -1 for a non-player binding.</summary>
        public int Seat { get; }

        public string ScopeId { get; }

        public bool IsPrivateAuthorized { get; }

        public ulong OwnerEpoch { get; }

        public ulong ConnectionEpoch { get; }

        /// <summary>
        /// True when this context carries the authorization identity introduced
        /// for the R1/R2 fixes. Legacy 9-argument contexts report false and may
        /// only feed public events; private events require a full identity.
        /// </summary>
        public bool HasAuthorizationIdentity =>
            Role != PlayerTableRole.Unknown ||
            Seat >= 0 ||
            !string.IsNullOrEmpty(ScopeId) ||
            IsPrivateAuthorized ||
            OwnerEpoch != 0 ||
            ConnectionEpoch != 0;

        internal static ConfirmedAnimationContext FromIdentity(PlayerTableIdentity identity)
        {
            return new ConfirmedAnimationContext(
                identity.Generation,
                identity.SessionId,
                identity.RoomId,
                identity.RoundId,
                identity.BindingEpoch,
                identity.StateVersion,
                identity.EventSeq,
                identity.StreamVersion,
                identity.ProjectionSeq,
                identity.Role,
                identity.Seat,
                identity.ScopeId,
                identity.IsPrivateAuthorized,
                identity.OwnerEpoch,
                identity.ConnectionEpoch);
        }

        internal bool SameLegacyIdentity(ConfirmedAnimationContext other)
        {
            return Generation == other.Generation &&
                   string.Equals(SessionId, other.SessionId, StringComparison.Ordinal) &&
                   string.Equals(RoomId, other.RoomId, StringComparison.Ordinal) &&
                   string.Equals(RoundId, other.RoundId, StringComparison.Ordinal) &&
                   BindingEpoch == other.BindingEpoch;
        }

        internal bool SameIdentity(ConfirmedAnimationContext other)
        {
            if (!SameLegacyIdentity(other))
            {
                return false;
            }

            if (!HasAuthorizationIdentity || !other.HasAuthorizationIdentity)
            {
                return false;
            }

            return Role == other.Role &&
                   Seat == other.Seat &&
                   string.Equals(ScopeId, other.ScopeId, StringComparison.Ordinal) &&
                   IsPrivateAuthorized == other.IsPrivateAuthorized &&
                   OwnerEpoch == other.OwnerEpoch &&
                   ConnectionEpoch == other.ConnectionEpoch;
        }
    }

    /// <summary>
    /// Typed, already-confirmed visible event. This type is not a network
    /// envelope and cannot be constructed from raw bytes here.
    /// </summary>
    public sealed class ConfirmedVisibleEvent
    {
        public ConfirmedVisibleEvent(
            ulong eventSeq,
            uint eventOrdinal,
            ConfirmedVisibleEventKind kind,
            ConfirmedEventVisibility visibility,
            int ownerSeat,
            int seat,
            uint? tileId)
        {
            if (eventSeq == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(eventSeq), "A visible event needs a positive event sequence.");
            }

            if (ownerSeat < -1 || ownerSeat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(ownerSeat));
            }

            if (seat < -1 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat));
            }

            if (tileId.HasValue && tileId.Value > 135)
            {
                throw new ArgumentOutOfRangeException(nameof(tileId));
            }

            EventSeq = eventSeq;
            EventOrdinal = eventOrdinal;
            Kind = kind;
            Visibility = visibility;
            OwnerSeat = ownerSeat;
            Seat = seat;
            TileId = tileId;
        }

        public ulong EventSeq { get; }

        public uint EventOrdinal { get; }

        public ConfirmedVisibleEventKind Kind { get; }

        public ConfirmedEventVisibility Visibility { get; }

        /// <summary>Required for a private event; -1 is allowed for a public event.</summary>
        public int OwnerSeat { get; }

        public int Seat { get; }

        public uint? TileId { get; }
    }

    public sealed class TableAnimationAudience
    {
        private TableAnimationAudience(bool isSpectator, int seat)
        {
            IsSpectator = isSpectator;
            Seat = seat;
        }

        public bool IsSpectator { get; }

        public int Seat { get; }

        public static TableAnimationAudience Player(int seat)
        {
            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat));
            }

            return new TableAnimationAudience(false, seat);
        }

        public static TableAnimationAudience Spectator()
        {
            return new TableAnimationAudience(true, -1);
        }
    }

    public sealed class TableAnimationItem
    {
        public TableAnimationItem(
            long sequence,
            TableAnimationKind kind,
            TableAnimationSource source,
            ConfirmedAnimationContext context,
            int seat,
            uint? tileId,
            PlayerDiscardState? discard,
            PlayerMeldState? meld,
            bool isPrivate,
            int ownerSeat)
        {
            if (sequence <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sequence));
            }

            Sequence = sequence;
            Kind = kind;
            Source = source;
            Context = context ?? throw new ArgumentNullException(nameof(context));
            Seat = seat;
            TileId = tileId;
            Discard = discard;
            Meld = meld;
            IsPrivate = isPrivate;
            OwnerSeat = ownerSeat;
        }

        public long Sequence { get; }

        public TableAnimationKind Kind { get; }

        public TableAnimationSource Source { get; }

        public ConfirmedAnimationContext Context { get; }

        public int Seat { get; }

        public uint? TileId { get; }

        public PlayerDiscardState? Discard { get; }

        public PlayerMeldState? Meld { get; }

        public bool IsPrivate { get; }

        public int OwnerSeat { get; }
    }

    public sealed class TableAnimationResult
    {
        private TableAnimationResult(bool accepted, string code, int enqueued, int dropped)
        {
            Accepted = accepted;
            Code = code;
            Enqueued = enqueued;
            Dropped = dropped;
        }

        public bool Accepted { get; }

        public string Code { get; }

        public int Enqueued { get; }

        public int Dropped { get; }

        internal static TableAnimationResult Ok(string code, int enqueued, int dropped)
        {
            return new TableAnimationResult(true, code, enqueued, dropped);
        }

        internal static TableAnimationResult Rejected(string code)
        {
            return new TableAnimationResult(false, code, 0, 0);
        }
    }

    /// <summary>
    /// Bounded visual-only queue fed by confirmed snapshot differences or typed
    /// visible events. It never parses bytes, never owns synchronization, never
    /// mutates the table projection and never gates the confirmed input path.
    ///
    /// Identity fields are kept separate: state version fences snapshots, event
    /// sequence fences events, and stream/projection cursors are context only.
    /// </summary>
    public sealed class ConfirmedAnimationQueue
    {
        public const int DefaultCapacity = 64;
        private const int RecentKeyLimit = 512;

        private readonly int _capacity;
        private readonly Queue<TableAnimationItem> _items = new Queue<TableAnimationItem>();
        private readonly HashSet<string> _seenKeys = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<string> _seenOrder = new Queue<string>();

        private ConfirmedAnimationContext? _context;
        private PlayerTableViewState? _lastView;
        private ulong _lastStateVersion;
        private ulong _lastSnapshotEventSeq;
        private ulong _lastPlayedEventSeq;
        private uint _lastPlayedEventOrdinal;
        private long _sequence;
        private long _dropped;
        private string _lastRejection = string.Empty;

        public ConfirmedAnimationQueue(int capacity = DefaultCapacity)
        {
            if (capacity <= 0 || capacity > 4096)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Animation capacity must be 1..4096.");
            }

            _capacity = capacity;
        }

        public int Capacity => _capacity;

        public int PendingCount => _items.Count;

        public long DroppedCount => _dropped;

        public string LastRejection => _lastRejection;

        public bool HasConfirmedBaseline => _context != null;

        public ConfirmedAnimationContext? Context => _context;

        /// <summary>
        /// Accepts a confirmed full baseline. A new generation/binding/round is
        /// only admitted here; a non-baseline difference with another identity
        /// is rejected by <see cref="AcceptConfirmedDifference"/>.
        /// </summary>
        public TableAnimationResult AcceptConfirmedBaseline(PlayerTableViewState current)
        {
            if (current == null)
            {
                throw new ArgumentNullException(nameof(current));
            }

            ConfirmedAnimationContext next = ConfirmedAnimationContext.FromIdentity(current.Identity);
            if (_context != null)
            {
                if (next.Generation < _context.Generation)
                {
                    return Reject(TableAnimationCodes.StaleGeneration);
                }

                if (next.Generation == _context.Generation)
                {
                    if (next.BindingEpoch < _context.BindingEpoch)
                    {
                        return Reject(TableAnimationCodes.ContextMismatch);
                    }

                    if (next.BindingEpoch == _context.BindingEpoch)
                    {
                        if (!string.Equals(next.SessionId, _context.SessionId, StringComparison.Ordinal))
                        {
                            return Reject(TableAnimationCodes.ContextMismatch);
                        }

                        if (next.StateVersion < _lastStateVersion)
                        {
                            return Reject(TableAnimationCodes.StaleStateVersion);
                        }
                    }
                }
            }

            ClearItems();
            _context = next;
            _lastView = current;
            _lastStateVersion = next.StateVersion;
            _lastSnapshotEventSeq = next.EventSeq;
            _lastPlayedEventSeq = next.EventSeq;
            _lastPlayedEventOrdinal = uint.MaxValue;
            _lastRejection = string.Empty;
            return TableAnimationResult.Ok(TableAnimationCodes.BaselineReset, 0, 0);
        }

        /// <summary>
        /// Accepts a confirmed snapshot difference (previous confirmed view to
        /// current confirmed view). The caller supplies both states; this queue
        /// does not observe or reconstruct an optimistic state.
        /// </summary>
        public TableAnimationResult AcceptConfirmedDifference(
            PlayerTableViewState previous,
            PlayerTableViewState current,
            bool isFullBaseline)
        {
            if (previous == null)
            {
                throw new ArgumentNullException(nameof(previous));
            }

            if (current == null)
            {
                throw new ArgumentNullException(nameof(current));
            }

            if (isFullBaseline)
            {
                return AcceptConfirmedBaseline(current);
            }

            if (_context == null)
            {
                return Reject(TableAnimationCodes.NoBaseline);
            }

            if (!IsCurrentPrevious(previous))
            {
                return Reject(TableAnimationCodes.StalePrevious);
            }

            ConfirmedAnimationContext next = ConfirmedAnimationContext.FromIdentity(current.Identity);
            if (!_context.SameIdentity(next))
            {
                return Reject(TableAnimationCodes.ContextMismatch);
            }

            if (next.Generation < _context.Generation)
            {
                return Reject(TableAnimationCodes.StaleGeneration);
            }

            // These two version families are intentionally checked separately.
            if (next.StateVersion < _lastStateVersion)
            {
                return Reject(TableAnimationCodes.StaleStateVersion);
            }

            if (next.EventSeq < _lastSnapshotEventSeq)
            {
                return Reject(TableAnimationCodes.EventSeqRegression);
            }

            if (next.StateVersion == _lastStateVersion)
            {
                // The same confirmed state may be re-emitted with a newer
                // stream cursor. It produces no visual item, but it is the new
                // baseline for the next difference: otherwise the next legal
                // state would be rejected as stale against an older cursor.
                _context = next;
                _lastView = current;
                if (next.EventSeq > _lastSnapshotEventSeq)
                {
                    _lastSnapshotEventSeq = next.EventSeq;
                }

                _lastRejection = string.Empty;
                return TableAnimationResult.Ok(TableAnimationCodes.Duplicate, 0, 0);
            }

            TableAnimationResult? invalid = ValidateSnapshotMonotonic(previous, current);
            if (invalid != null)
            {
                return invalid;
            }

            int enqueued = 0;
            int droppedBefore = (int)Math.Min(int.MaxValue, _dropped);
            for (int seat = 0; seat < 4; seat++)
            {
                PlayerSeatState? before = previous.Seat(seat);
                PlayerSeatState? after = current.Seat(seat);
                if (before == null || after == null || !before.HasPublicData || !after.HasPublicData)
                {
                    continue;
                }

                var beforeDiscards = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < before.Discards.Count; i++)
                {
                    beforeDiscards.Add(before.Discards[i].EntityKey);
                }

                for (int i = 0; i < after.Discards.Count; i++)
                {
                    PlayerDiscardState discard = after.Discards[i];
                    if (beforeDiscards.Contains(discard.EntityKey))
                    {
                        continue;
                    }

                    string key = "s/" + next.StateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + "/d/" + seat.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + discard.EntityKey;
                    if (Remember(key))
                    {
                        continue;
                    }

                    if (Remember(DiscardEntityKey(seat, discard.TileId)))
                    {
                        continue;
                    }

                    Enqueue(
                        new TableAnimationItem(
                            ++_sequence,
                            TableAnimationKind.Discard,
                            TableAnimationSource.SnapshotDifference,
                            next,
                            seat,
                            discard.TileId,
                            discard,
                            null,
                            false,
                            -1));
                    enqueued++;
                }

                var beforeMelds = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < before.Melds.Count; i++)
                {
                    beforeMelds.Add(before.Melds[i].EntityKey);
                }

                for (int i = 0; i < after.Melds.Count; i++)
                {
                    PlayerMeldState meld = after.Melds[i];
                    if (beforeMelds.Contains(meld.EntityKey))
                    {
                        continue;
                    }

                    string key = "s/" + next.StateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + "/m/" + seat.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + meld.EntityKey;
                    if (Remember(key))
                    {
                        continue;
                    }

                    var meldKeys = MeldEntityKeys(seat, meld);
                    if (AnyKeySeen(meldKeys))
                    {
                        continue;
                    }

                    for (int m = 0; m < meldKeys.Count; m++)
                    {
                        Remember(meldKeys[m]);
                    }

                    bool privateMeld = current.Own != null &&
                                       current.Own.Seat == seat &&
                                       meld.IsConcealedKan &&
                                       !meld.IsPlaceholder;
                    Enqueue(
                        new TableAnimationItem(
                            ++_sequence,
                            TableAnimationKind.Meld,
                            TableAnimationSource.SnapshotDifference,
                            next,
                            seat,
                            meld.TileIds.Count > 0 ? meld.TileIds[0] : (uint?)null,
                            null,
                            meld,
                            privateMeld,
                            privateMeld ? seat : -1));
                    enqueued++;
                }
            }

            if (previous.Own != null && current.Own != null && previous.Own.Seat == current.Own.Seat)
            {
                uint? beforeDrawn = previous.Own.DrawnTileId;
                uint? afterDrawn = current.Own.DrawnTileId;
                if (afterDrawn.HasValue && beforeDrawn != afterDrawn)
                {
                    string key = "s/" + next.StateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + "/draw/" + current.Own.Seat.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + "/" + afterDrawn.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (!Remember(key) &&
                        !Remember(DrawEntityKey(current.Own.Seat, afterDrawn.Value)))
                    {
                        Enqueue(
                            new TableAnimationItem(
                                ++_sequence,
                                TableAnimationKind.OwnDraw,
                                TableAnimationSource.SnapshotDifference,
                                next,
                                current.Own.Seat,
                                afterDrawn,
                                null,
                                null,
                                true,
                                current.Own.Seat));
                        enqueued++;
                    }
                }
            }

            _context = next;
            _lastView = current;
            _lastStateVersion = next.StateVersion;
            _lastSnapshotEventSeq = next.EventSeq;
            _lastRejection = string.Empty;

            int dropped = (int)Math.Min(int.MaxValue, _dropped) - droppedBefore;
            if (enqueued == 0)
            {
                return TableAnimationResult.Ok(TableAnimationCodes.Duplicate, 0, dropped);
            }

            return TableAnimationResult.Ok(TableAnimationCodes.Accepted, enqueued, dropped);
        }

        /// <summary>
        /// Accepts typed visible events that the owner has already confirmed.
        /// The batch is validated in full before any item is enqueued, so a
        /// private event cannot leak into a spectator/player queue by ordering.
        /// </summary>
        public TableAnimationResult AcceptConfirmedEvents(
            ConfirmedAnimationContext context,
            IReadOnlyList<ConfirmedVisibleEvent> events,
            TableAnimationAudience audience)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (events == null)
            {
                throw new ArgumentNullException(nameof(events));
            }

            if (audience == null)
            {
                throw new ArgumentNullException(nameof(audience));
            }

            if (_context == null)
            {
                return Reject(TableAnimationCodes.NoBaseline);
            }

            bool legacyContext = !context.HasAuthorizationIdentity;
            if (legacyContext)
            {
                // Compatibility for the original 9-argument public event API:
                // it may carry public events only and can never authorize a
                // private event.
                if (!_context.SameLegacyIdentity(context))
                {
                    return Reject(TableAnimationCodes.ContextMismatch);
                }
            }
            else if (!_context.SameIdentity(context) || context.Generation < _context.Generation)
            {
                return Reject(TableAnimationCodes.ContextMismatch);
            }

            if (!AudienceMatchesBaseline(audience))
            {
                return Reject(TableAnimationCodes.AudienceMismatch);
            }

            for (int i = 0; i < events.Count; i++)
            {
                ConfirmedVisibleEvent visible = events[i];
                if (!IsSupported(visible.Kind))
                {
                    return Reject(TableAnimationCodes.UnsupportedEvent);
                }

                if (IsPrivateOnly(visible.Kind) && visible.Visibility != ConfirmedEventVisibility.Private)
                {
                    return Reject(TableAnimationCodes.EventVisibilityMismatch);
                }

                if (IsPublicOnly(visible.Kind) && visible.Visibility != ConfirmedEventVisibility.Public)
                {
                    return Reject(TableAnimationCodes.EventVisibilityMismatch);
                }

                if (visible.Visibility == ConfirmedEventVisibility.Private)
                {
                    if (legacyContext)
                    {
                        return Reject(TableAnimationCodes.ContextMismatch);
                    }

                    if (visible.OwnerSeat < 0)
                    {
                        return Reject(TableAnimationCodes.PrivateOwnerMissing);
                    }

                    if (audience.IsSpectator || visible.OwnerSeat != audience.Seat)
                    {
                        return Reject(TableAnimationCodes.PrivateNotAuthorized);
                    }
                }
                else if (visible.Visibility != ConfirmedEventVisibility.Public)
                {
                    return Reject(TableAnimationCodes.UnsupportedEvent);
                }
            }

            int enqueued = 0;
            for (int i = 0; i < events.Count; i++)
            {
                ConfirmedVisibleEvent visible = events[i];
                if (visible.EventSeq < _lastPlayedEventSeq ||
                    (visible.EventSeq == _lastPlayedEventSeq && visible.EventOrdinal <= _lastPlayedEventOrdinal))
                {
                    _lastRejection = TableAnimationCodes.StaleEventSeq;
                    continue;
                }

                string key = "e/" + visible.EventSeq.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "/" + visible.EventOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "/" + ((int)visible.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (Remember(key))
                {
                    _lastRejection = TableAnimationCodes.Duplicate;
                    continue;
                }

                if (visible.Kind == ConfirmedVisibleEventKind.TileDiscarded &&
                    visible.Seat >= 0 &&
                    visible.TileId.HasValue &&
                    Remember(DiscardEntityKey(visible.Seat, visible.TileId.Value)))
                {
                    _lastRejection = TableAnimationCodes.Duplicate;
                    continue;
                }

                if ((visible.Kind == ConfirmedVisibleEventKind.TileDrawn ||
                     visible.Kind == ConfirmedVisibleEventKind.RinshanDraw) &&
                    visible.Seat >= 0 &&
                    visible.TileId.HasValue &&
                    Remember(DrawEntityKey(visible.Seat, visible.TileId.Value)))
                {
                    _lastRejection = TableAnimationCodes.Duplicate;
                    continue;
                }

                if (IsMeldEvent(visible.Kind) &&
                    visible.Seat >= 0 &&
                    visible.TileId.HasValue &&
                    Remember(MeldTileKey(visible.Seat, visible.TileId.Value)))
                {
                    _lastRejection = TableAnimationCodes.Duplicate;
                    continue;
                }

                Enqueue(
                    new TableAnimationItem(
                        ++_sequence,
                        MapKind(visible.Kind),
                        TableAnimationSource.VisibleEvent,
                        context,
                        visible.Seat,
                        visible.TileId,
                        null,
                        null,
                        visible.Visibility == ConfirmedEventVisibility.Private,
                        visible.OwnerSeat));
                enqueued++;

                if (visible.EventSeq > _lastPlayedEventSeq)
                {
                    _lastPlayedEventSeq = visible.EventSeq;
                    _lastPlayedEventOrdinal = visible.EventOrdinal;
                }
                else if (visible.EventOrdinal > _lastPlayedEventOrdinal)
                {
                    _lastPlayedEventOrdinal = visible.EventOrdinal;
                }
            }

            if (enqueued == 0)
            {
                return TableAnimationResult.Ok(TableAnimationCodes.Duplicate, 0, 0);
            }

            return TableAnimationResult.Ok(TableAnimationCodes.Accepted, enqueued, 0);
        }

        public bool TryDequeue(out TableAnimationItem? item)
        {
            if (_items.Count == 0)
            {
                item = null;
                return false;
            }

            item = _items.Dequeue();
            return true;
        }

        public IReadOnlyList<TableAnimationItem> Drain()
        {
            var result = new List<TableAnimationItem>(_items.Count);
            while (_items.Count > 0)
            {
                result.Add(_items.Dequeue());
            }

            return result;
        }

        /// <summary>Clears visual items only; the confirmed baseline/context is retained.</summary>
        public void Clear()
        {
            ClearItems();
        }

        private static string DiscardEntityKey(int seat, uint tileId)
        {
            return "discard/" + seat.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "/" + tileId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string DrawEntityKey(int seat, uint tileId)
        {
            return "draw/" + seat.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "/" + tileId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string MeldTileKey(int seat, uint tileId)
        {
            return "meld/" + seat.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "/" + tileId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static List<string> MeldEntityKeys(int seat, PlayerMeldState meld)
        {
            var keys = new List<string>(Math.Max(1, meld.TileIds.Count));
            if (meld.IsPlaceholder)
            {
                keys.Add("meld/" + seat.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "/placeholder/" + meld.PlaceholderOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return keys;
            }

            for (int i = 0; i < meld.TileIds.Count; i++)
            {
                keys.Add(MeldTileKey(seat, meld.TileIds[i]));
            }

            return keys;
        }

        private bool AnyKeySeen(IReadOnlyList<string> keys)
        {
            for (int i = 0; i < keys.Count; i++)
            {
                if (_seenKeys.Contains(keys[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsMeldEvent(ConfirmedVisibleEventKind kind)
        {
            return kind == ConfirmedVisibleEventKind.ChiDeclared ||
                   kind == ConfirmedVisibleEventKind.PonDeclared ||
                   kind == ConfirmedVisibleEventKind.KanDeclared;
        }

        private bool IsCurrentPrevious(PlayerTableViewState previous)
        {
            if (_lastView == null)
            {
                return false;
            }

            PlayerTableIdentity before = previous.Identity;
            PlayerTableIdentity current = _lastView.Identity;
            return before.Generation == current.Generation &&
                   string.Equals(before.SessionId, current.SessionId, StringComparison.Ordinal) &&
                   string.Equals(before.RoomId, current.RoomId, StringComparison.Ordinal) &&
                   string.Equals(before.RoundId, current.RoundId, StringComparison.Ordinal) &&
                   before.BindingEpoch == current.BindingEpoch &&
                   before.Role == current.Role &&
                   before.Seat == current.Seat &&
                   string.Equals(before.ScopeId, current.ScopeId, StringComparison.Ordinal) &&
                   before.IsPrivateAuthorized == current.IsPrivateAuthorized &&
                   before.OwnerEpoch == current.OwnerEpoch &&
                   before.ConnectionEpoch == current.ConnectionEpoch &&
                   before.StateVersion == current.StateVersion &&
                   before.EventSeq == current.EventSeq &&
                   before.StreamVersion == current.StreamVersion &&
                   before.ProjectionSeq == current.ProjectionSeq;
        }

        private TableAnimationResult? ValidateSnapshotMonotonic(PlayerTableViewState previous, PlayerTableViewState current)
        {
            for (int seat = 0; seat < 4; seat++)
            {
                PlayerSeatState? before = previous.Seat(seat);
                PlayerSeatState? after = current.Seat(seat);
                if (before == null || after == null)
                {
                    continue;
                }

                if (before.HasPublicData && !after.HasPublicData)
                {
                    return RejectAndClear(TableAnimationCodes.NonMonotonicSnapshot);
                }

                for (int i = 0; i < before.Discards.Count; i++)
                {
                    PlayerDiscardState discard = before.Discards[i];
                    if (ContainsDiscard(after.Discards, discard.EntityKey))
                    {
                        continue;
                    }

                    if (MovesToNewMeld(previous, current, seat, discard))
                    {
                        continue;
                    }

                    return RejectAndClear(TableAnimationCodes.NonMonotonicSnapshot);
                }

                for (int i = 0; i < before.Melds.Count; i++)
                {
                    PlayerMeldState meld = before.Melds[i];
                    if (ContainsMeld(after.Melds, meld.EntityKey))
                    {
                        continue;
                    }

                    if (IsConfirmedMeldUpgrade(current, seat, meld))
                    {
                        continue;
                    }

                    return RejectAndClear(TableAnimationCodes.NonMonotonicSnapshot);
                }
            }

            return null;
        }

        private static bool ContainsDiscard(IReadOnlyList<PlayerDiscardState> discards, string key)
        {
            for (int i = 0; i < discards.Count; i++)
            {
                if (string.Equals(discards[i].EntityKey, key, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsMeld(IReadOnlyList<PlayerMeldState> melds, string key)
        {
            for (int i = 0; i < melds.Count; i++)
            {
                if (string.Equals(melds[i].EntityKey, key, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// A river tile may leave the river because it was called into a new
        /// meld. Only the already-confirmed before/after facts are compared:
        /// the new meld's called_tile_id and from_seat must identify the removed
        /// discard, and that meld must not already have existed.
        /// </summary>
        private static bool MovesToNewMeld(
            PlayerTableViewState previous,
            PlayerTableViewState current,
            int fromSeat,
            PlayerDiscardState discard)
        {
            for (int seat = 0; seat < 4; seat++)
            {
                PlayerSeatState? after = current.Seat(seat);
                PlayerSeatState? before = previous.Seat(seat);
                if (after == null || before == null)
                {
                    continue;
                }

                for (int i = 0; i < after.Melds.Count; i++)
                {
                    PlayerMeldState meld = after.Melds[i];
                    if (!meld.CalledTileId.HasValue ||
                        meld.CalledTileId.Value != discard.TileId ||
                        meld.FromSeat != fromSeat ||
                        ContainsMeld(before.Melds, meld.EntityKey))
                    {
                        continue;
                    }

                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Pon -> added/open kan and other confirmed meld upgrades keep the old
        /// tiles and add at least one physical identity on the same seat. This
        /// is a position migration, not a snapshot rollback.
        /// </summary>
        private static bool IsConfirmedMeldUpgrade(
            PlayerTableViewState current,
            int seat,
            PlayerMeldState before)
        {
            if (before.IsPlaceholder)
            {
                return false;
            }

            PlayerSeatState? after = current.Seat(seat);
            if (after == null)
            {
                return false;
            }

            for (int i = 0; i < after.Melds.Count; i++)
            {
                PlayerMeldState candidate = after.Melds[i];
                if (candidate.IsPlaceholder ||
                    candidate.FromSeat != before.FromSeat ||
                    candidate.TileIds.Count <= before.TileIds.Count)
                {
                    continue;
                }

                bool covers = true;
                for (int t = 0; t < before.TileIds.Count; t++)
                {
                    if (!ContainsTile(candidate.TileIds, before.TileIds[t]))
                    {
                        covers = false;
                        break;
                    }
                }

                if (covers)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsTile(IReadOnlyList<uint> tiles, uint tile)
        {
            for (int i = 0; i < tiles.Count; i++)
            {
                if (tiles[i] == tile)
                {
                    return true;
                }
            }

            return false;
        }

        private TableAnimationResult RejectAndClear(string code)
        {
            ClearItems();
            return Reject(code);
        }

        private void Enqueue(TableAnimationItem item)
        {
            while (_items.Count >= _capacity)
            {
                _items.Dequeue();
                _dropped++;
            }

            _items.Enqueue(item);
        }

        private bool Remember(string key)
        {
            if (!_seenKeys.Add(key))
            {
                return true;
            }

            _seenOrder.Enqueue(key);
            while (_seenOrder.Count > RecentKeyLimit)
            {
                string expired = _seenOrder.Dequeue();
                _seenKeys.Remove(expired);
            }

            return false;
        }

        private void ClearItems()
        {
            _items.Clear();
            _seenKeys.Clear();
            _seenOrder.Clear();
        }

        private TableAnimationResult Reject(string code)
        {
            _lastRejection = code;
            return TableAnimationResult.Rejected(code);
        }

        private static bool IsSupported(ConfirmedVisibleEventKind kind)
        {
            switch (kind)
            {
                case ConfirmedVisibleEventKind.TileDealt:
                case ConfirmedVisibleEventKind.TileDrawn:
                case ConfirmedVisibleEventKind.TileDiscarded:
                case ConfirmedVisibleEventKind.ChiDeclared:
                case ConfirmedVisibleEventKind.PonDeclared:
                case ConfirmedVisibleEventKind.KanDeclared:
                case ConfirmedVisibleEventKind.RinshanDraw:
                case ConfirmedVisibleEventKind.RiichiDeclared:
                case ConfirmedVisibleEventKind.TsumoWin:
                case ConfirmedVisibleEventKind.RonWin:
                case ConfirmedVisibleEventKind.DoraRevealed:
                case ConfirmedVisibleEventKind.CheatPointsChanged:
                case ConfirmedVisibleEventKind.DrawGame:
                case ConfirmedVisibleEventKind.ScoreSettled:
                case ConfirmedVisibleEventKind.RoundEnded:
                case ConfirmedVisibleEventKind.MatchEnded:
                case ConfirmedVisibleEventKind.SleeveBloomResolved:
                case ConfirmedVisibleEventKind.BeamSwapResolved:
                    return true;
                default:
                    return false;
            }
        }

        private bool AudienceMatchesBaseline(TableAnimationAudience audience)
        {
            if (_context == null)
            {
                return false;
            }

            if (_context.Role == PlayerTableRole.Player && _context.IsPrivateAuthorized)
            {
                return !audience.IsSpectator && audience.Seat == _context.Seat;
            }

            return audience.IsSpectator;
        }

        private static bool IsPrivateOnly(ConfirmedVisibleEventKind kind)
        {
            return kind == ConfirmedVisibleEventKind.TileDealt ||
                   kind == ConfirmedVisibleEventKind.TileDrawn ||
                   kind == ConfirmedVisibleEventKind.RinshanDraw ||
                   kind == ConfirmedVisibleEventKind.CheatPointsChanged ||
                   kind == ConfirmedVisibleEventKind.SleeveBloomResolved ||
                   kind == ConfirmedVisibleEventKind.BeamSwapResolved;
        }

        private static bool IsPublicOnly(ConfirmedVisibleEventKind kind)
        {
            return kind == ConfirmedVisibleEventKind.TileDiscarded ||
                   kind == ConfirmedVisibleEventKind.ChiDeclared ||
                   kind == ConfirmedVisibleEventKind.PonDeclared ||
                   kind == ConfirmedVisibleEventKind.KanDeclared ||
                   kind == ConfirmedVisibleEventKind.RiichiDeclared ||
                   kind == ConfirmedVisibleEventKind.TsumoWin ||
                   kind == ConfirmedVisibleEventKind.RonWin ||
                   kind == ConfirmedVisibleEventKind.DoraRevealed ||
                   kind == ConfirmedVisibleEventKind.DrawGame ||
                   kind == ConfirmedVisibleEventKind.ScoreSettled ||
                   kind == ConfirmedVisibleEventKind.RoundEnded ||
                   kind == ConfirmedVisibleEventKind.MatchEnded;
        }

        private static TableAnimationKind MapKind(ConfirmedVisibleEventKind kind)
        {
            switch (kind)
            {
                case ConfirmedVisibleEventKind.TileDealt:
                    return TableAnimationKind.Deal;
                case ConfirmedVisibleEventKind.TileDrawn:
                case ConfirmedVisibleEventKind.RinshanDraw:
                    return TableAnimationKind.Draw;
                case ConfirmedVisibleEventKind.TileDiscarded:
                    return TableAnimationKind.Discard;
                case ConfirmedVisibleEventKind.ChiDeclared:
                case ConfirmedVisibleEventKind.PonDeclared:
                case ConfirmedVisibleEventKind.KanDeclared:
                    return TableAnimationKind.Meld;
                case ConfirmedVisibleEventKind.RiichiDeclared:
                    return TableAnimationKind.Riichi;
                case ConfirmedVisibleEventKind.TsumoWin:
                case ConfirmedVisibleEventKind.RonWin:
                    return TableAnimationKind.Win;
                case ConfirmedVisibleEventKind.DoraRevealed:
                    return TableAnimationKind.Dora;
                case ConfirmedVisibleEventKind.CheatPointsChanged:
                    return TableAnimationKind.CheatPoints;
                case ConfirmedVisibleEventKind.DrawGame:
                case ConfirmedVisibleEventKind.ScoreSettled:
                    return TableAnimationKind.Settlement;
                case ConfirmedVisibleEventKind.RoundEnded:
                    return TableAnimationKind.RoundEnd;
                case ConfirmedVisibleEventKind.MatchEnded:
                    return TableAnimationKind.MatchEnd;
                case ConfirmedVisibleEventKind.SleeveBloomResolved:
                    return TableAnimationKind.SleeveBloomResolved;
                case ConfirmedVisibleEventKind.BeamSwapResolved:
                    return TableAnimationKind.BeamSwapResolved;
                default:
                    return TableAnimationKind.Unknown;
            }
        }
    }
}
