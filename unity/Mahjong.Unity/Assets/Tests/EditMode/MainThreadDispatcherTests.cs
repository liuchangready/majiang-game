using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Mahjong.Unity.App;

namespace Mahjong.Unity.Tests.EditMode
{
    [TestFixture]
    public sealed class MainThreadDispatcherTests
    {
        [Test]
        public void Dispatcher_ExecutesInFifoOrder()
        {
            var dispatcher = new MainThreadDispatcher(128);
            var results = new List<int>();

            dispatcher.Post(() => results.Add(1));
            dispatcher.Post(() => results.Add(2));
            dispatcher.Post(() => results.Add(3));

            int executed = dispatcher.Update(128, 100f);
            Assert.AreEqual(3, executed);
            Assert.AreEqual(new[] { 1, 2, 3 }, results);
        }

        [Test]
        public void Dispatcher_DropsOldGenerationActions()
        {
            var dispatcher = new MainThreadDispatcher(128);
            dispatcher.CurrentGeneration = 2;

            var results = new List<int>();
            dispatcher.Post(() => results.Add(1), generation: 1); // old generation
            dispatcher.Post(() => results.Add(2), generation: 2); // current generation
            dispatcher.Post(() => results.Add(3), generation: 1); // old generation

            int executed = dispatcher.Update(128, 100f);
            Assert.AreEqual(1, executed);
            Assert.AreEqual(new[] { 2 }, results);
            Assert.AreEqual(2, dispatcher.DroppedOldGenerationCount);
        }

        [Test]
        public void Dispatcher_Overflow_TriggersFailureAndRejects()
        {
            LogAssert.Expect(LogType.Error, new Regex("UI_DISPATCH_OVERFLOW"));
            var dispatcher = new MainThreadDispatcher(capacity: 3);
            bool overflowEventFired = false;
            dispatcher.Overflowed += () => overflowEventFired = true;

            Assert.IsTrue(dispatcher.Post(() => { }));
            Assert.IsTrue(dispatcher.Post(() => { }));
            Assert.IsTrue(dispatcher.Post(() => { }));

            // 4th action exceeds capacity 3
            bool accepted = dispatcher.Post(() => { });
            Assert.IsFalse(accepted);
            Assert.IsTrue(dispatcher.HasOverflowed);
            Assert.IsTrue(overflowEventFired);
        }

        [Test]
        public void Dispatcher_HandlerException_DoesNotHaltQueue()
        {
            LogAssert.Expect(LogType.Error, new Regex("UI_DISPATCH_HANDLER_EXCEPTION"));
            var dispatcher = new MainThreadDispatcher(128);
            var results = new List<int>();

            dispatcher.Post(() => results.Add(1));
            dispatcher.Post(() => throw new InvalidOperationException("Simulated UI handler failure"));
            dispatcher.Post(() => results.Add(2));

            int executed = dispatcher.Update(128, 100f);
            Assert.AreEqual(3, executed);
            Assert.AreEqual(new[] { 1, 2 }, results);
            Assert.AreEqual(1, dispatcher.HandlerExceptionCount);
        }
    }
}
