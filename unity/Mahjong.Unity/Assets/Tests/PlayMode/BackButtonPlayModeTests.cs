#nullable enable
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using Mahjong.Unity.App;
using Mahjong.Unity.UI;

namespace Mahjong.Unity.Tests.PlayMode
{
    public sealed class BackButtonPlayModeTests
    {
        private sealed class TestNav : IAppNavigation
        {
            public int Plays;
            public void OpenLobby() { Plays++; }
            public void BackToMenu() { }
            public void RequestQuit() { }
        }

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

        [UnityTest]
        public IEnumerator ButtonBeforeTriggerMustNotDoubleActivatePointer()
        {
            var root = AppRoot.EnsureInitialized(new AppConfig());
            var load = root.Router.LoadAsync(SceneId.Menu);
            while (!load.IsCompleted) yield return null;
            Assert.IsTrue(load.Result);
            yield return null;
            UiInputLifecycle.ResetForTest();
            var nav = new TestNav();
            Object.FindFirstObjectByType<MenuPresenter>().Initialize(nav);
            var target = GameObject.Find("PlayButton");
            Object.DestroyImmediate(target.GetComponent<UiPointerLifecycleTrigger>());
            target.AddComponent<UiPointerLifecycleTrigger>();
            var p = new PointerEventData(EventSystem.current) { pointerId = -1, button = PointerEventData.InputButton.Left, eligibleForClick = true };
            ExecuteEvents.Execute(target, p, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(target, p, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.Execute(target, p, ExecuteEvents.pointerClickHandler);
            yield return null;
            ExecuteEvents.Execute(target, p, ExecuteEvents.pointerClickHandler);
            Assert.AreEqual(1, nav.Plays, "Pointer activation must consume the gesture independent of component order and reject its duplicate tail event.");
        }
    }
}
