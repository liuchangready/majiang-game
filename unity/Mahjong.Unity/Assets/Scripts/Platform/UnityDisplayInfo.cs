using UnityEngine;

namespace Mahjong.Unity.Platform
{
    public sealed class UnityDisplayInfo
    {
        public int Width => Screen.width;
        public int Height => Screen.height;
        public Rect SafeArea => Screen.safeArea;
        public float Dpi => Screen.dpi;
        public ScreenOrientation Orientation => Screen.orientation;

        public bool IsLandscape =>
            Orientation == ScreenOrientation.LandscapeLeft ||
            Orientation == ScreenOrientation.LandscapeRight ||
            Width >= Height;
    }
}
