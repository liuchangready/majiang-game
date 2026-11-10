#nullable enable
using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using TMPro;

namespace Mahjong.Unity.UI
{
    public sealed class MenuPresenter : MonoBehaviour
    {
        [SerializeField] private Button? _playButton;
        [SerializeField] private Button? _quitButton;
        [SerializeField] private TextMeshProUGUI? _titleText;
        [SerializeField] private ModalPresenter? _quitModal;

        private IAppNavigation? _navigation;
        private FocusRouter _focusRouter = new FocusRouter();
        private Button? _recordsButton;
        private ILocalRecordReplayController? _recordsPresenter;

        public void Initialize(IAppNavigation navigation, ILocalRecordReplayController? recordsPresenter = null)
        {
            _navigation = navigation;
            _recordsPresenter = recordsPresenter;
        }

        private void Start()
        {
            Debug.Log("MENU_PRESENTER_STARTED");
            if (_playButton != null)
            {
                _playButton.onClick.AddListener(OnPlayClicked);
            }

            if (_quitButton != null)
            {
                _quitButton.onClick.AddListener(OnQuitClicked);
            }

            if (_playButton != null)
            {
                _recordsButton = Instantiate(_playButton, _playButton.transform.parent);
                _recordsButton.name = "MatchRecordsButton";
                RectTransform rect = _recordsButton.GetComponent<RectTransform>();
                RectTransform? quitRect = _quitButton != null ? _quitButton.GetComponent<RectTransform>() : null;
                float recordsY = quitRect != null
                    ? (_playButton.GetComponent<RectTransform>().anchoredPosition.y + quitRect.anchoredPosition.y) * 0.5f
                    : _playButton.GetComponent<RectTransform>().anchoredPosition.y - 100;
                float gap = 112;
                rect.anchoredPosition = new Vector2(0, recordsY);
                _playButton.GetComponent<RectTransform>().anchoredPosition = new Vector2(0, recordsY + gap);
                if (quitRect != null) quitRect.anchoredPosition = new Vector2(0, recordsY - gap);
                var label = _recordsButton.GetComponentInChildren<TextMeshProUGUI>();
                if (label != null) label.text = "牌局记录";
                if (_recordsButton.GetComponent<UiPointerLifecycleTrigger>() == null)
                    _recordsButton.gameObject.AddComponent<UiPointerLifecycleTrigger>();
                _recordsButton.onClick.RemoveAllListeners();
                _recordsButton.onClick.AddListener(OnRecordsClicked);
            }

            var selectables = new System.Collections.Generic.List<Selectable>();
            if (_playButton != null) selectables.Add(_playButton);
            if (_recordsButton != null) selectables.Add(_recordsButton);
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

            if (_recordsPresenter?.IsOpen == true) return;

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
            if (_recordsButton != null) _recordsButton.onClick.RemoveListener(OnRecordsClicked);
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
            if (_navigation != null)
            {
                _navigation.OpenLobby();
            }
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

        private void OnRecordsClicked()
        {
            if (_quitModal != null && _quitModal.IsOpen) return;
            if (_recordsPresenter?.IsOpen == true) return;
            if (!UiInputLifecycle.Instance.ConsumeClickOrSubmit(_recordsButton != null ? _recordsButton.gameObject : null)) return;
            _recordsPresenter?.Open();
        }
    }
}
