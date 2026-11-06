#nullable enable
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
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
            public bool IsLocalGameStartAvailable => false;
            public System.Threading.Tasks.Task<LocalGameStartResult> StartLocalGameAsync() =>
                System.Threading.Tasks.Task.FromResult(LocalGameStartResult.Failed(LocalGameStartResult.LocalHostUnavailableCode));
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

        [UnityTest]
        public IEnumerator ButtonBeforeTriggerSubmitAfterPointerMustActivateBoth()
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
            Assert.AreEqual(1, nav.Plays);
            yield return null;

            ExecuteEvents.Execute(target, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler);
            Assert.AreEqual(2, nav.Plays, "First real keyboard Submit after consumed pointer must activate exactly once independent of component order.");
        }

        [UnityTest]
        public IEnumerator ButtonBeforeTriggerSubmitMustNotAuthorizePointerTail()
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

            ExecuteEvents.Execute(target, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler);
            Assert.AreEqual(1, nav.Plays);
            yield return null;

            var p = new PointerEventData(EventSystem.current) { pointerId = -1, button = PointerEventData.InputButton.Left, eligibleForClick = true };
            ExecuteEvents.Execute(target, p, ExecuteEvents.pointerClickHandler);
            Assert.AreEqual(1, nav.Plays, "A completed Submit must not leave a permit for a later click without a pointer gesture.");
        }

        [UnityTest]
        public IEnumerator TriggerBeforeButtonSubmitAfterPointerMustActivateBoth()
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

            var p = new PointerEventData(EventSystem.current) { pointerId = -1, button = PointerEventData.InputButton.Left, eligibleForClick = true };
            ExecuteEvents.Execute(target, p, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(target, p, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.Execute(target, p, ExecuteEvents.pointerClickHandler);
            Assert.AreEqual(1, nav.Plays);
            yield return null;

            ExecuteEvents.Execute(target, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler);
            Assert.AreEqual(2, nav.Plays, "Submit after pointer with standard trigger order must activate both.");
        }

        [UnityTest]
        public IEnumerator TriggerBeforeButtonSubmitMustNotAuthorizePointerTail()
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

            ExecuteEvents.Execute(target, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler);
            Assert.AreEqual(1, nav.Plays);
            yield return null;

            var p = new PointerEventData(EventSystem.current) { pointerId = -1, button = PointerEventData.InputButton.Left, eligibleForClick = true };
            ExecuteEvents.Execute(target, p, ExecuteEvents.pointerClickHandler);
            Assert.AreEqual(1, nav.Plays, "A completed Submit in standard order must not leave permit for tail click.");
        }

        [UnityTest]
        public IEnumerator ButtonBeforeTriggerConsecutiveSubmitsAcrossFramesMustActivate()
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

            ExecuteEvents.Execute(target, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler);
            Assert.AreEqual(1, nav.Plays);
            yield return null;

            ExecuteEvents.Execute(target, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler);
            Assert.AreEqual(2, nav.Plays, "Consecutive Submits across frames must activate independently.");
        }

        [UnityTest]
        public IEnumerator SubmitAfterPauseResume_GateBehavior()
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

            UiInputLifecycle.Instance.OnPause();
            ExecuteEvents.Execute(target, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler);
            Assert.AreEqual(0, nav.Plays, "Submit during pause must be blocked.");
            yield return null;

            UiInputLifecycle.Instance.OnResume();
            ExecuteEvents.Execute(target, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler);
            Assert.AreEqual(1, nav.Plays, "Submit after resume must succeed.");
        }

        [UnityTest]
        public IEnumerator ButtonBeforeTriggerTailThenSubmit() => RunOrderScenario(false, "tail");

        [UnityTest]
        public IEnumerator TriggerBeforeButtonTailThenSubmit() => RunOrderScenario(true, "tail");

        [UnityTest]
        public IEnumerator ButtonBeforeTriggerCancelledPauseThenSubmit() => RunOrderScenario(false, "pause");

        [UnityTest]
        public IEnumerator TriggerBeforeButtonCancelledPauseThenSubmit() => RunOrderScenario(true, "pause");

        [UnityTest]
        public IEnumerator ButtonBeforeTriggerCancelledFocusThenSubmit() => RunOrderScenario(false, "focus");

        [UnityTest]
        public IEnumerator TriggerBeforeButtonCancelledFocusThenSubmit() => RunOrderScenario(true, "focus");

        private IEnumerator RunOrderScenario(bool triggerFirst, string mode)
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

            if (!triggerFirst)
            {
                Object.DestroyImmediate(target.GetComponent<UiPointerLifecycleTrigger>());
                target.AddComponent<UiPointerLifecycleTrigger>();
            }

            var components = target.GetComponents<Component>();
            int triggerIndex = System.Array.FindIndex(components, c => c is UiPointerLifecycleTrigger);
            int buttonIndex = System.Array.FindIndex(components, c => c is Button);
            Assert.AreEqual(triggerFirst, triggerIndex < buttonIndex, "Must exercise the stated real component order");

            var pointer = new PointerEventData(EventSystem.current) { pointerId = -1, button = PointerEventData.InputButton.Left, eligibleForClick = true };
            int expected;
            if (mode == "tail")
            {
                ExecuteEvents.Execute(target, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler);
                Assert.AreEqual(1, nav.Plays);
                yield return null;

                ExecuteEvents.Execute(target, pointer, ExecuteEvents.pointerClickHandler);
                Assert.AreEqual(1, nav.Plays, "Stale click must remain rejected");
                expected = 2;
            }
            else
            {
                ExecuteEvents.Execute(target, pointer, ExecuteEvents.pointerDownHandler);
                Assert.IsTrue(UiInputLifecycle.Instance.IsArmed);

                if (mode == "pause")
                {
                    UiInputLifecycle.Instance.OnPause();
                    UiInputLifecycle.Instance.OnResume();
                }
                else
                {
                    UiInputLifecycle.Instance.OnFocusLost();
                    UiInputLifecycle.Instance.OnFocusGained();
                }

                Assert.IsFalse(UiInputLifecycle.Instance.IsArmed);
                expected = 1;
            }

            yield return null;
            ExecuteEvents.Execute(target, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler);
            Assert.AreEqual(expected, nav.Plays, "First fresh real Submit must succeed after " + mode + " recovery, independent of component order");
        }
    }
}
