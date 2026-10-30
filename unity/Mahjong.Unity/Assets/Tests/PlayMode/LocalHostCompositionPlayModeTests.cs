#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
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
    /// P4.2-B PlayMode evidence: the first three cases keep the scripted route and
    /// lifecycle boundary as independent error/routing evidence. The real-host
    /// cases use the production AppRoot composition and the real local factory to
    /// load Lobby -&gt; Table, observe a real snapshot, exercise one-owner double
    /// start and prove return-to-menu releases the host before a clean re-entry.
    /// They never reference the forbidden Mahjong.LocalHost or Domain namespaces.
    /// </summary>
    [TestFixture]
    public sealed class LocalHostCompositionPlayModeTests
    {
        private sealed class ScriptedChannel : ITableChannel, IDisposable
        {
            public bool IsOpen { get; private set; }

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
                IsOpen = false;
                return Task.CompletedTask;
            }

            public void Dispose()
            {
                IsOpen = false;
            }
        }

        private sealed class ScriptedFactory : ITableChannelFactory
        {
            public bool Delay;
            public int Creates;
            public readonly ScriptedChannel Channel = new ScriptedChannel();
            public readonly TaskCompletionSource<bool> Release =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public async Task<ITableChannel> CreateAsync(TableTransportKind kind, CancellationToken cancellationToken)
            {
                Creates++;
                if (Delay)
                {
                    var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
                    {
                        await Task.WhenAny(Release.Task, cancelled.Task);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                }

                return Channel;
            }
        }

        private static AppRouter NewRouter(ScriptedFactory factory, out AppComposition composition)
        {
            composition = new AppComposition(
                new AppConfig { pumpBarrierTimeoutMs = 2000, closeTimeoutMs = 2000, cancelObservationMs = 10 },
                new MainThreadDispatcher(),
                channelFactory: factory,
                persistentDataPath: System.IO.Path.GetFullPath("build/p42b-playmode/store-" + Guid.NewGuid().ToString("N")));
            return new AppRouter(composition, new BackRouter());
        }

        private static IEnumerator WaitUntil(Func<bool> predicate, float timeoutSeconds, string label)
        {
            float started = Time.realtimeSinceStartup;
            while (!predicate() && Time.realtimeSinceStartup - started < timeoutSeconds)
            {
                yield return null;
            }

            Assert.IsTrue(predicate(), "timeout waiting for " + label);
        }

        private static IEnumerator Cleanup(AppRouter router, AppComposition composition)
        {
            if (router.CurrentScene == SceneId.Table)
            {
                Task<bool> back = router.ReturnToMenuAsync();
                yield return WaitUntil(() => back.IsCompleted, 20f, "return to menu");
                Assert.IsTrue(back.Result, "cleanup return to menu failed");
            }

            Task<ShutdownResult> shutdown = composition.ShutdownAsync("PLAYMODE_TEST_DONE");
            yield return WaitUntil(() => shutdown.IsCompleted, 20f, "composition shutdown");
        }

        [UnityTest]
        public IEnumerator StartLocalGame_SuccessPath_LoadsTableAndKeepsSessionAlive()
        {
            AppRoot.EnsureInitialized(new AppConfig());
            var factory = new ScriptedFactory();
            AppRouter router = NewRouter(factory, out AppComposition composition);

            Task<bool> lobby = router.LoadAsync(SceneId.Lobby);
            yield return WaitUntil(() => lobby.IsCompleted, 20f, "Lobby load");
            Assert.IsTrue(lobby.Result);

            Task<LocalGameStartResult> start = router.StartLocalGameAsync();
            yield return WaitUntil(() => start.IsCompleted, 30f, "start completion");
            LocalGameStartResult result = start.Result;
            Debug.Log($"PLAYMODE_START status={result.Status} code={result.StableErrorCode} scene={router.CurrentScene} coordinator={composition.SessionCoordinator != null} channelOpen={factory.Channel.IsOpen} creates={factory.Creates}");

            Assert.AreEqual(LocalGameStartStatus.Started, result.Status);
            Assert.AreEqual(SceneId.Table, router.CurrentScene);
            Assert.AreEqual("Table", SceneManager.GetActiveScene().name);
            Assert.IsNotNull(composition.SessionCoordinator, "The started route must keep a live coordinator.");
            Assert.IsTrue(factory.Channel.IsOpen, "The scripted channel must stay open for the started route.");
            Assert.AreEqual(1, factory.Creates);
            Assert.IsFalse(router.IsStartInFlight);

            yield return Cleanup(router, composition);
        }

        [UnityTest]
        public IEnumerator ConcurrentDoubleStart_OneStarted_OneBusy_WithoutSecondShutdown()
        {
            AppRoot.EnsureInitialized(new AppConfig());
            var factory = new ScriptedFactory { Delay = true };
            AppRouter router = NewRouter(factory, out AppComposition composition);

            Task<bool> lobby = router.LoadAsync(SceneId.Lobby);
            yield return WaitUntil(() => lobby.IsCompleted, 20f, "Lobby load");
            Assert.IsTrue(lobby.Result);

            Task<LocalGameStartResult> first = router.StartLocalGameAsync();
            yield return WaitUntil(() => factory.Creates == 1, 10f, "factory entry");

            Task<LocalGameStartResult> second = router.StartLocalGameAsync();
            Assert.IsTrue(second.IsCompleted, "The second start must be rejected immediately.");
            LocalGameStartResult busy = second.Result;
            Assert.AreEqual(LocalGameStartStatus.Busy, busy.Status);
            Assert.AreEqual(1, factory.Creates, "The busy call must not create another channel.");

            factory.Release.TrySetResult(true);
            yield return WaitUntil(() => first.IsCompleted, 30f, "first start completion");
            LocalGameStartResult started = first.Result;
            Debug.Log($"PLAYMODE_CONCURRENT first={started.Status} second={busy.Status} scene={router.CurrentScene} coordinator={composition.SessionCoordinator != null} channelOpen={factory.Channel.IsOpen} creates={factory.Creates}");

            Assert.AreEqual(LocalGameStartStatus.Started, started.Status);
            Assert.AreEqual(SceneId.Table, router.CurrentScene);
            Assert.IsNotNull(composition.SessionCoordinator);
            Assert.IsTrue(factory.Channel.IsOpen, "The second caller must not shut down the owner's session.");
            Assert.AreEqual(1, factory.Creates);

            yield return Cleanup(router, composition);
        }

        [UnityTest]
        public IEnumerator PublicSameSceneLobbyReload_InvalidatesPendingStart()
        {
            AppRoot.EnsureInitialized(new AppConfig());
            var factory = new ScriptedFactory { Delay = true };
            AppRouter router = NewRouter(factory, out AppComposition composition);

            Task<bool> lobby = router.LoadAsync(SceneId.Lobby);
            yield return WaitUntil(() => lobby.IsCompleted, 20f, "initial Lobby load");
            Assert.IsTrue(lobby.Result);

            Task<LocalGameStartResult> start = router.StartLocalGameAsync();
            yield return WaitUntil(() => factory.Creates == 1, 10f, "factory entry");

            // Public API same-scene reload, no reflection. It must advance the
            // navigation generation and prevent the old open from entering Table.
            Task<bool> reload = router.LoadAsync(SceneId.Lobby);
            yield return WaitUntil(() => reload.IsCompleted, 20f, "same-scene Lobby reload");
            Assert.IsTrue(reload.Result);
            Assert.AreEqual(SceneId.Lobby, router.CurrentScene);

            factory.Release.TrySetResult(true);
            yield return WaitUntil(() => start.IsCompleted, 30f, "start completion after reload");
            LocalGameStartResult result = start.Result;
            Debug.Log($"PLAYMODE_SAME_SCENE status={result.Status} code={result.StableErrorCode} scene={router.CurrentScene} coordinator={composition.SessionCoordinator != null} channelOpen={factory.Channel.IsOpen}");

            Assert.AreEqual(LocalGameStartStatus.Cancelled, result.Status,
                "A public same-scene reload must cancel the pending start instead of entering Table.");
            Assert.AreEqual(SceneId.Lobby, router.CurrentScene);
            Assert.IsNull(composition.SessionCoordinator, "The cancelled open must release its session.");
            Assert.IsFalse(factory.Channel.IsOpen, "The cancelled open must close its scripted channel.");

            yield return Cleanup(router, composition);
        }

        // ------------------------------------------------------------------
        // Real accepted pure .NET local host through the production AppRoot
        // ------------------------------------------------------------------

        private static AppConfig RealConfig() => new AppConfig
        {
            pumpBarrierTimeoutMs = 5000,
            closeTimeoutMs = 5000,
            cancelObservationMs = 50
        };

        private static string NewRealRoot() =>
            Path.GetFullPath(Path.Combine("build", "p42b-playmode-realhost", Guid.NewGuid().ToString("N")));

        private static void ResetAppRoot()
        {
            foreach (AppRoot root in UnityEngine.Object.FindObjectsByType<AppRoot>(
                         FindObjectsInactive.Include,
                         FindObjectsSortMode.None))
            {
                UnityEngine.Object.DestroyImmediate(root.gameObject);
            }
        }

        private static IEnumerator WaitForRealSnapshot(AppComposition composition, float timeoutSeconds)
        {
            yield return WaitUntil(
                () => composition.SessionCoordinator != null &&
                      composition.SessionCoordinator.Current.State == TableConnectionState.Ready &&
                      composition.SessionCoordinator.Current.Binding != null &&
                      composition.SessionCoordinator.Current.PlayerSnapshot != null,
                timeoutSeconds,
                "real local snapshot");
        }

        [UnityTest]
        public IEnumerator RealLocalHost_StartFromLobby_PublishesRealSnapshot()
        {
            ResetAppRoot();
            string root = NewRealRoot();
            AppRoot app = AppRoot.EnsureInitialized(RealConfig(), null, root);
            AppComposition composition = app.Composition;

            Assert.IsTrue(app.Router.IsLocalGameStartAvailable,
                "The production default must advertise the real local host.");
            Assert.IsInstanceOf<LocalTableChannelFactory>(composition.ChannelFactory);

            Task<bool> lobby = app.Router.LoadAsync(SceneId.Lobby);
            yield return WaitUntil(() => lobby.IsCompleted, 20f, "Lobby load");
            Assert.IsTrue(lobby.Result);

            Task<LocalGameStartResult> start = app.Router.StartLocalGameAsync();
            yield return WaitUntil(() => start.IsCompleted, 60f, "real local start completion");
            LocalGameStartResult result = start.Result;
            Assert.AreEqual(LocalGameStartStatus.Started, result.Status,
                "The production route must start a real local table.");
            Assert.AreEqual(SceneId.Table, app.Router.CurrentScene);
            Assert.AreEqual("Table", SceneManager.GetActiveScene().name);

            yield return WaitForRealSnapshot(composition, 60f);
            SessionViewState state = composition.SessionCoordinator!.Current;
            Debug.Log(
                "PLAYMODE_REAL_START" +
                " room=" + state.Binding!.RoomId +
                " seat=" + state.Binding.SeatId +
                " stateVersion=" + state.PlayerSnapshot!.StateVersion +
                " baseline=" + state.IsFullBaseline +
                " batch=" + state.VisibleEventBatch.Kind);

            Assert.AreEqual(LocalTableChannelFactory.DefaultRoomId, state.Binding.RoomId);
            Assert.AreEqual(0, state.Binding.SeatId);
            Assert.IsNotNull(state.PlayerSnapshot.PrivateView);
            Assert.AreEqual(TableVisibleEventBatchKind.Baseline, state.VisibleEventBatch.Kind);
            Assert.AreEqual(0, state.VisibleEventBatch.PlayableEvents.Count,
                "Baseline history must not be exposed as new animation.");

            yield return Cleanup(app.Router, composition);
        }

        [UnityTest]
        public IEnumerator RealLocalHost_DoubleClick_OneStartedOneBusy()
        {
            ResetAppRoot();
            AppRoot app = AppRoot.EnsureInitialized(RealConfig(), null, NewRealRoot());
            AppComposition composition = app.Composition;

            Task<bool> lobby = app.Router.LoadAsync(SceneId.Lobby);
            yield return WaitUntil(() => lobby.IsCompleted, 20f, "Lobby load");
            Assert.IsTrue(lobby.Result);

            Task<LocalGameStartResult> first = app.Router.StartLocalGameAsync();
            Task<LocalGameStartResult> second = app.Router.StartLocalGameAsync();
            Assert.IsTrue(second.IsCompleted,
                "The second click must be rejected synchronously without entering the open.");
            Assert.AreEqual(LocalGameStartStatus.Busy, second.Result.Status);

            yield return WaitUntil(() => first.IsCompleted, 60f, "owner start completion");
            Assert.AreEqual(LocalGameStartStatus.Started, first.Result.Status);
            yield return WaitForRealSnapshot(composition, 60f);
            Assert.IsNotNull(composition.SessionCoordinator,
                "The owner start must leave exactly one live session.");
            Debug.Log(
                "PLAYMODE_REAL_CONCURRENT first=" + first.Result.Status +
                " second=" + second.Result.Status +
                " room=" + composition.SessionCoordinator!.Current.Binding!.RoomId);

            yield return Cleanup(app.Router, composition);
        }

        [UnityTest]
        public IEnumerator RealLocalHost_ReturnToMenuThenReenter_RecoversSameRoom()
        {
            ResetAppRoot();
            AppRoot app = AppRoot.EnsureInitialized(RealConfig(), null, NewRealRoot());
            AppComposition composition = app.Composition;

            Task<bool> lobby = app.Router.LoadAsync(SceneId.Lobby);
            yield return WaitUntil(() => lobby.IsCompleted, 20f, "Lobby load");
            Assert.IsTrue(lobby.Result);

            Task<LocalGameStartResult> firstStart = app.Router.StartLocalGameAsync();
            yield return WaitUntil(() => firstStart.IsCompleted, 60f, "first real start");
            Assert.AreEqual(LocalGameStartStatus.Started, firstStart.Result.Status);
            yield return WaitForRealSnapshot(composition, 60f);
            string firstSession = composition.SessionCoordinator!.Current.Binding!.SessionId;
            string firstRoom = composition.SessionCoordinator.Current.Binding.RoomId;

            Task<bool> back = app.Router.ReturnToMenuAsync();
            yield return WaitUntil(() => back.IsCompleted, 30f, "return to menu");
            Assert.IsTrue(back.Result);
            Assert.AreEqual(SceneId.Menu, app.Router.CurrentScene);
            Assert.IsNull(composition.SessionCoordinator,
                "Returning to the menu must release the real local session and its file lease.");
            Assert.IsTrue(app.Router.IsLocalGameStartAvailable,
                "A clean close must leave the composition reusable.");

            Task<LocalGameStartResult> secondStart = app.Router.StartLocalGameAsync();
            yield return WaitUntil(() => secondStart.IsCompleted, 60f, "second real start");
            Assert.AreEqual(LocalGameStartStatus.Started, secondStart.Result.Status,
                "A clean return-to-menu followed by a fresh start must reopen the real host.");
            yield return WaitForRealSnapshot(composition, 60f);
            SessionViewState second = composition.SessionCoordinator!.Current;
            Debug.Log(
                "PLAYMODE_REAL_REENTRY firstSession=" + firstSession +
                " secondSession=" + second.Binding!.SessionId +
                " room=" + second.Binding.RoomId);

            Assert.AreEqual(firstRoom, second.Binding.RoomId,
                "A re-entry must recover the same stable local room.");
            Assert.AreNotEqual(firstSession, second.Binding.SessionId,
                "A re-entry must open a fresh connection/session identity, never reuse the closed one.");

            yield return Cleanup(app.Router, composition);
        }
        [UnityTest]
        public IEnumerator RealLocalHost_DiscardBatchConfirmedByIndependentObserver()
        {
            ResetAppRoot();
            AppRoot app = AppRoot.EnsureInitialized(RealConfig(), null, NewRealRoot());
            AppComposition composition = app.Composition;

            Task<bool> lobby = app.Router.LoadAsync(SceneId.Lobby);
            yield return WaitUntil(() => lobby.IsCompleted, 20f, "Lobby load");
            Assert.IsTrue(lobby.Result);

            Task<LocalGameStartResult> start = app.Router.StartLocalGameAsync();
            yield return WaitUntil(() => start.IsCompleted, 60f, "real local start completion");
            Assert.AreEqual(LocalGameStartStatus.Started, start.Result.Status);
            yield return WaitForRealSnapshot(composition, 60f);

            SessionCoordinator coordinator = composition.SessionCoordinator!;
            var mapper = new PlayerTableProjectionMapper(new SystemTableClientClock());
            var intentFactory = new TableCommandIntentFactory(new SystemTableClientClock());
            PlayerLegalActionState? discard = null;
            yield return WaitUntil(
                () =>
                {
                    PlayerTableViewState projected = mapper.Project(coordinator.Current);
                    IReadOnlyList<PlayerLegalActionState>? candidates = projected.Own?.Decision?.Candidates;
                    if (candidates == null)
                    {
                        return false;
                    }

                    for (int i = 0; i < candidates.Count; i++)
                    {
                        if (candidates[i].Action == PlayerActionKind.DiscardTile)
                        {
                            discard = candidates[i];
                            return true;
                        }
                    }

                    return false;
                },
                30f,
                "player decision with a discard candidate");

            PlayerTableViewState view = mapper.Project(coordinator.Current);
            string requestId = "playmode-observer-" + Guid.NewGuid().ToString("N");
            var owner = new TableCommandOwnerContext(
                new RequestId(requestId),
                new RequestScopeId(view.Identity.ScopeId),
                new StateVersion(view.Identity.StateVersion),
                new OwnerEpoch(view.Identity.OwnerEpoch));
            TableCommandIntent intent = intentFactory.CreateSubmitAction(view, discard!, owner);

            using var observer = new ConfirmedVisibleEventObserver(coordinator);
            coordinator.ReceiptResolved += observer.OnReceiptResolved;
            try
            {
                observer.BeginAction(coordinator.Current, requestId, view.Identity.ScopeId, PlayerActionKind.DiscardTile);
                Task<CommandSubmission> submit = coordinator.SubmitAsync(intent.Command, CancellationToken.None);
                yield return WaitUntil(() => submit.IsCompleted, 10f, "discard submit");
                Assert.IsTrue(submit.Result.Sent, "the discard must be handed to the transport");

                yield return WaitUntil(() => observer.TryGetConfirmation() != null, 20f, "confirmed discard event");
                ConfirmedVisibleActionEvent? confirmed = observer.TryGetConfirmation();
                Assert.IsNotNull(confirmed);
                Assert.AreEqual(ProtocolEventType.TileDiscarded, confirmed!.Event.Type);
                Assert.AreEqual(0, confirmed.Event.Seat);
                Debug.Log(
                    "PLAYMODE_OBSERVER_ACTION request=" + requestId +
                    " commandVersion=" + confirmed.CommandVersion +
                    " batchStateVersion=" + confirmed.Batch.StateVersion +
                    " eventSeq=" + confirmed.Event.EventSeq +
                    " batches=" + observer.DescribeBatches());
            }
            finally
            {
                coordinator.ReceiptResolved -= observer.OnReceiptResolved;
            }

            yield return Cleanup(app.Router, composition);
        }

    }
}
