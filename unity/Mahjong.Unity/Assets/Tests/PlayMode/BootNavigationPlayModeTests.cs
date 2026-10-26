using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using Mahjong.Unity.App;

namespace Mahjong.Unity.Tests.PlayMode
{
    public sealed class BootNavigationPlayModeTests
    {
        [UnityTest]
        public IEnumerator Boot_EnsuresSingleAppRootAndEventSystem()
        {
            var root = AppRoot.EnsureInitialized(new AppConfig());
            yield return null;

            Assert.IsNotNull(AppRoot.Instance);
            Assert.AreSame(root, AppRoot.Instance);

            var roots = Object.FindObjectsByType<AppRoot>(FindObjectsSortMode.None);
            Assert.AreEqual(1, roots.Length, "Duplicate AppRoot instances must not exist.");

            var eventSystems = Object.FindObjectsByType<EventSystem>(FindObjectsSortMode.None);
            Assert.AreEqual(1, eventSystems.Length, "Single persistent EventSystem must exist.");
        }
    }
}
