using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Xunit;
using WireCursor = Mahjong.Contracts.Wire.StreamCursor;
using WirePublicMatchView = Mahjong.Contracts.Wire.PublicMatchView;
using WirePublicSessionSummary = Mahjong.Contracts.Wire.PublicSessionSummary;

namespace Mahjong.Client.Wss.Tests
{
    public sealed class TableSessionRunnerTests
    {
        [Fact]
        public async Task CodexLateBaselineMustNotClearExpiredHandshakeDeadline()
        {
            using var fixture = new RunnerFixture(options =>
            {
                options.HandshakeTimeout = TimeSpan.FromMilliseconds(250);
                options.MaxReconnectAttempts = 0;
            });
            fixture.Channel.OnReceive = () =>
            {
                if (fixture.Channel.ReceivedCount == 1)
                {
                    // WELCOME arrived in time; the complete baseline arrives
                    // after the absolute deadline, without any wall-clock sleep.
                    fixture.Clock.Advance(300);
                }
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            TableSessionRunResult result = await fixture.Runner.RunAsync(cts.Token);
            Assert.Equal(TableSessionRunStop.ReconnectExhausted, result.Stop);
            Assert.Equal(ProtocolErrorCode.HandshakeTimeout, result.ReasonCode);
        }

        [Fact]
        public async Task ReconnectsAfterTransientLossWithANewTicket()
        {
            using var fixture = new RunnerFixture();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Task<TableSessionRunResult> run = fixture.Runner.RunAsync(cts.Token);
            await TestWire.WaitUntilAsync(
                () => fixture.Session.State == TableConnectionState.Ready,
                TimeSpan.FromSeconds(5),
                "initial baseline");

            fixture.Channel.TriggerTransientFailure();
            await TestWire.WaitUntilAsync(
                () => fixture.Session.State == TableConnectionState.Ready && fixture.Channel.OpenCount == 2,
                TimeSpan.FromSeconds(5),
                "second baseline");

            Assert.Equal(2, fixture.Channel.Tickets.Count);
            Assert.NotEqual(fixture.Channel.Tickets[0], fixture.Channel.Tickets[1]);
            Assert.Equal(2, fixture.Provider.Requests.Count);

            cts.Cancel();
            TableSessionRunResult result = await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TableSessionRunStop.Canceled, result.Stop);
        }

        [Fact]
        public async Task PermanentOpenFailureStopsWithoutRetries()
        {
            using var fixture = new RunnerFixture();
            fixture.Channel.FailAllOpens = true;
            fixture.Channel.OpenFailurePermanent = true;
            fixture.Channel.OpenFailureCode = ProtocolErrorCode.ProtocolViolation;

            TableSessionRunResult result = await fixture.Runner.RunAsync(CancellationToken.None);

            Assert.Equal(TableSessionRunStop.Faulted, result.Stop);
            Assert.Equal(ProtocolErrorCode.ProtocolViolation, result.ReasonCode);
            Assert.Equal(1, result.ConnectionAttempts);
            Assert.Equal(1, fixture.Channel.OpenCount);
            Assert.Empty(fixture.Delays);
        }

        [Fact]
        public async Task ReconnectBudgetIsBoundedAndBackoffIsCapped()
        {
            using var fixture = new RunnerFixture(options =>
            {
                options.MaxReconnectAttempts = 3;
                options.InitialBackoff = TimeSpan.FromMilliseconds(50);
                options.MaxBackoff = TimeSpan.FromMilliseconds(100);
                options.BackoffMultiplier = 2.0;
            });
            fixture.Channel.FailAllOpens = true;

            TableSessionRunResult result = await fixture.Runner.RunAsync(CancellationToken.None);

            Assert.Equal(TableSessionRunStop.ReconnectExhausted, result.Stop);
            Assert.Equal(4, result.ConnectionAttempts);
            Assert.Equal(new[] { 50.0, 100.0, 100.0 }, fixture.DelaysMs());
        }

        [Fact]
        public async Task TransientOpenFailureUsesASecondFreshTicket()
        {
            using var fixture = new RunnerFixture();
            fixture.Channel.FailNextOpen = true;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Task<TableSessionRunResult> run = fixture.Runner.RunAsync(cts.Token);

            await TestWire.WaitUntilAsync(
                () => fixture.Session.State == TableConnectionState.Ready,
                TimeSpan.FromSeconds(5),
                "baseline after one transient open failure");

            Assert.Equal(2, fixture.Channel.OpenCount);
            Assert.Equal(2, fixture.Channel.Tickets.Count);
            Assert.NotEqual(fixture.Channel.Tickets[0], fixture.Channel.Tickets[1]);
            Assert.Equal(new[] { 50.0 }, fixture.DelaysMs());

            cts.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }

        [Fact]
        public async Task UnknownCommandIsReconciledWithOriginalIdAndNeverResent()
        {
            using var fixture = new RunnerFixture();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Task<TableSessionRunResult> run = fixture.Runner.RunAsync(cts.Token);
            await TestWire.WaitUntilAsync(
                () => fixture.Session.State == TableConnectionState.Ready,
                TimeSpan.FromSeconds(5),
                "initial baseline");

            CommandSubmission submission = await fixture.Session.SubmitAsync(
                TestWire.SubmitActionCommand(),
                CancellationToken.None);
            Assert.True(submission.Sent);
            await fixture.Channel.WaitForSentAsync(MessageType.Command, 1, TimeSpan.FromSeconds(5));
            Assert.Equal(
                "request-1",
                fixture.Channel.SentEnvelopes().First(envelope => envelope.MessageType == MessageType.Command).RequestId);

            fixture.Channel.TriggerTransientFailure();
            await TestWire.WaitUntilAsync(
                () => fixture.Session.State == TableConnectionState.Ready && fixture.Channel.OpenCount == 2,
                TimeSpan.FromSeconds(5),
                "reconnect baseline");
            await fixture.Channel.WaitForSentAsync(MessageType.ReceiptQuery, 1, TimeSpan.FromSeconds(5));

            Assert.Equal(1, fixture.Channel.CountSent(MessageType.Command));
            Envelope query = fixture.Channel.SentEnvelopes().Last(
                envelope => envelope.MessageType == MessageType.ReceiptQuery);
            Assert.Equal("request-1", query.ReceiptQuery.RequestId);
            Assert.Equal("scope-1", query.ReceiptQuery.RequestScopeId);
            PendingCommandSnapshot pending = Assert.Single(fixture.Session.PendingCommands);
            Assert.Equal("request-1", pending.RequestId);
            Assert.True(pending.NeedsReconciliation);
            Assert.Equal(2, fixture.Provider.Requests.Count);

            cts.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }

        [Fact]
        public async Task PermanentTicketFailureStopsImmediately()
        {
            using var fixture = new RunnerFixture();
            fixture.Provider.Failure = new TableTicketException(ProtocolErrorCode.InvalidToken, true, "expired");

            TableSessionRunResult result = await fixture.Runner.RunAsync(CancellationToken.None);

            Assert.Equal(TableSessionRunStop.TicketRejected, result.Stop);
            Assert.Equal(ProtocolErrorCode.InvalidToken, result.ReasonCode);
            Assert.Equal(0, fixture.Channel.OpenCount);
        }

        [Fact]
        public async Task TransientTicketFailureIsRetriedBeforeConnecting()
        {
            using var fixture = new RunnerFixture();
            fixture.Provider.Failures.Enqueue(new TableTicketException(ProtocolErrorCode.ServerBusy, false, "busy"));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Task<TableSessionRunResult> run = fixture.Runner.RunAsync(cts.Token);

            await TestWire.WaitUntilAsync(
                () => fixture.Session.State == TableConnectionState.Ready,
                TimeSpan.FromSeconds(5),
                "baseline after ticket retry");

            Assert.Equal(2, fixture.Provider.Requests.Count);
            Assert.Equal(1, fixture.Channel.OpenCount);
            Assert.Equal(new[] { 50.0 }, fixture.DelaysMs());

            cts.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }

        [Fact]
        public async Task HandshakeTimeoutRetriesAndThenExhaustsTheBudget()
        {
            using var fixture = new RunnerFixture(options =>
            {
                options.MaxReconnectAttempts = 1;
                options.InitialBackoff = TimeSpan.FromMilliseconds(20);
                options.MaxBackoff = TimeSpan.FromMilliseconds(20);
                options.HandshakeTimeout = TimeSpan.FromMilliseconds(80);
            });
            fixture.Channel.AutoHandshake = false;

            TableSessionRunResult result = await fixture.Runner.RunAsync(CancellationToken.None);

            Assert.Equal(TableSessionRunStop.ReconnectExhausted, result.Stop);
            Assert.Equal(2, result.ConnectionAttempts);
            Assert.Equal(ProtocolErrorCode.HandshakeTimeout, result.ReasonCode);
        }

        [Fact]
        public async Task FullBaselineBeforeDeadlineReachesReady()
        {
            using var fixture = new RunnerFixture(options =>
            {
                options.MaxReconnectAttempts = 0;
                options.HandshakeTimeout = TimeSpan.FromMilliseconds(250);
            });
            fixture.Channel.OnReceive = () =>
                fixture.Clock.Advance(fixture.Channel.ReceivedCount == 0 ? 100 : 140);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Task<TableSessionRunResult> run = fixture.Runner.RunAsync(cts.Token);
            await TestWire.WaitUntilAsync(
                () => fixture.Session.State == TableConnectionState.Ready,
                TimeSpan.FromSeconds(5),
                "baseline before the absolute deadline");

            Assert.Equal(240, fixture.Clock.MonotonicMilliseconds);
            Assert.Equal(1, fixture.Channel.CountSent(MessageType.SyncRequest));

            cts.Cancel();
            TableSessionRunResult result = await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TableSessionRunStop.Canceled, result.Stop);
        }

        [Fact]
        public async Task FullBaselineExactlyAtDeadlineIsHandshakeTimeout()
        {
            using var fixture = new RunnerFixture(options =>
            {
                options.MaxReconnectAttempts = 0;
                options.HandshakeTimeout = TimeSpan.FromMilliseconds(250);
            });
            fixture.Channel.OnReceive = () =>
                fixture.Clock.Advance(fixture.Channel.ReceivedCount == 0 ? 100 : 150);

            TableSessionRunResult result = await fixture.Runner
                .RunAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(TableSessionRunStop.ReconnectExhausted, result.Stop);
            Assert.Equal(ProtocolErrorCode.HandshakeTimeout, result.ReasonCode);
            Assert.Equal(1, result.ConnectionAttempts);
            Assert.Equal(250, fixture.Clock.MonotonicMilliseconds);
            Assert.NotEqual(TableConnectionState.Ready, fixture.Session.State);
        }

        [Fact]
        public async Task FullBaselineAfterDeadlineIsHandshakeTimeoutAndDoesNotLeaveReady()
        {
            using var fixture = new RunnerFixture(options =>
            {
                options.MaxReconnectAttempts = 0;
                options.HandshakeTimeout = TimeSpan.FromMilliseconds(250);
            });
            fixture.Channel.OnReceive = () =>
                fixture.Clock.Advance(fixture.Channel.ReceivedCount == 0 ? 100 : 200);

            TableSessionRunResult result = await fixture.Runner
                .RunAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(TableSessionRunStop.ReconnectExhausted, result.Stop);
            Assert.Equal(ProtocolErrorCode.HandshakeTimeout, result.ReasonCode);
            Assert.Equal(1, result.ConnectionAttempts);
            Assert.Equal(300, fixture.Clock.MonotonicMilliseconds);
            Assert.NotEqual(TableConnectionState.Ready, fixture.Session.State);
        }

        [Fact]
        public async Task ExpiredBaselineMustNotPublishReadyOrAdmitCommandFromSynchronousSubscriber()
        {
            using var fixture = new RunnerFixture(options =>
            {
                options.MaxReconnectAttempts = 0;
                options.HandshakeTimeout = TimeSpan.FromMilliseconds(250);
            });
            long? readyAt = null;
            Task<CommandSubmission>? submission = null;
            fixture.Session.SnapshotChanged += (_, __) =>
            {
                if (fixture.Session.State == TableConnectionState.Ready)
                {
                    readyAt = fixture.Clock.MonotonicMilliseconds;
                    submission = fixture.Session.SubmitAsync(
                        TestWire.SubmitActionCommand(),
                        CancellationToken.None);
                }
            };
            fixture.Channel.OnReceive = () =>
                fixture.Clock.Advance(fixture.Channel.ReceivedCount == 0 ? 100 : 200);

            TableSessionRunResult result = await fixture.Runner
                .RunAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));

            bool sent = submission != null && (await submission).Sent;
            Assert.Equal(TableSessionRunStop.ReconnectExhausted, result.Stop);
            Assert.Null(readyAt);
            Assert.False(sent);
            Assert.Equal(0, fixture.Channel.CountSent(MessageType.Command));
            Assert.NotEqual(TableConnectionState.Ready, fixture.Session.State);
        }

        [Fact]
        public async Task SynchronousSubscriberCanSubmitOnlyWhenBaselineIsBeforeDeadline()
        {
            using var fixture = new RunnerFixture(options =>
            {
                options.MaxReconnectAttempts = 0;
                options.HandshakeTimeout = TimeSpan.FromMilliseconds(250);
            });
            var readyEvent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            long? readyAt = null;
            Task<CommandSubmission>? submission = null;
            fixture.Session.SnapshotChanged += (_, __) =>
            {
                if (fixture.Session.State == TableConnectionState.Ready)
                {
                    readyAt = fixture.Clock.MonotonicMilliseconds;
                    submission = fixture.Session.SubmitAsync(
                        TestWire.SubmitActionCommand(),
                        CancellationToken.None);
                    readyEvent.TrySetResult(true);
                }
            };
            fixture.Channel.OnReceive = () =>
                fixture.Clock.Advance(fixture.Channel.ReceivedCount == 0 ? 100 : 140);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Task<TableSessionRunResult> run = fixture.Runner.RunAsync(cts.Token);
            await readyEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(240, readyAt);
            Assert.NotNull(submission);
            CommandSubmission actual = await submission!;
            Assert.True(actual.Sent);
            Assert.Equal(1, fixture.Channel.CountSent(MessageType.Command));

            cts.Cancel();
            TableSessionRunResult result = await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TableSessionRunStop.Canceled, result.Stop);
        }

        [Theory]
        [InlineData(150)]
        [InlineData(200)]
        public async Task SynchronousSubscriberSeesNoReadyAtOrAfterDeadline(int baselineAdvanceMs)
        {
            using var fixture = new RunnerFixture(options =>
            {
                options.MaxReconnectAttempts = 0;
                options.HandshakeTimeout = TimeSpan.FromMilliseconds(250);
            });
            long? readyAt = null;
            Task<CommandSubmission>? submission = null;
            fixture.Session.SnapshotChanged += (_, __) =>
            {
                if (fixture.Session.State == TableConnectionState.Ready)
                {
                    readyAt = fixture.Clock.MonotonicMilliseconds;
                    submission = fixture.Session.SubmitAsync(
                        TestWire.SubmitActionCommand(),
                        CancellationToken.None);
                }
            };
            fixture.Channel.OnReceive = () =>
                fixture.Clock.Advance(fixture.Channel.ReceivedCount == 0 ? 100 : baselineAdvanceMs);

            TableSessionRunResult result = await fixture.Runner
                .RunAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));

            bool sent = submission != null && (await submission).Sent;
            Assert.Equal(TableSessionRunStop.ReconnectExhausted, result.Stop);
            Assert.Null(readyAt);
            Assert.False(sent);
            Assert.Equal(0, fixture.Channel.CountSent(MessageType.Command));
            Assert.NotEqual(TableConnectionState.Ready, fixture.Session.State);
        }

        [Fact]
        public async Task CancellationDuringBaselinePublishesNoReadyOrCommand()
        {
            using var fixture = new RunnerFixture(options =>
            {
                options.MaxReconnectAttempts = 0;
                options.HandshakeTimeout = TimeSpan.FromSeconds(5);
            });
            using var cts = new CancellationTokenSource();
            long? readyAt = null;
            Task<CommandSubmission>? submission = null;
            fixture.Session.SnapshotChanged += (_, __) =>
            {
                if (fixture.Session.State == TableConnectionState.Ready)
                {
                    readyAt = fixture.Clock.MonotonicMilliseconds;
                    submission = fixture.Session.SubmitAsync(
                        TestWire.SubmitActionCommand(),
                        CancellationToken.None);
                }
            };
            fixture.Channel.OnReceive = () =>
            {
                if (fixture.Channel.ReceivedCount == 1)
                {
                    cts.Cancel();
                }
            };

            TableSessionRunResult result = await fixture.Runner
                .RunAsync(cts.Token)
                .WaitAsync(TimeSpan.FromSeconds(5));

            bool sent = submission != null && (await submission).Sent;
            Assert.Equal(TableSessionRunStop.Canceled, result.Stop);
            Assert.Null(readyAt);
            Assert.False(sent);
            Assert.Equal(0, fixture.Channel.CountSent(MessageType.Command));
            Assert.NotEqual(TableConnectionState.Ready, fixture.Session.State);
        }

        [Fact]
        public async Task NonBaselineMessagesCannotExtendTheAbsoluteHandshakeDeadline()
        {
            using var fixture = new RunnerFixture(options =>
            {
                options.MaxReconnectAttempts = 0;
                options.InitialBackoff = TimeSpan.FromMilliseconds(20);
                options.MaxBackoff = TimeSpan.FromMilliseconds(20);
                options.HandshakeTimeout = TimeSpan.FromMilliseconds(250);
            });
            fixture.Channel.SyncFactory = (index, correlation) =>
                TestWire.PlayerPush(10 + index, 5, 10, 6, 11);
            fixture.Channel.ExtraMessageFactory = index =>
                TestWire.PlayerPush(10 + index, 6, 11, 7, 12);
            fixture.Channel.ExtraMessagesAfterSync = 20;
            fixture.Channel.OnReceive = () =>
            {
                // Real time advances slowly per message while the manual
                // monotonic clock advances faster. A per-read CancelAfter would
                // keep being reset and consume every message; the absolute
                // Open->Ready deadline must stop after a few.
                Thread.Sleep(60);
                fixture.Clock.Advance(100);
            };

            TableSessionRunResult result = await fixture.Runner
                .RunAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(TableSessionRunStop.ReconnectExhausted, result.Stop);
            Assert.Equal(ProtocolErrorCode.HandshakeTimeout, result.ReasonCode);
            Assert.Equal(1, result.ConnectionAttempts);
            Assert.NotEqual(TableConnectionState.Ready, fixture.Session.State);
            Assert.True(fixture.Channel.ReceivedCount >= 2);
            Assert.True(fixture.Channel.ReceivedCount <= 5);
            Assert.Equal(1, fixture.Channel.CountSent(MessageType.SyncRequest));
            Assert.True(fixture.Clock.MonotonicMilliseconds >= 200);
        }

        [Fact]
        public async Task PermanentInboundCloseStopsTheRunner()
        {
            using var fixture = new RunnerFixture();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Task<TableSessionRunResult> run = fixture.Runner.RunAsync(cts.Token);
            await TestWire.WaitUntilAsync(
                () => fixture.Session.State == TableConnectionState.Ready,
                TimeSpan.FromSeconds(5),
                "initial baseline");

            fixture.Channel.Enqueue(TestWire.Close(ProtocolErrorCode.RoomClosed, true));

            TableSessionRunResult result = await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TableSessionRunStop.Faulted, result.Stop);
            Assert.Equal(TableConnectionState.Faulted, fixture.Session.State);
        }

        [Fact]
        public async Task CancellationStopsThePumpAndReleasesTheChannel()
        {
            using var fixture = new RunnerFixture();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Task<TableSessionRunResult> run = fixture.Runner.RunAsync(cts.Token);
            await TestWire.WaitUntilAsync(
                () => fixture.Session.State == TableConnectionState.Ready,
                TimeSpan.FromSeconds(5),
                "initial baseline");

            cts.Cancel();
            TableSessionRunResult result = await run.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(TableSessionRunStop.Canceled, result.Stop);
            Assert.False(fixture.Channel.IsOpen);
            Assert.NotEqual(TableConnectionState.Ready, fixture.Session.State);
        }

        [Fact]
        public async Task DeadlineCrossingBetweenAdmissionChecksDoesNotCommitBaselineCache()
        {
            using var channel = new ScriptedTableChannel();
            var session = new TableSession(channel, new InMemoryOutboxStore());
            var clock = new CrossingClock();
            int readyEvents = 0;
            session.SnapshotChanged += (_, __) =>
            {
                if (session.State == TableConnectionState.Ready)
                {
                    readyEvents++;
                }
            };
            channel.OnReceive = () =>
            {
                if (channel.ReceivedCount == 0)
                {
                    clock.Now = 100;
                }
                else
                {
                    clock.Armed = true;
                }
            };

            var runner = new TableSessionRunner(
                session,
                new DelegateTableTicketProvider((_, __) => Task.FromResult("probe-ticket")),
                new TableSessionRunnerOptions
                {
                    HandshakeTimeout = TimeSpan.FromMilliseconds(250),
                    MaxReconnectAttempts = 0
                },
                (_, __) => Task.CompletedTask,
                clock);

            TableSessionRunResult result = await runner
                .RunAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(TableSessionRunStop.ReconnectExhausted, result.Stop);
            Assert.Equal(ProtocolErrorCode.HandshakeTimeout, result.ReasonCode);
            Assert.Equal(0, readyEvents);
            Assert.Null(session.PlayerSnapshot);
            Assert.Null(session.ConfirmedCursor);
            Assert.Equal(0, channel.CountSent(MessageType.Command));
            Assert.Equal(0, channel.CountSent(MessageType.ReceiptQuery));
        }

        [Fact]
        public async Task CancellationAtSecondAdmissionCheckDoesNotCommitBaselineCache()
        {
            using var channel = new ScriptedTableChannel();
            var session = new TableSession(channel, new InMemoryOutboxStore());
            await session.OpenAsync(new TableSessionOpenRequest("ticket", "probe"), CancellationToken.None);
            await session.PumpOnceAsync(CancellationToken.None);

            using var cts = new CancellationTokenSource();
            int checks = 0;
            int readyEvents = 0;
            session.SnapshotChanged += (_, __) =>
            {
                if (session.State == TableConnectionState.Ready)
                {
                    readyEvents++;
                }
            };
            session.BaselineAdmissionCheck = token =>
            {
                if (++checks == 2)
                {
                    cts.Cancel();
                }

                return !token.IsCancellationRequested;
            };
            await session.PumpOnceAsync(cts.Token);

            Assert.NotEqual(TableConnectionState.Ready, session.State);
            Assert.Equal(0, readyEvents);
            Assert.Null(session.PlayerSnapshot);
            Assert.Null(session.ConfirmedCursor);
            Assert.Equal(0, channel.CountSent(MessageType.Command));
            Assert.Equal(0, channel.CountSent(MessageType.ReceiptQuery));
        }

        [Fact]
        public async Task RejectedResyncPreservesExistingAcceptedPlayerBaseline()
        {
            using var channel = new ScriptedTableChannel();
            var session = new TableSession(channel, new InMemoryOutboxStore());
            await session.OpenAsync(new TableSessionOpenRequest("ticket", "probe"), CancellationToken.None);
            await session.PumpOnceAsync(CancellationToken.None);
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(TableConnectionState.Ready, session.State);
            Assert.Equal(5UL, session.ConfirmedCursor!.Value.StreamVersion.Value);

            channel.AutoHandshake = false;
            int readyEvents = 0;
            session.SnapshotChanged += (_, __) =>
            {
                if (session.State == TableConnectionState.Ready)
                {
                    readyEvents++;
                }
            };
            int checks = 0;
            session.BaselineAdmissionCheck = _ => ++checks == 1;

            // Force an in-flight resync without applying a new baseline.
            channel.Enqueue(TestWire.PlayerPush(11, 99, 99, 100, 100));
            await session.PumpOnceAsync(CancellationToken.None);
            Envelope request = channel.SentEnvelopes().Last(
                envelope => envelope.MessageType == MessageType.SyncRequest);
            channel.Enqueue(TestWire.PlayerSync(
                11,
                request.CorrelationId,
                streamVersion: 6,
                projection: 11));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.NotEqual(TableConnectionState.Ready, session.State);
            Assert.Equal(0, readyEvents);
            Assert.Equal(5UL, session.ConfirmedCursor!.Value.StreamVersion.Value);
            Assert.Equal(5UL, session.PlayerSnapshot!.Cursor.StreamVersion);
            Assert.Equal(0, channel.CountSent(MessageType.Command));
            Assert.Equal(0, channel.CountSent(MessageType.ReceiptQuery));
        }

        [Fact]
        public async Task RejectedPublicBaselineDoesNotCommitPublicCache()
        {
            using var channel = new ScriptedTableChannel
            {
                WelcomeFactory = _ => TestWire.Welcome(
                    bindingEpoch: 11,
                    seatId: -1,
                    role: Role.Spectator),
                SyncFactory = (_, correlation) => PublicSync(11, correlation)
            };
            var session = new TableSession(channel, new InMemoryOutboxStore());
            await session.OpenAsync(new TableSessionOpenRequest("ticket", "probe"), CancellationToken.None);
            await session.PumpOnceAsync(CancellationToken.None);
            int readyEvents = 0;
            session.SnapshotChanged += (_, __) =>
            {
                if (session.State == TableConnectionState.Ready)
                {
                    readyEvents++;
                }
            };
            session.BaselineAdmissionCheck = _ => false;

            await session.PumpOnceAsync(CancellationToken.None);

            Assert.NotEqual(TableConnectionState.Ready, session.State);
            Assert.Equal(0, readyEvents);
            Assert.Null(session.PublicSnapshot);
            Assert.Null(session.ConfirmedCursor);
            Assert.Equal(0, channel.CountSent(MessageType.Command));
            Assert.Equal(0, channel.CountSent(MessageType.ReceiptQuery));
        }

        [Fact]
        public async Task RejectedRoomBaselineDoesNotCommitRoomCache()
        {
            using var channel = new ScriptedTableChannel
            {
                WelcomeFactory = _ => TestWire.Welcome(
                    bindingEpoch: 11,
                    seatId: -1,
                    role: Role.Lobby),
                SyncFactory = (_, correlation) => RoomSync(11, correlation)
            };
            var session = new TableSession(channel, new InMemoryOutboxStore());
            await session.OpenAsync(new TableSessionOpenRequest("ticket", "probe"), CancellationToken.None);
            await session.PumpOnceAsync(CancellationToken.None);
            int readyEvents = 0;
            session.SnapshotChanged += (_, __) =>
            {
                if (session.State == TableConnectionState.Ready)
                {
                    readyEvents++;
                }
            };
            session.BaselineAdmissionCheck = _ => false;

            await session.PumpOnceAsync(CancellationToken.None);

            Assert.NotEqual(TableConnectionState.Ready, session.State);
            Assert.Equal(0, readyEvents);
            Assert.Null(session.RoomSnapshot);
            Assert.Null(session.ConfirmedCursor);
            Assert.Equal(0, channel.CountSent(MessageType.Command));
            Assert.Equal(0, channel.CountSent(MessageType.ReceiptQuery));
        }

        private static Envelope PublicSync(int bindingEpoch, string correlationId)
        {
            var snapshot = new PublicSnapshot
            {
                SessionId = TestWire.SessionId,
                RoundId = "round-1",
                Cursor = new WireCursor { StreamVersion = 5, ProjectionSeq = 10 },
                StateVersion = 5,
                EventSeq = 105,
                SessionVersion = 1,
                RoundVersion = 2
            };
            snapshot.View = new WirePublicMatchView
            {
                Session = new WirePublicSessionSummary { Finished = false },
                Paused = false,
                SettlementRoundId = "round-1"
            };
            Envelope envelope = TestWire.Envelope(MessageType.SyncResponse, e => e.SyncResponse = new SyncResponse
            {
                Target = SyncTarget.PublicStream,
                IsFullSnapshot = true,
                Public = snapshot,
                StateVersion = 5,
                EventSeq = 105
            });
            envelope.CorrelationId = correlationId;
            return TestWire.Bound(envelope, bindingEpoch, seatId: -1);
        }

        private static Envelope RoomSync(int bindingEpoch, string correlationId)
        {
            Envelope envelope = TestWire.Envelope(MessageType.SyncResponse, e => e.SyncResponse = new SyncResponse
            {
                Target = SyncTarget.Room,
                IsFullSnapshot = true,
                Room = new RoomUpdate
                {
                    RoomId = TestWire.RoomId,
                    SessionId = TestWire.SessionId,
                    RoomVersion = 1,
                    Capacity = 4
                }
            });
            envelope.CorrelationId = correlationId;
            return TestWire.Bound(envelope, bindingEpoch, seatId: -1);
        }

        private sealed class CrossingClock : ITableClientClock
        {
            public long Now;
            public bool Armed;
            public int GateReads;

            public long MonotonicMilliseconds
            {
                get
                {
                    if (!Armed)
                    {
                        return Now;
                    }

                    GateReads++;
                    Now = GateReads == 1 ? 240 : 300;
                    return Now;
                }
            }

            public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch.AddMilliseconds(Now);
        }

        private sealed class RunnerFixture : IDisposable
        {
            private readonly List<TimeSpan> _delays = new List<TimeSpan>();

            public RunnerFixture(Action<TableSessionRunnerOptions>? configure = null)
            {
                Channel = new ScriptedTableChannel();
                Session = new TableSession(Channel, new InMemoryOutboxStore());
                Provider = new RecordingTicketProvider();
                Clock = new ManualTableClock();
                var options = new TableSessionRunnerOptions
                {
                    InitialBackoff = TimeSpan.FromMilliseconds(50),
                    MaxBackoff = TimeSpan.FromMilliseconds(200),
                    BackoffMultiplier = 2.0,
                    MaxReconnectAttempts = 8,
                    HandshakeTimeout = TimeSpan.FromSeconds(2)
                };
                configure?.Invoke(options);
                Options = options;
                Runner = new TableSessionRunner(
                    Session,
                    Provider,
                    options,
                    (delay, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        _delays.Add(delay);
                        return Task.CompletedTask;
                    },
                    Clock);
            }

            public ScriptedTableChannel Channel { get; }

            public TableSession Session { get; }

            public RecordingTicketProvider Provider { get; }

            public ManualTableClock Clock { get; }

            public TableSessionRunnerOptions Options { get; }

            public TableSessionRunner Runner { get; }

            public IReadOnlyList<TimeSpan> Delays => _delays;

            public double[] DelaysMs()
            {
                var result = new double[_delays.Count];
                for (int i = 0; i < _delays.Count; i++)
                {
                    result[i] = _delays[i].TotalMilliseconds;
                }

                return result;
            }

            public void Dispose()
            {
                Channel.Dispose();
            }
        }

        private sealed class RecordingTicketProvider : ITableTicketProvider
        {
            public List<TableTicketRequest> Requests { get; } = new List<TableTicketRequest>();

            public Queue<Exception> Failures { get; } = new Queue<Exception>();

            public Exception? Failure { get; set; }

            public Task<string> GetTicketAsync(TableTicketRequest request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Requests.Add(request);
                if (Failure != null)
                {
                    throw Failure;
                }

                if (Failures.Count > 0)
                {
                    throw Failures.Dequeue();
                }

                return Task.FromResult("ticket-" + request.Attempt);
            }
        }
    }
}
