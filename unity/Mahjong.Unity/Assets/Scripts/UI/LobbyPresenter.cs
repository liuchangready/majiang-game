#nullable enable
using System;
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
                // In P4.2-core, local host is unavailable until P4.1 is integrated in P4.2-B.
                // Explicitly disable the start button and explain state.
                _startLocalGameButton.interactable = false;
                _startLocalGameButton.onClick.AddListener(OnStartLocalGameClicked);
            }

            if (_statusText != null)
            {
                _statusText.text = "本地宿主未接入 (P4.1)";
            }

            if (_explanationText != null)
            {
                _explanationText.text = "本地对局通道尚未就绪 (LOCAL_HOST_UNAVAILABLE)。P4.2平台壳已就绪，等待P4.1本地工厂对接。";
            }

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

        private void OnDestroy()
        {
            if (_backButton != null) _backButton.onClick.RemoveListener(OnBackClicked);
            if (_startLocalGameButton != null) _startLocalGameButton.onClick.RemoveListener(OnStartLocalGameClicked);
            if (_sessionView != null) _sessionView.Changed -= OnSessionChanged;
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
            // Disabled in P4.2-core
        }
    }
}
