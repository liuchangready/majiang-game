using System;
using System.Collections.Generic;
using UnityEngine;

namespace Mahjong.Unity.UI
{
    public enum BackEventSource
    {
        DesktopEscape = 0,
        AndroidPlatformBack = 1
    }

    public enum BackRouteResult
    {
        Consumed = 0,
        Ignored = 1,
        RejectedWrongSource = 2
    }

    public sealed class BackRouter
    {
        private readonly Stack<Action> _modalStack = new Stack<Action>();
        private Func<bool>? _isTransitioning;
        private Func<bool>? _isPaused;
        private Action? _sceneBackAction;
        private Action? _menuQuitAction;
        private bool _isBootScene;

        public void RegisterModal(Action closeAction)
        {
            if (closeAction == null) throw new ArgumentNullException(nameof(closeAction));
            _modalStack.Push(closeAction);
        }

        public void UnregisterModal(Action closeAction)
        {
            if (_modalStack.Count > 0 && _modalStack.Peek() == closeAction)
            {
                _modalStack.Pop();
            }
        }

        public void SetTransitionGate(Func<bool> isTransitioning)
        {
            _isTransitioning = isTransitioning;
        }

        public void SetPauseGate(Func<bool> isPaused)
        {
            _isPaused = isPaused;
        }

        public void SetSceneBackHandler(Action? sceneBackAction, bool isBoot = false)
        {
            _sceneBackAction = sceneBackAction;
            _isBootScene = isBoot;
        }

        public void SetMenuQuitHandler(Action? menuQuitAction)
        {
            _menuQuitAction = menuQuitAction;
        }

        public BackRouteResult HandleBack(BackEventSource source, bool isAndroidPlatform = false)
        {
            // Validate platform source
            if (isAndroidPlatform && source == BackEventSource.DesktopEscape)
            {
                return BackRouteResult.RejectedWrongSource;
            }
            if (!isAndroidPlatform && source == BackEventSource.AndroidPlatformBack)
            {
                return BackRouteResult.RejectedWrongSource;
            }

            // Priority 1: Topmost Modal
            if (_modalStack.Count > 0)
            {
                var closeAction = _modalStack.Pop();
                closeAction?.Invoke();
                return BackRouteResult.Consumed;
            }

            // Priority 2: Transitioning
            if (_isTransitioning != null && _isTransitioning())
            {
                return BackRouteResult.Consumed;
            }

            // Priority 3: Paused
            if (_isPaused != null && _isPaused())
            {
                return BackRouteResult.Consumed;
            }

            // Priority 4: Boot scene
            if (_isBootScene)
            {
                return BackRouteResult.Ignored;
            }

            // Priority 5: Current scene back
            if (_sceneBackAction != null)
            {
                _sceneBackAction.Invoke();
                return BackRouteResult.Consumed;
            }

            // Priority 6: Menu root quit
            if (_menuQuitAction != null)
            {
                _menuQuitAction.Invoke();
                return BackRouteResult.Consumed;
            }

            return BackRouteResult.Ignored;
        }
    }
}
