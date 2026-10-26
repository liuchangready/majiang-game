#nullable enable
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Mahjong.Client;
using Mahjong.Unity.App;
using Mahjong.Unity.Platform;
using Mahjong.Unity.UI;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
#endif

namespace Mahjong.Unity.Tests.EditMode
{
    [TestFixture]
    public sealed class AppCompositionTests
    {
        [Test]
        public void AppComposition_Initializes_WithDefaults()
        {
            var dispatcher = new MainThreadDispatcher(128);
            var config = new AppConfig();
            var comp = new AppComposition(config, dispatcher);

            Assert.IsNotNull(comp.Paths);
            Assert.IsNotNull(comp.ThemeProvider);
            Assert.IsNotNull(comp.ChannelFactory);
            Assert.AreEqual(60, comp.Config.targetFrameRate);
            Assert.IsNull(comp.SessionCoordinator);
        }

        [Test]
        public void UnavailableLocalTableChannelFactory_ThrowsLocalTableUnavailable()
        {
            var factory = new UnavailableLocalTableChannelFactory();
            var ex = Assert.ThrowsAsync<LocalTableUnavailableException>(async () =>
            {
                await factory.CreateAsync(TableTransportKind.Local, CancellationToken.None);
            });

            Assert.AreEqual(LocalTableUnavailableException.ErrorCode, LocalTableUnavailableException.ErrorCode);
            StringAssert.Contains("LOCAL_HOST_UNAVAILABLE", LocalTableUnavailableException.ErrorCode);
        }

        [Test]
        public void UnavailableLocalTableChannelFactory_PreCancelled_ThrowsOperationCanceled()
        {
            var factory = new UnavailableLocalTableChannelFactory();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.ThrowsAsync<TaskCanceledException>(async () =>
            {
                await factory.CreateAsync(TableTransportKind.Local, cts.Token);
            });
        }

        [Test]
        public void UnavailableLocalTableChannelFactory_Wss_ThrowsNotSupported()
        {
            var factory = new UnavailableLocalTableChannelFactory();
            Assert.ThrowsAsync<NotSupportedException>(async () =>
            {
                await factory.CreateAsync(TableTransportKind.Wss, CancellationToken.None);
            });
        }

        [Test]
        public async Task AppComposition_ShutdownAsync_WithoutSession_ReturnsCompleted()
        {
            var dispatcher = new MainThreadDispatcher(128);
            var config = new AppConfig();
            var comp = new AppComposition(config, dispatcher);

            ShutdownResult result = await comp.ShutdownAsync("TEST_QUIT");
            Assert.AreEqual(ShutdownOutcome.Completed, result.Outcome);
            Assert.IsTrue(result.MenuAllowed);
            Assert.IsFalse(result.Quarantined);
        }

#if UNITY_EDITOR
        public static void BuildAllAssets()
        {
            Debug.Log("[ShellAssetBuilder] Starting asset and scene generation...");

            // 1. Create SDF Font Asset
            var font = AssetDatabase.LoadAssetAtPath<Font>("Assets/Art/UI/Fonts/NotoSansSC-Regular.ttf");
            if (font == null)
            {
                Debug.LogError("[ShellAssetBuilder] Could not load NotoSansSC-Regular.ttf");
                return;
            }

            var fontAsset = CreateFontAsset(font);
            if (fontAsset != null)
            {
                AssetDatabase.CreateAsset(fontAsset, "Assets/Art/UI/Fonts/NotoSansSC-Regular SDF.asset");
                var fontAssetType = fontAsset.GetType();
                var texs = fontAssetType.GetProperty("atlasTextures")?.GetValue(fontAsset) as Texture2D[];
                if (texs != null)
                {
                    foreach (var t in texs)
                    {
                        if (t != null)
                        {
                            AssetDatabase.AddObjectToAsset(t, fontAsset);
                        }
                    }
                }
                var mat = fontAssetType.GetProperty("material")?.GetValue(fontAsset) as Material;
                if (mat != null)
                {
                    AssetDatabase.AddObjectToAsset(mat, fontAsset);
                }
            }

            // 2. Create UiFontCatalog.asset
            var catalog = ScriptableObject.CreateInstance<UiFontCatalog>();
            var soCat = new SerializedObject(catalog);
            soCat.FindProperty("_primaryFont").objectReferenceValue = fontAsset;
            soCat.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.CreateAsset(catalog, "Assets/Art/UI/Fonts/UiFontCatalog.asset");

            // 3. Create UiTheme.asset
            var theme = ScriptableObject.CreateInstance<UiTheme>();
            var soTheme = new SerializedObject(theme);
            soTheme.FindProperty("fontAsset").objectReferenceValue = fontAsset;
            soTheme.FindProperty("buttonPrimarySprite").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Art/UI/MainMenu/button-primary.png");
            soTheme.FindProperty("buttonSecondarySprite").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Art/UI/MainMenu/button-secondary.png");
            soTheme.FindProperty("buttonFocusSprite").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Art/UI/MainMenu/button-focus.png");
            soTheme.FindProperty("backgroundSprite").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Art/UI/MainMenu/club-background.png");
            soTheme.FindProperty("menuIconsSprite").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Art/UI/MainMenu/menu-icons.png");
            soTheme.FindProperty("heroSprite").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Art/UI/MainMenu/octopus-hero.png");
            soTheme.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.CreateAsset(theme, "Assets/Art/UI/UiTheme.asset");

            // 4. Build Scenes
            BuildBootScene(theme, fontAsset);
            BuildMenuScene(theme, fontAsset);
            BuildLobbyScene(theme, fontAsset);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[ShellAssetBuilder] All assets and scenes generated successfully.");
        }

        private static UnityEngine.Object? CreateFontAsset(Font font)
        {
            var shaderUtilsType = Type.GetType("TMPro.ShaderUtilities, Unity.TextMeshPro");
            if (shaderUtilsType != null)
            {
                var field = shaderUtilsType.GetField("k_ShaderRef_MobileSDF", BindingFlags.Static | BindingFlags.NonPublic);
                if (field != null && field.GetValue(null) == null)
                {
                    var fallbackShader = Shader.Find("UI/Default") ?? Shader.Find("Sprites/Default") ?? Shader.Find("Hidden/Internal-Colored");
                    field.SetValue(null, fallbackShader);
                }
            }

            var type = Type.GetType("TMPro.TMP_FontAsset, Unity.TextMeshPro");
            if (type == null) return null;
            var method = type.GetMethod("CreateFontAsset", new[] { typeof(Font) });
            return method?.Invoke(null, new object[] { font }) as UnityEngine.Object;
        }

        private static Component CreateTmpText(GameObject go, string text, UnityEngine.Object? fontAsset, float fontSize, Color color)
        {
            var tmpType = Type.GetType("TMPro.TextMeshProUGUI, Unity.TextMeshPro");
            if (tmpType == null)
            {
                var legacy = go.AddComponent<Text>();
                legacy.text = text;
                legacy.fontSize = (int)fontSize;
                legacy.color = color;
                return legacy;
            }

            var comp = go.AddComponent(tmpType);
            var so = new SerializedObject(comp);
            var textProp = so.FindProperty("m_text");
            if (textProp != null) textProp.stringValue = text;
            var sizeProp = so.FindProperty("m_fontSize");
            if (sizeProp != null) sizeProp.floatValue = fontSize;
            var colorProp = so.FindProperty("m_fontColor");
            if (colorProp != null) colorProp.colorValue = color;
            if (fontAsset != null)
            {
                var fontProp = so.FindProperty("m_fontAsset");
                if (fontProp != null) fontProp.objectReferenceValue = fontAsset;
            }
            var alignProp = so.FindProperty("m_textAlignment");
            if (alignProp != null) alignProp.intValue = 514; // Center
            so.ApplyModifiedPropertiesWithoutUndo();
            return comp;
        }

        private static void BuildBootScene(UiTheme theme, UnityEngine.Object? fontAsset)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camGo = new GameObject("Main Camera");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = theme.backgroundColor;
            cam.orthographic = true;
            cam.orthographicSize = 5f;
            camGo.tag = "MainCamera";

            var bootGo = new GameObject("GameBootstrapper");
            var bootstrapper = bootGo.AddComponent<GameBootstrapper>();
            var so = new SerializedObject(bootstrapper);
            so.FindProperty("_theme").objectReferenceValue = theme;
            so.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.SaveScene(scene, "Assets/Scenes/Boot.unity");
        }

        private static void BuildMenuScene(UiTheme theme, UnityEngine.Object? fontAsset)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camGo = new GameObject("Main Camera");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = theme.backgroundColor;
            cam.orthographic = true;
            cam.orthographicSize = 5f;
            camGo.tag = "MainCamera";

            var guardGo = new GameObject("SceneGuard");
            guardGo.AddComponent<SceneGuard>();

            var canvasGo = new GameObject("Canvas");
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            canvasGo.AddComponent<GraphicRaycaster>();

            var bgGo = new GameObject("Background");
            bgGo.transform.SetParent(canvasGo.transform, false);
            var bgImg = bgGo.AddComponent<Image>();
            bgImg.sprite = theme.backgroundSprite;
            bgImg.color = theme.backgroundColor;
            var bgRect = bgGo.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.sizeDelta = Vector2.zero;

            var safeGo = new GameObject("SafeAreaRoot");
            safeGo.transform.SetParent(canvasGo.transform, false);
            var safeRect = safeGo.AddComponent<RectTransform>();
            var fitter = safeGo.AddComponent<SafeAreaFitter>();

            var designGo = new GameObject("DesignRoot");
            designGo.transform.SetParent(safeGo.transform, false);
            var designRect = designGo.AddComponent<RectTransform>();
            designRect.sizeDelta = new Vector2(1280, 720);
            designRect.anchorMin = new Vector2(0.5f, 0.5f);
            designRect.anchorMax = new Vector2(0.5f, 0.5f);
            designRect.pivot = new Vector2(0.5f, 0.5f);

            var soFitter = new SerializedObject(fitter);
            soFitter.FindProperty("_canvasScaler").objectReferenceValue = scaler;
            soFitter.FindProperty("_designRoot").objectReferenceValue = designRect;
            soFitter.ApplyModifiedPropertiesWithoutUndo();

            var titleGo = new GameObject("TitleText");
            titleGo.transform.SetParent(designGo.transform, false);
            var titleTmp = CreateTmpText(titleGo, "千术麻将 (Mahjong Cheater)", fontAsset, theme.titleFontSize, theme.textColor);
            var titleRect = titleGo.GetComponent<RectTransform>();
            titleRect.anchoredPosition = new Vector2(0, 180);
            titleRect.sizeDelta = new Vector2(900, 80);

            var playBtnGo = CreateButton("PlayButton", designGo.transform, new Vector2(0, 20), new Vector2(340, 68), "进入大厅", theme, fontAsset);
            var quitBtnGo = CreateButton("QuitButton", designGo.transform, new Vector2(0, -70), new Vector2(340, 68), "退出游戏", theme, fontAsset);

            var modalGo = CreateModalDialog("QuitModal", designGo.transform, theme, fontAsset);

            var presenterGo = new GameObject("MenuPresenter");
            presenterGo.transform.SetParent(designGo.transform, false);
            var presenter = presenterGo.AddComponent<MenuPresenter>();
            var soP = new SerializedObject(presenter);
            soP.FindProperty("_playButton").objectReferenceValue = playBtnGo.GetComponent<Button>();
            soP.FindProperty("_quitButton").objectReferenceValue = quitBtnGo.GetComponent<Button>();
            soP.FindProperty("_titleText").objectReferenceValue = titleTmp;
            soP.FindProperty("_quitModal").objectReferenceValue = modalGo.GetComponent<ModalPresenter>();
            soP.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.SaveScene(scene, "Assets/Scenes/Menu.unity");
        }

        private static void BuildLobbyScene(UiTheme theme, UnityEngine.Object? fontAsset)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camGo = new GameObject("Main Camera");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = theme.backgroundColor;
            cam.orthographic = true;
            cam.orthographicSize = 5f;
            camGo.tag = "MainCamera";

            var guardGo = new GameObject("SceneGuard");
            guardGo.AddComponent<SceneGuard>();

            var canvasGo = new GameObject("Canvas");
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            canvasGo.AddComponent<GraphicRaycaster>();

            var bgGo = new GameObject("Background");
            bgGo.transform.SetParent(canvasGo.transform, false);
            var bgImg = bgGo.AddComponent<Image>();
            bgImg.sprite = theme.backgroundSprite;
            bgImg.color = theme.backgroundColor;
            var bgRect = bgGo.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.sizeDelta = Vector2.zero;

            var safeGo = new GameObject("SafeAreaRoot");
            safeGo.transform.SetParent(canvasGo.transform, false);
            var safeRect = safeGo.AddComponent<RectTransform>();
            var fitter = safeGo.AddComponent<SafeAreaFitter>();

            var designGo = new GameObject("DesignRoot");
            designGo.transform.SetParent(safeGo.transform, false);
            var designRect = designGo.AddComponent<RectTransform>();
            designRect.sizeDelta = new Vector2(1280, 720);
            designRect.anchorMin = new Vector2(0.5f, 0.5f);
            designRect.anchorMax = new Vector2(0.5f, 0.5f);
            designRect.pivot = new Vector2(0.5f, 0.5f);

            var soFitter = new SerializedObject(fitter);
            soFitter.FindProperty("_canvasScaler").objectReferenceValue = scaler;
            soFitter.FindProperty("_designRoot").objectReferenceValue = designRect;
            soFitter.ApplyModifiedPropertiesWithoutUndo();

            var titleGo = new GameObject("TitleText");
            titleGo.transform.SetParent(designGo.transform, false);
            var titleTmp = CreateTmpText(titleGo, "游戏大厅 (Lobby)", fontAsset, theme.titleFontSize, theme.textColor);
            var titleRect = titleGo.GetComponent<RectTransform>();
            titleRect.anchoredPosition = new Vector2(0, 220);
            titleRect.sizeDelta = new Vector2(900, 80);

            // Banner
            var bannerGo = new GameObject("ConnectionBanner");
            bannerGo.transform.SetParent(designGo.transform, false);
            var bannerPresenter = bannerGo.AddComponent<ConnectionBannerPresenter>();
            var bannerTextGo = new GameObject("BannerText");
            bannerTextGo.transform.SetParent(bannerGo.transform, false);
            var bannerTmp = CreateTmpText(bannerTextGo, "本地宿主未接入 (LOCAL_HOST_UNAVAILABLE)", fontAsset, 24, theme.warningColor);
            var bannerRect = bannerGo.AddComponent<RectTransform>();
            bannerRect.anchoredPosition = new Vector2(0, 150);
            bannerRect.sizeDelta = new Vector2(800, 40);

            var soB = new SerializedObject(bannerPresenter);
            soB.FindProperty("_bannerRoot").objectReferenceValue = bannerGo;
            soB.FindProperty("_bannerText").objectReferenceValue = bannerTmp;
            soB.ApplyModifiedPropertiesWithoutUndo();

            // Status and explanation
            var statusGo = new GameObject("StatusText");
            statusGo.transform.SetParent(designGo.transform, false);
            var statusTmp = CreateTmpText(statusGo, "本地宿主未接入 (P4.1)", fontAsset, 28, theme.textColor);
            var statusRect = statusGo.GetComponent<RectTransform>();
            statusRect.anchoredPosition = new Vector2(0, 80);
            statusRect.sizeDelta = new Vector2(800, 40);

            var expGo = new GameObject("ExplanationText");
            expGo.transform.SetParent(designGo.transform, false);
            var expTmp = CreateTmpText(expGo, "本地对局通道尚未就绪 (LOCAL_HOST_UNAVAILABLE)。P4.2平台壳已就绪，等待P4.1本地工厂对接。", fontAsset, 22, theme.disabledTextColor);
            var expRect = expGo.GetComponent<RectTransform>();
            expRect.anchoredPosition = new Vector2(0, 30);
            expRect.sizeDelta = new Vector2(900, 40);

            var startBtnGo = CreateButton("StartLocalGameButton", designGo.transform, new Vector2(0, -50), new Vector2(360, 68), "开始本地对局 (不可用)", theme, fontAsset);
            startBtnGo.GetComponent<Button>().interactable = false;

            var backBtnGo = CreateButton("BackButton", designGo.transform, new Vector2(0, -140), new Vector2(340, 68), "返回菜单", theme, fontAsset);

            var presenterGo = new GameObject("LobbyPresenter");
            presenterGo.transform.SetParent(designGo.transform, false);
            var presenter = presenterGo.AddComponent<LobbyPresenter>();
            var soL = new SerializedObject(presenter);
            soL.FindProperty("_startLocalGameButton").objectReferenceValue = startBtnGo.GetComponent<Button>();
            soL.FindProperty("_backButton").objectReferenceValue = backBtnGo.GetComponent<Button>();
            soL.FindProperty("_statusText").objectReferenceValue = statusTmp;
            soL.FindProperty("_explanationText").objectReferenceValue = expTmp;
            soL.FindProperty("_connectionBanner").objectReferenceValue = bannerPresenter;
            soL.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.SaveScene(scene, "Assets/Scenes/Lobby.unity");
        }

        private static GameObject CreateButton(string name, Transform parent, Vector2 pos, Vector2 size, string text, UiTheme theme, UnityEngine.Object? fontAsset)
        {
            var btnGo = new GameObject(name);
            btnGo.transform.SetParent(parent, false);
            var rect = btnGo.AddComponent<RectTransform>();
            rect.anchoredPosition = pos;
            rect.sizeDelta = size;

            var img = btnGo.AddComponent<Image>();
            img.sprite = theme.buttonPrimarySprite;
            img.color = theme.primaryColor;

            var btn = btnGo.AddComponent<Button>();
            btn.targetGraphic = img;

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(btnGo.transform, false);
            var textRect = textGo.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.sizeDelta = Vector2.zero;

            CreateTmpText(textGo, text, fontAsset, theme.buttonFontSize, theme.textColor);

            return btnGo;
        }

        private static GameObject CreateModalDialog(string name, Transform parent, UiTheme theme, UnityEngine.Object? fontAsset)
        {
            var modalGo = new GameObject(name);
            modalGo.transform.SetParent(parent, false);
            var modalRect = modalGo.AddComponent<RectTransform>();
            modalRect.anchorMin = Vector2.zero;
            modalRect.anchorMax = Vector2.one;
            modalRect.sizeDelta = Vector2.zero;

            var bgImg = modalGo.AddComponent<Image>();
            bgImg.color = new Color(0, 0, 0, 0.75f);

            var panelGo = new GameObject("Panel");
            panelGo.transform.SetParent(modalGo.transform, false);
            var panelRect = panelGo.AddComponent<RectTransform>();
            panelRect.sizeDelta = new Vector2(600, 320);
            var panelImg = panelGo.AddComponent<Image>();
            panelImg.color = theme.secondaryColor;

            var titleGo = new GameObject("TitleText");
            titleGo.transform.SetParent(panelGo.transform, false);
            var titleTmp = CreateTmpText(titleGo, "提示", fontAsset, 32, theme.textColor);
            var titleRect = titleGo.GetComponent<RectTransform>();
            titleRect.anchoredPosition = new Vector2(0, 90);
            titleRect.sizeDelta = new Vector2(500, 40);

            var msgGo = new GameObject("MessageText");
            msgGo.transform.SetParent(panelGo.transform, false);
            var msgTmp = CreateTmpText(msgGo, "确定要退出游戏吗？", fontAsset, 24, theme.textColor);
            var msgRect = msgGo.GetComponent<RectTransform>();
            msgRect.anchoredPosition = new Vector2(0, 20);
            msgRect.sizeDelta = new Vector2(500, 60);

            var confirmBtnGo = CreateButton("ConfirmButton", panelGo.transform, new Vector2(-120, -70), new Vector2(180, 50), "确定", theme, fontAsset);
            var cancelBtnGo = CreateButton("CancelButton", panelGo.transform, new Vector2(120, -70), new Vector2(180, 50), "取消", theme, fontAsset);

            var tmpType = Type.GetType("TMPro.TextMeshProUGUI, Unity.TextMeshPro");
            var modalPresenter = modalGo.AddComponent<ModalPresenter>();
            var so = new SerializedObject(modalPresenter);
            so.FindProperty("_modalRoot").objectReferenceValue = modalGo;
            so.FindProperty("_titleText").objectReferenceValue = titleTmp;
            so.FindProperty("_messageText").objectReferenceValue = msgTmp;
            so.FindProperty("_confirmButton").objectReferenceValue = confirmBtnGo.GetComponent<Button>();
            so.FindProperty("_cancelButton").objectReferenceValue = cancelBtnGo.GetComponent<Button>();
            if (tmpType != null)
            {
                so.FindProperty("_confirmButtonText").objectReferenceValue = confirmBtnGo.GetComponentInChildren(tmpType);
                so.FindProperty("_cancelButtonText").objectReferenceValue = cancelBtnGo.GetComponentInChildren(tmpType);
            }
            so.ApplyModifiedPropertiesWithoutUndo();

            modalGo.SetActive(false);
            return modalGo;
        }
#endif
    }
}
