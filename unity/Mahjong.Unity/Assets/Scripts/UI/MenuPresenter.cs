#nullable enable
using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using TMPro;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Mahjong.Unity.UI
{
    public sealed class MenuPresenter : MonoBehaviour
    {
        [SerializeField] private Button? _playButton;
        [SerializeField] private Button? _quitButton;
        [SerializeField] private TextMeshProUGUI? _titleText;
        [SerializeField] private ModalPresenter? _quitModal;
        [SerializeField] private bool _localLiteMode;

        private IAppNavigation? _navigation;
        private FocusRouter _focusRouter = new FocusRouter();
        private Button? _testButton;
        private readonly List<Button> _scenarioButtons = new List<Button>();
        private readonly List<Button> _characterButtons = new List<Button>();
        private string _selectedCharacterId = "player";
        private bool _scenarioListOpen;
        private bool _starting;
        private bool _started;
        private bool _liteConfigured;

        public void Initialize(IAppNavigation navigation) => Initialize(navigation, false);

        public void Initialize(IAppNavigation navigation, bool localLiteMode)
        {
            _navigation = navigation;
            _localLiteMode = localLiteMode;
            if (_started && _localLiteMode && !_liteConfigured)
            {
                ConfigureLiteMenu();
            }
        }

        public void ShowLiteError(string error)
        {
            LocalLiteRunContext.SetPublicError(error);
            if (_titleText != null) _titleText.text = "G2 Local Lite: " + error;
        }

        private void Start()
        {
            _started = true;
            Debug.Log("MENU_PRESENTER_STARTED");
            if (_localLiteMode && !_liteConfigured) ConfigureLiteMenu();
            if (_playButton != null)
            {
                _playButton.onClick.AddListener(OnPlayClicked);
            }

            if (_quitButton != null)
            {
                _quitButton.onClick.AddListener(OnQuitClicked);
            }

            var selectables = new System.Collections.Generic.List<Selectable>();
            if (_playButton != null) selectables.Add(_playButton);
            if (_quitButton != null) selectables.Add(_quitButton);
            _focusRouter.RegisterSelectables(selectables);

            if (_playButton != null)
            {
                if (UnityEngine.EventSystems.EventSystem.current != null)
                {
                    UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(_playButton.gameObject);
                }
                else
                {
                    _playButton.Select();
                }
            }
        }

        private void Update()
        {
            if (!UiInputLifecycle.Instance.AcceptingInput)
            {
                return;
            }

            if (Keyboard.current != null && (_quitModal == null || !_quitModal.IsOpen))
            {
                var kb = Keyboard.current;
                bool shift = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
                if (kb.tabKey.wasPressedThisFrame)
                {
                    if (shift)
                    {
                        _focusRouter.FocusPrevious();
                    }
                    else
                    {
                        _focusRouter.FocusNext();
                    }
                }
            }
        }

        private void OnDestroy()
        {
            if (_playButton != null) _playButton.onClick.RemoveListener(OnPlayClicked);
            if (_quitButton != null) _quitButton.onClick.RemoveListener(OnQuitClicked);
            if (_testButton != null) _testButton.onClick.RemoveListener(OnTestClicked);
            foreach (Button button in _scenarioButtons) Destroy(button.gameObject);
            _scenarioButtons.Clear();
            foreach (Button button in _characterButtons) Destroy(button.gameObject);
            _characterButtons.Clear();
            if (_testButton != null) Destroy(_testButton.gameObject);
        }

        public void OnPlayClicked()
        {
            if (_quitModal != null && _quitModal.IsOpen)
            {
                return;
            }
            if (!UiInputLifecycle.Instance.ConsumeClickOrSubmit(_playButton != null ? _playButton.gameObject : null))
            {
                return;
            }
            Debug.Log("PLAY_BUTTON_CLICKED: Opening lobby...");
            if (_localLiteMode)
            {
                _ = StartLiteGameAsync(null, _selectedCharacterId);
                return;
            }
            if (_navigation != null)
            {
                _navigation.OpenLobby();
            }
        }

        private void ConfigureLiteMenu()
        {
            if (_liteConfigured) return;
            _liteConfigured = true;
            if (_titleText != null) _titleText.text = "G2 Local Lite v" + Application.version;
            SetLabel(_playButton, "开始一局");
            SetLabel(_quitButton, "退出");
            if (_playButton == null) return;

            RectTransform playRect = _playButton.GetComponent<RectTransform>();
            if (playRect != null) playRect.anchoredPosition = new Vector2(0f, 16f);
            if (_quitButton != null)
            {
                RectTransform quitRect = _quitButton.GetComponent<RectTransform>();
                if (quitRect != null) quitRect.anchoredPosition = new Vector2(0f, -184f);
            }

            _testButton = Instantiate(_playButton, _playButton.transform.parent);
            _testButton.name = "LocalLiteTestButton";
            _testButton.onClick.RemoveAllListeners();
            _testButton.onClick.AddListener(OnTestClicked);
            if (!SetWindowsSystemFontLabel(_testButton, "\u6D4B\u8BD5\u5C40\u9762"))
                SetLabel(_testButton, "Test cases");
            RectTransform testRect = _testButton.GetComponent<RectTransform>();
            if (testRect != null) testRect.anchoredPosition = new Vector2(0f, -84f);
            _testButton.transform.SetSiblingIndex(_playButton.transform.GetSiblingIndex() + 1);

            string[] characterIds = { "player", "octopus" };
            string[] characterLabels = { "\u724C\u624B", "\u516B\u722A\u9C7C" };
            for (int i = 0; i < characterIds.Length; i++)
            {
                int index = i;
                Button option = Instantiate(_playButton, _playButton.transform.parent);
                option.name = "LocalLiteCharacter-" + characterIds[i];
                option.onClick.RemoveAllListeners();
                option.onClick.AddListener(() => OnCharacterClicked(characterIds[index]));
                RectTransform rect = option.GetComponent<RectTransform>();
                if (rect != null)
                {
                    rect.sizeDelta = new Vector2(190f, 56f);
                    rect.anchoredPosition = new Vector2((i == 0 ? -1f : 1f) * 105f, 100f);
                }
                if (!SetWindowsSystemFontLabel(option, characterLabels[i]))
                    SetLabel(option, i == 0 ? "Player" : "Octopus");
                _characterButtons.Add(option);
            }
            UpdateCharacterLabels();

            string[] ids = { "pon", "chi", "riichi", "closed-kan", "dealer-tsumo" };
            string[] labels = { "碰", "吃", "立直", "暗杠", "庄家自摸" };
            for (int i = 0; i < ids.Length; i++)
            {
                int index = i;
                Button option = Instantiate(_playButton, _playButton.transform.parent);
                option.name = "LocalLiteScenario-" + ids[i];
                option.onClick.RemoveAllListeners();
                option.onClick.AddListener(() => OnScenarioClicked(ids[index]));
                RectTransform rect = option.GetComponent<RectTransform>();
                if (rect != null)
                {
                    rect.sizeDelta = new Vector2(220f, 44f);
                    rect.anchoredPosition = new Vector2(335f, 100f - 52f * i);
                }
                if (!SetWindowsSystemFontLabel(option, labels[i]))
                    SetLabel(option, new[] { "Pon", "Chi", "Riichi", "Closed kan", "Dealer tsumo" }[i]);
                option.gameObject.SetActive(false);
                _scenarioButtons.Add(option);
            }
            bool fontReady = SetWindowsSystemFontLabel(_playButton, "\u5F00\u59CB\u4E00\u5C40");
            if (!fontReady) SetLabel(_playButton, "New game");
            if (_quitButton != null && !SetWindowsSystemFontLabel(_quitButton, "\u9000\u51FA"))
                SetLabel(_quitButton, "Exit");
            if (_titleText != null)
                _titleText.text = LocalLiteRunContext.LastFailure ?? LocalLiteRunContext.LastPublicError ??
                    (!fontReady ? "Windows Chinese font unavailable; English labels are active" : "G2 Local Lite v" + Application.version);
        }

        private static void SetLabel(Button? button, string label)
        {
            TMP_Text? text = button != null ? button.GetComponentInChildren<TMP_Text>(true) : null;
            if (text == null) return;
            text.text = label;
            text.enabled = true;
            Transform? fallback = text.transform.Find("LocalLiteSystemFontLabel");
            if (fallback != null)
            {
                fallback.gameObject.SetActive(false);
                UnityEngine.UI.Text? fallbackText = fallback.GetComponent<UnityEngine.UI.Text>();
                if (fallbackText != null) fallbackText.enabled = false;
            }
        }

        private static bool SetWindowsSystemFontLabel(Button button, string label)
        {
            TMP_Text? source = button.GetComponentInChildren<TMP_Text>(true);
            if (source == null) return false;
            source.text = label;
            source.enabled = true;
            Transform? existing = source.transform.Find("LocalLiteSystemFontLabel");
            int sourceSize = Mathf.Max(1, Mathf.RoundToInt(source.fontSize));
            RectTransform? buttonRect = button.GetComponent<RectTransform>();
            int size = buttonRect != null && buttonRect.sizeDelta.y > 0f && buttonRect.sizeDelta.y < 60f
                ? Mathf.Min(sourceSize, 24)
                : sourceSize;
            Font? font = FontFallbackResolver.CreateWindowsSystemFont(label, size);
            if (font == null)
            {
                if (existing != null)
                {
                    existing.gameObject.SetActive(false);
                    UnityEngine.UI.Text? existingText = existing.GetComponent<UnityEngine.UI.Text>();
                    if (existingText != null) existingText.enabled = false;
                }
                return false;
            }

            source.enabled = false;
            GameObject labelObject;
            if (existing != null)
            {
                labelObject = existing.gameObject;
                labelObject.SetActive(true);
            }
            else
            {
                labelObject = new GameObject("LocalLiteSystemFontLabel", typeof(RectTransform), typeof(UnityEngine.UI.Text));
                labelObject.transform.SetParent(source.transform, false);
            }
            RectTransform rect = (RectTransform)labelObject.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            UnityEngine.UI.Text text = labelObject.GetComponent<UnityEngine.UI.Text>() ?? labelObject.AddComponent<UnityEngine.UI.Text>();
            text.font = font;
            text.fontSize = size;
            text.color = source.color;
            text.alignment = TextAnchor.MiddleCenter;
            text.raycastTarget = false;
            text.text = label;
            text.enabled = true;
            return true;
        }

        private void OnTestClicked()
        {
            if (_starting || !UiInputLifecycle.Instance.ConsumeClickOrSubmit(_testButton != null ? _testButton.gameObject : null)) return;
            _scenarioListOpen = !_scenarioListOpen;
            foreach (Button button in _scenarioButtons) button.gameObject.SetActive(_scenarioListOpen);
            if (_titleText != null) _titleText.text = _scenarioListOpen ? "Select a test case" : "G2 Local Lite v" + Application.version;
        }

        private void OnScenarioClicked(string scenarioId)
        {
            if (_starting || !UiInputLifecycle.Instance.ConsumeClickOrSubmit(EventSystemTarget(scenarioId))) return;
            _ = StartLiteGameAsync(scenarioId, _selectedCharacterId);
        }

        private void OnCharacterClicked(string characterId)
        {
            Button? button = _characterButtons.Find(value => value.name == "LocalLiteCharacter-" + characterId);
            if (_starting || !UiInputLifecycle.Instance.ConsumeClickOrSubmit(button != null ? button.gameObject : null)) return;
            _selectedCharacterId = characterId == "octopus" ? "octopus" : "player";
            UpdateCharacterLabels();
        }

        private void UpdateCharacterLabels()
        {
            string[] ids = { "player", "octopus" };
            string[] labels = { "\u724C\u624B", "\u516B\u722A\u9C7C" };
            for (int i = 0; i < _characterButtons.Count; i++)
            {
                bool selected = ids[i] == _selectedCharacterId;
                if (!SetWindowsSystemFontLabel(_characterButtons[i], (selected ? "\u2713 " : string.Empty) + labels[i]))
                    SetLabel(_characterButtons[i], (selected ? "> " : string.Empty) + (i == 0 ? "Player" : "Octopus"));
            }
        }

        private GameObject? EventSystemTarget(string scenarioId)
        {
            foreach (Button button in _scenarioButtons)
                if (button.name == "LocalLiteScenario-" + scenarioId) return button.gameObject;
            return null;
        }

        private async Task StartLiteGameAsync(string? scenarioId, string characterId = "player")
        {
            if (_starting) return;
            if (_navigation is not ILocalLiteNavigation liteNavigation)
            {
                if (_titleText != null) _titleText.text = "Local table unavailable";
                return;
            }

            _starting = true;
            LocalLiteRunContext.RecordStage(scenarioId, "starting");
            SetLiteButtonsInteractable(false);
            if (_titleText != null) _titleText.text = scenarioId == null ? "Creating a new round..." : "Preparing test case...";
            try
            {
                LocalGameStartResult result = await liteNavigation.StartLocalGameAsync(scenarioId, characterId);
                if (result.Status == LocalGameStartStatus.Started)
                {
                LocalLiteRunContext.RecordStage(scenarioId, "table");
                    return;
                }
                LocalLiteRunContext.RecordStage(scenarioId, "failed-" + (result.StableErrorCode ?? result.Status.ToString()));
                if (_titleText != null)
                    _titleText.text = result.IsTerminal ? "Close failed; start stopped" :
                        "Start failed: " + (result.StableErrorCode ?? result.Status.ToString());
                Debug.LogWarning("LOCAL_LITE_START_FAILED: " + (result.StableErrorCode ?? result.Status.ToString()));
                if (result.IsTerminal) return;
            }
            catch (Exception ex)
            {
                if (_titleText != null) _titleText.text = "Start failed: LOCAL_GAME_START_FAILED";
                Debug.LogWarning("LOCAL_LITE_START_FAILED: LOCAL_GAME_START_FAILED " + ex.GetType().Name);
            }
            _starting = false;
            SetLiteButtonsInteractable(true);
        }

        private void SetLiteButtonsInteractable(bool value)
        {
            if (_playButton != null) _playButton.interactable = value;
            if (_testButton != null) _testButton.interactable = value;
            if (_quitButton != null) _quitButton.interactable = value;
            foreach (Button button in _scenarioButtons) button.interactable = value;
            foreach (Button button in _characterButtons) button.interactable = value;
        }

        public void OnQuitClicked()
        {
            if (_quitModal != null && _quitModal.IsOpen)
            {
                return;
            }
            if (!UiInputLifecycle.Instance.ConsumeClickOrSubmit(_quitButton != null ? _quitButton.gameObject : null))
            {
                return;
            }
            Debug.Log("QUIT_BUTTON_CLICKED: Showing quit modal...");
            if (_quitModal != null)
            {
                _quitModal.Show(
                    "退出游戏",
                    "确定要退出千术麻将吗？",
                    confirmText: "确定",
                    cancelText: "取消",
                    onConfirm: () =>
                    {
                        Debug.Log("QUIT_CONFIRMED: Application quit confirmed by user.");
                        _navigation?.RequestQuit();
                    },
                    onCancel: null);
            }
            else
            {
                _navigation?.RequestQuit();
            }
        }
    }
}
