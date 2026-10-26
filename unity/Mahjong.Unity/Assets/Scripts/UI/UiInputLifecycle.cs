#nullable enable
using System;
using UnityEngine;
using UnityEngine.InputSystem;

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
        private GameObject? _armedTarget;
        private GameObject? _releasedTarget;
        private bool _submitPermit;
        private GameObject? _submitTarget;
        private GameObject? _earlySubmitTarget;
        private GameObject? _consumedSubmitTarget;
        private bool _isPointerClick;
        private GameObject? _pointerClickTarget;

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

        public void NotifySubmit(GameObject? target = null)
        {
            if (!AcceptingInput)
            {
                _submitPermit = false;
                _submitTarget = null;
                _earlySubmitTarget = null;
                return;
            }
            if (_earlySubmitTarget != null && (target == null || ReferenceEquals(_earlySubmitTarget, target)))
            {
                _earlySubmitTarget = null;
                _submitPermit = false;
                _submitTarget = null;
                _consumedSubmitTarget = target;
                _pointerState = PointerCycleState.Idle;
                _isArmed = false;
                _primaryPointerId = null;
                _armedTarget = null;
                _releasedTarget = null;
                _isPointerClick = false;
                _pointerClickTarget = null;
                return;
            }
            _submitPermit = true;
            _submitTarget = target;
            _earlySubmitTarget = null;
            _consumedSubmitTarget = null;
            _pointerState = PointerCycleState.Idle;
            _isArmed = false;
            _primaryPointerId = null;
            _armedTarget = null;
            _releasedTarget = null;
            _isPointerClick = false;
            _pointerClickTarget = null;
        }

        public void NotifyPointerClick(GameObject? target = null)
        {
            if (_pointerState == PointerCycleState.Consumed)
            {
                _pointerState = PointerCycleState.Idle;
                _consumedSubmitTarget = null;
            }
            else if (_pointerState != PointerCycleState.Released)
            {
                _isPointerClick = true;
                _pointerClickTarget = target;
            }
            _consumedSubmitTarget = null;
        }

        public bool TryArm(int pointerId, Vector2 screenPos, float scale = 1.0f, GameObject? target = null)
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
            _armedTarget = target;
            _releasedTarget = null;
            _submitPermit = false;
            _submitTarget = null;
            _earlySubmitTarget = null;
            _consumedSubmitTarget = null;
            _isPointerClick = false;
            _pointerClickTarget = null;
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

        public bool TryRelease(int pointerId, long checkGeneration, GameObject? target = null)
        {
            if (!_isArmed || _primaryPointerId != pointerId)
            {
                _pointerState = PointerCycleState.Cancelled;
                _armedTarget = null;
                _releasedTarget = null;
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
            _releasedTarget = target ?? _armedTarget;
            _armedTarget = null;
            return true;
        }

        private static bool IsInvokedByPointerClick()
        {
            try
            {
                var st = new System.Diagnostics.StackTrace(2, false);
                int maxFrames = Math.Min(st.FrameCount, 12);
                for (int i = 0; i < maxFrames; i++)
                {
                    var m = st.GetFrame(i)?.GetMethod();
                    if (m != null && string.Equals(m.Name, "OnPointerClick", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // Ignored
            }
            return false;
        }

        private static bool IsInvokedBySubmit()
        {
            try
            {
                var st = new System.Diagnostics.StackTrace(2, false);
                int maxFrames = Math.Min(st.FrameCount, 12);
                for (int i = 0; i < maxFrames; i++)
                {
                    var m = st.GetFrame(i)?.GetMethod();
                    if (m != null && string.Equals(m.Name, "OnSubmit", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // Ignored
            }
            return false;
        }

        public bool ConsumeClickOrSubmit(GameObject? caller = null)
        {
            if (!AcceptingInput)
            {
                _submitPermit = false;
                _submitTarget = null;
                _earlySubmitTarget = null;
                _isPointerClick = false;
                _pointerClickTarget = null;
                return false;
            }

            if (_submitPermit)
            {
                bool match = _submitTarget == null || caller == null || ReferenceEquals(_submitTarget, caller);
                _submitPermit = false;
                _submitTarget = null;
                _earlySubmitTarget = null;
                _consumedSubmitTarget = caller;
                _isPointerClick = false;
                _pointerClickTarget = null;
                if (match)
                {
                    return true;
                }
                return false;
            }

            // Real submit invocation via OnSubmit is always accepted even across consecutive frames
            if (IsInvokedBySubmit())
            {
                _earlySubmitTarget = caller;
                _consumedSubmitTarget = caller;
                _isPointerClick = false;
                _pointerClickTarget = null;
                if (_pointerState == PointerCycleState.Cancelled)
                {
                    _pointerState = PointerCycleState.Idle;
                }
                return true;
            }

            if (_pointerState == PointerCycleState.Released)
            {
                bool match = _releasedTarget == null || caller == null || ReferenceEquals(_releasedTarget, caller);
                _pointerState = PointerCycleState.Consumed;
                _releasedTarget = null;
                _armedTarget = null;
                _earlySubmitTarget = null;
                _consumedSubmitTarget = null;
                _isPointerClick = false;
                _pointerClickTarget = null;
                if (match)
                {
                    return true;
                }
                return false;
            }

            // If this is a pointer click without a released gesture, reject it
            if (IsInvokedByPointerClick() || _isPointerClick)
            {
                _isPointerClick = false;
                _pointerClickTarget = null;
                return false;
            }

            if (_pointerState == PointerCycleState.Armed || _pointerState == PointerCycleState.Consumed || _pointerState == PointerCycleState.Cancelled)
            {
                return false;
            }

            if (_consumedSubmitTarget != null && (caller == null || ReferenceEquals(_consumedSubmitTarget, caller)))
            {
                return false;
            }

            // Early submit (Button before Trigger) or direct programmatic invocation
            _earlySubmitTarget = caller;
            _consumedSubmitTarget = caller;
            return true;
        }

        public void ClearConsumedSubmit(GameObject? caller = null)
        {
            if (caller == null || _consumedSubmitTarget == null || ReferenceEquals(_consumedSubmitTarget, caller))
            {
                _consumedSubmitTarget = null;
            }
        }

        public void CancelArm()
        {
            if (_isArmed || _pointerState == PointerCycleState.Armed || _pointerState == PointerCycleState.Released)
            {
                _pointerState = PointerCycleState.Cancelled;
            }
            _isArmed = false;
            _primaryPointerId = null;
            _armedTarget = null;
            _releasedTarget = null;
            _pressGeneration++;
            _accumulatedDragDistance = 0f;
            _submitPermit = false;
            _submitTarget = null;
            _earlySubmitTarget = null;
            _consumedSubmitTarget = null;
            _isPointerClick = false;
            _pointerClickTarget = null;
        }

        public void OnPause()
        {
            _isPaused = true;
            CancelArm();
        }

        public void OnResume()
        {
            _isPaused = false;
            if (_pointerState == PointerCycleState.Cancelled)
            {
                _pointerState = PointerCycleState.Idle;
            }
        }

        public void OnTransition()
        {
            _isTransitioning = true;
            CancelArm();
        }

        public void TransitionEnd()
        {
            _isTransitioning = false;
            _pointerState = PointerCycleState.Idle;
            _isArmed = false;
            _primaryPointerId = null;
            _armedTarget = null;
            _releasedTarget = null;
            _submitPermit = false;
            _submitTarget = null;
            _earlySubmitTarget = null;
            _consumedSubmitTarget = null;
            _isPointerClick = false;
            _pointerClickTarget = null;
        }

        public void OnFocusLost()
        {
            _isFocusLost = true;
            CancelArm();
        }

        public void OnFocusGained()
        {
            _isFocusLost = false;
            if (_pointerState == PointerCycleState.Cancelled)
            {
                _pointerState = PointerCycleState.Idle;
            }
        }
    }
}
