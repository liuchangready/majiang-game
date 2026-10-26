using System;
using System.Collections.Generic;
using System.Globalization;

namespace Mahjong.TableService
{
    public enum TicketConsumptionStatus : byte
    {
        /// <summary>Provisionally admitted. <see cref="TableTicketCommitOutcome.DisplacedSessionId"/> carries a
        /// superseded live session only after <see cref="ITicketConsumptionRegistry.CommitAdmission"/>.</summary>
        Accepted = 0,

        /// <summary>This exact ticket (jti) was already consumed. A signed JWT alone is never
        /// replay protection; the second admission gets zero side effects.</summary>
        DuplicateJti = 1,

        /// <summary>The requested seat is bound to a different live account. Cross-account seat
        /// takeover is refused without touching the existing binding.</summary>
        SeatOccupiedByOtherAccount = 2,

        /// <summary>The room already holds the configured spectator maximum.</summary>
        SpectatorCapacityReached = 3,

        /// <summary>The ticket's assignment id was already bound to a different account, role,
        /// room or seat: one assignment = exactly one (account, role, room, seat).</summary>
        AssignmentMismatch = 4,

        /// <summary>The bounded retained-ticket store is full after expiry purging; admission
        /// fails closed rather than growing the store without a limit.</summary>
        ConsumptionStoreExhausted = 5
    }

    public sealed class TicketConsumptionOutcome
    {
        public static readonly TicketConsumptionOutcome DuplicateJti =
            new TicketConsumptionOutcome(TicketConsumptionStatus.DuplicateJti, null);

        public static readonly TicketConsumptionOutcome SeatOccupiedByOtherAccount =
            new TicketConsumptionOutcome(TicketConsumptionStatus.SeatOccupiedByOtherAccount, null);

        public static readonly TicketConsumptionOutcome SpectatorCapacityReached =
            new TicketConsumptionOutcome(TicketConsumptionStatus.SpectatorCapacityReached, null);

        public static readonly TicketConsumptionOutcome AssignmentMismatch =
            new TicketConsumptionOutcome(TicketConsumptionStatus.AssignmentMismatch, null);

        public static readonly TicketConsumptionOutcome ConsumptionStoreExhausted =
            new TicketConsumptionOutcome(TicketConsumptionStatus.ConsumptionStoreExhausted, null);

        public TicketConsumptionOutcome(TicketConsumptionStatus status, string? existingAccountId)
        {
            Status = status;
            ExistingAccountId = existingAccountId;
        }

        public TicketConsumptionStatus Status { get; }

        public string? ExistingAccountId { get; }

        public bool IsAccepted => Status == TicketConsumptionStatus.Accepted;
    }

    /// <summary>Result of the final admission commit (the seat-handover linearization point).</summary>
    public enum TicketCommitStatus : byte
    {
        /// <summary>The admission is final; <see cref="TableTicketCommitOutcome.DisplacedSessionId"/> carries the
        /// superseded live session (??????), if any. Displacement happens here, never earlier.</summary>
        Committed = 0,

        /// <summary>A newer provisional admission already superseded this one; the connection
        /// must be refused before any WELCOME (the newest valid consumption wins).</summary>
        Superseded = 1,

        /// <summary>Unknown session (already released or never provisionally consumed).</summary>
        Unknown = 2
    }

    public sealed class TableTicketCommitOutcome
    {
        public static readonly TableTicketCommitOutcome Unknown =
            new TableTicketCommitOutcome(TicketCommitStatus.Unknown, null);

        public static readonly TableTicketCommitOutcome Superseded =
            new TableTicketCommitOutcome(TicketCommitStatus.Superseded, null);

        public TableTicketCommitOutcome(TicketCommitStatus status, string? displacedSessionId)
        {
            Status = status;
            DisplacedSessionId = displacedSessionId;
        }

        public TicketCommitStatus Status { get; }

        /// <summary>Live session id to close with DISPLACED_BY_NEW_CONNECTION (??????), if any.</summary>
        public string? DisplacedSessionId { get; }
    }

    /// <summary>
    /// One-time table-ticket consumption port with a two-phase admission transaction:
    /// <see cref="Consume"/> reserves the one-time jti/assignment records, the seat spectator
    /// capacity and the account-seat lease PROVISIONALLY; <see cref="CommitAdmission"/> is the
    /// only place that finalizes the handover and fires ?????? displacement (after the room,
    /// subscription and stream are actually bound, so a failed newer admission can never
    /// evict an already-working connection); <see cref="Release"/> rolls back the provisional
    /// reservation or frees the ended session's live resources. A committed jti stays backed
    /// until ticket expiry + skew; a failed pre-commit admission frees everything it touched.
    /// The frozen Lobby consumption flow stays the authoritative one-time ledger (P5.3);
    /// the table host enforces identical explicit semantics locally until then.
    /// </summary>
    public interface ITicketConsumptionRegistry
    {
        TicketConsumptionOutcome Consume(ValidatedTableTicket ticket, string sessionId, DateTimeOffset now);

        /// <summary>Finalizes a previously consumed admission; the ONLY displacement point.</summary>
        TableTicketCommitOutcome CommitAdmission(string sessionId);

        /// <summary>
        /// Rolls back an admission that never reached WELCOME, or frees the session resources
        /// of an ended live session. Idempotent; unknown session ids are a no-op.
        /// </summary>
        void Release(string sessionId);

        /// <summary>
        /// True when (room, seat) is currently bound to the given committed session
        /// (D-1-08): the seat-disconnect fence a teardown consults BEFORE it releases, so a
        /// displaced or handover-lost connection can never disconnect the newer session's
        /// seat.
        /// </summary>
        bool TryGetSeatOwner(string roomId, int seat, out string? sessionId);

        /// <summary>
        /// D-1-03 WELCOME-failure safety: reverts a seat handover whose committing session
        /// then failed to deliver its WELCOME, so a failed admission never evicts a working
        /// previous owner. Only valid when <paramref name="sessionId"/> is the seat's CURRENT
        /// committed owner and <paramref name="previousSessionId"/> passed the same account
        /// gate; otherwise nothing changes. The jti/assignment consumption evidence is NOT
        /// reverted (a failed path never re-enables a replay).
        /// </summary>
        void RollbackSeatHandover(string sessionId, string previousSessionId);
    }

    /// <summary>In-memory P5.1 implementation. Cross-instance uniqueness arrives with P5.2.</summary>
    public sealed class InMemoryTicketConsumptionRegistry : ITicketConsumptionRegistry
    {
        private readonly object _gate = new object();

        /// <summary>jti ???consumed record. Committed records stay until expiry + skew; a
        /// pre-commit failure removes its own record (the jti was never backed by the scope).</summary>
        private readonly Dictionary<string, ConsumedTicket> _consumedJtis =
            new Dictionary<string, ConsumedTicket>(StringComparer.Ordinal);

        /// <summary>assignment_id ???the one (account, role, room, seat) it belongs to.</summary>
        private readonly Dictionary<string, AssignmentRecord> _assignments =
            new Dictionary<string, AssignmentRecord>(StringComparer.Ordinal);

        /// <summary>room#seat ???seat lease history (current provisional + committed winner).</summary>
        private readonly Dictionary<string, SeatLease> _seatBindings =
            new Dictionary<string, SeatLease>(StringComparer.Ordinal);

        private readonly Dictionary<string, int> _spectatorCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>sessionId ???what this admission reserved/freed.</summary>
        private readonly Dictionary<string, SessionRecord> _sessions =
            new Dictionary<string, SessionRecord>(StringComparer.Ordinal);

        private long _nextSeatSeq;
        private readonly TableServiceOptions _options;

        public InMemoryTicketConsumptionRegistry(TableServiceOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        /// <summary>Committed (ended) plus currently-live jti records; test-visible sizing aid.</summary>
        internal int ConsumedJtiCount
        {
            get
            {
                lock (_gate)
                {
                    return _consumedJtis.Count;
                }
            }
        }

        public TicketConsumptionOutcome Consume(ValidatedTableTicket ticket, string sessionId, DateTimeOffset now)
        {
            if (ticket == null)
            {
                throw new ArgumentNullException(nameof(ticket));
            }

            if (string.IsNullOrWhiteSpace(sessionId))
            {
                throw new ArgumentException("A consumption record requires a session id.", nameof(sessionId));
            }

            long nowMs = now.ToUnixTimeMilliseconds();

            lock (_gate)
            {
                PurgeExpired(nowMs);

                if (_consumedJtis.ContainsKey(ticket.Jti))
                {
                    // Still retained: the ticket's expiry + skew has not passed, so this is a
                    // real replay within its validity window.
                    return TicketConsumptionOutcome.DuplicateJti;
                }

                if (ticket.AssignmentId != null &&
                    _assignments.TryGetValue(ticket.AssignmentId, out AssignmentRecord? bound))
                {
                    bool mismatch =
                        !string.Equals(bound.AccountId, ticket.AccountId, StringComparison.Ordinal) ||
                        bound.Role != ticket.Role ||
                        !string.Equals(bound.RoomId, ticket.RoomId, StringComparison.Ordinal) ||
                        bound.SeatId != ticket.SeatId;
                    if (mismatch)
                    {
                        return new TicketConsumptionOutcome(
                            TicketConsumptionStatus.AssignmentMismatch,
                            bound.AccountId);
                    }

                    // A different jti cannot share an assignment: one assignment belongs to
                    // exactly one (account, role, room, seat) for its whole validity.
                    return TicketConsumptionOutcome.DuplicateJti;
                }

                string? seatKey = null;
                if (ticket.Role == TableAdmissionRole.Spectator)
                {
                    _spectatorCounts.TryGetValue(ticket.RoomId, out int spectators);
                    if (spectators >= _options.MaxSpectatorsPerRoom)
                    {
                        return TicketConsumptionOutcome.SpectatorCapacityReached;
                    }
                }
                else
                {
                    seatKey = SeatKey(ticket.RoomId, ticket.SeatId!.Value);
                    if (_seatBindings.TryGetValue(seatKey, out SeatLease? lease) &&
                        lease.Committed != null &&
                        !string.Equals(lease.Committed.AccountId, ticket.AccountId, StringComparison.Ordinal))
                    {
                        return new TicketConsumptionOutcome(
                            TicketConsumptionStatus.SeatOccupiedByOtherAccount,
                            lease.Committed.AccountId);
                    }
                }

                if (_consumedJtis.Count >= _options.MaxRetainedConsumedTickets)
                {
                    // Fail closed: every live-relevant entry is still retained; no evictions.
                    return TicketConsumptionOutcome.ConsumptionStoreExhausted;
                }

                long retainUntilMs = ticket.ExpiresAt.ToUnixTimeMilliseconds() +
                    (long)TimeSpan.FromSeconds(_options.ClockSkewSeconds).TotalMilliseconds;

                long seq = 0L;
                if (seatKey != null)
                {
                    seq = ++_nextSeatSeq;
                    SeatLease lease = _seatBindings.TryGetValue(seatKey, out SeatLease? existing) ? existing : new SeatLease();
                    lease.Provisional = new SeatLeaseEntry(ticket.AccountId, sessionId, seq);
                    _seatBindings[seatKey] = lease;
                }

                bool spectator = ticket.Role == TableAdmissionRole.Spectator;
                if (spectator)
                {
                    _spectatorCounts[ticket.RoomId] = _spectatorCounts.TryGetValue(ticket.RoomId, out int count)
                        ? count + 1
                        : 1;
                }

                var consumed = new ConsumedTicket(
                    ticket.AssignmentId,
                    ticket.AccountId,
                    ticket.Role,
                    ticket.RoomId,
                    ticket.SeatId,
                    ticket.Jti,
                    sessionId,
                    seq,
                    retainUntilMs,
                    committed: false);
                _consumedJtis[ticket.Jti] = consumed;

                if (ticket.AssignmentId != null)
                {
                    _assignments[ticket.AssignmentId] = new AssignmentRecord(
                        ticket.AccountId,
                        ticket.Role,
                        ticket.RoomId,
                        ticket.SeatId,
                        retainUntilMs);
                }

                _sessions[sessionId] = new SessionRecord(
                    ticket.RoomId,
                    ticket.Role,
                    spectator,
                    ticket.Jti,
                    consumed.AssignmentId,
                    ticket.SeatId);

                return new TicketConsumptionOutcome(TicketConsumptionStatus.Accepted, null);
            }
        }

        public TableTicketCommitOutcome CommitAdmission(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return TableTicketCommitOutcome.Unknown;
            }

            lock (_gate)
            {
                if (!_sessions.TryGetValue(sessionId, out SessionRecord? record))
                {
                    return TableTicketCommitOutcome.Unknown;
                }

                if (record.Spectator)
                {
                    // D-1-03: a spectator commit is a REAL committed admission: its jti stays
                    // backed until expiry + skew too (WithCommitted), a later replay of the
                    // same ticket can never re-enter, and a later Release frees the
                    // spectator count without erasing the consumption evidence.
                    if (_consumedJtis.TryGetValue(record.Jti, out ConsumedTicket? spectatorConsumed))
                    {
                        _consumedJtis[record.Jti] = spectatorConsumed.WithCommitted();
                    }

                    return new TableTicketCommitOutcome(TicketCommitStatus.Committed, null);
                }

                string seatKey = SeatKey(record.RoomId, record.Seat!.Value);
                if (!_seatBindings.TryGetValue(seatKey, out SeatLease? lease) || lease.Provisional == null)
                {
                    return TableTicketCommitOutcome.Superseded;
                }

                if (!string.Equals(lease.Provisional.SessionId, sessionId, StringComparison.Ordinal))
                {
                    // A newer provisional consumed this seat meanwhile: the newer commit is
                    // the only handover that may displace anybody.
                    return TableTicketCommitOutcome.Superseded;
                }

                SeatLeaseEntry provisional = lease.Provisional;
                string? displaced =
                    lease.Committed != null &&
                    !string.Equals(lease.Committed.SessionId, sessionId, StringComparison.Ordinal)
                        ? lease.Committed.SessionId
                        : null;
                lease.Committed = provisional;
                lease.Provisional = null;

                if (_consumedJtis.TryGetValue(record.Jti, out ConsumedTicket? consumed))
                {
                    _consumedJtis[record.Jti] = consumed.WithCommitted();
                }

                return new TableTicketCommitOutcome(TicketCommitStatus.Committed, displaced);
            }
        }

        /// <summary>
        /// D-1-03: reverts a committed seat handover whose new connection then failed before
        /// its WELCOME reached the wire. The handover was never announced (the displaced side
        /// is not notified until after WELCOME succeeds), so restoring the previous owner
        /// keeps the working old connection untouched. Atomic inside the registry gate.
        /// </summary>
        public void RollbackSeatHandover(string sessionId, string previousSessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(previousSessionId))
            {
                return;
            }

            lock (_gate)
            {
                if (!_sessions.TryGetValue(sessionId, out SessionRecord? record))
                {
                    return;
                }

                if (record.Spectator || !record.Seat.HasValue)
                {
                    return;
                }

                string seatKey = SeatKey(record.RoomId, record.Seat.Value);
                if (!_seatBindings.TryGetValue(seatKey, out SeatLease? lease) || lease.Committed == null)
                {
                    return;
                }

                if (!string.Equals(lease.Committed.SessionId, sessionId, StringComparison.Ordinal))
                {
                    // A newer admission already superseded this one; its own handover governs.
                    return;
                }

                SessionRecord? previous = _sessions.TryGetValue(previousSessionId, out SessionRecord? found)
                    ? found
                    : null;
                if (previous == null || previous.Spectator)
                {
                    return;
                }

                string previousAccount =
                    _consumedJtis.TryGetValue(previous.Jti, out ConsumedTicket? previousConsumed)
                        ? previousConsumed.AccountId
                        : string.Empty;
                var restored = new SeatLeaseEntry(previousAccount, previousSessionId, lease.Committed.Seq);
                lease.Committed = restored;
            }
        }

        public void Release(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return;
            }

            lock (_gate)
            {
                if (!_sessions.TryGetValue(sessionId, out SessionRecord? record))
                {
                    return;
                }

                _sessions.Remove(sessionId);

                // D-1-06: the session's LIVE resource ownership is independent of the
                // retained (and expirable) jti/assignment evidence — a purged record must
                // still allow this session's seat binding/spectator count to free cleanly.

                if (record.Spectator)
                {
                    if (_spectatorCounts.TryGetValue(record.RoomId, out int spectators))
                    {
                        if (spectators <= 1)
                        {
                            _spectatorCounts.Remove(record.RoomId);
                        }
                        else
                        {
                            _spectatorCounts[record.RoomId] = spectators - 1;
                        }
                    }
                }
                else if (record.Seat.HasValue)
                {
                    string seatKey = SeatKey(record.RoomId, record.Seat.Value);
                    if (_seatBindings.TryGetValue(seatKey, out SeatLease? lease))
                    {
                        if (lease.Committed != null &&
                            string.Equals(lease.Committed.SessionId, sessionId, StringComparison.Ordinal))
                        {
                            if (lease.Provisional != null)
                            {
                                // A NEWER provisional is already waiting on this seat: its
                                // reservation survives; only this session's ownership ends.
                                lease.Committed = null;
                            }
                            else
                            {
                                // The session ENDED while it owned the seat: the seat is free
                                // again (a same-account reconnect takes a fresh consumption).
                                _seatBindings.Remove(seatKey);
                            }
                        }
                        else if (lease.Provisional != null &&
                                 string.Equals(lease.Provisional.SessionId, sessionId, StringComparison.Ordinal))
                        {
                            // Failed before commit: the provisional reservation goes away,
                            // leaving any newer provisional (a different session) intact.
                            lease.Provisional = null;
                        }
                    }
                }

                // D-1-03: consumption evidence is release-independent — the jti/assignment
                // records stay exactly until their ticket's expiry + skew for EVERY consumed
                // admission (provisional or committed), so a failed release path can never
                // turn a used one-time ticket into a replayable one. Retained records are
                // purged by expiry only.
            }
        }

        public bool TryGetSeatOwner(string roomId, int seat, out string? sessionId)
        {
            lock (_gate)
            {
                if (_seatBindings.TryGetValue(SeatKey(roomId, seat), out SeatLease? lease) &&
                    lease.Committed != null)
                {
                    sessionId = lease.Committed.SessionId;
                    return true;
                }

                sessionId = null;
                return false;
            }
        }

        /// <summary>
        /// Drops every retained jti/assignment whose ticket expiry + skew has passed (D-1-06).
        /// Seat leases and counts live independently: their capacity frees as soon as a
        /// session commits/ends, independent of the retained jti records.
        /// </summary>
        private void PurgeExpired(long nowMs)
        {
            if (_consumedJtis.Count == 0 && _assignments.Count == 0)
            {
                return;
            }

            List<string>? expiredJtis = null;
            foreach (KeyValuePair<string, ConsumedTicket> entry in _consumedJtis)
            {
                if (entry.Value.RetainUntilMs <= nowMs)
                {
                    (expiredJtis ??= new List<string>()).Add(entry.Key);
                }
            }

            if (expiredJtis != null)
            {
                foreach (string jti in expiredJtis)
                {
                    _consumedJtis.Remove(jti);
                }
            }

            List<string>? expiredAssignments = null;
            foreach (KeyValuePair<string, AssignmentRecord> entry in _assignments)
            {
                if (entry.Value.ExpiresRetainAtMs <= nowMs)
                {
                    (expiredAssignments ??= new List<string>()).Add(entry.Key);
                }
            }

            if (expiredAssignments != null)
            {
                foreach (string assignmentId in expiredAssignments)
                {
                    _assignments.Remove(assignmentId);
                }
            }
        }

        private static string SeatKey(string roomId, int seat)
        {
            return roomId + "#" + seat.ToString(CultureInfo.InvariantCulture);
        }

        private sealed class ConsumedTicket
        {
            public ConsumedTicket(
                string? assignmentId,
                string accountId,
                TableAdmissionRole role,
                string roomId,
                int? seatId,
                string jti,
                string sessionId,
                long seatSeq,
                long retainUntilMs,
                bool committed)
            {
                AssignmentId = assignmentId;
                AccountId = accountId;
                Role = role;
                RoomId = roomId;
                SeatId = seatId;
                Jti = jti;
                SessionId = sessionId;
                SeatSeq = seatSeq;
                RetainUntilMs = retainUntilMs;
                Committed = committed;
            }

            public string? AssignmentId { get; }

            public string AccountId { get; }

            public TableAdmissionRole Role { get; }

            public string RoomId { get; }

            public int? SeatId { get; }

            public string Jti { get; }

            public string SessionId { get; }

            public long SeatSeq { get; }

            public long RetainUntilMs { get; }

            public bool Committed { get; }

            public ConsumedTicket WithCommitted()
            {
                return new ConsumedTicket(
                    AssignmentId,
                    AccountId,
                    Role,
                    RoomId,
                    SeatId,
                    Jti,
                    SessionId,
                    SeatSeq,
                    RetainUntilMs,
                    committed: true);
            }
        }

        private sealed class AssignmentRecord
        {
            public AssignmentRecord(
                string accountId,
                TableAdmissionRole role,
                string roomId,
                int? seatId,
                long expiresRetainAtMs)
            {
                AccountId = accountId;
                Role = role;
                RoomId = roomId;
                SeatId = seatId;
                ExpiresRetainAtMs = expiresRetainAtMs;
            }

            public string AccountId { get; }

            public TableAdmissionRole Role { get; }

            public string RoomId { get; }

            public int? SeatId { get; }

            public long ExpiresRetainAtMs { get; }
        }

        private sealed class SeatLease
        {
            public SeatLeaseEntry? Provisional;

            public SeatLeaseEntry? Committed;
        }

        private sealed class SeatLeaseEntry
        {
            public SeatLeaseEntry(string accountId, string sessionId, long seq)
            {
                AccountId = accountId;
                SessionId = sessionId;
                Seq = seq;
            }

            public string AccountId { get; }

            public string SessionId { get; }

            public long Seq { get; }
        }

        private sealed class SessionRecord
        {
            public SessionRecord(
                string roomId,
                TableAdmissionRole role,
                bool spectator,
                string jti,
                string? assignmentId,
                int? seat)
            {
                RoomId = roomId;
                Role = role;
                Spectator = spectator;
                Jti = jti;
                AssignmentId = assignmentId;
                Seat = seat;
            }

            public string RoomId { get; }

            public TableAdmissionRole Role { get; }

            public bool Spectator { get; }

            public string Jti { get; }

            public string? AssignmentId { get; }

            public int? Seat { get; }
        }
    }
}
