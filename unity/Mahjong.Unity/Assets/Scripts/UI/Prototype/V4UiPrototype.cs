#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Mahjong.Unity.UI.Prototype
{
    public sealed class V4UiPrototype : MonoBehaviour
    {
        [Header("Existing V5 menu art and theme")]
        [SerializeField] private UiTheme? _theme;
        [SerializeField] private Sprite? _mainBackground;
        [SerializeField] private Sprite? _octopus;
        [SerializeField] private Sprite? _primaryButton;
        [SerializeField] private Sprite? _secondaryButton;
        [SerializeField] private Sprite? _focusButton;
        [SerializeField] private Sprite? _menuIconAtlas;

        [Header("Imported V4 table art")]
        [SerializeField] private Sprite? _environment;
        [SerializeField] private Sprite? _board;
        [SerializeField] private Sprite? _panel;
        [SerializeField] private Sprite? _redButton;
        [SerializeField] private Sprite? _jadeButton;
        [SerializeField] private Sprite[] _portraits = Array.Empty<Sprite>();
        [SerializeField] private Sprite[] _targetCards = Array.Empty<Sprite>();
        [SerializeField] private Sprite? _compass;
        [SerializeField] private Sprite[] _tiles = Array.Empty<Sprite>();

        private Canvas? _canvas;
        private RectTransform? _content;
        [SerializeField] private RectTransform? _canvasRoot;
        [SerializeField] private RectTransform? _stageRoot;
        [SerializeField] private GameObject? _menuPage;
        [SerializeField] private GameObject? _preparationPage;
        [SerializeField] private GameObject? _drawerPage;
        [SerializeField] private GameObject? _receiptPage;
        [SerializeField] private GameObject? _playingPage;
        [SerializeField] private GameObject? _noticePage;
        [SerializeField] private RawImage? _menuBackgroundImage;
        [SerializeField] private RawImage? _tableBackgroundImage;
        [SerializeField] private Text? _noticeTitle;
        [SerializeField] private Text? _noticeMessage;
        private List<Button> _menuButtons = new List<Button>();
        private readonly Dictionary<Button, Vector3> _menuButtonHomePositions = new Dictionary<Button, Vector3>();
        private readonly Dictionary<Button, Coroutine> _menuEntranceCoroutines = new Dictionary<Button, Coroutine>();
        private int _menuEntranceGeneration;
        private float _designOffsetY;
        private int _lastScreenWidth;
        private int _lastScreenHeight;
        private bool _buildingPages;
        private float _designOffsetX;
        private Vector2Int _requestedResolution = new Vector2Int(1280, 720);
        private bool _seenThrough;
        private int _selectedTarget = -1;
        private bool _drawerOpen;
        private string _previewStage = "menu";

        private static readonly string[] TargetNames =
        {
            "清一色（万）", "清一色（条）", "清一色（筒）", "大三元", "国士无双", "大四喜"
        };

        public bool HasSerializedPreviewPages => _canvasRoot != null && _stageRoot != null && _menuPage != null
            && _preparationPage != null && _drawerPage != null && _receiptPage != null && _playingPage != null && _noticePage != null;

        private void Awake()
        {
            _requestedResolution = ReadPreviewResolution();
            if (!Application.isEditor)
            {
                Screen.SetResolution(_requestedResolution.x, _requestedResolution.y, false);
            }
            Debug.Log("V4_UI_PROTOTYPE_STARTED requested=" + _requestedResolution.x + "x" + _requestedResolution.y
                + " initial=" + Screen.width + "x" + Screen.height);
            if (_canvasRoot == null || _stageRoot == null || _menuPage == null)
            {
                Debug.LogError("V4_UI_PROTOTYPE_SCENE_MISSING_SERIALIZED_PAGES");
                enabled = false;
                return;
            }
            BindSerializedActions();
            ShowMenu();
            ApplyScreenLayout(true);
        }

        private void BindSerializedActions()
        {
            // Unity does not persist references to the page children created while building the prefab.
            // Rebind from the serialized page hierarchy so the six menu entries remain clickable.
            _menuButtons = _menuPage != null
                ? _menuPage.GetComponentsInChildren<Button>(true)
                    .Where(button => button.transform.parent == _menuPage.transform)
                    .ToList()
                : new List<Button>();
            _menuButtonHomePositions.Clear();
            foreach (Button button in _menuButtons) RememberMenuButton(button);
            BindMenuAction(0, () => ShowPreparation());
            BindMenuAction(1, () => OnMenuAction(1));
            BindMenuAction(2, () => OnMenuAction(2));
            BindMenuAction(3, () => OnMenuAction(3));
            BindMenuAction(4, () => OnMenuAction(4));
            BindMenuAction(5, () => OnMenuAction(5));
            BindNamedButton(_preparationPage, "偷梁换柱", () => ShowDrawer());
            BindNamedButton(_preparationPage, "看破：", () => { _seenThrough = !_seenThrough; ShowPreparation(); });
            BindNamedButton(_preparationPage, "开始本局", () => ShowReceipt());
            BindNamedButton(_preparationPage, "确认看破", () => ShowReceipt());
            BindNamedButton(_drawerPage, "返回", () => ReturnToPreparation());
            Button[] cards = _drawerPage != null
                ? _drawerPage.GetComponentsInChildren<Button>(true).Where(b => b.name == "Button · ").ToArray()
                : Array.Empty<Button>();
            for (int i = 0; i < cards.Length; i++)
            {
                int index = i;
                cards[i].onClick.AddListener(() => { _selectedTarget = _selectedTarget == index ? -1 : index; ShowDrawer(); });
            }
            BindNamedButton(_drawerPage, "看破：", () => { _seenThrough = !_seenThrough; ShowDrawer(); });
            BindNamedButton(_drawerPage, "直接开局", () => { _selectedTarget = -1; _seenThrough = false; ShowReceipt(); });
            BindNamedButton(_drawerPage, "确认施术", () => { if (_selectedTarget >= 0 || _seenThrough) ShowReceipt(); });
            BindNamedButton(_drawerPage, "确认看破", () => { if (_seenThrough) ShowReceipt(); });
            BindNamedButton(_receiptPage, "发牌预览", () => ShowPlaying());
            BindNamedButton(_receiptPage, "返回准备", () => ShowPreparation());
            BindNamedButton(_playingPage, "准备阶段预览", () => ShowPreparation());
            BindNamedButton(_noticePage, "返回主菜单", () => ShowMenu());
        }

        private void BindMenuAction(int index, Action action)
        {
            if (index < 0 || index >= _menuButtons.Count || _menuButtons[index] == null) return;
            _menuButtons[index].onClick.AddListener(() => action());
        }

        private static void BindNamedButton(GameObject? page, string namePart, Action action)
        {
            if (page == null) return;
            Button? button = page.GetComponentsInChildren<Button>(true).FirstOrDefault(b => b.name.Contains(namePart, StringComparison.Ordinal));
            if (button != null) button.onClick.AddListener(() => action());
        }

        private void Update()
        {
            if (Keyboard.current != null)
            {
                if (Keyboard.current.f1Key.wasPressedThisFrame) ShowMenu();
                if (Keyboard.current.f2Key.wasPressedThisFrame) ShowPreparation();
                if (Keyboard.current.f3Key.wasPressedThisFrame) ShowDrawer();
                if (Keyboard.current.f4Key.wasPressedThisFrame) ShowReceipt();
                if (Keyboard.current.f5Key.wasPressedThisFrame) ShowPlaying();
                if (!Application.isEditor && Keyboard.current.f6Key.wasPressedThisFrame) Screen.SetResolution(960, 540, false);
                if (!Application.isEditor && Keyboard.current.f7Key.wasPressedThisFrame) Screen.SetResolution(1600, 600, false);
                if (Keyboard.current.f8Key.wasPressedThisFrame) CapturePreviewFrame();
            }
            ApplyScreenLayout(false);
            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                if (_drawerOpen) ReturnToPreparation();
                else if (_selectedTarget >= 0 || _seenThrough) ShowPreparation();
                else ShowMenu();
            }
        }

        public void BuildSerializedPreviewPages()
        {
            if (_canvasRoot != null || _stageRoot != null || _menuPage != null)
                throw new InvalidOperationException("Refusing to replace an existing serialized UI hierarchy.");
            var canvasObject = new GameObject("Prototype Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(transform, false);
            _canvas = canvasObject.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            var root = new GameObject("Prototype Content", typeof(RectTransform));
            root.transform.SetParent(canvasObject.transform, false);
            _canvasRoot = root.GetComponent<RectTransform>();
            Stretch(_canvasRoot);
            _menuBackgroundImage = CreateBackground("Menu background", _mainBackground);
            _tableBackgroundImage = CreateBackground("Table environment", _environment);
            var stage = new GameObject("Design stage · 1280×720", typeof(RectTransform), typeof(RectMask2D));
            stage.transform.SetParent(_canvasRoot, false);
            _stageRoot = stage.GetComponent<RectTransform>();
            _stageRoot.anchorMin = _stageRoot.anchorMax = _stageRoot.pivot = new Vector2(0f, 1f);
            _stageRoot.sizeDelta = new Vector2(1280f, 720f);
            _menuPage = CreatePage("Main menu");
            _preparationPage = CreatePage("Preparation");
            _drawerPage = CreatePage("Target drawer");
            _receiptPage = CreatePage("Confirmation receipt");
            _playingPage = CreatePage("Dealt table");
            _noticePage = CreatePage("Menu preview notice");
            var eventObject = new GameObject("Prototype EventSystem", typeof(EventSystem), typeof(UnityEngine.InputSystem.UI.InputSystemUIInputModule));
            eventObject.transform.SetParent(transform, false);
            eventObject.GetComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>().AssignDefaultActions();

            _buildingPages = true;
            _content = _menuPage.GetComponent<RectTransform>(); ShowMenu();
            _content = _preparationPage.GetComponent<RectTransform>(); ShowPreparation();
            _content = _drawerPage.GetComponent<RectTransform>(); ShowDrawer();
            _content = _receiptPage.GetComponent<RectTransform>(); ShowReceipt();
            _content = _playingPage.GetComponent<RectTransform>(); ShowPlaying();
            _content = _noticePage.GetComponent<RectTransform>(); ShowNotice("预览入口", "此页面展示菜单入口的原型提示，不连接业务流程。");
            _buildingPages = false;
            ShowMenu();
            ApplyScreenLayout(true);
        }

        private GameObject CreatePage(string name)
        {
            var page = new GameObject(name, typeof(RectTransform));
            page.transform.SetParent(_stageRoot, false);
            var rect = page.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return page;
        }

        private RawImage CreateBackground(string name, Sprite? sprite)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            go.transform.SetParent(_canvasRoot, false);
            var image = go.GetComponent<RawImage>();
            image.texture = sprite != null ? sprite.texture : null;
            image.raycastTarget = false;
            Stretch(go.GetComponent<RectTransform>());
            return image;
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
            float actualScale = Mathf.Max(0.0001f, canvas.scaleFactor);
            float logicalWidth = canvas.pixelRect.width / actualScale;
            float logicalHeight = canvas.pixelRect.height / actualScale;
            _designOffsetX = Mathf.Max(0f, (logicalWidth - 1280f) * 0.5f);
            _designOffsetY = Mathf.Max(0f, (logicalHeight - 720f) * 0.5f);
            _stageRoot.anchoredPosition = new Vector2(_designOffsetX, -_designOffsetY);
            UpdateBackgroundCrop(_menuBackgroundImage, _mainBackground);
            UpdateBackgroundCrop(_tableBackgroundImage, _environment);
            Debug.Log("V4_UI_PROTOTYPE_LAYOUT screen=" + Screen.width + "x" + Screen.height
                + " offset=" + _designOffsetX + "," + _designOffsetY + " canvasScale=" + actualScale
                + " logical=" + logicalWidth + "x" + logicalHeight);
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

        private void ActivatePage(GameObject? active, bool menuBackground)
        {
            if (_buildingPages) return;
            if (active != _menuPage) StopMenuEntrance(true);
            if (_menuPage != null) _menuPage.SetActive(active == _menuPage);
            if (_preparationPage != null) _preparationPage.SetActive(active == _preparationPage);
            if (_drawerPage != null) _drawerPage.SetActive(active == _drawerPage);
            if (_receiptPage != null) _receiptPage.SetActive(active == _receiptPage);
            if (_playingPage != null) _playingPage.SetActive(active == _playingPage);
            if (_noticePage != null) _noticePage.SetActive(active == _noticePage);
            if (_menuBackgroundImage != null) _menuBackgroundImage.gameObject.SetActive(menuBackground);
            if (_tableBackgroundImage != null) _tableBackgroundImage.gameObject.SetActive(!menuBackground);
        }

        private static void SetPageText(GameObject? page, string namePrefix, string value)
        {
            if (page == null) return;
            var text = page.GetComponentsInChildren<Text>(true).FirstOrDefault(t => t.name.StartsWith(namePrefix, StringComparison.Ordinal));
            if (text != null) text.text = value;
        }

        private void RefreshPreparation()
        {
            SetPageText(_preparationPage, "Text · 看破：", _seenThrough ? "看破：已开启 · 10点" : "看破：未开启 · 10点");
            SetPageText(_preparationPage, "Text · 当前预览消耗：", "当前预览消耗：" + (_seenThrough ? "10" : "0") + " 点");
            SetPageText(_preparationPage, "Text · 开始本局", _seenThrough ? "确认看破 · 10点" : "开始本局 · 0点");
        }

        private void RefreshDrawer()
        {
            if (_drawerPage == null) return;
            Button[] cards = _drawerPage.GetComponentsInChildren<Button>(true).Where(b => b.name == "Button · ").ToArray();
            for (int i = 0; i < cards.Length; i++)
            {
                var outline = cards[i].GetComponent<Outline>();
                if (outline != null) outline.enabled = i == _selectedTarget;
                var marker = cards[i].transform.Find("Selected marker");
                if (marker != null) marker.gameObject.SetActive(i == _selectedTarget);
            }
            SetPageText(_drawerPage, "Text · 看破：", _seenThrough ? "看破：已开启 · 10点" : "看破：未开启 · 10点");
            int cost = (_selectedTarget >= 0 ? 30 : 0) + (_seenThrough ? 10 : 0);
            SetPageText(_drawerPage, "Text · 本次消耗", "本次消耗 " + cost + " 点");
            Button? confirm = _drawerPage.GetComponentsInChildren<Button>(true).FirstOrDefault(b => b.name.Contains("确认施术") || b.name.Contains("确认看破"));
            if (confirm != null)
            {
                confirm.interactable = _selectedTarget >= 0 || _seenThrough;
                Text? label = confirm.GetComponentInChildren<Text>(true);
                if (label != null) label.text = _seenThrough && _selectedTarget < 0 ? "确认看破 · 10点"
                    : _selectedTarget < 0 ? "先选择目标牌型" : "确认施术 · " + (_seenThrough ? "40" : "30") + "点";
            }
        }

        private void RefreshReceipt()
        {
            string selection = _selectedTarget >= 0 ? TargetNames[_selectedTarget] : (_seenThrough ? "看破" : "不使用千术");
            int cost = (_selectedTarget >= 0 ? 30 : 0) + (_seenThrough ? 10 : 0);
            SetPageText(_receiptPage, "Text · 预览确认", selection + "\n预览费用：" + cost + " 点\n（静态样例，不写入游戏经济）");
        }

        private void StartMenuEntrance()
        {
            StopMenuEntrance(true);
            int generation = _menuEntranceGeneration;
            for (int i = 0; i < _menuButtons.Count; i++)
            {
                Button button = _menuButtons[i];
                if (button == null) continue;
                RememberMenuButton(button);
                Vector3 target = _menuButtonHomePositions[button];
                button.interactable = false;
                if (Application.isPlaying)
                {
                    button.transform.localPosition = target + new Vector3(96f, 0f, 0f);
                    _menuEntranceCoroutines[button] = StartCoroutine(EnterMenuButton(button, target, i * .07f, generation));
                }
                else
                {
                    button.transform.localPosition = target;
                    button.interactable = true;
                }
            }
        }

        private void RememberMenuButton(Button button)
        {
            if (button != null && !_menuButtonHomePositions.ContainsKey(button))
                _menuButtonHomePositions.Add(button, button.transform.localPosition);
        }

        private void StopMenuEntrance(bool restoreHomePositions)
        {
            _menuEntranceGeneration++;
            foreach (Coroutine coroutine in _menuEntranceCoroutines.Values)
                if (coroutine != null) StopCoroutine(coroutine);
            _menuEntranceCoroutines.Clear();
            foreach (KeyValuePair<Button, Vector3> entry in _menuButtonHomePositions)
            {
                if (entry.Key == null) continue;
                if (restoreHomePositions) entry.Key.transform.localPosition = entry.Value;
                entry.Key.interactable = true;
            }
        }

        private void Clear()
        {
            if (!_buildingPages || _content == null) return;
            StopMenuEntrance(true);
            for (int i = _content.childCount - 1; i >= 0; i--)
            {
                if (Application.isPlaying) Destroy(_content.GetChild(i).gameObject);
                else DestroyImmediate(_content.GetChild(i).gameObject);
            }
            _menuButtons.Clear();
            _menuButtonHomePositions.Clear();
        }

        private void ShowMenu()
        {
            if (!_buildingPages)
            {
                _previewStage = "menu";
                _drawerOpen = false;
                ActivatePage(_menuPage, true);
                StartMenuEntrance();
                return;
            }
            _previewStage = "menu";
            _drawerOpen = false;
            Clear();
            ImageRect("Octopus mascot", _octopus, new Rect(24, 150, 620, 560), _content!, false, true);
            Label("疯狂动物麻将", new Rect(64, 32, 620, 76), 54, new Color(1f, .86f, .52f), TextAlignmentOptions.Left);
            Label("三大千术 · 牌桌心战", new Rect(72, 112, 600, 38), 24, new Color(1f, .83f, .49f), TextAlignmentOptions.Left);
            string[] labels = { "单机对局", "联机对战", "牌面皮肤", "千术秘籍", "游戏设置", "退出游戏" };
            Rect[] menuRects = {
                new Rect(752, 160, 440, 80), new Rect(752, 252, 440, 80),
                new Rect(752, 344, 440, 58), new Rect(752, 414, 440, 58),
                new Rect(752, 484, 440, 58), new Rect(752, 554, 440, 58)
            };
            for (int i = 0; i < labels.Length; i++)
            {
                int ix = i;
                bool major = i < 2;
                Rect itemRect = menuRects[i];
                bool redButton = major;
                var button = ButtonRect(labels[i], itemRect, redButton ? _primaryButton : _secondaryButton,
                    () => OnMenuAction(ix), major ? 29 : 23);
                var text = button.GetComponentInChildren<Text>();
                if (text != null)
                {
                    SetRect(text.rectTransform, new Rect(88, 0, itemRect.width - 104, itemRect.height));
                    text.alignment = TextAnchor.MiddleLeft;
                }
                AddMenuIcon(button.transform, i, major ? 58 : 44, major ? 7 : 7);
                _menuButtons.Add(button);
                RememberMenuButton(button);
            }
            Label("V4 视觉原型 · 按钮可点击预览", new Rect(752, 624, 440, 28), 16, new Color(1f, .86f, .56f), TextAlignmentOptions.Center);
            if (Application.isPlaying) StartMenuEntrance();
            QueuePreviewCapture();
        }

        private System.Collections.IEnumerator EnterMenuButton(Button button, Vector3 target, float delay, int generation)
        {
            yield return new WaitForSeconds(delay);
            if (button == null || generation != _menuEntranceGeneration) yield break;
            Vector3 start = target + new Vector3(96f, 0f, 0f);
            float elapsed = 0f;
            while (elapsed < .42f)
            {
                if (button == null || generation != _menuEntranceGeneration) yield break;
                elapsed += Time.unscaledDeltaTime;
                float p = Mathf.Clamp01(elapsed / .42f);
                float eased = 1f - Mathf.Pow(1f - p, 3f);
                button.transform.localPosition = Vector3.LerpUnclamped(start, target, eased);
                yield return null;
            }
            if (button != null && generation == _menuEntranceGeneration)
            {
                button.transform.localPosition = target;
                button.interactable = true;
                _menuEntranceCoroutines.Remove(button);
            }
        }

        private void OnMenuAction(int action)
        {
            switch (action)
            {
                case 0: ShowPreparation(); break;
                case 1: ShowNotice("联机对战", "此处仅预览 V4 菜单入口；没有连接服务器。"); break;
                case 2: ShowNotice("牌面皮肤", "此处仅展示已导入的牌面皮肤资源。"); break;
                case 3: ShowNotice("千术秘籍", "此处为菜单弹窗视觉占位，不执行任何千术逻辑。"); break;
                case 4: ShowNotice("游戏设置", "此处为设置面板视觉占位，不保存设置。"); break;
                default: ShowNotice("退出游戏", "原型预览不会关闭 Unity 编辑器。"); break;
            }
        }

        private void ShowNotice(string title, string message)
        {
            if (!_buildingPages)
            {
                _previewStage = "notice";
                if (_noticeTitle != null) _noticeTitle.text = title;
                if (_noticeMessage != null) _noticeMessage.text = message;
                ActivatePage(_noticePage, true);
                return;
            }
            Clear();
            ImageRect("Notice panel", _panel, new Rect(360, 210, 560, 300), _content!, false, false);
            _noticeTitle = Label(title, new Rect(400, 235, 480, 52), 34, new Color(1f, .82f, .42f), TextAlignmentOptions.Center);
            _noticeMessage = Label(message, new Rect(410, 310, 460, 72), 22, Color.white, TextAlignmentOptions.Center);
            ButtonRect("返回主菜单", new Rect(500, 420, 280, 64), _redButton, ShowMenu, 24);
        }

        private void ShowPreparation()
        {
            if (!_buildingPages)
            {
                _previewStage = "preparation";
                _drawerOpen = false;
                _selectedTarget = -1;
                ActivatePage(_preparationPage, false);
                RefreshPreparation();
                return;
            }
            _previewStage = "preparation";
            _drawerOpen = false;
            _selectedTarget = -1;
            Clear();
            DrawTableBase(false);
            ImageRect("Preparation panel", _panel, new Rect(936, 48, 312, 624), _content!, true, false);
            Label("本局准备", new Rect(958, 74, 268, 44), 30, new Color(1f, .83f, .48f), TextAlignmentOptions.Center);
            Label("演示倒计时  20 秒", new Rect(958, 128, 268, 30), 17, Color.white, TextAlignmentOptions.Center);
            ButtonRect("偷梁换柱 · 30点", new Rect(956, 188, 272, 76), _redButton, ShowDrawer, 23);
            Label("选择后再挑牌型", new Rect(962, 256, 260, 28), 15, new Color(1f, .88f, .64f), TextAlignmentOptions.Center);
            var toggle = ButtonRect(_seenThrough ? "看破：已开启 · 10点" : "看破：未开启 · 10点", new Rect(956, 334, 272, 58), _jadeButton,
                () => { _seenThrough = !_seenThrough; ShowPreparation(); }, 19);
            Label("仅切换原型展示状态", new Rect(962, 390, 260, 24), 14, Color.white, TextAlignmentOptions.Center);
            Label("当前预览消耗：" + (_seenThrough ? "10" : "0") + " 点", new Rect(956, 444, 272, 34), 19, Color.white, TextAlignmentOptions.Center);
            ButtonRect(_seenThrough ? "确认看破 · 10点" : "开始本局 · 0点", new Rect(956, 538, 272, 68), _jadeButton,
                ShowReceipt, 20);
            Label("未发牌 · 手牌留空", new Rect(254, 584, 532, 48), 24, new Color(1f, .88f, .56f), TextAlignmentOptions.Center);
            QueuePreviewCapture();
        }

        private void ShowDrawer()
        {
            if (!_buildingPages)
            {
                _previewStage = "targets";
                _drawerOpen = true;
                ActivatePage(_drawerPage, false);
                RefreshDrawer();
                return;
            }
            _previewStage = "targets";
            _drawerOpen = true;
            Clear();
            DrawTableBase(false);
            ImageRect("Target drawer", _panel, new Rect(688, 40, 560, 640), _content!, true, false);
            Label("偷梁换柱 · 选择目标", new Rect(710, 54, 400, 42), 28, new Color(1f, .83f, .48f), TextAlignmentOptions.Left);
            ButtonRect("返回", new Rect(1140, 54, 88, 40), _jadeButton, ReturnToPreparation, 17);
            Label("影响起手及后续摸牌，不保证成型", new Rect(708, 104, 520, 32), 17, Color.white, TextAlignmentOptions.Center);
            Rect[] cards = {
                new Rect(708, 148, 252, 120), new Rect(976, 148, 252, 120),
                new Rect(708, 282, 252, 120), new Rect(976, 282, 252, 120),
                new Rect(708, 416, 252, 120), new Rect(976, 416, 252, 120)
            };
            for (int i = 0; i < cards.Length; i++)
            {
                int index = i;
                var card = ButtonRect("", cards[i], _targetCards.Length > i ? _targetCards[i] : _secondaryButton,
                    () => { _selectedTarget = _selectedTarget == index ? -1 : index; ShowDrawer(); }, 17);
                var outline = card.gameObject.AddComponent<Outline>();
                outline.effectColor = new Color(1f, .79f, .28f, 1f);
                outline.effectDistance = new Vector2(4f, 4f);
                outline.enabled = _selectedTarget == i;
                Label(TargetNames[i], new Rect(cards[i].x + 8, cards[i].y + 82, cards[i].width - 16, 32), 19,
                    _selectedTarget == i ? new Color(1f, .83f, .35f) : Color.white, TextAlignmentOptions.Center);
                var marker = Label("已选", new Rect(cards[i].x + cards[i].width - 52, cards[i].y + 4, 44, 30), 16, Color.yellow, TextAlignmentOptions.Center);
                marker.name = "Selected marker";
                marker.gameObject.SetActive(_selectedTarget == i);
            }
            ButtonRect(_seenThrough ? "看破：已开启 · 10点" : "看破：未开启 · 10点", new Rect(708, 548, 252, 54), _jadeButton,
                () => { _seenThrough = !_seenThrough; ShowDrawer(); }, 18);
            Label("本次消耗 " + (_selectedTarget >= 0 ? (_seenThrough ? 40 : 30) : (_seenThrough ? 10 : 0)) + " 点", new Rect(976, 548, 252, 54), 18,
                new Color(1f, .85f, .48f), TextAlignmentOptions.Center);
            ButtonRect("直接开局 · 0点", new Rect(708, 616, 252, 48), _jadeButton,
                () => { _selectedTarget = -1; _seenThrough = false; ShowReceipt(); }, 17);
            var confirm = ButtonRect(_selectedTarget >= 0 ? "确认施术 · " + (_seenThrough ? "40" : "30") + "点" : (_seenThrough ? "确认看破 · 10点" : "先选择目标牌型"),
                new Rect(976, 616, 252, 48), _redButton, () => { if (_selectedTarget >= 0) ShowReceipt(); }, 17);
            confirm.name = "Button · 确认施术";
            confirm.onClick.RemoveAllListeners();
            confirm.onClick.AddListener(() => { if (_selectedTarget >= 0 || _seenThrough) ShowReceipt(); });
            confirm.interactable = _selectedTarget >= 0 || _seenThrough;
            QueuePreviewCapture();
        }

        private void ReturnToPreparation()
        {
            _selectedTarget = -1;
            ShowPreparation();
        }

        private void ShowReceipt()
        {
            if (!_buildingPages)
            {
                _previewStage = "receipt";
                _drawerOpen = false;
                ActivatePage(_receiptPage, false);
                RefreshReceipt();
                return;
            }
            _previewStage = "receipt";
            _drawerOpen = false;
            Clear();
            DrawTableBase(false);
            ImageRect("Confirmation receipt", _panel, new Rect(944, 160, 304, 260), _content!, true, false);
            Label("方案已确认", new Rect(960, 180, 272, 46), 28, new Color(1f, .83f, .48f), TextAlignmentOptions.Center);
            string selection = _selectedTarget >= 0 ? TargetNames[_selectedTarget] : (_seenThrough ? "看破" : "不使用千术");
            int cost = (_selectedTarget >= 0 ? 30 : 0) + (_seenThrough ? 10 : 0);
            var receiptText = Label(selection + "\n预览费用：" + cost + " 点\n（静态样例，不写入游戏经济）", new Rect(962, 242, 268, 104), 19, Color.white, TextAlignmentOptions.Center);
            receiptText.name = "Text · 预览确认";
            ButtonRect("发牌预览", new Rect(964, 360, 128, 48), _redButton, ShowPlaying, 17);
            ButtonRect("返回准备", new Rect(1094, 360, 132, 48), _jadeButton, ShowPreparation, 17);
            QueuePreviewCapture();
        }

        private void ShowPlaying()
        {
            if (!_buildingPages)
            {
                _previewStage = "playing";
                _drawerOpen = false;
                ActivatePage(_playingPage, false);
                return;
            }
            _previewStage = "playing";
            _drawerOpen = false;
            Clear();
            DrawTableBase(true);
            DrawRiver(new Vector2(490, 146), false, 0);
            DrawRiver(new Vector2(490, 470), false, 4);
            DrawRiver(new Vector2(270, 286), true, 8);
            DrawRiver(new Vector2(978, 286), true, 12);
            DrawSeat(0, "P0 · 东", new Rect(18, 568, 104, 104), new Rect(18, 674, 174, 38));
            DrawSeat(1, "P1 · 南", new Rect(1144, 228, 112, 112), new Rect(1084, 344, 184, 60));
            DrawSeat(2, "P2 · 西", new Rect(308, 4, 112, 112), new Rect(132, 22, 176, 60));
            DrawSeat(3, "P3 · 北", new Rect(16, 228, 112, 112), new Rect(12, 344, 176, 60));
            ImageRect("Wind compass", _compass, new Rect(544, 252, 192, 192), _content!, true, false);
            Label("東1局\n本0 供0\n牌山 69", new Rect(577, 300, 126, 100), 20, new Color(1f, .86f, .56f), TextAlignmentOptions.Center);
            var tray = ImageRect("Sample hand tray", _panel, new Rect(204, 596, 858, 108), _content!, true, false);
            int[] samples = { 0, 9, 18, 27, 30, 32, 33, 3, 12, 21, 28, 29, 31, 36 };
            for (int i = 0; i < samples.Length; i++)
            {
                Sprite? tile = _tiles.Length > samples[i] ? _tiles[samples[i]] : null;
                ImageRect("Sample tile " + i, tile, new Rect(10 + i * 58, 18, 54, 72), tray.transform, true, false);
            }
            Label("P0", new Rect(602, 456, 76, 24), 16, Color.white, TextAlignmentOptions.Center);
            ButtonRect("准备阶段预览", new Rect(1010, 24, 226, 46), _jadeButton, ShowPreparation, 18);
            QueuePreviewCapture();
        }

        private void DrawTableBase(bool playing)
        {
            ImageRect("Jade table board", _board, new Rect(32, 76, 1216, 620), _content!, true, false);
            if (!playing) DrawTableHud(_previewStage == "targets");
        }

        private void DrawTableHud(bool drawer)
        {
            DrawSeat(0, "P0 · 东", new Rect(16, 470, 144, 144), new Rect(16, 622, 176, 60));
            DrawSeat(1, "P1 · 南", drawer ? new Rect(544, 160, 124, 124) : new Rect(800, 178, 128, 128),
                drawer ? new Rect(536, 288, 140, 60) : new Rect(796, 310, 140, 60));
            DrawSeat(2, "P2 · 西", new Rect(368, 8, 120, 120), new Rect(496, 30, 176, 60));
            DrawSeat(3, "P3 · 北", new Rect(16, 184, 144, 144), new Rect(16, 332, 176, 60));
            ImageRect("Wind compass", _compass, new Rect(328, 254, 204, 204), _content!, false, true);
            Label("東1局\n本0 供0\n牌山 69", new Rect(366, 300, 128, 108), 19,
                new Color(1f, .86f, .56f), TextAlignmentOptions.Center);
        }

        private void ImageFullScreen(string name, Sprite? sprite)
        {
            if (sprite == null)
            {
                Image fallbackImage = ImageRect(name, null, new Rect(0, 0, 1280, 720), _content!, false, false);
                Stretch(fallbackImage.rectTransform);
                return;
            }
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            go.transform.SetParent(_content, false);
            var image = go.GetComponent<RawImage>();
            image.texture = sprite.texture;
            image.raycastTarget = false;
            Rect uv = new Rect(sprite.rect.x / sprite.texture.width, sprite.rect.y / sprite.texture.height,
                sprite.rect.width / sprite.texture.width, sprite.rect.height / sprite.texture.height);
            float spriteAspect = sprite.rect.width / sprite.rect.height;
            float screenAspect = _requestedResolution.x / (float)_requestedResolution.y;
            if (screenAspect > spriteAspect)
            {
                float visibleHeight = spriteAspect / screenAspect;
                uv.y += uv.height * (1f - visibleHeight) * 0.5f;
                uv.height *= visibleHeight;
            }
            else
            {
                float visibleWidth = screenAspect / spriteAspect;
                uv.x += uv.width * (1f - visibleWidth) * 0.5f;
                uv.width *= visibleWidth;
            }
            image.uvRect = uv;
            Stretch(go.GetComponent<RectTransform>());
        }

        private void DrawRiver(Vector2 origin, bool vertical, int firstTile)
        {
            for (int i = 0; i < 4; i++)
            {
                Sprite? tile = _tiles.Length > firstTile + i ? _tiles[firstTile + i] : null;
                Rect rect = vertical
                    ? new Rect(origin.x + (i % 2) * 34, origin.y + (i / 2) * 42, 30, 38)
                    : new Rect(origin.x + i * 34, origin.y, 30, 38);
                Image image = ImageRect("Sample river tile " + (firstTile + i), tile, rect, _content!, false, true);
                if (vertical) image.rectTransform.localRotation = Quaternion.Euler(0, 0, 90);
            }
        }

        private void MaskedMascot()
        {
            ImageRect("Octopus hero · full transparent source", _octopus, new Rect(-24, 150, 780, 744), _content!, false, true);
        }

        private void DrawSeat(int index, string name, Rect portrait, Rect plate)
        {
            Sprite? sprite = _portraits.Length > index ? _portraits[index] : null;
            ImageRect("Animal portrait " + index, sprite, portrait, _content!, true, false);
            ImageRect("Seat plate " + index, _panel, plate, _content!, true, false);
            Label(name + "\n25000", new Rect(plate.x + 6, plate.y + 4, plate.width - 12, plate.height - 8), 16, Color.white, TextAlignmentOptions.Center);
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
            else image = go.AddComponent<Image>();
            image.sprite = sprite;
            image.preserveAspect = preserve;
            image.raycastTarget = raycast;
            if (sprite == null) image.color = new Color(.02f, .16f, .12f, .95f);
            SetRect(go.GetComponent<RectTransform>(), rect);
            return image;
        }

        private void AddMenuIcon(Transform parent, int index, float size, float top)
        {
            if (_menuIconAtlas == null || _menuIconAtlas.texture == null) return;
            var go = new GameObject("Menu icon " + index, typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            go.transform.SetParent(parent, false);
            var raw = go.GetComponent<RawImage>();
            raw.texture = _menuIconAtlas.texture;
            raw.raycastTarget = false;
            raw.uvRect = new Rect(index / 6f, 0f, 1f / 6f, 1f);
            SetRect(go.GetComponent<RectTransform>(), new Rect(18, top, size, size));
        }

        private Text Label(string text, Rect rect, float size, Color color, TextAlignmentOptions alignment)
        {
            var go = new GameObject("Text · " + text, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(_content, false);
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

        private Text LabelInParent(string text, Rect rect, float size, Color color, TextAlignmentOptions alignment, Transform parent)
        {
            var go = new GameObject("Text · " + text, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
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

        private Button ButtonRect(string label, Rect rect, Sprite? sprite, Action action, float fontSize)
        {
            var image = ImageRect("Button · " + label, sprite, rect, _content!, true, false);
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
            button.onClick.AddListener(() => action());
            if (!string.IsNullOrEmpty(label))
                LabelInParent(label, new Rect(0, 0, rect.width, rect.height), fontSize, Color.white, TextAlignmentOptions.Center, image.transform);
            return button;
        }

        private void SetRect(RectTransform rt, Rect rect)
        {
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(rect.x, -rect.y);
            rt.sizeDelta = new Vector2(rect.width, rect.height);
        }

        private static TextAnchor ToTextAnchor(TextAlignmentOptions alignment)
        {
            return alignment == TextAlignmentOptions.Left || alignment == TextAlignmentOptions.MidlineLeft
                ? TextAnchor.MiddleLeft : TextAnchor.MiddleCenter;
        }

        private static Vector2Int ReadPreviewResolution()
        {
            string? value = Environment.GetEnvironmentVariable("V4_UI_PROTOTYPE_RESOLUTION");
            if (!string.IsNullOrWhiteSpace(value))
            {
                string[] parts = value.ToLowerInvariant().Split('x');
                if (parts.Length == 2 && int.TryParse(parts[0], out int width) && int.TryParse(parts[1], out int height)
                    && width >= 640 && height >= 360)
                    return new Vector2Int(width, height);
            }
            return new Vector2Int(1280, 720);
        }

        private void CapturePreviewFrame()
        {
            string? directory = Environment.GetEnvironmentVariable("V4_UI_PROTOTYPE_CAPTURE_DIR");
            if (string.IsNullOrWhiteSpace(directory)) directory = Application.persistentDataPath;
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "v4-ui-prototype-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".png");
            Type? captureType = Type.GetType("UnityEngine.ScreenCapture, UnityEngine.ScreenCaptureModule");
            MethodInfo? capture = captureType?.GetMethod("CaptureScreenshot", new[] { typeof(string) });
            if (capture == null)
            {
                Debug.LogError("V4_UI_PROTOTYPE_CAPTURE_UNAVAILABLE");
                return;
            }
            capture.Invoke(null, new object[] { path });
            Debug.Log("V4_UI_PROTOTYPE_CAPTURE=" + path + " stage=" + _previewStage + " screen=" + Screen.width + "x" + Screen.height);
        }

        private void QueuePreviewCapture()
        {
            if (Application.isPlaying && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("V4_UI_PROTOTYPE_CAPTURE_DIR")))
                StartCoroutine(CaptureAfterRender());
        }

        private System.Collections.IEnumerator CaptureAfterRender()
        {
            if (!Application.isEditor)
            {
                for (int i = 0; i < 300 && (Screen.width != _requestedResolution.x || Screen.height != _requestedResolution.y); i++)
                    yield return null;
            }
            if (_previewStage == "menu") yield return new WaitForSecondsRealtime(0.9f);
            yield return new WaitForEndOfFrame();
            CapturePreviewFrame();
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

    }
}
