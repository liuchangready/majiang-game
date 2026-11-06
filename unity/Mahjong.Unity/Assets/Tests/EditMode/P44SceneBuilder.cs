#nullable enable
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.InputSystem;
using TMPro;
using Mahjong.Unity.App;
using Mahjong.Unity.UI;
using Mahjong.Unity.Spectator;

namespace Mahjong.Unity.Tests.EditMode
{
    public static class P44SceneBuilder
    {
        public const string SpectatorScenePath = "Assets/Scenes/Spectator.unity";
        public const string SettlementScenePath = "Assets/Scenes/Settlement.unity";
        public const string FontAssetPath = "Assets/Art/UI/Fonts/NotoSansSC-Regular SDF.asset";
        public const string InputActionsPath = "Assets/Settings/InputSystem_Actions.inputactions";

        [MenuItem("Mahjong/Build P4.4 Scenes")]
        public static void BuildScenes()
        {
            var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath);
            if (font == null)
            {
                throw new InvalidOperationException($"Required TMP_FontAsset not found at {FontAssetPath}");
            }

            var inputActions = AssetDatabase.LoadAssetAtPath<InputActionAsset>(InputActionsPath);
            if (inputActions == null)
            {
                throw new InvalidOperationException($"Required InputActionAsset not found at {InputActionsPath}");
            }

            BuildSpectatorScene(font, inputActions);
            BuildSettlementScene(font, inputActions);

            AssetDatabase.SaveAssets();
            Debug.Log("[P44SceneBuilder] Both Spectator and Settlement scenes built successfully.");
        }

        private static void BuildSpectatorScene(TMP_FontAsset font, InputActionAsset inputActions)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // 1. Camera
            var camGo = new GameObject("Main Camera");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.08f, 0.12f, 0.10f, 1f);
            camGo.tag = "MainCamera";

            // 2. EventSystem
            var eventGo = new GameObject("EventSystem");
            eventGo.AddComponent<EventSystem>();
            var inputModule = eventGo.AddComponent<InputSystemUIInputModule>();
            inputModule.actionsAsset = inputActions;

            // 3. Canvas
            var canvasGo = new GameObject("Canvas");
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            scaler.matchWidthOrHeight = 0.5f;
            canvasGo.AddComponent<GraphicRaycaster>();

            // Background
            var bgGo = CreateUiObject("Background", canvasGo.transform);
            var bgRect = bgGo.GetComponent<RectTransform>();
            SetStretch(bgRect);
            var bgImg = bgGo.AddComponent<Image>();
            bgImg.color = new Color(0.07f, 0.11f, 0.09f, 1f);

            // SafeAreaRoot & DesignRoot
            var safeRootGo = CreateUiObject("SafeAreaRoot", canvasGo.transform);
            var safeRect = safeRootGo.GetComponent<RectTransform>();
            SetStretch(safeRect);
            var fitter = safeRootGo.AddComponent<SafeAreaFitter>();

            var designRootGo = CreateUiObject("DesignRoot", safeRootGo.transform);
            var designRect = designRootGo.GetComponent<RectTransform>();
            designRect.sizeDelta = new Vector2(1280, 720);
            designRect.anchorMin = new Vector2(0.5f, 0.5f);
            designRect.anchorMax = new Vector2(0.5f, 0.5f);
            designRect.pivot = new Vector2(0.5f, 0.5f);

            var soFitter = new SerializedObject(fitter);
            soFitter.FindProperty("_canvasScaler").objectReferenceValue = scaler;
            soFitter.FindProperty("_designRoot").objectReferenceValue = designRect;
            soFitter.ApplyModifiedPropertiesWithoutUndo();

            // SceneGuard (ReadOnlySceneBinding)
            var guardGo = new GameObject("SceneGuard");
            guardGo.transform.SetParent(designRootGo.transform, false);
            var guardBinding = guardGo.AddComponent<ReadOnlySceneBinding>();

            // Header Panel
            var headerGo = CreateUiObject("HeaderPanel", designRootGo.transform);
            var headerRect = headerGo.GetComponent<RectTransform>();
            headerRect.anchoredPosition = new Vector2(0, 290);
            headerRect.sizeDelta = new Vector2(1240, 76);

            var sessionInfo = CreateText("SessionInfoText", headerGo.transform, font, 13.5f, TextAlignmentOptions.Left,
                new Vector2(-390, 14), new Vector2(430, 44), "观战公共流 | 房间: 等待 | 会话: 等待", Color.white);

            var roundStatus = CreateText("RoundStatusText", headerGo.transform, font, 17, TextAlignmentOptions.Center,
                new Vector2(90, 18), new Vector2(500, 36), "等待对局开始...", new Color(1f, 0.84f, 0f), FontStyles.Bold);

            var doraText = CreateText("DoraIndicatorsText", headerGo.transform, font, 15, TextAlignmentOptions.Right,
                new Vector2(480, 18), new Vector2(240, 34), "宝牌指示牌: [无]", new Color(1f, 0.54f, 0.54f));

            var wallText = CreateText("WallCountText", headerGo.transform, font, 15, TextAlignmentOptions.Left,
                new Vector2(-400, -22), new Vector2(300, 22), "牌山余量: 0张", new Color(0.88f, 0.88f, 0.88f));

            var clocksText = CreateText("ClocksText", headerGo.transform, font, 15, TextAlignmentOptions.Center,
                new Vector2(60, -22), new Vector2(320, 22), "行动倒计时: 0s", new Color(0.88f, 0.88f, 0.88f));

            var pauseGo = CreateUiObject("PauseIndicator", headerGo.transform);
            var pauseRect = pauseGo.GetComponent<RectTransform>();
            pauseRect.anchoredPosition = new Vector2(460, -22);
            pauseRect.sizeDelta = new Vector2(180, 22);
            var pauseText = CreateText("Text", pauseGo.transform, font, 14, TextAlignmentOptions.Center,
                Vector2.zero, new Vector2(180, 22), "【对局暂停中】", new Color(1f, 0.32f, 0.32f), FontStyles.Bold);
            pauseGo.SetActive(false);

            // ConnectionBanner (placed at top edge, strictly above HeaderPanel)
            var bannerGo = CreateUiObject("ConnectionBanner", designRootGo.transform);
            var bannerRect = bannerGo.GetComponent<RectTransform>();
            bannerRect.anchoredPosition = new Vector2(0, 348);
            bannerRect.sizeDelta = new Vector2(360, 20);
            var bannerPresenter = bannerGo.AddComponent<ConnectionBannerPresenter>();
            var bannerText = CreateText("BannerText", bannerGo.transform, font, 13, TextAlignmentOptions.Center,
                Vector2.zero, new Vector2(360, 20), "连接正常", new Color(0.6f, 0.9f, 0.6f));
            var soBanner = new SerializedObject(bannerPresenter);
            soBanner.FindProperty("_bannerRoot").objectReferenceValue = bannerGo;
            soBanner.FindProperty("_bannerText").objectReferenceValue = bannerText;
            soBanner.ApplyModifiedPropertiesWithoutUndo();

            // IncompatibleBanner
            var incompGo = CreateUiObject("IncompatibleBanner", designRootGo.transform);
            var incompRect = incompGo.GetComponent<RectTransform>();
            incompRect.anchoredPosition = new Vector2(0, 245);
            incompRect.sizeDelta = new Vector2(900, 26);
            var incompBg = incompGo.AddComponent<Image>();
            incompBg.color = new Color(0.6f, 0.1f, 0.1f, 0.85f);
            var incompText = CreateText("WarningText", incompGo.transform, font, 14, TextAlignmentOptions.Center,
                Vector2.zero, new Vector2(880, 24), "[严重警告] 服务端协议版本不兼容", Color.white, FontStyles.Bold);
            incompGo.SetActive(false);

            // 4 Seat Panels
            var seatConfigs = new (string name, Vector2 pos, Vector2 size)[]
            {
                ("SeatPanel_0", new Vector2(0, -175), new Vector2(540, 120)),    // South / Bottom
                ("SeatPanel_1", new Vector2(440, 0), new Vector2(360, 210)),     // East / Right
                ("SeatPanel_2", new Vector2(0, 175), new Vector2(540, 120)),     // North / Top
                ("SeatPanel_3", new Vector2(-440, 0), new Vector2(360, 210))     // West / Left
            };

            var seatPanels = new SpectatorSeatPanel[4];
            for (int i = 0; i < 4; i++)
            {
                var cfg = seatConfigs[i];
                var sGo = CreateUiObject(cfg.name, designRootGo.transform);
                var sRect = sGo.GetComponent<RectTransform>();
                sRect.anchoredPosition = cfg.pos;
                sRect.sizeDelta = cfg.size;
                var sImg = sGo.AddComponent<Image>();
                sImg.color = new Color(0.12f, 0.18f, 0.15f, 0.85f);

                bool isWide = cfg.size.x >= 400;
                float nameX = isWide ? -140 : -90;
                float scoreX = isWide ? 140 : 80;
                float handX = isWide ? -140 : -90;
                float riichiX = isWide ? 180 : 120;
                float nameWidth = isWide ? 220 : 160;
                float scoreWidth = isWide ? 220 : 180;

                var sName = CreateText("SeatName", sGo.transform, font, 16, TextAlignmentOptions.Left,
                    new Vector2(nameX, 43), new Vector2(nameWidth, 26), $"席位 {i}", Color.white, FontStyles.Bold);

                var sScore = CreateText("ScoreText", sGo.transform, font, 16, TextAlignmentOptions.Right,
                    new Vector2(scoreX, 43), new Vector2(scoreWidth, 26), "点数: 25000点", new Color(1f, 0.84f, 0f));

                var sHand = CreateText("HandCountText", sGo.transform, font, 14, TextAlignmentOptions.Left,
                    new Vector2(handX, 15), new Vector2(nameWidth, 22), "手牌: 13张", new Color(0.5f, 0.83f, 0.98f));

                var riichiBadgeGo = CreateUiObject("RiichiBadge", sGo.transform);
                var rRect = riichiBadgeGo.GetComponent<RectTransform>();
                rRect.anchoredPosition = new Vector2(riichiX, 15);
                rRect.sizeDelta = new Vector2(65, 22);
                var rText = CreateText("Text", riichiBadgeGo.transform, font, 13, TextAlignmentOptions.Center,
                    Vector2.zero, new Vector2(65, 22), "[立直]", new Color(1f, 0.32f, 0.32f), FontStyles.Bold);
                riichiBadgeGo.SetActive(false);

                var sDiscards = CreateText("DiscardsText", sGo.transform, font, 13, TextAlignmentOptions.Left,
                    new Vector2(0, -11), new Vector2(cfg.size.x - 20, 20), "牌河: 无", new Color(0.75f, 0.78f, 0.8f));

                var sMelds = CreateText("MeldsText", sGo.transform, font, 12, TextAlignmentOptions.Left,
                    new Vector2(0, -33), new Vector2(cfg.size.x - 20, 20), "副露: 门清", new Color(0.85f, 0.88f, 0.9f));

                var sAnkan = CreateText("AnkanPlaceholderText", sGo.transform, font, 11, TextAlignmentOptions.Left,
                    new Vector2(0, -53), new Vector2(cfg.size.x - 20, 18), string.Empty, new Color(0.6f, 0.7f, 0.75f));

                seatPanels[i] = new SpectatorSeatPanel
                {
                    root = sGo,
                    seatNameText = sName,
                    scoreText = sScore,
                    handCountText = sHand,
                    riichiBadge = riichiBadgeGo,
                    discardsText = sDiscards,
                    meldsText = sMelds,
                    ankanPlaceholderText = sAnkan
                };
            }

            // Buttons
            var backBtn = CreateButton("BackButton", designRootGo.transform, font, 18,
                new Vector2(-500, -325), new Vector2(160, 44), "返回菜单");

            var viewSettlementBtn = CreateButton("ViewSettlementButton", designRootGo.transform, font, 18,
                new Vector2(500, -325), new Vector2(160, 44), "查看结算");

            // SpectatorPresenter GameObject
            var presenterGo = new GameObject("SpectatorPresenter");
            presenterGo.transform.SetParent(designRootGo.transform, false);
            var presenter = presenterGo.AddComponent<SpectatorPresenter>();

            var so = new SerializedObject(presenter);
            so.FindProperty("_sessionInfoText").objectReferenceValue = sessionInfo;
            so.FindProperty("_roundStatusText").objectReferenceValue = roundStatus;
            so.FindProperty("_wallCountText").objectReferenceValue = wallText;
            so.FindProperty("_doraIndicatorsText").objectReferenceValue = doraText;
            so.FindProperty("_clocksText").objectReferenceValue = clocksText;
            so.FindProperty("_pauseIndicator").objectReferenceValue = pauseGo;

            var propSeats = so.FindProperty("_seatPanels");
            propSeats.arraySize = 4;
            for (int i = 0; i < 4; i++)
            {
                var elem = propSeats.GetArrayElementAtIndex(i);
                elem.FindPropertyRelative("root").objectReferenceValue = seatPanels[i].root;
                elem.FindPropertyRelative("seatNameText").objectReferenceValue = seatPanels[i].seatNameText;
                elem.FindPropertyRelative("scoreText").objectReferenceValue = seatPanels[i].scoreText;
                elem.FindPropertyRelative("handCountText").objectReferenceValue = seatPanels[i].handCountText;
                elem.FindPropertyRelative("riichiBadge").objectReferenceValue = seatPanels[i].riichiBadge;
                elem.FindPropertyRelative("discardsText").objectReferenceValue = seatPanels[i].discardsText;
                elem.FindPropertyRelative("meldsText").objectReferenceValue = seatPanels[i].meldsText;
                elem.FindPropertyRelative("ankanPlaceholderText").objectReferenceValue = seatPanels[i].ankanPlaceholderText;
            }

            so.FindProperty("_connectionBanner").objectReferenceValue = bannerPresenter;
            so.FindProperty("_incompatibleBannerRoot").objectReferenceValue = incompGo;
            so.FindProperty("_incompatibleBannerText").objectReferenceValue = incompText;
            so.FindProperty("_backButton").objectReferenceValue = backBtn;
            so.FindProperty("_viewSettlementButton").objectReferenceValue = viewSettlementBtn;
            so.ApplyModifiedPropertiesWithoutUndo();

            var soGuard = new SerializedObject(guardBinding);
            soGuard.FindProperty("_spectatorPresenter").objectReferenceValue = presenter;
            soGuard.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.SaveScene(scene, SpectatorScenePath);
        }

        private static void BuildSettlementScene(TMP_FontAsset font, InputActionAsset inputActions)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // 1. Camera
            var camGo = new GameObject("Main Camera");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.08f, 0.10f, 0.13f, 1f);
            camGo.tag = "MainCamera";

            // 2. EventSystem
            var eventGo = new GameObject("EventSystem");
            eventGo.AddComponent<EventSystem>();
            var inputModule = eventGo.AddComponent<InputSystemUIInputModule>();
            inputModule.actionsAsset = inputActions;

            // 3. Canvas
            var canvasGo = new GameObject("Canvas");
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            scaler.matchWidthOrHeight = 0.5f;
            canvasGo.AddComponent<GraphicRaycaster>();

            // Background
            var bgGo = CreateUiObject("Background", canvasGo.transform);
            var bgRect = bgGo.GetComponent<RectTransform>();
            SetStretch(bgRect);
            var bgImg = bgGo.AddComponent<Image>();
            bgImg.color = new Color(0.08f, 0.10f, 0.13f, 1f);

            // SafeAreaRoot & DesignRoot
            var safeRootGo = CreateUiObject("SafeAreaRoot", canvasGo.transform);
            var safeRect = safeRootGo.GetComponent<RectTransform>();
            SetStretch(safeRect);
            var fitter = safeRootGo.AddComponent<SafeAreaFitter>();

            var designRootGo = CreateUiObject("DesignRoot", safeRootGo.transform);
            var designRect = designRootGo.GetComponent<RectTransform>();
            designRect.sizeDelta = new Vector2(1280, 720);
            designRect.anchorMin = new Vector2(0.5f, 0.5f);
            designRect.anchorMax = new Vector2(0.5f, 0.5f);
            designRect.pivot = new Vector2(0.5f, 0.5f);

            var soFitter = new SerializedObject(fitter);
            soFitter.FindProperty("_canvasScaler").objectReferenceValue = scaler;
            soFitter.FindProperty("_designRoot").objectReferenceValue = designRect;
            soFitter.ApplyModifiedPropertiesWithoutUndo();

            // SceneGuard (ReadOnlySceneBinding)
            var guardGo = new GameObject("SceneGuard");
            guardGo.transform.SetParent(designRootGo.transform, false);
            var guardBinding = guardGo.AddComponent<ReadOnlySceneBinding>();

            // Title
            var title = CreateText("TitleText", designRootGo.transform, font, 26, TextAlignmentOptions.Center,
                new Vector2(0, 312), new Vector2(800, 42), "【本局 / 整场终局结算】", new Color(1f, 0.84f, 0f), FontStyles.Bold);

            // ConnectionBanner
            var bannerGo = CreateUiObject("ConnectionBanner", designRootGo.transform);
            var bannerRect = bannerGo.GetComponent<RectTransform>();
            bannerRect.anchoredPosition = new Vector2(0, 350);
            bannerRect.sizeDelta = new Vector2(360, 20);
            var bannerPresenter = bannerGo.AddComponent<ConnectionBannerPresenter>();
            var bannerText = CreateText("BannerText", bannerGo.transform, font, 13, TextAlignmentOptions.Center,
                Vector2.zero, new Vector2(360, 20), "连接正常", new Color(0.6f, 0.9f, 0.6f));
            var soBanner = new SerializedObject(bannerPresenter);
            soBanner.FindProperty("_bannerRoot").objectReferenceValue = bannerGo;
            soBanner.FindProperty("_bannerText").objectReferenceValue = bannerText;
            soBanner.ApplyModifiedPropertiesWithoutUndo();

            // IncompatibleBanner
            var incompGo = CreateUiObject("IncompatibleBanner", designRootGo.transform);
            var incompRect = incompGo.GetComponent<RectTransform>();
            incompRect.anchoredPosition = new Vector2(0, 275);
            incompRect.sizeDelta = new Vector2(900, 32);
            var incompBg = incompGo.AddComponent<Image>();
            incompBg.color = new Color(0.6f, 0.1f, 0.1f, 0.85f);
            var incompText = CreateText("WarningText", incompGo.transform, font, 15, TextAlignmentOptions.Center,
                Vector2.zero, new Vector2(880, 30), "[严重警告] 服务端协议版本不兼容", Color.white, FontStyles.Bold);
            incompGo.SetActive(false);

            // Left: Round Settlement Panel
            var roundPanelGo = CreateUiObject("RoundSettlementPanel", designRootGo.transform);
            var roundPanelRect = roundPanelGo.GetComponent<RectTransform>();
            roundPanelRect.anchoredPosition = new Vector2(-310, 10);
            roundPanelRect.sizeDelta = new Vector2(610, 520);
            var roundPanelImg = roundPanelGo.AddComponent<Image>();
            roundPanelImg.color = new Color(0.12f, 0.15f, 0.20f, 0.9f);

            var roundInfo = CreateText("RoundInfoText", roundPanelGo.transform, font, 19, TextAlignmentOptions.Left,
                new Vector2(0, 230), new Vector2(570, 30), "局: - | 结果: -", Color.white, FontStyles.Bold);

            var winSummary = CreateText("WinSummaryText", roundPanelGo.transform, font, 17, TextAlignmentOptions.Left,
                new Vector2(0, 195), new Vector2(570, 30), "胜者席位: - | 得分: -点", new Color(0.5f, 0.83f, 0.98f));

            var scoreBreakdown = CreateText("ScoreBreakdownText", roundPanelGo.transform, font, 15, TextAlignmentOptions.Left,
                new Vector2(0, 153), new Vector2(570, 48), "【番符得分明细】\n-", new Color(0.88f, 0.88f, 0.88f));

            var yakuList = CreateText("YakuListText", roundPanelGo.transform, font, 14, TextAlignmentOptions.TopLeft,
                new Vector2(0, 58), new Vector2(570, 130), "【役种列表】\n-", new Color(1f, 0.98f, 0.77f));

            var payments = CreateText("PaymentsText", roundPanelGo.transform, font, 14, TextAlignmentOptions.TopLeft,
                new Vector2(0, -80), new Vector2(570, 130), "【点棒收付变动】\n-", new Color(1f, 0.80f, 0.50f));

            var newScores = CreateText("NewScoresText", roundPanelGo.transform, font, 14, TextAlignmentOptions.TopLeft,
                new Vector2(0, -215), new Vector2(570, 40), "【结算后点棒】\n-", new Color(0.85f, 0.95f, 1f));

            // Right-Top: Match Summary Root
            var matchSummaryGo = CreateUiObject("MatchSummaryRoot", designRootGo.transform);
            var matchRect = matchSummaryGo.GetComponent<RectTransform>();
            matchRect.anchoredPosition = new Vector2(330, 145);
            matchRect.sizeDelta = new Vector2(570, 230);
            var matchImg = matchSummaryGo.AddComponent<Image>();
            matchImg.color = new Color(0.14f, 0.18f, 0.22f, 0.9f);

            var matchRankings = CreateText("MatchRankingsText", matchSummaryGo.transform, font, 16, TextAlignmentOptions.TopLeft,
                new Vector2(0, 0), new Vector2(530, 190), "【整场终局名次】\n-", new Color(0.65f, 0.84f, 0.65f));

            // Right-Bottom: Private Ledger Root
            var ledgerGo = CreateUiObject("PrivateLedgerRoot", designRootGo.transform);
            var ledgerRect = ledgerGo.GetComponent<RectTransform>();
            ledgerRect.anchoredPosition = new Vector2(330, -135);
            ledgerRect.sizeDelta = new Vector2(570, 250);
            var ledgerImg = ledgerGo.AddComponent<Image>();
            ledgerImg.color = new Color(0.18f, 0.14f, 0.22f, 0.9f);

            var ledgerPresenter = ledgerGo.AddComponent<PrivateLedgerPresenter>();

            var authStatus = CreateText("AuthStatusText", ledgerGo.transform, font, 15, TextAlignmentOptions.Left,
                new Vector2(0, 95), new Vector2(530, 26), "出千账本 (未授权)", new Color(0.81f, 0.58f, 0.85f), FontStyles.Bold);

            var balance = CreateText("BalanceText", ledgerGo.transform, font, 17, TextAlignmentOptions.Left,
                new Vector2(0, 62), new Vector2(530, 30), "千术点余额: 0", new Color(0.73f, 0.41f, 0.78f), FontStyles.Bold);

            var entries = CreateText("EntriesText", ledgerGo.transform, font, 13, TextAlignmentOptions.TopLeft,
                new Vector2(0, -2), new Vector2(530, 85), "本局尚无千术费用记录", new Color(0.88f, 0.75f, 0.91f));

            var localTag = CreateText("LocalGameTag", ledgerGo.transform, font, 12, TextAlignmentOptions.Left,
                new Vector2(0, -90), new Vector2(530, 24), string.Empty, new Color(0.62f, 0.62f, 0.62f));
            localTag.gameObject.SetActive(false);

            var soLedger = new SerializedObject(ledgerPresenter);
            soLedger.FindProperty("_ledgerRoot").objectReferenceValue = ledgerGo;
            soLedger.FindProperty("_authStatusText").objectReferenceValue = authStatus;
            soLedger.FindProperty("_balanceText").objectReferenceValue = balance;
            soLedger.FindProperty("_entriesText").objectReferenceValue = entries;
            soLedger.FindProperty("_localGameTag").objectReferenceValue = localTag;
            soLedger.ApplyModifiedPropertiesWithoutUndo();

            // Bottom Back Button
            var backBtn = CreateButton("BackButton", designRootGo.transform, font, 18,
                new Vector2(0, -325), new Vector2(200, 46), "返回菜单");

            // SettlementPresenter
            var presenterGo = new GameObject("SettlementPresenter");
            presenterGo.transform.SetParent(designRootGo.transform, false);
            var presenter = presenterGo.AddComponent<SettlementPresenter>();

            var so = new SerializedObject(presenter);
            so.FindProperty("_titleText").objectReferenceValue = title;
            so.FindProperty("_roundInfoText").objectReferenceValue = roundInfo;
            so.FindProperty("_winSummaryText").objectReferenceValue = winSummary;
            so.FindProperty("_yakuListText").objectReferenceValue = yakuList;
            so.FindProperty("_scoreBreakdownText").objectReferenceValue = scoreBreakdown;
            so.FindProperty("_paymentsText").objectReferenceValue = payments;
            so.FindProperty("_newScoresText").objectReferenceValue = newScores;
            so.FindProperty("_matchSummaryRoot").objectReferenceValue = matchSummaryGo;
            so.FindProperty("_matchRankingsText").objectReferenceValue = matchRankings;
            so.FindProperty("_privateLedger").objectReferenceValue = ledgerPresenter;
            so.FindProperty("_connectionBanner").objectReferenceValue = bannerPresenter;
            so.FindProperty("_incompatibleBannerRoot").objectReferenceValue = incompGo;
            so.FindProperty("_incompatibleBannerText").objectReferenceValue = incompText;
            so.FindProperty("_backButton").objectReferenceValue = backBtn;
            so.ApplyModifiedPropertiesWithoutUndo();

            var soGuard = new SerializedObject(guardBinding);
            soGuard.FindProperty("_settlementPresenter").objectReferenceValue = presenter;
            soGuard.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.SaveScene(scene, SettlementScenePath);
        }

        private static GameObject CreateUiObject(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();
            return go;
        }

        private static TextMeshProUGUI CreateText(
            string name,
            Transform parent,
            TMP_FontAsset font,
            float fontSize,
            TextAlignmentOptions alignment,
            Vector2 anchoredPos,
            Vector2 size,
            string initialText,
            Color color,
            FontStyles fontStyle = FontStyles.Normal)
        {
            var go = CreateUiObject(name, parent);
            var rect = go.GetComponent<RectTransform>();
            rect.anchoredPosition = anchoredPos;
            rect.sizeDelta = size;

            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.font = font;
            tmp.fontSize = fontSize;
            tmp.alignment = alignment;
            tmp.text = initialText;
            tmp.color = color;
            tmp.fontStyle = fontStyle;
            tmp.raycastTarget = false;
            return tmp;
        }

        private static Button CreateButton(
            string name,
            Transform parent,
            TMP_FontAsset font,
            float fontSize,
            Vector2 anchoredPos,
            Vector2 size,
            string label)
        {
            var go = CreateUiObject(name, parent);
            var rect = go.GetComponent<RectTransform>();
            rect.anchoredPosition = anchoredPos;
            rect.sizeDelta = size;

            var img = go.AddComponent<Image>();
            img.color = new Color(0.24f, 0.32f, 0.42f, 1f);

            var btn = go.AddComponent<Button>();
            var colors = btn.colors;
            colors.normalColor = new Color(0.24f, 0.32f, 0.42f, 1f);
            colors.highlightedColor = new Color(0.32f, 0.42f, 0.54f, 1f);
            colors.pressedColor = new Color(0.18f, 0.24f, 0.32f, 1f);
            btn.colors = colors;

            var labelTmp = CreateText("Text", go.transform, font, fontSize, TextAlignmentOptions.Center,
                Vector2.zero, size, label, Color.white, FontStyles.Bold);

            return btn;
        }

        private static void SetStretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }
    }

    [NUnit.Framework.TestFixture]
    public sealed class P44SceneBuilderTests
    {
        [NUnit.Framework.Test]
        public void BuildAndVerifyScenes()
        {
            P44SceneBuilder.BuildScenes();
            NUnit.Framework.Assert.IsTrue(System.IO.File.Exists(P44SceneBuilder.SpectatorScenePath));
            NUnit.Framework.Assert.IsTrue(System.IO.File.Exists(P44SceneBuilder.SettlementScenePath));
        }
    }
}
#endif
