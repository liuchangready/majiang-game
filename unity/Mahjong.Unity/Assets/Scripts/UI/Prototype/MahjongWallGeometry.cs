using System;
using System.Collections.Generic;

namespace Mahjong.Unity.UI.Prototype
{
    public enum MahjongWallSeat { North, West, East }

    public struct MahjongWallPoint3
    {
        public float X, Y, Z;
        public MahjongWallPoint3(float x, float y, float z) { X = x; Y = y; Z = z; }
        public static MahjongWallPoint3 operator +(MahjongWallPoint3 a, MahjongWallPoint3 b) { return new MahjongWallPoint3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        public static MahjongWallPoint3 operator -(MahjongWallPoint3 a, MahjongWallPoint3 b) { return new MahjongWallPoint3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        public static MahjongWallPoint3 operator *(MahjongWallPoint3 a, float scale) { return new MahjongWallPoint3(a.X * scale, a.Y * scale, a.Z * scale); }
    }

    public struct MahjongWallPoint2
    {
        public float X, Y;
        public MahjongWallPoint2(float x, float y) { X = x; Y = y; }
        public static MahjongWallPoint2 operator +(MahjongWallPoint2 a, MahjongWallPoint2 b) { return new MahjongWallPoint2(a.X + b.X, a.Y + b.Y); }
        public static MahjongWallPoint2 operator -(MahjongWallPoint2 a, MahjongWallPoint2 b) { return new MahjongWallPoint2(a.X - b.X, a.Y - b.Y); }
        public static MahjongWallPoint2 operator *(MahjongWallPoint2 a, float scale) { return new MahjongWallPoint2(a.X * scale, a.Y * scale); }
    }

    public sealed class MahjongWallTileGeometry
    {
        public MahjongWallPoint3 Origin;
        public float Width, Height, Depth, Gap, PerspectiveScale;
        public MahjongWallPoint2[][] Quads;
        public MahjongWallPoint2[][] Triangles;
    }

    /// <summary>Canonical table-local tile chassis and one pinhole-style table projection shared by every seat.</summary>
    public static class MahjongWallGeometry
    {
        public const float TileWidth = .115f;
        public const float TileHeight = .17f;
        public const float TileDepth = .02f;
        public const float TileGap = .003f;
        public const float FarWallY = 1.3f;
        private const float CameraDistance = 3.2f;
        private const float FocalLength = 1350f;
        private const float TableVerticalSlope = .62f;
        private const float TileVerticalSlope = .82f;
        private const float ProjectionCenterX = 640f;
        private const float ProjectionCenterY = 330f;
        private const float Epsilon = .0001f;

        public static int TileCount(MahjongWallSeat seat) { return seat == MahjongWallSeat.North ? 14 : 11; }

        public static MahjongWallTileGeometry[] BuildRow(MahjongWallSeat seat)
        {
            int count = TileCount(seat);
            var row = new MahjongWallTileGeometry[count];
            for (int i = 0; i < count; i++) row[i] = BuildTile(seat, i, count, TileWidth, TileHeight, TileDepth, TileGap);
            return row;
        }

        private static MahjongWallTileGeometry BuildTile(MahjongWallSeat seat, int index, int count,
            float width, float height, float depth, float gap)
        {
            float step = width + gap;
            float span = count * width + (count - 1) * gap;
            MahjongWallPoint3 origin;
            MahjongWallPoint3 tangent;
            MahjongWallPoint3 inward;
            switch (seat)
            {
                case MahjongWallSeat.West:
                    origin = new MahjongWallPoint3(-NorthWallHalfSpan(), FarWallY - index * step, 0f);
                    tangent = new MahjongWallPoint3(0f, -1f, 0f);
                    inward = new MahjongWallPoint3(1f, 0f, 0f);
                    break;
                case MahjongWallSeat.East:
                    origin = new MahjongWallPoint3(NorthWallHalfSpan(), FarWallY - index * step, 0f);
                    tangent = new MahjongWallPoint3(0f, -1f, 0f);
                    inward = new MahjongWallPoint3(-1f, 0f, 0f);
                    break;
                default:
                    origin = new MahjongWallPoint3(-span * .5f + index * step, FarWallY, 0f);
                    tangent = new MahjongWallPoint3(1f, 0f, 0f);
                    inward = new MahjongWallPoint3(0f, -1f, 0f);
                    break;
            }

            var tile = new MahjongWallTileGeometry
            {
                Origin = origin,
                Width = width,
                Height = height,
                Depth = depth,
                Gap = gap,
                PerspectiveScale = FocalLength / (CameraDistance + origin.Y - height * .5f * .35f)
            };
            var back = new[] { Project(Point(seat, origin, tangent, inward, width, height, depth, 0f, 1f, 0f)),
                Project(Point(seat, origin, tangent, inward, width, height, depth, 1f, 1f, 0f)),
                Project(Point(seat, origin, tangent, inward, width, height, depth, 1f, 1f, 1f)),
                Project(Point(seat, origin, tangent, inward, width, height, depth, 0f, 1f, 1f)) };
            var side = new[] { Project(Point(seat, origin, tangent, inward, width, height, depth, 1f, 0f, 0f)),
                Project(Point(seat, origin, tangent, inward, width, height, depth, 1f, 1f, 0f)),
                Project(Point(seat, origin, tangent, inward, width, height, depth, 1f, 1f, 1f)),
                Project(Point(seat, origin, tangent, inward, width, height, depth, 1f, 0f, 1f)) };
            var top = new[] { Project(Point(seat, origin, tangent, inward, width, height, depth, 0f, 0f, 1f)),
                Project(Point(seat, origin, tangent, inward, width, height, depth, 1f, 0f, 1f)),
                Project(Point(seat, origin, tangent, inward, width, height, depth, 1f, 1f, 1f)),
                Project(Point(seat, origin, tangent, inward, width, height, depth, 0f, 1f, 1f)) };
            tile.Quads = new[] { Offset(back, 0f, 5f), side, top, back };
            MahjongWallPoint2 center = Bilinear(back, .5f, .5f);
            MahjongWallPoint2 up = Bilinear(back, .5f, .30f);
            MahjongWallPoint2 right = Bilinear(back, .70f, .5f);
            MahjongWallPoint2 down = Bilinear(back, .5f, .70f);
            MahjongWallPoint2 left = Bilinear(back, .30f, .5f);
            tile.Triangles = new[] { new[] { up, right, down }, new[] { up, down, left } };
            return tile;
        }

        private static MahjongWallPoint3 Point(MahjongWallSeat seat, MahjongWallPoint3 origin,
            MahjongWallPoint3 tangent, MahjongWallPoint3 inward, float width, float height, float depth,
            float u, float v, float z)
        {
            return origin + tangent * (width * u) + inward * (depth * v) + new MahjongWallPoint3(0f, 0f, height * z);
        }

        private static MahjongWallPoint2 Project(MahjongWallPoint3 point)
        {
            float cameraDepth = CameraDistance + point.Y - point.Z * .35f;
            if (cameraDepth <= .1f) throw new InvalidOperationException("Wall point crossed the projection plane.");
            float scale = FocalLength / cameraDepth;
            return new MahjongWallPoint2(ProjectionCenterX + scale * point.X,
                ProjectionCenterY - scale * (TableVerticalSlope * point.Y + TileVerticalSlope * point.Z));
        }

        private static MahjongWallPoint2[] Offset(MahjongWallPoint2[] points, float x, float y)
        {
            var result = new MahjongWallPoint2[points.Length];
            for (int i = 0; i < points.Length; i++) result[i] = points[i] + new MahjongWallPoint2(x, y);
            return result;
        }

        private static MahjongWallPoint2 Bilinear(MahjongWallPoint2[] quad, float u, float v)
        {
            return quad[0] * ((1f - u) * (1f - v)) + quad[1] * (u * (1f - v))
                + quad[2] * (u * v) + quad[3] * ((1f - u) * v);
        }

        private static MahjongWallPoint3 ExpectedOrigin(MahjongWallSeat seat, int index, int count, float width, float gap)
        {
            float step = width + gap;
            float span = count * width + (count - 1) * gap;
            if (seat == MahjongWallSeat.West) return new MahjongWallPoint3(-NorthWallHalfSpan(), FarWallY - index * step, 0f);
            if (seat == MahjongWallSeat.East) return new MahjongWallPoint3(NorthWallHalfSpan(), FarWallY - index * step, 0f);
            return new MahjongWallPoint3(-span * .5f + index * step, FarWallY, 0f);
        }

        private static float NorthWallHalfSpan()
        {
            int count = TileCount(MahjongWallSeat.North);
            return (count * TileWidth + (count - 1) * TileGap) * .5f;
        }

        private static bool ValidateRow(MahjongWallSeat seat, MahjongWallTileGeometry[] row)
        {
            if (row == null || row.Length != TileCount(seat)) return false;
            for (int i = 0; i < row.Length; i++)
            {
                MahjongWallTileGeometry tile = row[i];
                MahjongWallPoint3 expected = ExpectedOrigin(seat, i, row.Length, TileWidth, TileGap);
                if (!Near(tile.Origin, expected) || Math.Abs(tile.Width - TileWidth) > Epsilon
                    || Math.Abs(tile.Height - TileHeight) > Epsilon || Math.Abs(tile.Depth - TileDepth) > Epsilon
                    || Math.Abs(tile.Gap - TileGap) > Epsilon) return false;
                MahjongWallTileGeometry rebuilt = BuildTile(seat, i, row.Length, TileWidth, TileHeight, TileDepth, TileGap);
                if (!SameMesh(tile, rebuilt)) return false;
            }
            return true;
        }

        private static bool SameMesh(MahjongWallTileGeometry a, MahjongWallTileGeometry b)
        {
            for (int q = 0; q < a.Quads.Length; q++)
                for (int p = 0; p < 4; p++) if (!Near(a.Quads[q][p], b.Quads[q][p])) return false;
            for (int t = 0; t < a.Triangles.Length; t++)
                for (int p = 0; p < 3; p++) if (!Near(a.Triangles[t][p], b.Triangles[t][p])) return false;
            return true;
        }

        private static bool Near(MahjongWallPoint3 a, MahjongWallPoint3 b)
        {
            return Math.Abs(a.X - b.X) < Epsilon && Math.Abs(a.Y - b.Y) < Epsilon && Math.Abs(a.Z - b.Z) < Epsilon;
        }

        private static bool Near(MahjongWallPoint2 a, MahjongWallPoint2 b)
        {
            return Math.Abs(a.X - b.X) < Epsilon && Math.Abs(a.Y - b.Y) < Epsilon;
        }

        private static float TriangleArea(MahjongWallPoint2 a, MahjongWallPoint2 b, MahjongWallPoint2 c)
        {
            return Math.Abs((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X)) * .5f;
        }

        private static bool Separated(MahjongWallPoint2[] a, MahjongWallPoint2[] b)
        {
            return HasSeparatingAxis(a, b) || HasSeparatingAxis(b, a);
        }

        private static bool HasSeparatingAxis(MahjongWallPoint2[] axes, MahjongWallPoint2[] other)
        {
            for (int i = 0; i < axes.Length; i++)
            {
                MahjongWallPoint2 edge = axes[(i + 1) % axes.Length] - axes[i];
                float nx = -edge.Y, ny = edge.X;
                float minA = float.MaxValue, maxA = float.MinValue, minB = float.MaxValue, maxB = float.MinValue;
                for (int p = 0; p < axes.Length; p++) { float d = axes[p].X * nx + axes[p].Y * ny; minA = Math.Min(minA, d); maxA = Math.Max(maxA, d); }
                for (int p = 0; p < other.Length; p++) { float d = other[p].X * nx + other[p].Y * ny; minB = Math.Min(minB, d); maxB = Math.Max(maxB, d); }
                if (maxA < minB - Epsilon || maxB < minA - Epsilon) return true;
            }
            return false;
        }

        public static string RunSelfCheck()
        {
            int assertions = 0, triangles = 0;
            var seatSummaries = new List<string>();
            MahjongWallSeat[] seats = { MahjongWallSeat.North, MahjongWallSeat.West, MahjongWallSeat.East };
            MahjongWallTileGeometry[][] rows = new MahjongWallTileGeometry[seats.Length][];
            for (int s = 0; s < seats.Length; s++)
            {
                rows[s] = BuildRow(seats[s]);
                Require(ValidateRow(seats[s], rows[s]), seats[s] + " canonical row"); assertions++;
                int seatTriangles = 0;
                for (int i = 0; i < rows[s].Length; i++)
                {
                    MahjongWallTileGeometry tile = rows[s][i];
                    Require(Math.Abs(tile.Width - TileWidth) < Epsilon && Math.Abs(tile.Height - TileHeight) < Epsilon
                        && Math.Abs(tile.Depth - TileDepth) < Epsilon, seats[s] + " shared chassis dimensions"); assertions++;
                    for (int q = 0; q < tile.Quads.Length; q++)
                    {
                        MahjongWallPoint2[] quad = tile.Quads[q];
                        float first = TriangleArea(quad[0], quad[1], quad[2]);
                        float second = TriangleArea(quad[0], quad[2], quad[3]);
                        Require(first > Epsilon && second > Epsilon, seats[s] + " nonzero surface triangle " + i + "/" + q);
                        triangles += 2; seatTriangles += 2; assertions += 2;
                    }
                    for (int t = 0; t < tile.Triangles.Length; t++)
                    {
                        Require(TriangleArea(tile.Triangles[t][0], tile.Triangles[t][1], tile.Triangles[t][2]) > Epsilon,
                            seats[s] + " nonzero emblem triangle " + i + "/" + t);
                        triangles++; seatTriangles++; assertions++;
                    }
                    if (i + 1 < rows[s].Length)
                    {
                        MahjongWallPoint3 end = tile.Origin + RowVector(seats[s]) * tile.Width;
                        MahjongWallPoint3 next = rows[s][i + 1].Origin;
                        MahjongWallPoint3 gap = next - end;
                        float gapLength = (float)Math.Sqrt(gap.X * gap.X + gap.Y * gap.Y + gap.Z * gap.Z);
                        Require(Math.Abs(gapLength - TileGap) < Epsilon, seats[s] + " shared seam " + i);
                        Require(Separated(tile.Quads[3], rows[s][i + 1].Quads[3]), seats[s] + " adjacent back faces do not overlap " + i);
                        assertions += 2;
                    }
                }
                if (seats[s] != MahjongWallSeat.North)
                {
                    Require(rows[s][rows[s].Length - 1].PerspectiveScale > rows[s][0].PerspectiveScale * 1.2f,
                        seats[s] + " near end perspective scale"); assertions++;
                }
                seatSummaries.Add(seats[s] + "=" + seatTriangles + ":0");
            }

            for (int i = 0; i < rows[1].Length; i++)
                for (int q = 0; q < rows[1][i].Quads.Length; q++)
                    for (int p = 0; p < 4; p++)
                    {
                        MahjongWallPoint2 west = rows[1][i].Quads[q][p];
                        MahjongWallPoint2 east = rows[2][i].Quads[q][p];
                        Require(Math.Abs(west.X + east.X - 2f * ProjectionCenterX) < .002f
                            && Math.Abs(west.Y - east.Y) < .002f, "left/right mirrored projection " + i + "/" + q + "/" + p);
                        assertions++;
                    }

            Require(Near(rows[0][0].Origin, rows[1][0].Origin), "north/west corner shares one table-local vertex"); assertions++;
            MahjongWallPoint3 northEastCorner = rows[0][rows[0].Length - 1].Origin + RowVector(MahjongWallSeat.North) * TileWidth;
            Require(Near(northEastCorner, rows[2][0].Origin), "north/east corner shares one table-local vertex"); assertions++;

            MahjongWallTileGeometry[] badOffset = BuildRow(MahjongWallSeat.West);
            badOffset[5].Origin.X += .04f;
            Require(!ValidateRow(MahjongWallSeat.West, badOffset), "negative rejects seat-specific origin offset"); assertions++;
            MahjongWallTileGeometry[] badSize = BuildRow(MahjongWallSeat.East);
            badSize[3].Width *= 1.1f;
            Require(!ValidateRow(MahjongWallSeat.East, badSize), "negative rejects seat-specific tile width"); assertions++;
            MahjongWallTileGeometry[] badSide = BuildRow(MahjongWallSeat.North);
            badSide[13].Quads[1][2] = badSide[13].Quads[1][1];
            Require(!ValidateRow(MahjongWallSeat.North, badSide), "negative rejects collapsed end side plane"); assertions++;
            return "GEOMETRY_SELF_CHECK_PASS assertions=" + assertions + " triangles=" + triangles + " zeroArea=0"
                + " seats=3 positiveRows=3 sharedCorners=2 negativeCases=3 " + string.Join(" ", seatSummaries.ToArray());
        }

        private static MahjongWallPoint3 RowVector(MahjongWallSeat seat)
        {
            return seat == MahjongWallSeat.North ? new MahjongWallPoint3(1f, 0f, 0f) : new MahjongWallPoint3(0f, -1f, 0f);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("GEOMETRY_SELF_CHECK_FAIL: " + message);
        }
    }
}
