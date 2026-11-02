#nullable enable
using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Unity.App;
using Mahjong.Unity.UI;
using Mahjong.Unity.UI.Session;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

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
