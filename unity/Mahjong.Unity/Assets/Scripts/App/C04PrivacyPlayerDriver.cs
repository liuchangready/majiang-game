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
using UnityEngine;
using UnityEngine.SceneManagement;
using DecisionView = Mahjong.Contracts.Wire.DecisionView;
using PlayerSnapshot = Mahjong.Contracts.Wire.PlayerSnapshot;
using PrivateCheatView = Mahjong.Contracts.Wire.PrivateCheatView;
using WireEnvelope = Mahjong.Contracts.Wire.Envelope;
using WireMessageType = Mahjong.Contracts.Wire.MessageType;

namespace Mahjong.Unity.App
{
    public sealed class C04PrivacyPlayerCaseSuite
    {

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
                StateRoot("c04-privacy-room"));

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
                Assert.InRange(state.PlayerSnapshot.PrivateView.ClosedTiles.Count +
                    (state.PlayerSnapshot.PrivateView.HasDrawnTileId ? 1 : 0), 13, 14);
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


        public IEnumerator RealRoom_DelayedOldRawReceiveIsRejectedByNewBinding()
        {
            LocalTableChannelFactory.EnableC04PrivacyRoomMode();
            string root = StateRoot("c04-delayed");
            var paths = new UnityAppPaths(root);
            paths.EnsureDirectoriesCreated();
            Directory.CreateDirectory(paths.LocalSaveDirectory);
            var factory = new LocalTableChannelFactory(
                new AppConfig { pumpBarrierTimeoutMs = 5000, closeTimeoutMs = 5000, cancelObservationMs = 50 },
                paths,
                "r-" + Guid.NewGuid().ToString("N"),
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


        public IEnumerator RealRoom_ColdReopenReconcilesOriginalRequestWithoutDuplicate()
        {
            LocalTableChannelFactory.DisableC04PrivacyRoomModeForTest();
            string root = StateRoot("c04-recovery");
            string roomId = "r-" + Guid.NewGuid().ToString("N");
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
                string requestId = "c04-unity-recovery-original";
                ITableChannel rawFirst = firstFactory.CreateRoomViewerAsync(LocalRoomViewerRole.Player, 0, CancellationToken.None).GetAwaiter().GetResult();
                var firstCapture = new RawCaptureChannel(rawFirst, requestId);
                firstChannel = firstCapture;
                var firstTicket = rawFirst as ILocalTableTicketSource;
                Assert.IsNotNull(firstTicket);
                firstSession = new TableSession(firstCapture, outbox);
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
                Assert.Greater(firstSession.PendingCommands.Count, 0, "The original request was confirmed before recovery; no receipt lookup would be exercised.");

                CloseBlocking(firstSession);
                CloseBlocking(firstChannel);
                firstSession = null;
                firstChannel = null;
                firstFactory.CloseRoomAsync(CancellationToken.None).GetAwaiter().GetResult();

                LocalTableChannelFactory.EnableC04PrivacyRoomMode();
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
                Assert.IsNotNull(reconciled, "Recovery did not resolve the original request from a receipt lookup.");
                Assert.IsTrue(reconciled!.Resolution.FromReceiptLookup);
                Assert.AreEqual(Mahjong.Contracts.ReceiptStatus.Applied, reconciled.Resolution.ReceiptStatus);
                Assert.AreEqual(originalScope.Value, reconciled.Command.Key.ScopeId.Value);
                Assert.AreEqual(requestId, reconciled.Command.Key.RequestId.Value);
                Assert.AreEqual(originalDigest, reconciled.Resolution.CanonicalPayloadSha256);
                Assert.AreEqual(afterSubmitVersion, reconciled.Resolution.CommandVersion?.Value,
                    "Receipt lookup did not return the original request's committed command version.");
                Assert.AreEqual(afterSubmitEventSeq, reconciled.Resolution.EventSeq?.Value,
                    "Receipt lookup did not return the original request's committed event sequence.");
                Assert.Greater(recoveredCapture.ReceiptQueryFrames, 0, "Recovery did not send a ReceiptQuery.");
                Assert.AreEqual(0, recoveredCapture.CommandFrames);
                Assert.GreaterOrEqual(recoveredSession.PlayerSnapshot!.StateVersion, afterSubmitVersion,
                    "Recovered snapshot is behind the original request's committed version.");
                Assert.GreaterOrEqual(recoveredSession.PlayerSnapshot.EventSeq, afterSubmitEventSeq,
                    "Recovered snapshot is behind the original request's committed event sequence.");

                for (int seat = 1; seat < 4; seat++)
                {
                    recoveryOtherChannels[seat - 1] = recoveredFactory.CreateRoomViewerAsync(LocalRoomViewerRole.Player, seat, CancellationToken.None).GetAwaiter().GetResult();
                    var otherTicket = recoveryOtherChannels[seat - 1] as ILocalTableTicketSource;
                    Assert.IsNotNull(otherTicket);
                    recoveryOtherSessions[seat - 1] = new TableSession(recoveryOtherChannels[seat - 1], new InMemoryOutboxStore());
                    Task otherOpen = recoveryOtherSessions[seat - 1].OpenAsync(
                        new TableSessionOpenRequest(otherTicket!.SessionTicket, LocalTableChannelFactory.ClientBuild),
                        pumpCts.Token);
                    yield return WaitTask(() => otherOpen.IsCompleted, 30f, "recovery positive seat " + seat + " open");
                    Assert.IsFalse(otherOpen.IsFaulted, otherOpen.Exception?.ToString());
                    recoveryOtherPumps.Add(PumpLoopAsync(recoveryOtherSessions[seat - 1], pumpCts.Token));
                }
                for (int seat = 1; seat < 4; seat++)
                {
                    Task otherReady = WaitReadyAsync(recoveryOtherSessions[seat - 1], pumpCts.Token);
                    yield return WaitTask(() => otherReady.IsCompleted, 30f, "recovery positive seat " + seat);
                    Assert.IsFalse(otherReady.IsFaulted, otherReady.Exception?.ToString());
                    for (int i = 0; i < 16 && recoveryOtherSessions[seat - 1].PlayerSnapshot == null; i++)
                    {
                        yield return null;
                    }
                    Assert.IsNotNull(recoveryOtherSessions[seat - 1].PlayerSnapshot);
                }

                ulong ownerProjectionVersionFloor = recoveredSession.PlayerSnapshot!.StateVersion;
                ulong ownerProjectionEventFloor = recoveredSession.PlayerSnapshot.EventSeq;
                for (int seat = 1; seat < 4; seat++)
                {
                    PlayerSnapshot ownerSnapshot = recoveryOtherSessions[seat - 1].PlayerSnapshot!;
                    ownerProjectionVersionFloor = Math.Max(ownerProjectionVersionFloor, ownerSnapshot.StateVersion);
                    ownerProjectionEventFloor = Math.Max(ownerProjectionEventFloor, ownerSnapshot.EventSeq);
                }
                yield return WaitForSnapshotFloor(recoveredSession, ownerProjectionVersionFloor, ownerProjectionEventFloor, 30f, "recovery owner zero projection floor");
                for (int seat = 1; seat < 4; seat++)
                    yield return WaitForSnapshotFloor(recoveryOtherSessions[seat - 1], ownerProjectionVersionFloor, ownerProjectionEventFloor, 30f, "recovery owner " + seat + " projection floor");

                string evidence = Path.Combine(EvidenceRoot(), "recovery-query");
                Directory.CreateDirectory(evidence);
                File.WriteAllText(Path.Combine(evidence, "recovery-query.json"),
                    "{\"schemaVersion\":1,\"requestId\":\"" + requestId +
                    "\",\"scopeId\":\"" + originalScope.Value +
                    "\",\"canonicalDigest\":\"" + originalDigest +
                    "\",\"stateVersion\":\"" + afterSubmitVersion +
                    "\",\"eventSeq\":\"" + afterSubmitEventSeq +
                    "\",\"receiptStatus\":\"" + reconciled!.Resolution.ReceiptStatus +
                    "\",\"receiptCommandVersion\":\"" + reconciled.Resolution.CommandVersion?.Value +
                    "\",\"receiptEventSeq\":\"" + reconciled.Resolution.EventSeq?.Value +
                    "\",\"commandFrames\":0,\"receiptQueryFrames\":" + recoveredCapture.ReceiptQueryFrames +
                    ",\"fromReceiptLookup\":" + reconciled.Resolution.FromReceiptLookup.ToString().ToLowerInvariant() +
                    ",\"zeroDuplicateStateDelta\":" +
                    (reconciled.Resolution.CommandVersion?.Value == afterSubmitVersion &&
                     reconciled.Resolution.EventSeq?.Value == afterSubmitEventSeq && recoveredCapture.CommandFrames == 0).ToString().ToLowerInvariant() + "}", new UTF8Encoding(false));
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
                try { Task.WhenAll(recoveryOtherPumps).GetAwaiter().GetResult(); } catch { }
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


        public IEnumerator RealRoom_ClosedPlayerToSpectatorToNewPlayerReceivePathRejectsOldRaw()
        {
            string root = StateRoot("c04-closed-path");
            string roomId = "r-" + Guid.NewGuid().ToString("N");
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


        public IEnumerator RealRoom_LegalSleeveAndFeeOwnershipAcrossFourSeatsAndSpectator()
        {
            string root = StateRoot("c04-owner");
            string roomId = "r-" + Guid.NewGuid().ToString("N");
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


        public IEnumerator RealRoom_OpenDecisionDeadlineContinuesAfterPlayerClose()
        {
            string root = StateRoot("c04-deadline");
            string roomId = "r-" + Guid.NewGuid().ToString("N");
            Task<OpenDeadlineEvidence> task = Task.Run(() => RunOpenDeadlineScenarioAsync(root, roomId));
            yield return WaitTask(() => task.IsCompleted, 75f, "open deadline after player close");
            Assert.IsFalse(task.IsFaulted, task.Exception?.ToString());
            OpenDeadlineEvidence evidence = task.Result;
            Assert.IsTrue(evidence.AdvancedWithoutPlayerBinding, "Open decision deadline did not continue after player close.");
            Debug.Log("C04_DEADLINE_FLOORS releaseVersion=" + evidence.ReleaseVersion + " spectatorBaselineVersion=" + evidence.SpectatorBaselineVersion +
                " afterVersion=" + evidence.AfterVersion + " baselineCursor=" + evidence.SpectatorBaselineCursorStreamVersion + "/" + evidence.SpectatorBaselineCursorProjectionSeq +
                " afterCursor=" + evidence.AfterCursorStreamVersion + "/" + evidence.AfterCursorProjectionSeq);
            Assert.Greater(evidence.AfterVersion, evidence.ReleaseVersion, "Deadline stateVersion did not advance beyond the released player baseline.");
            Assert.Greater(evidence.SpectatorBaselineVersion, 0UL, "Spectator baseline stateVersion must be nonzero.");
            Assert.Greater(evidence.AfterVersion, evidence.SpectatorBaselineVersion, "Deadline progress did not exceed the spectator baseline stateVersion.");
            Assert.Greater(evidence.AfterCursorStreamVersion, evidence.SpectatorBaselineCursorStreamVersion,
                "Deadline cursor streamVersion did not advance: baseline=" + evidence.SpectatorBaselineCursorStreamVersion + " after=" + evidence.AfterCursorStreamVersion +
                " projection=" + evidence.SpectatorBaselineCursorProjectionSeq + "->" + evidence.AfterCursorProjectionSeq);
            string evidencePath = Path.Combine(EvidenceRoot(), "deadline-evidence.json");
            File.WriteAllText(evidencePath,
                "{\"schemaVersion\":1,\"case\":\"RealRoom_OpenDecisionDeadlineContinuesAfterPlayerClose\"" +
                ",\"roomId\":\"" + evidence.RoomId + "\",\"roundId\":\"" + evidence.RoundId + "\"" +
                ",\"decisionId\":\"" + evidence.DecisionId + "\",\"decisionSeat\":" + evidence.DecisionSeat.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"controlEpoch\":" + evidence.ControlEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"decisionDeadlineRemainingMs\":" + evidence.DecisionDeadlineRemainingMs.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"clockDecisionSeat\":" + evidence.ClockDecisionSeat.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"clockServerTimeMs\":" + evidence.ClockServerTimeMs.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"clockDeadlineRemainingMs\":" + evidence.ClockDeadlineRemainingMs.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"authoritativeDeadlineServerTimeMs\":" + evidence.AuthoritativeDeadlineServerTimeMs.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"playerSessionId\":\"" + evidence.PlayerSessionId + "\",\"playerScopeId\":\"" + evidence.PlayerScopeId + "\"" +
                ",\"playerRole\":" + evidence.PlayerRole.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"playerSeat\":" + evidence.PlayerSeat.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"playerBindingEpoch\":" + evidence.PlayerBindingEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"releaseVersion\":" + evidence.ReleaseVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"releaseCursorStreamVersion\":" + evidence.ReleaseCursorStreamVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"releaseCursorProjectionSeq\":" + evidence.ReleaseCursorProjectionSeq.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"closeRequestedUtcMs\":" + evidence.CloseRequestedUtcMs.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"closeCompletedUtcMs\":" + evidence.CloseCompletedUtcMs.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"playerBindingClosed\":" + evidence.PlayerBindingClosed.ToString().ToLowerInvariant() +
                ",\"spectatorSessionId\":\"" + evidence.SpectatorSessionId + "\",\"spectatorRole\":" + evidence.SpectatorRole.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"spectatorSeat\":" + evidence.SpectatorSeat.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"spectatorBaselineVersion\":" + evidence.SpectatorBaselineVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"spectatorBaselineCursorStreamVersion\":" + evidence.SpectatorBaselineCursorStreamVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"spectatorBaselineCursorProjectionSeq\":" + evidence.SpectatorBaselineCursorProjectionSeq.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"afterVersion\":" + evidence.AfterVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"afterCursorStreamVersion\":" + evidence.AfterCursorStreamVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"afterCursorProjectionSeq\":" + evidence.AfterCursorProjectionSeq.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"afterPhase\":\"" + evidence.AfterPhase + "\",\"advancedWithoutPlayerBinding\":" + evidence.AdvancedWithoutPlayerBinding.ToString().ToLowerInvariant() + "}",
                new UTF8Encoding(false));
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }


        public IEnumerator RealRoom_ReleasedSeatAiAdvancesAfterPlayerCloseWithSpectatorOnly()
        {
            string root = StateRoot("c04-release");
            string roomId = "r-" + Guid.NewGuid().ToString("N");
            Task<ReleaseEvidence> task = Task.Run(() => RunReleasedControlScenarioAsync(root, roomId));
            yield return WaitTask(() => task.IsCompleted, 90f, "released-seat authority scenario");
            Assert.IsFalse(task.IsFaulted, task.Exception?.ToString());
            ReleaseEvidence evidence = task.Result;
            string directory = Path.Combine(EvidenceRoot(), "released-control");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "released-control.json"),
                "{\"schemaVersion\":1,\"releaseVersion\":\"" + evidence.ReleaseVersion +
                "\",\"afterVersion\":\"" + evidence.AfterVersion +
                "\",\"claimEpochBefore\":\"" + evidence.ClaimEpochBefore +
                "\",\"claimEpoch\":\"" + evidence.ClaimEpoch +
                "\",\"advancedWithoutPlayerBinding\":" + evidence.AdvancedWithoutPlayerBinding.ToString().ToLowerInvariant() +
                ",\"claimed\":" + evidence.Claimed.ToString().ToLowerInvariant() +
                ",\"claimEpochAdvanced\":" + evidence.ClaimEpochAdvanced.ToString().ToLowerInvariant() +
                ",\"controlPresent\":" + evidence.ControlPresent.ToString().ToLowerInvariant() +
                ",\"controlHuman\":" + evidence.ControlHuman.ToString().ToLowerInvariant() +
                ",\"claimRequestId\":\"" + evidence.ClaimRequestId +
                "\",\"claimReceiptStatus\":\"" + evidence.ClaimReceiptStatus +
                "\",\"claimReceiptReason\":\"" + evidence.ClaimReceiptReason +
                "\",\"staleRequestId\":\"" + evidence.StaleRequestId +
                "\",\"staleDecisionId\":\"" + evidence.StaleDecisionId +
                "\",\"staleSessionId\":\"" + evidence.StaleSessionId +
                "\",\"staleScopeId\":\"" + evidence.StaleScopeId +
                "\",\"staleBindingEpoch\":" + evidence.StaleBindingEpoch +
                ",\"staleReceiptStatus\":\"" + evidence.StaleReceiptStatus +
                "\",\"expectedStaleReason\":\"" + evidence.ExpectedStaleReason +
                "\",\"staleReason\":\"" + evidence.StaleReason +
                "\",\"capturedDecisionEpoch\":" + evidence.CapturedDecisionEpoch +
                ",\"staleEpoch\":" + evidence.StaleEpoch +
                ",\"staleRound\":\"" + evidence.StaleRound +
                "\",\"staleCommandFrames\":" + evidence.StaleCommandFrames +
                ",\"stalePendingAfter\":" + evidence.StalePendingAfter +
                ",\"staleNoCommitMetadata\":" + evidence.StaleNoCommitMetadata.ToString().ToLowerInvariant() +
                ",\"staleDecisionRejected\":" + evidence.StaleDecisionRejected.ToString().ToLowerInvariant() + "}", new UTF8Encoding(false));
            C04ControlContract.Assert(
                evidence.ControlPresent,
                evidence.ClaimEpochAdvanced,
                evidence.ControlHuman,
                evidence.ExpectedStaleReason,
                evidence.StaleReason,
                evidence.StaleDecisionRejected,
                evidence.StaleCommandFrames,
                evidence.StalePendingAfter,
                evidence.StaleNoCommitMetadata);
            Assert.IsTrue(evidence.AdvancedWithoutPlayerBinding, "Released AI did not advance after the player binding closed.");
            Assert.Greater(evidence.AfterVersion, evidence.ReleaseVersion);
            Assert.IsTrue(evidence.Claimed, "ClaimControl was not accepted.");
            Assert.IsTrue(evidence.ControlPresent, "ClaimControl did not produce a client control projection.");
            Assert.IsTrue(evidence.ClaimEpochAdvanced, "ClaimControl did not advance the client control epoch.");
            Assert.IsTrue(evidence.ControlHuman, "ClaimControl did not restore explicit human control.");
            Assert.AreEqual("LOCAL_CONTROL", evidence.ClaimReceiptReason, "ClaimControl returned an unexpected receipt reason.");
            Assert.IsTrue(evidence.ClaimReceiptStatus == "Recorded" || evidence.ClaimReceiptStatus == "Applied", "ClaimControl receipt was not accepted.");
            Assert.AreEqual("Rejected", evidence.StaleReceiptStatus, "Stale action receipt was not rejected.");
            Assert.AreEqual(evidence.CapturedDecisionEpoch, evidence.StaleEpoch, "The stale request did not use the decision epoch it was captured with.");
            Assert.AreEqual(evidence.ExpectedStaleReason, evidence.StaleReason, "The stale request was rejected for the wrong reason.");
            Assert.IsTrue(evidence.StaleDecisionRejected, "A legal stale decision was not rejected.");
            Assert.AreEqual(1, evidence.StaleCommandFrames, "The stale request caused duplicate command frames.");
            Assert.AreEqual(0, evidence.StalePendingAfter, "The rejected stale request left a pending target command.");
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

        private static async Task<OpenDeadlineEvidence> RunOpenDeadlineScenarioAsync(string root, string roomId)
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
                DecisionView decision = baseline.PrivateView.Decision
                    ?? throw new InvalidOperationException("The open-deadline scenario needs a live decision.");
                Mahjong.Contracts.Wire.ClockView? decisionClock = null;
                foreach (Mahjong.Contracts.Wire.ClockView clock in baseline.PublicView.Clocks)
                {
                    if (clock.DecisionSeat == baseline.PrivateView.Seat) { decisionClock = clock; break; }
                }
                if (decisionClock == null) throw new InvalidOperationException("The open decision has no matching public authority clock.");
                if (decision.DeadlineRemainingMs <= 0 || decisionClock.DeadlineRemainingMs <= 0)
                    throw new InvalidOperationException("The open decision deadline is not active.");
                if (Math.Abs(decision.DeadlineRemainingMs - decisionClock.DeadlineRemainingMs) > 1000)
                    throw new InvalidOperationException("Private decision and public authority clock disagree on the deadline.");
                var playerBinding = session.Binding
                    ?? throw new InvalidOperationException("The deadline player binding is missing.");
                ulong beforeVersion = baseline.StateVersion;
                ulong releaseStreamVersion = baseline.Cursor.StreamVersion;
                ulong releaseProjectionSeq = baseline.Cursor.ProjectionSeq;
                long closeRequestedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                await session.CloseAsync("DEADLINE_CLOSE", CancellationToken.None).ConfigureAwait(false);
                await channel.CloseAsync(new TableChannelCloseReason("DEADLINE_CLOSE", true), CancellationToken.None).ConfigureAwait(false);
                bool playerBindingClosed = session.State == TableConnectionState.Closed;
                if (!playerBindingClosed) throw new InvalidOperationException("The player session did not close before spectator recovery.");
                session = null;
                channel = null;
                long closeCompletedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                spectatorChannel = await factory.CreateRoomViewerAsync(LocalRoomViewerRole.Spectator, -1, CancellationToken.None).ConfigureAwait(false);
                var spectatorTicket = spectatorChannel as ILocalTableTicketSource
                    ?? throw new InvalidOperationException("The deadline spectator channel did not expose its ticket.");
                spectator = new TableSession(spectatorChannel, new InMemoryOutboxStore());
                await spectator.OpenAsync(new TableSessionOpenRequest(spectatorTicket.SessionTicket, LocalTableChannelFactory.ClientBuild), pumpCts.Token).ConfigureAwait(false);
                spectatorPump = PumpLoopAsync(spectator, pumpCts.Token);
                await WaitReadyAsync(spectator, pumpCts.Token).ConfigureAwait(false);
                PublicSnapshot spectatorBaseline = spectator.PublicSnapshot
                    ?? throw new InvalidOperationException("No spectator baseline after player close.");
                if (spectatorBaseline.StateVersion != beforeVersion)
                    throw new InvalidOperationException("The spectator baseline was already advanced before deadline observation.");
                ulong after = beforeVersion;
                PublicSnapshot afterSnapshot = spectatorBaseline;
                for (int i = 0; i < 1200; i++)
                {
                    afterSnapshot = spectator.PublicSnapshot ?? afterSnapshot;
                    after = afterSnapshot.StateVersion;
                    if (after > beforeVersion) break;
                    await Task.Delay(25, pumpCts.Token).ConfigureAwait(false);
                }
                bool advanced = after > beforeVersion;
                return new OpenDeadlineEvidence
                {
                    RoomId = roomId,
                    RoundId = baseline.RoundId,
                    DecisionId = decision.DecisionId,
                    DecisionSeat = decisionClock.DecisionSeat,
                    ControlEpoch = decision.ControlEpoch,
                    DecisionDeadlineRemainingMs = decision.DeadlineRemainingMs,
                    ClockDecisionSeat = decisionClock.DecisionSeat,
                    ClockServerTimeMs = decisionClock.ServerTimeMs,
                    ClockDeadlineRemainingMs = decisionClock.DeadlineRemainingMs,
                    AuthoritativeDeadlineServerTimeMs = checked(decisionClock.ServerTimeMs + decisionClock.DeadlineRemainingMs),
                    PlayerSessionId = playerBinding.SessionId.Value,
                    PlayerScopeId = playerBinding.Scope.ScopeId.Value,
                    PlayerRole = (int)playerBinding.Role,
                    PlayerSeat = playerBinding.SeatId,
                    PlayerBindingEpoch = playerBinding.BindingEpoch.Value,
                    ReleaseVersion = beforeVersion,
                    ReleaseCursorStreamVersion = releaseStreamVersion,
                    ReleaseCursorProjectionSeq = releaseProjectionSeq,
                    CloseRequestedUtcMs = closeRequestedUtcMs,
                    CloseCompletedUtcMs = closeCompletedUtcMs,
                    PlayerBindingClosed = playerBindingClosed,
                    SpectatorSessionId = spectator.Binding?.SessionId.Value ?? string.Empty,
                    SpectatorRole = (int)(spectator.Binding?.Role ?? ProtocolRole.Unspecified),
                    SpectatorSeat = spectator.Binding?.SeatId ?? -2,
                    SpectatorBaselineVersion = spectatorBaseline.StateVersion,
                    SpectatorBaselineCursorStreamVersion = spectatorBaseline.Cursor.StreamVersion,
                    SpectatorBaselineCursorProjectionSeq = spectatorBaseline.Cursor.ProjectionSeq,
                    AfterVersion = afterSnapshot.StateVersion,
                    AfterCursorStreamVersion = afterSnapshot.Cursor.StreamVersion,
                    AfterCursorProjectionSeq = afterSnapshot.Cursor.ProjectionSeq,
                    AfterPhase = afterSnapshot.View?.Board?.Phase.ToString() ?? string.Empty,
                    AdvancedWithoutPlayerBinding = advanced && playerBindingClosed && session == null && channel == null
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
            C04PrivacyProbe.BeginPlayerSeatZero(Path.Combine(EvidenceRoot(), "released-control-raw"));
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
            RawCaptureChannel? claimedCapture = null;
            TableSession? claimed = null;
            CancellationTokenSource? claimedPumpCts = null;
            Task? playerPump = null;
            Task? spectatorPump = null;
            Task? claimedPump = null;
            string? claimedPumpError = null;
            Mahjong.Contracts.Wire.DecisionView? staleDecisionSeed = null;
            ulong staleEpochSeed = 0UL;
            string staleRoundSeed = string.Empty;
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
                staleDecisionSeed = before.PrivateView.Decision;
                staleEpochSeed = before.PrivateView.Control?.ControlEpoch ?? 0UL;
                staleRoundSeed = before.RoundId;
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
                claimedCapture = new RawCaptureChannel(claimedChannel);
                var claimedTicket = claimedChannel as ILocalTableTicketSource
                    ?? throw new InvalidOperationException("The claimed player channel did not expose its ticket.");
                claimed = new TableSession(claimedCapture, new InMemoryOutboxStore());
                await claimed.OpenAsync(new TableSessionOpenRequest(claimedTicket.SessionTicket, LocalTableChannelFactory.ClientBuild), pumpCts.Token).ConfigureAwait(false);
                claimedPumpCts = CancellationTokenSource.CreateLinkedTokenSource(pumpCts.Token);
                claimedPump = PumpLoopAsync(claimed, claimedPumpCts.Token,
                    exception => Interlocked.CompareExchange(ref claimedPumpError, exception.ToString(), null));
                await WaitReadyAsync(claimed, pumpCts.Token).ConfigureAwait(false);
                PlayerSnapshot claimBase = claimed.PlayerSnapshot
                    ?? throw new InvalidOperationException("No claimed baseline.");
                for (int i = 0; i < 300 && (claimed.PlayerSnapshot?.Cursor?.ProjectionSeq ?? 0UL) < 2UL; i++)
                    await Task.Delay(10, pumpCts.Token).ConfigureAwait(false);
                if ((claimed.PlayerSnapshot?.Cursor?.ProjectionSeq ?? 0UL) < 2UL)
                    throw new InvalidOperationException("The real claimed-player cursor did not reach ProjectionSeq 2 before ClaimControl.");
                claimBase = claimed.PlayerSnapshot ?? claimBase;
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
                ulong preRecoveryCachedControlEpoch = claimedSnapshot.PrivateView.Control?.ControlEpoch ?? 0UL;
                if (claimed.State != TableConnectionState.Reconnecting)
                    throw new InvalidOperationException("The control cursor-floor recovery branch was not entered; state=" + claimed.State + ", epoch=" + preRecoveryCachedControlEpoch);
                TableSessionBinding oldResumeBinding = claimed.Binding
                    ?? throw new InvalidOperationException("The reconnecting claimed player lost its original session binding.");
                ulong oldResumeBindingEpoch = oldResumeBinding.BindingEpoch.Value;
                string oldResumeSessionId = oldResumeBinding.SessionId.Value;
                string oldResumeScopeId = oldResumeBinding.Scope.ScopeId.Value;
                string oldResumeRoomId = oldResumeBinding.RoomId.Value;
                ProtocolRole oldResumeRole = oldResumeBinding.Role;
                int oldResumeSeat = oldResumeBinding.SeatId;
                Welcome oldResumeWelcome = claimedCapture.CapturedWelcome
                    ?? throw new InvalidOperationException("No original WELCOME frame was captured before control recovery.");
                int oldWelcomeFrameCount = claimedCapture.WelcomeFrameCount;
                string resumeTicketSha256 = Sha256Hex(Encoding.UTF8.GetBytes(claimedTicket.SessionTicket));
                claimedPumpCts.Cancel();
                if (claimedPump != null) await claimedPump.ConfigureAwait(false);
                claimedPumpCts.Dispose();
                claimedPumpCts = CancellationTokenSource.CreateLinkedTokenSource(pumpCts.Token);
                await claimed.OpenAsync(new TableSessionOpenRequest(claimedTicket.SessionTicket, LocalTableChannelFactory.ClientBuild), pumpCts.Token).ConfigureAwait(false);
                claimedPumpError = null;
                claimedPump = PumpLoopAsync(claimed, claimedPumpCts.Token,
                    exception => Interlocked.CompareExchange(ref claimedPumpError, exception.ToString(), null));
                await WaitReadyAsync(claimed, pumpCts.Token).ConfigureAwait(false);
                PlayerSnapshot resumedSnapshot = claimed.PlayerSnapshot
                    ?? throw new InvalidOperationException("The resumed claimed player has no baseline.");
                TableSessionBinding resumedBinding = claimed.Binding
                    ?? throw new InvalidOperationException("The resumed claimed player has no binding.");
                Welcome resumedWelcome = claimedCapture.CapturedWelcome
                    ?? throw new InvalidOperationException("No resumed WELCOME frame was captured during control recovery.");
                ulong claimedEpoch = resumedSnapshot.PrivateView.Control?.ControlEpoch ?? 0UL;
                bool resumedIdentityValid = resumedBinding.SessionId.Value == oldResumeSessionId &&
                    resumedBinding.RoomId.Value == oldResumeRoomId && resumedBinding.Role == oldResumeRole && resumedBinding.SeatId == oldResumeSeat &&
                    resumedBinding.BindingEpoch.Value > oldResumeBindingEpoch && resumedSnapshot.PrivateView.Control?.ControlledByComputer == false &&
                    claimedEpoch > claimEpoch && claimedCapture.IsOpen && claimed.PendingCommands.Count == 0;
                bool welcomeBindingValid = claimedCapture.WelcomeFrameCount > oldWelcomeFrameCount &&
                    resumedWelcome.SessionId == resumedBinding.SessionId.Value && resumedWelcome.RoomId == resumedBinding.RoomId.Value &&
                    resumedWelcome.Scope?.ScopeId == resumedBinding.Scope.ScopeId.Value && (int)resumedWelcome.Role == (int)resumedBinding.Role &&
                    resumedWelcome.SeatId == resumedBinding.SeatId && oldResumeWelcome.SessionId == oldResumeSessionId && oldResumeWelcome.RoomId == oldResumeRoomId &&
                    oldResumeWelcome.Scope?.ScopeId == oldResumeScopeId && (int)oldResumeWelcome.Role == (int)oldResumeRole && oldResumeWelcome.SeatId == oldResumeSeat &&
                    !string.Equals(oldResumeScopeId, resumedBinding.Scope.ScopeId.Value, StringComparison.Ordinal);
                if (!resumedIdentityValid || !welcomeBindingValid)
                    throw new InvalidOperationException("Formal control recovery failed same-account/session binding or WELCOME scope validation. " +
                        "oldSession=" + oldResumeSessionId + ",newSession=" + resumedBinding.SessionId.Value +
                        ",oldScope=" + oldResumeScopeId + ",newScope=" + resumedBinding.Scope.ScopeId.Value +
                        ",oldBindingEpoch=" + oldResumeBindingEpoch + ",newBindingEpoch=" + resumedBinding.BindingEpoch.Value +
                        ",preRecoveryCachedControlEpoch=" + preRecoveryCachedControlEpoch + ",claimBeforeEpoch=" + claimEpoch +
                        ",newSnapshotControlEpoch=" + claimedEpoch + ",newWelcomeFrames=" + claimedCapture.WelcomeFrameCount +
                        ",transportOpen=" + claimedCapture.IsOpen + ",pending=" + claimed.PendingCommands.Count);
                string controlEvidenceDirectory = Path.Combine(EvidenceRoot(), "released-control");
                Directory.CreateDirectory(controlEvidenceDirectory);
                File.WriteAllText(Path.Combine(EvidenceRoot(), "control-session-reconnect.json"),
                    "{\"schemaVersion\":1,\"case\":\"RealRoom_ReleasedSeatAiAdvancesAfterPlayerCloseWithSpectatorOnly\",\"recoveryBranchTaken\":true," +
                    "\"oldState\":\"Reconnecting\",\"newState\":\"" + claimed.State + "\",\"oldSessionId\":\"" + oldResumeSessionId +
                    "\",\"newSessionId\":\"" + resumedBinding.SessionId.Value + "\",\"oldRoomId\":\"" + oldResumeRoomId +
                    "\",\"newRoomId\":\"" + resumedBinding.RoomId.Value + "\",\"oldRole\":" + ((int)oldResumeRole).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    ",\"newRole\":" + ((int)resumedBinding.Role).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    ",\"oldSeat\":" + oldResumeSeat.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"newSeat\":" + resumedBinding.SeatId.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    ",\"oldScopeId\":\"" + oldResumeScopeId + "\",\"newScopeId\":\"" + resumedBinding.Scope.ScopeId.Value +
                    "\",\"oldWelcomeSessionId\":\"" + oldResumeWelcome.SessionId + "\",\"oldWelcomeRoomId\":\"" + oldResumeWelcome.RoomId +
                    "\",\"oldWelcomeScopeId\":\"" + oldResumeWelcome.Scope.ScopeId + "\",\"oldWelcomeRole\":" + ((int)oldResumeWelcome.Role).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    ",\"oldWelcomeSeat\":" + oldResumeWelcome.SeatId.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    ",\"newWelcomeSessionId\":\"" + resumedWelcome.SessionId + "\",\"newWelcomeRoomId\":\"" + resumedWelcome.RoomId +
                    "\",\"newWelcomeScopeId\":\"" + resumedWelcome.Scope.ScopeId + "\",\"newWelcomeRole\":" + ((int)resumedWelcome.Role).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    ",\"newWelcomeSeat\":" + resumedWelcome.SeatId.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    ",\"welcomeFrameCountBefore\":" + oldWelcomeFrameCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"welcomeFrameCountAfter\":" + claimedCapture.WelcomeFrameCount.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    ",\"resumeTicketSha256Before\":\"" + resumeTicketSha256 + "\",\"resumeTicketSha256After\":\"" + Sha256Hex(Encoding.UTF8.GetBytes(claimedTicket.SessionTicket)) +
                    "\",\"sameSessionIdentity\":" + (oldResumeSessionId == resumedBinding.SessionId.Value).ToString().ToLowerInvariant() +
                    ",\"scopeRotated\":" + (oldResumeScopeId != resumedBinding.Scope.ScopeId.Value).ToString().ToLowerInvariant() +
                    ",\"oldBindingEpoch\":" + oldResumeBindingEpoch + ",\"newBindingEpoch\":" + resumedBinding.BindingEpoch.Value +
                    ",\"preRecoveryCachedControlEpoch\":" + preRecoveryCachedControlEpoch +
                    ",\"newSnapshotControlEpoch\":" + (resumedSnapshot.PrivateView.Control?.ControlEpoch ?? 0UL) +
                    ",\"transportOpen\":" + claimedCapture.IsOpen.ToString().ToLowerInvariant() + ",\"pendingCommands\":" + claimed.PendingCommands.Count + "}", new UTF8Encoding(false));
                claimedSnapshot = resumedSnapshot;
                PlayerSnapshot noChangeBase = claimed.PlayerSnapshot
                    ?? throw new InvalidOperationException("No snapshot for the normal Ready control positive control.");
                ulong noChangeEpochBefore = noChangeBase.PrivateView.Control?.ControlEpoch ?? 0UL;
                int noChangeFramesBefore = claimedCapture.CommandFrames;
                var noChangeClaim = new ProtocolCommand(
                    CommandKind.ClaimControl,
                    CommandPayloadFactory.ClaimControl(noChangeBase.RoundId, noChangeEpochBefore),
                    new RequestId("unity-nochange-claim-" + Guid.NewGuid().ToString("N")),
                    claimed.Binding!.Scope.ScopeId,
                    new StateVersion(noChangeBase.StateVersion),
                    new RoundId(noChangeBase.RoundId),
                    decisionId: null,
                    controlEpoch: new ControlEpoch(noChangeEpochBefore));
                TableReceiptResolvedEventArgs noChangeReceipt = await SubmitExpectReceiptAsync(claimed, noChangeClaim, pumpCts.Token).ConfigureAwait(false);
                PlayerSnapshot noChangeAfter = noChangeBase;
                for (int i = 0; i < 200; i++)
                {
                    noChangeAfter = claimed.PlayerSnapshot ?? noChangeAfter;
                    if (claimed.State == TableConnectionState.Ready &&
                        (noChangeAfter.PrivateView.Control?.ControlEpoch ?? 0UL) == noChangeEpochBefore &&
                        noChangeAfter.PrivateView.Control?.ControlledByComputer == false && claimed.PendingCommands.Count == 0) break;
                    await Task.Delay(10, pumpCts.Token).ConfigureAwait(false);
                }
                bool noChangeRecoveryBranchTaken = claimed.State == TableConnectionState.Reconnecting;
                string noChangeOldSessionId = claimed.Binding!.SessionId.Value;
                string noChangeOldRoomId = claimed.Binding.RoomId.Value;
                string noChangeOldScopeId = claimed.Binding.Scope.ScopeId.Value;
                ulong noChangeOldBindingEpoch = claimed.Binding.BindingEpoch.Value;
                int noChangeWelcomeCountBefore = claimedCapture.WelcomeFrameCount;
                Welcome noChangeWelcomeBefore = claimedCapture.CapturedWelcome
                    ?? throw new InvalidOperationException("No WELCOME was captured before no-change control verification.");
                string noChangeResumeTicketHash = Sha256Hex(Encoding.UTF8.GetBytes(claimedTicket.SessionTicket));
                if (noChangeRecoveryBranchTaken)
                {
                    claimedPumpCts.Cancel();
                    if (claimedPump != null) await claimedPump.ConfigureAwait(false);
                    claimedPumpCts.Dispose();
                    claimedPumpCts = CancellationTokenSource.CreateLinkedTokenSource(pumpCts.Token);
                    await claimed.OpenAsync(new TableSessionOpenRequest(claimedTicket.SessionTicket, LocalTableChannelFactory.ClientBuild), pumpCts.Token).ConfigureAwait(false);
                    claimedPumpError = null;
                    claimedPump = PumpLoopAsync(claimed, claimedPumpCts.Token,
                        exception => Interlocked.CompareExchange(ref claimedPumpError, exception.ToString(), null));
                    await WaitReadyAsync(claimed, pumpCts.Token).ConfigureAwait(false);
                    TableSessionBinding noChangeResumedBinding = claimed.Binding
                        ?? throw new InvalidOperationException("No binding after no-change control recovery.");
                    Welcome noChangeResumedWelcome = claimedCapture.CapturedWelcome
                        ?? throw new InvalidOperationException("No resumed WELCOME after no-change control recovery.");
                    if (noChangeResumedBinding.SessionId.Value != noChangeOldSessionId || noChangeResumedBinding.RoomId.Value != noChangeOldRoomId ||
                        noChangeResumedBinding.Role != ProtocolRole.Player || noChangeResumedBinding.SeatId != 0 ||
                        noChangeResumedBinding.BindingEpoch.Value <= noChangeOldBindingEpoch || noChangeResumedBinding.Scope.ScopeId.Value == noChangeOldScopeId ||
                        noChangeResumedWelcome.SessionId != noChangeResumedBinding.SessionId.Value || noChangeResumedWelcome.RoomId != noChangeResumedBinding.RoomId.Value ||
                        noChangeResumedWelcome.Scope?.ScopeId != noChangeResumedBinding.Scope.ScopeId.Value || (int)noChangeResumedWelcome.Role != (int)ProtocolRole.Player || noChangeResumedWelcome.SeatId != 0 ||
                        noChangeWelcomeBefore.SessionId != noChangeOldSessionId || noChangeWelcomeBefore.RoomId != noChangeOldRoomId || noChangeWelcomeBefore.Scope?.ScopeId != noChangeOldScopeId)
                        throw new InvalidOperationException("No-change control recovery changed authenticated session identity or did not bind its new WELCOME.");
                }
                Welcome noChangeWelcomeAfter = claimedCapture.CapturedWelcome
                    ?? throw new InvalidOperationException("No current WELCOME after no-change control verification.");
                noChangeAfter = claimed.PlayerSnapshot ?? noChangeAfter;
                int noChangeFrames = claimedCapture.CommandFrames - noChangeFramesBefore;
                bool noChangeIdentityValid = claimed.Binding != null && claimed.State == TableConnectionState.Ready &&
                    claimed.Binding.SessionId.Value == noChangeOldSessionId && claimed.Binding.RoomId.Value == noChangeOldRoomId &&
                    claimed.Binding.Role == ProtocolRole.Player && claimed.Binding.SeatId == 0 &&
                    noChangeWelcomeAfter.SessionId == claimed.Binding.SessionId.Value && noChangeWelcomeAfter.RoomId == claimed.Binding.RoomId.Value &&
                    noChangeWelcomeAfter.Scope?.ScopeId == claimed.Binding.Scope.ScopeId.Value && (int)noChangeWelcomeAfter.Role == (int)ProtocolRole.Player &&
                    noChangeWelcomeAfter.SeatId == 0 && noChangeWelcomeBefore.Scope?.AccountId == noChangeWelcomeAfter.Scope?.AccountId &&
                    noChangeResumeTicketHash == Sha256Hex(Encoding.UTF8.GetBytes(claimedTicket.SessionTicket)) &&
                    (noChangeRecoveryBranchTaken
                        ? claimed.Binding.BindingEpoch.Value > noChangeOldBindingEpoch && claimed.Binding.Scope.ScopeId.Value != noChangeOldScopeId && claimedCapture.WelcomeFrameCount > noChangeWelcomeCountBefore
                        : claimed.Binding.BindingEpoch.Value == noChangeOldBindingEpoch && claimed.Binding.Scope.ScopeId.Value == noChangeOldScopeId && claimedCapture.WelcomeFrameCount == noChangeWelcomeCountBefore);
                bool noChangeReady = claimed.State == TableConnectionState.Ready &&
                    (noChangeAfter.PrivateView.Control?.ControlEpoch ?? 0UL) == noChangeEpochBefore &&
                    noChangeAfter.PrivateView.Control?.ControlledByComputer == false && claimed.PendingCommands.Count == 0 && noChangeFrames == 1 &&
                    noChangeAfter.StateVersion >= noChangeBase.StateVersion && noChangeAfter.EventSeq >= noChangeBase.EventSeq && noChangeIdentityValid &&
                    (noChangeReceipt.Resolution.ReceiptStatus == Mahjong.Contracts.ReceiptStatus.Applied ||
                     noChangeReceipt.Resolution.ReceiptStatus == Mahjong.Contracts.ReceiptStatus.Recorded) &&
                    string.Equals(noChangeReceipt.Resolution.ReasonCode, "LOCAL_CONTROL", StringComparison.Ordinal);
                File.WriteAllText(Path.Combine(EvidenceRoot(), "control-session-ready-positive.json"),
                    "{\"schemaVersion\":1,\"case\":\"RealRoom_ReleasedSeatAiAdvancesAfterPlayerCloseWithSpectatorOnly\",\"phase\":\"post-recovery-no-change-claim\",\"sessionState\":\"" + claimed.State +
                    "\",\"requestId\":\"" + noChangeClaim.RequestId.Value + "\",\"receiptStatus\":\"" + noChangeReceipt.Resolution.ReceiptStatus +
                    "\",\"receiptReason\":\"" + noChangeReceipt.Resolution.ReasonCode + "\",\"recoveryBranchTaken\":" + noChangeRecoveryBranchTaken.ToString().ToLowerInvariant() +
                    ",\"oldSessionId\":\"" + noChangeOldSessionId + "\",\"newSessionId\":\"" + claimed.Binding!.SessionId.Value +
                    "\",\"oldRoomId\":\"" + noChangeOldRoomId + "\",\"newRoomId\":\"" + claimed.Binding.RoomId.Value +
                    "\",\"oldScopeId\":\"" + noChangeOldScopeId + "\",\"newScopeId\":\"" + claimed.Binding.Scope.ScopeId.Value +
                    "\",\"oldBindingEpoch\":" + noChangeOldBindingEpoch + ",\"bindingEpoch\":" + claimed.Binding.BindingEpoch.Value +
                    ",\"welcomeFrameCountBefore\":" + noChangeWelcomeCountBefore + ",\"welcomeFrameCountAfter\":" + claimedCapture.WelcomeFrameCount +
                    ",\"oldWelcomeAccountId\":\"" + noChangeWelcomeBefore.Scope?.AccountId + "\",\"newWelcomeAccountId\":\"" + noChangeWelcomeAfter.Scope?.AccountId +
                    "\",\"oldWelcomeSessionId\":\"" + noChangeWelcomeBefore.SessionId + "\",\"newWelcomeSessionId\":\"" + noChangeWelcomeAfter.SessionId +
                    "\",\"oldWelcomeRoomId\":\"" + noChangeWelcomeBefore.RoomId + "\",\"newWelcomeRoomId\":\"" + noChangeWelcomeAfter.RoomId +
                    "\",\"oldWelcomeScopeId\":\"" + noChangeWelcomeBefore.Scope?.ScopeId + "\",\"newWelcomeScopeId\":\"" + noChangeWelcomeAfter.Scope?.ScopeId +
                    "\",\"oldWelcomeRole\":" + (int)noChangeWelcomeBefore.Role + ",\"newWelcomeRole\":" + (int)noChangeWelcomeAfter.Role +
                    ",\"oldWelcomeSeat\":" + noChangeWelcomeBefore.SeatId + ",\"newWelcomeSeat\":" + noChangeWelcomeAfter.SeatId +
                    ",\"resumeTicketSha256Before\":\"" + noChangeResumeTicketHash + "\",\"resumeTicketSha256After\":\"" + Sha256Hex(Encoding.UTF8.GetBytes(claimedTicket.SessionTicket)) + "\"" +
                    ",\"controlEpochBefore\":" + noChangeEpochBefore +
                    ",\"controlEpochAfter\":" + (noChangeAfter.PrivateView.Control?.ControlEpoch ?? 0UL) + ",\"controlHuman\":" +
                    (noChangeAfter.PrivateView.Control?.ControlledByComputer == false).ToString().ToLowerInvariant() + ",\"sessionId\":\"" + claimed.Binding!.SessionId.Value +
                    "\",\"scopeId\":\"" + claimed.Binding.Scope.ScopeId.Value + "\",\"bindingEpoch\":" + claimed.Binding.BindingEpoch.Value +
                    ",\"commandFrames\":" + noChangeFrames + ",\"pendingCommands\":" + claimed.PendingCommands.Count + "}", new UTF8Encoding(false));
                if (!noChangeReady) throw new InvalidOperationException("The no-regression ClaimControl positive control did not remain Ready without changing owner or epoch. state=" + claimed.State +
                    ",epochBefore=" + noChangeEpochBefore + ",epochAfter=" + (noChangeAfter.PrivateView.Control?.ControlEpoch ?? 0UL) +
                    ",human=" + (noChangeAfter.PrivateView.Control?.ControlledByComputer == false) + ",receipt=" + noChangeReceipt.Resolution.ReceiptStatus +
                    ",reason=" + noChangeReceipt.Resolution.ReasonCode + ",frames=" + noChangeFrames + ",pending=" + claimed.PendingCommands.Count +
                    ",transportOpen=" + claimedCapture.IsOpen + ",lastError=" + (claimed.LastError ?? "<none>") + ",pumpError=" + (claimedPumpError ?? "<none>"));

                claimedSnapshot = claimed.PlayerSnapshot ?? noChangeAfter;

                Mahjong.Contracts.Wire.DecisionView? staleDecision = staleDecisionSeed
                    ?? throw new InvalidOperationException("No captured decision is available for a legal stale-epoch request.");
                Mahjong.Contracts.Wire.LegalActionView? staleAction = null;
                foreach (Mahjong.Contracts.Wire.LegalActionView action in staleDecision.Actions)
                {
                    if (action.ActionType == (uint)ProtocolActionType.Pass) { staleAction = action; break; }
                    if (staleAction == null) staleAction = action;
                }
                if (staleAction == null) throw new InvalidOperationException("The live decision has no action for a legal stale-epoch request.");
                PlayerSnapshot? staleSubmitSnapshot = claimed.PlayerSnapshot;
                var staleSubmitBinding = claimed.Binding;
                File.WriteAllText(Path.Combine(EvidenceRoot(), "control-session-before-stale-submit.json"),
                    "{\"schemaVersion\":1,\"case\":\"RealRoom_ReleasedSeatAiAdvancesAfterPlayerCloseWithSpectatorOnly\",\"phase\":\"before-stale-submit\"" +
                    ",\"sessionState\":\"" + claimed.State + "\",\"bindingPresent\":" + (staleSubmitBinding != null).ToString().ToLowerInvariant() +
                    ",\"sessionId\":\"" + (staleSubmitBinding?.SessionId.Value ?? string.Empty) + "\",\"scopeId\":\"" + (staleSubmitBinding?.Scope.ScopeId.Value ?? string.Empty) + "\"" +
                    ",\"role\":" + ((int)(staleSubmitBinding?.Role ?? ProtocolRole.Unspecified)).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    ",\"seat\":" + (staleSubmitBinding?.SeatId ?? -2).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    ",\"bindingEpoch\":" + (staleSubmitBinding?.BindingEpoch.Value ?? 0UL).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    ",\"snapshotStateVersion\":" + (staleSubmitSnapshot?.StateVersion ?? 0UL).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    ",\"snapshotEventSeq\":" + (staleSubmitSnapshot?.EventSeq ?? 0UL).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    ",\"snapshotRoundId\":\"" + (staleSubmitSnapshot?.RoundId ?? string.Empty) + "\",\"snapshotPhase\":\"" + (staleSubmitSnapshot?.PublicView.Board?.Phase.ToString() ?? string.Empty) + "\"" +
                    ",\"snapshotCurrentSeat\":" + (staleSubmitSnapshot?.PublicView.Board?.CurrentSeat ?? -2).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    ",\"snapshotDecisionId\":\"" + (staleSubmitSnapshot?.PrivateView.Decision?.DecisionId ?? string.Empty) + "\",\"snapshotControlEpoch\":" + claimedEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    ",\"capturedStaleDecisionId\":\"" + (staleDecisionSeed?.DecisionId ?? string.Empty) + "\",\"capturedStaleDecisionEpoch\":" + staleEpochSeed.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    ",\"pendingCommands\":" + claimed.PendingCommands.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    ",\"transportOpen\":" + claimedCapture!.IsOpen.ToString().ToLowerInvariant() +
                    ",\"claimCommandFrames\":" + claimedCapture!.CommandFrames.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}", new UTF8Encoding(false));
                if (claimed.State != TableConnectionState.Ready || staleSubmitBinding == null || staleSubmitBinding.Scope == null || staleSubmitSnapshot == null)
                {
                    File.WriteAllText(Path.Combine(controlEvidenceDirectory, "control-session-submit-error.txt"),
                        "state=" + claimed.State + Environment.NewLine +
                        "lastError=" + (claimed.LastError ?? "<none>") + Environment.NewLine +
                        "transportOpen=" + claimedCapture!.IsOpen + Environment.NewLine +
                        "pumpError=" + (claimedPumpError ?? "<none>") + Environment.NewLine +
                        "bindingPresent=" + (staleSubmitBinding != null) + Environment.NewLine +
                        "pendingCommands=" + claimed.PendingCommands.Count + Environment.NewLine +
                        "commandFrames=" + claimedCapture.CommandFrames + Environment.NewLine,
                        new UTF8Encoding(false));
                    throw new InvalidOperationException("Claimed player session lost its ready binding before the stale-epoch probe; state=" + claimed.State +
                        ", binding=" + (staleSubmitBinding != null) + ", snapshot=" + (staleSubmitSnapshot != null) + ", epoch=" + claimedEpoch +
                        ", lastError=" + (claimed.LastError ?? "<none>") + ", transportOpen=" + claimedCapture.IsOpen +
                        ", pumpError=" + (claimedPumpError ?? "<none>"));
                }
                int staleCommandFramesBefore = claimedCapture!.CommandFrames;
                var staleCommand = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    CommandPayloadFactory.SubmitAction(
                        staleRoundSeed,
                        staleDecision.DecisionId,
                        staleEpochSeed,
                        (ProtocolActionType)staleAction!.ActionType,
                        tileIds: staleAction!.TileIds),
                    new RequestId("unity-stale-" + Guid.NewGuid().ToString("N")),
                    claimed.Binding.Scope.ScopeId,
                    new StateVersion(claimedSnapshot.StateVersion),
                    new RoundId(staleRoundSeed),
                    decisionId: new DecisionId(staleDecision.DecisionId),
                    controlEpoch: new ControlEpoch(staleEpochSeed));
                TableReceiptResolvedEventArgs staleReceipt = await SubmitExpectReceiptAsync(claimed, staleCommand, pumpCts.Token).ConfigureAwait(false);
                bool staleRejected = staleReceipt.Resolution.ReceiptStatus == Mahjong.Contracts.ReceiptStatus.Rejected &&
                    string.Equals(staleReceipt.Resolution.ReasonCode, "DECISION_MISMATCH", StringComparison.Ordinal);
                int staleCommandFrames = claimedCapture.CommandFrames - staleCommandFramesBefore;
                int stalePendingAfter = claimed.PendingCommands.Count;
                bool staleNoCommitMetadata = !staleReceipt.Resolution.CommandVersion.HasValue &&
                    !staleReceipt.Resolution.EventSeq.HasValue &&
                    !staleReceipt.Resolution.CommittedAtMs.HasValue;

                string releasedRawHash = C04PrivacyProbe.Seal();
                Debug.Log("C04_PRIVACY_RAW scene=released-control frames=" + C04PrivacyProbe.CapturedFrameCount + " sha256=" + releasedRawHash);
                return new ReleaseEvidence
                {
                    ReleaseVersion = releaseVersion,
                    AfterVersion = after,
                    AdvancedWithoutPlayerBinding = after > releaseVersion,
                    Claimed = true,
                    ClaimEpochBefore = claimEpoch,
                    ClaimEpoch = claimedEpoch,
                    ClaimEpochAdvanced = claimedEpoch > claimEpoch,
                    ControlPresent = claimedSnapshot.PrivateView.Control != null,
                    ControlHuman = claimedSnapshot.PrivateView.Control != null && claimedSnapshot.PrivateView.Control.ControlledByComputer == false,
                    ClaimRequestId = claimCommand.RequestId.Value,
                    ClaimReceiptStatus = claimReceipt.Resolution.ReceiptStatus.ToString(),
                    ClaimReceiptReason = claimReceipt.Resolution.ReasonCode,
                    StaleRequestId = staleCommand.RequestId.Value,
                    StaleDecisionId = staleDecision.DecisionId,
                    StaleSessionId = claimed.Binding!.SessionId.Value,
                    StaleScopeId = claimed.Binding.Scope.ScopeId.Value,
                    StaleBindingEpoch = claimed.Binding.BindingEpoch.Value,
                    StaleReceiptStatus = staleReceipt.Resolution.ReceiptStatus.ToString(),
                    ExpectedStaleReason = "DECISION_MISMATCH",
                    StaleReason = staleReceipt.Resolution.ReasonCode,
                    StaleEpoch = staleEpochSeed,
                    CapturedDecisionEpoch = staleDecision.ControlEpoch,
                    StaleRound = staleRoundSeed,
                    StaleCommandFrames = staleCommandFrames,
                    StalePendingAfter = stalePendingAfter,
                    StaleNoCommitMetadata = staleNoCommitMetadata,
                    StaleDecisionRejected = staleRejected
                };
            }
            finally
            {
                if (claimedPumpCts != null) { try { claimedPumpCts.Cancel(); } catch { } }
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
                claimedPumpCts?.Dispose();
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

        private static async Task PumpLoopAsync(TableSession session, CancellationToken cancellationToken, Action<Exception>? onTransientError = null)
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
                onTransientError?.Invoke(exception);
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

        private sealed class OpenDeadlineEvidence
        {
            public string RoomId = string.Empty;
            public string RoundId = string.Empty;
            public string DecisionId = string.Empty;
            public int DecisionSeat;
            public ulong ControlEpoch;
            public long DecisionDeadlineRemainingMs;
            public int ClockDecisionSeat;
            public long ClockServerTimeMs;
            public long ClockDeadlineRemainingMs;
            public long AuthoritativeDeadlineServerTimeMs;
            public string PlayerSessionId = string.Empty;
            public string PlayerScopeId = string.Empty;
            public int PlayerRole;
            public int PlayerSeat;
            public ulong PlayerBindingEpoch;
            public ulong ReleaseVersion;
            public ulong ReleaseCursorStreamVersion;
            public ulong ReleaseCursorProjectionSeq;
            public long CloseRequestedUtcMs;
            public long CloseCompletedUtcMs;
            public bool PlayerBindingClosed;
            public string SpectatorSessionId = string.Empty;
            public int SpectatorRole;
            public int SpectatorSeat;
            public ulong SpectatorBaselineVersion;
            public ulong SpectatorBaselineCursorStreamVersion;
            public ulong SpectatorBaselineCursorProjectionSeq;
            public ulong AfterVersion;
            public ulong AfterCursorStreamVersion;
            public ulong AfterCursorProjectionSeq;
            public string AfterPhase = string.Empty;
            public bool AdvancedWithoutPlayerBinding;
        }

        private sealed class ReleaseEvidence
        {
            public ulong ReleaseVersion;
            public ulong AfterVersion;
            public bool AdvancedWithoutPlayerBinding;
            public bool Claimed;
            public ulong ClaimEpochBefore;
            public ulong ClaimEpoch;
            public bool ClaimEpochAdvanced;
            public bool ControlPresent;
            public bool ControlHuman;
            public string ClaimRequestId = string.Empty;
            public string ClaimReceiptStatus = string.Empty;
            public string ClaimReceiptReason = string.Empty;
            public string StaleRequestId = string.Empty;
            public string StaleDecisionId = string.Empty;
            public string StaleSessionId = string.Empty;
            public string StaleScopeId = string.Empty;
            public ulong StaleBindingEpoch;
            public string StaleReceiptStatus = string.Empty;
            public string ExpectedStaleReason = string.Empty;
            public string StaleReason = string.Empty;
            public ulong StaleEpoch;
            public ulong CapturedDecisionEpoch;
            public string StaleRound = string.Empty;
            public int StaleCommandFrames;
            public int StalePendingAfter;
            public bool StaleNoCommitMetadata;
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

        private static IEnumerator WaitForSnapshotFloor(TableSession session, ulong stateVersion, ulong eventSeq, float seconds, string label)
        {
            float deadline = Time.realtimeSinceStartup + seconds;
            while (((session.PlayerSnapshot?.StateVersion ?? 0UL) < stateVersion || (session.PlayerSnapshot?.EventSeq ?? 0UL) < eventSeq) && Time.realtimeSinceStartup < deadline)
                yield return null;
            Assert.GreaterOrEqual(session.PlayerSnapshot!.StateVersion, stateVersion, label + " stateVersion");
            Assert.GreaterOrEqual(session.PlayerSnapshot.EventSeq, eventSeq, label + " eventSeq");
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
            private string? _dropReceiptRequestId;

            public RawCaptureChannel(ITableChannel inner) => _inner = inner;

            public RawCaptureChannel(ITableChannel inner, string dropReceiptRequestId)
            {
                _inner = inner;
                _dropReceiptRequestId = dropReceiptRequestId;
            }

            public bool IsOpen => _inner.IsOpen;
            public byte[]? CapturedPlayerSnapshotFrame { get; private set; }
            public Welcome? CapturedWelcome { get; private set; }
            public int WelcomeFrameCount { get; private set; }
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
                while (true)
                {
                    ReadOnlyMemory<byte> frame = await _inner.ReceiveAsync(cancellationToken);
                    WireDecodeResult decoded = WireCodec.Decode(frame.ToArray());
                    if (decoded.IsOk && decoded.Envelope?.Welcome != null)
                    {
                        CapturedWelcome = decoded.Envelope.Welcome;
                        WelcomeFrameCount++;
                    }
                    if (decoded.IsOk && decoded.Envelope != null &&
                        decoded.Envelope.MessageType == WireMessageType.Receipt &&
                        !string.IsNullOrEmpty(_dropReceiptRequestId) &&
                        string.Equals(decoded.Envelope.RequestId, _dropReceiptRequestId, StringComparison.Ordinal))
                    {
                        _dropReceiptRequestId = null;
                        continue;
                    }
                    if (decoded.IsOk && decoded.Envelope != null &&
                        decoded.Envelope.MessageType == WireMessageType.SyncResponse &&
                        decoded.Envelope.SyncResponse?.Target == SyncTarget.PlayerStream)
                    {
                        CapturedPlayerSnapshotFrame = frame.ToArray();
                    }
                    return frame;
                }
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

        private static string StateRoot(string prefix)
        {
            string evidence = EvidenceRoot();
            string runRoot = Path.GetDirectoryName(evidence) ?? Application.temporaryCachePath;
            return Path.Combine(runRoot, "state", prefix.Substring(0, Math.Min(2, prefix.Length)) + "-" + Guid.NewGuid().ToString("N"));
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
    /// <summary>Runtime assertion subset for the exact PlayMode scenario bodies hosted by the opt-in Player driver.</summary>
    internal static class Assert
    {
        public static void IsTrue(bool value, string message = "assertion expected true") { if (!value) throw new InvalidOperationException(message); }
        public static void IsFalse(bool value, string message = "assertion expected false") { if (value) throw new InvalidOperationException(message); }
        public static void AreEqual(object? expected, object? actual, string message = "") { if (!Equals(expected, actual)) throw new InvalidOperationException((message.Length == 0 ? "values differ" : message) + ": expected=" + expected + " actual=" + actual); }
        public static void AreNotEqual(object? expected, object? actual, string message = "") { if (Equals(expected, actual)) throw new InvalidOperationException((message.Length == 0 ? "values unexpectedly equal" : message) + ": " + actual); }
        public static void IsNull(object? value, string message = "expected null") { if (value != null) throw new InvalidOperationException(message); }
        public static void IsNotNull(object? value, string message = "expected non-null") { if (value == null) throw new InvalidOperationException(message); }
        public static void Greater<T>(T actual, T threshold, string message = "expected value greater than threshold") where T : IComparable<T> { if (actual.CompareTo(threshold) <= 0) throw new InvalidOperationException(message + ": actual=" + actual + " threshold=" + threshold); }
        public static void GreaterOrEqual<T>(T actual, T threshold, string message = "expected value at least threshold") where T : IComparable<T> { if (actual.CompareTo(threshold) < 0) throw new InvalidOperationException(message + ": actual=" + actual + " threshold=" + threshold); }
        public static void IsEmpty(System.Collections.IEnumerable values, string message = "expected empty sequence") { foreach (object? _ in values) throw new InvalidOperationException(message); }
        public static void InRange(int actual, int minimum, int maximum) { if (actual < minimum || actual > maximum) throw new InvalidOperationException("value out of expected range: " + actual); }
    }

    internal static class C04ControlContract
    {
        public static void Assert(bool controlPresent, bool epochAdvanced, bool human,
            string expectedStaleReason, string actualStaleReason, bool staleRejected,
            int staleCommandFrames, int stalePendingAfter, bool staleNoCommitMetadata)
        {
            if (!controlPresent) throw new InvalidOperationException("control projection is null");
            if (!epochAdvanced) throw new InvalidOperationException("control epoch did not advance");
            if (!human) throw new InvalidOperationException("control is still automated");
            if (!string.Equals(expectedStaleReason, actualStaleReason, StringComparison.Ordinal))
                throw new InvalidOperationException($"stale reason {actualStaleReason} != expected {expectedStaleReason}");
            if (!staleRejected) throw new InvalidOperationException("stale request was not rejected");
            if (staleCommandFrames != 1) throw new InvalidOperationException($"stale request command frames {staleCommandFrames} != 1");
            if (stalePendingAfter != 0) throw new InvalidOperationException("stale request left pending target command");
            if (!staleNoCommitMetadata) throw new InvalidOperationException("stale rejection carried target commit metadata");
        }
    }

    /// <summary>Opt-in runtime driver; no args means no driver object, host, path, or thread is created.</summary>
    public sealed class C04PrivacyPlayerDriver : MonoBehaviour
    {
        public const string EnableArgument = "-c04PrivacyPlayerDriver";
        public const string CaseArgument = "-c04PrivacyPlayerCase";
        public const string EvidenceArgument = "-c04PrivacyPlayerEvidence";
        private string _caseName = string.Empty;
        private string _evidence = string.Empty;
        private StreamWriter? _log;
        private C04PrivacyPlayerCaseSuite? _suite;
        private int _exit = 0;
        private bool _done;

        private static readonly string[] Cases = {
            "RealRoom_FourPlayersSpectatorAndRoleSwitchUseIndependentSessions",
            "RealRoom_DelayedOldRawReceiveIsRejectedByNewBinding",
            "RealRoom_ColdReopenReconcilesOriginalRequestWithoutDuplicate",
            "RealRoom_LegalSleeveAndFeeOwnershipAcrossFourSeatsAndSpectator",
            "RealRoom_ReleasedSeatAiAdvancesAfterPlayerCloseWithSpectatorOnly",
            "RealRoom_OpenDecisionDeadlineContinuesAfterPlayerClose",
            "RealRoom_ClosedPlayerToSpectatorToNewPlayerReceivePathRejectsOldRaw"
        };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            string[] args = Environment.GetCommandLineArgs();
            int enabled = Count(args, EnableArgument);
            if (enabled == 0) return;
            try
            {
                if (enabled != 1) throw new ArgumentException("Opt-in flag must appear exactly once.");
                string caseName = Required(args, CaseArgument);
                string evidence = Required(args, EvidenceArgument);
                if (Array.IndexOf(Cases, caseName) < 0) throw new ArgumentException("Unknown C04 Player case.");
                string full = Path.GetFullPath(evidence);
                if (Directory.Exists(full) || File.Exists(full)) throw new IOException("Evidence root must be a new path.");
                Application.runInBackground = true;
                var go = new GameObject("C04PrivacyPlayerDriver");
                DontDestroyOnLoad(go);
                var driver = go.AddComponent<C04PrivacyPlayerDriver>();
                driver._caseName = caseName;
                driver._evidence = full;
            }
            catch (Exception exception)
            {
                Debug.LogError("C04_PRIVACY_PLAYER_ARGS_REJECTED " + exception.GetType().Name + ": " + exception.Message);
                Application.Quit(71);
            }
        }

        private void Start()
        {
            try
            {
                Directory.CreateDirectory(_evidence);
                _log = new StreamWriter(Path.Combine(_evidence, "player-case.jsonl"), false, new UTF8Encoding(false)) { AutoFlush = true };
                _log.WriteLine("{\"event\":\"start\",\"case\":\"" + _caseName + "\",\"pid\":" + System.Diagnostics.Process.GetCurrentProcess().Id + "}");
                _suite = new C04PrivacyPlayerCaseSuite();
                StartCoroutine(Run());
            }
            catch (Exception exception) { Fail(exception); Finish(); }
        }

        private IEnumerator Run()
        {
            IEnumerator? scenario = _caseName switch
            {
                "RealRoom_FourPlayersSpectatorAndRoleSwitchUseIndependentSessions" => _suite!.RealRoom_FourPlayersSpectatorAndRoleSwitchUseIndependentSessions(),
                "RealRoom_DelayedOldRawReceiveIsRejectedByNewBinding" => _suite!.RealRoom_DelayedOldRawReceiveIsRejectedByNewBinding(),
                "RealRoom_ColdReopenReconcilesOriginalRequestWithoutDuplicate" => _suite!.RealRoom_ColdReopenReconcilesOriginalRequestWithoutDuplicate(),
                "RealRoom_LegalSleeveAndFeeOwnershipAcrossFourSeatsAndSpectator" => _suite!.RealRoom_LegalSleeveAndFeeOwnershipAcrossFourSeatsAndSpectator(),
                "RealRoom_ReleasedSeatAiAdvancesAfterPlayerCloseWithSpectatorOnly" => _suite!.RealRoom_ReleasedSeatAiAdvancesAfterPlayerCloseWithSpectatorOnly(),
                "RealRoom_OpenDecisionDeadlineContinuesAfterPlayerClose" => _suite!.RealRoom_OpenDecisionDeadlineContinuesAfterPlayerClose(),
                "RealRoom_ClosedPlayerToSpectatorToNewPlayerReceivePathRejectsOldRaw" => _suite!.RealRoom_ClosedPlayerToSpectatorToNewPlayerReceivePathRejectsOldRaw(),
                _ => null
            };
            if (scenario == null) { Fail(new InvalidOperationException("Case dispatch failed.")); Finish(); yield break; }
            IEnumerator core = RunCore(scenario);
            while (true)
            {
                bool moved;
                try { moved = core.MoveNext(); }
                catch (Exception exception) { Fail(exception); moved = false; }
                if (!moved) break;
                yield return core.Current;
            }
            Finish();
        }

        private IEnumerator RunCore(IEnumerator scenario)
        {
            var stack = new Stack<IEnumerator>(); stack.Push(scenario);
            try
            {
                while (stack.Count > 0)
                {
                    IEnumerator current = stack.Peek();
                    bool moved = current.MoveNext();
                    if (!moved) { (current as IDisposable)?.Dispose(); stack.Pop(); continue; }
                    if (current.Current is IEnumerator nested) stack.Push(nested);
                    else yield return current.Current;
                }
                _log!.WriteLine("{\"event\":\"case_complete\",\"case\":\"" + _caseName + "\"}");
                _exit = 0;
            }
            finally { while (stack.Count > 0) { (stack.Pop() as IDisposable)?.Dispose(); } }
        }

        private void Fail(Exception exception)
        {
            _exit = 72;
            Debug.LogError("C04_PRIVACY_PLAYER_CASE_FAILED " + exception);
            if (_log != null) _log.WriteLine("{\"event\":\"case_failed\",\"type\":\"" + exception.GetType().Name + "\",\"message\":\"" + Json(exception.Message) + "\"}");
        }

        private void Finish()
        {
            if (_done) return; _done = true;
            try { _log?.Flush(); _log?.Dispose(); } catch { _exit = 73; }
            try
            {
                File.WriteAllText(Path.Combine(_evidence, "summary.json"), "{\"schemaVersion\":1,\"case\":\"" + _caseName + "\",\"exitCode\":" + _exit + ",\"evidence\":\"player-case.jsonl\"}", new UTF8Encoding(false));
            }
            catch { _exit = 73; }
            Application.Quit(_exit);
        }

        private static int Count(string[] args, string key) { int count = 0; foreach (string arg in args) if (string.Equals(arg,key,StringComparison.Ordinal)) count++; return count; }
        private static string Required(string[] args, string key) { int index = Array.IndexOf(args,key); if (index < 0 || index + 1 >= args.Length || args[index+1].StartsWith("-",StringComparison.Ordinal)) throw new ArgumentException("Missing value for " + key); if (Array.LastIndexOf(args,key) != index) throw new ArgumentException("Duplicate argument " + key); return args[index+1]; }
        private static string Json(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
    }
}
