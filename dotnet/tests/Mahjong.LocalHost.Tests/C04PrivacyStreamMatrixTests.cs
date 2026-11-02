using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Xunit;
using DecisionView = Mahjong.Contracts.Wire.DecisionView;
using PlayerSnapshot = Mahjong.Contracts.Wire.PlayerSnapshot;
using PublicSnapshot = Mahjong.Contracts.Wire.PublicSnapshot;

namespace Mahjong.LocalHost.Tests
{
    /// <summary>
    /// C04-D05 acceptance-matrix scenarios. Every test uses the real local room,
    /// production channel and public TableSession entry points; no test writes
    /// authority state or substitutes a scripted channel.
    /// </summary>
    public sealed class C04PrivacyStreamMatrixTests
    {
        private static readonly TimeSpan MatrixTimeout = TimeSpan.FromSeconds(45);

        [Fact]
        public async Task Public_and_player_snapshots_are_typed_and_seat_owned_on_real_channels()
        {
            await using var harness = new P41Harness();
            harness.Options.SeatPlan = LocalTableSeatPlan.AllFourHumanSeats;
            await using LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, TestContext.Current.CancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(MatrixTimeout);
            var tickets = new LocalTableConnectionTicket[4];
            var channels = new ITableChannel[4];
            var sessions = new TableSession[4];
            try
            {
                for (int seat = 0; seat < 4; seat++)
                {
                    tickets[seat] = room.CreatePlayerTicket(seat);
                    channels[seat] = await room.ConnectAsync(tickets[seat], timeout.Token);
                    sessions[seat] = new TableSession(channels[seat], new InMemoryOutboxStore());
                    await sessions[seat].OpenAsync(new TableSessionOpenRequest(tickets[seat].Value, "privacy-seat-" + seat), timeout.Token);
                }
                foreach (TableSession session in sessions)
                {
                    for (int i = 0; i < 16 && session.State != TableConnectionState.Ready; i++)
                        await session.PumpOnceAsync(timeout.Token);
                    Assert.Equal(TableConnectionState.Ready, session.State);
                }
                for (int owner = 0; owner < 4; owner++)
                {
                    var snapshot = Assert.IsType<PlayerSnapshot>(sessions[owner].PlayerSnapshot);
                    Assert.Equal(owner, snapshot.PrivateView.Seat);
                    Assert.Equal(owner, sessions[owner].Binding!.SeatId);
                    Assert.NotEmpty(snapshot.PrivateView.ClosedTiles);
                    Assert.InRange(snapshot.PrivateView.ClosedTiles.Count + (snapshot.PrivateView.HasDrawnTileId ? 1 : 0), 13, 14);
                    for (int foreign = 0; foreign < 4; foreign++)
                        if (foreign != owner) Assert.NotEqual(foreign, snapshot.PrivateView.Seat);
                }
                LocalTableConnectionTicket spectatorTicket = room.CreateSpectatorTicket();
                ITableChannel spectatorChannel = await room.ConnectAsync(spectatorTicket, timeout.Token);
                var spectator = new TableSession(spectatorChannel, new InMemoryOutboxStore());
                await spectator.OpenAsync(new TableSessionOpenRequest(spectatorTicket.Value, "privacy-spectator"), timeout.Token);
                for (int i = 0; i < 16 && spectator.State != TableConnectionState.Ready; i++)
                    await spectator.PumpOnceAsync(timeout.Token);
                Assert.Equal(TableConnectionState.Ready, spectator.State);
                Assert.Null(spectator.PlayerSnapshot);
                var publicSnapshot = Assert.IsType<PublicSnapshot>(spectator.PublicSnapshot);
                Assert.NotEmpty(publicSnapshot.RoundId);
                Assert.NotNull(publicSnapshot.View);
                await spectator.CloseAsync("TEST_DONE", CancellationToken.None);
                await spectatorChannel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }
            finally
            {
                for (int i = 0; i < sessions.Length; i++)
                {
                    if (sessions[i] != null) try { await sessions[i].CloseAsync("TEST_DONE", CancellationToken.None); } catch { }
                    if (channels[i] != null) try { await channels[i].CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None); } catch { }
                }
            }
        }

        [Fact]
        public async Task Legal_public_commands_produce_non_empty_owner_sleeve_and_fee_ledger()
        {
            await using var harness = new P41Harness();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(MatrixTimeout);
            (ITableChannel channel, TableSession session) = await OpenLegacySessionAsync(harness, timeout.Token);
            using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            Task pump = PumpAsync(session, pumpCts.Token);
            try
            {
                LocalPrivacyScenarioFacts facts = await LocalPrivacyScenario.CaptureSleeveAndFeeAsync(session, timeout.Token);
                Assert.False(string.IsNullOrWhiteSpace(facts.RoundId));
                Assert.True(facts.BaselineStateVersion < facts.TileMarkStateVersion);
                Assert.True(facts.TileMarkStateVersion < facts.SleeveStateVersion);
                Assert.True(facts.BaselineLedgerEntryCount >= 0);
                Assert.True(facts.TileMarkLedgerEntryCount > facts.BaselineLedgerEntryCount);
                Assert.True(facts.SleeveLedgerEntryCount > facts.TileMarkLedgerEntryCount);
                Assert.True(facts.TileMarkCheatPoints < facts.BaselineCheatPoints);
                Assert.True(facts.SleeveCheatPoints < facts.TileMarkCheatPoints);
                Assert.Equal(4, facts.MarkedTiles.Count);
                Assert.True(facts.MarkedTileCount >= 4);
                Assert.False(string.IsNullOrWhiteSpace(facts.SleeveOfferId));
                Assert.NotEmpty(facts.SleeveOfferTiles);
                Assert.True(facts.SleeveOfferTiles.Count >= 2);
            }
            finally
            {
                pumpCts.Cancel();
                try { await pump; } catch { }
                await session.CloseAsync("TEST_DONE", CancellationToken.None);
                await channel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }
        }

        [Fact]
        public async Task Slow_real_channel_queue_saturates_without_stopping_room_or_other_bindings()
        {
            await using var harness = new P41Harness();
            await using LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, TestContext.Current.CancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(MatrixTimeout);

            LocalTableConnectionTicket playerTicket = room.CreatePlayerTicket(0);
            ITableChannel playerChannel = await room.ConnectAsync(playerTicket, timeout.Token);
            var player = new TableSession(playerChannel, new InMemoryOutboxStore());
            LocalTableConnectionTicket slowTicket = room.CreateSpectatorTicket();
            ITableChannel slowChannel = await room.ConnectAsync(slowTicket, timeout.Token);
            var slow = new TableSession(slowChannel, new InMemoryOutboxStore());
            try
            {
                await player.OpenAsync(new TableSessionOpenRequest(playerTicket.Value, "slow-player"), timeout.Token);
                await PumpUntilReadyAsync(player, timeout.Token);

                await slow.OpenAsync(new TableSessionOpenRequest(slowTicket.Value, "slow-spectator"), timeout.Token);
                await PumpUntilReadyAsync(slow, timeout.Token);
                Assert.Equal(ProtocolRole.Spectator, slow.Binding!.Role);
                Assert.Null(slow.PlayerSnapshot);
                Assert.NotNull(slow.PublicSnapshot);
                string slowSessionId = slow.Binding.SessionId.Value;
                ulong slowBindingEpoch = slow.Binding.BindingEpoch.Value;

                int accepted = 0;
                try
                {
                    for (int i = 0; i < 600; i++)
                    {
                        await slowChannel.SendAsync(
                            WireCodec.Encode(HeartbeatEnvelope(slowSessionId, slowBindingEpoch, harness.Options.RoomId, i)),
                            timeout.Token);
                        accepted++;
                    }
                }
                catch (TableChannelClosedException)
                {
                    // The bounded queue closed the slow consumer; that is the expected
                    // saturation path and must not propagate to another binding.
                }

                Assert.True(accepted >= 257, "The slow channel never reached the production queue bound.");
                await Assert.ThrowsAnyAsync<TableChannelClosedException>(
                    async () => await slowChannel.ReceiveAsync(timeout.Token));

                Assert.Equal(TableConnectionState.Ready, player.State);
                Assert.NotNull(player.PlayerSnapshot);

                LocalTableConnectionTicket replacementTicket = room.CreateSpectatorTicket();
                ITableChannel replacementChannel = await room.ConnectAsync(replacementTicket, timeout.Token);
                var replacement = new TableSession(replacementChannel, new InMemoryOutboxStore());
                try
                {
                    await replacement.OpenAsync(new TableSessionOpenRequest(replacementTicket.Value, "slow-replacement"), timeout.Token);
                    await PumpUntilReadyAsync(replacement, timeout.Token);
                    Assert.NotNull(replacement.PublicSnapshot);
                }
                finally
                {
                    await replacement.CloseAsync("TEST_DONE", CancellationToken.None);
                    await replacementChannel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
                }
            }
            finally
            {
                try { await player.CloseAsync("TEST_DONE", CancellationToken.None); } catch { }
                try { await playerChannel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None); } catch { }
                try { await slow.CloseAsync("TEST_DONE", CancellationToken.None); } catch { }
                try { await slowChannel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None); } catch { }
            }
        }

        [Fact]
        public async Task Delayed_real_private_wire_from_old_seat_binding_is_ignored_by_new_session()
        {
            await using var harness = new P41Harness();
            harness.Options.SeatPlan = LocalTableSeatPlan.AllFourHumanSeats;
            await using LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, TestContext.Current.CancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(MatrixTimeout);

            LocalTableConnectionTicket seatZeroTicket = room.CreatePlayerTicket(0);
            ITableChannel seatZeroInner = await room.ConnectAsync(seatZeroTicket, timeout.Token);
            var seatZeroChannel = new CaptureChannel(seatZeroInner);
            var seatZero = new TableSession(seatZeroChannel, new InMemoryOutboxStore());
            LocalTableConnectionTicket seatOneTicket;
            ITableChannel seatOneInner;
            var seatOneChannel = default(CaptureChannel);
            TableSession? seatOne = null;
            try
            {
                await seatZero.OpenAsync(new TableSessionOpenRequest(seatZeroTicket.Value, "delayed-v0"), timeout.Token);
                await PumpUntilReadyAsync(seatZero, timeout.Token);
                var oldPrivate = Assert.IsType<PlayerSnapshot>(seatZero.PlayerSnapshot);
                byte[] delayedFrame = seatZeroChannel.CapturedPlayerSnapshotFrame
                    ?? throw new InvalidOperationException("No real serialized player snapshot was captured.");
                WireDecodeResult decodedOld = WireCodec.Decode(delayedFrame);
                Assert.True(decodedOld.IsOk);
                Assert.Equal(0, decodedOld.Envelope!.SeatId);
                Assert.Equal(seatZero.Binding!.SessionId.Value, decodedOld.Envelope.SessionId);

                seatOneTicket = room.CreatePlayerTicket(1);
                seatOneInner = await room.ConnectAsync(seatOneTicket, timeout.Token);
                seatOneChannel = new CaptureChannel(seatOneInner);
                seatOne = new TableSession(seatOneChannel, new InMemoryOutboxStore());
                await seatOne.OpenAsync(new TableSessionOpenRequest(seatOneTicket.Value, "delayed-v1"), timeout.Token);
                await PumpUntilReadyAsync(seatOne, timeout.Token);
                var current = Assert.IsType<PlayerSnapshot>(seatOne.PlayerSnapshot);
                Assert.Equal(1, current.PrivateView.Seat);
                string currentTiles = string.Join(",", current.PrivateView.ClosedTiles);

                seatOneChannel.InjectNextReceive(delayedFrame);
                await seatOne.PumpOnceAsync(timeout.Token);
                var after = Assert.IsType<PlayerSnapshot>(seatOne.PlayerSnapshot);
                Assert.Equal(seatOne.Binding!.SessionId.Value, after.SessionId);
                Assert.Equal(1, after.PrivateView.Seat);
                Assert.Equal(currentTiles, string.Join(",", after.PrivateView.ClosedTiles));
                Assert.NotEqual(seatZero.Binding.SessionId.Value, seatOne.Binding.SessionId.Value);
            }
            finally
            {
                if (seatOne != null)
                {
                    try { await seatOne.CloseAsync("TEST_DONE", CancellationToken.None); } catch { }
                    try { await seatOneChannel!.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None); } catch { }
                }
                try { await seatZero.CloseAsync("TEST_DONE", CancellationToken.None); } catch { }
                try { await seatZeroChannel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None); } catch { }
            }
        }

        [Fact]
        public async Task New_room_cold_reopen_queries_original_scope_without_resubmitting_or_duplicating()
        {
            await using var harness = new P41Harness();
            harness.Options.AutoPlay = false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(MatrixTimeout);
            var outbox = new InMemoryOutboxStore();
            LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, timeout.Token);
            TableSession? firstSession = null;
            ITableChannel? firstChannel = null;
            LocalTableRoom? recoveredRoom = null;
            CaptureChannel? recoveredChannel = null;
            TableSession? recoveredSession = null;
            try
            {
                LocalTableConnectionTicket firstTicket = room.CreatePlayerTicket(0);
                firstChannel = await room.ConnectAsync(firstTicket, timeout.Token);
                firstSession = new TableSession(firstChannel, outbox);
                await firstSession.OpenAsync(new TableSessionOpenRequest(firstTicket.Value, "recovery-first"), timeout.Token);
                await PumpUntilReadyAsync(firstSession, timeout.Token);
                PlayerSnapshot baseline = Assert.IsType<PlayerSnapshot>(firstSession.PlayerSnapshot);
                var decision = Assert.IsType<DecisionView>(baseline.PrivateView.Decision);
                var discard = decision.Actions.First(action => action.ActionType == (uint)ProtocolActionType.DiscardTile);
                BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                    baseline.RoundId,
                    decision.DecisionId,
                    decision.ControlEpoch,
                    ProtocolActionType.DiscardTile,
                    tileIds: discard.TileIds);
                var request = new RequestId("privacy-recovery-original");
                RequestScopeId originalScope = firstSession.Binding!.Scope.ScopeId;
                string originalDigest = CanonicalPayloadDigest.Compute(payload).ToHex();
                var command = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    payload,
                    request,
                    originalScope,
                    new StateVersion(baseline.StateVersion),
                    new RoundId(baseline.RoundId),
                    decisionId: new DecisionId(decision.DecisionId),
                    controlEpoch: new ControlEpoch(decision.ControlEpoch));
                CommandSubmission submitted = await firstSession.SubmitAsync(command, timeout.Token);
                Assert.True(submitted.Sent);

                LocalTableRecoveryFacts before = (await LocalTableHost.CaptureRecoveryAsync(harness.Options.SaveDirectory, timeout.Token)).Facts;
                LocalTableRecoveryRequestFact original = Assert.Single(before.Requests,
                    fact => fact.AccountId == "local-" + harness.Options.RoomId + "-seat0" &&
                        fact.ScopeId == originalScope.Value && fact.RequestId == request.Value);
                Assert.Equal("APPLIED", original.Status);

                await firstSession.CloseAsync("ROOM_COLD_REOPEN", CancellationToken.None);
                await firstChannel.CloseAsync(new TableChannelCloseReason("ROOM_COLD_REOPEN", true), CancellationToken.None);
                await room.DisposeAsync();

                recoveredRoom = await LocalTableHost.OpenRoomAsync(harness.Options, timeout.Token);
                LocalTableConnectionTicket recoveredTicket = recoveredRoom.CreatePlayerTicket(0);
                ITableChannel rawRecovered = await recoveredRoom.ConnectAsync(recoveredTicket, timeout.Token);
                recoveredChannel = new CaptureChannel(rawRecovered);
                recoveredSession = new TableSession(recoveredChannel, outbox);
                TableReceiptResolvedEventArgs? reconciled = null;
                recoveredSession.ReceiptResolved += (_, args) =>
                {
                    if (args.Command.Key.RequestId.Value == request.Value) reconciled = args;
                };
                await recoveredSession.OpenAsync(new TableSessionOpenRequest(recoveredTicket.Value, "recovery-reopened"), timeout.Token);
                await PumpUntilReadyAsync(recoveredSession, timeout.Token);
                for (int i = 0; i < 30 && recoveredSession.PendingCommands.Count > 0; i++)
                {
                    await recoveredSession.ReconcilePendingAsync(timeout.Token);
                    await recoveredSession.PumpOnceAsync(timeout.Token);
                }

                Assert.Empty(recoveredSession.PendingCommands);
                Assert.NotNull(reconciled);
                Assert.True(reconciled!.Resolution.FromReceiptLookup);
                Assert.Equal(Mahjong.Contracts.ReceiptStatus.Applied, reconciled.Resolution.ReceiptStatus);
                Assert.Equal(originalScope.Value, reconciled.Command.Key.ScopeId.Value);
                Assert.Equal(request.Value, reconciled.Command.Key.RequestId.Value);
                Assert.Equal(originalDigest, reconciled.Resolution.CanonicalPayloadSha256);
                Assert.Equal(0, recoveredChannel.CommandFrames);
                Assert.True(recoveredChannel.ReceiptQueryFrames > 0);

                LocalTableRecoveryFacts after = (await LocalTableHost.CaptureRecoveryAsync(harness.Options.SaveDirectory, timeout.Token)).Facts;
                Assert.Equal(before.StateVersion, after.StateVersion);
                Assert.Equal(before.EventSeq, after.EventSeq);
                Assert.Equal(before.StateFingerprint, after.StateFingerprint);
                Assert.Equal(before.Ledger.EntriesSha256, after.Ledger.EntriesSha256);
                Assert.Equal(before.Ledger.BalancesSha256, after.Ledger.BalancesSha256);
                Assert.Equal(before.Physical.TilesSha256, after.Physical.TilesSha256);
            }
            finally
            {
                if (recoveredSession != null) try { await recoveredSession.CloseAsync("TEST_DONE", CancellationToken.None); } catch { }
                if (recoveredChannel != null) try { await recoveredChannel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None); } catch { }
                if (recoveredRoom != null) try { await recoveredRoom.DisposeAsync(); } catch { }
                if (firstSession != null) try { await firstSession.CloseAsync("TEST_DONE", CancellationToken.None); } catch { }
                if (firstChannel != null) try { await firstChannel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None); } catch { }
                try { await room.DisposeAsync(); } catch { }
            }
        }

        private static async Task<(ITableChannel, TableSession)> OpenLegacySessionAsync(
            P41Harness harness,
            CancellationToken cancellationToken)
        {
            ITableChannel channel = await LocalTableHost.CreateChannelAsync(harness.Options, cancellationToken);
            string channelTicket = LocalTableHost.IssueTicket(harness.Options);
            await channel.OpenAsync(new TableChannelConnectRequest(channelTicket, "privacy-matrix", null), cancellationToken);
            var session = new TableSession(channel, new InMemoryOutboxStore());
            string sessionTicket = LocalTableHost.IssueTicket(harness.Options);
            await session.OpenAsync(new TableSessionOpenRequest(sessionTicket, "privacy-matrix"), cancellationToken);
            await PumpUntilReadyAsync(session, cancellationToken);
            return (channel, session);
        }

        private static async Task PumpUntilReadyAsync(TableSession session, CancellationToken cancellationToken)
        {
            for (int i = 0; i < 32 && session.State != TableConnectionState.Ready; i++)
            {
                if (session.State == TableConnectionState.Faulted)
                    throw new InvalidOperationException("Session faulted before Ready: " + (session.LastError ?? "<no error>"));
                await session.PumpOnceAsync(cancellationToken);
            }
            Assert.Equal(TableConnectionState.Ready, session.State);
        }

        private static async Task PumpAsync(TableSession session, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                    await session.PumpOnceAsync(cancellationToken);
            }
            catch (Exception exception) when (
                exception is OperationCanceledException ||
                exception is TableChannelClosedException ||
                exception is InvalidOperationException)
            {
            }
        }

        private static Envelope HeartbeatEnvelope(string sessionId, ulong bindingEpoch, string roomId, int sequence)
        {
            return new Envelope
            {
                ProtocolMajor = Mahjong.Contracts.ProtocolConstants.Major,
                ProtocolMinor = Mahjong.Contracts.ProtocolConstants.Minor,
                RulesetVersion = Mahjong.Contracts.ProtocolConstants.RulesetVersion,
                ProjectionVersion = Mahjong.Contracts.ProtocolConstants.ProjectionVersion,
                MessageType = MessageType.Heartbeat,
                SessionId = sessionId,
                RoomId = roomId,
                SeatId = -1,
                BindingEpoch = bindingEpoch,
                Heartbeat = new Heartbeat
                {
                    Ping = new Ping { Nonce = sequence.ToString("x16", System.Globalization.CultureInfo.InvariantCulture) }
                }
            };
        }

        private sealed class CaptureChannel : ITableChannel
        {
            private readonly ITableChannel _inner;
            private byte[]? _injectedFrame;

            public CaptureChannel(ITableChannel inner) => _inner = inner;

            public bool IsOpen => _inner.IsOpen;
            public byte[]? CapturedPlayerSnapshotFrame { get; private set; }
            public int CommandFrames { get; private set; }
            public int ReceiptQueryFrames { get; private set; }

            public Task OpenAsync(TableChannelConnectRequest request, CancellationToken cancellationToken) =>
                _inner.OpenAsync(request, cancellationToken);

            public Task SendAsync(ReadOnlyMemory<byte> wireEnvelope, CancellationToken cancellationToken)
            {
                WireDecodeResult decoded = WireCodec.Decode(wireEnvelope.ToArray());
                if (decoded.IsOk && decoded.Envelope != null)
                {
                    if (decoded.Envelope.MessageType == MessageType.Command) CommandFrames++;
                    if (decoded.Envelope.MessageType == MessageType.ReceiptQuery) ReceiptQueryFrames++;
                }
                return _inner.SendAsync(wireEnvelope, cancellationToken);
            }

            public async Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
            {
                byte[]? injected = Interlocked.Exchange(ref _injectedFrame, null);
                if (injected != null) return new ReadOnlyMemory<byte>(injected);
                ReadOnlyMemory<byte> frame = await _inner.ReceiveAsync(cancellationToken);
                WireDecodeResult decoded = WireCodec.Decode(frame.ToArray());
                if (decoded.IsOk && decoded.Envelope?.MessageType == MessageType.SyncResponse &&
                    decoded.Envelope.SyncResponse?.Target == SyncTarget.PlayerStream)
                    CapturedPlayerSnapshotFrame = frame.ToArray();
                return frame;
            }

            public void InjectNextReceive(byte[] frame)
            {
                if (frame == null) throw new ArgumentNullException(nameof(frame));
                if (Interlocked.CompareExchange(ref _injectedFrame, (byte[])frame.Clone(), null) != null)
                    throw new InvalidOperationException("An injected wire frame is already pending.");
            }

            public Task CloseAsync(TableChannelCloseReason reason, CancellationToken cancellationToken) =>
                _inner.CloseAsync(reason, cancellationToken);
        }
    }
}
