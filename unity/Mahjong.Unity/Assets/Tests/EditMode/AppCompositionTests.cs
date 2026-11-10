#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Unity.App;
using Mahjong.Unity.Platform;
using Mahjong.Unity.UI;
using Mahjong.Unity.UI.Session;
using TMPro;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.UI;
using UnityEngine.TestTools;
#endif

namespace Mahjong.Unity.Tests.EditMode
{
    [TestFixture]
    public sealed class AppCompositionTests
    {
        [Test]
        public void RecordReplayList_RendersEmptyOneAndTwentyRowsAndLoadFailure()
        {
            Scene menuScene = EditorSceneManager.OpenScene("Assets/Scenes/Menu.unity", OpenSceneMode.Additive);
            var root = new GameObject("RecordReplayListTest");
            try
            {
                LocalRecordReplayPresenter presenter = root.AddComponent<LocalRecordReplayPresenter>();
                presenter.Initialize();
                Canvas canvas = root.GetComponentInChildren<Canvas>(true);
                canvas.gameObject.SetActive(true);

                Type roundType = Type.GetType("Mahjong.LocalHost.LocalRecordedRound, Mahjong.LocalHost", true)!;
                ConstructorInfo roundConstructor = roundType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
                Type eventType = roundType.GetProperty("Events")!.PropertyType.GetGenericArguments()[0];
                Array events = Array.CreateInstance(eventType, 0);
                MethodInfo showRecords = typeof(LocalRecordReplayPresenter).GetMethod("ShowRecords", BindingFlags.Instance | BindingFlags.NonPublic)!;
                foreach (int count in new[] { 0, 1, 20 })
                {
                    Array records = Array.CreateInstance(roundType, count);
                    for (int i = 0; i < count; i++)
                    {
                        object record = roundConstructor.Invoke(new object[]
                        {
                            "record-" + i, "local-main", "round-" + i, DateTimeOffset.UtcNow, "进行中", events
                        });
                        records.SetValue(record, i);
                    }
                    showRecords.Invoke(presenter, new object[] { records });
                    Assert.AreEqual(count, ((System.Collections.ICollection)GetPrivate(presenter, "_rows")!).Count);
                    var status = (TextMeshProUGUI)GetPrivate(presenter, "_status")!;
                    if (count == 0) Assert.AreEqual("暂无牌局记录", status.text);
                    else StringAssert.Contains(count.ToString(), status.text);
                }

                LogAssert.Expect(LogType.Warning, new Regex("LOCAL_RECORD_LIST_FAILED: InvalidOperationException: synthetic read failure"));
                InvokePrivate(presenter, "ShowLoadFailure", new InvalidOperationException("synthetic read failure"));
                StringAssert.Contains("读取失败", ((TextMeshProUGUI)GetPrivate(presenter, "_status")!).text);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                if (menuScene.IsValid() && menuScene.isLoaded) EditorSceneManager.CloseScene(menuScene, true);
            }
        }

        [Test]
        public void MenuAndRecordReplayControls_StayVisibleAndUsePointerLifecycle()
        {
            UiInputLifecycle.ResetForTest();
            var eventSystemObject = new GameObject("RecordReplayTestEventSystem", typeof(EventSystem));
            EventSystem testEventSystem = eventSystemObject.GetComponent<EventSystem>();
            var menuRoot = new GameObject("MenuLayoutTest");
            var replayRoot = new GameObject("ReplayPresenterTest");
            Scene menuScene = default;
            try
            {
                menuScene = EditorSceneManager.OpenScene("Assets/Scenes/Menu.unity", OpenSceneMode.Additive);
                MenuPresenter sceneMenu = menuScene.GetRootGameObjects()
                    .SelectMany(x => x.GetComponentsInChildren<MenuPresenter>(true)).Single();
                InvokePrivate(sceneMenu, "Start");
                Button[] sceneMenuButtons = new[]
                {
                    (Button)GetPrivate(sceneMenu, "_playButton")!,
                    (Button)GetPrivate(sceneMenu, "_recordsButton")!,
                    (Button)GetPrivate(sceneMenu, "_quitButton")!
                };
                AssertMenuButtonsDoNotOverlap(sceneMenuButtons);
                Assert.That(sceneMenuButtons.All(x => x.GetComponent<UiPointerLifecycleTrigger>() != null));
                EditorSceneManager.CloseScene(menuScene, true);
                menuScene = default;
                testEventSystem.enabled = false;
                testEventSystem.enabled = true;
                InvokePrivate(testEventSystem, "OnEnable");
                EventSystem.current = testEventSystem;

                Button play = CreateLayoutButton(menuRoot.transform, "PlayButton", 30);
                Button quit = CreateLayoutButton(menuRoot.transform, "QuitButton", -80);
                MenuPresenter menu = menuRoot.AddComponent<MenuPresenter>();
                SetPrivate(menu, "_playButton", play);
                SetPrivate(menu, "_quitButton", quit);
                menu.Initialize(null!);
                InvokePrivate(menu, "Start");

                Button[] menuButtons = menuRoot.GetComponentsInChildren<Button>(true);
                Assert.AreEqual(3, menuButtons.Length);
                Assert.Less(Math.Abs(play.GetComponent<RectTransform>().anchoredPosition.y -
                    menuButtons.Single(x => x.name == "MatchRecordsButton").GetComponent<RectTransform>().anchoredPosition.y), 200);
                Assert.That(menuButtons.All(x => x.GetComponent<UiPointerLifecycleTrigger>() != null));
                AssertMenuButtonsDoNotOverlap(menuButtons);

                LocalRecordReplayPresenter replay = replayRoot.AddComponent<LocalRecordReplayPresenter>();
                replay.Initialize();
                Canvas replayCanvas = replayRoot.GetComponentInChildren<Canvas>(true);
                replayCanvas.gameObject.SetActive(true);
                RectTransform panel = replayCanvas.transform.Find("Panel") as RectTransform;
                Assert.IsNotNull(panel);
                Button[] controls = panel!.GetComponentsInChildren<Button>(true);
                Assert.AreEqual(4, controls.Length);
                Assert.That(controls.All(x => x.GetComponent<UiPointerLifecycleTrigger>() != null));
                AssertControlButtonsFit(panel, controls);

                Button playPause = controls.Single(x => x.GetComponent<RectTransform>().anchoredPosition.x == -330);
                FireRealPointerClick(playPause);
                Assert.IsTrue((bool)GetPrivate(replay, "_playing")!);
                FireRealPointerClick(playPause);
                Assert.IsFalse((bool)GetPrivate(replay, "_playing")!);

                FieldInfo eventsField = typeof(LocalRecordReplayPresenter).GetField("_events", BindingFlags.Instance | BindingFlags.NonPublic)!;
                Type eventType = eventsField.FieldType.GetGenericArguments()[0];
                ConstructorInfo eventConstructor = eventType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
                Type roundEventType = Type.GetType("Mahjong.Domain.RoundEventType, Mahjong.Domain", throwOnError: true)!;
                object sampleEvent = eventConstructor.Invoke(new object[] { (ulong)1, Enum.Parse(roundEventType, "TileDiscarded"), 0, (int?)5 });
                Array eventArray = Array.CreateInstance(eventType, 1);
                eventArray.SetValue(sampleEvent, 0);
                eventsField.SetValue(replay, eventArray);
                Button step = controls.Single(x => x.GetComponent<RectTransform>().anchoredPosition.x == -110);
                FireRealPointerClick(step);
                Assert.AreEqual(1, (int)GetPrivate(replay, "_cursor")!);

                Button speed = controls.Single(x => x.GetComponent<RectTransform>().anchoredPosition.x == 110);
                FireRealPointerClick(speed);
                FireRealPointerClick(speed);
                Assert.AreEqual(0.8f / 3f, (float)GetPrivate(replay, "_interval")!, 0.0001f);

                var priorSelection = CreateLayoutButton(replayRoot.transform, "PriorSelection", 0);
                EventSystem.current = testEventSystem;
                EventSystem.current!.SetSelectedGameObject(priorSelection.gameObject);
                var backRouter = new BackRouter();
                SetPrivate(replay, "_backRouter", backRouter);
                replayCanvas.gameObject.SetActive(false);
                replay.Open();
                Assert.IsTrue(replay.IsOpen);
                Assert.AreEqual(BackRouteResult.Consumed, backRouter.HandleBack(BackEventSource.DesktopEscape));
                Assert.IsFalse(replay.IsOpen);
                Assert.AreSame(priorSelection.gameObject, EventSystem.current.currentSelectedGameObject);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(menuRoot);
                UnityEngine.Object.DestroyImmediate(replayRoot);
                UnityEngine.Object.DestroyImmediate(eventSystemObject);
                if (menuScene.IsValid() && menuScene.isLoaded) EditorSceneManager.CloseScene(menuScene, true);
            }
        }

        private static Button CreateLayoutButton(Transform parent, string name, float y)
        {
            var gameObject = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button), typeof(UiPointerLifecycleTrigger));
            gameObject.transform.SetParent(parent, false);
            var rect = gameObject.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(400, 88);
            rect.anchoredPosition = new Vector2(0, y);
            return gameObject.GetComponent<Button>();
        }

        private static void AssertMenuButtonsDoNotOverlap(Button[] buttons)
        {
            foreach (Button button in buttons)
            {
                RectTransform rect = button.GetComponent<RectTransform>();
                Assert.That(rect.anchoredPosition.y + rect.rect.height / 2f, Is.LessThan(540));
                Assert.That(rect.anchoredPosition.y - rect.rect.height / 2f, Is.GreaterThan(-540));
            }
            float[] centers = buttons.Select(x => x.GetComponent<RectTransform>().anchoredPosition.y).OrderByDescending(x => x).ToArray();
            for (int i = 1; i < centers.Length; i++) Assert.That(centers[i - 1] - centers[i], Is.GreaterThanOrEqualTo(88));
        }

        private static void AssertControlButtonsFit(RectTransform panel, Button[] buttons)
        {
            var rects = buttons.Select(x => x.GetComponent<RectTransform>()).OrderBy(x => x.anchoredPosition.x).ToArray();
            foreach (RectTransform rect in rects)
            {
                Assert.That(Math.Abs(rect.anchoredPosition.x) + rect.rect.width / 2f, Is.LessThanOrEqualTo(panel.rect.width / 2f));
                Assert.That(Math.Abs(rect.anchoredPosition.y) + rect.rect.height / 2f, Is.LessThanOrEqualTo(panel.rect.height / 2f));
            }
            for (int i = 1; i < rects.Length; i++)
                Assert.That(rects[i - 1].anchoredPosition.x + rects[i - 1].rect.width / 2f,
                    Is.LessThanOrEqualTo(rects[i].anchoredPosition.x - rects[i].rect.width / 2f));
        }

        private static void FireRealPointerClick(Button button)
        {
            var data = new PointerEventData(EventSystem.current)
            {
                button = PointerEventData.InputButton.Left,
                pointerId = -1,
                position = new Vector2(100, 100),
                pressPosition = new Vector2(100, 100),
                pointerPress = button.gameObject,
                pointerClick = button.gameObject
            };
            ExecuteEvents.Execute(button.gameObject, data, ExecuteEvents.pointerDownHandler);
            Assert.IsTrue(UiInputLifecycle.Instance.IsArmed);
            ExecuteEvents.Execute(button.gameObject, data, ExecuteEvents.pointerUpHandler);
            Assert.IsFalse(UiInputLifecycle.Instance.IsArmed);
            ExecuteEvents.Execute(button.gameObject, data, ExecuteEvents.pointerClickHandler);
        }

        private static void SetPrivate(object target, string name, object value) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

        private static object? GetPrivate(object target, string name) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);

        private static void InvokePrivate(object target, string name, params object[] arguments) =>
            target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);

        [Test]
        public void AppComposition_Initializes_WithDefaults()
        {
            var dispatcher = new MainThreadDispatcher(128);
            var config = new AppConfig();
            var comp = new AppComposition(config, dispatcher);

            Assert.IsNotNull(comp.Paths);
            Assert.IsNotNull(comp.ThemeProvider);
            Assert.IsNotNull(comp.ChannelFactory);
            Assert.AreEqual(60, comp.Config.targetFrameRate);
            Assert.IsNull(comp.SessionCoordinator);
        }

        [Test]
        public void UnavailableLocalTableChannelFactory_ThrowsLocalTableUnavailable()
        {
            var factory = new UnavailableLocalTableChannelFactory();
            var ex = Assert.ThrowsAsync<LocalTableUnavailableException>(async () =>
            {
                await factory.CreateAsync(TableTransportKind.Local, CancellationToken.None);
            });

            Assert.AreEqual(LocalTableUnavailableException.ErrorCode, LocalTableUnavailableException.ErrorCode);
            StringAssert.Contains("LOCAL_HOST_UNAVAILABLE", LocalTableUnavailableException.ErrorCode);
        }

        [Test]
        public void UnavailableLocalTableChannelFactory_PreCancelled_ThrowsOperationCanceled()
        {
            var factory = new UnavailableLocalTableChannelFactory();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.ThrowsAsync<TaskCanceledException>(async () =>
            {
                await factory.CreateAsync(TableTransportKind.Local, cts.Token);
            });
        }

        [Test]
        public void UnavailableLocalTableChannelFactory_Wss_ThrowsNotSupported()
        {
            var factory = new UnavailableLocalTableChannelFactory();
            Assert.ThrowsAsync<NotSupportedException>(async () =>
            {
                await factory.CreateAsync(TableTransportKind.Wss, CancellationToken.None);
            });
        }

        [Test]
        public async Task AppComposition_ShutdownAsync_WithoutSession_ReturnsCompleted()
        {
            var dispatcher = new MainThreadDispatcher(128);
            var config = new AppConfig();
            var comp = new AppComposition(config, dispatcher);

            ShutdownResult result = await comp.ShutdownAsync("TEST_QUIT");
            Assert.AreEqual(ShutdownOutcome.Completed, result.Outcome);
            Assert.IsTrue(result.MenuAllowed);
            Assert.IsFalse(result.Quarantined);
        }

        private sealed class TestChannel : ITableChannel, IDisposable
        {
            public bool CloseEntered, Disposed;
            public bool DelayClose;
            public readonly TaskCompletionSource<bool> CloseRelease = new TaskCompletionSource<bool>();
            public bool IsOpen => true;
            public Task OpenAsync(TableChannelConnectRequest r, CancellationToken t) => Task.CompletedTask;
            public Task SendAsync(ReadOnlyMemory<byte> b, CancellationToken t) => Task.CompletedTask;
            public Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken t)
            {
                var pending = new TaskCompletionSource<ReadOnlyMemory<byte>>();
                t.Register(() => pending.TrySetCanceled());
                return pending.Task;
            }
            public async Task CloseAsync(TableChannelCloseReason r, CancellationToken t)
            {
                CloseEntered = true;
                if (DelayClose) await CloseRelease.Task;
            }
            public void Dispose() { Disposed = true; }
        }

        private sealed class TestChannelFactory : ITableChannelFactory
        {
            public bool Delay;
            public int Creates;
            public readonly TestChannel Channel = new TestChannel();
            public readonly TaskCompletionSource<bool> Release = new TaskCompletionSource<bool>();
            public async Task<ITableChannel> CreateAsync(TableTransportKind k, CancellationToken t)
            {
                Creates++;
                if (Delay) await Release.Task;
                return Channel;
            }
        }

        [Test]
        public async Task RepeatedShutdownMustPreserveInflightQuarantine()
        {
            var f = new TestChannelFactory { Delay = true };
            var comp = new AppComposition(
                new AppConfig { pumpBarrierTimeoutMs = 50, closeTimeoutMs = 50, cancelObservationMs = 10 },
                new MainThreadDispatcher(), channelFactory: f,
                persistentDataPath: System.IO.Path.GetFullPath("build/p42/store-" + Guid.NewGuid().ToString("N")));
            var open = comp.OpenLocalSessionAsync(CancellationToken.None);
            var first = await comp.ShutdownAsync("RETURN_TO_MENU");
            Assert.IsTrue(first.Quarantined);
            f.Release.TrySetResult(true);
            try { await open; } catch { }
            var again = await comp.ShutdownAsync("RETURN_TO_MENU");
            Assert.IsTrue(again.Quarantined && !again.MenuAllowed,
                "Composition-level quarantine must remain a terminal failure on repeated shutdown, including after late factory cleanup.");
        }

        [Test]
        public async Task OpenDuringCloseMustNotReturnClosingCoordinator()
        {
            var f = new TestChannelFactory(); f.Channel.DelayClose = true;
            var comp = new AppComposition(
                new AppConfig { pumpBarrierTimeoutMs = 2000, closeTimeoutMs = 2000, cancelObservationMs = 10 },
                new MainThreadDispatcher(), channelFactory: f,
                persistentDataPath: System.IO.Path.GetFullPath("build/p42/store-" + Guid.NewGuid().ToString("N")));
            var first = await comp.OpenLocalSessionAsync(CancellationToken.None);
            Assert.IsNotNull(first);
            var stop = comp.ShutdownAsync("RETURN_TO_MENU");
            int attempts = 0;
            while (!f.Channel.CloseEntered && attempts++ < 100) await Task.Delay(10);
            Assert.IsTrue(f.Channel.CloseEntered);
            var secondTask = comp.OpenLocalSessionAsync(CancellationToken.None);
            bool returnedClosing = secondTask.IsCompleted && !secondTask.IsFaulted && !secondTask.IsCanceled && ReferenceEquals(first, secondTask.Result);
            f.Channel.CloseRelease.TrySetResult(true);
            await stop;
            Assert.IsFalse(returnedClosing, "Open while shutdown is in progress must reject or wait; it must not report a closing/disposed owner as a live session.");
        }

        [Test]
        public async Task ConcurrentCompositionShutdownMustShareTaskAndResult()
        {
            var f = new TestChannelFactory(); f.Channel.DelayClose = true;
            var comp = new AppComposition(
                new AppConfig { pumpBarrierTimeoutMs = 2000, closeTimeoutMs = 2000, cancelObservationMs = 10 },
                new MainThreadDispatcher(), channelFactory: f,
                persistentDataPath: System.IO.Path.GetFullPath("build/p42/store-" + Guid.NewGuid().ToString("N")));
            var open = await comp.OpenLocalSessionAsync(CancellationToken.None);
            Assert.IsNotNull(open);
            var first = comp.ShutdownAsync("RETURN_TO_MENU");
            var second = comp.ShutdownAsync("RETURN_TO_MENU");
            bool same = ReferenceEquals(first, second);
            f.Channel.CloseRelease.TrySetResult(true);
            var res1 = await first;
            var res2 = await second;
            Assert.IsTrue(same, "The composition owner must retain and return the same in-progress shutdown task rather than running two completion/reset paths.");
            Assert.AreEqual(res1.Outcome, res2.Outcome);
        }

        private SessionViewState View(long generation) => new SessionViewState(generation, TableConnectionState.Ready, null, new PlayerSnapshot(), null, null, null, null);
        private SessionCoordinator Coordinator(FakeSession session, MainThreadDispatcher dispatcher, TableSessionOperationQueue? queue = null) =>
            new SessionCoordinator(session, queue, null, dispatcher, new AppConfig { closeTimeoutMs = 50, pumpBarrierTimeoutMs = 50, cancelObservationMs = 10 }, 1);

        [Test]
        public async Task CloseFaultMustNotReportCompleted()
        {
            var session = new FakeSession { Close = _ => Task.FromException(new InvalidOperationException("CLOSE_FAILED")) };
            var owner = Coordinator(session, new MainThreadDispatcher());
            var result = await owner.ShutdownAsync();
            Assert.AreNotEqual(ShutdownOutcome.Completed, result.Outcome, "Faulted Close must not allow menu/dispose.");
            Assert.IsFalse(result.MenuAllowed);
            Assert.IsTrue(result.Quarantined);
        }

        [Test]
        public async Task ShutdownMustDetachSessionEvent()
        {
            var session = new FakeSession();
            var owner = Coordinator(session, new MainThreadDispatcher());
            Assert.AreEqual(1, session.Subscribers);
            await owner.ShutdownAsync();
            Assert.AreEqual(0, session.Subscribers, "Shutdown leaves SnapshotChanged owner retained.");
        }

        [Test]
        public async Task QueuedPublishMustNotDeliverAfterShutdown()
        {
            var dispatcher = new MainThreadDispatcher { CurrentGeneration = 1 };
            var owner = Coordinator(new FakeSession(), dispatcher);
            int delivered = 0;
            owner.Changed += (_, __) => delivered++;
            owner.Publish(1, View(1));
            await owner.ShutdownAsync();
            dispatcher.Update(128, 1000);
            Assert.AreEqual(0, delivered, "Queued Changed executes after terminal shutdown.");
        }

        [Test]
        public void FreshCoordinatorMustPublishToDefaultDispatcher()
        {
            var dispatcher = new MainThreadDispatcher();
            var owner = Coordinator(new FakeSession(), dispatcher);
            int delivered = 0;
            owner.Changed += (_, __) => delivered++;
            owner.Publish(1, View(1));
            dispatcher.Update(128, 1000);
            Assert.AreEqual(1, delivered, "Composition/owner never updates dispatcher generation (default 0 vs session 1).");
        }

        [Test]
        public async Task SnapshotEventOnOwnerMustNotDeadlockCapture()
        {
            var session = new FakeSession { RaiseOnOpen = true };
            var queue = new TableSessionOperationQueue(session);
            var owner = Coordinator(session, new MainThreadDispatcher(), queue);
            var open = Task.Run(() => queue.OpenAsync(new TableSessionOpenRequest("review", "review"), CancellationToken.None));
            var winner = await Task.WhenAny(open, Task.Delay(500));
            Assert.AreSame(open, winner, "SnapshotChanged blocks on CaptureAsync.Result queued behind its own running Open.");
        }

        [Test]
        public void FocusGainedMustNotOverridePauseGate()
        {
            UiInputLifecycle.ResetForTest();
            var input = UiInputLifecycle.Instance;
            input.OnPause();
            input.OnFocusGained();
            Assert.IsFalse(input.TryArm(1, UnityEngine.Vector2.zero), "Still paused; focus regained must not re-enable input.");
        }

        [Test]
        public void FontMaterialMustUseSdfShader()
        {
#if UNITY_EDITOR
            var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/Art/UI/Fonts/NotoSansSC-Regular SDF.asset");
            Assert.IsNotNull(font);
            Assert.That(font.material.shader.name, Does.StartWith("TextMeshPro/"), "UI/Default is incompatible with TMP SDF atlas.");
#endif
        }

        [Test]
        public void Dispatcher_Overflow_TriggersFailureAndQuarantine()
        {
            UnityEngine.TestTools.LogAssert.Expect(LogType.Error, "UI_DISPATCH_OVERFLOW: Dispatcher exceeded capacity 4.");
            var dispatcher = new MainThreadDispatcher(4);
            var session = new FakeSession();
            var owner = Coordinator(session, dispatcher);

            Assert.IsFalse(owner.IsQuarantined);
            for (int i = 0; i < 5; i++)
            {
                dispatcher.Post(() => { });
            }

            Assert.IsTrue(dispatcher.HasOverflowed);
            Assert.IsTrue(owner.IsQuarantined);
            Assert.AreEqual("UI_DISPATCH_OVERFLOW", owner.Current.LastError);
        }

        private sealed class FakeSession : IGameSession
        {
            public int Subscribers;
            private EventHandler<TableSnapshotChangedEventArgs>? _changed;
            public bool RaiseOnOpen;
            public Func<CancellationToken, Task> Close = _ => Task.CompletedTask;
            public TableConnectionState State => TableConnectionState.Ready;
            public Func<CancellationToken, bool>? BaselineAdmissionCheck { get; set; }
            public TableSessionBinding? Binding => null;
            public Mahjong.Contracts.StreamCursor? ConfirmedCursor => null;
            public PlayerSnapshot? PlayerSnapshot => null;
            public PublicSnapshot? PublicSnapshot => null;
            public RoomUpdate? RoomSnapshot => null;
            public IReadOnlyList<PendingCommandSnapshot> PendingCommands => Array.Empty<PendingCommandSnapshot>();
            public int EarlyBufferItemCount => 0;
            public long EarlyBufferByteCount => 0;
            public long EarlyBufferOverflowCount => 0;
            public string? LastError => null;

            public event EventHandler<TableSnapshotChangedEventArgs>? SnapshotChanged
            {
                add { _changed += value; Subscribers++; }
                remove { _changed -= value; Subscribers--; }
            }
            public event EventHandler<TableReceiptResolvedEventArgs>? ReceiptResolved;

            public Task OpenAsync(TableSessionOpenRequest r, CancellationToken ct)
            {
                if (RaiseOnOpen) _changed?.Invoke(this, new TableSnapshotChangedEventArgs(default, true, new PlayerSnapshot(), null, null));
                return Task.CompletedTask;
            }
            public Task PumpOnceAsync(CancellationToken ct) => Task.CompletedTask;
            public Task DisconnectAsync(CancellationToken ct) => Task.CompletedTask;
            public Task<CommandSubmission> SubmitAsync(ProtocolCommand c, CancellationToken ct) => throw new NotSupportedException();
            public Task ReconcilePendingAsync(CancellationToken ct) => Task.CompletedTask;
            public Task<CommandSubmission> RetryPendingAsync(IdempotencyKey k, CancellationToken ct) => throw new NotSupportedException();
            public Task CloseAsync(string r, CancellationToken ct) => Close(ct);
        }

#if UNITY_EDITOR
        public static void BuildAllAssets()
        {
            Debug.Log("[ShellAssetBuilder] Starting asset and scene generation...");

            // 1. Create SDF Font Asset
            var font = AssetDatabase.LoadAssetAtPath<Font>("Assets/Art/UI/Fonts/NotoSansSC-Regular.ttf");
            if (font == null)
            {
                Debug.LogError("[ShellAssetBuilder] Could not load NotoSansSC-Regular.ttf");
                return;
            }

            var fontAsset = CreateFontAsset(font);
            if (fontAsset != null)
            {
                AssetDatabase.CreateAsset(fontAsset, "Assets/Art/UI/Fonts/NotoSansSC-Regular SDF.asset");
                var fontAssetType = fontAsset.GetType();
                var texs = fontAssetType.GetProperty("atlasTextures")?.GetValue(fontAsset) as Texture2D[];
                if (texs != null)
                {
                    foreach (var t in texs)
                    {
                        if (t != null)
                        {
                            AssetDatabase.AddObjectToAsset(t, fontAsset);
                        }
                    }
                }
                var mat = fontAssetType.GetProperty("material")?.GetValue(fontAsset) as Material;
                if (mat != null)
                {
                    AssetDatabase.AddObjectToAsset(mat, fontAsset);
                }
            }

            // 2. Create UiFontCatalog.asset
            var catalog = ScriptableObject.CreateInstance<UiFontCatalog>();
            var soCat = new SerializedObject(catalog);
            soCat.FindProperty("_primaryFont").objectReferenceValue = fontAsset;
            soCat.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.CreateAsset(catalog, "Assets/Art/UI/Fonts/UiFontCatalog.asset");

            // 3. Create UiTheme.asset
            var theme = ScriptableObject.CreateInstance<UiTheme>();
            var soTheme = new SerializedObject(theme);
            soTheme.FindProperty("fontAsset").objectReferenceValue = fontAsset;
            soTheme.FindProperty("buttonPrimarySprite").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Art/UI/MainMenu/button-primary.png");
            soTheme.FindProperty("buttonSecondarySprite").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Art/UI/MainMenu/button-secondary.png");
            soTheme.FindProperty("buttonFocusSprite").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Art/UI/MainMenu/button-focus.png");
            soTheme.FindProperty("backgroundSprite").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Art/UI/MainMenu/club-background.png");
            soTheme.FindProperty("menuIconsSprite").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Art/UI/MainMenu/menu-icons.png");
            soTheme.FindProperty("heroSprite").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Art/UI/MainMenu/octopus-hero.png");
            soTheme.FindProperty("buttonFontSize").floatValue = 32f;
            soTheme.FindProperty("bodyFontSize").floatValue = 28f;
            soTheme.FindProperty("titleFontSize").floatValue = 56f;
            soTheme.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.CreateAsset(theme, "Assets/Art/UI/UiTheme.asset");

            // 4. Build Scenes
            BuildBootScene(theme, fontAsset);
            BuildMenuScene(theme, fontAsset);
            BuildLobbyScene(theme, fontAsset);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[ShellAssetBuilder] All assets and scenes generated successfully.");
        }

        private static UnityEngine.Object? CreateFontAsset(Font font)
        {
            var type = Type.GetType("TMPro.TMP_FontAsset, Unity.TextMeshPro");
            if (type == null) return null;

            var sdfShader = Shader.Find("TextMeshPro/Mobile/Distance Field");
            var shaderUtilsType = Type.GetType("TMPro.ShaderUtilities, Unity.TextMeshPro");
            if (shaderUtilsType != null && sdfShader != null)
            {
                var field = shaderUtilsType.GetField("k_ShaderRef_MobileSDF", BindingFlags.Static | BindingFlags.NonPublic);
                if (field != null)
                {
                    field.SetValue(null, sdfShader);
                }
            }

            var method = type.GetMethod("CreateFontAsset", new[] {
                typeof(Font), typeof(int), typeof(int), typeof(GlyphRenderMode), typeof(int), typeof(int), typeof(AtlasPopulationMode), typeof(bool)
            });

            UnityEngine.Object? fontAsset = null;
            if (method != null)
            {
                fontAsset = method.Invoke(null, new object[] { font, 36, 4, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, true }) as UnityEngine.Object;
            }
            else
            {
                var simpleMethod = type.GetMethod("CreateFontAsset", new[] { typeof(Font) });
                fontAsset = simpleMethod?.Invoke(null, new object[] { font }) as UnityEngine.Object;
            }

            if (fontAsset != null && sdfShader != null)
            {
                var matProp = type.GetProperty("material");
                var mat = matProp?.GetValue(fontAsset) as Material;
                if (mat != null)
                {
                    mat.shader = sdfShader;
                }
            }
            return fontAsset;
        }

        private static Component CreateTmpText(GameObject go, string text, UnityEngine.Object? fontAsset, float fontSize, Color color)
        {
            var tmpType = Type.GetType("TMPro.TextMeshProUGUI, Unity.TextMeshPro");
            if (tmpType == null)
            {
                var legacy = go.AddComponent<Text>();
                legacy.text = text;
                legacy.fontSize = (int)fontSize;
                legacy.color = color;
                return legacy;
            }

            var comp = go.AddComponent(tmpType);
            var so = new SerializedObject(comp);
            var textProp = so.FindProperty("m_text");
            if (textProp != null) textProp.stringValue = text;
            var sizeProp = so.FindProperty("m_fontSize");
            if (sizeProp != null) sizeProp.floatValue = fontSize;
            var colorProp = so.FindProperty("m_fontColor");
            if (colorProp != null) colorProp.colorValue = color;
            if (fontAsset != null)
            {
                var fontProp = so.FindProperty("m_fontAsset");
                if (fontProp != null) fontProp.objectReferenceValue = fontAsset;
            }
            var alignProp = so.FindProperty("m_textAlignment");
            if (alignProp != null) alignProp.intValue = 514; // Center
            so.ApplyModifiedPropertiesWithoutUndo();
            return comp;
        }

        private static void BuildBootScene(UiTheme theme, UnityEngine.Object? fontAsset)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camGo = new GameObject("Main Camera");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = theme.backgroundColor;
            cam.orthographic = true;
            cam.orthographicSize = 5f;
            camGo.tag = "MainCamera";

            var bootGo = new GameObject("GameBootstrapper");
            var bootstrapper = bootGo.AddComponent<GameBootstrapper>();
            var so = new SerializedObject(bootstrapper);
            so.FindProperty("_theme").objectReferenceValue = theme;
            so.ApplyModifiedPropertiesWithoutUndo();

            var eventSystemGo = new GameObject("EventSystem", typeof(UnityEngine.EventSystems.EventSystem));
            var module = eventSystemGo.AddComponent<InputSystemUIInputModule>();
            module.actionsAsset = AssetDatabase.LoadAssetAtPath<InputActionAsset>("Assets/Settings/InputSystem_Actions.inputactions");

            EditorSceneManager.SaveScene(scene, "Assets/Scenes/Boot.unity");
        }

        private static void BuildMenuScene(UiTheme theme, UnityEngine.Object? fontAsset)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camGo = new GameObject("Main Camera");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = theme.backgroundColor;
            cam.orthographic = true;
            cam.orthographicSize = 5f;
            camGo.tag = "MainCamera";

            var guardGo = new GameObject("SceneGuard");
            guardGo.AddComponent<SceneGuard>();

            var canvasGo = new GameObject("Canvas");
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            canvasGo.AddComponent<GraphicRaycaster>();

            var bgGo = new GameObject("Background");
            bgGo.transform.SetParent(canvasGo.transform, false);
            var bgImg = bgGo.AddComponent<Image>();
            bgImg.sprite = theme.backgroundSprite;
            bgImg.color = theme.backgroundColor;
            var bgRect = bgGo.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.sizeDelta = Vector2.zero;

            var safeGo = new GameObject("SafeAreaRoot");
            safeGo.transform.SetParent(canvasGo.transform, false);
            var safeRect = safeGo.AddComponent<RectTransform>();
            var fitter = safeGo.AddComponent<SafeAreaFitter>();

            var designGo = new GameObject("DesignRoot");
            designGo.transform.SetParent(safeGo.transform, false);
            var designRect = designGo.AddComponent<RectTransform>();
            designRect.sizeDelta = new Vector2(1280, 720);
            designRect.anchorMin = new Vector2(0.5f, 0.5f);
            designRect.anchorMax = new Vector2(0.5f, 0.5f);
            designRect.pivot = new Vector2(0.5f, 0.5f);

            var soFitter = new SerializedObject(fitter);
            soFitter.FindProperty("_canvasScaler").objectReferenceValue = scaler;
            soFitter.FindProperty("_designRoot").objectReferenceValue = designRect;
            soFitter.ApplyModifiedPropertiesWithoutUndo();

            var titleGo = new GameObject("TitleText");
            titleGo.transform.SetParent(designGo.transform, false);
            var titleTmp = CreateTmpText(titleGo, "千术麻将 (Mahjong Cheater)", fontAsset, theme.titleFontSize, theme.textColor);
            var titleRect = titleGo.GetComponent<RectTransform>();
            titleRect.anchoredPosition = new Vector2(0, 180);
            titleRect.sizeDelta = new Vector2(900, 80);

            var playBtnGo = CreateButton("PlayButton", designGo.transform, new Vector2(0, 30), new Vector2(400, 88), "进入大厅", theme, fontAsset);
            var quitBtnGo = CreateButton("QuitButton", designGo.transform, new Vector2(0, -80), new Vector2(400, 88), "退出游戏", theme, fontAsset);

            var modalGo = CreateModalDialog("QuitModal", designGo.transform, theme, fontAsset);

            var presenterGo = new GameObject("MenuPresenter");
            presenterGo.transform.SetParent(designGo.transform, false);
            var presenter = presenterGo.AddComponent<MenuPresenter>();
            var soP = new SerializedObject(presenter);
            soP.FindProperty("_playButton").objectReferenceValue = playBtnGo.GetComponent<Button>();
            soP.FindProperty("_quitButton").objectReferenceValue = quitBtnGo.GetComponent<Button>();
            soP.FindProperty("_titleText").objectReferenceValue = titleTmp;
            soP.FindProperty("_quitModal").objectReferenceValue = modalGo.GetComponent<ModalPresenter>();
            soP.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.SaveScene(scene, "Assets/Scenes/Menu.unity");
        }

        private static void BuildLobbyScene(UiTheme theme, UnityEngine.Object? fontAsset)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camGo = new GameObject("Main Camera");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = theme.backgroundColor;
            cam.orthographic = true;
            cam.orthographicSize = 5f;
            camGo.tag = "MainCamera";

            var guardGo = new GameObject("SceneGuard");
            guardGo.AddComponent<SceneGuard>();

            var canvasGo = new GameObject("Canvas");
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            canvasGo.AddComponent<GraphicRaycaster>();

            var bgGo = new GameObject("Background");
            bgGo.transform.SetParent(canvasGo.transform, false);
            var bgImg = bgGo.AddComponent<Image>();
            bgImg.sprite = theme.backgroundSprite;
            bgImg.color = theme.backgroundColor;
            var bgRect = bgGo.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.sizeDelta = Vector2.zero;

            var safeGo = new GameObject("SafeAreaRoot");
            safeGo.transform.SetParent(canvasGo.transform, false);
            var safeRect = safeGo.AddComponent<RectTransform>();
            var fitter = safeGo.AddComponent<SafeAreaFitter>();

            var designGo = new GameObject("DesignRoot");
            designGo.transform.SetParent(safeGo.transform, false);
            var designRect = designGo.AddComponent<RectTransform>();
            designRect.sizeDelta = new Vector2(1280, 720);
            designRect.anchorMin = new Vector2(0.5f, 0.5f);
            designRect.anchorMax = new Vector2(0.5f, 0.5f);
            designRect.pivot = new Vector2(0.5f, 0.5f);

            var soFitter = new SerializedObject(fitter);
            soFitter.FindProperty("_canvasScaler").objectReferenceValue = scaler;
            soFitter.FindProperty("_designRoot").objectReferenceValue = designRect;
            soFitter.ApplyModifiedPropertiesWithoutUndo();

            var titleGo = new GameObject("TitleText");
            titleGo.transform.SetParent(designGo.transform, false);
            var titleTmp = CreateTmpText(titleGo, "游戏大厅 (Lobby)", fontAsset, theme.titleFontSize, theme.textColor);
            var titleRect = titleGo.GetComponent<RectTransform>();
            titleRect.anchoredPosition = new Vector2(0, 220);
            titleRect.sizeDelta = new Vector2(900, 80);

            // Banner
            var bannerGo = new GameObject("ConnectionBanner");
            bannerGo.transform.SetParent(designGo.transform, false);
            var bannerPresenter = bannerGo.AddComponent<ConnectionBannerPresenter>();
            var bannerTextGo = new GameObject("BannerText");
            bannerTextGo.transform.SetParent(bannerGo.transform, false);
            var bannerTmp = CreateTmpText(bannerTextGo, "本地宿主未接入 (LOCAL_HOST_UNAVAILABLE)", fontAsset, 28, theme.warningColor);
            var bannerRect = bannerGo.AddComponent<RectTransform>();
            bannerRect.anchoredPosition = new Vector2(0, 150);
            bannerRect.sizeDelta = new Vector2(800, 48);

            var soB = new SerializedObject(bannerPresenter);
            soB.FindProperty("_bannerRoot").objectReferenceValue = bannerGo;
            soB.FindProperty("_bannerText").objectReferenceValue = bannerTmp;
            soB.ApplyModifiedPropertiesWithoutUndo();

            // Status and explanation
            var statusGo = new GameObject("StatusText");
            statusGo.transform.SetParent(designGo.transform, false);
            var statusTmp = CreateTmpText(statusGo, "本地宿主未接入 (P4.1)", fontAsset, 28, theme.textColor);
            var statusRect = statusGo.GetComponent<RectTransform>();
            statusRect.anchoredPosition = new Vector2(0, 80);
            statusRect.sizeDelta = new Vector2(800, 40);

            var expGo = new GameObject("ExplanationText");
            expGo.transform.SetParent(designGo.transform, false);
            var expTmp = CreateTmpText(expGo, "本地对局通道尚未就绪 (LOCAL_HOST_UNAVAILABLE)。P4.2平台壳已就绪，等待P4.1本地工厂对接。", fontAsset, 28, theme.disabledTextColor);
            var expRect = expGo.GetComponent<RectTransform>();
            expRect.anchoredPosition = new Vector2(0, 30);
            expRect.sizeDelta = new Vector2(900, 48);

            var startBtnGo = CreateButton("StartLocalGameButton", designGo.transform, new Vector2(0, -50), new Vector2(400, 88), "开始本地对局 (不可用)", theme, fontAsset);
            startBtnGo.GetComponent<Button>().interactable = false;

            var backBtnGo = CreateButton("BackButton", designGo.transform, new Vector2(0, -150), new Vector2(400, 88), "返回菜单", theme, fontAsset);

            var presenterGo = new GameObject("LobbyPresenter");
            presenterGo.transform.SetParent(designGo.transform, false);
            var presenter = presenterGo.AddComponent<LobbyPresenter>();
            var soL = new SerializedObject(presenter);
            soL.FindProperty("_startLocalGameButton").objectReferenceValue = startBtnGo.GetComponent<Button>();
            soL.FindProperty("_backButton").objectReferenceValue = backBtnGo.GetComponent<Button>();
            soL.FindProperty("_statusText").objectReferenceValue = statusTmp;
            soL.FindProperty("_explanationText").objectReferenceValue = expTmp;
            soL.FindProperty("_connectionBanner").objectReferenceValue = bannerPresenter;
            soL.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.SaveScene(scene, "Assets/Scenes/Lobby.unity");
        }

        private static GameObject CreateButton(string name, Transform parent, Vector2 pos, Vector2 size, string text, UiTheme theme, UnityEngine.Object? fontAsset)
        {
            var btnGo = new GameObject(name);
            btnGo.transform.SetParent(parent, false);
            var rect = btnGo.AddComponent<RectTransform>();
            rect.anchoredPosition = pos;
            rect.sizeDelta = size;

            var img = btnGo.AddComponent<Image>();
            img.sprite = theme.buttonPrimarySprite;
            img.color = theme.primaryColor;

            var btn = btnGo.AddComponent<Button>();
            btn.targetGraphic = img;

            // Attach UiPointerLifecycleTrigger so pointerDown arms UiInputLifecycle
            btnGo.AddComponent<UiPointerLifecycleTrigger>();

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(btnGo.transform, false);
            var textRect = textGo.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.sizeDelta = Vector2.zero;

            CreateTmpText(textGo, text, fontAsset, theme.buttonFontSize, theme.textColor);

            return btnGo;
        }

        private static GameObject CreateModalDialog(string name, Transform parent, UiTheme theme, UnityEngine.Object? fontAsset)
        {
            var modalGo = new GameObject(name);
            modalGo.transform.SetParent(parent, false);
            var modalRect = modalGo.AddComponent<RectTransform>();
            modalRect.anchorMin = Vector2.zero;
            modalRect.anchorMax = Vector2.one;
            modalRect.sizeDelta = Vector2.zero;

            var bgImg = modalGo.AddComponent<Image>();
            bgImg.color = new Color(0, 0, 0, 0.75f);

            var panelGo = new GameObject("Panel");
            panelGo.transform.SetParent(modalGo.transform, false);
            var panelRect = panelGo.AddComponent<RectTransform>();
            panelRect.sizeDelta = new Vector2(640, 360);
            var panelImg = panelGo.AddComponent<Image>();
            panelImg.color = theme.secondaryColor;

            var titleGo = new GameObject("TitleText");
            titleGo.transform.SetParent(panelGo.transform, false);
            var titleTmp = CreateTmpText(titleGo, "提示", fontAsset, 36, theme.textColor);
            var titleRect = titleGo.GetComponent<RectTransform>();
            titleRect.anchoredPosition = new Vector2(0, 100);
            titleRect.sizeDelta = new Vector2(560, 48);

            var msgGo = new GameObject("MessageText");
            msgGo.transform.SetParent(panelGo.transform, false);
            var msgTmp = CreateTmpText(msgGo, "确定要退出游戏吗？", fontAsset, 28, theme.textColor);
            var msgRect = msgGo.GetComponent<RectTransform>();
            msgRect.anchoredPosition = new Vector2(0, 20);
            msgRect.sizeDelta = new Vector2(560, 60);

            var confirmBtnGo = CreateButton("ConfirmButton", panelGo.transform, new Vector2(-140, -80), new Vector2(220, 88), "确定", theme, fontAsset);
            var cancelBtnGo = CreateButton("CancelButton", panelGo.transform, new Vector2(140, -80), new Vector2(220, 88), "取消", theme, fontAsset);

            var tmpType = Type.GetType("TMPro.TextMeshProUGUI, Unity.TextMeshPro");
            var modalPresenter = modalGo.AddComponent<ModalPresenter>();
            var so = new SerializedObject(modalPresenter);
            so.FindProperty("_modalRoot").objectReferenceValue = modalGo;
            so.FindProperty("_titleText").objectReferenceValue = titleTmp;
            so.FindProperty("_messageText").objectReferenceValue = msgTmp;
            so.FindProperty("_confirmButton").objectReferenceValue = confirmBtnGo.GetComponent<Button>();
            so.FindProperty("_cancelButton").objectReferenceValue = cancelBtnGo.GetComponent<Button>();
            if (tmpType != null)
            {
                so.FindProperty("_confirmButtonText").objectReferenceValue = confirmBtnGo.GetComponentInChildren(tmpType);
                so.FindProperty("_cancelButtonText").objectReferenceValue = cancelBtnGo.GetComponentInChildren(tmpType);
            }
            so.ApplyModifiedPropertiesWithoutUndo();

            modalGo.SetActive(false);
            return modalGo;
        }
#endif
    }
}
