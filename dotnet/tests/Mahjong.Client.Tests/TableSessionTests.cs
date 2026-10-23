using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Xunit;
using ContractCursor = Mahjong.Contracts.StreamCursor;

namespace Mahjong.Client.Tests
{
    public sealed class TableSessionTests
    {
        [Fact]
        public async Task PersistBeforeSendOrdersStoreWriteBeforeTransportSend()
        {
            var timeline = new List<string>();
            (TableSession session, FakeTableChannel channel, RecordingOutboxStore store) = await TestSession.ConnectedAsync(timeline: timeline);
            timeline.Clear();

            CommandSubmission submission = await session.SubmitAsync(TestWire.Command(), CancellationToken.None);

            Assert.True(submission.Sent);
            Assert.False(submission.WasDuplicate);
            Assert.Equal(new[] { "store.save", "channel.send" }, timeline);
            Assert.Single(channel.SentEnvelopes(), e => e.MessageType == MessageType.Command);
            Assert.Single(session.PendingCommands);
            Assert.Equal(1, store.SaveCount);
        }

        [Fact]
        public async Task SubmitRetainsRequestScopeAndOriginalCanonicalBytes()
        {
            (TableSession session, FakeTableChannel channel, _) = await TestSession.ConnectedAsync();
            ProtocolCommand command = TestWire.Command();
            byte[] originalBytes = TestWire.PayloadBytes(command);

            await session.SubmitAsync(command, CancellationToken.None);

            Envelope sent = channel.LastSent(MessageType.Command);
            Assert.Equal("request-1", sent.RequestId);
            Assert.Equal("scope-1", sent.Scope.ScopeId);
            Assert.Equal(originalBytes, sent.Command.CanonicalPayload.ToByteArray());
            Assert.Equal(command.Payload.Digest.ToHex(), session.PendingCommands.Single().DigestSha256);
            Assert.Equal((ulong)command.ExpectedStateVersion.Value, sent.Command.ExpectedStateVersion);
            Assert.Equal(7ul, sent.ControlEpoch);
            Assert.Equal(3ul, sent.OwnerEpoch);
        }

        [Fact]
        public async Task SyncRequestAlwaysAsksForTheBoundRoleFullBaseline()
        {
            (TableSession _, FakeTableChannel channel, _) = await TestSession.ConnectedAsync();

            Envelope sync = channel.LastSent(MessageType.SyncRequest);
            Assert.Equal(SyncTarget.PlayerStream, sync.SyncRequest.Target);
            Assert.True(sync.SyncRequest.RequestFullBaseline);
            Assert.Equal(11ul, sync.BindingEpoch);
            Assert.Equal(5ul, sync.SyncRequest.KnownCursor.StreamVersion);
            Assert.Equal(10ul, sync.SyncRequest.KnownCursor.ProjectionSeq);
        }

        [Fact]
        public async Task ReorderedPushesAreAppliedInCursorOrderAfterGap()
        {
            (TableSession session, FakeTableChannel channel, _) = await TestSession.ConnectedAsync();

            // Push 12 arrives before its predecessor 11. Its `from` cursor still
            // names the missing frame, so the client must not apply it yet.
            channel.Enqueue(TestWire.PlayerPush(11, 6, 11, 6, 12));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(TableConnectionState.Synchronizing, session.State);
            Assert.Equal(1, session.EarlyBufferItemCount);
            Assert.Equal(2, channel.CountSent(MessageType.SyncRequest));

            channel.Enqueue(TestWire.PlayerPush(11, 5, 10, 6, 11));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(2, session.EarlyBufferItemCount);

            // Baseline at the pre-gap cursor. The drain must find 11 first and
            // then apply 12, even though 12 is stored earlier in the buffer.
            channel.Enqueue(TestWire.PlayerSync(channel, 11, 5, 10));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(TableConnectionState.Ready, session.State);
            Assert.Equal(0, session.EarlyBufferItemCount);
            Assert.Equal(12ul, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
            Assert.Equal(6ul, session.ConfirmedCursor.Value.StreamVersion.Value);
            Assert.Equal(6ul, session.PlayerSnapshot!.StateVersion);
        }

        [Fact]
        public async Task DuplicateAndStalePushesDoNotAdvanceCursor()
        {
            (TableSession session, FakeTableChannel channel, _) = await TestSession.ConnectedAsync();

            channel.Enqueue(TestWire.PlayerPush(11, 5, 10, 6, 11));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(11ul, session.ConfirmedCursor!.Value.ProjectionSeq.Value);

            channel.Enqueue(TestWire.PlayerPush(11, 5, 10, 6, 11));
            await session.PumpOnceAsync(CancellationToken.None);
            channel.Enqueue(TestWire.PlayerPush(11, 5, 10, 6, 11));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(TableConnectionState.Ready, session.State);
            Assert.Equal(11ul, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
            Assert.Equal(6ul, session.ConfirmedCursor.Value.StreamVersion.Value);
            Assert.Equal(1, channel.CountSent(MessageType.SyncRequest));
        }

        [Fact]
        public async Task StreamVersionRegressionIsStaleAndIgnored()
        {
            (TableSession session, FakeTableChannel channel, _) = await TestSession.ConnectedAsync();

            // projection_seq advances but stream_version moves backwards.
            channel.Enqueue(TestWire.PlayerPush(11, 5, 10, 4, 11));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(TableConnectionState.Ready, session.State);
            Assert.Equal(10ul, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
            Assert.Equal(5ul, session.ConfirmedCursor.Value.StreamVersion.Value);
            Assert.Equal(1, channel.CountSent(MessageType.SyncRequest));
        }

        [Fact]
        public async Task EarlyBufferOverflowClearsAndRequestsANewFullBaseline()
        {
            var options = new TableSessionOptions
            {
                MaxEarlyBufferItems = 2,
                MaxEarlyBufferBytes = TableSessionOptions.DefaultMaxEarlyBufferBytes
            };
            (TableSession session, FakeTableChannel channel, _) = await TestSession.HandshakeAsync(options: options);

            Assert.Equal(1, channel.CountSent(MessageType.SyncRequest));
            channel.Enqueue(TestWire.PlayerPush(11, 5, 10, 6, 11));
            await session.PumpOnceAsync(CancellationToken.None);
            channel.Enqueue(TestWire.PlayerPush(11, 6, 11, 6, 12));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(2, session.EarlyBufferItemCount);

            channel.Enqueue(TestWire.PlayerPush(11, 6, 12, 6, 13));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(1, session.EarlyBufferOverflowCount);
            Assert.Equal(0, session.EarlyBufferItemCount);
            Assert.Equal(0, session.EarlyBufferByteCount);
            Assert.Equal(TableConnectionState.Synchronizing, session.State);
            Assert.Equal(2, channel.CountSent(MessageType.SyncRequest));

            // The overflow recovery baseline must still be able to settle the session.
            channel.Enqueue(TestWire.PlayerSync(channel, 11, 5, 10));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(TableConnectionState.Ready, session.State);
            Assert.Equal(10ul, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
        }

        [Fact]
        public async Task EarlyBufferByteLimitAlsoTriggersFullBaselineRecovery()
        {
            var options = new TableSessionOptions
            {
                MaxEarlyBufferItems = TableSessionOptions.DefaultMaxEarlyBufferItems,
                MaxEarlyBufferBytes = 32
            };
            (TableSession session, FakeTableChannel channel, _) = await TestSession.HandshakeAsync(options: options);

            channel.Enqueue(TestWire.PlayerPush(11, 5, 10, 6, 11));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(1, session.EarlyBufferOverflowCount);
            Assert.Equal(0, session.EarlyBufferItemCount);
            Assert.Equal(0, session.EarlyBufferByteCount);
            Assert.Equal(2, channel.CountSent(MessageType.SyncRequest));
        }

        [Fact]
        public async Task OldBindingMessagesDoNotOverwriteTheNewBaseline()
        {
            (TableSession session, FakeTableChannel channel, _) = await TestSession.ConnectedAsync(bindingEpoch: 11);

            channel.Enqueue(TestWire.PlayerPush(11, 5, 10, 6, 11));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(11ul, session.ConfirmedCursor!.Value.ProjectionSeq.Value);

            await TestSession.ReconnectAsync(session, channel, bindingEpoch: 22, scopeId: "scope-2", baselineStreamVersion: 8, baselineProjection: 20);
            Assert.Equal(22ul, session.Binding!.BindingEpoch.Value);

            // Even a newer-looking frame from the previous binding must be ignored.
            channel.Enqueue(TestWire.PlayerPush(11, 8, 20, 9, 21));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(TableConnectionState.Ready, session.State);
            Assert.Equal(20ul, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
            Assert.Equal(8ul, session.ConfirmedCursor.Value.StreamVersion.Value);
            Assert.Equal(8ul, session.PlayerSnapshot!.StateVersion);
        }

        [Fact]
        public async Task LostReplyAfterServerCommitIsReconciledByReceiptQueryOnly()
        {
            (TableSession session, FakeTableChannel channel, RecordingOutboxStore store) = await TestSession.ConnectedAsync();
            ProtocolCommand command = TestWire.Command();
            await session.SubmitAsync(command, CancellationToken.None);
            string digest = session.PendingCommands.Single().DigestSha256;
            Assert.Equal(1, channel.CountSent(MessageType.Command));

            await TestSession.ReconnectAsync(session, channel, bindingEpoch: 22, scopeId: "scope-2", baselineStreamVersion: 8, baselineProjection: 20);

            Assert.Equal(1, channel.CountSent(MessageType.Command));
            Envelope query = TestWire.OutboundReceiptQuery(channel, "request-1");
            Assert.Equal("scope-1", query.ReceiptQuery.RequestScopeId);
            Assert.Single(session.PendingCommands);

            var receipt = new global::Mahjong.Contracts.Wire.Receipt
            {
                Status = global::Mahjong.Contracts.Wire.ReceiptStatus.Applied,
                EvaluatedStateVersion = 5,
                CommandVersion = 6,
                EventSeq = 12,
                CanonicalPayloadSha256 = digest
            };
            channel.Enqueue(TestWire.ReceiptLookup(query.CorrelationId, LookupStatus.Found, receipt, digest));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Empty(session.PendingCommands);
            Assert.Equal(1, store.ResolveCount);
            Assert.Empty(await store.LoadUnresolvedAsync(CancellationToken.None));
            Assert.Equal(1, channel.CountSent(MessageType.Command));
        }

        [Fact]
        public async Task RestartRecoveryQueriesTheOriginalScopeAndRetriesTheExactIdentity()
        {
            var store = new RecordingOutboxStore();
            var firstChannel = new FakeTableChannel();
            (TableSession firstSession, FakeTableChannel connectedFirst, _) =
                await TestSession.ConnectedAsync(channel: firstChannel, store: store);
            ProtocolCommand command = TestWire.Command();
            await firstSession.SubmitAsync(command, CancellationToken.None);
            byte[] originalPayload = TestWire.PayloadBytes(command);
            IdempotencyKey key = firstSession.PendingCommands.Single().Key;

            // A new session over the same store models a process restart. It
            // cannot know whether the old process sent the command.
            var secondChannel = new FakeTableChannel();
            var secondSession = new TableSession(secondChannel, store);
            await secondSession.OpenAsync(new TableSessionOpenRequest("ticket-2", "client-build-1"), CancellationToken.None);
            Assert.Single(secondSession.PendingCommands);
            Assert.True(secondSession.PendingCommands.Single().DeliveryUnknown);

            secondChannel.Enqueue(TestWire.Welcome(22, "scope-2", 8, 20));
            await secondSession.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(TableConnectionState.Synchronizing, secondSession.State);
            Assert.Equal(0, secondChannel.CountSent(MessageType.ReceiptQuery));

            secondChannel.Enqueue(TestWire.PlayerSync(secondChannel, 22, 8, 20));
            await secondSession.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(0, secondChannel.CountSent(MessageType.Command));
            Envelope query = TestWire.OutboundReceiptQuery(secondChannel, "request-1");
            Assert.Equal("scope-1", query.ReceiptQuery.RequestScopeId);
            Assert.Equal("request-1", query.ReceiptQuery.RequestId);

            // No retry before reconciliation.
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => secondSession.RetryPendingAsync(key, CancellationToken.None));
            Assert.Equal(0, secondChannel.CountSent(MessageType.Command));

            string digest = secondSession.PendingCommands.Single().DigestSha256;
            secondChannel.Enqueue(TestWire.ReceiptLookup(query.CorrelationId, LookupStatus.Unknown, null, digest));
            await secondSession.PumpOnceAsync(CancellationToken.None);

            PendingCommandSnapshot pending = secondSession.PendingCommands.Single();
            Assert.Equal(ReceiptLookupStatus.Unknown, pending.LastLookup);
            Assert.Equal("request-1", pending.RequestId);
            Assert.Equal("scope-1", pending.ScopeId);

            CommandSubmission retry = await secondSession.RetryPendingAsync(key, CancellationToken.None);
            Assert.True(retry.Sent);
            Assert.Equal(1, secondChannel.CountSent(MessageType.Command));

            Envelope resent = secondChannel.LastSent(MessageType.Command);
            Assert.Equal("request-1", resent.RequestId);
            Assert.Equal("scope-1", resent.Scope.ScopeId);
            Assert.Equal(originalPayload, resent.Command.CanonicalPayload.ToByteArray());
            Assert.Equal(CanonicalPayloadDigest.Compute(originalPayload), CanonicalPayloadDigest.Compute(resent.Command.CanonicalPayload.Span));
        }

        [Fact]
        public async Task ReceiptDigestMismatchBecomesAConflictWithoutResending()
        {
            (TableSession session, FakeTableChannel channel, RecordingOutboxStore store) = await TestSession.ConnectedAsync();
            await session.SubmitAsync(TestWire.Command(), CancellationToken.None);

            OutboxResolution? resolved = null;
            session.ReceiptResolved += (_, args) => resolved = args.Resolution;

            channel.Enqueue(TestWire.Receipt(
                11,
                "scope-1",
                "request-1",
                new string('0', 64),
                global::Mahjong.Contracts.Wire.ReceiptStatus.Applied,
                5,
                6,
                12));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Empty(session.PendingCommands);
            Assert.NotNull(resolved);
            Assert.Equal(OutboxDisposition.Conflict, resolved!.Disposition);
            Assert.False(resolved.Succeeded);
            Assert.Equal(1, store.ResolveCount);
            Assert.Equal(1, channel.CountSent(MessageType.Command));
        }

        [Fact]
        public async Task OutboxWriteFailurePreventsAnyTransportSend()
        {
            var timeline = new List<string>();
            (TableSession session, FakeTableChannel channel, RecordingOutboxStore store) =
                await TestSession.ConnectedAsync(timeline: timeline);
            store.ThrowOnSave = true;
            timeline.Clear();

            await Assert.ThrowsAsync<IOException>(() => session.SubmitAsync(TestWire.Command(), CancellationToken.None));

            Assert.DoesNotContain("channel.send", timeline);
            Assert.Empty(session.PendingCommands);
            Assert.Equal(0, channel.CountSent(MessageType.Command));
        }

        [Fact]
        public async Task DuplicateSubmissionWithSameKeyAndBytesDoesNotSendTwice()
        {
            (TableSession session, FakeTableChannel channel, _) = await TestSession.ConnectedAsync();
            ProtocolCommand command = TestWire.Command();

            CommandSubmission first = await session.SubmitAsync(command, CancellationToken.None);
            CommandSubmission second = await session.SubmitAsync(command.Retry(), CancellationToken.None);

            Assert.True(first.Sent);
            Assert.False(first.WasDuplicate);
            Assert.False(second.Sent);
            Assert.True(second.WasDuplicate);
            Assert.Equal(1, channel.CountSent(MessageType.Command));
        }

        [Fact]
        public async Task OldBindingSyncResponseCannotResetTheNewBindingBaseline()
        {
            (TableSession session, FakeTableChannel channel, _) = await TestSession.ConnectedAsync(bindingEpoch: 11);

            await session.DisconnectAsync(CancellationToken.None);
            await session.OpenAsync(new TableSessionOpenRequest("ticket-1", "client-build-1"), CancellationToken.None);
            channel.Enqueue(TestWire.Welcome(22, "scope-2", 8, 20));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(TableConnectionState.Synchronizing, session.State);

            // A late baseline from binding 11 must not be mistaken for the new one.
            channel.Enqueue(TestWire.PlayerSync(11, 9, 21));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(TableConnectionState.Synchronizing, session.State);
            Assert.Equal(10ul, session.ConfirmedCursor!.Value.ProjectionSeq.Value);

            channel.Enqueue(TestWire.PlayerSync(channel, 22, 8, 20));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(TableConnectionState.Ready, session.State);
            Assert.Equal(20ul, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
        }

        [Fact]
        public async Task UnknownReceiptDoesNotAuthorizeRetryBeforeAReceiptLookup()
        {
            (TableSession session, FakeTableChannel channel, _) = await TestSession.ConnectedAsync();
            await session.SubmitAsync(TestWire.Command(), CancellationToken.None);
            IdempotencyKey key = session.PendingCommands.Single().Key;

            channel.Enqueue(TestWire.Receipt(
                11,
                "scope-1",
                "request-1",
                session.PendingCommands.Single().DigestSha256,
                global::Mahjong.Contracts.Wire.ReceiptStatus.Unknown,
                0,
                null,
                null));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Single(session.PendingCommands);
            Assert.Equal(global::Mahjong.Contracts.ReceiptStatus.Unknown, session.PendingCommands.Single().LastReceipt);
            Assert.Null(session.PendingCommands.Single().LastLookup);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.RetryPendingAsync(key, CancellationToken.None));
            Assert.Equal(1, channel.CountSent(MessageType.Command));
        }

        [Fact]
        public async Task SpectatorSessionAppliesPublicPushAndNeverPlayerSnapshots()
        {
            (TableSession session, FakeTableChannel channel, _) = await TestSession.ConnectedSpectatorAsync();
            Assert.Equal(ProtocolRole.Spectator, session.Binding!.Role);

            channel.Enqueue(TestWire.PublicPush(11, 5, 10, 6, 11));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(TableConnectionState.Ready, session.State);
            Assert.Equal(11ul, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
            Assert.NotNull(session.PublicSnapshot);
            Assert.Null(session.PlayerSnapshot);
        }

        [Fact]
        public async Task SameBindingSyncWithCurrentCorrelationCannotRollBackConfirmedCursor()
        {
            (TableSession session, FakeTableChannel channel, _) = await TestSession.ConnectedAsync();

            channel.Enqueue(TestWire.PlayerPush(11, 5, 10, 6, 11));
            await session.PumpOnceAsync(CancellationToken.None);
            channel.Enqueue(TestWire.PlayerPush(11, 8, 13, 9, 14));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(TableConnectionState.Synchronizing, session.State);

            string correlation = channel.LastSent(MessageType.SyncRequest).CorrelationId;

            // The correlation is current, but the baseline is older than the
            // already confirmed cursor: it must not roll the stream back.
            channel.Enqueue(TestWire.PlayerSync(11, 4, 9, correlationId: correlation));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(TableConnectionState.Synchronizing, session.State);
            Assert.Equal(11ul, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
            Assert.Equal(6ul, session.ConfirmedCursor.Value.StreamVersion.Value);
            Assert.Equal(6ul, session.PlayerSnapshot!.StateVersion);

            channel.Enqueue(TestWire.PlayerSync(11, 9, 14, correlationId: correlation));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(TableConnectionState.Ready, session.State);
            Assert.Equal(14ul, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
            Assert.Equal(9ul, session.PlayerSnapshot!.StateVersion);
        }

        [Fact]
        public async Task SupersededSyncResponseCannotApplyAfterOverflowForcedRequest()
        {
            var options = new TableSessionOptions
            {
                MaxEarlyBufferItems = 2,
                MaxEarlyBufferBytes = TableSessionOptions.DefaultMaxEarlyBufferBytes
            };
            (TableSession session, FakeTableChannel channel, _) = await TestSession.HandshakeAsync(options: options);

            channel.Enqueue(TestWire.PlayerPush(11, 5, 10, 6, 11));
            await session.PumpOnceAsync(CancellationToken.None);
            channel.Enqueue(TestWire.PlayerPush(11, 6, 11, 6, 12));
            await session.PumpOnceAsync(CancellationToken.None);
            string superseded = channel.LastSent(MessageType.SyncRequest).CorrelationId;

            channel.Enqueue(TestWire.PlayerPush(11, 6, 12, 6, 13));
            await session.PumpOnceAsync(CancellationToken.None);
            string active = channel.LastSent(MessageType.SyncRequest).CorrelationId;

            Assert.NotEqual(superseded, active);
            Assert.Equal(2, channel.CountSent(MessageType.SyncRequest));

            // A newer-looking baseline from the superseded request must be
            // ignored even though it is above the confirmed cursor.
            channel.Enqueue(TestWire.PlayerSync(11, 7, 12, correlationId: superseded));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(TableConnectionState.Synchronizing, session.State);
            Assert.Null(session.ConfirmedCursor);

            channel.Enqueue(TestWire.PlayerSync(11, 7, 12, correlationId: active));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(TableConnectionState.Ready, session.State);
            Assert.Equal(12ul, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
            Assert.Equal(7ul, session.PlayerSnapshot!.StateVersion);
        }

        [Fact]
        public async Task NewBindingCanResetTheStreamAtItsAdvertisedBaseline()
        {
            (TableSession session, FakeTableChannel channel, _) = await TestSession.ConnectedAsync(bindingEpoch: 11);

            channel.Enqueue(TestWire.PlayerPush(11, 5, 10, 6, 11));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(11ul, session.ConfirmedCursor!.Value.ProjectionSeq.Value);

            await session.DisconnectAsync(CancellationToken.None);
            await session.OpenAsync(new TableSessionOpenRequest("ticket-1", "client-build-1"), CancellationToken.None);
            channel.Enqueue(TestWire.Welcome(33, "scope-3", 4, 9));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(TableConnectionState.Synchronizing, session.State);

            string correlation = channel.LastSent(MessageType.SyncRequest).CorrelationId;

            // A binding reset may move the stream backwards only down to the
            // baseline advertised by that new WELCOME binding.
            channel.Enqueue(TestWire.PlayerSync(33, 3, 8, correlationId: correlation));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(TableConnectionState.Synchronizing, session.State);
            Assert.Equal(11ul, session.ConfirmedCursor!.Value.ProjectionSeq.Value);

            channel.Enqueue(TestWire.PlayerSync(33, 4, 9, correlationId: correlation));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(TableConnectionState.Ready, session.State);
            Assert.Equal(9ul, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
            Assert.Equal(4ul, session.ConfirmedCursor.Value.StreamVersion.Value);
            Assert.Equal(4ul, session.PlayerSnapshot!.StateVersion);
        }

        [Fact]
        public async Task CloseAfterUnknownLookupReissuesQueryAndAllowsExactRetry()
        {
            (TableSession session, FakeTableChannel channel, _) = await TestSession.ConnectedAsync();
            await session.SubmitAsync(TestWire.Command(), CancellationToken.None);
            await session.ReconcilePendingAsync(CancellationToken.None);
            Assert.Equal(1, channel.CountSent(MessageType.ReceiptQuery));

            string firstQuery = TestWire.OutboundReceiptQuery(channel, "request-1").CorrelationId;
            string digest = session.PendingCommands.Single().DigestSha256;
            channel.Enqueue(TestWire.ReceiptLookup(firstQuery, LookupStatus.Unknown, null, digest));
            await session.PumpOnceAsync(CancellationToken.None);

            PendingCommandSnapshot pending = session.PendingCommands.Single();
            Assert.Equal(ReceiptLookupStatus.Unknown, pending.LastLookup);
            Assert.False(pending.QueryInFlight);

            channel.Enqueue(TestWire.Envelope(MessageType.Close, e => e.Close = new Close
                { Code = "TEST_TRANSIENT", Permanent = false }));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(TableConnectionState.Reconnecting, session.State);
            Assert.Null(session.PendingCommands.Single().LastLookup);

            await session.OpenAsync(new TableSessionOpenRequest("ticket-1", "client-build-1"), CancellationToken.None);
            channel.Enqueue(TestWire.Welcome(22, "scope-2", 8, 20));
            await session.PumpOnceAsync(CancellationToken.None);
            channel.Enqueue(TestWire.PlayerSync(channel, 22, 8, 20));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(TableConnectionState.Ready, session.State);
            Assert.Equal(2, channel.CountSent(MessageType.ReceiptQuery));

            Envelope secondQuery = TestWire.OutboundReceiptQuery(channel, "request-1");
            Assert.NotEqual(firstQuery, secondQuery.CorrelationId);
            PendingCommandSnapshot reissued = session.PendingCommands.Single();
            Assert.True(reissued.QueryInFlight);
            Assert.Null(reissued.LastLookup);

            channel.Enqueue(TestWire.ReceiptLookup(secondQuery.CorrelationId, LookupStatus.Unknown, null, digest));
            await session.PumpOnceAsync(CancellationToken.None);
            PendingCommandSnapshot reconciled = session.PendingCommands.Single();
            Assert.False(reconciled.QueryInFlight);
            Assert.Equal(ReceiptLookupStatus.Unknown, reconciled.LastLookup);

            CommandSubmission retry = await session.RetryPendingAsync(reconciled.Key, CancellationToken.None);
            Assert.True(retry.Sent);
            Assert.Equal(2, channel.CountSent(MessageType.Command));

            Envelope resent = channel.LastSent(MessageType.Command);
            Assert.Equal("request-1", resent.RequestId);
            Assert.Equal("scope-1", resent.Scope.ScopeId);
            Assert.Equal(digest, CanonicalPayloadDigest.Compute(resent.Command.CanonicalPayload.Span).ToHex());
        }

        [Fact]
        public async Task TransportFailureDuringLookupClearsInFlightAndReissuesAfterReconnect()
        {
            (TableSession session, FakeTableChannel channel, _) = await TestSession.ConnectedAsync();
            await session.SubmitAsync(TestWire.Command(), CancellationToken.None);
            await session.ReconcilePendingAsync(CancellationToken.None);
            Assert.Equal(1, channel.CountSent(MessageType.ReceiptQuery));
            Assert.True(session.PendingCommands.Single().QueryInFlight);

            await session.DisconnectAsync(CancellationToken.None);

            Assert.Equal(TableConnectionState.Reconnecting, session.State);
            PendingCommandSnapshot pending = session.PendingCommands.Single();
            Assert.False(pending.QueryInFlight);
            Assert.Null(pending.LastLookup);
            Assert.True(pending.DeliveryUnknown);
            Assert.True(pending.NeedsReconciliation);

            await session.OpenAsync(new TableSessionOpenRequest("ticket-1", "client-build-1"), CancellationToken.None);
            channel.Enqueue(TestWire.Welcome(22, "scope-2", 8, 20));
            await session.PumpOnceAsync(CancellationToken.None);
            channel.Enqueue(TestWire.PlayerSync(channel, 22, 8, 20));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(TableConnectionState.Ready, session.State);
            Assert.Equal(2, channel.CountSent(MessageType.ReceiptQuery));
            Assert.Equal(1, channel.CountSent(MessageType.Command));
        }

        [Fact]
        public async Task HelloSendFailureBeforeFirstWelcomeRemainsReconnectable()
        {
            var channel = new FakeTableChannel();
            var session = new TableSession(channel, new InMemoryOutboxStore());
            channel.FailNextSend = true;

            await Assert.ThrowsAsync<TableChannelClosedException>(
                () => session.OpenAsync(new TableSessionOpenRequest("ticket-1", "client-build-1"), CancellationToken.None));

            Assert.Equal(TableConnectionState.Reconnecting, session.State);

            await session.OpenAsync(new TableSessionOpenRequest("ticket-1", "client-build-1"), CancellationToken.None);
            Assert.Equal(TableConnectionState.Handshaking, session.State);
        }

        [Fact]
        public async Task ChannelOpenFailureBeforeFirstWelcomeRemainsReconnectable()
        {
            var channel = new FakeTableChannel { FailNextOpen = true };
            var session = new TableSession(channel, new InMemoryOutboxStore());

            await Assert.ThrowsAsync<TableChannelClosedException>(
                () => session.OpenAsync(new TableSessionOpenRequest("ticket-1", "client-build-1"), CancellationToken.None));

            Assert.Equal(TableConnectionState.Reconnecting, session.State);

            await session.OpenAsync(new TableSessionOpenRequest("ticket-1", "client-build-1"), CancellationToken.None);
            Assert.Equal(TableConnectionState.Handshaking, session.State);
        }

        [Fact]
        public async Task CancelledOpenBeforeFirstWelcomeRemainsReconnectable()
        {
            // Prime the outbox load and reach Reconnecting first so the
            // cancellation is observed inside the channel-open attempt rather
            // than before the state transition.
            var channel = new FakeTableChannel { HonorCancellation = true, FailNextOpen = true };
            var session = new TableSession(channel, new InMemoryOutboxStore());
            await Assert.ThrowsAsync<TableChannelClosedException>(
                () => session.OpenAsync(new TableSessionOpenRequest("ticket-1", "client-build-1"), CancellationToken.None));
            Assert.Equal(TableConnectionState.Reconnecting, session.State);

            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => session.OpenAsync(new TableSessionOpenRequest("ticket-1", "client-build-1"), cts.Token));
            }

            Assert.Equal(TableConnectionState.Reconnecting, session.State);

            await session.OpenAsync(new TableSessionOpenRequest("ticket-1", "client-build-1"), CancellationToken.None);
            Assert.Equal(TableConnectionState.Handshaking, session.State);
        }

        [Fact]
        public async Task PushWithSnapshotCursorMismatchIsRejectedWithoutMutatingCache()
        {
            (TableSession session, FakeTableChannel channel, _) = await TestSession.ConnectedAsync();
            Envelope push = TestWire.PlayerPush(11, 5, 10, 6, 11);
            push.PlayerUpdate.Snapshot.Cursor.ProjectionSeq = 12;
            channel.Enqueue(push);
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(10ul, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
            Assert.Equal(5ul, session.PlayerSnapshot!.StateVersion);
        }

        [Fact]
        public async Task PushWithDifferentPrivateSeatIsRejectedWithoutMutatingCache()
        {
            (TableSession session, FakeTableChannel channel, _) = await TestSession.ConnectedAsync();
            Envelope push = TestWire.PlayerPush(11, 5, 10, 6, 11);
            push.PlayerUpdate.Snapshot.PrivateView.Seat = 1;
            channel.Enqueue(push);
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(10ul, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
            Assert.Equal(5ul, session.PlayerSnapshot!.StateVersion);
        }

        [Fact]
        public async Task PublicPushWithDifferentSnapshotSessionIsRejectedWithoutMutatingCache()
        {
            (TableSession session, FakeTableChannel channel, _) = await TestSession.ConnectedSpectatorAsync();
            Envelope push = TestWire.PublicPush(11, 5, 10, 6, 11);
            push.PublicUpdate.Snapshot.SessionId = "different-session";
            channel.Enqueue(push);
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(10ul, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
            Assert.Equal(TestSession.SessionId, session.PublicSnapshot!.SessionId);
        }

        [Fact]
        public async Task SyncSnapshotSessionMismatchIsIgnoredUntilTheAssociatedBaselineArrives()
        {
            (TableSession session, FakeTableChannel channel, _) = await TestSession.HandshakeAsync();
            string correlation = channel.LastSent(MessageType.SyncRequest).CorrelationId;

            Envelope misrouted = TestWire.PlayerSync(11, 5, 10, correlationId: correlation);
            misrouted.SyncResponse.Player.SessionId = "different-session";
            channel.Enqueue(misrouted);
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(TableConnectionState.Synchronizing, session.State);
            Assert.Null(session.PlayerSnapshot);
            Assert.Null(session.ConfirmedCursor);

            channel.Enqueue(TestWire.PlayerSync(11, 5, 10, correlationId: correlation));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(TableConnectionState.Ready, session.State);
            Assert.Equal(TestSession.SessionId, session.PlayerSnapshot!.SessionId);
        }

        [Fact]
        public async Task SpectatorCannotRetryAPlayerOutboxRecoveredFromTheStore()
        {
            var store = new RecordingOutboxStore();
            var playerChannel = new FakeTableChannel();
            var (playerSession, _, _) = await TestSession.ConnectedAsync(channel: playerChannel, store: store);
            await playerSession.SubmitAsync(TestWire.Command(), CancellationToken.None);
            IdempotencyKey key = playerSession.PendingCommands.Single().Key;

            var spectatorChannel = new FakeTableChannel();
            var spectatorSession = new TableSession(spectatorChannel, store);
            await spectatorSession.OpenAsync(new TableSessionOpenRequest("ticket-2", "client-build-1"), CancellationToken.None);
            spectatorChannel.Enqueue(TestWire.Welcome(
                22,
                "scope-2",
                8,
                20,
                seatId: -1,
                role: global::Mahjong.Contracts.Wire.Role.Spectator));
            await spectatorSession.PumpOnceAsync(CancellationToken.None);
            spectatorChannel.Enqueue(TestWire.PublicSync(spectatorChannel, 22, 8, 20));
            await spectatorSession.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(TableConnectionState.Ready, spectatorSession.State);
            Assert.Single(spectatorSession.PendingCommands);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => spectatorSession.RetryPendingAsync(key, CancellationToken.None));
            Assert.Equal(0, spectatorChannel.CountSent(MessageType.Command));
        }

        [Fact]
        public void ClientAssemblyDoesNotReferenceDomainOrTableRuntime()
        {
            Assembly assembly = typeof(TableSession).Assembly;
            string[] references = assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToArray();

            Assert.DoesNotContain("Mahjong.Domain", references);
            Assert.DoesNotContain("Mahjong.TableRuntime", references);
            Assert.DoesNotContain("Mahjong.LocalHost", references);
            Assert.Contains("Mahjong.Contracts", references);
            Assert.Contains("Mahjong.Contracts.Wire", references);
        }

        [Fact]
        public void ConnectionStateMachineRejectsAnIllegalBaselineTransition()
        {
            var machine = new TableConnectionStateMachine();

            Assert.Throws<InvalidOperationException>(() => machine.Fire(TableConnectionTrigger.BaselineApplied));

            machine.Fire(TableConnectionTrigger.BeginConnect);
            machine.Fire(TableConnectionTrigger.TransportOpened);
            machine.Fire(TableConnectionTrigger.WelcomeAccepted);
            machine.Fire(TableConnectionTrigger.BaselineApplied);
            Assert.Equal(TableConnectionState.Ready, machine.State);
            machine.Fire(TableConnectionTrigger.Close);
            Assert.Equal(TableConnectionState.Closed, machine.State);
        }
    }
}
