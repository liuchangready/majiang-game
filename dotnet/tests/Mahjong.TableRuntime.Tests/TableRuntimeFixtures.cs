using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Ai;
using Mahjong.Contracts;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    internal sealed class ManualTableClock : ITableClock
    {
        public long Now { get; set; }

        public long MonotonicMillis => Now;

        public long UtcNowMs => 1700000000000L + Now;
    }

    internal enum CommitBehaviorKind
    {
        Success = 0,
        Fail = 1,
        Cancel = 2,
        StaleOwner = 3,
        StaleState = 4
    }

    internal sealed class RecordingCommitPort : ITableCommitPort
    {
        private readonly object _gate = new object();
        private readonly Queue<CommitBehaviorKind> _behaviors = new Queue<CommitBehaviorKind>();
        private readonly List<TableCommitRequest> _requests = new List<TableCommitRequest>();
        private readonly List<TableCommitRequest> _committed = new List<TableCommitRequest>();
        private StateVersion _version = new StateVersion(0);
        private EventSeq _lastSeq = new EventSeq(0);
        private readonly OwnerEpoch _owner = new OwnerEpoch(1);
        private int _active;
        private int _maxConcurrent;
        private TaskCompletionSource<bool>? _gateSource;
        private TaskCompletionSource<bool>? _enteredSource;

        public void BlockNext()
        {
            lock (_gate)
            {
                _gateSource = NewSignal();
                _enteredSource = NewSignal();
            }
        }

        public Task Entered
        {
            get
            {
                lock (_gate)
                {
                    if (_enteredSource == null)
                    {
                        throw new InvalidOperationException("No blocked commit was configured.");
                    }

                    return _enteredSource.Task;
                }
            }
        }

        public void Release()
        {
            TaskCompletionSource<bool>? signal;
            lock (_gate)
            {
                signal = _gateSource;
                _gateSource = null;
            }

            signal?.TrySetResult(true);
        }

        public void SucceedNext()
        {
            lock (_gate)
            {
                _behaviors.Enqueue(CommitBehaviorKind.Success);
            }
        }

        public void FailNext()
        {
            lock (_gate)
            {
                _behaviors.Enqueue(CommitBehaviorKind.Fail);
            }
        }

        public void CancelNext()
        {
            lock (_gate)
            {
                _behaviors.Enqueue(CommitBehaviorKind.Cancel);
            }
        }

        public void StaleOwnerNext()
        {
            lock (_gate)
            {
                _behaviors.Enqueue(CommitBehaviorKind.StaleOwner);
            }
        }

        public void StaleStateNext()
        {
            lock (_gate)
            {
                _behaviors.Enqueue(CommitBehaviorKind.StaleState);
            }
        }

        /// <summary>Simulates a durable recovery baseline that the fake already holds.</summary>
        public void SetDurableBaseline(StateVersion stateVersion, EventSeq lastEventSeq)
        {
            lock (_gate)
            {
                _version = stateVersion;
                _lastSeq = lastEventSeq;
            }
        }

        public IReadOnlyList<TableCommitRequest> Requests
        {
            get
            {
                lock (_gate)
                {
                    return _requests.ToArray();
                }
            }
        }

        public IReadOnlyList<TableCommitRequest> CommittedRequests
        {
            get
            {
                lock (_gate)
                {
                    return _committed.ToArray();
                }
            }
        }

        public int MaxConcurrentCommits
        {
            get
            {
                lock (_gate)
                {
                    return _maxConcurrent;
                }
            }
        }

        public async ValueTask<TableCommitResult> CommitAsync(
            TableCommitRequest request,
            CancellationToken cancellationToken)
        {
            TaskCompletionSource<bool>? entered;
            TaskCompletionSource<bool>? gate;
            lock (_gate)
            {
                _active++;
                if (_active > _maxConcurrent)
                {
                    _maxConcurrent = _active;
                }

                _requests.Add(request);
                entered = _enteredSource;
                gate = _gateSource;
            }

            entered?.TrySetResult(true);
            try
            {
                if (gate != null)
                {
                    await gate.Task.ConfigureAwait(false);
                }
            }
            finally
            {
                lock (_gate)
                {
                    _active--;
                }
            }

            CommitBehaviorKind behavior;
            lock (_gate)
            {
                behavior = _behaviors.Count > 0
                    ? _behaviors.Dequeue()
                    : CommitBehaviorKind.Success;
            }

            switch (behavior)
            {
                case CommitBehaviorKind.Fail:
                    return TableCommitResult.Failed("COMMIT_FAILED", "injected commit failure");
                case CommitBehaviorKind.Cancel:
                    throw new OperationCanceledException("injected commit cancellation");
                case CommitBehaviorKind.StaleOwner:
                    return TableCommitResult.StaleOwner("injected stale owner");
                case CommitBehaviorKind.StaleState:
                    return TableCommitResult.StaleState("injected stale state");
            }

            lock (_gate)
            {
                if (request.OwnerEpoch != _owner)
                {
                    return TableCommitResult.StaleOwner("owner epoch mismatch");
                }

                if (request.ExpectedStateVersion != _version)
                {
                    return TableCommitResult.StaleState(
                        "expected " + request.ExpectedStateVersion + ", actual " + _version);
                }

                if (request.Events.Count == 0)
                {
                    return TableCommitResult.Failed("COMMIT_FAILED", "accepted command without events");
                }

                if (request.CommandVersion != _version.Next())
                {
                    return TableCommitResult.StaleState("command version is not the immediate successor");
                }

                ulong expectedSeq = _lastSeq.Value + 1;
                for (int i = 0; i < request.Events.Count; i++)
                {
                    TableEventRecord record = request.Events[i];
                    if (record.Ordinal != i ||
                        record.CommandVersion != request.CommandVersion ||
                        record.Seq.Value != expectedSeq + (ulong)i)
                    {
                        return TableCommitResult.Failed(
                            "COMMIT_FAILED",
                            "event ordering is not contiguous");
                    }
                }

                if (request.LastEventSeq.Value != expectedSeq + (ulong)request.Events.Count - 1)
                {
                    return TableCommitResult.Failed("COMMIT_FAILED", "last event sequence mismatch");
                }

                _version = request.CommandVersion;
                _lastSeq = request.LastEventSeq;
                _committed.Add(request);
                return TableCommitResult.Committed();
            }
        }

        private static TaskCompletionSource<bool> NewSignal() =>
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class RecordingPublicationSink : ITablePublicationSink
    {
        private readonly object _gate = new object();
        private readonly List<TablePublication> _publications = new List<TablePublication>();

        public RecordingPublicationSink(RecordingCommitPort commitPort)
        {
            CommitPort = commitPort;
        }

        public RecordingCommitPort CommitPort { get; }

        /// <summary>Set if a publication claimed a version that the fake did not commit first.</summary>
        public bool PublishedBeforeCommit { get; private set; }

        public void Publish(TablePublication publication)
        {
            if (publication.Receipt != null &&
                publication.Receipt.CommandVersion.HasValue &&
                !CommitPort.CommittedRequests.Any(
                    request => request.CommandVersion == publication.Receipt.CommandVersion.Value))
            {
                PublishedBeforeCommit = true;
            }

            lock (_gate)
            {
                _publications.Add(publication);
            }
        }

        public IReadOnlyList<TablePublication> Publications
        {
            get
            {
                lock (_gate)
                {
                    return _publications.ToArray();
                }
            }
        }
    }

    internal sealed class ActorHarness : IAsyncDisposable
    {
        public ActorHarness(
            TableActor actor,
            ManualTableClock clock,
            RecordingCommitPort commitPort,
            RecordingPublicationSink sink,
            RoundStateMachine initialRound)
        {
            Actor = actor;
            Clock = clock;
            CommitPort = commitPort;
            Sink = sink;
            InitialRound = initialRound;
        }

        public TableActor Actor { get; }

        public ManualTableClock Clock { get; }

        public RecordingCommitPort CommitPort { get; }

        public RecordingPublicationSink Sink { get; }

        public RoundStateMachine InitialRound { get; }

        public ValueTask<TableOperationResult> SubmitAsync(TableCommandEnvelope command) =>
            Actor.SubmitAsync(command, TestContext.Current.CancellationToken);

        public ValueTask<TableOperationResult> OpenDecisionAsync(PendingDecision decision) =>
            Actor.OpenDecisionAsync(decision, TestContext.Current.CancellationToken);

        public ValueTask<TableTickResult> TickAsync(TableTickRequest? request = null) =>
            Actor.TickAsync(request, TestContext.Current.CancellationToken);

        public ValueTask<TableTickResult> CompleteAiAsync(AiDecision decision) =>
            Actor.CompleteAiAsync(decision, TestContext.Current.CancellationToken);

        public ValueTask<TableOperationResult> BeginRecoveryAsync(string reason) =>
            Actor.BeginRecoveryAsync(reason, TestContext.Current.CancellationToken);

        public ValueTask<TableOperationResult> CompleteRecoveryAsync(TableRecoveryBaseline baseline) =>
            Actor.CompleteRecoveryAsync(baseline, TestContext.Current.CancellationToken);

        public ValueTask<TableOperationResult> ReportLeaseLostAsync(OwnerEpoch ownerEpoch, string reason) =>
            Actor.ReportLeaseLostAsync(ownerEpoch, reason, TestContext.Current.CancellationToken);

        public ValueTask<TableSnapshot> GetSnapshotAsync() =>
            Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);

        public ValueTask CloseAsync() =>
            Actor.CloseAsync(TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            await Actor.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal sealed class DiscardDecisionFixture
    {
        public DiscardDecisionFixture(
            PendingDecision pending,
            AiDecision ai,
            LegalActionOption fallback,
            LegalActionOption chosen)
        {
            Pending = pending;
            Ai = ai;
            Fallback = fallback;
            Chosen = chosen;
        }

        public PendingDecision Pending { get; }

        public AiDecision Ai { get; }

        public LegalActionOption Fallback { get; }

        public LegalActionOption Chosen { get; }
    }

    internal sealed class SingleActionDecisionFixture
    {
        public SingleActionDecisionFixture(
            PendingDecision pending,
            AiDecision ai,
            LegalActionOption action)
        {
            Pending = pending;
            Ai = ai;
            Action = action;
        }

        public PendingDecision Pending { get; }

        public AiDecision Ai { get; }

        public LegalActionOption Action { get; }
    }

    internal static class P31Fixtures
    {
        public static RoundStateMachine Dealt(string roundId, int dealer = 0)
        {
            var spec = new RoundSpec(
                roundId,
                dealer,
                1,
                0,
                0,
                new long[] { 25000, 25000, 25000, 25000 },
                0);
            TilePositionMap dealt = TilePositionMap.CreateDealt(
                RecordedRandomDraw.Standard("p31-dealt-" + roundId),
                dealer);
            return RoundStateMachine.CreateDealt(spec, dealt, RoundPhase.CheatWindow);
        }

        public static ActorHarness CreateActor(
            string roundId = "round-1",
            int mailboxCapacity = 8,
            int controlReserve = 8,
            DecisionClockConfig? decisionClockConfig = null)
        {
            var clock = new ManualTableClock { Now = 1000L };
            var commitPort = new RecordingCommitPort();
            var sink = new RecordingPublicationSink(commitPort);
            var controllers = new Dictionary<int, SeatControllerKind>
            {
                [0] = SeatControllerKind.Computer,
                [1] = SeatControllerKind.Computer,
                [2] = SeatControllerKind.Computer,
                [3] = SeatControllerKind.Computer
            };
            var options = new TableActorOptions
            {
                RoomId = "room-1",
                MailboxCapacity = mailboxCapacity,
                ControlReserve = controlReserve,
                DecisionClockConfig = decisionClockConfig ??
                    new DecisionClockConfig(1000L, 0L, ReserveScope.Round),
                InitialControllers = controllers
            };
            RoundStateMachine round = Dealt(roundId);
            var actor = new TableActor(options, round, commitPort, clock, sink);
            return new ActorHarness(actor, clock, commitPort, sink, round);
        }

        public static TableCommandEnvelope Command(
            RoundCommand command,
            int expectedStateVersion,
            string requestId,
            DecisionId? decisionId = null,
            ulong controlEpoch = 0UL,
            ulong ownerEpoch = 1UL)
        {
            return new TableCommandEnvelope(
                command,
                new RequestId(requestId),
                new StateVersion((ulong)expectedStateVersion),
                new OwnerEpoch(ownerEpoch),
                new ControlEpoch(controlEpoch),
                decisionId);
        }

        public static RoundCommand DiscardCurrent(RoundStateMachine round)
        {
            int seat = round.CurrentSeat;
            TileId tile = round.Tiles.TilesInZone(TileZone.Hand, seat)[0];
            return RoundCommand.Discard(round.RoundId, seat, tile);
        }

        public static List<TableCommandEnvelope> BuildDiscardChain(
            RoundStateMachine start,
            int count)
        {
            var chain = new List<TableCommandEnvelope>(count);
            RoundStateMachine simulation = start;
            for (int i = 0; i < count; i++)
            {
                int seat = simulation.CurrentSeat;
                TileId tile = simulation.Tiles.TilesInZone(TileZone.Hand, seat)[0];
                RoundCommand command = RoundCommand.Discard(start.RoundId, seat, tile);
                chain.Add(Command(command, i, "chain-" + i));
                RoundTransition transition = simulation.Execute(command);
                if (!transition.IsAccepted || transition.State == null)
                {
                    throw new InvalidOperationException(
                        "Discard chain fixture diverged at index " + i + ": " +
                        (transition.Error == null ? "unknown" : transition.Error.ToString()));
                }

                simulation = transition.State;
            }

            return chain;
        }

        /// <summary>
        /// Builds a two-discard decision for the current seat. The AI chooses the higher tile and
        /// the Domain timeout fallback chooses the lower tile, so a test can tell which path won.
        /// </summary>
        public static DiscardDecisionFixture BuildDiscardDecision(
            ActorHarness harness,
            ulong controlEpoch = 0UL,
            string decisionId = "d1",
            string? roundId = null)
        {
            RoundStateMachine round = harness.InitialRound;
            int seat = round.CurrentSeat;
            IReadOnlyList<TileId> hand = round.Tiles.TilesInZone(TileZone.Hand, seat);
            if (hand.Count < 2)
            {
                throw new InvalidOperationException("The fixture needs at least two hand tiles.");
            }

            TileId first = hand[0];
            TileId second = hand[1];
            TileId lower = first.Value <= second.Value ? first : second;
            TileId higher = first.Value <= second.Value ? second : first;
            var firstOption = new LegalActionOption(LegalActionKind.Discard, seat, first);
            var secondOption = new LegalActionOption(LegalActionKind.Discard, seat, second);
            var candidates = new List<LegalActionOption> { firstOption, secondOption };
            var fallback = new LegalActionOption(LegalActionKind.Discard, seat, lower);
            var chosen = new LegalActionOption(LegalActionKind.Discard, seat, higher);
            string keyRoundId = roundId ?? round.RoundId;
            var key = new DecisionKey(keyRoundId, seat, decisionId);
            var observation = new AiObservation(
                "session-1",
                key,
                (long)controlEpoch,
                0L,
                harness.Clock.Now + 1000L,
                round.Phase,
                round.DealerSeat,
                round.RoundWind,
                round.RoundNumber,
                round.HonbaSticks,
                round.RiichiSticks,
                round.Tiles.WallRemaining,
                hand,
                round.Tiles.DrawnTile?.Tile,
                Array.Empty<AiVisibleMeld>(),
                Array.Empty<AiVisibleMeld>(),
                Array.Empty<AiVisibleDiscard>(),
                Array.Empty<TileId>(),
                round.Scores,
                candidates);
            var intent = new CommandIntent(observation, chosen, "test-ai", 1);
            var decision = new AiDecision(observation, intent, 0.5d);
            var pending = new PendingDecision(key, (long)controlEpoch, candidates, round.Tiles.DrawnTile?.Tile);
            return new DiscardDecisionFixture(pending, decision, fallback, chosen);
        }

        /// <summary>Builds one-candidate (Pass/Tsumo/...) decision used for rejection-path tests.</summary>
        public static SingleActionDecisionFixture BuildSingleActionDecision(
            ActorHarness harness,
            LegalActionKind kind,
            string decisionId = "d1",
            int? seat = null,
            ulong controlEpoch = 0UL)
        {
            RoundStateMachine round = harness.InitialRound;
            int actualSeat = seat ?? round.CurrentSeat;
            IReadOnlyList<TileId> hand = round.Tiles.TilesInZone(TileZone.Hand, actualSeat);
            var action = new LegalActionOption(kind, actualSeat);
            var candidates = new List<LegalActionOption> { action };
            var key = new DecisionKey(round.RoundId, actualSeat, decisionId);
            var observation = new AiObservation(
                "session-1",
                key,
                (long)controlEpoch,
                0L,
                harness.Clock.Now + 1000L,
                round.Phase,
                round.DealerSeat,
                round.RoundWind,
                round.RoundNumber,
                round.HonbaSticks,
                round.RiichiSticks,
                round.Tiles.WallRemaining,
                hand,
                round.Tiles.DrawnTile?.Tile,
                Array.Empty<AiVisibleMeld>(),
                Array.Empty<AiVisibleMeld>(),
                Array.Empty<AiVisibleDiscard>(),
                Array.Empty<TileId>(),
                round.Scores,
                candidates);
            var intent = new CommandIntent(observation, action, "test-ai", 1);
            var decision = new AiDecision(observation, intent, 0.5d);
            var pending = new PendingDecision(key, (long)controlEpoch, candidates, round.Tiles.DrawnTile?.Tile);
            return new SingleActionDecisionFixture(pending, decision, action);
        }
    }
}
