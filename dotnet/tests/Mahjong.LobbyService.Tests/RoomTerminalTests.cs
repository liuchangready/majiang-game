using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.LobbyService;
using Xunit;

namespace Mahjong.LobbyService.Tests
{
    public sealed class RoomTerminalTests
    {
        [Theory]
        [InlineData("CLOSED")]
        [InlineData("FINISHED")]
        public async Task ObservedTerminalProjectionRejectsNewJoinAndSpectate(string lifecycle)
        {
            var projection = new MutableProjectionReader();
            await using LobbyFixture fixture = LobbyFixture.Create(projections: projection);
            GuestSessionView host = await fixture.CreateGuestAsync("terminal-host-" + lifecycle);
            CreateRoomView room = await fixture.CreateRoomAsync(host);
            projection.Set(room.Room.RoomId, lifecycle, fixture.Clock.UtcNow.AddSeconds(1));

            GuestSessionView joiner = await fixture.CreateGuestAsync("terminal-join-" + lifecycle);
            LobbyException join = await Assert.ThrowsAsync<LobbyException>(
                () => fixture.JoinAsync(joiner, room.Room.RoomId));
            Assert.Equal(409, join.StatusCode);
            Assert.Equal("ROOM_CLOSED", join.Code);

            GuestSessionView spectator = await fixture.CreateGuestAsync("terminal-spectate-" + lifecycle);
            LobbyException spectate = await Assert.ThrowsAsync<LobbyException>(
                () => fixture.SpectateAsync(spectator, room.Room.RoomId));
            Assert.Equal("ROOM_CLOSED", spectate.Code);

            RoomSummaryView summary = LobbyJson.Deserialize<RoomSummaryView>(
                (await fixture.Application.GetRoomAsync(room.Room.RoomId, default)).Json);
            Assert.Equal("CLOSED", summary.State);
            RoomCatalogView catalog = LobbyJson.Deserialize<RoomCatalogView>(
                (await fixture.Application.ListRoomsAsync(null, 20, default)).Json);
            Assert.Empty(catalog.Items);
        }

        [Theory]
        [InlineData("CLOSED")]
        [InlineData("FINISHED")]
        public async Task TicketIssuedBeforeTerminalCannotBeConsumedAfterTerminal(string lifecycle)
        {
            var projection = new MutableProjectionReader();
            await using LobbyFixture fixture = LobbyFixture.Create(projections: projection);
            GuestSessionView host = await fixture.CreateGuestAsync("terminal-ticket-host-" + lifecycle);
            CreateRoomView room = await fixture.CreateRoomAsync(host);
            GuestSessionView guest = await fixture.CreateGuestAsync("terminal-ticket-guest-" + lifecycle);
            JoinRoomView joined = await fixture.JoinAsync(guest, room.Room.RoomId);
            TableTicketView ticket = await fixture.IssueTicketAsync(guest, joined.Assignment.AssignmentId);

            projection.Set(room.Room.RoomId, lifecycle, fixture.Clock.UtcNow.AddSeconds(1));
            LobbyException consume = await Assert.ThrowsAsync<LobbyException>(
                () => fixture.ConsumeAsync(ticket, guest.AccountId));
            Assert.Equal(409, consume.StatusCode);
            Assert.Equal("ROOM_CLOSED", consume.Code);

            LobbyAssignment? assignment = await fixture.Application.Store.ExecuteAsync(
                fixture.Clock.UtcNow,
                (tx, ct) => tx.FindAssignmentForUpdateAsync(joined.Assignment.AssignmentId, ct),
                default);
            Assert.NotNull(assignment);
            Assert.Equal(LobbyAssignmentStatus.Cancelled, assignment!.Status);
            Assert.Null(assignment.TableConfirmedAt);

            LobbyTableTicketRecord? record = await fixture.Application.Store.ExecuteAsync(
                fixture.Clock.UtcNow,
                (tx, ct) => tx.FindTableTicketForUpdateAsync(ticket.Jti, ct),
                default);
            Assert.NotNull(record);
            Assert.Null(record!.ConsumedAt);

            GuestSessionView hostB = await fixture.CreateGuestAsync("terminal-ticket-host-b-" + lifecycle);
            CreateRoomView roomB = await fixture.CreateRoomAsync(hostB, "B");
            JoinRoomView rejoined = await fixture.JoinAsync(guest, roomB.Room.RoomId);
            Assert.Equal(roomB.Room.RoomId, rejoined.Assignment.RoomId);
        }

        [Fact]
        public async Task TicketIssuanceIsRejectedAfterTerminalProjection()
        {
            var projection = new MutableProjectionReader();
            await using LobbyFixture fixture = LobbyFixture.Create(projections: projection);
            GuestSessionView host = await fixture.CreateGuestAsync("terminal-issue-host");
            CreateRoomView room = await fixture.CreateRoomAsync(host);
            GuestSessionView guest = await fixture.CreateGuestAsync("terminal-issue-guest");
            JoinRoomView joined = await fixture.JoinAsync(guest, room.Room.RoomId);

            projection.Set(room.Room.RoomId, "CLOSED", fixture.Clock.UtcNow.AddSeconds(1));
            LobbyException issue = await Assert.ThrowsAsync<LobbyException>(
                () => fixture.IssueTicketAsync(guest, joined.Assignment.AssignmentId));
            Assert.Equal(409, issue.StatusCode);
            Assert.Equal("ROOM_CLOSED", issue.Code);

            LobbyAssignment? assignment = await fixture.Application.Store.ExecuteAsync(
                fixture.Clock.UtcNow,
                (tx, ct) => tx.FindAssignmentForUpdateAsync(joined.Assignment.AssignmentId, ct),
                default);
            Assert.Equal(LobbyAssignmentStatus.Cancelled, assignment!.Status);
        }

        [Fact]
        public async Task ExplicitReconcileReleasesAccountAndCannotBeReopenedByOldProjection()
        {
            var projection = new MutableProjectionReader();
            await using LobbyFixture fixture = LobbyFixture.Create(projections: projection);
            GuestSessionView host = await fixture.CreateGuestAsync("terminal-reconcile-host");
            CreateRoomView room = await fixture.CreateRoomAsync(host);
            GuestSessionView guest = await fixture.CreateGuestAsync("terminal-reconcile-guest");
            JoinRoomView joined = await fixture.JoinAsync(guest, room.Room.RoomId);
            TableTicketView oldTicket = await fixture.IssueTicketAsync(guest, joined.Assignment.AssignmentId);

            projection.Set(room.Room.RoomId, "CLOSED", fixture.Clock.UtcNow.AddSeconds(1));
            LobbySessionInfo guestSession = await fixture.AuthenticateAsync(guest);
            string reconcileRequest = LobbyFixture.NewRequestId();
            LobbyResponse first = await fixture.Application.ReconcileRoomAsync(
                guestSession.Account,
                room.Room.RoomId,
                reconcileRequest,
                default);
            Assert.Equal(200, first.StatusCode);
            Assert.False(first.Replayed);
            RoomSummaryView closed = LobbyJson.Deserialize<RoomSummaryView>(first.Json);
            Assert.Equal("CLOSED", closed.State);
            Assert.Equal(0, closed.SeatsTaken);

            LobbyResponse second = await fixture.Application.ReconcileRoomAsync(
                guestSession.Account,
                room.Room.RoomId,
                reconcileRequest,
                default);
            Assert.Equal(200, second.StatusCode);
            Assert.True(second.Replayed);
            Assert.Equal(first.Json, second.Json);

            projection.Remove(room.Room.RoomId);
            LobbyResponse missingReplay = await fixture.Application.ReconcileRoomAsync(
                guestSession.Account,
                room.Room.RoomId,
                reconcileRequest,
                default);
            Assert.True(missingReplay.Replayed);
            Assert.Equal(first.Json, missingReplay.Json);

            LobbyResponse third = await fixture.Application.ReconcileRoomAsync(
                guestSession.Account,
                room.Room.RoomId,
                LobbyFixture.NewRequestId(),
                default);
            Assert.Equal(200, third.StatusCode);
            Assert.Equal("CLOSED", LobbyJson.Deserialize<RoomSummaryView>(third.Json).State);

            // A delayed/old OPEN or PLAYING projection must not reopen the room.
            projection.Set(room.Room.RoomId, "PLAYING", fixture.Clock.UtcNow.AddSeconds(2));
            LobbyException joinClosed = await Assert.ThrowsAsync<LobbyException>(
                () => fixture.JoinAsync(guest, room.Room.RoomId));
            Assert.Equal("ROOM_CLOSED", joinClosed.Code);

            LobbyException oldConsume = await Assert.ThrowsAsync<LobbyException>(
                () => fixture.ConsumeAsync(oldTicket, guest.AccountId));
            Assert.Equal("ROOM_CLOSED", oldConsume.Code);

            LobbyException newIssue = await Assert.ThrowsAsync<LobbyException>(
                () => fixture.IssueTicketAsync(guest, joined.Assignment.AssignmentId));
            Assert.Equal("ROOM_CLOSED", newIssue.Code);

            GuestSessionView hostB = await fixture.CreateGuestAsync("terminal-reconcile-host-b");
            CreateRoomView roomB = await fixture.CreateRoomAsync(hostB, "B");
            JoinRoomView rejoined = await fixture.JoinAsync(guest, roomB.Room.RoomId);
            Assert.Equal(roomB.Room.RoomId, rejoined.Assignment.RoomId);
        }

        [Fact]
        public async Task TerminalReconciliationReleasesMatchReservationForAnotherRoom()
        {
            var projection = new MutableProjectionReader();
            await using LobbyFixture fixture = LobbyFixture.Create(projections: projection);
            var guests = new List<GuestSessionView>();
            var tickets = new List<MatchTicketView>();
            for (int i = 0; i < 4; i++)
            {
                GuestSessionView guest = await fixture.CreateGuestAsync("terminal-match-" + i);
                guests.Add(guest);
                tickets.Add(await fixture.CreateMatchAsync(guest, region: "terminal-match"));
            }

            MatchTicketView reserved = await fixture.GetMatchAsync(guests[0], tickets[0].MatchTicketId);
            string matchRoomId = reserved.RoomId!;
            projection.Set(matchRoomId, "CLOSED", fixture.Clock.UtcNow.AddSeconds(1));
            LobbySessionInfo session = await fixture.AuthenticateAsync(guests[0]);
            LobbyResponse reconciled = await fixture.Application.ReconcileRoomAsync(
                session.Account,
                matchRoomId,
                LobbyFixture.NewRequestId(),
                default);
            Assert.Equal(200, reconciled.StatusCode);
            MatchTicketView cancelled = await fixture.GetMatchAsync(guests[0], tickets[0].MatchTicketId);
            Assert.Equal("CANCELLED", cancelled.Status);

            LobbyException issue = await Assert.ThrowsAsync<LobbyException>(
                () => fixture.IssueTicketAsync(guests[0], reserved.AssignmentId!));
            Assert.Equal("ROOM_CLOSED", issue.Code);

            GuestSessionView hostB = await fixture.CreateGuestAsync("terminal-match-host-b");
            CreateRoomView roomB = await fixture.CreateRoomAsync(hostB, "B");
            JoinRoomView rejoined = await fixture.JoinAsync(guests[0], roomB.Room.RoomId);
            Assert.Equal(roomB.Room.RoomId, rejoined.Assignment.RoomId);
        }

        [Fact]
        public async Task MissingProjectionRemainsAnExplicitAdmissionDegradation()
        {
            await using LobbyFixture fixture = LobbyFixture.Create();
            GuestSessionView host = await fixture.CreateGuestAsync("terminal-missing-host");
            CreateRoomView room = await fixture.CreateRoomAsync(host);
            GuestSessionView guest = await fixture.CreateGuestAsync("terminal-missing-guest");

            JoinRoomView joined = await fixture.JoinAsync(guest, room.Room.RoomId);
            Assert.Equal(room.Room.RoomId, joined.Assignment.RoomId);
            RoomSummaryView summary = LobbyJson.Deserialize<RoomSummaryView>(
                (await fixture.Application.GetRoomAsync(room.Room.RoomId, default)).Json);
            Assert.True(summary.ProjectionStale);
            Assert.Equal("OPEN", summary.State);
        }

        [Fact]
        public async Task TerminalProjectionForAnotherRulesetDoesNotCloseTheRoom()
        {
            var projection = new MutableProjectionReader();
            await using LobbyFixture fixture = LobbyFixture.Create(projections: projection);
            GuestSessionView host = await fixture.CreateGuestAsync("terminal-ruleset-host");
            CreateRoomView room = await fixture.CreateRoomAsync(host);
            projection.Set(room.Room.RoomId, "CLOSED", fixture.Clock.UtcNow.AddSeconds(1), rulesetVersion: 2);

            GuestSessionView guest = await fixture.CreateGuestAsync("terminal-ruleset-guest");
            JoinRoomView joined = await fixture.JoinAsync(guest, room.Room.RoomId);
            Assert.Equal(room.Room.RoomId, joined.Assignment.RoomId);
        }

        [Theory]
        [InlineData("CLOSED")]
        [InlineData("FINISHED")]
        public async Task CompletedJoinSpectateAndIssueReceiptsReplayAfterTerminal(string lifecycle)
        {
            var projection = new MutableProjectionReader();
            await using LobbyFixture fixture = LobbyFixture.Create(projections: projection);
            GuestSessionView host = await fixture.CreateGuestAsync("receipt-host-" + lifecycle);
            CreateRoomView room = await fixture.CreateRoomAsync(host);
            GuestSessionView joiner = await fixture.CreateGuestAsync("receipt-joiner-" + lifecycle);
            GuestSessionView spectator = await fixture.CreateGuestAsync("receipt-spectator-" + lifecycle);
            LobbySessionInfo joinerSession = await fixture.AuthenticateAsync(joiner);
            LobbySessionInfo spectatorSession = await fixture.AuthenticateAsync(spectator);

            string joinKey = LobbyFixture.NewRequestId();
            string spectateKey = LobbyFixture.NewRequestId();
            string issueKey = LobbyFixture.NewRequestId();
            var joinCommand = new JoinRoomCommand(1);

            LobbyResponse joinFirst = await fixture.Application.JoinRoomAsync(
                joinerSession.Account,
                room.Room.RoomId,
                joinCommand,
                joinKey,
                default);
            Assert.Equal(201, joinFirst.StatusCode);
            JoinRoomView joined = LobbyJson.Deserialize<JoinRoomView>(joinFirst.Json);

            LobbyResponse spectateFirst = await fixture.Application.SpectateRoomAsync(
                spectatorSession.Account,
                room.Room.RoomId,
                spectateKey,
                default);
            Assert.Equal(201, spectateFirst.StatusCode);

            LobbyResponse issueFirst = await fixture.Application.IssueTableTicketAsync(
                joinerSession.Account,
                new IssueTableTicketCommand(joined.Assignment.AssignmentId),
                issueKey,
                default);
            Assert.Equal(201, issueFirst.StatusCode);
            TableTicketView issued = LobbyJson.Deserialize<TableTicketView>(issueFirst.Json);

            projection.Set(room.Room.RoomId, lifecycle, fixture.Clock.UtcNow.AddSeconds(1));
            LobbySessionInfo hostSession = await fixture.AuthenticateAsync(host);
            LobbyResponse closed = await fixture.Application.ReconcileRoomAsync(
                hostSession.Account,
                room.Room.RoomId,
                LobbyFixture.NewRequestId(),
                default);
            Assert.Equal("CLOSED", LobbyJson.Deserialize<RoomSummaryView>(closed.Json).State);

            LobbyResponse joinReplay = await fixture.Application.JoinRoomAsync(
                joinerSession.Account,
                room.Room.RoomId,
                joinCommand,
                joinKey,
                default);
            Assert.Equal(201, joinReplay.StatusCode);
            Assert.True(joinReplay.Replayed);
            Assert.Equal(joinFirst.Json, joinReplay.Json);

            LobbyResponse spectateReplay = await fixture.Application.SpectateRoomAsync(
                spectatorSession.Account,
                room.Room.RoomId,
                spectateKey,
                default);
            Assert.Equal(201, spectateReplay.StatusCode);
            Assert.True(spectateReplay.Replayed);
            Assert.Equal(spectateFirst.Json, spectateReplay.Json);

            LobbyResponse issueReplay = await fixture.Application.IssueTableTicketAsync(
                joinerSession.Account,
                new IssueTableTicketCommand(joined.Assignment.AssignmentId),
                issueKey,
                default);
            Assert.Equal(201, issueReplay.StatusCode);
            Assert.True(issueReplay.Replayed);
            Assert.Equal(issueFirst.Json, issueReplay.Json);
            Assert.Equal(issued.Jti, LobbyJson.Deserialize<TableTicketView>(issueReplay.Json).Jti);

            IReadOnlyList<LobbyAssignment> active = await fixture.Application.Store.ExecuteAsync(
                fixture.Clock.UtcNow,
                (tx, ct) => tx.ListActiveAssignmentsForAccountAsync(joinerSession.Account.AccountId, ct),
                default);
            Assert.Empty(active);

            LobbyException consume = await Assert.ThrowsAsync<LobbyException>(
                () => fixture.ConsumeAsync(issued, joiner.AccountId));
            Assert.Equal("ROOM_CLOSED", consume.Code);

            LobbyException newJoin = await Assert.ThrowsAsync<LobbyException>(
                () => fixture.Application.JoinRoomAsync(
                    joinerSession.Account,
                    room.Room.RoomId,
                    joinCommand,
                    LobbyFixture.NewRequestId(),
                    default));
            Assert.Equal("ROOM_CLOSED", newJoin.Code);
            LobbyException newSpectate = await Assert.ThrowsAsync<LobbyException>(
                () => fixture.Application.SpectateRoomAsync(
                    spectatorSession.Account,
                    room.Room.RoomId,
                    LobbyFixture.NewRequestId(),
                    default));
            Assert.Equal("ROOM_CLOSED", newSpectate.Code);
            LobbyException newIssue = await Assert.ThrowsAsync<LobbyException>(
                () => fixture.Application.IssueTableTicketAsync(
                    joinerSession.Account,
                    new IssueTableTicketCommand(joined.Assignment.AssignmentId),
                    LobbyFixture.NewRequestId(),
                    default));
            Assert.Equal("ROOM_CLOSED", newIssue.Code);
        }

        [Fact]
        public async Task UsedJoinKeyForDifferentRoomConflictsBeforeStateChecks()
        {
            var projection = new MutableProjectionReader();
            await using LobbyFixture fixture = LobbyFixture.Create(projections: projection);
            GuestSessionView hostA = await fixture.CreateGuestAsync("used-key-host-a");
            CreateRoomView roomA = await fixture.CreateRoomAsync(hostA, "A");
            GuestSessionView hostB = await fixture.CreateGuestAsync("used-key-host-b");
            CreateRoomView roomB = await fixture.CreateRoomAsync(hostB, "B");
            GuestSessionView guest = await fixture.CreateGuestAsync("used-key-guest");
            LobbySessionInfo session = await fixture.AuthenticateAsync(guest);
            string key = LobbyFixture.NewRequestId();

            LobbyResponse first = await fixture.Application.JoinRoomAsync(
                session.Account,
                roomA.Room.RoomId,
                new JoinRoomCommand(1),
                key,
                default);
            Assert.Equal(201, first.StatusCode);

            LobbyException conflict = await Assert.ThrowsAsync<LobbyException>(
                () => fixture.Application.JoinRoomAsync(
                    session.Account,
                    roomB.Room.RoomId,
                    new JoinRoomCommand(1),
                    key,
                    default));
            Assert.Equal(409, conflict.StatusCode);
            Assert.Equal("REQUEST_ID_CONFLICT", conflict.Code);

            LobbyException payloadConflict = await Assert.ThrowsAsync<LobbyException>(
                () => fixture.Application.JoinRoomAsync(
                    session.Account,
                    roomA.Room.RoomId,
                    new JoinRoomCommand(2),
                    key,
                    default));
            Assert.Equal(409, payloadConflict.StatusCode);
            Assert.Equal("REQUEST_ID_CONFLICT", payloadConflict.Code);
        }

        [Fact]
        public async Task UsedIssueKeyForDifferentAssignmentConflictsBeforeStateChecks()
        {
            var projection = new MutableProjectionReader();
            await using LobbyFixture fixture = LobbyFixture.Create(projections: projection);
            GuestSessionView hostA = await fixture.CreateGuestAsync("used-issue-host-a");
            CreateRoomView roomA = await fixture.CreateRoomAsync(hostA, "A");
            GuestSessionView hostB = await fixture.CreateGuestAsync("used-issue-host-b");
            CreateRoomView roomB = await fixture.CreateRoomAsync(hostB, "B");
            GuestSessionView guest = await fixture.CreateGuestAsync("used-issue-guest");
            LobbySessionInfo session = await fixture.AuthenticateAsync(guest);
            JoinRoomView joined = await fixture.JoinAsync(guest, roomA.Room.RoomId);
            string key = LobbyFixture.NewRequestId();

            LobbyResponse first = await fixture.Application.IssueTableTicketAsync(
                session.Account,
                new IssueTableTicketCommand(joined.Assignment.AssignmentId),
                key,
                default);
            Assert.Equal(201, first.StatusCode);

            LobbyException conflict = await Assert.ThrowsAsync<LobbyException>(
                () => fixture.Application.IssueTableTicketAsync(
                    session.Account,
                    new IssueTableTicketCommand(roomB.Assignment.AssignmentId),
                    key,
                    default));
            Assert.Equal(409, conflict.StatusCode);
            Assert.Equal("REQUEST_ID_CONFLICT", conflict.Code);
        }

        [Fact]
        public async Task ConcurrentTerminalObservationDoesNotChangeReconcileReceiptDigest()
        {
            var projection = new PausingProjectionReader();
            await using LobbyFixture fixture = LobbyFixture.Create(projections: projection);
            GuestSessionView host = await fixture.CreateGuestAsync("reconcile-race-host");
            CreateRoomView room = await fixture.CreateRoomAsync(host);
            LobbySessionInfo session = await fixture.AuthenticateAsync(host);
            string key = LobbyFixture.NewRequestId();

            projection.PauseNext = true;
            Task<LobbyResponse> delayed = fixture.Application.ReconcileRoomAsync(
                session.Account,
                room.Room.RoomId,
                key,
                default);
            await projection.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            projection.Item = new PublicRoomProjection(
                room.Room.RoomId,
                "CLOSED",
                0,
                0,
                1,
                1,
                fixture.Clock.UtcNow);
            await fixture.Application.ReconcileRoomAsync(
                session.Account,
                room.Room.RoomId,
                LobbyFixture.NewRequestId(),
                default);
            projection.Resume.TrySetResult();

            LobbyResponse first = await delayed;
            Assert.Equal(200, first.StatusCode);
            Assert.Equal("CLOSED", LobbyJson.Deserialize<RoomSummaryView>(first.Json).State);

            LobbyResponse retry = await fixture.Application.ReconcileRoomAsync(
                session.Account,
                room.Room.RoomId,
                key,
                default);
            Assert.Equal(200, retry.StatusCode);
            Assert.True(retry.Replayed);
            Assert.Equal(first.Json, retry.Json);
        }

        [Fact]
        public async Task NonTerminalReconcileThenTerminalSameKeyReplaysTheOriginalResponse()
        {
            var projection = new MutableProjectionReader();
            await using LobbyFixture fixture = LobbyFixture.Create(projections: projection);
            GuestSessionView host = await fixture.CreateGuestAsync("reconcile-nonterminal-host");
            CreateRoomView room = await fixture.CreateRoomAsync(host);
            LobbySessionInfo session = await fixture.AuthenticateAsync(host);
            string key = LobbyFixture.NewRequestId();

            LobbyResponse first = await fixture.Application.ReconcileRoomAsync(
                session.Account,
                room.Room.RoomId,
                key,
                default);
            Assert.Equal(200, first.StatusCode);
            Assert.False(first.Replayed);
            Assert.Equal("OPEN", LobbyJson.Deserialize<RoomSummaryView>(first.Json).State);

            projection.Set(room.Room.RoomId, "CLOSED", fixture.Clock.UtcNow.AddSeconds(1));
            LobbyResponse replay = await fixture.Application.ReconcileRoomAsync(
                session.Account,
                room.Room.RoomId,
                key,
                default);
            Assert.True(replay.Replayed);
            Assert.Equal(first.Json, replay.Json);

            LobbyRoomSnapshot? persisted = await fixture.Application.Store.FindRoomSnapshotAsync(
                room.Room.RoomId,
                fixture.Clock.UtcNow,
                default);
            Assert.Equal(LobbyRoomState.Open, persisted!.Room.State);

            LobbyResponse newKey = await fixture.Application.ReconcileRoomAsync(
                session.Account,
                room.Room.RoomId,
                LobbyFixture.NewRequestId(),
                default);
            Assert.Equal(200, newKey.StatusCode);
            Assert.Equal("CLOSED", LobbyJson.Deserialize<RoomSummaryView>(newKey.Json).State);
            persisted = await fixture.Application.Store.FindRoomSnapshotAsync(
                room.Room.RoomId,
                fixture.Clock.UtcNow,
                default);
            Assert.Equal(LobbyRoomState.Closed, persisted!.Room.State);
        }

        [Fact]
        public async Task SameReconcileKeyForDifferentOperationIsARealConflict()
        {
            var projection = new MutableProjectionReader();
            await using LobbyFixture fixture = LobbyFixture.Create(projections: projection);
            GuestSessionView host = await fixture.CreateGuestAsync("reconcile-op-host");
            CreateRoomView room = await fixture.CreateRoomAsync(host);
            GuestSessionView guest = await fixture.CreateGuestAsync("reconcile-op-guest");
            LobbySessionInfo session = await fixture.AuthenticateAsync(guest);
            string key = LobbyFixture.NewRequestId();

            LobbyResponse reconcile = await fixture.Application.ReconcileRoomAsync(
                session.Account,
                room.Room.RoomId,
                key,
                default);
            Assert.Equal(200, reconcile.StatusCode);

            LobbyException conflict = await Assert.ThrowsAsync<LobbyException>(
                () => fixture.JoinAsync(
                    guest,
                    room.Room.RoomId,
                    preferSeat: 1,
                    requestId: key));
            Assert.Equal(409, conflict.StatusCode);
            Assert.Equal("REQUEST_ID_CONFLICT", conflict.Code);
        }

        [Fact]
        public async Task SameReconcileKeyForDifferentRoomIsARealConflict()
        {
            var projection = new MutableProjectionReader();
            await using LobbyFixture fixture = LobbyFixture.Create(projections: projection);
            GuestSessionView hostA = await fixture.CreateGuestAsync("reconcile-key-host-a");
            CreateRoomView roomA = await fixture.CreateRoomAsync(hostA, "A");
            GuestSessionView hostB = await fixture.CreateGuestAsync("reconcile-key-host-b");
            CreateRoomView roomB = await fixture.CreateRoomAsync(hostB, "B");
            LobbySessionInfo session = await fixture.AuthenticateAsync(hostA);
            string key = LobbyFixture.NewRequestId();

            LobbyResponse first = await fixture.Application.ReconcileRoomAsync(
                session.Account,
                roomA.Room.RoomId,
                key,
                default);
            Assert.Equal(200, first.StatusCode);

            LobbyException conflict = await Assert.ThrowsAsync<LobbyException>(
                () => fixture.Application.ReconcileRoomAsync(
                    session.Account,
                    roomB.Room.RoomId,
                    key,
                    default));
            Assert.Equal(409, conflict.StatusCode);
            Assert.Equal("REQUEST_ID_CONFLICT", conflict.Code);
        }

        private sealed class PausingProjectionReader : IRoomProjectionReader
        {
            public bool PauseNext { get; set; }

            public PublicRoomProjection? Item { get; set; }

            public TaskCompletionSource Entered { get; } =
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource Resume { get; } =
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            public async Task<PublicRoomProjectionBatch> ReadAsync(
                IReadOnlyList<string> roomIds,
                CancellationToken cancellationToken)
            {
                PublicRoomProjection? captured = Item;
                if (PauseNext)
                {
                    PauseNext = false;
                    Entered.TrySetResult();
                    await Resume.Task.WaitAsync(cancellationToken);
                }

                return new PublicRoomProjectionBatch(
                    captured == null
                        ? Array.Empty<PublicRoomProjection>()
                        : new[] { captured });
            }
        }

        private sealed class MutableProjectionReader : IRoomProjectionReader
        {
            private readonly Dictionary<string, PublicRoomProjection> _items =
                new Dictionary<string, PublicRoomProjection>(StringComparer.Ordinal);

            public void Set(
                string roomId,
                string lifecycle,
                DateTimeOffset updatedAt,
                int seats = 0,
                int spectators = 0,
                uint rulesetVersion = 1)
            {
                _items[roomId] = new PublicRoomProjection(
                    roomId,
                    lifecycle,
                    seats,
                    spectators,
                    1,
                    rulesetVersion,
                    updatedAt);
            }

            public void Remove(string roomId)
            {
                _items.Remove(roomId);
            }

            public Task<PublicRoomProjectionBatch> ReadAsync(
                IReadOnlyList<string> roomIds,
                CancellationToken cancellationToken)
            {
                var items = roomIds
                    .Where(_items.ContainsKey)
                    .Select(id => _items[id])
                    .ToList();
                return Task.FromResult(new PublicRoomProjectionBatch(items));
            }
        }
    }
}
