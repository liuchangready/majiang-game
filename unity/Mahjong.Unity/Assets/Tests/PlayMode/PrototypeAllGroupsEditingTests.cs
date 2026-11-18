#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Mahjong.Unity.UI.Prototype;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Mahjong.Unity.Tests.PlayMode
{
    public sealed class PrototypeAllGroupsEditingTests : InputTestFixture
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly List<GameObject> _roots = new List<GameObject>();
        private Keyboard _keyboard;
        private string _savedPath;

        [SetUp]
        public override void Setup()
        {
            base.Setup();
            _keyboard = InputSystem.AddDevice<Keyboard>();
        }

        [TearDown]
        public override void TearDown()
        {
            if (_keyboard != null) InputSystem.RemoveDevice(_keyboard);
            foreach (GameObject root in _roots)
                if (root != null) Object.DestroyImmediate(root);
            _roots.Clear();
            Tile2D5View.IsPausedProvider = null;
            if (_savedPath != null && File.Exists(_savedPath)) File.Delete(_savedPath);
            base.TearDown();
        }

        [TestCase(MahjongWallSeat.North, 14)]
        [TestCase(MahjongWallSeat.West, 13)]
        [TestCase(MahjongWallSeat.East, 13)]
        [TestCase(MahjongWallSeat.South, 14)]
        [TestCase(MahjongWallSeat.NorthDiscard, 6)]
        [TestCase(MahjongWallSeat.WestDiscard, 6)]
        [TestCase(MahjongWallSeat.EastDiscard, 6)]
        [TestCase(MahjongWallSeat.SouthDiscard, 6)]
        public void RealV3GroupsSupportYEditingCopiesAndColdReload(MahjongWallSeat seat, int count)
        {
            MahjongWallLayoutEditor editor = BuildScene();
            object[] walls = Walls(editor);
            Assert.AreEqual(8, walls.Length, "Test the production V3 composition, not hand-configured rows.");
            object wall = walls.Single(w => (MahjongWallSeat)Field(w, "Seat") == seat);
            Assert.IsTrue((bool)Field(wall, "PerTileFrontRenderer"));
            Assert.IsTrue((bool)Field(wall, "WidthByYAxis"));
            Assert.AreEqual(count, Field(wall, "TileCountOverride"));
            Assert.IsInstanceOf<MahjongExplicitFrontWallGraphic>(Field(wall, "Graphic"));
            AssertWestWallRendering(wall);
            Select(editor, wall);
            Press(editor, Key.Q);
            Text label = (Text)Hidden(editor, "_widthShrinkLabel");
            Assert.IsTrue(label.gameObject.activeSelf);
            Assert.That(label.text, Does.Contain("Y系数"));
            float initial = (float)Field(wall, "WidthFalloffPerScreenY");
            Press(editor, Key.P);
            Assert.That((float)Field(wall, "WidthFalloffPerScreenY"), Is.EqualTo(initial + .0001f).Within(.000001f));
            Press(editor, Key.M);
            Assert.That((float)Field(wall, "WidthFalloffPerScreenY"), Is.EqualTo(initial).Within(.000001f));

            // Horizontal rows have equal Y: tilt the real editable frame to verify a visible mesh change.
            MahjongWallAxisFrame frame = (MahjongWallAxisFrame)Field(wall, "Frame");
            frame.AxisSlopeDegrees = 75f;
            Invoke(editor, "ApplyFrame", wall, frame);
            Vector3[] before = MeshVertices((MaskableGraphic)Field(wall, "Graphic"));
            Press(editor, Key.P);
            Vector3[] after = MeshVertices((MaskableGraphic)Field(wall, "Graphic"));
            Assert.IsNotEmpty(after);
            Assert.IsTrue(before.Where((point, i) => Vector3.Distance(point, after[i]) > .0001f).Any());
            Press(editor, Key.C);
            object copy = Walls(editor).Last();
            Assert.IsTrue((bool)Field(copy, "IsRuntimeCopy"));
            Assert.IsTrue((bool)Field(copy, "WidthByYAxis"));
            AssertWestWallRendering(copy);
            float originalCoefficient = (float)Field(wall, "WidthFalloffPerScreenY");
            Press(editor, Key.P);
            Assert.That((float)Field(wall, "WidthFalloffPerScreenY"), Is.EqualTo(originalCoefficient));
            Assert.That((float)Field(copy, "WidthFalloffPerScreenY"), Is.EqualTo(originalCoefficient + .0001f).Within(.000001f));
            Press(editor, Key.C);
            Assert.AreEqual(10, Walls(editor).Length);
            string[] expected = Entries(Invoke(editor, "CaptureDocument")).Select(e => JsonUtility.ToJson(e)).OrderBy(x => x).ToArray();
            _savedPath = Path.Combine(Application.temporaryCachePath, Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(_savedPath, JsonUtility.ToJson(Invoke(editor, "CaptureDocument")));
            MahjongWallLayoutEditor cold = BuildScene(File.ReadAllText(_savedPath));
            CollectionAssert.AreEqual(expected,
                Entries(Invoke(cold, "CaptureDocument")).Select(e => JsonUtility.ToJson(e)).OrderBy(x => x).ToArray());
            string copyId = (string)Field(copy, "WallId");
            object restoredCopy = Walls(cold).Single(w => (string)Field(w, "WallId") == copyId);
            AssertWestWallRendering(restoredCopy);
            Select(cold, restoredCopy);
            Press(cold, Key.M);
            Assert.That((float)Field(restoredCopy, "WidthFalloffPerScreenY"), Is.EqualTo(originalCoefficient).Within(.000001f));
        }

        [Test]
        public void CandidateWestDiscardAndCopiesKeepTheirOrientationOnColdReload()
        {
            string layout = Path.GetFullPath(Path.Combine(Application.dataPath,
                "../../../tools/unity/layouts/table-reference-v3.json"));
            MahjongWallLayoutEditor editor = BuildScene(File.ReadAllText(layout));
            object wall = Walls(editor).First(w => (MahjongWallSeat)Field(w, "Seat") == MahjongWallSeat.WestDiscard);
            AssertWestDiscardOrientation(editor);
            Select(editor, wall);
            Press(editor, Key.C);
            AssertWestDiscardOrientation(editor);
            MahjongWallLayoutEditor cold = BuildScene(JsonUtility.ToJson(Invoke(editor, "CaptureDocument")));
            AssertWestDiscardOrientation(cold);
        }

        private static void AssertWestDiscardOrientation(MahjongWallLayoutEditor editor)
        {
            foreach (object wall in Walls(editor).Where(w => (MahjongWallSeat)Field(w, "Seat") == MahjongWallSeat.WestDiscard))
                foreach (MahjongWallFaceRevealGraphic face in (IEnumerable)Field(wall, "FaceGraphics"))
                {
                    Sprite sprite = (Sprite)Hidden(face, "_face");
                    Rect pixels = sprite.textureRect;
                    Vector2 lowerLeft = new Vector2(pixels.xMin / sprite.texture.width, pixels.yMin / sprite.texture.height);
                    Vector2 lowerRight = new Vector2(pixels.xMax / sprite.texture.width, lowerLeft.y);
                    Vector2 upperLeft = new Vector2(lowerLeft.x, pixels.yMax / sprite.texture.height);
                    using (var mesh = new VertexHelper())
                    {
                        Invoke(face, "OnPopulateMesh", mesh);
                        Vector2 origin = PositionAtUv(mesh, lowerLeft);
                        Vector2 right = (PositionAtUv(mesh, lowerRight) - origin).normalized;
                        Vector2 up = (PositionAtUv(mesh, upperLeft) - origin).normalized;
                        Assert.That(Vector2.Dot(up, Vector2.right), Is.GreaterThan(.999f));
                        Assert.That(Vector2.Dot(right, Vector2.down), Is.GreaterThan(.999f));
                    }
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
            Assert.Fail("Missing face UV corner " + uv);
            return default;
        }

        private static void AssertWestWallRendering(object wall)
        {
            if ((MahjongWallSeat)Field(wall, "Seat") != MahjongWallSeat.West) return;
            Assert.AreEqual(MahjongWallOuterPoint.A1, Field(wall, "OuterPoint"));
            Assert.AreEqual(MahjongWallFrontFace.Front, Field(wall, "FrontFace"));
            var graphic = (MahjongExplicitFrontWallGraphic)Field(wall, "Graphic");
            Assert.AreEqual(MahjongWallOuterPoint.A1, Hidden(graphic, "_outerPoint"));
            var specs = (MahjongWallTileFrontSpec[])Hidden(graphic, "_tileSpecs");
            Assert.IsTrue(specs.All(spec => spec.Face == MahjongWallSurface.Front));
            var frame = (MahjongWallAxisFrame)Field(wall, "Frame");
            MahjongWallSkeleton skeleton = MahjongExplicitFrontWallGraphic.BuildSkeleton(frame, specs.Length);
            MahjongExplicitTileGeometry tile = MahjongExplicitFrontWallGraphic.BuildTileGeometry(skeleton, specs.Length)[0];
            Assert.AreEqual(skeleton.C1.X, tile.FrontFace[0].X);
            Assert.AreEqual(skeleton.C1.Y, tile.FrontFace[0].Y);
            Assert.AreEqual(skeleton.D1.X, tile.FrontFace[1].X);
            Assert.AreEqual(skeleton.D1.Y, tile.FrontFace[1].Y);
            Assert.That(tile.FrontFace[3].X - tile.FrontFace[2].X,
                Is.EqualTo(tile.FrontFace[0].X - tile.FrontFace[1].X).Within(.001f));
        }

        private MahjongWallLayoutEditor BuildScene(string snapshot = null)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Prototypes/V5ArtPrototype.prefab");
            Assert.IsNotNull(prefab);
            GameObject root = Object.Instantiate(prefab);
            _roots.Add(root);
            V5ArtPrototype prototype = root.GetComponent<V5ArtPrototype>();
            prototype.enabled = false;
            Invoke(prototype, "BindHierarchy");
            RectTransform coordinates = (RectTransform)Hidden(prototype, "_canvasRoot");
            Assert.IsNotNull(coordinates);
            coordinates.anchorMin = coordinates.anchorMax = new Vector2(.5f, .5f);
            coordinates.sizeDelta = new Vector2(1280f, 720f);
            MahjongWallLayoutEditor editor = coordinates.gameObject.AddComponent<MahjongWallLayoutEditor>();
            editor.enabled = false;
            editor.Configure(coordinates);
            typeof(MahjongWallLayoutEditor).GetField("_loaded", Private).SetValue(editor, true);
            if (snapshot != null)
            {
                Type document = typeof(MahjongWallLayoutEditor).GetNestedType("LayoutDocument", BindingFlags.NonPublic);
                Invoke(editor, "ApplyDocument", JsonUtility.FromJson(snapshot, document));
            }
            var page = new GameObject("V3 capability test page", typeof(RectTransform));
            page.transform.SetParent(coordinates, false);
            page.GetComponent<RectTransform>().sizeDelta = new Vector2(1280f, 720f);
            Invoke(prototype, "BuildSeatPrototypeContent", page.transform, false, true);
            return editor;
        }

        private void Press(MahjongWallLayoutEditor editor, Key key)
        {
            base.Press(_keyboard[key]);
            Invoke(editor, "Update");
            base.Release(_keyboard[key]);
            Invoke(editor, "Update");
        }

        private static Vector3[] MeshVertices(MaskableGraphic graphic)
        {
            using (var vertices = new VertexHelper())
            {
                Invoke(graphic, "OnPopulateMesh", vertices);
                var mesh = new Mesh();
                vertices.FillMesh(mesh);
                Vector3[] points = mesh.vertices;
                Object.DestroyImmediate(mesh);
                return points;
            }
        }

        private static void Select(MahjongWallLayoutEditor editor, object wall) =>
            typeof(MahjongWallLayoutEditor).GetField("_selectedWall", Private).SetValue(editor, Array.IndexOf(Walls(editor), wall));
        private static object[] Walls(MahjongWallLayoutEditor editor) => ((IEnumerable)Hidden(editor, "_walls")).Cast<object>().ToArray();
        private static object[] Entries(object document) => ((Array)Field(document, "walls")).Cast<object>().ToArray();
        private static object Field(object value, string name) => value.GetType().GetField(name).GetValue(value);
        private static object Hidden(object value, string name) => value.GetType().GetField(name, Private).GetValue(value);
        private static object Invoke(object value, string name, params object[] args) => value.GetType().GetMethod(name, Private | BindingFlags.DeclaredOnly).Invoke(value, args);
    }
}
#endif
