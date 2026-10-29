using System.Threading.Tasks;
using NUnit.Framework;
using Mahjong.Unity.App;
using Mahjong.Unity.UI;

namespace Mahjong.Unity.Tests.EditMode
{
    [TestFixture]
    public sealed class SceneRoutingTests
    {
        [Test]
        public void AppRouter_Initializes_WithBootScene()
        {
            var dispatcher = new MainThreadDispatcher(128);
            var comp = new AppComposition(new AppConfig(), dispatcher);
            var backRouter = new BackRouter();
            var router = new AppRouter(comp, backRouter);

            Assert.AreEqual(SceneId.Boot, router.CurrentScene);
            Assert.IsFalse(router.IsTransitioning);
        }

        [Test]
        public void BackRouter_TransitionGate_BlocksBack()
        {
            var backRouter = new BackRouter();
            bool isTransitioning = true;
            backRouter.SetTransitionGate(() => isTransitioning);

            bool sceneActionCalled = false;
            backRouter.SetSceneBackHandler(() => sceneActionCalled = true);

            var result = backRouter.HandleBack(BackEventSource.DesktopEscape, isAndroidPlatform: false);
            Assert.AreEqual(BackRouteResult.Consumed, result);
            Assert.IsFalse(sceneActionCalled, "Scene action should not be called while transitioning");
        }
    }
}
