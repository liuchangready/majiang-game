#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
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
        private bool _startupBlocked;

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);

            string[] args = Environment.GetCommandLineArgs();
            string? smokeDataPath = null;
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "-smoke-data") { smokeDataPath = args[i + 1]; break; }

#if G2_LOCAL_LITE
            _config.localLiteMode = true;
            try
            {
                string liteRunId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N");
                string liteBase = string.IsNullOrWhiteSpace(smokeDataPath) ? Application.persistentDataPath : Path.GetFullPath(smokeDataPath);
                string liteRoot = Path.Combine(liteBase, "G2LocalLite", "runs", liteRunId);
                Directory.CreateDirectory(Path.Combine(liteRoot, "logs"));
                string buildIdentityPath = Path.Combine(Application.streamingAssetsPath, "G2LocalLiteBuild.json");
                string buildIdentity = File.Exists(buildIdentityPath) ? File.ReadAllText(buildIdentityPath) : "{\"identity\":\"unknown\"}";
                LocalLiteRunContext.Initialize(liteRoot, Application.version, Application.buildGUID, buildIdentity);
                if (!string.IsNullOrEmpty(LocalLiteRunContext.LastFailure)) throw new IOException(LocalLiteRunContext.LastFailure);
                _config.localLiteDataRoot = liteRoot;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is System.Security.SecurityException)
            {
                _startupBlocked = true;
                Debug.LogError("LOCAL_LITE_STARTUP_STORAGE_UNAVAILABLE");
                return;
            }
#endif

            // The local Player smoke runs against an isolated absolute persistent
            // root so it never reads or writes the real user's save/outbox. The
            // default launch keeps Application.persistentDataPath.
            string? persistentDataPath = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-smoke-data" && i + 1 < args.Length)
                {
                    persistentDataPath = _config.localLiteMode ? _config.localLiteDataRoot : args[i + 1];
                    break;
                }
            }

            // Automated smoke runs may start without OS focus on a build agent.
            // Keep the player loop alive and make each synthetic click immune to
            // a focus-loss gate the harness cannot control, so the smoke tests the
            // real menu route instead of the window manager.
            for (int i = 0; i < args.Length; i++)
            {
                    if (args[i] == "-smoke-test" || args[i] == "-smoke-localhost" || args[i] == "-g2-local-lite-lifecycle")
                {
                    Application.runInBackground = true;
                    _smokeInputOverride = true;
                    Debug.Log("SMOKE_INPUT_OVERRIDE: focus/pause gates cleared per synthetic smoke click; pointer and Button.onClick remain real.");
                    break;
                }
            }

            AppRoot.EnsureInitialized(_config, _theme,
                string.IsNullOrWhiteSpace(persistentDataPath) ? _config.localLiteDataRoot : persistentDataPath);
        }

        private async void Start()
        {
            if (_startupBlocked) return;
            Debug.Log("BOOTSTRAPPER_START: Initializing Menu load...");
#if G2_LOCAL_LITE
            LocalLiteRunContext.RecordStage(null, "boot");
#endif
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

                    if (args[i] == "-g2-local-lite-lifecycle")
                    {
                        StartCoroutine(RunLocalLiteLifecycleSequence());
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

            var intentFactory = new TableCommandIntentFactory(new SystemTableClientClock());
            var observer = new ConfirmedVisibleEventObserver(coordinator);
            coordinator.ReceiptResolved += observer.OnReceiptResolved;
            ConfirmedVisibleActionEvent? confirmedEvent = null;
            TableCommandIntent? lastIntent = null;
            string lastAction = "none";
            try
            {
                // A response-window decision may legitimately contain no
                // DiscardTile candidate. Its real action can be committed with
                // an empty incremental batch (for example a domain event that
                // has no protocol projection yet); never invent an animation
                // for it. Advance through real Client actions until a confirmed
                // visible event arrives, or fail with the observed batch chain.
                const int maxActions = 3;
                for (int attempt = 0; attempt < maxActions && confirmedEvent == null; attempt++)
                {
                    view = mapper.Project(coordinator.Current);
                    float nextDecisionTimer = 0f;
                    while (nextDecisionTimer < 15f && (view.Own?.Decision == null || view.Own.Decision.Candidates.Count == 0))
                    {
                        nextDecisionTimer += Time.unscaledDeltaTime;
                        yield return null;
                        coordinator = composition.SessionCoordinator;
                        if (coordinator == null) break;
                        view = mapper.Project(coordinator.Current);
                    }

                    if (coordinator == null || view.Own?.Decision == null || view.Own.Decision.Candidates.Count == 0)
                    {
                        Debug.LogError(
                            "SMOKE_OBSERVER: no further legal decision action=" + lastAction +
                            " " + observer.ReceiptSummary + " " + observer.DescribeBatches());
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
                    lastAction = action.Action + "/" + action.RawActionType;
                    Debug.Log(
                        "SMOKE_STEP:LOCAL_ACTION_SELECTED" +
                        " action=" + action.Action +
                        " rawActionType=" + action.RawActionType +
                        " option=" + (string.IsNullOrEmpty(action.OptionId) ? "<none>" : action.OptionId) +
                        " tiles=" + action.TileIds.Count +
                        " attempt=" + (attempt + 1) +
                        " decisionCandidates=" + view.Own.Decision.Candidates.Count);

                    string requestId = "smoke-" + Guid.NewGuid().ToString("N");
                    var ownerContext = new TableCommandOwnerContext(
                        new RequestId(requestId),
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

                    lastIntent = intent;
                    ulong versionBefore = coordinator.Current.PlayerSnapshot?.StateVersion ?? 0UL;
                    observer.BeginAction(coordinator.Current, requestId, view.Identity.ScopeId, action.Action);
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
                    bool receiptLogged = false;
                    while (eventTimer < 20f)
                    {
                        confirmedEvent = observer.TryGetConfirmation();
                        if (confirmedEvent != null)
                        {
                            break;
                        }

                        if (observer.HasAppliedReceipt)
                        {
                            ulong? appliedCommand = observer.AppliedCommandVersion;
                            if (!receiptLogged)
                            {
                                receiptLogged = true;
                                Debug.Log(
                                    "SMOKE_STEP:LOCAL_ACTION_APPLIED_WAITING_EVENT" +
                                    " action=" + lastAction +
                                    " " + observer.ReceiptSummary +
                                    " stateVersionBefore=" + versionBefore);
                            }

                            if (appliedCommand.HasValue &&
                                observer.FindActionPublication(appliedCommand.Value) != null)
                            {
                                // The action's own publication has been accepted;
                                // if it carried no matching visible event, this is
                                // a confirmed state-only action, not a timeout.
                                break;
                            }
                        }

                        eventTimer += Time.unscaledDeltaTime;
                        yield return null;
                    }

                    confirmedEvent ??= observer.TryGetConfirmation();
                    if (confirmedEvent != null)
                    {
                        break;
                    }

                    if (observer.HasAppliedReceipt)
                    {
                        ulong? appliedCommand = observer.AppliedCommandVersion;
                        ObservedVisibleEventBatch? publication = appliedCommand.HasValue
                            ? observer.FindActionPublication(appliedCommand.Value)
                            : null;
                        Debug.LogWarning(
                            "SMOKE_STEP:LOCAL_ACTION_STATE_ONLY" +
                            " action=" + lastAction +
                            " publicationStateVersion=" + (publication == null ? "none" : publication.StateVersion.ToString()) +
                            " publicationEvents=" + (publication == null ? -1 : publication.Events.Count) +
                            " " + observer.ReceiptSummary +
                            " baseline=" + versionBefore +
                            " " + observer.DescribeBatches());
                        if (publication == null)
                        {
                            Debug.LogError(
                                "SMOKE_OBSERVER: the Applied action publication was never accepted" +
                                " action=" + lastAction +
                                " " + observer.ReceiptSummary +
                                " " + observer.DescribeBatches());
                            FailLocalSmoke("Applied action publication was not observed", 31);
                            yield break;
                        }

                        if (!SmokeActionEventPolicy.AllowsEmptyPublication(action.Action))
                        {
                            if (publication.Events.Count > 0)
                            {
                                Debug.LogError(
                                    "SMOKE_OBSERVER: the action publication carried events, but none matched the required type/acting seat" +
                                    " action=" + lastAction +
                                    " requirement=" + SmokeActionEventPolicy.DescribeRequirement(action.Action) +
                                    " publicationStateVersion=" + publication.StateVersion +
                                    " publicationEventSeq=" + publication.EventSeq +
                                    " " + observer.ReceiptSummary +
                                    " " + observer.DescribeBatches());
                                FailLocalSmoke("Action publication events did not match the action's required type/seat", 31);
                                yield break;
                            }

                            Debug.LogError(
                                "SMOKE_OBSERVER: action requires a visible event, but its Applied publication was empty" +
                                " action=" + lastAction +
                                " requirement=" + SmokeActionEventPolicy.DescribeRequirement(action.Action) +
                                " publicationStateVersion=" + publication.StateVersion +
                                " publicationEventSeq=" + publication.EventSeq +
                                " " + observer.ReceiptSummary +
                                " " + observer.DescribeBatches());
                            FailLocalSmoke("Action requires a visible event but its publication was empty", 31);
                            yield break;
                        }

                        // Only the explicit state-only action above may continue.
                        // A Pass publication may carry a window/settlement event
                        // for another seat; it is never this request's confirmation.
                        // The next real action must still confirm itself.
                        continue;
                    }

                    Debug.LogError(
                        "SMOKE_OBSERVER: " + observer.DescribeAction() +
                        " action=" + lastAction +
                        " currentStateVersion=" + (coordinator.Current.PlayerSnapshot?.StateVersion ?? 0UL) +
                        " currentBatch=" + coordinator.Current.VisibleEventBatch.Kind +
                        " currentBatchCount=" + coordinator.Current.VisibleEventBatch.Events.Count +
                        " " + observer.ReceiptSummary +
                        " " + observer.DescribeBatches());
                    FailLocalSmoke("No incremental confirmed visible event after the legal action", 31);
                    yield break;
                }

                if (confirmedEvent == null || lastIntent == null)
                {
                    Debug.LogError(
                        "SMOKE_OBSERVER: no confirmed action event after bounded actions" +
                        " action=" + lastAction +
                        " " + observer.ReceiptSummary +
                        " " + observer.DescribeBatches());
                    FailLocalSmoke("No incremental confirmed visible event after the legal action", 31);
                    yield break;
                }

                Debug.Log(
                    "SMOKE_STEP:LOCAL_ACTION_SUBMITTED" +
                    " kind=" + lastIntent.Kind +
                    " action=" + lastAction +
                    " stateVersion=" + confirmedEvent.Batch.StateVersion +
                    " commandVersion=" + confirmedEvent.CommandVersion);
                Debug.Log(
                    "SMOKE_STEP:LOCAL_VISIBLE_EVENT" +
                    " batch=" + confirmedEvent.Batch.Kind +
                    " count=" + confirmedEvent.Batch.Events.Count +
                    " type=" + confirmedEvent.Event.Type +
                    " seq=" + confirmedEvent.Event.EventSeq +
                    " commandVersion=" + confirmedEvent.Event.CommandVersion +
                    " stateVersion=" + confirmedEvent.Batch.StateVersion);
                Debug.Log("SMOKE_OBSERVER_ACCEPTED: " + observer.DescribeAction() + " " + observer.DescribeBatches());
            }
            finally
            {
                coordinator.ReceiptResolved -= observer.OnReceiptResolved;
                observer.Dispose();
            }

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

        [Serializable]
        private sealed class LiteLifecycleEvidence
        {
            public string status = "NOT_RUN";
            public string failure = string.Empty;
            public string[] cases = Array.Empty<string>();
            public string[] rooms = Array.Empty<string>();
            public string[] rounds = Array.Empty<string>();
            public string[] scopes = Array.Empty<string>();
            public long[] generations = Array.Empty<long>();
        public string acceptedActionRequestId = string.Empty;
        public string acceptedActionEvent = string.Empty;
        public string settlementRoundId = string.Empty;
            public bool roomClosed;
        }

        private IEnumerator RunLocalLiteLifecycleSequence()
        {
            var evidence = new LiteLifecycleEvidence();
            var cases = new List<string>();
            var rooms = new List<string>();
            var rounds = new List<string>();
            var scopes = new List<string>();
            var generations = new List<long>();
            string[] sequence = { "random", "pon", "random", "chi", "riichi", "random", "closed-kan", "dealer-tsumo", "random", "pon" };
            AppRoot? app = AppRoot.Instance;
            LocalTableChannelFactory? factory = app?.Composition.ChannelFactory as LocalTableChannelFactory;
            string failure = app == null || factory == null || !app.Composition.Config.localLiteMode
                ? "LITE_PLAYER_CONTEXT_UNAVAILABLE"
                : string.Empty;
            for (int i = 0; i < sequence.Length && failure.Length == 0; i++)
            {
                string scenario = sequence[i];
                Task<LocalGameStartResult> start = app!.Router.StartLocalGameAsync(scenario == "random" ? null : scenario);
                float deadline = Time.realtimeSinceStartup + 60f;
                while (!start.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
                if (!start.IsCompleted || start.IsFaulted || start.Result.Status != LocalGameStartStatus.Started)
                {
                    failure = "LITE_START_FAILED_" + scenario;
                    break;
                }
                SessionCoordinator? coordinator = app.Composition.SessionCoordinator;
                while ((coordinator == null || coordinator.Current.State != TableConnectionState.Ready ||
                        coordinator.Current.PlayerSnapshot == null || coordinator.Current.Binding == null) &&
                       Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                    coordinator = app.Composition.SessionCoordinator;
                }
                if (coordinator?.Current.PlayerSnapshot == null || coordinator.Current.Binding == null)
                {
                    failure = "LITE_SESSION_NOT_READY_" + scenario;
                    break;
                }
                string room = factory!.RoomId;
                string round = coordinator.Current.PlayerSnapshot.RoundId;
                string scope = coordinator.Current.Binding.ScopeId;
                if (string.IsNullOrWhiteSpace(round) || string.IsNullOrWhiteSpace(scope) || !factory.IsRoomOpen)
                {
                    failure = "LITE_SCOPE_OR_ROOM_MISSING_" + scenario;
                    break;
                }
                cases.Add(scenario);
                rooms.Add(room);
                rounds.Add(round);
                scopes.Add(scope);
                generations.Add(coordinator.Generation);

                // Bind one real client command to the fixed dealer-tsumo fixture
                // in this same-package lifecycle run. This is automated Client
                // input, not physical mouse evidence.
                if (scenario == "dealer-tsumo")
                {
                    var mapper = new PlayerTableProjectionMapper(new SystemTableClientClock());
                    PlayerTableViewState view = mapper.Project(coordinator.Current);
                    PlayerLegalActionState? tsumo = null;
                    if (view.Own?.Decision != null)
                    {
                        for (int candidateIndex = 0; candidateIndex < view.Own.Decision.Candidates.Count; candidateIndex++)
                        {
                            if (view.Own.Decision.Candidates[candidateIndex].Action == PlayerActionKind.DeclareTsumo)
                            {
                                tsumo = view.Own.Decision.Candidates[candidateIndex];
                                break;
                            }
                        }
                    }
                    if (tsumo == null)
                    {
                        failure = "LITE_DEALER_TSUMO_CANDIDATE_MISSING";
                        break;
                    }

                    string requestId = "lite-lifecycle-tsumo-" + Guid.NewGuid().ToString("N");
                    var actionObserver = new ConfirmedVisibleEventObserver(coordinator);
                    coordinator.ReceiptResolved += actionObserver.OnReceiptResolved;
                    var ownerContext = new TableCommandOwnerContext(new RequestId(requestId),
                        new RequestScopeId(view.Identity.ScopeId), new StateVersion(view.Identity.StateVersion),
                        new OwnerEpoch(view.Identity.OwnerEpoch));
                    TableCommandIntent intent = new TableCommandIntentFactory(new SystemTableClientClock())
                        .CreateSubmitAction(view, tsumo, ownerContext);
                    actionObserver.BeginAction(coordinator.Current, requestId, view.Identity.ScopeId, tsumo.Action);
                    Task<CommandSubmission> submit = coordinator.SubmitAsync(intent.Command, CancellationToken.None);
                    float actionDeadline = Time.realtimeSinceStartup + 15f;
                    while (!submit.IsCompleted && Time.realtimeSinceStartup < actionDeadline) yield return null;
                    if (!submit.IsCompleted || submit.IsFaulted || submit.IsCanceled || !submit.Result.Sent)
                    {
                        coordinator.ReceiptResolved -= actionObserver.OnReceiptResolved;
                        failure = "LITE_DEALER_TSUMO_SUBMIT_FAILED";
                        break;
                    }

                    ConfirmedVisibleActionEvent? confirmed = null;
                    while (confirmed == null && Time.realtimeSinceStartup < actionDeadline)
                    {
                        confirmed = actionObserver.TryGetConfirmation();
                        yield return null;
                    }
                    coordinator.ReceiptResolved -= actionObserver.OnReceiptResolved;
                    if (confirmed == null || confirmed.Event.Type != ProtocolEventType.TsumoWin)
                    {
                        failure = "LITE_DEALER_TSUMO_NOT_CONFIRMED";
                        break;
                    }
                    PlayerTableViewState settledView = mapper.Project(coordinator.Current);
                    if (settledView.Board.Phase != PlayerGamePhase.RoundEnd ||
                        !string.Equals(settledView.Board.SettlementRoundId, round, StringComparison.Ordinal))
                    {
                        failure = "LITE_DEALER_TSUMO_SETTLEMENT_MISSING";
                        break;
                    }
                    evidence.acceptedActionRequestId = requestId;
                    evidence.acceptedActionEvent = confirmed.Event.Type.ToString();
                    evidence.settlementRoundId = settledView.Board.SettlementRoundId;
                }

                Task<bool> close = app.Router.ReturnToMenuLiteAsync();
                deadline = Time.realtimeSinceStartup + 15f;
                while (!close.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
                if (!close.IsCompleted || close.IsFaulted || !close.Result || factory.IsRoomOpen || app.Composition.SessionCoordinator != null)
                {
                    failure = "LITE_CLOSE_FAILED_" + scenario;
                    break;
                }
            }
            if (failure.Length == 0 && (new HashSet<string>(rooms).Count != sequence.Length ||
                new HashSet<string>(rounds).Count != sequence.Length || new HashSet<string>(scopes).Count != sequence.Length ||
                new HashSet<long>(generations).Count != sequence.Length)) failure = "LITE_IDENTITY_REUSED";
            if (failure.Length == 0 && (evidence.acceptedActionEvent != ProtocolEventType.TsumoWin.ToString() ||
                string.IsNullOrWhiteSpace(evidence.acceptedActionRequestId) || string.IsNullOrWhiteSpace(evidence.settlementRoundId)))
                failure = "LITE_REAL_ACTION_SETTLEMENT_NOT_OBSERVED";
            evidence.status = failure.Length == 0 ? "PASS" : "FAIL";
            evidence.failure = failure;
            evidence.cases = cases.ToArray();
            evidence.rooms = rooms.ToArray();
            evidence.rounds = rounds.ToArray();
            evidence.scopes = scopes.ToArray();
            evidence.generations = generations.ToArray();
            evidence.roomClosed = factory == null || !factory.IsRoomOpen;
            string? root = LocalLiteRunContext.RunRoot;
            if (!string.IsNullOrWhiteSpace(root))
            {
                try { File.WriteAllText(Path.Combine(root, "lifecycle-matrix.json"), JsonUtility.ToJson(evidence, true) + "\n"); }
                catch (Exception ex) { failure = "LITE_EVIDENCE_WRITE_FAILED_" + ex.GetType().Name; evidence.status = "FAIL"; evidence.failure = failure; }
            }
            if (failure.Length == 0) Debug.Log("LOCAL_LITE_LIFECYCLE_PASS: count=" + cases.Count);
            else Debug.LogError("LOCAL_LITE_LIFECYCLE_FAILED: " + failure);
            if (app != null)
            {
                Task<ShutdownResult> shutdown = app.Composition.ShutdownAsync("APP_QUIT");
                float shutdownDeadline = Time.realtimeSinceStartup + 10f;
                while (!shutdown.IsCompleted && Time.realtimeSinceStartup < shutdownDeadline) yield return null;
                evidence.roomClosed = factory == null || !factory.IsRoomOpen;
                if (!evidence.roomClosed) { evidence.status = "FAIL"; evidence.failure = "LITE_QUIT_CLOSE_FAILED"; }
                if (!string.IsNullOrWhiteSpace(root))
                {
                    try { File.WriteAllText(Path.Combine(root, "lifecycle-matrix.json"), JsonUtility.ToJson(evidence, true) + "\n"); }
                    catch { }
                }
            }
            Application.Quit(evidence.status == "PASS" && evidence.roomClosed ? 0 : 1);
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

        private void OnGUI()
        {
            if (!_startupBlocked) return;
            const float panelWidth = 520f;
            const float panelHeight = 150f;
            var panel = new Rect((Screen.width - panelWidth) * 0.5f, (Screen.height - panelHeight) * 0.5f, panelWidth, panelHeight);
            GUI.Box(panel, "G2 Local Lite");
            GUI.Label(new Rect(panel.x + 20f, panel.y + 35f, panel.width - 40f, 50f),
                "Test data storage is unavailable. No game was started. Check the folder permissions and restart.\nLOCAL_LITE_STARTUP_STORAGE_UNAVAILABLE");
            if (GUI.Button(new Rect(panel.x + 180f, panel.y + 95f, 160f, 36f), "退出 / Exit"))
                Application.Quit();
        }
    }
}
