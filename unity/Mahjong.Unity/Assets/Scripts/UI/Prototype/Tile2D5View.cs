#nullable enable
using System;
using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Mahjong.Unity.UI.Prototype
{
    public enum Tile2D5Mode
    {
        Standing = 0,
        FlatFront = 1,
        FlatBack = 2
    }

    /// <summary>
    /// 2.5D Mahjong Tile presentation view with physical thickness, bevel chamfers, and drop shadow.
    /// Purely visual presentation for prototype display and animations.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public sealed class Tile2D5View : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
    {
        [Header("Configuration")]
        [SerializeField] private int _tileIndex = -1;
        [SerializeField] private string _tileName = string.Empty;
        [SerializeField] private Tile2D5Mode _mode = Tile2D5Mode.Standing;
        [SerializeField] private bool _isHoverable = true;
        [SerializeField] private bool _isSelectable = true;
        [SerializeField] private bool _isRotated90 = false;

        [Header("Internal References")]
        [SerializeField] private RectTransform? _shadowRect;
        [SerializeField] private Image? _shadowImage;
        [SerializeField] private RectTransform? _bodyRect;
        [SerializeField] private Image? _chassisImage;
        [SerializeField] private RectTransform? _glyphRect;
        [SerializeField] private Image? _glyphImage;
        [SerializeField] private Image? _glowImage;

        [Header("2.5D Shared Sprites")]
        [SerializeField] private Sprite? _standingChassis;
        [SerializeField] private Sprite? _flatFrontChassis;
        [SerializeField] private Sprite? _flatBackChassis;
        [SerializeField] private Sprite? _softShadow;
        [SerializeField] private Sprite? _glowSprite;

        public int TileIndex => _tileIndex;
        public string TileName => _tileName;
        public Tile2D5Mode Mode => _mode;
        public bool IsSelected { get; private set; }
        public bool IsHovered { get; private set; }
        public bool IsRotated90 => _isRotated90;

        public event Action<Tile2D5View>? Clicked;
        public event Action<Tile2D5View, bool>? HoverChanged;

        /// <summary>
        /// Optional global pause provider delegate (e.g. from V5ArtPrototype) to pause view-level tweens.
        /// </summary>
        public static Func<bool>? IsPausedProvider { get; set; }

        public void ClearClickListeners()
        {
            Clicked = null;
        }

        private float _currentElevation = 0f;
        private float _targetElevation = 0f;
        private Coroutine? _elevationCoroutine;
        private Coroutine? _moveCoroutine;

        public void Setup(
            int tileIndex,
            string tileName,
            Sprite? faceSprite,
            Tile2D5Mode mode,
            Sprite? standingChassis,
            Sprite? flatFrontChassis,
            Sprite? flatBackChassis,
            Sprite? softShadow,
            Sprite? glowSprite,
            bool isRotated90 = false)
        {
            _tileIndex = tileIndex;
            _tileName = tileName;
            _mode = mode;
            _standingChassis = standingChassis;
            _flatFrontChassis = flatFrontChassis;
            _flatBackChassis = flatBackChassis;
            _softShadow = softShadow;
            _glowSprite = glowSprite;
            _isRotated90 = isRotated90;

            EnsureHierarchy();
            ApplyModeVisuals(faceSprite);
            UpdateElevationInstant(0f);
        }

        public void SetMode(Tile2D5Mode mode, Sprite? faceSprite = null, bool isRotated90 = false)
        {
            _mode = mode;
            _isRotated90 = isRotated90;
            ApplyModeVisuals(faceSprite ?? (_glyphImage != null ? _glyphImage.sprite : null));
        }

        public void SetInteractive(bool hoverable, bool selectable)
        {
            _isHoverable = hoverable;
            _isSelectable = selectable;
        }

        public void SetHovered(bool hovered, bool animated = true)
        {
            if (IsHovered == hovered) return;
            IsHovered = hovered;
            UpdateTargetElevation();
            if (animated && Application.isPlaying)
            {
                StartElevationAnimation(_targetElevation, 0.12f);
            }
            else
            {
                UpdateElevationInstant(_targetElevation);
            }
            HoverChanged?.Invoke(this, hovered);
        }

        public void SetSelected(bool selected, bool animated = true)
        {
            if (IsSelected == selected) return;
            IsSelected = selected;
            UpdateTargetElevation();
            if (animated && Application.isPlaying)
            {
                StartElevationAnimation(_targetElevation, 0.15f);
            }
            else
            {
                UpdateElevationInstant(_targetElevation);
            }
            if (_glowImage != null)
            {
                _glowImage.gameObject.SetActive(IsSelected || IsHovered);
            }
        }

        public void AnimateMove(Vector2 targetAnchoredPosition, float duration, Action? onComplete = null)
        {
            if (!Application.isPlaying)
            {
                GetComponent<RectTransform>().anchoredPosition = targetAnchoredPosition;
                onComplete?.Invoke();
                return;
            }

            if (_moveCoroutine != null) StopCoroutine(_moveCoroutine);
            _moveCoroutine = StartCoroutine(DoMove(targetAnchoredPosition, duration, onComplete));
        }

        private IEnumerator DoMove(Vector2 target, float duration, Action? onComplete)
        {
            RectTransform rt = GetComponent<RectTransform>();
            Vector2 start = rt.anchoredPosition;
            float elapsed = 0f;
            while (elapsed < duration)
            {
                while (IsPausedProvider != null && IsPausedProvider()) yield return null;
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                float eased = 1f - Mathf.Pow(1f - t, 3f);
                rt.anchoredPosition = Vector2.LerpUnclamped(start, target, eased);
                yield return null;
            }
            rt.anchoredPosition = target;
            _moveCoroutine = null;
            onComplete?.Invoke();
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (!_isHoverable) return;
            SetHovered(true, true);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            if (!_isHoverable) return;
            SetHovered(false, true);
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (!_isSelectable) return;
            SetSelected(!IsSelected, true);
            Clicked?.Invoke(this);
        }

        private void UpdateTargetElevation()
        {
            if (_mode != Tile2D5Mode.Standing)
            {
                _targetElevation = IsSelected ? 12f : (IsHovered ? 8f : 0f);
            }
            else
            {
                _targetElevation = IsSelected ? 24f : (IsHovered ? 18f : 0f);
            }
        }

        private void StartElevationAnimation(float target, float duration)
        {
            if (_elevationCoroutine != null) StopCoroutine(_elevationCoroutine);
            _elevationCoroutine = StartCoroutine(DoElevation(target, duration));
        }

        private IEnumerator DoElevation(float target, float duration)
        {
            float start = _currentElevation;
            float elapsed = 0f;
            while (elapsed < duration)
            {
                while (IsPausedProvider != null && IsPausedProvider()) yield return null;
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                float eased = 1f - Mathf.Pow(1f - t, 2f);
                UpdateElevationInstant(Mathf.Lerp(start, target, eased));
                yield return null;
            }
            UpdateElevationInstant(target);
            _elevationCoroutine = null;
        }

        private void UpdateElevationInstant(float elevation)
        {
            _currentElevation = elevation;
            if (_bodyRect != null)
            {
                _bodyRect.anchoredPosition = new Vector2(0f, elevation);
            }

            if (_shadowRect != null && _shadowImage != null)
            {
                float elevationFactor = Mathf.Clamp01(elevation / 24f);
                float shadowOffsetY = Mathf.Lerp(-6f, -16f, elevationFactor);
                float shadowScale = Mathf.Lerp(1.0f, 1.15f, elevationFactor);
                float shadowAlpha = Mathf.Lerp(0.48f, 0.32f, elevationFactor);

                _shadowRect.anchoredPosition = new Vector2(0f, shadowOffsetY);
                _shadowRect.localScale = new Vector3(shadowScale, shadowScale, 1f);
                _shadowImage.color = new Color(0.04f, 0.08f, 0.06f, shadowAlpha);
            }

            if (_glowImage != null)
            {
                _glowImage.gameObject.SetActive(IsSelected || (IsHovered && _isHoverable));
            }
        }

        private void ApplyModeVisuals(Sprite? faceSprite)
        {
            EnsureHierarchy();
            RectTransform rootRt = GetComponent<RectTransform>();

            if (_isRotated90)
            {
                rootRt.localRotation = Quaternion.Euler(0, 0, 90);
            }
            else
            {
                rootRt.localRotation = Quaternion.identity;
            }

            switch (_mode)
            {
                case Tile2D5Mode.Standing:
                    if (_chassisImage != null)
                    {
                        _chassisImage.sprite = _standingChassis;
                        _chassisImage.color = Color.white;
                    }
                    if (_glyphImage != null)
                    {
                        _glyphImage.gameObject.SetActive(true);
                        _glyphImage.sprite = faceSprite;
                        _glyphImage.preserveAspect = true;
                    }
                    if (_glyphRect != null)
                    {
                        _glyphRect.anchorMin = new Vector2(0.10f, 0.12f);
                        _glyphRect.anchorMax = new Vector2(0.90f, 0.88f);
                        _glyphRect.offsetMin = Vector2.zero;
                        _glyphRect.offsetMax = Vector2.zero;
                    }
                    if (_shadowRect != null)
                    {
                        _shadowRect.anchorMin = new Vector2(0.05f, 0.0f);
                        _shadowRect.anchorMax = new Vector2(0.95f, 0.28f);
                        _shadowRect.offsetMin = Vector2.zero;
                        _shadowRect.offsetMax = Vector2.zero;
                    }
                    break;

                case Tile2D5Mode.FlatFront:
                    if (_chassisImage != null)
                    {
                        _chassisImage.sprite = _flatFrontChassis;
                        _chassisImage.color = Color.white;
                    }
                    if (_glyphImage != null)
                    {
                        _glyphImage.gameObject.SetActive(true);
                        _glyphImage.sprite = faceSprite;
                        _glyphImage.preserveAspect = true;
                    }
                    if (_glyphRect != null)
                    {
                        _glyphRect.anchorMin = new Vector2(0.08f, 0.16f);
                        _glyphRect.anchorMax = new Vector2(0.82f, 0.90f);
                        _glyphRect.offsetMin = Vector2.zero;
                        _glyphRect.offsetMax = Vector2.zero;
                    }
                    if (_shadowRect != null)
                    {
                        _shadowRect.anchorMin = new Vector2(0.05f, 0.0f);
                        _shadowRect.anchorMax = new Vector2(0.98f, 0.32f);
                        _shadowRect.offsetMin = Vector2.zero;
                        _shadowRect.offsetMax = Vector2.zero;
                    }
                    break;

                case Tile2D5Mode.FlatBack:
                    if (_chassisImage != null)
                    {
                        _chassisImage.sprite = _flatBackChassis;
                        _chassisImage.color = Color.white;
                    }
                    if (_glyphImage != null)
                    {
                        _glyphImage.gameObject.SetActive(false);
                    }
                    if (_shadowRect != null)
                    {
                        _shadowRect.anchorMin = new Vector2(0.05f, 0.0f);
                        _shadowRect.anchorMax = new Vector2(0.98f, 0.32f);
                        _shadowRect.offsetMin = Vector2.zero;
                        _shadowRect.offsetMax = Vector2.zero;
                    }
                    break;
            }
        }

        private void EnsureHierarchy()
        {
            if (_bodyRect != null && _chassisImage != null && _glyphImage != null && _shadowImage != null) return;

            Transform shadowTransform = transform.Find("Shadow");
            if (shadowTransform == null)
            {
                var go = new GameObject("Shadow", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                go.transform.SetParent(transform, false);
                shadowTransform = go.transform;
            }
            _shadowRect = shadowTransform.GetComponent<RectTransform>();
            _shadowImage = shadowTransform.GetComponent<Image>();
            _shadowImage.sprite = _softShadow;
            _shadowImage.raycastTarget = false;

            Transform bodyTransform = transform.Find("Body");
            if (bodyTransform == null)
            {
                var go = new GameObject("Body", typeof(RectTransform));
                go.transform.SetParent(transform, false);
                bodyTransform = go.transform;
            }
            _bodyRect = bodyTransform.GetComponent<RectTransform>();
            _bodyRect.anchorMin = Vector2.zero;
            _bodyRect.anchorMax = Vector2.one;
            _bodyRect.offsetMin = Vector2.zero;
            _bodyRect.offsetMax = Vector2.zero;

            Transform chassisTransform = bodyTransform.Find("Chassis");
            if (chassisTransform == null)
            {
                var go = new GameObject("Chassis", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                go.transform.SetParent(bodyTransform, false);
                chassisTransform = go.transform;
            }
            var chassisRect = chassisTransform.GetComponent<RectTransform>();
            chassisRect.anchorMin = Vector2.zero;
            chassisRect.anchorMax = Vector2.one;
            chassisRect.offsetMin = Vector2.zero;
            chassisRect.offsetMax = Vector2.zero;
            _chassisImage = chassisTransform.GetComponent<Image>();
            _chassisImage.raycastTarget = true;

            Transform glyphTransform = bodyTransform.Find("Glyph");
            if (glyphTransform == null)
            {
                var go = new GameObject("Glyph", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                go.transform.SetParent(bodyTransform, false);
                glyphTransform = go.transform;
            }
            _glyphRect = glyphTransform.GetComponent<RectTransform>();
            _glyphImage = glyphTransform.GetComponent<Image>();
            _glyphImage.raycastTarget = false;

            Transform glowTransform = bodyTransform.Find("Glow");
            if (glowTransform == null)
            {
                var go = new GameObject("Glow", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                go.transform.SetParent(bodyTransform, false);
                glowTransform = go.transform;
            }
            var glowRect = glowTransform.GetComponent<RectTransform>();
            glowRect.anchorMin = new Vector2(-0.06f, -0.06f);
            glowRect.anchorMax = new Vector2(1.06f, 1.06f);
            glowRect.offsetMin = Vector2.zero;
            glowRect.offsetMax = Vector2.zero;
            _glowImage = glowTransform.GetComponent<Image>();
            _glowImage.sprite = _glowSprite;
            _glowImage.raycastTarget = false;
            _glowImage.gameObject.SetActive(false);
        }
    }
}
