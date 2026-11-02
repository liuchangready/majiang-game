#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
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
using PrivateCheatView = Mahjong.Contracts.Wire.PrivateCheatView;
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
            string evidenceRoot = Path.Combine(EvidenceRoot(), "role-switch-raw");
            C04PrivacyProbe.Begin(evidenceRoot);
            AppRoot app = AppRoot.EnsureInitialized(
                new AppConfig { pumpBarrierTimeoutMs = 5000, closeTimeoutMs = 5000, cancelObservationMs = 50 },
                null,
                Path.Combine(Application.persistentDataPath, "c04-privacy-room-" + Guid.NewGuid().ToString("N")));

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
            string root = Path.Combine(Application.persistentDataPath, "c04-delayed-" + Guid.NewGuid().ToString("N"));
            var paths = new UnityAppPaths(root);
            paths.EnsureDirectoriesCreated();
            Directory.CreateDirectory(paths.LocalSaveDirectory);
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
            using var pumpCts = new CancellationTokenSource(TimeSpan.FromSeconds(70));
            Task? zeroPump = null;
            Task? onePump = null;
            try
            {
                ITableChannel rawZero = factory.CreateRoomViewerAsync(LocalRoomViewerRole.Player, 0, CancellationToken.None).GetAwaiter().GetResult();
                seatZeroChannel = rawZero;
                seatZeroCapture = new RawCaptureChannel(rawZero);
                var zeroTicket = rawZero as ILocalTableTicketSource;
                Assert.IsNotNull(zeroTicket);
                seatZero = new TableSession(seatZeroCapture, new InMemoryOutboxStore());
                Task zeroOpen = seatZero.OpenAsync(new TableSessionOpenRequest(zeroTicket!.SessionTicket, LocalTableChannelFactory.ClientBuild), pumpCts.Token);
                yield return WaitTask(() => zeroOpen.IsCompleted, 30f, "delayed seat zero open");
                Assert.IsFalse(zeroOpen.IsFaulted, zeroOpen.Exception?.ToString());
                zeroPump = PumpLoopAsync(seatZero, pumpCts.Token);
                Task zeroReady = WaitReadyAsync(seatZero, pumpCts.Token);
                yield return WaitTask(() => zeroReady.IsCompleted, 30f, "delayed seat zero baseline");
                Assert.IsFalse(zeroReady.IsFaulted, zeroReady.Exception?.ToString());
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
                Task oneOpen = seatOne.OpenAsync(new TableSessionOpenRequest(oneTicket!.SessionTicket, LocalTableChannelFactory.ClientBuild), pumpCts.Token);
                yield return WaitTask(() => oneOpen.IsCompleted, 30f, "delayed seat one open");
                Assert.IsFalse(oneOpen.IsFaulted, oneOpen.Exception?.ToString());
                onePump = PumpLoopAsync(seatOne, pumpCts.Token);
                Task oneReady = WaitReadyAsync(seatOne, pumpCts.Token);
                yield return WaitTask(() => oneReady.IsCompleted, 30f, "delayed seat one baseline");
                Assert.IsFalse(oneReady.IsFaulted, oneReady.Exception?.ToString());
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
                pumpCts.Cancel();
                if (zeroPump != null) { try { zeroPump.GetAwaiter().GetResult(); } catch { } }
                if (onePump != null) { try { onePump.GetAwaiter().GetResult(); } catch { } }
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
            string root = Path.Combine(Application.persistentDataPath, "c04-recovery-" + Guid.NewGuid().ToString("N"));
            string roomId = "c04-recovery-" + Guid.NewGuid().ToString("N");
            var paths = new UnityAppPaths(root);
            paths.EnsureDirectoriesCreated();
            Directory.CreateDirectory(paths.LocalSaveDirectory);
            var config = new AppConfig { pumpBarrierTimeoutMs = 5000, closeTimeoutMs = 5000, cancelObservationMs = 50 };
            using var pumpCts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            Task? firstPump = null;
            Task? recoveredPump = null;
            LocalTableChannelFactory? firstFactory = null;
            LocalTableChannelFactory? recoveredFactory = null;
            TableSession? firstSession = null;
            TableSession? recoveredSession = null;
            ITableChannel? firstChannel = null;
            ITableChannel? recoveredChannel = null;
            RawCaptureChannel? recoveredCapture = null;
            var recoveryOtherChannels = new ITableChannel[3];
            var recoveryOtherSessions = new TableSession[3];
            var recoveryOtherPumps = new List<Task>();
            var outbox = new InMemoryOutboxStore();
            try
            {
                firstFactory = new LocalTableChannelFactory(config, paths, roomId, "owner-" + Guid.NewGuid().ToString("N"), TimeSpan.FromMinutes(2));
                ITableChannel rawFirst = firstFactory.CreateRoomViewerAsync(LocalRoomViewerRole.Player, 0, CancellationToken.None).GetAwaiter().GetResult();
                firstChannel = rawFirst;
                var firstTicket = rawFirst as ILocalTableTicketSource;
                Assert.IsNotNull(firstTicket);
                firstSession = new TableSession(rawFirst, outbox);
                Task open = firstSession.OpenAsync(new TableSessionOpenRequest(firstTicket!.SessionTicket, LocalTableChannelFactory.ClientBuild), pumpCts.Token);
                yield return WaitTask(() => open.IsCompleted, 30f, "recovery first open");
                Assert.IsFalse(open.IsFaulted, open.Exception?.ToString());
                firstPump = PumpLoopAsync(firstSession, pumpCts.Token);
                Task firstReady = WaitReadyAsync(firstSession, pumpCts.Token);
                yield return WaitTask(() => firstReady.IsCompleted, 30f, "recovery first baseline");
                Assert.IsFalse(firstReady.IsFaulted, firstReady.Exception?.ToString());
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
                C04PrivacyProbe.Begin(Path.Combine(EvidenceRoot(), "recovery-raw"));
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
                Task recoverOpen = recoveredSession.OpenAsync(new TableSessionOpenRequest(recoveredTicket!.SessionTicket, LocalTableChannelFactory.ClientBuild), pumpCts.Token);
                yield return WaitTask(() => recoverOpen.IsCompleted, 30f, "recovery reopen");
                Assert.IsFalse(recoverOpen.IsFaulted, recoverOpen.Exception?.ToString());
                recoveredPump = PumpLoopAsync(recoveredSession, pumpCts.Token);
                Task recoveredReady = WaitReadyAsync(recoveredSession, pumpCts.Token);
                yield return WaitTask(() => recoveredReady.IsCompleted, 30f, "recovery reopened baseline");
                Assert.IsFalse(recoveredReady.IsFaulted, recoveredReady.Exception?.ToString());
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

                for (int seat = 1; seat < 4; seat++)
                {
                    recoveryOtherChannels[seat - 1] = recoveredFactory.CreateRoomViewerAsync(LocalRoomViewerRole.Player, seat, CancellationToken.None).GetAwaiter().GetResult();
                    var otherTicket = recoveryOtherChannels[seat - 1] as ILocalTableTicketSource;
                    Assert.IsNotNull(otherTicket);
                    recoveryOtherSessions[seat - 1] = new TableSession(recoveryOtherChannels[seat - 1], new InMemoryOutboxStore());
                    Task otherOpen = recoveryOtherSessions[seat - 1].OpenAsync(
                        new TableSessionOpenRequest(otherTicket!.SessionTicket, LocalTableChannelFactory.ClientBuild),
                        CancellationToken.None);
                    yield return WaitTask(() => otherOpen.IsCompleted, 30f, "recovery positive seat " + seat + " open");
                    Assert.IsFalse(otherOpen.IsFaulted, otherOpen.Exception?.ToString());
                    yield return PumpUntilReady(recoveryOtherSessions[seat - 1], 30f, "recovery positive seat " + seat);
                    for (int i = 0; i < 16 && recoveryOtherSessions[seat - 1].PlayerSnapshot == null; i++)
                    {
                        Task pump = recoveryOtherSessions[seat - 1].PumpOnceAsync(CancellationToken.None);
                        yield return WaitTask(() => pump.IsCompleted, 10f, "recovery positive seat " + seat + " snapshot");
                    }
                    Assert.IsNotNull(recoveryOtherSessions[seat - 1].PlayerSnapshot);
                }

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
                Assert.Greater(C04PrivacyProbe.CapturedFrameCount, 0);
                string rawSha = C04PrivacyProbe.Seal();
                Assert.AreEqual(64, rawSha.Length);
                Debug.Log("C04_PRIVACY_RAW scene=recovery frames=" + C04PrivacyProbe.CapturedFrameCount + " sha256=" + rawSha);
            }
            finally
            {
                pumpCts.Cancel();
                if (firstPump != null) { try { firstPump.GetAwaiter().GetResult(); } catch { } }
                if (recoveredPump != null) { try { recoveredPump.GetAwaiter().GetResult(); } catch { } }
                C04PrivacyProbe.ResetForTest();
                for (int seat = 0; seat < 3; seat++)
                {
                    CloseBlocking(recoveryOtherSessions[seat]);
                    CloseBlocking(recoveryOtherChannels[seat]);
                }
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

        [UnityTest]
        public IEnumerator RealRoom_ClosedPlayerToSpectatorToNewPlayerReceivePathRejectsOldRaw()
        {
            string root = Path.Combine(Application.persistentDataPath, "c04-closed-path-" + Guid.NewGuid().ToString("N"));
            string roomId = "c04-closed-path-" + Guid.NewGuid().ToString("N");
            try
            {
                Task<bool> task = Task.Run(() => RunClosedBindingRawPathAsync(root, roomId));
                yield return WaitTask(() => task.IsCompleted, 90f, "closed player/spectator/player raw path");
                Assert.IsFalse(task.IsFaulted, task.Exception?.ToString());
                Assert.IsTrue(task.Result);
                Assert.Greater(C04PrivacyProbe.CapturedFrameCount, 0);
                string rawSha = C04PrivacyProbe.Seal();
                Assert.AreEqual(64, rawSha.Length);
                string rawDirectory = Path.Combine(EvidenceRoot(), "closed-binding-raw");
                Directory.CreateDirectory(rawDirectory);
                File.Copy(Path.Combine(root, "closed-binding-raw", "raw-frames.json"), Path.Combine(rawDirectory, "raw-frames.json"), true);
                Debug.Log("C04_PRIVACY_RAW scene=closed-binding frames=" + C04PrivacyProbe.CapturedFrameCount + " sha256=" + rawSha);
            }
            finally
            {
                C04PrivacyProbe.ResetForTest();
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            }
        }

        [UnityTest]
        public IEnumerator RealRoom_LegalSleeveAndFeeOwnershipAcrossFourSeatsAndSpectator()
        {
            string root = Path.Combine(Application.persistentDataPath, "c04-owner-" + Guid.NewGuid().ToString("N"));
            string roomId = "c04-owner-" + Guid.NewGuid().ToString("N");
            try
            {
            Task<FourOwnerEvidence> task = Task.Run(() => RunFourOwnerPrivacyScenarioAsync(root, roomId));
            yield return WaitTask(() => task.IsCompleted, 180f, "four-owner privacy scenario");
            Assert.IsFalse(task.IsFaulted, task.Exception?.ToString());
            FourOwnerEvidence evidence = task.Result;
            Assert.AreEqual(4, evidence.SeatCount);
            Assert.AreEqual(evidence.Version, evidence.SpectatorVersion);
            Assert.IsTrue(evidence.TargetClosedTiles > 0);
            Assert.IsTrue(evidence.TargetLedgerEntries > 0);
            Assert.IsTrue(evidence.TargetCheatPoints < 120);
            Assert.IsFalse(string.IsNullOrEmpty(evidence.TargetOfferId));
                Assert.IsTrue(evidence.SpectatorHasPublic);
                Assert.IsFalse(evidence.SpectatorHasPlayer);
                Assert.Greater(C04PrivacyProbe.CapturedFrameCount, 0);
                string rawSha = C04PrivacyProbe.Seal();
                Assert.AreEqual(64, rawSha.Length);
                Debug.Log("C04_PRIVACY_RAW scene=four-owner frames=" + C04PrivacyProbe.CapturedFrameCount + " sha256=" + rawSha);
                string rawDirectory = Path.Combine(EvidenceRoot(), "four-owner-raw");
                Directory.CreateDirectory(rawDirectory);
                File.Copy(Path.Combine(root, "four-owner-raw", "raw-frames.json"), Path.Combine(rawDirectory, "raw-frames.json"), true);
            string directory = Path.Combine(EvidenceRoot(), "four-owner");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "four-owner.json"),
                "{\"schemaVersion\":1,\"version\":\"" + evidence.Version +
                "\",\"seats\":" + evidence.SeatCount +
                ",\"targetClosedTiles\":" + evidence.TargetClosedTiles +
                ",\"targetLedgerEntries\":" + evidence.TargetLedgerEntries +
                ",\"targetCheatPoints\":" + evidence.TargetCheatPoints +
                ",\"targetOfferId\":\"" + evidence.TargetOfferId +
                "\",\"spectatorVersion\":\"" + evidence.SpectatorVersion +
                "\",\"spectatorHasPublic\":" + evidence.SpectatorHasPublic.ToString().ToLowerInvariant() +
                ",\"spectatorHasPlayer\":" + evidence.SpectatorHasPlayer.ToString().ToLowerInvariant() + "}",
                new UTF8Encoding(false));
            }
            finally
            {
                C04PrivacyProbe.ResetForTest();
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            }
        }

        [UnityTest]
        public IEnumerator RealRoom_OpenDecisionDeadlineContinuesAfterPlayerClose()
        {
            string root = Path.Combine(Application.persistentDataPath, "c04-deadline-" + Guid.NewGuid().ToString("N"));
            string roomId = "c04-deadline-" + Guid.NewGuid().ToString("N");
            Task<ReleaseEvidence> task = Task.Run(() => RunOpenDeadlineScenarioAsync(root, roomId));
            yield return WaitTask(() => task.IsCompleted, 75f, "open deadline after player close");
            Assert.IsFalse(task.IsFaulted, task.Exception?.ToString());
            ReleaseEvidence evidence = task.Result;
            Assert.IsTrue(evidence.AdvancedWithoutPlayerBinding, "Open decision deadline did not continue after player close.");
            Assert.Greater(evidence.AfterVersion, evidence.ReleaseVersion);
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }

        [UnityTest]
        public IEnumerator RealRoom_ReleasedSeatAiAdvancesAfterPlayerCloseWithSpectatorOnly()
        {
            string root = Path.Combine(Application.persistentDataPath, "c04-release-" + Guid.NewGuid().ToString("N"));
            string roomId = "c04-release-" + Guid.NewGuid().ToString("N");
            Task<ReleaseEvidence> task = Task.Run(() => RunReleasedControlScenarioAsync(root, roomId));
            yield return WaitTask(() => task.IsCompleted, 90f, "released-seat authority scenario");
            Assert.IsFalse(task.IsFaulted, task.Exception?.ToString());
            ReleaseEvidence evidence = task.Result;
            Assert.IsTrue(evidence.AdvancedWithoutPlayerBinding, "Released AI did not advance after the player binding closed.");
            Assert.Greater(evidence.AfterVersion, evidence.ReleaseVersion);
            Assert.IsTrue(evidence.Claimed, "ClaimControl did not restore human control.");
            Assert.IsTrue(evidence.StaleDecisionRejected, "A stale control epoch decision was not rejected.");
            string directory = Path.Combine(EvidenceRoot(), "released-control");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "released-control.json"),
                "{\"schemaVersion\":1,\"releaseVersion\":\"" + evidence.ReleaseVersion +
                "\",\"afterVersion\":\"" + evidence.AfterVersion +
                "\",\"claimEpoch\":\"" + evidence.ClaimEpoch +
                "\",\"advancedWithoutPlayerBinding\":" + evidence.AdvancedWithoutPlayerBinding.ToString().ToLowerInvariant() +
                ",\"claimed\":" + evidence.Claimed.ToString().ToLowerInvariant() +
                ",\"staleDecisionRejected\":" + evidence.StaleDecisionRejected.ToString().ToLowerInvariant() + "}", new UTF8Encoding(false));
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }

        private static async Task<bool> RunClosedBindingRawPathAsync(string root, string roomId)
        {
            C04PrivacyProbe.Begin(Path.Combine(root, "closed-binding-raw"));
            LocalTableChannelFactory.EnableC04PrivacyRoomMode();
            var paths = new UnityAppPaths(root);
            paths.EnsureDirectoriesCreated();
            Directory.CreateDirectory(paths.LocalSaveDirectory);
            var config = new AppConfig { pumpBarrierTimeoutMs = 5000, closeTimeoutMs = 5000, cancelObservationMs = 50 };
            var factory = new LocalTableChannelFactory(config, paths, roomId, "owner-" + Guid.NewGuid().ToString("N"), TimeSpan.FromMinutes(2));
            using var pumpCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            ITableChannel? firstInner = null;
            RawCaptureChannel? firstChannel = null;
            TableSession? first = null;
            ITableChannel? spectatorChannel = null;
            TableSession? spectator = null;
            ITableChannel? reopenedInner = null;
            RawCaptureChannel? reopenedChannel = null;
            TableSession? reopened = null;
            var otherChannels = new ITableChannel[3];
            var otherSessions = new TableSession[3];
            var otherPumps = new List<Task>();
            Task? firstPump = null;
            Task? spectatorPump = null;
            Task? reopenedPump = null;
            try
            {
                firstInner = await factory.CreateRoomViewerAsync(LocalRoomViewerRole.Player, 0, CancellationToken.None).ConfigureAwait(false);
                firstChannel = new RawCaptureChannel(firstInner);
                var firstTicket = firstInner as ILocalTableTicketSource
                    ?? throw new InvalidOperationException("The first player channel did not expose its ticket.");
                first = new TableSession(firstChannel, new InMemoryOutboxStore());
                await first.OpenAsync(new TableSessionOpenRequest(firstTicket.SessionTicket, LocalTableChannelFactory.ClientBuild), pumpCts.Token).ConfigureAwait(false);
                firstPump = PumpLoopAsync(first, pumpCts.Token);
                await WaitReadyAsync(first, pumpCts.Token).ConfigureAwait(false);
                byte[] oldFrame = firstChannel.CapturedPlayerSnapshotFrame
                    ?? throw new InvalidOperationException("No old private frame was captured.");
                string oldSession = first.Binding!.SessionId.Value;

                await first.CloseAsync("ROLE_SWITCH", CancellationToken.None).ConfigureAwait(false);
                await firstChannel.CloseAsync(new TableChannelCloseReason("ROLE_SWITCH", true), CancellationToken.None).ConfigureAwait(false);
                first = null;
                firstChannel = null;
                firstInner = null;

                spectatorChannel = await factory.CreateRoomViewerAsync(LocalRoomViewerRole.Spectator, -1, CancellationToken.None).ConfigureAwait(false);
                var spectatorTicket = spectatorChannel as ILocalTableTicketSource
                    ?? throw new InvalidOperationException("The spectator channel did not expose its ticket.");
                spectator = new TableSession(spectatorChannel, new InMemoryOutboxStore());
                await spectator.OpenAsync(new TableSessionOpenRequest(spectatorTicket.SessionTicket, LocalTableChannelFactory.ClientBuild), pumpCts.Token).ConfigureAwait(false);
                spectatorPump = PumpLoopAsync(spectator, pumpCts.Token);
                await WaitReadyAsync(spectator, pumpCts.Token).ConfigureAwait(false);
                if (spectator.PlayerSnapshot != null) throw new InvalidOperationException("Spectator exposed a player snapshot.");

                reopenedInner = await factory.CreateRoomViewerAsync(LocalRoomViewerRole.Player, 0, CancellationToken.None).ConfigureAwait(false);
                reopenedChannel = new RawCaptureChannel(reopenedInner);
                var reopenedTicket = reopenedInner as ILocalTableTicketSource
                    ?? throw new InvalidOperationException("The reopened player channel did not expose its ticket.");
                reopened = new TableSession(reopenedChannel, new InMemoryOutboxStore());
                await reopened.OpenAsync(new TableSessionOpenRequest(reopenedTicket.SessionTicket, LocalTableChannelFactory.ClientBuild), pumpCts.Token).ConfigureAwait(false);
                reopenedPump = PumpLoopAsync(reopened, pumpCts.Token);
                await WaitReadyAsync(reopened, pumpCts.Token).ConfigureAwait(false);
                if (reopened.Binding!.SessionId.Value == oldSession) throw new InvalidOperationException("Reopened player reused the old session.");
                for (int seat = 1; seat < 4; seat++)
                {
                    otherChannels[seat - 1] = await factory.CreateRoomViewerAsync(LocalRoomViewerRole.Player, seat, CancellationToken.None).ConfigureAwait(false);
                    var otherTicket = otherChannels[seat - 1] as ILocalTableTicketSource
                        ?? throw new InvalidOperationException("A positive-control player channel did not expose its ticket.");
                    otherSessions[seat - 1] = new TableSession(otherChannels[seat - 1], new InMemoryOutboxStore());
                    await otherSessions[seat - 1].OpenAsync(new TableSessionOpenRequest(otherTicket.SessionTicket, LocalTableChannelFactory.ClientBuild), pumpCts.Token).ConfigureAwait(false);
                    otherPumps.Add(PumpLoopAsync(otherSessions[seat - 1], pumpCts.Token));
                    await WaitReadyAsync(otherSessions[seat - 1], pumpCts.Token).ConfigureAwait(false);
                }
                string currentTiles = string.Join(",", reopened.PlayerSnapshot!.PrivateView.ClosedTiles);
                reopenedChannel.InjectNextReceive(oldFrame);
                Task pump = reopened.PumpOnceAsync(CancellationToken.None);
                if (!pump.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Injected old raw receive did not complete.");
                PlayerSnapshot after = reopened.PlayerSnapshot
                    ?? throw new InvalidOperationException("No snapshot after old raw injection.");
                if (after.SessionId != reopened.Binding.SessionId.Value) throw new InvalidOperationException("Old session id replaced the new binding.");
                if (after.PrivateView.Seat != 0) throw new InvalidOperationException("Old raw changed the owner seat.");
                if (string.Join(",", after.PrivateView.ClosedTiles) != currentTiles) throw new InvalidOperationException("Old raw changed the reopened hand.");
                return true;
            }
            finally
            {
                pumpCts.Cancel();
                if (firstPump != null) { try { await firstPump.ConfigureAwait(false); } catch { } }
                if (spectatorPump != null) { try { await spectatorPump.ConfigureAwait(false); } catch { } }
                if (reopenedPump != null) { try { await reopenedPump.ConfigureAwait(false); } catch { } }
                try { await Task.WhenAll(otherPumps).ConfigureAwait(false); } catch { }
                if (first != null) try { await first.CloseAsync("C04_TEST_DONE", CancellationToken.None).ConfigureAwait(false); } catch { }
                if (firstChannel != null) try { await firstChannel.CloseAsync(new TableChannelCloseReason("C04_TEST_DONE", true), CancellationToken.None).ConfigureAwait(false); } catch { }
                if (spectator != null) try { await spectator.CloseAsync("C04_TEST_DONE", CancellationToken.None).ConfigureAwait(false); } catch { }
                if (spectatorChannel != null) try { await spectatorChannel.CloseAsync(new TableChannelCloseReason("C04_TEST_DONE", true), CancellationToken.None).ConfigureAwait(false); } catch { }
                if (reopened != null) try { await reopened.CloseAsync("C04_TEST_DONE", CancellationToken.None).ConfigureAwait(false); } catch { }
                if (reopenedChannel != null) try { await reopenedChannel.CloseAsync(new TableChannelCloseReason("C04_TEST_DONE", true), CancellationToken.None).ConfigureAwait(false); } catch { }
                for (int seat = 0; seat < 3; seat++)
                {
                    if (otherSessions[seat] != null) try { await otherSessions[seat].CloseAsync("C04_TEST_DONE", CancellationToken.None).ConfigureAwait(false); } catch { }
                    if (otherChannels[seat] != null) try { await otherChannels[seat].CloseAsync(new TableChannelCloseReason("C04_TEST_DONE", true), CancellationToken.None).ConfigureAwait(false); } catch { }
                }
                try { await factory.CloseRoomAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            }
        }

        private static async Task<FourOwnerEvidence> RunFourOwnerPrivacyScenarioAsync(string root, string roomId)
        {
            C04PrivacyProbe.Begin(Path.Combine(root, "four-owner-raw"));
            LocalTableChannelFactory.EnableC04PrivacyRoomMode();
            var paths = new UnityAppPaths(root);
            paths.EnsureDirectoriesCreated();
            Directory.CreateDirectory(paths.LocalSaveDirectory);
            var config = new AppConfig { pumpBarrierTimeoutMs = 5000, closeTimeoutMs = 5000, cancelObservationMs = 50 };
            var factory = new LocalTableChannelFactory(config, paths, roomId, "owner-" + Guid.NewGuid().ToString("N"), TimeSpan.FromMinutes(2));
            var channels = new ITableChannel[4];
            var sessions = new TableSession[4];
            ITableChannel? spectatorChannel = null;
            TableSession? spectator = null;
            using var pumpCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var pumps = new List<Task>();
            Task? driver = null;
            using var driverCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            try
            {
                for (int seat = 0; seat < 4; seat++)
                {
                    channels[seat] = await factory.CreateRoomViewerAsync(LocalRoomViewerRole.Player, seat, CancellationToken.None).ConfigureAwait(false);
                    var ticket = channels[seat] as ILocalTableTicketSource
                        ?? throw new InvalidOperationException("The real room channel did not expose its ticket.");
                    sessions[seat] = new TableSession(channels[seat], new InMemoryOutboxStore());
                    await sessions[seat].OpenAsync(new TableSessionOpenRequest(ticket.SessionTicket, LocalTableChannelFactory.ClientBuild), pumpCts.Token).ConfigureAwait(false);
                    pumps.Add(PumpLoopAsync(sessions[seat], pumpCts.Token));
                }
                foreach (TableSession session in sessions) await WaitReadyAsync(session, pumpCts.Token).ConfigureAwait(false);

                spectatorChannel = await factory.CreateRoomViewerAsync(LocalRoomViewerRole.Spectator, -1, CancellationToken.None).ConfigureAwait(false);
                var spectatorTicket = spectatorChannel as ILocalTableTicketSource
                    ?? throw new InvalidOperationException("The spectator channel did not expose its ticket.");
                spectator = new TableSession(spectatorChannel, new InMemoryOutboxStore());
                await spectator.OpenAsync(new TableSessionOpenRequest(spectatorTicket.SessionTicket, LocalTableChannelFactory.ClientBuild), pumpCts.Token).ConfigureAwait(false);
                pumps.Add(PumpLoopAsync(spectator, pumpCts.Token));
                await WaitReadyAsync(spectator, pumpCts.Token).ConfigureAwait(false);

                driver = DriveOtherSeatsAsync(sessions, 0, driverCts.Token);
                TargetPrivacyFacts facts = await RunTargetSleeveAsync(sessions[0], pumpCts.Token).ConfigureAwait(false);
                driverCts.Cancel();
                try { await driver.ConfigureAwait(false); } catch (OperationCanceledException) { }

                ulong common = facts.SleeveStateVersion;
                foreach (TableSession session in sessions) common = Math.Max(common, session.PlayerSnapshot?.StateVersion ?? 0UL);
                common = Math.Max(common, spectator.PublicSnapshot?.StateVersion ?? 0UL);
                for (int i = 0; i < 400; i++)
                {
                    bool all = true;
                    foreach (TableSession session in sessions) all &= (session.PlayerSnapshot?.StateVersion ?? 0UL) == common;
                    all &= (spectator.PublicSnapshot?.StateVersion ?? 0UL) == common;
                    if (all) break;
                    await Task.Delay(20, pumpCts.Token).ConfigureAwait(false);
                }

                var snapshots = new PlayerSnapshot[4];
                for (int seat = 0; seat < 4; seat++)
                {
                    snapshots[seat] = sessions[seat].PlayerSnapshot
                        ?? throw new InvalidOperationException("No player snapshot for seat " + seat);
                    if (snapshots[seat].PrivateView.Seat != seat) throw new InvalidOperationException("Owner seat mismatch.");
                    if (snapshots[seat].PrivateView.ClosedTiles.Count == 0) throw new InvalidOperationException("Owner hand is empty.");
                    if (snapshots[seat].StateVersion != common) throw new InvalidOperationException("Owner state version is not common.");
                }
                var publicSnapshot = spectator.PublicSnapshot
                    ?? throw new InvalidOperationException("No spectator public snapshot.");
                if (publicSnapshot.StateVersion != common) throw new InvalidOperationException("Spectator version is not common.");
                if (spectator.PlayerSnapshot != null) throw new InvalidOperationException("Spectator exposed a player snapshot.");
                PrivateCheatView? targetCheat = snapshots[0].PrivateView.Cheat;
                if (targetCheat?.SleeveBloom == null) throw new InvalidOperationException("Target SleeveBloom offer is empty.");
                if (targetCheat.LedgerEntries.Count == 0) throw new InvalidOperationException("Target private ledger is empty.");

                return new FourOwnerEvidence
                {
                    Version = common,
                    SeatCount = 4,
                    TargetClosedTiles = snapshots[0].PrivateView.ClosedTiles.Count,
                    TargetLedgerEntries = targetCheat.LedgerEntries.Count,
                    TargetCheatPoints = targetCheat.CheatPoints,
                    TargetOfferId = targetCheat.SleeveBloom.OfferId,
                    SpectatorVersion = publicSnapshot.StateVersion,
                    SpectatorHasPublic = true,
                    SpectatorHasPlayer = false
                };
            }
            finally
            {
                driverCts.Cancel();
                if (driver != null) { try { await driver.ConfigureAwait(false); } catch { } }
                pumpCts.Cancel();
                try { await Task.WhenAll(pumps).ConfigureAwait(false); } catch { }
                if (spectator != null) try { await spectator.CloseAsync("C04_TEST_DONE", CancellationToken.None).ConfigureAwait(false); } catch { }
                if (spectatorChannel != null) try { await spectatorChannel.CloseAsync(new TableChannelCloseReason("C04_TEST_DONE", true), CancellationToken.None).ConfigureAwait(false); } catch { }
                for (int seat = 0; seat < 4; seat++)
                {
                    if (sessions[seat] != null) try { await sessions[seat].CloseAsync("C04_TEST_DONE", CancellationToken.None).ConfigureAwait(false); } catch { }
                    if (channels[seat] != null) try { await channels[seat].CloseAsync(new TableChannelCloseReason("C04_TEST_DONE", true), CancellationToken.None).ConfigureAwait(false); } catch { }
                }
                try { await factory.CloseRoomAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
                LocalTableChannelFactory.DisableC04PrivacyRoomModeForTest();
            }
        }

        private static async Task<ReleaseEvidence> RunOpenDeadlineScenarioAsync(string root, string roomId)
        {
            LocalTableChannelFactory.DisableC04PrivacyRoomModeForTest();
            var paths = new UnityAppPaths(root);
            paths.EnsureDirectoriesCreated();
            Directory.CreateDirectory(paths.LocalSaveDirectory);
            var config = new AppConfig { pumpBarrierTimeoutMs = 5000, closeTimeoutMs = 5000, cancelObservationMs = 50 };
            var factory = new LocalTableChannelFactory(config, paths, roomId, "owner-" + Guid.NewGuid().ToString("N"), TimeSpan.FromMinutes(2));
            using var pumpCts = new CancellationTokenSource(TimeSpan.FromSeconds(65));
            ITableChannel? channel = null;
            TableSession? session = null;
            ITableChannel? spectatorChannel = null;
            TableSession? spectator = null;
            Task? playerPump = null;
            Task? spectatorPump = null;
            try
            {
                channel = await factory.CreateRoomViewerAsync(LocalRoomViewerRole.Player, 0, CancellationToken.None).ConfigureAwait(false);
                var ticket = channel as ILocalTableTicketSource
                    ?? throw new InvalidOperationException("The deadline player channel did not expose its ticket.");
                session = new TableSession(channel, new InMemoryOutboxStore());
                await session.OpenAsync(new TableSessionOpenRequest(ticket.SessionTicket, LocalTableChannelFactory.ClientBuild), pumpCts.Token).ConfigureAwait(false);
                playerPump = PumpLoopAsync(session, pumpCts.Token);
                await WaitReadyAsync(session, pumpCts.Token).ConfigureAwait(false);
                PlayerSnapshot baseline = session.PlayerSnapshot
                    ?? throw new InvalidOperationException("No deadline baseline.");
                if (baseline.PrivateView.Decision == null) throw new InvalidOperationException("The open-deadline scenario needs a live decision.");
                ulong beforeVersion = baseline.StateVersion;

                await session.CloseAsync("DEADLINE_CLOSE", CancellationToken.None).ConfigureAwait(false);
                await channel.CloseAsync(new TableChannelCloseReason("DEADLINE_CLOSE", true), CancellationToken.None).ConfigureAwait(false);
                session = null;
                channel = null;

                spectatorChannel = await factory.CreateRoomViewerAsync(LocalRoomViewerRole.Spectator, -1, CancellationToken.None).ConfigureAwait(false);
                var spectatorTicket = spectatorChannel as ILocalTableTicketSource
                    ?? throw new InvalidOperationException("The deadline spectator channel did not expose its ticket.");
                spectator = new TableSession(spectatorChannel, new InMemoryOutboxStore());
                await spectator.OpenAsync(new TableSessionOpenRequest(spectatorTicket.SessionTicket, LocalTableChannelFactory.ClientBuild), pumpCts.Token).ConfigureAwait(false);
                spectatorPump = PumpLoopAsync(spectator, pumpCts.Token);
                await WaitReadyAsync(spectator, pumpCts.Token).ConfigureAwait(false);
                ulong after = beforeVersion;
                for (int i = 0; i < 1200; i++)
                {
                    after = spectator.PublicSnapshot?.StateVersion ?? after;
                    if (after > beforeVersion) break;
                    await Task.Delay(25, pumpCts.Token).ConfigureAwait(false);
                }
                return new ReleaseEvidence
                {
                    ReleaseVersion = beforeVersion,
                    AfterVersion = after,
                    AdvancedWithoutPlayerBinding = after > beforeVersion
                };
            }
            finally
            {
                pumpCts.Cancel();
                if (playerPump != null) { try { await playerPump.ConfigureAwait(false); } catch { } }
                if (spectatorPump != null) { try { await spectatorPump.ConfigureAwait(false); } catch { } }
                if (session != null) try { await session.CloseAsync("C04_TEST_DONE", CancellationToken.None).ConfigureAwait(false); } catch { }
                if (channel != null) try { await channel.CloseAsync(new TableChannelCloseReason("C04_TEST_DONE", true), CancellationToken.None).ConfigureAwait(false); } catch { }
                if (spectator != null) try { await spectator.CloseAsync("C04_TEST_DONE", CancellationToken.None).ConfigureAwait(false); } catch { }
                if (spectatorChannel != null) try { await spectatorChannel.CloseAsync(new TableChannelCloseReason("C04_TEST_DONE", true), CancellationToken.None).ConfigureAwait(false); } catch { }
                try { await factory.CloseRoomAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            }
        }

        private static async Task<ReleaseEvidence> RunReleasedControlScenarioAsync(string root, string roomId)
        {
            LocalTableChannelFactory.DisableC04PrivacyRoomModeForTest();
            var paths = new UnityAppPaths(root);
            paths.EnsureDirectoriesCreated();
            Directory.CreateDirectory(paths.LocalSaveDirectory);
            var config = new AppConfig { pumpBarrierTimeoutMs = 5000, closeTimeoutMs = 5000, cancelObservationMs = 50 };
            var factory = new LocalTableChannelFactory(config, paths, roomId, "owner-" + Guid.NewGuid().ToString("N"), TimeSpan.FromMinutes(2));
            using var pumpCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            ITableChannel? channel = null;
            TableSession? session = null;
            ITableChannel? spectatorChannel = null;
            TableSession? spectator = null;
            ITableChannel? claimedChannel = null;
            TableSession? claimed = null;
            Task? playerPump = null;
            Task? spectatorPump = null;
            Task? claimedPump = null;
            try
            {
                channel = await factory.CreateRoomViewerAsync(LocalRoomViewerRole.Player, 0, CancellationToken.None).ConfigureAwait(false);
                var ticket = channel as ILocalTableTicketSource
                    ?? throw new InvalidOperationException("The player channel did not expose its ticket.");
                session = new TableSession(channel, new InMemoryOutboxStore());
                await session.OpenAsync(new TableSessionOpenRequest(ticket.SessionTicket, LocalTableChannelFactory.ClientBuild), pumpCts.Token).ConfigureAwait(false);
                playerPump = PumpLoopAsync(session, pumpCts.Token);
                await WaitReadyAsync(session, pumpCts.Token).ConfigureAwait(false);
                PlayerSnapshot before = session.PlayerSnapshot
                    ?? throw new InvalidOperationException("No baseline player snapshot.");
                ulong epoch = before.PrivateView.Control?.ControlEpoch ?? 0UL;
                BusinessPayload payload = CommandPayloadFactory.ReleaseControl(before.RoundId, epoch);
                var command = new ProtocolCommand(
                    CommandKind.ReleaseControl,
                    payload,
                    new RequestId("unity-release-" + Guid.NewGuid().ToString("N")),
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(before.StateVersion),
                    new RoundId(before.RoundId),
                    decisionId: null,
                    controlEpoch: new ControlEpoch(epoch));
                PlayerSnapshot released = await SubmitAndWaitAsync(session, command, before.StateVersion, pumpCts.Token).ConfigureAwait(false);
                ulong releaseVersion = released.StateVersion;

                await session.CloseAsync("RELEASE_CLOSE", CancellationToken.None).ConfigureAwait(false);
                await channel.CloseAsync(new TableChannelCloseReason("RELEASE_CLOSE", true), CancellationToken.None).ConfigureAwait(false);
                session = null;
                channel = null;

                spectatorChannel = await factory.CreateRoomViewerAsync(LocalRoomViewerRole.Spectator, -1, CancellationToken.None).ConfigureAwait(false);
                var spectatorTicket = spectatorChannel as ILocalTableTicketSource
                    ?? throw new InvalidOperationException("The spectator channel did not expose its ticket.");
                spectator = new TableSession(spectatorChannel, new InMemoryOutboxStore());
                await spectator.OpenAsync(new TableSessionOpenRequest(spectatorTicket.SessionTicket, LocalTableChannelFactory.ClientBuild), pumpCts.Token).ConfigureAwait(false);
                spectatorPump = PumpLoopAsync(spectator, pumpCts.Token);
                await WaitReadyAsync(spectator, pumpCts.Token).ConfigureAwait(false);
                ulong after = releaseVersion;
                for (int i = 0; i < 600; i++)
                {
                    after = spectator.PublicSnapshot?.StateVersion ?? after;
                    if (after > releaseVersion) break;
                    await Task.Delay(25, pumpCts.Token).ConfigureAwait(false);
                }

                claimedChannel = await factory.CreateRoomViewerAsync(LocalRoomViewerRole.Player, 0, CancellationToken.None).ConfigureAwait(false);
                var claimedTicket = claimedChannel as ILocalTableTicketSource
                    ?? throw new InvalidOperationException("The claimed player channel did not expose its ticket.");
                claimed = new TableSession(claimedChannel, new InMemoryOutboxStore());
                await claimed.OpenAsync(new TableSessionOpenRequest(claimedTicket.SessionTicket, LocalTableChannelFactory.ClientBuild), pumpCts.Token).ConfigureAwait(false);
                claimedPump = PumpLoopAsync(claimed, pumpCts.Token);
                await WaitReadyAsync(claimed, pumpCts.Token).ConfigureAwait(false);
                PlayerSnapshot claimBase = claimed.PlayerSnapshot
                    ?? throw new InvalidOperationException("No claimed baseline.");
                ulong claimEpoch = claimBase.PrivateView.Control?.ControlEpoch ?? 0UL;
                var claimCommand = new ProtocolCommand(
                    CommandKind.ClaimControl,
                    CommandPayloadFactory.ClaimControl(claimBase.RoundId, claimEpoch),
                    new RequestId("unity-claim-" + Guid.NewGuid().ToString("N")),
                    claimed.Binding!.Scope.ScopeId,
                    new StateVersion(claimBase.StateVersion),
                    new RoundId(claimBase.RoundId),
                    decisionId: null,
                    controlEpoch: new ControlEpoch(claimEpoch));
                TableReceiptResolvedEventArgs claimReceipt = await SubmitExpectReceiptAsync(claimed, claimCommand, pumpCts.Token).ConfigureAwait(false);
                if (claimReceipt.Resolution.ReceiptStatus != Mahjong.Contracts.ReceiptStatus.Applied &&
                    claimReceipt.Resolution.ReceiptStatus != Mahjong.Contracts.ReceiptStatus.Recorded)
                    throw new InvalidOperationException("ClaimControl was rejected: " + claimReceipt.Resolution.ReasonCode);
                PlayerSnapshot claimedSnapshot = claimed.PlayerSnapshot
                    ?? throw new InvalidOperationException("No snapshot after ClaimControl.");
                for (int i = 0; i < 200; i++)
                {
                    claimedSnapshot = claimed.PlayerSnapshot ?? claimedSnapshot;
                    if ((claimedSnapshot.PrivateView.Control?.ControlEpoch ?? 0UL) > claimEpoch &&
                        claimedSnapshot.PrivateView.Control?.ControlledByComputer != true) break;
                    await Task.Delay(20, pumpCts.Token).ConfigureAwait(false);
                }
                ulong claimedEpoch = claimedSnapshot.PrivateView.Control?.ControlEpoch ?? 0UL;
                if (claimedEpoch <= claimEpoch) throw new InvalidOperationException("ClaimControl did not advance the control epoch.");
                if (claimedSnapshot.PrivateView.Control?.ControlledByComputer == true)
                    throw new InvalidOperationException("ClaimControl did not restore human control.");

                var staleCommand = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    CommandPayloadFactory.SubmitAction(claimedSnapshot.RoundId, "unity-stale", 0UL, ProtocolActionType.Pass, tileIds: null),
                    new RequestId("unity-stale-" + Guid.NewGuid().ToString("N")),
                    claimed.Binding.Scope.ScopeId,
                    new StateVersion(claimedSnapshot.StateVersion),
                    new RoundId(claimedSnapshot.RoundId),
                    decisionId: null,
                    controlEpoch: new ControlEpoch(0));
                TableReceiptResolvedEventArgs staleReceipt = await SubmitExpectReceiptAsync(claimed, staleCommand, pumpCts.Token).ConfigureAwait(false);
                if (staleReceipt.Resolution.ReceiptStatus != Mahjong.Contracts.ReceiptStatus.Rejected)
                    throw new InvalidOperationException("A stale control epoch decision was not rejected.");

                return new ReleaseEvidence
                {
                    ReleaseVersion = releaseVersion,
                    AfterVersion = after,
                    AdvancedWithoutPlayerBinding = after > releaseVersion,
                    Claimed = true,
                    ClaimEpoch = claimedEpoch,
                    StaleDecisionRejected = true
                };
            }
            finally
            {
                pumpCts.Cancel();
                if (playerPump != null) { try { await playerPump.ConfigureAwait(false); } catch { } }
                if (spectatorPump != null) { try { await spectatorPump.ConfigureAwait(false); } catch { } }
                if (claimedPump != null) { try { await claimedPump.ConfigureAwait(false); } catch { } }
                if (session != null) try { await session.CloseAsync("C04_TEST_DONE", CancellationToken.None).ConfigureAwait(false); } catch { }
                if (channel != null) try { await channel.CloseAsync(new TableChannelCloseReason("C04_TEST_DONE", true), CancellationToken.None).ConfigureAwait(false); } catch { }
                if (spectator != null) try { await spectator.CloseAsync("C04_TEST_DONE", CancellationToken.None).ConfigureAwait(false); } catch { }
                if (spectatorChannel != null) try { await spectatorChannel.CloseAsync(new TableChannelCloseReason("C04_TEST_DONE", true), CancellationToken.None).ConfigureAwait(false); } catch { }
                if (claimed != null) try { await claimed.CloseAsync("C04_TEST_DONE", CancellationToken.None).ConfigureAwait(false); } catch { }
                if (claimedChannel != null) try { await claimedChannel.CloseAsync(new TableChannelCloseReason("C04_TEST_DONE", true), CancellationToken.None).ConfigureAwait(false); } catch { }
                try { await factory.CloseRoomAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            }
        }

        private static async Task<TargetPrivacyFacts> RunTargetSleeveAsync(TableSession session, CancellationToken cancellationToken)
        {
            PlayerSnapshot baseline = session.PlayerSnapshot
                ?? throw new InvalidOperationException("No target baseline.");
            if (baseline.PrivateView.ClosedTiles.Count < 4) throw new InvalidOperationException("Target has fewer than four closed tiles.");
            uint[] markTiles = new uint[4];
            for (int i = 0; i < 4; i++) markTiles[i] = baseline.PrivateView.ClosedTiles[i];
            PlayerSnapshot marked = await SubmitCheatAndWaitAsync(
                session,
                CommandKind.SubmitTileMark,
                CommandPayloadFactory.SubmitTileMark(baseline.RoundId, "unity-room", 0UL, markTiles),
                "unity-room-mark",
                cancellationToken).ConfigureAwait(false);
            if (marked.PrivateView.Cheat == null || marked.PrivateView.Cheat.LedgerEntries.Count == 0)
                throw new InvalidOperationException("TileMark did not produce private fee/ledger facts.");

            await DiscardOnceAsync(session, cancellationToken).ConfigureAwait(false);
            await AdvanceToTargetCheatWindowAsync(session, cancellationToken).ConfigureAwait(false);
            PlayerSnapshot bloom = await SubmitCheatAndWaitAsync(
                session,
                CommandKind.SubmitSleeveBloom,
                CommandPayloadFactory.SubmitSleeveBloom(session.PlayerSnapshot!.RoundId, "unity-room", 0UL),
                "unity-room-sleeve",
                cancellationToken).ConfigureAwait(false);
            PrivateCheatView cheat = bloom.PrivateView.Cheat
                ?? throw new InvalidOperationException("SleeveBloom did not produce a private cheat view.");
            if (cheat.SleeveBloom == null) throw new InvalidOperationException("SleeveBloom offer is empty.");
            return new TargetPrivacyFacts { SleeveStateVersion = bloom.StateVersion };
        }

        private static async Task DiscardOnceAsync(TableSession session, CancellationToken cancellationToken)
        {
            PlayerSnapshot snapshot = session.PlayerSnapshot
                ?? throw new InvalidOperationException("No target snapshot for discard.");
            uint tile = snapshot.PrivateView.HasDrawnTileId
                ? snapshot.PrivateView.DrawnTileId
                : snapshot.PrivateView.ClosedTiles[0];
            await SubmitActionNoWaitAsync(session, ProtocolActionType.DiscardTile, new[] { tile }, cancellationToken).ConfigureAwait(false);
            await Task.Delay(30, cancellationToken).ConfigureAwait(false);
        }

        private static async Task AdvanceToTargetCheatWindowAsync(TableSession session, CancellationToken cancellationToken)
        {
            for (int i = 0; i < 600; i++)
            {
                PlayerSnapshot? snapshot = session.PlayerSnapshot;
                var board = snapshot?.PublicView?.Board;
                if (snapshot != null && board != null && board.CurrentSeat == 0)
                {
                    if (board.Phase == Mahjong.Contracts.Wire.GamePhase.CheatWindow) return;
                    if (board.Phase == Mahjong.Contracts.Wire.GamePhase.Discard)
                    {
                        uint tile = snapshot.PrivateView.HasDrawnTileId
                            ? snapshot.PrivateView.DrawnTileId
                            : snapshot.PrivateView.ClosedTiles[0];
                        await SubmitActionNoWaitAsync(session, ProtocolActionType.DiscardTile, new[] { tile }, cancellationToken).ConfigureAwait(false);
                    }
                    else if (board.Phase == Mahjong.Contracts.Wire.GamePhase.ResponseWindow ||
                             board.Phase == Mahjong.Contracts.Wire.GamePhase.ChankanWindow)
                    {
                        await SubmitActionNoWaitAsync(session, ProtocolActionType.Pass, null, cancellationToken).ConfigureAwait(false);
                    }
                }
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
            throw new TimeoutException("Target seat did not reach a legal CheatWindow.");
        }

        private static async Task<PlayerSnapshot> SubmitCheatAndWaitAsync(
            TableSession session,
            CommandKind kind,
            BusinessPayload payload,
            string prefix,
            CancellationToken cancellationToken)
        {
            PlayerSnapshot before = session.PlayerSnapshot
                ?? throw new InvalidOperationException("No snapshot before " + prefix + ".");
            var command = new ProtocolCommand(
                kind,
                payload,
                new RequestId(prefix + "-" + Guid.NewGuid().ToString("N")),
                session.Binding!.Scope.ScopeId,
                new StateVersion(before.StateVersion),
                new RoundId(before.RoundId),
                decisionId: null,
                controlEpoch: new ControlEpoch(before.PrivateView.Control?.ControlEpoch ?? 0UL));
            return await SubmitAndWaitAsync(session, command, before.StateVersion, cancellationToken).ConfigureAwait(false);
        }

        private static async Task SubmitActionNoWaitAsync(
            TableSession session,
            ProtocolActionType action,
            uint[]? tiles,
            CancellationToken cancellationToken)
        {
            PlayerSnapshot before = session.PlayerSnapshot
                ?? throw new InvalidOperationException("No snapshot for action.");
            BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                before.RoundId,
                "unity-room-driver",
                0UL,
                action,
                tileIds: tiles);
            var command = new ProtocolCommand(
                CommandKind.SubmitAction,
                payload,
                new RequestId("unity-room-action-" + Guid.NewGuid().ToString("N")),
                session.Binding!.Scope.ScopeId,
                new StateVersion(before.StateVersion),
                new RoundId(before.RoundId),
                decisionId: null,
                controlEpoch: new ControlEpoch(before.PrivateView.Control?.ControlEpoch ?? 0UL));
            CommandSubmission submission = await session.SubmitAsync(command, cancellationToken).ConfigureAwait(false);
            if (!submission.Sent) throw new InvalidOperationException("A room action was not sent.");
        }

        private static async Task<TableReceiptResolvedEventArgs> SubmitExpectReceiptAsync(
            TableSession session,
            ProtocolCommand command,
            CancellationToken cancellationToken)
        {
            var resolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<TableReceiptResolvedEventArgs> handler = (_, args) =>
            {
                if (args.Command.Key.RequestId.Value == command.RequestId.Value) resolved.TrySetResult(args);
            };
            session.ReceiptResolved += handler;
            try
            {
                CommandSubmission submission = await session.SubmitAsync(command, cancellationToken).ConfigureAwait(false);
                if (!submission.Sent) throw new InvalidOperationException("The command was not sent.");
                for (int i = 0; i < 600 && !resolved.Task.IsCompleted; i++)
                    await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                if (!resolved.Task.IsCompleted) throw new TimeoutException("The command receipt did not resolve.");
                return await resolved.Task.ConfigureAwait(false);
            }
            finally
            {
                session.ReceiptResolved -= handler;
            }
        }

        private static async Task<PlayerSnapshot> SubmitAndWaitAsync(
            TableSession session,
            ProtocolCommand command,
            ulong beforeVersion,
            CancellationToken cancellationToken)
        {
            var resolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<TableReceiptResolvedEventArgs> handler = (_, args) =>
            {
                if (args.Command.Key.RequestId.Value == command.RequestId.Value) resolved.TrySetResult(args);
            };
            session.ReceiptResolved += handler;
            try
            {
                CommandSubmission submission = await session.SubmitAsync(command, cancellationToken).ConfigureAwait(false);
                if (!submission.Sent) throw new InvalidOperationException("The command was not sent.");
                for (int i = 0; i < 600 && !resolved.Task.IsCompleted; i++)
                    await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                if (!resolved.Task.IsCompleted) throw new TimeoutException("The command receipt did not resolve.");
                TableReceiptResolvedEventArgs receipt = await resolved.Task.ConfigureAwait(false);
                if (receipt.Resolution.ReceiptStatus != Mahjong.Contracts.ReceiptStatus.Applied &&
                    receipt.Resolution.ReceiptStatus != Mahjong.Contracts.ReceiptStatus.Recorded)
                    throw new InvalidOperationException("The command was rejected: " + receipt.Resolution.ReasonCode);
                for (int i = 0; i < 600 && (session.PlayerSnapshot?.StateVersion ?? 0UL) <= beforeVersion; i++)
                    await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                return session.PlayerSnapshot
                    ?? throw new InvalidOperationException("No snapshot after command resolution.");
            }
            finally
            {
                session.ReceiptResolved -= handler;
            }
        }

        private static async Task PumpLoopAsync(TableSession session, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested) await session.PumpOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is OperationCanceledException ||
                exception is TableChannelClosedException ||
                exception is InvalidOperationException)
            {
            }
        }

        private static async Task WaitReadyAsync(TableSession session, CancellationToken cancellationToken)
        {
            for (int i = 0; i < 400 && session.State != TableConnectionState.Ready; i++)
            {
                if (session.State == TableConnectionState.Faulted)
                    throw new InvalidOperationException("Session faulted: " + (session.LastError ?? "<none>"));
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
            if (session.State != TableConnectionState.Ready) throw new TimeoutException("Session did not become Ready.");
        }

        private static async Task DriveOtherSeatsAsync(TableSession[] sessions, int targetSeat, CancellationToken cancellationToken)
        {
            var lastSentVersion = new Dictionary<int, ulong>();
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    for (int seat = 0; seat < sessions.Length; seat++)
                    {
                        if (seat == targetSeat || sessions[seat] == null) continue;
                        PlayerSnapshot? snapshot = sessions[seat].PlayerSnapshot;
                        var board = snapshot?.PublicView?.Board;
                        if (snapshot == null || board == null || board.CurrentSeat != seat) continue;
                        if (sessions[seat].PendingCommands.Count > 0) continue;
                        if (lastSentVersion.TryGetValue(seat, out ulong sent) && sent == snapshot.StateVersion) continue;
                        ProtocolActionType? action = null;
                        uint[]? tiles = null;
                        if (board.Phase == Mahjong.Contracts.Wire.GamePhase.CheatWindow ||
                            board.Phase == Mahjong.Contracts.Wire.GamePhase.Discard)
                        {
                            action = ProtocolActionType.DiscardTile;
                            uint tile = snapshot.PrivateView.HasDrawnTileId
                                ? snapshot.PrivateView.DrawnTileId
                                : snapshot.PrivateView.ClosedTiles[0];
                            tiles = new[] { tile };
                        }
                        else if (board.Phase == Mahjong.Contracts.Wire.GamePhase.ResponseWindow ||
                                 board.Phase == Mahjong.Contracts.Wire.GamePhase.ChankanWindow)
                        {
                            action = ProtocolActionType.Pass;
                        }
                        if (!action.HasValue) continue;
                        try
                        {
                            await SubmitActionNoWaitAsync(sessions[seat], action.Value, tiles, cancellationToken).ConfigureAwait(false);
                            lastSentVersion[seat] = snapshot.StateVersion;
                        }
                        catch (Exception exception) when (
                            exception is OperationCanceledException ||
                            exception is TableChannelClosedException ||
                            exception is InvalidOperationException)
                        {
                        }
                    }
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private sealed class FourOwnerEvidence
        {
            public ulong Version;
            public int SeatCount;
            public int TargetClosedTiles;
            public int TargetLedgerEntries;
            public long TargetCheatPoints;
            public string TargetOfferId = string.Empty;
            public ulong SpectatorVersion;
            public bool SpectatorHasPublic;
            public bool SpectatorHasPlayer;
        }

        private sealed class ReleaseEvidence
        {
            public ulong ReleaseVersion;
            public ulong AfterVersion;
            public bool AdvancedWithoutPlayerBinding;
            public bool Claimed;
            public ulong ClaimEpoch;
            public bool StaleDecisionRejected;
        }

        private sealed class TargetPrivacyFacts
        {
            public ulong SleeveStateVersion;
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
            return Path.Combine(Application.persistentDataPath, "c04-privacy-evidence-" + Guid.NewGuid().ToString("N"));
        }
    }
}
