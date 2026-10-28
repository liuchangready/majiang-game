#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Unity.UI;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Mahjong.Unity.App
{
    public sealed class AppRouter : IAppNavigation
    {
        private readonly AppComposition _composition;
        private readonly BackRouter _backRouter;
        private SceneId _currentScene = SceneId.Boot;
        private bool _isTransitioning;

        public event Action<SceneId>? SceneLoaded;

        public SceneId CurrentScene => _currentScene;
        public bool IsTransitioning => _isTransitioning;

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
            if (_isTransitioning)
            {
                Debug.LogWarning("NAV_BUSY: StartLocalGameAsync rejected, transition already in progress");
                return LocalGameStartResult.Busy();
            }

            SceneId sceneAtStart = _currentScene;
            try
            {
                await _composition.OpenLocalSessionAsync(CancellationToken.None);
            }
            catch (LocalTableUnavailableException)
            {
                return LocalGameStartResult.Failed(LocalGameStartResult.LocalHostUnavailableCode);
            }
            catch (OperationCanceledException)
            {
                // Shutdown/back/cancel aborted the open; never surfaced as an error.
                return LocalGameStartResult.Cancelled();
            }
            catch (Exception)
            {
                // Quarantined composition, lease conflicts, corrupt saves and other host
                // failures surface as one stable generic code here. They are never mapped
                // to LOCAL_HOST_UNAVAILABLE and never clear saves or silently open a new
                // room. The P4.2-B activation patch refines this mapping with the accepted
                // LocalTableErrorKind values once the real host is integrable.
                return LocalGameStartResult.Failed(LocalGameStartResult.LocalHostStartFailedCode);
            }

            if (_currentScene != sceneAtStart)
            {
                // The user left the lobby while the open was in flight. Do not yank the UI
                // to Table late; release the already-opened session so no host, actor or
                // file lock is orphaned.
                await _composition.ShutdownAsync("OPEN_ABANDONED");
                return LocalGameStartResult.Cancelled();
            }

            if (!await LoadAsync(SceneId.Table))
            {
                await _composition.ShutdownAsync("TABLE_SCENE_LOAD_FAILED");
                return LocalGameStartResult.Failed(LocalGameStartResult.TableSceneUnavailableCode);
            }

            return LocalGameStartResult.Started();
        }

        public async Task<bool> LoadAsync(SceneId sceneId)
        {
            if (_isTransitioning)
            {
                Debug.LogWarning($"NAV_BUSY: Transition already in progress, rejecting load to {sceneId}");
                return false;
            }

            _isTransitioning = true;
            UiInputLifecycle.Instance.OnTransition();

            try
            {
                return await LoadAsyncInternal(sceneId);
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
