using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Mahjong.LobbyService
{
    public sealed partial class LobbyApplication
    {
        public async Task<LobbyResponse> CreateMatchTicketAsync(
            LobbyAccount account,
            CreateMatchCommand command,
            string requestId,
            CancellationToken cancellationToken)
        {
            if (command == null)
            {
                throw new LobbyException(400, "MALFORMED_REQUEST", "A match-ticket body is required.");
            }

            if (command.Mode != LobbyMatchMode.Casual && command.Mode != LobbyMatchMode.Ranked)
            {
                throw new LobbyException(400, "MALFORMED_REQUEST", "mode must be CASUAL or RANKED.");
            }

            if (command.RulesetVersion != Mahjong.Contracts.ProtocolConstants.RulesetVersion)
            {
                throw new LobbyException(400, "RULESET_VERSION_UNSUPPORTED", "Only rulesetVersion=1 is supported.");
            }

            string region = LobbyText.RequireRegion(command.Region);
            string payloadDigest = LobbyDigest.Compute(
                "match.create.v1",
                command.Mode.ToString(),
                command.RulesetVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                region);
            DateTimeOffset now = _clock.UtcNow;
            string matchTicketId = LobbyIds.NewPrefixed("mt_");
            var key = new LobbyIdempotencyKey(account.AccountId, requestId);
            LobbyResponse? replay = await _store.TryReplayAsync(key, payloadDigest, cancellationToken)
                .ConfigureAwait(false);
            if (replay != null)
            {
                return replay;
            }

            if (command.Mode == LobbyMatchMode.Ranked && !RankedMatchAllowed)
            {
                throw new LobbyException(
                    403,
                    "RANKED_MATCH_DISABLED",
                    "Guest ranked matchmaking is a pending product decision and is disabled.");
            }

            if (!_options.MatchingEnabled)
            {
                throw new LobbyException(503, "MATCHING_DISABLED", "Matchmaking is temporarily disabled.");
            }

            LobbyResponse response = await _store.ExecuteIdempotentAsync(
                key,
                payloadDigest,
                now,
                async (tx, ct) =>
                {
                    await tx.ExpireDueAsync(now, ct).ConfigureAwait(false);
                    await tx.LockAccountAsync(account.AccountId, ct).ConfigureAwait(false);
                    LobbyAssignment? activeAssignment = await tx.FindAnyActiveAssignmentForAccountAsync(
                        account.AccountId,
                        ct).ConfigureAwait(false);
                    if (activeAssignment != null)
                    {
                        throw new LobbyException(409, "ALREADY_ASSIGNED", "Leave the active room assignment before matchmaking.");
                    }

                    LobbyMatchTicket? active = await tx.FindActiveMatchTicketForAccountAsync(
                        account.AccountId,
                        ct).ConfigureAwait(false);
                    if (active != null)
                    {
                        throw new LobbyException(409, "ALREADY_QUEUED", "This account already has an active match ticket.");
                    }

                    var ticket = new LobbyMatchTicket(
                        matchTicketId,
                        account.AccountId,
                        command.Mode,
                        LobbyMatchStatus.Queued,
                        command.RulesetVersion,
                        region,
                        now,
                        now.AddSeconds(_options.QueueTtlSeconds),
                        null,
                        null,
                        null,
                        null,
                        null);
                    await tx.InsertMatchTicketAsync(ticket, ct).ConfigureAwait(false);
                    return new LobbyResponse(201, LobbyJson.Serialize(ToView(ticket, now)));
                },
                cancellationToken).ConfigureAwait(false);

            if (!response.Replayed)
            {
                try
                {
                    await RunMatchmakingOnceAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(
                        "Post-enqueue matchmaking pass failed; the ticket remains queued. Error={ErrorType}",
                        ex.GetType().Name);
                }
            }

            return response;
        }

        public async Task<LobbyResponse> GetMatchTicketAsync(
            LobbyAccount account,
            string? matchTicketId,
            CancellationToken cancellationToken)
        {
            string id = LobbyText.RequireId(matchTicketId, "matchTicketId");
            LobbyMatchTicket? ticket = await _store.FindMatchTicketAsync(id, cancellationToken).ConfigureAwait(false);
            if (ticket == null || !string.Equals(ticket.AccountId, account.AccountId, StringComparison.Ordinal))
            {
                throw new LobbyException(404, "MATCH_TICKET_NOT_FOUND", "The match ticket does not exist.");
            }

            return new LobbyResponse(200, LobbyJson.Serialize(ToView(ticket, _clock.UtcNow)));
        }

        public async Task<LobbyResponse> CancelMatchTicketAsync(
            LobbyAccount account,
            string? matchTicketId,
            string requestId,
            CancellationToken cancellationToken)
        {
            string id = LobbyText.RequireId(matchTicketId, "matchTicketId");
            string payloadDigest = LobbyDigest.Compute("match.cancel.v1", id);
            DateTimeOffset now = _clock.UtcNow;
            var key = new LobbyIdempotencyKey(account.AccountId, requestId);
            LobbyResponse? replay = await _store.TryReplayAsync(key, payloadDigest, cancellationToken)
                .ConfigureAwait(false);
            if (replay != null)
            {
                return replay;
            }

            return await _store.ExecuteIdempotentAsync(
                key,
                payloadDigest,
                now,
                async (tx, ct) =>
                {
                    await tx.ExpireDueAsync(now, ct).ConfigureAwait(false);
                    LobbyMatchTicket? snapshot = await tx.FindMatchTicketAsync(id, ct).ConfigureAwait(false);
                    if (snapshot == null || !string.Equals(snapshot.AccountId, account.AccountId, StringComparison.Ordinal))
                    {
                        throw new LobbyException(404, "MATCH_TICKET_NOT_FOUND", "The match ticket does not exist.");
                    }

                    // Lock assignment before the match ticket, matching the
                    // ticket-consumption lock order. Snapshot values are
                    // re-read under lock below.
                    if (snapshot.AssignmentId != null)
                    {
                        await tx.FindAssignmentForUpdateAsync(snapshot.AssignmentId, ct).ConfigureAwait(false);
                    }

                    LobbyMatchTicket? ticket = await tx.FindMatchTicketForUpdateAsync(id, ct).ConfigureAwait(false);
                    if (ticket == null || !string.Equals(ticket.AccountId, account.AccountId, StringComparison.Ordinal))
                    {
                        throw new LobbyException(404, "MATCH_TICKET_NOT_FOUND", "The match ticket does not exist.");
                    }

                    if (ticket.Status == LobbyMatchStatus.Confirmed)
                    {
                        throw new LobbyException(409, "MATCH_TICKET_CONFIRMED", "A confirmed match ticket cannot be cancelled.");
                    }

                    if (ticket.Status == LobbyMatchStatus.Cancelled || ticket.Status == LobbyMatchStatus.Expired)
                    {
                        return new LobbyResponse(200, LobbyJson.Serialize(ToView(ticket, now)));
                    }

                    if (ticket.AssignmentId != null)
                    {
                        LobbyAssignment? assignment = await tx.FindAssignmentForUpdateAsync(
                            ticket.AssignmentId,
                            ct).ConfigureAwait(false);
                        if (assignment != null &&
                            (assignment.Status == LobbyAssignmentStatus.Reserved ||
                             assignment.Status == LobbyAssignmentStatus.Confirmed))
                        {
                            await tx.UpdateAssignmentAsync(
                                assignment with
                                {
                                    Status = LobbyAssignmentStatus.Cancelled,
                                    UpdatedAt = now,
                                    ExpiresAt = null
                                },
                                ct).ConfigureAwait(false);
                        }
                    }

                    LobbyMatchTicket cancelled = ticket with
                    {
                        Status = LobbyMatchStatus.Cancelled,
                        CancelledAt = now
                    };
                    await tx.UpdateMatchTicketAsync(cancelled, ct).ConfigureAwait(false);
                    return new LobbyResponse(200, LobbyJson.Serialize(ToView(cancelled, now)));
                },
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// One explicit matchmaking pass. The temporary rule is FIFO within
        /// (mode, rulesetVersion, region), four seats per fresh room. It makes
        /// no skill/latency/fairness claim and is not a product decision.
        /// </summary>
        public async Task<int> RunMatchmakingOnceAsync(CancellationToken cancellationToken)
        {
            if (!_options.MatchingEnabled)
            {
                return 0;
            }

            DateTimeOffset now = _clock.UtcNow;
            return await _store.ExecuteAsync(
                now,
                async (tx, ct) =>
                {
                    await tx.ExpireDueAsync(now, ct).ConfigureAwait(false);
                    int matches = 0;
                    LobbyMatchMode[] modes = RankedMatchAllowed
                        ? new[] { LobbyMatchMode.Casual, LobbyMatchMode.Ranked }
                        : new[] { LobbyMatchMode.Casual };
                    foreach (LobbyMatchMode mode in modes)
                    {
                        IReadOnlyList<LobbyMatchTicket> queued = await tx.ListQueuedMatchTicketsForUpdateAsync(
                            mode,
                            Mahjong.Contracts.ProtocolConstants.RulesetVersion,
                            null,
                            256,
                            ct).ConfigureAwait(false);

                        foreach (IGrouping<string, LobbyMatchTicket> regionGroup in queued.GroupBy(
                                     t => t.Region,
                                     StringComparer.Ordinal))
                        {
                            List<LobbyMatchTicket> candidates = regionGroup
                                .OrderBy(t => t.CreatedAt)
                                .ThenBy(t => t.MatchTicketId, StringComparer.Ordinal)
                                .ToList();
                            int complete = candidates.Count - (candidates.Count % 4);
                            for (int offset = 0; offset < complete; offset += 4)
                            {
                                await CreateMatchRoomAsync(tx, candidates.Skip(offset).Take(4).ToList(), now, ct)
                                    .ConfigureAwait(false);
                                matches++;
                            }
                        }
                    }

                    return matches;
                },
                cancellationToken).ConfigureAwait(false);
        }

        public async Task<int> ExpireOnceAsync(CancellationToken cancellationToken)
        {
            return await RunMaintenanceOnceAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task CreateMatchRoomAsync(
            ILobbyTransaction tx,
            IReadOnlyList<LobbyMatchTicket> group,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            string roomId = LobbyIds.NewPrefixed("room_");
            DateTimeOffset reservationExpiry = now.AddSeconds(_options.ReservationTtlSeconds);
            var room = new LobbyRoom(
                roomId,
                "Match " + roomId.Substring(roomId.Length - 8),
                LobbyRoomVisibility.Public,
                LobbyRoomState.Open,
                true,
                4,
                Mahjong.Contracts.ProtocolConstants.RulesetVersion,
                group[0].AccountId,
                now,
                now,
                null);
            await tx.InsertRoomAsync(room, cancellationToken).ConfigureAwait(false);

            for (int seat = 0; seat < group.Count; seat++)
            {
                LobbyMatchTicket ticket = group[seat];
                string assignmentId = LobbyIds.NewPrefixed("asg_");
                var assignment = new LobbyAssignment(
                    assignmentId,
                    roomId,
                    ticket.AccountId,
                    LobbyRole.Player,
                    seat,
                    LobbyAssignmentStatus.Reserved,
                    now,
                    now,
                    reservationExpiry,
                    null,
                    null,
                    ticket.MatchTicketId);
                await tx.InsertAssignmentAsync(assignment, cancellationToken).ConfigureAwait(false);

                LobbyMatchTicket reserved = ticket with
                {
                    Status = LobbyMatchStatus.Reserved,
                    ReservedAt = now,
                    ExpiresAt = reservationExpiry,
                    RoomId = roomId,
                    AssignmentId = assignmentId
                };
                await tx.UpdateMatchTicketAsync(reserved, cancellationToken).ConfigureAwait(false);
            }
        }

        private static MatchTicketView ToView(LobbyMatchTicket ticket, DateTimeOffset now)
        {
            LobbyMatchStatus status = ticket.Status;
            if ((status == LobbyMatchStatus.Queued || status == LobbyMatchStatus.Reserved) &&
                ticket.ExpiresAt <= now)
            {
                status = LobbyMatchStatus.Expired;
            }

            return new MatchTicketView(
                ticket.MatchTicketId,
                ticket.Mode == LobbyMatchMode.Casual ? "CASUAL" : "RANKED",
                MatchStatus(status),
                ticket.RulesetVersion,
                ticket.Region,
                ticket.RoomId,
                ticket.AssignmentId,
                ticket.CreatedAt,
                ticket.ExpiresAt,
                ticket.ReservedAt,
                ticket.ConfirmedAt,
                ticket.CancelledAt);
        }
    }
}
