using System.Collections;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;
using Mahjong.Unity.App;

namespace Mahjong.Unity.Tests.PlayMode
{
    public sealed class MainThreadDispatchPlayModeTests
    {
        [UnityTest]
        public IEnumerator Dispatcher_ExecutesOnMainThread()
        {
            var root = AppRoot.EnsureInitialized(new AppConfig());
            yield return null;

            bool executed = false;
            bool wasMainThread = false;

            Task.Run(() =>
            {
                root.Dispatcher?.Post(() =>
                {
                    executed = true;
                    wasMainThread = AppRoot.IsMainThread;
                });
            });

            // Wait a few frames for dispatcher update
            for (int i = 0; i < 5; i++)
            {
                yield return null;
                if (executed) break;
            }

            Assert.IsTrue(executed, "Queued action should have executed");
            Assert.IsTrue(wasMainThread, "Queued action should execute on Unity main thread");
        }
    }
}
