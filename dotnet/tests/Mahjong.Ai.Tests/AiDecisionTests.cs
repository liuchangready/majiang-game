using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.Ai.Tests
{
    public class AiDecisionTests
    {
        private const string RoundId = "round-20261023-1";
        private const string DecisionId = "turn-round-20261023-1-7-seat1";
        private const int AiSeat = 1;
        private const long Epoch = 0L;
        private const long Deadline = 23000L;

        [Fact]
        public async Task ReasoningUsesOnlyTheSeatObservationAndProducesARecordedIntent()
        {
            AiObservation observation = Observation();
            var authority = new CountingAuthorityPort();
            LegalActionOption selected = observation.LegalActions[0];
            var service = new AiDecisionService(
                new FixedStrategy(new AiStrategicChoice(selected, 12.5d)),
                authority);

            AiDecision decision = await service.ReasonAsync(
                observation,
                TestContext.Current.CancellationToken);

            // The authority port is only for admission; reasoning never reads it.
            Assert.Equal(0, authority.Captures);
            Assert.Same(observation, decision.Observation);
            Assert.Same(selected, decision.Intent.Action);
            Assert.Equal(observation.DecisionKey, decision.Intent.DecisionKey);
            Assert.Equal(observation.ControlEpoch, decision.Intent.ControlEpoch);
            Assert.Equal(observation.StateVersion, decision.Intent.ObservationStateVersion);
            Assert.Equal("test-ai", decision.Intent.AlgorithmId);
            Assert.Equal(1, decision.Intent.AlgorithmVersion);
            Assert.Equal(12.5d, decision.HeuristicScore);
            Assert.Equal(64, decision.AuthorityHash.Length);
        }

        [Fact]
        public async Task ReasoningRunsAsynchronouslyAndReturnsControlBeforeTheStrategyFinishes()
        {
            AiObservation observation = Observation();
            using (var gate = new ManualResetEventSlim(false))
            {
                var service = new AiDecisionService(new DelegateStrategy(
                    (view, token) =>
                    {
                        Assert.True(gate.Wait(TimeSpan.FromSeconds(10)), "Strategy gate was not released.");
                        return new AiStrategicChoice(view.LegalActions[0], 0d);
                    }));

                Task<AiDecision> pending = service.ReasonAsync(
                    observation,
                    TestContext.Current.CancellationToken);
                Assert.False(pending.IsCompleted);

                gate.Set();
                AiDecision decision = await pending;
                Assert.Equal(observation.DecisionKey, decision.Intent.DecisionKey);
            }
        }

        [Fact]
        public async Task SubmissionAcceptsCurrentResultAndFeedsTheDomainDeadlineArbiter()
        {
            AiObservation observation = Observation();
            var service = new AiDecisionService(new FixedStrategy(
                new AiStrategicChoice(observation.LegalActions[0], 1d)));
            AiDecision decision = await service.ReasonAsync(
                observation,
                TestContext.Current.CancellationToken);
            AiAuthoritySnapshot authority = Authority(observation);

            AiSubmissionResult result = service.Submit(decision, authority, 1000L);

            Assert.True(result.IsAccepted, result.Detail);
            Assert.Equal(TickRejectionCode.None, result.Code);
            Assert.NotNull(result.Submission);
            AiDecisionSubmission submission = result.Submission!;
            Assert.Equal(decision.Intent.DecisionKey, submission.Key);
            Assert.Equal(decision.Intent.ControlEpoch, submission.ControlEpoch);
            Assert.Same(decision.Intent.Action, submission.Action);
            Assert.Equal(1000L, submission.SubmittedAtMillis);
            Assert.Same(decision.Intent, result.RecordedIntent);

            // P1.3 integration: the Domain arbiter accepts the exact same intent.
            var clock = new DecisionClock();
            DecisionTiming timing = clock.Open(decision.Intent.DecisionKey, 0L);
            Assert.Equal(Deadline, timing.DeadlineMillis);
            var control = new ControlRegistry();
            var pending = new PendingDecision(
                decision.Intent.DecisionKey,
                decision.Intent.ControlEpoch,
                observation.LegalActions,
                observation.MyDrawnTileId);

            TickResult tick = new DeadlineArbiter(clock, control).ProcessTick(
                new TickInput(
                    observation.RoundId,
                    1000L,
                    new[] { pending },
                    new[] { submission }));

            Assert.Empty(tick.Timeouts);
            Assert.Empty(tick.RejectedAi);
            Assert.Single(tick.AcceptedAi);
            Assert.Same(decision.Intent.Action, tick.AcceptedAi[0].Action);
        }

        [Fact]
        public async Task SubmissionRejectsStaleRoundDecisionEpochAndMissingDecision()
        {
            AiObservation observation = Observation();
            var service = new AiDecisionService(new ThrowingStrategy());
            AiDecision decision = await DecisionAsync(observation);
            int seat = observation.Seat;

            AiSubmissionResult staleRound = service.Submit(
                decision,
                new AiAuthoritySnapshot(
                    seat,
                    "round-other",
                    new DecisionKey("round-other", seat, observation.DecisionId),
                    Epoch,
                    true,
                    false,
                    Deadline,
                    observation.LegalActions),
                1000L);
            Assert.Equal(TickRejectionCode.StaleRound, staleRound.Code);
            Assert.Null(staleRound.Submission);

            AiSubmissionResult wrongSeat = service.Submit(
                decision,
                new AiAuthoritySnapshot(
                    2,
                    RoundId,
                    new DecisionKey(RoundId, 2, observation.DecisionId),
                    Epoch,
                    true,
                    false,
                    Deadline,
                    Array.Empty<LegalActionOption>()),
                1000L);
            Assert.Equal(TickRejectionCode.StaleDecision, wrongSeat.Code);
            Assert.Null(wrongSeat.Submission);

            AiSubmissionResult staleDecision = service.Submit(
                decision,
                new AiAuthoritySnapshot(
                    seat,
                    RoundId,
                    new DecisionKey(RoundId, seat, "different-decision"),
                    Epoch,
                    true,
                    false,
                    Deadline,
                    observation.LegalActions),
                1000L);
            Assert.Equal(TickRejectionCode.StaleDecision, staleDecision.Code);
            Assert.Null(staleDecision.Submission);

            AiSubmissionResult staleEpoch = service.Submit(
                decision,
                new AiAuthoritySnapshot(
                    seat,
                    RoundId,
                    observation.DecisionKey,
                    Epoch + 1L,
                    true,
                    false,
                    Deadline,
                    observation.LegalActions),
                1000L);
            Assert.Equal(TickRejectionCode.StaleEpoch, staleEpoch.Code);

            AiSubmissionResult noDecision = service.Submit(
                decision,
                new AiAuthoritySnapshot(
                    seat,
                    RoundId,
                    null,
                    Epoch,
                    true,
                    false,
                    0L,
                    Array.Empty<LegalActionOption>()),
                1000L);
            Assert.Equal(TickRejectionCode.StaleDecision, noDecision.Code);
            Assert.Null(noDecision.Submission);
        }

        [Fact]
        public async Task SubmissionRejectsLateHumanPausedAndIllegalResults()
        {
            AiObservation observation = Observation();
            var service = new AiDecisionService(new ThrowingStrategy());
            AiDecision decision = await DecisionAsync(observation);

            AiSubmissionResult late = service.Submit(
                decision,
                Authority(observation),
                Deadline);
            Assert.Equal(TickRejectionCode.DeadlineExpired, late.Code);
            Assert.Null(late.Submission);

            AiSubmissionResult oneMillisecondEarly = service.Submit(
                decision,
                Authority(observation),
                Deadline - 1L);
            Assert.True(oneMillisecondEarly.IsAccepted, oneMillisecondEarly.Detail);

            AiSubmissionResult human = service.Submit(
                decision,
                Authority(observation, automated: false),
                1000L);
            Assert.Equal(TickRejectionCode.SeatNotAutomated, human.Code);

            AiSubmissionResult paused = service.Submit(
                decision,
                Authority(observation, paused: true),
                1000L);
            Assert.Equal(TickRejectionCode.ClockPaused, paused.Code);

            LegalActionOption pass = observation.LegalActions.Single(
                action => action.Kind == LegalActionKind.Pass);
            AiSubmissionResult illegal = service.Submit(
                decision,
                Authority(observation, legalActions: new[] { pass }),
                1000L);
            Assert.Equal(TickRejectionCode.IllegalAction, illegal.Code);
            Assert.Null(illegal.Submission);
        }

        [Fact]
        public async Task SubmissionCanUseTheInjectedAuthorityPort()
        {
            AiObservation observation = Observation();
            AiDecision decision = await DecisionAsync(observation);
            var port = new FixedAuthorityPort(Authority(observation));
            var service = new AiDecisionService(new ThrowingStrategy(), port);

            AiSubmissionResult result = service.Submit(decision, 1000L);

            Assert.True(result.IsAccepted, result.Detail);
            Assert.Equal(1, port.Captures);
            Assert.Equal(AiSeat, port.LastSeat);
        }

        [Fact]
        public void CommandIntentAuthorityHashIsIndependentOfFloatingHeuristicScores()
        {
            AiObservation observation = Observation();
            var intent = new CommandIntent(observation, observation.LegalActions[0], "baseline-ai", 4);
            var lowScore = new AiDecision(observation, intent, 0.25d);
            var highScore = new AiDecision(observation, intent, double.PositiveInfinity);
            var notANumber = new AiDecision(observation, intent, double.NaN);

            Assert.Equal(lowScore.AuthorityHash, highScore.AuthorityHash);
            Assert.Equal(lowScore.AuthorityHash, notANumber.AuthorityHash);
            Assert.True(lowScore.Intent.CanonicalBytes().SequenceEqual(notANumber.Intent.CanonicalBytes()));
            Assert.Equal(64, intent.AuthorityHash.Length);

            var otherIntent = new CommandIntent(observation, observation.LegalActions[1], "baseline-ai", 4);
            Assert.NotEqual(intent.AuthorityHash, otherIntent.AuthorityHash);
            Assert.False(intent.CanonicalBytes().SequenceEqual(otherIntent.CanonicalBytes()));
        }

        [Fact]
        public void ReplayInjectsTheRecordedIntentWithoutInvokingAnyStrategy()
        {
            AiObservation original = Observation();
            LegalActionOption selected = original.LegalActions[0];
            var recorded = new CommandIntent(original, selected, "recorded-ai", 9);

            // A separate but value-identical observation models another runtime/restart.
            AiObservation replayObservation = Observation();
            AiDecision replay = AiDecisionService.Replay(recorded, replayObservation);

            Assert.Same(recorded, replay.Intent);
            Assert.Equal(0d, replay.HeuristicScore);
            Assert.Equal(recorded.AuthorityHash, replay.AuthorityHash);
            Assert.Same(selected, replay.Intent.Action);

            var service = new AiDecisionService(new ThrowingStrategy());
            AiSubmissionResult result = service.Submit(replay, Authority(replayObservation), 1000L);
            Assert.True(result.IsAccepted, result.Detail);
            Assert.Equal(recorded.AuthorityHash, result.RecordedIntent.AuthorityHash);

            // The same recorded values on the second observation produce the same hash.
            var recordedAgain = new CommandIntent(replayObservation, replayObservation.LegalActions[0], "recorded-ai", 9);
            Assert.Equal(recorded.AuthorityHash, recordedAgain.AuthorityHash);
            Assert.True(recorded.CanonicalBytes().SequenceEqual(recordedAgain.CanonicalBytes()));
        }

        [Fact]
        public void ReplayRejectsAStaleOrTamperedRecord()
        {
            AiObservation observation = Observation();
            var otherObservation = Observation(decisionId: "another-decision");
            var recorded = new CommandIntent(observation, observation.LegalActions[0], "recorded-ai", 1);

            Assert.Throws<ArgumentException>(() => AiDecisionService.Replay(recorded, otherObservation));

            AiObservation narrowed = Observation(
                actions: new[] { observation.LegalActions.Single(a => a.Kind == LegalActionKind.Pass) });
            Assert.Throws<ArgumentException>(() => AiDecisionService.Replay(recorded, narrowed));
        }

        [Fact]
        public void CommandIntentRefusesActionsOutsideItsObservation()
        {
            AiObservation observation = Observation();
            var foreign = new LegalActionOption(LegalActionKind.Ron, AiSeat, new TileId(60), "ron");

            Assert.Throws<ArgumentException>(() =>
                new CommandIntent(observation, foreign, "baseline-ai", 1));
        }

        [Fact]
        public void ObservationDefensivelyCopiesEveryVisibleCollection()
        {
            var hand = new List<TileId> { new TileId(20), new TileId(24) };
            var actions = new List<LegalActionOption>
            {
                new LegalActionOption(LegalActionKind.Discard, AiSeat, new TileId(20), "discard-T20"),
                new LegalActionOption(LegalActionKind.Pass, AiSeat, optionId: "pass")
            };
            var scores = new List<long> { 25000L, 25000L, 25000L, 25000L };
            var dora = new List<TileId> { new TileId(100) };
            var melds = new List<AiVisibleMeld>
            {
                new AiVisibleMeld(AiSeat, new[] { new TileId(40), new TileId(41), new TileId(42) })
            };

            var observation = new AiObservation(
                "session-1",
                new DecisionKey(RoundId, AiSeat, DecisionId),
                Epoch,
                7L,
                Deadline,
                RoundPhase.Discard,
                0,
                1,
                1,
                0,
                0,
                69,
                hand,
                new TileId(33),
                melds,
                Array.Empty<AiVisibleMeld>(),
                new[] { new AiVisibleDiscard(2, new TileId(60)) },
                dora,
                scores,
                actions);

            hand.Clear();
            actions.Clear();
            scores.Clear();
            dora.Clear();
            melds.Clear();

            Assert.Equal(2, observation.MyHandTileIds.Count);
            Assert.Equal(2, observation.LegalActions.Count);
            Assert.Equal(4, observation.Scores.Count);
            Assert.Single(observation.VisibleDoraIndicators);
            Assert.Single(observation.MyMelds);
        }

        [Fact]
        public void ObservationRejectsForeignSeatAndDuplicateCandidates()
        {
            var foreign = new LegalActionOption(LegalActionKind.Discard, 2, new TileId(20), "foreign");
            Assert.Throws<ArgumentException>(() => Observation(actions: new[] { foreign }));

            var duplicate = new LegalActionOption(LegalActionKind.Pass, AiSeat, optionId: "pass");
            Assert.Throws<ArgumentException>(() => Observation(actions: new[] { duplicate, duplicate }));

            Assert.Throws<ArgumentException>(() => Observation(sessionId: " "));
            Assert.Throws<ArgumentException>(() => Observation(scores: new[] { 1L, 2L, 3L }));
        }

        [Fact]
        public void ObservationSurfaceExposesNoHiddenDomainState()
        {
            var allowedDomainTypes = new HashSet<string>(StringComparer.Ordinal)
            {
                "DecisionKey",
                "LegalActionOption",
                "RoundPhase",
                "TileId"
            };

            foreach (PropertyInfo property in typeof(AiObservation).GetProperties(
                BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (Type type in Flatten(property.PropertyType))
                {
                    if (string.Equals(type.Namespace, "Mahjong.Domain", StringComparison.Ordinal))
                    {
                        Assert.True(
                            allowedDomainTypes.Contains(type.Name),
                            "AiObservation." + property.Name + " leaks Domain type " + type.FullName + ".");
                    }
                }
            }

            string[] forbiddenNames =
            {
                "Hidden", "OpponentHand", "UraDora", "Seed", "WallTiles", "DeadWall",
                "TilePositions", "GameState", "RoundState", "FullState"
            };
            foreach (PropertyInfo property in typeof(AiObservation).GetProperties(
                BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (string forbidden in forbiddenNames)
                {
                    Assert.False(
                        property.Name.IndexOf(forbidden, StringComparison.OrdinalIgnoreCase) >= 0,
                        "AiObservation exposes forbidden hidden-state property " + property.Name + ".");
                }
            }

            MethodInfo? decide = typeof(IAiStrategy).GetMethod(nameof(IAiStrategy.Decide));
            Assert.NotNull(decide);
            ParameterInfo[] parameters = decide!.GetParameters();
            Assert.Equal(2, parameters.Length);
            Assert.Equal(typeof(AiObservation), parameters[0].ParameterType);
            Assert.Equal(typeof(CancellationToken), parameters[1].ParameterType);

            AssemblyName[] references = typeof(AiObservation).Assembly.GetReferencedAssemblies();
            foreach (AssemblyName reference in references)
            {
                Assert.False(
                    string.Equals(reference.Name, "Mahjong.TableRuntime", StringComparison.Ordinal) ||
                    string.Equals(reference.Name, "Mahjong.Contracts", StringComparison.Ordinal) ||
                    string.Equals(reference.Name, "Mahjong.LocalHost", StringComparison.Ordinal) ||
                    string.Equals(reference.Name, "UnityEngine", StringComparison.Ordinal),
                    "Mahjong.Ai must not reference " + reference.Name + ".");
            }
        }

        [Fact]
        public void CommandIntentCanonicalBytesAreStableAcrossCultures()
        {
            AiObservation observation = Observation();
            var baseline = new CommandIntent(observation, observation.LegalActions[0], "baseline-ai", 1);
            CultureInfo previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
                var turkish = new CommandIntent(observation, observation.LegalActions[0], "baseline-ai", 1);
                Assert.Equal(baseline.AuthorityHash, turkish.AuthorityHash);
                Assert.True(baseline.CanonicalBytes().SequenceEqual(turkish.CanonicalBytes()));
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        private static async Task<AiDecision> DecisionAsync(AiObservation observation)
        {
            var service = new AiDecisionService(new FixedStrategy(
                new AiStrategicChoice(observation.LegalActions[0], 1d)));
            return await service.ReasonAsync(observation, TestContext.Current.CancellationToken);
        }

        private static AiAuthoritySnapshot Authority(
            AiObservation observation,
            bool automated = true,
            bool paused = false,
            IReadOnlyList<LegalActionOption>? legalActions = null)
        {
            return new AiAuthoritySnapshot(
                observation.Seat,
                observation.RoundId,
                observation.DecisionKey,
                observation.ControlEpoch,
                automated,
                paused,
                observation.DecisionDeadlineMillis,
                legalActions ?? observation.LegalActions);
        }

        private static AiObservation Observation(
            string sessionId = "session-1",
            string roundId = RoundId,
            string decisionId = DecisionId,
            int seat = AiSeat,
            long epoch = Epoch,
            long stateVersion = 7L,
            long deadline = Deadline,
            IReadOnlyList<LegalActionOption>? actions = null,
            IReadOnlyList<long>? scores = null)
        {
            IReadOnlyList<LegalActionOption> legalActions = actions ?? new[]
            {
                new LegalActionOption(LegalActionKind.Discard, seat, new TileId(20), "discard-T20"),
                new LegalActionOption(LegalActionKind.Pass, seat, optionId: "pass")
            };

            return new AiObservation(
                sessionId,
                new DecisionKey(roundId, seat, decisionId),
                epoch,
                stateVersion,
                deadline,
                RoundPhase.Discard,
                0,
                1,
                1,
                0,
                0,
                69,
                Hand(),
                new TileId(33),
                Array.Empty<AiVisibleMeld>(),
                Array.Empty<AiVisibleMeld>(),
                new[] { new AiVisibleDiscard((seat + 1) % 4, new TileId(60)) },
                new[] { new TileId(100) },
                scores ?? new[] { 25000L, 25000L, 25000L, 25000L },
                legalActions);
        }

        private static IReadOnlyList<TileId> Hand()
        {
            var hand = new List<TileId>(13);
            for (int i = 0; i < 13; i++)
            {
                hand.Add(new TileId(20 + i));
            }

            return hand;
        }

        private static IEnumerable<Type> Flatten(Type type)
        {
            yield return type;
            if (type.IsArray)
            {
                Type? element = type.GetElementType();
                if (element != null)
                {
                    foreach (Type nested in Flatten(element))
                    {
                        yield return nested;
                    }
                }
            }

            if (type.IsGenericType)
            {
                Type[] arguments = type.GetGenericArguments();
                for (int i = 0; i < arguments.Length; i++)
                {
                    foreach (Type nested in Flatten(arguments[i]))
                    {
                        yield return nested;
                    }
                }
            }
        }

        private sealed class FixedStrategy : IAiStrategy
        {
            private readonly AiStrategicChoice _choice;

            public FixedStrategy(AiStrategicChoice choice)
            {
                _choice = choice;
            }

            public string AlgorithmId => "test-ai";

            public int AlgorithmVersion => 1;

            public AiStrategicChoice Decide(AiObservation observation, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return _choice;
            }
        }

        private sealed class DelegateStrategy : IAiStrategy
        {
            private readonly Func<AiObservation, CancellationToken, AiStrategicChoice> _decide;

            public DelegateStrategy(Func<AiObservation, CancellationToken, AiStrategicChoice> decide)
            {
                _decide = decide;
            }

            public string AlgorithmId => "delegate-ai";

            public int AlgorithmVersion => 1;

            public AiStrategicChoice Decide(AiObservation observation, CancellationToken cancellationToken) =>
                _decide(observation, cancellationToken);
        }

        private sealed class ThrowingStrategy : IAiStrategy
        {
            public string AlgorithmId => "throwing-ai";

            public int AlgorithmVersion => 1;

            public AiStrategicChoice Decide(AiObservation observation, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("No strategy may run during replay or admission.");
        }

        private sealed class CountingAuthorityPort : IAiAuthorityPort
        {
            public int Captures { get; private set; }

            public AiAuthoritySnapshot Capture(int seat)
            {
                Captures++;
                throw new InvalidOperationException("Reasoning must not read authority.");
            }
        }

        private sealed class FixedAuthorityPort : IAiAuthorityPort
        {
            private readonly AiAuthoritySnapshot _snapshot;

            public FixedAuthorityPort(AiAuthoritySnapshot snapshot)
            {
                _snapshot = snapshot;
            }

            public int Captures { get; private set; }

            public int LastSeat { get; private set; }

            public AiAuthoritySnapshot Capture(int seat)
            {
                Captures++;
                LastSeat = seat;
                return _snapshot;
            }
        }
    }
}
