#nullable enable
using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Mahjong.Client;
using Mahjong.Unity.App;
using Mahjong.Unity.Platform;
using Mahjong.Unity.UI;

namespace Mahjong.Unity.Tests.PlayMode
{
    /// <summary>
    /// P4.2-B preparation PlayMode evidence for the route itself (not a fake
    /// navigation): a scripted channel is wired into AppComposition so one real
    /// Lobby -&gt; Table scene load and the router's owner gate can be observed.
    /// This is route/lifecycle evidence only; it is never proof that a real
    /// Mahjong.LocalHost is integrated in production.
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
    }
}
