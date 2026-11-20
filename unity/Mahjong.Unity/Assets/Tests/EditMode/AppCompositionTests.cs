#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.LocalHost;
using Mahjong.Unity.App;
using Mahjong.Unity.Platform;
using Mahjong.Unity.Table;
using Mahjong.Unity.UI;
using Mahjong.Unity.UI.Session;
using TMPro;
using WireEnvelope = Mahjong.Contracts.Wire.Envelope;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.UI;
#endif

namespace Mahjong.Unity.Tests.EditMode
{
    [TestFixture]
    public sealed class AppCompositionTests
    {
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
        public async Task LocalLiteGamePreparation_RotatesRoomIdWithoutOpeningAHost()
        {
            string root = System.IO.Path.GetFullPath("build/local-lite-prep-" + Guid.NewGuid().ToString("N"));
            var paths = new UnityAppPaths(root);
            var factory = new LocalTableChannelFactory(new AppConfig { localLiteMode = true }, paths);
            string first = factory.RoomId;

            await factory.PrepareLocalLiteGameAsync("pon", CancellationToken.None);
            string ponRoom = factory.RoomId;
            string ponOutbox = factory.CurrentOutboxDirectory;
            await factory.PrepareLocalLiteGameAsync("chi", CancellationToken.None);
            string chiRoom = factory.RoomId;
            string chiOutbox = factory.CurrentOutboxDirectory;
            Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
                await factory.PrepareLocalLiteGameAsync("not-a-case", CancellationToken.None));

            Assert.AreNotEqual(first, ponRoom);
            Assert.AreNotEqual(ponRoom, chiRoom);
            Assert.AreNotEqual(ponOutbox, chiOutbox, "Every Lite room must own an isolated, retained client outbox scope.");
            Assert.AreEqual(chiRoom, factory.RoomId, "An invalid case must not replace the selected room scope.");
            StringAssert.StartsWith("local-lite-", ponRoom);
            Assert.IsFalse(factory.IsRoomOpen, "Preparing a case must not create its host before StartLocalGameAsync owns the transition.");
            Assert.IsFalse(System.IO.Directory.Exists(root), "Preparing a case must not create save or outbox directories.");
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

        [Test]
        public async Task SharedRoomPrimarySeatConflictAndReturnKeepExistingViewerAlive()
        {
            string root = System.IO.Path.GetFullPath("build/shared-room-owner-" + Guid.NewGuid().ToString("N"));
            var comp = new AppComposition(new AppConfig(), new MainThreadDispatcher(), persistentDataPath: root);
            SessionCoordinator viewer = await comp.OpenRoomViewerAsync(LocalRoomViewerRole.Player, 0, CancellationToken.None);
            for (int i = 0; i < 500 && viewer.Current.PlayerSnapshot == null; i++)
            {
                comp.UpdateRoomViewers();
                await Task.Delay(10);
            }
            Assert.IsNotNull(viewer.Current.PlayerSnapshot, "The viewer must have received its initial real host projection before the ownership negative case.");
            Assert.AreEqual(TableConnectionState.Ready, viewer.Current.State);

            Assert.ThrowsAsync<LocalTableException>(async () => await comp.OpenLocalSessionAsync(CancellationToken.None));
            Assert.AreEqual(TableConnectionState.Ready, viewer.Current.State, "A duplicate primary seat must not close the room owned by a registered viewer.");
            Assert.IsTrue(viewer.CanSubmit, "The existing viewer must remain attached to its original binding after the rejected open.");
            Assert.IsNotNull(viewer.Current.PlayerSnapshot, "The retained viewer must keep receiving its private projection.");
            int laterProjections = 0;
            viewer.Changed += (_, __) => laterProjections++;
            for (int i = 0; i < 500 && laterProjections == 0; i++)
            {
                comp.UpdateRoomViewers();
                await Task.Delay(10);
            }
            Assert.Greater(laterProjections, 0, "The original room viewer must continue receiving projections after the rejected open.");

            ShutdownResult returned = await comp.ShutdownAsync("RETURN_TO_MENU");
            Assert.AreEqual(ShutdownOutcome.Completed, returned.Outcome);
            Assert.AreEqual(TableConnectionState.Ready, viewer.Current.State, "Non-exit cleanup without a primary must retain registered viewers and their shared host.");
            Assert.IsTrue(viewer.CanSubmit);

            ShutdownResult exited = await comp.ShutdownAsync("APP_QUIT");
            Assert.AreEqual(ShutdownOutcome.Completed, exited.Outcome);
            Assert.AreEqual(TableConnectionState.Closed, viewer.Current.State);
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
        private SessionCoordinator Coordinator(FakeSession session, MainThreadDispatcher dispatcher, TableSessionOperationQueue? queue = null,
            Func<Task<TableSessionQueueSnapshot?>>? captureProvider = null, int closeTimeoutMs = 50) =>
            new SessionCoordinator(session, queue, null, dispatcher,
                new AppConfig { closeTimeoutMs = closeTimeoutMs, pumpBarrierTimeoutMs = 50, cancelObservationMs = 10 }, 1, captureProvider);

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
        public async Task ShutdownReconcilesOldScopeBeforeClosingAndKeepsUnknownAuthorityOpen()
        {
            var session = new FakeSession();
            var key = new IdempotencyKey("player", new RequestScopeId("old-room-scope"), new RequestId("old-request"));
            session.PendingCommands = new[]
            {
                new PendingCommandSnapshot(key, CommandKind.SubmitAction, "digest", 32,
                    handedToTransport: true, deliveryUnknown: true, needsReconciliation: true,
                    lastLookup: null, lastReceipt: null, queryInFlight: false)
            };
            session.Reconcile = () => session.PendingCommands = Array.Empty<PendingCommandSnapshot>();
            var queue = new TableSessionOperationQueue(session);
            var owner = Coordinator(session, new MainThreadDispatcher(), queue);
            await queue.OpenAsync(new TableSessionOpenRequest("ticket", "test"), CancellationToken.None);
            owner.SetPumpTask(Task.CompletedTask);

            ShutdownResult result = await owner.ShutdownAsync("RETURN_TO_MENU");

            Assert.AreEqual(ShutdownOutcome.Completed, result.Outcome);
            CollectionAssert.AreEqual(new[] { "query:old-room-scope/old-request", "close" }, session.Lifecycle);
        }

        [Test]
        public async Task UnknownOldReceiptMustQuarantineWithoutClosingItsAuthority()
        {
            var session = new FakeSession();
            var key = new IdempotencyKey("player", new RequestScopeId("old-room-scope"), new RequestId("old-request"));
            session.PendingCommands = new[]
            {
                new PendingCommandSnapshot(key, CommandKind.SubmitAction, "digest", 32,
                    handedToTransport: true, deliveryUnknown: true, needsReconciliation: true,
                    lastLookup: null, lastReceipt: null, queryInFlight: false)
            };
            session.Reconcile = () => { };
            var queue = new TableSessionOperationQueue(session);
            var owner = Coordinator(session, new MainThreadDispatcher(), queue);
            await queue.OpenAsync(new TableSessionOpenRequest("ticket", "test"), CancellationToken.None);
            owner.SetPumpTask(Task.CompletedTask);

            ShutdownResult result = await owner.ShutdownAsync("RETURN_TO_MENU");

            Assert.AreEqual(ShutdownOutcome.CloseFault, result.Outcome);
            Assert.IsTrue(owner.IsQuarantined);
            Assert.IsFalse(owner.RunToken.IsCancellationRequested, "Unknown is not proof of non-application; keep the original authority and writer alive.");
            CollectionAssert.AreEqual(new[] { "query:old-room-scope/old-request" }, session.Lifecycle);
            Assert.AreEqual("old-request", session.PendingCommands[0].RequestId);
        }

        [Test]
        public async Task ShutdownWaitsForAlreadyInFlightOldScopeLookupWithoutIssuingAnotherQuery()
        {
            var session = new FakeSession();
            var key = new IdempotencyKey("player", new RequestScopeId("old-room-scope"), new RequestId("old-request"));
            session.PendingCommands = new[]
            {
                new PendingCommandSnapshot(key, CommandKind.SubmitAction, "digest", 32,
                    handedToTransport: true, deliveryUnknown: true, needsReconciliation: true,
                    lastLookup: null, lastReceipt: null, queryInFlight: true)
            };
            var queue = new TableSessionOperationQueue(session);
            var owner = Coordinator(session, new MainThreadDispatcher(), queue, closeTimeoutMs: 250);
            await queue.OpenAsync(new TableSessionOpenRequest("ticket", "test"), CancellationToken.None);
            owner.SetPumpTask(Task.CompletedTask);

            _ = Task.Run(async () =>
            {
                await Task.Delay(50);
                session.Lifecycle.Add("query-resolved:old-room-scope/old-request");
                session.PendingCommands = Array.Empty<PendingCommandSnapshot>();
            });
            ShutdownResult result = await owner.ShutdownAsync("RETURN_TO_MENU");

            Assert.AreEqual(ShutdownOutcome.Completed, result.Outcome);
            CollectionAssert.AreEqual(new[] { "query-resolved:old-room-scope/old-request", "close" }, session.Lifecycle,
                "An in-flight lookup must settle in the original scope before close; shutdown must not issue a duplicate query.");
        }

        [Test]
        public async Task InFlightOldScopeLookupTimeoutQuarantinesAndKeepsAuthorityOpen()
        {
            var session = new FakeSession();
            var key = new IdempotencyKey("player", new RequestScopeId("old-room-scope"), new RequestId("old-request"));
            session.PendingCommands = new[]
            {
                new PendingCommandSnapshot(key, CommandKind.SubmitAction, "digest", 32,
                    handedToTransport: true, deliveryUnknown: true, needsReconciliation: true,
                    lastLookup: null, lastReceipt: null, queryInFlight: true)
            };
            var queue = new TableSessionOperationQueue(session);
            var owner = Coordinator(session, new MainThreadDispatcher(), queue, closeTimeoutMs: 75);
            await queue.OpenAsync(new TableSessionOpenRequest("ticket", "test"), CancellationToken.None);
            owner.SetPumpTask(Task.CompletedTask);

            ShutdownResult result = await owner.ShutdownAsync("RETURN_TO_MENU");

            Assert.AreEqual(ShutdownOutcome.CloseTimeout, result.Outcome);
            Assert.IsTrue(owner.IsQuarantined);
            Assert.IsFalse(owner.RunToken.IsCancellationRequested, "Keep the original receive path available while its old-scope receipt query remains in flight.");
            CollectionAssert.IsEmpty(session.Lifecycle, "No close or duplicate lookup may pass an in-flight receipt query.");
        }

        [Test]
        public async Task ShutdownCaptureStallIsBoundedAndLateCaptureCannotCloseAuthority()
        {
            var session = new FakeSession();
            var queue = new TableSessionOperationQueue(session);
            var capture = new TaskCompletionSource<TableSessionQueueSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var owner = Coordinator(session, new MainThreadDispatcher(), queue, () => capture.Task, closeTimeoutMs: 75);
            await queue.OpenAsync(new TableSessionOpenRequest("ticket", "test"), CancellationToken.None);
            owner.SetPumpTask(Task.CompletedTask);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            ShutdownResult result = await owner.ShutdownAsync("RETURN_TO_MENU");
            stopwatch.Stop();

            Assert.AreEqual(ShutdownOutcome.CloseTimeout, result.Outcome);
            Assert.Less(stopwatch.ElapsedMilliseconds, 400, "The owner capture must be covered by the configured close budget.");
            Assert.IsTrue(owner.IsQuarantined);
            Assert.IsFalse(session.Lifecycle.Contains("close"));
            capture.TrySetResult(new TableSessionQueueSnapshot(TableConnectionState.Ready, null, null,
                Array.Empty<PendingCommandSnapshot>(), null, 0));
            await Task.Delay(25);
            Assert.IsFalse(session.Lifecycle.Contains("close"), "A late owner completion must not revive shutdown or release the old authority.");
        }

        [Test]
        public async Task RealLiteComposition_ReconcilesAppliedCommandAfterReceiptLossBeforeClosingRoom()
        {
            string root = System.IO.Path.GetFullPath("build/lite-close-applied-" + Guid.NewGuid().ToString("N"));
            var config = new AppConfig { localLiteMode = true, closeTimeoutMs = 5000 };
            var paths = new UnityAppPaths(root);
            var faultChannel = new LocalCloseFaultChannel();
            var factory = new LocalTableChannelFactory(config, paths, channelDecoratorForTest: channel =>
            {
                faultChannel.Inner = channel;
                faultChannel.DropFirstActionReceipt = true;
                faultChannel.DelayActionSend = true;
                faultChannel.DelayReceiptLookup = true;
                return faultChannel;
            });
            var composition = new AppComposition(config, new MainThreadDispatcher(), channelFactory: factory, persistentDataPath: root);
            await composition.PrepareLocalLiteGameAsync("dealer-tsumo", CancellationToken.None);
            SessionCoordinator coordinator = await composition.OpenLocalSessionAsync(CancellationToken.None);
            PlayerTableViewState view = await WaitForPlayerDecisionAsync(composition, coordinator);
            PlayerLegalActionState tsumo = FindAction(view, PlayerActionKind.DeclareTsumo);
            string scope = view.Identity.ScopeId;
            var intent = CreateActionIntent(view, tsumo, "lite-r4-applied-" + Guid.NewGuid().ToString("N"));
            Task<CommandSubmission> submit = coordinator.SubmitAsync(intent.Command, CancellationToken.None);
            Assert.AreSame(faultChannel.ActionSendEntered.Task,
                await Task.WhenAny(faultChannel.ActionSendEntered.Task, Task.Delay(5000)),
                "The real Client must hand the action to the instrumented room channel.");
            Task<ShutdownResult> shutdown = composition.ShutdownAsync("APP_QUIT");
            await Task.Delay(30);
            Assert.IsFalse(shutdown.IsCompleted, "Shutdown must include a Submit already handed to the real room channel.");
            faultChannel.ReleaseActionSend.TrySetResult(true);
            CommandSubmission sent = await submit;
            Assert.IsTrue(sent.Sent);

            Assert.AreSame(faultChannel.ReceiptLookupEntered.Task,
                await Task.WhenAny(faultChannel.ReceiptLookupEntered.Task, Task.Delay(5000)),
                "Shutdown must query the same old room for the command whose first receipt was lost.");
            Assert.IsFalse(shutdown.IsCompleted, "A query already in flight must keep the old room open until its answer arrives.");
            Assert.IsTrue(factory.IsRoomOpen);
            faultChannel.ReleaseReceiptLookup.TrySetResult(true);

            ShutdownResult result = await shutdown;
            Assert.AreEqual(ShutdownOutcome.Completed, result.Outcome,
                "real old-scope reconcile did not complete; queries=" + faultChannel.ReceiptQueries +
                " dropped=" + faultChannel.DroppedActionReceipts + " commands=" + faultChannel.ActionCommands);
            Assert.AreEqual(1, faultChannel.ActionCommands, "The accepted command must not be retried while recovering its receipt.");
            Assert.AreEqual(1, faultChannel.DroppedActionReceipts);
            Assert.Greater(faultChannel.ReceiptQueries, 0, "The old authority must answer a ReceiptQuery before room close.");
            Assert.AreEqual(intent.Command.RequestId.Value, faultChannel.ReceiptQueryRequestIds[0]);
            Assert.AreEqual(scope, faultChannel.ReceiptQueryScopeIds[0]);
            Assert.IsFalse(factory.IsRoomOpen);
        }

        [Test]
        public async Task RealLiteComposition_UnappliedCommandRemainsUnknownAndBlocksSecondHost()
        {
            string root = System.IO.Path.GetFullPath("build/lite-close-unapplied-" + Guid.NewGuid().ToString("N"));
            var config = new AppConfig { localLiteMode = true, closeTimeoutMs = 3000 };
            var paths = new UnityAppPaths(root);
            var faultChannel = new LocalCloseFaultChannel { SuppressActionSend = true };
            var factory = new LocalTableChannelFactory(config, paths, channelDecoratorForTest: channel =>
            {
                faultChannel.Inner = channel;
                return faultChannel;
            });
            var composition = new AppComposition(config, new MainThreadDispatcher(), channelFactory: factory, persistentDataPath: root);
            await composition.PrepareLocalLiteGameAsync("dealer-tsumo", CancellationToken.None);
            SessionCoordinator coordinator = await composition.OpenLocalSessionAsync(CancellationToken.None);
            PlayerTableViewState view = await WaitForPlayerDecisionAsync(composition, coordinator);
            PlayerLegalActionState tsumo = FindAction(view, PlayerActionKind.DeclareTsumo);
            var intent = CreateActionIntent(view, tsumo, "lite-r4-unapplied-" + Guid.NewGuid().ToString("N"));
            CommandSubmission sent = await coordinator.SubmitAsync(intent.Command, CancellationToken.None);
            Assert.IsTrue(sent.Sent, "The client only knows transport acceptance; local authority intentionally receives no command.");

            ShutdownResult result = await composition.ShutdownAsync("RETURN_TO_MENU");

            Assert.AreEqual(ShutdownOutcome.CloseFault, result.Outcome, "A ReceiptLookup Unknown must not be treated as a safe retry or clean close.");
            Assert.IsTrue(factory.IsRoomOpen, "The original LocalTableRoom remains the only authority while outcome is unknown.");
            Assert.AreEqual(0, faultChannel.ActionCommands);
            Assert.AreEqual(1, faultChannel.ReceiptQueries);
            Assert.AreEqual(intent.Command.RequestId.Value, faultChannel.ReceiptQueryRequestIds[0]);
            Assert.AreEqual(view.Identity.ScopeId, faultChannel.ReceiptQueryScopeIds[0]);
            Assert.IsTrue(composition.IsTerminal);
            IReadOnlyList<OutboxEntry> unresolved = await new FileOutboxStore(factory.CurrentOutboxDirectory)
                .LoadUnresolvedAsync(CancellationToken.None);
            Assert.AreEqual(1, unresolved.Count, "Unknown must retain its durable request instead of clearing or retrying it.");
            Assert.AreEqual(intent.Command.RequestId, unresolved[0].RequestId);
            Assert.AreEqual(new RequestScopeId(view.Identity.ScopeId), unresolved[0].ScopeId);
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await composition.PrepareLocalLiteGameAsync("random", CancellationToken.None));
            await factory.CloseRoomAsync(CancellationToken.None);
        }

        [Test]
        public async Task RealLiteComposition_AppliedReceiptTransportLossQuarantinesUnresolvedBeforeRoomClose()
        {
            string root = System.IO.Path.GetFullPath("build/lite-transport-loss-applied-" + Guid.NewGuid().ToString("N"));
            var config = new AppConfig { localLiteMode = true, closeTimeoutMs = 3000 };
            var paths = new UnityAppPaths(root);
            var faultChannel = new LocalCloseFaultChannel
            {
                DropFirstActionReceipt = true,
                InterruptAfterDroppedActionReceipt = true
            };
            var factory = new LocalTableChannelFactory(config, paths, channelDecoratorForTest: channel =>
            {
                faultChannel.Inner = channel;
                return faultChannel;
            });
            var composition = new AppComposition(config, new MainThreadDispatcher(), channelFactory: factory, persistentDataPath: root);
            await composition.PrepareLocalLiteGameAsync("dealer-tsumo", CancellationToken.None);
            SessionCoordinator coordinator = await composition.OpenLocalSessionAsync(CancellationToken.None);
            PlayerTableViewState view = await WaitForPlayerDecisionAsync(composition, coordinator);
            PlayerLegalActionState tsumo = FindAction(view, PlayerActionKind.DeclareTsumo);
            string scope = view.Identity.ScopeId;
            var intent = CreateActionIntent(view, tsumo, "lite-r5-transport-loss-" + Guid.NewGuid().ToString("N"));

            CommandSubmission submission = await coordinator.SubmitAsync(intent.Command, CancellationToken.None);
            Assert.IsTrue(submission.Sent);
            await WaitForConnectionStateAsync(coordinator, TableConnectionState.Reconnecting);

            Assert.AreEqual("Applied", faultChannel.DroppedActionReceiptStatus,
                "The LocalTableRoom must apply the legal tsumo before the wrapper interrupts receipt transport.");
            Assert.AreEqual(1, faultChannel.ActionCommands, "Transport loss must not cause action replay.");
            ShutdownResult result = await composition.ShutdownAsync("RETURN_TO_MENU");

            Assert.AreEqual(ShutdownOutcome.CloseFault, result.Outcome,
                "A non-Ready Client with a durable unresolved command must not report a clean close.");
            Assert.IsTrue(result.Quarantined);
            Assert.IsTrue(factory.IsRoomOpen, "The original room remains the authority needed to reconcile the applied request.");
            Assert.AreEqual(0, faultChannel.ReceiptQueries, "A reconnecting Client cannot query through a broken transport.");
            IReadOnlyList<OutboxEntry> unresolved = await new FileOutboxStore(factory.CurrentOutboxDirectory)
                .LoadUnresolvedAsync(CancellationToken.None);
            Assert.AreEqual(1, unresolved.Count);
            Assert.AreEqual(intent.Command.RequestId, unresolved[0].RequestId);
            Assert.AreEqual(new RequestScopeId(scope), unresolved[0].ScopeId);
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await composition.PrepareLocalLiteGameAsync("random", CancellationToken.None));

            await factory.CloseRoomAsync(CancellationToken.None);
        }

        [Test]
        public async Task RealLiteComposition_NonReadyClientWithoutUnresolvedCommandsClosesCleanly()
        {
            string root = System.IO.Path.GetFullPath("build/lite-transport-loss-empty-" + Guid.NewGuid().ToString("N"));
            var config = new AppConfig { localLiteMode = true, closeTimeoutMs = 3000 };
            var paths = new UnityAppPaths(root);
            var faultChannel = new LocalCloseFaultChannel();
            var factory = new LocalTableChannelFactory(config, paths, channelDecoratorForTest: channel =>
            {
                faultChannel.Inner = channel;
                return faultChannel;
            });
            var composition = new AppComposition(config, new MainThreadDispatcher(), channelFactory: factory, persistentDataPath: root);
            await composition.PrepareLocalLiteGameAsync(null, CancellationToken.None);
            SessionCoordinator coordinator = await composition.OpenLocalSessionAsync(CancellationToken.None);
            _ = await WaitForPlayerDecisionAsync(composition, coordinator);

            faultChannel.TriggerTransientTransportLoss();
            await WaitForConnectionStateAsync(coordinator, TableConnectionState.Reconnecting);
            Assert.AreEqual(0, (await new FileOutboxStore(factory.CurrentOutboxDirectory)
                .LoadUnresolvedAsync(CancellationToken.None)).Count);

            ShutdownResult result = await composition.ShutdownAsync("RETURN_TO_MENU");

            Assert.AreEqual(ShutdownOutcome.Completed, result.Outcome);
            Assert.IsFalse(result.Quarantined);
            Assert.IsFalse(factory.IsRoomOpen, "A non-Ready session with no unresolved command remains cleanly closable.");
        }

        private static async Task WaitForConnectionStateAsync(SessionCoordinator coordinator, TableConnectionState expected)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (GetTransportState(coordinator) != expected && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            Assert.AreEqual(expected, GetTransportState(coordinator), "Timed out waiting for Client state " + expected + ".");
        }

        private static TableConnectionState GetTransportState(SessionCoordinator coordinator)
        {
            var field = typeof(SessionCoordinator).GetField("_operationQueue", BindingFlags.Instance | BindingFlags.NonPublic);
            var queue = field?.GetValue(coordinator) as TableSessionOperationQueue;
            return queue?.State ?? coordinator.Current.State;
        }

        [TestCase(false, ShutdownOutcome.CloseFault)]
        [TestCase(true, ShutdownOutcome.CloseTimeout)]
        public async Task RealLiteComposition_WriterCloseFailureQuarantinesWithoutSecondHost(bool delayClose, ShutdownOutcome expected)
        {
            string root = System.IO.Path.GetFullPath("build/lite-close-writer-" + Guid.NewGuid().ToString("N"));
            var config = new AppConfig { localLiteMode = true, closeTimeoutMs = 100, pumpBarrierTimeoutMs = 100 };
            var paths = new UnityAppPaths(root);
            var faultChannel = new LocalCloseFaultChannel { DelayClose = delayClose, FailClose = !delayClose };
            var factory = new LocalTableChannelFactory(config, paths, channelDecoratorForTest: channel =>
            {
                faultChannel.Inner = channel;
                return faultChannel;
            });
            var composition = new AppComposition(config, new MainThreadDispatcher(), channelFactory: factory, persistentDataPath: root);
            await composition.PrepareLocalLiteGameAsync(null, CancellationToken.None);
            SessionCoordinator coordinator = await composition.OpenLocalSessionAsync(CancellationToken.None);
            _ = await WaitForPlayerDecisionAsync(composition, coordinator);

            ShutdownResult result = await composition.ShutdownAsync("RETURN_TO_MENU");

            Assert.AreEqual(expected, result.Outcome);
            Assert.IsTrue(result.Quarantined);
            Assert.IsTrue(factory.IsRoomOpen);
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await composition.PrepareLocalLiteGameAsync("pon", CancellationToken.None));
            faultChannel.ReleaseClose.TrySetResult(true);
            await factory.CloseRoomAsync(CancellationToken.None);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task RealLiteComposition_LocalTableRoomDisposeTimeoutOrFaultQuarantines(bool delayRoomDispose)
        {
            string root = System.IO.Path.GetFullPath("build/lite-room-dispose-" + Guid.NewGuid().ToString("N"));
            bool failNextDispose = !delayRoomDispose;
            var releaseRoomDispose = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var roomDisposeEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Func<Func<Task>, Task> disposeHook = async dispose =>
            {
                roomDisposeEntered.TrySetResult(true);
                if (delayRoomDispose)
                {
                    await releaseRoomDispose.Task.ConfigureAwait(false);
                    await dispose().ConfigureAwait(false);
                    return;
                }
                if (failNextDispose)
                {
                    failNextDispose = false;
                    await dispose().ConfigureAwait(false);
                    throw new InvalidOperationException("TEST_LOCAL_TABLE_ROOM_WRITER_DISPOSE_FAILURE");
                }
                await dispose().ConfigureAwait(false);
            };
            var config = new AppConfig { localLiteMode = true, closeTimeoutMs = 1000 };
            var paths = new UnityAppPaths(root);
            var factory = new LocalTableChannelFactory(config, paths, roomDisposeForTest: disposeHook);
            var composition = new AppComposition(config, new MainThreadDispatcher(), channelFactory: factory, persistentDataPath: root);
            await composition.PrepareLocalLiteGameAsync(null, CancellationToken.None);
            SessionCoordinator coordinator = await composition.OpenLocalSessionAsync(CancellationToken.None);
            _ = await WaitForPlayerDecisionAsync(composition, coordinator);

            ShutdownResult result = await composition.ShutdownAsync("RETURN_TO_MENU");

            Assert.AreSame(roomDisposeEntered.Task,
                await Task.WhenAny(roomDisposeEntered.Task, Task.Delay(5000)), "The real LocalTableRoom dispose boundary must be reached.");
            Assert.AreEqual(ShutdownOutcome.CloseFault, result.Outcome,
                "Room writer disposal timeout/failure must not be converted to a clean menu transition.");
            Assert.IsTrue(result.Quarantined);
            Assert.IsTrue(factory.IsRoomOpen, "A failed LocalTableRoom dispose must remain the only owned host.");
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await composition.PrepareLocalLiteGameAsync("pon", CancellationToken.None));

            releaseRoomDispose.TrySetResult(true);
            if (delayRoomDispose)
            {
                await factory.CloseRoomAsync(CancellationToken.None);
                Assert.IsFalse(factory.IsRoomOpen);
            }
            else
            {
                Assert.ThrowsAsync<InvalidOperationException>(async () => await factory.CloseRoomAsync(CancellationToken.None));
                Assert.IsTrue(factory.IsRoomOpen, "A faulted room-close record remains quarantined until an explicit recovery path exists.");
            }
        }

        private static async Task<PlayerTableViewState> WaitForPlayerDecisionAsync(AppComposition composition, SessionCoordinator coordinator)
        {
            var mapper = new PlayerTableProjectionMapper(new SystemTableClientClock());
            PlayerTableViewState? view = null;
            for (int i = 0; i < 500; i++)
            {
                composition.Dispatcher.Update(128, 1000);
                view = mapper.Project(coordinator.Current);
                if (coordinator.Current.State == TableConnectionState.Ready &&
                    view.Own?.Decision != null && view.Own.Decision.Candidates.Count > 0)
                    return view;
                await Task.Delay(10);
            }
            Assert.Fail("Real AppComposition did not publish a legal player decision within 5 seconds.");
            throw new AssertionException("unreachable");
        }

        private static PlayerLegalActionState FindAction(PlayerTableViewState view, PlayerActionKind action)
        {
            if (view.Own?.Decision != null)
                for (int i = 0; i < view.Own.Decision.Candidates.Count; i++)
                    if (view.Own.Decision.Candidates[i].Action == action) return view.Own.Decision.Candidates[i];
            Assert.Fail("Expected legal action candidate was not projected: " + action);
            throw new AssertionException("unreachable");
        }

        private static TableCommandIntent CreateActionIntent(PlayerTableViewState view, PlayerLegalActionState action, string requestId) =>
            new TableCommandIntentFactory(new SystemTableClientClock()).CreateSubmitAction(view, action,
                new TableCommandOwnerContext(new RequestId(requestId), new RequestScopeId(view.Identity.ScopeId),
                    new StateVersion(view.Identity.StateVersion), new OwnerEpoch(view.Identity.OwnerEpoch)));

        private sealed class LocalCloseFaultChannel : ITableChannel
        {
            public ITableChannel? Inner;
            public bool DropFirstActionReceipt;
            public bool InterruptAfterDroppedActionReceipt;
            public bool SuppressActionSend;
            public bool DelayActionSend;
            public bool DelayReceiptLookup;
            public bool DelayClose;
            public bool FailClose;
            public int ActionCommands;
            public int DroppedActionReceipts;
            public int ReceiptQueries;
            public readonly List<string> ReceiptQueryRequestIds = new List<string>();
            public readonly List<string> ReceiptQueryScopeIds = new List<string>();
            public readonly TaskCompletionSource<bool> ActionSendEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> ReleaseActionSend = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> ReceiptLookupEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> ReleaseReceiptLookup = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> ReleaseClose = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> _transportLoss = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _transportLossDelivered;
            private string? _actionRequestId;
            private bool _dropped;
            public string? DroppedActionReceiptStatus;
            public bool IsOpen => Inner?.IsOpen ?? false;
            public Task OpenAsync(TableChannelConnectRequest request, CancellationToken cancellationToken) => RequireInner().OpenAsync(request, cancellationToken);

            public async Task SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
            {
                var envelope = WireEnvelope.Parser.ParseFrom(frame.ToArray());
                if (envelope.BodyCase == WireEnvelope.BodyOneofCase.Command)
                {
                    _actionRequestId = envelope.RequestId;
                    ActionSendEntered.TrySetResult(true);
                    if (DelayActionSend) await ReleaseActionSend.Task.ConfigureAwait(false);
                    if (SuppressActionSend) return;
                    ActionCommands++;
                }
                if (envelope.BodyCase == WireEnvelope.BodyOneofCase.ReceiptQuery && envelope.ReceiptQuery != null)
                {
                    ReceiptQueries++;
                    ReceiptQueryRequestIds.Add(envelope.ReceiptQuery.RequestId);
                    ReceiptQueryScopeIds.Add(envelope.ReceiptQuery.RequestScopeId);
                }
                await RequireInner().SendAsync(frame, cancellationToken).ConfigureAwait(false);
            }

            public async Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
            {
                while (true)
                {
                    Task<ReadOnlyMemory<byte>> receive = RequireInner().ReceiveAsync(cancellationToken);
                    if (Volatile.Read(ref _transportLossDelivered) == 0)
                    {
                        Task winner = await Task.WhenAny(receive, _transportLoss.Task).ConfigureAwait(false);
                        if (winner == _transportLoss.Task && Interlocked.Exchange(ref _transportLossDelivered, 1) == 0)
                            throw new TableChannelClosedException("TEST_TRANSIENT_TRANSPORT_LOSS", permanent: false);
                    }
                    ReadOnlyMemory<byte> frame = await receive.ConfigureAwait(false);
                    var envelope = WireEnvelope.Parser.ParseFrom(frame.ToArray());
                    if (DelayReceiptLookup && envelope.BodyCase == WireEnvelope.BodyOneofCase.ReceiptLookup)
                    {
                        ReceiptLookupEntered.TrySetResult(true);
                        await ReleaseReceiptLookup.Task.ConfigureAwait(false);
                    }
                    if (DropFirstActionReceipt && !_dropped && envelope.BodyCase == WireEnvelope.BodyOneofCase.Receipt &&
                        string.Equals(envelope.RequestId, _actionRequestId, StringComparison.Ordinal))
                    {
                        _dropped = true;
                        DroppedActionReceipts++;
                        DroppedActionReceiptStatus = envelope.Receipt.Status.ToString();
                        if (InterruptAfterDroppedActionReceipt)
                            throw new TableChannelClosedException("TEST_APPLIED_RECEIPT_TRANSPORT_LOSS", permanent: false);
                        continue;
                    }
                    return frame;
                }
            }

            public async Task CloseAsync(TableChannelCloseReason reason, CancellationToken cancellationToken)
            {
                if (FailClose) throw new InvalidOperationException("TEST_WRITER_CLOSE_FAILURE");
                if (DelayClose) await ReleaseClose.Task.ConfigureAwait(false);
                await RequireInner().CloseAsync(reason, cancellationToken).ConfigureAwait(false);
            }

            public void Dispose()
            {
                if (Inner is IDisposable disposable) disposable.Dispose();
            }
            public void TriggerTransientTransportLoss() => _transportLoss.TrySetResult(true);
            private ITableChannel RequireInner() => Inner ?? throw new InvalidOperationException("Test decorator is not connected.");
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
            public Action Reconcile = () => { };
            public List<string> Lifecycle { get; } = new List<string>();
            public IReadOnlyList<PendingCommandSnapshot> PendingCommands { get; set; } = Array.Empty<PendingCommandSnapshot>();
            public TableConnectionState State => TableConnectionState.Ready;
            public Func<CancellationToken, bool>? BaselineAdmissionCheck { get; set; }
            public TableSessionBinding? Binding => null;
            public Mahjong.Contracts.StreamCursor? ConfirmedCursor => null;
            public PlayerSnapshot? PlayerSnapshot => null;
            public PublicSnapshot? PublicSnapshot => null;
            public RoomUpdate? RoomSnapshot => null;
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
            public Task ReconcilePendingAsync(CancellationToken ct)
            {
                foreach (PendingCommandSnapshot command in PendingCommands)
                    Lifecycle.Add("query:" + command.ScopeId + "/" + command.RequestId);
                Reconcile();
                return Task.CompletedTask;
            }
            public Task<CommandSubmission> RetryPendingAsync(IdempotencyKey k, CancellationToken ct) => throw new NotSupportedException();
            public Task CloseAsync(string r, CancellationToken ct)
            {
                Lifecycle.Add("close");
                return Close(ct);
            }
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
