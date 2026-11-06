using System;
using UnityEngine;

namespace Mahjong.Unity.UI
{
    public static class DesignViewport
    {
        public const float DesignWidth = 1280f;
        public const float DesignHeight = 720f;
        public const float MinDesignPadding = 24f;

        public static float CalculateScale(float screenWidth, float screenHeight)
        {
            if (screenWidth <= 0f || screenHeight <= 0f) return 1.0f;
            return Math.Min(screenWidth / DesignWidth, screenHeight / DesignHeight);
        }

        public static Rect CalculateContentRect(float screenWidth, float screenHeight, float scale)
        {
            float contentW = DesignWidth * scale;
            float contentH = DesignHeight * scale;
            float contentX = (screenWidth - contentW) / 2f;
            float contentY = (screenHeight - contentH) / 2f;
            return new Rect(contentX, contentY, contentW, contentH);
        }

        public static Rect Intersect(Rect a, Rect b)
        {
            float xMin = Math.Max(a.xMin, b.xMin);
            float xMax = Math.Min(a.xMax, b.xMax);
            float yMin = Math.Max(a.yMin, b.yMin);
            float yMax = Math.Min(a.yMax, b.yMax);

            if (xMax >= xMin && yMax >= yMin)
            {
                return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
            }
            return Rect.zero;
        }

        public static Rect CalculateSafeDesignRect(float screenWidth, float screenHeight, Rect safeArea)
        {
            float scale = CalculateScale(screenWidth, screenHeight);
            Rect contentRect = CalculateContentRect(screenWidth, screenHeight, scale);

            Rect safeScreen = Intersect(safeArea, contentRect);
            if (safeScreen.width <= 0f || safeScreen.height <= 0f)
            {
                safeScreen = contentRect;
            }

            float safeX = (safeScreen.x - contentRect.x) / scale;
            float safeY = (safeScreen.y - contentRect.y) / scale;
            float safeW = safeScreen.width / scale;
            float safeH = safeScreen.height / scale;

            // Clamp to design bounds
            safeX = Math.Max(0f, Math.Min(DesignWidth, safeX));
            safeY = Math.Max(0f, Math.Min(DesignHeight, safeY));
            safeW = Math.Max(0f, Math.Min(DesignWidth - safeX, safeW));
            safeH = Math.Max(0f, Math.Min(DesignHeight - safeY, safeH));

            return new Rect(safeX, safeY, safeW, safeH);
        }

        public static Vector2 ScreenToDesignPoint(Vector2 screenPoint, float screenWidth, float screenHeight)
        {
            float scale = CalculateScale(screenWidth, screenHeight);
            Rect contentRect = CalculateContentRect(screenWidth, screenHeight, scale);
            return new Vector2(
                (screenPoint.x - contentRect.x) / scale,
                (screenPoint.y - contentRect.y) / scale);
        }

        public static float CalculateMinTouchTargetDesignUnits(float dpi, float scale)
        {
            if (dpi <= 0f || scale <= 0f) return 88f;
            float dpUnits = 44f * dpi / 160f / scale;
            return Math.Max(88f, dpUnits);
        }
    }
}
