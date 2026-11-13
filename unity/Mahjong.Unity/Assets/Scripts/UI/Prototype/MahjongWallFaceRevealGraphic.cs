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

        public override Texture mainTexture => _face != null ? _face.texture : s_WhiteTexture;

        public void Configure(MahjongWallSeat seat, Sprite? face)
        {
            _seat = seat;
            _face = face;
            raycastTarget = false;
            SetMaterialDirty();
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (_face == null || _face.texture == null) return;

            MahjongWallTileGeometry[] row = MahjongWallGeometry.BuildReferenceRow(_seat);
            MahjongWallTileGeometry tile = row[row.Length / 2];
            MahjongWallPoint2[] revealFace = _seat == MahjongWallSeat.West
                ? tile.FrontFace : tile.BackFace;
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
                vertex.color = new Color(1f, 1f, 1f, .85f);
                vh.AddVert(vertex);
            }
            vh.AddTriangle(start, start + 1, start + 2);
            vh.AddTriangle(start, start + 2, start + 3);
        }

    }
}
