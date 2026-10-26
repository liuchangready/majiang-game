#nullable enable
using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;

namespace Mahjong.Unity.UI
{
    public sealed class ModalPresenter : MonoBehaviour
    {
        [SerializeField] private GameObject? _modalRoot;
        [SerializeField] private TextMeshProUGUI? _titleText;
        [SerializeField] private TextMeshProUGUI? _messageText;
        [SerializeField] private Button? _confirmButton;
        [SerializeField] private Button? _cancelButton;
        [SerializeField] private TextMeshProUGUI? _confirmButtonText;
        [SerializeField] private TextMeshProUGUI? _cancelButtonText;

        private Action? _onConfirm;
        private Action? _onCancel;
        private BackRouter? _backRouter;
        private bool _isBackRegistered;
        private GameObject? _previousSelectedGameObject;

        public bool IsOpen => _modalRoot != null && _modalRoot.activeSelf;

        public void SetBackRouter(BackRouter router)
        {
            _backRouter = router;
        }

        public void ShowConfirmation(string title, string message, Action onConfirm, Action onCancel)
        {
            Show(title, message, "确定", "取消", onConfirm, onCancel);
        }

        public void Show(
            string title,
            string message,
            string confirmText = "确定",
            string cancelText = "取消",
            Action? onConfirm = null,
            Action? onCancel = null)
        {
            _onConfirm = onConfirm;
            _onCancel = onCancel;

            if (!IsOpen)
            {
                _previousSelectedGameObject = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            }

            if (_titleText != null) _titleText.text = title;
            if (_messageText != null) _messageText.text = message;
            if (_confirmButtonText != null) _confirmButtonText.text = confirmText;
            if (_cancelButtonText != null) _cancelButtonText.text = cancelText;

            if (_confirmButton != null)
            {
                _confirmButton.onClick.RemoveAllListeners();
                _confirmButton.onClick.AddListener(OnConfirmClicked);
            }

            if (_cancelButton != null)
            {
                _cancelButton.onClick.RemoveAllListeners();
                _cancelButton.onClick.AddListener(OnCancelClicked);
            }

            if (_modalRoot != null)
            {
                _modalRoot.SetActive(true);
            }

            var focusTarget = _cancelButton != null ? _cancelButton.gameObject : _confirmButton?.gameObject;
            if (focusTarget != null && EventSystem.current != null)
            {
                EventSystem.current.SetSelectedGameObject(focusTarget);
            }

            if (!_isBackRegistered && _backRouter != null)
            {
                _backRouter.RegisterModal(Close);
                _isBackRegistered = true;
            }
        }

        public void Close()
        {
            if (_modalRoot != null)
            {
                _modalRoot.SetActive(false);
            }
            if (_isBackRegistered && _backRouter != null)
            {
                _backRouter.UnregisterModal(Close);
                _isBackRegistered = false;
            }
            RestorePreviousFocus();
            _onCancel?.Invoke();
        }

        public void Dismiss() => Close();

        private void OnConfirmClicked()
        {
            if (!UiInputLifecycle.Instance.ConsumeClickOrSubmit(_confirmButton != null ? _confirmButton.gameObject : null))
            {
                return;
            }

            if (_modalRoot != null)
            {
                _modalRoot.SetActive(false);
            }
            if (_isBackRegistered && _backRouter != null)
            {
                _backRouter.UnregisterModal(Close);
                _isBackRegistered = false;
            }
            RestorePreviousFocus();
            _onConfirm?.Invoke();
        }

        private void OnCancelClicked()
        {
            if (!UiInputLifecycle.Instance.ConsumeClickOrSubmit(_cancelButton != null ? _cancelButton.gameObject : null))
            {
                return;
            }
            Close();
        }

        private void RestorePreviousFocus()
        {
            if (_previousSelectedGameObject != null && EventSystem.current != null && _previousSelectedGameObject.activeInHierarchy)
            {
                EventSystem.current.SetSelectedGameObject(_previousSelectedGameObject);
            }
            _previousSelectedGameObject = null;
        }

        private void OnDestroy()
        {
            if (_isBackRegistered && _backRouter != null)
            {
                _backRouter.UnregisterModal(Close);
                _isBackRegistered = false;
            }
            RestorePreviousFocus();
        }
    }
}
