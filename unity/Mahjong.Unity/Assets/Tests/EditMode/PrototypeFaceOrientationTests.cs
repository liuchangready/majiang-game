using System.Reflection;
using Mahjong.Unity.UI.Prototype;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Mahjong.Unity.Tests.EditMode
{
    public sealed class PrototypeFaceOrientationTests
    {
        [TestCase(MahjongWallSeat.South, MahjongWallTextureMode.FaceUp, 0f, false)]
        [TestCase(MahjongWallSeat.South, MahjongWallTextureMode.FaceUp, 0f, true)]
        [TestCase(MahjongWallSeat.South, MahjongWallTextureMode.FaceUp, 90f, false)]
        [TestCase(MahjongWallSeat.South, MahjongWallTextureMode.FaceUp, 90f, true)]
        [TestCase(MahjongWallSeat.South, MahjongWallTextureMode.FaceUp, 180f, false)]
        [TestCase(MahjongWallSeat.South, MahjongWallTextureMode.FaceUp, 180f, true)]
        [TestCase(MahjongWallSeat.South, MahjongWallTextureMode.FaceUp, 270f, false)]
        [TestCase(MahjongWallSeat.South, MahjongWallTextureMode.FaceUp, 270f, true)]
        [TestCase(MahjongWallSeat.SouthDiscard, MahjongWallTextureMode.FaceUpWithSideAndBottom, 0f, false)]
        [TestCase(MahjongWallSeat.SouthDiscard, MahjongWallTextureMode.FaceUpWithSideAndBottom, 0f, true)]
        [TestCase(MahjongWallSeat.NorthDiscard, MahjongWallTextureMode.FaceUpWithSideAndBottom, 180f, false)]
        [TestCase(MahjongWallSeat.NorthDiscard, MahjongWallTextureMode.FaceUpWithSideAndBottom, 180f, true)]
        public void FaceUpGlyphsKeepTheirSeatOrientation(MahjongWallSeat seat,
            MahjongWallTextureMode mode, float rotation, bool explicitGeometry)
        {
            WithMesh(seat, mode, rotation, explicitGeometry, mesh =>
            {
                Vector2 origin = PositionAtUv(mesh, Vector2.zero);
                Vector2 right = (PositionAtUv(mesh, Vector2.right) - origin).normalized;
                Vector2 up = (PositionAtUv(mesh, Vector2.up) - origin).normalized;
                int quarter = Mathf.RoundToInt(rotation / 90f);
                Vector2[] expectedRight = { Vector2.right, Vector2.down, Vector2.left, Vector2.up };
                Vector2[] expectedUp = { Vector2.up, Vector2.right, Vector2.down, Vector2.left };
                Assert.That(Vector2.Dot(right, expectedRight[quarter]), Is.GreaterThan(.999f), "glyph right");
                Assert.That(Vector2.Dot(up, expectedUp[quarter]), Is.GreaterThan(.999f), "glyph up");
            });
        }

        [TestCase(MahjongWallSeat.WestDiscard, 90f)]
        [TestCase(MahjongWallSeat.EastDiscard, -90f)]
        public void SideDiscardGlyphsUseTheirSeatCornerMapping(MahjongWallSeat seat, float rotation)
        {
            WithMesh(seat, MahjongWallTextureMode.SideDiscardWall, rotation, true, mesh =>
            {
                Vector2[] expected = rotation > 0f
                    ? new[] { Vector2.right, Vector2.one, Vector2.up, Vector2.zero }
                    : new[] { Vector2.up, Vector2.zero, Vector2.right, Vector2.one };
                for (int i = 0; i < 4; i++)
                {
                    UIVertex vertex = default;
                    mesh.PopulateUIVertex(ref vertex, i);
                    Assert.That(Vector2.Distance(vertex.uv0, expected[i]), Is.LessThan(.0001f));
                }
            });
        }

        [Test]
        public void WestDiscardGlyphsFaceIntoTheTableWithoutMirroring()
        {
            var root = new GameObject("West discard orientation", typeof(RectTransform));
            var texture = new Texture2D(16, 24);
            var sprite = Sprite.Create(texture, new Rect(0, 0, 16, 24), Vector2.zero);
            try
            {
                var axes = new MahjongWallAxisFrame(new MahjongWallPoint2(460, 361),
                    new MahjongWallPoint2(422, 361), new MahjongWallPoint2(460, 353), 150, -90,
                    faceUvRotationDegrees: 90);
                var graphic = root.AddComponent<MahjongWallFaceRevealGraphic>();
                graphic.Configure(MahjongWallSeat.WestDiscard, sprite, 2, axes,
                    MahjongWallTextureMode.SideDiscardWall, tileCountOverride: 6,
                    useExplicitFrontGeometry: true, widthByYAxis: true);
                using (var mesh = new VertexHelper())
                {
                    typeof(MahjongWallFaceRevealGraphic).GetMethod("OnPopulateMesh",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                        .Invoke(graphic, new object[] { mesh });
                    Vector2 origin = PositionAtUv(mesh, Vector2.zero);
                    Vector2 right = (PositionAtUv(mesh, Vector2.right) - origin).normalized;
                    Vector2 up = (PositionAtUv(mesh, Vector2.up) - origin).normalized;
                    Assert.That(Vector2.Dot(up, Vector2.right), Is.GreaterThan(.999f), "glyph top faces the table");
                    Assert.That(Vector2.Dot(right, Vector2.down), Is.GreaterThan(.999f), "glyph is not mirrored");
                }
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(sprite);
                Object.DestroyImmediate(texture);
            }
        }

        private static void WithMesh(MahjongWallSeat seat, MahjongWallTextureMode mode, float rotation,
            bool explicitGeometry, System.Action<VertexHelper> check)
        {
            var root = new GameObject("face orientation", typeof(RectTransform));
            var texture = new Texture2D(16, 24);
            var sprite = Sprite.Create(texture, new Rect(0, 0, 16, 24), Vector2.zero);
            try
            {
                var axes = new MahjongWallAxisFrame(new MahjongWallPoint2(20, 80),
                    new MahjongWallPoint2(20, 20), new MahjongWallPoint2(24, 84), 120, 0,
                    faceUvRotationDegrees: rotation);
                var graphic = root.AddComponent<MahjongWallFaceRevealGraphic>();
                graphic.Configure(seat, sprite, 1, axes, mode, 30, tileCountOverride: 4,
                    useExplicitFrontGeometry: explicitGeometry, widthByYAxis: true);
                using (var mesh = new VertexHelper())
                {
                    typeof(MahjongWallFaceRevealGraphic).GetMethod("OnPopulateMesh",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                        .Invoke(graphic, new object[] { mesh });
                    Assert.AreEqual(4, mesh.currentVertCount);
                    check(mesh);
                }
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(sprite);
                Object.DestroyImmediate(texture);
            }
        }

        private static Vector2 PositionAtUv(VertexHelper mesh, Vector2 uv)
        {
            for (int i = 0; i < mesh.currentVertCount; i++)
            {
                UIVertex vertex = default;
                mesh.PopulateUIVertex(ref vertex, i);
                if (Vector2.Distance(vertex.uv0, uv) < .0001f) return vertex.position;
            }
            Assert.Fail("Missing UV corner " + uv);
            return default;
        }
    }
}
