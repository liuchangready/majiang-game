#nullable enable
using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using TMPro;
using Mahjong.Unity.UI.Session;

namespace Mahjong.Unity.UI
{
    public sealed class LobbyPresenter : MonoBehaviour
    {
        [SerializeField] private Button? _startLocalGameButton;
        [SerializeField] private Button? _backButton;
        [SerializeField] private TextMeshProUGUI? _statusText;
        [SerializeField] private TextMeshProUGUI? _explanationText;
        [SerializeField] private ConnectionBannerPresenter? _connectionBanner;

        private IAppNavigation? _navigation;
        private ISessionView? _sessionView;
        private FocusRouter _focusRouter = new FocusRouter();
        private bool _destroyed;
        private bool _startInFlight;

        /// <summary>True while one start-local-game attempt is still in flight.</summary>
        public bool IsStartInFlight => _startInFlight;

        public void Initialize(IAppNavigation navigation, ISessionView? sessionView = null)
        {
            _navigation = navigation;
            _sessionView = sessionView;

            if (_sessionView != null)
            {
                _sessionView.Changed += OnSessionChanged;
                UpdateSessionState(_sessionView.Current);
            }
        }

        private void Start()
        {
            Debug.Log("LOBBY_PRESENTER_STARTED");
            if (_backButton != null)
            {
                _backButton.onClick.AddListener(OnBackClicked);
            }

            if (_startLocalGameButton != null)
            {
                _startLocalGameButton.onClick.AddListener(OnStartLocalGameClicked);
            }

            ApplyLocalGameStartCapability();

            var list = new System.Collections.Generic.List<Selectable>();
            if (_backButton != null) list.Add(_backButton);
            if (_startLocalGameButton != null) list.Add(_startLocalGameButton);

            var canvas = GetComponentInParent<Canvas>();
            if (canvas != null)
            {
                foreach (var s in canvas.GetComponentsInChildren<Selectable>(true))
                {
                    if (!list.Contains(s)) list.Add(s);
                }
            }
            else
            {
                foreach (var s in GetComponentsInChildren<Selectable>(true))
                {
                    if (!list.Contains(s)) list.Add(s);
                }
            }

            _focusRouter.RegisterSelectables(list);

            if (_backButton != null)
            {
                if (UnityEngine.EventSystems.EventSystem.current != null)
                {
                    UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(_backButton.gameObject);
                }
                else
                {
                    _backButton.Select();
                }
            }
        }

        private void OnDestroy()
        {
            _destroyed = true;
            if (_backButton != null) _backButton.onClick.RemoveListener(OnBackClicked);
            if (_startLocalGameButton != null) _startLocalGameButton.onClick.RemoveListener(OnStartLocalGameClicked);
            if (_sessionView != null) _sessionView.Changed -= OnSessionChanged;
        }

        /// <summary>
        /// Reflects the navigation's actual local-start capability instead of a hard-coded
        /// disabled state: unavailable factories keep the button disabled with the stable
        /// reason; capable factories enable it for real attempts.
        /// </summary>
        private void ApplyLocalGameStartCapability()
        {
            bool available = _navigation != null && _navigation.IsLocalGameStartAvailable;
            if (_startLocalGameButton != null)
            {
                _startLocalGameButton.interactable = available && !_startInFlight;
                var label = _startLocalGameButton.GetComponentInChildren<TMP_Text>(true);
                if (label != null)
                {
                    label.text = available ? "开始本地对局" : "开始本地对局 (不可用)";
                }
            }

            if (_statusText != null)
            {
                _statusText.text = available ? "本地对局可用" : "本地宿主未接入 (LOCAL_HOST_UNAVAILABLE)";
            }

            if (_explanationText != null)
            {
                _explanationText.text = available
                    ? "本地对局已就绪，点击开始后进入牌桌。"
                    : "本地对局通道尚未就绪 (LOCAL_HOST_UNAVAILABLE)。等待本地宿主接入。";
            }
        }

        private void Update()
        {
            if (Keyboard.current != null)
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

        private void OnSessionChanged(object? sender, SessionViewState state)
        {
            UpdateSessionState(state);
        }

        private void UpdateSessionState(SessionViewState state)
        {
            if (_connectionBanner != null)
            {
                _connectionBanner.UpdateState(state);
            }
        }

        public void OnBackClicked()
        {
            if (!UiInputLifecycle.Instance.ConsumeClickOrSubmit(_backButton != null ? _backButton.gameObject : null))
            {
                return;
            }
            Debug.Log("LOBBY_BACK_CLICKED: Returning to menu...");
            _navigation?.BackToMenu();
        }

        public void OnStartLocalGameClicked()
        {
            if (_destroyed || _startInFlight)
            {
                // Concurrent double-clicks create at most one open attempt.
                return;
            }

            if (!UiInputLifecycle.Instance.ConsumeClickOrSubmit(_startLocalGameButton != null ? _startLocalGameButton.gameObject : null))
            {
                return;
            }

            if (_navigation == null || !_navigation.IsLocalGameStartAvailable)
            {
                ApplyLocalGameStartCapability();
                return;
            }

            _startInFlight = true;
            if (_startLocalGameButton != null)
            {
                _startLocalGameButton.interactable = false;
            }
            if (_statusText != null)
            {
                _statusText.text = "正在打开本地对局…";
            }
            _ = StartLocalGameFlowAsync();
        }

        private async Task StartLocalGameFlowAsync()
        {
            LocalGameStartResult result;
            try
            {
                result = await _navigation!.StartLocalGameAsync();
            }
            catch (Exception)
            {
                result = LocalGameStartResult.Failed(LocalGameStartResult.UnexpectedFailureCode);
            }

            _startInFlight = false;
            if (_destroyed)
            {
                // Scene changed or presenter destroyed while opening; the navigation owns
                // any cleanup and must not observe late UI writes here.
                return;
            }

            switch (result.Status)
            {
                case LocalGameStartStatus.Started:
                case LocalGameStartStatus.Busy:
                case LocalGameStartStatus.Cancelled:
                    ApplyLocalGameStartCapability();
                    break;
                case LocalGameStartStatus.Failed:
                    if (_startLocalGameButton != null)
                    {
                        _startLocalGameButton.interactable = _navigation != null && _navigation.IsLocalGameStartAvailable;
                    }
                    if (_statusText != null)
                    {
                        _statusText.text = "本地对局打开失败 (" + result.StableErrorCode + ")，可重试";
                    }
                    if (_explanationText != null)
                    {
                        _explanationText.text = "打开未成功，可再次点击重试。错误码: " + result.StableErrorCode;
                    }
                    break;
            }
        }
    }
}
