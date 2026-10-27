#nullable enable
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.InputSystem;
using TMPro;
using Mahjong.Unity.App;
using Mahjong.Unity.Table;
using Mahjong.Unity.UI;

namespace Mahjong.Unity.Tests.EditMode
{
    public static class TableSceneBuilder
    {
        public const string TableScenePath = "Assets/Scenes/Table.unity";
        public const string FontAssetPath = "Assets/Art/UI/Fonts/NotoSansSC-Regular SDF.asset";
        public const string InputActionsPath = "Assets/Settings/InputSystem_Actions.inputactions";

        [MenuItem("Mahjong/Build Table Scene")]
        public static void BuildTableScene()
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

            BuildScene(font, inputActions);
            AssetDatabase.SaveAssets();
            Debug.Log("[TableSceneBuilder] Table.unity built successfully.");
        }

        private static void BuildScene(TMP_FontAsset font, InputActionAsset inputActions)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // 1. Camera
            var camGo = new GameObject("Main Camera");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.06f, 0.11f, 0.08f, 1f); // Dark Mahjong green felt
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
            bgImg.color = new Color(0.05f, 0.10f, 0.07f, 1f);

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

            // TablePresenter & TableSceneBinding
            var presenter = designRootGo.AddComponent<TablePresenter>();
            var binding = designRootGo.AddComponent<TableSceneBinding>();

            // --- Top Header Panel ---
            var headerGo = CreateUiObject("HeaderPanel", designRootGo.transform);
            var headerRect = headerGo.GetComponent<RectTransform>();
            headerRect.anchoredPosition = new Vector2(0, 325);
            headerRect.sizeDelta = new Vector2(1240, 56);
            var headerBg = headerGo.AddComponent<Image>();
            headerBg.color = new Color(0.08f, 0.14f, 0.10f, 0.9f);

            var backBtn = CreateButton("BackButton", headerGo.transform, font, 13, new Vector2(-540, 0), new Vector2(100, 36), "返回菜单");
            var settlementBtn = CreateButton("ViewSettlementButton", headerGo.transform, font, 13, new Vector2(-430, 0), new Vector2(100, 36), "查看结算");
            var nextRoundBtn = CreateButton("NextRoundButton", headerGo.transform, font, 13, new Vector2(-320, 0), new Vector2(100, 36), "下一局");
            nextRoundBtn.gameObject.SetActive(false);

            var sessionInfo = CreateText("SessionInfoText", headerGo.transform, font, 13.5f, TextAlignmentOptions.Left,
                new Vector2(-30, 10), new Vector2(460, 26), "东1局 0本场 | 供托:0点 | 牌山:68张", Color.white);

            var turnCountdown = CreateText("TurnCountdownText", headerGo.transform, font, 13.5f, TextAlignmentOptions.Left,
                new Vector2(-30, -12), new Vector2(260, 24), "思考时间: 20s", new Color(1f, 0.88f, 0.2f), FontStyles.Bold);

            var controlStatus = CreateText("ControlStatusText", headerGo.transform, font, 12, TextAlignmentOptions.Right,
                new Vector2(400, 10), new Vector2(420, 24), "控制状态: 本地 (ClaimControl暂不可用: 协议字段冲突)", new Color(0.8f, 0.8f, 0.8f));

            var pauseGo = CreateUiObject("PauseIndicator", headerGo.transform);
            var pauseRect = pauseGo.GetComponent<RectTransform>();
            pauseRect.anchoredPosition = new Vector2(520, -12);
            pauseRect.sizeDelta = new Vector2(140, 24);
            var pauseText = CreateText("Text", pauseGo.transform, font, 13, TextAlignmentOptions.Center,
                Vector2.zero, new Vector2(140, 24), "【对局暂停中】", new Color(1f, 0.3f, 0.3f), FontStyles.Bold);
            pauseGo.SetActive(false);

            // --- Board Center Panel ---
            var centerGo = CreateUiObject("CenterBoardPanel", designRootGo.transform);
            var centerRect = centerGo.GetComponent<RectTransform>();
            centerRect.anchoredPosition = new Vector2(0, 40);
            centerRect.sizeDelta = new Vector2(440, 150);
            var centerBg = centerGo.AddComponent<Image>();
            centerBg.color = new Color(0.07f, 0.13f, 0.09f, 0.75f);

            var roundStatus = CreateText("RoundStatusText", centerGo.transform, font, 16, TextAlignmentOptions.Center,
                new Vector2(0, 50), new Vector2(420, 28), "东1局 0本场 供托:0", new Color(1f, 0.85f, 0.1f), FontStyles.Bold);

            var doraText = CreateText("DoraIndicatorsText", centerGo.transform, font, 14, TextAlignmentOptions.Center,
                new Vector2(0, 20), new Vector2(420, 26), "宝牌指示: [无]", new Color(1f, 0.55f, 0.55f));

            var wallText = CreateText("WallRemainingText", centerGo.transform, font, 14, TextAlignmentOptions.Center,
                new Vector2(0, -10), new Vector2(420, 24), "牌山剩余: 68张", new Color(0.9f, 0.9f, 0.9f));

            var lastDiscard = CreateText("LastDiscardText", centerGo.transform, font, 13.5f, TextAlignmentOptions.Center,
                new Vector2(0, -40), new Vector2(420, 24), "最新舍牌: 无", new Color(0.85f, 0.85f, 0.85f));

            // --- 4 Seats ---
            // Seat 0: South / Bottom (Own Seat)
            // Seat 1: East / Right
            // Seat 2: North / Top
            // Seat 3: West / Left
            var seatConfigs = new (string name, Vector2 pos, Vector2 size)[]
            {
                ("SeatPanel_0", new Vector2(0, -145), new Vector2(580, 100)),   // Bottom / Own
                ("SeatPanel_1", new Vector2(445, 40), new Vector2(340, 210)),    // Right / East
                ("SeatPanel_2", new Vector2(0, 220), new Vector2(580, 100)),    // Top / North
                ("SeatPanel_3", new Vector2(-445, 40), new Vector2(340, 210))   // Left / West
            };

            var seatViews = new TableSeatView[4];
            for (int i = 0; i < 4; i++)
            {
                var cfg = seatConfigs[i];
                var sGo = CreateUiObject(cfg.name, designRootGo.transform);
                var sRect = sGo.GetComponent<RectTransform>();
                sRect.anchoredPosition = cfg.pos;
                sRect.sizeDelta = cfg.size;
                var sImg = sGo.AddComponent<Image>();
                sImg.color = new Color(0.10f, 0.16f, 0.12f, 0.85f);

                bool isWide = cfg.size.x >= 500;
                float nameX = isWide ? -160 : -75;
                float scoreX = isWide ? 160 : 75;

                var nameTxt = CreateText("SeatName", sGo.transform, font, 15, TextAlignmentOptions.Left,
                    new Vector2(nameX, isWide ? 30 : 80), new Vector2(220, 26), $"座位{i}", Color.white, FontStyles.Bold);

                var scoreTxt = CreateText("ScoreText", sGo.transform, font, 15, TextAlignmentOptions.Right,
                    new Vector2(scoreX, isWide ? 30 : 80), new Vector2(180, 26), "25000点", new Color(0.3f, 0.9f, 0.4f), FontStyles.Bold);

                var handTxt = CreateText("HandText", sGo.transform, font, 13, TextAlignmentOptions.Left,
                    new Vector2(0, isWide ? 5 : 45), new Vector2(cfg.size.x - 20, isWide ? 24 : 44), "手牌: 13张", new Color(0.9f, 0.9f, 0.9f));

                var discardsTxt = CreateText("DiscardsText", sGo.transform, font, 12, TextAlignmentOptions.Left,
                    new Vector2(0, isWide ? -18 : 10), new Vector2(cfg.size.x - 20, 22), "牌河: 无", new Color(0.85f, 0.85f, 0.85f));

                var meldsTxt = CreateText("MeldsText", sGo.transform, font, 12, TextAlignmentOptions.Left,
                    new Vector2(0, isWide ? -38 : -25), new Vector2(cfg.size.x - 20, 22), "副露: 门清", new Color(0.95f, 0.8f, 0.5f));

                var ankanTxt = CreateText("AnkanText", sGo.transform, font, 11, TextAlignmentOptions.Left,
                    new Vector2(0, isWide ? -42 : -60), new Vector2(cfg.size.x - 20, 20), string.Empty, new Color(0.7f, 0.8f, 0.9f));

                var badgeGo = CreateUiObject("RiichiBadge", sGo.transform);
                var badgeRect = badgeGo.GetComponent<RectTransform>();
                badgeRect.anchoredPosition = new Vector2(isWide ? 50 : 0, isWide ? 30 : 80);
                badgeRect.sizeDelta = new Vector2(70, 22);
                var badgeBg = badgeGo.AddComponent<Image>();
                badgeBg.color = new Color(0.8f, 0.2f, 0.2f, 1f);
                CreateText("BadgeText", badgeGo.transform, font, 12, TextAlignmentOptions.Center,
                    Vector2.zero, new Vector2(70, 22), "立直", Color.white, FontStyles.Bold);
                badgeGo.SetActive(false);

                var seatView = new TableSeatView();
                seatView.BindControls(sGo, nameTxt, scoreTxt, handTxt, badgeGo, discardsTxt, meldsTxt, ankanTxt);
                seatViews[i] = seatView;
            }

            // --- Own Hand & Decision Panel (Bottom) ---
            var handPanelGo = CreateUiObject("OwnHandPanel", designRootGo.transform);
            var handPanelRect = handPanelGo.GetComponent<RectTransform>();
            handPanelRect.anchoredPosition = new Vector2(0, -280);
            handPanelRect.sizeDelta = new Vector2(1240, 130);
            var handPanelBg = handPanelGo.AddComponent<Image>();
            handPanelBg.color = new Color(0.08f, 0.13f, 0.10f, 0.9f);

            // Action candidate buttons (Chi, Pon, Kan, Riichi, Tsumo, Ron, Pass, Discard, Cancel)
            var actionBtnConfigs = new (string name, string label, float x)[]
            {
                ("ChiButton", "吃", -320),
                ("PonButton", "碰", -245),
                ("KanButton", "杠", -170),
                ("RiichiButton", "立直", -95),
                ("TsumoButton", "自摸", -20),
                ("RonButton", "荣和", 55),
                ("PassButton", "跳过", 130),
                ("DiscardButton", "打牌", 215),
                ("CancelSelectionButton", "取消", 295)
            };

            var actionButtons = new Dictionary<string, Button>();
            foreach (var act in actionBtnConfigs)
            {
                var btn = CreateButton(act.name, handPanelGo.transform, font, 13, new Vector2(act.x, 38), new Vector2(68, 32), act.label);
                btn.gameObject.SetActive(false);
                actionButtons[act.name] = btn;
            }

            // Closed hand tiles (13 tile buttons)
            var handTileButtons = new Button[13];
            var handTileTexts = new TextMeshProUGUI[13];
            float startTileX = -380;
            float tileWidth = 52;
            float tileSpacing = 56;

            for (int i = 0; i < 13; i++)
            {
                float x = startTileX + (i * tileSpacing);
                var btn = CreateButton($"HandTile_{i}", handPanelGo.transform, font, 13.5f, new Vector2(x, -18), new Vector2(tileWidth, 68), $"牌{i}");
                var txt = btn.GetComponentInChildren<TextMeshProUGUI>();
                handTileButtons[i] = btn;
                handTileTexts[i] = txt;
                btn.gameObject.SetActive(false);
            }

            // Drawn tile button
            float drawnTileX = startTileX + (13 * tileSpacing) + 20; // 20px gap for drawn tile
            var drawnBtn = CreateButton("DrawnTileButton", handPanelGo.transform, font, 13.5f, new Vector2(drawnTileX, -18), new Vector2(tileWidth, 68), "摸牌");
            var drawnTxt = drawnBtn.GetComponentInChildren<TextMeshProUGUI>();
            drawnBtn.gameObject.SetActive(false);

            // --- Cheat Panel (Left Bottom) ---
            var cheatPanelGo = CreateUiObject("CheatPanel", designRootGo.transform);
            var cheatRect = cheatPanelGo.GetComponent<RectTransform>();
            cheatRect.anchoredPosition = new Vector2(-510, -145);
            cheatRect.sizeDelta = new Vector2(210, 100);
            var cheatBg = cheatPanelGo.AddComponent<Image>();
            cheatBg.color = new Color(0.12f, 0.10f, 0.18f, 0.9f);

            var cheatPointsTxt = CreateText("CheatPointsText", cheatPanelGo.transform, font, 13.5f, TextAlignmentOptions.Center,
                new Vector2(0, 32), new Vector2(190, 24), "出千点数: 100", new Color(1f, 0.8f, 0.3f), FontStyles.Bold);

            var tileMarkBtn = CreateButton("TileMarkButton", cheatPanelGo.transform, font, 12, new Vector2(-50, 2), new Vector2(90, 26), "落汗");
            var sleeveBloomBtn = CreateButton("SleeveBloomButton", cheatPanelGo.transform, font, 12, new Vector2(50, 2), new Vector2(90, 26), "袖里藏花");
            var beamSwapBtn = CreateButton("BeamSwapButton", cheatPanelGo.transform, font, 12, new Vector2(-50, -28), new Vector2(90, 26), "偷梁换柱");
            var prepBtn = CreateButton("PreparationButton", cheatPanelGo.transform, font, 12, new Vector2(50, -28), new Vector2(90, 26), "码牌准备");

            // --- TileMark Dialog ---
            var tileMarkDlg = CreateUiObject("TileMarkDialog", designRootGo.transform);
            var tmRect = tileMarkDlg.GetComponent<RectTransform>();
            tmRect.anchoredPosition = new Vector2(0, 40);
            tmRect.sizeDelta = new Vector2(500, 180);
            var tmBg = tileMarkDlg.AddComponent<Image>();
            tmBg.color = new Color(0.15f, 0.12f, 0.22f, 0.95f);

            var tmTitle = CreateText("Title", tileMarkDlg.transform, font, 16, TextAlignmentOptions.Center,
                new Vector2(0, 65), new Vector2(460, 28), "【落汗绝技】选择4张手牌做标记", Color.white, FontStyles.Bold);
            var tmStatus = CreateText("TileMarkStatusText", tileMarkDlg.transform, font, 13, TextAlignmentOptions.Center,
                new Vector2(0, 25), new Vector2(460, 40), "请从手牌选择4张进行落汗 (消耗:10点)。已选: 0/4", new Color(0.9f, 0.9f, 0.9f));
            var tmConfirm = CreateButton("ConfirmTileMarkButton", tileMarkDlg.transform, font, 13, new Vector2(-90, -45), new Vector2(120, 34), "确认落汗");
            var tmCancel = CreateButton("CancelTileMarkButton", tileMarkDlg.transform, font, 13, new Vector2(90, -45), new Vector2(120, 34), "取消");
            tileMarkDlg.SetActive(false);

            // --- SleeveBloom Dialog ---
            var sleeveDlg = CreateUiObject("SleeveBloomDialog", designRootGo.transform);
            var slRect = sleeveDlg.GetComponent<RectTransform>();
            slRect.anchoredPosition = new Vector2(0, 40);
            slRect.sizeDelta = new Vector2(500, 180);
            var slBg = sleeveDlg.AddComponent<Image>();
            slBg.color = new Color(0.15f, 0.12f, 0.22f, 0.95f);

            var slTitle = CreateText("Title", sleeveDlg.transform, font, 16, TextAlignmentOptions.Center,
                new Vector2(0, 65), new Vector2(460, 28), "【袖里藏花】调换或藏匿袖牌", Color.white, FontStyles.Bold);
            var slStatus = CreateText("SleeveBloomStatusText", sleeveDlg.transform, font, 13, TextAlignmentOptions.Center,
                new Vector2(0, 32), new Vector2(460, 26), "发动袖里藏花 (消耗:10点):", new Color(0.9f, 0.9f, 0.9f));
            var slSleeveTile = CreateText("SleeveTileDisplayText", sleeveDlg.transform, font, 13, TextAlignmentOptions.Center,
                new Vector2(0, 5), new Vector2(460, 24), "当前袖牌: 无", new Color(1f, 0.85f, 0.3f));
            var slStore = CreateButton("SleeveStoreButton", sleeveDlg.transform, font, 12, new Vector2(-130, -45), new Vector2(100, 34), "藏牌");
            var slSwap = CreateButton("SleeveSwapButton", sleeveDlg.transform, font, 12, new Vector2(-20, -45), new Vector2(100, 34), "调换");
            var slPass = CreateButton("SleevePassButton", sleeveDlg.transform, font, 12, new Vector2(90, -45), new Vector2(100, 34), "放弃");
            var slCancel = CreateButton("CancelSleeveButton", sleeveDlg.transform, font, 12, new Vector2(190, -45), new Vector2(80, 34), "取消");
            sleeveDlg.SetActive(false);

            // --- BeamSwap Dialog ---
            var beamDlg = CreateUiObject("BeamSwapDialog", designRootGo.transform);
            var bmRect = beamDlg.GetComponent<RectTransform>();
            bmRect.anchoredPosition = new Vector2(0, 40);
            bmRect.sizeDelta = new Vector2(520, 200);
            var bmBg = beamDlg.AddComponent<Image>();
            bmBg.color = new Color(0.15f, 0.12f, 0.22f, 0.95f);

            var bmTitle = CreateText("Title", beamDlg.transform, font, 16, TextAlignmentOptions.Center,
                new Vector2(0, 75), new Vector2(480, 28), "【偷梁换柱】手牌与牌山槽位互换", Color.white, FontStyles.Bold);
            var bmStatus = CreateText("BeamSwapStatusText", beamDlg.transform, font, 13, TextAlignmentOptions.Center,
                new Vector2(0, 40), new Vector2(480, 34), "偷梁换柱要约中，请选手牌与目标槽位:", new Color(0.9f, 0.9f, 0.9f));

            var beamSlotButtons = new Button[4];
            var beamSlotTexts = new TextMeshProUGUI[4];
            for (int b = 0; b < 4; b++)
            {
                float bx = -180 + (b * 120);
                var btn = CreateButton($"BeamSlot_{b}", beamDlg.transform, font, 11, new Vector2(bx, 0), new Vector2(110, 30), $"槽位{b}");
                beamSlotButtons[b] = btn;
                beamSlotTexts[b] = btn.GetComponentInChildren<TextMeshProUGUI>();
            }

            var bmConfirm = CreateButton("ConfirmBeamSwapButton", beamDlg.transform, font, 13, new Vector2(-120, -55), new Vector2(110, 34), "确认换牌");
            var bmKeep = CreateButton("KeepBeamOriginalButton", beamDlg.transform, font, 13, new Vector2(10, -55), new Vector2(110, 34), "保留原牌");
            var bmCancel = CreateButton("CancelBeamButton", beamDlg.transform, font, 13, new Vector2(140, -55), new Vector2(110, 34), "取消");
            beamDlg.SetActive(false);

            // --- Preparation Dialog ---
            var prepDlg = CreateUiObject("PreparationDialog", designRootGo.transform);
            var prRect = prepDlg.GetComponent<RectTransform>();
            prRect.anchoredPosition = new Vector2(0, 40);
            prRect.sizeDelta = new Vector2(480, 180);
            var prBg = prepDlg.AddComponent<Image>();
            prBg.color = new Color(0.15f, 0.12f, 0.22f, 0.95f);

            var prTitle = CreateText("Title", prepDlg.transform, font, 16, TextAlignmentOptions.Center,
                new Vector2(0, 65), new Vector2(440, 28), "【码牌准备】设定牌山生成目标", Color.white, FontStyles.Bold);
            var prInfo = CreateText("PreparationInfoText", prepDlg.transform, font, 13, TextAlignmentOptions.Center,
                new Vector2(0, 25), new Vector2(440, 34), "码牌准备 | 目标牌型: 0 | 消耗: 10点", new Color(0.9f, 0.9f, 0.9f));

            // SeeThrough Toggle
            var seeThroughBtn = CreateButton("SeeThroughButton", prepDlg.transform, font, 13, new Vector2(0, -10), new Vector2(180, 34), "透视: [关] (10点)");
            var seeThroughTxt = seeThroughBtn.GetComponentInChildren<TextMeshProUGUI>();

            var prConfirm = CreateButton("ConfirmPreparationButton", prepDlg.transform, font, 13, new Vector2(-70, -55), new Vector2(120, 34), "确认准备");
            var prCancel = CreateButton("CancelPreparationButton", prepDlg.transform, font, 13, new Vector2(80, -55), new Vector2(120, 34), "取消");
            prepDlg.SetActive(false);

            // --- Status & Animation Banners ---
            var statusBanner = CreateText("StatusBannerText", designRootGo.transform, font, 13, TextAlignmentOptions.Right,
                new Vector2(370, -215), new Vector2(480, 26), "就绪", new Color(0.7f, 0.9f, 0.7f));

            var animBanner = CreateText("AnimationBannerText", designRootGo.transform, font, 13.5f, TextAlignmentOptions.Center,
                new Vector2(0, 140), new Vector2(500, 28), string.Empty, new Color(1f, 0.85f, 0.2f), FontStyles.Bold);

            // Serialize TablePresenter references
            var soPresenter = new SerializedObject(presenter);
            soPresenter.FindProperty("_sessionInfoText").objectReferenceValue = sessionInfo;
            soPresenter.FindProperty("_turnCountdownText").objectReferenceValue = turnCountdown;
            soPresenter.FindProperty("_pauseIndicator").objectReferenceValue = pauseGo;
            soPresenter.FindProperty("_controlStatusText").objectReferenceValue = controlStatus;
            soPresenter.FindProperty("_backButton").objectReferenceValue = backBtn;
            soPresenter.FindProperty("_viewSettlementButton").objectReferenceValue = settlementBtn;
            soPresenter.FindProperty("_nextRoundButton").objectReferenceValue = nextRoundBtn;

            soPresenter.FindProperty("_roundStatusText").objectReferenceValue = roundStatus;
            soPresenter.FindProperty("_wallRemainingText").objectReferenceValue = wallText;
            soPresenter.FindProperty("_doraIndicatorsText").objectReferenceValue = doraText;
            soPresenter.FindProperty("_lastDiscardText").objectReferenceValue = lastDiscard;

            // Seats
            var seatsProp = soPresenter.FindProperty("_seatViews");
            seatsProp.arraySize = 4;
            for (int i = 0; i < 4; i++)
            {
                var sp = seatsProp.GetArrayElementAtIndex(i);
                sp.FindPropertyRelative("_root").objectReferenceValue = seatViews[i].Root;
                sp.FindPropertyRelative("_seatNameText").objectReferenceValue = seatViews[i].SeatNameText;
                sp.FindPropertyRelative("_scoreText").objectReferenceValue = seatViews[i].ScoreText;
                sp.FindPropertyRelative("_handText").objectReferenceValue = seatViews[i].HandText;
                sp.FindPropertyRelative("_riichiBadge").objectReferenceValue = seatViews[i].RiichiBadge;
                sp.FindPropertyRelative("_discardsText").objectReferenceValue = seatViews[i].DiscardsText;
                sp.FindPropertyRelative("_meldsText").objectReferenceValue = seatViews[i].MeldsText;
                sp.FindPropertyRelative("_ankanText").objectReferenceValue = seatViews[i].AnkanText;
            }

            // Own Hand
            soPresenter.FindProperty("_ownHandPanelRoot").objectReferenceValue = handPanelGo;
            var handBtnsProp = soPresenter.FindProperty("_handTileButtons");
            handBtnsProp.arraySize = 13;
            var handTxtsProp = soPresenter.FindProperty("_handTileTexts");
            handTxtsProp.arraySize = 13;
            for (int i = 0; i < 13; i++)
            {
                handBtnsProp.GetArrayElementAtIndex(i).objectReferenceValue = handTileButtons[i];
                handTxtsProp.GetArrayElementAtIndex(i).objectReferenceValue = handTileTexts[i];
            }
            soPresenter.FindProperty("_drawnTileButton").objectReferenceValue = drawnBtn;
            soPresenter.FindProperty("_drawnTileText").objectReferenceValue = drawnTxt;

            soPresenter.FindProperty("_discardButton").objectReferenceValue = actionButtons["DiscardButton"];
            soPresenter.FindProperty("_chiButton").objectReferenceValue = actionButtons["ChiButton"];
            soPresenter.FindProperty("_ponButton").objectReferenceValue = actionButtons["PonButton"];
            soPresenter.FindProperty("_kanButton").objectReferenceValue = actionButtons["KanButton"];
            soPresenter.FindProperty("_riichiButton").objectReferenceValue = actionButtons["RiichiButton"];
            soPresenter.FindProperty("_tsumoButton").objectReferenceValue = actionButtons["TsumoButton"];
            soPresenter.FindProperty("_ronButton").objectReferenceValue = actionButtons["RonButton"];
            soPresenter.FindProperty("_passButton").objectReferenceValue = actionButtons["PassButton"];
            soPresenter.FindProperty("_cancelSelectionButton").objectReferenceValue = actionButtons["CancelSelectionButton"];

            // Cheats
            soPresenter.FindProperty("_cheatPanelRoot").objectReferenceValue = cheatPanelGo;
            soPresenter.FindProperty("_cheatPointsText").objectReferenceValue = cheatPointsTxt;
            soPresenter.FindProperty("_tileMarkButton").objectReferenceValue = tileMarkBtn;
            soPresenter.FindProperty("_sleeveBloomButton").objectReferenceValue = sleeveBloomBtn;
            soPresenter.FindProperty("_beamSwapButton").objectReferenceValue = beamSwapBtn;
            soPresenter.FindProperty("_preparationButton").objectReferenceValue = prepBtn;

            // Sub-dialogs
            soPresenter.FindProperty("_tileMarkDialog").objectReferenceValue = tileMarkDlg;
            soPresenter.FindProperty("_tileMarkStatusText").objectReferenceValue = tmStatus;
            soPresenter.FindProperty("_confirmTileMarkButton").objectReferenceValue = tmConfirm;
            soPresenter.FindProperty("_cancelTileMarkButton").objectReferenceValue = tmCancel;

            soPresenter.FindProperty("_sleeveBloomDialog").objectReferenceValue = sleeveDlg;
            soPresenter.FindProperty("_sleeveBloomStatusText").objectReferenceValue = slStatus;
            soPresenter.FindProperty("_sleeveTileDisplayText").objectReferenceValue = slSleeveTile;
            soPresenter.FindProperty("_sleeveStoreButton").objectReferenceValue = slStore;
            soPresenter.FindProperty("_sleeveSwapButton").objectReferenceValue = slSwap;
            soPresenter.FindProperty("_sleevePassButton").objectReferenceValue = slPass;
            soPresenter.FindProperty("_cancelSleeveButton").objectReferenceValue = slCancel;

            soPresenter.FindProperty("_beamSwapDialog").objectReferenceValue = beamDlg;
            soPresenter.FindProperty("_beamSwapStatusText").objectReferenceValue = bmStatus;
            var beamBtnsProp = soPresenter.FindProperty("_beamSlotButtons");
            beamBtnsProp.arraySize = 4;
            var beamTxtsProp = soPresenter.FindProperty("_beamSlotTexts");
            beamTxtsProp.arraySize = 4;
            for (int b = 0; b < 4; b++)
            {
                beamBtnsProp.GetArrayElementAtIndex(b).objectReferenceValue = beamSlotButtons[b];
                beamTxtsProp.GetArrayElementAtIndex(b).objectReferenceValue = beamSlotTexts[b];
            }
            soPresenter.FindProperty("_confirmBeamSwapButton").objectReferenceValue = bmConfirm;
            soPresenter.FindProperty("_keepBeamOriginalButton").objectReferenceValue = bmKeep;
            soPresenter.FindProperty("_cancelBeamButton").objectReferenceValue = bmCancel;

            soPresenter.FindProperty("_preparationDialog").objectReferenceValue = prepDlg;
            soPresenter.FindProperty("_preparationInfoText").objectReferenceValue = prInfo;
            soPresenter.FindProperty("_seeThroughButton").objectReferenceValue = seeThroughBtn;
            soPresenter.FindProperty("_seeThroughButtonText").objectReferenceValue = seeThroughTxt;
            soPresenter.FindProperty("_confirmPreparationButton").objectReferenceValue = prConfirm;
            soPresenter.FindProperty("_cancelPreparationButton").objectReferenceValue = prCancel;

            soPresenter.FindProperty("_statusBannerText").objectReferenceValue = statusBanner;
            soPresenter.FindProperty("_animationBannerText").objectReferenceValue = animBanner;
            soPresenter.ApplyModifiedPropertiesWithoutUndo();

            // Serialize TableSceneBinding
            var soBinding = new SerializedObject(binding);
            soBinding.FindProperty("_tablePresenter").objectReferenceValue = presenter;
            soBinding.ApplyModifiedPropertiesWithoutUndo();

            // Save scene
            EditorSceneManager.SaveScene(scene, TableScenePath);
        }

        private static GameObject CreateUiObject(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
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
            img.color = new Color(0.20f, 0.28f, 0.38f, 1f);

            var btn = go.AddComponent<Button>();
            var colors = btn.colors;
            colors.normalColor = new Color(0.20f, 0.28f, 0.38f, 1f);
            colors.highlightedColor = new Color(0.28f, 0.38f, 0.50f, 1f);
            colors.pressedColor = new Color(0.14f, 0.20f, 0.28f, 1f);
            btn.colors = colors;

            CreateText("Text", go.transform, font, fontSize, TextAlignmentOptions.Center,
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
}
#endif
