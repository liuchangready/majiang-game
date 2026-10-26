using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Mahjong.LobbyService
{
    /// <summary>
    /// Transactional in-memory contract-test double. Every mutating operation
    /// clones committed state and publishes it only on success, so a thrown
    /// exception is a real rollback. This is NOT a durable store and is never
    /// evidence that PostgreSQL persistence works.
    /// </summary>
    public sealed class InMemoryLobbyStore : ILobbyStore
    {
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private State _state = new State();
        private bool _initialized;

        public string ProviderName => "Memory (contract tests only, not durable)";

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            _initialized = true;
            return Task.CompletedTask;
        }

        public Task<bool> CheckReadyAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(_initialized);
        }

        public async Task<LobbySessionInfo?> FindSessionByTokenHashAsync(
            string tokenHash,
            CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_state.Sessions.TryGetValue(tokenHash, out LobbySession? session))
                {
                    return null;
                }

                if (!_state.Accounts.TryGetValue(session.AccountId, out LobbyAccount? account))
                {
                    return null;
                }

                return new LobbySessionInfo(session, account);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<LobbyAccount?> FindAccountAsync(string accountId, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _state.Accounts.TryGetValue(accountId, out LobbyAccount? account);
                return account;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<LobbyRoomPage> ListRoomsAsync(
            string? cursor,
            int limit,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                LobbyCursor parsed = default;
                bool hasCursor = LobbyCursor.TryDecode(cursor, out parsed);
                if (!string.IsNullOrEmpty(cursor) && !hasCursor)
                {
                    throw new LobbyException(400, "INVALID_CURSOR", "The room catalog cursor is invalid.");
                }

                IEnumerable<LobbyRoom> query = _state.Rooms.Values
                    .Where(room => room.Visibility == LobbyRoomVisibility.Public && room.State == LobbyRoomState.Open);

                if (hasCursor)
                {
                    query = query.Where(room =>
                        room.CreatedAt < parsed.CreatedAt ||
                        (room.CreatedAt == parsed.CreatedAt &&
                         string.CompareOrdinal(room.RoomId, parsed.RoomId) > 0));
                }

                List<LobbyRoom> ordered = query
                    .OrderByDescending(room => room.CreatedAt)
                    .ThenBy(room => room.RoomId, StringComparer.Ordinal)
                    .Take(limit + 1)
                    .ToList();

                bool hasMore = ordered.Count > limit;
                List<LobbyRoomSnapshot> items = ordered
                    .Take(limit)
                    .Select(room => Snapshot(room, now))
                    .ToList();

                string? next = hasMore && items.Count > 0
                    ? LobbyCursor.Encode(items[items.Count - 1].Room.CreatedAt, items[items.Count - 1].Room.RoomId)
                    : null;

                return new LobbyRoomPage(items, next);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<LobbyRoomSnapshot?> FindRoomSnapshotAsync(
            string roomId,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_state.Rooms.TryGetValue(roomId, out LobbyRoom? room))
                {
                    return null;
                }

                return Snapshot(room, now);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<LobbyAssignment?> FindAssignmentAsync(
            string assignmentId,
            CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _state.Assignments.TryGetValue(assignmentId, out LobbyAssignment? assignment);
                return assignment;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<LobbyMatchTicket?> FindMatchTicketAsync(
            string matchTicketId,
            CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _state.MatchTickets.TryGetValue(matchTicketId, out LobbyMatchTicket? ticket);
                return ticket;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<LobbyResponse?> TryReplayAsync(
            LobbyIdempotencyKey key,
            string payloadDigest,
            CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var receiptKey = new ReceiptKey(key.Principal, key.RequestId);
                if (!_state.Receipts.TryGetValue(receiptKey, out LobbyReceiptRecord? existing))
                {
                    return null;
                }

                if (!string.Equals(existing.PayloadDigest, payloadDigest, StringComparison.Ordinal))
                {
                    throw new LobbyException(409, "REQUEST_ID_CONFLICT", "The idempotency key was already used with a different payload.");
                }

                if (!string.Equals(existing.Status, "COMPLETED", StringComparison.Ordinal))
                {
                    throw new LobbyException(409, "REQUEST_IN_PROGRESS", "The original request is still in progress.");
                }

                return new LobbyResponse(existing.HttpStatus, existing.ResultBody, Replayed: true);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<LobbyResponse> ExecuteIdempotentAsync(
            LobbyIdempotencyKey key,
            string payloadDigest,
            DateTimeOffset now,
            Func<ILobbyTransaction, CancellationToken, Task<LobbyResponse>> operation,
            CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                State working = _state.Clone();
                var tx = new InMemoryTransaction(working);
                var receiptKey = new ReceiptKey(key.Principal, key.RequestId);
                if (working.Receipts.TryGetValue(receiptKey, out LobbyReceiptRecord? existing))
                {
                    if (!string.Equals(existing.PayloadDigest, payloadDigest, StringComparison.Ordinal))
                    {
                        throw new LobbyException(409, "REQUEST_ID_CONFLICT", "The idempotency key was already used with a different payload.");
                    }

                    if (!string.Equals(existing.Status, "COMPLETED", StringComparison.Ordinal))
                    {
                        throw new LobbyException(409, "REQUEST_IN_PROGRESS", "The original request is still in progress.");
                    }

                    return new LobbyResponse(existing.HttpStatus, existing.ResultBody, Replayed: true);
                }

                working.Receipts.Add(receiptKey, new LobbyReceiptRecord(
                    key.Principal,
                    key.RequestId,
                    payloadDigest,
                    "IN_PROGRESS",
                    0,
                    string.Empty,
                    now,
                    null));

                LobbyResponse response = await operation(tx, cancellationToken).ConfigureAwait(false);
                working.Receipts[receiptKey] = new LobbyReceiptRecord(
                    key.Principal,
                    key.RequestId,
                    payloadDigest,
                    "COMPLETED",
                    response.StatusCode,
                    response.Json,
                    now,
                    now);

                _state = working;
                return response;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<T> ExecuteAsync<T>(
            DateTimeOffset now,
            Func<ILobbyTransaction, CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                State working = _state.Clone();
                var tx = new InMemoryTransaction(working);
                T result = await operation(tx, cancellationToken).ConfigureAwait(false);
                _state = working;
                return result;
            }
            finally
            {
                _gate.Release();
            }
        }

        public ValueTask DisposeAsync()
        {
            _gate.Dispose();
            return ValueTask.CompletedTask;
        }

        private LobbyRoomSnapshot Snapshot(LobbyRoom room, DateTimeOffset now)
        {
            int seats = _state.Assignments.Values.Count(a =>
                string.Equals(a.RoomId, room.RoomId, StringComparison.Ordinal) &&
                a.Role == LobbyRole.Player &&
                IsActive(a.Status) &&
                (a.ExpiresAt == null || a.ExpiresAt > now));
            int spectators = _state.Assignments.Values.Count(a =>
                string.Equals(a.RoomId, room.RoomId, StringComparison.Ordinal) &&
                a.Role == LobbyRole.Spectator &&
                IsActive(a.Status) &&
                (a.ExpiresAt == null || a.ExpiresAt > now));
            return new LobbyRoomSnapshot(room, seats, spectators);
        }

        private static bool IsActive(LobbyAssignmentStatus status)
        {
            return status == LobbyAssignmentStatus.Reserved || status == LobbyAssignmentStatus.Confirmed;
        }

        private readonly record struct ReceiptKey(string Principal, string RequestId);

        private readonly record struct BootstrapKey(string DeviceDigest, string BootstrapHash);

        private sealed class State
        {
            public Dictionary<string, LobbyAccount> Accounts { get; } = new Dictionary<string, LobbyAccount>(StringComparer.Ordinal);

            public Dictionary<BootstrapKey, string> BootstrapAccounts { get; } = new Dictionary<BootstrapKey, string>();

            public Dictionary<string, LobbySession> Sessions { get; } = new Dictionary<string, LobbySession>(StringComparer.Ordinal);

            public Dictionary<string, LobbyRoom> Rooms { get; } = new Dictionary<string, LobbyRoom>(StringComparer.Ordinal);

            public Dictionary<string, LobbyAssignment> Assignments { get; } = new Dictionary<string, LobbyAssignment>(StringComparer.Ordinal);

            public Dictionary<string, LobbyMatchTicket> MatchTickets { get; } = new Dictionary<string, LobbyMatchTicket>(StringComparer.Ordinal);

            public Dictionary<string, LobbyTableTicketRecord> TableTickets { get; } = new Dictionary<string, LobbyTableTicketRecord>(StringComparer.Ordinal);

            public Dictionary<string, LobbyTicketConsumption> Consumptions { get; } = new Dictionary<string, LobbyTicketConsumption>(StringComparer.Ordinal);

            public Dictionary<ReceiptKey, LobbyReceiptRecord> Receipts { get; } = new Dictionary<ReceiptKey, LobbyReceiptRecord>();

            public State Clone()
            {
                var clone = new State();
                foreach (KeyValuePair<string, LobbyAccount> item in Accounts)
                {
                    clone.Accounts.Add(item.Key, item.Value);
                }

                foreach (KeyValuePair<BootstrapKey, string> item in BootstrapAccounts)
                {
                    clone.BootstrapAccounts.Add(item.Key, item.Value);
                }

                foreach (KeyValuePair<string, LobbySession> item in Sessions)
                {
                    clone.Sessions.Add(item.Key, item.Value);
                }

                foreach (KeyValuePair<string, LobbyRoom> item in Rooms)
                {
                    clone.Rooms.Add(item.Key, item.Value);
                }

                foreach (KeyValuePair<string, LobbyAssignment> item in Assignments)
                {
                    clone.Assignments.Add(item.Key, item.Value);
                }

                foreach (KeyValuePair<string, LobbyMatchTicket> item in MatchTickets)
                {
                    clone.MatchTickets.Add(item.Key, item.Value);
                }

                foreach (KeyValuePair<string, LobbyTableTicketRecord> item in TableTickets)
                {
                    clone.TableTickets.Add(item.Key, item.Value);
                }

                foreach (KeyValuePair<string, LobbyTicketConsumption> item in Consumptions)
                {
                    clone.Consumptions.Add(item.Key, item.Value);
                }

                foreach (KeyValuePair<ReceiptKey, LobbyReceiptRecord> item in Receipts)
                {
                    clone.Receipts.Add(item.Key, item.Value);
                }

                return clone;
            }
        }

        private sealed class InMemoryTransaction : ILobbyTransaction
        {
            private readonly State _state;

            public InMemoryTransaction(State state)
            {
                _state = state;
            }

            public Task LockAccountAsync(string accountId, CancellationToken cancellationToken)
            {
                // The in-memory store already serializes every transaction.
                return Task.CompletedTask;
            }

            public Task<LobbyAccount?> FindAccountByBootstrapAsync(
                string deviceDigest,
                string bootstrapHash,
                CancellationToken cancellationToken)
            {
                if (_state.BootstrapAccounts.TryGetValue(
                        new BootstrapKey(deviceDigest, bootstrapHash),
                        out string? accountId) &&
                    _state.Accounts.TryGetValue(accountId, out LobbyAccount? account))
                {
                    return Task.FromResult<LobbyAccount?>(account);
                }

                return Task.FromResult<LobbyAccount?>(null);
            }

            public Task<LobbyAccount?> FindAccountAsync(string accountId, CancellationToken cancellationToken)
            {
                _state.Accounts.TryGetValue(accountId, out LobbyAccount? account);
                return Task.FromResult(account);
            }

            public Task InsertAccountAsync(
                LobbyAccount account,
                string deviceDigest,
                string bootstrapHash,
                CancellationToken cancellationToken)
            {
                var key = new BootstrapKey(deviceDigest, bootstrapHash);
                if (_state.BootstrapAccounts.ContainsKey(key))
                {
                    throw new LobbyException(409, "BOOTSTRAP_ALREADY_BOUND", "This device/bootstrap pair already has a guest account.");
                }

                if (_state.Accounts.ContainsKey(account.AccountId))
                {
                    throw new LobbyException(409, "ACCOUNT_EXISTS", "The account id already exists.");
                }

                _state.Accounts.Add(account.AccountId, account);
                _state.BootstrapAccounts.Add(key, account.AccountId);
                return Task.CompletedTask;
            }

            public Task InsertSessionAsync(LobbySession session, CancellationToken cancellationToken)
            {
                if (_state.Sessions.ContainsKey(session.TokenHash))
                {
                    throw new LobbyException(409, "SESSION_COLLISION", "The generated session token collided.");
                }

                _state.Sessions.Add(session.TokenHash, session);
                return Task.CompletedTask;
            }

            public Task<LobbyRoom?> FindRoomForUpdateAsync(string roomId, CancellationToken cancellationToken)
            {
                _state.Rooms.TryGetValue(roomId, out LobbyRoom? room);
                return Task.FromResult(room);
            }

            public Task InsertRoomAsync(LobbyRoom room, CancellationToken cancellationToken)
            {
                if (_state.Rooms.ContainsKey(room.RoomId))
                {
                    throw new LobbyException(409, "ROOM_EXISTS", "The room id already exists.");
                }

                _state.Rooms.Add(room.RoomId, room);
                return Task.CompletedTask;
            }

            public Task UpdateRoomAsync(LobbyRoom room, CancellationToken cancellationToken)
            {
                if (!_state.Rooms.ContainsKey(room.RoomId))
                {
                    throw new LobbyException(404, "ROOM_NOT_FOUND", "The room does not exist.");
                }

                _state.Rooms[room.RoomId] = room;
                return Task.CompletedTask;
            }

            public Task<int> CancelActiveAssignmentsForRoomAsync(
                string roomId,
                DateTimeOffset now,
                CancellationToken cancellationToken)
            {
                int cancelled = 0;
                foreach (LobbyAssignment assignment in _state.Assignments.Values.ToList())
                {
                    if (!string.Equals(assignment.RoomId, roomId, StringComparison.Ordinal) ||
                        !IsActive(assignment.Status))
                    {
                        continue;
                    }

                    // Terminal reconciliation releases every seat directly; it
                    // intentionally bypasses the active-placement uniqueness
                    // check because it is removing, not adding, placements.
                    _state.Assignments[assignment.AssignmentId] = assignment with
                    {
                        Status = LobbyAssignmentStatus.Cancelled,
                        UpdatedAt = now,
                        ExpiresAt = null
                    };

                    if (assignment.MatchTicketId != null &&
                        _state.MatchTickets.TryGetValue(assignment.MatchTicketId, out LobbyMatchTicket? matchTicket) &&
                        matchTicket.Status == LobbyMatchStatus.Reserved)
                    {
                        _state.MatchTickets[matchTicket.MatchTicketId] = matchTicket with
                        {
                            Status = LobbyMatchStatus.Cancelled,
                            CancelledAt = now
                        };
                    }

                    cancelled++;
                }

                // Also release match reservations that point at this terminal
                // room even if an older assignment lost its match_ticket_id.
                foreach (LobbyMatchTicket matchTicket in _state.MatchTickets.Values.ToList())
                {
                    if (string.Equals(matchTicket.RoomId, roomId, StringComparison.Ordinal) &&
                        matchTicket.Status == LobbyMatchStatus.Reserved)
                    {
                        _state.MatchTickets[matchTicket.MatchTicketId] = matchTicket with
                        {
                            Status = LobbyMatchStatus.Cancelled,
                            CancelledAt = now
                        };
                    }
                }

                return Task.FromResult(cancelled);
            }

            public Task<LobbyAssignment?> FindAssignmentForUpdateAsync(
                string assignmentId,
                CancellationToken cancellationToken)
            {
                _state.Assignments.TryGetValue(assignmentId, out LobbyAssignment? assignment);
                return Task.FromResult(assignment);
            }

            public Task<LobbyAssignment?> FindAnyActiveAssignmentForAccountAsync(
                string accountId,
                CancellationToken cancellationToken)
            {
                LobbyAssignment? assignment = _state.Assignments.Values
                    .Where(a => string.Equals(a.AccountId, accountId, StringComparison.Ordinal) && IsActive(a.Status))
                    .OrderByDescending(a => a.UpdatedAt)
                    .FirstOrDefault();
                return Task.FromResult(assignment);
            }

            public Task<IReadOnlyList<LobbyAssignment>> ListActiveAssignmentsForAccountAsync(
                string accountId,
                CancellationToken cancellationToken)
            {
                IReadOnlyList<LobbyAssignment> list = _state.Assignments.Values
                    .Where(a => string.Equals(a.AccountId, accountId, StringComparison.Ordinal) && IsActive(a.Status))
                    .OrderBy(a => a.CreatedAt)
                    .ToList();
                return Task.FromResult(list);
            }

            public Task<IReadOnlyList<LobbyAssignment>> ListAssignmentsAsync(
                string roomId,
                CancellationToken cancellationToken)
            {
                IReadOnlyList<LobbyAssignment> list = _state.Assignments.Values
                    .Where(a => string.Equals(a.RoomId, roomId, StringComparison.Ordinal))
                    .OrderBy(a => a.SeatId ?? int.MaxValue)
                    .ThenBy(a => a.CreatedAt)
                    .ToList();
                return Task.FromResult(list);
            }

            public Task<int> CountActiveAssignmentsAsync(
                string roomId,
                LobbyRole role,
                CancellationToken cancellationToken)
            {
                int count = _state.Assignments.Values.Count(a =>
                    string.Equals(a.RoomId, roomId, StringComparison.Ordinal) &&
                    a.Role == role &&
                    IsActive(a.Status));
                return Task.FromResult(count);
            }

            public Task InsertAssignmentAsync(LobbyAssignment assignment, CancellationToken cancellationToken)
            {
                if (_state.Assignments.ContainsKey(assignment.AssignmentId))
                {
                    throw new LobbyException(409, "ASSIGNMENT_EXISTS", "The assignment id already exists.");
                }

                EnsureAssignmentUnique(assignment, null);
                _state.Assignments.Add(assignment.AssignmentId, assignment);
                return Task.CompletedTask;
            }

            public Task UpdateAssignmentAsync(LobbyAssignment assignment, CancellationToken cancellationToken)
            {
                if (!_state.Assignments.ContainsKey(assignment.AssignmentId))
                {
                    throw new LobbyException(404, "ASSIGNMENT_NOT_FOUND", "The assignment does not exist.");
                }

                EnsureAssignmentUnique(assignment, assignment.AssignmentId);
                _state.Assignments[assignment.AssignmentId] = assignment;
                return Task.CompletedTask;
            }

            public Task<LobbyMatchTicket?> FindMatchTicketAsync(
                string matchTicketId,
                CancellationToken cancellationToken)
            {
                _state.MatchTickets.TryGetValue(matchTicketId, out LobbyMatchTicket? ticket);
                return Task.FromResult(ticket);
            }

            public Task<LobbyMatchTicket?> FindMatchTicketForUpdateAsync(
                string matchTicketId,
                CancellationToken cancellationToken)
            {
                _state.MatchTickets.TryGetValue(matchTicketId, out LobbyMatchTicket? ticket);
                return Task.FromResult(ticket);
            }

            public Task<LobbyMatchTicket?> FindActiveMatchTicketForAccountAsync(
                string accountId,
                CancellationToken cancellationToken)
            {
                LobbyMatchTicket? ticket = _state.MatchTickets.Values
                    .Where(t => string.Equals(t.AccountId, accountId, StringComparison.Ordinal) &&
                                (t.Status == LobbyMatchStatus.Queued || t.Status == LobbyMatchStatus.Reserved))
                    .OrderBy(t => t.CreatedAt)
                    .FirstOrDefault();
                return Task.FromResult(ticket);
            }

            public Task<IReadOnlyList<LobbyMatchTicket>> ListQueuedMatchTicketsForUpdateAsync(
                LobbyMatchMode mode,
                uint rulesetVersion,
                string? region,
                int limit,
                CancellationToken cancellationToken)
            {
                IReadOnlyList<LobbyMatchTicket> tickets = _state.MatchTickets.Values
                    .Where(t => t.Status == LobbyMatchStatus.Queued &&
                                t.Mode == mode &&
                                t.RulesetVersion == rulesetVersion &&
                                (region == null || string.Equals(t.Region, region, StringComparison.Ordinal)))
                    .OrderBy(t => t.CreatedAt)
                    .ThenBy(t => t.MatchTicketId, StringComparer.Ordinal)
                    .Take(limit)
                    .ToList();
                return Task.FromResult(tickets);
            }

            public Task InsertMatchTicketAsync(LobbyMatchTicket ticket, CancellationToken cancellationToken)
            {
                if (_state.MatchTickets.ContainsKey(ticket.MatchTicketId))
                {
                    throw new LobbyException(409, "MATCH_TICKET_EXISTS", "The match ticket id already exists.");
                }

                bool hasActive = _state.MatchTickets.Values.Any(t =>
                    string.Equals(t.AccountId, ticket.AccountId, StringComparison.Ordinal) &&
                    (t.Status == LobbyMatchStatus.Queued || t.Status == LobbyMatchStatus.Reserved));
                if (hasActive)
                {
                    throw new LobbyException(409, "ALREADY_QUEUED", "This account already has an active match ticket.");
                }

                _state.MatchTickets.Add(ticket.MatchTicketId, ticket);
                return Task.CompletedTask;
            }

            public Task UpdateMatchTicketAsync(LobbyMatchTicket ticket, CancellationToken cancellationToken)
            {
                if (!_state.MatchTickets.ContainsKey(ticket.MatchTicketId))
                {
                    throw new LobbyException(404, "MATCH_TICKET_NOT_FOUND", "The match ticket does not exist.");
                }

                _state.MatchTickets[ticket.MatchTicketId] = ticket;
                return Task.CompletedTask;
            }

            public Task<LobbyTableTicketRecord?> FindTableTicketForUpdateAsync(
                string jti,
                CancellationToken cancellationToken)
            {
                _state.TableTickets.TryGetValue(jti, out LobbyTableTicketRecord? ticket);
                return Task.FromResult(ticket);
            }

            public Task<LobbyTableTicketRecord?> FindActiveTableTicketForAssignmentAsync(
                string assignmentId,
                CancellationToken cancellationToken)
            {
                LobbyTableTicketRecord? ticket = _state.TableTickets.Values
                    .Where(t => string.Equals(t.AssignmentId, assignmentId, StringComparison.Ordinal) &&
                                t.ConsumedAt == null &&
                                t.SupersededAt == null)
                    .OrderByDescending(t => t.IssuedAt)
                    .FirstOrDefault();
                return Task.FromResult(ticket);
            }

            public Task InsertTableTicketAsync(LobbyTableTicketRecord ticket, CancellationToken cancellationToken)
            {
                if (_state.TableTickets.ContainsKey(ticket.Jti))
                {
                    throw new LobbyException(409, "TABLE_TICKET_EXISTS", "The ticket id already exists.");
                }

                bool hasActive = _state.TableTickets.Values.Any(t =>
                    string.Equals(t.AssignmentId, ticket.AssignmentId, StringComparison.Ordinal) &&
                    t.ConsumedAt == null &&
                    t.SupersededAt == null);
                if (hasActive)
                {
                    throw new LobbyException(409, "TABLE_TICKET_ALREADY_ACTIVE", "The assignment already has an active table ticket.");
                }

                _state.TableTickets.Add(ticket.Jti, ticket);
                return Task.CompletedTask;
            }

            public Task UpdateTableTicketAsync(LobbyTableTicketRecord ticket, CancellationToken cancellationToken)
            {
                if (!_state.TableTickets.ContainsKey(ticket.Jti))
                {
                    throw new LobbyException(404, "TABLE_TICKET_NOT_FOUND", "The table ticket does not exist.");
                }

                _state.TableTickets[ticket.Jti] = ticket;
                return Task.CompletedTask;
            }

            public Task InsertTicketConsumptionAsync(
                LobbyTicketConsumption consumption,
                CancellationToken cancellationToken)
            {
                if (_state.Consumptions.ContainsKey(consumption.Jti))
                {
                    throw new LobbyException(409, "TICKET_ALREADY_CONSUMED", "The table ticket was already consumed.");
                }

                // Seat uniqueness is enforced by the active assignment unique
                // index, not by ticket consumption: a reconnect gets a new
                // one-time ticket for the already confirmed assignment.
                _state.Consumptions.Add(consumption.Jti, consumption);
                return Task.CompletedTask;
            }

            public Task<int> ExpireDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
            {
                int changed = 0;
                foreach (LobbyMatchTicket ticket in _state.MatchTickets.Values.ToList())
                {
                    if ((ticket.Status != LobbyMatchStatus.Queued && ticket.Status != LobbyMatchStatus.Reserved) ||
                        ticket.ExpiresAt > now)
                    {
                        continue;
                    }

                    if (ticket.Status == LobbyMatchStatus.Reserved && ticket.AssignmentId != null &&
                        _state.Assignments.TryGetValue(ticket.AssignmentId, out LobbyAssignment? assignment) &&
                        assignment.Status == LobbyAssignmentStatus.Reserved)
                    {
                        _state.Assignments[assignment.AssignmentId] = assignment with
                        {
                            Status = LobbyAssignmentStatus.Expired,
                            UpdatedAt = now,
                            ExpiresAt = null
                        };
                    }

                    _state.MatchTickets[ticket.MatchTicketId] = ticket with
                    {
                        Status = LobbyMatchStatus.Expired
                    };
                    changed++;
                }

                foreach (LobbyAssignment assignment in _state.Assignments.Values.ToList())
                {
                    if (assignment.Status == LobbyAssignmentStatus.Reserved &&
                        assignment.ExpiresAt.HasValue &&
                        assignment.ExpiresAt.Value <= now)
                    {
                        _state.Assignments[assignment.AssignmentId] = assignment with
                        {
                            Status = LobbyAssignmentStatus.Expired,
                            UpdatedAt = now,
                            ExpiresAt = null
                        };
                        changed++;
                    }
                }

                return Task.FromResult(changed);
            }

            private void EnsureAssignmentUnique(LobbyAssignment assignment, string? excludingAssignmentId)
            {
                foreach (LobbyAssignment existing in _state.Assignments.Values)
                {
                    if (excludingAssignmentId != null &&
                        string.Equals(existing.AssignmentId, excludingAssignmentId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!IsActive(existing.Status))
                    {
                        continue;
                    }

                    // One active lobby placement per account, across all rooms
                    // and roles. A player seat can never be held twice.
                    if (string.Equals(existing.AccountId, assignment.AccountId, StringComparison.Ordinal))
                    {
                        throw new LobbyException(409, "ALREADY_ASSIGNED", "This account already has an active assignment.");
                    }

                    if (string.Equals(existing.RoomId, assignment.RoomId, StringComparison.Ordinal) &&
                        assignment.Role == LobbyRole.Player &&
                        existing.Role == LobbyRole.Player &&
                        existing.SeatId == assignment.SeatId)
                    {
                        throw new LobbyException(409, "SEAT_TAKEN", "The preferred seat is already reserved.");
                    }
                }
            }


        }
    }
}
