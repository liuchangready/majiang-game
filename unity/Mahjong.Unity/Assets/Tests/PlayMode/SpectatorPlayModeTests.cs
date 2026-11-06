#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using TMPro;
using Mahjong.Client;
using Mahjong.Contracts.Wire;
using Mahjong.Unity.App;
using Mahjong.Unity.Spectator;
using Mahjong.Unity.UI;
using Mahjong.Unity.UI.Session;
using ProtocolRole = Mahjong.Contracts.ProtocolRole;
using RequestScope = Mahjong.Contracts.RequestScope;
using RequestScopeId = Mahjong.Contracts.RequestScopeId;
using RoomId = Mahjong.Contracts.RoomId;
using SessionId = Mahjong.Contracts.SessionId;
using ConnectionEpoch = Mahjong.Contracts.ConnectionEpoch;
using OwnerEpoch = Mahjong.Contracts.OwnerEpoch;
using BindingEpoch = Mahjong.Contracts.BindingEpoch;
using StreamBaseline = Mahjong.Contracts.StreamBaseline;
using StateVersion = Mahjong.Contracts.StateVersion;
using EventSeq = Mahjong.Contracts.EventSeq;
using StreamCursor = Mahjong.Contracts.StreamCursor;
using StreamVersion = Mahjong.Contracts.StreamVersion;
using ProjectionSeq = Mahjong.Contracts.ProjectionSeq;

namespace Mahjong.Unity.Tests.PlayMode
{
    public sealed class SpectatorPlayModeTests
    {
        [SetUp]
        public void SetUp()
        {
            UiThemeProvider.EnsureLineBreakingRules();
        }

        private IEnumerator LoadSceneAsync(AppRoot root, SceneId sceneId)
        {
            var task = root.Router.LoadAsync(sceneId);
            while (!task.IsCompleted)
            {
                yield return null;
            }
            Assert.IsTrue(task.Result, $"Failed to load scene {sceneId}");
            yield return null;
            Canvas.ForceUpdateCanvases();
        }

        private TableSessionBinding CreateBinding(string sessionId, string roomId = "room_888", ProtocolRole role = ProtocolRole.Spectator, int seat = -1)
        {
            return new TableSessionBinding(
                new SessionId(sessionId),
                new RoomId(roomId),
                "table_1",
                role,
                seat,
                new ConnectionEpoch(1),
                new OwnerEpoch(1),
                new BindingEpoch(1),
                new RequestScope(new RequestScopeId("scope_1"), "account_1", role, new RoomId(roomId), 1000, 2000),
                new StreamBaseline(new StateVersion(1), new EventSeq(1), new StreamCursor(new StreamVersion(1), new ProjectionSeq(1))));
        }

        [UnityTest]
        public IEnumerator SpectatorView_FullPublicStream_RendersFourSeats_CapturesScreenshot()
        {
            var root = AppRoot.EnsureInitialized(new AppConfig());
            yield return LoadSceneAsync(root, SceneId.Spectator);

            var presenter = UnityEngine.Object.FindFirstObjectByType<SpectatorPresenter>();
            Assert.IsNotNull(presenter, "SpectatorPresenter must exist in Spectator scene.");
            Assert.AreEqual(4, presenter.SeatPanels.Length, "Spectator scene must have 4 seat panels wired.");

            // Construct realistic Public Snapshot
            var snapshot = new PublicSnapshot
            {
                SessionId = "sess_spec_999",
                RoundId = "round_E1_0",
                View = new PublicMatchView
                {
                    Board = new PublicBoardView
                    {
                        Phase = GamePhase.Discard,
                        RoundWind = 0,
                        RoundNumber = 1,
                        Honba = 0,
                        RiichiSticks = 1,
                        DealerSeat = 0,
                        CurrentSeat = 0,
                        WallRemaining = 68,
                        Paused = false,
                    }
                }
            };

            snapshot.View.Board.DoraIndicators.AddRange(new uint[] { 5, 14 });
            snapshot.View.Board.Scores.Add(new ScoreView { Seat = 0, Score = 25000 });
            snapshot.View.Board.Scores.Add(new ScoreView { Seat = 1, Score = 24000 });
            snapshot.View.Board.Scores.Add(new ScoreView { Seat = 2, Score = 26000 });
            snapshot.View.Board.Scores.Add(new ScoreView { Seat = 3, Score = 25000 });

            var p0 = new PlayerPublicView { Seat = 0, HandCount = 13, Riichi = false };
            p0.Discards.Add(new DiscardView { TileId = 1, RiichiDiscard = false, Tsumogiri = false });
            p0.Discards.Add(new DiscardView { TileId = 2, RiichiDiscard = false, Tsumogiri = true });
            snapshot.View.Board.Players.Add(p0);

            var p1 = new PlayerPublicView { Seat = 1, HandCount = 10, Riichi = true };
            p1.Discards.Add(new DiscardView { TileId = 9, RiichiDiscard = true, Tsumogiri = false });
            var meld = new MeldView { Type = MeldType.Chi };
            meld.TileIds.AddRange(new uint[] { 11, 12, 13 });
            p1.Melds.Add(meld);
            p1.ConcealedMelds.Add(new ConcealedMeldView { Type = MeldType.ClosedKan, TileCount = 4, FromSeat = -1 }); // 1 ankan
            snapshot.View.Board.Players.Add(p1);

            var p2 = new PlayerPublicView { Seat = 2, HandCount = 13, Riichi = false };
            snapshot.View.Board.Players.Add(p2);

            var p3 = new PlayerPublicView { Seat = 3, HandCount = 13, Riichi = false };
            snapshot.View.Board.Players.Add(p3);

            snapshot.View.Clocks.Add(new ClockView { DecisionSeat = 0, DeadlineRemainingMs = 10000, ReserveRemainingMs = 20000 });

            var binding = CreateBinding("sess_spec_999", "room_888");
            var state = new SessionViewState(
                generation: 1,
                state: TableConnectionState.Ready,
                binding: binding,
                playerSnapshot: null,
                publicSnapshot: snapshot,
                roomSnapshot: null,
                pendingCommands: null,
                lastError: null
            );

            presenter.UpdateSessionState(state);
            yield return null;
            Canvas.ForceUpdateCanvases();

            // Assertions on public whitelist rendering
            StringAssert.Contains("room_888", presenter.SessionInfoText?.text);
            StringAssert.Contains("东1局", presenter.RoundStatusText?.text);
            StringAssert.Contains("68张", presenter.WallCountText?.text);
            StringAssert.Contains("5, 14", presenter.DoraIndicatorsText?.text);
            StringAssert.Contains("10s", presenter.ClocksText?.text);

            // Seat 0 assertions
            Assert.AreEqual("手牌: 13张", presenter.SeatPanels[0].HandCountText?.text);
            Assert.IsFalse(presenter.SeatPanels[0].RiichiBadge?.activeSelf ?? true);
            StringAssert.Contains("[庄]", presenter.SeatPanels[0].SeatNameText?.text);

            // Seat 1 assertions
            Assert.AreEqual("手牌: 10张", presenter.SeatPanels[1].HandCountText?.text);
            Assert.IsTrue(presenter.SeatPanels[1].RiichiBadge?.activeSelf ?? false);
            StringAssert.Contains("[吃:11,12,13]", presenter.SeatPanels[1].MeldsText?.text);
            StringAssert.Contains("暗杠x1", presenter.SeatPanels[1].AnkanPlaceholderText?.text);

            // Capture Screenshot 01
            TestScreenshotHelper.CaptureFrame("01_spectator_public_stream.png");
            string shotPath = Path.Combine(TestScreenshotHelper.GetScreenshotDirectory(), "01_spectator_public_stream.png");
            Assert.IsTrue(File.Exists(shotPath), "Screenshot 01 must exist.");
            Assert.Greater(new FileInfo(shotPath).Length, 1000, "Screenshot 01 must be > 1KB.");
        }

        [UnityTest]
        public IEnumerator SpectatorView_ConnectionAndVersionIncompatibilityBanners()
        {
            var root = AppRoot.EnsureInitialized(new AppConfig());
            yield return LoadSceneAsync(root, SceneId.Spectator);

            var presenter = UnityEngine.Object.FindFirstObjectByType<SpectatorPresenter>();
            Assert.IsNotNull(presenter);

            var incompRoot = presenter.IncompatibleBannerRoot;
            var incompText = presenter.IncompatibleBannerText;
            Assert.IsNotNull(incompRoot, "Incompatible banner root must exist in scene");
            Assert.IsNotNull(incompText, "Incompatible banner text must exist in scene");

            var binding = CreateBinding("sess_1", "room_1");

            // 1. Incompatible protocol
            var incompState = new SessionViewState(
                generation: 1,
                state: TableConnectionState.Faulted,
                binding: binding,
                playerSnapshot: null,
                publicSnapshot: null,
                roomSnapshot: null,
                pendingCommands: null,
                lastError: "Protocol version mismatch: V4 vs V5"
            );

            presenter.UpdateSessionState(incompState);
            yield return null;

            Assert.IsTrue(incompRoot.activeSelf, "Incompatible banner must be active on protocol version mismatch.");
            StringAssert.Contains("服务端协议版本不兼容", incompText.text);

            // 2. Normal protocol
            var normalState = new SessionViewState(
                generation: 2,
                state: TableConnectionState.Ready,
                binding: binding,
                playerSnapshot: null,
                publicSnapshot: null,
                roomSnapshot: null,
                pendingCommands: null,
                lastError: null
            );

            presenter.UpdateSessionState(normalState);
            yield return null;

            Assert.IsFalse(incompRoot.activeSelf, "Incompatible banner must be hidden when protocol is compatible.");
        }

        [UnityTest]
        public IEnumerator SpectatorView_BackButton_RoutesToMenu()
        {
            var root = AppRoot.EnsureInitialized(new AppConfig());
            yield return LoadSceneAsync(root, SceneId.Spectator);

            var presenter = UnityEngine.Object.FindFirstObjectByType<SpectatorPresenter>();
            Assert.IsNotNull(presenter);
            Assert.IsNotNull(presenter!.BackButton, "BackButton must be wired in Spectator scene.");

            presenter.BackButton!.onClick.Invoke();
            yield return null;

            float timeout = 3f;
            while (root.Router.CurrentScene != SceneId.Menu && timeout > 0f)
            {
                timeout -= Time.deltaTime;
                yield return null;
            }

            Assert.AreEqual(SceneId.Menu, root.Router.CurrentScene, "Back button must navigate back to Menu.");
        }

        [UnityTest]
        public IEnumerator SpectatorView_OnDestroy_UnsubscribesCleanly()
        {
            var root = AppRoot.EnsureInitialized(new AppConfig());
            yield return LoadSceneAsync(root, SceneId.Spectator);

            var presenter = UnityEngine.Object.FindFirstObjectByType<SpectatorPresenter>();
            Assert.IsNotNull(presenter);

            var testView = new TestSessionView();
            presenter!.Rebind(testView);

            // Destroy presenter
            UnityEngine.Object.DestroyImmediate(presenter.gameObject);
            yield return null;

            // Triggering Changed on testView after presenter is destroyed must NOT throw
            Assert.DoesNotThrow(() =>
            {
                testView.TriggerChanged(new SessionViewState(
                    generation: 99,
                    state: TableConnectionState.Ready,
                    binding: CreateBinding("sess_after_destroy"),
                    playerSnapshot: null,
                    publicSnapshot: null,
                    roomSnapshot: null,
                    pendingCommands: null,
                    lastError: null));
            }, "Invoking session change after presenter destruction must not throw because presenter unsubscribed cleanly.");
        }

        [UnityTest]
        public IEnumerator SpectatorView_Layout_DisjointPanels_AndNoOverlaps()
        {
            var root = AppRoot.EnsureInitialized(new AppConfig());
            yield return LoadSceneAsync(root, SceneId.Spectator);

            var presenter = UnityEngine.Object.FindFirstObjectByType<SpectatorPresenter>();
            Assert.IsNotNull(presenter);
            Assert.AreEqual(4, presenter!.SeatPanels.Length);

            // Verify Seat panels rects are mutually disjoint
            var rects = new Rect[4];
            for (int i = 0; i < 4; i++)
            {
                var rt = presenter.SeatPanels[i].root!.GetComponent<RectTransform>();
                rects[i] = new Rect(rt.anchoredPosition - rt.sizeDelta * 0.5f, rt.sizeDelta);
            }

            for (int i = 0; i < 4; i++)
            {
                for (int j = i + 1; j < 4; j++)
                {
                    Assert.IsFalse(rects[i].Overlaps(rects[j]), $"SeatPanel {i} and {j} must not overlap! R{i}={rects[i]}, R{j}={rects[j]}");
                }
            }

            // Verify Header widgets do not overlap horizontally in row 1
            var sessionRt = presenter.SessionInfoText!.rectTransform;
            var roundRt = presenter.RoundStatusText!.rectTransform;
            var doraRt = presenter.DoraIndicatorsText!.rectTransform;

            float sessionRight = sessionRt.anchoredPosition.x + sessionRt.sizeDelta.x * 0.5f;
            float roundLeft = roundRt.anchoredPosition.x - roundRt.sizeDelta.x * 0.5f;
            float roundRight = roundRt.anchoredPosition.x + roundRt.sizeDelta.x * 0.5f;
            float doraLeft = doraRt.anchoredPosition.x - doraRt.sizeDelta.x * 0.5f;

            Assert.LessOrEqual(sessionRight, roundLeft, "SessionInfoText right edge must be <= RoundStatusText left edge");
            Assert.LessOrEqual(roundRight, doraLeft, "RoundStatusText right edge must be <= DoraIndicatorsText left edge");

            // Verify ConnectionBanner does not overlap RoundStatusText
            var bannerText = presenter.ConnectionBanner!.GetComponentInChildren<TextMeshProUGUI>(true);
            var bannerRt = bannerText.rectTransform;
            var bannerCorners = new Vector3[4];
            var roundCorners = new Vector3[4];
            bannerRt.GetWorldCorners(bannerCorners);
            roundRt.GetWorldCorners(roundCorners);
            var bannerBounds = Rect.MinMaxRect(bannerCorners[0].x, bannerCorners[0].y, bannerCorners[2].x, bannerCorners[2].y);
            var roundBounds = Rect.MinMaxRect(roundCorners[0].x, roundCorners[0].y, roundCorners[2].x, roundCorners[2].y);
            Assert.IsFalse(bannerBounds.Overlaps(roundBounds), $"Connection banner {bannerBounds} must not overlap round status {roundBounds}");

            // Verify typical round status text fits available height without overflow
            presenter.RoundStatusText.text = "东1局 0本场 | 供托: 1000点 | 状态: 出牌阶段";
            presenter.RoundStatusText.ForceMeshUpdate();
            Assert.LessOrEqual(presenter.RoundStatusText.preferredHeight, roundRt.rect.height + 0.5f,
                $"Typical round status text ({presenter.RoundStatusText.preferredHeight}px) must fit within box height ({roundRt.rect.height}px)");
            yield return null;
        }

        [UnityTest]
        public IEnumerator SpectatorView_MultiAspect_And_SafeArea_Bounds_Check()
        {
            var root = AppRoot.EnsureInitialized(new AppConfig());
            yield return LoadSceneAsync(root, SceneId.Spectator);

            var presenter = UnityEngine.Object.FindFirstObjectByType<SpectatorPresenter>();
            Assert.IsNotNull(presenter, "SpectatorPresenter must exist in Spectator scene.");

            var canvas = presenter!.GetComponentInParent<Canvas>();
            Assert.IsNotNull(canvas, "Canvas must exist.");

            // Supply realistic populated match snapshot
            var publicSnap = new PublicSnapshot
            {
                SessionId = "spectator_multi",
                RoundId = "round_1",
                StateVersion = 1,
                EventSeq = 101,
                Cursor = new Mahjong.Contracts.Wire.StreamCursor { StreamVersion = 1, ProjectionSeq = 1 },
                View = new PublicMatchView
                {
                    Board = new PublicBoardView
                    {
                        WallRemaining = 68,
                        Phase = GamePhase.Discard,
                        RoundWind = 0,
                        RoundNumber = 1,
                        Honba = 0,
                        RiichiSticks = 0,
                        DealerSeat = 0,
                        CurrentSeat = 0
                    },
                    SettlementRoundId = "round_1"
                }
            };
            for (int i = 0; i < 4; i++)
            {
                publicSnap.View.Board.Players.Add(new PlayerPublicView { Seat = i, HandCount = 13, Riichi = false });
                publicSnap.View.Board.Scores.Add(new ScoreView { Seat = i, Score = 25000 });
            }
            publicSnap.View.Board.DoraIndicators.Add(11);
            publicSnap.View.Board.DoraIndicators.Add(21);

            var binding = CreateBinding("spectator_multi", "room_multi", ProtocolRole.Spectator, -1);
            var state = new SessionViewState(1, TableConnectionState.Ready, binding, null, publicSnap, null, null, null);
            presenter.UpdateSessionState(state);

            // Aspect 1: 16:9 Standard (1280x720)
            Rect screen16x9 = new Rect(0, 0, 1280, 720);
            TestScreenshotHelper.ApplyViewport(canvas, 1280f, 720f, screen16x9);

            var backBtnRt = presenter.BackButton!.GetComponent<RectTransform>();
            var bannerRt = presenter.ConnectionBanner!.GetComponent<RectTransform>();
            var roundRt = presenter.RoundStatusText!.GetComponent<RectTransform>();
            var headerRt = roundRt.parent.GetComponent<RectTransform>();

            // Numeric assertion 1: ConnectionBanner vs Header vertical disjointness
            Vector3[] bannerCorners = new Vector3[4];
            Vector3[] headerCorners = new Vector3[4];
            bannerRt.GetWorldCorners(bannerCorners);
            headerRt.GetWorldCorners(headerCorners);
            Assert.GreaterOrEqual(bannerCorners[0].y - headerCorners[2].y, 5f,
                $"16:9: Banner bottom ({bannerCorners[0].y}) must be strictly above Header top ({headerCorners[2].y}) by >= 5px");

            // Numeric assertion 2: RoundStatusText preferredHeight fits box
            presenter.RoundStatusText.ForceMeshUpdate();
            Assert.LessOrEqual(presenter.RoundStatusText.preferredHeight, roundRt.rect.height + 0.5f,
                $"16:9: Round status text ({presenter.RoundStatusText.preferredHeight}px) must fit within box height ({roundRt.rect.height}px)");

            // Numeric assertion 3: BackButton meets touch target and area
            Vector3[] backCorners = new Vector3[4];
            backBtnRt.GetWorldCorners(backCorners);
            Assert.GreaterOrEqual(backCorners[2].x - backCorners[0].x, 80f, "16:9: Back button width >= 80");
            Assert.GreaterOrEqual(backCorners[2].y - backCorners[0].y, 36f, "16:9: Back button height >= 36");

            // Numeric assertion 4: All populated header and four seat texts fit both viewports (1280x720 & 2560x1080)
            TestScreenshotHelper.AssertAllActiveTextCornersAndHeightsFitViewports(canvas);

            // Capture actual 21:9 ultrawide screenshot for verification
            TestScreenshotHelper.CaptureFrame("06_spectator_21x9_ultrawide.png", 2560, 1080);
            yield return null;
        }

        [UnityTest]
        public IEnumerator SpectatorScene_PauseResume_Lifecycle_And_ButtonGating()
        {
            var root = AppRoot.EnsureInitialized(new AppConfig());
            yield return LoadSceneAsync(root, SceneId.Spectator);

            var presenter = UnityEngine.Object.FindFirstObjectByType<SpectatorPresenter>();
            Assert.IsNotNull(presenter, "SpectatorPresenter must exist.");
            Assert.IsNotNull(presenter!.BackButton, "BackButton must exist.");

            UiInputLifecycle.ResetForTest();

            // 1. Pause application / input lifecycle
            UiInputLifecycle.Instance.OnPause();
            Assert.IsFalse(UiInputLifecycle.Instance.AcceptingInput, "AcceptingInput must be false when paused");

            // Attempt to trigger back navigation during pause -> must be blocked
            ExecuteEvents.Execute(presenter.BackButton!.gameObject, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler);
            yield return null;
            Assert.AreEqual(SceneId.Spectator, root.Router.CurrentScene, "BackButton submit during pause must NOT navigate away from Spectator scene");

            // 2. Resume application / input lifecycle
            UiInputLifecycle.Instance.OnResume();
            Assert.IsTrue(UiInputLifecycle.Instance.AcceptingInput, "AcceptingInput must be restored on resume");

            // Submitting back button after resume must now succeed
            ExecuteEvents.Execute(presenter.BackButton.gameObject, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler);
            yield return null;

            float timeout = 3f;
            while (root.Router.CurrentScene != SceneId.Menu && timeout > 0f)
            {
                timeout -= Time.deltaTime;
                yield return null;
            }
            Assert.AreEqual(SceneId.Menu, root.Router.CurrentScene, "BackButton submit after resume must navigate to Menu scene");
        }

        private sealed class TestSessionView : ISessionView
        {
            public SessionViewState Current { get; set; } = new SessionViewState(0, TableConnectionState.Ready, null, null, null, null, null, null);
            public event EventHandler<SessionViewState>? Changed;

            public void TriggerChanged(SessionViewState state)
            {
                Current = state;
                Changed?.Invoke(this, state);
            }
        }
    }
}
