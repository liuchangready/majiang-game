#nullable enable
using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using Mahjong.Unity.UI;

namespace Mahjong.Unity.App
{
    public sealed class GameBootstrapper : MonoBehaviour
    {
        [SerializeField] private AppConfig _config = new AppConfig();
        [SerializeField] private UiTheme? _theme;

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            AppRoot.EnsureInitialized(_config, _theme);
        }

        private async void Start()
        {
            Debug.Log("BOOTSTRAPPER_START: Initializing Menu load...");
            if (AppRoot.Instance != null && AppRoot.Instance.Router != null)
            {
                bool success = await AppRoot.Instance.Router.LoadAsync(SceneId.Menu);
                if (!success)
                {
                    Debug.LogError("BOOT_SCENE_LOAD_FAILED: Failed to load Menu scene from Boot.");
                    return;
                }
                Debug.Log("BOOT_SCENE_LOAD_SUCCESS: Menu scene loaded.");

                string[] args = Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "-smoke-test" && i + 1 < args.Length)
                    {
                        string dir = args[i + 1];
                        StartCoroutine(RunSmokeSequence(dir));
                        break;
                    }
                }
            }
        }

        private void CaptureFrame(string path)
        {
            int w = Screen.width;
            int h = Screen.height;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            byte[] bytes = ImageConversion.EncodeToPNG(tex);
            File.WriteAllBytes(path, bytes);
            Destroy(tex);
        }

        private IEnumerator RunSmokeSequence(string screenshotDir)
        {
            Directory.CreateDirectory(screenshotDir);
            Debug.Log($"SMOKE_START: Output directory={screenshotDir}");

            // Wait for Menu scene to settle
            for (int f = 0; f < 10; f++) yield return null;
            yield return new WaitForEndOfFrame();

            // 1. Capture 01-menu.png
            string shot1 = Path.Combine(screenshotDir, "01-menu.png");
            CaptureFrame(shot1);
            yield return new WaitForSeconds(0.2f);
            Debug.Log($"SMOKE_CAPTURED: 01-menu.png (exists={File.Exists(shot1)})");

            // 2. Navigate to Lobby via MenuPresenter / PlayButton
            var menuPresenter = FindFirstObjectByType<MenuPresenter>();
            if (menuPresenter != null)
            {
                Debug.Log("SMOKE: Clicking PlayButton via MenuPresenter...");
                menuPresenter.OnPlayClicked();
            }
            else
            {
                var playBtn = GameObject.Find("PlayButton")?.GetComponent<Button>();
                if (playBtn != null)
                {
                    Debug.Log("SMOKE: Clicking PlayButton directly...");
                    playBtn.onClick.Invoke();
                }
                else
                {
                    Debug.LogError("SMOKE_ERROR: PlayButton not found in Menu scene!");
                    Application.Quit(1);
                    yield break;
                }
            }

            // Wait for Lobby scene to load
            float timer = 0f;
            while (AppRoot.Instance?.Router?.CurrentScene != SceneId.Lobby && timer < 10f)
            {
                timer += Time.unscaledDeltaTime;
                yield return null;
            }

            if (AppRoot.Instance?.Router?.CurrentScene != SceneId.Lobby)
            {
                Debug.LogError("SMOKE_ERROR: Failed to transition to Lobby within 10s!");
                Application.Quit(2);
                yield break;
            }

            for (int f = 0; f < 10; f++) yield return null;
            yield return new WaitForEndOfFrame();

            // 3. Capture 02-lobby.png
            string shot2 = Path.Combine(screenshotDir, "02-lobby.png");
            CaptureFrame(shot2);
            yield return new WaitForSeconds(0.2f);
            Debug.Log($"SMOKE_CAPTURED: 02-lobby.png (exists={File.Exists(shot2)})");

            // 4. Navigate back to Menu via LobbyPresenter / BackButton
            var lobbyPresenter = FindFirstObjectByType<LobbyPresenter>();
            if (lobbyPresenter != null)
            {
                Debug.Log("SMOKE: Clicking BackButton via LobbyPresenter...");
                lobbyPresenter.OnBackClicked();
            }
            else
            {
                var backBtn = GameObject.Find("BackButton")?.GetComponent<Button>();
                if (backBtn != null)
                {
                    Debug.Log("SMOKE: Clicking BackButton directly...");
                    backBtn.onClick.Invoke();
                }
                else
                {
                    Debug.LogError("SMOKE_ERROR: BackButton not found in Lobby scene!");
                    Application.Quit(3);
                    yield break;
                }
            }

            // Wait for Menu scene to return
            timer = 0f;
            while (AppRoot.Instance?.Router?.CurrentScene != SceneId.Menu && timer < 10f)
            {
                timer += Time.unscaledDeltaTime;
                yield return null;
            }

            if (AppRoot.Instance?.Router?.CurrentScene != SceneId.Menu)
            {
                Debug.LogError("SMOKE_ERROR: Failed to return to Menu within 10s!");
                Application.Quit(4);
                yield break;
            }

            for (int f = 0; f < 10; f++) yield return null;
            yield return new WaitForEndOfFrame();

            // 5. Capture 03-menu-returned.png
            string shot3 = Path.Combine(screenshotDir, "03-menu-returned.png");
            CaptureFrame(shot3);
            yield return new WaitForSeconds(0.2f);
            Debug.Log($"SMOKE_CAPTURED: 03-menu-returned.png (exists={File.Exists(shot3)})");

            // 6. Show Quit confirmation modal
            menuPresenter = FindFirstObjectByType<MenuPresenter>();
            if (menuPresenter != null)
            {
                Debug.Log("SMOKE: Clicking QuitButton to open confirmation modal...");
                menuPresenter.OnQuitClicked();
            }
            else
            {
                Debug.LogError("SMOKE_ERROR: MenuPresenter not found after returning to Menu!");
                Application.Quit(5);
                yield break;
            }

            for (int f = 0; f < 10; f++) yield return null;
            yield return new WaitForEndOfFrame();

            // 7. Capture 04-quit-modal.png
            string shot4 = Path.Combine(screenshotDir, "04-quit-modal.png");
            CaptureFrame(shot4);
            yield return new WaitForSeconds(0.2f);
            Debug.Log($"SMOKE_CAPTURED: 04-quit-modal.png (exists={File.Exists(shot4)})");

            Debug.Log("SMOKE_SUCCESS: All scenes and modals visited and captured successfully.");
            yield return new WaitForSeconds(0.2f);
            Application.Quit(0);
        }
    }
}
