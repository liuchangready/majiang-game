using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Mahjong.LobbyService
{
    /// <summary>
    /// Lobby application boundary. It owns guest sessions, directory,
    /// matchmaking and ticket issuance/consumption. It has no Domain or
    /// TableRuntime reference; the only table input is public room_projection.
    /// </summary>
    public sealed partial class LobbyApplication
    {
        private readonly ILobbyStore _store;
        private readonly ITableTicketSigner _tickets;
        private readonly ILobbyClock _clock;
        private readonly LobbyOptions _options;
        private readonly IRoomProjectionReader _projections;
        private readonly ILogger<LobbyApplication>? _logger;

        public LobbyApplication(
            ILobbyStore store,
            ITableTicketSigner tickets,
            ILobbyClock clock,
            LobbyOptions options,
            IRoomProjectionReader projections,
            ILogger<LobbyApplication>? logger = null)
        {
            _store = store;
            _tickets = tickets;
            _clock = clock;
            _options = options;
            _projections = projections;
            _logger = logger;
        }

        private bool RankedMatchAllowed =>
            _options.AllowGuestRankedMatch && _options.ProductDecisionAcknowledged;

        public LobbyCapabilitiesView Capabilities => new LobbyCapabilitiesView(
            "GUEST_ONLY",
            GuestOnly: true,
            RankedMatchAllowed: RankedMatchAllowed,
            SpectateAllowed: true,
            MatchRule: _options.MatchRule,
            SpectatorDelaySeconds: 0,
            ProductDecision: "PENDING",
            DataRetention:
                "TEMPORARY guest account " + _options.GuestAccountRetentionDays +
                "d, receipts " + _options.ReceiptRetentionDays +
                "d; cleanup/export policy not implemented",
            LaunchBlocked: new[]
            {
                "OIDC_ACCOUNTS",
                "RANKED_MATCH",
                "ACCOUNT_ASSETS",
                "SPECTATOR_DELAY",
                "MATCHMAKING_POLICY",
                "DATA_RETENTION_POLICY",
                "REAL_TABLE_INTEGRATION",
                "PUBLIC_HTTPS_DEPLOYMENT"
            });

        public ILobbyStore Store => _store;

        public async Task<LobbySessionInfo> AuthenticateAsync(string? sessionToken, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(sessionToken) || sessionToken.Length > 256)
            {
                throw new LobbyException(401, "UNAUTHENTICATED", "A bearer lobby session is required.");
            }

            string tokenHash = LobbyIds.HashToken(sessionToken);
            LobbySessionInfo? info = await _store.FindSessionByTokenHashAsync(tokenHash, cancellationToken)
                .ConfigureAwait(false);
            DateTimeOffset now = _clock.UtcNow;
            if (info == null ||
                info.Session.RevokedAt.HasValue ||
                info.Session.ExpiresAt <= now ||
                info.Account.ExpiresAt <= now)
            {
                throw new LobbyException(401, "UNAUTHENTICATED", "The lobby session is unknown, expired or revoked.");
            }

            return info;
        }

        public async Task<LobbyResponse> ListRoomsAsync(
            string? cursor,
            int? limit,
            CancellationToken cancellationToken)
        {
            int pageSize = limit ?? _options.DefaultPageSize;
            if (pageSize < 1 || pageSize > _options.MaxPageSize)
            {
                throw new LobbyException(400, "INVALID_PAGE_SIZE", "limit must be between 1 and " + _options.MaxPageSize + ".");
            }

            DateTimeOffset now = _clock.UtcNow;
            LobbyRoomPage page = await _store.ListRoomsAsync(cursor, pageSize, now, cancellationToken)
                .ConfigureAwait(false);
            PublicRoomProjectionBatch projections = await ReadProjectionsSafeAsync(
                page.Items.Select(item => item.Room.RoomId).ToList(),
                cancellationToken).ConfigureAwait(false);
            Dictionary<string, PublicRoomProjection> byRoom = projections.Items
                .ToDictionary(item => item.RoomId, StringComparer.Ordinal);

            var views = new List<RoomSummaryView>(page.Items.Count);
            foreach (LobbyRoomSnapshot snapshot in page.Items)
            {
                byRoom.TryGetValue(snapshot.Room.RoomId, out PublicRoomProjection? projection);
                RoomSummaryView summary = ToSummary(snapshot, projection);
                if (!string.Equals(summary.State, "CLOSED", StringComparison.Ordinal))
                {
                    views.Add(summary);
                }
            }

            return new LobbyResponse(200, LobbyJson.Serialize(new RoomCatalogView(views, page.NextCursor)));
        }

        public async Task<LobbyResponse> GetRoomAsync(string? roomId, CancellationToken cancellationToken)
        {
            string id = LobbyText.RequireId(roomId, "roomId");
            DateTimeOffset now = _clock.UtcNow;
            LobbyRoomSnapshot? snapshot = await _store.FindRoomSnapshotAsync(id, now, cancellationToken)
                .ConfigureAwait(false);
            if (snapshot == null)
            {
                throw new LobbyException(404, "ROOM_NOT_FOUND", "The room does not exist.");
            }

            PublicRoomProjectionBatch projections = await ReadProjectionsSafeAsync(
                new[] { id },
                cancellationToken).ConfigureAwait(false);
            PublicRoomProjection? projection = projections.Items.FirstOrDefault(
                item => string.Equals(item.RoomId, id, StringComparison.Ordinal));
            return new LobbyResponse(200, LobbyJson.Serialize(ToSummary(snapshot, projection)));
        }

        public Task<LobbyResponse> GetCapabilitiesAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new LobbyResponse(200, LobbyJson.Serialize(Capabilities)));
        }

        public async Task<int> RunMaintenanceOnceAsync(CancellationToken cancellationToken)
        {
            DateTimeOffset now = _clock.UtcNow;
            return await _store.ExecuteAsync(
                now,
                (tx, ct) => tx.ExpireDueAsync(now, ct),
                cancellationToken).ConfigureAwait(false);
        }

        private async Task<PublicRoomProjectionBatch> ReadProjectionsSafeAsync(
            IReadOnlyList<string> roomIds,
            CancellationToken cancellationToken)
        {
            if (roomIds.Count == 0)
            {
                return PublicRoomProjectionBatch.Empty;
            }

            try
            {
                return await _projections.ReadAsync(roomIds, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(
                    "Public room projection is unavailable; serving lobby-owned directory state. Error={ErrorType}",
                    ex.GetType().Name);
                return PublicRoomProjectionBatch.Empty;
            }
        }

        private RoomSummaryView ToSummary(LobbyRoomSnapshot snapshot, PublicRoomProjection? projection)
        {
            LobbyRoom room = snapshot.Room;
            int seatsTaken = snapshot.SeatsTaken;
            int spectators = snapshot.SpectatorCount;
            string state;
            bool stale = projection == null;

            if (room.State == LobbyRoomState.Closed)
            {
                state = "CLOSED";
            }
            else if (projection != null &&
                     projection.RulesetVersion == room.RulesetVersion &&
                     (string.Equals(projection.Lifecycle, "CLOSED", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(projection.Lifecycle, "FINISHED", StringComparison.OrdinalIgnoreCase)))
            {
                state = "CLOSED";
            }
            else if (projection != null &&
                     projection.RulesetVersion == room.RulesetVersion &&
                     (string.Equals(projection.Lifecycle, "PLAYING", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(projection.Lifecycle, "PREPARING", StringComparison.OrdinalIgnoreCase)))
            {
                state = "IN_GAME";
            }
            else
            {
                state = seatsTaken >= room.MaxPlayers ? "FULL" : "OPEN";
            }

            if (projection != null && projection.RulesetVersion == room.RulesetVersion)
            {
                seatsTaken = Math.Max(seatsTaken, projection.SeatsOccupied);
                spectators = Math.Max(spectators, projection.SpectatorCount);
            }
            else
            {
                stale = true;
            }

            seatsTaken = Math.Clamp(seatsTaken, 0, room.MaxPlayers);
            return new RoomSummaryView(
                room.RoomId,
                room.Title,
                room.Visibility == LobbyRoomVisibility.Public ? "PUBLIC" : "PRIVATE",
                state,
                room.AllowSpectators,
                room.MaxPlayers,
                seatsTaken,
                Math.Max(0, room.MaxPlayers - seatsTaken),
                Math.Max(0, spectators),
                room.RulesetVersion,
                room.CreatedAt,
                projection != null && projection.UpdatedAt > room.UpdatedAt ? projection.UpdatedAt : room.UpdatedAt,
                stale);
        }

        private static string AssignmentStatus(LobbyAssignmentStatus status)
        {
            return status switch
            {
                LobbyAssignmentStatus.Reserved => "RESERVED",
                LobbyAssignmentStatus.Confirmed => "CONFIRMED",
                LobbyAssignmentStatus.Cancelled => "CANCELLED",
                LobbyAssignmentStatus.Expired => "EXPIRED",
                _ => "UNSPECIFIED"
            };
        }

        private static string MatchStatus(LobbyMatchStatus status)
        {
            return status switch
            {
                LobbyMatchStatus.Queued => "QUEUED",
                LobbyMatchStatus.Reserved => "RESERVED",
                LobbyMatchStatus.Confirmed => "CONFIRMED",
                LobbyMatchStatus.Cancelled => "CANCELLED",
                LobbyMatchStatus.Expired => "EXPIRED",
                _ => "UNSPECIFIED"
            };
        }

        private static string RoleText(LobbyRole role)
        {
            return role == LobbyRole.Player ? "PLAYER" : "SPECTATOR";
        }

        private static AssignmentView ToView(LobbyAssignment assignment)
        {
            return new AssignmentView(
                assignment.AssignmentId,
                assignment.RoomId,
                RoleText(assignment.Role),
                assignment.SeatId,
                AssignmentStatus(assignment.Status),
                assignment.CreatedAt,
                assignment.UpdatedAt,
                assignment.ExpiresAt,
                assignment.ConfirmedAt);
        }

    }
}
