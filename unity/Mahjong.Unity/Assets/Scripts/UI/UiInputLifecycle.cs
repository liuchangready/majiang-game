#nullable enable
using System;
using UnityEngine;

namespace Mahjong.Unity.UI
{
    public sealed class UiInputLifecycle
    {
        private static UiInputLifecycle? _instance;
        public static UiInputLifecycle Instance => _instance ??= new UiInputLifecycle();

        public const float MaxDragThresholdDesignUnits = 8.0f;

        private bool _isArmed;
        private int? _primaryPointerId;
        private Vector2 _pointerDownPos;
        private Vector2 _lastDragPos;
        private float _accumulatedDragDistance;
        private long _pressGeneration;
        private bool _eligibleForClick;
        private bool _pointerUpCompleted;

        // Independent gate states
        private bool _isPaused;
        private bool _isTransitioning;
        private bool _isFocusLost;

        public bool IsArmed => _isArmed;
        public int? PrimaryPointerId => _primaryPointerId;
        public long PressGeneration => _pressGeneration;
        public bool IsPaused => _isPaused;
        public bool IsTransitioning => _isTransitioning;
        public bool IsFocusLost => _isFocusLost;

        public bool AcceptingInput => !_isPaused && !_isTransitioning && !_isFocusLost;

        public static void ResetForTest()
        {
            _instance = new UiInputLifecycle();
        }

        public bool TryArm(int pointerId, Vector2 screenPos, float scale = 1.0f)
        {
            _pointerUpCompleted = false;
            _eligibleForClick = false;
            if (!AcceptingInput)
            {
                return false;
            }

            // Only primary pointer can arm
            if (_isArmed)
            {
                // Multi-finger / secondary pointer ignored
                return false;
            }

            _isArmed = true;
            _primaryPointerId = pointerId;
            _pointerDownPos = screenPos;
            _lastDragPos = screenPos;
            _accumulatedDragDistance = 0f;
            return true;
        }

        public void UpdateDrag(int pointerId, Vector2 currentScreenPos, float scale = 1.0f)
        {
            if (!_isArmed || _primaryPointerId != pointerId)
            {
                return;
            }

            float delta = Vector2.Distance(_lastDragPos, currentScreenPos);
            _lastDragPos = currentScreenPos;
            float effectiveScale = scale > 0.001f ? scale : 1.0f;
            _accumulatedDragDistance += delta / effectiveScale;

            if (_accumulatedDragDistance > MaxDragThresholdDesignUnits)
            {
                // Drag threshold exceeded: cancel arm
                CancelArm();
            }
        }

        public bool TryRelease(int pointerId, long checkGeneration)
        {
            _pointerUpCompleted = true;
            if (!_isArmed || _primaryPointerId != pointerId)
            {
                _eligibleForClick = false;
                return false;
            }

            if (!AcceptingInput || checkGeneration != _pressGeneration)
            {
                CancelArm();
                _eligibleForClick = false;
                return false;
            }

            _isArmed = false;
            _primaryPointerId = null;
            _eligibleForClick = true;
            return true;
        }

        public bool ConsumeClickOrSubmit()
        {
            if (!AcceptingInput)
            {
                return false;
            }

            if (_eligibleForClick)
            {
                _eligibleForClick = false;
                _pointerUpCompleted = false;
                return true;
            }

            if (_pointerUpCompleted)
            {
                _pointerUpCompleted = false;
                return false;
            }

            return true;
        }

        public void CancelArm()
        {
            _isArmed = false;
            _primaryPointerId = null;
            _pressGeneration++;
            _accumulatedDragDistance = 0f;
            _eligibleForClick = false;
        }

        public void OnPause()
        {
            _isPaused = true;
            CancelArm();
        }

        public void OnResume()
        {
            _isPaused = false;
        }

        public void OnTransition()
        {
            _isTransitioning = true;
            CancelArm();
        }

        public void TransitionEnd()
        {
            _isTransitioning = false;
        }

        public void OnFocusLost()
        {
            _isFocusLost = true;
            CancelArm();
        }

        public void OnFocusGained()
        {
            _isFocusLost = false;
        }
    }
}
