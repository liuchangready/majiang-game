#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using Mahjong.Client;
using Mahjong.Contracts.Wire;
using ProtocolConstants = Mahjong.Contracts.ProtocolConstants;
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
    public sealed class SettlementPlayModeTests
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

        private TableSessionBinding CreateBinding(string sessionId, string roomId = "room_tsumo", ProtocolRole role = ProtocolRole.Spectator, int seat = -1)
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

        private void WireSettlementUi(SettlementPresenter presenter, Canvas canvas)
        {
            if (presenter.TitleText != null && presenter.PrivateLedger != null)
            {
                return;
            }

            var roundPanel = new GameObject("RoundSettlementPanel");
            roundPanel.transform.SetParent(canvas.transform, false);

            var titleGo = new GameObject("TitleText");
            titleGo.transform.SetParent(roundPanel.transform, false);
            var titleText = titleGo.AddComponent<TMPro.TextMeshProUGUI>();
            titleText.rectTransform.sizeDelta = new Vector2(800, 50);
            titleText.rectTransform.anchoredPosition = new Vector2(0, 260);

            var roundInfoGo = new GameObject("RoundInfoText");
            roundInfoGo.transform.SetParent(roundPanel.transform, false);
            var roundInfoText = roundInfoGo.AddComponent<TMPro.TextMeshProUGUI>();
            roundInfoText.rectTransform.sizeDelta = new Vector2(800, 40);
            roundInfoText.rectTransform.anchoredPosition = new Vector2(0, 210);

            var winSummaryGo = new GameObject("WinSummaryText");
            winSummaryGo.transform.SetParent(roundPanel.transform, false);
            var winSummaryText = winSummaryGo.AddComponent<TMPro.TextMeshProUGUI>();
            winSummaryText.rectTransform.sizeDelta = new Vector2(800, 40);
            winSummaryText.rectTransform.anchoredPosition = new Vector2(0, 160);

            var yakuListGo = new GameObject("YakuListText");
            yakuListGo.transform.SetParent(roundPanel.transform, false);
            var yakuListText = yakuListGo.AddComponent<TMPro.TextMeshProUGUI>();
            yakuListText.rectTransform.sizeDelta = new Vector2(800, 120);
            yakuListText.rectTransform.anchoredPosition = new Vector2(0, 70);

            var scoreBreakdownGo = new GameObject("ScoreBreakdownText");
            scoreBreakdownGo.transform.SetParent(roundPanel.transform, false);
            var scoreBreakdownText = scoreBreakdownGo.AddComponent<TMPro.TextMeshProUGUI>();
            scoreBreakdownText.rectTransform.sizeDelta = new Vector2(800, 48);
            scoreBreakdownText.rectTransform.anchoredPosition = new Vector2(0, -10);

            var paymentsGo = new GameObject("PaymentsText");
            paymentsGo.transform.SetParent(roundPanel.transform, false);
            var paymentsText = paymentsGo.AddComponent<TMPro.TextMeshProUGUI>();
            paymentsText.rectTransform.sizeDelta = new Vector2(800, 80);
            paymentsText.rectTransform.anchoredPosition = new Vector2(0, -80);

            var newScoresGo = new GameObject("NewScoresText");
            newScoresGo.transform.SetParent(roundPanel.transform, false);
            var newScoresText = newScoresGo.AddComponent<TMPro.TextMeshProUGUI>();
            newScoresText.rectTransform.sizeDelta = new Vector2(800, 40);
            newScoresText.rectTransform.anchoredPosition = new Vector2(0, -120);

            var matchSummaryRoot = new GameObject("MatchSummaryRoot");
            matchSummaryRoot.transform.SetParent(canvas.transform, false);
            var matchRankingsGo = new GameObject("MatchRankingsText");
            matchRankingsGo.transform.SetParent(matchSummaryRoot.transform, false);
            var matchRankingsText = matchRankingsGo.AddComponent<TMPro.TextMeshProUGUI>();
            matchRankingsText.rectTransform.sizeDelta = new Vector2(800, 180);
            matchRankingsText.rectTransform.anchoredPosition = new Vector2(0, -20);

            // Ledger UI
            var ledgerRoot = new GameObject("LedgerRoot");
            ledgerRoot.transform.SetParent(canvas.transform, false);
            var ledgerPresenter = ledgerRoot.AddComponent<PrivateLedgerPresenter>();

            var balanceGo = new GameObject("BalanceText");
            balanceGo.transform.SetParent(ledgerRoot.transform, false);
            var balanceText = balanceGo.AddComponent<TMPro.TextMeshProUGUI>();
            balanceText.rectTransform.sizeDelta = new Vector2(400, 30);
            balanceText.rectTransform.anchoredPosition = new Vector2(0, -140);

            var entriesGo = new GameObject("EntriesText");
            entriesGo.transform.SetParent(ledgerRoot.transform, false);
            var entriesText = entriesGo.AddComponent<TMPro.TextMeshProUGUI>();
            entriesText.rectTransform.sizeDelta = new Vector2(800, 100);
            entriesText.rectTransform.anchoredPosition = new Vector2(0, -210);

            var authStatusGo = new GameObject("AuthStatusText");
            authStatusGo.transform.SetParent(ledgerRoot.transform, false);
            var authStatusText = authStatusGo.AddComponent<TMPro.TextMeshProUGUI>();
            authStatusText.rectTransform.sizeDelta = new Vector2(400, 30);
            authStatusText.rectTransform.anchoredPosition = new Vector2(0, -270);

            var localGameGo = new GameObject("LocalGameTag");
            localGameGo.transform.SetParent(ledgerRoot.transform, false);
            var localGameTag = localGameGo.AddComponent<TMPro.TextMeshProUGUI>();
            localGameTag.rectTransform.sizeDelta = new Vector2(400, 30);
            localGameTag.rectTransform.anchoredPosition = new Vector2(0, -300);

            ledgerPresenter.Configure(
                ledgerRoot: ledgerRoot,
                balanceText: balanceText,
                entriesText: entriesText,
                authStatusText: authStatusText,
                localGameTag: localGameTag
            );

            presenter.Configure(
                titleText: titleText,
                roundInfoText: roundInfoText,
                winSummaryText: winSummaryText,
                yakuListText: yakuListText,
                scoreBreakdownText: scoreBreakdownText,
                paymentsText: paymentsText,
                newScoresText: newScoresText,
                matchSummaryRoot: matchSummaryRoot,
                matchRankingsText: matchRankingsText,
                privateLedger: ledgerPresenter
            );
        }

        [UnityTest]
        public IEnumerator SettlementView_RoundSettlement_TsumoWin_RendersCorrectly()
        {
            var root = AppRoot.EnsureInitialized(new AppConfig());
            yield return LoadSceneAsync(root, SceneId.Settlement);

            var presenter = UnityEngine.Object.FindFirstObjectByType<SettlementPresenter>();
            Assert.IsNotNull(presenter, "SettlementPresenter must exist in Settlement scene.");

            var canvas = UnityEngine.Object.FindFirstObjectByType<Canvas>();
            Assert.IsNotNull(canvas);
            WireSettlementUi(presenter, canvas);

            var snapshot = new PublicSnapshot
            {
                SessionId = "sess_tsumo_1",
                RoundId = "round_E1_0",
                View = new PublicMatchView
                {
                    SettlementRoundId = "round_E1_0"
                }
            };

            var tsumoEnv = new PublicEventEnvelope
            {
                TsumoWin = new TsumoWin
                {
                    Player = 0,
                    WinTileId = 5,
                    Han = 3,
                    Fu = 30,
                    Score = 3900
                }
            };
            tsumoEnv.TsumoWin.Yaku.Add(new YakuView { Code = "TANYAO", Han = 1 });
            tsumoEnv.TsumoWin.Yaku.Add(new YakuView { Code = "PINFU", Han = 1 });
            tsumoEnv.TsumoWin.Yaku.Add(new YakuView { Code = "MENZEN_TSUMO", Han = 1 });
            snapshot.View.SettlementEvents.Add(tsumoEnv);

            var scoreEnv = new PublicEventEnvelope { ScoreSettled = new ScoreSettled() };
            scoreEnv.ScoreSettled.Payments.Add(new PaymentView { FromSeat = 1, ToSeat = 0, Amount = 1300 });
            scoreEnv.ScoreSettled.Payments.Add(new PaymentView { FromSeat = 2, ToSeat = 0, Amount = 1300 });
            scoreEnv.ScoreSettled.Payments.Add(new PaymentView { FromSeat = 3, ToSeat = 0, Amount = 1300 });
            scoreEnv.ScoreSettled.NewScores.Add(new ScoreView { Seat = 0, Score = 28900 });
            scoreEnv.ScoreSettled.NewScores.Add(new ScoreView { Seat = 1, Score = 23700 });
            scoreEnv.ScoreSettled.NewScores.Add(new ScoreView { Seat = 2, Score = 23700 });
            scoreEnv.ScoreSettled.NewScores.Add(new ScoreView { Seat = 3, Score = 23700 });
            snapshot.View.SettlementEvents.Add(scoreEnv);

            var binding = CreateBinding("sess_tsumo_1", "room_tsumo");
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

            StringAssert.Contains("自摸", presenter.RoundInfoText?.text);
            StringAssert.Contains("胜者席位: 0", presenter.WinSummaryText?.text);
            StringAssert.Contains("3900点", presenter.WinSummaryText?.text);
            StringAssert.Contains("断幺九", presenter.YakuListText?.text);
            StringAssert.Contains("平和", presenter.YakuListText?.text);
            StringAssert.Contains("门前清自摸和", presenter.YakuListText?.text);
            StringAssert.Contains("席位1 -> 席位0: 1300点", presenter.PaymentsText?.text);
            StringAssert.Contains("席位0: 28900点", presenter.NewScoresText?.text);

            // Capture Screenshot 02
            TestScreenshotHelper.CaptureFrame("02_round_settlement_tsumo.png");
            string shotPath = Path.Combine(TestScreenshotHelper.GetScreenshotDirectory(), "02_round_settlement_tsumo.png");
            Assert.IsTrue(File.Exists(shotPath), "Screenshot 02 must exist.");
            Assert.Greater(new FileInfo(shotPath).Length, 1000, "Screenshot 02 must be > 1KB.");
        }

        [UnityTest]
        public IEnumerator SettlementView_MatchRankings_Final_RendersCorrectly()
        {
            var root = AppRoot.EnsureInitialized(new AppConfig());
            yield return LoadSceneAsync(root, SceneId.Settlement);

            var presenter = UnityEngine.Object.FindFirstObjectByType<SettlementPresenter>();
            Assert.IsNotNull(presenter);

            var canvas = UnityEngine.Object.FindFirstObjectByType<Canvas>();
            Assert.IsNotNull(canvas);
            WireSettlementUi(presenter, canvas);

            var snapshot = new PublicSnapshot
            {
                SessionId = "sess_final_1",
                View = new PublicMatchView
                {
                    Session = new PublicSessionSummary
                    {
                        Finished = true,
                        FinishReason = "ROUND_LIMIT"
                    }
                }
            };
            snapshot.View.Session.Rankings.AddRange(new int[] { 2, 0, 1, 3 });
            snapshot.View.Session.FinalScores.Add(new ScoreView { Seat = 0, Score = 32000 });
            snapshot.View.Session.FinalScores.Add(new ScoreView { Seat = 1, Score = 15000 });
            snapshot.View.Session.FinalScores.Add(new ScoreView { Seat = 2, Score = 41000 });
            snapshot.View.Session.FinalScores.Add(new ScoreView { Seat = 3, Score = 12000 });

            var binding = CreateBinding("sess_final_1", "room_final");
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

            Assert.IsTrue(presenter.MatchSummaryRoot?.activeSelf ?? false, "Match summary root must be active on match finish.");
            StringAssert.Contains("第1位: 席位2 (41000点)", presenter.MatchRankingsText?.text);
            StringAssert.Contains("第2位: 席位0 (32000点)", presenter.MatchRankingsText?.text);
            StringAssert.Contains("第3位: 席位1 (15000点)", presenter.MatchRankingsText?.text);
            StringAssert.Contains("第4位: 席位3 (12000点)", presenter.MatchRankingsText?.text);

            // Capture Screenshot 03
            TestScreenshotHelper.CaptureFrame("03_match_rankings_final.png");
            string shotPath = Path.Combine(TestScreenshotHelper.GetScreenshotDirectory(), "03_match_rankings_final.png");
            Assert.IsTrue(File.Exists(shotPath), "Screenshot 03 must exist.");
            Assert.Greater(new FileInfo(shotPath).Length, 1000, "Screenshot 03 must be > 1KB.");
        }

        [UnityTest]
        public IEnumerator SettlementView_PrivateLedger_Authorized_RendersCorrectly()
        {
            var root = AppRoot.EnsureInitialized(new AppConfig());
            yield return LoadSceneAsync(root, SceneId.Settlement);

            var presenter = UnityEngine.Object.FindFirstObjectByType<SettlementPresenter>();
            Assert.IsNotNull(presenter);

            var canvas = UnityEngine.Object.FindFirstObjectByType<Canvas>();
            Assert.IsNotNull(canvas);
            WireSettlementUi(presenter, canvas);

            var playerSnap = new PlayerSnapshot
            {
                SessionId = "sess_player_auth",
                RoundId = "round_E1_0",
                PrivateView = new PlayerPrivateView
                {
                    Seat = 0,
                    Cheat = new PrivateCheatView
                    {
                        CheatPoints = 500
                    }
                }
            };
            playerSnap.PrivateView.Cheat.LedgerEntries.Add(new CheatEntryView
            {
                EntryId = "e1",
                Amount = -100,
                BalanceAfter = 400,
                ReasonCode = "TILE_MARK"
            });
            playerSnap.PrivateView.Cheat.LedgerEntries.Add(new CheatEntryView
            {
                EntryId = "e2",
                Amount = -200,
                BalanceAfter = 200,
                ReasonCode = "BEAM_SWAP"
            });

            // Player role + seat 0 + session match = Authorized
            var binding = CreateBinding("sess_player_auth", "room_cheat", ProtocolRole.Player, 0);
            var state = new SessionViewState(
                generation: 1,
                state: TableConnectionState.Ready,
                binding: binding,
                playerSnapshot: playerSnap,
                publicSnapshot: null,
                roomSnapshot: null,
                pendingCommands: null,
                lastError: null
            );

            presenter.UpdateSessionState(state);
            yield return null;
            Canvas.ForceUpdateCanvases();

            Assert.IsTrue(presenter.PrivateLedger?.IsVisible ?? false, "Ledger must be visible for authorized player.");
            StringAssert.Contains("500", presenter.PrivateLedger?.BalanceText?.text);
            StringAssert.Contains("落汗", presenter.PrivateLedger?.EntriesText?.text);
            StringAssert.Contains("偷梁换柱", presenter.PrivateLedger?.EntriesText?.text);
            Assert.IsFalse(presenter.PrivateLedger?.LocalGameTag?.gameObject.activeSelf ?? true, "LocalGameTag must not be shown when origin is not proven local.");

            // Capture Screenshot 04
            TestScreenshotHelper.CaptureFrame("04_private_ledger_authorized.png");
            string shotPath = Path.Combine(TestScreenshotHelper.GetScreenshotDirectory(), "04_private_ledger_authorized.png");
            Assert.IsTrue(File.Exists(shotPath), "Screenshot 04 must exist.");
            Assert.Greater(new FileInfo(shotPath).Length, 1000, "Screenshot 04 must be > 1KB.");
        }

        [UnityTest]
        public IEnumerator SettlementView_PrivateLedger_ClearedOnSpectator()
        {
            var root = AppRoot.EnsureInitialized(new AppConfig());
            yield return LoadSceneAsync(root, SceneId.Settlement);

            var presenter = UnityEngine.Object.FindFirstObjectByType<SettlementPresenter>();
            Assert.IsNotNull(presenter);

            var canvas = UnityEngine.Object.FindFirstObjectByType<Canvas>();
            Assert.IsNotNull(canvas);
            WireSettlementUi(presenter, canvas);

            var playerSnap = new PlayerSnapshot
            {
                SessionId = "sess_player_auth",
                RoundId = "round_E1_0",
                PrivateView = new PlayerPrivateView
                {
                    Seat = 0,
                    Cheat = new PrivateCheatView
                    {
                        CheatPoints = 500
                    }
                }
            };
            playerSnap.PrivateView.Cheat.LedgerEntries.Add(new CheatEntryView
            {
                EntryId = "e1",
                Amount = -100,
                BalanceAfter = 400,
                ReasonCode = "TILE_MARK"
            });

            // 1. First feed authorized player state -> must show ledger
            var bindingAuth = CreateBinding("sess_player_auth", "room_cheat", ProtocolRole.Player, 0);
            var authState = new SessionViewState(
                generation: 1,
                state: TableConnectionState.Ready,
                binding: bindingAuth,
                playerSnapshot: playerSnap,
                publicSnapshot: null,
                roomSnapshot: null,
                pendingCommands: null,
                lastError: null
            );

            presenter.UpdateSessionState(authState);
            yield return null;
            Canvas.ForceUpdateCanvases();

            Assert.IsTrue(presenter.PrivateLedger?.IsVisible ?? false, "Precondition: Ledger must first be visible when authorized.");
            StringAssert.Contains("500", presenter.PrivateLedger?.BalanceText?.text);

            // 2. Spectator role -> Unauthorized -> Private ledger must be hidden and cleared on the SAME component
            var bindingSpectator = CreateBinding("sess_player_auth", "room_cheat", ProtocolRole.Spectator, -1);
            var spectatorState = new SessionViewState(
                generation: 2,
                state: TableConnectionState.Ready,
                binding: bindingSpectator,
                playerSnapshot: playerSnap,
                publicSnapshot: null,
                roomSnapshot: null,
                pendingCommands: null,
                lastError: null
            );

            presenter.UpdateSessionState(spectatorState);
            yield return null;
            Canvas.ForceUpdateCanvases();

            Assert.IsFalse(presenter.PrivateLedger?.IsVisible ?? true, "Ledger must be HIDDEN for spectator.");
            Assert.IsEmpty(presenter.PrivateLedger?.BalanceText?.text ?? string.Empty, "Balance must be cleared.");
            Assert.IsEmpty(presenter.PrivateLedger?.EntriesText?.text ?? string.Empty, "Entries must be cleared.");

            // Capture Screenshot 05
            TestScreenshotHelper.CaptureFrame("05_private_ledger_cleared_spectator.png");
            string shotPath = Path.Combine(TestScreenshotHelper.GetScreenshotDirectory(), "05_private_ledger_cleared_spectator.png");
            Assert.IsTrue(File.Exists(shotPath), "Screenshot 05 must exist.");
            Assert.Greater(new FileInfo(shotPath).Length, 1000, "Screenshot 05 must be > 1KB.");
        }

        [UnityTest]
        public IEnumerator SettlementView_BackButton_RoutesToMenu()
        {
            var root = AppRoot.EnsureInitialized(new AppConfig());
            yield return LoadSceneAsync(root, SceneId.Settlement);

            var presenter = UnityEngine.Object.FindFirstObjectByType<SettlementPresenter>();
            Assert.IsNotNull(presenter);
            Assert.IsNotNull(presenter!.BackButton, "BackButton must be wired.");

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
        public IEnumerator SettlementView_MultiAspect_And_SafeArea_Bounds_Check()
        {
            var root = AppRoot.EnsureInitialized(new AppConfig());
            yield return LoadSceneAsync(root, SceneId.Settlement);

            var presenter = UnityEngine.Object.FindFirstObjectByType<SettlementPresenter>();
            Assert.IsNotNull(presenter, "SettlementPresenter must exist in Settlement scene.");

            var canvas = presenter!.GetComponentInParent<Canvas>();
            Assert.IsNotNull(canvas, "Canvas must exist.");

            // Supply realistic populated settlement snapshot
            var snap = new PublicSnapshot
            {
                SessionId = "settlement_multi",
                RoundId = "round_1",
                StateVersion = 2,
                EventSeq = 102,
                Cursor = new Mahjong.Contracts.Wire.StreamCursor { StreamVersion = 1, ProjectionSeq = 2 },
                View = new PublicMatchView
                {
                    Board = new PublicBoardView
                    {
                        WallRemaining = 40,
                        Phase = GamePhase.RoundEnd,
                        RoundWind = 0,
                        RoundNumber = 1,
                        Honba = 0,
                        RiichiSticks = 0,
                        DealerSeat = 0,
                        CurrentSeat = 0
                    },
                    Session = new PublicSessionSummary { Finished = true, FinishReason = "match_completed" },
                    SettlementRoundId = "round_1"
                }
            };
            for (int i = 0; i < 4; i++)
            {
                snap.View.Board.Players.Add(new PlayerPublicView { Seat = i, HandCount = 13 });
                snap.View.Board.Scores.Add(new ScoreView { Seat = i, Score = 25000 });
            }
            snap.View.Session.Rankings.AddRange(new int[] { 0, 1, 2, 3 });
            snap.View.Session.FinalScores.Add(new ScoreView { Seat = 0, Score = 35000 });
            snap.View.Session.FinalScores.Add(new ScoreView { Seat = 1, Score = 25000 });
            snap.View.Session.FinalScores.Add(new ScoreView { Seat = 2, Score = 22000 });
            snap.View.Session.FinalScores.Add(new ScoreView { Seat = 3, Score = 18000 });

            var settled = new ScoreSettled();
            settled.Payments.Add(new PaymentView { FromSeat = 1, ToSeat = 0, Amount = 3900 });

            snap.View.SettlementEvents.Add(new PublicEventEnvelope
            {
                EventType = Mahjong.Contracts.Wire.EventType.RonWin,
                EventSchemaVersion = ProtocolConstants.EventSchemaVersion,
                Visibility = EventVisibility.SettlementPublic,
                RonWin = new RonWin { Winner = 0, Loser = 1, Score = 3900, Han = 2, Fu = 30 }
            });
            snap.View.SettlementEvents.Add(new PublicEventEnvelope
            {
                EventType = Mahjong.Contracts.Wire.EventType.ScoreSettled,
                EventSchemaVersion = ProtocolConstants.EventSchemaVersion,
                Visibility = EventVisibility.SettlementPublic,
                ScoreSettled = settled
            });

            var playerBinding = PlayModeScriptedTableChannel.CreateBinding("settlement_multi", "room_multi", ProtocolRole.Player, 0);
            var playerSnap = PlayModeScriptedTableChannel.CreatePlayerSync("settlement_multi", "room_multi", 0, 1, 500).SyncResponse.Player;

            var state = new SessionViewState(1, TableConnectionState.Ready, playerBinding, playerSnap, snap, null, null, null);
            presenter.UpdateSessionState(state);

            // Aspect 1: 16:9 Standard (1280x720)
            Rect screen16x9 = new Rect(0, 0, 1280, 720);
            TestScreenshotHelper.ApplyViewport(canvas, 1280f, 720f, screen16x9);

            var backBtnRt = presenter.BackButton!.GetComponent<RectTransform>();
            var titleRt = presenter.TitleText!.GetComponent<RectTransform>();

            // Numeric assertion 1: Title and BackButton area and touch targets
            Vector3[] backCorners = new Vector3[4];
            backBtnRt.GetWorldCorners(backCorners);
            Assert.GreaterOrEqual(backCorners[2].x - backCorners[0].x, 80f, "16:9: Back button width >= 80");
            Assert.GreaterOrEqual(backCorners[2].y - backCorners[0].y, 36f, "16:9: Back button height >= 36");

            Vector3[] titleCorners = new Vector3[4];
            titleRt.GetWorldCorners(titleCorners);
            Assert.GreaterOrEqual(titleCorners[2].y, 300f, "16:9: Title text must be in top area");

            // Numeric assertion 2: All populated settlement texts (round, win, breakdown, payments, rankings, ledger) fit both viewports
            TestScreenshotHelper.AssertAllActiveTextCornersAndHeightsFitViewports(canvas);

            // Capture actual 21:9 ultrawide screenshot for verification
            TestScreenshotHelper.CaptureFrame("07_settlement_21x9_ultrawide.png", 2560, 1080);
            yield return null;
        }

        [UnityTest]
        public IEnumerator SettlementScene_PauseResume_Lifecycle_And_ButtonGating()
        {
            var root = AppRoot.EnsureInitialized(new AppConfig());
            yield return LoadSceneAsync(root, SceneId.Settlement);

            var presenter = UnityEngine.Object.FindFirstObjectByType<SettlementPresenter>();
            Assert.IsNotNull(presenter, "SettlementPresenter must exist.");
            Assert.IsNotNull(presenter!.BackButton, "BackButton must exist.");

            UiInputLifecycle.ResetForTest();

            // 1. Pause application / input lifecycle
            UiInputLifecycle.Instance.OnPause();
            Assert.IsFalse(UiInputLifecycle.Instance.AcceptingInput, "AcceptingInput must be false when paused");

            // Attempt submit during pause -> must be blocked
            ExecuteEvents.Execute(presenter.BackButton!.gameObject, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler);
            yield return null;
            Assert.AreEqual(SceneId.Settlement, root.Router.CurrentScene, "BackButton submit during pause must NOT navigate away from Settlement scene");

            // 2. Resume application / input lifecycle
            UiInputLifecycle.Instance.OnResume();
            Assert.IsTrue(UiInputLifecycle.Instance.AcceptingInput, "AcceptingInput must be restored on resume");

            // Submitting after resume must now succeed
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
    }
}
