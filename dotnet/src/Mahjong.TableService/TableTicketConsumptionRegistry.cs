using System;
using System.Collections.Generic;
using System.Globalization;

namespace Mahjong.TableService
{
    public enum TicketConsumptionStatus : byte
    {
        /// <summary>Admitted. <see cref="TicketConsumptionOutcome.DisplacedSessionId"/> carries a
        /// superseded live session when the same account re-bound its own seat (顶号).</summary>
        Accepted = 0,

        /// <summary>This exact ticket (jti) was already consumed. A signed JWT alone is never
        /// replay protection; the second admission gets zero side effects.</summary>
        DuplicateJti = 1,

        /// <summary>The requested seat is bound to a different live account. Cross-account seat
        /// takeover is refused without touching the existing binding.</summary>
        SeatOccupiedByOtherAccount = 2,

        /// <summary>The room already holds the configured spectator maximum.</summary>
        SpectatorCapacityReached = 3
    }

    public sealed class TicketConsumptionOutcome
    {
        public static readonly TicketConsumptionOutcome DuplicateJti =
            new TicketConsumptionOutcome(TicketConsumptionStatus.DuplicateJti, null, null);

        public static readonly TicketConsumptionOutcome SeatOccupiedByOtherAccount =
            new TicketConsumptionOutcome(TicketConsumptionStatus.SeatOccupiedByOtherAccount, null, null);

        public static readonly TicketConsumptionOutcome SpectatorCapacityReached =
            new TicketConsumptionOutcome(TicketConsumptionStatus.SpectatorCapacityReached, null, null);

        public TicketConsumptionOutcome(TicketConsumptionStatus status, string? displacedSessionId, string? existingAccountId)
        {
            Status = status;
            DisplacedSessionId = displacedSessionId;
            ExistingAccountId = existingAccountId;
        }

        public TicketConsumptionStatus Status { get; }

        /// <summary>Live session id to close with DISPLACED_BY_NEW_CONNECTION (顶号), if any.</summary>
        public string? DisplacedSessionId { get; }

        public string? ExistingAccountId { get; }

        public bool IsAccepted => Status == TicketConsumptionStatus.Accepted;
    }

    /// <summary>
    /// One-time table-ticket consumption port. The frozen Lobby consumption flow remains the
    /// authoritative one-time ledger; until P5.2/P5.3 wire it, the table host enforces the same
    /// explicit semantics locally: a jti admits exactly one connection, a player seat is bound
    /// to exactly one live account connection, the newest valid consumption of the same
    /// (account, room, seat) displaces the older connection (顶号 → DISPLACED_BY_NEW_CONNECTION),
    /// and every refusal leaves existing sessions untouched.
    /// Tickets never carry ownerEpoch; the connection takes the lease generation from the actor.
    /// </summary>
    public interface ITicketConsumptionRegistry
    {
        TicketConsumptionOutcome Consume(ValidatedTableTicket ticket, string sessionId, DateTimeOffset now);

        void Release(string sessionId);
    }

    /// <summary>In-memory P5.1 implementation. Cross-instance uniqueness arrives with P5.2.</summary>
    public sealed class InMemoryTicketConsumptionRegistry : ITicketConsumptionRegistry
    {
        private readonly object _gate = new object();
        private readonly HashSet<string> _consumedJtis = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, SeatBinding> _seatBindings =
            new Dictionary<string, SeatBinding>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _spectatorCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _sessionRooms = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly TableServiceOptions _options;

        public InMemoryTicketConsumptionRegistry(TableServiceOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
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

            lock (_gate)
            {
                if (!_consumedJtis.Add(ticket.Jti))
                {
                    return TicketConsumptionOutcome.DuplicateJti;
                }

                if (ticket.Role == TableAdmissionRole.Spectator)
                {
                    _spectatorCounts.TryGetValue(ticket.RoomId, out int spectators);
                    if (spectators >= _options.MaxSpectatorsPerRoom)
                    {
                        _consumedJtis.Remove(ticket.Jti);
                        return TicketConsumptionOutcome.SpectatorCapacityReached;
                    }

                    _spectatorCounts[ticket.RoomId] = spectators + 1;
                    _sessionRooms[sessionId] = ticket.RoomId;
                    return new TicketConsumptionOutcome(TicketConsumptionStatus.Accepted, null, null);
                }

                string key = SeatKey(ticket.RoomId, ticket.SeatId!.Value);
                if (_seatBindings.TryGetValue(key, out SeatBinding existing))
                {
                    if (!string.Equals(existing.AccountId, ticket.AccountId, StringComparison.Ordinal))
                    {
                        _consumedJtis.Remove(ticket.Jti);
                        return new TicketConsumptionOutcome(
                            TicketConsumptionStatus.SeatOccupiedByOtherAccount,
                            null,
                            existing.AccountId);
                    }

                    // 顶号: the same account re-binds its own seat. The older live session is
                    // displaced; the newest valid consumption wins.
                    _seatBindings[key] = new SeatBinding(ticket.AccountId, sessionId);
                    _sessionRooms[sessionId] = ticket.RoomId;
                    return new TicketConsumptionOutcome(
                        TicketConsumptionStatus.Accepted,
                        existing.SessionId,
                        existing.AccountId);
                }

                _seatBindings[key] = new SeatBinding(ticket.AccountId, sessionId);
                _sessionRooms[sessionId] = ticket.RoomId;
                return new TicketConsumptionOutcome(TicketConsumptionStatus.Accepted, null, null);
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
                if (!_sessionRooms.TryGetValue(sessionId, out string? roomId))
                {
                    return;
                }

                _sessionRooms.Remove(sessionId);
                List<string> seatsToClear = new List<string>();
                foreach (KeyValuePair<string, SeatBinding> binding in _seatBindings)
                {
                    if (string.Equals(binding.Value.SessionId, sessionId, StringComparison.Ordinal))
                    {
                        seatsToClear.Add(binding.Key);
                    }
                }

                foreach (string seat in seatsToClear)
                {
                    _seatBindings.Remove(seat);
                }

                if (_spectatorCounts.TryGetValue(roomId, out int spectators))
                {
                    if (spectators <= 1)
                    {
                        _spectatorCounts.Remove(roomId);
                    }
                    else
                    {
                        _spectatorCounts[roomId] = spectators - 1;
                    }
                }
            }
        }

        private static string SeatKey(string roomId, int seat)
        {
            return roomId + "#" + seat.ToString(CultureInfo.InvariantCulture);
        }

        private readonly struct SeatBinding
        {
            public SeatBinding(string accountId, string sessionId)
            {
                AccountId = accountId;
                SessionId = sessionId;
            }

            public string AccountId { get; }

            public string SessionId { get; }
        }
    }
}
