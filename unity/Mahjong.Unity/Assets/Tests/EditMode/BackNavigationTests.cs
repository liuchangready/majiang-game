using NUnit.Framework;
using Mahjong.Unity.UI;

namespace Mahjong.Unity.Tests.EditMode
{
    [TestFixture]
    public sealed class BackNavigationTests
    {
        [Test]
        public void BackRouter_ModalPriority_ClosesTopmostModalOnly()
        {
            var router = new BackRouter();
            bool modalClosed = false;
            bool sceneBackCalled = false;

            router.RegisterModal(() => modalClosed = true);
            router.SetSceneBackHandler(() => sceneBackCalled = true);

            var result = router.HandleBack(BackEventSource.DesktopEscape, isAndroidPlatform: false);

            Assert.AreEqual(BackRouteResult.Consumed, result);
            Assert.IsTrue(modalClosed, "Modal should be closed");
            Assert.IsFalse(sceneBackCalled, "Scene back should not be triggered when modal is open");

            // Second back invocation now triggers scene back
            var result2 = router.HandleBack(BackEventSource.DesktopEscape, isAndroidPlatform: false);
            Assert.AreEqual(BackRouteResult.Consumed, result2);
            Assert.IsTrue(sceneBackCalled, "Second back should trigger scene back");
        }

        [Test]
        public void BackRouter_PausePriority_ConsumesWithoutTriggeringBack()
        {
            var router = new BackRouter();
            bool isPaused = true;
            router.SetPauseGate(() => isPaused);

            bool sceneBackCalled = false;
            router.SetSceneBackHandler(() => sceneBackCalled = true);

            var result = router.HandleBack(BackEventSource.DesktopEscape, isAndroidPlatform: false);
            Assert.AreEqual(BackRouteResult.Consumed, result);
            Assert.IsFalse(sceneBackCalled);
        }

        [Test]
        public void BackRouter_SourceValidation_RejectsWrongPlatformSource()
        {
            var router = new BackRouter();
            router.SetSceneBackHandler(() => { });

            // Desktop platform receiving AndroidPlatformBack
            var r1 = router.HandleBack(BackEventSource.AndroidPlatformBack, isAndroidPlatform: false);
            Assert.AreEqual(BackRouteResult.RejectedWrongSource, r1);

            // Android platform receiving DesktopEscape
            var r2 = router.HandleBack(BackEventSource.DesktopEscape, isAndroidPlatform: true);
            Assert.AreEqual(BackRouteResult.RejectedWrongSource, r2);
        }
    }
}
