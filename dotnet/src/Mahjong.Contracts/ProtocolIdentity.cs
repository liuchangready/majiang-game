using System;

namespace Mahjong.Contracts
{
    /// <summary>
    /// Identity, scope and version primitives. The distinct struct types are
    /// deliberate: a state version can never be passed where an event
    /// sequence, stream version, projection sequence or epoch is expected.
    /// </summary>
    public static class ProtocolText
    {
        public static string RequireId(string? value, string parameterName, int maxChars = ProtocolConstants.MaxIdChars)
        {
            if (value == null)
            {
                throw new ArgumentNullException(parameterName);
            }

            if (value.Length == 0)
            {
                throw new ArgumentException("Identifier must not be empty.", parameterName);
            }

            if (value.Length > maxChars)
            {
                throw new ArgumentException("Identifier exceeds " + maxChars + " characters.", parameterName);
            }

            if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[value.Length - 1]))
            {
                throw new ArgumentException("Identifier must not have leading or trailing whitespace.", parameterName);
            }

            for (int i = 0; i < value.Length; i++)
            {
                if (char.IsControl(value[i]))
                {
                    throw new ArgumentException("Identifier must not contain control characters.", parameterName);
                }
            }

            return value;
        }
    }

    public readonly struct RequestId : IEquatable<RequestId>
    {
        public RequestId(string value) { Value = ProtocolText.RequireId(value, nameof(value)); }
        public string Value { get; }
        public bool IsEmpty => string.IsNullOrEmpty(Value);
        public bool Equals(RequestId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
        public override bool Equals(object? obj) => obj is RequestId other && Equals(other);
        public override int GetHashCode() => Value == null ? 0 : StringComparer.Ordinal.GetHashCode(Value);
        public override string ToString() => Value ?? string.Empty;
        public static bool operator ==(RequestId left, RequestId right) => left.Equals(right);
        public static bool operator !=(RequestId left, RequestId right) => !left.Equals(right);
    }

    public readonly struct RequestScopeId : IEquatable<RequestScopeId>
    {
        public RequestScopeId(string value) { Value = ProtocolText.RequireId(value, nameof(value)); }
        public string Value { get; }
        public bool IsEmpty => string.IsNullOrEmpty(Value);
        public bool Equals(RequestScopeId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
        public override bool Equals(object? obj) => obj is RequestScopeId other && Equals(other);
        public override int GetHashCode() => Value == null ? 0 : StringComparer.Ordinal.GetHashCode(Value);
        public override string ToString() => Value ?? string.Empty;
        public static bool operator ==(RequestScopeId left, RequestScopeId right) => left.Equals(right);
        public static bool operator !=(RequestScopeId left, RequestScopeId right) => !left.Equals(right);
    }

    public readonly struct RoomId : IEquatable<RoomId>
    {
        public RoomId(string value) { Value = ProtocolText.RequireId(value, nameof(value)); }
        public string Value { get; }
        public bool IsEmpty => string.IsNullOrEmpty(Value);
        public bool Equals(RoomId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
        public override bool Equals(object? obj) => obj is RoomId other && Equals(other);
        public override int GetHashCode() => Value == null ? 0 : StringComparer.Ordinal.GetHashCode(Value);
        public override string ToString() => Value ?? string.Empty;
        public static bool operator ==(RoomId left, RoomId right) => left.Equals(right);
        public static bool operator !=(RoomId left, RoomId right) => !left.Equals(right);
    }

    public readonly struct SessionId : IEquatable<SessionId>
    {
        public SessionId(string value) { Value = ProtocolText.RequireId(value, nameof(value)); }
        public string Value { get; }
        public bool IsEmpty => string.IsNullOrEmpty(Value);
        public bool Equals(SessionId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
        public override bool Equals(object? obj) => obj is SessionId other && Equals(other);
        public override int GetHashCode() => Value == null ? 0 : StringComparer.Ordinal.GetHashCode(Value);
        public override string ToString() => Value ?? string.Empty;
        public static bool operator ==(SessionId left, SessionId right) => left.Equals(right);
        public static bool operator !=(SessionId left, SessionId right) => !left.Equals(right);
    }

    public readonly struct RoundId : IEquatable<RoundId>
    {
        public RoundId(string value) { Value = ProtocolText.RequireId(value, nameof(value)); }
        public string Value { get; }
        public bool IsEmpty => string.IsNullOrEmpty(Value);
        public bool Equals(RoundId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
        public override bool Equals(object? obj) => obj is RoundId other && Equals(other);
        public override int GetHashCode() => Value == null ? 0 : StringComparer.Ordinal.GetHashCode(Value);
        public override string ToString() => Value ?? string.Empty;
        public static bool operator ==(RoundId left, RoundId right) => left.Equals(right);
        public static bool operator !=(RoundId left, RoundId right) => !left.Equals(right);
    }

    public readonly struct DecisionId : IEquatable<DecisionId>
    {
        public DecisionId(string value) { Value = ProtocolText.RequireId(value, nameof(value)); }
        public string Value { get; }
        public bool IsEmpty => string.IsNullOrEmpty(Value);
        public bool Equals(DecisionId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
        public override bool Equals(object? obj) => obj is DecisionId other && Equals(other);
        public override int GetHashCode() => Value == null ? 0 : StringComparer.Ordinal.GetHashCode(Value);
        public override string ToString() => Value ?? string.Empty;
        public static bool operator ==(DecisionId left, DecisionId right) => left.Equals(right);
        public static bool operator !=(DecisionId left, DecisionId right) => !left.Equals(right);
    }

    public readonly struct CorrelationId : IEquatable<CorrelationId>
    {
        public CorrelationId(string value) { Value = ProtocolText.RequireId(value, nameof(value)); }
        public string Value { get; }
        public bool IsEmpty => string.IsNullOrEmpty(Value);
        public bool Equals(CorrelationId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
        public override bool Equals(object? obj) => obj is CorrelationId other && Equals(other);
        public override int GetHashCode() => Value == null ? 0 : StringComparer.Ordinal.GetHashCode(Value);
        public override string ToString() => Value ?? string.Empty;
        public static bool operator ==(CorrelationId left, CorrelationId right) => left.Equals(right);
        public static bool operator !=(CorrelationId left, CorrelationId right) => !left.Equals(right);
    }

    /// <summary>Authoritative table version. One accepted state-changing command is exactly +1.</summary>
    public readonly struct StateVersion : IEquatable<StateVersion>, IComparable<StateVersion>
    {
        public StateVersion(ulong value) { Value = value; }
        public ulong Value { get; }
        public StateVersion Next() => new StateVersion(checked(Value + 1));
        public bool Equals(StateVersion other) => Value == other.Value;
        public override bool Equals(object? obj) => obj is StateVersion other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public int CompareTo(StateVersion other) => Value.CompareTo(other.Value);
        public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public static bool operator ==(StateVersion left, StateVersion right) => left.Equals(right);
        public static bool operator !=(StateVersion left, StateVersion right) => !left.Equals(right);
    }

    /// <summary>Room event-log sequence. It is not a state version and not a per-stream cursor.</summary>
    public readonly struct EventSeq : IEquatable<EventSeq>, IComparable<EventSeq>
    {
        public EventSeq(ulong value) { Value = value; }
        public ulong Value { get; }
        public EventSeq Next() => new EventSeq(checked(Value + 1));
        public bool Equals(EventSeq other) => Value == other.Value;
        public override bool Equals(object? obj) => obj is EventSeq other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public int CompareTo(EventSeq other) => Value.CompareTo(other.Value);
        public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public static bool operator ==(EventSeq left, EventSeq right) => left.Equals(right);
        public static bool operator !=(EventSeq left, EventSeq right) => !left.Equals(right);
    }

    /// <summary>
    /// A stream's projection watermark: the table state version observed when
    /// that stream last emitted. It may lag other seats' private commands and
    /// may repeat on a same-state refresh. It is not the table's current version.
    /// </summary>
    public readonly struct StreamVersion : IEquatable<StreamVersion>, IComparable<StreamVersion>
    {
        public StreamVersion(ulong value) { Value = value; }
        public ulong Value { get; }
        public bool Equals(StreamVersion other) => Value == other.Value;
        public override bool Equals(object? obj) => obj is StreamVersion other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public int CompareTo(StreamVersion other) => Value.CompareTo(other.Value);
        public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public static bool operator ==(StreamVersion left, StreamVersion right) => left.Equals(right);
        public static bool operator !=(StreamVersion left, StreamVersion right) => !left.Equals(right);
    }

    /// <summary>Per-stream emission counter, incremented for every baseline or push.</summary>
    public readonly struct ProjectionSeq : IEquatable<ProjectionSeq>, IComparable<ProjectionSeq>
    {
        public ProjectionSeq(ulong value) { Value = value; }
        public ulong Value { get; }
        public ProjectionSeq Next() => new ProjectionSeq(checked(Value + 1));
        public bool Equals(ProjectionSeq other) => Value == other.Value;
        public override bool Equals(object? obj) => obj is ProjectionSeq other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public int CompareTo(ProjectionSeq other) => Value.CompareTo(other.Value);
        public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public static bool operator ==(ProjectionSeq left, ProjectionSeq right) => left.Equals(right);
        public static bool operator !=(ProjectionSeq left, ProjectionSeq right) => !left.Equals(right);
    }

    /// <summary>Table lease generation for the single-writer/DB fence.</summary>
    public readonly struct OwnerEpoch : IEquatable<OwnerEpoch>, IComparable<OwnerEpoch>
    {
        public OwnerEpoch(ulong value) { Value = value; }
        public ulong Value { get; }
        public OwnerEpoch Next() => new OwnerEpoch(checked(Value + 1));
        public bool Equals(OwnerEpoch other) => Value == other.Value;
        public override bool Equals(object? obj) => obj is OwnerEpoch other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public int CompareTo(OwnerEpoch other) => Value.CompareTo(other.Value);
        public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public static bool operator ==(OwnerEpoch left, OwnerEpoch right) => left.Equals(right);
        public static bool operator !=(OwnerEpoch left, OwnerEpoch right) => !left.Equals(right);
    }

    /// <summary>Client-local binding generation; never a server authorization token.</summary>
    public readonly struct BindingEpoch : IEquatable<BindingEpoch>, IComparable<BindingEpoch>
    {
        public BindingEpoch(ulong value) { Value = value; }
        public ulong Value { get; }
        public BindingEpoch Next() => new BindingEpoch(checked(Value + 1));
        public bool Equals(BindingEpoch other) => Value == other.Value;
        public override bool Equals(object? obj) => obj is BindingEpoch other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public int CompareTo(BindingEpoch other) => Value.CompareTo(other.Value);
        public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public static bool operator ==(BindingEpoch left, BindingEpoch right) => left.Equals(right);
        public static bool operator !=(BindingEpoch left, BindingEpoch right) => !left.Equals(right);
    }

    /// <summary>Seat controller generation. The only epoch used to authorize seat actions.</summary>
    public readonly struct ControlEpoch : IEquatable<ControlEpoch>, IComparable<ControlEpoch>
    {
        public ControlEpoch(ulong value) { Value = value; }
        public ulong Value { get; }
        public ControlEpoch Next() => new ControlEpoch(checked(Value + 1));
        public bool Equals(ControlEpoch other) => Value == other.Value;
        public override bool Equals(object? obj) => obj is ControlEpoch other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public int CompareTo(ControlEpoch other) => Value.CompareTo(other.Value);
        public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public static bool operator ==(ControlEpoch left, ControlEpoch right) => left.Equals(right);
        public static bool operator !=(ControlEpoch left, ControlEpoch right) => !left.Equals(right);
    }

    /// <summary>Transport connection generation. It proves only this connection.</summary>
    public readonly struct ConnectionEpoch : IEquatable<ConnectionEpoch>, IComparable<ConnectionEpoch>
    {
        public ConnectionEpoch(ulong value) { Value = value; }
        public ulong Value { get; }
        public ConnectionEpoch Next() => new ConnectionEpoch(checked(Value + 1));
        public bool Equals(ConnectionEpoch other) => Value == other.Value;
        public override bool Equals(object? obj) => obj is ConnectionEpoch other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public int CompareTo(ConnectionEpoch other) => Value.CompareTo(other.Value);
        public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public static bool operator ==(ConnectionEpoch left, ConnectionEpoch right) => left.Equals(right);
        public static bool operator !=(ConnectionEpoch left, ConnectionEpoch right) => !left.Equals(right);
    }

    /// <summary>Domain session version kept for diagnostics only; never used for stream sync.</summary>
    public readonly struct SessionVersion : IEquatable<SessionVersion>
    {
        public SessionVersion(ulong value) { Value = value; }
        public ulong Value { get; }
        public bool Equals(SessionVersion other) => Value == other.Value;
        public override bool Equals(object? obj) => obj is SessionVersion other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public static bool operator ==(SessionVersion left, SessionVersion right) => left.Equals(right);
        public static bool operator !=(SessionVersion left, SessionVersion right) => !left.Equals(right);
    }

    /// <summary>Domain round version kept for diagnostics only; never used for stream sync.</summary>
    public readonly struct RoundVersion : IEquatable<RoundVersion>
    {
        public RoundVersion(ulong value) { Value = value; }
        public ulong Value { get; }
        public bool Equals(RoundVersion other) => Value == other.Value;
        public override bool Equals(object? obj) => obj is RoundVersion other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public static bool operator ==(RoundVersion left, RoundVersion right) => left.Equals(right);
        public static bool operator !=(RoundVersion left, RoundVersion right) => !left.Equals(right);
    }

    /// <summary>
    /// A stream position pair. `StreamVersion` is the table state version at the
    /// last emission; `ProjectionSeq` is the per-stream emission counter.
    /// </summary>
    public readonly struct StreamCursor : IEquatable<StreamCursor>
    {
        public StreamCursor(StreamVersion streamVersion, ProjectionSeq projectionSeq)
        {
            StreamVersion = streamVersion;
            ProjectionSeq = projectionSeq;
        }

        public StreamVersion StreamVersion { get; }

        public ProjectionSeq ProjectionSeq { get; }

        public static StreamCursor Empty => new StreamCursor(new StreamVersion(0), new ProjectionSeq(0));

        public bool Equals(StreamCursor other)
        {
            return StreamVersion == other.StreamVersion && ProjectionSeq == other.ProjectionSeq;
        }

        public override bool Equals(object? obj) => obj is StreamCursor other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return (StreamVersion.GetHashCode() * 397) ^ ProjectionSeq.GetHashCode();
            }
        }

        public override string ToString()
        {
            return StreamVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" +
                   ProjectionSeq.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        public static bool operator ==(StreamCursor left, StreamCursor right) => left.Equals(right);
        public static bool operator !=(StreamCursor left, StreamCursor right) => !left.Equals(right);

        /// <summary>
        /// Classifies an incoming projection against the client's current cursor.
        /// A baseline resets; otherwise both `from` equality and
        /// `projectionSeq + 1` are required. A jump in streamVersion alone is not
        /// a gap because other seats' private commands may advance the table.
        /// </summary>
        public static CursorRelation Classify(
            StreamCursor current,
            StreamCursor from,
            StreamCursor incoming,
            bool isFullSnapshot)
        {
            if (isFullSnapshot)
            {
                return CursorRelation.Baseline;
            }

            if (incoming.ProjectionSeq.Value <= current.ProjectionSeq.Value)
            {
                return CursorRelation.Stale;
            }

            if (from != current)
            {
                return CursorRelation.Gap;
            }

            if (incoming.ProjectionSeq.Value != current.ProjectionSeq.Value + 1)
            {
                return CursorRelation.Gap;
            }

            if (incoming.StreamVersion.Value < current.StreamVersion.Value)
            {
                return CursorRelation.Stale;
            }

            return incoming.StreamVersion.Value == current.StreamVersion.Value
                ? CursorRelation.Refresh
                : CursorRelation.Advance;
        }
    }

    public enum CursorRelation
    {
        Unspecified = 0,
        Baseline = 1,
        Stale = 2,
        Gap = 3,
        Refresh = 4,
        Advance = 5
    }

    public enum ProtocolRole
    {
        Unspecified = 0,
        Player = 1,
        Spectator = 2,
        Lobby = 3
    }

    /// <summary>
    /// A server-issued capability binding account + role + room + lifetime.
    /// The scope id is not an epoch and not a request id.
    /// </summary>
    public sealed class RequestScope
    {
        public RequestScope(
            RequestScopeId scopeId,
            string accountId,
            ProtocolRole role,
            RoomId roomId,
            ulong issuedAtMs,
            ulong expiresAtMs)
        {
            if (scopeId.IsEmpty)
            {
                throw new ArgumentException("Scope id must not be empty.", nameof(scopeId));
            }

            if (roomId.IsEmpty)
            {
                throw new ArgumentException("Scope room id must not be empty.", nameof(roomId));
            }

            if (role == ProtocolRole.Unspecified)
            {
                throw new ArgumentException("Scope role must be explicit.", nameof(role));
            }

            if (expiresAtMs <= issuedAtMs)
            {
                throw new ArgumentException("Scope expiry must be after issue time.", nameof(expiresAtMs));
            }

            ScopeId = scopeId;
            AccountId = ProtocolText.RequireId(accountId, nameof(accountId));
            Role = role;
            RoomId = roomId;
            IssuedAtMs = issuedAtMs;
            ExpiresAtMs = expiresAtMs;
        }

        public RequestScopeId ScopeId { get; }

        public string AccountId { get; }

        public ProtocolRole Role { get; }

        public RoomId RoomId { get; }

        public ulong IssuedAtMs { get; }

        public ulong ExpiresAtMs { get; }

        public bool IsExpiredAt(ulong nowMs)
        {
            return nowMs >= ExpiresAtMs;
        }

        public override string ToString()
        {
            return "scope=" + ScopeId + ";role=" + Role + ";room=" + RoomId;
        }
    }

    public readonly struct StreamBaseline
    {
        public StreamBaseline(StateVersion stateVersion, EventSeq eventSeq, StreamCursor cursor)
        {
            StateVersion = stateVersion;
            EventSeq = eventSeq;
            Cursor = cursor;
        }

        public StateVersion StateVersion { get; }

        public EventSeq EventSeq { get; }

        public StreamCursor Cursor { get; }
    }

    /// <summary>Idempotency key: (accountId, requestScopeId, requestId).</summary>
    public readonly struct IdempotencyKey : IEquatable<IdempotencyKey>
    {
        public IdempotencyKey(string accountId, RequestScopeId scopeId, RequestId requestId)
        {
            if (scopeId.IsEmpty)
            {
                throw new ArgumentException("Idempotency scope id must not be empty.", nameof(scopeId));
            }

            if (requestId.IsEmpty)
            {
                throw new ArgumentException("Idempotency request id must not be empty.", nameof(requestId));
            }

            AccountId = ProtocolText.RequireId(accountId, nameof(accountId));
            ScopeId = scopeId;
            RequestId = requestId;
        }

        public string AccountId { get; }

        public RequestScopeId ScopeId { get; }

        public RequestId RequestId { get; }

        public bool Equals(IdempotencyKey other)
        {
            return string.Equals(AccountId, other.AccountId, StringComparison.Ordinal) &&
                   ScopeId == other.ScopeId &&
                   RequestId == other.RequestId;
        }

        public override bool Equals(object? obj) => obj is IdempotencyKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = StringComparer.Ordinal.GetHashCode(AccountId);
                hash = (hash * 397) ^ ScopeId.GetHashCode();
                hash = (hash * 397) ^ RequestId.GetHashCode();
                return hash;
            }
        }

        public override string ToString()
        {
            return AccountId + "/" + ScopeId + "/" + RequestId;
        }
    }
}
