using System;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Mahjong.Unity.App
{
    public sealed class AppLifecycle
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private bool _isPaused;
        private bool _isFocused = true;
        private bool _isQuitting;
        private long _pauseTimestampMs;

        public bool IsPaused => _isPaused;
        public bool IsFocused => _isFocused;
        public bool IsQuitting => _isQuitting;
        public long MonotonicMs => _stopwatch.ElapsedMilliseconds;
        public long UtcNowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public event Action<bool>? PauseChanged;
        public event Action<bool>? FocusChanged;
        public event Action? Quitting;

        public void HandleApplicationPause(bool pauseStatus)
        {
            if (_isPaused == pauseStatus) return;

            _isPaused = pauseStatus;
            if (_isPaused)
            {
                _pauseTimestampMs = MonotonicMs;
                Debug.Log($"APP_PAUSED at {_pauseTimestampMs} ms");
            }
            else
            {
                long resumeTime = MonotonicMs;
                long elapsedInPause = resumeTime - _pauseTimestampMs;
                Debug.Log($"APP_RESUMED after {elapsedInPause} ms");
            }

            PauseChanged?.Invoke(_isPaused);
        }

        public void HandleApplicationFocus(bool hasFocus)
        {
            if (_isFocused == hasFocus) return;

            _isFocused = hasFocus;
            Debug.Log($"APP_FOCUS_CHANGED: hasFocus={_isFocused}");
            FocusChanged?.Invoke(_isFocused);
        }

        public void HandleApplicationQuit()
        {
            if (_isQuitting) return;
            _isQuitting = true;
            Debug.Log("APP_QUIT_REQUESTED");
            Quitting?.Invoke();
        }
    }
}
