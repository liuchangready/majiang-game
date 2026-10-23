using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Mahjong.LobbyService
{
    /// <summary>
    /// Terminal-room admission and reconciliation. A known public projection
    /// CLOSED/FINISHED (same ruleset) is monotonically persisted into the
    /// lobby room state and releases active placements. Missing/failed
    /// projections remain an explicit eventual-consistency degradation; they
    /// never close or reopen a room.
    /// </summary>
    public sealed partial class LobbyApplication
    {
        public async Task<LobbyResponse> ReconcileRoomAsync(
            LobbyAccount account,
            string? roomId,
            string requestId,
            CancellationToken cancellationToken)
        {
            if (account == null)
            {
                throw new ArgumentNullException(nameof(account));
            }

            string id = LobbyText.RequireId(roomId, "roomId");
            DateTimeOffset now = _clock.UtcNow;
            // Idempotency digest is derived ONLY from stable client request
            // semantics (operation + roomId). It must never include a mutable
            // snapshot/projection/terminal observation: the same successful
            // request must replay its original committed response even when a
            // concurrent reconcile has since moved the room to CLOSED. A new
            // observation is a new request key.
            string digest = LobbyDigest.Compute("room.reconcile.v1", id);
            var key = new LobbyIdempotencyKey(account.AccountId, requestId);
            LobbyResponse? replay = await _store.TryReplayAsync(key, digest, cancellationToken)
                .ConfigureAwait(false);
            if (replay != null)
            {
                return replay;
            }

            LobbyRoomSnapshot? snapshot = await _store.FindRoomSnapshotAsync(id, now, cancellationToken)
                .ConfigureAwait(false);
            if (snapshot == null)
            {
                throw new LobbyException(404, "ROOM_NOT_FOUND", "The room does not exist.");
            }

            PublicRoomProjection? projection = await ReadProjectionAsync(id, cancellationToken)
                .ConfigureAwait(false);
            bool terminal = snapshot.Room.State == LobbyRoomState.Closed ||
                            IsTerminalProjection(projection, snapshot.Room.RulesetVersion);

            return await _store.ExecuteIdempotentAsync(
                key,
                digest,
                now,
                async (tx, ct) =>
                {
                    LobbyRoom? room = await tx.FindRoomForUpdateAsync(id, ct).ConfigureAwait(false);
                    if (room == null)
                    {
                        throw new LobbyException(404, "ROOM_NOT_FOUND", "The room does not exist.");
                    }

                    if (terminal && room.State != LobbyRoomState.Closed)
                    {
                        LobbyRoom closed = room with
                        {
                            State = LobbyRoomState.Closed,
                            ClosedAt = now,
                            UpdatedAt = now
                        };
                        await tx.UpdateRoomAsync(closed, ct).ConfigureAwait(false);
                        await tx.CancelActiveAssignmentsForRoomAsync(id, now, ct).ConfigureAwait(false);
                        room = closed;
                    }

                    IReadOnlyList<LobbyAssignment> assignments = await tx.ListAssignmentsAsync(id, ct)
                        .ConfigureAwait(false);
                    RoomSummaryView summary = ToSummary(
                        new LobbyRoomSnapshot(
                            room,
                            assignments.Count(a => IsActiveForCount(a, now) && a.Role == LobbyRole.Player),
                            assignments.Count(a => IsActiveForCount(a, now) && a.Role == LobbyRole.Spectator)),
                        projection);
                    return new LobbyResponse(200, LobbyJson.Serialize(summary));
                },
                cancellationToken).ConfigureAwait(false);
        }

        private async Task<bool> EnsureTerminalRoomReconciledAsync(
            string roomId,
            uint rulesetVersion,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            PublicRoomProjection? projection = await ReadProjectionAsync(roomId, cancellationToken)
                .ConfigureAwait(false);
            if (!IsTerminalProjection(projection, rulesetVersion))
            {
                return false;
            }

            await _store.ExecuteAsync(
                now,
                async (tx, ct) =>
                {
                    LobbyRoom? room = await tx.FindRoomForUpdateAsync(roomId, ct).ConfigureAwait(false);
                    if (room == null || room.State == LobbyRoomState.Closed)
                    {
                        return 0;
                    }

                    await tx.UpdateRoomAsync(
                        room with
                        {
                            State = LobbyRoomState.Closed,
                            ClosedAt = now,
                            UpdatedAt = now
                        },
                        ct).ConfigureAwait(false);
                    await tx.CancelActiveAssignmentsForRoomAsync(roomId, now, ct).ConfigureAwait(false);
                    return 1;
                },
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        private async Task<PublicRoomProjection?> ReadProjectionAsync(
            string roomId,
            CancellationToken cancellationToken)
        {
            PublicRoomProjectionBatch batch = await ReadProjectionsSafeAsync(
                new[] { roomId },
                cancellationToken).ConfigureAwait(false);
            return batch.Items.FirstOrDefault(
                item => string.Equals(item.RoomId, roomId, StringComparison.Ordinal));
        }

        private static bool IsTerminalProjection(PublicRoomProjection? projection, uint rulesetVersion)
        {
            if (projection == null || projection.RulesetVersion != rulesetVersion)
            {
                return false;
            }

            return string.Equals(projection.Lifecycle, "CLOSED", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(projection.Lifecycle, "FINISHED", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsActiveForCount(LobbyAssignment assignment, DateTimeOffset now)
        {
            return (assignment.Status == LobbyAssignmentStatus.Reserved ||
                    assignment.Status == LobbyAssignmentStatus.Confirmed) &&
                   (!assignment.ExpiresAt.HasValue || assignment.ExpiresAt.Value > now);
        }

        private static LobbyResponse RoomClosedResponse()
        {
            return new LobbyResponse(
                409,
                LobbyJson.Serialize(new LobbyErrorView("ROOM_CLOSED", "The room is closed.")));
        }

        /// <summary>
        /// A terminal rejection is a committed receipt. The exception is raised
        /// only after the store transaction committed the receipt and the
        /// close/release side effects, so a retry can replay the original result
        /// instead of re-running or losing it.
        /// </summary>
        private static LobbyResponse ThrowIfCommittedRejection(LobbyResponse response)
        {
            if (response.StatusCode < 400)
            {
                return response;
            }

            LobbyErrorView error = LobbyJson.Deserialize<LobbyErrorView>(response.Json);
            throw new LobbyException(response.StatusCode, error.Code, error.Message);
        }
    }
}
