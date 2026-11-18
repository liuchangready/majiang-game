#nullable enable
using UnityEngine;
using UnityEngine.UI;

namespace Mahjong.Unity.UI.Prototype
{
    /// <summary>Draws a face on the hidden middle wall tile, beneath its translucent back.</summary>
    [AddComponentMenu("")]
    public sealed class MahjongWallFaceRevealGraphic : MaskableGraphic
    {
        [SerializeField] private MahjongWallSeat _seat;
        [SerializeField] private Sprite? _face;
        [SerializeField] private int _tileIndex = MahjongWallGeometry.ReferenceTileCount / 2;
        [SerializeField] private MahjongWallTextureMode _textureMode;
        private MahjongWallAxisFrame _axisFrame;
        private float _faceWidthOverride;
        private int _gapAfterTileIndex = -1;
        private float _extraGap;
        private int _tileCountOverride;
        private bool _useExplicitFrontGeometry;
        private float _widthShrinkPercent;
        private bool _widthByYAxis;
        private float _widthFalloffPerScreenY = MahjongExplicitFrontWallGraphic.DefaultWidthFalloffPerScreenY;

        public override Texture mainTexture => _face != null ? _face.texture : s_WhiteTexture;

        public void Configure(MahjongWallSeat seat, Sprite? face, int tileIndex,
            MahjongWallAxisFrame axisFrame,
            MahjongWallTextureMode textureMode = MahjongWallTextureMode.BackFacingTable,
            float faceWidthOverride = 0f, int gapAfterTileIndex = -1, float extraGap = 0f,
            int tileCountOverride = 0, bool useExplicitFrontGeometry = false,
            float widthShrinkPercent = 0f, bool widthByYAxis = false,
            float widthFalloffPerScreenY = MahjongExplicitFrontWallGraphic.DefaultWidthFalloffPerScreenY)
        {
            _seat = seat;
            _face = face;
            _tileIndex = tileIndex;
            _axisFrame = axisFrame;
            _textureMode = textureMode;
            _faceWidthOverride = faceWidthOverride;
            _gapAfterTileIndex = gapAfterTileIndex;
            _extraGap = extraGap;
            _tileCountOverride = Mathf.Max(0, tileCountOverride);
            _useExplicitFrontGeometry = useExplicitFrontGeometry;
            _widthShrinkPercent = Mathf.Clamp(widthShrinkPercent, 0f, 100f);
            _widthByYAxis = widthByYAxis;
            _widthFalloffPerScreenY = Mathf.Max(0f, widthFalloffPerScreenY);
            raycastTarget = false;
            SetMaterialDirty();
            SetVerticesDirty();
        }

        public void SetAxisFrame(MahjongWallAxisFrame axisFrame)
        {
            _axisFrame = axisFrame;
            SetVerticesDirty();
        }

        public void SetWidthShrinkPercent(float percent)
        {
            _widthShrinkPercent = Mathf.Clamp(percent, 0f, 100f);
            SetVerticesDirty();
        }

        public void SetWidthFalloffPerScreenY(float value)
        {
            _widthFalloffPerScreenY = Mathf.Max(0f, value);
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (_face == null || _face.texture == null) return;

            MahjongWallAxisFrame axes = _axisFrame.AxisLength > 0f
                ? _axisFrame : MahjongWallGeometry.BuildReferenceAxisFrame(_seat);
            MahjongWallPoint2[] revealFace;
            if (_useExplicitFrontGeometry)
            {
                int tileCount = _tileCountOverride > 0
                    ? _tileCountOverride : MahjongWallGeometry.ReferenceTileCount;
                MahjongWallSkeleton skeleton = MahjongExplicitFrontWallGraphic.BuildSkeleton(axes,
                    tileCount, _faceWidthOverride, _gapAfterTileIndex, _extraGap);
                MahjongExplicitTileGeometry[] row = MahjongExplicitFrontWallGraphic.BuildTileGeometry(
                    skeleton, tileCount, _gapAfterTileIndex, _extraGap, _widthShrinkPercent,
                    _widthByYAxis, _widthFalloffPerScreenY);
                revealFace = row[Mathf.Clamp(_tileIndex, 0, row.Length - 1)].FrontFace;
                if (_seat == MahjongWallSeat.WestDiscard
                    && _textureMode == MahjongWallTextureMode.SideDiscardWall)
                {
                    // West's cross section winds opposite to East's; keep the glyph facing the table.
                    revealFace = new[] { revealFace[1], revealFace[0], revealFace[3], revealFace[2] };
                }
                else if (_textureMode == MahjongWallTextureMode.FaceUp
                    || _textureMode == MahjongWallTextureMode.FaceUpWithSideAndBottom)
                {
                    // Face-up UVs follow the row first; explicit faces follow the cross section first.
                    revealFace = new[] { revealFace[0], revealFace[3], revealFace[2], revealFace[1] };
                }
            }
            else
            {
                MahjongWallTileGeometry[] row = MahjongWallGeometry.BuildWallFromAxes(_seat, 1, axes,
                    _faceWidthOverride, _gapAfterTileIndex, _extraGap, _tileCountOverride);
                MahjongWallTileGeometry tile = row[Mathf.Clamp(_tileIndex, 0, row.Length - 1)];
                revealFace = _textureMode == MahjongWallTextureMode.SideDiscardWall
                    ? tile.SideDiscardFace
                    : _textureMode == MahjongWallTextureMode.FaceUp
                    || _textureMode == MahjongWallTextureMode.FaceUpWithSideAndBottom
                    ? tile.BackFace
                    : _seat == MahjongWallSeat.North ? tile.BackFace : tile.FrontFace;
            }
            Rect pixels = _face.textureRect;
            float invWidth = 1f / _face.texture.width;
            float invHeight = 1f / _face.texture.height;
            Vector2 uvMin = new Vector2(pixels.xMin * invWidth, pixels.yMin * invHeight);
            Vector2 uvMax = new Vector2(pixels.xMax * invWidth, pixels.yMax * invHeight);
            float rotation = Mathf.Repeat(axes.FaceUvRotationDegrees, 360f);
            Vector2[] uv = rotation >= 135f && rotation < 225f
                ? new[] { new Vector2(uvMax.x, uvMax.y), new Vector2(uvMin.x, uvMax.y),
                    new Vector2(uvMin.x, uvMin.y), new Vector2(uvMax.x, uvMin.y) }
                : rotation >= 45f && rotation < 135f
                    ? new[] { new Vector2(uvMax.x, uvMin.y), new Vector2(uvMax.x, uvMax.y),
                        new Vector2(uvMin.x, uvMax.y), new Vector2(uvMin.x, uvMin.y) }
                    : rotation >= 225f && rotation < 315f
                        ? new[] { new Vector2(uvMin.x, uvMax.y), new Vector2(uvMin.x, uvMin.y),
                            new Vector2(uvMax.x, uvMin.y), new Vector2(uvMax.x, uvMax.y) }
                        : new[] { new Vector2(uvMin.x, uvMin.y), new Vector2(uvMax.x, uvMin.y),
                            new Vector2(uvMax.x, uvMax.y), new Vector2(uvMin.x, uvMax.y) };

            int start = vh.currentVertCount;
            for (int i = 0; i < revealFace.Length; i++)
            {
                UIVertex vertex = UIVertex.simpleVert;
                vertex.position = new Vector2(revealFace[i].X, -revealFace[i].Y);
                vertex.uv0 = uv[i];
                vertex.color = new Color(1f, 1f, 1f,
                    _textureMode == MahjongWallTextureMode.BackFacingTable ? .85f : 1f);
                vh.AddVert(vertex);
            }
            vh.AddTriangle(start, start + 1, start + 2);
            vh.AddTriangle(start, start + 2, start + 3);
        }

    }
}
