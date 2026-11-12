#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace Mahjong.Unity.UI.Prototype
{
    /// <summary>
    /// V5 Mahjong Art Prototype Controller.
    /// Demonstrates 2.5D tiles with thickness, bevels, drop shadows, hand/wall/river/melds,
    /// interactive animations (draw, select/hover, discard, riichi, opponent discard, chi/pon/kan, menu switch),
    /// demo controls (auto, pause, replay, step, speed), and 136-tile gallery with CC BY-SA 4.0 attribution.
    /// Purely presentation; not connected to network, authority, or settlement logic.
    /// </summary>
    public sealed class V5ArtPrototype : MonoBehaviour
    {
        [Header("Theme and Font")]
        [SerializeField] private UiTheme? _theme;

        [Header("V4 Menu Art Resources")]
        [SerializeField] private Sprite? _mainBackground;
        [SerializeField] private Sprite? _octopus;
        [SerializeField] private Sprite? _primaryButton;
        [SerializeField] private Sprite? _secondaryButton;
        [SerializeField] private Sprite? _focusButton;
        [SerializeField] private Sprite? _menuIconAtlas;

        [Header("V4 Table Art Resources")]
        [SerializeField] private Sprite? _environment;
        [SerializeField] private Sprite? _board;
        [SerializeField] private Sprite? _panel;
        [SerializeField] private Sprite? _redButton;
        [SerializeField] private Sprite? _jadeButton;
        [SerializeField] private Sprite[] _portraits = Array.Empty<Sprite>();
        [SerializeField] private Sprite[] _targetCards = Array.Empty<Sprite>();
        [SerializeField] private Sprite? _compass;
        [SerializeField] private Sprite[] _tiles = Array.Empty<Sprite>();

        [Header("2.5D Tile Art Resources")]
        [SerializeField] private Sprite? _tileStandingChassis;
        [SerializeField] private Sprite? _tileFlatFrontChassis;
        [SerializeField] private Sprite? _tileFlatBackChassis;
        [SerializeField] private Sprite? _tileSoftShadow;
        [SerializeField] private Sprite? _tileGlow;
        [SerializeField] private Sprite? _riichiStick;

        [Header("Hierarchy Containers")]
        [SerializeField] private Canvas? _canvas;
        [SerializeField] private RectTransform? _canvasRoot;
        [SerializeField] private RectTransform? _stageRoot;
        [SerializeField] private GameObject? _menuPage;
        [SerializeField] private GameObject? _tablePage;
        private GameObject? _seatPrototypePage;
        [SerializeField] private GameObject? _galleryModal;
        [SerializeField] private GameObject? _noticeModal;
        [SerializeField] private RawImage? _menuBackgroundImage;
        [SerializeField] private RawImage? _tableBackgroundImage;

        [Header("Table Element Roots")]
        [SerializeField] private RectTransform? _handTrayRoot;
        [SerializeField] private RectTransform? _riverRootP0;
        [SerializeField] private RectTransform? _riverRootP1;
        [SerializeField] private RectTransform? _riverRootP2;
        [SerializeField] private RectTransform? _riverRootP3;
        [SerializeField] private RectTransform? _meldsRoot;
        [SerializeField] private RectTransform? _wallRoot;
        [SerializeField] private RectTransform? _demoControlsBar;
        [SerializeField] private GameObject? _seatPrototypeV2Page;
        private RectTransform? _matchHudRoot;
        [SerializeField] private Text? _demoStatusText;
        [SerializeField] private Text? _wallCountText;
        [SerializeField] private Text? _pauseResumeButtonText;
        [SerializeField] private Text? _autoButtonText;
        [SerializeField] private Text? _speedButtonText;
        [SerializeField] private Image? _riichiStickImage;

        // Runtime animation & state tracking
        private readonly List<Tile2D5View> _handTiles = new List<Tile2D5View>();
        private Tile2D5View? _drawnTile;
        private readonly List<Tile2D5View> _riverTilesP0 = new List<Tile2D5View>();
        private readonly List<Tile2D5View>[] _opponentRiverTiles =
        {
            new List<Tile2D5View>(), new List<Tile2D5View>(), new List<Tile2D5View>()
        };
        private readonly List<Tile2D5View> _wallStackViews = new List<Tile2D5View>();
        private readonly List<Tile2D5View> _meldViews = new List<Tile2D5View>();
        private readonly List<Button> _menuButtons = new List<Button>();
        private readonly Dictionary<Button, Vector3> _menuButtonHomePositions = new Dictionary<Button, Vector3>();
        private readonly Dictionary<Button, CanvasGroup> _menuButtonCanvasGroups = new Dictionary<Button, CanvasGroup>();
        private readonly Dictionary<Sprite, Sprite> _croppedFaceSprites = new Dictionary<Sprite, Sprite>();
        private readonly List<Coroutine> _menuEntranceCoroutines = new List<Coroutine>();
        private readonly List<Coroutine> _activeCoroutines = new List<Coroutine>();
        private readonly List<Tile2D5View> _activeFlyingTiles = new List<Tile2D5View>();
        private readonly List<Button> _shuffleTargetButtons = new List<Button>();
        private readonly string[] _shuffleTargetNames = { "清一色（万）", "清一色（条）", "清一色（筒）", "大三元", "国士无双", "大四喜" };
        private Coroutine? _autoLoopCoroutine;
        private GameObject? _preparationPanel;
        private GameObject? _targetPickerPanel;
        private CanvasGroup? _targetPickerCanvasGroup;
        private Coroutine? _targetPickerEntranceCoroutine;
        private Text? _preparationSelectionText;
        private Button? _confirmTargetButton;
        private Button? _startDemoButton;
        private int _selectedShuffleTarget = -1;
        private bool _shuffleTargetConfirmed;

        private sealed class CoroutineHolder
        {
            public Coroutine? Coroutine;
            public bool IsCompleted;
        }

        private int _selectedHandIndex = -1;
        private bool _isPaused = false;
        private bool _isAutoLooping = false;
        private float _animationSpeedMultiplier = 1.0f;
        private int _remainingWallTiles = 69;
        private int _discardSequence = 0;
        private int _opponentDiscardSequence = 0;
        private int _meldDemoSequence = 0;
        private bool _isAnimating = false;
        private string _activePage = "menu";
        private int _lastScreenWidth;
        private int _lastScreenHeight;

        private enum DemoMeldKind
        {
            Chi,
            Pon,
            Kan
        }

        // Sample starting hand: 13 tiles + 1 draw slot
        private static readonly int[] InitialHandTiles = { 0, 1, 2, 9, 10, 11, 18, 19, 20, 27, 27, 31, 31 };
        private static readonly int[] SampleDrawDeck = { 34, 3, 12, 21, 28, 29, 32, 33, 4, 13, 22 };
        private int _drawDeckIndex = 0;
        private int _screenshotSequence;
        private bool _directTableMode;
        private bool _directTableV2Mode;

        public bool IsPaused => _isPaused;
        public bool IsAutoLooping => _isAutoLooping;
        public float AnimationSpeed => _animationSpeedMultiplier;

        private void Awake()
        {
            Tile2D5View.IsPausedProvider = () => _isPaused;
            BindHierarchy();
        }

        private void OnEnable()
        {
            Tile2D5View.IsPausedProvider = () => _isPaused;
        }

        private void Start()
        {
            Tile2D5View.IsPausedProvider = () => _isPaused;
            BindHierarchy();
            EventSystem eventSystem = GetComponentInChildren<EventSystem>(true);
            if (eventSystem != null)
            {
                InputSystemUIInputModule inputModule = eventSystem.GetComponent<InputSystemUIInputModule>();
                if (inputModule != null) inputModule.AssignDefaultActions();
            }
            EnsureRuntimeDemoControls();
            EnsurePreparationUi();
            EnsureSeatPrototypeUi();
            BindMenuButtons();
            BindModalButtons();
            ResetTableState();
            string[] args = Environment.GetCommandLineArgs();
            _directTableV2Mode = !Application.isEditor
                && Array.IndexOf(args, "--prototype-mahjong-table-v2") >= 0;
            _directTableMode = !Application.isEditor
                && (_directTableV2Mode || Array.IndexOf(args, "--prototype-mahjong-table") >= 0);
            if (_directTableV2Mode)
                ShowSeatPrototypeV2();
            else if (_directTableMode)
                ShowSeatPrototype();
            else
                ShowMenu();
            ApplyScreenLayout(true);
        }

        private void Update()
        {
            ApplyScreenLayout(false);
            HandleInput();
            if (_isAutoLooping && !_isAnimating && _autoLoopCoroutine == null)
            {
                _autoLoopCoroutine = StartControlledCoroutine(AutoLoopRoutine());
            }
        }

        private void HandleInput()
        {
            if (Keyboard.current == null) return;
            if (_directTableMode && (_activePage == "seat-prototype" || _activePage == "seat-prototype-v2")
                && Keyboard.current.cKey.wasPressedThisFrame)
                StartCoroutine(CapturePrototypeScreenshot());
            if (Keyboard.current.tabKey.wasPressedThisFrame || Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                if (_galleryModal != null && _galleryModal.activeSelf) _galleryModal.SetActive(false);
                else if (_noticeModal != null && _noticeModal.activeSelf) _noticeModal.SetActive(false);
                else if (_targetPickerPanel != null && _targetPickerPanel.activeSelf) CloseTargetPicker();
                else TogglePage();
            }
            if (Keyboard.current.f1Key.wasPressedThisFrame) ShowMenu();
            if (Keyboard.current.f2Key.wasPressedThisFrame) ShowTable();
            if (Keyboard.current.f3Key.wasPressedThisFrame) ShowGalleryModal();
            if (_activePage != "table") return;

            if (Keyboard.current.spaceKey.wasPressedThisFrame) TogglePause();
            if (Keyboard.current.rKey.wasPressedThisFrame) Replay();
            if (Keyboard.current.digit1Key.wasPressedThisFrame) TriggerDraw();
            if (Keyboard.current.digit2Key.wasPressedThisFrame) TriggerHover();
            if (Keyboard.current.digit3Key.wasPressedThisFrame) TriggerDiscard();
            if (Keyboard.current.digit4Key.wasPressedThisFrame) TriggerRiichi();
            if (Keyboard.current.digit5Key.wasPressedThisFrame) TriggerOpponentDiscard();
            if (Keyboard.current.digit6Key.wasPressedThisFrame) TriggerChi();
            if (Keyboard.current.digit7Key.wasPressedThisFrame) TriggerPon();
            if (Keyboard.current.digit8Key.wasPressedThisFrame) TriggerKan();
        }

        private IEnumerator CapturePrototypeScreenshot()
        {
            yield return new WaitForEndOfFrame();
            int width = Screen.width;
            int height = Screen.height;
            if (width < 2 || height < 2)
            {
                Debug.LogWarning("Prototype screenshot skipped: Player has no drawable client area.");
                yield break;
            }
            string screenshotDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "screenshots"));
            Directory.CreateDirectory(screenshotDirectory);
            string path = Path.Combine(screenshotDirectory,
                "table-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + _screenshotSequence++ + ".png");
            var screenshot = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                screenshot.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                screenshot.Apply();
                File.WriteAllBytes(path, ImageConversion.EncodeToPNG(screenshot));
            }
            finally
            {
                Destroy(screenshot);
            }
            Debug.Log("Prototype screenshot saved: " + path);
        }

        private void BindHierarchy()
        {
            if (_canvas == null) _canvas = GetComponentInChildren<Canvas>(true);
            if (_canvasRoot == null)
            {
                Transform root = transform.Find("Prototype Canvas/Prototype Content");
                if (root != null) _canvasRoot = root.GetComponent<RectTransform>();
            }
            if (_stageRoot == null)
            {
                Transform stage = transform.Find("Prototype Canvas/Prototype Content/Design stage · 1280×720");
                if (stage != null) _stageRoot = stage.GetComponent<RectTransform>();
            }
            if (_stageRoot != null)
            {
                if (_menuPage == null)
                {
                    Transform menu = _stageRoot.Find("Main menu");
                    if (menu != null) _menuPage = menu.gameObject;
                }
                if (_tablePage == null)
                {
                    Transform table = _stageRoot.Find("Mahjong table");
                    if (table != null) _tablePage = table.gameObject;
                }
                if (_seatPrototypePage == null)
                {
                    Transform seatPrototype = _stageRoot.Find("Prototype Mahjong");
                    if (seatPrototype != null) _seatPrototypePage = seatPrototype.gameObject;
                }
                if (_matchHudRoot == null && _tablePage != null)
                {
                    Transform hud = _tablePage.transform.Find("Match HUD");
                    if (hud != null) _matchHudRoot = hud.GetComponent<RectTransform>();
                }
                if (_galleryModal == null)
                {
                    Transform gallery = _stageRoot.Find("Gallery modal");
                    if (gallery != null) _galleryModal = gallery.gameObject;
                }
                if (_noticeModal == null)
                {
                    Transform notice = _stageRoot.Find("Notice modal");
                    if (notice != null) _noticeModal = notice.gameObject;
                }
            }

            if (_tablePage != null)
            {
                if (_handTrayRoot == null)
                {
                    Transform tray = _tablePage.transform.Find("HandTray");
                    if (tray != null) _handTrayRoot = tray.GetComponent<RectTransform>();
                }
                if (_wallRoot == null)
                {
                    Transform wall = _tablePage.transform.Find("WallRoot");
                    if (wall != null) _wallRoot = wall.GetComponent<RectTransform>();
                }
                if (_riverRootP0 == null)
                {
                    Transform r0 = _tablePage.transform.Find("RiverRoot_P0");
                    if (r0 != null) _riverRootP0 = r0.GetComponent<RectTransform>();
                }
                if (_meldsRoot == null)
                {
                    Transform m = _tablePage.transform.Find("MeldsRoot");
                    if (m != null) _meldsRoot = m.GetComponent<RectTransform>();
                }
                if (_demoControlsBar == null)
                {
                    Transform bar = _tablePage.transform.Find("DemoControlsBar");
                    if (bar != null) _demoControlsBar = bar.GetComponent<RectTransform>();
                }
                if (_demoStatusText == null && _demoControlsBar != null)
                {
                    Transform st = _demoControlsBar.Find("Text · DemoStatusText");
                    if (st != null) _demoStatusText = st.GetComponent<Text>();
                }
                if (_wallCountText == null)
                {
                    Transform wt = _tablePage.transform.Find("Text · WallCountText");
                    if (wt != null) _wallCountText = wt.GetComponent<Text>();
                }
                if (_riichiStickImage == null)
                {
                    Transform stick = _tablePage.transform.Find("RiichiStickImage");
                    if (stick != null) _riichiStickImage = stick.GetComponent<Image>();
                }

                BindDemoBarButtons();
            }

            BindModalButtons();
        }

        private void BindModalButtons()
        {
            if (_galleryModal != null)
            {
                Button[] gBtns = _galleryModal.GetComponentsInChildren<Button>(true);
                foreach (Button b in gBtns)
                {
                    if (b.name == "CloseGalleryButton" || b.name.Contains("Close"))
                    {
                        b.onClick.RemoveAllListeners();
                        b.onClick.AddListener(CloseGalleryModal);
                    }
                }
            }
            if (_noticeModal != null)
            {
                Button[] nBtns = _noticeModal.GetComponentsInChildren<Button>(true);
                foreach (Button b in nBtns)
                {
                    if (b.name == "CloseNoticeButton" || b.name.Contains("Close"))
                    {
                        b.onClick.RemoveAllListeners();
                        b.onClick.AddListener(CloseNotice);
                    }
                }
            }
        }

        private void BindDemoBarButtons()
        {
            if (_demoControlsBar == null) return;
            Button[] btns = _demoControlsBar.GetComponentsInChildren<Button>(true);
            foreach (Button b in btns)
            {
                b.onClick.RemoveAllListeners();
                if (b.name.Contains("Auto"))
                {
                    _autoButtonText = b.GetComponentInChildren<Text>();
                    b.onClick.AddListener(ToggleAutoLoop);
                }
                else if (b.name.Contains("Pause"))
                {
                    _pauseResumeButtonText = b.GetComponentInChildren<Text>();
                    b.onClick.AddListener(TogglePause);
                }
                else if (b.name.Contains("Replay"))
                {
                    b.onClick.AddListener(Replay);
                }
                else if (b.name.Contains("OpponentDiscard"))
                {
                    b.onClick.AddListener(TriggerOpponentDiscard);
                }
                else if (b.name.Contains("Chi"))
                {
                    b.onClick.AddListener(TriggerChi);
                }
                else if (b.name.Contains("Pon"))
                {
                    b.onClick.AddListener(TriggerPon);
                }
                else if (b.name.Contains("Kan"))
                {
                    b.onClick.AddListener(TriggerKan);
                }
                else if (b.name.Contains("Draw"))
                {
                    b.onClick.AddListener(TriggerDraw);
                }
                else if (b.name.Contains("Hover"))
                {
                    b.onClick.AddListener(TriggerHover);
                }
                else if (b.name.Contains("Discard"))
                {
                    b.onClick.AddListener(TriggerDiscard);
                }
                else if (b.name.Contains("Riichi"))
                {
                    b.onClick.AddListener(TriggerRiichi);
                }
                else if (b.name.Contains("Speed"))
                {
                    _speedButtonText = b.GetComponentInChildren<Text>();
                    b.onClick.AddListener(CycleSpeed);
                }
                else if (b.name.Contains("Menu") || b.name.Contains("ExitTable"))
                {
                    b.onClick.AddListener(ShowMenu);
                }
            }
        }

        private void EnsureRuntimeDemoControls()
        {
            if (_demoControlsBar == null) return;
            _demoControlsBar.sizeDelta = new Vector2(1200f, 96f);

            if (_demoStatusText != null)
            {
                SetRect(_demoStatusText.rectTransform, new Rect(14f, 8f, 258f, 76f));
                _demoStatusText.fontSize = 13;
            }

            EnsureDemoButton("AutoButton", "▶ 自动演示", new Rect(286f, 8f, 88f, 38f), _redButton, 14f);
            EnsureDemoButton("PauseButton", "⏸ 暂停", new Rect(378f, 8f, 66f, 38f), _jadeButton, 14f);
            EnsureDemoButton("ReplayButton", "🔄 重置", new Rect(448f, 8f, 68f, 38f), _jadeButton, 14f);
            EnsureDemoButton("DrawButton", "摸牌 [1]", new Rect(520f, 8f, 64f, 38f), _jadeButton, 14f);
            EnsureDemoButton("HoverButton", "选牌 [2]", new Rect(588f, 8f, 64f, 38f), _jadeButton, 14f);
            EnsureDemoButton("DiscardButton", "打牌 [3]", new Rect(656f, 8f, 64f, 38f), _redButton, 14f);
            EnsureDemoButton("RiichiButton", "立直 [4]", new Rect(724f, 8f, 64f, 38f), _redButton, 14f);
            EnsureDemoButton("SpeedButton", "1.0x", new Rect(792f, 8f, 58f, 38f), _jadeButton, 13f);
            EnsureDemoButton("OpponentDiscardButton", "对手出牌 [5]", new Rect(854f, 8f, 100f, 38f), _redButton, 14f);
            EnsureDemoButton("MenuButton", "退出牌桌", new Rect(958f, 8f, 132f, 38f), _jadeButton, 15f);
            EnsureDemoButton("ChiButton", "吃 [6]", new Rect(286f, 52f, 84f, 36f), _jadeButton, 14f);
            EnsureDemoButton("PonButton", "碰 [7]", new Rect(374f, 52f, 84f, 36f), _jadeButton, 14f);
            EnsureDemoButton("KanButton", "杠 [8]", new Rect(462f, 52f, 84f, 36f), _redButton, 14f);

            Text? hint = _demoControlsBar.GetComponentsInChildren<Text>(true).FirstOrDefault(t => t.name.Contains("CallPreviewHint"));
            if (hint == null) hint = Label("CallPreviewHint", new Rect(558f, 56f, 530f, 28f), 13f, new Color(1f, .86f, .58f), TextAlignmentOptions.Left, _demoControlsBar);
            hint.text = "对手舍牌动画与吃、碰、杠副露动画（视觉演示，不接入规则）";
            BindDemoBarButtons();
        }

        private Button EnsureDemoButton(string name, string label, Rect rect, Sprite? sprite, float fontSize)
        {
            Transform? existing = _demoControlsBar != null ? _demoControlsBar.Find(name) : null;
            Button? button = existing != null ? existing.GetComponent<Button>() : null;
            if (button == null)
            {
                button = ButtonRect(name, rect, sprite, _demoControlsBar!, fontSize);
            }
            else
            {
                SetRect(button.GetComponent<RectTransform>(), rect);
            }

            Text? text = button.GetComponentInChildren<Text>(true);
            if (text != null)
            {
                text.text = label;
                text.fontSize = Mathf.RoundToInt(fontSize);
            }
            return button;
        }

        private void BindMenuButtons()
        {
            if (_menuPage == null) return;
            _menuButtons.Clear();
            _menuButtonHomePositions.Clear();

            Button[] buttons = _menuPage.GetComponentsInChildren<Button>(true);
            foreach (Button button in buttons)
            {
                if (button.transform.parent != _menuPage.transform && button.transform.parent?.parent != _menuPage.transform)
                    continue;

                _menuButtons.Add(button);
                _menuButtonHomePositions[button] = button.transform.localPosition;
                CanvasGroup canvasGroup = button.GetComponent<CanvasGroup>();
                if (canvasGroup == null) canvasGroup = button.gameObject.AddComponent<CanvasGroup>();
                _menuButtonCanvasGroups[button] = canvasGroup;

                string label = button.name;
                if (label.Contains("原型麻将"))
                {
                    button.onClick.RemoveAllListeners();
                    button.onClick.AddListener(ShowSeatPrototype);
                }
                else if (label.Contains("单机对局") || label.Contains("进入牌桌"))
                {
                    button.onClick.RemoveAllListeners();
                    button.onClick.AddListener(ShowTable);
                }
                else if (label.Contains("牌面图鉴") || label.Contains("皮肤") || label.Contains("授权"))
                {
                    button.onClick.RemoveAllListeners();
                    button.onClick.AddListener(ShowGalleryModal);
                }
                else
                {
                    string noticeTitle = label.Replace("Button · ", "").Trim();
                    button.onClick.RemoveAllListeners();
                    button.onClick.AddListener(() => ShowNotice(noticeTitle, "此为 V5 美术原型占位入口，不连接实际网络与业务逻辑。"));
                }
            }
        }

        #region Page Navigation & Layout

        public void ShowMenu()
        {
            _isAutoLooping = false;
            if (_autoButtonText != null) _autoButtonText.text = "▶ 自动演示";
            StopAllActiveAnimations();
            _activePage = "menu";
            if (_menuPage != null) _menuPage.SetActive(true);
            if (_tablePage != null) _tablePage.SetActive(false);
            if (_seatPrototypePage != null) _seatPrototypePage.SetActive(false);
            if (_seatPrototypeV2Page != null) _seatPrototypeV2Page.SetActive(false);
            if (_galleryModal != null) _galleryModal.SetActive(false);
            if (_noticeModal != null) _noticeModal.SetActive(false);
            StopTargetPickerEntrance();
            if (_preparationPanel != null) _preparationPanel.SetActive(false);
            if (_targetPickerPanel != null) _targetPickerPanel.SetActive(false);

            if (_menuBackgroundImage != null) _menuBackgroundImage.gameObject.SetActive(true);
            if (_tableBackgroundImage != null) _tableBackgroundImage.gameObject.SetActive(false);

            SetFlyingTilesVisible(false);

            StartMenuEntranceAnimation();
        }

        public void ShowTable()
        {
            _activePage = "preparation";
            _selectedShuffleTarget = -1;
            _shuffleTargetConfirmed = false;
            ResetTableState();
            if (_menuPage != null) _menuPage.SetActive(false);
            if (_tablePage != null) _tablePage.SetActive(true);
            if (_seatPrototypePage != null) _seatPrototypePage.SetActive(false);
            if (_seatPrototypeV2Page != null) _seatPrototypeV2Page.SetActive(false);
            if (_galleryModal != null) _galleryModal.SetActive(false);
            if (_noticeModal != null) _noticeModal.SetActive(false);

            if (_menuBackgroundImage != null) _menuBackgroundImage.gameObject.SetActive(false);
            if (_tableBackgroundImage != null) _tableBackgroundImage.gameObject.SetActive(true);

            SetMatchDemoVisible(false);
            if (_preparationPanel != null) _preparationPanel.SetActive(true);
            if (_targetPickerPanel != null) _targetPickerPanel.SetActive(false);
            RefreshPreparationSelection();

            SetStatus("本局准备：选择偷天换日目标，或直接开始视觉演示");
        }

        public void ShowSeatPrototype()
        {
            if (_stageRoot == null) return;
            _isAutoLooping = false;
            StopAllActiveAnimations();
            _activePage = "seat-prototype";
            if (_menuPage != null) _menuPage.SetActive(false);
            if (_tablePage != null) _tablePage.SetActive(false);
            if (_seatPrototypeV2Page != null) _seatPrototypeV2Page.SetActive(false);
            if (_seatPrototypePage != null) _seatPrototypePage.SetActive(true);
            if (_galleryModal != null) _galleryModal.SetActive(false);
            if (_noticeModal != null) _noticeModal.SetActive(false);
            if (_preparationPanel != null) _preparationPanel.SetActive(false);
            if (_targetPickerPanel != null) _targetPickerPanel.SetActive(false);
            if (_menuBackgroundImage != null) _menuBackgroundImage.gameObject.SetActive(false);
            if (_tableBackgroundImage != null) _tableBackgroundImage.gameObject.SetActive(false);
            SetFlyingTilesVisible(false);
        }

        public void ShowSeatPrototypeV2()
        {
            if (_stageRoot == null) return;
            _isAutoLooping = false;
            StopAllActiveAnimations();
            EnsureSeatPrototypeV2Ui();
            _activePage = "seat-prototype-v2";
            if (_menuPage != null) _menuPage.SetActive(false);
            if (_tablePage != null) _tablePage.SetActive(false);
            if (_seatPrototypePage != null) _seatPrototypePage.SetActive(false);
            if (_seatPrototypeV2Page != null) _seatPrototypeV2Page.SetActive(true);
            if (_galleryModal != null) _galleryModal.SetActive(false);
            if (_noticeModal != null) _noticeModal.SetActive(false);
            if (_preparationPanel != null) _preparationPanel.SetActive(false);
            if (_targetPickerPanel != null) _targetPickerPanel.SetActive(false);
            if (_menuBackgroundImage != null) _menuBackgroundImage.gameObject.SetActive(false);
            if (_tableBackgroundImage != null) _tableBackgroundImage.gameObject.SetActive(false);
            SetFlyingTilesVisible(false);
        }

        private void EnsureSeatPrototypeV2Ui()
        {
            if (_stageRoot == null || _seatPrototypeV2Page != null) return;
            _seatPrototypeV2Page = CreatePage("Prototype Mahjong V2", _stageRoot);
            BuildSeatPrototypeContent(_seatPrototypeV2Page.transform, true);
            _seatPrototypeV2Page.SetActive(false);
        }

        private void EnsureSeatPrototypeUi()
        {
            if (_stageRoot == null || _menuPage == null) return;

            if (_seatPrototypePage == null)
            {
                _seatPrototypePage = CreatePage("Prototype Mahjong", _stageRoot);
                BuildSeatPrototypeContent(_seatPrototypePage.transform);
            }
            _seatPrototypePage.SetActive(false);

            var menuButton = _menuPage.transform.Find("Button · 原型麻将")?.GetComponent<Button>();
            if (menuButton == null)
            {
                menuButton = ButtonRect("Button · 原型麻将", new Rect(752, 370, 440, 50), _secondaryButton, _menuPage.transform, 20);
                AddMenuIcon(menuButton.transform, 2, _menuIconAtlas, 42, 7);
            }
            Text? menuText = menuButton.GetComponentInChildren<Text>(true);
            if (menuText != null)
            {
                menuText.text = "原型麻将";
                SetRect(menuText.rectTransform, new Rect(84, 0, 340, 50));
                menuText.alignment = TextAnchor.MiddleLeft;
            }

            string[] names = { "单机对局", "联机对战", "牌面图鉴", "原型麻将", "千术秘籍", "游戏设置", "退出游戏" };
            float[] y = { 150, 230, 310, 370, 430, 490, 550 };
            for (int i = 0; i < names.Length; i++)
            {
                Transform? buttonTransform = _menuPage.transform.Find("Button · " + names[i]);
                if (buttonTransform == null) continue;
                RectTransform buttonRect = buttonTransform.GetComponent<RectTransform>();
                SetRect(buttonRect, new Rect(752, y[i], 440, i < 2 ? 70 : 50));
            }
        }

        private void BuildSeatPrototypeContent(Transform parent, bool connectedWallV2 = false)
        {
            var bgGo = new GameObject("Blue felt background", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            bgGo.transform.SetParent(parent, false);
            RawImage bg = bgGo.GetComponent<RawImage>();
            bg.raycastTarget = false;
            Sprite? feltSprite = Resources.Load<Sprite>("Prototype/Table/mahjong-table-felt");
            if (feltSprite != null)
            {
                bg.texture = feltSprite.texture;
                float sourceAspect = (float)bg.texture.width / bg.texture.height;
                float targetAspect = 1280f / 720f;
                if (sourceAspect > targetAspect)
                {
                    float cropWidth = targetAspect / sourceAspect;
                    bg.uvRect = new Rect((1f - cropWidth) * .5f, 0f, cropWidth, 1f);
                }
                else
                {
                    float cropHeight = sourceAspect / targetAspect;
                    bg.uvRect = new Rect(0f, (1f - cropHeight) * .5f, 1f, cropHeight);
                }
            }
            Stretch(bg.rectTransform);

            var feltWashGo = new GameObject("Blue felt light wash", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            feltWashGo.transform.SetParent(parent, false);
            Image feltWash = feltWashGo.GetComponent<Image>();
            feltWash.color = new Color(.38f, .52f, .72f, .14f);
            feltWash.raycastTarget = false;
            Stretch(feltWash.rectTransform);

            if (connectedWallV2)
            {
                CreateMahjongWall("WallOpposite V2", MahjongWallSeat.North, parent);
                CreateMahjongWall("WallLeft V2", MahjongWallSeat.West, parent);
                CreateMahjongWall("WallRight V2", MahjongWallSeat.East, parent);
            }
            else
            {
                // The tile backs meet at their front faces; overlap the hidden sprite margins.
                int[] topWall = Enumerable.Repeat(0, 14).ToArray();
                int[] sideWall = Enumerable.Repeat(0, 13).ToArray();
                CreateConnectedTileRow("WallOpposite", topWall, Tile2D5Mode.StandingBack,
                    new Vector2(535, 14), new Vector2(28, 44), 25.5f, false, parent);
                CreateConnectedTileRow("WallLeft", sideWall, Tile2D5Mode.StandingBack,
                    new Vector2(220, 83), new Vector2(28, 38), 16.5f, true, parent, -4.5f);
                CreateConnectedTileRow("WallRight", sideWall, Tile2D5Mode.StandingBack,
                    new Vector2(1085, 222), new Vector2(34, 46), 21.5f, true, parent, 6.5f);
            }

            int[] river = { 27, 28, 29, 31, 32, 33, 34, 35, 36, 4, 13, 22, 30, 5, 14, 23, 6, 15, 24, 26 };
            CreateConnectedTileRow("RiverOpposite", river.Take(5).ToArray(), Tile2D5Mode.FlatFront,
                new Vector2(520, 159), new Vector2(34, 44), 29, false, parent);
            CreateConnectedTileRow("RiverSelf", river.Skip(5).Take(5).ToArray(), Tile2D5Mode.FlatFront,
                new Vector2(520, 435), new Vector2(34, 44), 29, false, parent);
            CreateConnectedTileRow("RiverLeft", river.Skip(10).Take(5).ToArray(), Tile2D5Mode.FlatFront,
                new Vector2(430, 287), new Vector2(34, 44), 29, true, parent);
            CreateConnectedTileRow("RiverRight", river.Skip(15).Take(5).ToArray(), Tile2D5Mode.FlatFront,
                new Vector2(818, 287), new Vector2(34, 44), 29, true, parent);

            Image centerFrame = ImageRect("Center plate frame", null, new Rect(562, 272, 156, 138), parent, false, false);
            centerFrame.color = new Color(.57f, .43f, .23f, 1f);
            Image center = ImageRect("Center plate", null, new Rect(567, 277, 146, 128), parent, false, false);
            center.color = new Color(.035f, .09f, .15f, .96f);
            Label("东一局", new Rect(577, 282, 126, 25), 17, new Color(.82f, .88f, .94f), TextAlignmentOptions.Center, parent);
            Label("25,000", new Rect(593, 311, 94, 28), 19, new Color(1f, .84f, .42f), TextAlignmentOptions.Center, parent);
            Label("本场 0  ·  供托 0", new Rect(573, 343, 134, 20), 12, Color.white, TextAlignmentOptions.Center, parent);
            Label("牌山 69", new Rect(590, 371, 100, 20), 13, new Color(.65f, .86f, .96f), TextAlignmentOptions.Center, parent);

            DrawPrototypeSeat("对家", _portraits.Length > 1 ? _portraits[1] : null,
                new Rect(918, 22, 58, 68), new Rect(977, 36, 110, 42), parent);
            DrawPrototypeSeat("上家", _portraits.Length > 2 ? _portraits[2] : null,
                new Rect(20, 104, 62, 72), new Rect(16, 178, 92, 42), parent);
            DrawPrototypeSeat("下家", _portraits.Length > 3 ? _portraits[3] : null,
                new Rect(1174, 148, 58, 68), new Rect(1148, 220, 88, 42), parent);
            DrawPrototypeSeat("本家", _portraits.Length > 0 ? _portraits[0] : null,
                new Rect(254, 518, 60, 70), new Rect(316, 530, 126, 44), parent);

            CreateConnectedTileRow("PlayerHand", InitialHandTiles, Tile2D5Mode.FlatFront,
                new Vector2(150, 618), new Vector2(64, 92), 56, false, parent);
            CreatePrototypeTile("PlayerDrawn", SampleDrawDeck[0], Tile2D5Mode.FlatFront,
                new Vector2(150 + 13 * 56 + 13, 618), new Vector2(64, 92), false, parent);

            Button back = ButtonRect("Back to menu", new Rect(1142, 12, 104, 32), _secondaryButton, parent, 14);
            Text? backText = back.GetComponentInChildren<Text>(true);
            if (backText != null) backText.text = "返回";
            back.onClick.AddListener(ShowMenu);
        }

        private static void CreateMahjongWall(string name, MahjongWallSeat seat, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(MahjongWallRowGraphic));
            go.transform.SetParent(parent, false);
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(1280f, 720f);
            go.GetComponent<MahjongWallRowGraphic>().Configure(seat);
        }

        private void DrawPrototypeSeat(string name, Sprite? portrait, Rect portraitRect, Rect plateRect, Transform parent)
        {
            ImageRect("Seat " + name + " portrait", portrait, portraitRect, parent, false, true);
            Image plate = ImageRect("Seat " + name + " plate", null, plateRect, parent, false, false);
            plate.color = new Color(.025f, .045f, .085f, .85f);
            Label(name + "\n25,000", new Rect(plateRect.x + 3, plateRect.y + 1, plateRect.width - 6, plateRect.height - 2),
                12, Color.white, TextAlignmentOptions.Center, parent);
        }

        private void CreateConnectedTileRow(string name, int[] tileIndices, Tile2D5Mode mode,
            Vector2 origin, Vector2 size, float step, bool rotated, Transform parent, float horizontalDrift = 0f)
        {
            for (int i = 0; i < tileIndices.Length; i++)
            {
                Vector2 position = rotated
                    ? new Vector2(origin.x + i * horizontalDrift, origin.y + i * step)
                    : new Vector2(origin.x + i * step, origin.y);
                Tile2D5View tile = CreatePrototypeTile(name + "_" + i, tileIndices[i], mode, position, size, rotated, parent);
                if (rotated && horizontalDrift != 0f)
                {
                    float angle = Mathf.Atan2(horizontalDrift, step) * Mathf.Rad2Deg;
                    tile.transform.localRotation = Quaternion.Euler(0f, 0f, 90f + angle);
                }
            }
        }

        private Tile2D5View CreatePrototypeTile(string name, int tileIndex, Tile2D5Mode mode,
            Vector2 position, Vector2 size, bool rotated, Transform parent)
        {
            Tile2D5View tile = CreateTileInstance(name, tileIndex, mode, position, size, parent, false, false, rotated);
            if (rotated)
            {
                RectTransform rect = tile.GetComponent<RectTransform>();
                rect.pivot = new Vector2(.5f, .5f);
                rect.anchoredPosition = new Vector2(position.x + size.x * .5f, -position.y - size.y * .5f);
            }
            return tile;
        }

        private void StartDemoMatch()
        {
            _activePage = "table";
            if (_preparationPanel != null) _preparationPanel.SetActive(false);
            if (_targetPickerPanel != null) _targetPickerPanel.SetActive(false);
            SetMatchDemoVisible(true);
            string choice = _shuffleTargetConfirmed && _selectedShuffleTarget >= 0
                ? "视觉选择：" + _shuffleTargetNames[_selectedShuffleTarget] + "（未提交真实技能）"
                : "直接开局演示（不施术）";
            SetStatus(choice + " · 可用底部按钮查看牌桌动画");
        }

        private void SetMatchDemoVisible(bool visible)
        {
            if (_matchHudRoot != null) _matchHudRoot.gameObject.SetActive(visible);
            if (_handTrayRoot != null) _handTrayRoot.gameObject.SetActive(visible);
            if (_wallRoot != null) _wallRoot.gameObject.SetActive(visible);
            if (_riverRootP0 != null) _riverRootP0.gameObject.SetActive(visible);
            if (_riverRootP1 != null) _riverRootP1.gameObject.SetActive(visible);
            if (_riverRootP2 != null) _riverRootP2.gameObject.SetActive(visible);
            if (_riverRootP3 != null) _riverRootP3.gameObject.SetActive(visible);
            if (_meldsRoot != null) _meldsRoot.gameObject.SetActive(visible);
            if (_demoControlsBar != null) _demoControlsBar.gameObject.SetActive(visible);
            SetFlyingTilesVisible(visible);
        }

        private void EnsurePreparationUi()
        {
            if (_tablePage == null || _preparationPanel != null) return;

            RectTransform table = _tablePage.GetComponent<RectTransform>();
            if (_matchHudRoot == null)
            {
                GameObject hud = CreatePage("Match HUD", table);
                _matchHudRoot = hud.GetComponent<RectTransform>();
                for (int childIndex = table.childCount - 1; childIndex >= 0; childIndex--)
                {
                    Transform child = table.GetChild(childIndex);
                    if (child == hud.transform) continue;
                    string childName = child.name;
                    if (childName.StartsWith("Animal portrait", StringComparison.Ordinal)
                        || childName.StartsWith("Seat plate", StringComparison.Ordinal)
                        || childName == "Round compass"
                        || childName == "Text · WallCountText"
                        || childName == "RiichiStickImage")
                    {
                        child.SetParent(_matchHudRoot, false);
                    }
                }
            }
            _preparationPanel = CreatePage("Round preparation", table);
            Image prepPanel = ImageRect("Preparation panel", _panel, new Rect(936, 48, 312, 624), _preparationPanel.transform, true, false);
            prepPanel.color = new Color(1f, 1f, 1f, .98f);
            Label("本局准备", new Rect(956, 70, 272, 48), 30, new Color(1f, .86f, .52f), TextAlignmentOptions.Center, _preparationPanel.transform);
            Label("进入牌桌后再确认本局方案", new Rect(956, 122, 272, 30), 16, Color.white, TextAlignmentOptions.Center, _preparationPanel.transform);
            Button menuButton = ButtonRect("PreparationMenuButton", new Rect(956, 150, 272, 32), _secondaryButton, _preparationPanel.transform, 15);
            menuButton.GetComponentInChildren<Text>().text = "返回主菜单";
            menuButton.onClick.AddListener(ShowMenu);

            Button chooseTarget = ButtonRect("OpenShuffleTargetPicker", new Rect(956, 188, 272, 156), _targetCards.Length > 0 ? _targetCards[0] : _primaryButton, _preparationPanel.transform, 24);
            chooseTarget.GetComponentInChildren<Text>().text = "偷天换日 · 30点\n选择一个目标牌型";
            chooseTarget.onClick.AddListener(OpenTargetPicker);

            _preparationSelectionText = Label("当前方案：不施术", new Rect(956, 464, 272, 56), 17, Color.white, TextAlignmentOptions.Center, _preparationPanel.transform);
            _startDemoButton = ButtonRect("StartDemoMatch", new Rect(956, 552, 272, 76), _primaryButton, _preparationPanel.transform, 23);
            _startDemoButton.onClick.AddListener(StartDemoMatch);
            Label("仅演示选择界面；不扣点、不执行真实码牌", new Rect(944, 642, 296, 24), 12, new Color(1f, .87f, .65f), TextAlignmentOptions.Center, _preparationPanel.transform);

            _targetPickerPanel = CreatePage("Shuffle target picker", table);
            _targetPickerCanvasGroup = _targetPickerPanel.AddComponent<CanvasGroup>();
            Image pickerPanel = ImageRect("Target picker panel", _panel, new Rect(688, 40, 560, 640), _targetPickerPanel.transform, true, false);
            pickerPanel.color = new Color(1f, 1f, 1f, .99f);
            Label("偷天换日 · 选择目标", new Rect(712, 56, 512, 46), 28, new Color(1f, .86f, .52f), TextAlignmentOptions.Center, _targetPickerPanel.transform);
            Label("影响起手及后续摸牌，不保证成型。", new Rect(712, 104, 512, 32), 16, Color.white, TextAlignmentOptions.Center, _targetPickerPanel.transform);

            Rect[] targetRects = {
                new Rect(704, 152, 252, 94), new Rect(968, 152, 252, 94),
                new Rect(704, 260, 252, 94), new Rect(968, 260, 252, 94),
                new Rect(704, 368, 252, 94), new Rect(968, 368, 252, 94)
            };
            for (int i = 0; i < _shuffleTargetNames.Length; i++)
            {
                int targetIndex = i;
                Sprite? card = i < _targetCards.Length ? _targetCards[i] : _secondaryButton;
                Button targetButton = ButtonRect("ShuffleTarget_" + i, targetRects[i], card, _targetPickerPanel.transform, 18);
                targetButton.GetComponentInChildren<Text>().text = _shuffleTargetNames[i];
                targetButton.onClick.AddListener(() => SelectShuffleTarget(targetIndex));
                _shuffleTargetButtons.Add(targetButton);
            }

            Label("仅目标偏好 · 视觉原型不提交技能", new Rect(712, 480, 512, 28), 14, new Color(1f, .87f, .65f), TextAlignmentOptions.Center, _targetPickerPanel.transform);
            Button backButton = ButtonRect("ReturnFromTargetPicker", new Rect(704, 548, 252, 56), _secondaryButton, _targetPickerPanel.transform, 18);
            backButton.GetComponentInChildren<Text>().text = "返回准备";
            backButton.onClick.AddListener(CloseTargetPicker);
            _confirmTargetButton = ButtonRect("ConfirmShuffleTarget", new Rect(968, 548, 252, 56), _primaryButton, _targetPickerPanel.transform, 18);
            _confirmTargetButton.GetComponentInChildren<Text>().text = "确认方案 · 30点";
            _confirmTargetButton.interactable = false;
            _confirmTargetButton.onClick.AddListener(ConfirmShuffleTarget);

            _targetPickerPanel.SetActive(false);
            _preparationPanel.SetActive(false);
        }

        private void OpenTargetPicker()
        {
            if (_targetPickerPanel == null || _preparationPanel == null) return;
            _selectedShuffleTarget = -1;
            _shuffleTargetConfirmed = false;
            _targetPickerPanel.SetActive(true);
            _preparationPanel.SetActive(false);
            RefreshTargetButtons();
            _targetPickerEntranceCoroutine = StartCoroutine(AnimateTargetPickerEntrance());
        }

        private void CloseTargetPicker()
        {
            StopTargetPickerEntrance();
            _selectedShuffleTarget = -1;
            if (_targetPickerPanel != null) _targetPickerPanel.SetActive(false);
            if (_preparationPanel != null) _preparationPanel.SetActive(true);
            RefreshPreparationSelection();
        }

        private void SelectShuffleTarget(int index)
        {
            _selectedShuffleTarget = _selectedShuffleTarget == index ? -1 : index;
            RefreshTargetButtons();
        }

        private void RefreshTargetButtons()
        {
            for (int i = 0; i < _shuffleTargetButtons.Count; i++)
            {
                Button button = _shuffleTargetButtons[i];
                if (button == null) continue;
                Text? text = button.GetComponentInChildren<Text>();
                if (text != null) text.text = (_selectedShuffleTarget == i ? "✓  " : string.Empty) + _shuffleTargetNames[i];
                button.transform.localScale = _selectedShuffleTarget == i ? Vector3.one * 1.035f : Vector3.one;
            }
            if (_confirmTargetButton != null) _confirmTargetButton.interactable = _selectedShuffleTarget >= 0;
        }

        private void ConfirmShuffleTarget()
        {
            if (_selectedShuffleTarget < 0) return;
            StopTargetPickerEntrance();
            _shuffleTargetConfirmed = true;
            if (_targetPickerPanel != null) _targetPickerPanel.SetActive(false);
            if (_preparationPanel != null) _preparationPanel.SetActive(true);
            RefreshPreparationSelection();
        }

        private void RefreshPreparationSelection()
        {
            bool selected = _shuffleTargetConfirmed && _selectedShuffleTarget >= 0;
            if (_preparationSelectionText != null) _preparationSelectionText.text = selected
                ? "当前方案：" + _shuffleTargetNames[_selectedShuffleTarget] + " · 30点（视觉预览）"
                : "当前方案：不施术 · 0点";
            Text? startText = _startDemoButton != null ? _startDemoButton.GetComponentInChildren<Text>() : null;
            if (startText != null) startText.text = selected ? "使用所选方案 · 开始演示" : "直接开局演示 · 0点";
        }

        private IEnumerator AnimateTargetPickerEntrance()
        {
            if (_targetPickerPanel == null || _targetPickerCanvasGroup == null) yield break;
            RectTransform rect = _targetPickerPanel.GetComponent<RectTransform>();
            Vector2 target = rect.anchoredPosition;
            Vector2 start = target + new Vector2(160f, 0f);
            _targetPickerCanvasGroup.alpha = 0f;
            _targetPickerCanvasGroup.interactable = false;
            _targetPickerCanvasGroup.blocksRaycasts = false;
            rect.anchoredPosition = start;
            float elapsed = 0f;
            const float duration = 0.28f;
            try
            {
                while (elapsed < duration)
                {
                    elapsed += Time.unscaledDeltaTime;
                    float t = Mathf.Clamp01(elapsed / duration);
                    float eased = 1f - Mathf.Pow(1f - t, 3f);
                    rect.anchoredPosition = Vector2.LerpUnclamped(start, target, eased);
                    _targetPickerCanvasGroup.alpha = t;
                    yield return null;
                }
            }
            finally
            {
                if (rect != null) rect.anchoredPosition = target;
                if (_targetPickerCanvasGroup != null)
                {
                    _targetPickerCanvasGroup.alpha = 1f;
                    _targetPickerCanvasGroup.interactable = true;
                    _targetPickerCanvasGroup.blocksRaycasts = true;
                }
                _targetPickerEntranceCoroutine = null;
            }
        }

        private void StopTargetPickerEntrance()
        {
            if (_targetPickerEntranceCoroutine == null) return;
            StopCoroutine(_targetPickerEntranceCoroutine);
            _targetPickerEntranceCoroutine = null;
        }

        private void SetFlyingTilesVisible(bool visible)
        {
            for (int i = _activeFlyingTiles.Count - 1; i >= 0; i--)
            {
                var t = _activeFlyingTiles[i];
                if (t != null)
                {
                    t.gameObject.SetActive(visible);
                }
                else
                {
                    _activeFlyingTiles.RemoveAt(i);
                }
            }
            if (_stageRoot != null)
            {
                for (int i = 0; i < _stageRoot.childCount; i++)
                {
                    Transform child = _stageRoot.GetChild(i);
                    if (child != null && child.GetComponent<Tile2D5View>() != null)
                    {
                        child.gameObject.SetActive(visible);
                    }
                }
            }
        }

        private void RestoreMenuButtonsState()
        {
            if (_menuButtons.Count == 0 && _menuPage != null)
            {
                BindMenuButtons();
            }

            for (int i = 0; i < _menuButtons.Count; i++)
            {
                var b = _menuButtons[i];
                if (b != null)
                {
                    if (_menuButtonHomePositions.TryGetValue(b, out var homePos))
                    {
                        b.transform.localPosition = homePos;
                    }
                    b.transform.localScale = Vector3.one;
                    if (_menuButtonCanvasGroups.TryGetValue(b, out var group) && group != null) group.alpha = 1f;
                    b.interactable = true;
                }
            }
        }

        private void ClearFlyingTiles()
        {
            for (int i = _activeFlyingTiles.Count - 1; i >= 0; i--)
            {
                var t = _activeFlyingTiles[i];
                if (t != null)
                {
                    DestroyImmediate(t.gameObject);
                }
            }
            _activeFlyingTiles.Clear();

            if (_stageRoot != null)
            {
                for (int i = _stageRoot.childCount - 1; i >= 0; i--)
                {
                    Transform child = _stageRoot.GetChild(i);
                    if (child != null && child.GetComponent<Tile2D5View>() != null)
                    {
                        DestroyImmediate(child.gameObject);
                    }
                }
            }
        }

        public void TogglePage()
        {
            if (_activePage == "menu") ShowTable();
            else ShowMenu();
        }

        public void ShowGalleryModal()
        {
            BindModalButtons();
            if (_galleryModal != null)
            {
                _galleryModal.SetActive(true);
                SetStatus("正在查看 136 张麻将牌面图鉴与 CC BY-SA 4.0 授权署名");
            }
        }

        public void CloseGalleryModal()
        {
            if (_galleryModal != null) _galleryModal.SetActive(false);
        }

        public void ShowNotice(string title, string message)
        {
            BindModalButtons();
            if (_noticeModal != null)
            {
                Text[] texts = _noticeModal.GetComponentsInChildren<Text>(true);
                Text? titleText = texts.FirstOrDefault(t => t.name.Contains("Title"));
                Text? msgText = texts.FirstOrDefault(t => t.name.Contains("Message"));
                if (titleText != null) titleText.text = title;
                if (msgText != null) msgText.text = message;
                _noticeModal.SetActive(true);
            }
        }

        public void CloseNotice()
        {
            if (_noticeModal != null) _noticeModal.SetActive(false);
        }

        private void StartMenuEntranceAnimation()
        {
            if (!Application.isPlaying) return;
            foreach (Coroutine coroutine in _menuEntranceCoroutines) StopCoroutine(coroutine);
            _menuEntranceCoroutines.Clear();
            for (int i = 0; i < _menuButtons.Count; i++)
            {
                Button button = _menuButtons[i];
                if (button == null) continue;
                Vector3 target = _menuButtonHomePositions.TryGetValue(button, out var pos) ? pos : button.transform.localPosition;
                if (!_menuButtonCanvasGroups.TryGetValue(button, out var group) || group == null)
                {
                    group = button.GetComponent<CanvasGroup>();
                    if (group == null) group = button.gameObject.AddComponent<CanvasGroup>();
                    _menuButtonCanvasGroups[button] = group;
                }
                button.transform.localPosition = target + new Vector3(280f, 0f, 0f);
                button.transform.localScale = Vector3.one * 0.88f;
                group.alpha = 0f;
                button.interactable = false;
                _menuEntranceCoroutines.Add(StartCoroutine(EnterButtonCoroutine(button, group, target, i * 0.12f)));
            }
        }

        private IEnumerator EnterButtonCoroutine(Button button, CanvasGroup group, Vector3 target, float delay)
        {
            try
            {
                yield return new WaitForSecondsRealtime(delay);
                if (button == null) yield break;
                Vector3 start = target + new Vector3(280f, 0f, 0f);
                Vector3 startScale = Vector3.one * 0.88f;
                float elapsed = 0f;
                float duration = 0.62f;
                while (elapsed < duration)
                {
                    elapsed += Time.unscaledDeltaTime;
                    float t = Mathf.Clamp01(elapsed / duration);
                    float eased = 1f - Mathf.Pow(1f - t, 4f);
                    button.transform.localPosition = Vector3.LerpUnclamped(start, target, eased);
                    button.transform.localScale = Vector3.LerpUnclamped(startScale, Vector3.one, eased);
                    group.alpha = t;
                    yield return null;
                }
            }
            finally
            {
                if (button != null)
                {
                    button.transform.localPosition = target;
                    button.transform.localScale = Vector3.one;
                    button.interactable = true;
                }
                if (group != null) group.alpha = 1f;
            }
        }

        private void ApplyScreenLayout(bool force)
        {
            if (_canvasRoot == null || _stageRoot == null || Screen.width <= 0 || Screen.height <= 0) return;
            if (!force && _lastScreenWidth == Screen.width && _lastScreenHeight == Screen.height) return;
            _lastScreenWidth = Screen.width;
            _lastScreenHeight = Screen.height;

            var canvas = _canvasRoot.GetComponentInParent<Canvas>();
            var scaler = canvas != null ? canvas.GetComponent<CanvasScaler>() : null;
            if (scaler == null) return;
            scaler.matchWidthOrHeight = Screen.width / 1280f <= Screen.height / 720f ? 0f : 1f;
            Canvas.ForceUpdateCanvases();

            float actualScale = Mathf.Max(0.0001f, canvas != null ? canvas.scaleFactor : 1f);
            float logicalWidth = canvas != null ? canvas.pixelRect.width / actualScale : 1280f;
            float logicalHeight = canvas != null ? canvas.pixelRect.height / actualScale : 720f;
            float offsetX = Mathf.Max(0f, (logicalWidth - 1280f) * 0.5f);
            float offsetY = Mathf.Max(0f, (logicalHeight - 720f) * 0.5f);
            _stageRoot.anchoredPosition = new Vector2(offsetX, -offsetY);

            UpdateBackgroundCrop(_menuBackgroundImage, _mainBackground);
            UpdateBackgroundCrop(_tableBackgroundImage, _environment);
        }

        private void UpdateBackgroundCrop(RawImage? image, Sprite? sprite)
        {
            if (image == null || sprite == null || sprite.rect.height <= 0f) return;
            float imageAspect = sprite.rect.width / sprite.rect.height;
            float screenAspect = Screen.width / (float)Screen.height;
            Rect uv = new Rect(sprite.rect.x / sprite.texture.width, sprite.rect.y / sprite.texture.height,
                sprite.rect.width / sprite.texture.width, sprite.rect.height / sprite.texture.height);
            if (screenAspect > imageAspect)
            {
                float visibleHeight = imageAspect / screenAspect;
                uv.y += uv.height * (1f - visibleHeight) * 0.5f;
                uv.height *= visibleHeight;
            }
            else
            {
                float visibleWidth = screenAspect / imageAspect;
                uv.x += uv.width * (1f - visibleWidth) * 0.5f;
                uv.width *= visibleWidth;
            }
            image.uvRect = uv;
        }

        #endregion

        #region Table Initialization & State

        public void ResetTableState()
        {
            ResetTableState(false);
        }

        public void ResetTableState(bool preserveAutoLoop)
        {
            StopAllActiveAnimations();
            _isAnimating = false;
            _selectedHandIndex = -1;
            _discardSequence = 0;
            _opponentDiscardSequence = 0;
            _meldDemoSequence = 0;
            _remainingWallTiles = 69;
            _drawDeckIndex = 0;
            if (!preserveAutoLoop)
            {
                _isAutoLooping = false;
                if (_autoButtonText != null) _autoButtonText.text = "▶ 自动演示";
            }
            if (_wallCountText != null) _wallCountText.text = "東1局\n本0 供0\n牌山 " + _remainingWallTiles;
            if (_riichiStickImage != null) _riichiStickImage.gameObject.SetActive(false);

            RebuildHandTiles();
            RebuildRiverP0();
            RebuildOpponentRivers();
            RebuildMelds();
            RebuildWall();

            SetStatus("牌桌已重置：手牌 13 张 + 摸牌位就绪，牌墙与副露已展示");
        }

        private void StopAllActiveAnimations()
        {
            StopTargetPickerEntrance();
            StopAllCoroutines();
            _activeCoroutines.Clear();
            _autoLoopCoroutine = null;
            _isAnimating = false;
            ClearFlyingTiles();
            RestoreMenuButtonsState();
        }

        private void RebuildHandTiles()
        {
            if (_handTrayRoot == null) return;
            for (int i = _handTrayRoot.childCount - 1; i >= 0; i--)
            {
                DestroyImmediate(_handTrayRoot.GetChild(i).gameObject);
            }
            _handTiles.Clear();
            _drawnTile = null;

            for (int i = 0; i < InitialHandTiles.Length; i++)
            {
                int tileIdx = InitialHandTiles[i];
                var tile = CreateTileInstance(
                    "HandTile_" + i,
                    tileIdx,
                    Tile2D5Mode.Standing,
                    new Vector2(16 + i * 54, 18),
                    new Vector2(52, 74),
                    _handTrayRoot,
                    true,
                    true
                );
                tile.ClearClickListeners();
                tile.Clicked += OnTileClicked;
                _handTiles.Add(tile);
            }

            int drawnTileIdx = SampleDrawDeck[_drawDeckIndex % SampleDrawDeck.Length];
            _drawnTile = CreateTileInstance(
                "HandTile_Drawn",
                drawnTileIdx,
                Tile2D5Mode.Standing,
                new Vector2(16 + 13 * 54 + 18, 18),
                new Vector2(52, 74),
                _handTrayRoot,
                true,
                true
            );
            _drawnTile.ClearClickListeners();
            _drawnTile.Clicked += OnTileClicked;
        }

        private void RebuildRiverP0()
        {
            if (_riverRootP0 == null) return;
            for (int i = _riverRootP0.childCount - 1; i >= 0; i--)
            {
                DestroyImmediate(_riverRootP0.GetChild(i).gameObject);
            }
            _riverTilesP0.Clear();

            int[] riverSamples = { 27, 28, 9, 18 };
            for (int i = 0; i < riverSamples.Length; i++)
            {
                AddTileToRiverP0(riverSamples[i], false);
            }
        }

        private void RebuildOpponentRivers()
        {
            RectTransform?[] roots = { _riverRootP1, _riverRootP2, _riverRootP3 };
            for (int i = 0; i < roots.Length; i++)
            {
                if (roots[i] != null)
                {
                    for (int child = roots[i]!.childCount - 1; child >= 0; child--)
                    {
                        DestroyImmediate(roots[i]!.GetChild(child).gameObject);
                    }
                }
                _opponentRiverTiles[i].Clear();
            }
        }

        private void AddTileToRiverP0(int tileIdx, bool isRotatedRiichi)
        {
            if (_riverRootP0 == null) return;
            int count = _riverTilesP0.Count;
            int col = count % 6;
            int row = count / 6;

            float x = col * 36f;
            float y = row * 46f;

            var tile = CreateTileInstance(
                "RiverTile_P0_" + count,
                tileIdx,
                Tile2D5Mode.FlatFront,
                new Vector2(x, y),
                new Vector2(34, 44),
                _riverRootP0,
                true,
                false,
                isRotatedRiichi
            );
            _riverTilesP0.Add(tile);
        }

        private void RebuildMelds()
        {
            if (_meldsRoot == null) return;
            for (int i = _meldsRoot.childCount - 1; i >= 0; i--)
            {
                DestroyImmediate(_meldsRoot.GetChild(i).gameObject);
            }
            _meldViews.Clear();

            // Meld 1: Chi (4-5-6 Sou, left tile rotated)
            CreateTileInstance("Chi_1", 21, Tile2D5Mode.FlatFront, new Vector2(0, 0), new Vector2(34, 44), _meldsRoot, false, false, true);
            CreateTileInstance("Chi_2", 22, Tile2D5Mode.FlatFront, new Vector2(44, 0), new Vector2(34, 44), _meldsRoot, false, false);
            CreateTileInstance("Chi_3", 23, Tile2D5Mode.FlatFront, new Vector2(80, 0), new Vector2(34, 44), _meldsRoot, false, false);

            // Meld 2: Pon (Dragon 2 / 發, middle tile rotated)
            CreateTileInstance("Pon_1", 32, Tile2D5Mode.FlatFront, new Vector2(128, 0), new Vector2(34, 44), _meldsRoot, false, false);
            CreateTileInstance("Pon_2", 32, Tile2D5Mode.FlatFront, new Vector2(164, 0), new Vector2(34, 44), _meldsRoot, false, false, true);
            CreateTileInstance("Pon_3", 32, Tile2D5Mode.FlatFront, new Vector2(208, 0), new Vector2(34, 44), _meldsRoot, false, false);

            // Meld 3: Concealed Kan (1 Pin, outer 2 face down backs, middle 2 open!)
            CreateTileInstance("Kan_1", 9, Tile2D5Mode.FlatBack, new Vector2(256, 0), new Vector2(34, 44), _meldsRoot, false, false);
            CreateTileInstance("Kan_2", 9, Tile2D5Mode.FlatFront, new Vector2(292, 0), new Vector2(34, 44), _meldsRoot, false, false);
            CreateTileInstance("Kan_3", 9, Tile2D5Mode.FlatFront, new Vector2(328, 0), new Vector2(34, 44), _meldsRoot, false, false);
            CreateTileInstance("Kan_4", 9, Tile2D5Mode.FlatBack, new Vector2(364, 0), new Vector2(34, 44), _meldsRoot, false, false);
        }

        private void RebuildWall()
        {
            if (_wallRoot == null) return;
            for (int i = _wallRoot.childCount - 1; i >= 0; i--)
            {
                DestroyImmediate(_wallRoot.GetChild(i).gameObject);
            }
            _wallStackViews.Clear();

            for (int stack = 0; stack < 10; stack++)
            {
                float stackX = stack * 34f;

                // Bottom tile
                CreateTileInstance(
                    "WallStack_B_" + stack,
                    0,
                    Tile2D5Mode.FlatBack,
                    new Vector2(stackX, 0),
                    new Vector2(32, 42),
                    _wallRoot,
                    false,
                    false
                );

                // Top tile: slightly offset up by 6px to show 2-layer thickness!
                bool isDoraIndicator = (stack == 2);
                var topTile = CreateTileInstance(
                    "WallStack_T_" + stack,
                    isDoraIndicator ? 34 : 0, // Red 5 Man as Dora Indicator
                    isDoraIndicator ? Tile2D5Mode.FlatFront : Tile2D5Mode.FlatBack,
                    new Vector2(stackX + 2, -6),
                    new Vector2(32, 42),
                    _wallRoot,
                    false,
                    false
                );
                _wallStackViews.Add(topTile);
            }
        }

        private Tile2D5View CreateTileInstance(
            string name,
            int tileIndex,
            Tile2D5Mode mode,
            Vector2 anchoredPos,
            Vector2 size,
            Transform parent,
            bool hoverable,
            bool selectable,
            bool isRotated90 = false)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(anchoredPos.x, -anchoredPos.y);
            rt.sizeDelta = size;

            Sprite? faceSprite = GetTileFaceSprite(tileIndex);
            string tileName = GetTileDisplayName(tileIndex);

            var view = go.AddComponent<Tile2D5View>();
            view.Setup(
                tileIndex,
                tileName,
                faceSprite,
                mode,
                _tileStandingChassis,
                _tileFlatFrontChassis,
                _tileFlatBackChassis,
                _tileSoftShadow,
                _tileGlow,
                isRotated90
            );
            view.SetInteractive(hoverable, selectable);
            return view;
        }

        #endregion

        #region Interactive Hand & Animation Controls

        private void OnTileClicked(Tile2D5View clickedTile)
        {
            if (_isAnimating || clickedTile == null) return;

            int currentIndex = -1;
            if (clickedTile == _drawnTile)
            {
                currentIndex = 13;
            }
            else
            {
                currentIndex = _handTiles.IndexOf(clickedTile);
            }
            if (currentIndex < 0) return;

            OnHandTileClicked(currentIndex, clickedTile);
        }

        private void OnHandTileClicked(int index, Tile2D5View clickedTile)
        {
            if (_isAnimating || clickedTile == null) return;

            if (_selectedHandIndex == index)
            {
                _selectedHandIndex = -1;
                clickedTile.SetSelected(false, true);
                SetStatus("取消选牌");
            }
            else
            {
                if (_selectedHandIndex == 13 && _drawnTile != null)
                {
                    _drawnTile.SetSelected(false, true);
                }
                else if (_selectedHandIndex >= 0 && _selectedHandIndex < _handTiles.Count && _handTiles[_selectedHandIndex] != null)
                {
                    _handTiles[_selectedHandIndex].SetSelected(false, true);
                }

                _selectedHandIndex = index;
                clickedTile.SetSelected(true, true);
                SetStatus("已选中 [" + clickedTile.TileName + "]：可点击【打牌入河】或按键 3 打出");
            }
        }

        public void TriggerDraw()
        {
            if (_isAnimating) return;
            if (_drawnTile != null)
            {
                // Clear existing drawn tile so user/runner can reliably replay draw animation
                DestroyImmediate(_drawnTile.gameObject);
                _drawnTile = null;
            }
            StartControlledCoroutine(AnimateDrawRoutine());
        }

        public void TriggerHover()
        {
            if (_isAnimating) return;
            StartControlledCoroutine(AnimateHoverWaveRoutine());
        }

        public void TriggerDiscard()
        {
            if (_isAnimating) return;
            if (_handTiles.Count == 0 && _drawnTile == null)
            {
                SetStatus("手牌已空，无法继续打牌");
                return;
            }
            StartControlledCoroutine(AnimateDiscardRoutine(false));
        }

        public void TriggerRiichi()
        {
            if (_isAnimating) return;
            if (_handTiles.Count == 0 && _drawnTile == null)
            {
                SetStatus("手牌已空，无法立直出牌");
                return;
            }
            StartControlledCoroutine(AnimateDiscardRoutine(true));
        }

        public void TriggerOpponentDiscard()
        {
            if (_isAnimating) return;
            StartControlledCoroutine(AnimateOpponentDiscardRoutine());
        }

        public void TriggerChi()
        {
            TriggerMeldCall(DemoMeldKind.Chi);
        }

        public void TriggerPon()
        {
            TriggerMeldCall(DemoMeldKind.Pon);
        }

        public void TriggerKan()
        {
            TriggerMeldCall(DemoMeldKind.Kan);
        }

        private void TriggerMeldCall(DemoMeldKind kind)
        {
            if (_isAnimating) return;
            StartControlledCoroutine(AnimateMeldCallRoutine(kind));
        }

        public void ToggleAutoLoop()
        {
            _isAutoLooping = !_isAutoLooping;
            if (_autoButtonText != null)
            {
                _autoButtonText.text = _isAutoLooping ? "⏹ 停止自动" : "▶ 自动演示";
            }
            if (_isAutoLooping)
            {
                if (!_isAnimating && _autoLoopCoroutine == null)
                {
                    _autoLoopCoroutine = StartControlledCoroutine(AutoLoopRoutine());
                }
            }
            SetStatus(_isAutoLooping ? "自动演示已启动：将循环演示【摸牌 -> 选牌悬停 -> 打牌入河 -> 理牌】" : "自动演示已停止");
        }

        public void TogglePause()
        {
            _isPaused = !_isPaused;
            if (_pauseResumeButtonText != null)
            {
                _pauseResumeButtonText.text = _isPaused ? "▶ 继续" : "⏸ 暂停";
            }
            SetStatus(_isPaused ? "演示已暂停：按空格或点击【继续】恢复" : "演示已继续");
        }

        public void Replay()
        {
            ResetTableState();
            SetStatus("已重播初始状态");
        }

        public void CycleSpeed()
        {
            if (_animationSpeedMultiplier >= 1.9f) _animationSpeedMultiplier = 0.5f;
            else if (_animationSpeedMultiplier <= 0.6f) _animationSpeedMultiplier = 1.0f;
            else _animationSpeedMultiplier = 2.0f;

            if (_speedButtonText != null)
            {
                _speedButtonText.text = _animationSpeedMultiplier.ToString("0.0") + "x";
            }
            SetStatus("动画速度切换为：" + _animationSpeedMultiplier.ToString("0.0") + "x");
        }

        private Coroutine StartControlledCoroutine(IEnumerator routine)
        {
            var holder = new CoroutineHolder();
            Coroutine c = StartCoroutine(WrapControlledRoutine(routine, holder));
            holder.Coroutine = c;
            if (!holder.IsCompleted)
            {
                _activeCoroutines.Add(c);
            }
            return c;
        }

        private IEnumerator WrapControlledRoutine(IEnumerator routine, CoroutineHolder holder)
        {
            try
            {
                while (true)
                {
                    while (_isPaused) yield return null;
                    bool hasNext = false;
                    try
                    {
                        hasNext = routine.MoveNext();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex);
                        _isAnimating = false;
                        yield break;
                    }
                    if (!hasNext) break;
                    yield return routine.Current;
                }
            }
            finally
            {
                holder.IsCompleted = true;
                if (holder.Coroutine != null)
                {
                    _activeCoroutines.Remove(holder.Coroutine);
                }
                CheckAutoLoopResume();
            }
        }

        private void CheckAutoLoopResume()
        {
            if (_isAutoLooping && !_isAnimating && _autoLoopCoroutine == null)
            {
                _autoLoopCoroutine = StartControlledCoroutine(AutoLoopRoutine());
            }
        }

        private IEnumerator WaitWithSpeed(float baseSeconds)
        {
            float duration = baseSeconds / Mathf.Max(0.1f, _animationSpeedMultiplier);
            float elapsed = 0f;
            while (elapsed < duration)
            {
                while (_isPaused) yield return null;
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }
        }

        #endregion

        #region Animation Routines

        private IEnumerator AnimateDrawRoutine()
        {
            _isAnimating = true;
            SetStatus("【摸牌演示】：从牌山抽取 1 张牌加入手牌摸牌位...");

            _drawDeckIndex++;
            int drawnIndex = SampleDrawDeck[_drawDeckIndex % SampleDrawDeck.Length];
            string drawnName = GetTileDisplayName(drawnIndex);
            Sprite? faceSprite = GetTileFaceSprite(drawnIndex);

            if (_remainingWallTiles > 0)
            {
                _remainingWallTiles--;
                if (_wallCountText != null) _wallCountText.text = "東1局\n本0 供0\n牌山 " + _remainingWallTiles;
            }

            Vector3 wallStartWorld;
            if (_wallRoot != null)
            {
                wallStartWorld = _wallRoot.TransformPoint(new Vector3(9 * 34f, 0f, 0f));
            }
            else
            {
                wallStartWorld = transform.position + new Vector3(0f, 150f, 0f);
            }

            Vector3 handTargetWorld;
            if (_handTrayRoot != null)
            {
                handTargetWorld = _handTrayRoot.TransformPoint(new Vector3(16 + 13 * 54 + 18, -18, 0f));
            }
            else
            {
                handTargetWorld = transform.position + new Vector3(300f, -200f, 0f);
            }

            Transform animParent = _stageRoot != null ? _stageRoot : transform;
            var drawingTile = CreateTileInstance(
                "HandTile_Drawn",
                drawnIndex,
                Tile2D5Mode.FlatFront,
                Vector2.zero,
                new Vector2(40, 56),
                animParent,
                false,
                false
            );
            drawingTile.transform.position = wallStartWorld;
            drawingTile.transform.SetAsLastSibling();
            _activeFlyingTiles.Add(drawingTile);
            if (_activePage == "menu") drawingTile.gameObject.SetActive(false);

            float duration = 0.45f;
            float elapsed = 0f;
            RectTransform rt = drawingTile.GetComponent<RectTransform>();

            try
            {
                while (elapsed < duration)
                {
                    while (_isPaused) yield return null;
                    elapsed += Time.unscaledDeltaTime * _animationSpeedMultiplier;
                    float t = Mathf.Clamp01(elapsed / duration);

                    float eased = 1f - Mathf.Pow(1f - t, 2.5f);
                    Vector3 curWorld = Vector3.Lerp(wallStartWorld, handTargetWorld, eased);
                    float arc = Mathf.Sin(t * Mathf.PI) * 45f;

                    drawingTile.transform.position = curWorld + new Vector3(0f, arc, 0f);
                    rt.sizeDelta = Vector2.Lerp(new Vector2(40, 56), new Vector2(52, 74), t);

                    if (t > 0.6f && drawingTile.Mode != Tile2D5Mode.Standing)
                    {
                        drawingTile.SetMode(Tile2D5Mode.Standing, faceSprite);
                    }

                    yield return null;
                }

                if (_handTrayRoot != null)
                {
                    drawingTile.transform.SetParent(_handTrayRoot, false);
                    rt.anchorMin = new Vector2(0, 1);
                    rt.anchorMax = new Vector2(0, 1);
                    rt.pivot = new Vector2(0, 1);
                    rt.anchoredPosition = new Vector2(16 + 13 * 54 + 18, -18);
                }
                rt.sizeDelta = new Vector2(52, 74);
                drawingTile.SetMode(Tile2D5Mode.Standing, faceSprite);
                drawingTile.gameObject.SetActive(true);
                drawingTile.SetInteractive(true, true);
                drawingTile.ClearClickListeners();
                drawingTile.Clicked += OnTileClicked;
                _drawnTile = drawingTile;

                yield return WaitWithSpeed(0.08f);
                SetStatus("【摸牌完成】：摸入 [" + drawnName + "]，手牌共有 14 张，有 18px 独立摸牌间隙");
            }
            finally
            {
                _activeFlyingTiles.Remove(drawingTile);
                _isAnimating = false;
            }
        }

        private IEnumerator AnimateHoverWaveRoutine()
        {
            _isAnimating = true;
            SetStatus("【选牌/悬停演示】：手牌逐张升起 18px 2.5D 高度，投影扩展并点亮倒角高光...");

            try
            {
                for (int i = 0; i < _handTiles.Count; i++)
                {
                    if (!_isAutoLooping && _autoLoopCoroutine != null)
                    {
                        if (_handTiles[i] != null) _handTiles[i].SetHovered(false, false);
                        break;
                    }
                    if (_handTiles[i] != null) _handTiles[i].SetHovered(true, true);
                    yield return WaitWithSpeed(0.04f);
                    if (_handTiles[i] != null) _handTiles[i].SetHovered(false, true);
                }

                if (_isAutoLooping || _autoLoopCoroutine == null)
                {
                    if (_drawnTile != null)
                    {
                        _drawnTile.SetHovered(true, true);
                        yield return WaitWithSpeed(0.06f);
                        if (_drawnTile != null) _drawnTile.SetHovered(false, true);
                    }
                }

                SetStatus("【悬停演示完成】：光标移上任意手牌可实时体验 2.5D 上浮与阴影缩放");
            }
            finally
            {
                _isAnimating = false;
            }
        }

        private IEnumerator AnimateDiscardRoutine(bool isRiichi)
        {
            _isAnimating = true;

            Tile2D5View? tileToDiscard = null;
            int discardIndex = _selectedHandIndex;
            if (discardIndex == 13 && _drawnTile != null)
            {
                tileToDiscard = _drawnTile;
            }
            else if (discardIndex >= 0 && discardIndex < _handTiles.Count)
            {
                tileToDiscard = _handTiles[discardIndex];
            }
            else if (_drawnTile != null)
            {
                tileToDiscard = _drawnTile;
                discardIndex = 13;
            }
            else if (_handTiles.Count > 0)
            {
                discardIndex = _handTiles.Count - 1;
                tileToDiscard = _handTiles[discardIndex];
            }
            else
            {
                _isAnimating = false;
                yield break;
            }

            if (tileToDiscard == null)
            {
                _isAnimating = false;
                yield break;
            }

            string discardName = tileToDiscard.TileName;
            int tileIndex = tileToDiscard.TileIndex;
            SetStatus("【打牌入河演示】：打出 [" + discardName + "]" + (isRiichi ? "（宣告立直，横置牌面）" : "") + "...");

            tileToDiscard.SetSelected(false, false);
            Vector3 startWorldPos = tileToDiscard.transform.position;

            int riverCount = _riverTilesP0.Count;
            int col = riverCount % 6;
            int row = riverCount / 6;

            Vector3 targetWorldPos;
            if (_riverRootP0 != null)
            {
                targetWorldPos = _riverRootP0.TransformPoint(new Vector3(col * 36f, -row * 46f, 0f));
            }
            else
            {
                targetWorldPos = transform.position;
            }

            Transform animParent = _stageRoot != null ? _stageRoot : transform;
            tileToDiscard.transform.SetParent(animParent, true);
            tileToDiscard.transform.SetAsLastSibling();
            _activeFlyingTiles.Add(tileToDiscard);
            if (_activePage == "menu") tileToDiscard.gameObject.SetActive(false);

            RectTransform discardRt = tileToDiscard.GetComponent<RectTransform>();

            float duration = 0.40f;
            float elapsed = 0f;

            try
            {
                while (elapsed < duration)
                {
                    while (_isPaused) yield return null;
                    elapsed += Time.unscaledDeltaTime * _animationSpeedMultiplier;
                    float t = Mathf.Clamp01(elapsed / duration);
                    float eased = 1f - Mathf.Pow(1f - t, 2.5f);

                    Vector3 curWorld = Vector3.Lerp(startWorldPos, targetWorldPos, eased);
                    float arc = Mathf.Sin(t * Mathf.PI) * 40f;
                    tileToDiscard.transform.position = curWorld + new Vector3(0f, arc, 0f);

                    discardRt.sizeDelta = Vector2.Lerp(new Vector2(52, 74), new Vector2(34, 44), t);

                    if (t > 0.4f && tileToDiscard.Mode != Tile2D5Mode.FlatFront)
                    {
                        Sprite? face = GetTileFaceSprite(tileIndex);
                        tileToDiscard.SetMode(Tile2D5Mode.FlatFront, face, isRiichi);
                    }

                    yield return null;
                }

                tileToDiscard.transform.position = targetWorldPos;

                if (discardIndex == 13)
                {
                    _drawnTile = null;
                }
                else if (discardIndex >= 0 && discardIndex < _handTiles.Count)
                {
                    _handTiles.RemoveAt(discardIndex);
                }
                Destroy(tileToDiscard.gameObject);

                AddTileToRiverP0(tileIndex, isRiichi);

                if (isRiichi && _riichiStickImage != null)
                {
                    _riichiStickImage.gameObject.SetActive(true);
                    RectTransform stickRt = _riichiStickImage.GetComponent<RectTransform>();
                    stickRt.localScale = new Vector3(1.4f, 1.4f, 1f);
                    yield return WaitWithSpeed(0.08f);
                    stickRt.localScale = Vector3.one;
                }

                if (discardIndex != 13 && _drawnTile != null)
                {
                    _handTiles.Add(_drawnTile);
                    _drawnTile = null;
                }

                yield return RealignHandTilesRoutine();

                _selectedHandIndex = -1;
                SetStatus("【打牌完成】：[" + discardName + "] 已入河，手牌理牌对齐完成" + (isRiichi ? "，立直棒已就位" : ""));
            }
            finally
            {
                _activeFlyingTiles.Remove(tileToDiscard);
                _isAnimating = false;
            }
        }

        private IEnumerator AnimateOpponentDiscardRoutine()
        {
            _isAnimating = true;
            int seat = (_opponentDiscardSequence++ % 3) + 1;
            RectTransform? river = GetOpponentRiverRoot(seat);
            List<Tile2D5View>? riverTiles = GetOpponentRiverTiles(seat);
            if (river == null || riverTiles == null)
            {
                _isAnimating = false;
                yield break;
            }

            int tileIndex = new[] { 4, 13, 22, 28, 32, 18 }[(_opponentDiscardSequence - 1) % 6];
            bool sideSeat = seat == 1 || seat == 3;
            int count = riverTiles.Count;
            int col = count % (sideSeat ? 3 : 6);
            int row = count / (sideSeat ? 3 : 6);
            Vector2 target = sideSeat ? new Vector2(col * 44f, -row * 34f) : new Vector2(col * 36f, -row * 46f);
            Vector2 targetSize = sideSeat ? new Vector2(34f, 44f) : new Vector2(34f, 44f);
            Sprite? face = GetTileFaceSprite(tileIndex);
            SetStatus("對手 P" + seat + " 打出「" + GetTileDisplayName(tileIndex) + "」：牌從座位飞入牌河");

            Transform animationParent = _stageRoot != null ? _stageRoot : transform;
            Tile2D5View flyingTile = CreateTileInstance(
                "OpponentDiscard_P" + seat,
                tileIndex,
                Tile2D5Mode.Standing,
                Vector2.zero,
                new Vector2(54f, 76f),
                animationParent,
                false,
                false);
            flyingTile.transform.position = GetOpponentSeatWorld(seat);
            flyingTile.transform.SetAsLastSibling();
            _activeFlyingTiles.Add(flyingTile);

            Vector3 start = flyingTile.transform.position;
            Vector3 end = river.TransformPoint(new Vector3(target.x, target.y, 0f));
            RectTransform tileRect = flyingTile.GetComponent<RectTransform>();
            float elapsed = 0f;
            const float duration = 0.72f;
            try
            {
                while (elapsed < duration)
                {
                    while (_isPaused) yield return null;
                    elapsed += Time.unscaledDeltaTime * _animationSpeedMultiplier;
                    float t = Mathf.Clamp01(elapsed / duration);
                    float eased = 1f - Mathf.Pow(1f - t, 3f);
                    flyingTile.transform.position = Vector3.Lerp(start, end, eased) + Vector3.up * (Mathf.Sin(t * Mathf.PI) * 82f);
                    tileRect.sizeDelta = Vector2.Lerp(new Vector2(54f, 76f), targetSize, eased);
                    tileRect.localRotation = Quaternion.Euler(0f, 0f, Mathf.Lerp(0f, sideSeat ? 90f : 0f, eased));
                    if (t > 0.55f && flyingTile.Mode != Tile2D5Mode.FlatFront)
                    {
                        flyingTile.SetMode(Tile2D5Mode.FlatFront, face, sideSeat);
                    }
                    yield return null;
                }

                flyingTile.transform.SetParent(river, false);
                tileRect.anchorMin = new Vector2(0f, 1f);
                tileRect.anchorMax = new Vector2(0f, 1f);
                tileRect.pivot = new Vector2(0f, 1f);
                tileRect.anchoredPosition = target;
                tileRect.sizeDelta = targetSize;
                flyingTile.SetMode(Tile2D5Mode.FlatFront, face, sideSeat);
                riverTiles.Add(flyingTile);
                _activeFlyingTiles.Remove(flyingTile);
                SetStatus("對手 P" + seat + " 的舍牌已落入牌河");
            }
            finally
            {
                _activeFlyingTiles.Remove(flyingTile);
                _isAnimating = false;
            }
        }

        private IEnumerator AnimateMeldCallRoutine(DemoMeldKind kind)
        {
            _isAnimating = true;
            if (_meldsRoot == null)
            {
                _isAnimating = false;
                yield break;
            }

            int[] tileIndexes = kind == DemoMeldKind.Chi
                ? new[] { 20, 21, 22 }
                : kind == DemoMeldKind.Pon ? new[] { 32, 32, 32 } : new[] { 9, 9, 9, 9 };
            string action = kind == DemoMeldKind.Chi ? "吃" : kind == DemoMeldKind.Pon ? "碰" : "杠";
            SetStatus("演示「" + action + "」：對手舍牌與手牌飛入副露區（僅美術演示）");

            for (int i = _meldsRoot.childCount - 1; i >= 0; i--)
            {
                DestroyImmediate(_meldsRoot.GetChild(i).gameObject);
            }
            _meldViews.Clear();
            Image panel = ImageRect("Call effect panel", _panel, new Rect(0f, 0f, 420f, 58f), _meldsRoot, false, true);
            panel.color = new Color(1f, 0.91f, 0.66f, 0.96f);
            Text label = Label(action + "！", new Rect(4f, 5f, 76f, 46f), 24f, new Color(1f, 0.76f, 0.26f), TextAlignmentOptions.Center, _meldsRoot);
            label.fontStyle = FontStyle.Bold;

            Transform animationParent = _stageRoot != null ? _stageRoot : transform;
            int claimedIndex = kind == DemoMeldKind.Chi ? 2 : 1;
            RectTransform? claimSource = GetOpponentRiverRoot(1);
            Vector3 claimStart = claimSource != null ? claimSource.TransformPoint(new Vector3(48f, -72f, 0f)) : _meldsRoot.position;

            try
            {
                for (int i = 0; i < tileIndexes.Length; i++)
                {
                    int tileIndex = tileIndexes[i];
                    Tile2D5View tile = CreateTileInstance(
                        action + "DemoTile_" + i,
                        tileIndex,
                        Tile2D5Mode.Standing,
                        Vector2.zero,
                        new Vector2(52f, 74f),
                        animationParent,
                        false,
                        false);
                    Vector3 handStart = _handTiles.Count > 0 ? _handTiles[i % _handTiles.Count].transform.position : GetOpponentSeatWorld(1);
                    tile.transform.position = i == claimedIndex ? claimStart : handStart;
                    tile.transform.SetAsLastSibling();
                    _activeFlyingTiles.Add(tile);

                    Vector2 target = new Vector2(84f + i * 42f, -6f);
                    Vector3 start = tile.transform.position;
                    Vector3 end = _meldsRoot.TransformPoint(new Vector3(target.x, target.y, 0f));
                    RectTransform tileRect = tile.GetComponent<RectTransform>();
                    float elapsed = 0f;
                    const float duration = 0.48f;
                    while (elapsed < duration)
                    {
                        while (_isPaused) yield return null;
                        elapsed += Time.unscaledDeltaTime * _animationSpeedMultiplier;
                        float t = Mathf.Clamp01(elapsed / duration);
                        float eased = 1f - Mathf.Pow(1f - t, 3f);
                        tile.transform.position = Vector3.Lerp(start, end, eased) + Vector3.up * (Mathf.Sin(t * Mathf.PI) * 48f);
                        tileRect.sizeDelta = Vector2.Lerp(new Vector2(52f, 74f), new Vector2(36f, 48f), eased);
                        if (t > 0.45f && tile.Mode != Tile2D5Mode.FlatFront)
                        {
                            tile.SetMode(Tile2D5Mode.FlatFront, GetTileFaceSprite(tileIndex), i == claimedIndex);
                        }
                        yield return null;
                    }

                    tile.transform.SetParent(_meldsRoot, false);
                    tileRect.anchorMin = new Vector2(0f, 1f);
                    tileRect.anchorMax = new Vector2(0f, 1f);
                    tileRect.pivot = new Vector2(0f, 1f);
                    tileRect.anchoredPosition = target;
                    tileRect.sizeDelta = new Vector2(36f, 48f);
                    tile.SetMode(Tile2D5Mode.FlatFront, GetTileFaceSprite(tileIndex), i == claimedIndex);
                    _meldViews.Add(tile);
                    _activeFlyingTiles.Remove(tile);
                    label.transform.localScale = Vector3.one * 1.15f;
                    yield return WaitWithSpeed(0.05f);
                    label.transform.localScale = Vector3.one;
                }
                SetStatus("「" + action + "」演示完成：橫置的舍牌與手牌已組成副露");
            }
            finally
            {
                _isAnimating = false;
            }
        }

        private RectTransform? GetOpponentRiverRoot(int seat)
        {
            return seat == 1 ? _riverRootP1 : seat == 2 ? _riverRootP2 : seat == 3 ? _riverRootP3 : null;
        }

        private List<Tile2D5View>? GetOpponentRiverTiles(int seat)
        {
            return seat >= 1 && seat <= 3 ? _opponentRiverTiles[seat - 1] : null;
        }

        private Vector3 GetOpponentSeatWorld(int seat)
        {
            Transform? portrait = _tablePage != null ? _tablePage.transform.Find("Animal portrait " + seat) : null;
            if (portrait is RectTransform rect)
            {
                return rect.TransformPoint(new Vector3(rect.rect.width * 0.5f, -rect.rect.height * 0.5f, 0f));
            }
            RectTransform? river = GetOpponentRiverRoot(seat);
            return river != null ? river.TransformPoint(new Vector3(42f, -72f, 0f)) : transform.position;
        }

        private IEnumerator RealignHandTilesRoutine()
        {
            if (_handTiles.Count == 0) yield break;

            float duration = 0.22f;
            float elapsed = 0f;
            List<Vector2> starts = _handTiles.Select(t => t != null ? t.GetComponent<RectTransform>().anchoredPosition : Vector2.zero).ToList();
            List<Vector2> targets = new List<Vector2>();

            for (int i = 0; i < _handTiles.Count; i++)
            {
                targets.Add(new Vector2(16 + i * 54, -18));
            }

            while (elapsed < duration)
            {
                while (_isPaused) yield return null;
                elapsed += Time.unscaledDeltaTime * _animationSpeedMultiplier;
                float t = Mathf.Clamp01(elapsed / duration);
                float eased = 1f - Mathf.Pow(1f - t, 2.5f);

                for (int i = 0; i < _handTiles.Count && i < starts.Count; i++)
                {
                    if (_handTiles[i] != null)
                    {
                        _handTiles[i].GetComponent<RectTransform>().anchoredPosition = Vector2.Lerp(starts[i], targets[i], eased);
                    }
                }
                yield return null;
            }

            for (int i = 0; i < _handTiles.Count && i < targets.Count; i++)
            {
                if (_handTiles[i] != null)
                {
                    _handTiles[i].GetComponent<RectTransform>().anchoredPosition = targets[i];
                }
            }
        }

        private IEnumerator AutoLoopRoutine()
        {
            try
            {
                while (_isAutoLooping)
                {
                    if (_drawnTile == null)
                    {
                        yield return AnimateDrawRoutine();
                        if (!_isAutoLooping) break;
                        yield return WaitWithSpeed(0.25f);
                    }

                    if (!_isAutoLooping) break;

                    yield return AnimateHoverWaveRoutine();
                    if (!_isAutoLooping) break;
                    yield return WaitWithSpeed(0.15f);

                    if (!_isAutoLooping) break;

                    yield return AnimateDiscardRoutine(_riverTilesP0.Count == 5);
                    if (!_isAutoLooping) break;
                    yield return WaitWithSpeed(0.35f);

                    yield return AnimateOpponentDiscardRoutine();
                    if (!_isAutoLooping) break;
                    yield return WaitWithSpeed(0.16f);

                    DemoMeldKind meldKind = (DemoMeldKind)(_meldDemoSequence++ % 3);
                    yield return AnimateMeldCallRoutine(meldKind);
                    if (!_isAutoLooping) break;
                    yield return WaitWithSpeed(0.35f);

                    if (_riverTilesP0.Count >= 18)
                    {
                        SetStatus("【自动循环】：对局已满 18 巡，牌桌重置并开启新一局演示...");
                        RebuildRiverP0();
                        RebuildOpponentRivers();
                        RebuildMelds();
                        RebuildWall();
                        _opponentDiscardSequence = 0;
                        _meldDemoSequence = 0;
                        _remainingWallTiles = 69;
                        if (_wallCountText != null) _wallCountText.text = "東1局\n本0 供0\n牌山 69";
                        if (_riichiStickImage != null) _riichiStickImage.gameObject.SetActive(false);
                        yield return WaitWithSpeed(0.6f);
                    }
                }
            }
            finally
            {
                _autoLoopCoroutine = null;
            }
        }

        #endregion

        #region Serialized Page Builder (Invoked by Editor Builder)

        public void BuildSerializedPreviewPages()
        {
            var canvasGo = new GameObject("Prototype Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);
            _canvas = canvasGo.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);

            var eventGo = new GameObject("Prototype EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
            eventGo.transform.SetParent(transform, false);
            eventGo.GetComponent<InputSystemUIInputModule>().AssignDefaultActions();

            var contentGo = new GameObject("Prototype Content", typeof(RectTransform));
            contentGo.transform.SetParent(canvasGo.transform, false);
            _canvasRoot = contentGo.GetComponent<RectTransform>();
            Stretch(_canvasRoot);

            _menuBackgroundImage = CreateBackground("Menu background", _mainBackground, _canvasRoot);
            _tableBackgroundImage = CreateBackground("Table environment", _environment, _canvasRoot);
            _tableBackgroundImage.gameObject.SetActive(false);

            var stageGo = new GameObject("Design stage · 1280×720", typeof(RectTransform), typeof(RectMask2D));
            stageGo.transform.SetParent(_canvasRoot, false);
            _stageRoot = stageGo.GetComponent<RectTransform>();
            _stageRoot.anchorMin = _stageRoot.anchorMax = _stageRoot.pivot = new Vector2(0f, 1f);
            _stageRoot.sizeDelta = new Vector2(1280f, 720f);

            _menuPage = CreatePage("Main menu", _stageRoot);
            _tablePage = CreatePage("Mahjong table", _stageRoot);
            _galleryModal = CreatePage("Gallery modal", _stageRoot);
            _noticeModal = CreatePage("Notice modal", _stageRoot);
            _tablePage.SetActive(false);
            _galleryModal.SetActive(false);
            _noticeModal.SetActive(false);

            BuildMainMenuContent(_menuPage.transform);
            BuildTableContent(_tablePage.transform);
            BuildGalleryModalContent(_galleryModal.transform);
            BuildNoticeModalContent(_noticeModal.transform);
        }

        private void BuildMainMenuContent(Transform parent)
        {
            ImageRect("Octopus mascot", _octopus, new Rect(24, 150, 620, 560), parent, false, true);

            Label("疯狂动物麻将 · V5 美术原型", new Rect(64, 32, 680, 76), 50, new Color(1f, .86f, .52f), TextAlignmentOptions.Left, parent);
            Label("2.5D 立体牌桌 · 动画演示与 136 牌面图鉴", new Rect(72, 112, 600, 38), 22, new Color(1f, .83f, .49f), TextAlignmentOptions.Left, parent);

            string[] labels = { "单机对局", "联机对战", "牌面图鉴", "原型麻将", "千术秘籍", "游戏设置", "退出游戏" };
            Rect[] menuRects = {
                new Rect(752, 150, 440, 70), new Rect(752, 230, 440, 70),
                new Rect(752, 310, 440, 50), new Rect(752, 370, 440, 50),
                new Rect(752, 430, 440, 50), new Rect(752, 490, 440, 50),
                new Rect(752, 550, 440, 50)
            };

            for (int i = 0; i < labels.Length; i++)
            {
                bool major = (i < 2);
                Rect rect = menuRects[i];
                var button = ButtonRect("Button · " + labels[i], rect, major ? _primaryButton : _secondaryButton, parent, major ? 28 : 22);
                var text = button.GetComponentInChildren<Text>();
                if (text != null)
                {
                    SetRect(text.rectTransform, new Rect(84, 0, rect.width - 100, rect.height));
                    text.alignment = TextAnchor.MiddleLeft;
                }
                int iconIndex = i switch { 3 => 2, 4 => 3, 5 => 4, _ => Math.Min(i, 5) };
                AddMenuIcon(button.transform, iconIndex, _menuIconAtlas, major ? 56 : 42, major ? 10 : 7);
            }

            Label("V5 视觉美术原型 · 点击【单机对局】进入 2.5D 牌桌演示 · 点击【牌面图鉴】查看 136 牌谱与授权",
                new Rect(650, 620, 580, 36), 15, new Color(1f, .86f, .56f), TextAlignmentOptions.Center, parent);
        }

        private void BuildTableContent(Transform parent)
        {
            ImageRect("Jade table board", _board, new Rect(32, 60, 1216, 640), parent, true, false);

            var matchHud = CreatePage("Match HUD", parent.GetComponent<RectTransform>());
            _matchHudRoot = matchHud.GetComponent<RectTransform>();
            DrawSeat(0, "P0 · 东 (玩家)", _portraits.Length > 0 ? _portraits[0] : null, new Rect(18, 544, 100, 100), new Rect(18, 648, 168, 38), matchHud.transform);
            DrawSeat(1, "P1 · 南", _portraits.Length > 1 ? _portraits[1] : null, new Rect(1150, 240, 104, 104), new Rect(1096, 348, 172, 54), matchHud.transform);
            DrawSeat(2, "P2 · 西", _portraits.Length > 2 ? _portraits[2] : null, new Rect(310, 68, 104, 104), new Rect(142, 82, 164, 54), matchHud.transform);
            DrawSeat(3, "P3 · 北", _portraits.Length > 3 ? _portraits[3] : null, new Rect(16, 240, 104, 104), new Rect(12, 348, 172, 54), matchHud.transform);

            ImageRect("Round compass", _compass, new Rect(544, 252, 192, 192), matchHud.transform, true, false);
            _wallCountText = Label("WallCountText", new Rect(577, 300, 126, 100), 19, new Color(1f, .86f, .56f), TextAlignmentOptions.Center, matchHud.transform);
            _wallCountText.text = "東1局\n本0 供0\n牌山 69";

            var stickGo = new GameObject("RiichiStickImage", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            stickGo.transform.SetParent(matchHud.transform, false);
            var stickRt = stickGo.GetComponent<RectTransform>();
            SetRect(stickRt, new Rect(556, 452, 168, 24));
            _riichiStickImage = stickGo.GetComponent<Image>();
            _riichiStickImage.sprite = _riichiStick;
            _riichiStickImage.preserveAspect = true;
            _riichiStickImage.gameObject.SetActive(false);

            var wallGo = new GameObject("WallRoot", typeof(RectTransform));
            wallGo.transform.SetParent(parent, false);
            _wallRoot = wallGo.GetComponent<RectTransform>();
            SetRect(_wallRoot, new Rect(460, 204, 360, 48));

            _riverRootP0 = CreateContainer("RiverRoot_P0", new Rect(480, 470, 240, 140), parent);
            _riverRootP1 = CreateContainer("RiverRoot_P1", new Rect(960, 260, 140, 200), parent);
            _riverRootP2 = CreateContainer("RiverRoot_P2", new Rect(480, 70, 240, 140), parent);
            _riverRootP3 = CreateContainer("RiverRoot_P3", new Rect(220, 260, 140, 200), parent);

            _meldsRoot = CreateContainer("MeldsRoot", new Rect(810, 526, 420, 58), parent);

            var trayGo = ImageRect("HandTray", _panel, new Rect(188, 594, 816, 114), parent, true, false);
            _handTrayRoot = trayGo.rectTransform;
            _meldsRoot.SetAsLastSibling();

            var barGo = ImageRect("DemoControlsBar", _panel, new Rect(40, 6, 1200, 96), parent, true, false);
            _demoControlsBar = barGo.rectTransform;

            _demoStatusText = Label("DemoStatusText", new Rect(14, 8, 258, 76), 13, new Color(1f, .92f, .65f), TextAlignmentOptions.Left, _demoControlsBar);
            _demoStatusText.text = "2.5D 麻将牌桌美术原型就绪";

            Button autoBtn = ButtonRect("AutoButton", new Rect(286, 8, 88, 38), _redButton, _demoControlsBar, 14);
            _autoButtonText = autoBtn.GetComponentInChildren<Text>();
            _autoButtonText.text = "▶ 自动演示";

            Button pauseBtn = ButtonRect("PauseButton", new Rect(378, 8, 66, 38), _jadeButton, _demoControlsBar, 14);
            _pauseResumeButtonText = pauseBtn.GetComponentInChildren<Text>();
            _pauseResumeButtonText.text = "⏸ 暂停";

            Button replayBtn = ButtonRect("ReplayButton", new Rect(448, 8, 68, 38), _jadeButton, _demoControlsBar, 14);
            replayBtn.GetComponentInChildren<Text>().text = "🔄 重置";

            Button drawBtn = ButtonRect("DrawButton", new Rect(520, 8, 64, 38), _jadeButton, _demoControlsBar, 14);
            drawBtn.GetComponentInChildren<Text>().text = "摸牌 [1]";

            Button hoverBtn = ButtonRect("HoverButton", new Rect(588, 8, 64, 38), _jadeButton, _demoControlsBar, 14);
            hoverBtn.GetComponentInChildren<Text>().text = "选牌 [2]";

            Button discardBtn = ButtonRect("DiscardButton", new Rect(656, 8, 64, 38), _redButton, _demoControlsBar, 14);
            discardBtn.GetComponentInChildren<Text>().text = "打牌 [3]";

            Button riichiBtn = ButtonRect("RiichiButton", new Rect(724, 8, 64, 38), _redButton, _demoControlsBar, 14);
            riichiBtn.GetComponentInChildren<Text>().text = "立直 [4]";

            Button speedBtn = ButtonRect("SpeedButton", new Rect(792, 8, 58, 38), _jadeButton, _demoControlsBar, 13);
            _speedButtonText = speedBtn.GetComponentInChildren<Text>();
            _speedButtonText.text = "1.0x";

            Button opponentBtn = ButtonRect("OpponentDiscardButton", new Rect(854, 8, 100, 38), _redButton, _demoControlsBar, 14);
            opponentBtn.GetComponentInChildren<Text>().text = "对手出牌 [5]";

            Button exitBtn = ButtonRect("MenuButton", new Rect(958, 8, 132, 38), _jadeButton, _demoControlsBar, 15);
            exitBtn.GetComponentInChildren<Text>().text = "退出牌桌";
            exitBtn.onClick.AddListener(ShowMenu);

            Button chiBtn = ButtonRect("ChiButton", new Rect(286, 52, 84, 36), _jadeButton, _demoControlsBar, 14);
            chiBtn.GetComponentInChildren<Text>().text = "吃 [6]";

            Button ponBtn = ButtonRect("PonButton", new Rect(374, 52, 84, 36), _jadeButton, _demoControlsBar, 14);
            ponBtn.GetComponentInChildren<Text>().text = "碰 [7]";

            Button kanBtn = ButtonRect("KanButton", new Rect(462, 52, 84, 36), _redButton, _demoControlsBar, 14);
            kanBtn.GetComponentInChildren<Text>().text = "杠 [8]";

            Text actionHint = Label("CallPreviewHint", new Rect(558, 56, 530, 28), 13, new Color(1f, .86f, .58f), TextAlignmentOptions.Left, _demoControlsBar);
            actionHint.text = "对手舍牌动画与吃、碰、杠副露动画（视觉演示，不接入规则）";
        }

        private void BuildGalleryModalContent(Transform parent)
        {
            var backdrop = ImageRect("Backdrop", null, new Rect(0, 0, 1280, 720), parent, true, false);
            backdrop.color = new Color(0.02f, 0.05f, 0.04f, 0.90f);

            var panelImg = ImageRect("GalleryPanel", _panel, new Rect(80, 30, 1120, 660), parent, true, false);
            Transform pt = panelImg.transform;

            Label("136 张麻将实体牌图鉴与 CC BY-SA 4.0 授权署名", new Rect(40, 24, 880, 42), 26, new Color(1f, .86f, .52f), TextAlignmentOptions.Left, pt);
            Label("素材来源：Wikimedia Commons 现代麻将牌单牌插画系列 · 许可证：CC BY-SA 4.0\n原作者：Jerry Crimson Mann (original) / Cangjie6 (remake) / User:Dewclouds (vectorisation)\n许可证全文：https://creativecommons.org/licenses/by-sa/4.0/legalcode · 本项目牌面未做二次修改",
                new Rect(40, 68, 980, 48), 13, new Color(0.9f, 0.9f, 0.9f), TextAlignmentOptions.Left, pt);

            Button closeBtn = ButtonRect("CloseGalleryButton", new Rect(980, 24, 100, 40), _jadeButton, pt, 16);
            closeBtn.GetComponentInChildren<Text>().text = "关闭 [Esc]";
            closeBtn.onClick.AddListener(CloseGalleryModal);

            Label("【万子 9种36张】", new Rect(40, 120, 140, 24), 15, new Color(1f, .84f, .48f), TextAlignmentOptions.Left, pt);
            for (int i = 0; i < 9; i++) CreateGalleryTile(pt, i, 40 + i * 42, 148);
            CreateGalleryTile(pt, 34, 40 + 9 * 42 + 8, 148);
            Label("赤5万", new Rect(40 + 9 * 42 + 8, 206, 38, 18), 11, Color.red, TextAlignmentOptions.Center, pt);

            Label("【筒子 9种36张】", new Rect(40, 230, 140, 24), 15, new Color(1f, .84f, .48f), TextAlignmentOptions.Left, pt);
            for (int i = 0; i < 9; i++) CreateGalleryTile(pt, 9 + i, 40 + i * 42, 258);
            CreateGalleryTile(pt, 35, 40 + 9 * 42 + 8, 258);
            Label("赤5筒", new Rect(40 + 9 * 42 + 8, 316, 38, 18), 11, Color.red, TextAlignmentOptions.Center, pt);

            Label("【索子 9种36张】", new Rect(40, 340, 140, 24), 15, new Color(1f, .84f, .48f), TextAlignmentOptions.Left, pt);
            for (int i = 0; i < 9; i++) CreateGalleryTile(pt, 18 + i, 40 + i * 42, 368);
            CreateGalleryTile(pt, 36, 40 + 9 * 42 + 8, 368);
            Label("赤5索", new Rect(40 + 9 * 42 + 8, 426, 38, 18), 11, Color.red, TextAlignmentOptions.Center, pt);

            Label("【风牌 4种16张】", new Rect(40, 450, 140, 24), 15, new Color(1f, .84f, .48f), TextAlignmentOptions.Left, pt);
            for (int i = 0; i < 4; i++) CreateGalleryTile(pt, 27 + i, 40 + i * 42, 478);

            Label("【三元牌 3种12张】", new Rect(240, 450, 140, 24), 15, new Color(1f, .84f, .48f), TextAlignmentOptions.Left, pt);
            for (int i = 0; i < 3; i++) CreateGalleryTile(pt, 31 + i, 240 + i * 42, 478);

            var infoRect = new Rect(520, 130, 560, 490);
            ImageRect("AttributionBox", _panel, infoRect, pt, false, false);
            string attributionText =
                "<b>【136 张物理守恒与素材授权说明】</b>\n\n" +
                "• <b>实体牌守恒（136张）</b>：\n" +
                "  本游戏日式立直麻将标准一副牌严格闭环 136 张物理实体（编号 0..135）。\n" +
                "  包含 34 种牌型 × 每种 4 张 = 136 张。出千与牌山操作严禁无中生有。\n" +
                "  赤宝牌（赤五万、赤五筒、赤五索）各 1 张，分别替换对应普通五号牌中的 1 张。\n\n" +
                "• <b>Wikipedia / Wikimedia Commons 来源与授权</b>：\n" +
                "  词条页：https://zh.wikipedia.org/wiki/麻将\n" +
                "  单牌插画系列（MJt1~9、MJs1~9、MJw1~9、MJf1~4、MJd1~3、赤五牌）\n" +
                "  许可证：<b>CC BY-SA 4.0</b> (Attribution-ShareAlike 4.0 International)\n" +
                "  原作者：<b>Jerry Crimson Mann</b> (original), <b>Cangjie6</b> (remake), <b>User:Dewclouds</b> (vectorisation)\n" +
                "  保留文件：Assets/Art/UI/V4Prototype/Tiles/CREDITS.md\n\n" +
                "• <b>无外部网络与权威接入声明</b>：\n" +
                "  本场景为纯前端视觉表现原型，旨在检验 2.5D 厚度、斜角、投影与动画效果；\n" +
                "  未连接任何网络服务端，不更改游戏规则、算分、计费或 TableAuthority。";

            Label(attributionText, new Rect(infoRect.x + 20, infoRect.y + 18, infoRect.width - 40, infoRect.height - 36),
                13, Color.white, TextAlignmentOptions.Left, pt);
        }

        private void CreateGalleryTile(Transform parent, int tileIndex, float x, float y)
        {
            var go = new GameObject("GalleryTile_" + tileIndex, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            SetRect(rt, new Rect(x, y, 38, 54));

            var shadowGo = new GameObject("Shadow", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            shadowGo.transform.SetParent(go.transform, false);
            var sRt = shadowGo.GetComponent<RectTransform>();
            sRt.anchorMin = new Vector2(0.05f, 0f);
            sRt.anchorMax = new Vector2(0.95f, 0.28f);
            sRt.offsetMin = sRt.offsetMax = Vector2.zero;
            var sImg = shadowGo.GetComponent<Image>();
            sImg.sprite = _tileSoftShadow;
            sImg.color = new Color(0.04f, 0.08f, 0.06f, 0.45f);
            sImg.raycastTarget = false;

            var chassisGo = new GameObject("Chassis", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            chassisGo.transform.SetParent(go.transform, false);
            var cRt = chassisGo.GetComponent<RectTransform>();
            cRt.anchorMin = Vector2.zero;
            cRt.anchorMax = Vector2.one;
            cRt.offsetMin = cRt.offsetMax = Vector2.zero;
            var cImg = chassisGo.GetComponent<Image>();
            cImg.sprite = _tileStandingChassis;
            cImg.raycastTarget = false;

            Sprite? faceSprite = GetTileFaceSprite(tileIndex);
            if (faceSprite != null)
            {
                var glyphGo = new GameObject("Glyph", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                glyphGo.transform.SetParent(chassisGo.transform, false);
                var gRt = glyphGo.GetComponent<RectTransform>();
                gRt.anchorMin = new Vector2(0.10f, 0.12f);
                gRt.anchorMax = new Vector2(0.90f, 0.88f);
                gRt.offsetMin = gRt.offsetMax = Vector2.zero;
                var gImg = glyphGo.GetComponent<Image>();
                gImg.sprite = faceSprite;
                gImg.preserveAspect = true;
                gImg.raycastTarget = false;
            }
        }

        private void BuildNoticeModalContent(Transform parent)
        {
            var backdrop = ImageRect("Backdrop", null, new Rect(0, 0, 1280, 720), parent, true, false);
            backdrop.color = new Color(0.02f, 0.05f, 0.04f, 0.75f);

            var panelImg = ImageRect("NoticePanel", _panel, new Rect(360, 210, 560, 300), parent, true, false);
            Transform pt = panelImg.transform;

            var title = Label("NoticeTitle", new Rect(40, 28, 480, 48), 30, new Color(1f, .84f, .48f), TextAlignmentOptions.Center, pt);
            title.text = "入口提示";

            var msg = Label("NoticeMessage", new Rect(40, 96, 480, 68), 18, Color.white, TextAlignmentOptions.Center, pt);
            msg.text = "此页面为原型占位入口，不连接网络或产品逻辑。";

            Button closeBtn = ButtonRect("CloseNoticeButton", new Rect(190, 190, 180, 56), _redButton, pt, 20);
            closeBtn.GetComponentInChildren<Text>().text = "确定";
            closeBtn.onClick.AddListener(CloseNotice);
        }

        private void DrawSeat(int index, string name, Sprite? portrait, Rect portraitRect, Rect plateRect, Transform parent)
        {
            ImageRect("Animal portrait " + index, portrait, portraitRect, parent, true, false);
            ImageRect("Seat plate " + index, _panel, plateRect, parent, true, false);
            Label(name + "\n25000", new Rect(plateRect.x + 4, plateRect.y + 4, plateRect.width - 8, plateRect.height - 8), 15, Color.white, TextAlignmentOptions.Center, parent);
        }

        private static RectTransform CreateContainer(string name, Rect rect, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            SetRect(rt, rect);
            return rt;
        }

        private static GameObject CreatePage(string name, RectTransform stage)
        {
            var page = new GameObject(name, typeof(RectTransform));
            page.transform.SetParent(stage, false);
            var rect = page.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return page;
        }

        private static RawImage CreateBackground(string name, Sprite? sprite, RectTransform parent)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            go.transform.SetParent(parent, false);
            var image = go.GetComponent<RawImage>();
            image.texture = sprite != null ? sprite.texture : null;
            image.raycastTarget = false;
            Stretch(go.GetComponent<RectTransform>());
            return image;
        }

        private Image ImageRect(string name, Sprite? sprite, Rect rect, Transform parent, bool raycast, bool preserve)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer));
            go.transform.SetParent(parent, false);
            Image image;
            if (sprite != null && (sprite.border.sqrMagnitude > 0f || sprite.name.StartsWith("button-", StringComparison.Ordinal)))
            {
                var sliced = go.AddComponent<PrototypeNineSliceImage>();
                Vector4 sourceBorder = sprite.border.sqrMagnitude > 0f ? sprite.border : new Vector4(18f, 18f, 18f, 18f);
                Vector4 targetBorder = sprite.name == "jade-panel-skin" ? new Vector4(44f, 44f, 44f, 44f)
                    : sprite.name.StartsWith("prototype_button_", StringComparison.Ordinal) ? new Vector4(32f, 18f, 32f, 18f)
                    : new Vector4(18f, 18f, 18f, 18f);
                sliced.Configure(sourceBorder, targetBorder);
                image = sliced;
            }
            else
            {
                image = go.AddComponent<Image>();
            }

            image.sprite = sprite;
            image.preserveAspect = preserve;
            image.raycastTarget = raycast;
            if (sprite == null) image.color = new Color(.02f, .16f, .12f, .95f);
            SetRect(go.GetComponent<RectTransform>(), rect);
            return image;
        }

        private Text Label(string text, Rect rect, float size, Color color, TextAlignmentOptions alignment, Transform parent)
        {
            var go = new GameObject("Text · " + (text.Length > 16 ? text.Substring(0, 16) : text), typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, false);
            var label = go.GetComponent<Text>();
            label.text = text;
            label.font = _theme != null && _theme.fontAsset != null ? _theme.fontAsset.sourceFontFile : null;
            label.fontSize = Mathf.RoundToInt(size);
            label.color = color;
            label.alignment = ToTextAnchor(alignment);
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            label.raycastTarget = false;
            SetRect(go.GetComponent<RectTransform>(), rect);
            return label;
        }

        private Button ButtonRect(string name, Rect rect, Sprite? sprite, Transform parent, float fontSize)
        {
            var image = ImageRect(name, sprite, rect, parent, true, false);
            var button = image.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.transition = Selectable.Transition.ColorTint;
            button.colors = new ColorBlock
            {
                normalColor = Color.white,
                highlightedColor = new Color(1f, .94f, .76f, 1f),
                pressedColor = new Color(.82f, .72f, .54f, 1f),
                selectedColor = new Color(1f, .91f, .61f, 1f),
                disabledColor = new Color(.62f, .62f, .62f, 1f),
                colorMultiplier = 1f,
                fadeDuration = .08f
            };
            Label("Text", new Rect(0, 0, rect.width, rect.height), fontSize, Color.white, TextAlignmentOptions.Center, image.transform);
            return button;
        }

        private static void AddMenuIcon(Transform parent, int index, Sprite? menuAtlas, float size, float top)
        {
            if (menuAtlas == null || menuAtlas.texture == null) return;
            var go = new GameObject("Menu icon " + index, typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            go.transform.SetParent(parent, false);
            var raw = go.GetComponent<RawImage>();
            raw.texture = menuAtlas.texture;
            raw.raycastTarget = false;
            raw.uvRect = new Rect(index / 6f, 0f, 1f / 6f, 1f);
            SetRect(go.GetComponent<RectTransform>(), new Rect(18, top, size, size));
        }

        private static void SetRect(RectTransform rt, Rect rect)
        {
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(rect.x, -rect.y);
            rt.sizeDelta = new Vector2(rect.width, rect.height);
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private static TextAnchor ToTextAnchor(TextAlignmentOptions alignment)
        {
            return alignment == TextAlignmentOptions.Left || alignment == TextAlignmentOptions.MidlineLeft
                ? TextAnchor.MiddleLeft : TextAnchor.MiddleCenter;
        }

        #endregion

        #region Helpers & Metadata

        private void SetStatus(string message)
        {
            if (_demoStatusText != null)
            {
                _demoStatusText.text = message;
            }
            Debug.Log("[V5ArtPrototype] " + message);
        }

        public static string GetTileDisplayName(int tileIndex)
        {
            if (tileIndex < 0) return "牌背";
            if (tileIndex >= 0 && tileIndex <= 8) return (tileIndex + 1) + "万";
            if (tileIndex >= 9 && tileIndex <= 17) return (tileIndex - 8) + "筒";
            if (tileIndex >= 18 && tileIndex <= 26) return (tileIndex - 17) + "条";
            if (tileIndex == 27) return "东风";
            if (tileIndex == 28) return "南风";
            if (tileIndex == 29) return "西风";
            if (tileIndex == 30) return "北风";
            if (tileIndex == 31) return "白板";
            if (tileIndex == 32) return "發财";
            if (tileIndex == 33) return "红中";
            if (tileIndex == 34) return "赤五万";
            if (tileIndex == 35) return "赤五筒";
            if (tileIndex == 36) return "赤五条";
            return "未知牌 " + tileIndex;
        }

        private Sprite? GetTileFaceSprite(int tileIndex)
        {
            if (tileIndex < 0 || tileIndex >= _tiles.Length || _tiles[tileIndex] == null) return null;
            Sprite source = _tiles[tileIndex];
            if (_croppedFaceSprites.TryGetValue(source, out Sprite? cached)) return cached;

            Rect sourceRect = source.rect;
            var faceRect = new Rect(
                sourceRect.x + sourceRect.width * 0.06f,
                sourceRect.y + sourceRect.height * 0.05f,
                sourceRect.width * 0.78f,
                sourceRect.height * 0.75f);
            Sprite face = Sprite.Create(source.texture, faceRect, new Vector2(0.5f, 0.5f), source.pixelsPerUnit, 0, SpriteMeshType.FullRect);
            face.name = source.name + "_SingleTileFace";
            _croppedFaceSprites.Add(source, face);
            return face;
        }

        #endregion
    }
}
