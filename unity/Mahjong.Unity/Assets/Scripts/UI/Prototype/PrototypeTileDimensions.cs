namespace Mahjong.Unity.UI.Prototype
{
    /// <summary>Shared V3 prototype tile dimensions; seat sizes are derived from the self-hand face.</summary>
    public static class PrototypeTileDimensions
    {
        public const float SelfHandFaceWidth = 50f;
        public const float SelfHandFaceHeight = 86f;
        public const float ProjectedThickness = 8f;

        public const float OppositeWidthRatio = .48f;
        public const float OppositeHeightRatio = .49f;

        public static float ProjectedDepthForFaceWidth(float faceWidth)
        {
            return ProjectedThickness * faceWidth / SelfHandFaceWidth;
        }

        public static float SideWidthRatio(MahjongWallSeat seat, float progress)
        {
            return 24f / SelfHandFaceWidth;
        }

        public static float SideHeightRatio(MahjongWallSeat seat, float progress)
        {
            return 42f / SelfHandFaceHeight;
        }

    }
}
