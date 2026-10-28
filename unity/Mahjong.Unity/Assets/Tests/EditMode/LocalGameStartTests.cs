#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Mahjong.Client;
using Mahjong.Unity.App;
using Mahjong.Unity.Platform;
using Mahjong.Unity.UI;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Mahjong.Unity.Tests.EditMode
{
    /// <summary>
    /// P4.2-B preparation tests: drive the local-game-start capability state machine
    /// (LobbyPresenter button states), the AppRouter stable error-code mapping and the
    /// composition-level open dedup / late-abandon invariants with scripted factories.
    /// These tests cover the P4.2-core default (unavailable factory) and explicitly
    /// injected scripted factories. They are UI/lifecycle evidence only — never
    /// evidence that a real pure .NET Mahjong.LocalHost is integrated in production.
    /// </summary>
    [TestFixture]
    public sealed class LocalGameStartTests
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

        private sealed class ScriptedChannelFactory : ITableChannelFactory
        {
            public bool Delay;
            public int Creates;
            public Exception? ThrowOnCreate;
            public readonly TaskCompletionSource<bool> Release =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly ScriptedTableChannel Channel = new ScriptedTableChannel();

            public async Task<ITableChannel> CreateAsync(TableTransportKind kind, CancellationToken cancellationToken)
            {
                Creates++;
                cancellationToken.ThrowIfCancellationRequested();
                if (ThrowOnCreate != null) throw ThrowOnCreate;
                if (Delay) await Release.Task;
                return Channel;
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

        private LobbyUiFixture BuildLobbyUi(IAppNavigation navigation)
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

            presenter.Initialize(navigation);

            // EditMode does not dispatch MonoBehaviour lifecycle messages, and Unity's
            // SendMessage path trips the editor's ShouldRunBehaviour assertion. Direct
            // reflection invocation calls the same private Start() the player would run,
            // exactly as the real lobby flow does after Initialize.
            typeof(LobbyPresenter)
                .GetMethod("Start", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(presenter, null);

            return new LobbyUiFixture
            {
                Presenter = presenter,
                StartButton = startButton,
                StartLabel = startLabel,
                StatusText = statusText,
                ExplanationText = expText
            };
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
        public async Task Router_DefaultComposition_MapsUnavailableFactoryToStableCode()
        {
            var comp = new AppComposition(
                new AppConfig(),
                new MainThreadDispatcher(),
                persistentDataPath: System.IO.Path.GetFullPath("build/p42b/store-" + Guid.NewGuid().ToString("N")));
            var router = new AppRouter(comp, new BackRouter());

            Assert.IsFalse(router.IsLocalGameStartAvailable,
                "Default P4.2-core production composition must report local start unavailable.");

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
#endif
    }
}
