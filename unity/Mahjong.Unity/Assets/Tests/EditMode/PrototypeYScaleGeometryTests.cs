using Mahjong.Unity.UI.Prototype;
using NUnit.Framework;
using UnityEngine;

namespace Mahjong.Unity.Tests.EditMode
{
    public sealed class PrototypeYScaleGeometryTests
    {
        private const int Count = 6;
        private const float Falloff = .004f;

        [TestCase(75f)]
        [TestCase(-75f)]
        [TestCase(105f)]
        [TestCase(-105f)]
        public void YFalloffScalesBothEdgesOfEveryFaceByTheSameFactor(float slope)
        {
            MahjongWallSkeleton skeleton = MahjongExplicitFrontWallGraphic.BuildSkeleton(Frame(slope), Count);
            MahjongExplicitTileGeometry[] baseline = Build(skeleton, 0f);
            MahjongExplicitTileGeometry[] scaled = Build(skeleton, Falloff);
            float nearY = Mathf.Max(skeleton.A1.Y, skeleton.A2.Y);
            for (int tile = 0; tile < Count; tile++)
            {
                float centerY = Mathf.Lerp(skeleton.A1.Y, skeleton.A2.Y, (tile + .5f) / Count);
                float expectedScale = Mathf.Exp(-Falloff * (nearY - centerY));
                MahjongWallPoint2[][] originalFaces = Faces(baseline[tile]);
                MahjongWallPoint2[][] scaledFaces = Faces(scaled[tile]);
                for (int face = 0; face < originalFaces.Length; face++)
                    for (int edge = 0; edge < 4; edge++)
                    {
                        int next = (edge + 1) % 4;
                        float originalLength = Distance(originalFaces[face][edge], originalFaces[face][next]);
                        float scaledLength = Distance(scaledFaces[face][edge], scaledFaces[face][next]);
                        Assert.That(scaledLength / originalLength, Is.EqualTo(expectedScale).Within(.0002f),
                            "tile " + tile + ", face " + face + ", edge " + edge);
                    }
            }
        }

        [TestCase(0f)]
        [TestCase(-.004f)]
        public void NonPositiveCoefficientKeepsTheOriginalGeometry(float coefficient)
        {
            MahjongWallSkeleton skeleton = MahjongExplicitFrontWallGraphic.BuildSkeleton(Frame(75f), Count);
            AssertGeometry(Build(skeleton, 0f), Build(skeleton, coefficient), Vector2.zero);
        }

        [Test]
        public void HorizontalRowDoesNotShrinkWhenAllTilesHaveTheSameY()
        {
            MahjongWallSkeleton skeleton = MahjongExplicitFrontWallGraphic.BuildSkeleton(Frame(0f), Count);
            AssertGeometry(Build(skeleton, 0f), Build(skeleton, Falloff), Vector2.zero);
        }

        [Test]
        public void DisabledYModeKeepsTheLegacyPercentTaper()
        {
            MahjongWallSkeleton skeleton = MahjongExplicitFrontWallGraphic.BuildSkeleton(Frame(75f), Count);
            MahjongExplicitTileGeometry[] baseline = MahjongExplicitFrontWallGraphic.BuildTileGeometry(
                skeleton, Count, widthShrinkPercent: 8f, widthByYAxis: false, widthFalloffPerScreenY: 0f);
            MahjongExplicitTileGeometry[] actual = MahjongExplicitFrontWallGraphic.BuildTileGeometry(
                skeleton, Count, widthShrinkPercent: 8f, widthByYAxis: false, widthFalloffPerScreenY: Falloff);
            AssertGeometry(baseline, actual, Vector2.zero);
            float sectionWidth = Distance(actual[0].StartEndFace[0], actual[0].StartEndFace[1]);
            for (int tile = 1; tile < Count; tile++)
                Assert.That(Distance(actual[tile].StartEndFace[0], actual[tile].StartEndFace[1]),
                    Is.EqualTo(sectionWidth).Within(.001f));
        }

        [Test]
        public void MovingTheWallDoesNotChangeItsRelativePerspectiveScale()
        {
            MahjongWallAxisFrame original = Frame(75f);
            MahjongWallAxisFrame moved = original;
            var offset = new MahjongWallPoint2(-120f, 300f);
            moved.Axis1Start += offset;
            moved.Axis2Start += offset;
            moved.Axis3Start += offset;
            AssertGeometry(
                Build(MahjongExplicitFrontWallGraphic.BuildSkeleton(original, Count), Falloff),
                Build(MahjongExplicitFrontWallGraphic.BuildSkeleton(moved, Count), Falloff),
                new Vector2(offset.X, offset.Y));
        }

        private static MahjongWallAxisFrame Frame(float slope) => new MahjongWallAxisFrame(
            new MahjongWallPoint2(240f, 400f), new MahjongWallPoint2(236f, 365f),
            new MahjongWallPoint2(258f, 409f), 180f, slope);

        private static MahjongExplicitTileGeometry[] Build(MahjongWallSkeleton skeleton, float coefficient) =>
            MahjongExplicitFrontWallGraphic.BuildTileGeometry(skeleton, Count,
                widthByYAxis: true, widthFalloffPerScreenY: coefficient);

        private static MahjongWallPoint2[][] Faces(MahjongExplicitTileGeometry tile) => new[]
        {
            tile.BackFace, tile.FrontFace, tile.TopFace, tile.BottomFace, tile.StartEndFace, tile.EndEndFace
        };

        private static float Distance(MahjongWallPoint2 a, MahjongWallPoint2 b) =>
            Vector2.Distance(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y));

        private static void AssertGeometry(MahjongExplicitTileGeometry[] expected,
            MahjongExplicitTileGeometry[] actual, Vector2 offset)
        {
            Assert.AreEqual(expected.Length, actual.Length);
            for (int tile = 0; tile < expected.Length; tile++)
            {
                MahjongWallPoint2[][] expectedFaces = Faces(expected[tile]);
                MahjongWallPoint2[][] actualFaces = Faces(actual[tile]);
                for (int face = 0; face < expectedFaces.Length; face++)
                    for (int vertex = 0; vertex < 4; vertex++)
                    {
                        Assert.That(actualFaces[face][vertex].X,
                            Is.EqualTo(expectedFaces[face][vertex].X + offset.x).Within(.001f));
                        Assert.That(actualFaces[face][vertex].Y,
                            Is.EqualTo(expectedFaces[face][vertex].Y + offset.y).Within(.001f));
                    }
            }
        }
    }
}
