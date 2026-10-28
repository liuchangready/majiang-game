using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    internal sealed class RecordingStreamSink : ITableStreamSink
    {
        private readonly object _gate = new object();
        private readonly List<TableStreamFrame> _frames = new List<TableStreamFrame>();
        private TaskCompletionSource<bool> _signal = NewSignal();
        private TaskCompletionSource<bool>? _block;
        private TaskCompletionSource<bool>? _activeBlock;
        private TaskCompletionSource<bool>? _entered;

        public void BlockNext()
        {
            lock (_gate)
            {
                _block = NewSignal();
                _activeBlock = null;
                _entered = NewSignal();
            }
        }

        public Task Entered
        {
            get
            {
                lock (_gate)
                {
                    return _entered == null
                        ? throw new InvalidOperationException("No blocked delivery was configured.")
                        : _entered.Task;
                }
            }
        }

        public void Release()
        {
            TaskCompletionSource<bool>? block;
            lock (_gate)
            {
                block = _block ?? _activeBlock;
                _block = null;
                _activeBlock = null;
            }

            block?.TrySetResult(true);
        }

        public async ValueTask PublishAsync(TableStreamFrame frame, CancellationToken cancellationToken)
        {
            TaskCompletionSource<bool>? block;
            TaskCompletionSource<bool>? entered;
            lock (_gate)
            {
                block = _block;
                entered = _entered;
                _block = null;
                _activeBlock = block;
            }

            entered?.TrySetResult(true);
            if (block != null)
            {
                // Deliberately ignore cancellation: this models a sink that has already accepted
                // a frame and completes late. The channel must never let that completion reach a
                // newer registration.
                await block.Task.ConfigureAwait(false);
            }

            TaskCompletionSource<bool> signal;
            lock (_gate)
            {
                _frames.Add(frame);
                signal = _signal;
                _signal = NewSignal();
            }

            signal.TrySetResult(true);
        }

        public int FrameCount
        {
            get
            {
                lock (_gate)
                {
                    return _frames.Count;
                }
            }
        }

        public IReadOnlyList<TableStreamFrame> Frames
        {
            get
            {
                lock (_gate)
                {
                    return _frames.ToArray();
                }
            }
        }

        public async Task<IReadOnlyList<TableStreamFrame>> WaitForCountAsync(int count, int timeoutMs = 5000)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            while (true)
            {
                Task signal;
                lock (_gate)
                {
                    if (_frames.Count >= count)
                    {
                        return _frames.ToArray();
                    }

                    signal = _signal.Task;
                }

                long remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                {
                    throw new TimeoutException(
                        "Expected " + count + " stream frames but observed " + FrameCount + ".");
                }

                await Task.WhenAny(signal, Task.Delay((int)Math.Min(remaining, 50))).ConfigureAwait(false);
            }
        }

        private static TaskCompletionSource<bool> NewSignal() =>
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class RecordingStreamTerminationSink : ITableStreamTerminationSink
    {
        private readonly object _gate = new object();
        private readonly List<TableStreamTermination> _terminations = new List<TableStreamTermination>();
        private TaskCompletionSource<bool> _signal = NewSignal();

        public void OnStreamTerminated(TableStreamTermination termination)
        {
            TaskCompletionSource<bool> signal;
            lock (_gate)
            {
                _terminations.Add(termination);
                signal = _signal;
                _signal = NewSignal();
            }

            signal.TrySetResult(true);
        }

        public IReadOnlyList<TableStreamTermination> Terminations
        {
            get
            {
                lock (_gate)
                {
                    return _terminations.ToArray();
                }
            }
        }

        public async Task<TableStreamTermination> WaitForAsync(
            Func<TableStreamTermination, bool> predicate,
            int timeoutMs = 5000)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            while (true)
            {
                Task signal;
                lock (_gate)
                {
                    for (int i = 0; i < _terminations.Count; i++)
                    {
                        if (predicate(_terminations[i]))
                        {
                            return _terminations[i];
                        }
                    }

                    signal = _signal.Task;
                }

                long remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                {
                    throw new TimeoutException("The expected stream termination was not observed.");
                }

                await Task.WhenAny(signal, Task.Delay((int)Math.Min(remaining, 50))).ConfigureAwait(false);
            }
        }

        private static TaskCompletionSource<bool> NewSignal() =>
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class TestProjectionSource : ITableProjectionContextSource
    {
        public int Calls { get; private set; }

        /// <summary>1-based call number that throws; 0 disables the injection.</summary>
        public int ThrowOnCall { get; set; }

        public bool IncludeCheat { get; set; } = true;

        public string? RoundIdOverride { get; set; }

        public StateVersion? StateVersionOverride { get; set; }

        public TableProjectionPolicy Policy { get; set; } = new TableProjectionPolicy();

        public TableSessionProjection? Session { get; set; } =
            new TableSessionProjection(false, string.Empty, null, null);

        /// <summary>Trusted private cheat aggregate; overrides the default empty wallet.</summary>
        public CheatState? CheatOverride { get; set; }

        /// <summary>Trusted public meld/discard/riichi facts; defaults to the empty fixture.</summary>
        public IReadOnlyList<TableSeatPublicFacts>? PublicFactsOverride { get; set; }

        /// <summary>Explicit authoritative event facts bound to the committed event capture.</summary>
        public IReadOnlyList<TableProjectionEventFact>? EventFacts { get; set; }

        /// <summary>Builds facts from the same frozen request; used by the real Wire joint tests.</summary>
        public Func<TableProjectionContextRequest, IReadOnlyList<TableProjectionEventFact>>? EventFactsFactory { get; set; }

        /// <summary>Negative control: omit all event facts so the production mapper must fail closed.</summary>
        public bool OmitEventFacts { get; set; }

        public IReadOnlyList<TablePublicConcealedMeldFact>? ConcealedMeldFacts { get; set; }

        /// <summary>Trusted furiten facts used when the request does not carry an actor snapshot.</summary>
        public TableFuritenSnapshot? FuritenOverride { get; set; }

        public TableProjectionContextData Capture(TableProjectionContextRequest request)
        {
            Calls++;
            if (ThrowOnCall > 0 && Calls == ThrowOnCall)
            {
                throw new InvalidOperationException("injected projection failure");
            }

            IReadOnlyList<TableProjectionEventFact> facts = OmitEventFacts
                ? Array.Empty<TableProjectionEventFact>()
                : EventFactsFactory?.Invoke(request) ??
                  EventFacts ??
                  Array.Empty<TableProjectionEventFact>();
            return new TableProjectionContextData(
                RoundIdOverride ?? request.Round.RoundId,
                StateVersionOverride ?? request.StateVersion,
                7UL,
                CheatOverride ?? (IncludeCheat ? V32Fixtures.Cheat() : null),
                PublicFactsOverride ?? V32Fixtures.BuildPublicFacts(request.Round),
                V32Fixtures.BuildPrivateFacts(request.Round, furitenSnapshot: request.Furiten ?? FuritenOverride),
                Session,
                Policy,
                facts,
                ConcealedMeldFacts);
        }
    }

    internal sealed class HashingStreamEncoder : ITableStreamFrameEncoder
    {
        public int Calls { get; private set; }

        public Func<TableStreamFrame, TableEncodedFrame>? Override { get; set; }

        public TableEncodedFrame Encode(TableStreamFrame frame)
        {
            Calls++;
            if (Override != null)
            {
                return Override(frame);
            }

            string text = Describe(frame);
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            return new TableEncodedFrame(bytes, bytes.Length);
        }

        public static string Describe(TableStreamFrame frame)
        {
            var builder = new StringBuilder();
            builder.Append(frame.IsFullSnapshot ? "BASELINE|" : "UPDATE|");
            builder.Append(frame.RoundId).Append('|');
            builder.Append(frame.StreamVersion.Value).Append('|');
            builder.Append(frame.ProjectionSeq.Value).Append('|');
            builder.Append(frame.ControlVersion).Append('|');
            builder.Append(frame.OwnerEpoch.Value).Append('|');
            if (frame.Public != null)
            {
                builder.Append(VisibilitySurface.Dump(frame.Public.View));
            }
            else if (frame.Player != null)
            {
                builder.Append(VisibilitySurface.Dump(frame.Player.Public));
                builder.Append('|');
                builder.Append(VisibilitySurface.Dump(frame.Player.Private));
                if (frame.OwnerPrivate != null)
                {
                    builder.Append('|');
                    builder.Append(VisibilitySurface.Dump(frame.OwnerPrivate));
                }
            }

            return builder.ToString();
        }
    }

    internal sealed class StreamHarness : IAsyncDisposable
    {
        private StreamHarness(
            TableActor actor,
            ManualTableClock clock,
            RecordingCommitPort commitPort,
            RecordingPublicationSink publicationSink,
            RecordingControlCommitPort? controlCommitPort,
            TestProjectionSource projectionSource,
            RecordingStreamTerminationSink terminations,
            TableStreamOptions streamOptions,
            RoundStateMachine initialRound)
        {
            Actor = actor;
            Clock = clock;
            CommitPort = commitPort;
            PublicationSink = publicationSink;
            ControlCommitPort = controlCommitPort;
            ProjectionSource = projectionSource;
            Terminations = terminations;
            StreamOptions = streamOptions;
            InitialRound = initialRound;
        }

        public TableActor Actor { get; }

        public ManualTableClock Clock { get; }

        public RecordingCommitPort CommitPort { get; }

        public RecordingPublicationSink PublicationSink { get; }

        public RecordingControlCommitPort? ControlCommitPort { get; }

        public TestProjectionSource ProjectionSource { get; }

        public RecordingStreamTerminationSink Terminations { get; }

        public TableStreamOptions StreamOptions { get; }

        public RoundStateMachine InitialRound { get; }

        public static StreamHarness Create(
            TableStreamOptions? streamOptions = null,
            TestProjectionSource? projectionSource = null,
            bool withControlCommit = false,
            int mailboxCapacity = 16,
            int controlReserve = 16,
            string roundId = "round-1",
            RoundStateMachine? initialRound = null,
            int? snapshotEventWindow = null)
        {
            var clock = new ManualTableClock { Now = 1000L };
            var commitPort = new RecordingCommitPort();
            var publicationSink = new RecordingPublicationSink(commitPort);
            var terminations = new RecordingStreamTerminationSink();
            TableStreamOptions options = streamOptions ?? new TableStreamOptions();
            options.TerminationSink ??= terminations;
            var controlCommitPort = withControlCommit ? new RecordingControlCommitPort() : null;
            var source = projectionSource ?? new TestProjectionSource();
            var actorOptions = new TableActorOptions
            {
                RoomId = "room-1",
                MailboxCapacity = mailboxCapacity,
                ControlReserve = controlReserve,
                DecisionClockConfig = new DecisionClockConfig(1000L, 0L, ReserveScope.Round),
                InitialControllers = P31Fixtures.Controllers(
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman)
            };
            if (snapshotEventWindow.HasValue)
            {
                actorOptions.SnapshotEventWindow = snapshotEventWindow.Value;
            }

            RoundStateMachine round = initialRound ?? P31Fixtures.Dealt(roundId);
            var actor = new TableActor(
                actorOptions,
                round,
                commitPort,
                clock,
                publicationSink,
                controlCommitPort,
                source,
                options);
            return new StreamHarness(
                actor,
                clock,
                commitPort,
                publicationSink,
                controlCommitPort,
                source,
                terminations,
                options,
                round);
        }

        public ValueTask<TableStreamControlResult> SubscribeSeatAsync(
            int seat,
            string session,
            ulong bindingEpoch,
            ulong syncEpoch,
            ITableStreamSink sink,
            ulong? expectedControlEpoch = 0UL)
        {
            var request = new TableStreamSubscribeRequest(
                new TableStreamIdentity(
                    new SessionId(session),
                    TableStreamAudience.ForSeat(seat),
                    new BindingEpoch(bindingEpoch),
                    new SyncEpoch(syncEpoch)),
                new OwnerEpoch(1),
                0L,
                sink,
                new RoundId(InitialRound.RoundId),
                expectedControlEpoch.HasValue ? new ControlEpoch(expectedControlEpoch.Value) : (ControlEpoch?)null);
            return Actor.SubscribeStreamAsync(request, TestContext.Current.CancellationToken);
        }

        public ValueTask<TableStreamControlResult> SubscribeSpectatorAsync(
            string session,
            ulong bindingEpoch,
            ulong syncEpoch,
            ITableStreamSink sink,
            long recoveryGeneration = 0L)
        {
            var request = new TableStreamSubscribeRequest(
                new TableStreamIdentity(
                    new SessionId(session),
                    TableStreamAudience.Spectator,
                    new BindingEpoch(bindingEpoch),
                    new SyncEpoch(syncEpoch)),
                new OwnerEpoch(1),
                recoveryGeneration,
                sink,
                new RoundId(InitialRound.RoundId));
            return Actor.SubscribeStreamAsync(request, TestContext.Current.CancellationToken);
        }

        public ValueTask<TableStreamControlResult> SyncAsync(
            TableStreamHandle handle,
            StreamCursor cursor,
            RoundId? expectedRoundId = null,
            SyncEpoch? expectedSyncEpoch = null)
        {
            return Actor.SyncStreamAsync(
                new TableStreamSyncRequest(handle, cursor, expectedRoundId, expectedSyncEpoch),
                TestContext.Current.CancellationToken);
        }

        public ValueTask<TableStreamControlResult> UnsubscribeAsync(TableStreamHandle handle)
        {
            return Actor.UnsubscribeStreamAsync(handle, TestContext.Current.CancellationToken);
        }

        public IReadOnlyList<TableCommandEnvelope> DiscardChain(int count)
        {
            return P31Fixtures.BuildDiscardChain(InitialRound, count);
        }

        public async ValueTask DisposeAsync()
        {
            await Actor.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal static class StreamAssert
    {
        public static void Baseline(TableStreamFrame frame, ulong streamVersion, ulong projectionSeq)
        {
            Assert.True(frame.IsFullSnapshot, "The frame must be an explicit full baseline.");
            Assert.Equal(new StreamVersion(streamVersion), frame.StreamVersion);
            Assert.Equal(new ProjectionSeq(projectionSeq), frame.ProjectionSeq);
            Assert.True(frame.Public != null || frame.Player != null, "A baseline must carry a snapshot.");
        }

        public static void Update(TableStreamFrame frame, ulong streamVersion, ulong projectionSeq)
        {
            Assert.False(frame.IsFullSnapshot, "An update must not claim to be a baseline.");
            Assert.Equal(new StreamVersion(streamVersion), frame.StreamVersion);
            Assert.Equal(new ProjectionSeq(projectionSeq), frame.ProjectionSeq);
            Assert.True(frame.Public != null || frame.Player != null, "An update must carry the current snapshot.");
        }
    }
}
