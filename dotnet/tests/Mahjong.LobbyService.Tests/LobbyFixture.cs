using System;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.LobbyService;

namespace Mahjong.LobbyService.Tests
{
    internal sealed class TestClock : ILobbyClock
    {
        public TestClock(DateTimeOffset now)
        {
            UtcNow = now;
        }

        public DateTimeOffset UtcNow { get; set; }

        public void Advance(TimeSpan delta)
        {
            UtcNow = UtcNow.Add(delta);
        }
    }

    internal sealed class LobbyFixture : IAsyncDisposable
    {
        private readonly ILobbyStore _store;

        private LobbyFixture(
            ILobbyStore store,
            LobbyOptions options,
            TestClock clock,
            TicketKeyRing keyRing,
            JwtTableTicketSigner signer,
            LobbyApplication application)
        {
            _store = store;
            Options = options;
            Clock = clock;
            KeyRing = keyRing;
            Signer = signer;
            Application = application;
        }

        public LobbyOptions Options { get; }

        public TestClock Clock { get; }

        public TicketKeyRing KeyRing { get; }

        public JwtTableTicketSigner Signer { get; }

        public LobbyApplication Application { get; }

        public static LobbyFixture Create(
            Action<LobbyOptions>? configure = null,
            IRoomProjectionReader? projections = null)
        {
            var options = new LobbyOptions
            {
                TicketTtlSeconds = 60,
                TicketMaxTtlSeconds = 300,
                ReservationTtlSeconds = 15,
                QueueTtlSeconds = 120,
                SessionTtlHours = 24,
                GuestAccountRetentionDays = 7,
                MatchingEnabled = true
            };
            configure?.Invoke(options);
            options.Validate("Development");

            var clock = new TestClock(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
            var store = new InMemoryLobbyStore();
            store.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
            var ring = new TicketKeyRing();
            ring.AddEphemeralSigningKey(clock.UtcNow);
            var signer = new JwtTableTicketSigner(ring, options);
            var application = new LobbyApplication(
                store,
                signer,
                clock,
                options,
                projections ?? new EmptyRoomProjectionReader());
            return new LobbyFixture(store, options, clock, ring, signer, application);
        }

        public static LobbyFixture CreateWithStore(
            ILobbyStore store,
            LobbyOptions options,
            TestClock clock,
            IRoomProjectionReader? projections = null)
        {
            var ring = new TicketKeyRing();
            ring.AddEphemeralSigningKey(clock.UtcNow);
            var signer = new JwtTableTicketSigner(ring, options);
            var application = new LobbyApplication(
                store,
                signer,
                clock,
                options,
                projections ?? new EmptyRoomProjectionReader());
            return new LobbyFixture(store, options, clock, ring, signer, application);
        }

        public async Task<GuestSessionView> CreateGuestAsync(
            string deviceId,
            string displayName = "Guest",
            string? bootstrapSecret = null,
            string? requestId = null,
            CancellationToken cancellationToken = default)
        {
            // The default is intentionally fresh per call: a public deviceId
            // alone must never select an existing account, even in test helpers.
            string secret = bootstrapSecret ?? ("test-bootstrap-" + Guid.NewGuid().ToString("N"));
            LobbyResponse response = await Application.CreateGuestSessionAsync(
                new GuestSessionCommand(deviceId, secret, displayName),
                requestId ?? NewRequestId(),
                cancellationToken).ConfigureAwait(false);
            return LobbyJson.Deserialize<GuestSessionView>(response.Json);
        }

        public static string DefaultBootstrap(string deviceId)
        {
            return "test-bootstrap-" + deviceId + "-0123456789abcdef";
        }

        public async Task<LobbySessionInfo> AuthenticateAsync(
            GuestSessionView guest,
            CancellationToken cancellationToken = default)
        {
            return await Application.AuthenticateAsync(guest.SessionToken, cancellationToken).ConfigureAwait(false);
        }

        public async Task<CreateRoomView> CreateRoomAsync(
            GuestSessionView guest,
            string title = "Test Room",
            LobbyRoomVisibility visibility = LobbyRoomVisibility.Public,
            bool allowSpectators = true,
            string? requestId = null,
            CancellationToken cancellationToken = default)
        {
            LobbySessionInfo session = await AuthenticateAsync(guest, cancellationToken).ConfigureAwait(false);
            LobbyResponse response = await Application.CreateRoomAsync(
                session.Account,
                new CreateRoomCommand(title, 4, visibility, allowSpectators),
                requestId ?? NewRequestId(),
                cancellationToken).ConfigureAwait(false);
            return LobbyJson.Deserialize<CreateRoomView>(response.Json);
        }

        public async Task<JoinRoomView> JoinAsync(
            GuestSessionView guest,
            string roomId,
            int? preferSeat = null,
            string? requestId = null,
            CancellationToken cancellationToken = default)
        {
            LobbySessionInfo session = await AuthenticateAsync(guest, cancellationToken).ConfigureAwait(false);
            LobbyResponse response = await Application.JoinRoomAsync(
                session.Account,
                roomId,
                new JoinRoomCommand(preferSeat),
                requestId ?? NewRequestId(),
                cancellationToken).ConfigureAwait(false);
            return LobbyJson.Deserialize<JoinRoomView>(response.Json);
        }

        public async Task<JoinRoomView> SpectateAsync(
            GuestSessionView guest,
            string roomId,
            string? requestId = null,
            CancellationToken cancellationToken = default)
        {
            LobbySessionInfo session = await AuthenticateAsync(guest, cancellationToken).ConfigureAwait(false);
            LobbyResponse response = await Application.SpectateRoomAsync(
                session.Account,
                roomId,
                requestId ?? NewRequestId(),
                cancellationToken).ConfigureAwait(false);
            return LobbyJson.Deserialize<JoinRoomView>(response.Json);
        }

        public async Task<MatchTicketView> CreateMatchAsync(
            GuestSessionView guest,
            LobbyMatchMode mode = LobbyMatchMode.Casual,
            string region = "test",
            string? requestId = null,
            CancellationToken cancellationToken = default)
        {
            LobbySessionInfo session = await AuthenticateAsync(guest, cancellationToken).ConfigureAwait(false);
            LobbyResponse response = await Application.CreateMatchTicketAsync(
                session.Account,
                new CreateMatchCommand(mode, 1, region),
                requestId ?? NewRequestId(),
                cancellationToken).ConfigureAwait(false);
            return LobbyJson.Deserialize<MatchTicketView>(response.Json);
        }

        public async Task<MatchTicketView> GetMatchAsync(
            GuestSessionView guest,
            string matchTicketId,
            CancellationToken cancellationToken = default)
        {
            LobbySessionInfo session = await AuthenticateAsync(guest, cancellationToken).ConfigureAwait(false);
            LobbyResponse response = await Application.GetMatchTicketAsync(
                session.Account,
                matchTicketId,
                cancellationToken).ConfigureAwait(false);
            return LobbyJson.Deserialize<MatchTicketView>(response.Json);
        }

        public async Task<TableTicketView> IssueTicketAsync(
            GuestSessionView guest,
            string assignmentId,
            string? requestId = null,
            CancellationToken cancellationToken = default)
        {
            LobbySessionInfo session = await AuthenticateAsync(guest, cancellationToken).ConfigureAwait(false);
            LobbyResponse response = await Application.IssueTableTicketAsync(
                session.Account,
                new IssueTableTicketCommand(assignmentId),
                requestId ?? NewRequestId(),
                cancellationToken).ConfigureAwait(false);
            return LobbyJson.Deserialize<TableTicketView>(response.Json);
        }

        public Task<TicketConsumptionResult> ConsumeAsync(
            TableTicketView ticket,
            string accountId,
            string? roomId = null,
            int? seatId = null,
            CancellationToken cancellationToken = default)
        {
            int? seat = seatId ?? ticket.SeatId;
            var expectation = new TableTicketExpectation(
                roomId ?? ticket.RoomId,
                ticket.AssignmentId,
                ticket.Role == "PLAYER" ? LobbyRole.Player : LobbyRole.Spectator,
                seat,
                accountId);
            return Application.ConsumeTableTicketAsync(ticket.Ticket, expectation, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await _store.DisposeAsync().ConfigureAwait(false);
        }

        public static string NewRequestId()
        {
            return "req_" + Guid.NewGuid().ToString("N");
        }
    }
}
