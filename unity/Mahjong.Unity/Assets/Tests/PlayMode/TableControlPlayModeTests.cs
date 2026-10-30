#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Unity.App;
using Mahjong.Unity.Platform;
using Mahjong.Unity.Table;
using Mahjong.Unity.UI;
using Mahjong.Unity.UI.Session;

namespace Mahjong.Unity.Tests.PlayMode
{
    /// <summary>
    /// Real LocalHost + real Client + real Table scene/buttons. It deliberately
    /// exercises the delivered presenter entry points and the authoritative V2
    /// receipt chain; it never calls LocalTableHost/TableRuntime control methods
    /// directly. After a control change the accepted host fences the old seat
    /// stream, so the test performs the same explicit reconnect (fresh session
    /// baseline) that the product requires and verifies that reconnect does not
    /// auto-claim.
    /// </summary>
    [TestFixture]
    public sealed class TableControlPlayModeTests
    {
        private AppComposition? _composition;
        private MainThreadDispatcher? _dispatcher;
        private TablePresenter? _presenter;
        private TableReceiptResolvedEventArgs? _lastReceipt;

        [SetUp]
        public void SetUp()
        {
            UiInputLifecycle.ResetForTest();
            Application.runInBackground = true;
            UiInputLifecycle.Instance.OnResume();
            UiInputLifecycle.Instance.OnFocusGained();
            _lastReceipt = null;
        }

        [TearDown]
        public void TearDown()
        {
            if (_composition != null)
            {
                try
                {
                    _composition.ShutdownAsync("TEST_CONTROL_TEARDOWN").Wait(TimeSpan.FromSeconds(10));
                }
                catch
                {
                    // Best effort on teardown.
                }
            }
        }

        [UnityTest]
        public IEnumerator RealLocalHost_ReleaseThenReconnect_ClaimAndOldEpochRejection()
        {
            string root = Path.GetFullPath(Path.Combine(
                Application.dataPath,
                "..",
                "build",
                "p45-control-playmode",
                Guid.NewGuid().ToString("N")));

            var config = new AppConfig
            {
                pumpBarrierTimeoutMs = 10000,
                closeTimeoutMs = 10000,
                cancelObservationMs = 100
            };
            _dispatcher = new MainThreadDispatcher();
            _composition = new AppComposition(
                config,
                _dispatcher,
                channelFactory: new LocalTableChannelFactory(new AppConfig(), new UnityAppPaths(root)),
                persistentDataPath: root);

            yield return OpenAndLoadTableAsync();
            TablePresenter first = RequirePresenter();
            yield return WaitForView(first, view => view.Control != null && !view.Control.ControlledByComputer,
                "an initial human control state");
            Assert.IsNotNull(first.LatestView!.Own!.Decision, "The real local host must open the initial decision.");

            PlayerTableViewState initial = first.LatestView!;
            ulong initialEpoch = initial.Control!.ControlEpoch;
            string initialRoundId = initial.Identity.RoundId;
            string oldDecisionId = initial.Own!.Decision!.DecisionId;
            ulong oldDecisionEpoch = initial.Own.Decision.ControlEpoch;

            ClickControlButton(first, "_releaseControlButton");
            yield return WaitForPending(first, CommandKind.ReleaseControl, "the ReleaseControl pending intent");
            yield return WaitForControlReceipt(CommandKind.ReleaseControl, "the ReleaseControl receipt");
            Assert.AreEqual(OutboxDisposition.Recorded, _lastReceipt!.Resolution.Disposition);
            Assert.AreEqual("LOCAL_CONTROL", _lastReceipt.Resolution.ReasonCode);

            // The accepted host fences the old seat stream on an epoch change.
            // A fresh explicit reconnect is required; it must not auto-claim.
            yield return ReconnectAsync();
            TablePresenter afterRelease = RequirePresenter();
            yield return WaitForView(afterRelease, view => view.Control != null &&
                                                          view.Control.ControlledByComputer &&
                                                          view.Control.ControlEpoch == initialEpoch + 1UL,
                "the reconnected automated control state");
            Assert.IsNull(afterRelease.InteractionState.Pending,
                "Reconnect must not auto-submit a claim.");

            ClickControlButton(afterRelease, "_claimControlButton");
            yield return WaitForPending(afterRelease, CommandKind.ClaimControl, "the ClaimControl pending intent");
            yield return WaitForControlReceipt(CommandKind.ClaimControl, "the ClaimControl receipt");
            Assert.AreEqual(OutboxDisposition.Recorded, _lastReceipt!.Resolution.Disposition);

            yield return ReconnectAsync();
            TablePresenter afterClaim = RequirePresenter();
            yield return WaitForView(afterClaim, view => view.Control != null &&
                                                          !view.Control.ControlledByComputer &&
                                                          view.Control.ControlEpoch == initialEpoch + 2UL,
                "the reconnected human control state");
            PlayerTableViewState reclaimed = afterClaim.LatestView!;
            Assert.AreEqual(initialEpoch + 2UL, reclaimed.Control!.ControlEpoch);
            Assert.AreEqual(initialRoundId, reclaimed.Identity.RoundId);

            // A stale control-epoch action must be rejected by the real host and
            // arrive as a Rejected receipt through the real Client.
            yield return WaitForView(afterClaim, view => view.Own?.Decision != null,
                "a decision after reclaiming control");
            PlayerDecisionState decision = afterClaim.LatestView!.Own!.Decision!;
            PlayerLegalActionState? discard = null;
            for (int i = 0; i < decision.Candidates.Count; i++)
            {
                if (decision.Candidates[i].Action == PlayerActionKind.DiscardTile)
                {
                    discard = decision.Candidates[i];
                    break;
                }
            }

            Assert.IsNotNull(discard, "The reclaimed human decision must expose a real discard candidate.");
            BusinessPayload stalePayload = CommandPayloadFactory.SubmitAction(
                reclaimed.Identity.RoundId,
                decision.DecisionId,
                oldDecisionEpoch,
                ProtocolActionType.DiscardTile,
                string.IsNullOrEmpty(discard!.OptionId) ? null : discard.OptionId,
                discard.TileIds);
            var staleCommand = new ProtocolCommand(
                CommandKind.SubmitAction,
                stalePayload,
                new RequestId("p45-stale-epoch-" + Guid.NewGuid().ToString("N")),
                new RequestScopeId(reclaimed.Identity.ScopeId),
                new StateVersion(reclaimed.Identity.StateVersion),
                new RoundId(reclaimed.Identity.RoundId),
                new DecisionId(decision.DecisionId),
                new ControlEpoch(oldDecisionEpoch),
                new OwnerEpoch(reclaimed.Identity.OwnerEpoch));

            _lastReceipt = null;
            Task<CommandSubmission> submit = _composition.SessionCoordinator!.SubmitAsync(
                staleCommand,
                CancellationToken.None);
            while (!submit.IsCompleted)
            {
                _dispatcher?.Update();
                yield return null;
            }
            Assert.IsTrue(submit.Result.Sent);
            yield return WaitForControlReceipt(CommandKind.SubmitAction, "the stale-epoch action receipt");
            Assert.AreEqual(ReceiptStatus.Rejected, _lastReceipt!.Command.LastReceipt,
                "A stale control-epoch action must not be accepted.");
            Assert.AreEqual(ProtocolErrorCode.StaleControlEpoch, _lastReceipt.Resolution.ReasonCode);
        }

        [UnityTest]
        public IEnumerator UnsentSubmitFailuresClearPendingAndPossibleDeliveryBecomesUnknown()
        {
            string root = Path.GetFullPath(Path.Combine(
                Application.dataPath,
                "..",
                "build",
                "p45-control-playmode",
                Guid.NewGuid().ToString("N")));
            var config = new AppConfig
            {
                pumpBarrierTimeoutMs = 10000,
                closeTimeoutMs = 10000,
                cancelObservationMs = 100
            };
            _dispatcher = new MainThreadDispatcher();
            _composition = new AppComposition(
                config,
                _dispatcher,
                channelFactory: new LocalTableChannelFactory(new AppConfig(), new UnityAppPaths(root)),
                persistentDataPath: root);

            yield return OpenAndLoadTableAsync();
            TablePresenter presenter = RequirePresenter();
            yield return WaitForView(presenter, view => view.Control != null && !view.Control.ControlledByComputer,
                "an initial human control state for submit-failure probes");

            SessionViewState baseline = _composition.SessionCoordinator!.Current;
            var staticView = new StaticSessionView(baseline);
            var sink = new FakeCommandSink
            {
                CanSubmit = false,
                Generation = baseline.Generation
            };

            presenter.Bind(sink, new NoopNavigation(), staticView, null);
            yield return null;
            ClickControlButton(presenter, "_releaseControlButton");
            yield return WaitForPendingCleared(presenter, "sink unavailable");
            Assert.AreEqual(0, sink.Calls, "An unavailable sink must not receive the command.");

            sink.CanSubmit = true;
            sink.Generation = baseline.Generation + 1;
            presenter.Bind(sink, new NoopNavigation(), staticView, null);
            yield return null;
            ClickControlButton(presenter, "_releaseControlButton");
            yield return WaitForPendingCleared(presenter, "sink generation mismatch");
            Assert.AreEqual(0, sink.Calls, "A generation-mismatched sink must not receive the command.");

            sink.Generation = baseline.Generation;
            sink.ThrowCancellation = true;
            presenter.Bind(sink, new NoopNavigation(), staticView, null);
            yield return null;
            ClickControlButton(presenter, "_releaseControlButton");
            yield return WaitForPendingUnknown(presenter, "possible-delivery exception");
            Assert.AreEqual(1, sink.Calls);
            string requestId = presenter.InteractionState.Pending!.Intent.Command.RequestId.Value;
            Assert.IsTrue(presenter.InteractionState.Pending.NeedsReconciliation);
            Assert.IsTrue(presenter.InteractionState.Pending.LastOutcome == TablePendingOutcome.Unknown);

            // A second click must not allocate a new request or reach the sink.
            ClickControlButton(presenter, "_releaseControlButton", expectInteractable: false);
            yield return null;
            Assert.AreEqual(1, sink.Calls, "An unknown delivery must never be retried automatically.");
            Assert.AreEqual(requestId, presenter.InteractionState.Pending!.Intent.Command.RequestId.Value);
        }

        private IEnumerator OpenAndLoadTableAsync()
        {
            Task<SessionCoordinator> open = _composition!.OpenLocalSessionAsync(CancellationToken.None);
            while (!open.IsCompleted)
            {
                _dispatcher?.Update();
                yield return null;
            }
            Assert.IsTrue(open.IsCompletedSuccessfully, "The real local composition must open: " + open.Exception);
            _composition.SessionCoordinator!.ReceiptResolved += OnReceiptResolved;

            AsyncOperation load = SceneManager.LoadSceneAsync("Table", LoadSceneMode.Single);
            Assert.IsNotNull(load, "Table scene must be loadable in PlayMode");
            while (!load.isDone)
            {
                _dispatcher?.Update();
                yield return null;
            }

            yield return null;

            TablePresenter presenter = RequirePresenter();
            presenter.Bind(
                _composition.SessionCoordinator,
                new NoopNavigation(),
                _composition.SessionCoordinator,
                null);
            yield return WaitForView(presenter, view => view.Identity.IsPrivateAuthorized,
                "the first confirmed table baseline");
        }

        private IEnumerator ReconnectAsync()
        {
            SessionCoordinator? closing = _composition!.SessionCoordinator;
            if (closing != null)
            {
                closing.ReceiptResolved -= OnReceiptResolved;
            }

            Task<ShutdownResult> shutdown = _composition.ShutdownAsync("PLAYMODE_CONTROL_RECONNECT");
            while (!shutdown.IsCompleted)
            {
                _dispatcher?.Update();
                yield return null;
            }
            Assert.AreEqual(ShutdownOutcome.Completed, shutdown.Result.Outcome);
            yield return OpenAndLoadTableAsync();
        }

        private void OnReceiptResolved(object? sender, TableReceiptResolvedEventArgs e)
        {
            _lastReceipt = e;
        }

        private IEnumerator WaitForPendingCleared(TablePresenter presenter, string label)
        {
            float deadline = Time.realtimeSinceStartup + 5f;
            while (Time.realtimeSinceStartup < deadline)
            {
                _dispatcher?.Update();
                if (presenter.InteractionState.Pending == null)
                {
                    yield break;
                }

                yield return null;
            }

            Assert.Fail("Timed out waiting for pending clear after " + label);
        }

        private IEnumerator WaitForPendingUnknown(TablePresenter presenter, string label)
        {
            float deadline = Time.realtimeSinceStartup + 5f;
            while (Time.realtimeSinceStartup < deadline)
            {
                _dispatcher?.Update();
                TablePendingIntent? pending = presenter.InteractionState.Pending;
                if (pending != null &&
                    pending.LastOutcome == TablePendingOutcome.Unknown &&
                    pending.NeedsReconciliation)
                {
                    yield break;
                }

                yield return null;
            }

            Assert.Fail("Timed out waiting for unknown pending after " + label);
        }

        private IEnumerator WaitForPending(TablePresenter presenter, CommandKind kind, string label)
        {
            float deadline = Time.realtimeSinceStartup + 10f;
            while (Time.realtimeSinceStartup < deadline)
            {
                _dispatcher?.Update();
                TablePendingIntent? pending = presenter.InteractionState.Pending;
                if (pending != null && pending.Intent.Kind == kind)
                {
                    yield break;
                }

                yield return null;
            }

            Assert.Fail("Timed out waiting for " + label + "; status=" + ReadControlStatus(presenter));
        }

        private static string ReadControlStatus(TablePresenter presenter)
        {
            FieldInfo? field = typeof(TablePresenter).GetField("_controlStatusText", BindingFlags.Instance | BindingFlags.NonPublic);
            TMPro.TextMeshProUGUI? text = field?.GetValue(presenter) as TMPro.TextMeshProUGUI;
            return text?.text ?? "<none>";
        }

        private IEnumerator WaitForControlReceipt(CommandKind kind, string label)
        {
            float deadline = Time.realtimeSinceStartup + 20f;
            while (Time.realtimeSinceStartup < deadline)
            {
                _dispatcher?.Update();
                if (_lastReceipt != null &&
                    _lastReceipt.Command.Kind == kind &&
                    _lastReceipt.Command.LastReceipt.HasValue)
                {
                    yield break;
                }

                yield return null;
            }

            Assert.Fail("Timed out waiting for " + label + "; last=" + (_lastReceipt?.Resolution.ReasonCode ?? "<none>"));
        }

        private IEnumerator WaitForView(
            TablePresenter presenter,
            Func<PlayerTableViewState, bool> predicate,
            string label)
        {
            float deadline = Time.realtimeSinceStartup + 25f;
            while (Time.realtimeSinceStartup < deadline)
            {
                _dispatcher?.Update();
                PlayerTableViewState? view = presenter.LatestView;
                if (view != null && predicate(view))
                {
                    yield break;
                }

                yield return null;
            }

            Assert.Fail("Timed out waiting for " + label + "; lastView=" +
                        (presenter.LatestView == null ? "<null>" : presenter.LatestView.Identity.RoundId));
        }

        private static TablePresenter RequirePresenter()
        {
            TablePresenter? presenter = UnityEngine.Object.FindFirstObjectByType<TablePresenter>();
            Assert.IsNotNull(presenter, "TablePresenter must exist in the real Table scene");
            return presenter!;
        }

        private static void ClickControlButton(TablePresenter presenter, string fieldName, bool expectInteractable = true)
        {
            FieldInfo? field = typeof(TablePresenter).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, fieldName + " must be serialized on TablePresenter");
            Button? button = field!.GetValue(presenter) as Button;
            Assert.IsNotNull(button, fieldName + " must be bound to a real scene Button");
            Assert.IsTrue(button!.gameObject.activeInHierarchy, fieldName + " must be visible for the authorized owner");
            if (expectInteractable)
            {
                Assert.IsTrue(button.interactable, fieldName + " must be enabled by the confirmed state");
            }
            else
            {
                Assert.IsFalse(button.interactable,
                    fieldName + " must stay disabled while the delivery outcome is unknown");
            }
            UiInputLifecycle.Instance.OnResume();
            UiInputLifecycle.Instance.OnFocusGained();
            UiInputLifecycle.Instance.ClearConsumedSubmit(button.gameObject);
            button.onClick.Invoke();
        }

        private sealed class StaticSessionView : ISessionView
        {
            public StaticSessionView(SessionViewState current)
            {
                Current = current;
            }

            public SessionViewState Current { get; }

            public event EventHandler<SessionViewState>? Changed
            {
                add { }
                remove { }
            }
        }

        private sealed class FakeCommandSink : ITableCommandSink
        {
            public bool CanSubmit { get; set; } = true;

            public long Generation { get; set; }

            public bool ThrowCancellation { get; set; }

            public int Calls { get; private set; }

            public event EventHandler<TableReceiptResolvedEventArgs>? ReceiptResolved
            {
                add { }
                remove { }
            }

            public Task<CommandSubmission> SubmitAsync(ProtocolCommand command, CancellationToken cancellationToken)
            {
                Calls++;
                if (ThrowCancellation)
                {
                    throw new OperationCanceledException("simulated possible delivery", cancellationToken);
                }

                throw new InvalidOperationException("The fake sink only supports the cancellation probe.");
            }
        }

        private sealed class NoopNavigation : IAppNavigation
        {
            public bool IsLocalGameStartAvailable => false;

            public void OpenLobby()
            {
            }

            public void BackToMenu()
            {
            }

            public void RequestQuit()
            {
            }

            public Task<LocalGameStartResult> StartLocalGameAsync()
            {
                return Task.FromResult(LocalGameStartResult.Failed("TEST_NAVIGATION"));
            }
        }
    }
}
