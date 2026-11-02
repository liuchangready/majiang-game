#nullable enable
using System;
using System.Collections;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Unity.App;
using Mahjong.Unity.Table;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Mahjong.Unity.Tests.PlayMode
{
    /// <summary>Real LocalHost/Client action-path regression; never a scripted channel.</summary>
    public sealed class C04PlayerActionScenarioTests
    {
        [UnityTest] public IEnumerator Pon_FixedPlayerActionIsAppliedAndVisible() => RunCase("C04-G2-01", "pon", PlayerActionKind.DeclarePon, ProtocolEventType.PonDeclared);
        [UnityTest] public IEnumerator Chi_FixedPlayerActionIsAppliedAndVisible() => RunCase("C04-G2-02", "chi", PlayerActionKind.DeclareChi, ProtocolEventType.ChiDeclared);
        [UnityTest] public IEnumerator Riichi_FixedPlayerActionIsAppliedAndVisible() => RunCase("C04-G2-03", "riichi", PlayerActionKind.DeclareRiichi, ProtocolEventType.RiichiDeclared);
        [UnityTest] public IEnumerator ClosedKan_FixedPlayerActionAndReplacementDrawAreApplied() => RunCase("C04-G2-04", "closed-kan", PlayerActionKind.DeclareKan, ProtocolEventType.KanDeclared);

        private static IEnumerator RunCase(string caseId, string scenario, PlayerActionKind action, ProtocolEventType requiredEvent)
        {
            AppRoot? previous = AppRoot.Instance;
            if (previous != null) UnityEngine.Object.DestroyImmediate(previous.gameObject);
            yield return null;
            string dataRoot = Path.Combine(Application.temporaryCachePath, "c04-playmode-" + Guid.NewGuid().ToString("N"));
            LocalTableChannelFactory.EnablePlayerActionScenario(scenario);
            AppRoot app = AppRoot.EnsureInitialized(new AppConfig(), null, dataRoot);
            try
            {
                AsyncOperation bootLoad = SceneManager.LoadSceneAsync("Boot", LoadSceneMode.Single);
                Assert.IsNotNull(bootLoad, "The frozen enabled Boot scene must be loadable by the real application entry point.");
                while (!bootLoad!.isDone) yield return null;
                float deadline = Time.realtimeSinceStartup + 30f;
                while (app.Router.CurrentScene != SceneId.Menu && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.AreEqual(SceneId.Menu, app.Router.CurrentScene);
                Assert.IsTrue(Click("PlayButton"));
                deadline = Time.realtimeSinceStartup + 45f;
                while (app.Router.CurrentScene != SceneId.Lobby && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.AreEqual(SceneId.Lobby, app.Router.CurrentScene);
                Assert.IsTrue(Click("StartLocalGameButton"));
                deadline = Time.realtimeSinceStartup + 60f;
                while (app.Router.CurrentScene != SceneId.Table && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.AreEqual(SceneId.Table, app.Router.CurrentScene);

                SessionCoordinator? coordinator = app.Composition.SessionCoordinator;
                deadline = Time.realtimeSinceStartup + 45f;
                while ((coordinator == null || coordinator.Current.State != TableConnectionState.Ready || coordinator.Current.PlayerSnapshot == null) &&
                       Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                    coordinator = app.Composition.SessionCoordinator;
                }
                Assert.IsNotNull(coordinator);
                Assert.AreEqual(TableConnectionState.Ready, coordinator!.Current.State);

                var mapper = new PlayerTableProjectionMapper(new SystemTableClientClock());
                var factory = new TableCommandIntentFactory(new SystemTableClientClock());
                PlayerTableViewState view = mapper.Project(coordinator.Current);
                PlayerLegalActionState? candidate = Candidate(view, action);
                deadline = Time.realtimeSinceStartup + 30f;
                while (candidate == null && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                    coordinator = app.Composition.SessionCoordinator;
                    if (coordinator == null) break;
                    view = mapper.Project(coordinator.Current);
                    candidate = Candidate(view, action);
                }
                Assert.IsNotNull(candidate, "The fixed legal candidate must come from the accepted Player snapshot.");
                Assert.AreEqual(0, view.Identity.Seat);
                var baselineSnapshot = coordinator.Current.PlayerSnapshot!;

                string requestId = "c04-playmode-" + caseId + "-1";
                var owner = new TableCommandOwnerContext(new RequestId(requestId), new RequestScopeId(view.Identity.ScopeId),
                    new StateVersion(view.Identity.StateVersion), new OwnerEpoch(view.Identity.OwnerEpoch));
                TableCommandIntent intent = factory.CreateSubmitAction(view, candidate!, owner);
                using var observer = new ConfirmedVisibleEventObserver(coordinator);
                EventHandler<TableReceiptResolvedEventArgs> receiptHandler = (_, args) => observer.RecordReceipt(args);
                coordinator.ReceiptResolved += receiptHandler;
                observer.BeginAction(coordinator.Current, requestId, view.Identity.ScopeId, action);
                Task<CommandSubmission> submit = coordinator.SubmitAsync(intent.Command, CancellationToken.None);
                deadline = Time.realtimeSinceStartup + 45f;
                while (!submit.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.IsTrue(submit.IsCompleted && !submit.IsFaulted && submit.Result.Sent);

                ConfirmedVisibleActionEvent? confirmed = null;
                while (confirmed == null && Time.realtimeSinceStartup < deadline)
                {
                    confirmed = observer.TryGetConfirmation();
                    yield return null;
                }
                coordinator.ReceiptResolved -= receiptHandler;
                Assert.IsTrue(observer.HasAppliedReceipt, observer.ReceiptSummary);
                Assert.IsNotNull(confirmed, "Applied without the corresponding visible action event must fail.");
                Assert.AreEqual(requiredEvent, confirmed!.Event.Type);
                Assert.AreEqual(0, confirmed.Event.Seat);
                Assert.AreEqual(view.Identity.RoundId, confirmed.Batch.RoundId);
                Assert.AreEqual(view.Identity.ScopeId, confirmed.Batch.ScopeId);
                Assert.AreEqual(view.Identity.BindingEpoch, confirmed.Batch.BindingEpoch);
                if (action == PlayerActionKind.DeclareKan)
                {
                    deadline = Time.realtimeSinceStartup + 15f;
                    PlayerTableViewState after = mapper.Project(coordinator.Current);
                    while (after.Own?.DrawnTileId == null && Time.realtimeSinceStartup < deadline)
                    {
                        yield return null;
                        after = mapper.Project(coordinator.Current);
                    }
                    Assert.IsNotNull(after.Own?.DrawnTileId, "Closed Kan must be followed by the authority's replacement draw.");
                    Assert.AreEqual(73u, after.Own!.DrawnTileId);
                    Assert.AreEqual(PlayerMeldKind.ClosedKan, after.OwnMelds[0].Kind);
                    CollectionAssert.AreEquivalent(new uint[] { 0, 1, 2, 3 }, after.OwnMelds[0].TileIds);
                    Assert.IsFalse(ContainsAny(after.Own!.ClosedTiles, new uint[] { 0, 1, 2, 3 }),
                        "Kan entities must leave the closed hand in the authoritative post-state.");
                }
                else if (action == PlayerActionKind.DeclareRiichi)
                {
                    Assert.IsTrue(coordinator.Current.PlayerSnapshot!.PublicView.Board.Players[0].Riichi);
                    Assert.AreEqual(1u, coordinator.Current.PlayerSnapshot.PublicView.Board.RiichiSticks);
                    Assert.AreEqual(25_000L, baselineSnapshot.PublicView.Board.Scores[0].Score);
                    Assert.AreEqual(24_000L, coordinator.Current.PlayerSnapshot.PublicView.Board.Scores[0].Score);
                }
                else
                {
                    Assert.AreEqual(action == PlayerActionKind.DeclarePon ? PlayerMeldKind.Pon : PlayerMeldKind.Chi,
                        mapper.Project(coordinator.Current).OwnMelds[0].Kind);
                    uint[] expectedTiles = action == PlayerActionKind.DeclarePon ? new uint[] { 0, 1, 2 } : new uint[] { 0, 4, 8 };
                    PlayerTableViewState after = mapper.Project(coordinator.Current);
                    CollectionAssert.AreEquivalent(expectedTiles, after.OwnMelds[0].TileIds);
                    Assert.IsFalse(ContainsAny(after.Own!.ClosedTiles, expectedTiles),
                        "Claimed entities must leave the closed hand in the authoritative post-state.");
                }
            }
            finally
            {
                LocalTableChannelFactory.DisablePlayerActionScenarioForTest();
                try { _ = app.Composition.ShutdownAsync("C04_PLAYMODE_END"); } catch { }
                UnityEngine.Object.DestroyImmediate(app.gameObject);
            }
        }

        private static PlayerLegalActionState? Candidate(PlayerTableViewState view, PlayerActionKind kind)
        {
            if (view.Own?.Decision == null || view.Identity.Seat != 0) return null;
            foreach (PlayerLegalActionState candidate in view.Own.Decision.Candidates)
            {
                if (candidate.Action == kind && (kind != PlayerActionKind.DeclareKan || candidate.OptionId.StartsWith("closed-kan-", StringComparison.Ordinal)))
                    return candidate;
            }
            return null;
        }

        private static bool Click(string name)
        {
            GameObject? target = GameObject.Find(name);
            UnityEngine.UI.Button? button = target != null ? target.GetComponent<UnityEngine.UI.Button>() : null;
            if (button == null || !button.interactable) return false;
            button.onClick.Invoke();
            return true;
        }

        private static bool ContainsAny(System.Collections.Generic.IReadOnlyList<uint> actual, uint[] expected)
        {
            for (int i = 0; i < actual.Count; i++)
                for (int j = 0; j < expected.Length; j++)
                    if (actual[i] == expected[j]) return true;
            return false;
        }
    }
}
