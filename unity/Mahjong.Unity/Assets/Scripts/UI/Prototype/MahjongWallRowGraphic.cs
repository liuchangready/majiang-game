#nullable enable
using System;
using UnityEngine;
using UnityEngine.UI;

namespace Mahjong.Unity.UI.Prototype
{
    /// <summary>Renders the shared table-projected tile chassis returned by MahjongWallGeometry.</summary>
    public sealed class MahjongWallRowGraphic : MaskableGraphic
    {
        [SerializeField] private MahjongWallSeat _seat;

        public void Configure(MahjongWallSeat seat)
        {
            _seat = seat;
            raycastTarget = false;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            MahjongWallTileGeometry[] row = MahjongWallGeometry.BuildRow(_seat);
            for (int i = 0; i < row.Length; i++) AddTile(vh, row[i], i);
        }

        private static void AddTile(VertexHelper vh, MahjongWallTileGeometry tile, int index)
        {
            Color[] colors = {
                new Color(.008f, .022f, .027f, .22f),
                new Color(.43f, .55f, .49f, 1f),
                new Color(.79f, .84f, .74f, 1f),
                new Color(.08f + (index % 3 - 1) * .018f, .34f + (index % 3 - 1) * .018f, .31f + (index % 3 - 1) * .018f, 1f)
            };
            for (int q = 0; q < tile.Quads.Length; q++)
                AddQuad(vh, tile.Quads[q], colors[q]);
            AddTriangle(vh, tile.Triangles[0], new Color(.62f, .82f, .69f, 1f));
            AddTriangle(vh, tile.Triangles[1], new Color(.31f, .58f, .49f, 1f));
        }

        private static Vector2 ToUnity(MahjongWallPoint2 point) => new Vector2(point.X, -point.Y);

        private static void AddQuad(VertexHelper vh, MahjongWallPoint2[] points, Color color)
        {
            float area1 = TriangleArea(points[0], points[1], points[2]);
            float area2 = TriangleArea(points[0], points[2], points[3]);
            if (area1 <= .0001f || area2 <= .0001f)
                throw new InvalidOperationException("Projected mahjong tile quad contains a zero-area triangle.");
            int start = vh.currentVertCount;
            for (int i = 0; i < 4; i++) vh.AddVert(ToUnity(points[i]), color, Vector2.zero);
            vh.AddTriangle(start, start + 1, start + 2);
            vh.AddTriangle(start, start + 2, start + 3);
        }

        private static void AddTriangle(VertexHelper vh, MahjongWallPoint2[] points, Color color)
        {
            if (TriangleArea(points[0], points[1], points[2]) <= .0001f)
                throw new InvalidOperationException("Projected mahjong tile detail contains a zero-area triangle.");
            int start = vh.currentVertCount;
            for (int i = 0; i < 3; i++) vh.AddVert(ToUnity(points[i]), color, Vector2.zero);
            vh.AddTriangle(start, start + 1, start + 2);
        }

        private static float TriangleArea(MahjongWallPoint2 a, MahjongWallPoint2 b, MahjongWallPoint2 c)
        {
            return Mathf.Abs((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X)) * .5f;
        }
    }
}
