using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Mahjong.Unity.UI;

namespace Mahjong.Unity.Tests.PlayMode
{
    public sealed class PauseResumePlayModeTests
    {
        [UnityTest]
        public IEnumerator InputLifecycle_CancelArmOnPause_PreventsDelayedClick()
        {
            var lifecycle = UiInputLifecycle.Instance;
            UiInputLifecycle.ResetForTest();
            lifecycle = UiInputLifecycle.Instance;

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

            // Transition begins
            lifecycle.OnTransition();
            Assert.IsFalse(lifecycle.IsArmed);

            // Transition ends in next scene
            lifecycle.TransitionEnd();
            Assert.IsFalse(lifecycle.IsArmed);

            // Release
            bool released = lifecycle.TryRelease(pointerId: 1, downGen);
            Assert.IsFalse(released);

            yield return null;
        }
    }
}
