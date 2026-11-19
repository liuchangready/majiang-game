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
        public void FilterKeepsOriginalMeshPositionsAndDoesNotChangeSavedLayout()
        {
            MahjongWallLayoutEditor editor = BuildScene();
            object wall = Walls(editor).First(w => (MahjongWallSeat)Field(w, "Seat") == MahjongWallSeat.West);
            Select(editor, wall);
            var graphic = (MahjongExplicitFrontWallGraphic)Field(wall, "Graphic");
            string before = JsonUtility.ToJson(Invoke(editor, "CaptureDocument"));
            Vector3[] full = MeshVertices(graphic);
            graphic.SetVisibleRange(6, 6);
            Vector3[] single = MeshVertices(graphic);
            graphic.SetVisibleRange(5, 7);
            Vector3[] three = MeshVertices(graphic);
            Assert.That(single.Length, Is.GreaterThan(0).And.LessThan(three.Length));
            Assert.That(three.Length, Is.LessThan(full.Length));
            Assert.IsTrue(single.All(point => three.Any(candidate => Vector3.Distance(point, candidate) < .0001f)));
            Assert.IsTrue(three.All(point => full.Any(candidate => Vector3.Distance(point, candidate) < .0001f)));
            object faceWall = Walls(editor).First(w => ((IEnumerable)Field(w, "FaceGraphics")).Cast<MahjongWallFaceRevealGraphic>().Any());
            var face = ((IEnumerable)Field(faceWall, "FaceGraphics")).Cast<MahjongWallFaceRevealGraphic>().First();
            int faceIndex = (int)Hidden(face, "_tileIndex");
            face.SetVisibleRange(faceIndex, faceIndex);
            Assert.IsNotEmpty(MeshVertices(face));
            face.SetVisibleRange(faceIndex + 1, faceIndex + 1);
            Assert.IsEmpty(MeshVertices(face));
            Assert.AreEqual(before, JsonUtility.ToJson(Invoke(editor, "CaptureDocument")));
        }

        [Test]
        public void PlacementPersistsForCopyAndMissingV2FieldsUseOriginalDefaults()
        {
            MahjongWallLayoutEditor editor = BuildScene();
            object original = Walls(editor).First(w => (MahjongWallSeat)Field(w, "Seat") == MahjongWallSeat.South);
            Select(editor, original);
            Press(editor, Key.C);
            object copy = Walls(editor).Last();
            typeof(MahjongWallLayoutEditor).GetMethod("SetPlacement", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { copy, 12f, 3f });
            string snapshot = JsonUtility.ToJson(Invoke(editor, "CaptureDocument"));
            MahjongWallLayoutEditor cold = BuildScene(snapshot);
            object restored = Walls(cold).Single(w => (string)Field(w, "WallId") == (string)Field(copy, "WallId"));
            Assert.AreEqual(12f, Field(restored, "FaceWidthOverride"));
            Assert.AreEqual(3f, Field(restored, "ExtraGap"));
            Assert.AreNotEqual(Field(original, "FaceWidthOverride"), Field(restored, "FaceWidthOverride"));

            string oldLayout = File.ReadAllText(Path.GetFullPath(Path.Combine(Application.dataPath, "../../../tools/unity/layouts/table-reference-v3.json")));
            MahjongWallLayoutEditor legacy = BuildScene(oldLayout);
            object legacyWall = Walls(legacy).First(w => (MahjongWallSeat)Field(w, "Seat") == MahjongWallSeat.South);
            Assert.AreEqual(Field(legacyWall, "DefaultFaceWidthOverride"), Field(legacyWall, "FaceWidthOverride"));
            Assert.AreEqual(Field(legacyWall, "DefaultExtraGap"), Field(legacyWall, "ExtraGap"));
        }

        [Test]
        public void ComparisonRestoresDraftIncludingIndependentPlacement()
        {
            MahjongWallLayoutEditor editor = BuildScene();
            object wall = Walls(editor).First();
            Select(editor, wall);
            string a = JsonUtility.ToJson(Entries(Invoke(editor, "CaptureDocument")).Single(e => (string)Field(e, "wallId") == (string)Field(wall, "WallId")));
            Invoke(editor, "SetComparisonA", wall);
            var frame = (MahjongWallAxisFrame)Field(wall, "Frame");
            frame.AxisLength += 12f;
            Invoke(editor, "ApplyFrame", wall, frame);
            typeof(MahjongWallLayoutEditor).GetMethod("SetPlacement", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { wall, 12f, 3f });
            string b = JsonUtility.ToJson(Entries(Invoke(editor, "CaptureDocument")).Single(e => (string)Field(e, "wallId") == (string)Field(wall, "WallId")));
            Invoke(editor, "ToggleComparison");
            Assert.AreEqual(a, JsonUtility.ToJson(Entries(Invoke(editor, "CaptureDocument")).Single(e => (string)Field(e, "wallId") == (string)Field(wall, "WallId"))));
            Invoke(editor, "ToggleComparison");
            Assert.AreEqual(b, JsonUtility.ToJson(Entries(Invoke(editor, "CaptureDocument")).Single(e => (string)Field(e, "wallId") == (string)Field(wall, "WallId"))));
        }

        [Test]
        public void ObservationFollowsCopyDeleteAndExit()
        {
            MahjongWallLayoutEditor editor = BuildScene();
            object original = Walls(editor).First(w => (MahjongWallSeat)Field(w, "Seat") == MahjongWallSeat.WestDiscard);
            Select(editor, original);
            Press(editor, Key.F2); Press(editor, Key.F3); Press(editor, Key.F4);
            var body = (MahjongExplicitFrontWallGraphic)Field(original, "Graphic");
            Assert.IsTrue(body.enabled);
            Assert.GreaterOrEqual((int)Hidden(body, "_visibleStart"), 0);
            Press(editor, Key.C);
            object copy = Walls(editor).Last();
            var copyBody = (MahjongExplicitFrontWallGraphic)Field(copy, "Graphic");
            Assert.IsFalse(body.enabled);
            Assert.IsTrue(copyBody.enabled);
            Assert.AreEqual(Hidden(copyBody, "_visibleStart"), Hidden(copyBody, "_visibleEnd"));
            foreach (MahjongWallFaceRevealGraphic face in (IEnumerable)Field(copy, "FaceGraphics"))
                Assert.AreEqual(Hidden(copyBody, "_visibleStart"), Hidden(face, "_visibleStart"));
            Press(editor, Key.D);
            Assert.AreEqual(8, Walls(editor).Length);
            Press(editor, Key.F2);
            foreach (object item in Walls(editor))
            {
                var graphic = (MahjongExplicitFrontWallGraphic)Field(item, "Graphic");
                Assert.IsTrue(graphic.enabled);
                Assert.AreEqual(-1, Hidden(graphic, "_visibleStart"));
            }
        }

        [Test]
        public void LoadingAnotherFileResetsComparisonHistoryAndBaseline()
        {
            MahjongWallLayoutEditor editor = BuildScene();
            object wall = Walls(editor).First();
            string id = (string)Field(wall, "WallId");
            Select(editor, wall);
            Invoke(editor, "SetComparisonA", wall);
            Invoke(editor, "RememberEdit", wall);
            var frame = (MahjongWallAxisFrame)Field(wall, "Frame");
            frame.AxisLength += 20f;
            Invoke(editor, "ApplyFrame", wall, frame);
            _savedPath = Path.Combine(Application.temporaryCachePath, Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(_savedPath, JsonUtility.ToJson(Invoke(editor, "CaptureDocument")));
            Invoke(editor, "LoadLayoutFile", _savedPath);
            wall = Walls(editor).Single(w => (string)Field(w, "WallId") == id);
            Assert.AreEqual(Path.GetFullPath(_savedPath), Hidden(editor, "_loadedLayoutPath"));
            Assert.IsNull(Hidden(editor, "_aWallId"));
            Assert.AreEqual(0, ((ICollection)Hidden(editor, "_undo")).Count);
            Assert.AreEqual(0, ((ICollection)Hidden(editor, "_redo")).Count);
            var baseline = (IDictionary)Hidden(editor, "_baselineEntries");
            Assert.That((float)Field(baseline[id], "axisLength"), Is.EqualTo(frame.AxisLength).Within(.001f));
            Select(editor, wall);
            Press(editor, Key.F5);
            Assert.IsFalse((bool)Hidden(editor, "_previewA"));
        }

        [Test]
        public void PlacementRejectsWidthAbovePitchAndGapBoundaries()
        {
            MahjongWallLayoutEditor editor = BuildScene();
            object wall = Walls(editor).First(w => (int)Field(w, "TileCountOverride") > 1);
            int count = (int)Field(wall, "TileCountOverride");
            float length = (count - 1) * 10f;
            var valid = typeof(MahjongWallLayoutEditor).GetMethod("PlacementValid", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsTrue((bool)valid.Invoke(null, new object[] { wall, length, 5f, 0f }));
            Assert.IsTrue((bool)valid.Invoke(null, new object[] { wall, length, 10f, 0f }));
            Assert.IsFalse((bool)valid.Invoke(null, new object[] { wall, length, 20f, 0f }));
            Assert.IsFalse((bool)valid.Invoke(null, new object[] { wall, length / 2, 10f, 0f }));
            wall.GetType().GetField("GapAfterTileIndex").SetValue(wall, count - 2);
            Assert.IsTrue((bool)valid.Invoke(null, new object[] { wall, length, 9f, count - 1f }));
            Assert.IsFalse((bool)valid.Invoke(null, new object[] { wall, length, 10f, count - 1f }));
        }

        [TestCase("width")]
        [TestCase("falloff")]
        [TestCase("gap")]
        public void SkeletonEdgesFollowMeshAcrossComparisonAndHistory(string parameter)
        {
            MahjongWallLayoutEditor editor = BuildScene();
            object wall = Walls(editor).First(w => (MahjongWallSeat)Field(w, "Seat") == MahjongWallSeat.South);
            Select(editor, wall);
            typeof(MahjongWallLayoutEditor).GetField("_showSkeleton", Private).SetValue(editor, true);
            typeof(MahjongWallLayoutEditor).GetField("_showTileFrames", Private).SetValue(editor, true);
            typeof(MahjongWallLayoutEditor).GetField("_panelSkeleton", Private).SetValue(editor, true);
            typeof(MahjongWallLayoutEditor).GetMethod("SetPlacement", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { wall, 10f, 5f });
            Invoke(editor, "RefreshSkeleton");
            Invoke(editor, "SetComparisonA", wall);
            AssertSkeletonMatchesMesh(editor, wall);
            Invoke(editor, "RememberEdit", wall);
            if (parameter != "falloff")
                typeof(MahjongWallLayoutEditor).GetMethod("SetPlacement", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { wall, parameter == "width" ? 12f : 10f, parameter == "gap" ? 8f : 5f });
            else Invoke(editor, "SetWidthFalloffPerScreenY", wall, .002f);
            Invoke(editor, "ApplyFrame", wall, Field(wall, "Frame"));
            AssertSkeletonMatchesMesh(editor, wall);
            Invoke(editor, "ToggleComparison"); AssertSkeletonMatchesMesh(editor, wall);
            Invoke(editor, "ToggleComparison"); AssertSkeletonMatchesMesh(editor, wall);
            Invoke(editor, "RestoreHistory", Hidden(editor, "_undo"), Hidden(editor, "_redo"));
            AssertSkeletonMatchesMesh(editor, wall);
            Invoke(editor, "RestoreHistory", Hidden(editor, "_redo"), Hidden(editor, "_undo"));
            AssertSkeletonMatchesMesh(editor, wall);
            typeof(MahjongWallLayoutEditor).GetField("_viewMode", Private).SetValue(editor, 0);
            Invoke(editor, "RefreshView"); AssertSkeletonMatchesMesh(editor, wall);
            typeof(MahjongWallLayoutEditor).GetField("_viewMode", Private).SetValue(editor, 1);
            Invoke(editor, "RefreshView"); AssertSkeletonMatchesMesh(editor, wall);
            string snapshot = JsonUtility.ToJson(Invoke(editor, "CaptureDocument"));
            MahjongWallLayoutEditor cold = BuildScene(snapshot);
            object restored = Walls(cold).Single(w => (string)Field(w, "WallId") == (string)Field(wall, "WallId"));
            Select(cold, restored);
            typeof(MahjongWallLayoutEditor).GetField("_showSkeleton", Private).SetValue(cold, true);
            typeof(MahjongWallLayoutEditor).GetField("_showTileFrames", Private).SetValue(cold, true);
            typeof(MahjongWallLayoutEditor).GetField("_panelSkeleton", Private).SetValue(cold, true);
            Invoke(cold, "RefreshSkeleton");
            AssertSkeletonMatchesMesh(cold, restored);
        }

        private static void AssertSkeletonMatchesMesh(MahjongWallLayoutEditor editor, object wall)
        {
            int count = (int)Field(wall, "TileCountOverride"), gapIndex = (int)Field(wall, "GapAfterTileIndex");
            float width = (float)Field(wall, "FaceWidthOverride"), gap = (float)Field(wall, "ExtraGap");
            var skeleton = MahjongExplicitFrontWallGraphic.BuildSkeleton((MahjongWallAxisFrame)Field(wall, "Frame"), count, width, gapIndex, gap);
            var tiles = MahjongExplicitFrontWallGraphic.BuildTileGeometry(skeleton, count, gapIndex, gap,
                (float)Field(wall, "WidthShrinkPercent"), (bool)Field(wall, "WidthByYAxis"), (float)Field(wall, "WidthFalloffPerScreenY"));
            int mode = (int)Hidden(editor, "_viewMode");
            int center = (int)Invoke(editor, "SelectedTileIndex", wall, count);
            int length = mode == 0 ? 1 : Math.Min(3, count);
            int index = mode == 2 ? 0 : Mathf.Clamp(center - length / 2, 0, count - length);
            var surface = MahjongExplicitFrontWallGraphic.GetOuterSurfaces(tiles[index], (MahjongWallOuterPoint)Field(wall, "OuterPoint"), index, count)[0];
            var faces = new[] { MahjongWallSurface.Front, MahjongWallSurface.Back, MahjongWallSurface.Top, MahjongWallSurface.Bottom, MahjongWallSurface.LeftSide, MahjongWallSurface.RightSide };
            int lineIndex = Array.IndexOf(faces, surface) * 4;
            var points = MahjongExplicitFrontWallGraphic.Surface(tiles[index], surface);
            var mesh = MeshVertices((MaskableGraphic)Field(wall, "Graphic"));
            Assert.IsTrue(mesh.Any(v => Vector2.Distance(v, new Vector2(points[0].X, -points[0].Y)) < .001f));
            var lines = ((IEnumerable)Field(wall, "TileFrameLines")).Cast<Image>().ToArray();
            RectTransform line = lines[lineIndex].rectTransform;
            var a = (MahjongWallPoint2)Invoke(editor, "ToEditorPoint", wall, points[0]);
            var b = (MahjongWallPoint2)Invoke(editor, "ToEditorPoint", wall, points[1]);
            Vector2 expected = new Vector2((a.X + b.X) * .5f, -(a.Y + b.Y) * .5f);
            Assert.That(Vector2.Distance(line.anchoredPosition, expected), Is.LessThan(.001f));
            Assert.That(line.sizeDelta.x, Is.EqualTo(Vector2.Distance(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y))).Within(.001f));
            float angle = line.localEulerAngles.z * Mathf.Deg2Rad;
            Vector2 halfLine = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * (line.sizeDelta.x * .5f);
            Assert.That(Vector2.Distance(line.anchoredPosition - halfLine, new Vector2(a.X, -a.Y)), Is.LessThan(.001f));
            Assert.That(Vector2.Distance(line.anchoredPosition + halfLine, new Vector2(b.X, -b.Y)), Is.LessThan(.001f));
        }

        [Test]
        public void DragCannotMakeExplicitWidthInvalidAndColdReloadKeepsIt()
        {
            MahjongWallLayoutEditor editor = BuildScene();
            object wall = Walls(editor).First(w => (MahjongWallSeat)Field(w, "Seat") == MahjongWallSeat.South);
            Select(editor, wall);
            var frame = (MahjongWallAxisFrame)Field(wall, "Frame");
            frame.AxisLength = 130f;
            Invoke(editor, "ApplyFrame", wall, frame);
            typeof(MahjongWallLayoutEditor).GetMethod("SetPlacement", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { wall, 10f, 0f });
            var points = (MahjongWallPoint2[])Invoke(editor, "GetDisplayControlPointsInEditor", wall);
            typeof(MahjongWallLayoutEditor).GetField("_draggingHandle", Private).SetValue(editor, 3);
            typeof(MahjongWallLayoutEditor).GetField("_lastPointer", Private).SetValue(editor, points[3]);
            Invoke(editor, "DragHandle", points[0] + (points[3] - points[0]) * .55f);
            Assert.That(((MahjongWallAxisFrame)Field(wall, "Frame")).AxisLength, Is.EqualTo(130f).Within(.001f));
            Assert.That((string)Hidden(editor, "_fieldError"), Does.Contain("Invalid placement"));
            string id = (string)Field(wall, "WallId");
            MahjongWallLayoutEditor cold = BuildScene(JsonUtility.ToJson(Invoke(editor, "CaptureDocument")));
            object restored = Walls(cold).Single(w => (string)Field(w, "WallId") == id);
            Assert.That(((MahjongWallAxisFrame)Field(restored, "Frame")).AxisLength, Is.EqualTo(130f).Within(.001f));
            Assert.That((float)Field(restored, "FaceWidthOverride"), Is.EqualTo(10f).Within(.001f));
        }

        [Test]
        public void KeyboardAndImguiSignalsInSameFrameTogglePanelOnce()
        {
            MahjongWallLayoutEditor editor = BuildScene();
            Invoke(editor, "HandleTuningShortcut", KeyCode.F2, false, false, false);
            Assert.IsTrue((bool)Hidden(editor, "_tuningPanel"));
            Invoke(editor, "HandleTuningShortcut", KeyCode.F2, false, false, true);
            Assert.IsTrue((bool)Hidden(editor, "_tuningPanel"));
        }

        [Test]
        public void MouseReleaseOverPanelEndsExistingDrag()
        {
            MahjongWallLayoutEditor editor = BuildScene();
            object wall = Walls(editor).First();
            Select(editor, wall);
            var mouse = InputSystem.AddDevice<Mouse>();
            try
            {
                base.Press(mouse.leftButton);
                var points = (MahjongWallPoint2[])Invoke(editor, "GetDisplayControlPointsInEditor", wall);
                Invoke(editor, "BeginPointerAction", points[0]);
                Assert.GreaterOrEqual((int)Hidden(editor, "_draggingHandle"), 0);
                typeof(MahjongWallLayoutEditor).GetField("_tuningPanel", Private).SetValue(editor, true);
                Set(mouse.position, new Vector2(50f, Screen.height - 50f));
                base.Release(mouse.leftButton);
                Invoke(editor, "Update");
                Assert.AreEqual(-1, Hidden(editor, "_draggingHandle"));
                string before = JsonUtility.ToJson(Invoke(editor, "CaptureDocument"));
                base.Press(mouse.leftButton);
                Invoke(editor, "Update");
                Set(mouse.position, new Vector2(Screen.width - 5f, 5f));
                Invoke(editor, "Update");
                Assert.AreEqual(before, JsonUtility.ToJson(Invoke(editor, "CaptureDocument")));
            }
            finally { InputSystem.RemoveDevice(mouse); }
        }

        [Test]
        public void InvalidLoadedPlacementLeavesCurrentLayoutIntact()
        {
            MahjongWallLayoutEditor editor = BuildScene();
            object document = Invoke(editor, "CaptureDocument");
            string before = JsonUtility.ToJson(document);
            object entry = Entries(document).First();
            entry.GetType().GetField("hasPlacement").SetValue(entry, true);
            entry.GetType().GetField("faceWidthOverride").SetValue(entry, 100000f);
            _savedPath = Path.Combine(Application.temporaryCachePath, Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(_savedPath, JsonUtility.ToJson(document));
            var failure = Assert.Throws<TargetInvocationException>(() => Invoke(editor, "LoadLayoutFile", _savedPath));
            Assert.IsInstanceOf<InvalidDataException>(failure.InnerException);
            Assert.AreEqual(before, JsonUtility.ToJson(Invoke(editor, "CaptureDocument")));
        }

        [Test]
        public void FocusedInputDoesNotTriggerTableShortcuts()
        {
            MahjongWallLayoutEditor editor = BuildScene();
            object wall = Walls(editor).First();
            Select(editor, wall);
            string before = JsonUtility.ToJson(Invoke(editor, "CaptureDocument"));
            typeof(MahjongWallLayoutEditor).GetField("<HasInputFocus>k__BackingField", Private).SetValue(editor, true);
            Press(editor, Key.C); Press(editor, Key.D); Press(editor, Key.LeftArrow);
            base.Press(_keyboard[Key.LeftCtrl]);
            Press(editor, Key.Z);
            base.Release(_keyboard[Key.LeftCtrl]);
            Assert.AreEqual(before, JsonUtility.ToJson(Invoke(editor, "CaptureDocument")));
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
