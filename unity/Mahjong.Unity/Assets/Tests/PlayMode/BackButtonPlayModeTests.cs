#nullable enable
using System.Collections;
using System.Reflection;
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

        [UnityTest]
        public IEnumerator PersistentBackRouterMustBindPauseGate()
        {
            var root = AppRoot.EnsureInitialized(new AppConfig());
            yield return null;
            var field = typeof(BackRouter).GetField("_isPaused", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field?.GetValue(root.BackRouter), "Root never binds lifecycle pause gate to BackRouter.");
        }

        [UnityTest]
        public IEnumerator RealQuitModalMustRegisterBackRouter()
        {
            var root = AppRoot.EnsureInitialized(new AppConfig());
            var load = root.Router.LoadAsync(SceneId.Menu);
            while (!load.IsCompleted) yield return null;
            yield return null;
            var menu = Object.FindFirstObjectByType<MenuPresenter>();
            Assert.IsNotNull(menu);
            menu.OnQuitClicked();
            var modal = Object.FindFirstObjectByType<ModalPresenter>();
            Assert.IsNotNull(modal);
            Assert.IsTrue(modal.IsOpen);
            var field = typeof(ModalPresenter).GetField("_backRouter", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.AreSame(root.BackRouter, field?.GetValue(modal), "App never calls SetBackRouter for real modal.");
        }
    }
}
