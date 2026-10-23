using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.LobbyService;
using Npgsql;
using Xunit;

namespace Mahjong.LobbyService.Tests
{
    /// <summary>
    /// Real PostgreSQL coverage for the P5.3 lobby store. These tests use the
    /// production Npgsql store and migration, not the in-memory contract store.
    /// They are skipped explicitly when MAHJONG_LOBBY_TEST_POSTGRES is absent;
    /// the runner scripts set it to a disposable loopback instance.
    /// </summary>
    [Collection(PostgresLobbyCollection.Name)]
    public sealed class PostgresLobbyStoreTests
    {
        [Fact]
        public async Task MigrationCanBeRepeatedAndCreatesTheNoDoubleBookingFences()
        {
            await PostgresLobbyTestDb.EnsureSchemaAsync();
            await PostgresLobbyTestDb.ResetAsync();

            LobbyOptions options = PostgresLobbyTestDb.CreateOptions(autoMigrate: true);
            await using (var first = new PostgresLobbyStore(PostgresLobbyTestDb.RequireConnectionString(), options))
            {
                await first.InitializeAsync(default);
                await first.InitializeAsync(default);
                Assert.True(await first.CheckReadyAsync(default));
            }

            await using (var second = new PostgresLobbyStore(PostgresLobbyTestDb.RequireConnectionString(), options))
            {
                await second.InitializeAsync(default);
            }

            long tables = await PostgresLobbyTestDb.CountAsync(
                "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'lobby'");
            Assert.Equal(8, (int)tables);

            long accountColumns = await PostgresLobbyTestDb.CountAsync(@"
SELECT count(*) FROM information_schema.columns
WHERE table_schema = 'lobby' AND table_name = 'account'
  AND column_name IN ('device_digest', 'bootstrap_hash')");
            Assert.Equal(2, (int)accountColumns);

            string[] definitions = await PostgresLobbyTestDb.QueryStringsAsync(@"
SELECT indexdef FROM pg_indexes
WHERE schemaname = 'lobby'
  AND indexname IN ('uq_assignment_active_account', 'uq_assignment_active_seat',
                    'uq_table_ticket_active_assignment', 'uq_match_active_account')
ORDER BY indexname");
            Assert.Equal(4, definitions.Length);
            string activeAccount = definitions.Single(d => d.Contains("uq_assignment_active_account", StringComparison.Ordinal));
            string activeSeat = definitions.Single(d => d.Contains("uq_assignment_active_seat", StringComparison.Ordinal));
            Assert.Contains("UNIQUE", activeAccount, StringComparison.Ordinal);
            Assert.Contains("RESERVED", activeAccount, StringComparison.Ordinal);
            Assert.Contains("CONFIRMED", activeAccount, StringComparison.Ordinal);
            Assert.Contains("UNIQUE", activeSeat, StringComparison.Ordinal);
            Assert.Contains("seat_id", activeSeat, StringComparison.Ordinal);

            PostgresEvidence.Record(
                "migration.repeated",
                ("migrationRuns", 3),
                ("lobbyTables", tables),
                ("activeAccountIndex", activeAccount),
                ("activeSeatIndex", activeSeat));
        }

        [Fact]
        public async Task CommittedReceiptReplaysOnANewStoreAndDifferentPayloadConflicts()
        {
            CreateRoomView created;
            LobbyResponse firstResponse;
            string accountId;
            string requestId = NewRequestId();
            var command = new CreateRoomCommand("PG replay room", 4, LobbyRoomVisibility.Public, true);

            await using (LobbyFixture first = await PostgresLobbyTestDb.NewIsolatedFixtureAsync())
            {
                GuestSessionView host = await first.CreateGuestAsync("pg-replay-host");
                accountId = host.AccountId;
                LobbySessionInfo session = await first.AuthenticateAsync(host);
                firstResponse = await first.Application.CreateRoomAsync(session.Account, command, requestId, default);
                Assert.Equal(201, firstResponse.StatusCode);
                Assert.False(firstResponse.Replayed);
                created = LobbyJson.Deserialize<CreateRoomView>(firstResponse.Json);
            }

            long roomsBefore = await PostgresLobbyTestDb.CountAsync(
                "SELECT count(*) FROM lobby.room WHERE room_id = @room",
                default,
                ("room", created.Room.RoomId));
            Assert.Equal(1, (int)roomsBefore);

            await using LobbyFixture second = await PostgresLobbyTestDb.NewFixtureAsync();
            LobbyAccount account = await LobbyTestAsync.RequireAccountAsync(second, accountId);
            LobbyResponse replay = await second.Application.CreateRoomAsync(account, command, requestId, default);
            Assert.Equal(201, replay.StatusCode);
            Assert.True(replay.Replayed);
            Assert.Equal(firstResponse.Json, replay.Json);
            Assert.Equal(created.Room.RoomId, LobbyJson.Deserialize<CreateRoomView>(replay.Json).Room.RoomId);

            long roomsAfter = await PostgresLobbyTestDb.CountAsync(
                "SELECT count(*) FROM lobby.room WHERE room_id = @room",
                default,
                ("room", created.Room.RoomId));
            Assert.Equal(1, (int)roomsAfter);
            long receipts = await PostgresLobbyTestDb.CountAsync(@"
SELECT count(*) FROM lobby.request_receipt
WHERE principal = @principal AND request_id = @request",
                default,
                ("principal", accountId),
                ("request", requestId));
            Assert.Equal(1, (int)receipts);

            LobbyException conflict = await Assert.ThrowsAsync<LobbyException>(
                () => second.Application.CreateRoomAsync(
                    account,
                    new CreateRoomCommand("PG replay room with different payload", 4, LobbyRoomVisibility.Public, true),
                    requestId,
                    default));
            Assert.Equal(409, conflict.StatusCode);
            Assert.Equal("REQUEST_ID_CONFLICT", conflict.Code);

            PostgresEvidence.Record(
                "receipt.replay",
                ("firstStatus", firstResponse.StatusCode),
                ("replayStatus", replay.StatusCode),
                ("replayed", replay.Replayed),
                ("sameBody", firstResponse.Json == replay.Json),
                ("roomRows", roomsAfter),
                ("conflict", conflict.Code));
        }

        [Fact]
        public async Task InjectedTransactionFailureRollsBackReceiptAndSideEffects()
        {
            await using LobbyFixture fixture = await PostgresLobbyTestDb.NewIsolatedFixtureAsync();
            ILobbyStore store = fixture.Application.Store;
            GuestSessionView host = await fixture.CreateGuestAsync("pg-rollback-host");
            LobbyAccount account = await LobbyTestAsync.RequireAccountAsync(fixture, host.AccountId);
            DateTimeOffset now = fixture.Clock.UtcNow;
            string requestId = NewRequestId();
            var key = new LobbyIdempotencyKey(account.AccountId, requestId);
            string digest = new string('a', 64);
            string roomId = NewId("room_rollback");
            string assignmentId = NewId("asg_rollback");
            var room = new LobbyRoom(
                roomId,
                "rollback room",
                LobbyRoomVisibility.Public,
                LobbyRoomState.Open,
                true,
                4,
                1u,
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

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.ExecuteIdempotentAsync(
                    key,
                    digest,
                    now,
                    async (tx, ct) =>
                    {
                        await tx.InsertRoomAsync(room, ct);
                        await tx.InsertAssignmentAsync(assignment, ct);
                        throw new InvalidOperationException("injected rollback after both inserts");
                    },
                    default));

            Assert.Null(await store.TryReplayAsync(key, digest, default));
            Assert.Equal(0, (int)await PostgresLobbyTestDb.CountAsync(
                "SELECT count(*) FROM lobby.room WHERE room_id = @room",
                default,
                ("room", roomId)));
            Assert.Equal(0, (int)await PostgresLobbyTestDb.CountAsync(
                "SELECT count(*) FROM lobby.assignment WHERE assignment_id = @assignment",
                default,
                ("assignment", assignmentId)));
            Assert.Equal(0, (int)await PostgresLobbyTestDb.CountAsync(@"
SELECT count(*) FROM lobby.request_receipt
WHERE principal = @principal AND request_id = @request",
                default,
                ("principal", account.AccountId),
                ("request", requestId)));

            LobbyResponse recovered = await store.ExecuteIdempotentAsync(
                key,
                digest,
                now,
                (tx, ct) => Task.FromResult(new LobbyResponse(200, "{\"ok\":true}")),
                default);
            Assert.Equal(200, recovered.StatusCode);
            LobbyResponse recoveredReplay = await store.TryReplayAsync(key, digest, default)
                                            ?? throw new InvalidOperationException("The reused key did not commit a receipt.");
            Assert.True(recoveredReplay.Replayed);
            Assert.Equal(recovered.Json, recoveredReplay.Json);

            CreateRoomView roomView = await fixture.CreateRoomAsync(host, "rollback execute room");
            GuestSessionView guest = await fixture.CreateGuestAsync("pg-rollback-guest");
            JoinRoomView joined = await fixture.JoinAsync(guest, roomView.Room.RoomId, preferSeat: 1);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.ExecuteAsync<LobbyAssignment>(
                    now,
                    async (tx, ct) =>
                    {
                        LobbyAssignment? current = await tx.FindAssignmentForUpdateAsync(
                            joined.Assignment.AssignmentId,
                            ct);
                        Assert.NotNull(current);
                        await tx.UpdateAssignmentAsync(
                            current! with
                            {
                                Status = LobbyAssignmentStatus.Confirmed,
                                TableConfirmedAt = now
                            },
                            ct);
                        throw new InvalidOperationException("injected execute rollback");
                    },
                    default));

            LobbyAssignment? afterRollback = await store.ExecuteAsync(
                now,
                (tx, ct) => tx.FindAssignmentForUpdateAsync(joined.Assignment.AssignmentId, ct),
                default);
            Assert.NotNull(afterRollback);
            Assert.Equal(LobbyAssignmentStatus.Reserved, afterRollback!.Status);
            Assert.Null(afterRollback.TableConfirmedAt);

            PostgresEvidence.Record(
                "transaction.rollback",
                ("receiptRowsAfterFailure", 0),
                ("roomRowsAfterFailure", 0),
                ("reusedKeyCommitted", true),
                ("assignmentStatusAfterExecuteRollback", afterRollback.Status.ToString()));
        }

        [Fact]
        public async Task ConcurrentCrossRoomJoinOnTwoStoresCreatesExactlyOneActiveAssignment()
        {
            await using LobbyFixture first = await PostgresLobbyTestDb.NewIsolatedFixtureAsync();
            GuestSessionView hostA = await first.CreateGuestAsync("pg-cross-host-a");
            CreateRoomView roomA = await first.CreateRoomAsync(hostA, "PG cross A");
            GuestSessionView hostB = await first.CreateGuestAsync("pg-cross-host-b");
            CreateRoomView roomB = await first.CreateRoomAsync(hostB, "PG cross B");
            await using LobbyFixture second = await PostgresLobbyTestDb.NewFixtureAsync();

            const int rounds = 3;
            for (int round = 1; round <= rounds; round++)
            {
                GuestSessionView guest = await first.CreateGuestAsync("pg-cross-guest-" + round.ToString(CultureInfo.InvariantCulture));
                LobbyAccount account = await LobbyTestAsync.RequireAccountAsync(first, guest.AccountId);
                Task<(LobbyResponse? Response, LobbyException? Error)> joinA = LobbyTestAsync.CaptureAsync(
                    () => first.Application.JoinRoomAsync(
                        account,
                        roomA.Room.RoomId,
                        new JoinRoomCommand(1),
                        NewRequestId(),
                        default));
                Task<(LobbyResponse? Response, LobbyException? Error)> joinB = LobbyTestAsync.CaptureAsync(
                    () => second.Application.JoinRoomAsync(
                        account,
                        roomB.Room.RoomId,
                        new JoinRoomCommand(1),
                        NewRequestId(),
                        default));
                (LobbyResponse? Response, LobbyException? Error)[] outcomes =
                    await Task.WhenAll(joinA, joinB);
                LobbyResponse winner = Assert.Single(outcomes.Where(o => o.Response != null).Select(o => o.Response!));
                LobbyException loser = Assert.Single(outcomes.Where(o => o.Error != null).Select(o => o.Error!));
                Assert.Equal(201, winner.StatusCode);
                Assert.Equal("ALREADY_ASSIGNED", loser.Code);

                long active = await PostgresLobbyTestDb.CountAsync(@"
SELECT count(*) FROM lobby.assignment
WHERE account_id = @account AND status IN ('RESERVED', 'CONFIRMED')",
                    default,
                    ("account", account.AccountId));
                Assert.Equal(1, (int)active);
                string? winningRoom = await PostgresLobbyTestDb.ScalarStringAsync(@"
SELECT room_id FROM lobby.assignment
WHERE account_id = @account AND status IN ('RESERVED', 'CONFIRMED')",
                    default,
                    ("account", account.AccountId));
                Assert.NotNull(winningRoom);
                JoinRoomView view = LobbyJson.Deserialize<JoinRoomView>(winner.Json);
                Assert.Equal(view.Assignment.RoomId, winningRoom);

                PostgresEvidence.Record(
                    "concurrency.crossRoomJoin",
                    ("round", round),
                    ("winnerRoom", winningRoom),
                    ("winnerSeat", view.Assignment.SeatId),
                    ("denial", loser.Code),
                    ("activeAssignments", active));
            }
        }

        [Fact]
        public async Task ConcurrentDuplicateTicketConsumptionCreatesOneConsumptionRow()
        {
            await using LobbyFixture first = await PostgresLobbyTestDb.NewIsolatedFixtureAsync();
            GuestSessionView host = await first.CreateGuestAsync("pg-consume-host");
            CreateRoomView room = await first.CreateRoomAsync(host, "PG consume room");
            GuestSessionView guest = await first.CreateGuestAsync("pg-consume-guest");
            JoinRoomView joined = await first.JoinAsync(guest, room.Room.RoomId, preferSeat: 1);
            TableTicketView ticket = await first.IssueTicketAsync(guest, joined.Assignment.AssignmentId);
            LobbyAccount account = await LobbyTestAsync.RequireAccountAsync(first, guest.AccountId);

            var expectation = new TableTicketExpectation(
                ticket.RoomId,
                ticket.AssignmentId,
                LobbyRole.Player,
                ticket.SeatId,
                account.AccountId);
            // Both calls use the same application/signing key, so the only
            // thing that can decide the race is the database row lock and the
            // jti consumption primary key. The store opens a distinct physical
            // connection per call.
            Task<(TicketConsumptionResult? Result, LobbyException? Error)> one =
                CaptureConsumptionAsync(() => first.Application.ConsumeTableTicketAsync(
                    ticket.Ticket,
                    expectation,
                    default));
            Task<(TicketConsumptionResult? Result, LobbyException? Error)> two =
                CaptureConsumptionAsync(() => first.Application.ConsumeTableTicketAsync(
                    ticket.Ticket,
                    expectation,
                    default));
            (TicketConsumptionResult? Result, LobbyException? Error)[] outcomes = await Task.WhenAll(one, two);
            TicketConsumptionResult consumed = Assert.Single(
                outcomes.Where(o => o.Result != null).Select(o => o.Result!));
            LobbyException rejection = Assert.Single(
                outcomes.Where(o => o.Error != null).Select(o => o.Error!));
            Assert.Equal("TICKET_ALREADY_CONSUMED", rejection.Code);
            Assert.Equal(joined.Assignment.AssignmentId, consumed.AssignmentId);

            long consumptionRows = await PostgresLobbyTestDb.CountAsync(
                "SELECT count(*) FROM lobby.table_ticket_consumption WHERE jti = @jti",
                default,
                ("jti", ticket.Jti));
            Assert.Equal(1, (int)consumptionRows);
            long consumedRows = await PostgresLobbyTestDb.CountAsync(
                "SELECT count(*) FROM lobby.table_ticket WHERE jti = @jti AND consumed_at IS NOT NULL",
                default,
                ("jti", ticket.Jti));
            Assert.Equal(1, (int)consumedRows);
            string? assignmentState = await PostgresLobbyTestDb.ScalarStringAsync(@"
SELECT status || '|' || coalesce(table_confirmed_at::text, '')
FROM lobby.assignment WHERE assignment_id = @assignment",
                default,
                ("assignment", joined.Assignment.AssignmentId));
            Assert.NotNull(assignmentState);
            Assert.StartsWith("CONFIRMED|", assignmentState!, StringComparison.Ordinal);
            Assert.False(assignmentState!.EndsWith("|", StringComparison.Ordinal));

            LobbyException third = await Assert.ThrowsAsync<LobbyException>(
                () => first.Application.ConsumeTableTicketAsync(ticket.Ticket, expectation, default));
            Assert.Equal("TICKET_ALREADY_CONSUMED", third.Code);
            Assert.Equal(1, (int)await PostgresLobbyTestDb.CountAsync(
                "SELECT count(*) FROM lobby.table_ticket_consumption WHERE jti = @jti",
                default,
                ("jti", ticket.Jti)));

            PostgresEvidence.Record(
                "concurrency.ticketConsumption",
                ("consumptionRows", consumptionRows),
                ("winnerAssignment", consumed.AssignmentId),
                ("loserCode", rejection.Code),
                ("thirdAttempt", third.Code));
        }

        [Fact]
        public async Task CrossConnectionRoomRowLockBlocksJoinUntilTheHolderReleases()
        {
            await using LobbyFixture fixture = await PostgresLobbyTestDb.NewIsolatedFixtureAsync();
            GuestSessionView host = await fixture.CreateGuestAsync("pg-lock-host");
            CreateRoomView room = await fixture.CreateRoomAsync(host, "PG lock room");
            GuestSessionView guest = await fixture.CreateGuestAsync("pg-lock-guest");
            LobbyAccount account = await LobbyTestAsync.RequireAccountAsync(fixture, guest.AccountId);

            await using var holder = new NpgsqlConnection(PostgresLobbyTestDb.RequireConnectionString());
            await holder.OpenAsync();
            await using NpgsqlTransaction holderTransaction = await holder.BeginTransactionAsync();
            await using (var lockCommand = new NpgsqlCommand(
                             "SELECT room_id FROM lobby.room WHERE room_id = @room FOR UPDATE",
                             holder,
                             holderTransaction))
            {
                lockCommand.Parameters.AddWithValue("room", room.Room.RoomId);
                await using NpgsqlDataReader reader = await lockCommand.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
            }

            var stopwatch = Stopwatch.StartNew();
            Task<LobbyResponse> join = fixture.Application.JoinRoomAsync(
                account,
                room.Room.RoomId,
                new JoinRoomCommand(1),
                NewRequestId(),
                default);

            bool observedWaiter = false;
            long ungrantedLocks = 0;
            for (int attempt = 0; attempt < 200 && !join.IsCompleted; attempt++)
            {
                ungrantedLocks = await PostgresLobbyTestDb.CountAsync(@"
SELECT count(*) FROM pg_locks WHERE NOT granted");
                if (ungrantedLocks > 0)
                {
                    observedWaiter = true;
                    break;
                }

                await Task.Delay(25);
            }

            Assert.True(observedWaiter, "The join never requested a row lock visible in pg_locks.");
            Assert.False(join.IsCompleted, "The join completed while another connection held the room row lock.");
            await Task.Delay(250);
            long heldMilliseconds = stopwatch.ElapsedMilliseconds;
            Assert.False(join.IsCompleted);

            await holderTransaction.RollbackAsync();
            LobbyResponse joined = await join.WaitAsync(TimeSpan.FromSeconds(15));
            stopwatch.Stop();
            Assert.Equal(201, joined.StatusCode);
            Assert.Equal(room.Room.RoomId, LobbyJson.Deserialize<JoinRoomView>(joined.Json).Assignment.RoomId);
            Assert.True(
                stopwatch.ElapsedMilliseconds >= heldMilliseconds,
                "The join elapsed time is shorter than the measured lock hold.");

            PostgresEvidence.Record(
                "concurrency.crossConnectionRowLock",
                ("observedUngrantedLocks", ungrantedLocks),
                ("blockedWhileHeld", true),
                ("heldMilliseconds", heldMilliseconds),
                ("totalMilliseconds", stopwatch.ElapsedMilliseconds));
        }

        [Theory]
        [InlineData("CLOSED")]
        [InlineData("FINISHED")]
        public async Task TerminalProjectionReleasesPlacementAndBlocksOldTickets(string lifecycle)
        {
            var projection = new MutableTestProjectionReader();
            await using LobbyFixture fixture = await PostgresLobbyTestDb.NewIsolatedFixtureAsync(projection);
            GuestSessionView host = await fixture.CreateGuestAsync("pg-terminal-host-" + lifecycle);
            CreateRoomView room = await fixture.CreateRoomAsync(host, "PG terminal " + lifecycle);
            GuestSessionView guest = await fixture.CreateGuestAsync("pg-terminal-guest-" + lifecycle);
            JoinRoomView joined = await fixture.JoinAsync(guest, room.Room.RoomId, preferSeat: 1);
            TableTicketView oldTicket = await fixture.IssueTicketAsync(guest, joined.Assignment.AssignmentId);
            projection.Set(room.Room.RoomId, lifecycle, fixture.Clock.UtcNow.AddSeconds(1));

            GuestSessionView lateJoiner = await fixture.CreateGuestAsync("pg-terminal-late-" + lifecycle);
            LobbyException denied = await Assert.ThrowsAsync<LobbyException>(
                () => fixture.JoinAsync(lateJoiner, room.Room.RoomId));
            Assert.Equal(409, denied.StatusCode);
            Assert.Equal("ROOM_CLOSED", denied.Code);

            string? roomState = await PostgresLobbyTestDb.ScalarStringAsync(@"
SELECT state || '|' || CASE WHEN closed_at IS NULL THEN 'no-close-time' ELSE 'closed' END
FROM lobby.room WHERE room_id = @room",
                default,
                ("room", room.Room.RoomId));
            Assert.Equal("CLOSED|closed", roomState);

            string? assignmentState = await PostgresLobbyTestDb.ScalarStringAsync(
                "SELECT status FROM lobby.assignment WHERE assignment_id = @assignment",
                default,
                ("assignment", joined.Assignment.AssignmentId));
            Assert.Equal("CANCELLED", assignmentState);

            LobbyException oldConsume = await Assert.ThrowsAsync<LobbyException>(
                () => fixture.ConsumeAsync(oldTicket, guest.AccountId));
            Assert.Equal(409, oldConsume.StatusCode);
            Assert.Equal("ROOM_CLOSED", oldConsume.Code);
            Assert.Equal(0, (int)await PostgresLobbyTestDb.CountAsync(
                "SELECT count(*) FROM lobby.table_ticket_consumption WHERE jti = @jti",
                default,
                ("jti", oldTicket.Jti)));
            Assert.Equal(0, (int)await PostgresLobbyTestDb.CountAsync(@"
SELECT count(*) FROM lobby.assignment
WHERE assignment_id = @assignment AND table_confirmed_at IS NOT NULL",
                default,
                ("assignment", joined.Assignment.AssignmentId)));

            GuestSessionView hostB = await fixture.CreateGuestAsync("pg-terminal-host-b-" + lifecycle);
            CreateRoomView roomB = await fixture.CreateRoomAsync(hostB, "PG terminal B " + lifecycle);
            JoinRoomView rejoined = await fixture.JoinAsync(guest, roomB.Room.RoomId);
            Assert.Equal(roomB.Room.RoomId, rejoined.Assignment.RoomId);

            LobbyResponse catalog = await fixture.Application.ListRoomsAsync(null, 100, default);
            RoomCatalogView page = LobbyJson.Deserialize<RoomCatalogView>(catalog.Json);
            Assert.DoesNotContain(page.Items, item => item.RoomId == room.Room.RoomId);

            PostgresEvidence.Record(
                "terminal.projection",
                ("lifecycle", lifecycle),
                ("roomState", roomState),
                ("assignmentState", assignmentState),
                ("newJoinDenial", denied.Code),
                ("oldTicketDenial", oldConsume.Code),
                ("releasedForAnotherRoom", rejoined.Assignment.RoomId));
        }

        [Fact]
        public async Task CompletedJoinSpectateAndIssueReceiptsReplayAfterTerminalOnANewStore()
        {
            var projection = new MutableTestProjectionReader();
            string joinKey = NewRequestId();
            string spectateKey = NewRequestId();
            string issueKey = NewRequestId();
            var joinCommand = new JoinRoomCommand(1);
            string joinBody;
            string spectateBody;
            string issueBody;
            string joinerId;
            string spectatorId;
            string roomId;
            string assignmentId;
            string jti;

            await using (LobbyFixture first = await PostgresLobbyTestDb.NewIsolatedFixtureAsync(projection))
            {
                GuestSessionView host = await first.CreateGuestAsync("pg-receipt-host");
                CreateRoomView room = await first.CreateRoomAsync(host, "PG receipt room");
                roomId = room.Room.RoomId;
                GuestSessionView joiner = await first.CreateGuestAsync("pg-receipt-joiner");
                GuestSessionView spectator = await first.CreateGuestAsync("pg-receipt-spectator");
                joinerId = joiner.AccountId;
                spectatorId = spectator.AccountId;
                LobbySessionInfo joinerSession = await first.AuthenticateAsync(joiner);
                LobbySessionInfo spectatorSession = await first.AuthenticateAsync(spectator);

                LobbyResponse joinFirst = await first.Application.JoinRoomAsync(
                    joinerSession.Account,
                    roomId,
                    joinCommand,
                    joinKey,
                    default);
                Assert.Equal(201, joinFirst.StatusCode);
                joinBody = joinFirst.Json;
                JoinRoomView joined = LobbyJson.Deserialize<JoinRoomView>(joinBody);
                assignmentId = joined.Assignment.AssignmentId;

                LobbyResponse spectateFirst = await first.Application.SpectateRoomAsync(
                    spectatorSession.Account,
                    roomId,
                    spectateKey,
                    default);
                Assert.Equal(201, spectateFirst.StatusCode);
                spectateBody = spectateFirst.Json;

                LobbyResponse issueFirst = await first.Application.IssueTableTicketAsync(
                    joinerSession.Account,
                    new IssueTableTicketCommand(assignmentId),
                    issueKey,
                    default);
                Assert.Equal(201, issueFirst.StatusCode);
                issueBody = issueFirst.Json;
                jti = LobbyJson.Deserialize<TableTicketView>(issueBody).Jti;

                projection.Set(roomId, "CLOSED", first.Clock.UtcNow.AddSeconds(1));
                LobbyResponse closed = await first.Application.ReconcileRoomAsync(
                    (await first.AuthenticateAsync(host)).Account,
                    roomId,
                    NewRequestId(),
                    default);
                Assert.Equal("CLOSED", LobbyJson.Deserialize<RoomSummaryView>(closed.Json).State);

                LobbyResponse joinReplay = await first.Application.JoinRoomAsync(
                    joinerSession.Account, roomId, joinCommand, joinKey, default);
                Assert.True(joinReplay.Replayed);
                Assert.Equal(joinBody, joinReplay.Json);
                LobbyResponse spectateReplay = await first.Application.SpectateRoomAsync(
                    spectatorSession.Account, roomId, spectateKey, default);
                Assert.True(spectateReplay.Replayed);
                Assert.Equal(spectateBody, spectateReplay.Json);
                LobbyResponse issueReplay = await first.Application.IssueTableTicketAsync(
                    joinerSession.Account,
                    new IssueTableTicketCommand(assignmentId),
                    issueKey,
                    default);
                Assert.True(issueReplay.Replayed);
                Assert.Equal(issueBody, issueReplay.Json);

                TableTicketView oldTicket = LobbyJson.Deserialize<TableTicketView>(issueBody);
                LobbyException consume = await Assert.ThrowsAsync<LobbyException>(
                    () => first.ConsumeAsync(oldTicket, joiner.AccountId));
                Assert.Equal("ROOM_CLOSED", consume.Code);
            }

            await using LobbyFixture second = await PostgresLobbyTestDb.NewFixtureAsync(projection);
            LobbyAccount joinerAccount = await LobbyTestAsync.RequireAccountAsync(second, joinerId);
            LobbyAccount spectatorAccount = await LobbyTestAsync.RequireAccountAsync(second, spectatorId);

            LobbyResponse joinAfterRestart = await second.Application.JoinRoomAsync(
                joinerAccount, roomId, joinCommand, joinKey, default);
            Assert.Equal(201, joinAfterRestart.StatusCode);
            Assert.True(joinAfterRestart.Replayed);
            Assert.Equal(joinBody, joinAfterRestart.Json);
            LobbyResponse spectateAfterRestart = await second.Application.SpectateRoomAsync(
                spectatorAccount, roomId, spectateKey, default);
            Assert.Equal(201, spectateAfterRestart.StatusCode);
            Assert.True(spectateAfterRestart.Replayed);
            Assert.Equal(spectateBody, spectateAfterRestart.Json);
            LobbyResponse issueAfterRestart = await second.Application.IssueTableTicketAsync(
                joinerAccount,
                new IssueTableTicketCommand(assignmentId),
                issueKey,
                default);
            Assert.Equal(201, issueAfterRestart.StatusCode);
            Assert.True(issueAfterRestart.Replayed);
            Assert.Equal(issueBody, issueAfterRestart.Json);

            long tickets = await PostgresLobbyTestDb.CountAsync(
                "SELECT count(*) FROM lobby.table_ticket WHERE assignment_id = @assignment",
                default,
                ("assignment", assignmentId));
            Assert.Equal(1, (int)tickets);
            Assert.Equal(1, (int)await PostgresLobbyTestDb.CountAsync(
                "SELECT count(*) FROM lobby.table_ticket WHERE jti = @jti",
                default,
                ("jti", jti)));
            long active = await PostgresLobbyTestDb.CountAsync(@"
SELECT count(*) FROM lobby.assignment
WHERE account_id = @account AND status IN ('RESERVED', 'CONFIRMED')",
                default,
                ("account", joinerId));
            Assert.Equal(0, (int)active);

            LobbyException newKey = await Assert.ThrowsAsync<LobbyException>(
                () => second.Application.JoinRoomAsync(
                    joinerAccount,
                    roomId,
                    joinCommand,
                    NewRequestId(),
                    default));
            Assert.Equal("ROOM_CLOSED", newKey.Code);

            PostgresEvidence.Record(
                "receipt.terminalReplay",
                ("joinReplayed", joinAfterRestart.Replayed),
                ("spectateReplayed", spectateAfterRestart.Replayed),
                ("issueReplayed", issueAfterRestart.Replayed),
                ("sameJoinBody", joinBody == joinAfterRestart.Json),
                ("sameSpectateBody", spectateBody == spectateAfterRestart.Json),
                ("sameIssueBody", issueBody == issueAfterRestart.Json),
                ("ticketRows", tickets),
                ("activeAssignments", active),
                ("newKeyDenial", newKey.Code));
        }

        [Fact]
        public async Task SixConcurrentJoinsFillAFourSeatRoomWithoutDuplicateSeats()
        {
            await using LobbyFixture fixture = await PostgresLobbyTestDb.NewIsolatedFixtureAsync();
            GuestSessionView host = await fixture.CreateGuestAsync("pg-fill-host");
            CreateRoomView room = await fixture.CreateRoomAsync(host, "PG fill room");
            var guests = new List<GuestSessionView>();
            for (int index = 0; index < 6; index++)
            {
                guests.Add(await fixture.CreateGuestAsync(
                    "pg-fill-guest-" + index.ToString(CultureInfo.InvariantCulture)));
            }

            var tasks = new List<Task<(LobbyResponse? Response, LobbyException? Error)>>();
            foreach (GuestSessionView guest in guests)
            {
                LobbyAccount account = await LobbyTestAsync.RequireAccountAsync(fixture, guest.AccountId);
                tasks.Add(LobbyTestAsync.CaptureAsync(
                    () => fixture.Application.JoinRoomAsync(
                        account,
                        room.Room.RoomId,
                        new JoinRoomCommand(null),
                        NewRequestId(),
                        default)));
            }

            (LobbyResponse? Response, LobbyException? Error)[] outcomes = await Task.WhenAll(tasks);
            LobbyResponse[] winners = outcomes.Where(o => o.Response != null).Select(o => o.Response!).ToArray();
            LobbyException[] losers = outcomes.Where(o => o.Error != null).Select(o => o.Error!).ToArray();
            Assert.Equal(3, winners.Length);
            Assert.Equal(3, losers.Length);
            Assert.All(losers, error => Assert.Equal("ROOM_FULL", error.Code));
            int[] seats = winners
                .Select(response => LobbyJson.Deserialize<JoinRoomView>(response.Json).Assignment.SeatId!.Value)
                .OrderBy(seat => seat)
                .ToArray();
            Assert.Equal(new[] { 1, 2, 3 }, seats);
            long activePlayers = await PostgresLobbyTestDb.CountAsync(@"
SELECT count(*) FROM lobby.assignment
WHERE room_id = @room AND role = 'PLAYER' AND status IN ('RESERVED', 'CONFIRMED')",
                default,
                ("room", room.Room.RoomId));
            Assert.Equal(4, (int)activePlayers);

            PostgresEvidence.Record(
                "concurrency.roomFill",
                ("attempts", outcomes.Length),
                ("winners", winners.Length),
                ("roomFullRejections", losers.Length),
                ("seats", string.Join(",", seats)),
                ("activePlayers", activePlayers));
        }

        [Fact]
        public async Task ExpiredReservationReleasesTheGlobalPlacementForAnotherRoom()
        {
            await using LobbyFixture fixture = await PostgresLobbyTestDb.NewIsolatedFixtureAsync();
            GuestSessionView hostA = await fixture.CreateGuestAsync("pg-expiry-host-a");
            CreateRoomView roomA = await fixture.CreateRoomAsync(hostA, "PG expiry A");
            GuestSessionView hostB = await fixture.CreateGuestAsync("pg-expiry-host-b");
            CreateRoomView roomB = await fixture.CreateRoomAsync(hostB, "PG expiry B");
            GuestSessionView guest = await fixture.CreateGuestAsync("pg-expiry-guest");

            JoinRoomView joinedA = await fixture.JoinAsync(guest, roomA.Room.RoomId, preferSeat: 1);
            Assert.Equal("RESERVED", joinedA.Assignment.Status);
            fixture.Clock.Advance(TimeSpan.FromSeconds(20));

            JoinRoomView joinedB = await fixture.JoinAsync(guest, roomB.Room.RoomId, preferSeat: 1);
            Assert.Equal(roomB.Room.RoomId, joinedB.Assignment.RoomId);
            string? expired = await PostgresLobbyTestDb.ScalarStringAsync(
                "SELECT status FROM lobby.assignment WHERE assignment_id = @assignment",
                default,
                ("assignment", joinedA.Assignment.AssignmentId));
            Assert.Equal("EXPIRED", expired);
            long active = await PostgresLobbyTestDb.CountAsync(@"
SELECT count(*) FROM lobby.assignment
WHERE account_id = @account AND status IN ('RESERVED', 'CONFIRMED')",
                default,
                ("account", guest.AccountId));
            Assert.Equal(1, (int)active);

            PostgresEvidence.Record(
                "recovery.expiredReservation",
                ("oldAssignment", expired),
                ("newRoom", joinedB.Assignment.RoomId),
                ("activeAssignments", active));
        }

        private static async Task<(TicketConsumptionResult? Result, LobbyException? Error)> CaptureConsumptionAsync(
            Func<Task<TicketConsumptionResult>> action)
        {
            try
            {
                return (await action().ConfigureAwait(false), null);
            }
            catch (LobbyException ex)
            {
                return (null, ex);
            }
        }

        private static string NewRequestId()
        {
            return "req_pg_" + Guid.NewGuid().ToString("N");
        }

        private static string NewId(string prefix)
        {
            return prefix + "_" + Guid.NewGuid().ToString("N");
        }
    }
}
