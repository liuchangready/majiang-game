using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;

namespace Mahjong.LocalHost
{
    public sealed class LocalTableRoom : IAsyncDisposable
    {
        private readonly object _gate = new object();
        private readonly LocalTableHostOptions _options;
        private readonly Dictionary<string, TicketEntry> _tickets = new Dictionary<string, TicketEntry>(StringComparer.Ordinal);
        private readonly HashSet<LocalTableRuntime> _connections = new HashSet<LocalTableRuntime>();
        private readonly HashSet<int> _reservedSeats = new HashSet<int>();
        private readonly HashSet<int> _releasedSeats = new HashSet<int>();
        private int _spectatorReservations;
        private int _spectatorSequence;
        private bool _disposed;
        private bool _phaseStarted;
        private LocalTableRuntime? _authority;

        private sealed class TicketEntry
        {
            public TicketEntry(LocalTableConnectionTicket ticket, long expiresAtMs)
            {
                Ticket = ticket;
                ExpiresAtMs = expiresAtMs;
                State = LocalTicketState.Issued;
            }
            public LocalTableConnectionTicket Ticket { get; }
            public long ExpiresAtMs { get; }
            public LocalTicketState State { get; set; }
        }

        private LocalTableRoom(LocalTableHostOptions options)
        {
            _options = options;
        }

        public string RoomId => _options.RoomId;
        public string SaveDirectory => LocalTableHostPaths.ResolveSaveDirectory(_options.SaveDirectory);
        public LocalTableSeatPlan SeatPlan => _options.SeatPlan;

        public static async ValueTask<LocalTableRoom> OpenAsync(
            LocalTableHostOptions options,
            CancellationToken cancellationToken = default)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            options.Validate();
            cancellationToken.ThrowIfCancellationRequested();
            var room = new LocalTableRoom(options);
            room._authority = await LocalTableRuntime.OpenRoomAuthorityAsync(options, room, cancellationToken).ConfigureAwait(false);
            return room;
        }

        public LocalTableConnectionTicket CreatePlayerTicket(int seatId)
        {
            if (seatId < 0 || seatId > 3 || !_options.SeatPlan.Contains(seatId))
                throw new LocalTableException(LocalTableErrorKind.InvalidOptions, "The requested seat is not a human seat in this room.");
            lock (_gate)
            {
                ThrowIfDisposed();
                PruneExpired();
                if (_reservedSeats.Contains(seatId))
                    throw new LocalTableException(LocalTableErrorKind.InvalidOptions, "The player seat already has an issued or attached ticket.");
                LocalTableConnectionTicket ticket = NewTicket(LocalViewerRole.Player, seatId,
                    "local-" + _options.RoomId + "-seat" + seatId);
                _reservedSeats.Add(seatId);
                _tickets.Add(ticket.Value, new TicketEntry(ticket, NowMs() + 30_000L));
                return ticket;
            }
        }

        public LocalTableConnectionTicket CreateSpectatorTicket()
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                PruneExpired();
                if (!_options.AllowSpectators || _spectatorReservations >= _options.MaxSpectators)
                    throw new LocalTableException(LocalTableErrorKind.InvalidOptions, "Spectator admission is closed or full.");
                int ordinal = ++_spectatorSequence;
                LocalTableConnectionTicket ticket = NewTicket(LocalViewerRole.Spectator, -1,
                    "local-" + _options.RoomId + "-spectator-" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
                _spectatorReservations++;
                _tickets.Add(ticket.Value, new TicketEntry(ticket, NowMs() + 30_000L));
                return ticket;
            }
        }

        public ValueTask<ITableChannel> ConnectAsync(
            LocalTableConnectionTicket ticket,
            CancellationToken cancellationToken = default)
        {
            if (ticket == null) throw new ArgumentNullException(nameof(ticket));
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ThrowIfDisposed();
                PruneExpired();
                if (!_tickets.TryGetValue(ticket.Value, out TicketEntry? entry) ||
                    !ReferenceEquals(entry.Ticket, ticket) || entry.State != LocalTicketState.Issued)
                    throw new LocalTableException(LocalTableErrorKind.InvalidOptions, "Ticket is unknown, expired, reserved or consumed.");
                entry.State = LocalTicketState.Reserved;
                return new ValueTask<ITableChannel>(new LocalTableChannel(_options, this, ticket));
            }
        }

        internal ValueTask<LocalTableRuntime> AttachAsync(LocalTableConnectionTicket ticket, CancellationToken cancellationToken)
        {
            LocalTableRuntime authority;
            TicketEntry entry;
            lock (_gate)
            {
                ThrowIfDisposed();
                if (!_tickets.TryGetValue(ticket.Value, out entry!) || !ReferenceEquals(entry.Ticket, ticket) ||
                    entry.State != LocalTicketState.Reserved || entry.ExpiresAtMs <= NowMs())
                {
                    if (_tickets.TryGetValue(ticket.Value, out TicketEntry? expired) && expired.State == LocalTicketState.Reserved)
                        FailTicket(expired, LocalTicketState.Expired);
                    throw new LocalTableException(LocalTableErrorKind.Unavailable, "Ticket reservation is no longer valid.");
                }
                authority = _authority ?? throw new LocalTableException(LocalTableErrorKind.Unavailable, "Room authority is closed.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            LocalTableRuntime connection = authority.CreateConnection(ticket, this);
            lock (_gate)
            {
                if (_disposed || entry.State != LocalTicketState.Reserved)
                {
                    entry.State = LocalTicketState.Failed;
                    ReleaseReservation(entry.Ticket);
                    throw new LocalTableException(LocalTableErrorKind.Unavailable, "Ticket attach lost a concurrent close race.");
                }
                entry.State = LocalTicketState.Attached;
                _connections.Add(connection);
            }
            return new ValueTask<LocalTableRuntime>(connection);
        }

        internal void Detach(LocalTableRuntime connection)
        {
            lock (_gate)
            {
                if (!_connections.Remove(connection)) return;
                foreach (TicketEntry entry in _tickets.Values)
                {
                    if (entry.State == LocalTicketState.Attached &&
                        string.Equals(entry.Ticket.Value, connection.TicketValueForRoom, StringComparison.Ordinal))
                    {
                        entry.State = LocalTicketState.Detached;
                        ReleaseReservation(entry.Ticket);
                        break;
                    }
                }
            }
        }

        internal void CancelReserved(LocalTableConnectionTicket ticket)
        {
            lock (_gate)
            {
                if (_tickets.TryGetValue(ticket.Value, out TicketEntry? entry) &&
                    ReferenceEquals(entry.Ticket, ticket) && entry.State == LocalTicketState.Reserved)
                    FailTicket(entry, LocalTicketState.Failed);
            }
        }

        internal bool CanOpenHumanDecision(int seat)
        {
            lock (_gate) return _connections.Any(connection => connection.RoleForRoom == LocalViewerRole.Player && connection.SeatIdForRoom == seat && connection.IsSessionOpenForRoom);
        }

        internal void MarkControlChanged(int seat, bool released)
        {
            lock (_gate)
            {
                if (released) _releasedSeats.Add(seat);
                else _releasedSeats.Remove(seat);
            }
        }

        internal bool CanTickAuthority
        {
            get
            {
                lock (_gate)
                {
                    if (_disposed) return false;
                    if (_phaseStarted) return _authority != null;
                    foreach (int seat in _options.SeatPlan.HumanSeats)
                    {
                        if (_releasedSeats.Contains(seat)) continue;
                        bool live = false;
                        foreach (LocalTableRuntime connection in _connections)
                        {
                            if (connection.RoleForRoom == LocalViewerRole.Player && connection.SeatIdForRoom == seat && connection.IsSessionOpenForRoom)
                            {
                                live = true;
                                break;
                            }
                        }
                        if (!live) return false;
                    }
                    _phaseStarted = true;
                    return _authority != null;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            LocalTableRuntime[] connections;
            LocalTableRuntime? authority;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                connections = new LocalTableRuntime[_connections.Count];
                _connections.CopyTo(connections);
                _connections.Clear();
                authority = _authority;
                _authority = null;
                foreach (TicketEntry ticket in _tickets.Values)
                    if (ticket.State == LocalTicketState.Issued || ticket.State == LocalTicketState.Reserved) ticket.State = LocalTicketState.Failed;
                _tickets.Clear();
            }
            for (int i = 0; i < connections.Length; i++) await connections[i].DisposeAsync().ConfigureAwait(false);
            if (authority != null) await authority.DisposeAuthorityAsync().ConfigureAwait(false);
        }

        private LocalTableConnectionTicket NewTicket(LocalViewerRole role, int seat, string account)
        {
            byte[] bytes = new byte[32];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            string value = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            Array.Clear(bytes, 0, bytes.Length);
            return new LocalTableConnectionTicket(value, role, seat, account);
        }

        private void PruneExpired()
        {
            long now = NowMs();
            var expired = new List<TicketEntry>();
            foreach (TicketEntry entry in _tickets.Values)
                if ((entry.State == LocalTicketState.Issued || entry.State == LocalTicketState.Reserved) && entry.ExpiresAtMs <= now) expired.Add(entry);
            for (int i = 0; i < expired.Count; i++) FailTicket(expired[i], LocalTicketState.Expired);
        }

        private void FailTicket(TicketEntry entry, LocalTicketState state)
        {
            entry.State = state;
            ReleaseReservation(entry.Ticket);
        }

        private void ReleaseReservation(LocalTableConnectionTicket ticket)
        {
            if (ticket.Role == LocalViewerRole.Player) _reservedSeats.Remove(ticket.SeatId);
            else if (_spectatorReservations > 0) _spectatorReservations--;
        }

        private long NowMs() => _options.Environment?.ResolveClock().UtcNowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new LocalTableException(LocalTableErrorKind.Unavailable, "Room is closed.");
        }
    }
}
