#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Unity.App;
using Mahjong.Unity.Platform;
using Mahjong.Unity.Table;
using Mahjong.Unity.UI;
using Mahjong.Unity.UI.Session;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Mahjong.Unity.Tests.EditMode
{
    /// <summary>
    /// P4.2-B composition tests: the local-game-start capability state machine
    /// (LobbyPresenter button states), AppRouter stable error-code mapping, open
    /// dedup / late-abandon invariants with scripted factories, and the production
    /// default factory driving the accepted pure .NET local host through the App
    /// public surface. The real-host cases below use only App/Client/Table types,
    /// never the forbidden Mahjong.LocalHost or Domain namespaces.
    /// </summary>
    [TestFixture]
    public sealed class LocalHostCompositionTests
    {
#if UNITY_EDITOR
        private readonly List<GameObject> _roots = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (var root in _roots)
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
            }
            _roots.Clear();
            UiInputLifecycle.ResetForTest();
        }

        private sealed class ScriptedNavigation : IAppNavigation
        {
            public bool Available;
            public int StartCalls;
            public Func<Task<LocalGameStartResult>> StartImpl =
                () => Task.FromResult(LocalGameStartResult.Started());
            private readonly TaskCompletionSource<LocalGameStartResult>? _pending;

            public ScriptedNavigation() { }
            public ScriptedNavigation(TaskCompletionSource<LocalGameStartResult> pending) { _pending = pending; }

            public void OpenLobby() { }
            public void BackToMenu() { }
            public void RequestQuit() { }

            public bool IsLocalGameStartAvailable => Available;

            public Task<LocalGameStartResult> StartLocalGameAsync()
            {
                StartCalls++;
                if (_pending != null) return _pending.Task;
                return StartImpl();
            }
        }

        private sealed class ScriptedChannel : ITableChannel, IDisposable
        {
            public bool IsOpen { get; private set; }
            public bool FaultClose;
            public int CloseCalls;
            public int DisposeCalls;

            public Task OpenAsync(TableChannelConnectRequest request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IsOpen = true;
                return Task.CompletedTask;
            }

            public Task SendAsync(ReadOnlyMemory<byte> wireEnvelope, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }

            public Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return Task.FromCanceled<ReadOnlyMemory<byte>>(cancellationToken);
                }

                var tcs = new TaskCompletionSource<ReadOnlyMemory<byte>>();
                cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
                return tcs.Task;
            }

            public Task CloseAsync(TableChannelCloseReason reason, CancellationToken cancellationToken)
            {
                CloseCalls++;
                IsOpen = false;
                if (FaultClose)
                {
                    throw new InvalidOperationException("PROBE_CLOSE_FAULT");
                }

                return Task.CompletedTask;
            }

            public void Dispose()
            {
                DisposeCalls++;
                IsOpen = false;
            }
        }

        private sealed class ScriptedChannelFactory : ITableChannelFactory
        {
            public bool Delay;
            public bool DelayIgnoresCancellation;
            public int Creates;
            public Exception? ThrowOnCreate;
            public readonly TaskCompletionSource<bool> Release =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly ScriptedChannel Channel = new ScriptedChannel();

            public async Task<ITableChannel> CreateAsync(TableTransportKind kind, CancellationToken cancellationToken)
            {
                Creates++;
                cancellationToken.ThrowIfCancellationRequested();
                if (ThrowOnCreate != null) throw ThrowOnCreate;
                if (Delay)
                {
                    if (DelayIgnoresCancellation)
                    {
                        // Bounded-timeout evidence needs a factory that cannot be
                        // released by shutdown cancellation.
                        await Release.Task;
                    }
                    else
                    {
                        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
                        {
                            await Task.WhenAny(Release.Task, cancelled.Task);
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }

                return Channel;
            }
        }

        private sealed class ScriptedSessionView : ISessionView
        {
            private EventHandler<SessionViewState>? _changed;

            public int AddCalls { get; private set; }
            public int RemoveCalls { get; private set; }
            public int HandlerCount => _changed?.GetInvocationList().Length ?? 0;

            public SessionViewState Current { get; } = new SessionViewState(
                1, TableConnectionState.Closed, null, null, null, null, null, null);

            public event EventHandler<SessionViewState>? Changed
            {
                add
                {
                    AddCalls++;
                    _changed += value;
                }
                remove
                {
                    RemoveCalls++;
                    _changed -= value;
                }
            }

            public void Raise()
            {
                _changed?.Invoke(this, Current);
            }
        }

        private sealed class LobbyUiFixture
        {
            public LobbyPresenter Presenter = null!;
            public Button StartButton = null!;
            public TMP_Text StartLabel = null!;
            public TextMeshProUGUI StatusText = null!;
            public TextMeshProUGUI ExplanationText = null!;
        }

        private LobbyUiFixture BuildLobbyUi(
            IAppNavigation navigation,
            bool initializeBeforeStart = true,
            ISessionView? sessionView = null)
        {
            var rootGo = new GameObject("LobbyRoot");
            _roots.Add(rootGo);

            var startGo = new GameObject("StartLocalGameButton");
            startGo.transform.SetParent(rootGo.transform, false);
            var startButton = startGo.AddComponent<Button>();
            startGo.AddComponent<UiPointerLifecycleTrigger>();
            var labelGo = new GameObject("Text");
            labelGo.transform.SetParent(startGo.transform, false);
            var startLabel = labelGo.AddComponent<TextMeshProUGUI>();
            startLabel.text = "开始本地对局";

            var statusGo = new GameObject("StatusText");
            statusGo.transform.SetParent(rootGo.transform, false);
            var statusText = statusGo.AddComponent<TextMeshProUGUI>();
            statusText.text = "初始状态";

            var expGo = new GameObject("ExplanationText");
            expGo.transform.SetParent(rootGo.transform, false);
            var expText = expGo.AddComponent<TextMeshProUGUI>();
            expText.text = "初始说明";

            var presenterGo = new GameObject("LobbyPresenter");
            presenterGo.transform.SetParent(rootGo.transform, false);
            var presenter = presenterGo.AddComponent<LobbyPresenter>();
            var so = new SerializedObject(presenter);
            so.FindProperty("_startLocalGameButton").objectReferenceValue = startButton;
            so.FindProperty("_statusText").objectReferenceValue = statusText;
            so.FindProperty("_explanationText").objectReferenceValue = expText;
            so.ApplyModifiedPropertiesWithoutUndo();

            if (initializeBeforeStart)
            {
                presenter.Initialize(navigation, sessionView);
            }

            // EditMode does not dispatch MonoBehaviour lifecycle messages, and Unity's
            // SendMessage path trips the editor's ShouldRunBehaviour assertion. Direct
            // reflection invocation calls the same private Start() the player would run,
            // exactly as the real lobby flow does after Initialize.
            InvokeStart(presenter);

            if (!initializeBeforeStart)
            {
                presenter.Initialize(navigation, sessionView);
            }

            return new LobbyUiFixture
            {
                Presenter = presenter,
                StartButton = startButton,
                StartLabel = startLabel,
                StatusText = statusText,
                ExplanationText = expText
            };
        }

        private static void InvokeStart(LobbyPresenter presenter)
        {
            typeof(LobbyPresenter)
                .GetMethod("Start", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(presenter, null);
        }

        private static void InvokeOnDestroy(LobbyPresenter presenter)
        {
            typeof(LobbyPresenter)
                .GetMethod("OnDestroy", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(presenter, null);
        }

        // ------------------------------------------------------------------
        // LobbyPresenter capability state machine
        // ------------------------------------------------------------------

        [Test]
        public void CapabilityUnavailable_DisablesButtonWithStableReason_AndBlocksStart()
        {
            var nav = new ScriptedNavigation { Available = false };
            var ui = BuildLobbyUi(nav);

            Assert.IsFalse(ui.StartButton.interactable, "Unavailable capability must keep the start button disabled.");
            Assert.AreEqual("开始本地对局 (不可用)", ui.StartLabel.text);
            StringAssert.Contains("LOCAL_HOST_UNAVAILABLE", ui.StatusText.text, "Status must carry the stable unavailable code.");

            UiInputLifecycle.ResetForTest();
            ui.Presenter.OnStartLocalGameClicked();
            Assert.AreEqual(0, nav.StartCalls, "Clicks must not reach the navigation while capability is unavailable.");
        }

        [Test]
        public void CapabilityAvailable_EnablesButtonForRealAttempts()
        {
            var nav = new ScriptedNavigation { Available = true };
            var ui = BuildLobbyUi(nav);

            Assert.IsTrue(ui.StartButton.interactable, "Available capability must enable the start button.");
            Assert.AreEqual("开始本地对局", ui.StartLabel.text);
            Assert.AreEqual("本地对局可用", ui.StatusText.text);
        }

        [Test]
        public void InitializeAfterStart_RefreshesCapabilityToMatchStartBeforeInitialize()
        {
            var beforeNav = new ScriptedNavigation { Available = true };
            var before = BuildLobbyUi(beforeNav, initializeBeforeStart: true);
            Assert.IsTrue(before.StartButton.interactable);
            Assert.AreEqual("开始本地对局", before.StartLabel.text);

            var afterNav = new ScriptedNavigation { Available = true };
            var after = BuildLobbyUi(afterNav, initializeBeforeStart: false);
            Assert.IsTrue(after.StartButton.interactable,
                "A late Initialize must recompute the capability instead of leaving the default disabled state.");
            Assert.AreEqual("开始本地对局", after.StartLabel.text);
            Assert.AreEqual(before.StatusText.text, after.StatusText.text,
                "Start-before-Initialize and Initialize-before-Start must converge on the same status.");
        }

        [Test]
        public void RepeatedInitialize_ReplacesSessionSubscriptionInsteadOfDuplicatingIt()
        {
            var nav = new ScriptedNavigation { Available = true };
            var view = new ScriptedSessionView();
            var ui = BuildLobbyUi(nav, initializeBeforeStart: true, sessionView: view);

            Assert.AreEqual(1, view.HandlerCount, "Initialize must bind exactly one session listener.");
            ui.Presenter.Initialize(nav, view);
            ui.Presenter.Initialize(nav, view);
            Assert.AreEqual(1, view.HandlerCount, "Repeated Initialize must not accumulate duplicate listeners.");
            Assert.AreEqual(3, view.AddCalls);
            Assert.AreEqual(3, view.RemoveCalls,
                "Every rebind must remove before adding, even the first bind after a null view.");

            Assert.DoesNotThrow(() => view.Raise(),
                "A single Changed notification with one listener must be handled once.");
        }

        [Test]
        public void TerminalResult_DisablesButtonShowsTerminalCodeAndNeverRetries()
        {
            var nav = new ScriptedNavigation
            {
                Available = true,
                StartImpl = () => Task.FromResult(
                    LocalGameStartResult.Terminal(LocalGameStartResult.LocalHostTerminalCode))
            };
            var ui = BuildLobbyUi(nav);

            UiInputLifecycle.ResetForTest();
            ui.Presenter.OnStartLocalGameClicked();
            Assert.AreEqual(1, nav.StartCalls);
            Assert.IsFalse(ui.Presenter.IsStartInFlight);
            Assert.IsTrue(ui.Presenter.IsTerminal);
            Assert.IsFalse(ui.StartButton.interactable, "A terminal result must leave the button disabled.");
            StringAssert.Contains(LocalGameStartResult.LocalHostTerminalCode, ui.StatusText.text);
            StringAssert.DoesNotContain("可重试", ui.StatusText.text,
                "A terminal failure must never be advertised as retryable.");

            UiInputLifecycle.ResetForTest();
            ui.Presenter.OnStartLocalGameClicked();
            Assert.AreEqual(1, nav.StartCalls, "A terminal lobby must not issue another start.");
        }

        [Test]
        public async Task TerminalResult_AfterOnDestroy_DoesNotWriteTmpOrButton()
        {
            var pending = new TaskCompletionSource<LocalGameStartResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var nav = new ScriptedNavigation(pending) { Available = true };
            var ui = BuildLobbyUi(nav);

            UiInputLifecycle.ResetForTest();
            ui.Presenter.OnStartLocalGameClicked();
            Assert.AreEqual(1, nav.StartCalls);
            string statusBefore = ui.StatusText.text;
            string labelBefore = ui.StartLabel.text;
            bool interactableBefore = ui.StartButton.interactable;

            // Simulate the scene being replaced while the open is still in flight.
            InvokeOnDestroy(ui.Presenter);
            pending.TrySetResult(LocalGameStartResult.Terminal(LocalGameStartResult.LocalHostTerminalCode));
            await Task.Delay(150);

            Assert.AreEqual(statusBefore, ui.StatusText.text, "Late completion must not write TMP after OnDestroy.");
            Assert.AreEqual(labelBefore, ui.StartLabel.text, "Late completion must not rewrite the button label.");
            Assert.AreEqual(interactableBefore, ui.StartButton.interactable,
                "Late completion must not change the button interactable state after OnDestroy.");
        }

        [Test]
        public void ClickWhileInputPaused_IsIgnored()
        {
            var nav = new ScriptedNavigation { Available = true };
            var ui = BuildLobbyUi(nav);

            UiInputLifecycle.ResetForTest();
            UiInputLifecycle.Instance.OnPause();
            ui.Presenter.OnStartLocalGameClicked();
            Assert.AreEqual(0, nav.StartCalls, "Paused input lifecycle must swallow start clicks.");
        }

        [Test]
        public void DoubleClickWhileInFlight_StartsExactlyOnce_AndShowsOpeningState()
        {
            var pending = new TaskCompletionSource<LocalGameStartResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var nav = new ScriptedNavigation(pending) { Available = true };
            var ui = BuildLobbyUi(nav);

            UiInputLifecycle.ResetForTest();
            ui.Presenter.OnStartLocalGameClicked();
            Assert.AreEqual(1, nav.StartCalls);
            Assert.IsTrue(ui.Presenter.IsStartInFlight, "First click must mark the attempt in flight.");
            Assert.IsFalse(ui.StartButton.interactable, "Opening state must disable the start button.");
            StringAssert.Contains("正在打开", ui.StatusText.text, "Opening state must be visible to the user.");

            // Concurrent double-click: the second press must not issue a second start.
            UiInputLifecycle.ResetForTest();
            ui.Presenter.OnStartLocalGameClicked();
            Assert.AreEqual(1, nav.StartCalls, "Concurrent double-click must create at most one start attempt.");

            pending.TrySetResult(LocalGameStartResult.Cancelled());
        }

        [Test]
        public void FailedResult_KeepsButtonRetryable_WithStableCodeOnly()
        {
            var nav = new ScriptedNavigation
            {
                Available = true,
                StartImpl = () => Task.FromResult(LocalGameStartResult.Failed(LocalGameStartResult.LocalHostStartFailedCode))
            };
            var ui = BuildLobbyUi(nav);

            UiInputLifecycle.ResetForTest();
            ui.Presenter.OnStartLocalGameClicked();
            Assert.AreEqual(1, nav.StartCalls);
            Assert.IsFalse(ui.Presenter.IsStartInFlight);
            Assert.IsTrue(ui.StartButton.interactable, "Failed starts must leave the button retryable.");
            StringAssert.Contains("LOCAL_HOST_START_FAILED", ui.StatusText.text, "Failure state must surface the stable code.");
            StringAssert.Contains("可重试", ui.StatusText.text);

            // Retry after failure reaches the navigation again.
            UiInputLifecycle.ResetForTest();
            ui.Presenter.OnStartLocalGameClicked();
            Assert.AreEqual(2, nav.StartCalls, "Retry after failure must be possible.");
        }

        [Test]
        public void CancelledResult_RestoresCapabilityQuietly()
        {
            var nav = new ScriptedNavigation
            {
                Available = true,
                StartImpl = () => Task.FromResult(LocalGameStartResult.Cancelled())
            };
            var ui = BuildLobbyUi(nav);

            UiInputLifecycle.ResetForTest();
            ui.Presenter.OnStartLocalGameClicked();
            Assert.IsFalse(ui.Presenter.IsStartInFlight);
            Assert.IsTrue(ui.StartButton.interactable);
            Assert.IsFalse(ui.StatusText.text.Contains("失败"), "Cancellation must not render as an error.");
            Assert.AreEqual("本地对局可用", ui.StatusText.text);
        }

        [Test]
        public void BusyAndStartedResults_RestoreCapabilityState()
        {
            var busyNav = new ScriptedNavigation
            {
                Available = true,
                StartImpl = () => Task.FromResult(LocalGameStartResult.Busy())
            };
            var busyUi = BuildLobbyUi(busyNav);
            UiInputLifecycle.ResetForTest();
            busyUi.Presenter.OnStartLocalGameClicked();
            Assert.IsTrue(busyUi.StartButton.interactable, "Busy result must restore the capability state.");
            Assert.AreEqual("本地对局可用", busyUi.StatusText.text);

            var startedNav = new ScriptedNavigation
            {
                Available = true,
                StartImpl = () => Task.FromResult(LocalGameStartResult.Started())
            };
            var startedUi = BuildLobbyUi(startedNav);
            UiInputLifecycle.ResetForTest();
            startedUi.Presenter.OnStartLocalGameClicked();
            Assert.IsFalse(startedUi.Presenter.IsStartInFlight);
            Assert.IsTrue(startedUi.StartButton.interactable);
        }

        // ------------------------------------------------------------------
        // AppRouter stable error-code mapping
        // ------------------------------------------------------------------

        [Test]
        public async Task Router_ExplicitUnavailableFactory_MapsToStableCode()
        {
            var comp = new AppComposition(
                new AppConfig(),
                new MainThreadDispatcher(),
                channelFactory: new UnavailableLocalTableChannelFactory(),
                persistentDataPath: System.IO.Path.GetFullPath("build/p42b/store-" + Guid.NewGuid().ToString("N")));
            var router = new AppRouter(comp, new BackRouter());

            Assert.IsFalse(router.IsLocalGameStartAvailable,
                "The explicit unavailable test factory must report local start unavailable.");

            LocalGameStartResult result = await router.StartLocalGameAsync();
            Assert.AreEqual(LocalGameStartStatus.Failed, result.Status);
            Assert.AreEqual(LocalGameStartResult.LocalHostUnavailableCode, result.StableErrorCode);
            Assert.IsNull(comp.SessionCoordinator, "Failed opens must not leave a coordinator behind.");
        }

        [Test]
        public async Task Router_HostFailure_NeverMapsToUnavailableCode()
        {
            var f = new ScriptedChannelFactory { ThrowOnCreate = new InvalidOperationException("LEASE_OR_STATE_FAULT") };
            var comp = new AppComposition(
                new AppConfig(),
                new MainThreadDispatcher(),
                channelFactory: f,
                persistentDataPath: System.IO.Path.GetFullPath("build/p42b/store-" + Guid.NewGuid().ToString("N")));
            var router = new AppRouter(comp, new BackRouter());

            LocalGameStartResult result = await router.StartLocalGameAsync();
            Assert.AreEqual(LocalGameStartStatus.Failed, result.Status);
            Assert.AreEqual(LocalGameStartResult.LocalHostStartFailedCode, result.StableErrorCode,
                "Host failures must map to the generic stable code.");
            Assert.AreNotEqual(LocalGameStartResult.LocalHostUnavailableCode, result.StableErrorCode,
                "Non-unavailable host failures must never masquerade as LOCAL_HOST_UNAVAILABLE.");
            Assert.IsNull(comp.SessionCoordinator);
        }

        [Test]
        public async Task Router_CancelledOpen_MapsToCancelledWithoutError()
        {
            var f = new ScriptedChannelFactory { ThrowOnCreate = new OperationCanceledException("OPEN_CANCELLED") };
            var comp = new AppComposition(
                new AppConfig(),
                new MainThreadDispatcher(),
                channelFactory: f,
                persistentDataPath: System.IO.Path.GetFullPath("build/p42b/store-" + Guid.NewGuid().ToString("N")));
            var router = new AppRouter(comp, new BackRouter());

            LocalGameStartResult result = await router.StartLocalGameAsync();
            Assert.AreEqual(LocalGameStartStatus.Cancelled, result.Status);
            Assert.IsNull(result.StableErrorCode);
            Assert.IsNull(comp.SessionCoordinator);

            // The owner gate must be released by the first attempt's finally; a later
            // start is allowed to reach the factory again instead of staying Busy.
            LocalGameStartResult retry = await router.StartLocalGameAsync();
            Assert.AreEqual(LocalGameStartStatus.Cancelled, retry.Status);
            Assert.AreEqual(2, f.Creates, "A cancelled start must release the owner gate.");
        }

        [Test]
        public async Task Router_SceneChangedDuringOpen_AbandonsAndCleansSession()
        {
            var f = new ScriptedChannelFactory { Delay = true };
            var comp = new AppComposition(
                new AppConfig { pumpBarrierTimeoutMs = 2000, closeTimeoutMs = 2000, cancelObservationMs = 10 },
                new MainThreadDispatcher(),
                channelFactory: f,
                persistentDataPath: System.IO.Path.GetFullPath("build/p42b/store-" + Guid.NewGuid().ToString("N")));
            var router = new AppRouter(comp, new BackRouter());

            Task<LocalGameStartResult> open = router.StartLocalGameAsync();
            for (int i = 0; i < 500 && f.Creates == 0; i++) await Task.Delay(10);
            Assert.AreEqual(1, f.Creates, "Factory must be entered exactly once for a single start attempt.");

            // Simulate the user leaving the lobby while the open is still in flight.
            var sceneField = typeof(AppRouter).GetField("_currentScene", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(sceneField, "Router scene tracking field must exist for the abandon simulation.");
            sceneField!.SetValue(router, SceneId.Menu);

            f.Release.TrySetResult(true);
            var winner = await Task.WhenAny(open, Task.Delay(10000));
            Assert.AreSame(open, winner, "Start attempt must complete after the channel is released.");

            LocalGameStartResult result = await open;
            Assert.AreEqual(LocalGameStartStatus.Cancelled, result.Status,
                "A late open completion after the user left must surface as cancellation, not as entering the table.");
            Assert.IsNull(comp.SessionCoordinator, "Abandoned opens must release the session; no host/actor may be orphaned.");
            Assert.IsFalse(f.Channel.IsOpen, "The scripted channel must be closed by the abandon shutdown.");

            ShutdownResult finalState = await comp.ShutdownAsync("TEST_FINAL");
            Assert.AreEqual(ShutdownOutcome.Completed, finalState.Outcome);
            Assert.IsTrue(finalState.MenuAllowed, "Abandoned-open cleanup must leave the composition reusable, not quarantined.");
        }

        private static async Task WaitForCreate(ScriptedChannelFactory factory)
        {
            for (int i = 0; i < 500 && factory.Creates == 0; i++)
            {
                await Task.Delay(10);
            }
            Assert.AreEqual(1, factory.Creates, "Factory must be entered exactly once.");
        }

        private static void SetScene(AppRouter router, SceneId scene)
        {
            var sceneField = typeof(AppRouter).GetField("_currentScene", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(sceneField);
            sceneField!.SetValue(router, scene);
        }

        [Test]
        public async Task Router_Reentry_SecondStartIsBusy_AndOwnerAloneCleansUp()
        {
            var f = new ScriptedChannelFactory { Delay = true };
            var comp = new AppComposition(
                new AppConfig { pumpBarrierTimeoutMs = 2000, closeTimeoutMs = 2000, cancelObservationMs = 10 },
                new MainThreadDispatcher(),
                channelFactory: f,
                persistentDataPath: System.IO.Path.GetFullPath("build/p42b/store-" + Guid.NewGuid().ToString("N")));
            var router = new AppRouter(comp, new BackRouter());
            SetScene(router, SceneId.Lobby);

            Task<LocalGameStartResult> first = router.StartLocalGameAsync();
            Task<LocalGameStartResult> second = router.StartLocalGameAsync();
            await WaitForCreate(f);

            Assert.IsTrue(second.IsCompleted, "The second concurrent start must be rejected without entering the open.");
            LocalGameStartResult busy = await second;
            Assert.AreEqual(LocalGameStartStatus.Busy, busy.Status);
            Assert.AreEqual(1, f.Creates, "The busy second call must not create a second channel.");

            // Complete after a scene change so the first owner takes its cleanup path.
            SetScene(router, SceneId.Menu);
            f.Release.TrySetResult(true);
            var winner = await Task.WhenAny(first, Task.Delay(10000));
            Assert.AreSame(first, winner, "The owner start must complete.");
            LocalGameStartResult result = await first;
            Assert.AreEqual(LocalGameStartStatus.Cancelled, result.Status);
            Assert.IsNull(comp.SessionCoordinator);
            Assert.IsFalse(f.Channel.IsOpen, "Only the owner cleaned the abandoned session.");
            Assert.AreEqual(1, f.Channel.CloseCalls, "The second caller must not run a second shutdown.");
        }

        [Test]
        public async Task Router_CloseFaultDuringAbandon_ReturnsTerminalAndBlocksRetry()
        {
            var f = new ScriptedChannelFactory { Delay = true };
            f.Channel.FaultClose = true;
            var comp = new AppComposition(
                new AppConfig { pumpBarrierTimeoutMs = 2000, closeTimeoutMs = 2000, cancelObservationMs = 10 },
                new MainThreadDispatcher(),
                channelFactory: f,
                persistentDataPath: System.IO.Path.GetFullPath("build/p42b/store-" + Guid.NewGuid().ToString("N")));
            var router = new AppRouter(comp, new BackRouter());
            SetScene(router, SceneId.Lobby);

            Task<LocalGameStartResult> start = router.StartLocalGameAsync();
            await WaitForCreate(f);
            SetScene(router, SceneId.Menu);
            f.Release.TrySetResult(true);
            var winner = await Task.WhenAny(start, Task.Delay(10000));
            Assert.AreSame(start, winner, "The close-fault start must complete.");
            LocalGameStartResult result = await start;

            Assert.AreEqual(LocalGameStartStatus.Terminal, result.Status,
                "A CloseFault during abandon cleanup must surface as a stable terminal state.");
            Assert.AreEqual(LocalGameStartResult.LocalHostTerminalCode, result.StableErrorCode);
            Assert.IsTrue(comp.IsTerminal);
            Assert.IsFalse(comp.SupportsLocalStart);
            Assert.IsFalse(router.IsLocalGameStartAvailable,
                "A terminal composition must not advertise local-start capability.");
            Assert.IsFalse(await router.ReturnToMenuAsync(),
                "MenuAllowed=false must reject returning to the menu after quarantine.");

            LocalGameStartResult retry = await router.StartLocalGameAsync();
            Assert.AreEqual(LocalGameStartStatus.Terminal, retry.Status);
            Assert.AreEqual(1, f.Creates, "A terminal composition must not create another channel.");
        }

        [Test]
        public async Task Router_BoundedShutdownTimeout_ReturnsTerminalAndBlocksRetry()
        {
            var f = new ScriptedChannelFactory { Delay = true, DelayIgnoresCancellation = true };
            var comp = new AppComposition(
                new AppConfig { pumpBarrierTimeoutMs = 100, closeTimeoutMs = 100, cancelObservationMs = 10 },
                new MainThreadDispatcher(),
                channelFactory: f,
                persistentDataPath: System.IO.Path.GetFullPath("build/p42b/store-" + Guid.NewGuid().ToString("N")));
            var router = new AppRouter(comp, new BackRouter());
            SetScene(router, SceneId.Lobby);

            Task<LocalGameStartResult> start = router.StartLocalGameAsync();
            await WaitForCreate(f);

            ShutdownResult timeout = await comp.ShutdownAsync("TEST_BOUNDED_TIMEOUT");
            Assert.AreEqual(ShutdownOutcome.PumpBarrierTimeout, timeout.Outcome);
            Assert.IsTrue(timeout.Quarantined);
            Assert.IsFalse(timeout.MenuAllowed);

            f.Release.TrySetResult(true);
            var winner = await Task.WhenAny(start, Task.Delay(10000));
            Assert.AreSame(start, winner, "The start must settle after the bounded timeout released the factory.");
            LocalGameStartResult result = await start;

            Assert.AreEqual(LocalGameStartStatus.Terminal, result.Status,
                "A bounded open timeout must not be reported as a retryable failure or benign cancellation.");
            Assert.IsFalse(comp.SupportsLocalStart);
            Assert.IsFalse(router.IsLocalGameStartAvailable);

            LocalGameStartResult retry = await router.StartLocalGameAsync();
            Assert.AreEqual(LocalGameStartStatus.Terminal, retry.Status);
            Assert.AreEqual(1, f.Creates);
        }

        [Test]
        public async Task Composition_CleanShutdown_AllowsFreshSessionAndStartCapability()
        {
            var f = new ScriptedChannelFactory();
            var comp = new AppComposition(
                new AppConfig { pumpBarrierTimeoutMs = 2000, closeTimeoutMs = 2000, cancelObservationMs = 10 },
                new MainThreadDispatcher(),
                channelFactory: f,
                persistentDataPath: System.IO.Path.GetFullPath("build/p42b/store-" + Guid.NewGuid().ToString("N")));

            SessionCoordinator first = await comp.OpenLocalSessionAsync(CancellationToken.None);
            ShutdownResult clean = await comp.ShutdownAsync("TEST_CLEAN");
            Assert.AreEqual(ShutdownOutcome.Completed, clean.Outcome);
            Assert.IsTrue(clean.MenuAllowed);
            Assert.IsFalse(comp.IsTerminal);
            Assert.IsTrue(comp.SupportsLocalStart, "A clean shutdown must leave the composition reusable.");

            SessionCoordinator second = await comp.OpenLocalSessionAsync(CancellationToken.None);
            Assert.IsFalse(ReferenceEquals(first, second));
            Assert.AreEqual(2, f.Creates, "A fresh start after clean shutdown must create a new channel.");
            ShutdownResult final = await comp.ShutdownAsync("TEST_CLEAN_FINAL");
            Assert.AreEqual(ShutdownOutcome.Completed, final.Outcome);
        }

        // ------------------------------------------------------------------
        // Composition open dedup
        // ------------------------------------------------------------------

        [Test]
        public async Task Composition_ConcurrentDoubleOpen_SharesOneCoordinator()
        {
            var f = new ScriptedChannelFactory();
            var comp = new AppComposition(
                new AppConfig { pumpBarrierTimeoutMs = 2000, closeTimeoutMs = 2000, cancelObservationMs = 10 },
                new MainThreadDispatcher(),
                channelFactory: f,
                persistentDataPath: System.IO.Path.GetFullPath("build/p42b/store-" + Guid.NewGuid().ToString("N")));

            Task<SessionCoordinator> first = comp.OpenLocalSessionAsync(CancellationToken.None);
            Task<SessionCoordinator> second = comp.OpenLocalSessionAsync(CancellationToken.None);
            var all = Task.WhenAll(first, second);
            var timeout = Task.Delay(10000);
            var winner = await Task.WhenAny(all, timeout);
            Assert.AreSame(all, winner, "Both opens must complete in time.");

            Assert.IsTrue(ReferenceEquals(first.Result, second.Result),
                "Concurrent opens must share one session coordinator.");
            Assert.AreEqual(1, f.Creates, "Concurrent opens must create exactly one channel.");
            Assert.IsNotNull(comp.SessionCoordinator);

            ShutdownResult shutdown = await comp.ShutdownAsync("TEST_DEDUP_DONE");
            Assert.AreEqual(ShutdownOutcome.Completed, shutdown.Outcome);
        }

        // ------------------------------------------------------------------
        // Real accepted pure .NET local host through the App public surface
        // ------------------------------------------------------------------

        private static string NewRealRoot() =>
            Path.GetFullPath(Path.Combine("build", "p42b-realhost", Guid.NewGuid().ToString("N")));

        private static AppConfig RealConfig() => new AppConfig
        {
            pumpBarrierTimeoutMs = 5000,
            closeTimeoutMs = 5000,
            cancelObservationMs = 50
        };

        private static AppComposition NewRealComposition(string root, ITableChannelFactory? factory = null) =>
            new AppComposition(
                RealConfig(),
                new MainThreadDispatcher(),
                channelFactory: factory,
                persistentDataPath: root);

        private static async Task<SessionViewState> WaitForSession(
            AppComposition comp,
            Func<SessionViewState, bool> predicate,
            int timeoutMs,
            string label)
        {
            var started = DateTime.UtcNow;
            string last = string.Empty;
            while ((DateTime.UtcNow - started).TotalMilliseconds < timeoutMs)
            {
                SessionCoordinator? coordinator = comp.SessionCoordinator;
                if (coordinator != null)
                {
                    try
                    {
                        SessionViewState current = coordinator.Current;
                        last = "state=" + current.State +
                               " binding=" + (current.Binding != null) +
                               " snapshot=" + (current.PlayerSnapshot != null) +
                               " error=" + (current.LastError ?? "<none>");
                        if (predicate(current))
                        {
                            return current;
                        }
                    }
                    catch (Exception exception)
                    {
                        last = exception.GetType().Name + ": " + exception.Message;
                    }
                }

                await Task.Delay(25);
            }

            Assert.Fail("Timed out waiting for " + label + (last.Length > 0 ? " (" + last + ")" : string.Empty));
            return null!;
        }

        private static Task<SessionViewState> WaitForReady(AppComposition comp, int timeoutMs = 30000) =>
            WaitForSession(
                comp,
                state => state.State == TableConnectionState.Ready &&
                         state.Binding != null &&
                         state.PlayerSnapshot != null &&
                         state.PlayerSnapshot.PrivateView != null,
                timeoutMs,
                "real local session Ready with a private snapshot");

        private static async Task<TableCommandIntent> WaitForSubmittableAction(
            AppComposition comp,
            int timeoutMs = 20000)
        {
            var mapper = new PlayerTableProjectionMapper(new SystemTableClientClock());
            var intentFactory = new TableCommandIntentFactory(new SystemTableClientClock());
            var started = DateTime.UtcNow;
            string last = "no snapshot";
            while ((DateTime.UtcNow - started).TotalMilliseconds < timeoutMs)
            {
                SessionCoordinator? coordinator = comp.SessionCoordinator;
                if (coordinator != null)
                {
                    SessionViewState current = coordinator.Current;
                    if (current.State == TableConnectionState.Ready &&
                        current.Binding != null &&
                        current.PlayerSnapshot != null)
                    {
                        PlayerTableViewState view;
                        try
                        {
                            view = mapper.Project(current);
                        }
                        catch (Exception exception)
                        {
                            last = "projection: " + exception.Message;
                            await Task.Delay(25);
                            continue;
                        }

                        if (view.CanSubmit &&
                            view.Own?.Decision != null &&
                            view.Own.Decision.Candidates.Count > 0)
                        {
                            PlayerLegalActionState? action = null;
                            for (int i = 0; i < view.Own.Decision.Candidates.Count; i++)
                            {
                                if (view.Own.Decision.Candidates[i].Action == PlayerActionKind.DiscardTile)
                                {
                                    action = view.Own.Decision.Candidates[i];
                                    break;
                                }
                            }

                            action ??= view.Own.Decision.Candidates[0];
                            try
                            {
                                return intentFactory.CreateSubmitAction(
                                    view,
                                    action,
                                    new TableCommandOwnerContext(
                                        new RequestId("p42b-" + Guid.NewGuid().ToString("N")),
                                        new RequestScopeId(view.Identity.ScopeId),
                                        new StateVersion(view.Identity.StateVersion),
                                        new OwnerEpoch(view.Identity.OwnerEpoch)));
                            }
                            catch (TableIntentUnavailableException exception)
                            {
                                last = exception.Code;
                            }
                        }
                    }
                }

                await Task.Delay(25);
            }

            Assert.Fail("Timed out waiting for a submittable legal action: " + last);
            return null!;
        }

        [Test]
        public async Task DefaultComposition_IsRealLocalHost_AndPublishesBaselineSnapshot()
        {
            string root = NewRealRoot();
            var comp = NewRealComposition(root);
            try
            {
                Assert.IsTrue(comp.SupportsLocalStart,
                    "The production AppComposition default must advertise a real local host.");
                Assert.IsInstanceOf<LocalTableChannelFactory>(comp.ChannelFactory,
                    "The production default must be the real local host factory, not the unavailable stub.");

                SessionCoordinator coordinator = await comp.OpenLocalSessionAsync(CancellationToken.None);
                SessionViewState baseline = await WaitForReady(comp);

                Assert.AreSame(coordinator, comp.SessionCoordinator);
                Assert.AreEqual(LocalTableChannelFactory.DefaultRoomId, baseline.Binding!.RoomId,
                    "The production room id must be stable, not the old placeholder 'local'.");
                Assert.AreEqual(0, baseline.Binding.SeatId);
                Assert.IsNotNull(baseline.PlayerSnapshot);
                Assert.IsNotNull(baseline.PlayerSnapshot!.PrivateView);
                Assert.IsTrue(baseline.IsFullBaseline,
                    "The first confirmed publication must be a full baseline snapshot.");
                Assert.AreEqual(TableVisibleEventBatchKind.Baseline, baseline.VisibleEventBatch.Kind);
                Assert.AreEqual(0, baseline.VisibleEventBatch.PlayableEvents.Count,
                    "A baseline's historical events must never be replayed as new animation.");

                string save = Path.Combine(root, "local", LocalTableChannelFactory.DefaultRoomId + ".save");
                Assert.IsTrue(File.Exists(save), "The real host must own the local save file.");
                Assert.IsTrue(Directory.Exists(Path.Combine(root, "client", "outbox")),
                    "The client outbox must be created separately from the host save directory.");
                Assert.AreNotEqual(
                    Path.GetFullPath(Path.Combine(root, "local")),
                    Path.GetFullPath(Path.Combine(root, "client", "outbox")));
            }
            finally
            {
                ShutdownResult shutdown = await comp.ShutdownAsync("TEST_REAL_DEFAULT_DONE");
                Assert.AreEqual(ShutdownOutcome.Completed, shutdown.Outcome);
            }
        }

        [Test]
        public async Task RealFactory_PreCancelled_CreatesNoDirectoryTicketOrActor()
        {
            string root = NewRealRoot();
            var factory = new LocalTableChannelFactory(new AppConfig(), new UnityAppPaths(root));
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            OperationCanceledException? factoryError = Assert.CatchAsync<OperationCanceledException>(
                async () => { await factory.CreateAsync(TableTransportKind.Local, cts.Token); });
            Assert.IsNotNull(factoryError);
            Assert.IsTrue(cts.IsCancellationRequested);
            Assert.IsFalse(Directory.Exists(root),
                "A pre-cancelled factory call must not create the save/outbox root.");

            var comp = NewRealComposition(root);
            OperationCanceledException? compositionError = Assert.CatchAsync<OperationCanceledException>(
                async () => { await comp.OpenLocalSessionAsync(cts.Token); });
            Assert.IsNotNull(compositionError);
            Assert.IsFalse(Directory.Exists(root),
                "A pre-cancelled composition open must not create a directory, ticket, lease or actor.");
            Assert.IsNull(comp.SessionCoordinator);
        }

        [Test]
        public async Task RealHost_LegalDiscard_ProducesIncrementalConfirmedVisibleEvent()
        {
            string root = NewRealRoot();
            var comp = NewRealComposition(root);
            try
            {
                await comp.OpenLocalSessionAsync(CancellationToken.None);
                await WaitForReady(comp);

                TableCommandIntent intent = await WaitForSubmittableAction(comp);
                SessionCoordinator coordinator = comp.SessionCoordinator!;
                ulong before = coordinator.Current.PlayerSnapshot!.StateVersion;

                CommandSubmission submission = await coordinator.SubmitAsync(
                    intent.Command,
                    CancellationToken.None);
                Assert.IsTrue(submission.Sent, "The legal discard must reach the real local host.");

                SessionViewState confirmed = await WaitForSession(
                    comp,
                    state => (state.PlayerSnapshot?.StateVersion ?? 0UL) > before &&
                             state.VisibleEventBatch.Kind == TableVisibleEventBatchKind.Incremental &&
                             state.VisibleEventBatch.PlayableEvents.Count > 0,
                    30000,
                    "an incremental confirmed visible event after the legal action");

                Assert.Greater(confirmed.PlayerSnapshot!.StateVersion, before,
                    "A confirmed player action must advance the authoritative state version.");
                Assert.AreEqual(TableVisibleEventBatchKind.Incremental, confirmed.VisibleEventBatch.Kind);
                Assert.Greater(confirmed.VisibleEventBatch.PlayableEvents.Count, 0,
                    "A confirmed incremental batch must expose at least one playable visible event.");
            }
            finally
            {
                ShutdownResult shutdown = await comp.ShutdownAsync("TEST_REAL_ACTION_DONE");
                Assert.AreEqual(ShutdownOutcome.Completed, shutdown.Outcome);
            }
        }

        [Test]
        public async Task RealHost_ConcurrentOpen_SharesOneSessionAndOneSave()
        {
            string root = NewRealRoot();
            var comp = NewRealComposition(root);
            try
            {
                Task<SessionCoordinator> first = comp.OpenLocalSessionAsync(CancellationToken.None);
                Task<SessionCoordinator> second = comp.OpenLocalSessionAsync(CancellationToken.None);
                Task all = Task.WhenAll(first, second);
                Task winner = await Task.WhenAny(all, Task.Delay(30000));
                Assert.AreSame(all, winner, "Both concurrent opens must settle.");
                await all;

                Assert.AreSame(first.Result, second.Result,
                    "Concurrent opens must share one real host session.");
                await WaitForReady(comp);
                string[] saves = Directory.GetFiles(
                    Path.Combine(root, "local"),
                    LocalTableChannelFactory.DefaultRoomId + ".save");
                Assert.AreEqual(1, saves.Length, "One room must own exactly one save file.");
            }
            finally
            {
                ShutdownResult shutdown = await comp.ShutdownAsync("TEST_REAL_CONCURRENT_DONE");
                Assert.AreEqual(ShutdownOutcome.Completed, shutdown.Outcome);
            }
        }

        [Test]
        public async Task RealHost_SecondOwnerSameRoom_MapsLeaseConflict_AndKeepsOwnerAlive()
        {
            string sharedRoot = NewRealRoot();
            string secondRoot = NewRealRoot();
            var owner = NewRealComposition(sharedRoot);
            var second = new AppComposition(
                RealConfig(),
                new MainThreadDispatcher(),
                channelFactory: new LocalTableChannelFactory(new AppConfig(), new UnityAppPaths(sharedRoot)),
                persistentDataPath: secondRoot);
            try
            {
                await owner.OpenLocalSessionAsync(CancellationToken.None);
                await WaitForReady(owner);

                var router = new AppRouter(second, new BackRouter());
                SetScene(router, SceneId.Lobby);
                LocalGameStartResult result = await router.StartLocalGameAsync();

                Assert.AreEqual(LocalGameStartStatus.Failed, result.Status);
                Assert.AreEqual(LocalGameStartResult.LocalHostLeaseConflictCode, result.StableErrorCode,
                    "A second owner of the same room must surface the stable lease code.");
                Assert.AreNotEqual(LocalGameStartResult.LocalHostUnavailableCode, result.StableErrorCode,
                    "A lease conflict must never be disguised as a missing host.");
                Assert.IsFalse(second.IsTerminal, "A lease conflict is retryable and must not quarantine.");
                Assert.IsNull(second.SessionCoordinator);

                await WaitForReady(owner);
                Assert.IsTrue(File.Exists(
                    Path.Combine(sharedRoot, "local", LocalTableChannelFactory.DefaultRoomId + ".save")),
                    "The losing owner must not delete the owner's save.");
            }
            finally
            {
                await second.ShutdownAsync("TEST_REAL_LEASE_SECOND_DONE");
                ShutdownResult shutdown = await owner.ShutdownAsync("TEST_REAL_LEASE_OWNER_DONE");
                Assert.AreEqual(ShutdownOutcome.Completed, shutdown.Outcome);
            }
        }

        [Test]
        public async Task RealHost_CorruptSave_MapsCorruptCode_AndPreservesBytes()
        {
            string root = NewRealRoot();
            var created = NewRealComposition(root);
            await created.OpenLocalSessionAsync(CancellationToken.None);
            await WaitForReady(created);
            ShutdownResult createdShutdown = await created.ShutdownAsync("TEST_REAL_CREATE_SAVE");
            Assert.AreEqual(ShutdownOutcome.Completed, createdShutdown.Outcome);

            string saveDir = Path.Combine(root, "local");
            string[] saveFiles = Directory.GetFiles(
                saveDir,
                LocalTableChannelFactory.DefaultRoomId + ".save*");
            Assert.Greater(saveFiles.Length, 0, "The real host must have persisted a save envelope.");
            byte[] garbage = Encoding.UTF8.GetBytes("not-a-local-save-envelope");
            foreach (string file in saveFiles)
            {
                File.WriteAllBytes(file, garbage);
            }

            var reopened = NewRealComposition(root);
            try
            {
                var router = new AppRouter(reopened, new BackRouter());
                SetScene(router, SceneId.Lobby);
                LocalGameStartResult result = await router.StartLocalGameAsync();

                Assert.AreEqual(LocalGameStartStatus.Failed, result.Status);
                Assert.AreEqual(LocalGameStartResult.LocalHostSaveCorruptCode, result.StableErrorCode,
                    "A corrupt save must surface the stable corruption code.");
                Assert.AreNotEqual(LocalGameStartResult.LocalHostUnavailableCode, result.StableErrorCode,
                    "A corrupt save must never be disguised as a missing host.");
                Assert.IsFalse(reopened.IsTerminal);

                foreach (string file in saveFiles)
                {
                    Assert.IsTrue(File.Exists(file),
                        "A failed corrupt-save open must not delete the save file.");
                    CollectionAssert.AreEqual(garbage, File.ReadAllBytes(file),
                        "A failed corrupt-save open must not rewrite the save bytes.");
                }
            }
            finally
            {
                await reopened.ShutdownAsync("TEST_REAL_CORRUPT_DONE");
            }
        }

        [Test]
        public async Task RealHost_CleanCloseThenReopen_RecoversCommittedState()
        {
            string root = NewRealRoot();
            var first = NewRealComposition(root);
            ulong committed;
            try
            {
                await first.OpenLocalSessionAsync(CancellationToken.None);
                await WaitForReady(first);

                TableCommandIntent intent = await WaitForSubmittableAction(first);
                SessionCoordinator coordinator = first.SessionCoordinator!;
                ulong before = coordinator.Current.PlayerSnapshot!.StateVersion;
                CommandSubmission submission = await coordinator.SubmitAsync(
                    intent.Command,
                    CancellationToken.None);
                Assert.IsTrue(submission.Sent);

                SessionViewState confirmed = await WaitForSession(
                    first,
                    state => (state.PlayerSnapshot?.StateVersion ?? 0UL) > before,
                    30000,
                    "a committed player action before clean close");
                committed = confirmed.PlayerSnapshot!.StateVersion;
            }
            finally
            {
                ShutdownResult shutdown = await first.ShutdownAsync("TEST_REAL_RECOVERY_CLOSE");
                Assert.AreEqual(ShutdownOutcome.Completed, shutdown.Outcome);
                Assert.IsNull(first.SessionCoordinator);
            }

            var second = NewRealComposition(root);
            try
            {
                await second.OpenLocalSessionAsync(CancellationToken.None);
                SessionViewState recovered = await WaitForReady(second);
                Assert.GreaterOrEqual(recovered.PlayerSnapshot!.StateVersion, committed,
                    "A clean close followed by a fresh open must recover the committed state, not a new room.");
                Assert.AreEqual(LocalTableChannelFactory.DefaultRoomId, recovered.Binding!.RoomId);
            }
            finally
            {
                ShutdownResult shutdown = await second.ShutdownAsync("TEST_REAL_RECOVERY_REOPEN");
                Assert.AreEqual(ShutdownOutcome.Completed, shutdown.Outcome);
            }
        }
#endif
    }
}
