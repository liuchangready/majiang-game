#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Unity.App;
using Mahjong.Unity.Table;
using Mahjong.Unity.UI;
using Mahjong.Unity.UI.Session;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;
using WirePlayerSnapshot = Mahjong.Contracts.Wire.PlayerSnapshot;
using WirePlayerControlView = Mahjong.Contracts.Wire.PlayerControlView;

namespace Mahjong.Unity.Tests.PlayMode
{
    /// <summary>
    /// Opt-in boundary and real match-mode session/candidate path for the full-match
    /// driver assembly. A normal Player launch must not enable match composition;
    /// the real case starts from the production Lobby route with the real local
    /// factory in match mode, observes a server decision, submits one canonical
    /// action through the existing SessionCoordinator, and shuts down cleanly.
    /// </summary>
    [TestFixture]
    public sealed class LocalFullMatchDriverPlayModeTests
    {
        [UnityTest]
        public IEnumerator BootstrapWithoutArgument_DoesNotCreateDriverOrRun()
        {
            LocalFullMatchPlayerDriver existing = Object.FindFirstObjectByType<LocalFullMatchPlayerDriver>();
            if (existing != null)
            {
                Object.DestroyImmediate(existing.gameObject);
            }

            MethodInfo? bootstrap = typeof(LocalFullMatchPlayerDriver).GetMethod(
                "Bootstrap",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(bootstrap, "The opt-in bootstrap method must exist.");
            bootstrap!.Invoke(null, null);
            yield return null;

            LocalFullMatchPlayerDriver created = Object.FindFirstObjectByType<LocalFullMatchPlayerDriver>();
            Assert.IsNull(created, "A normal launch must not create the full-match driver.");
        }

        [UnityTest]
        public IEnumerator MatchModeRealSession_ExposesCandidateAndSubmitsOneAction()
        {
            AppRoot? previous = Object.FindFirstObjectByType<AppRoot>();
            if (previous != null)
            {
                Object.DestroyImmediate(previous.gameObject);
            }

            yield return null;
            string root = Path.Combine(
                Application.temporaryCachePath,
                "p45-full-match-playmode-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            AppRoot app = AppRoot.EnsureInitialized(new AppConfig(), null, root);
            LocalTableChannelFactory.EnableMatchMode();
            try
            {
                Task<bool> lobby = app.Router.LoadAsync(SceneId.Lobby);
                yield return WaitTask(lobby, 30f, "Lobby load");
                Assert.IsTrue(lobby.Result, "Lobby must load.");

                Task<LocalGameStartResult> start = app.Router.StartLocalGameAsync();
                yield return WaitTask(start, 60f, "local start");
                Assert.AreEqual(LocalGameStartStatus.Started, start.Result.Status);

                SessionCoordinator? coordinator = app.Composition.SessionCoordinator;
                float ready = 0f;
                while (ready < 60f &&
                       (coordinator == null ||
                        coordinator.Current.State != TableConnectionState.Ready ||
                        coordinator.Current.PlayerSnapshot == null))
                {
                    ready += Time.unscaledDeltaTime;
                    yield return null;
                    coordinator = app.Composition.SessionCoordinator;
                }

                Assert.IsNotNull(coordinator, "A real match-mode session is required.");
                Assert.AreEqual(TableConnectionState.Ready, coordinator!.Current.State);
                object control = app.Composition.ChannelFactory;
                PropertyInfo? matchProperty = control.GetType().GetProperty("CurrentMatch");
                Assert.IsNotNull(matchProperty, "The factory must expose match control.");
                object? match = matchProperty!.GetValue(control);
                Assert.IsNotNull(match, "A durable match must be open in match mode.");
                string roundId = (string?)match!.GetType().GetProperty("RoundId")!.GetValue(match) ?? string.Empty;
                Assert.IsFalse(string.IsNullOrEmpty(roundId));

                var mapper = new PlayerTableProjectionMapper(new SystemTableClientClock());
                var intentFactory = new TableCommandIntentFactory(new SystemTableClientClock());
                PlayerTableViewState view = mapper.Project(coordinator.Current);
                float decisionWait = 0f;
                while (decisionWait < 30f &&
                       (view.Own?.Decision == null || view.Own.Decision.Candidates.Count == 0))
                {
                    decisionWait += Time.unscaledDeltaTime;
                    yield return null;
                    coordinator = app.Composition.SessionCoordinator;
                    if (coordinator == null)
                    {
                        break;
                    }

                    view = mapper.Project(coordinator.Current);
                }

                Assert.IsNotNull(view.Own?.Decision, "The server must provide a real decision.");
                Assert.Greater(view.Own!.Decision!.Candidates.Count, 0);
                ulong before = view.Identity.StateVersion;
                var transport = new FullMatchSessionTransport(coordinator);
                var execution = new FullMatchExecutionBoundary(
                    transport,
                    new FullMatchRequestLedger(),
                    FullMatchExecutionBudget.Production);
                bool confirmed = false;
                float executeDeadline = 0f;
                try
                {
                    while (executeDeadline < 60f)
                    {
                        coordinator = app.Composition.SessionCoordinator;
                        if (coordinator == null)
                        {
                            break;
                        }

                        transport.Attach(coordinator);
                        view = mapper.Project(coordinator.Current);
                        FullMatchExecutionOutcome outcome = execution.Poll(
                            coordinator.Current,
                            view,
                            intentFactory,
                            (long)(Time.realtimeSinceStartup * 1000f));
                        if (outcome.Status == FullMatchExecutionStatus.Confirmed)
                        {
                            confirmed = true;
                            break;
                        }

                        if (outcome.Status == FullMatchExecutionStatus.Failed)
                        {
                            Assert.Fail(outcome.Code + ": " + outcome.Detail);
                        }

                        executeDeadline += Time.unscaledDeltaTime;
                        yield return null;
                    }
                }
                finally
                {
                    execution.Dispose();
                }

                Assert.IsTrue(confirmed, "The shared production boundary must confirm the real action.");
                view = mapper.Project(coordinator.Current);
                Assert.IsTrue(view.Identity.StateVersion > before || view.Board.Phase == PlayerGamePhase.RoundEnd,
                    "The real authoritative state must advance after the submitted action.");
            }
            finally
            {
                LocalTableChannelFactory.DisableMatchModeForTest();
                if (app != null)
                {
                    try
                    {
                        app.Composition.ShutdownAsync("PLAYMODE_MATCH_TEST_END")
                            .Wait(TimeSpan.FromSeconds(10));
                    }
                    catch
                    {
                    }
                }
            }
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator MatchModeRealSession_CompletesOneRoundAndOpensNext()
        {
            AppRoot? previous = Object.FindFirstObjectByType<AppRoot>();
            if (previous != null)
            {
                Object.DestroyImmediate(previous.gameObject);
            }

            yield return null;
            string root = Path.Combine(
                Application.temporaryCachePath,
                "p45-full-match-round-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            AppRoot app = AppRoot.EnsureInitialized(new AppConfig(), null, root);
            LocalTableChannelFactory.EnableMatchMode();
            try
            {
                Task<bool> lobby = app.Router.LoadAsync(SceneId.Lobby);
                yield return WaitTask(lobby, 30f, "Lobby load");
                Assert.IsTrue(lobby.Result, "Lobby must load.");

                Task<LocalGameStartResult> start = app.Router.StartLocalGameAsync();
                yield return WaitTask(start, 60f, "local start");
                Assert.AreEqual(LocalGameStartStatus.Started, start.Result.Status);

                SessionCoordinator? coordinator = app.Composition.SessionCoordinator;
                float ready = 0f;
                while (ready < 60f &&
                       (coordinator == null ||
                        coordinator.Current.State != TableConnectionState.Ready ||
                        coordinator.Current.PlayerSnapshot == null))
                {
                    ready += Time.unscaledDeltaTime;
                    yield return null;
                    coordinator = app.Composition.SessionCoordinator;
                }

                Assert.IsNotNull(coordinator, "A real match-mode session is required.");
                Assert.AreEqual(TableConnectionState.Ready, coordinator!.Current.State);
                object control = app.Composition.ChannelFactory;
                string firstRound = ReadMatchRoundId(control);
                Assert.IsFalse(string.IsNullOrEmpty(firstRound), "A durable match round must be open.");

                var clock = new SystemTableClientClock();
                var mapper = new PlayerTableProjectionMapper(clock);
                var intentFactory = new TableCommandIntentFactory(clock);
                var transport = new FullMatchSessionTransport(coordinator);
                var execution = new FullMatchExecutionBoundary(
                    transport,
                    new FullMatchRequestLedger(),
                    FullMatchExecutionBudget.Production);
                float elapsed = 0f;
                bool terminal = false;
                PlayerTableViewState view = mapper.Project(coordinator.Current);
                try
                {
                    while (elapsed < 180f)
                    {
                        coordinator = app.Composition.SessionCoordinator;
                        if (coordinator == null)
                        {
                            break;
                        }

                        transport.Attach(coordinator);
                        view = mapper.Project(coordinator.Current);
                        if (view.Board.Phase == PlayerGamePhase.RoundEnd)
                        {
                            // Same production terminal/commit fence as the driver:
                            // a terminal phase is evidence, not confirmation. Keep
                            // polling the shared boundary while an own request is
                            // still in flight; never commit on the phase alone.
                            FullMatchTerminalGateResult gate = FullMatchTerminalCommitGate.Poll(
                                execution,
                                coordinator.Current,
                                view,
                                intentFactory,
                                (long)(Time.realtimeSinceStartup * 1000f));
                            if (gate.Status == FullMatchTerminalGateStatus.Waiting)
                            {
                                elapsed += Time.unscaledDeltaTime;
                                yield return null;
                                continue;
                            }

                            if (gate.MustFail)
                            {
                                Assert.Fail(gate.Code + ": " + gate.Detail);
                            }

                            terminal = true;
                            break;
                        }

                        FullMatchExecutionOutcome outcome = execution.Poll(
                            coordinator.Current,
                            view,
                            intentFactory,
                            (long)(Time.realtimeSinceStartup * 1000f));
                        if (outcome.Status == FullMatchExecutionStatus.Failed)
                        {
                            Assert.Fail(outcome.Code + ": " + outcome.Detail);
                        }

                        if (outcome.Status == FullMatchExecutionStatus.Confirmed)
                        {
                            yield return null;
                            continue;
                        }

                        elapsed += Time.unscaledDeltaTime;
                        yield return null;
                    }
                }
                finally
                {
                    execution.Dispose();
                }
                if (!terminal)
                {
                    Debug.Log(
                        "PLAYMODE_ROUND_STALL_DETAIL" +
                        " immediateSleeve=" + (view.Own?.Cheat?.ImmediateSleeveBloom != null) +
                        " offTurnSleeve=" + (view.Own?.Cheat?.OffTurnSleeveBloom != null) +
                        " beamSwap=" + (view.Own?.Cheat?.BeamSwap != null) +
                        " preparation=" + (view.Own?.Cheat?.Preparation != null) +
                        " decision=" + (view.Own?.Decision != null) +
                        " runtime=" + (control.GetType().GetMethod("DescribeRuntimeForTest")?.Invoke(control, null) ?? "<none>"));
                    LogAssert.Expect(LogType.Error, new Regex("PLAYMODE_ROUND_NOT_TERMINAL"));
                    Debug.LogError(
                        "PLAYMODE_ROUND_NOT_TERMINAL phase=" + view.Board.Phase +
                        " seat=" + view.Board.CurrentSeat +
                        " stateVersion=" + view.Identity.StateVersion +
                        " pending=" + (coordinator?.Current.PendingCommands.Count ?? -1) +
                        " lastError=" + (coordinator?.Current.LastError ?? "<none>"));
                }

                Assert.IsTrue(terminal, "The real round must reach its authoritative terminal phase.");
                string completedRound = ReadMatchRoundId(control);
                Assert.AreEqual(firstRound, completedRound);

                MethodInfo? recordMethod = control.GetType().GetMethod("RecordRoundCompletionAsync");
                Assert.IsNotNull(recordMethod, "Match control must expose round completion.");
                Task recordTask = (Task)recordMethod!.Invoke(control, new object[] { CancellationToken.None })!;
                yield return WaitTask(recordTask, 30f, "match round completion");

                Task<ShutdownResult> shutdown = app.Composition.ShutdownAsync("PLAYMODE_ROUND_DONE");
                yield return WaitTask(shutdown, 30f, "round session shutdown");
                Assert.AreEqual(ShutdownOutcome.Completed, shutdown.Result.Outcome);

                Task<SessionCoordinator> open = app.Composition.OpenLocalSessionAsync(CancellationToken.None);
                yield return WaitTask(open, 60f, "next round session open");
                SessionCoordinator next = open.Result;
                float nextReady = 0f;
                while (nextReady < 60f &&
                       (next.Current.State != TableConnectionState.Ready ||
                        next.Current.PlayerSnapshot == null))
                {
                    nextReady += Time.unscaledDeltaTime;
                    yield return null;
                    next = app.Composition.SessionCoordinator ?? next;
                }

                Assert.AreEqual(TableConnectionState.Ready, next.Current.State);
                string nextRound = ReadMatchRoundId(control);
                Assert.IsFalse(string.IsNullOrEmpty(nextRound));
                Assert.AreNotEqual(firstRound, nextRound, "The next round must be a fresh durable round.");
            }
            finally
            {
                LocalTableChannelFactory.DisableMatchModeForTest();
                try
                {
                    app.Composition.ShutdownAsync("PLAYMODE_ROUND_TEST_END")
                        .Wait(TimeSpan.FromSeconds(10));
                }
                catch
                {
                }
            }
        }

        [UnityTest]
        public IEnumerator DriverExecutionFixture_NoDecisionExpiresAtTheBoundedProductionWait()
        {
            PlayModeExecutionTransport transport = PlayModeExecutionTransport.DrawWithoutDecision();
            yield return RunExecutionFixture(
                transport,
                new FullMatchExecutionBudget(1_000L, 1_000L, 120L),
                "NO_DECISION_WAIT",
                42,
                "DECISION_WAIT_EXPIRED",
                0);
            Assert.AreEqual(0, transport.SubmitCount, "A missing decision must never submit a command.");
        }

        [UnityTest]
        public IEnumerator DriverExecutionFixture_NeverCompletingSubmitTimesOutOnce()
        {
            var neverCompletes = new TaskCompletionSource<CommandSubmission>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            PlayModeExecutionTransport transport = PlayModeExecutionTransport.DrawForSubmission(
                _ => neverCompletes.Task);
            yield return RunExecutionFixture(
                transport,
                new FullMatchExecutionBudget(120L, 1_000L, 1_000L),
                "SUBMIT_TIMEOUT",
                43,
                "SUBMIT_TIMEOUT",
                1);
            Assert.AreEqual(1, transport.SubmitCount, "The unresolved original request must not be resent.");
        }

        [UnityTest]
        public IEnumerator DriverExecutionFixture_AppliedWithoutMatchingVisibleEventStaysUnresolved()
        {
            PlayModeExecutionTransport transport =
                PlayModeExecutionTransport.DrawForAppliedWithoutVisibleEvent();
            yield return RunExecutionFixture(
                transport,
                new FullMatchExecutionBudget(1_000L, 120L, 1_000L),
                "APPLIED_NO_EVENT",
                44,
                "VISIBLE_EVENT_MISSING",
                1);
            Assert.AreEqual(1, transport.SubmitCount, "An applied but unconfirmed request must not be resent.");
        }

        [UnityTest]
        public IEnumerator DriverExecutionFixture_SentWithoutReceiptStaysUnresolved()
        {
            PlayModeExecutionTransport transport = PlayModeExecutionTransport.DrawForSubmission(
                command => Task.FromResult(PlayModeExecutionTransport.SentSubmission(command)));
            yield return RunExecutionFixture(
                transport,
                new FullMatchExecutionBudget(1_000L, 120L, 1_000L),
                "UNCONFIRMED",
                43,
                "RECEIPT_UNRESOLVED",
                1);
            Assert.AreEqual(1, transport.SubmitCount, "A sent but unconfirmed request must not be resent.");
        }

        private static IEnumerator RunExecutionFixture(
            PlayModeExecutionTransport transport,
            FullMatchExecutionBudget budget,
            string label,
            int expectedExit,
            string expectedCode,
            int expectedPlanCount)
        {
            string evidence = Path.Combine(
                Application.temporaryCachePath,
                "p45-execution-fixture-" + label);
            if (Directory.Exists(evidence))
            {
                Directory.Delete(evidence, true);
            }

            var completed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            LocalFullMatchPlayerDriver driver = LocalFullMatchPlayerDriver.CreateForTest(
                evidence,
                "fixture-" + label,
                code => completed.TrySetResult(code));
            LogAssert.Expect(LogType.Error, new Regex("FULL_MATCH_DRIVER_FAILED: code=" + expectedCode));
            try
            {
                driver.StartCoroutine(driver.RunExecutionFixtureForTest(transport, budget, label));
                yield return WaitTask(completed.Task, 15f, "driver execution fixture " + label);
                Assert.AreEqual(expectedExit, completed.Task.Result);
                string summary = File.ReadAllText(Path.Combine(evidence, "summary.json"));
                StringAssert.Contains("\"outcome\": \"FAIL\"", summary);
                StringAssert.Contains("\"failureCode\": \"" + expectedCode + "\"", summary);
                StringAssert.Contains("\"exitCode\": " + expectedExit, summary);
                StringAssert.Contains("\"matchFinished\": false", summary);
                string jsonl = File.ReadAllText(Path.Combine(evidence, "full-match.jsonl"));
                Assert.AreEqual(
                    expectedPlanCount,
                    CountOccurrences(jsonl, "\"kind\":\"action_plan\""),
                    "action_plan rows must come from the production boundary ledger, not the fixture.");
            }
            finally
            {
                Object.DestroyImmediate(driver.gameObject);
            }
        }
        /// <summary>
        /// Real-shaped scripted session/view plus a counting command sink. The
        /// fixture supplies only data and the submit result; planning, ledger,
        /// waits and confirmation remain in the production boundary.
        /// </summary>
        private sealed class PlayModeExecutionTransport : IFullMatchExecutionTransport
        {
            private readonly ScriptedSessionView _view;
            private readonly Func<ProtocolCommand, Task<CommandSubmission>> _submit;
            private readonly bool _raiseAppliedWithoutVisibleEvent;

            public PlayModeExecutionTransport(
                SessionViewState initial,
                Func<ProtocolCommand, Task<CommandSubmission>>? submit = null,
                bool raiseAppliedWithoutVisibleEvent = false)
            {
                _view = new ScriptedSessionView(initial);
                _submit = submit ?? (command => Task.FromResult(SentSubmission(command)));
                _raiseAppliedWithoutVisibleEvent = raiseAppliedWithoutVisibleEvent;
            }

            public ISessionView View => _view;

            public List<ProtocolCommand> Submitted { get; } = new List<ProtocolCommand>();

            public int SubmitCount => Submitted.Count;

            public event EventHandler<TableReceiptResolvedEventArgs>? ReceiptResolved;

            public static PlayModeExecutionTransport DrawWithoutDecision()
            {
                return new PlayModeExecutionTransport(BuildState(GamePhase.Discard));
            }

            public static PlayModeExecutionTransport DrawForSubmission(
                Func<ProtocolCommand, Task<CommandSubmission>> submit)
            {
                return new PlayModeExecutionTransport(BuildState(GamePhase.Draw), submit);
            }

            public static PlayModeExecutionTransport DrawForAppliedWithoutVisibleEvent()
            {
                return new PlayModeExecutionTransport(
                    BuildState(GamePhase.Draw),
                    raiseAppliedWithoutVisibleEvent: true);
            }

            public static CommandSubmission SentSubmission(ProtocolCommand command)
            {
                var key = new IdempotencyKey(
                    "local-player",
                    command.ScopeId!.Value,
                    command.RequestId);
                var pending = new PendingCommandSnapshot(
                    key,
                    command.Kind,
                    "digest",
                    command.Payload.Length,
                    true,
                    false,
                    false,
                    null,
                    null,
                    false);
                return new CommandSubmission(pending, sent: true, wasDuplicate: false);
            }

            public Task<CommandSubmission> SubmitAsync(
                ProtocolCommand command,
                CancellationToken cancellationToken)
            {
                Submitted.Add(command);
                Task<CommandSubmission> submission = _submit(command);
                if (_raiseAppliedWithoutVisibleEvent)
                {
                    ReceiptResolved?.Invoke(this, AppliedReceipt(command));
                }

                return submission;
            }

            private static TableReceiptResolvedEventArgs AppliedReceipt(ProtocolCommand command)
            {
                var key = new IdempotencyKey(
                    "local-player",
                    command.ScopeId!.Value,
                    command.RequestId);
                var pending = new PendingCommandSnapshot(
                    key,
                    command.Kind,
                    "digest",
                    command.Payload.Length,
                    true,
                    false,
                    false,
                    null,
                    null,
                    false);
                OutboxResolution resolution = OutboxResolution.FromReceipt(
                    Mahjong.Contracts.ReceiptStatus.Applied,
                    string.Empty,
                    new StateVersion(5),
                    new StateVersion(6),
                    new EventSeq(106),
                    1,
                    "digest",
                    false);
                return new TableReceiptResolvedEventArgs(pending, resolution);
            }

            private static SessionViewState BuildState(GamePhase phase)
            {
                TableSessionBinding binding = PlayModeScriptedTableChannel.CreateBinding(
                    "session_fixture",
                    "room_fixture",
                    ProtocolRole.Player,
                    0);
                WirePlayerSnapshot snapshot = PlayModeScriptedTableChannel.CreatePlayerSync(
                    "session_fixture",
                    "room_fixture",
                    0,
                    1,
                    500).SyncResponse.Player;
                snapshot.PublicView.Board.Phase = phase;
                snapshot.PublicView.Board.CurrentSeat = 0;
                snapshot.PrivateView.Control = new WirePlayerControlView
                {
                    ControlEpoch = 7,
                    ControlledByComputer = false,
                    ControlVersion = 1
                };
                return new SessionViewState(
                    1,
                    TableConnectionState.Ready,
                    binding,
                    snapshot,
                    null,
                    null,
                    null,
                    null);
            }
        }

        private sealed class ScriptedSessionView : ISessionView
        {
            public ScriptedSessionView(SessionViewState initial)
            {
                Current = initial;
            }

            public SessionViewState Current { get; private set; }

            public event EventHandler<SessionViewState>? Changed;

            public void Publish(SessionViewState state)
            {
                Current = state;
                Changed?.Invoke(this, state);
            }
        }
        private static int CountOccurrences(string text, string value)
        {
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }

            return count;
        }

        private static string ReadMatchRoundId(object control)
        {
            PropertyInfo? current = control.GetType().GetProperty("CurrentMatch");
            object? match = current?.GetValue(control);
            if (match == null)
            {
                return string.Empty;
            }

            return (string?)match.GetType().GetProperty("RoundId")?.GetValue(match) ?? string.Empty;
        }

        private static IEnumerator WaitTask(Task task, float timeoutSeconds, string label)
        {
            float elapsed = 0f;
            while (!task.IsCompleted && elapsed < timeoutSeconds)
            {
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            Assert.IsTrue(task.IsCompleted, label + " timed out.");
            if (task.IsFaulted)
            {
                throw task.Exception!.GetBaseException();
            }

            Assert.IsFalse(task.IsCanceled, label + " was cancelled.");
        }
    }
}
