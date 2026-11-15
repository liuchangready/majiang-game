#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Mahjong.Unity.UI.Prototype
{
    public struct MahjongWallTileFrontSpec
    {
        public MahjongWallSurface Face;
        public float UvRotationDegrees;

        public MahjongWallTileFrontSpec(MahjongWallSurface face, float uvRotationDegrees)
        {
            Face = face;
            UvRotationDegrees = uvRotationDegrees;
        }
    }

    public struct MahjongExplicitTileGeometry
    {
        public MahjongWallPoint2[] BackFace;
        public MahjongWallPoint2[] FrontFace;
        public MahjongWallPoint2[] TopFace;
        public MahjongWallPoint2[] BottomFace;
        public MahjongWallPoint2[] StartEndFace;
        public MahjongWallPoint2[] EndEndFace;
    }

    public enum MahjongWallOuterPoint
    {
        A1, B1, C1, D1, A2, B2, C2, D2
    }

    public struct MahjongWallSkeleton
    {
        public MahjongWallPoint2 A1, B1, C1, D1;
        public MahjongWallPoint2 A2, B2, C2, D2;
        public float TilePitch;
        public float TileFaceWidth;
    }

    public struct MahjongWallRenderView
    {
        public float ScreenBottomY;
        public MahjongWallPoint2 TableCenter;

        public MahjongWallRenderView(float screenBottomY)
        {
            ScreenBottomY = screenBottomY;
            TableCenter = new MahjongWallPoint2(640f, 360f);
        }
    }

    /// <summary>Renders specified front/back faces and only the exposed seams for one wall row.</summary>
    [AddComponentMenu("")]
    public sealed class MahjongExplicitFrontWallGraphic : MaskableGraphic
    {
        private MahjongWallAxisFrame _axisFrame;
        private MahjongWallRenderView _view = new MahjongWallRenderView(720f);
        private MahjongWallTileFrontSpec[] _tileSpecs = Array.Empty<MahjongWallTileFrontSpec>();
        private MahjongWallOuterPoint _outerPoint = MahjongWallOuterPoint.B1;
        private float _faceWidthOverride;
        private float _widthShrinkPercent;
        private int _gapAfterTileIndex = -1;
        private float _extraGap;

        public override Texture mainTexture => MahjongTileFaceAtlas.Texture != null
            ? MahjongTileFaceAtlas.Texture : s_WhiteTexture;

        public void Configure(MahjongWallAxisFrame axisFrame,
            MahjongWallTileFrontSpec[] tileSpecs, float faceWidthOverride = 0f,
            int gapAfterTileIndex = -1, float extraGap = 0f,
            MahjongWallOuterPoint outerPoint = MahjongWallOuterPoint.B1,
            float widthShrinkPercent = 0f)
        {
            _axisFrame = axisFrame;
            _tileSpecs = tileSpecs ?? Array.Empty<MahjongWallTileFrontSpec>();
            _faceWidthOverride = faceWidthOverride;
            _widthShrinkPercent = Mathf.Clamp(widthShrinkPercent, 0f, 100f);
            _gapAfterTileIndex = gapAfterTileIndex;
            _extraGap = extraGap;
            _outerPoint = outerPoint;
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

        public void SetRenderView(MahjongWallRenderView view)
        {
            _view = view;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (_tileSpecs.Length == 0 || _axisFrame.AxisLength <= 0f) return;

            MahjongWallSkeleton skeleton = BuildSkeleton(_axisFrame, _tileSpecs.Length,
                _faceWidthOverride, _gapAfterTileIndex, _extraGap);
            MahjongExplicitTileGeometry[] tiles = BuildTileGeometry(skeleton, _tileSpecs.Length,
                _gapAfterTileIndex, _extraGap, _widthShrinkPercent);
            int[] drawOrder = new int[tiles.Length];
            for (int i = 0; i < drawOrder.Length; i++) drawOrder[i] = i;

            // Sort back to front so any projected overlap is covered by the nearer tile.
            Array.Sort(drawOrder, (left, right) => DistanceToBottom(tiles[right].FrontFace, _view)
                .CompareTo(DistanceToBottom(tiles[left].FrontFace, _view)));

            for (int drawIndex = 0; drawIndex < tiles.Length; drawIndex++)
            {
                int tileIndex = drawOrder[drawIndex];
                MahjongExplicitTileGeometry tile = tiles[tileIndex];
                MahjongWallTileFrontSpec spec = _tileSpecs[tileIndex];
                MahjongWallSurface[] outerFaces = GetOuterSurfaces(tile, _outerPoint,
                    tileIndex, tiles.Length);
                for (int faceIndex = 0; faceIndex < outerFaces.Length; faceIndex++)
                {
                    MahjongWallSurface surface = outerFaces[faceIndex];
                    MahjongWallPoint2[] face = Surface(tile, surface);
                    Rect uv = TextureForSurface(surface);
                    Vector2[] faceUv = UvForSurface(face, surface, uv, _view.TableCenter,
                        spec.UvRotationDegrees);
                    AddQuad(vh, face, faceUv, Color.white);
                }
            }
        }

        public static MahjongWallSkeleton BuildSkeleton(MahjongWallAxisFrame frame,
            int tileCount, float faceWidthOverride = 0f, int gapAfterTileIndex = -1, float extraGap = 0f)
        {
            int count = Mathf.Max(0, tileCount);
            if (count == 0 || frame.AxisLength <= 0f) return default;
            float gap = gapAfterTileIndex >= 0 && gapAfterTileIndex < count - 1
                ? Mathf.Max(0f, extraGap) : 0f;
            float pitch = count <= 1 ? frame.AxisLength
                : Mathf.Max(.0001f, frame.AxisLength - gap) / (count - 1);
            float faceWidth = faceWidthOverride > .0001f ? faceWidthOverride
                : Mathf.Max(1f, pitch - 1f);
            float radians = frame.AxisSlopeDegrees * Mathf.Deg2Rad;
            var rowAxis = new MahjongWallPoint2(Mathf.Cos(radians), Mathf.Sin(radians));
            float totalLength = pitch * Mathf.Max(0, count - 1) + gap + faceWidth;
            MahjongWallPoint2 a1 = frame.Axis1Start;
            MahjongWallPoint2 b1 = frame.Axis2Start;
            MahjongWallPoint2 c1 = frame.Axis3Start;
            MahjongWallPoint2 d1 = b1 + c1 - a1;
            MahjongWallPoint2 row = rowAxis * totalLength;
            return new MahjongWallSkeleton
            {
                A1 = a1, B1 = b1, C1 = c1, D1 = d1,
                A2 = a1 + row, B2 = b1 + row, C2 = c1 + row, D2 = d1 + row,
                TilePitch = pitch, TileFaceWidth = faceWidth
            };
        }

        public static MahjongExplicitTileGeometry[] BuildTileGeometry(MahjongWallSkeleton skeleton,
            int tileCount, int gapAfterTileIndex = -1, float extraGap = 0f,
            float widthShrinkPercent = 0f)
        {
            int count = Mathf.Max(0, tileCount);
            var tiles = new MahjongExplicitTileGeometry[count];
            if (count == 0) return tiles;
            float gap = gapAfterTileIndex >= 0 && gapAfterTileIndex < count - 1
                ? Mathf.Max(0f, extraGap) : 0f;
            float fullLength = Mathf.Max(.0001f, skeleton.TilePitch * Mathf.Max(0, count - 1)
                + gap + skeleton.TileFaceWidth);
            float regularGap = Mathf.Max(0f, skeleton.TilePitch - skeleton.TileFaceWidth);
            float widthRetention = 1f - Mathf.Clamp(widthShrinkPercent, 0f, 100f) * .01f;
            float widthWeightSum = 0f;
            for (int i = 0; i < count; i++) widthWeightSum += Mathf.Pow(widthRetention, i);
            float totalTileWidth = Mathf.Max(.0001f,
                fullLength - regularGap * (count - 1) - gap);
            float firstTileWidth = totalTileWidth / Mathf.Max(.0001f, widthWeightSum);
            MahjongWallPoint2 Lerp(MahjongWallPoint2 start, MahjongWallPoint2 end, float t) =>
                start + (end - start) * t;
            float cursor = 0f;
            for (int i = 0; i < count; i++)
            {
                if (i > 0) cursor += regularGap;
                if (gapAfterTileIndex >= 0 && i > gapAfterTileIndex) cursor += gap;
                float tileWidth = firstTileWidth * Mathf.Pow(widthRetention, i);
                float offset = cursor;
                float startT = offset / fullLength;
                float endT = (offset + tileWidth) / fullLength;
                MahjongWallPoint2 a1 = Lerp(skeleton.A1, skeleton.A2, startT);
                MahjongWallPoint2 b1 = Lerp(skeleton.B1, skeleton.B2, startT);
                MahjongWallPoint2 c1 = Lerp(skeleton.C1, skeleton.C2, startT);
                MahjongWallPoint2 d1 = Lerp(skeleton.D1, skeleton.D2, startT);
                MahjongWallPoint2 a2 = Lerp(skeleton.A1, skeleton.A2, endT);
                MahjongWallPoint2 b2 = Lerp(skeleton.B1, skeleton.B2, endT);
                MahjongWallPoint2 c2 = Lerp(skeleton.C1, skeleton.C2, endT);
                MahjongWallPoint2 d2 = Lerp(skeleton.D1, skeleton.D2, endT);
                tiles[i] = new MahjongExplicitTileGeometry
                {
                    BackFace = new[] { a1, b1, b2, a2 },
                    FrontFace = new[] { c1, d1, d2, c2 },
                    TopFace = new[] { b1, d1, d2, b2 },
                    BottomFace = new[] { a1, c1, c2, a2 },
                    StartEndFace = new[] { a1, b1, d1, c1 },
                    EndEndFace = new[] { a2, b2, d2, c2 }
                };
                cursor += tileWidth;
            }
            return tiles;
        }

        public static MahjongWallSurface[] GetOuterSurfaces(MahjongExplicitTileGeometry tile,
            MahjongWallOuterPoint outerPoint, int tileIndex, int tileCount)
        {
            var faces = new List<MahjongWallSurface>(3);
            MahjongWallSurface[] longitudinalFaces =
            {
                MahjongWallSurface.Back, MahjongWallSurface.Front,
                MahjongWallSurface.Top, MahjongWallSurface.Bottom
            };
            for (int i = 0; i < longitudinalFaces.Length; i++)
            {
                MahjongWallSurface surface = longitudinalFaces[i];
                if (ContainsCorner(Surface(tile, surface), tile, outerPoint))
                    faces.Add(surface);
            }

            if (IsStartPoint(outerPoint) && tileIndex == 0)
                faces.Add(MahjongWallSurface.LeftSide);
            else if (IsEndPoint(outerPoint) && tileIndex == tileCount - 1)
                faces.Add(MahjongWallSurface.RightSide);
            return faces.ToArray();
        }

        public static MahjongWallPoint2[] Surface(MahjongExplicitTileGeometry tile,
            MahjongWallSurface surface)
        {
            return surface switch
            {
                MahjongWallSurface.Back => tile.BackFace,
                MahjongWallSurface.Top => tile.TopFace,
                MahjongWallSurface.Bottom => tile.BottomFace,
                MahjongWallSurface.LeftSide => tile.StartEndFace,
                MahjongWallSurface.RightSide => tile.EndEndFace,
                _ => tile.FrontFace
            };
        }

        private static bool ContainsCorner(MahjongWallPoint2[] face,
            MahjongExplicitTileGeometry tile, MahjongWallOuterPoint corner)
        {
            MahjongWallPoint2 point = Corner(tile, corner);
            for (int i = 0; i < face.Length; i++)
                if (Mathf.Abs(face[i].X - point.X) < .001f
                    && Mathf.Abs(face[i].Y - point.Y) < .001f)
                    return true;
            return false;
        }

        private static MahjongWallPoint2 Corner(MahjongExplicitTileGeometry tile,
            MahjongWallOuterPoint corner)
        {
            return corner switch
            {
                MahjongWallOuterPoint.A1 => tile.BackFace[0],
                MahjongWallOuterPoint.B1 => tile.BackFace[1],
                MahjongWallOuterPoint.B2 => tile.BackFace[2],
                MahjongWallOuterPoint.A2 => tile.BackFace[3],
                MahjongWallOuterPoint.C1 => tile.FrontFace[0],
                MahjongWallOuterPoint.D1 => tile.FrontFace[1],
                MahjongWallOuterPoint.D2 => tile.FrontFace[2],
                _ => tile.FrontFace[3]
            };
        }

        private static bool IsStartPoint(MahjongWallOuterPoint point) =>
            point == MahjongWallOuterPoint.A1 || point == MahjongWallOuterPoint.B1
            || point == MahjongWallOuterPoint.C1 || point == MahjongWallOuterPoint.D1;

        private static bool IsEndPoint(MahjongWallOuterPoint point) => !IsStartPoint(point);

        private static Rect TextureForSurface(MahjongWallSurface surface)
        {
            return surface switch
            {
                MahjongWallSurface.Back => MahjongTileFaceAtlas.BackUv,
                MahjongWallSurface.Front => MahjongTileFaceAtlas.BackUv,
                MahjongWallSurface.Top => MahjongTileFaceAtlas.BottomUv,
                MahjongWallSurface.Bottom => MahjongTileFaceAtlas.BottomUv,
                _ => MahjongTileFaceAtlas.SideUv
            };
        }

        private static Vector2[] UvForSurface(MahjongWallPoint2[] face,
            MahjongWallSurface surface, Rect texture, MahjongWallPoint2 tableCenter,
            float faceRotationDegrees)
        {
            return surface switch
            {
                MahjongWallSurface.Back => BuildTileFaceUv(texture, faceRotationDegrees),
                MahjongWallSurface.Front => BuildTileFaceUv(texture, faceRotationDegrees),
                MahjongWallSurface.Top => BuildHorizontalFaceUv(face, texture, tableCenter),
                MahjongWallSurface.Bottom => BuildHorizontalFaceUv(face, texture, tableCenter),
                _ => BuildEndFaceUv(face, texture, tableCenter)
            };
        }

        private static Vector2[] BuildTileFaceUv(Rect rect, float rotationDegrees)
        {
            // Face quad order is bottom-left, top-left, top-right, bottom-right:
            // its portrait axis follows the tile height, not the wall-row axis.
            Vector2 bl = new Vector2(rect.xMin, rect.yMin);
            Vector2 tl = new Vector2(rect.xMin, rect.yMax);
            Vector2 tr = new Vector2(rect.xMax, rect.yMax);
            Vector2 br = new Vector2(rect.xMax, rect.yMin);
            float rotation = Mathf.Repeat(rotationDegrees, 360f);
            if (rotation >= 45f && rotation < 135f) return new[] { br, tr, tl, bl };
            if (rotation >= 135f && rotation < 225f) return new[] { tr, br, bl, tl };
            if (rotation >= 225f && rotation < 315f) return new[] { tl, bl, br, tr };
            return new[] { bl, tl, tr, br };
        }

        private static Vector2[] BuildHorizontalFaceUv(MahjongWallPoint2[] face, Rect rect,
            MahjongWallPoint2 tableCenter)
        {
            if (face == null || face.Length != 4) return RectUv(rect);

            // The face quad is [row-start/outer, depth-start, depth-end, row-end].
            // Keep the texture's wide axis along the row; put its yellow band on the
            // depth edge closer to the table center.
            Vector2 white = new Vector2(rect.xMin, rect.yMax);
            Vector2 yellow = new Vector2(rect.xMin, rect.yMin);
            Vector2 whiteEnd = new Vector2(rect.xMax, rect.yMax);
            Vector2 yellowEnd = new Vector2(rect.xMax, rect.yMin);
            bool depthEndFacesTable = EdgeMidpointDistanceSquared(face[1], face[2], tableCenter)
                < EdgeMidpointDistanceSquared(face[0], face[3], tableCenter);
            return depthEndFacesTable
                ? new[] { white, yellow, yellowEnd, whiteEnd }
                : new[] { yellow, white, whiteEnd, yellowEnd };
        }

        private static Vector2[] BuildEndFaceUv(MahjongWallPoint2[] face, Rect rect,
            MahjongWallPoint2 tableCenter)
        {
            if (face == null || face.Length != 4) return RectUv(rect);

            // End-face order is A, B, D, C: A-B and C-D are the tile-height
            // edges; A-C and B-D span thickness. Keep this semantic mapping even
            // when perspective makes the projected thickness look longer.
            bool firstRailFacesTable = EdgeMidpointDistanceSquared(face[0], face[1], tableCenter)
                < EdgeMidpointDistanceSquared(face[3], face[2], tableCenter);

            // SideUv is portrait, with white at U-min and yellow at U-max.
            // Its V axis follows the longer (tile-height) edge pair. Select U by
            // table-center proximity so the yellow half faces inward.
            float u0 = firstRailFacesTable ? rect.xMax : rect.xMin;
            float u1 = firstRailFacesTable ? rect.xMin : rect.xMax;
            float v0 = rect.yMin;
            float v1 = rect.yMax;
            return new[]
            {
                new Vector2(u0, v0), new Vector2(u0, v1),
                new Vector2(u1, v1), new Vector2(u1, v0)
            };
        }

        private static Vector2[] RectUv(Rect rect)
        {
            return new[]
            {
                new Vector2(rect.xMin, rect.yMin), new Vector2(rect.xMax, rect.yMin),
                new Vector2(rect.xMax, rect.yMax), new Vector2(rect.xMin, rect.yMax)
            };
        }

        private static float EdgeMidpointDistanceSquared(MahjongWallPoint2 a,
            MahjongWallPoint2 b, MahjongWallPoint2 point)
        {
            float x = (a.X + b.X) * .5f - point.X;
            float y = (a.Y + b.Y) * .5f - point.Y;
            return x * x + y * y;
        }

        private static float DistanceToBottom(MahjongWallPoint2[] face, MahjongWallRenderView view)
        {
            if (face == null || face.Length == 0) return float.PositiveInfinity;
            float minY = face[0].Y;
            float maxY = face[0].Y;
            for (int i = 1; i < face.Length; i++)
            {
                minY = Mathf.Min(minY, face[i].Y);
                maxY = Mathf.Max(maxY, face[i].Y);
            }
            if (view.ScreenBottomY >= minY && view.ScreenBottomY <= maxY) return 0f;
            return Mathf.Min(Mathf.Abs(view.ScreenBottomY - minY),
                Mathf.Abs(view.ScreenBottomY - maxY));
        }

        private static void AddQuad(VertexHelper vh, MahjongWallPoint2[] points,
            Vector2[] uv, Color color)
        {
            int start = vh.currentVertCount;
            for (int i = 0; i < 4; i++)
            {
                UIVertex vertex = UIVertex.simpleVert;
                vertex.position = new Vector2(points[i].X, -points[i].Y);
                vertex.uv0 = uv[i];
                vertex.color = color;
                vh.AddVert(vertex);
            }
            vh.AddTriangle(start, start + 1, start + 2);
            vh.AddTriangle(start, start + 2, start + 3);
        }

    }
}
