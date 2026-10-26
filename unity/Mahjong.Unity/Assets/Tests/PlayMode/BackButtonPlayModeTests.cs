using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Mahjong.Unity.App;
using Mahjong.Unity.UI;

namespace Mahjong.Unity.Tests.PlayMode
{
    public sealed class BackButtonPlayModeTests
    {
        [UnityTest]
        public IEnumerator BackRouter_ClosesModal_InPlayMode()
        {
            var root = AppRoot.EnsureInitialized(new AppConfig());
            yield return null;

            Assert.IsNotNull(root.BackRouter);

            bool modalClosed = false;
            root.BackRouter.RegisterModal(() => modalClosed = true);

            var result = root.BackRouter.HandleBack(BackEventSource.DesktopEscape, isAndroidPlatform: false);
            Assert.AreEqual(BackRouteResult.Consumed, result);
            Assert.IsTrue(modalClosed);
        }
    }
}
