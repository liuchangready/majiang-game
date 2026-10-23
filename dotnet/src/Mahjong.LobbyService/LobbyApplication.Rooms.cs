using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Mahjong.LobbyService
{
    public sealed partial class LobbyApplication
    {
        public async Task<LobbyResponse> CreateRoomAsync(
            LobbyAccount account,
            CreateRoomCommand command,
            string requestId,
            CancellationToken cancellationToken)
        {
            if (command == null)
            {
                throw new LobbyException(400, "MALFORMED_REQUEST", "A create-room body is required.");
            }

            string title = LobbyText.RequireTitle(command.Title);
            if (command.MaxPlayers != 4)
            {
                throw new LobbyException(
                    400,
                    "UNSUPPORTED_SEAT_COUNT",
                    "rulesetVersion=1 is a four-player ruleset; maxPlayers must be 4.");
            }

            LobbyRoomVisibility visibility = command.Visibility;
            if (visibility == LobbyRoomVisibility.Unspecified)
            {
                throw new LobbyException(400, "MALFORMED_REQUEST", "visibility must be PUBLIC or PRIVATE.");
            }

            string payloadDigest = LobbyDigest.Compute(
                "room.create.v1",
                title,
                command.MaxPlayers.ToString(System.Globalization.CultureInfo.InvariantCulture),
                visibility.ToString(),
                command.AllowSpectators ? "1" : "0");
            DateTimeOffset now = _clock.UtcNow;
            string roomId = LobbyIds.NewPrefixed("room_");
            string assignmentId = LobbyIds.NewPrefixed("asg_");
            var key = new LobbyIdempotencyKey(account.AccountId, requestId);

            return await _store.ExecuteIdempotentAsync(
                key,
                payloadDigest,
                now,
                async (tx, ct) =>
                {
                    await tx.ExpireDueAsync(now, ct).ConfigureAwait(false);
                    await tx.LockAccountAsync(account.AccountId, ct).ConfigureAwait(false);
                    LobbyAssignment? activeRoom = await tx.FindAnyActiveAssignmentForAccountAsync(
                        account.AccountId,
                        ct).ConfigureAwait(false);
                    if (activeRoom != null)
                    {
                        throw new LobbyException(409, "ALREADY_ASSIGNED", "Leave the active room assignment before creating another room.");
                    }

                    LobbyMatchTicket? activeMatch = await tx.FindActiveMatchTicketForAccountAsync(
                        account.AccountId,
                        ct).ConfigureAwait(false);
                    if (activeMatch != null)
                    {
                        throw new LobbyException(409, "ALREADY_QUEUED", "Cancel the active match ticket before creating a room.");
                    }

                    var room = new LobbyRoom(
                        roomId,
                        title,
                        visibility,
                        LobbyRoomState.Open,
                        command.AllowSpectators,
                        command.MaxPlayers,
                        Mahjong.Contracts.ProtocolConstants.RulesetVersion,
                        account.AccountId,
                        now,
                        now,
                        null);
                    var assignment = new LobbyAssignment(
                        assignmentId,
                        roomId,
                        account.AccountId,
                        LobbyRole.Player,
                        0,
                        LobbyAssignmentStatus.Confirmed,
                        now,
                        now,
                        null,
                        now,
                        null,
                        null);
                    await tx.InsertRoomAsync(room, ct).ConfigureAwait(false);
                    await tx.InsertAssignmentAsync(assignment, ct).ConfigureAwait(false);

                    var roomView = new RoomSummaryView(
                        room.RoomId,
                        room.Title,
                        "PUBLIC",
                        "OPEN",
                        room.AllowSpectators,
                        room.MaxPlayers,
                        1,
                        room.MaxPlayers - 1,
                        0,
                        room.RulesetVersion,
                        room.CreatedAt,
                        room.UpdatedAt,
                        ProjectionStale: true);
                    if (visibility == LobbyRoomVisibility.Private)
                    {
                        roomView = roomView with { Visibility = "PRIVATE" };
                    }

                    var view = new CreateRoomView(roomView, ToView(assignment));
                    return new LobbyResponse(201, LobbyJson.Serialize(view));
                },
                cancellationToken).ConfigureAwait(false);
        }

        public async Task<LobbyResponse> JoinRoomAsync(
            LobbyAccount account,
            string? roomId,
            JoinRoomCommand command,
            string requestId,
            CancellationToken cancellationToken)
        {
            string id = LobbyText.RequireId(roomId, "roomId");
            int? preferredSeat = command?.PreferSeat;
            if (preferredSeat.HasValue &&
                (preferredSeat.Value < Mahjong.Contracts.ProtocolConstants.MinSeat ||
                 preferredSeat.Value > Mahjong.Contracts.ProtocolConstants.MaxSeat))
            {
                throw new LobbyException(400, "MALFORMED_REQUEST", "preferSeat must be 0..3.");
            }

            string payloadDigest = LobbyDigest.Compute(
                "room.join.v1",
                id,
                preferredSeat?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-");
            DateTimeOffset now = _clock.UtcNow;
            string assignmentId = LobbyIds.NewPrefixed("asg_");
            var key = new LobbyIdempotencyKey(account.AccountId, requestId);

            return await _store.ExecuteIdempotentAsync(
                key,
                payloadDigest,
                now,
                async (tx, ct) =>
                {
                    await tx.ExpireDueAsync(now, ct).ConfigureAwait(false);
                    await tx.LockAccountAsync(account.AccountId, ct).ConfigureAwait(false);
                    LobbyRoom? room = await tx.FindRoomForUpdateAsync(id, ct).ConfigureAwait(false);
                    if (room == null)
                    {
                        throw new LobbyException(404, "ROOM_NOT_FOUND", "The room does not exist.");
                    }

                    if (room.State != LobbyRoomState.Open)
                    {
                        throw new LobbyException(409, "ROOM_CLOSED", "The room is closed.");
                    }

                    LobbyAssignment? existing = await tx.FindAnyActiveAssignmentForAccountAsync(
                        account.AccountId,
                        ct).ConfigureAwait(false);
                    if (existing != null)
                    {
                        throw new LobbyException(409, "ALREADY_ASSIGNED", "This account already has an active assignment.");
                    }

                    LobbyMatchTicket? activeMatch = await tx.FindActiveMatchTicketForAccountAsync(
                        account.AccountId,
                        ct).ConfigureAwait(false);
                    if (activeMatch != null)
                    {
                        throw new LobbyException(409, "ALREADY_QUEUED", "Cancel the active match ticket before joining a room.");
                    }

                    IReadOnlyList<LobbyAssignment> assignments = await tx.ListAssignmentsAsync(id, ct)
                        .ConfigureAwait(false);
                    var occupied = new HashSet<int>(assignments
                        .Where(a => a.Role == LobbyRole.Player &&
                                    (a.Status == LobbyAssignmentStatus.Reserved ||
                                     a.Status == LobbyAssignmentStatus.Confirmed) &&
                                    a.SeatId.HasValue)
                        .Select(a => a.SeatId!.Value));

                    int seat = -1;
                    if (preferredSeat.HasValue && !occupied.Contains(preferredSeat.Value))
                    {
                        seat = preferredSeat.Value;
                    }
                    else
                    {
                        for (int candidate = 0; candidate < room.MaxPlayers; candidate++)
                        {
                            if (!occupied.Contains(candidate))
                            {
                                seat = candidate;
                                break;
                            }
                        }
                    }

                    if (seat < 0)
                    {
                        throw new LobbyException(409, "ROOM_FULL", "The room has no free player seat.");
                    }

                    var assignment = new LobbyAssignment(
                        assignmentId,
                        id,
                        account.AccountId,
                        LobbyRole.Player,
                        seat,
                        LobbyAssignmentStatus.Reserved,
                        now,
                        now,
                        now.AddSeconds(_options.ReservationTtlSeconds),
                        null,
                        null,
                        null);
                    await tx.InsertAssignmentAsync(assignment, ct).ConfigureAwait(false);
                    return new LobbyResponse(201, LobbyJson.Serialize(new JoinRoomView(ToView(assignment))));
                },
                cancellationToken).ConfigureAwait(false);
        }

        public async Task<LobbyResponse> SpectateRoomAsync(
            LobbyAccount account,
            string? roomId,
            string requestId,
            CancellationToken cancellationToken)
        {
            string id = LobbyText.RequireId(roomId, "roomId");
            string payloadDigest = LobbyDigest.Compute("room.spectate.v1", id);
            DateTimeOffset now = _clock.UtcNow;
            string assignmentId = LobbyIds.NewPrefixed("asg_");
            var key = new LobbyIdempotencyKey(account.AccountId, requestId);

            return await _store.ExecuteIdempotentAsync(
                key,
                payloadDigest,
                now,
                async (tx, ct) =>
                {
                    await tx.ExpireDueAsync(now, ct).ConfigureAwait(false);
                    await tx.LockAccountAsync(account.AccountId, ct).ConfigureAwait(false);
                    LobbyRoom? room = await tx.FindRoomForUpdateAsync(id, ct).ConfigureAwait(false);
                    if (room == null)
                    {
                        throw new LobbyException(404, "ROOM_NOT_FOUND", "The room does not exist.");
                    }

                    if (room.State != LobbyRoomState.Open)
                    {
                        throw new LobbyException(409, "ROOM_CLOSED", "The room is closed.");
                    }

                    if (!room.AllowSpectators)
                    {
                        throw new LobbyException(403, "SPECTATING_DISABLED", "This room does not allow spectators.");
                    }

                    LobbyAssignment? existing = await tx.FindAnyActiveAssignmentForAccountAsync(
                        account.AccountId,
                        ct).ConfigureAwait(false);
                    if (existing != null)
                    {
                        throw new LobbyException(409, "ALREADY_ASSIGNED", "This account already has an active assignment.");
                    }

                    LobbyMatchTicket? activeMatch = await tx.FindActiveMatchTicketForAccountAsync(
                        account.AccountId,
                        ct).ConfigureAwait(false);
                    if (activeMatch != null)
                    {
                        throw new LobbyException(409, "ALREADY_QUEUED", "Cancel the active match ticket before joining a room.");
                    }

                    int spectators = await tx.CountActiveAssignmentsAsync(id, LobbyRole.Spectator, ct)
                        .ConfigureAwait(false);
                    if (spectators >= _options.MaxSpectatorsPerRoom)
                    {
                        throw new LobbyException(409, "SPECTATOR_LIMIT", "The spectator limit for this room is reached.");
                    }

                    var assignment = new LobbyAssignment(
                        assignmentId,
                        id,
                        account.AccountId,
                        LobbyRole.Spectator,
                        null,
                        LobbyAssignmentStatus.Confirmed,
                        now,
                        now,
                        null,
                        now,
                        null,
                        null);
                    await tx.InsertAssignmentAsync(assignment, ct).ConfigureAwait(false);
                    return new LobbyResponse(201, LobbyJson.Serialize(new JoinRoomView(ToView(assignment))));
                },
                cancellationToken).ConfigureAwait(false);
        }
    }
}
