#if UNITY_EDITOR
using Mahjong.Unity.UI;
using Mahjong.Unity.Spectator;
using Mahjong.Unity.App;
using NUnit.Framework;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using System.Threading.Tasks;

namespace Mahjong.Unity.Tests.EditMode
{
    public sealed class LocalLiteFontFallbackTests
    {
        [Test]
        public void WindowsFallback_ProvidesEveryLiteMenuGlyph()
        {
            const string label = "\u6D4B\u8BD5\u5C40\u9762";
            Font font = FontFallbackResolver.CreateWindowsSystemFont(label, 32);
            Assert.IsNotNull(font);
            foreach (char character in label)
                Assert.IsTrue(font.GetCharacterInfo(character, out _, 32, FontStyle.Normal), "Missing glyph U+" + ((int)character).ToString("X4"));
        }

        [Test]
        public void LiteMenuAndSettlementActions_HaveOneReadableLabelWhenOsFontIsUnavailable()
        {
            FontFallbackResolver.WindowsSystemFontProviderForTest = (_, _) => null;
            var root = new GameObject("LiteLabels");
            try
            {
                Button play = MakeButton(root.transform, "Play", "template");
                Button quit = MakeButton(root.transform, "Quit", "template");
                var titleObject = new GameObject("Title");
                titleObject.transform.SetParent(root.transform, false);
                var title = titleObject.AddComponent<TextMeshProUGUI>();
                var menu = root.AddComponent<MenuPresenter>();
                Set(menu, "_playButton", play);
                Set(menu, "_quitButton", quit);
                Set(menu, "_titleText", title);
                Invoke(menu, "ConfigureLiteMenu");
                AssertVisibleLabel(play.gameObject, "New game");
                AssertVisibleLabel(quit.gameObject, "Exit");
                Button test = root.transform.Find("LocalLiteTestButton")!.GetComponent<Button>();
                AssertVisibleLabel(test.gameObject, "Test cases");
                Button player = root.transform.Find("LocalLiteCharacter-player")!.GetComponent<Button>();
                Button octopus = root.transform.Find("LocalLiteCharacter-octopus")!.GetComponent<Button>();
                AssertVisibleLabel(player.gameObject, "> Player");
                AssertVisibleLabel(octopus.gameObject, "Octopus");

                Button back = MakeButton(root.transform, "SettlementBack", "template");
                var settlement = root.AddComponent<SettlementPresenter>();
                Set(settlement, "_backButton", back);
                Invoke(settlement, "ConfigureLiteActions");
                Button restart = root.transform.Find("LocalLiteRestartButton")!.GetComponent<Button>();
                AssertVisibleLabel(back.gameObject, "Back to menu");
                AssertVisibleLabel(restart.gameObject, "New round");
            }
            finally
            {
                FontFallbackResolver.WindowsSystemFontProviderForTest = null;
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void RunContext_ChangesFixtureToRandomAndReportsWriteFailureWithoutLockingCaller()
        {
            LocalLiteRunContext.ResetForTest();
            string root = System.IO.Path.Combine(Application.temporaryCachePath, "lite-run-context-" + System.Guid.NewGuid().ToString("N"));
            LocalLiteRunContext.Initialize(root, "test", "build", "identity");
            LocalLiteRunContext.SetCharacter("octopus");
            StringAssert.Contains("\"characterId\":\"octopus\"", System.IO.File.ReadAllText(System.IO.Path.Combine(root, "run.json")));
            Assert.IsTrue(LocalLiteRunContext.RecordStage("pon", "table"));
            Assert.IsTrue(LocalLiteRunContext.RecordStage(null, "starting"));
            Assert.AreEqual("random", LocalLiteRunContext.CurrentCaseId);
            StringAssert.Contains("\"caseId\":\"random\"", System.IO.File.ReadAllText(System.IO.Path.Combine(root, "current-case.json")));
            StringAssert.Contains("\"characterId\":\"octopus\"", System.IO.File.ReadAllText(System.IO.Path.Combine(root, "current-case.json")));

            string blocker = System.IO.Path.Combine(root, "file-is-not-a-directory");
            System.IO.File.WriteAllText(blocker, "kept");
            LocalLiteRunContext.Initialize(blocker, "test", "build", "identity");
            Assert.IsFalse(LocalLiteRunContext.RecordStage("chi", "starting"));
            Assert.AreEqual("LOCAL_LITE_DIAGNOSTICS_UNAVAILABLE", LocalLiteRunContext.LastFailure);
            LocalLiteRunContext.ResetForTest();
        }

        [Test]
        public async Task LiteMenu_ClickedOctopusFlowsIntoLocalStart()
        {
            UiInputLifecycle.ResetForTest();
            FontFallbackResolver.WindowsSystemFontProviderForTest = (_, _) => null;
            var root = new GameObject("LiteCharacterStart");
            try
            {
                Button play = MakeButton(root.transform, "Play", "template");
                Button quit = MakeButton(root.transform, "Quit", "template");
                var menu = root.AddComponent<MenuPresenter>();
                var navigation = new LiteNavigationStub();
                menu.Initialize(navigation, true);
                Set(menu, "_playButton", play);
                Set(menu, "_quitButton", quit);
                Invoke(menu, "ConfigureLiteMenu");

                Button octopus = root.transform.Find("LocalLiteCharacter-octopus")!.GetComponent<Button>();
                octopus.onClick.Invoke();
                Assert.AreEqual("octopus", typeof(MenuPresenter).GetField("_selectedCharacterId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(menu));

                menu.OnPlayClicked();
                await Task.Yield();
                Assert.AreEqual("octopus", navigation.StartedCharacterId);
            }
            finally
            {
                FontFallbackResolver.WindowsSystemFontProviderForTest = null;
                UiInputLifecycle.ResetForTest();
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void LiteMenu_RepeatedCharacterSwitches_ReuseSingleLabelAndUniqueMarker(bool windowsFontAvailable)
        {
            FontFallbackResolver.WindowsSystemFontProviderForTest = windowsFontAvailable
                ? null
                : ((_, _) => null);
            var root = new GameObject("LiteSwitchLabels");
            try
            {
                Button play = MakeButton(root.transform, "Play", "template");
                Button quit = MakeButton(root.transform, "Quit", "template");
                var menu = root.AddComponent<MenuPresenter>();
                Set(menu, "_playButton", play);
                Set(menu, "_quitButton", quit);
                Invoke(menu, "ConfigureLiteMenu");

                Button player = root.transform.Find("LocalLiteCharacter-player")!.GetComponent<Button>();
                Button octopus = root.transform.Find("LocalLiteCharacter-octopus")!.GetComponent<Button>();

                string expectedPlayerActive = windowsFontAvailable ? "\u2713 \u724C\u624B" : "> Player";
                string expectedPlayerInactive = windowsFontAvailable ? "\u724C\u624B" : "Player";
                string expectedOctopusActive = windowsFontAvailable ? "\u2713 \u516B\u722A\u9C7C" : "> Octopus";
                string expectedOctopusInactive = windowsFontAvailable ? "\u516B\u722A\u9C7C" : "Octopus";

                AssertVisibleLabel(player.gameObject, expectedPlayerActive);
                AssertVisibleLabel(octopus.gameObject, expectedOctopusInactive);

                for (int cycle = 0; cycle < 3; cycle++)
                {
                    octopus.onClick.Invoke();
                    AssertSingleReusableLabel(player.gameObject, expectedPlayerInactive, windowsFontAvailable);
                    AssertSingleReusableLabel(octopus.gameObject, expectedOctopusActive, windowsFontAvailable);

                    player.onClick.Invoke();
                    AssertSingleReusableLabel(player.gameObject, expectedPlayerActive, windowsFontAvailable);
                    AssertSingleReusableLabel(octopus.gameObject, expectedOctopusInactive, windowsFontAvailable);
                }
            }
            finally
            {
                FontFallbackResolver.WindowsSystemFontProviderForTest = null;
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static void AssertSingleReusableLabel(GameObject button, string expected, bool windowsFontAvailable)
        {
            AssertVisibleLabel(button, expected);
            int legacyNodes = 0;
            foreach (Transform child in button.GetComponentsInChildren<Transform>(true))
            {
                if (child.name == "LocalLiteSystemFontLabel")
                {
                    legacyNodes++;
                }
            }

            Assert.LessOrEqual(legacyNodes, 1, button.name + " must reuse at most one LocalLiteSystemFontLabel child");
            if (windowsFontAvailable)
            {
                Assert.AreEqual(1, legacyNodes, button.name + " should reuse its single LocalLiteSystemFontLabel node");
            }
        }

        private sealed class LiteNavigationStub : IAppNavigation, ILocalLiteNavigation
        {
            public string? StartedCharacterId { get; private set; }
            public bool IsLocalGameStartAvailable => true;
            public void OpenLobby() { }
            public void BackToMenu() { }
            public void RequestQuit() { }
            public Task<LocalGameStartResult> StartLocalGameAsync() => Task.FromResult(LocalGameStartResult.Started());
            public Task<LocalGameStartResult> StartLocalGameAsync(string? scenarioId) => Task.FromResult(LocalGameStartResult.Started());
            public Task<LocalGameStartResult> StartLocalGameAsync(string? scenarioId, string characterId)
            {
                StartedCharacterId = characterId;
                return Task.FromResult(LocalGameStartResult.Started());
            }
            public Task<LocalGameStartResult> RestartLocalGameAsync() => Task.FromResult(LocalGameStartResult.Started());
            public Task<bool> ReturnToMenuLiteAsync() => Task.FromResult(true);
        }

        private static Button MakeButton(Transform parent, string name, string label)
        {
            var buttonObject = new GameObject(name, typeof(RectTransform), typeof(Button));
            buttonObject.transform.SetParent(parent, false);
            var textObject = new GameObject("Label", typeof(RectTransform));
            textObject.transform.SetParent(buttonObject.transform, false);
            textObject.AddComponent<TextMeshProUGUI>().text = label;
            return buttonObject.GetComponent<Button>();
        }

        private static void AssertVisibleLabel(GameObject button, string expected)
        {
            var labels = new System.Collections.Generic.List<string>();
            foreach (TMP_Text text in button.GetComponentsInChildren<TMP_Text>(true))
                if (text.enabled && text.gameObject.activeInHierarchy) labels.Add(text.text);
            foreach (UnityEngine.UI.Text text in button.GetComponentsInChildren<UnityEngine.UI.Text>(true))
                if (text.enabled && text.gameObject.activeInHierarchy) labels.Add(text.text);
            Assert.AreEqual(1, labels.Count, button.name + " must expose exactly one visible text label");
            Assert.AreEqual(expected, labels[0], button.name);
        }

        private static void Set(object target, string field, object value) =>
            typeof(MenuPresenter).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

        private static void Set(object target, string field, Button value) =>
            target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

        private static void Invoke(object target, string method) =>
            target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);
    }
}
#endif
