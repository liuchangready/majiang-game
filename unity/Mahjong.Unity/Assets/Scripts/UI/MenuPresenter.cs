using System;
using UnityEngine;
using UnityEngine.UI;
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

        public void Initialize(IAppNavigation navigation)
        {
            _navigation = navigation;
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

        private void OnDestroy()
        {
            if (_playButton != null) _playButton.onClick.RemoveListener(OnPlayClicked);
            if (_quitButton != null) _quitButton.onClick.RemoveListener(OnQuitClicked);
        }

        public void OnPlayClicked()
        {
            if (!UiInputLifecycle.Instance.ConsumeClickOrSubmit())
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
            if (!UiInputLifecycle.Instance.ConsumeClickOrSubmit())
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
                    onConfirm: () => _navigation?.RequestQuit(),
                    onCancel: null);
            }
            else
            {
                _navigation?.RequestQuit();
            }
        }
    }
}
