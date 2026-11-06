#nullable enable
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using Mahjong.Unity.App;
using Mahjong.Unity.UI;

namespace Mahjong.Unity.Tests.PlayMode
{
    public sealed class PauseResumePlayModeTests
    {
        [UnityTest]
        public IEnumerator InputLifecycle_CancelArmOnPause_PreventsDelayedClick()
        {
            UiInputLifecycle.ResetForTest();
            var lifecycle = UiInputLifecycle.Instance;

            // 1. Pointer Down arms
            bool armed = lifecycle.TryArm(pointerId: 1, new Vector2(100, 100));
            Assert.IsTrue(armed);
            Assert.IsTrue(lifecycle.IsArmed);
            long downGen = lifecycle.PressGeneration;

            // 2. Pause happens while pointer is held down
            lifecycle.OnPause();
            Assert.IsFalse(lifecycle.IsArmed, "Arm should be cancelled on pause");
            Assert.IsFalse(lifecycle.AcceptingInput);
            Assert.Greater(lifecycle.PressGeneration, downGen);

            // 3. Application resumes
            lifecycle.OnResume();
            Assert.IsTrue(lifecycle.AcceptingInput, "Accepting input restored on resume");
            Assert.IsFalse(lifecycle.IsArmed, "Arm should remain false after resume");

            // 4. Pointer Release arrives after resume
            bool released = lifecycle.TryRelease(pointerId: 1, downGen);
            Assert.IsFalse(released, "Delayed release must not trigger a click");

            yield return null;
        }

        [UnityTest]
        public IEnumerator InputLifecycle_CancelArmOnTransition_PreventsCrossSceneClick()
        {
            UiInputLifecycle.ResetForTest();
            var lifecycle = UiInputLifecycle.Instance;

            // Pointer down in scene
            bool armed = lifecycle.TryArm(pointerId: 1, new Vector2(200, 200));
            Assert.IsTrue(armed);
            long downGen = lifecycle.PressGeneration;

            // Scene transition starts
            lifecycle.OnTransition();
            Assert.IsFalse(lifecycle.IsArmed);
            Assert.IsFalse(lifecycle.AcceptingInput);
            Assert.Greater(lifecycle.PressGeneration, downGen);

            // Transition finishes
            lifecycle.TransitionEnd();
            Assert.IsTrue(lifecycle.AcceptingInput);
            Assert.IsFalse(lifecycle.IsArmed);

            // Old release arrives
            bool released = lifecycle.TryRelease(pointerId: 1, downGen);
            Assert.IsFalse(released);

            yield return null;
        }

        [UnityTest]
        public IEnumerator RealButtonDownMustArmLifecycle()
        {
            var root = AppRoot.EnsureInitialized(new AppConfig());
            var load = root.Router.LoadAsync(SceneId.Menu);
            while (!load.IsCompleted) yield return null;
            yield return null;

            UiInputLifecycle.ResetForTest();
            var button = GameObject.Find("PlayButton");
            Assert.IsNotNull(button, "PlayButton must exist in Menu scene.");
            var data = new PointerEventData(EventSystem.current)
            {
                pointerId = -1,
                button = PointerEventData.InputButton.Left,
                position = new Vector2(Screen.width / 2f, Screen.height / 2f)
            };
            ExecuteEvents.Execute(button, data, ExecuteEvents.pointerDownHandler);
            Assert.IsTrue(UiInputLifecycle.Instance.IsArmed, "Real UGUI down must reach UiInputLifecycle.TryArm.");
        }
    }
}
