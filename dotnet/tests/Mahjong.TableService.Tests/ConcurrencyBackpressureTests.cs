using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Domain;
using Mahjong.TableRuntime;
using Xunit;

namespace Mahjong.TableService.Tests
{
    /// <summary>
    /// Per-table concurrency and the full backpressure chain. A connection that stops
    /// reading is closed by the frozen rate/backpressure fence (4429) while every other
    /// session on the same table keeps flowing, and a busy table never stalls another table.
    /// </summary>
    public sealed class ConcurrencyBackpressureTests
    {
        [Fact]
        public async Task FourSeatsConcurrentCommands_AllGetAuthoritativeAnswers()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            string room = "room-conc";

            var clients = new List<TableTestClient>();
            try
            {
                for (int seat = 0; seat < 4; seat++)
                {
                    clients.Add(await host.ConnectPlayerAsync(room, "acct-c-" + seat, seat));
                }

                foreach (TableTestClient client in clients)
                {
                    await client.ReadUntilAsync(
                        envelope => envelope.MessageType == MessageType.PlayerUpdate &&
                                    envelope.PlayerUpdate.IsFullSnapshot,
                        TimeSpan.FromSeconds(20));
                }

                var tasks = new List<Task>();
                foreach (TableTestClient client in clients)
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        Welcome welcome = client.Welcome!;
                        for (int i = 0; i < 5; i++)
                        {
                            string nonce = TableWire.RandomNonce();
                            await client.SendAsync(TableWire.HeartbeatPing(nonce));
                            Envelope pong = await client.ReadUntilAsync(
                                envelope => envelope.MessageType == MessageType.Heartbeat &&
                                            envelope.Heartbeat.BeatCase == Heartbeat.BeatOneofCase.Pong,
                                TimeSpan.FromSeconds(20));
                            Assert.Equal(nonce, pong.Heartbeat.Pong.Nonce);

                            string requestId = TableWire.RandomId("req");
                            byte[] payload = TableWire.SubmitActionPayload(
                                "room-conc-r1", "decision-missing-" + i, 0, (uint)ProtocolActionType.Pass);
                            await client.SendAsync(TableWire.Command(
                                welcome, requestId, Contracts.CommandKind.SubmitAction, payload));
                            await client.ReadUntilAsync(
                                envelope => envelope.MessageType == MessageType.Receipt,
                                TimeSpan.FromSeconds(20));
                        }
                    }));
                }

                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(90));
            }
            finally
            {
                foreach (TableTestClient client in clients)
                {
                    await client.DisposeAsync();
                }
            }
        }

        [Fact]
        public async Task IngressFlood_IsClosed4429_WhileOtherSessionsKeepFlowing()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            string room = "room-flood";

            TableTestClient flooder = await host.ConnectPlayerAsync(room, "acct-flood", 0);
            TableTestClient observer = await host.ConnectPlayerAsync(room, "acct-calm", 3);
            try
            {
                await flooder.ReadUntilAsync(
                    envelope => envelope.MessageType == MessageType.PlayerUpdate, TimeSpan.FromSeconds(20));
                await observer.ReadUntilAsync(
                    envelope => envelope.MessageType == MessageType.PlayerUpdate, TimeSpan.FromSeconds(20));

                // Fire-and-forget far beyond the burst capacity without reading anything.
                Welcome welcome = flooder.Welcome!;
                byte[] payload = TableWire.SubmitActionPayload(
                    "room-flood-r1", "decision-flood", 0, (uint)ProtocolActionType.Pass);
                for (int i = 0; i < 200; i++)
                {
                    await flooder.SendAsync(TableWire.Command(
                        welcome, TableWire.RandomId("flood"), Contracts.CommandKind.SubmitAction, payload));
                }

                (int code, string reason, _) = await flooder.ExpectCloseAsync(TimeSpan.FromSeconds(30));
                Assert.Equal(4429, code);
                Assert.Equal(ProtocolErrorCode.SlowConsumer, reason);
                await flooder.DisposeAsync();

                // The calm session never noticed.
                string nonce = TableWire.RandomNonce();
                await observer.SendAsync(TableWire.HeartbeatPing(nonce));
                Envelope pong = await observer.ReadUntilAsync(
                    envelope => envelope.MessageType == MessageType.Heartbeat &&
                                envelope.Heartbeat.BeatCase == Heartbeat.BeatOneofCase.Pong,
                    TimeSpan.FromSeconds(20));
                Assert.Equal(nonce, pong.Heartbeat.Pong.Nonce);
            }
            finally
            {
                await flooder.DisposeAsync();
                await observer.DisposeAsync();
            }
        }

        /// <summary>
        /// D-1-07 round 3: the negotiated snapshot limit is a PER-FRAME rule over every
        /// encoded stream frame — the frozen P3.2 stream contract makes every PUSH carry the
        /// complete authoritative snapshot, so a NON-baseline update frame is exactly as
        /// oversized as its baseline twin. Real actor-encoded frames (baseline + the trusted
        /// auto-draw's committed non-baseline update) against real sink limits: one byte over
        /// the per-frame cap is MessageTooLarge on BOTH frame kinds, while the minimum
        /// production-allowed limit (1024) accepts both untouched.
        /// </summary>
        [Fact]
        public async Task SnapshotLimit_AppliesToBaselineAndEveryFullStateUpdate()
        {
            var clock = new ProbeClock { Now = 100 };
            var registry = new InMemoryTableHostRegistry(
                new TableServiceOptions { LeaseTtlSeconds = 90 },
                new WireTableStreamContractCodec(),
                clock,
                new FixedRoundSource(roomId => new N1aTable { Dealer = 0 }.Build(roomId + "-r1", RoundPhase.Draw)));
            RoomHost host = registry.GetOrAdd("snaplimit");
            try
            {
                var capture = new FrameCaptureSink();
                TableStreamControlResult subscribed = await host.Actor.SubscribeStreamAsync(
                    new TableStreamSubscribeRequest(
                        new TableStreamIdentity(
                            new SessionId("snaplimit"),
                            TableStreamAudience.Spectator,
                            new BindingEpoch(1),
                            new SyncEpoch(1)),
                        host.OwnerEpoch,
                        recoveryGeneration: 0,
                        capture));
                TableStreamHandle streamHandle = subscribed.Handle ??
                    throw new InvalidOperationException("the subscription returned without a handle");

                TableStreamFrame baseline = await capture.Frames.Reader.ReadAsync();
                byte[] baselinePayload = baseline.EncodedPayload ??
                    throw new InvalidOperationException("the baseline arrived without an encoded payload");

                // The trusted draw commits a NON-baseline full-state update frame.
                await host.DecisionState.DriveAsync(host);
                TableStreamFrame update = await capture.Frames.Reader.ReadAsync();
                byte[] updatePayload = update.EncodedPayload ??
                    throw new InvalidOperationException("the update arrived without an encoded payload");
                Assert.False(
                    update.IsFullSnapshot,
                    "the regression shape requires a NON-baseline frame that still carries the full state");
                Assert.True(updatePayload.Length > 0 && baselinePayload.Length > 0);

                // The baseline above a tight per-frame cap: MessageTooLarge (never queued).
                // Production binds every sink to its subscription handle before frames flow,
                // and the sink's termination record requires that handle — the probe binds the
                // real one too (the identity check then also proves the frames are this
                // subscription's own).
                var tightForBaseline = new WireTableStreamSink(
                    64, 4 * 1024 * 1024, baselinePayload.Length - 1);
                tightForBaseline.Bind(streamHandle);
                await tightForBaseline.PublishAsync(baseline, CancellationToken.None);
                Assert.NotNull(tightForBaseline.Termination);
                Assert.Equal(
                    TableStreamTerminationKind.MessageTooLarge,
                    tightForBaseline.Termination!.Kind);

                // The exact review case: the NON-baseline full-state update is refused by the
                // same per-frame rule (one byte over) instead of riding a SlowConsumer fall-
                // through or skipping the snapshot check entirely.
                var tightForUpdate = new WireTableStreamSink(
                    64, 4 * 1024 * 1024, updatePayload.Length - 1);
                tightForUpdate.Bind(streamHandle);
                await tightForUpdate.PublishAsync(update, CancellationToken.None);
                Assert.NotNull(tightForUpdate.Termination);
                Assert.Equal(
                    TableStreamTerminationKind.MessageTooLarge,
                    tightForUpdate.Termination!.Kind);

                // The minimum production-allowed configuration (1024) accepts both frames and
                // stays a normal bounded queue (no termination at all).
                var loose = new WireTableStreamSink(64, 4 * 1024 * 1024, 1024);
                loose.Bind(streamHandle);
                await loose.PublishAsync(baseline, CancellationToken.None);
                await loose.PublishAsync(update, CancellationToken.None);
                Assert.Null(loose.Termination);
            }
            finally
            {
                await host.Actor.DisposeAsync();
            }
        }

        private sealed class FrameCaptureSink : ITableStreamSink
        {
            public System.Threading.Channels.Channel<TableStreamFrame> Frames =
                System.Threading.Channels.Channel.CreateUnbounded<TableStreamFrame>();

            public ValueTask PublishAsync(TableStreamFrame frame, CancellationToken cancellationToken)
            {
                return Frames.Writer.WriteAsync(frame, cancellationToken);
            }
        }

        private sealed class ProbeClock : ITableClock
        {
            public long Now = 100;

            public long MonotonicMillis => Now;

            public long UtcNowMs => 1_760_000_000_000L + Now;
        }

        [Fact]
        public async Task DownstreamStall_TriggersActorSlowConsumerTermination()
        {            // High ingress limits so the bounded backpressure chain (not the rate bucket) is
            // what stops a stalled client: every accepted sync queues a full baseline frame,
            // so a sustained flood against a trickle reader makes consumption fall behind
            // production until the bounded reply channel, then the bounded sink → the actor's
            // bounded outbox saturates — whichever fence fires first closes the session as a
            // slow consumer → close 4429. The idle-dead fence is opened with them: the stall
            // verdict must come from the saturation fence, never from the 30s inbound-idle
            // fence preempting a flood that heavy suite-wide load has slowed below its own
            // send pace (measured under two concurrent full suites: HEARTBEAT_TIMEOUT won
            // the race before the reply chain ever filled). The connection rides a raw
            // socket with a pinned tiny kernel receive buffer: loopback autotuning otherwise
            // absorbs megabytes undrained and no server-side bound ever engages; with the
            // window pinned the server's send path blocks deterministically after a few
            // hundred undrained frames and the bounded chain actually fills.
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync(
                options =>
                {
                    options.CommandRatePerSecond = 10_000;
                    options.CommandBurstCapacity = 10_000;
                    options.HeartbeatIntervalMs = 600_000;
                    options.IdleDeadMs = 3_600_000;
                });
            TableTestClient staller = await host.ConnectPlayerOverThrottledSocketAsync(
                "room-stall", "acct-stall", 0, receiveBufferSize: 4096);
            try
            {
                await staller.ReadUntilAsync(
                    envelope => envelope.MessageType == MessageType.PlayerUpdate &&
                                envelope.PlayerUpdate.IsFullSnapshot,
                    TimeSpan.FromSeconds(20));

                Welcome welcome = staller.Welcome!;
                // The flood is COMMANDS, not syncs: a command's authoritative receipt is
                // enqueued at dispatch pace (the ingress rate bucket is opened above), so
                // the bounded reply channel fills within seconds. A sync flood would pace
                // itself to the baseline-capture stall instead (measured: ~88s to the fence
                // at any trickle rate — metastable, not a saturation). The flood is bounded
                // and read-blind: reading at any steady pace lets the pipeline equilibrate
                // below the bound, while a blind client lets undrained replies pile up until
                // the bounded reply queue saturates and the next Enqueue trips the
                // slow-consumer fence (measured: outbound=256 at ~0.6s). The bound matters:
                // once the server has closed, a client that keeps sending for seconds makes
                // the server reset the connection, and that reset destroys the buffered
                // close frame before a slow reader reaches it — so the flood stops, then
                // the drain below delivers the close at full read speed.
                byte[] payload = TableWire.SubmitActionPayload(
                    "room-stall-r1", "decision-stall", 0, (uint)ProtocolActionType.Pass);
                var flood = Task.Run(async () =>
                {
                    // 8000, not fewer: measured on this loopback, socket buffering absorbs
                    // roughly 1500 receipts (~200KB) before the server's send path blocks —
                    // a 1500-command flood starves every fence (outbound=0 at teardown, no
                    // close, the verdict wait runs its full budget); ~8000 receipts (~1MB)
                    // drive the bounded reply channel to its 256 cap in about a second.
                    for (int i = 0; i < 8000; i++)
                    {
                        if (staller.CloseStatus.HasValue)
                        {
                            return;
                        }

                        try
                        {
                            await staller.SendAsync(TableWire.Command(
                                welcome, TableWire.RandomId("stall"), Contracts.CommandKind.SubmitAction, payload));
                        }
                        catch (Exception)
                        {
                            // Starved send or aborted socket; the verdict loop owns the exit.
                            return;
                        }
                    }
                });

                // The verdict loop runs in two measured phases. Phase 1 — trickle reads
                // (~4 fps) while the flood runs: the reads keep the pinned receive window
                // honest, because a fully read-blind client lets the loopback fast path
                // absorb megabytes undrained and no fence ever engages (measured:
                // outbound=0 at teardown). The flood then saturates the bounded reply
                // chain in about a second (measured: outbound=256). Phase 2 — full-speed
                // drain once the flood is done: the close bytes sit at the tail of the
                // backlog already buffered client-side, and a trickle would need minutes
                // to reach them; the reserved close jumps the queued baselines at the
                // send loop, so full-speed draining collects the verdict in seconds.
                long deadline = Environment.TickCount64 + 90_000;
                bool terminated = false;
                while (Environment.TickCount64 < deadline)
                {
                    TableWireMessage message;
                    try
                    {
                        message = await staller.ReceiveAsync(TimeSpan.FromSeconds(10));
                    }
                    catch (OperationCanceledException)
                    {
                        // Ten silent seconds mean production stopped; the deadline above
                        // stays the single authority on the verdict.
                        continue;
                    }
                    catch (WebSocketException) when (staller.CloseStatus.HasValue)
                    {
                        // The server's close decision landed and the client already parsed
                        // the close status, but the hostile flood prevented the polite
                        // handshake completion; the verdict is on the client either way.
                        Assert.Equal((WebSocketCloseStatus)4429, staller.CloseStatus);
                        Assert.Equal(ProtocolErrorCode.SlowConsumer, staller.CloseDescription);
                        terminated = true;
                        break;
                    }

                    if (message.IsClose)
                    {
                        Assert.Equal(4429, message.WsCloseCode);
                        Assert.Equal(ProtocolErrorCode.SlowConsumer, message.WsCloseReason);
                        terminated = true;
                        break;
                    }

                    if (!flood.IsCompleted)
                    {
                        // Phase 1: the stall — consumption deliberately starves the server's
                        // drain path while the flood saturates it.
                        await Task.Delay(250);
                    }
                }

                Assert.True(terminated, "the slow consumer was never terminated");
            }
            finally
            {
                await staller.DisposeAsync();
            }
        }

        [Fact]
        public async Task BusyTable_DoesNotStallAnotherTable()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            TableTestClient busy = await host.ConnectPlayerAsync("room-busy", "acct-busy", 0);
            TableTestClient quiet = await host.ConnectPlayerAsync("room-quiet", "acct-quiet", 0);
            try
            {
                // Hammer the busy table's connection with commands (its own pipeline work).
                Welcome busyWelcome = busy.Welcome!;
                byte[] payload = TableWire.SubmitActionPayload(
                    "room-busy-r1", "decision-busy", 0, (uint)ProtocolActionType.Pass);
                var hammer = Task.Run(async () =>
                {
                    for (int i = 0; i < 30; i++)
                    {
                        await busy.SendAsync(TableWire.Command(
                            busyWelcome, TableWire.RandomId("busy"), Contracts.CommandKind.SubmitAction, payload));
                        await Task.Delay(20);
                    }
                });

                // The quiet table's round-trip latency stays bounded.
                long worst = 0;
                var timer = new Stopwatch();
                for (int i = 0; i < 5; i++)
                {
                    string nonce = TableWire.RandomNonce();
                    timer.Restart();
                    await quiet.SendAsync(TableWire.HeartbeatPing(nonce));
                    Envelope pong = await quiet.ReadUntilAsync(
                        envelope => envelope.MessageType == MessageType.Heartbeat &&
                                    envelope.Heartbeat.BeatCase == Heartbeat.BeatOneofCase.Pong,
                        TimeSpan.FromSeconds(10));
                    timer.Stop();
                    Assert.Equal(nonce, pong.Heartbeat.Pong.Nonce);
                    if (timer.ElapsedMilliseconds > worst)
                    {
                        worst = timer.ElapsedMilliseconds;
                    }
                }

                await hammer.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.True(worst < 2000, "a busy sibling table must not stall another table; worst=" + worst + "ms");
            }
            finally
            {
                await busy.DisposeAsync();
                await quiet.DisposeAsync();
            }
        }
    }
}
