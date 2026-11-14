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

        public override Texture mainTexture => _face != null ? _face.texture : s_WhiteTexture;

        public void Configure(MahjongWallSeat seat, Sprite? face, int tileIndex,
            MahjongWallAxisFrame axisFrame,
            MahjongWallTextureMode textureMode = MahjongWallTextureMode.BackFacingTable,
            float faceWidthOverride = 0f, int gapAfterTileIndex = -1, float extraGap = 0f)
        {
            _seat = seat;
            _face = face;
            _tileIndex = tileIndex;
            _axisFrame = axisFrame;
            _textureMode = textureMode;
            _faceWidthOverride = faceWidthOverride;
            _gapAfterTileIndex = gapAfterTileIndex;
            _extraGap = extraGap;
            raycastTarget = false;
            SetMaterialDirty();
            SetVerticesDirty();
        }

        public void SetAxisFrame(MahjongWallAxisFrame axisFrame)
        {
            _axisFrame = axisFrame;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (_face == null || _face.texture == null) return;

            MahjongWallAxisFrame axes = _axisFrame.AxisLength > 0f
                ? _axisFrame : MahjongWallGeometry.BuildReferenceAxisFrame(_seat);
            MahjongWallTileGeometry[] row = MahjongWallGeometry.BuildWallFromAxes(_seat, 1, axes,
                _faceWidthOverride, _gapAfterTileIndex, _extraGap);
            MahjongWallTileGeometry tile = row[Mathf.Clamp(_tileIndex, 0, row.Length - 1)];
            MahjongWallPoint2[] revealFace = _textureMode == MahjongWallTextureMode.FaceUp
                ? tile.BackFace
                : _seat == MahjongWallSeat.North ? tile.BackFace : tile.FrontFace;
            Rect pixels = _face.textureRect;
            float invWidth = 1f / _face.texture.width;
            float invHeight = 1f / _face.texture.height;
            Vector2 uvMin = new Vector2(pixels.xMin * invWidth, pixels.yMin * invHeight);
            Vector2 uvMax = new Vector2(pixels.xMax * invWidth, pixels.yMax * invHeight);
            Vector2[] uv =
            {
                new Vector2(uvMin.x, uvMin.y),
                new Vector2(uvMax.x, uvMin.y),
                new Vector2(uvMax.x, uvMax.y),
                new Vector2(uvMin.x, uvMax.y)
            };

            int start = vh.currentVertCount;
            for (int i = 0; i < revealFace.Length; i++)
            {
                UIVertex vertex = UIVertex.simpleVert;
                vertex.position = new Vector2(revealFace[i].X, -revealFace[i].Y);
                vertex.uv0 = uv[i];
                vertex.color = new Color(1f, 1f, 1f,
                    _textureMode == MahjongWallTextureMode.FaceUp ? 1f : .85f);
                vh.AddVert(vertex);
            }
            vh.AddTriangle(start, start + 1, start + 2);
            vh.AddTriangle(start, start + 2, start + 3);
        }

    }
}
