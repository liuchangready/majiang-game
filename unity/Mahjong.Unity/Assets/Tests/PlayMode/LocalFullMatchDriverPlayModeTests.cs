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
using Mahjong.Unity.App;
using Mahjong.Unity.Table;
using Mahjong.Unity.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

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
                PlayerLegalActionState action = view.Own.Decision.Candidates[0];
                var owner = new TableCommandOwnerContext(
                    new RequestId("playmode-match-" + Guid.NewGuid().ToString("N")),
                    new RequestScopeId(view.Identity.ScopeId),
                    new StateVersion(view.Identity.StateVersion),
                    new OwnerEpoch(view.Identity.OwnerEpoch));
                TableCommandIntent intent = intentFactory.CreateSubmitAction(view, action, owner);
                ulong before = view.Identity.StateVersion;

                Task<CommandSubmission> submit = coordinator.SubmitAsync(intent.Command, CancellationToken.None);
                yield return WaitTask(submit, 20f, "action submit");
                Assert.IsTrue(submit.Result.Sent, "The canonical action must reach the real transport.");

                float advance = 0f;
                while (advance < 30f)
                {
                    coordinator = app.Composition.SessionCoordinator;
                    if (coordinator == null)
                    {
                        break;
                    }

                    view = mapper.Project(coordinator.Current);
                    if (view.Identity.StateVersion > before ||
                        view.Board.Phase == PlayerGamePhase.RoundEnd)
                    {
                        break;
                    }

                    advance += Time.unscaledDeltaTime;
                    yield return null;
                }

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
                float elapsed = 0f;
                bool terminal = false;
                var submitted = new HashSet<string>();
                PlayerTableViewState view = mapper.Project(coordinator.Current);
                while (elapsed < 180f)
                {
                    coordinator = app.Composition.SessionCoordinator;
                    if (coordinator == null)
                    {
                        break;
                    }

                    view = mapper.Project(coordinator.Current);
                    if (view.Board.Phase == PlayerGamePhase.RoundEnd)
                    {
                        terminal = true;
                        break;
                    }

                    FullMatchActionPlan? plan = null;
                    if (view.Own?.Decision != null &&
                        LocalFullMatchPolicy.TryCreateActionPlan(
                            coordinator.Current,
                            view,
                            intentFactory,
                            clock.MonotonicMilliseconds,
                            out plan,
                            out _,
                            out _))
                    {
                    }
                    else if ((view.Board.Phase == PlayerGamePhase.Draw ||
                              view.Board.Phase == PlayerGamePhase.KanDraw) &&
                             view.Board.CurrentSeat == view.Identity.Seat &&
                             LocalFullMatchPolicy.TryCreateMandatoryDrawPlan(
                                 coordinator.Current,
                                 view,
                                 clock.MonotonicMilliseconds,
                                 out plan,
                                 out _,
                                 out _))
                    {
                    }

                    if (plan != null)
                    {
                        string key = plan.RoundId + "|" + plan.DecisionId + "|" + (int)plan.Action + "|" + plan.RequestId;
                        if (!submitted.Add(key))
                        {
                            elapsed += Time.unscaledDeltaTime;
                            yield return null;
                            continue;
                        }

                        ulong before = plan.BaselineStateVersion;
                        Task<CommandSubmission> submit = coordinator.SubmitAsync(plan.Command, CancellationToken.None);
                        yield return WaitTask(submit, 20f, "round action submit");
                        Assert.IsTrue(submit.Result.Sent);
                        float advance = 0f;
                        while (advance < 20f)
                        {
                            coordinator = app.Composition.SessionCoordinator;
                            if (coordinator == null)
                            {
                                break;
                            }

                            PlayerTableViewState after = mapper.Project(coordinator.Current);
                            if (after.Identity.StateVersion > before ||
                                after.Board.Phase == PlayerGamePhase.RoundEnd)
                            {
                                break;
                            }

                            advance += Time.unscaledDeltaTime;
                            yield return null;
                        }
                    }
                    else
                    {
                        elapsed += Time.unscaledDeltaTime;
                        yield return null;
                    }
                }

                if (!terminal)
                {
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
        public IEnumerator DriverFaultFixture_NoDecisionFailsBounded()
        {
            yield return RunFaultFixture("NO_DECISION", 42, "NO_DECISION");
        }

        [UnityTest]
        public IEnumerator DriverFaultFixture_SubmitTimeoutKeepsFailure()
        {
            yield return RunFaultFixture("SUBMIT_TIMEOUT", 43, "SUBMIT_TIMEOUT");
        }

        [UnityTest]
        public IEnumerator DriverFaultFixture_UnconfirmedRequestDoesNotRetry()
        {
            yield return RunFaultFixture("UNCONFIRMED", 43, "RECEIPT_UNRESOLVED");
            string evidence = Path.Combine(Application.temporaryCachePath, "p45-driver-fixture-UNCONFIRMED");
            string jsonl = File.ReadAllText(Path.Combine(evidence, "full-match.jsonl"));
            Assert.AreEqual(1, CountOccurrences(jsonl, "\"kind\":\"action_plan\""));
            StringAssert.Contains("fm-test-unconfirmed", jsonl);
            Directory.Delete(evidence, true);
        }

        private static IEnumerator RunFaultFixture(string mode, int expectedExit, string expectedCode)
        {
            string evidence = Path.Combine(
                Application.temporaryCachePath,
                "p45-driver-fixture-" + mode);
            if (Directory.Exists(evidence))
            {
                Directory.Delete(evidence, true);
            }

            var completed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            LocalFullMatchPlayerDriver driver = LocalFullMatchPlayerDriver.CreateForTest(
                evidence,
                "fixture-" + mode,
                code => completed.TrySetResult(code));
            LogAssert.Expect(LogType.Error, new Regex("FULL_MATCH_DRIVER_FAILED: code=" + expectedCode));
            try
            {
                driver.StartCoroutine(driver.RunFaultFixtureForTest(mode));
                yield return WaitTask(completed.Task, 15f, "driver fixture " + mode);
                Assert.AreEqual(expectedExit, completed.Task.Result);
                string summary = File.ReadAllText(Path.Combine(evidence, "summary.json"));
                StringAssert.Contains("\"outcome\": \"FAIL\"", summary);
                StringAssert.Contains("\"failureCode\": \"" + expectedCode + "\"", summary);
                StringAssert.Contains("\"exitCode\": " + expectedExit, summary);
                StringAssert.Contains("\"matchFinished\": false", summary);
            }
            finally
            {
                Object.DestroyImmediate(driver.gameObject);
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
