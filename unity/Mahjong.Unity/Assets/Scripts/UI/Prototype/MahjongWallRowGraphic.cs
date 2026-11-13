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
        [SerializeField] private bool _referenceLayout;

        public override Texture mainTexture => _referenceLayout && MahjongTileFaceAtlas.Texture != null
            ? MahjongTileFaceAtlas.Texture : s_WhiteTexture;

        public void Configure(MahjongWallSeat seat, bool referenceLayout = false)
        {
            _seat = seat;
            _referenceLayout = referenceLayout;
            raycastTarget = false;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            MahjongWallTileGeometry[] row = _referenceLayout
                ? MahjongWallGeometry.BuildReferenceRow(_seat)
                : MahjongWallGeometry.BuildRow(_seat);
            int revealIndex = _referenceLayout ? row.Length / 2 : -1;
            Rect revealBounds = revealIndex >= 0 ? TileBounds(row[revealIndex]) : default;
            for (int i = 0; i < row.Length; i++)
            {
                bool revealFace = i == revealIndex;
                bool translucentOccluder = _referenceLayout && i > revealIndex
                    && Overlaps(TileBounds(row[i]), revealBounds);
                AddTile(vh, row[i], i, revealFace || translucentOccluder, revealFace);
            }
        }

        private void AddTile(VertexHelper vh, MahjongWallTileGeometry tile, int index,
            bool translucentShell, bool revealMiddleFace)
        {
            float variation = (index % 3 - 1) * .012f;
            Color[] colors = _referenceLayout
                ? new[] {
                    new Color(.10f, .045f, .012f, .20f),
                    new Color(.53f, .32f, .12f, 1f),
                    new Color(.94f, .76f, .50f, 1f),
                    new Color(.98f + variation, .55f + variation, .12f, 1f)
                }
                : new[] {
                    new Color(.008f, .022f, .027f, .22f),
                    new Color(.43f, .55f, .49f, 1f),
                    new Color(.79f, .84f, .74f, 1f),
                    new Color(.08f + variation * 1.5f, .34f + variation * 1.5f, .31f + variation * 1.5f, 1f)
                };
            if (_referenceLayout)
            {
                AddQuad(vh, tile.ShadowFace, colors[0]);
                Color shellTint = translucentShell ? new Color(1f, 1f, 1f, .42f) : Color.white;
                if (MahjongTileFaceAtlas.Texture != null)
                {
                    AddTextureQuad(vh, tile.BottomFace, MahjongTileFaceAtlas.BottomUv, FaceUvOrder.Bottom, shellTint);
                    AddTextureQuad(vh, tile.SideFace, MahjongTileFaceAtlas.SideUv, FaceUvOrder.Side, shellTint);
                    AddTextureQuad(vh, tile.TopFace, MahjongTileFaceAtlas.BottomUv, FaceUvOrder.Bottom, shellTint);
                    AddTextureQuad(vh, tile.BackFace, MahjongTileFaceAtlas.BackUv, FaceUvOrder.Standard, shellTint);
                }
                else
                {
                    Color bottom = new Color(.44f, .25f, .09f, translucentShell ? .42f : 1f);
                    Color side = colors[1], top = colors[2];
                    if (translucentShell) { side.a = .42f; top.a = .42f; }
                    AddQuad(vh, tile.BottomFace, bottom);
                    AddQuad(vh, tile.SideFace, side);
                    AddQuad(vh, tile.TopFace, top);
                    AddQuad(vh, tile.BackFace, revealMiddleFace
                        ? new Color(.50f, .30f, .10f, .42f)
                        : new Color(.50f, .30f, .10f, 1f));
                    Color backMark = colors[3];
                    if (translucentShell) backMark.a = .42f;
                    AddQuad(vh, Inset(tile.BackFace, 1.5f), backMark);
                }
            }
            else
            {
                for (int q = 0; q < tile.Quads.Length; q++) AddQuad(vh, tile.Quads[q], colors[q]);
            }
            if (tile.ShowBackMark)
            {
                AddTriangle(vh, tile.Triangles[0], new Color(.62f, .82f, .69f, 1f));
                AddTriangle(vh, tile.Triangles[1], new Color(.31f, .58f, .49f, 1f));
            }
        }

        private static Rect TileBounds(MahjongWallTileGeometry tile)
        {
            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < tile.Quads.Length; i++) Include(tile.Quads[i], ref minX, ref minY, ref maxX, ref maxY);
            Include(tile.BottomFace, ref minX, ref minY, ref maxX, ref maxY);
            return Rect.MinMaxRect(minX, minY, maxX, maxY);
        }

        private static void Include(MahjongWallPoint2[] points, ref float minX, ref float minY,
            ref float maxX, ref float maxY)
        {
            for (int i = 0; i < points.Length; i++)
            {
                minX = Mathf.Min(minX, points[i].X);
                minY = Mathf.Min(minY, points[i].Y);
                maxX = Mathf.Max(maxX, points[i].X);
                maxY = Mathf.Max(maxY, points[i].Y);
            }
        }

        private static bool Overlaps(Rect tile, Rect target)
        {
            Rect overlap = Rect.MinMaxRect(Mathf.Max(tile.xMin, target.xMin), Mathf.Max(tile.yMin, target.yMin),
                Mathf.Min(tile.xMax, target.xMax), Mathf.Min(tile.yMax, target.yMax));
            return overlap.width > .1f && overlap.height > .1f;
        }

        private static Vector2 ToUnity(MahjongWallPoint2 point) => new Vector2(point.X, -point.Y);

        private enum FaceUvOrder { Standard, Side, Bottom }

        private static void AddTextureQuad(VertexHelper vh, MahjongWallPoint2[] points, Rect rect,
            FaceUvOrder order, Color tint)
        {
            float left = rect.xMin, right = rect.xMax, bottom = rect.yMin, top = rect.yMax;
            Vector2[] uv = order switch
            {
                FaceUvOrder.Side => new[] { new Vector2(left, bottom), new Vector2(right, bottom),
                    new Vector2(right, top), new Vector2(left, top) },
                FaceUvOrder.Bottom => new[] { new Vector2(left, top), new Vector2(right, top),
                    new Vector2(right, bottom), new Vector2(left, bottom) },
                _ => new[] { new Vector2(left, bottom), new Vector2(right, bottom),
                    new Vector2(right, top), new Vector2(left, top) }
            };
            AddQuad(vh, points, uv, tint);
        }

        private static void AddQuad(VertexHelper vh, MahjongWallPoint2[] points, Color color)
        {
            AddQuad(vh, points, new[] { Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero }, color);
        }

        private static void AddQuad(VertexHelper vh, MahjongWallPoint2[] points, Vector2[] uv, Color color)
        {
            float area1 = TriangleArea(points[0], points[1], points[2]);
            float area2 = TriangleArea(points[0], points[2], points[3]);
            if (area1 <= .0001f || area2 <= .0001f)
                throw new InvalidOperationException("Projected mahjong tile quad contains a zero-area triangle.");
            int start = vh.currentVertCount;
            for (int i = 0; i < 4; i++) vh.AddVert(ToUnity(points[i]), color, uv[i]);
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

        private static MahjongWallPoint2[] Inset(MahjongWallPoint2[] points, float amount)
        {
            MahjongWallPoint2 center = new MahjongWallPoint2(0f, 0f);
            for (int i = 0; i < points.Length; i++) center += points[i];
            center *= .25f;
            var inset = new MahjongWallPoint2[points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                MahjongWallPoint2 delta = points[i] - center;
                float length = Mathf.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
                float scale = Mathf.Max(0f, length - amount) / length;
                inset[i] = center + delta * scale;
            }
            return inset;
        }

        private static float TriangleArea(MahjongWallPoint2 a, MahjongWallPoint2 b, MahjongWallPoint2 c)
        {
            return Mathf.Abs((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X)) * .5f;
        }
    }
}
