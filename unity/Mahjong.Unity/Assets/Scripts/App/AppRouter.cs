#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Unity.UI;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Mahjong.Unity.App
{
    /// <summary>
    /// Distinguishes a rejected scene transition from a scene that actually failed
    /// to load. Callers must never tear down another owner on Busy.
    /// </summary>
    public enum SceneLoadOutcome
    {
        Loaded = 0,
        Busy = 1,
        Failed = 2
    }

    public sealed class AppRouter : IAppNavigation
    {
        private readonly AppComposition _composition;
        private readonly BackRouter _backRouter;
        private SceneId _currentScene = SceneId.Boot;
        private bool _isTransitioning;
        private int _startInFlight;
        private long _navigationGeneration;

        public event Action<SceneId>? SceneLoaded;

        public SceneId CurrentScene => _currentScene;
        public bool IsTransitioning => _isTransitioning;

        /// <summary>True while one owner is inside StartLocalGameAsync.</summary>
        public bool IsStartInFlight => Volatile.Read(ref _startInFlight) != 0;

        public AppRouter(AppComposition composition, BackRouter backRouter)
        {
            _composition = composition ?? throw new ArgumentNullException(nameof(composition));
            _backRouter = backRouter ?? throw new ArgumentNullException(nameof(backRouter));

            _backRouter.SetTransitionGate(() => _isTransitioning);
            UpdateBackRouterForScene(_currentScene);
        }

        public void OpenLobby()
        {
            _ = LoadAsync(SceneId.Lobby);
        }

        public void OpenTable()
        {
            _ = LoadAsync(SceneId.Table);
        }

        public void OpenSpectator()
        {
            _ = LoadAsync(SceneId.Spectator);
        }

        public void OpenSettlement()
        {
            _ = LoadAsync(SceneId.Settlement);
        }

        public void BackToMenu()
        {
            _ = ReturnToMenuAsync();
        }

        public bool IsLocalGameStartAvailable => _composition.SupportsLocalStart;

        public async Task<LocalGameStartResult> StartLocalGameAsync()
        {
            // One owner per router. The second concurrent call is rejected before it
            // can enter AppComposition, load a scene or run any shutdown/cleanup.
            if (Interlocked.CompareExchange(ref _startInFlight, 1, 0) != 0)
            {
                Debug.LogWarning("NAV_BUSY: StartLocalGameAsync rejected, another local start is in flight");
                return LocalGameStartResult.Busy();
            }

            if (_isTransitioning)
            {
                Volatile.Write(ref _startInFlight, 0);
                Debug.LogWarning("NAV_BUSY: StartLocalGameAsync rejected, transition already in progress");
                return LocalGameStartResult.Busy();
            }

            long generationAtStart = Interlocked.Read(ref _navigationGeneration);
            SceneId sceneAtStart = _currentScene;
            try
            {
                if (!_composition.SupportsLocalStart)
                {
                    if (_composition.IsTerminal)
                    {
                        return TerminalStartResult();
                    }

                    if (_composition.IsShutdownInProgress)
                    {
                        // Do not issue a start against a closing owner; this is not a
                        // retryable host failure and must not create a session.
                        return LocalGameStartResult.Busy();
                    }

                    return LocalGameStartResult.Failed(LocalGameStartResult.LocalHostUnavailableCode);
                }

                try
                {
                    await _composition.OpenLocalSessionAsync(CancellationToken.None);
                }
                catch (LocalTableUnavailableException)
                {
                    if (_composition.IsTerminal) return TerminalStartResult();
                    return LocalGameStartResult.Failed(LocalGameStartResult.LocalHostUnavailableCode);
                }
                catch (OperationCanceledException)
                {
                    // A real cancellation is benign only when the composition is still
                    // healthy. If the same cancellation raced a terminal quarantine or a
                    // shutdown, expose the terminal result instead of hiding it.
                    if (_composition.IsTerminal) return TerminalStartResult();
                    if (_composition.IsShutdownInProgress) return LocalGameStartResult.Busy();
                    return LocalGameStartResult.Cancelled();
                }
                catch (Exception)
                {
                    // Quarantined composition, lease conflicts, corrupt saves and other host
                    // failures surface as one stable generic code here. They are never mapped
                    // to LOCAL_HOST_UNAVAILABLE and never clear saves or silently open a new
                    // room. The P4.2-B activation patch refines this mapping with the accepted
                    // LocalTableErrorKind values once the real host is integrable.
                    if (_composition.IsTerminal) return TerminalStartResult();
                    if (_composition.IsShutdownInProgress) return LocalGameStartResult.Busy();
                    return LocalGameStartResult.Failed(LocalGameStartResult.LocalHostStartFailedCode);
                }

                if (_currentScene != sceneAtStart ||
                    Interlocked.Read(ref _navigationGeneration) != generationAtStart)
                {
                    // The user left the lobby (or began another navigation, including a
                    // same-scene reload) while the open was in flight. Do not yank the UI
                    // to Table late; release the already-opened session so no host, actor
                    // or file lock is orphaned. A quarantine during that release is a
                    // terminal result, never a benign cancellation.
                    ShutdownResult abandon = await _composition.ShutdownAsync("OPEN_ABANDONED");
                    if (IsTerminalShutdown(abandon) || _composition.IsTerminal)
                    {
                        return TerminalStartResult();
                    }

                    return LocalGameStartResult.Cancelled();
                }

                SceneLoadOutcome loadOutcome = await LoadForStartOwnerAsync(SceneId.Table);
                if (loadOutcome == SceneLoadOutcome.Busy)
                {
                    // Another navigation owner holds the transition. Returning Busy is
                    // deliberate: this start must not shut down a session the other owner
                    // (for example ReturnToMenu) is already draining.
                    Debug.LogWarning("NAV_BUSY: Table scene load deferred to the active navigation owner");
                    return LocalGameStartResult.Busy();
                }

                if (loadOutcome != SceneLoadOutcome.Loaded)
                {
                    ShutdownResult cleanup = await _composition.ShutdownAsync("TABLE_SCENE_LOAD_FAILED");
                    if (IsTerminalShutdown(cleanup) || _composition.IsTerminal)
                    {
                        return TerminalStartResult();
                    }

                    return LocalGameStartResult.Failed(LocalGameStartResult.TableSceneUnavailableCode);
                }

                if (_composition.IsTerminal)
                {
                    return TerminalStartResult();
                }

                return LocalGameStartResult.Started();
            }
            finally
            {
                // Only the owner that acquired the gate may release it.
                Volatile.Write(ref _startInFlight, 0);
            }
        }

        private LocalGameStartResult TerminalStartResult() =>
            LocalGameStartResult.Terminal(LocalGameStartResult.LocalHostTerminalCode);

        private static bool IsTerminalShutdown(ShutdownResult result)
        {
            if (result == null) return true;
            if (result.Quarantined) return true;
            return result.Outcome != ShutdownOutcome.Completed &&
                   result.Outcome != ShutdownOutcome.AlreadyIncomplete;
        }

        public async Task<bool> LoadAsync(SceneId sceneId)
        {
            return await TryLoadAsync(sceneId) == SceneLoadOutcome.Loaded;
        }

        /// <summary>
        /// Public scene-load entry that preserves the difference between a rejected
        /// (Busy) transition and a genuinely failed load. Every accepted navigation
        /// intent advances the generation so an in-flight local start cannot complete
        /// late into Table, including a same-scene reload.
        /// </summary>
        public async Task<SceneLoadOutcome> TryLoadAsync(SceneId sceneId)
        {
            Interlocked.Increment(ref _navigationGeneration);

            if (_isTransitioning)
            {
                Debug.LogWarning($"NAV_BUSY: Transition already in progress, rejecting load to {sceneId}");
                return SceneLoadOutcome.Busy;
            }

            _isTransitioning = true;
            UiInputLifecycle.Instance.OnTransition();

            try
            {
                return await LoadAsyncInternal(sceneId) ? SceneLoadOutcome.Loaded : SceneLoadOutcome.Failed;
            }
            finally
            {
                _isTransitioning = false;
                UiInputLifecycle.Instance.TransitionEnd();
            }
        }

        private async Task<SceneLoadOutcome> LoadForStartOwnerAsync(SceneId sceneId)
        {
            if (_isTransitioning)
            {
                return SceneLoadOutcome.Busy;
            }

            _isTransitioning = true;
            UiInputLifecycle.Instance.OnTransition();

            try
            {
                return await LoadAsyncInternal(sceneId) ? SceneLoadOutcome.Loaded : SceneLoadOutcome.Failed;
            }
            finally
            {
                _isTransitioning = false;
                UiInputLifecycle.Instance.TransitionEnd();
            }
        }

        private async Task<bool> LoadAsyncInternal(SceneId sceneId)
        {
            try
            {
                string sceneName = sceneId.ToString();
                AsyncOperation op = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
                if (op == null)
                {
                    Debug.LogError($"SCENE_LOAD_FAILED:{sceneId}:AsyncOperation was null");
                    return false;
                }

                var tcs = new TaskCompletionSource<bool>();
                op.completed += _ => tcs.SetResult(true);
                await tcs.Task;

                _currentScene = sceneId;
                Debug.Log($"SCENE_LOAD_COMPLETED:{sceneId}");
                if (AppRoot.Instance != null && AppRoot.Instance.Lifecycle != null && !AppRoot.Instance.Lifecycle.IsPaused && UiInputLifecycle.Instance.IsPaused)
                {
                    UiInputLifecycle.Instance.OnResume();
                }
                SceneLoaded?.Invoke(sceneId);

                // Update BackRouter scene back action
                UpdateBackRouterForScene(sceneId);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"SCENE_LOAD_FAILED:{sceneId}:{ex.Message}");
                return false;
            }
        }

        public async Task<bool> ReturnToMenuAsync()
        {
            // A menu intent invalidates any in-flight local start before shutdown
            // begins, even if the scene name later returns to Lobby.
            Interlocked.Increment(ref _navigationGeneration);

            if (_isTransitioning)
            {
                Debug.LogWarning("NAV_BUSY: Already transitioning");
                return false;
            }

            _isTransitioning = true;
            UiInputLifecycle.Instance.OnTransition();

            try
            {
                ShutdownResult result = await _composition.ShutdownAsync("RETURN_TO_MENU");
                if (!result.MenuAllowed)
                {
                    Debug.LogWarning($"NAV_BLOCKED: Shutdown outcome {result.Outcome} disallowed menu");
                    return false;
                }

                return await LoadAsyncInternal(SceneId.Menu);
            }
            finally
            {
                _isTransitioning = false;
                UiInputLifecycle.Instance.TransitionEnd();
            }
        }

        public void RequestQuit()
        {
            Debug.Log("QUIT_REQUESTED: Initiating application shutdown.");
            _composition.ShutdownAsync("APP_QUIT").ContinueWith(task =>
            {
                if (task.IsFaulted || task.IsCanceled)
                {
                    Debug.LogError($"QUIT_SHUTDOWN_FAULT: Shutdown task faulted or canceled: {task.Exception}");
#if UNITY_EDITOR
                    UnityEditor.EditorApplication.isPlaying = false;
#else
                    Application.Quit(11);
#endif
                    return;
                }

                ShutdownResult result = task.Result;
                Debug.Log($"QUIT_SHUTDOWN_RESULT: Outcome={result.Outcome}, Quarantined={result.Quarantined}, MenuAllowed={result.MenuAllowed}");

                if (result.Outcome != ShutdownOutcome.Completed || result.Quarantined)
                {
                    Debug.LogError($"SMOKE_ERROR: Application shutdown failed or quarantined: {result.Outcome} (Quarantined={result.Quarantined})");
#if UNITY_EDITOR
                    UnityEditor.EditorApplication.isPlaying = false;
#else
                    Application.Quit(13);
#endif
                    return;
                }

                Debug.Log("SMOKE_SUCCESS: Application shutdown completed cleanly.");
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
#else
                Application.Quit(0);
#endif
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        public void UpdateBackRouterForScene(SceneId sceneId)
        {
            switch (sceneId)
            {
                case SceneId.Boot:
                    _backRouter.SetSceneBackHandler(null, isBoot: true);
                    break;
                case SceneId.Menu:
                    _backRouter.SetSceneBackHandler(() =>
                    {
                        var modal = UnityEngine.Object.FindFirstObjectByType<ModalPresenter>(FindObjectsInactive.Include);
                        if (modal != null && !modal.IsOpen)
                        {
                            modal.ShowConfirmation("提示", "确定要退出游戏吗？", () =>
                            {
                                Debug.Log("QUIT_CONFIRMED: Application quit confirmed by user.");
                                RequestQuit();
                            }, () => { });
                        }
                        else if (modal == null)
                        {
                            RequestQuit();
                        }
                    }, isBoot: false);
                    _backRouter.SetMenuQuitHandler(() => RequestQuit());
                    break;
                case SceneId.Lobby:
                    _backRouter.SetSceneBackHandler(() =>
                    {
                        _ = ReturnToMenuAsync();
                    }, isBoot: false);
                    _backRouter.SetMenuQuitHandler(null);
                    break;
                case SceneId.Table:
                    _backRouter.SetSceneBackHandler(() =>
                    {
                        _ = ReturnToMenuAsync();
                    }, isBoot: false);
                    _backRouter.SetMenuQuitHandler(null);
                    break;
                case SceneId.Spectator:
                    _backRouter.SetSceneBackHandler(() =>
                    {
                        _ = ReturnToMenuAsync();
                    }, isBoot: false);
                    _backRouter.SetMenuQuitHandler(null);
                    break;
                case SceneId.Settlement:
                    _backRouter.SetSceneBackHandler(() =>
                    {
                        _ = ReturnToMenuAsync();
                    }, isBoot: false);
                    _backRouter.SetMenuQuitHandler(null);
                    break;
                default:
                    _backRouter.SetSceneBackHandler(null, isBoot: false);
                    _backRouter.SetMenuQuitHandler(null);
                    break;
            }
        }
    }
}
