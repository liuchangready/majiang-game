using UnityEngine;
using UnityEngine.UI;

namespace Mahjong.Unity.UI
{
    [RequireComponent(typeof(RectTransform))]
    [ExecuteAlways]
    public sealed class SafeAreaFitter : MonoBehaviour
    {
        [SerializeField] private CanvasScaler? _canvasScaler;
        [SerializeField] private RectTransform? _designRoot;

        private RectTransform? _rectTransform;
        private Rect _lastSafeArea;
        private Vector2 _lastScreenSize;

        private void Awake()
        {
            _rectTransform = GetComponent<RectTransform>();
            ApplySafeArea();
        }

        private void Update()
        {
            if (_lastSafeArea != Screen.safeArea ||
                _lastScreenSize.x != Screen.width ||
                _lastScreenSize.y != Screen.height)
            {
                ApplySafeArea();
            }
        }

        public void ApplySafeArea()
        {
            if (_rectTransform == null)
            {
                _rectTransform = GetComponent<RectTransform>();
            }

            _lastSafeArea = Screen.safeArea;
            _lastScreenSize = new Vector2(Screen.width, Screen.height);

            float scale = DesignViewport.CalculateScale(Screen.width, Screen.height);

            if (_canvasScaler != null)
            {
                _canvasScaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
                _canvasScaler.scaleFactor = scale;
            }

            if (_designRoot != null)
            {
                _designRoot.sizeDelta = new Vector2(DesignViewport.DesignWidth, DesignViewport.DesignHeight);
                _designRoot.anchorMin = new Vector2(0.5f, 0.5f);
                _designRoot.anchorMax = new Vector2(0.5f, 0.5f);
                _designRoot.pivot = new Vector2(0.5f, 0.5f);
            }

            Rect safeDesign = DesignViewport.CalculateSafeDesignRect(Screen.width, Screen.height, Screen.safeArea);

            if (_rectTransform != null)
            {
                _rectTransform.anchorMin = Vector2.zero;
                _rectTransform.anchorMax = Vector2.one;
                _rectTransform.offsetMin = new Vector2(safeDesign.xMin, safeDesign.yMin);
                _rectTransform.offsetMax = new Vector2(
                    -(DesignViewport.DesignWidth - safeDesign.xMax),
                    -(DesignViewport.DesignHeight - safeDesign.yMax));
            }
        }
    }
}
