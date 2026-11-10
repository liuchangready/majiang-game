#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Mahjong.LocalHost;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using Mahjong.Unity.UI;

namespace Mahjong.Unity.App
{
    /// <summary>Player-facing read-only browser for the local host's committed round journal.</summary>
    public sealed class LocalRecordReplayPresenter : MonoBehaviour, ILocalRecordReplayController
    {
        private readonly List<GameObject> _rows = new List<GameObject>();
        private readonly Dictionary<string, LocalRecordedRound> _records = new Dictionary<string, LocalRecordedRound>(StringComparer.Ordinal);
        private Canvas? _canvas;
        private RectTransform? _list;
        private TextMeshProUGUI? _status;
        private TextMeshProUGUI? _timeline;
        private TMP_FontAsset? _font;
        private IReadOnlyList<LocalRecordedEvent> _events = Array.Empty<LocalRecordedEvent>();
        private int _cursor;
        private float _interval = 0.8f;
        private float _elapsed;
        private bool _playing;
        private bool _backRegistered;
        private BackRouter? _backRouter;
        private Button? _playPauseButton;
        private Button? _speedButton;
        private Button? _closeButton;
        private int _speedLevel = 1;
        private int _loadGeneration;
        private GameObject? _previousSelected;

        public bool IsOpen => _canvas != null && _canvas.gameObject.activeSelf;

        public void Initialize()
        {
            var title = FindFirstObjectByType<TextMeshProUGUI>();
            _font = title != null ? title.font : TMP_Settings.defaultFontAsset;
            Build();
            _canvas!.gameObject.SetActive(false);
            _backRouter = AppRoot.Instance?.BackRouter;
        }

        public void Open()
        {
            _previousSelected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            _canvas!.gameObject.SetActive(true);
            if (EventSystem.current != null && _closeButton != null)
                EventSystem.current.SetSelectedGameObject(_closeButton.gameObject);
            if (!_backRegistered && _backRouter != null)
            {
                _backRouter.RegisterModal(Close);
                _backRegistered = true;
            }
            _playing = false;
            _cursor = 0;
            _events = Array.Empty<LocalRecordedEvent>();
            _records.Clear();
            _timeline!.text = "选择一局开始回放。";
            _status!.text = "读取中…";
            _ = LoadAsync(++_loadGeneration);
        }

        private async Task LoadAsync(int generation)
        {
            try
            {
                string root = AppRoot.Instance!.Composition.Paths.LocalSaveDirectory;
                IReadOnlyList<LocalRecordedRound> records = await Task.Run(() => LocalRecordedRounds.List(root, 20));
                if (this == null || generation != _loadGeneration) return;
                foreach (GameObject row in _rows) Destroy(row);
                _rows.Clear();
                _records.Clear();
                foreach (LocalRecordedRound record in records)
                {
                    string id = record.Id;
                    _records[id] = record;
                    AddButton(_list!, $"{record.RecordedAt.ToLocalTime():yyyy-MM-dd HH:mm}  ·  {record.RoundId}  ·  {record.Result}", () => Select(id));
                }
                _status!.text = records.Count == 0 ? "暂无牌局记录" : $"最近 {records.Count} 局";
                GameObject focus = _rows.Count > 0 ? _rows[0] : _closeButton!.gameObject;
                if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(focus);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"LOCAL_RECORD_LIST_FAILED: {exception.GetType().Name}");
                if (this != null && generation == _loadGeneration) _status!.text = "读取失败，请稍后重试。";
            }
        }

        private void Select(string id)
        {
            try
            {
                if (!_records.TryGetValue(id, out LocalRecordedRound? record)) { _status!.text = "记录已不可用，请重新打开列表。"; return; }
                _events = record.Events;
                _cursor = 0;
                _playing = false;
                SetButtonText(_playPauseButton, "播放");
                _status!.text = $"{record.RoundId} · {record.Result} · {_events.Count} 个公开事件";
                ShowCurrent();
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"LOCAL_RECORD_READ_FAILED: {exception.GetType().Name}");
                _status!.text = "回放读取失败。";
            }
        }

        private void Close()
        {
            _loadGeneration++;
            _playing = false;
            if (_backRegistered && _backRouter != null)
            {
                _backRouter.UnregisterModal(Close);
                _backRegistered = false;
            }
            _canvas!.gameObject.SetActive(false);
            if (_previousSelected != null && _previousSelected.activeInHierarchy && EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(_previousSelected);
            _previousSelected = null;
        }

        private void Update()
        {
            if (!_playing || !UiInputLifecycle.Instance.AcceptingInput) return;
            _elapsed += Time.unscaledDeltaTime;
            if (_elapsed >= _interval)
            {
                _elapsed = 0;
                Advance();
            }
        }

        private void Advance()
        {
            if (_cursor < _events.Count) _cursor++;
            ShowCurrent();
            if (_cursor >= _events.Count)
            {
                _playing = false;
                SetButtonText(_playPauseButton, "播放");
            }
        }

        private void ShowCurrent()
        {
            if (_events.Count == 0) { _timeline!.text = "这局没有可展示的公开事件。"; return; }
            if (_cursor == 0) { _timeline!.text = "回放开始"; return; }
            LocalRecordedEvent item = _events[_cursor - 1];
            string tile = item.PublicTile.HasValue ? $" · 牌 {item.PublicTile.Value / 4}" : string.Empty;
            _timeline!.text = $"{_cursor}/{_events.Count}  第 {item.Sequence} 项\n{EventName(item.TypeCode)} · {SeatName(item.Seat)}{tile}";
        }

        private void Build()
        {
            var canvasObject = new GameObject("LocalRecordReplayCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(transform, false);
            _canvas = canvasObject.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 100;
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            var panel = Rect(canvasObject.transform, "Panel", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(1040, 800));
            Image(panel.gameObject, new Color(0.04f, 0.06f, 0.1f, 0.97f));
            Text(panel, "牌局记录", new Vector2(0, 354), new Vector2(800, 48), 30, TextAlignmentOptions.Center);
            _status = Text(panel, "读取中…", new Vector2(0, 310), new Vector2(900, 38), 20, TextAlignmentOptions.Center);
            var viewport = Rect(panel, "Viewport", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(940, 390));
            viewport.anchoredPosition = new Vector2(0, 65);
            Image(viewport.gameObject, new Color(0, 0, 0, 0.18f));
            viewport.gameObject.AddComponent<RectMask2D>();
            var content = Rect(viewport, "Content", new Vector2(0, 1), new Vector2(1, 1), Vector2.zero);
            content.pivot = new Vector2(0.5f, 1);
            var layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 5;
            layout.padding = new RectOffset(8, 8, 8, 8);
            layout.childControlHeight = true;
            layout.childForceExpandHeight = false;
            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var scroll = viewport.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = viewport;
            scroll.content = content;
            scroll.vertical = true;
            _list = content;
            _timeline = Text(panel, "选择一局开始回放。", new Vector2(0, -190), new Vector2(900, 88), 22, TextAlignmentOptions.Center);
            _playPauseButton = AddButton(panel, "播放", () =>
            {
                _playing = !_playing;
                SetButtonText(_playPauseButton, _playing ? "暂停" : "播放");
            }, new Vector2(-330, -300));
            AddButton(panel, "单步", Advance, new Vector2(-110, -300));
            _speedButton = AddButton(panel, "速度 1×", () =>
            {
                _speedLevel = _speedLevel % 3 + 1;
                _interval = _speedLevel == 1 ? 0.8f : _speedLevel == 2 ? 0.4f : 0.2f;
                SetButtonText(_speedButton, "速度 " + _speedLevel + "×");
            }, new Vector2(110, -300));
            _closeButton = AddButton(panel, "关闭", Close, new Vector2(330, -300));
        }

        private Button AddButton(RectTransform parent, string label, Action action, Vector2? position = null)
        {
            var rect = Rect(parent, "RecordButton", new Vector2(0.5f, position.HasValue ? 0 : 1), new Vector2(0.5f, position.HasValue ? 0 : 1),
                new Vector2(920, position.HasValue ? 54 : 46));
            if (position.HasValue) rect.anchoredPosition = position.Value;
            else _rows.Add(rect.gameObject);
            Image image = Image(rect.gameObject, new Color(0.16f, 0.27f, 0.42f, 1));
            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(() =>
            {
                if (UiInputLifecycle.Instance.ConsumeClickOrSubmit(rect.gameObject)) action();
            });
            var labelRect = Rect(rect, "Label", Vector2.zero, Vector2.one, Vector2.zero);
            var text = labelRect.gameObject.AddComponent<TextMeshProUGUI>();
            text.font = _font;
            text.text = label;
            text.fontSize = position.HasValue ? 21 : 18;
            text.alignment = TextAlignmentOptions.Center;
            text.color = Color.white;
            if (!position.HasValue)
            {
                var fit = rect.gameObject.AddComponent<LayoutElement>();
                fit.preferredHeight = 46;
            }
            return button;
        }

        private static void SetButtonText(Button? button, string value)
        {
            var label = button != null ? button.GetComponentInChildren<TextMeshProUGUI>() : null;
            if (label != null) label.text = value;
        }

        private TextMeshProUGUI Text(RectTransform parent, string value, Vector2 position, Vector2 size, float fontSize, TextAlignmentOptions alignment)
        {
            var rect = Rect(parent, "Text", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), size);
            rect.anchoredPosition = position;
            var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            text.font = _font;
            text.text = value;
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = Color.white;
            return text;
        }

        private static RectTransform Rect(Transform parent, string name, Vector2 min, Vector2 max, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = min;
            rect.anchorMax = max;
            rect.sizeDelta = size;
            return rect;
        }

        private static Image Image(GameObject go, Color color)
        {
            var image = go.AddComponent<Image>();
            image.color = color;
            return image;
        }

        private static string SeatName(int seat) => seat < 0 ? "全场" : $"{seat + 1}号位";
        private static string EventName(string type) => type switch
        {
            "RoundStarted" => "牌局开始",
            "TilesDealt" => "发牌",
            "DoraRevealed" => "翻开宝牌",
            "TileDrawn" => "摸牌",
            "RinshanDrawn" => "岭上摸牌",
            "TileDiscarded" => "打出牌",
            "ResponseWindowOpened" => "进入响应",
            "ChankanWindowOpened" => "进入抢杠响应",
            "ResponseClaimed" => "响应完成",
            "RiichiDeclared" => "宣布立直",
            "ChiDeclared" => "吃牌",
            "PonDeclared" => "碰牌",
            "KanDeclared" => "杠牌",
            "TsumoWin" => "自摸和牌",
            "RonWin" => "荣和",
            "ExhaustiveDraw" => "荒牌流局",
            "AbortiveDraw" => "途中流局",
            "RoundEnded" => "本局结束",
            _ => "牌局事件"
        };
    }
}
