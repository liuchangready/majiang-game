#nullable enable
using System.Collections;
using System.Reflection;
using Mahjong.Unity.App;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Mahjong.Unity.Tests.PlayMode
{
    /// <summary>
    /// Opt-in boundary for the full-match driver. A normal Player launch must not
    /// create the driver component or enable match composition; only the explicit
    /// -full-match-driver argument does that. The real-session candidate/action and
    /// re-entry PlayMode cases remain LocalHostCompositionPlayModeTests.
    /// </summary>
    [TestFixture]
    public sealed class LocalFullMatchDriverPlayModeTests
    {
        [UnityTest]
        public IEnumerator BootstrapWithoutArgument_DoesNotCreateDriverOrRun()
        {
            LocalFullMatchPlayerDriver existing = Object.FindFirstObjectByType<LocalFullMatchPlayerDriver>();
            if (existing != null)
            {
                Object.DestroyImmediate(existing.gameObject);
            }

            MethodInfo? bootstrap = typeof(LocalFullMatchPlayerDriver).GetMethod(
                "Bootstrap",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(bootstrap, "The opt-in bootstrap method must exist.");
            bootstrap!.Invoke(null, null);
            yield return null;

            LocalFullMatchPlayerDriver created = Object.FindFirstObjectByType<LocalFullMatchPlayerDriver>();
            Assert.IsNull(created, "A normal launch must not create the full-match driver.");
        }
    }
}
