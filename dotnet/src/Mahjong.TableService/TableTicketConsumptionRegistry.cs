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
        /// superseded live session, if any. Displacement happens here, never earlier.</summary>
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

        /// <summary>Live session id whose connection may be closed with
        /// DISPLACED_BY_NEW_CONNECTION AFTER this admission's WELCOME succeeded on the wire
        /// (evict-on-welcome through <see cref="ITicketConsumptionRegistry.EvictOnWelcome"/>) — never earlier.</summary>
        public string? DisplacedSessionId { get; }
    }

    /// <summary>The eviction verdict <see cref="ITicketConsumptionRegistry.EvictOnWelcome"/>
    /// hands back once a WELCOME reached the wire (round 5 D-1-03).</summary>
    public enum TableEvictionKind
    {
        /// <summary>No previous live owner exists to displace; this welcome simply owns the seat.</summary>
        None,

        /// <summary>This welcome is the seat's newest live owner;
        /// <see cref="TableEvictionDecision.SessionId"/> names the previous live connection
        /// to displace (it leaves the chain with its eviction).</summary>
        DisplacedOther,

        /// <summary>A NEWER handover already delivered its own WELCOME: this late welcome
        /// LOST the seat. Its own chain entry is removed by the registry; the caller must
        /// close this connection instead of registering it, so two live owners can never
        /// coexist and a later failure of the winner can never resurrect the loser.</summary>
        SelfSuperseded
    }

    /// <summary>The immutable eviction verdict: which connection moves after a welcome.</summary>
    public readonly struct TableEvictionDecision
    {
        public TableEvictionDecision(TableEvictionKind kind, string? sessionId = null)
        {
            Kind = kind;
            SessionId = sessionId;
        }

        public static TableEvictionDecision None { get; } =
            new TableEvictionDecision(TableEvictionKind.None);

        public TableEvictionKind Kind { get; }

        /// <summary>The displaced session for <see cref="TableEvictionKind.DisplacedOther"/>; null otherwise.</summary>
        public string? SessionId { get; }
    }

    /// <summary>
    /// One-time table-ticket consumption port with a two-phase admission transaction:
    /// <see cref="Consume"/> reserves the one-time jti/assignment records, the seat spectator
    /// capacity and the account-seat lease PROVISIONALLY; <see cref="CommitAdmission"/> is the
    /// only place that finalizes the handover and reserves the seat-owner displacement (after
    /// the room, subscription and stream are actually bound, so a failed newer admission can
    /// never evict an already-working connection); <see cref="Release"/> rolls back the
    /// provisional reservation or frees the ended session's live resources. A committed jti
    /// stays backed until ticket expiry + skew; a failed pre-commit admission frees everything
    /// it touched. The frozen Lobby consumption flow stays the authoritative one-time ledger
    /// (P5.3); the table host enforces identical explicit semantics locally until then.
    ///
    /// D-1-03 (round 3): the seat handover is a COMMIT CHAIN, not a single overwritten owner.
    /// Every committed admission appends its own immutable entry (account, session, sequence,
    /// welcomed flag) to the seat's lease, so a failure chain that supersedes an older failed
    /// admission always rolls back to the last session that actually delivered its WELCOME —
    /// independent of the order in which the failures and newer commits interleaved, and
    /// independent of the expirable jti table (the account/role/room/seat identity lives ON the
    /// seat lease entry itself). The displacement of the previous LIVE connection is decided by
    /// <see cref="EvictOnWelcome"/> AFTER the WELCOME reached the wire, so a welcome that
    /// succeeds out of commit order can never displace a seat its session no longer owns.
    /// </summary>
    public interface ITicketConsumptionRegistry
    {
        TicketConsumptionOutcome Consume(ValidatedTableTicket ticket, string sessionId, DateTimeOffset now);

        /// <summary>Finalizes a previously consumed admission; the ONLY displacement reservation point.</summary>
        TableTicketCommitOutcome CommitAdmission(string sessionId);

        /// <summary>
        /// D-1-03: called AFTER WELCOME was delivered (players only). Returns the eviction
        /// verdict that keeps EXACTLY ONE live owner per seat whatever order welcomes arrive
        /// in: DisplacedOther names the previous live connection to take down (this welcome
        /// is the seat's newest live owner); SelfSuperseded means a newer handover already
        /// delivered its own WELCOME — this late welcome lost the seat, its chain entry is
        /// removed by the registry and the CALLER's connection must be closed instead of
        /// registered. This is the ONLY eviction decision point: commit-time displaced ids
        /// are a reservation hint, a failed or superseded admission can never evict a
        /// working connection.
        /// </summary>
        TableEvictionDecision EvictOnWelcome(string sessionId);

        /// <summary>
        /// Rolls back an admission that never reached WELCOME, or frees the session resources
        /// of an ended live session. Idempotent; unknown session ids are a no-op. A not-yet-
        /// welcomed commit entry is simply removed from the seat's commit chain: the handover
        /// reverts to the last WELCOME-successful owner regardless of which newer/older
        /// entries existed around it. The jti/assignment consumption evidence is NOT reverted
        /// (a failed path never re-enables a replay).
        /// </summary>
        void Release(string sessionId);

        /// <summary>
        /// True when (room, seat) is currently bound to the given committed session
        /// (D-1-08): the seat-disconnect fence a teardown consults BEFORE it releases, so a
        /// displaced or handover-lost connection can never disconnect the newer session's
        /// seat. The LIVE seat identity is the newest WELCOME-delivered owner: an entry whose
        /// WELCOME never arrived owns nothing and its own failure path governs it.
        /// </summary>
        bool TryGetSeatOwner(string roomId, int seat, out string? sessionId);

        /// <summary>
        /// D-1-03 WELCOME-failure safety: removes a seat-handover committed entry whose
        /// committing session then failed to deliver its WELCOME, so a failed admission never
        /// evicts or hides a working previous owner. Valid for that session's own not-yet-
        /// welcomed entry regardless of newer/older entries in the chain; a WELCOMED entry
        /// (its WELCOME did arrive) must never be reverted by a post-hoc failure. Idempotent.
        /// </summary>
        void RollbackSeatHandover(string sessionId);
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

        /// <summary>room#seat ???seat lease history (provisional + the whole committed chain).</summary>
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

                    // The assignment's identity still matches. A DIFFERENT jti is the
                    // legitimate reconnect/re-ticket path for the same (account, role, room,
                    // seat): the exact-jti replay check above already rejected reuse of a
                    // consumed ticket, and the seat fence below still rejects a live owner
                    // of a different account. The new jti proceeds to the two-phase
                    // consume/commit handover; it never inherits the old jti's one-time
                    // grant, and the assignment record is redistributed with the new
                    // ticket's expiry below.
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
                    if (_seatBindings.TryGetValue(seatKey, out SeatLease? lease))
                    {
                        // D-1-03: the LIVE seat identity is the newest WELCOME-delivered owner
                        // entry on the lease chain — the same in-memory record this rollback
                        // restores, never an expirable jti/replay table row.
                        SeatLeaseEntry? liveOwner = LiveOwner(lease);
                        if (liveOwner != null &&
                            !string.Equals(liveOwner.AccountId, ticket.AccountId, StringComparison.Ordinal))
                        {
                            return new TicketConsumptionOutcome(
                                TicketConsumptionStatus.SeatOccupiedByOtherAccount,
                                liveOwner.AccountId);
                        }
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
                    lease.Provisional = new SeatLeaseEntry(
                        ticket.AccountId,
                        sessionId,
                        seq,
                        welcomed: false);
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
                    // The assignment binding outlives every one of its consumed tickets'
                    // retention windows. A later, shorter-lived re-ticket must never shorten
                    // the binding: otherwise the old longer-lived ticket's assignment could
                    // be re-bound to a different tuple after the new ticket expires while
                    // the old ticket is still valid. Taking the max continues to satisfy
                    // "an old expiry never deletes a newer binding" in both directions.
                    long assignmentRetainUntilMs =
                        _assignments.TryGetValue(ticket.AssignmentId, out AssignmentRecord? previousAssignment)
                            ? Math.Max(previousAssignment.ExpiresRetainAtMs, retainUntilMs)
                            : retainUntilMs;
                    _assignments[ticket.AssignmentId] = new AssignmentRecord(
                        ticket.AccountId,
                        ticket.Role,
                        ticket.RoomId,
                        ticket.SeatId,
                        assignmentRetainUntilMs);
                }

                _sessions[sessionId] = new SessionRecord(
                    ticket.RoomId,
                    ticket.Role,
                    spectator,
                    ticket.Jti,
                    consumed.AssignmentId,
                    ticket.SeatId,
                    ticket.AccountId);

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
                SeatLeaseEntry? displaced =
                    LiveOwner(lease) is SeatLeaseEntry live &&
                    !string.Equals(live.SessionId, sessionId, StringComparison.Ordinal)
                        ? live
                        : null;

                // The commit CHAIN (D-1-03): the entry is immutable, its account is part of the
                // seat/lease resource itself (never re-derived from an expirable table), and
                // every failure of THIS handover only removes this one entry.
                lease.Commits.Add(new SeatLeaseEntry(
                    provisional.AccountId,
                    sessionId,
                    provisional.Seq,
                    welcomed: false));
                lease.Provisional = null;

                if (_consumedJtis.TryGetValue(record.Jti, out ConsumedTicket? consumed))
                {
                    _consumedJtis[record.Jti] = consumed.WithCommitted();
                }

                return new TableTicketCommitOutcome(TicketCommitStatus.Committed, displaced?.SessionId);
            }
        }

        /// <summary>
        /// D-1-03 (round 5): the eviction decision AFTER WELCOME delivery — exactly ONE live
        /// owner per seat, whatever order welcomes arrive in. A welcome whose newer chain
        /// entry already delivered its own WELCOME has LOST the seat: it is NOT recorded
        /// welcomed (a losing welcome owns nothing, not even the commit-only fallback), its
        /// entry leaves the chain and the verdict is SelfSuperseded, so the caller closes
        /// THIS connection and a later failure of the winner can never resurrect it.
        /// Otherwise the welcome takes the seat: the closest earlier WELCOME-successful
        /// entry is the previous live owner and leaves the chain with its eviction
        /// (DisplacedOther); an entry that committed earlier but never welcomed is skipped —
        /// it is about to be rolled back by its own failure path and owns nothing.
        /// </summary>
        public TableEvictionDecision EvictOnWelcome(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return TableEvictionDecision.None;
            }

            lock (_gate)
            {
                if (!_sessions.TryGetValue(sessionId, out SessionRecord? record) ||
                    record.Spectator ||
                    !record.Seat.HasValue)
                {
                    return TableEvictionDecision.None;
                }

                string seatKey = SeatKey(record.RoomId, record.Seat.Value);
                if (!_seatBindings.TryGetValue(seatKey, out SeatLease? lease))
                {
                    return TableEvictionDecision.None;
                }

                int selfIndex = -1;
                for (int i = 0; i < lease.Commits.Count; i++)
                {
                    if (string.Equals(lease.Commits[i].SessionId, sessionId, StringComparison.Ordinal))
                    {
                        selfIndex = i;
                        break;
                    }
                }

                if (selfIndex < 0)
                {
                    return TableEvictionDecision.None;
                }

                // A NEWER handover already welcomed: this welcome lost the seat. Its entry
                // leaves the chain unrecorded-as-welcomed, so the seat's live-owner identity
                // — LiveOwner's newest-welcomed scan, TryGetSeatOwner's disconnect fence and
                // the commit-only fallback — can never name this closed connection.
                for (int i = selfIndex + 1; i < lease.Commits.Count; i++)
                {
                    if (lease.Commits[i].Welcomed)
                    {
                        lease.Commits.RemoveAt(selfIndex);
                        return new TableEvictionDecision(TableEvictionKind.SelfSuperseded);
                    }
                }

                // THIS call is the single "WELCOME reached the wire" witness: the entry is
                // recorded welcomed here (and only here), so the seat's live-owner identity —
                // LiveOwner's newest-welcomed scan, TryGetSeatOwner's disconnect fence and
                // the eviction scan below — is exactly "the newest delivered welcome", never
                // an unwelcomed commit.
                SeatLeaseEntry self = lease.Commits[selfIndex];
                if (!self.Welcomed)
                {
                    lease.Commits[selfIndex] = new SeatLeaseEntry(
                        self.AccountId, self.SessionId, self.Seq, welcomed: true);
                }

                // Displace exactly the previous live owner: the closest earlier WELCOMED
                // entry, which leaves the chain with its eviction. An entry that committed
                // earlier but never welcomed is skipped: it is about to be rolled back by
                // its own failure path and owns nothing.
                for (int i = selfIndex - 1; i >= 0; i--)
                {
                    if (lease.Commits[i].Welcomed)
                    {
                        SeatLeaseEntry displaced = lease.Commits[i];
                        lease.Commits.RemoveAt(i);
                        return new TableEvictionDecision(
                            TableEvictionKind.DisplacedOther,
                            displaced.SessionId);
                    }
                }

                return TableEvictionDecision.None;
            }
        }

        /// <summary>
        /// D-1-03: reverts a committed seat handover whose new connection then failed before
        /// its WELCOME reached the wire by REMOVING this session's own not-yet-welcomed entry
        /// from the seat's commit chain. The prior owner is whoever the chain still holds —
        /// no lookup race against a previously released session, no dependency on the
        /// expirable jti table. Atomic inside the registry gate; idempotent.
        /// </summary>
        public void RollbackSeatHandover(string sessionId)
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

                if (record.Spectator || !record.Seat.HasValue)
                {
                    return;
                }

                string seatKey = SeatKey(record.RoomId, record.Seat.Value);
                if (!_seatBindings.TryGetValue(seatKey, out SeatLease? lease))
                {
                    return;
                }

                for (int i = lease.Commits.Count - 1; i >= 0; i--)
                {
                    SeatLeaseEntry entry = lease.Commits[i];
                    if (string.Equals(entry.SessionId, sessionId, StringComparison.Ordinal))
                    {
                        if (!entry.Welcomed)
                        {
                            // The WELCOME never made it: this handover is undone. Every other
                            // entry stays exactly where the interleaved commits put it, so a
                            // B/or-C failure chain always reverts to the last WELCOME-success
                            // owner regardless of removal order.
                            lease.Commits.RemoveAt(i);
                        }

                        // A WELCOMED entry must never be reverted by a post-hoc failure call.
                        return;
                    }
                }
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
                        // D-1-03: remove THIS session's own commit-chain entry (the newest
                        // unwelcomed one is the common failed-admission case; a welcomed entry
                        // belongs to a session that ended while it owned the seat). The chain
                        // keeps every other entry, so a same-account reconnect, a cross-account
                        // refusal and a late-displaced teardown all keep their exact semantics.
                        for (int i = lease.Commits.Count - 1; i >= 0; i--)
                        {
                            if (string.Equals(lease.Commits[i].SessionId, sessionId, StringComparison.Ordinal))
                            {
                                lease.Commits.RemoveAt(i);
                                break;
                            }
                        }

                        if (lease.Provisional != null &&
                            string.Equals(lease.Provisional.SessionId, sessionId, StringComparison.Ordinal))
                        {
                            // Failed before commit: the provisional reservation goes away.
                            lease.Provisional = null;
                        }

                        if (lease.Provisional == null && lease.Commits.Count == 0)
                        {
                            // Nothing left on this seat: the binding is free again (a
                            // same-account reconnect takes a fresh consumption).
                            _seatBindings.Remove(seatKey);
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
                    LiveOwner(lease) is SeatLeaseEntry live)
                {
                    sessionId = live.SessionId;
                    return true;
                }

                sessionId = null;
                return false;
            }
        }

        /// <summary>The seat's LIVE owner: the newest WELCOME-delivered entry on the chain.
        /// An entry whose WELCOME never arrived (still in flight or already failed) owns
        /// nothing once a delivered welcome exists anywhere on the chain — its own rollback
        /// removes it. A chain that has never delivered a WELCOME (the plain commit-only
        /// lifecycle: commit is still the admission contract there) falls back to its newest
        /// commit, so a seat is never ownerless merely for skipping the eviction scan.</summary>
        private static SeatLeaseEntry? LiveOwner(SeatLease lease)
        {
            SeatLeaseEntry? newest = null;
            for (int i = lease.Commits.Count - 1; i >= 0; i--)
            {
                if (lease.Commits[i].Welcomed)
                {
                    return lease.Commits[i];
                }

                newest ??= lease.Commits[i];
            }

            return newest;
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

        /// <summary>
        /// One immutable committed (or provisional) seat handover entry. The account identity
        /// lives HERE — on the seat/lease resource itself — so a rollback or the live-owner
        /// check never depends on the expirable jti replay evidence (D-1-03).
        /// </summary>
        private sealed class SeatLeaseEntry
        {
            public SeatLeaseEntry(string accountId, string sessionId, long seq, bool welcomed)
            {
                AccountId = accountId;
                SessionId = sessionId;
                Seq = seq;
                Welcomed = welcomed;
            }

            public string AccountId { get; }

            public string SessionId { get; }

            public long Seq { get; }

            public bool Welcomed { get; }
        }

        /// <summary>The per-seat lease: the provisional reservation plus the whole committed
        /// handover chain (ordered by sequence number; commit order is the handover order).</summary>
        private sealed class SeatLease
        {
            public SeatLeaseEntry? Provisional;

            public List<SeatLeaseEntry> Commits = new List<SeatLeaseEntry>();
        }

        private sealed class SessionRecord
        {
            public SessionRecord(
                string roomId,
                TableAdmissionRole role,
                bool spectator,
                string jti,
                string? assignmentId,
                int? seat,
                string accountId)
            {
                RoomId = roomId;
                Role = role;
                Spectator = spectator;
                Jti = jti;
                AssignmentId = assignmentId;
                Seat = seat;
                AccountId = accountId;
            }

            public string RoomId { get; }

            public TableAdmissionRole Role { get; }

            public bool Spectator { get; }

            public string Jti { get; }

            public string? AssignmentId { get; }

            public int? Seat { get; }

            /// <summary>The session's own account: part of the session resource itself (D-1-03).</summary>
            public string AccountId { get; }
        }
    }
}
