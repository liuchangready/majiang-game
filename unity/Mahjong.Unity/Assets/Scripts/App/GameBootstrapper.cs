#nullable enable
using System;
using System.Collections;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Unity.Table;
using Mahjong.Unity.UI;
using Mahjong.Unity.UI.Session;

namespace Mahjong.Unity.App
{
    public sealed class GameBootstrapper : MonoBehaviour
    {
        [SerializeField] private AppConfig _config = new AppConfig();
        [SerializeField] private UiTheme? _theme;

        // Automated Player smoke runs are synthetic: the OS window may never be
        // foreground on a build agent, so the focus-loss input gate would reject
        // the programmatic click before the real Button listener sees it. This
        // flag only neutralises that environment gate; the pointer gesture,
        // Button.onClick and the production route remain exactly the real ones.
        private bool _smokeInputOverride;

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);

            // The local Player smoke runs against an isolated absolute persistent
            // root so it never reads or writes the real user's save/outbox. The
            // default launch keeps Application.persistentDataPath.
            string[] args = Environment.GetCommandLineArgs();
            string? persistentDataPath = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-smoke-data" && i + 1 < args.Length)
                {
                    persistentDataPath = args[i + 1];
                    break;
                }
            }

            // Automated smoke runs may start without OS focus on a build agent.
            // Keep the player loop alive and make each synthetic click immune to
            // a focus-loss gate the harness cannot control, so the smoke tests the
            // real menu route instead of the window manager.
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-smoke-test" || args[i] == "-smoke-localhost")
                {
                    Application.runInBackground = true;
                    _smokeInputOverride = true;
                    Debug.Log("SMOKE_INPUT_OVERRIDE: focus/pause gates cleared per synthetic smoke click; pointer and Button.onClick remain real.");
                    break;
                }
            }

            AppRoot.EnsureInitialized(_config, _theme, persistentDataPath);
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

                    if (args[i] == "-smoke-localhost" && i + 1 < args.Length)
                    {
                        string dir = args[i + 1];
                        StartCoroutine(RunLocalHostSmokeSequence(dir));
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
            if (_smokeInputOverride)
            {
                // Same-frame clear: no queued focus event can re-arm the gate
                // between this and UiPointerLifecycleTrigger.TryArm.
                UiInputLifecycle.Instance.OnResume();
                UiInputLifecycle.Instance.OnFocusGained();
            }

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
            var confirmBtn = GameObject.Find("ConfirmButton");
            if (confirmBtn == null)
            {
                Debug.LogError("SMOKE_ERROR: ConfirmButton not found in modal!");
                Application.Quit(10);
                yield break;
            }

            Debug.Log("SMOKE_STEP:MODAL_CONFIRM_CLICKED");
            DispatchPointerClick(confirmBtn);

            // Wait for shutdown and application quit to be triggered by RequestQuit()
            float quitTimer = 0f;
            while (quitTimer < 5.0f)
            {
                quitTimer += Time.unscaledDeltaTime;
                yield return null;
            }

            // If we are still running after 5 seconds, the quit flow did not complete
            Debug.LogError("SMOKE_ERROR: Application did not quit after modal confirmation!");
            Application.Quit(12);
        }

        /// <summary>
        /// Real menu -&gt; Lobby -&gt; StartLocalGame -&gt; Table smoke. It exercises the
        /// production AppComposition default factory (the accepted pure .NET local
        /// host), waits for a real snapshot, submits one legal player action and
        /// requires an incremental confirmed visible event before returning to the
        /// menu. It never references LocalHost types or reads a save directory.
        /// </summary>
        private IEnumerator RunLocalHostSmokeSequence(string screenshotDir)
        {
            Directory.CreateDirectory(screenshotDir);
            Debug.Log($"LOCAL_SMOKE_START: Output directory={screenshotDir}");

            for (int f = 0; f < 10; f++) yield return null;
            yield return new WaitForEndOfFrame();

            string shot1 = Path.Combine(screenshotDir, "01-menu.png");
            CaptureFrame(shot1);
            Debug.Log($"SMOKE_CAPTURED: 01-menu.png (exists={File.Exists(shot1)})");

            var playBtn = GameObject.Find("PlayButton");
            if (playBtn == null)
            {
                FailLocalSmoke("PlayButton not found", 21);
                yield break;
            }

            Debug.Log("SMOKE_STEP:NAVIGATE_TO_LOBBY");
            DispatchPointerClick(playBtn);

            yield return WaitForScene(SceneId.Lobby, 15f);
            if (AppRoot.Instance?.Router?.CurrentScene != SceneId.Lobby)
            {
                FailLocalSmoke("Lobby did not load", 22);
                yield break;
            }

            for (int f = 0; f < 10; f++) yield return null;
            yield return new WaitForEndOfFrame();
            Debug.Log("SMOKE_STEP:LOBBY_LOADED");
            string shot2 = Path.Combine(screenshotDir, "02-lobby.png");
            CaptureFrame(shot2);

            var startGo = GameObject.Find("StartLocalGameButton");
            var startButton = startGo != null ? startGo.GetComponent<Button>() : null;
            if (startButton == null)
            {
                FailLocalSmoke("StartLocalGameButton not found", 23);
                yield break;
            }

            if (!startButton.interactable)
            {
                FailLocalSmoke("StartLocalGameButton is disabled: production capability is not available", 24);
                yield break;
            }

            // DispatchPointerClick clears the environment focus/pause gate on the
            // smoke path in the same frame as the real pointer gesture and
            // Button.onClick; the production focus pause behaviour is unchanged.
            Debug.Log("SMOKE_STEP:LOCAL_START_CLICKED");
            DispatchPointerClick(startButton.gameObject);

            yield return WaitForScene(SceneId.Table, 30f);
            if (AppRoot.Instance?.Router?.CurrentScene != SceneId.Table)
            {
                FailLocalSmoke(
                    "Table did not load after StartLocalGame (startInFlight=" +
                    (AppRoot.Instance?.Router?.IsStartInFlight ?? false) +
                    ", startButtonInteractable=" + startButton.interactable + ")",
                    25);
                yield break;
            }

            Debug.Log("SMOKE_STEP:LOCAL_TABLE_LOADED");

            var composition = AppRoot.Instance?.Composition;
            if (composition == null)
            {
                FailLocalSmoke("AppComposition missing", 26);
                yield break;
            }

            float readyTimer = 0f;
            SessionCoordinator? coordinator = composition.SessionCoordinator;
            while (readyTimer < 30f &&
                   (coordinator == null ||
                    coordinator.Current.State != TableConnectionState.Ready ||
                    coordinator.Current.Binding == null ||
                    coordinator.Current.PlayerSnapshot == null))
            {
                readyTimer += Time.unscaledDeltaTime;
                yield return null;
                coordinator = composition.SessionCoordinator;
            }

            if (coordinator == null ||
                coordinator.Current.State != TableConnectionState.Ready ||
                coordinator.Current.Binding == null ||
                coordinator.Current.PlayerSnapshot == null)
            {
                FailLocalSmoke("Real local session did not reach Ready with a snapshot", 27);
                yield break;
            }

            SessionViewState baseline = coordinator.Current;
            Debug.Log(
                "SMOKE_STEP:LOCAL_SNAPSHOT" +
                " room=" + baseline.Binding!.RoomId +
                " seat=" + baseline.Binding.SeatId +
                " session=" + baseline.Binding.SessionId +
                " stateVersion=" + baseline.PlayerSnapshot!.StateVersion +
                " hand=" + baseline.PlayerSnapshot.PrivateView?.ClosedTiles.Count);

            string shot3 = Path.Combine(screenshotDir, "03-local-table.png");
            CaptureFrame(shot3);
            Debug.Log($"SMOKE_CAPTURED: 03-local-table.png (exists={File.Exists(shot3)})");

            var mapper = new PlayerTableProjectionMapper(new SystemTableClientClock());
            PlayerTableViewState view = mapper.Project(coordinator.Current);
            float decisionTimer = 0f;
            while (decisionTimer < 15f && (view.Own?.Decision == null || view.Own.Decision.Candidates.Count == 0))
            {
                decisionTimer += Time.unscaledDeltaTime;
                yield return null;
                coordinator = composition.SessionCoordinator;
                if (coordinator == null) break;
                view = mapper.Project(coordinator.Current);
            }

            if (coordinator == null || view.Own?.Decision == null || view.Own.Decision.Candidates.Count == 0)
            {
                FailLocalSmoke("No legal player decision was confirmed", 28);
                yield break;
            }

            PlayerLegalActionState? action = null;
            for (int i = 0; i < view.Own.Decision.Candidates.Count; i++)
            {
                if (view.Own.Decision.Candidates[i].Action == PlayerActionKind.DiscardTile)
                {
                    action = view.Own.Decision.Candidates[i];
                    break;
                }
            }

            action ??= view.Own.Decision.Candidates[0];
            var intentFactory = new TableCommandIntentFactory(new SystemTableClientClock());
            var ownerContext = new TableCommandOwnerContext(
                new RequestId("smoke-" + Guid.NewGuid().ToString("N")),
                new RequestScopeId(view.Identity.ScopeId),
                new StateVersion(view.Identity.StateVersion),
                new OwnerEpoch(view.Identity.OwnerEpoch));

            TableCommandIntent intent;
            try
            {
                intent = intentFactory.CreateSubmitAction(view, action, ownerContext);
            }
            catch (Exception exception)
            {
                FailLocalSmoke("Legal action intent failed: " + exception.GetType().Name, 29);
                yield break;
            }

            ulong versionBefore = coordinator.Current.PlayerSnapshot?.StateVersion ?? 0UL;
            Task<CommandSubmission> submit = coordinator.SubmitAsync(intent.Command, CancellationToken.None);
            float submitTimer = 0f;
            while (submitTimer < 10f && !submit.IsCompleted)
            {
                submitTimer += Time.unscaledDeltaTime;
                yield return null;
            }

            if (!submit.IsCompleted || submit.IsFaulted || submit.IsCanceled || !submit.Result.Sent)
            {
                FailLocalSmoke("Legal player action was not sent", 30);
                yield break;
            }

            float eventTimer = 0f;
            SessionViewState confirmed = coordinator.Current;
            while (eventTimer < 20f &&
                   ((confirmed.PlayerSnapshot?.StateVersion ?? 0UL) <= versionBefore ||
                    confirmed.VisibleEventBatch.Kind != TableVisibleEventBatchKind.Incremental ||
                    confirmed.VisibleEventBatch.Events.Count == 0))
            {
                eventTimer += Time.unscaledDeltaTime;
                yield return null;
                confirmed = coordinator.Current;
            }

            if ((confirmed.PlayerSnapshot?.StateVersion ?? 0UL) <= versionBefore ||
                confirmed.VisibleEventBatch.Kind != TableVisibleEventBatchKind.Incremental ||
                confirmed.VisibleEventBatch.Events.Count == 0)
            {
                FailLocalSmoke("No incremental confirmed visible event after the legal action", 31);
                yield break;
            }

            Debug.Log(
                "SMOKE_STEP:LOCAL_ACTION_SUBMITTED" +
                " kind=" + intent.Kind +
                " stateVersion=" + confirmed.PlayerSnapshot!.StateVersion);
            Debug.Log(
                "SMOKE_STEP:LOCAL_VISIBLE_EVENT" +
                " batch=" + confirmed.VisibleEventBatch.Kind +
                " count=" + confirmed.VisibleEventBatch.Events.Count +
                " type=" + confirmed.VisibleEventBatch.Events[0].Type +
                " seq=" + confirmed.VisibleEventBatch.Events[0].EventSeq);

            string shot4 = Path.Combine(screenshotDir, "04-local-action.png");
            CaptureFrame(shot4);
            Debug.Log($"SMOKE_CAPTURED: 04-local-action.png (exists={File.Exists(shot4)})");

            Debug.Log("SMOKE_STEP:LOCAL_RETURN_TO_MENU");
            Task<bool> back = AppRoot.Instance!.Router.ReturnToMenuAsync();
            float backTimer = 0f;
            while (backTimer < 20f && !back.IsCompleted)
            {
                backTimer += Time.unscaledDeltaTime;
                yield return null;
            }

            if (!back.IsCompleted || !back.Result || AppRoot.Instance.Router.CurrentScene != SceneId.Menu)
            {
                FailLocalSmoke("ReturnToMenu after a real local table failed", 32);
                yield break;
            }

            Debug.Log("SMOKE_STEP:LOCAL_RETURNED_MENU");
            Debug.Log("SMOKE_SUCCESS: Local host player smoke completed.");

            AppRoot.Instance.Router.RequestQuit();

            float quitTimer = 0f;
            while (quitTimer < 8f)
            {
                quitTimer += Time.unscaledDeltaTime;
                yield return null;
            }

            FailLocalSmoke("Application did not quit after local smoke returned to menu", 33);
        }

        private IEnumerator WaitForScene(SceneId scene, float timeoutSeconds)
        {
            float timer = 0f;
            while (AppRoot.Instance?.Router?.CurrentScene != scene && timer < timeoutSeconds)
            {
                timer += Time.unscaledDeltaTime;
                yield return null;
            }
        }

        private static void FailLocalSmoke(string message, int exitCode)
        {
            Debug.LogError("SMOKE_ERROR: " + message);
            Application.Quit(exitCode);
        }
    }
}
