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
