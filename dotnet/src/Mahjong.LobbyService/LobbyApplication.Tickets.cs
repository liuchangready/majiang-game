using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Mahjong.LobbyService
{
    public sealed partial class LobbyApplication
    {
        public async Task<LobbyResponse> IssueTableTicketAsync(
            LobbyAccount account,
            IssueTableTicketCommand command,
            string requestId,
            CancellationToken cancellationToken)
        {
            if (command == null)
            {
                throw new LobbyException(400, "MALFORMED_REQUEST", "A table-ticket body is required.");
            }

            string assignmentId = LobbyText.RequireId(command.AssignmentId, "assignmentId");
            string payloadDigest = LobbyDigest.Compute("table.ticket.issue.v1", assignmentId);
            DateTimeOffset now = _clock.UtcNow;
            DateTimeOffset issuedAt = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds());
            DateTimeOffset expiresAt = issuedAt.AddSeconds(_options.TicketTtlSeconds);
            var key = new LobbyIdempotencyKey(account.AccountId, requestId);

            return await _store.ExecuteIdempotentAsync(
                key,
                payloadDigest,
                now,
                async (tx, ct) =>
                {
                    await tx.ExpireDueAsync(now, ct).ConfigureAwait(false);
                    LobbyAssignment? assignment = await tx.FindAssignmentForUpdateAsync(
                        assignmentId,
                        ct).ConfigureAwait(false);
                    if (assignment == null ||
                        !string.Equals(assignment.AccountId, account.AccountId, StringComparison.Ordinal))
                    {
                        throw new LobbyException(404, "ASSIGNMENT_NOT_FOUND", "The assignment does not exist for this account.");
                    }

                    if (assignment.Status != LobbyAssignmentStatus.Reserved &&
                        assignment.Status != LobbyAssignmentStatus.Confirmed)
                    {
                        throw new LobbyException(409, "ASSIGNMENT_NOT_ACTIVE", "The assignment is no longer active.");
                    }

                    if (assignment.ExpiresAt.HasValue && assignment.ExpiresAt.Value <= now)
                    {
                        throw new LobbyException(409, "ASSIGNMENT_EXPIRED", "The seat reservation has expired.");
                    }

                    IReadOnlyList<LobbyAssignment> activeAssignments =
                        await tx.ListActiveAssignmentsForAccountAsync(assignment.AccountId, ct).ConfigureAwait(false);
                    if (activeAssignments.Any(a =>
                            !string.Equals(a.AssignmentId, assignment.AssignmentId, StringComparison.Ordinal)))
                    {
                        throw new LobbyException(
                            409,
                            "ACCOUNT_ALREADY_SEATED",
                            "This account has more than one active assignment; a ticket cannot be issued.");
                    }

                    LobbyTableTicketRecord? previous = await tx.FindActiveTableTicketForAssignmentAsync(
                        assignment.AssignmentId,
                        ct).ConfigureAwait(false);
                    if (previous != null)
                    {
                        await tx.UpdateTableTicketAsync(
                            previous with { SupersededAt = now },
                            ct).ConfigureAwait(false);
                    }

                    string jti = LobbyIds.NewPrefixed("jti_");
                    var claims = new TableTicketClaims(
                        _options.TicketIssuer,
                        assignment.AccountId,
                        _options.TicketAudience,
                        assignment.RoomId,
                        assignment.AssignmentId,
                        assignment.Role,
                        assignment.SeatId,
                        jti,
                        issuedAt,
                        expiresAt,
                        Mahjong.Contracts.ProtocolConstants.Major,
                        Mahjong.Contracts.ProtocolConstants.RulesetVersion,
                        Mahjong.Contracts.ProtocolConstants.ProjectionVersion);
                    IssuedTableTicket ticket = _tickets.Issue(claims);
                    var record = new LobbyTableTicketRecord(
                        ticket.Jti,
                        assignment.AssignmentId,
                        assignment.AccountId,
                        assignment.RoomId,
                        assignment.Role,
                        assignment.SeatId,
                        ticket.IssuedAt,
                        ticket.ExpiresAt,
                        ticket.Kid,
                        null,
                        null);
                    await tx.InsertTableTicketAsync(record, ct).ConfigureAwait(false);

                    var view = new TableTicketView(
                        ticket.Ticket,
                        ticket.Jti,
                        ticket.Kid,
                        ticket.RoomId,
                        ticket.AssignmentId,
                        RoleText(ticket.Role),
                        ticket.SeatId,
                        ticket.IssuedAt,
                        ticket.ExpiresAt,
                        ticket.ProtocolMajor,
                        ticket.RulesetVersion,
                        ticket.ProjectionVersion);
                    return new LobbyResponse(201, LobbyJson.Serialize(view));
                },
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Reference one-time consumption boundary for the future TableService
        /// handshake. The real WSS handshake is P5.1 and is NOT integrated here.
        /// Ticket check, assignment seat confirmation and consumption record are
        /// one store transaction; a signed JWT alone is never replay protection.
        /// </summary>
        public async Task<TicketConsumptionResult> ConsumeTableTicketAsync(
            string? compactTicket,
            TableTicketExpectation expectation,
            CancellationToken cancellationToken)
        {
            string compact = LobbyText.RequireTicket(compactTicket);
            if (expectation == null)
            {
                throw new ArgumentNullException(nameof(expectation));
            }

            DateTimeOffset now = _clock.UtcNow;
            TableTicketClaims claims;
            try
            {
                claims = _tickets.Validate(compact, expectation, now);
            }
            catch (TicketValidationException ex)
            {
                int status = ex.Code == "TICKET_CLAIM_MISMATCH" ||
                             ex.Code == "TICKET_ROLE_INVALID" ||
                             ex.Code == "TICKET_SEAT_INVALID" ||
                             ex.Code == "TICKET_VERSION_MISMATCH"
                    ? 403
                    : 401;
                throw new LobbyException(status, ex.Code, ex.Message);
            }

            return await _store.ExecuteAsync(
                now,
                async (tx, ct) =>
                {
                    await tx.ExpireDueAsync(now, ct).ConfigureAwait(false);
                    // Lock order is assignment -> table ticket -> match ticket
                    // in both issuance and consumption. This keeps the
                    // consume/issue/reconnect paths from taking locks in
                    // opposite order under PostgreSQL.
                    LobbyAssignment? assignment = await tx.FindAssignmentForUpdateAsync(
                        claims.AssignmentId,
                        ct).ConfigureAwait(false);
                    LobbyTableTicketRecord? record = await tx.FindTableTicketForUpdateAsync(
                        claims.Jti,
                        ct).ConfigureAwait(false);
                    if (record == null)
                    {
                        throw new LobbyException(401, "TICKET_UNKNOWN", "The table ticket was not issued by this lobby.");
                    }

                    if (record.ConsumedAt.HasValue)
                    {
                        throw new LobbyException(409, "TICKET_ALREADY_CONSUMED", "The table ticket was already consumed.");
                    }

                    if (record.SupersededAt.HasValue)
                    {
                        throw new LobbyException(409, "TICKET_SUPERSEDED", "A newer table ticket replaced this one.");
                    }

                    if (record.ExpiresAt <= now)
                    {
                        throw new LobbyException(401, "TICKET_EXPIRED", "The table ticket has expired.");
                    }

                    if (!string.Equals(record.AccountId, claims.AccountId, StringComparison.Ordinal) ||
                        !string.Equals(record.RoomId, claims.RoomId, StringComparison.Ordinal) ||
                        !string.Equals(record.AssignmentId, claims.AssignmentId, StringComparison.Ordinal) ||
                        record.Role != claims.Role ||
                        record.SeatId != claims.SeatId ||
                        record.ExpiresAt.ToUnixTimeSeconds() != claims.ExpiresAt.ToUnixTimeSeconds())
                    {
                        throw new LobbyException(401, "TICKET_RECORD_MISMATCH", "The table ticket does not match the persisted issuance record.");
                    }

                    if (assignment == null ||
                        !string.Equals(assignment.AccountId, claims.AccountId, StringComparison.Ordinal) ||
                        !string.Equals(assignment.RoomId, claims.RoomId, StringComparison.Ordinal) ||
                        assignment.Role != claims.Role ||
                        assignment.SeatId != claims.SeatId)
                    {
                        throw new LobbyException(401, "TICKET_ASSIGNMENT_MISMATCH", "The table ticket assignment no longer matches.");
                    }

                    if (assignment.Status != LobbyAssignmentStatus.Reserved &&
                        assignment.Status != LobbyAssignmentStatus.Confirmed)
                    {
                        throw new LobbyException(409, "ASSIGNMENT_NOT_ACTIVE", "The assignment is no longer active.");
                    }

                    if (assignment.ExpiresAt.HasValue && assignment.ExpiresAt.Value <= now)
                    {
                        throw new LobbyException(409, "ASSIGNMENT_EXPIRED", "The seat reservation has expired.");
                    }

                    IReadOnlyList<LobbyAssignment> activeAssignments =
                        await tx.ListActiveAssignmentsForAccountAsync(assignment.AccountId, ct).ConfigureAwait(false);
                    if (activeAssignments.Any(a =>
                            !string.Equals(a.AssignmentId, assignment.AssignmentId, StringComparison.Ordinal)))
                    {
                        throw new LobbyException(
                            409,
                            "ACCOUNT_ALREADY_SEATED",
                            "This account has more than one active assignment; ticket consumption is refused.");
                    }

                    if (assignment.Role == LobbyRole.Player && assignment.SeatId.HasValue)
                    {
                        IReadOnlyList<LobbyAssignment> assignments = await tx.ListAssignmentsAsync(
                            assignment.RoomId,
                            ct).ConfigureAwait(false);
                        bool seatConflict = assignments.Any(a =>
                            !string.Equals(a.AssignmentId, assignment.AssignmentId, StringComparison.Ordinal) &&
                            a.Role == LobbyRole.Player &&
                            a.SeatId == assignment.SeatId &&
                            (a.Status == LobbyAssignmentStatus.Reserved ||
                             a.Status == LobbyAssignmentStatus.Confirmed));
                        if (seatConflict)
                        {
                            throw new LobbyException(409, "SEAT_ALREADY_CONFIRMED", "Another assignment holds this seat.");
                        }
                    }

                    if (assignment.MatchTicketId != null)
                    {
                        LobbyMatchTicket? matchTicket = await tx.FindMatchTicketForUpdateAsync(
                            assignment.MatchTicketId,
                            ct).ConfigureAwait(false);
                        if (matchTicket == null)
                        {
                            throw new LobbyException(409, "MATCH_TICKET_NOT_FOUND", "The associated match ticket is missing.");
                        }

                        if (matchTicket.Status == LobbyMatchStatus.Cancelled ||
                            matchTicket.Status == LobbyMatchStatus.Expired ||
                            matchTicket.Status == LobbyMatchStatus.Queued)
                        {
                            throw new LobbyException(409, "MATCH_TICKET_NOT_ACTIVE", "The associated match ticket is no longer reserved.");
                        }

                        if (matchTicket.Status == LobbyMatchStatus.Reserved)
                        {
                            await tx.UpdateMatchTicketAsync(
                                matchTicket with
                                {
                                    Status = LobbyMatchStatus.Confirmed,
                                    ConfirmedAt = now
                                },
                                ct).ConfigureAwait(false);
                        }
                    }

                    await tx.UpdateAssignmentAsync(
                        assignment with
                        {
                            Status = LobbyAssignmentStatus.Confirmed,
                            ConfirmedAt = assignment.ConfirmedAt ?? now,
                            TableConfirmedAt = assignment.TableConfirmedAt ?? now,
                            ExpiresAt = null,
                            UpdatedAt = now
                        },
                        ct).ConfigureAwait(false);

                    await tx.InsertTicketConsumptionAsync(
                        new LobbyTicketConsumption(
                            claims.Jti,
                            assignment.AssignmentId,
                            assignment.AccountId,
                            assignment.RoomId,
                            assignment.SeatId,
                            now),
                        ct).ConfigureAwait(false);

                    await tx.UpdateTableTicketAsync(
                        record with { ConsumedAt = now },
                        ct).ConfigureAwait(false);

                    return new TicketConsumptionResult(
                        claims.Jti,
                        assignment.AccountId,
                        assignment.RoomId,
                        assignment.AssignmentId,
                        assignment.Role,
                        assignment.SeatId,
                        assignment.MatchTicketId,
                        now);
                },
                cancellationToken).ConfigureAwait(false);
        }
    }
}
