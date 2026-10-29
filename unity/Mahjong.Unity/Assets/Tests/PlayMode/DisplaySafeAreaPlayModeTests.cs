#nullable enable
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.TestTools;
using Mahjong.Unity.App;
using Mahjong.Unity.UI;

namespace Mahjong.Unity.Tests.PlayMode
{
    public sealed class DisplaySafeAreaPlayModeTests
    {
        [UnityTest]
        public IEnumerator SafeAreaFitter_AppliesScaleAndBounds()
        {
            var canvasGo = new GameObject("TestCanvas");
            var canvas = canvasGo.AddComponent<Canvas>();
            var scaler = canvasGo.AddComponent<CanvasScaler>();

            var safeRootGo = new GameObject("SafeAreaRoot");
            safeRootGo.transform.SetParent(canvasGo.transform, false);
            var safeRect = safeRootGo.AddComponent<RectTransform>();
            var fitter = safeRootGo.AddComponent<SafeAreaFitter>();

            yield return null;

            fitter.ApplySafeArea();

            Assert.AreEqual(CanvasScaler.ScaleMode.ConstantPixelSize, scaler.uiScaleMode);
            Assert.Greater(scaler.scaleFactor, 0f);
            Assert.IsNotNull(safeRect);

            Object.Destroy(canvasGo);
        }

        [UnityTest]
        public IEnumerator ActualTouchTargetsMustMeetFrozenMinimum()
        {
            var root = AppRoot.EnsureInitialized(new AppConfig());
            var load = root.Router.LoadAsync(SceneId.Menu);
            while (!load.IsCompleted) yield return null;
            yield return null;

            var playBtn = GameObject.Find("PlayButton");
            Assert.IsNotNull(playBtn, "PlayButton must exist in Menu scene.");
            var rect = playBtn.GetComponent<RectTransform>();
            Assert.GreaterOrEqual(rect.rect.height, 88f, "Button height must meet frozen 88 design unit target.");
        }

        [UnityTest]
        public IEnumerator MenuControlsMustHaveScreenArea()
        {
            var root = AppRoot.EnsureInitialized(new AppConfig());
            var load = root.Router.LoadAsync(SceneId.Menu);
            while (!load.IsCompleted) yield return null;
            yield return null;

            Canvas.ForceUpdateCanvases();
            var playBtn = GameObject.Find("PlayButton");
            Assert.IsNotNull(playBtn);
            var rect = playBtn.GetComponent<RectTransform>();
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            Assert.Greater(corners[2].x - corners[0].x, 10f, "Button width must be positive screen area.");
            Assert.Greater(corners[2].y - corners[0].y, 10f, "Button height must be positive screen area.");
            Assert.That(corners[0].x, Is.InRange(0f, (float)Screen.width));
            Assert.That(corners[0].y, Is.InRange(0f, (float)Screen.height));
        }
    }
}
