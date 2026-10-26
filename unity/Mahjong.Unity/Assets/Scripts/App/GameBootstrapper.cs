#nullable enable
using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.EventSystems;
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

        private void DispatchPointerClick(GameObject target)
        {
            var data = new PointerEventData(EventSystem.current)
            {
                pointerId = -1,
                button = PointerEventData.InputButton.Left,
                eligibleForClick = true
            };
            ExecuteEvents.Execute(target, data, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(target, data, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.Execute(target, data, ExecuteEvents.pointerClickHandler);
        }

        private IEnumerator RunSmokeSequence(string screenshotDir)
        {
            Directory.CreateDirectory(screenshotDir);
            Debug.Log($"SMOKE_START: Output directory={screenshotDir}");

            // 1. Wait for Menu scene to settle
            for (int f = 0; f < 10; f++) yield return null;
            yield return new WaitForEndOfFrame();

            string shot1 = Path.Combine(screenshotDir, "01-menu.png");
            CaptureFrame(shot1);
            yield return new WaitForSeconds(0.2f);
            Debug.Log($"SMOKE_CAPTURED: 01-menu.png (exists={File.Exists(shot1)})");

            // 2. Navigate to Lobby
            Debug.Log("SMOKE_STEP:NAVIGATE_TO_LOBBY");
            var playBtn = GameObject.Find("PlayButton");
            if (playBtn != null)
            {
                DispatchPointerClick(playBtn);
            }
            else
            {
                Debug.LogError("SMOKE_ERROR: PlayButton not found!");
                Application.Quit(1);
                yield break;
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

            Debug.Log("SMOKE_STEP:LOBBY_LOADED");
            string shot2 = Path.Combine(screenshotDir, "02-lobby.png");
            CaptureFrame(shot2);
            yield return new WaitForSeconds(0.2f);
            Debug.Log($"SMOKE_CAPTURED: 02-lobby.png (exists={File.Exists(shot2)})");

            // 3. Navigate back to Menu
            Debug.Log("SMOKE_STEP:NAVIGATE_TO_MENU");
            var backBtn = GameObject.Find("BackButton");
            if (backBtn != null)
            {
                DispatchPointerClick(backBtn);
            }
            else
            {
                Debug.LogError("SMOKE_ERROR: BackButton not found!");
                Application.Quit(3);
                yield break;
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

            Debug.Log("SMOKE_STEP:MENU_RETURNED");
            string shot3 = Path.Combine(screenshotDir, "03-menu-returned.png");
            CaptureFrame(shot3);
            yield return new WaitForSeconds(0.2f);
            Debug.Log($"SMOKE_CAPTURED: 03-menu-returned.png (exists={File.Exists(shot3)})");

            // 4. Open Quit modal
            var quitBtn = GameObject.Find("QuitButton");
            if (quitBtn != null)
            {
                DispatchPointerClick(quitBtn);
            }
            else
            {
                Debug.LogError("SMOKE_ERROR: QuitButton not found!");
                Application.Quit(5);
                yield break;
            }

            for (int f = 0; f < 5; f++) yield return null;
            var modal = FindFirstObjectByType<ModalPresenter>();
            if (modal == null || !modal.IsOpen)
            {
                Debug.LogError("SMOKE_ERROR: ModalPresenter failed to open!");
                Application.Quit(6);
                yield break;
            }
            Debug.Log("SMOKE_STEP:MODAL_OPEN");

            // 5. Cancel Quit modal
            var cancelBtn = GameObject.Find("CancelButton");
            if (cancelBtn != null)
            {
                DispatchPointerClick(cancelBtn);
            }
            else
            {
                Debug.LogError("SMOKE_ERROR: CancelButton not found in modal!");
                Application.Quit(7);
                yield break;
            }

            for (int f = 0; f < 5; f++) yield return null;
            if (modal.IsOpen)
            {
                Debug.LogError("SMOKE_ERROR: Modal remained open after CancelButton click!");
                Application.Quit(8);
                yield break;
            }
            Debug.Log("SMOKE_STEP:MODAL_CANCEL_CLICKED");

            // 6. Reopen Quit modal
            quitBtn = GameObject.Find("QuitButton");
            if (quitBtn != null)
            {
                DispatchPointerClick(quitBtn);
            }
            for (int f = 0; f < 5; f++) yield return null;
            if (!modal.IsOpen)
            {
                Debug.LogError("SMOKE_ERROR: Modal failed to reopen!");
                Application.Quit(9);
                yield break;
            }
            Debug.Log("SMOKE_STEP:MODAL_REOPENED");

            yield return new WaitForEndOfFrame();
            string shot4 = Path.Combine(screenshotDir, "04-quit-modal.png");
            CaptureFrame(shot4);
            yield return new WaitForSeconds(0.2f);
            Debug.Log($"SMOKE_CAPTURED: 04-quit-modal.png (exists={File.Exists(shot4)})");

            // 7. Confirm Quit
            Debug.Log("SMOKE_STEP:MODAL_CONFIRM_CLICKED");
            var confirmBtn = GameObject.Find("ConfirmButton");
            if (confirmBtn != null)
            {
                DispatchPointerClick(confirmBtn);
            }

            Debug.Log("SMOKE_SUCCESS");
            yield return new WaitForSeconds(0.2f);
            Application.Quit(0);
        }
    }
}
