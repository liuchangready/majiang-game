#nullable enable
using System;
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

        public void BackToMenu()
        {
            _ = ReturnToMenuAsync();
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
                // If session is active, shutdown gracefully
                if (_composition.SessionCoordinator != null)
                {
                    ShutdownResult result = await _composition.ShutdownAsync("RETURN_TO_MENU");
                    if (!result.MenuAllowed)
                    {
                        Debug.LogError($"NAV_BLOCKED: Shutdown outcome {result.Outcome} disallowed menu");
                        return false;
                    }
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
            _composition.ShutdownAsync("APP_QUIT").ContinueWith(_ =>
            {
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
                            modal.ShowConfirmation("提示", "确定要退出游戏吗？", () => RequestQuit(), () => { });
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
                default:
                    _backRouter.SetSceneBackHandler(null, isBoot: false);
                    _backRouter.SetMenuQuitHandler(null);
                    break;
            }
        }
    }
}
