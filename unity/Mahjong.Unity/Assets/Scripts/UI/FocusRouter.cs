#nullable enable
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Mahjong.Unity.UI
{
    public sealed class FocusRouter
    {
        private readonly List<Selectable> _selectables = new List<Selectable>();
        private int _currentIndex = -1;

        public void RegisterSelectables(IEnumerable<Selectable> selectables)
        {
            _selectables.Clear();
            if (selectables != null)
            {
                foreach (var s in selectables)
                {
                    if (s != null && s.isActiveAndEnabled && s.interactable)
                    {
                        _selectables.Add(s);
                    }
                }
            }
            _currentIndex = _selectables.Count > 0 ? 0 : -1;
            FocusCurrent();
        }

        public Selectable? GetCurrent()
        {
            if (_currentIndex >= 0 && _currentIndex < _selectables.Count)
            {
                return _selectables[_currentIndex];
            }
            return null;
        }

        public void FocusCurrent()
        {
            var target = GetCurrent();
            if (target != null && EventSystem.current != null)
            {
                EventSystem.current.SetSelectedGameObject(target.gameObject);
            }
        }

        public Selectable? FocusNext()
        {
            if (_selectables.Count == 0) return null;
            _currentIndex = (_currentIndex + 1) % _selectables.Count;
            var target = _selectables[_currentIndex];
            target.Select();
            return target;
        }

        public Selectable? FocusPrevious()
        {
            if (_selectables.Count == 0) return null;
            _currentIndex = (_currentIndex - 1 + _selectables.Count) % _selectables.Count;
            var target = _selectables[_currentIndex];
            target.Select();
            return target;
        }
    }
}
