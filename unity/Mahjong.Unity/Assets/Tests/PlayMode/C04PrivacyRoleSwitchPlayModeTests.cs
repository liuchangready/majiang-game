#nullable enable
using System;
using System.Collections;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Unity.App;
using Mahjong.Unity.Platform;
using Mahjong.Unity.UI;
using Mahjong.Unity.UI.Session;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using DecisionView = Mahjong.Contracts.Wire.DecisionView;
using PlayerSnapshot = Mahjong.Contracts.Wire.PlayerSnapshot;
using WireEnvelope = Mahjong.Contracts.Wire.Envelope;
using WireMessageType = Mahjong.Contracts.Wire.MessageType;

namespace Mahjong.Unity.Tests.PlayMode
{
    public sealed class C04PrivacyRoleSwitchPlayModeTests
    {
        [UnityTest]
        public IEnumerator RealRoom_FourPlayersSpectatorAndRoleSwitchUseIndependentSessions()
        {
            AppRoot? previous = AppRoot.Instance;
            if (previous != null) UnityEngine.Object.DestroyImmediate(previous.gameObject);
            yield return null;
            LocalTableChannelFactory.EnableC04PrivacyRoomMode();
            string evidenceRoot = EvidenceRoot();
            C04PrivacyProbe.Begin(evidenceRoot);
            AppRoot app = AppRoot.EnsureInitialized(
                new AppConfig { pumpBarrierTimeoutMs = 5000, closeTimeoutMs = 5000, cancelObservationMs = 50 },
                null,
                Path.Combine(Application.temporaryCachePath, "c04-privacy-room-" + Guid.NewGuid().ToString("N")));

            Task<bool> lobby = app.Router.LoadAsync(SceneId.Lobby);
            yield return WaitTask(() => lobby.IsCompleted, 30f, "Lobby load");
            Assert.IsTrue(lobby.Result);
            Task<LocalGameStartResult> start = app.Router.StartLocalGameAsync();
            yield return WaitTask(() => start.IsCompleted, 60f, "seat zero start");
            Assert.AreEqual(LocalGameStartStatus.Started, start.Result.Status);
            yield return WaitCoordinator(app.Composition.SessionCoordinator, 45f, "seat zero baseline");
            SessionCoordinator[] players = new SessionCoordinator[4];
            players[0] = app.Composition.SessionCoordinator!;
            for (int seat = 1; seat < 4; seat++)
            {
                Task<SessionCoordinator> open = app.Composition.OpenRoomViewerAsync(LocalRoomViewerRole.Player, seat, default);
                yield return WaitTask(() => open.IsCompleted, 30f, "seat " + seat + " open");
                players[seat] = open.Result;
                yield return WaitCoordinator(players[seat], app.Composition, 30f, "seat " + seat + " baseline");
            }

            var privateSessionIds = new string[4];
            ulong baselineVersion = players[0].Current.PlayerSnapshot!.StateVersion;
            for (int seat = 0; seat < 4; seat++)
            {
                SessionViewState state = players[seat].Current;
                Assert.AreEqual(TableConnectionState.Ready, state.State);
                Assert.AreEqual((int)ProtocolRole.Player, state.Binding!.Role);
                Assert.AreEqual(seat, state.Binding.SeatId);
                Assert.IsNotNull(state.PlayerSnapshot);
                Assert.AreEqual(seat, state.PlayerSnapshot!.PrivateView.Seat);
                Assert.Greater(state.PlayerSnapshot.PrivateView.ClosedTiles.Count, 0);
                Assert.That(state.PlayerSnapshot.PrivateView.ClosedTiles.Count +
                    (state.PlayerSnapshot.PrivateView.HasDrawnTileId ? 1 : 0), Is.InRange(13, 14));
                Assert.AreEqual(baselineVersion, state.PlayerSnapshot.StateVersion);
                privateSessionIds[seat] = state.Binding.SessionId;
            }

            Task<SessionCoordinator> spectatorOpen = app.Composition.OpenRoomViewerAsync(LocalRoomViewerRole.Spectator, -1, default);
            yield return WaitTask(() => spectatorOpen.IsCompleted, 30f, "spectator open");
            SessionCoordinator spectator = spectatorOpen.Result;
            yield return WaitCoordinator(spectator, app.Composition, 30f, "spectator public baseline");
            Assert.AreEqual((int)ProtocolRole.Spectator, spectator.Current.Binding!.Role);
            Assert.AreEqual(-1, spectator.Current.Binding.SeatId);
            Assert.IsNull(spectator.Current.PlayerSnapshot);
            Assert.IsNotNull(spectator.Current.PublicSnapshot);
            Assert.IsFalse(Array.Exists(privateSessionIds, id => id == spectator.Current.Binding.SessionId));

            SessionCoordinator oldSeatOne = players[1];
            string oldSessionId = oldSeatOne.Current.Binding!.SessionId;
            SessionViewState delayedOldSeatOneView = oldSeatOne.Current;
            long oldSeatOneGeneration = oldSeatOne.Generation;
            ShutdownResult closed = default;
            Task<ShutdownResult> closeSeatOne = app.Composition.CloseRoomViewerAsync(oldSeatOne, "C04_ROLE_SWITCH");
            yield return WaitTask(() => closeSeatOne.IsCompleted, 20f, "seat one close");
            closed = closeSeatOne.Result;
            Assert.AreEqual(ShutdownOutcome.Completed, closed.Outcome);
            Assert.IsTrue(app.Router.IsLocalGameStartAvailable, "Closing one view must leave the room authority available.");

            Task<SessionCoordinator> switchSpectatorOpen = app.Composition.OpenRoomViewerAsync(LocalRoomViewerRole.Spectator, -1, default);
            yield return WaitTask(() => switchSpectatorOpen.IsCompleted, 30f, "seat one to spectator open");
            SessionCoordinator switchedSpectator = switchSpectatorOpen.Result;
            yield return WaitCoordinator(switchedSpectator, app.Composition, 30f, "switched spectator baseline");
            Assert.AreNotEqual(oldSessionId, switchedSpectator.Current.Binding!.SessionId);
            Assert.AreEqual((int)ProtocolRole.Spectator, switchedSpectator.Current.Binding.Role);
            Assert.IsNull(switchedSpectator.Current.PlayerSnapshot);
            Assert.IsNotNull(switchedSpectator.Current.PublicSnapshot);
            Assert.IsNull(oldSeatOne.Current.PlayerSnapshot, "Closed player cache must be cleared before the role switch is published.");

            Task<ShutdownResult> closeSwitchedSpectator = app.Composition.CloseRoomViewerAsync(switchedSpectator, "C04_ROLE_SWITCH");
            yield return WaitTask(() => closeSwitchedSpectator.IsCompleted, 20f, "switched spectator close");
            Assert.AreEqual(ShutdownOutcome.Completed, closeSwitchedSpectator.Result.Outcome);
            Task<SessionCoordinator> seatOneAgainOpen = app.Composition.OpenRoomViewerAsync(LocalRoomViewerRole.Player, 1, default);
            yield return WaitTask(() => seatOneAgainOpen.IsCompleted, 30f, "spectator to seat one open");
            SessionCoordinator seatOneAgain = seatOneAgainOpen.Result;
            yield return WaitCoordinator(seatOneAgain, app.Composition, 30f, "seat one new baseline");
            Assert.AreNotEqual(oldSessionId, seatOneAgain.Current.Binding!.SessionId);
            Assert.AreEqual(1, seatOneAgain.Current.Binding.SeatId);
            Assert.AreEqual(1, seatOneAgain.Current.PlayerSnapshot!.PrivateView.Seat);
            Assert.Greater(seatOneAgain.Current.PlayerSnapshot.PrivateView.ClosedTiles.Count, 0);
            oldSeatOne.Publish(oldSeatOneGeneration, delayedOldSeatOneView);
            Assert.IsNull(oldSeatOne.Current.PlayerSnapshot, "A delayed accepted view from a closed binding must not repopulate its private cache.");
            Assert.AreEqual(seatOneAgain.Current.Binding.SessionId, seatOneAgain.Current.PlayerSnapshot!.SessionId,
                "A late old binding publication must not replace the fresh player's session snapshot.");

            Assert.Greater(C04PrivacyProbe.CapturedFrameCount, 0);
            string rawSha = C04PrivacyProbe.Seal();
            Assert.AreEqual(64, rawSha.Length);
            Debug.Log("C04_PRIVACY_RAW frames=" + C04PrivacyProbe.CapturedFrameCount + " sha256=" + rawSha + " evidence=" + evidenceRoot);

            Task<ShutdownResult> closeSeatOneAgain = app.Composition.CloseRoomViewerAsync(seatOneAgain, "C04_PRIVACY_DONE");
            yield return WaitTask(() => closeSeatOneAgain.IsCompleted, 20f, "seat one final close");
            for (int seat = 2; seat < 4; seat++)
            {
                Task<ShutdownResult> close = app.Composition.CloseRoomViewerAsync(players[seat], "C04_PRIVACY_DONE");
                yield return WaitTask(() => close.IsCompleted, 20f, "seat " + seat + " close");
            }
            Task<ShutdownResult> closeSpectator = app.Composition.CloseRoomViewerAsync(spectator, "C04_PRIVACY_DONE");
            yield return WaitTask(() => closeSpectator.IsCompleted, 20f, "spectator close");
            Task<ShutdownResult> closePrimary = app.Composition.ShutdownAsync("C04_PRIVACY_DONE");
            yield return WaitTask(() => closePrimary.IsCompleted, 20f, "primary close");
            Assert.AreEqual(ShutdownOutcome.Completed, closePrimary.Result.Outcome);
            Task closeRoom = app.Composition.CloseLocalRoomAsync();
            yield return WaitTask(() => closeRoom.IsCompleted, 20f, "room close");
            Assert.IsFalse(closeRoom.IsFaulted, closeRoom.Exception?.ToString());
            LocalTableChannelFactory.DisableC04PrivacyRoomModeForTest();
            C04PrivacyProbe.ResetForTest();
            UnityEngine.Object.DestroyImmediate(app.gameObject);
        }

        [UnityTest]
        public IEnumerator RealRoom_DelayedOldRawReceiveIsRejectedByNewBinding()
        {
            LocalTableChannelFactory.DisableC04PrivacyRoomModeForTest();
            string root = Path.Combine(Application.temporaryCachePath, "c04-delayed-" + Guid.NewGuid().ToString("N"));
            var paths = new UnityAppPaths(root);
            paths.EnsureDirectoriesCreated();
            var factory = new LocalTableChannelFactory(
                new AppConfig { pumpBarrierTimeoutMs = 5000, closeTimeoutMs = 5000, cancelObservationMs = 50 },
                paths,
                "c04-delayed-" + Guid.NewGuid().ToString("N"),
                "owner-" + Guid.NewGuid().ToString("N"),
                TimeSpan.FromMinutes(2));
            TableSession? seatZero = null;
            TableSession? seatOne = null;
            ITableChannel? seatZeroChannel = null;
            ITableChannel? seatOneChannel = null;
            RawCaptureChannel? seatZeroCapture = null;
            RawCaptureChannel? seatOneCapture = null;
            try
            {
                ITableChannel rawZero = factory.CreateRoomViewerAsync(LocalRoomViewerRole.Player, 0, CancellationToken.None).GetAwaiter().GetResult();
                seatZeroChannel = rawZero;
                seatZeroCapture = new RawCaptureChannel(rawZero);
                var zeroTicket = rawZero as ILocalTableTicketSource;
                Assert.IsNotNull(zeroTicket);
                seatZero = new TableSession(seatZeroCapture, new InMemoryOutboxStore());
                Task zeroOpen = seatZero.OpenAsync(new TableSessionOpenRequest(zeroTicket!.SessionTicket, LocalTableChannelFactory.ClientBuild), CancellationToken.None);
                yield return WaitTask(() => zeroOpen.IsCompleted, 30f, "delayed seat zero open");
                Assert.IsFalse(zeroOpen.IsFaulted, zeroOpen.Exception?.ToString());
                yield return PumpUntilReady(seatZero, 30f, "delayed seat zero baseline");
                Assert.IsNotNull(seatZeroCapture.CapturedPlayerSnapshotFrame);
                byte[] oldFrame = seatZeroCapture.CapturedPlayerSnapshotFrame!;
                WireDecodeResult oldDecode = WireCodec.Decode(oldFrame);
                Assert.IsTrue(oldDecode.IsOk);
                Assert.AreEqual(0, oldDecode.Envelope!.SeatId);

                ITableChannel rawOne = factory.CreateRoomViewerAsync(LocalRoomViewerRole.Player, 1, CancellationToken.None).GetAwaiter().GetResult();
                seatOneChannel = rawOne;
                seatOneCapture = new RawCaptureChannel(rawOne);
                var oneTicket = rawOne as ILocalTableTicketSource;
                Assert.IsNotNull(oneTicket);
                seatOne = new TableSession(seatOneCapture, new InMemoryOutboxStore());
                Task oneOpen = seatOne.OpenAsync(new TableSessionOpenRequest(oneTicket!.SessionTicket, LocalTableChannelFactory.ClientBuild), CancellationToken.None);
                yield return WaitTask(() => oneOpen.IsCompleted, 30f, "delayed seat one open");
                Assert.IsFalse(oneOpen.IsFaulted, oneOpen.Exception?.ToString());
                yield return PumpUntilReady(seatOne, 30f, "delayed seat one baseline");
                PlayerSnapshot current = seatOne.PlayerSnapshot!;
                Assert.AreEqual(1, current.PrivateView.Seat);
                string currentTiles = string.Join(",", current.PrivateView.ClosedTiles);
                string currentSession = seatOne.Binding!.SessionId.Value;

                seatOneCapture.InjectNextReceive(oldFrame);
                Task pump = seatOne.PumpOnceAsync(CancellationToken.None);
                yield return WaitTask(() => pump.IsCompleted, 10f, "delayed old raw receive");
                Assert.IsFalse(pump.IsFaulted, pump.Exception?.ToString());
                PlayerSnapshot after = seatOne.PlayerSnapshot!;
                Assert.AreEqual(currentSession, after.SessionId);
                Assert.AreEqual(1, after.PrivateView.Seat);
                Assert.AreEqual(currentTiles, string.Join(",", after.PrivateView.ClosedTiles));

                string evidence = Path.Combine(EvidenceRoot(), "delayed-old-raw");
                Directory.CreateDirectory(evidence);
                File.WriteAllText(Path.Combine(evidence, "delayed-old-raw.json"),
                    "{\"schemaVersion\":1,\"oldSessionId\":\"" + seatZero.Binding!.SessionId.Value +
                    "\",\"newSessionId\":\"" + currentSession +
                    "\",\"oldSeat\":0,\"newSeat\":1,\"oldFrameSha256\":\"" + Sha256Hex(oldFrame) +
                    "\",\"oldFrameBase64\":\"" + Convert.ToBase64String(oldFrame) +
                    "\",\"stateVersion\":\"" + current.StateVersion +
                    "\",\"tilesBefore\":\"" + currentTiles + "\",\"tilesAfter\":\"" + string.Join(",", after.PrivateView.ClosedTiles) +
                    "\",\"accepted\":false}", new UTF8Encoding(false));
            }
            finally
            {
                CloseBlocking(seatOne);
                CloseBlocking(seatOneChannel);
                CloseBlocking(seatZero);
                CloseBlocking(seatZeroChannel);
                try { factory.CloseRoomAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
                LocalTableChannelFactory.DisableC04PrivacyRoomModeForTest();
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            }
        }

        [UnityTest]
        public IEnumerator RealRoom_ColdReopenReconcilesOriginalRequestWithoutDuplicate()
        {
            LocalTableChannelFactory.DisableC04PrivacyRoomModeForTest();
            string root = Path.Combine(Application.temporaryCachePath, "c04-recovery-" + Guid.NewGuid().ToString("N"));
            string roomId = "c04-recovery-" + Guid.NewGuid().ToString("N");
            var paths = new UnityAppPaths(root);
            paths.EnsureDirectoriesCreated();
            var config = new AppConfig { pumpBarrierTimeoutMs = 5000, closeTimeoutMs = 5000, cancelObservationMs = 50 };
            LocalTableChannelFactory? firstFactory = null;
            LocalTableChannelFactory? recoveredFactory = null;
            TableSession? firstSession = null;
            TableSession? recoveredSession = null;
            ITableChannel? firstChannel = null;
            ITableChannel? recoveredChannel = null;
            RawCaptureChannel? recoveredCapture = null;
            var outbox = new InMemoryOutboxStore();
            try
            {
                firstFactory = new LocalTableChannelFactory(config, paths, roomId, "owner-" + Guid.NewGuid().ToString("N"), TimeSpan.FromMinutes(2));
                ITableChannel rawFirst = firstFactory.CreateRoomViewerAsync(LocalRoomViewerRole.Player, 0, CancellationToken.None).GetAwaiter().GetResult();
                firstChannel = rawFirst;
                var firstTicket = rawFirst as ILocalTableTicketSource;
                Assert.IsNotNull(firstTicket);
                firstSession = new TableSession(rawFirst, outbox);
                Task open = firstSession.OpenAsync(new TableSessionOpenRequest(firstTicket!.SessionTicket, LocalTableChannelFactory.ClientBuild), CancellationToken.None);
                yield return WaitTask(() => open.IsCompleted, 30f, "recovery first open");
                Assert.IsFalse(open.IsFaulted, open.Exception?.ToString());
                yield return PumpUntilReady(firstSession, 30f, "recovery first baseline");
                PlayerSnapshot baseline = firstSession.PlayerSnapshot!;
                DecisionView? decision = baseline.PrivateView.Decision;
                Assert.IsNotNull(decision);
                var discard = decision!.Actions[0];
                for (int i = 0; i < decision.Actions.Count; i++)
                {
                    if (decision.Actions[i].ActionType == (uint)ProtocolActionType.DiscardTile)
                    {
                        discard = decision.Actions[i];
                        break;
                    }
                }
                string requestId = "c04-unity-recovery-original";
                BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                    baseline.RoundId,
                    decision.DecisionId,
                    decision.ControlEpoch,
                    ProtocolActionType.DiscardTile,
                    tileIds: discard.TileIds);
                RequestScopeId originalScope = firstSession.Binding!.Scope.ScopeId;
                string originalDigest = CanonicalPayloadDigest.Compute(payload).ToHex();
                var command = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    payload,
                    new RequestId(requestId),
                    originalScope,
                    new StateVersion(baseline.StateVersion),
                    new RoundId(baseline.RoundId),
                    decisionId: new DecisionId(decision.DecisionId),
                    controlEpoch: new ControlEpoch(decision.ControlEpoch));
                Task<CommandSubmission> submitted = firstSession.SubmitAsync(command, CancellationToken.None);
                yield return WaitTask(() => submitted.IsCompleted, 30f, "recovery original submit");
                Assert.IsFalse(submitted.IsFaulted, submitted.Exception?.ToString());
                Assert.IsTrue(submitted.Result.Sent);
                yield return PumpUntilVersion(firstSession, baseline.StateVersion + 1, 30f, "recovery original applied");
                ulong afterSubmitVersion = firstSession.PlayerSnapshot!.StateVersion;
                ulong afterSubmitEventSeq = firstSession.PlayerSnapshot.EventSeq;

                CloseBlocking(firstSession);
                CloseBlocking(firstChannel);
                firstSession = null;
                firstChannel = null;
                firstFactory.CloseRoomAsync(CancellationToken.None).GetAwaiter().GetResult();

                recoveredFactory = new LocalTableChannelFactory(config, paths, roomId, "owner-" + Guid.NewGuid().ToString("N"), TimeSpan.FromMinutes(2));
                ITableChannel rawRecovered = recoveredFactory.CreateRoomViewerAsync(LocalRoomViewerRole.Player, 0, CancellationToken.None).GetAwaiter().GetResult();
                recoveredChannel = rawRecovered;
                recoveredCapture = new RawCaptureChannel(rawRecovered);
                var recoveredTicket = rawRecovered as ILocalTableTicketSource;
                Assert.IsNotNull(recoveredTicket);
                recoveredSession = new TableSession(recoveredCapture, outbox);
                TableReceiptResolvedEventArgs? reconciled = null;
                recoveredSession.ReceiptResolved += (_, args) =>
                {
                    if (args.Command.Key.RequestId.Value == requestId) reconciled = args;
                };
                Task recoverOpen = recoveredSession.OpenAsync(new TableSessionOpenRequest(recoveredTicket!.SessionTicket, LocalTableChannelFactory.ClientBuild), CancellationToken.None);
                yield return WaitTask(() => recoverOpen.IsCompleted, 30f, "recovery reopen");
                Assert.IsFalse(recoverOpen.IsFaulted, recoverOpen.Exception?.ToString());
                yield return PumpUntilReady(recoveredSession, 30f, "recovery reopened baseline");
                for (int i = 0; i < 30 && recoveredSession.PendingCommands.Count > 0; i++)
                {
                    Task reconcile = recoveredSession.ReconcilePendingAsync(CancellationToken.None);
                    yield return WaitTask(() => reconcile.IsCompleted, 15f, "recovery reconcile");
                    Assert.IsFalse(reconcile.IsFaulted, reconcile.Exception?.ToString());
                    Task pump = recoveredSession.PumpOnceAsync(CancellationToken.None);
                    yield return WaitTask(() => pump.IsCompleted, 15f, "recovery reconcile pump");
                }

                Assert.IsEmpty(recoveredSession.PendingCommands);
                Assert.IsNotNull(reconciled);
                Assert.IsTrue(reconciled!.Resolution.FromReceiptLookup);
                Assert.AreEqual(Mahjong.Contracts.ReceiptStatus.Applied, reconciled.Resolution.ReceiptStatus);
                Assert.AreEqual(originalScope.Value, reconciled.Command.Key.ScopeId.Value);
                Assert.AreEqual(requestId, reconciled.Command.Key.RequestId.Value);
                Assert.AreEqual(originalDigest, reconciled.Resolution.CanonicalPayloadSha256);
                Assert.AreEqual(0, recoveredCapture.CommandFrames);
                Assert.Greater(recoveredCapture.ReceiptQueryFrames, 0);
                Assert.AreEqual(afterSubmitVersion, recoveredSession.PlayerSnapshot!.StateVersion);
                Assert.AreEqual(afterSubmitEventSeq, recoveredSession.PlayerSnapshot.EventSeq);

                string evidence = Path.Combine(EvidenceRoot(), "recovery-query");
                Directory.CreateDirectory(evidence);
                File.WriteAllText(Path.Combine(evidence, "recovery-query.json"),
                    "{\"schemaVersion\":1,\"requestId\":\"" + requestId +
                    "\",\"scopeId\":\"" + originalScope.Value +
                    "\",\"canonicalDigest\":\"" + originalDigest +
                    "\",\"stateVersion\":\"" + afterSubmitVersion +
                    "\",\"eventSeq\":\"" + afterSubmitEventSeq +
                    "\",\"commandFrames\":0,\"receiptQueryFrames\":" + recoveredCapture.ReceiptQueryFrames +
                    ",\"fromReceiptLookup\":true,\"zeroDuplicateStateDelta\":true}", new UTF8Encoding(false));
            }
            finally
            {
                CloseBlocking(recoveredSession);
                CloseBlocking(recoveredChannel);
                CloseBlocking(firstSession);
                CloseBlocking(firstChannel);
                try { if (recoveredFactory != null) recoveredFactory.CloseRoomAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
                try { if (firstFactory != null) firstFactory.CloseRoomAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
                LocalTableChannelFactory.DisableC04PrivacyRoomModeForTest();
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            }
        }

        private static IEnumerator PumpUntilReady(TableSession session, float seconds, string label)
        {
            float deadline = Time.realtimeSinceStartup + seconds;
            while (session.State == TableConnectionState.Connecting && Time.realtimeSinceStartup < deadline)
            {
                Task pump = session.PumpOnceAsync(CancellationToken.None);
                yield return WaitTask(() => pump.IsCompleted, 5f, label + " pump");
            }
            Assert.AreEqual(TableConnectionState.Ready, session.State, label + " did not become Ready.");
        }

        private static IEnumerator PumpUntilVersion(TableSession session, ulong version, float seconds, string label)
        {
            float deadline = Time.realtimeSinceStartup + seconds;
            while ((session.PlayerSnapshot?.StateVersion ?? 0UL) < version && Time.realtimeSinceStartup < deadline)
            {
                Task pump = session.PumpOnceAsync(CancellationToken.None);
                yield return WaitTask(() => pump.IsCompleted, 5f, label + " pump");
            }
            Assert.GreaterOrEqual(session.PlayerSnapshot!.StateVersion, version, label);
        }

        private static void CloseBlocking(TableSession? session)
        {
            if (session == null) return;
            try
            {
                Task close = session.CloseAsync("C04_TEST_DONE", CancellationToken.None);
                if (!close.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Session close timed out.");
            }
            catch { }
        }

        private static void CloseBlocking(ITableChannel? channel)
        {
            if (channel == null) return;
            try
            {
                Task close = channel.CloseAsync(new TableChannelCloseReason("C04_TEST_DONE", true), CancellationToken.None);
                if (!close.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Channel close timed out.");
            }
            catch { }
        }

        private static string Sha256Hex(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                var builder = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) builder.Append(hash[i].ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
                return builder.ToString();
            }
        }

        private sealed class RawCaptureChannel : ITableChannel
        {
            private readonly ITableChannel _inner;
            private byte[]? _injectedFrame;

            public RawCaptureChannel(ITableChannel inner) => _inner = inner;

            public bool IsOpen => _inner.IsOpen;
            public byte[]? CapturedPlayerSnapshotFrame { get; private set; }
            public int CommandFrames { get; private set; }
            public int ReceiptQueryFrames { get; private set; }

            public Task OpenAsync(TableChannelConnectRequest request, CancellationToken cancellationToken) =>
                _inner.OpenAsync(request, cancellationToken);

            public Task SendAsync(ReadOnlyMemory<byte> wireEnvelope, CancellationToken cancellationToken)
            {
                WireDecodeResult decoded = WireCodec.Decode(wireEnvelope.ToArray());
                if (decoded.IsOk && decoded.Envelope != null)
                {
                    if (decoded.Envelope.MessageType == WireMessageType.Command) CommandFrames++;
                    if (decoded.Envelope.MessageType == WireMessageType.ReceiptQuery) ReceiptQueryFrames++;
                }
                return _inner.SendAsync(wireEnvelope, cancellationToken);
            }

            public async Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
            {
                byte[]? injected = Interlocked.Exchange(ref _injectedFrame, null);
                if (injected != null) return new ReadOnlyMemory<byte>(injected);
                ReadOnlyMemory<byte> frame = await _inner.ReceiveAsync(cancellationToken);
                WireDecodeResult decoded = WireCodec.Decode(frame.ToArray());
                if (decoded.IsOk && decoded.Envelope != null &&
                    decoded.Envelope.MessageType == WireMessageType.SyncResponse &&
                    decoded.Envelope.SyncResponse?.Target == SyncTarget.PlayerStream)
                {
                    CapturedPlayerSnapshotFrame = frame.ToArray();
                }
                return frame;
            }

            public void InjectNextReceive(byte[] frame)
            {
                if (frame == null) throw new ArgumentNullException(nameof(frame));
                if (Interlocked.CompareExchange(ref _injectedFrame, (byte[])frame.Clone(), null) != null)
                    throw new InvalidOperationException("An injected wire frame is already pending.");
            }

            public Task CloseAsync(TableChannelCloseReason reason, CancellationToken cancellationToken) =>
                _inner.CloseAsync(reason, cancellationToken);
        }

        private static IEnumerator WaitCoordinator(SessionCoordinator? coordinator, float seconds, string label)
        {
            yield return WaitCoordinator(coordinator, null, seconds, label);
        }

        private static IEnumerator WaitCoordinator(SessionCoordinator? coordinator, AppComposition? composition, float seconds, string label)
        {
            float deadline = Time.realtimeSinceStartup + seconds;
            while ((coordinator == null || coordinator.Current.State != TableConnectionState.Ready ||
                    (coordinator.Current.PlayerSnapshot == null && coordinator.Current.PublicSnapshot == null)) &&
                   Time.realtimeSinceStartup < deadline)
            {
                composition?.UpdateRoomViewers();
                yield return null;
            }
            Assert.IsNotNull(coordinator, "No coordinator for " + label);
            Assert.AreEqual(TableConnectionState.Ready, coordinator!.Current.State, label + " did not become Ready.");
        }

        private static IEnumerator WaitTask(Func<bool> predicate, float seconds, string label)
        {
            float deadline = Time.realtimeSinceStartup + seconds;
            while (!predicate() && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(predicate(), "Timed out waiting for " + label);
        }

        private static string EvidenceRoot()
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "-c04PrivacyEvidence");
            if (index >= 0 && index + 1 < args.Length) return args[index + 1];
            string? environmentPath = Environment.GetEnvironmentVariable("MAHJONG_C04_PRIVACY_EVIDENCE");
            if (!string.IsNullOrWhiteSpace(environmentPath)) return environmentPath;
            return Path.Combine(Application.temporaryCachePath, "c04-privacy-evidence-" + Guid.NewGuid().ToString("N"));
        }
    }
}
