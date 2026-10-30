#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using Mahjong.Unity.App;
using Mahjong.Unity.Table;
using Mahjong.Unity.UI;
using Mahjong.Unity.UI.Session;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

namespace Mahjong.Unity.Tests.EditMode
{
    [TestFixture]
    public sealed class TableSceneEditModeTests
    {
        [Test]
        public void Step01_BuildTableScene_GeneratesAssetAndFileExists()
        {
#if UNITY_EDITOR
            TableSceneBuilder.BuildTableScene();
            Assert.IsTrue(File.Exists(TableSceneBuilder.TableScenePath), "Table.unity scene file must exist on disk.");
#endif
        }

        [Test]
        public void Step02_TableScene_HierarchyAndPresenterBindingsIntegrity()
        {
#if UNITY_EDITOR
            var scene = EditorSceneManager.OpenScene(TableSceneBuilder.TableScenePath, OpenSceneMode.Single);
            Assert.IsTrue(scene.IsValid(), "Scene must be valid.");

            var presenter = UnityEngine.Object.FindFirstObjectByType<TablePresenter>();
            Assert.IsNotNull(presenter, "TablePresenter must exist in Table.unity");

            var binding = UnityEngine.Object.FindFirstObjectByType<TableSceneBinding>();
            Assert.IsNotNull(binding, "TableSceneBinding must exist in Table.unity");
            Assert.AreEqual(presenter, binding!.Presenter, "TableSceneBinding must link to TablePresenter.");

            // Verify Camera and Canvas
            var cam = UnityEngine.Object.FindFirstObjectByType<Camera>();
            Assert.IsNotNull(cam, "Camera must exist in Table.unity");
            Assert.AreEqual("MainCamera", cam!.tag);

            var canvas = UnityEngine.Object.FindFirstObjectByType<Canvas>();
            Assert.IsNotNull(canvas, "Canvas must exist in Table.unity");
            Assert.AreEqual(RenderMode.ScreenSpaceOverlay, canvas!.renderMode);

            var scaler = canvas.GetComponent<CanvasScaler>();
            Assert.IsNotNull(scaler);
            Assert.AreEqual(new Vector2(1280, 720), scaler!.referenceResolution);
#endif
        }

        [Test]
        public void Step03_TableScene_CanvasAndSafeAreaHierarchy()
        {
#if UNITY_EDITOR
            var scene = EditorSceneManager.OpenScene(TableSceneBuilder.TableScenePath, OpenSceneMode.Single);
            var safeAreaFitter = UnityEngine.Object.FindFirstObjectByType<SafeAreaFitter>();
            Assert.IsNotNull(safeAreaFitter, "SafeAreaFitter must exist in Table.unity");

            var designRoot = safeAreaFitter!.transform.Find("DesignRoot");
            Assert.IsNotNull(designRoot, "DesignRoot must exist under SafeAreaRoot");

            var presenter = designRoot!.GetComponent<TablePresenter>();
            Assert.IsNotNull(presenter, "TablePresenter must be attached to DesignRoot");
#endif
        }

        [Test]
        public void Step04_TableScene_ButtonsHavePointerLifecycleTrigger()
        {
#if UNITY_EDITOR
            var scene = EditorSceneManager.OpenScene(TableSceneBuilder.TableScenePath, OpenSceneMode.Single);
            var buttons = UnityEngine.Object.FindObjectsByType<Button>(FindObjectsSortMode.None);
            Assert.Greater(buttons.Length, 20, "Table scene must contain interactive buttons");

            foreach (var btn in buttons)
            {
                var trigger = btn.GetComponent<UiPointerLifecycleTrigger>();
                Assert.IsNotNull(trigger, $"Button '{btn.name}' must have UiPointerLifecycleTrigger attached");
            }
#endif
        }

        [Test]
        public void Step05_TableScene_CheatSubdialogsAndBadgesBound()
        {
#if UNITY_EDITOR
            var scene = EditorSceneManager.OpenScene(TableSceneBuilder.TableScenePath, OpenSceneMode.Single);
            var presenter = UnityEngine.Object.FindFirstObjectByType<TablePresenter>();
            Assert.IsNotNull(presenter);

            var so = new SerializedObject(presenter!);
            Assert.IsNotNull(so.FindProperty("_tileMarkDialog").objectReferenceValue, "TileMarkDialog must be bound");
            Assert.IsNotNull(so.FindProperty("_sleeveBloomDialog").objectReferenceValue, "SleeveBloomDialog must be bound");
            Assert.IsNotNull(so.FindProperty("_beamSwapDialog").objectReferenceValue, "BeamSwapDialog must be bound");
            Assert.IsNotNull(so.FindProperty("_preparationDialog").objectReferenceValue, "PreparationDialog must be bound");

            var badgesProp = so.FindProperty("_handTileMarkBadges");
            Assert.AreEqual(14, badgesProp.arraySize, "Must have 14 hand tile mark badges bound (13 closed + 1 drawn)");
#endif
        }

        [Test]
        public void Step06_TableScene_ControlButtonsAndStatusBound()
        {
#if UNITY_EDITOR
            var scene = EditorSceneManager.OpenScene(TableSceneBuilder.TableScenePath, OpenSceneMode.Single);
            var presenter = UnityEngine.Object.FindFirstObjectByType<TablePresenter>();
            Assert.IsNotNull(presenter);

            var so = new SerializedObject(presenter!);
            var claim = so.FindProperty("_claimControlButton").objectReferenceValue as Button;
            var release = so.FindProperty("_releaseControlButton").objectReferenceValue as Button;
            var status = so.FindProperty("_controlStatusText").objectReferenceValue as TextMeshProUGUI;

            Assert.IsNotNull(claim, "ClaimControlButton must be bound to the presenter");
            Assert.IsNotNull(release, "ReleaseControlButton must be bound to the presenter");
            Assert.IsNotNull(status, "ControlStatusText must be bound to the presenter");
            Assert.AreEqual("ClaimControlButton", claim!.gameObject.name);
            Assert.AreEqual("ReleaseControlButton", release!.gameObject.name);
            Assert.AreEqual("ControlStatusText", status!.gameObject.name);
            Assert.IsNotNull(claim.GetComponent<UiPointerLifecycleTrigger>());
            Assert.IsNotNull(release.GetComponent<UiPointerLifecycleTrigger>());
            Assert.IsNotNull(claim.transform.Find("Text"), "Claim button must carry a visible label");
            Assert.IsNotNull(release.transform.Find("Text"), "Release button must carry a visible label");
#endif
        }
    }

#if UNITY_EDITOR
    /// <summary>
    /// Programmatic generator for the Table.unity scene.
    /// Builds Canvas hierarchy, four seats, hand tiles, action candidates,
    /// three cheat disciplines, sub-dialogs, and ensures all interactive buttons
    /// have UiPointerLifecycleTrigger attached.
    /// </summary>
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

            // --- Header & Status Panel ---
            var sessionInfo = CreateText("SessionInfoText", designRootGo.transform, font, 13.5f, TextAlignmentOptions.Left,
                new Vector2(-340, 340), new Vector2(360, 26), "正在同步牌桌...", Color.white);

            var roundStatus = CreateText("RoundStatusText", designRootGo.transform, font, 16f, TextAlignmentOptions.Center,
                new Vector2(0, 340), new Vector2(280, 26), "东1局 0本场", new Color(1f, 0.85f, 0.3f, 1f), FontStyles.Bold);

            var turnCountdown = CreateText("TurnCountdownText", designRootGo.transform, font, 15f, TextAlignmentOptions.Right,
                new Vector2(340, 340), new Vector2(340, 26), "思考时间: 20.0秒", Color.green, FontStyles.Bold);

            var wallRemaining = CreateText("WallRemainingText", designRootGo.transform, font, 13.5f, TextAlignmentOptions.Left,
                new Vector2(-380, 312), new Vector2(240, 22), "牌山余牌: 70", new Color(0.8f, 0.9f, 1f));

            var doraIndicators = CreateText("DoraIndicatorsText", designRootGo.transform, font, 13.5f, TextAlignmentOptions.Center,
                new Vector2(-80, 312), new Vector2(320, 22), "宝牌指示: -", new Color(1f, 0.7f, 0.7f));

            var lastDiscard = CreateText("LastDiscardText", designRootGo.transform, font, 13.5f, TextAlignmentOptions.Right,
                new Vector2(380, 312), new Vector2(240, 22), "最新出牌: -", Color.white);

            var statusBanner = CreateText("StatusBanner", designRootGo.transform, font, 16f, TextAlignmentOptions.Center,
                new Vector2(0, 180), new Vector2(600, 32), "", new Color(1f, 0.9f, 0.4f), FontStyles.Bold);
            statusBanner.gameObject.SetActive(false);

            var animBanner = CreateText("AnimationBanner", designRootGo.transform, font, 18f, TextAlignmentOptions.Center,
                new Vector2(0, 140), new Vector2(600, 36), "", new Color(0.4f, 1f, 0.8f), FontStyles.Bold);
            animBanner.gameObject.SetActive(false);

            // --- Four Seats ---
            var seatViews = new TableSeatView[4];
            Vector2[] seatPos = {
                new Vector2(0, -180),    // Bottom (Self: Seat 0 default)
                new Vector2(380, 40),    // Right (Seat 1)
                new Vector2(0, 220),     // Top (Seat 2)
                new Vector2(-380, 40)    // Left (Seat 3)
            };
            Vector2 seatSize = new Vector2(320, 110);

            for (int i = 0; i < 4; i++)
            {
                var sGo = CreateUiObject($"Seat_{i}", designRootGo.transform);
                var sRect = sGo.GetComponent<RectTransform>();
                sRect.anchoredPosition = seatPos[i];
                sRect.sizeDelta = seatSize;

                var sImg = sGo.AddComponent<Image>();
                sImg.color = new Color(0.1f, 0.16f, 0.12f, 0.75f);

                var nameTxt = CreateText("SeatName", sGo.transform, font, 14f, TextAlignmentOptions.Left,
                    new Vector2(-80, 43), new Vector2(130, 24), $"座位 {i}", Color.white, FontStyles.Bold);

                var scoreTxt = CreateText("ScoreText", sGo.transform, font, 14f, TextAlignmentOptions.Right,
                    new Vector2(80, 43), new Vector2(130, 24), "25000点", new Color(1f, 0.9f, 0.3f), FontStyles.Bold);

                var handTxt = CreateText("HandText", sGo.transform, font, 12f, TextAlignmentOptions.Left,
                    new Vector2(0, 16), new Vector2(300, 26), "手牌: 13张", new Color(0.85f, 0.85f, 0.85f));

                var riichiBadge = CreateUiObject("RiichiBadge", sGo.transform);
                var rRect = riichiBadge.GetComponent<RectTransform>();
                rRect.anchoredPosition = new Vector2(0, 43);
                rRect.sizeDelta = new Vector2(50, 20);
                var rImg = riichiBadge.AddComponent<Image>();
                rImg.color = new Color(0.85f, 0.15f, 0.15f, 1f);
                CreateText("RText", riichiBadge.transform, font, 10f, TextAlignmentOptions.Center,
                    Vector2.zero, new Vector2(50, 20), "立直", Color.white, FontStyles.Bold);
                riichiBadge.SetActive(false);

                var discardsTxt = CreateText("DiscardsText", sGo.transform, font, 11f, TextAlignmentOptions.Left,
                    new Vector2(0, -10), new Vector2(300, 22), "牌河: 无", new Color(0.7f, 0.85f, 0.8f));

                var meldsTxt = CreateText("MeldsText", sGo.transform, font, 11f, TextAlignmentOptions.Left,
                    new Vector2(-50, -32), new Vector2(190, 22), "副露: 门清", new Color(0.9f, 0.8f, 0.6f));

                var ankanTxt = CreateText("AnkanText", sGo.transform, font, 10f, TextAlignmentOptions.Right,
                    new Vector2(95, -32), new Vector2(100, 24), "", new Color(0.6f, 0.7f, 0.8f));

                var sv = new TableSeatView();
                sv.BindControls(sGo, nameTxt, scoreTxt, handTxt, riichiBadge, discardsTxt, meldsTxt, ankanTxt);
                seatViews[i] = sv;
            }

            // --- Own Hand Panel ---
            var handPanelGo = CreateUiObject("OwnHandPanel", designRootGo.transform);
            var handRect = handPanelGo.GetComponent<RectTransform>();
            handRect.anchoredPosition = new Vector2(0, -290);
            handRect.sizeDelta = new Vector2(1040, 90);

            var handTileButtons = new Button[14];
            var handTileTexts = new TextMeshProUGUI[14];
            var handTileMarkBadges = new GameObject[14];

            for (int i = 0; i < 14; i++)
            {
                float x = -440f + (i * 60f);
                var btn = CreateButton($"HandTile_{i}", handPanelGo.transform, font, 13f,
                    new Vector2(x, 0), new Vector2(58, 80), $"-");
                handTileButtons[i] = btn;
                handTileTexts[i] = btn.GetComponentInChildren<TextMeshProUGUI>();

                // Badge indicator under tile
                var badge = CreateUiObject($"MarkBadge_{i}", btn.transform);
                var bRect = badge.GetComponent<RectTransform>();
                bRect.anchoredPosition = new Vector2(0, -32);
                bRect.sizeDelta = new Vector2(16, 16);
                var bImg = badge.AddComponent<Image>();
                bImg.color = new Color(1f, 0.8f, 0.2f, 1f);
                CreateText("★", badge.transform, font, 10f, TextAlignmentOptions.Center,
                    Vector2.zero, new Vector2(16, 16), "★", Color.black, FontStyles.Bold);
                badge.SetActive(false);
                handTileMarkBadges[i] = badge;
            }

            // Drawn tile
            var drawnBtn = CreateButton("DrawnTile", handPanelGo.transform, font, 13f,
                new Vector2(430, 0), new Vector2(62, 80), "摸:-");
            var drawnTxt = drawnBtn.GetComponentInChildren<TextMeshProUGUI>();

            var drawnBadge = CreateUiObject("DrawnMarkBadge", drawnBtn.transform);
            var dbRect = drawnBadge.GetComponent<RectTransform>();
            dbRect.anchoredPosition = new Vector2(0, -32);
            dbRect.sizeDelta = new Vector2(16, 16);
            var dbImg = drawnBadge.AddComponent<Image>();
            dbImg.color = new Color(1f, 0.8f, 0.2f, 1f);
            CreateText("★", drawnBadge.transform, font, 10f, TextAlignmentOptions.Center,
                Vector2.zero, new Vector2(16, 16), "★", Color.black, FontStyles.Bold);
            drawnBadge.SetActive(false);

            // --- Action Candidates Panel ---
            var actionPanelGo = CreateUiObject("ActionPanel", designRootGo.transform);
            var actionRect = actionPanelGo.GetComponent<RectTransform>();
            actionRect.anchoredPosition = new Vector2(0, -215);
            actionRect.sizeDelta = new Vector2(880, 50);

            var actionButtons = new Dictionary<string, Button>();
            string[] actNames = {
                "DiscardButton", "ChiButton", "PonButton", "KanButton",
                "RiichiButton", "TsumoButton", "RonButton", "PassButton",
                "CancelSelectionButton"
            };
            string[] actLabels = { "打牌", "吃", "碰", "杠", "立直", "自摸", "荣和", "跳过", "取消选择" };

            for (int i = 0; i < actNames.Length; i++)
            {
                float x = -380f + (i * 95f);
                var btn = CreateButton(actNames[i], actionPanelGo.transform, font, 13f,
                    new Vector2(x, 0), new Vector2(88, 42), actLabels[i]);
                actionButtons[actNames[i]] = btn;
            }

            // --- Candidate Option Sub-panel (for multiple Chi/Kan/Riichi candidates) ---
            var candPanelGo = CreateUiObject("CandidateSelectionPanel", designRootGo.transform);
            var candRect = candPanelGo.GetComponent<RectTransform>();
            candRect.anchoredPosition = new Vector2(0, -150);
            candRect.sizeDelta = new Vector2(720, 85);
            var candBg = candPanelGo.AddComponent<Image>();
            candBg.color = new Color(0.12f, 0.18f, 0.25f, 0.95f);

            var candOptionButtons = new Button[6];
            var candOptionTexts = new TextMeshProUGUI[6];
            for (int i = 0; i < 6; i++)
            {
                float x = -240f + ((i % 4) * 115f);
                float y = 18f - ((i / 4) * 36f);
                var btn = CreateButton($"CandidateOption_{i}", candPanelGo.transform, font, 11f,
                    new Vector2(x, y), new Vector2(105, 32), $"选项 {i + 1}");
                candOptionButtons[i] = btn;
                candOptionTexts[i] = btn.GetComponentInChildren<TextMeshProUGUI>();
                btn.gameObject.SetActive(false);
            }
            var cancelCandBtn = CreateButton("CancelCandidateOptionButton", candPanelGo.transform, font, 12f,
                new Vector2(280, 0), new Vector2(70, 40), "取消");
            candPanelGo.SetActive(false);

            // --- Cheat Panel ---
            var cheatPanelGo = CreateUiObject("CheatPanel", designRootGo.transform);
            var cheatRect = cheatPanelGo.GetComponent<RectTransform>();
            cheatRect.anchoredPosition = new Vector2(530, -180);
            cheatRect.sizeDelta = new Vector2(180, 180);
            var cheatBg = cheatPanelGo.AddComponent<Image>();
            cheatBg.color = new Color(0.18f, 0.12f, 0.22f, 0.85f);

            var cheatPointsTxt = CreateText("CheatPointsText", cheatPanelGo.transform, font, 13f, TextAlignmentOptions.Top,
                new Vector2(0, 75), new Vector2(160, 24), "出千点数: 100", new Color(1f, 0.7f, 1f), FontStyles.Bold);

            var tileMarkBtn = CreateButton("TileMarkButton", cheatPanelGo.transform, font, 12f,
                new Vector2(0, 40), new Vector2(150, 32), "落汗绝技");
            var sleeveBloomBtn = CreateButton("SleeveBloomButton", cheatPanelGo.transform, font, 12f,
                new Vector2(0, 0), new Vector2(150, 32), "袖里藏花");
            var beamSwapBtn = CreateButton("BeamSwapButton", cheatPanelGo.transform, font, 12f,
                new Vector2(0, -40), new Vector2(150, 32), "偷梁换柱");
            var prepBtn = CreateButton("PreparationButton", cheatPanelGo.transform, font, 12f,
                new Vector2(0, -75), new Vector2(150, 28), "码牌准备");

            // --- Sub-Dialog: TileMark ---
            var tileMarkDlg = CreateUiObject("TileMarkDialog", designRootGo.transform);
            var tmRect = tileMarkDlg.GetComponent<RectTransform>();
            tmRect.anchoredPosition = Vector2.zero;
            tmRect.sizeDelta = new Vector2(400, 160);
            var tmBg = tileMarkDlg.AddComponent<Image>();
            tmBg.color = new Color(0.15f, 0.15f, 0.25f, 0.95f);

            CreateText("TMTitle", tileMarkDlg.transform, font, 16f, TextAlignmentOptions.Top,
                new Vector2(0, 60), new Vector2(360, 30), "落汗绝技 (选择4张牌进行标记)", Color.white, FontStyles.Bold);

            var tmStatus = CreateText("TMStatus", tileMarkDlg.transform, font, 13f, TextAlignmentOptions.Center,
                new Vector2(0, 15), new Vector2(360, 30), "已选牌 (0/4): 无", new Color(1f, 0.85f, 0.4f));

            var tmConfirm = CreateButton("ConfirmTileMarkButton", tileMarkDlg.transform, font, 13f,
                new Vector2(-80, -45), new Vector2(120, 36), "确认落汗");
            var tmCancel = CreateButton("CancelTileMarkButton", tileMarkDlg.transform, font, 13f,
                new Vector2(80, -45), new Vector2(120, 36), "取消");
            tileMarkDlg.SetActive(false);

            // --- Sub-Dialog: SleeveBloom ---
            var sleeveDlg = CreateUiObject("SleeveBloomDialog", designRootGo.transform);
            var slRect = sleeveDlg.GetComponent<RectTransform>();
            slRect.anchoredPosition = Vector2.zero;
            slRect.sizeDelta = new Vector2(450, 180);
            var slBg = sleeveDlg.AddComponent<Image>();
            slBg.color = new Color(0.18f, 0.12f, 0.22f, 0.95f);

            CreateText("SLTitle", sleeveDlg.transform, font, 16f, TextAlignmentOptions.Top,
                new Vector2(0, 70), new Vector2(400, 30), "袖里藏花调换选择", Color.white, FontStyles.Bold);

            var slStatus = CreateText("SLStatus", sleeveDlg.transform, font, 13f, TextAlignmentOptions.Center,
                new Vector2(0, 35), new Vector2(400, 24), "模式: IMMEDIATE", new Color(0.8f, 0.9f, 1f));

            var slSleeveTile = CreateText("SLSleeveTile", sleeveDlg.transform, font, 14f, TextAlignmentOptions.Center,
                new Vector2(0, 5), new Vector2(400, 26), "袖牌: -", new Color(1f, 0.85f, 0.3f), FontStyles.Bold);

            var slStore = CreateButton("SleeveStoreButton", sleeveDlg.transform, font, 12f,
                new Vector2(-150, -45), new Vector2(90, 36), "藏匿调换");
            var slSwap = CreateButton("SleeveSwapButton", sleeveDlg.transform, font, 12f,
                new Vector2(-50, -45), new Vector2(90, 36), "直接调换");
            var slPass = CreateButton("SleevePassButton", sleeveDlg.transform, font, 12f,
                new Vector2(50, -45), new Vector2(90, 36), "保留原摸牌");
            var slCancel = CreateButton("CancelSleeveButton", sleeveDlg.transform, font, 12f,
                new Vector2(150, -45), new Vector2(80, 36), "取消");
            sleeveDlg.SetActive(false);

            // --- Sub-Dialog: BeamSwap ---
            var beamDlg = CreateUiObject("BeamSwapDialog", designRootGo.transform);
            var bmRect = beamDlg.GetComponent<RectTransform>();
            bmRect.anchoredPosition = Vector2.zero;
            bmRect.sizeDelta = new Vector2(580, 250);
            var bmBg = beamDlg.AddComponent<Image>();
            bmBg.color = new Color(0.12f, 0.18f, 0.22f, 0.95f);

            CreateText("BMTitle", beamDlg.transform, font, 16f, TextAlignmentOptions.Top,
                new Vector2(0, 100), new Vector2(520, 30), "偷梁换柱 (牌山/死山等额互换)", Color.white, FontStyles.Bold);

            var bmStatus = CreateText("BMStatus", beamDlg.transform, font, 13f, TextAlignmentOptions.Center,
                new Vector2(0, 75), new Vector2(520, 24), "请选择1张自家手牌及1个候选目标槽位", new Color(1f, 0.9f, 0.4f));

            // Support up to 8 beam candidate slots
            var beamSlotButtons = new Button[8];
            var beamSlotTexts = new TextMeshProUGUI[8];
            for (int b = 0; b < 8; b++)
            {
                int col = b % 4;
                int row = b / 4;
                float bx = -210f + (col * 140f);
                float by = 35f - (row * 38f);
                var bBtn = CreateButton($"BeamSlot_{b}", beamDlg.transform, font, 11f,
                    new Vector2(bx, by), new Vector2(135, 34), $"槽位{b}");
                beamSlotButtons[b] = bBtn;
                beamSlotTexts[b] = bBtn.GetComponentInChildren<TextMeshProUGUI>();
            }

            var bmConfirm = CreateButton("ConfirmBeamSwapButton", beamDlg.transform, font, 13f,
                new Vector2(-140, -95), new Vector2(120, 36), "确认调换");
            var bmKeep = CreateButton("KeepBeamOriginalButton", beamDlg.transform, font, 13f,
                new Vector2(0, -95), new Vector2(120, 36), "保留原手牌");
            var bmCancel = CreateButton("CancelBeamButton", beamDlg.transform, font, 13f,
                new Vector2(140, -95), new Vector2(100, 36), "取消");
            beamDlg.SetActive(false);

            // --- Sub-Dialog: Preparation ---
            var prepDlg = CreateUiObject("PreparationDialog", designRootGo.transform);
            var prRect = prepDlg.GetComponent<RectTransform>();
            prRect.anchoredPosition = Vector2.zero;
            prRect.sizeDelta = new Vector2(400, 160);
            var prBg = prepDlg.AddComponent<Image>();
            prBg.color = new Color(0.15f, 0.20f, 0.15f, 0.95f);

            CreateText("PRTitle", prepDlg.transform, font, 16f, TextAlignmentOptions.Top,
                new Vector2(0, 60), new Vector2(360, 30), "开局码牌准备", Color.white, FontStyles.Bold);

            var prInfo = CreateText("PRInfo", prepDlg.transform, font, 13f, TextAlignmentOptions.Center,
                new Vector2(0, 25), new Vector2(360, 24), "洗牌目标: 默认", new Color(0.8f, 0.95f, 0.8f));

            var seeThroughBtn = CreateButton("SeeThroughButton", prepDlg.transform, font, 12f,
                new Vector2(0, -10), new Vector2(240, 32), "透视绝技[关闭] (10点)");
            var seeThroughTxt = seeThroughBtn.GetComponentInChildren<TextMeshProUGUI>();

            var prConfirm = CreateButton("ConfirmPreparationButton", prepDlg.transform, font, 13f,
                new Vector2(-80, -50), new Vector2(120, 36), "确认准备");
            var prCancel = CreateButton("CancelPreparationButton", prepDlg.transform, font, 13f,
                new Vector2(80, -50), new Vector2(120, 36), "取消");
            prepDlg.SetActive(false);

            // --- Navigation Buttons ---
            var backBtn = CreateButton("BackButton", designRootGo.transform, font, 13f,
                new Vector2(-580, 270), new Vector2(90, 36), "返回大厅");

            var nextRoundBtn = CreateButton("NextRoundButton", designRootGo.transform, font, 13f,
                new Vector2(0, -60), new Vector2(160, 44), "下一局准备");
            nextRoundBtn.gameObject.SetActive(false);

            var viewSettlementBtn = CreateButton("ViewSettlementButton", designRootGo.transform, font, 13f,
                new Vector2(550, 270), new Vector2(120, 36), "查看结算");
            viewSettlementBtn.gameObject.SetActive(false);

            // --- Owner Seat Control Panel ---
            var controlStatus = CreateText("ControlStatusText", designRootGo.transform, font, 13f,
                TextAlignmentOptions.Center, new Vector2(300, 300), new Vector2(260, 24),
                "控制状态: 同步中", new Color(0.85f, 0.95f, 1f), FontStyles.Bold);

            var releaseControlBtn = CreateButton("ReleaseControlButton", designRootGo.transform, font, 13f,
                new Vector2(300, 260), new Vector2(140, 36), "托管给电脑");
            var claimControlBtn = CreateButton("ClaimControlButton", designRootGo.transform, font, 13f,
                new Vector2(300, 220), new Vector2(140, 36), "收回控制权");

            // Serialize TablePresenter
            var soPresenter = new SerializedObject(presenter);
            soPresenter.FindProperty("_sessionInfoText").objectReferenceValue = sessionInfo;
            soPresenter.FindProperty("_roundStatusText").objectReferenceValue = roundStatus;
            soPresenter.FindProperty("_turnCountdownText").objectReferenceValue = turnCountdown;
            soPresenter.FindProperty("_wallRemainingText").objectReferenceValue = wallRemaining;
            soPresenter.FindProperty("_doraIndicatorsText").objectReferenceValue = doraIndicators;
            soPresenter.FindProperty("_lastDiscardText").objectReferenceValue = lastDiscard;

            soPresenter.FindProperty("_backButton").objectReferenceValue = backBtn;
            soPresenter.FindProperty("_nextRoundButton").objectReferenceValue = nextRoundBtn;
            soPresenter.FindProperty("_viewSettlementButton").objectReferenceValue = viewSettlementBtn;
            soPresenter.FindProperty("_claimControlButton").objectReferenceValue = claimControlBtn;
            soPresenter.FindProperty("_releaseControlButton").objectReferenceValue = releaseControlBtn;
            soPresenter.FindProperty("_controlStatusText").objectReferenceValue = controlStatus;

            // Seats array
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
            handBtnsProp.arraySize = 14;
            var handTxtsProp = soPresenter.FindProperty("_handTileTexts");
            handTxtsProp.arraySize = 14;
            for (int i = 0; i < 14; i++)
            {
                handBtnsProp.GetArrayElementAtIndex(i).objectReferenceValue = handTileButtons[i];
                handTxtsProp.GetArrayElementAtIndex(i).objectReferenceValue = handTileTexts[i];
            }
            soPresenter.FindProperty("_drawnTileButton").objectReferenceValue = drawnBtn;
            soPresenter.FindProperty("_drawnTileText").objectReferenceValue = drawnTxt;
            soPresenter.FindProperty("_drawnTileMarkBadge").objectReferenceValue = drawnBadge;

            // Hand tile mark badges (14 elements)
            var badgesProp = soPresenter.FindProperty("_handTileMarkBadges");
            badgesProp.arraySize = 14;
            for (int i = 0; i < 14; i++)
            {
                badgesProp.GetArrayElementAtIndex(i).objectReferenceValue = handTileMarkBadges[i];
            }

            // Action buttons
            soPresenter.FindProperty("_actionPanelRoot").objectReferenceValue = actionPanelGo;
            soPresenter.FindProperty("_discardButton").objectReferenceValue = actionButtons["DiscardButton"];
            soPresenter.FindProperty("_chiButton").objectReferenceValue = actionButtons["ChiButton"];
            soPresenter.FindProperty("_ponButton").objectReferenceValue = actionButtons["PonButton"];
            soPresenter.FindProperty("_kanButton").objectReferenceValue = actionButtons["KanButton"];
            soPresenter.FindProperty("_riichiButton").objectReferenceValue = actionButtons["RiichiButton"];
            soPresenter.FindProperty("_tsumoButton").objectReferenceValue = actionButtons["TsumoButton"];
            soPresenter.FindProperty("_ronButton").objectReferenceValue = actionButtons["RonButton"];
            soPresenter.FindProperty("_passButton").objectReferenceValue = actionButtons["PassButton"];
            soPresenter.FindProperty("_cancelSelectionButton").objectReferenceValue = actionButtons["CancelSelectionButton"];

            // Candidate sub-panel
            soPresenter.FindProperty("_candidateSelectionPanel").objectReferenceValue = candPanelGo;
            var candBtnsProp = soPresenter.FindProperty("_candidateOptionButtons");
            candBtnsProp.arraySize = 6;
            var candTxtsProp = soPresenter.FindProperty("_candidateOptionTexts");
            candTxtsProp.arraySize = 6;
            for (int i = 0; i < 6; i++)
            {
                candBtnsProp.GetArrayElementAtIndex(i).objectReferenceValue = candOptionButtons[i];
                candTxtsProp.GetArrayElementAtIndex(i).objectReferenceValue = candOptionTexts[i];
            }
            soPresenter.FindProperty("_cancelCandidateOptionButton").objectReferenceValue = cancelCandBtn;

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
            beamBtnsProp.arraySize = 8;
            var beamTxtsProp = soPresenter.FindProperty("_beamSlotTexts");
            beamTxtsProp.arraySize = 8;
            for (int b = 0; b < 8; b++)
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
            if (File.Exists(TableScenePath))
            {
                var raw = File.ReadAllLines(TableScenePath);
                for (int i = 0; i < raw.Length; i++) raw[i] = raw[i].TrimEnd();
                File.WriteAllLines(TableScenePath, raw);
            }
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
            tmp.enableWordWrapping = false;
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

            // Attach UiPointerLifecycleTrigger so pointer down/up/click events properly drive UiInputLifecycle
            go.AddComponent<UiPointerLifecycleTrigger>();

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
#endif
}
