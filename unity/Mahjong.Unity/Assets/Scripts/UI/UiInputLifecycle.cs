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
        private float _accumulatedDragDistance;
        private long _pressGeneration;
        private bool _acceptingInput = true;

        public bool IsArmed => _isArmed;
        public int? PrimaryPointerId => _primaryPointerId;
        public long PressGeneration => _pressGeneration;
        public bool AcceptingInput => _acceptingInput;

        public static void ResetForTest()
        {
            _instance = new UiInputLifecycle();
        }

        public bool TryArm(int pointerId, Vector2 screenPos, float scale = 1.0f)
        {
            if (!_acceptingInput)
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
            _accumulatedDragDistance = 0f;
            return true;
        }

        public void UpdateDrag(int pointerId, Vector2 currentScreenPos, float scale = 1.0f)
        {
            if (!_isArmed || _primaryPointerId != pointerId)
            {
                return;
            }

            float distPixels = Vector2.Distance(_pointerDownPos, currentScreenPos);
            float effectiveScale = scale > 0.001f ? scale : 1.0f;
            _accumulatedDragDistance = distPixels / effectiveScale;

            if (_accumulatedDragDistance > MaxDragThresholdDesignUnits)
            {
                // Drag threshold exceeded: cancel arm
                _isArmed = false;
                _primaryPointerId = null;
            }
        }

        public bool TryRelease(int pointerId, long checkGeneration)
        {
            if (!_isArmed || _primaryPointerId != pointerId)
            {
                return false;
            }

            if (!_acceptingInput)
            {
                CancelArm();
                return false;
            }

            if (checkGeneration != _pressGeneration)
            {
                CancelArm();
                return false;
            }

            // Successfully released within threshold
            _isArmed = false;
            _primaryPointerId = null;
            return true;
        }

        public void CancelArm()
        {
            _isArmed = false;
            _primaryPointerId = null;
            _pressGeneration++;
            _acceptingInput = false;
            _accumulatedDragDistance = 0f;
        }

        public void OnPause()
        {
            CancelArm();
        }

        public void OnResume()
        {
            // Resume only restores acceptingInput, does NOT restore old arm
            _acceptingInput = true;
        }

        public void OnTransition()
        {
            CancelArm();
        }

        public void TransitionEnd()
        {
            // TransitionEnd only restores acceptingInput, does NOT restore old arm
            _acceptingInput = true;
        }

        public void OnFocusLost()
        {
            CancelArm();
        }

        public void OnFocusGained()
        {
            _acceptingInput = true;
        }
    }
}
