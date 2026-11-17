using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Mahjong.Unity.UI.Prototype;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Mahjong.Unity.Tests.PlayMode
{
    public sealed class PrototypeLayoutPersistenceTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly List<GameObject> _roots = new List<GameObject>();
        private string _tempPath;

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject root in _roots)
                if (root != null) Object.DestroyImmediate(root);
            _roots.Clear();
            Tile2D5View.IsPausedProvider = null;
            if (_tempPath != null && File.Exists(_tempPath)) File.Delete(_tempPath);
        }

        [Test]
        public void CopiesAndCopiesOfCopiesSurviveFileSaveAndColdLoad()
        {
            MahjongWallLayoutEditor editor = NewEditor(out V5ArtPrototype prototype);
            Draw(prototype, MahjongWallSeat.North);
            Draw(prototype, MahjongWallSeat.West);
            Copy(editor, 0);
            Copy(editor, 2);
            object document = Invoke(editor, "CaptureDocument");
            string[] expected = Entries(document).Select(JsonUtility.ToJson).OrderBy(x => x).ToArray();
            Assert.AreEqual(4, expected.Length);
            Assert.AreEqual(2, Entries(document).Count(e => (bool)Field(e, "isRuntimeCopy")));

            _tempPath = Path.Combine(Application.temporaryCachePath, Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(_tempPath, JsonUtility.ToJson(document));
            Object.DestroyImmediate(editor.gameObject);

            MahjongWallLayoutEditor reloaded = NewEditor(out V5ArtPrototype reloadedPrototype);
            Apply(reloaded, File.ReadAllText(_tempPath));
            Draw(reloadedPrototype, MahjongWallSeat.North);
            Draw(reloadedPrototype, MahjongWallSeat.West);
            CollectionAssert.AreEqual(expected,
                Entries(Invoke(reloaded, "CaptureDocument")).Select(JsonUtility.ToJson).OrderBy(x => x).ToArray());
        }

        [Test]
        public void LoadingTheSameSnapshotTwiceReplacesCopiesWithoutDuplicatingThem()
        {
            MahjongWallLayoutEditor editor = NewEditor(out V5ArtPrototype prototype);
            Draw(prototype, MahjongWallSeat.North);
            Copy(editor, 0);
            string snapshot = JsonUtility.ToJson(Invoke(editor, "CaptureDocument"));
            Copy(editor, 1);
            Assert.AreEqual(3, Entries(Invoke(editor, "CaptureDocument")).Length);
            Apply(editor, snapshot);
            Apply(editor, snapshot);
            Assert.AreEqual(2, Entries(Invoke(editor, "CaptureDocument")).Length);
        }

        [Test]
        public void DeletedBaseStaysDeletedWhileItsCopySurvivesColdLoad()
        {
            MahjongWallLayoutEditor editor = NewEditor(out V5ArtPrototype prototype);
            Draw(prototype, MahjongWallSeat.North);
            Copy(editor, 0);
            SetField(editor, "_selectedWall", 0);
            Invoke(editor, "DeleteSelectedWall");
            string snapshot = JsonUtility.ToJson(Invoke(editor, "CaptureDocument"));
            Object.DestroyImmediate(editor.gameObject);

            MahjongWallLayoutEditor reloaded = NewEditor(out V5ArtPrototype reloadedPrototype);
            Apply(reloaded, snapshot);
            Draw(reloadedPrototype, MahjongWallSeat.North);
            object[] entries = Entries(Invoke(reloaded, "CaptureDocument"));
            Assert.AreEqual(1, entries.Length);
            Assert.IsTrue((bool)Field(entries[0], "isRuntimeCopy"));
        }

        [Test]
        public void LoadingAnEarlierSnapshotRestoresDeletedBasesAndTheirFactories()
        {
            MahjongWallLayoutEditor editor = NewEditor(out V5ArtPrototype prototype);
            Draw(prototype, MahjongWallSeat.North);
            Draw(prototype, MahjongWallSeat.West);
            string snapshot = JsonUtility.ToJson(Invoke(editor, "CaptureDocument"));
            SetField(editor, "_selectedWall", 0);
            Invoke(editor, "DeleteSelectedWall");
            Assert.DoesNotThrow(() => Apply(editor, snapshot));
            Assert.AreEqual(2, Entries(Invoke(editor, "CaptureDocument")).Length);
            Copy(editor, 1);
            Assert.AreEqual(3, Entries(Invoke(editor, "CaptureDocument")).Length);
        }

        [Test]
        public void LegacyLayoutStillLoadsButInvalidCopiesAreRejected()
        {
            MahjongWallLayoutEditor editor = NewEditor(out V5ArtPrototype prototype);
            const string legacy = "{\"walls\":[{\"seat\":0,\"axis1X\":123,\"axis1Y\":200,"
                + "\"axis2X\":123,\"axis2Y\":170,\"axis3X\":140,\"axis3Y\":200,"
                + "\"axisLength\":160,\"axisSlopeDegrees\":0}]}";
            Apply(editor, legacy);
            Draw(prototype, MahjongWallSeat.North);
            object document = Invoke(editor, "CaptureDocument");
            object entry = Entries(document).Single();
            Assert.AreEqual(123f, (float)Field(entry, "axis1X"));
            SetPublicField(entry, "wallId", "invalid-copy");
            SetPublicField(entry, "isRuntimeCopy", true);
            SetPublicField(entry, "axisLength", -1f);
            Apply(editor, JsonUtility.ToJson(document));
            Assert.AreEqual(0, Entries(Invoke(editor, "CaptureDocument"))
                .Count(e => (bool)Field(e, "isRuntimeCopy")));
        }

        [Test]
        public void OldWestDiscardFramesMigrateForFallbackLoadCopiesAndResave()
        {
            MahjongWallLayoutEditor cold = NewEditor(out V5ArtPrototype prototype);
            const string old = "{\"version\":2,\"walls\":["
                + "{\"wallId\":\"base:6\",\"seat\":6,\"axis1X\":457,\"axis1Y\":202,\"axis2X\":457,\"axis2Y\":160,\"axis3X\":460.84,\"axis3Y\":205.84,\"axisLength\":224,\"axisSlopeDegrees\":101.31,\"faceWidthAxisOffsetDegrees\":-101.31,\"faceUvRotationDegrees\":90},"
                + "{\"wallId\":\"copy-old\",\"isRuntimeCopy\":true,\"seat\":6,\"axis1X\":500,\"axis1Y\":202,\"axis2X\":500,\"axis2Y\":160,\"axis3X\":503.84,\"axis3Y\":205.84,\"axisLength\":224,\"axisSlopeDegrees\":101.31,\"faceWidthAxisOffsetDegrees\":-101.31,\"faceUvRotationDegrees\":90}],\"deletedWallIds\":[\"base:0\"]}";
            var oldFrame = new MahjongWallAxisFrame(new MahjongWallPoint2(457, 202),
                new MahjongWallPoint2(457, 160), new MahjongWallPoint2(460.84f, 205.84f), 224, 101.31f, -101.31f, 90);
            MahjongWallAxisFrame fallback = cold.ResolveFrame(MahjongWallSeat.WestDiscard, oldFrame);
            Assert.That(Mathf.Abs(fallback.Axis2Start.X - fallback.Axis1Start.X), Is.GreaterThan(30f));
            var valid = new MahjongWallAxisFrame(new MahjongWallPoint2(460, 361),
                new MahjongWallPoint2(422, 361), new MahjongWallPoint2(460, 353), 150, -90, 0, 90);
            Assert.AreEqual(valid.Axis2Start.X, cold.ResolveFrame(MahjongWallSeat.WestDiscard, valid).Axis2Start.X);
            Assert.AreEqual(oldFrame.Axis2Start.X, cold.ResolveFrame(MahjongWallSeat.EastDiscard, oldFrame).Axis2Start.X);
            Apply(cold, old);
            Draw(prototype, MahjongWallSeat.WestDiscard);
            object[] first = Entries(Invoke(cold, "CaptureDocument"));
            Assert.AreEqual(2, first.Length);
            Assert.That(first.All(e => Mathf.Abs((float)Field(e, "axis2X") - (float)Field(e, "axis1X")) > 30f));
            string saved = JsonUtility.ToJson(Invoke(cold, "CaptureDocument"));
            Assert.That(saved, Does.Contain("base:0"));
            MahjongWallLayoutEditor loaded = NewEditor(out V5ArtPrototype second);
            Draw(second, MahjongWallSeat.WestDiscard);
            Apply(loaded, old);
            string runtime = JsonUtility.ToJson(Invoke(loaded, "CaptureDocument"));
            Assert.AreEqual(saved, runtime);
            Apply(loaded, saved);
            Assert.AreEqual(saved, JsonUtility.ToJson(Invoke(loaded, "CaptureDocument")));
            MahjongWallLayoutEditor reloaded = NewEditor(out V5ArtPrototype third);
            Apply(reloaded, saved);
            Draw(third, MahjongWallSeat.WestDiscard);
            Assert.AreEqual(saved, JsonUtility.ToJson(Invoke(reloaded, "CaptureDocument")));
        }

        [TestCase(0f)]
        [TestCase(90f)]
        public void EditedWestDiscardAndCopyKeepEveryFrameValueOnRuntimeAndColdLoad(float offset)
        {
            MahjongWallLayoutEditor editor = NewEditor(out V5ArtPrototype prototype);
            Draw(prototype, MahjongWallSeat.WestDiscard);
            object wall = ((System.Collections.IEnumerable)typeof(MahjongWallLayoutEditor)
                .GetField("_walls", PrivateInstance).GetValue(editor)).Cast<object>().Single();
            var frame = new MahjongWallAxisFrame(new MahjongWallPoint2(460, 361),
                new MahjongWallPoint2(450, 323), new MahjongWallPoint2(460, 353), 150, -90, offset, 90);
            Invoke(editor, "ApplyFrame", wall, frame);
            Invoke(editor, "SetWidthFalloffPerScreenY", wall, .0023f);
            Copy(editor, 0);
            Draw(prototype, MahjongWallSeat.North);
            SetField(editor, "_selectedWall", 2);
            Invoke(editor, "DeleteSelectedWall");
            object document = Invoke(editor, "CaptureDocument");
            object[] entries = Entries(document);
            Assert.AreEqual(2, entries.Length);
            Assert.That(JsonUtility.ToJson(document), Does.Contain("base:0"));
            object baseEntry = entries.Single(e => !(bool)Field(e, "isRuntimeCopy"));
            Assert.AreEqual(460f, Field(baseEntry, "axis1X"));
            Assert.AreEqual(361f, Field(baseEntry, "axis1Y"));
            Assert.AreEqual(450f, Field(baseEntry, "axis2X"));
            Assert.AreEqual(323f, Field(baseEntry, "axis2Y"));
            Assert.AreEqual(460f, Field(baseEntry, "axis3X"));
            Assert.AreEqual(353f, Field(baseEntry, "axis3Y"));
            Assert.AreEqual(offset, Field(baseEntry, "faceWidthAxisOffsetDegrees"));
            Assert.AreEqual(90f, Field(baseEntry, "faceUvRotationDegrees"));
            Assert.AreEqual(.0023f, Field(baseEntry, "widthFalloffPerScreenY"));
            string snapshot = JsonUtility.ToJson(document);
            Apply(editor, snapshot);
            Assert.AreEqual(snapshot, JsonUtility.ToJson(Invoke(editor, "CaptureDocument")));
            _tempPath = Path.Combine(Application.temporaryCachePath, Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(_tempPath, snapshot);
            MahjongWallLayoutEditor cold = NewEditor(out V5ArtPrototype coldPrototype);
            Apply(cold, File.ReadAllText(_tempPath));
            Draw(coldPrototype, MahjongWallSeat.WestDiscard);
            Draw(coldPrototype, MahjongWallSeat.North);
            Assert.AreEqual(snapshot, JsonUtility.ToJson(Invoke(cold, "CaptureDocument")));

            if (offset == 0f)
            {
                // Files created before the marker also preserve the reviewer's explicit-axis example.
                foreach (object entry in entries) SetPublicField(entry, "explicitTileGeometry", false);
                Apply(editor, JsonUtility.ToJson(document));
                object loadedBase = Entries(Invoke(editor, "CaptureDocument")).Single(e => !(bool)Field(e, "isRuntimeCopy"));
                Assert.AreEqual(450f, Field(loadedBase, "axis2X"));
                Assert.AreEqual(323f, Field(loadedBase, "axis2Y"));
            }
        }

        [Test]
        public void CandidateAndOtherSeatFramesRemainUnchangedThroughLoading()
        {
            MahjongWallLayoutEditor editor = NewEditor(out V5ArtPrototype prototype);
            Draw(prototype, MahjongWallSeat.WestDiscard);
            Draw(prototype, MahjongWallSeat.EastDiscard);
            object[] walls = ((System.Collections.IEnumerable)typeof(MahjongWallLayoutEditor)
                .GetField("_walls", PrivateInstance).GetValue(editor)).Cast<object>().ToArray();
            var candidate = new MahjongWallAxisFrame(new MahjongWallPoint2(460, 361),
                new MahjongWallPoint2(422, 361), new MahjongWallPoint2(460, 353), 150, -90, 0, 90);
            var other = new MahjongWallAxisFrame(new MahjongWallPoint2(460, 361),
                new MahjongWallPoint2(450, 323), new MahjongWallPoint2(460, 353), 150, -90, 0, -90);
            Invoke(editor, "ApplyFrame", walls[0], candidate);
            Invoke(editor, "ApplyFrame", walls[1], other);
            Copy(editor, 0);
            string snapshot = JsonUtility.ToJson(Invoke(editor, "CaptureDocument"));
            Apply(editor, snapshot);
            Assert.AreEqual(snapshot, JsonUtility.ToJson(Invoke(editor, "CaptureDocument")));
            MahjongWallLayoutEditor cold = NewEditor(out V5ArtPrototype coldPrototype);
            Apply(cold, snapshot);
            Draw(coldPrototype, MahjongWallSeat.WestDiscard);
            Draw(coldPrototype, MahjongWallSeat.EastDiscard);
            CollectionAssert.AreEqual(
                Entries(Invoke(editor, "CaptureDocument")).Select(JsonUtility.ToJson).OrderBy(x => x).ToArray(),
                Entries(Invoke(cold, "CaptureDocument")).Select(JsonUtility.ToJson).OrderBy(x => x).ToArray());
        }

        private MahjongWallLayoutEditor NewEditor(out V5ArtPrototype prototype)
        {
            var root = new GameObject("Prototype layout persistence test", typeof(RectTransform));
            _roots.Add(root);
            RectTransform rect = root.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(1280f, 720f);
            var fontObject = new GameObject("Control font", typeof(RectTransform), typeof(Text));
            fontObject.transform.SetParent(root.transform, false);
            fontObject.GetComponent<Text>().font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var editor = root.AddComponent<MahjongWallLayoutEditor>();
            editor.enabled = false;
            editor.Configure(rect);
            // Tests own their input and never read or overwrite the user's default.json.
            SetField(editor, "_loaded", true);
            prototype = root.AddComponent<V5ArtPrototype>();
            prototype.enabled = false;
            return editor;
        }

        private static void Draw(V5ArtPrototype prototype, MahjongWallSeat seat)
        {
            MethodInfo method = typeof(V5ArtPrototype).GetMethod("DrawMahjongWall", PrivateInstance);
            object[] args = method.GetParameters().Select(p => p.DefaultValue).ToArray();
            args[0] = "Test wall " + seat;
            args[1] = seat;
            args[2] = prototype.transform;
            args[3] = 0;
            float x = seat == MahjongWallSeat.North ? 100f : 450f;
            args[4] = new MahjongWallAxisFrame(new MahjongWallPoint2(x, 200f),
                new MahjongWallPoint2(x, 170f), new MahjongWallPoint2(x + 15f, 200f), 160f, 0f);
            if (seat == MahjongWallSeat.WestDiscard)
            {
                args[5] = MahjongWallTextureMode.SideDiscardWall;
                args[10] = 6;
                args[12] = true;
                args[13] = MahjongWallOuterPoint.C1;
                args[15] = true;
            }
            method.Invoke(prototype, args);
        }

        private static void Copy(MahjongWallLayoutEditor editor, int index)
        {
            SetField(editor, "_selectedWall", index);
            Invoke(editor, "DuplicateSelectedWall");
        }

        private static void Apply(MahjongWallLayoutEditor editor, string json)
        {
            Type type = typeof(MahjongWallLayoutEditor).GetNestedType("LayoutDocument", BindingFlags.NonPublic);
            Invoke(editor, "ApplyDocument", JsonUtility.FromJson(json, type));
        }

        private static object[] Entries(object document) => ((Array)Field(document, "walls")).Cast<object>().ToArray();
        private static object Field(object instance, string name) => instance.GetType().GetField(name).GetValue(instance);
        private static void SetPublicField(object instance, string name, object value) => instance.GetType().GetField(name).SetValue(instance, value);
        private static void SetField(object instance, string name, object value) => instance.GetType().GetField(name, PrivateInstance).SetValue(instance, value);
        private static object Invoke(object instance, string name, params object[] args) => instance.GetType().GetMethod(name, PrivateInstance).Invoke(instance, args);
    }
}
