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

        private enum PointerCycleState
        {
            Idle,
            Armed,
            Released,
            Consumed,
            Cancelled
        }

        private bool _isArmed;
        private int? _primaryPointerId;
        private Vector2 _pointerDownPos;
        private Vector2 _lastDragPos;
        private float _accumulatedDragDistance;
        private long _pressGeneration;

        private PointerCycleState _pointerState = PointerCycleState.Idle;
        private int _consumedFrame = -1;
        private int _cancelledFrame = -1;

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
            _pressGeneration++;
            _pointerState = PointerCycleState.Armed;
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
            if (!_isArmed || _primaryPointerId != pointerId)
            {
                _pointerState = PointerCycleState.Cancelled;
                _cancelledFrame = Time.frameCount;
                return false;
            }

            if (!AcceptingInput || checkGeneration != _pressGeneration)
            {
                CancelArm();
                return false;
            }

            _isArmed = false;
            _primaryPointerId = null;
            _pointerState = PointerCycleState.Released;
            return true;
        }

        public bool ConsumeClickOrSubmit()
        {
            if (!AcceptingInput)
            {
                return false;
            }

            if (_pointerState == PointerCycleState.Released)
            {
                _pointerState = PointerCycleState.Consumed;
                _consumedFrame = Time.frameCount;
                return true;
            }

            if (_pointerState == PointerCycleState.Consumed)
            {
                if (Time.frameCount != _consumedFrame)
                {
                    _pointerState = PointerCycleState.Idle;
                    return true;
                }
                return false;
            }

            if (_pointerState == PointerCycleState.Cancelled)
            {
                if (Time.frameCount != _cancelledFrame)
                {
                    _pointerState = PointerCycleState.Idle;
                    return true;
                }
                return false;
            }

            if (_pointerState == PointerCycleState.Armed)
            {
                return false;
            }

            // PointerCycleState.Idle: keyboard/gamepad submit
            return true;
        }

        public void CancelArm()
        {
            if (_isArmed || _pointerState == PointerCycleState.Armed || _pointerState == PointerCycleState.Released)
            {
                _pointerState = PointerCycleState.Cancelled;
                _cancelledFrame = Time.frameCount;
            }
            _isArmed = false;
            _primaryPointerId = null;
            _pressGeneration++;
            _accumulatedDragDistance = 0f;
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
