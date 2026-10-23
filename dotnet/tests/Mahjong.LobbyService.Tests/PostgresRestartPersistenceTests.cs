using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Mahjong.LobbyService;
using Xunit;

namespace Mahjong.LobbyService.Tests
{
    /// <summary>
    /// Cross-process / cross-database-restart persistence probe. The normal
    /// suite runs neither phase (explicit NOT_RUN skip); scripts/pg-lobby-verify
    /// runs the write phase, stops the PostgreSQL process with an immediate
    /// (crash-like) shutdown, starts it again, and then runs the read phase in a
    /// fresh OS process. Persisted lobby receipts and ticket consumption must
    /// still be readable and replayable from a brand new store instance.
    /// </summary>
    [Collection(PostgresLobbyCollection.Name)]
    public sealed class PostgresRestartPersistenceTests
    {
        private const string HandoffMarker = "p53-pg-restart-v1";

        [Fact]
        public async Task WritePhasePersistsConsumedTicketAndReplayableReceipts()
        {
            RequirePhase("write");
            string handoffPath = RequireHandoffPath();

            await using LobbyFixture fixture = await PostgresLobbyTestDb.NewIsolatedFixtureAsync();
            GuestSessionView host = await fixture.CreateGuestAsync("pg-restart-host");
            CreateRoomView room = await fixture.CreateRoomAsync(host, "PG restart room");
            GuestSessionView guest = await fixture.CreateGuestAsync("pg-restart-guest");
            LobbyAccount account = await LobbyTestAsync.RequireAccountAsync(fixture, guest.AccountId);

            // Join exactly once: a second join for the same account would be
            // (correctly) rejected as ALREADY_ASSIGNED. The receipt of this
            // single join is what the read phase replays after the restart.
            string joinKey = "req_restart_join_" + Guid.NewGuid().ToString("N");
            LobbyResponse joinResponse = await fixture.Application.JoinRoomAsync(
                account,
                room.Room.RoomId,
                new JoinRoomCommand(1),
                joinKey,
                default);
            if (joinResponse.StatusCode != 201)
            {
                throw new InvalidOperationException("The restart write phase could not join the room.");
            }

            JoinRoomView joinView = LobbyJson.Deserialize<JoinRoomView>(joinResponse.Json);
            string issueKey = "req_restart_issue_" + Guid.NewGuid().ToString("N");
            LobbyResponse issueResponse = await fixture.Application.IssueTableTicketAsync(
                account,
                new IssueTableTicketCommand(joinView.Assignment.AssignmentId),
                issueKey,
                default);
            if (issueResponse.StatusCode != 201)
            {
                throw new InvalidOperationException("The restart write phase could not issue a ticket.");
            }

            TableTicketView ticket = LobbyJson.Deserialize<TableTicketView>(issueResponse.Json);
            TicketConsumptionResult consumed = await fixture.ConsumeAsync(ticket, account.AccountId);
            Assert.Equal(joinView.Assignment.AssignmentId, consumed.AssignmentId);

            long consumptionRows = await PostgresLobbyTestDb.CountAsync(
                "SELECT count(*) FROM lobby.table_ticket_consumption WHERE jti = @jti",
                default,
                ("jti", ticket.Jti));
            Assert.Equal(1, (int)consumptionRows);
            string? startTime = await PostgresLobbyTestDb.ScalarStringAsync(
                "SELECT pg_postmaster_start_time()::text");
            Assert.NotNull(startTime);

            var handoff = new RestartHandoff
            {
                Marker = HandoffMarker,
                AccountId = account.AccountId,
                RoomId = room.Room.RoomId,
                AssignmentId = joinView.Assignment.AssignmentId,
                JoinRequestId = joinKey,
                JoinSeat = joinView.Assignment.SeatId ?? 1,
                JoinBody = joinResponse.Json,
                IssueRequestId = issueKey,
                IssueBody = issueResponse.Json,
                Jti = ticket.Jti,
                ConsumptionRows = consumptionRows,
                TicketRows = 1,
                WriterProcessId = Environment.ProcessId,
                PostmasterStartedAt = startTime!,
                DatabaseVersion = PostgresLobbyTestDb.ServerIdentity
            };
            string json = LobbyJson.Serialize(handoff);
            string tempPath = handoffPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, handoffPath, overwrite: true);

            PostgresEvidence.Record(
                "restart.write",
                ("writerProcessId", handoff.WriterProcessId),
                ("postmasterStartedAt", handoff.PostmasterStartedAt),
                ("accountId", handoff.AccountId),
                ("assignmentId", handoff.AssignmentId),
                ("jti", handoff.Jti),
                ("consumptionRows", consumptionRows));
        }

        [Fact]
        public async Task ReadPhaseAfterRealProcessRestartReplaysReceiptsAndSeesConsumption()
        {
            RequirePhase("read");
            string handoffPath = RequireHandoffPath();
            Assert.True(File.Exists(handoffPath), "The restart handoff file is missing; run the write phase first.");
            RestartHandoff handoff = LobbyJson.Deserialize<RestartHandoff>(File.ReadAllText(handoffPath));
            Assert.Equal(HandoffMarker, handoff.Marker);

            await using LobbyFixture fixture = await PostgresLobbyTestDb.NewFixtureAsync();
            LobbyAccount account = await LobbyTestAsync.RequireAccountAsync(fixture, handoff.AccountId);
            Assert.NotEqual(handoff.WriterProcessId, Environment.ProcessId);

            LobbyResponse joinReplay = await fixture.Application.JoinRoomAsync(
                account,
                handoff.RoomId,
                new JoinRoomCommand(handoff.JoinSeat),
                handoff.JoinRequestId,
                default);
            Assert.Equal(201, joinReplay.StatusCode);
            Assert.True(joinReplay.Replayed);
            Assert.Equal(handoff.JoinBody, joinReplay.Json);

            LobbyResponse issueReplay = await fixture.Application.IssueTableTicketAsync(
                account,
                new IssueTableTicketCommand(handoff.AssignmentId),
                handoff.IssueRequestId,
                default);
            Assert.Equal(201, issueReplay.StatusCode);
            Assert.True(issueReplay.Replayed);
            Assert.Equal(handoff.IssueBody, issueReplay.Json);
            Assert.Equal(handoff.Jti, LobbyJson.Deserialize<TableTicketView>(issueReplay.Json).Jti);

            string? assignmentState = await PostgresLobbyTestDb.ScalarStringAsync(@"
SELECT status || '|' ||
       CASE WHEN table_confirmed_at IS NULL THEN 'unconfirmed' ELSE 'table-confirmed' END || '|' ||
       CASE WHEN expires_at IS NULL THEN 'no-expiry' ELSE 'expiry-set' END
FROM lobby.assignment WHERE assignment_id = @assignment",
                default,
                ("assignment", handoff.AssignmentId));
            Assert.Equal("CONFIRMED|table-confirmed|no-expiry", assignmentState);

            long consumptionRows = await PostgresLobbyTestDb.CountAsync(
                "SELECT count(*) FROM lobby.table_ticket_consumption WHERE jti = @jti",
                default,
                ("jti", handoff.Jti));
            Assert.Equal(1, (int)consumptionRows);
            long consumedTicketRows = await PostgresLobbyTestDb.CountAsync(
                "SELECT count(*) FROM lobby.table_ticket WHERE jti = @jti AND consumed_at IS NOT NULL",
                default,
                ("jti", handoff.Jti));
            Assert.Equal(1, (int)consumedTicketRows);
            long ticketRows = await PostgresLobbyTestDb.CountAsync(
                "SELECT count(*) FROM lobby.table_ticket WHERE assignment_id = @assignment",
                default,
                ("assignment", handoff.AssignmentId));
            Assert.Equal(1, (int)ticketRows);

            string? currentStartTime = await PostgresLobbyTestDb.ScalarStringAsync(
                "SELECT pg_postmaster_start_time()::text");
            Assert.NotNull(currentStartTime);
            Assert.NotEqual(handoff.PostmasterStartedAt, currentStartTime);

            PostgresEvidence.Record(
                "restart.read",
                ("readerProcessId", Environment.ProcessId),
                ("writerProcessId", handoff.WriterProcessId),
                ("postmasterBeforeRestart", handoff.PostmasterStartedAt),
                ("postmasterAfterRestart", currentStartTime),
                ("joinReplayed", joinReplay.Replayed),
                ("issueReplayed", issueReplay.Replayed),
                ("consumptionRows", consumptionRows),
                ("assignmentState", assignmentState));
        }

        private static void RequirePhase(string expected)
        {
            string? phase = Environment.GetEnvironmentVariable(PostgresLobbyTestDb.RestartPhaseVariable);
            if (!string.Equals(phase, expected, StringComparison.OrdinalIgnoreCase))
            {
                Assert.Skip(
                    "NOT_RUN: restart phase '" + expected + "' only runs through scripts/pg-lobby-verify " +
                    "(it requires a real PostgreSQL process restart between phases).");
            }
        }

        private static string RequireHandoffPath()
        {
            string? path = Environment.GetEnvironmentVariable(PostgresLobbyTestDb.HandoffVariable);
            if (string.IsNullOrWhiteSpace(path))
            {
                Assert.Skip(
                    "NOT_RUN: " + PostgresLobbyTestDb.HandoffVariable + " is not set; the restart handoff path is required.");
            }

            return path!;
        }

        private sealed class RestartHandoff
        {
            public string Marker { get; set; } = string.Empty;

            public string AccountId { get; set; } = string.Empty;

            public string RoomId { get; set; } = string.Empty;

            public string AssignmentId { get; set; } = string.Empty;

            public string JoinRequestId { get; set; } = string.Empty;

            public int JoinSeat { get; set; }

            public string JoinBody { get; set; } = string.Empty;

            public string IssueRequestId { get; set; } = string.Empty;

            public string IssueBody { get; set; } = string.Empty;

            public string Jti { get; set; } = string.Empty;

            public long ConsumptionRows { get; set; }

            public long TicketRows { get; set; }

            public int WriterProcessId { get; set; }

            public string PostmasterStartedAt { get; set; } = string.Empty;

            public string DatabaseVersion { get; set; } = string.Empty;
        }
    }
}
