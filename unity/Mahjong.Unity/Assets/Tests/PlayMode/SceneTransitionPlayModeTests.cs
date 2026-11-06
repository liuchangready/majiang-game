using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Mahjong.Unity.App;
using Mahjong.Unity.UI;

namespace Mahjong.Unity.Tests.PlayMode
{
    public sealed class SceneTransitionPlayModeTests
    {
        [SetUp]
        public void SetUp()
        {
            UiThemeProvider.EnsureLineBreakingRules();
        }

        [UnityTest]
        public IEnumerator Router_TransitionsBetweenScenes_WithoutDuplicateRoots()
        {
            var root = AppRoot.EnsureInitialized(new AppConfig());
            yield return null;

            Assert.IsNotNull(root.Router);

            // Test transition to Menu
            var loadMenuTask = root.Router.LoadAsync(SceneId.Menu);
            while (!loadMenuTask.IsCompleted)
            {
                yield return null;
            }
            Assert.IsTrue(loadMenuTask.Result);
            Assert.AreEqual(SceneId.Menu, root.Router.CurrentScene);

            // Test transition to Lobby
            var loadLobbyTask = root.Router.LoadAsync(SceneId.Lobby);
            while (!loadLobbyTask.IsCompleted)
            {
                yield return null;
            }
            Assert.IsTrue(loadLobbyTask.Result);
            Assert.AreEqual(SceneId.Lobby, root.Router.CurrentScene);

            // Test return to Menu
            var returnTask = root.Router.ReturnToMenuAsync();
            while (!returnTask.IsCompleted)
            {
                yield return null;
            }
            Assert.IsTrue(returnTask.Result);
            Assert.AreEqual(SceneId.Menu, root.Router.CurrentScene);

            var roots = Object.FindObjectsByType<AppRoot>(FindObjectsSortMode.None);
            Assert.AreEqual(1, roots.Length, "Only 1 AppRoot must remain after transitions.");
        }
    }
}
