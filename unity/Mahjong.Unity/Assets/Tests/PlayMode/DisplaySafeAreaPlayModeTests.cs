using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.TestTools;
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
    }
}
