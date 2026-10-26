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
        private bool _isTransitioning;
        private SceneId _currentScene = SceneId.Boot;

        public SceneId CurrentScene => _currentScene;
        public bool IsTransitioning => _isTransitioning;

        public event Action<SceneId>? SceneLoaded;

        public AppRouter(AppComposition composition, BackRouter backRouter)
        {
            _composition = composition ?? throw new ArgumentNullException(nameof(composition));
            _backRouter = backRouter ?? throw new ArgumentNullException(nameof(backRouter));
            _backRouter.SetTransitionGate(() => _isTransitioning);
        }

        public void OpenLobby()
        {
            _ = LoadAsync(SceneId.Lobby);
        }

        public void BackToMenu()
        {
            _ = ReturnToMenuAsync();
        }

        public void RequestQuit()
        {
            _ = QuitApplicationAsync();
        }

        public async Task<bool> LoadAsync(SceneId sceneId)
        {
            if (_isTransitioning)
            {
                Debug.LogWarning($"NAV_BUSY: Already transitioning, ignoring request to load {sceneId}");
                return false;
            }

            _isTransitioning = true;
            UiInputLifecycle.Instance.OnTransition();

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
            finally
            {
                _isTransitioning = false;
                UiInputLifecycle.Instance.TransitionEnd();
            }
        }

        public async Task<bool> ReturnToMenuAsync()
        {
            if (_isTransitioning)
            {
                Debug.LogWarning("NAV_BUSY: Already transitioning");
                return false;
            }

            // If session is active, shutdown gracefully
            if (_composition.SessionCoordinator != null)
            {
                ShutdownResult result = await _composition.ShutdownAsync("RETURN_TO_MENU");
                if (!result.MenuAllowed)
                {
                    Debug.LogError($"RETURN_TO_MENU_BLOCKED: Shutdown outcome was {result.Outcome}, quarantined={result.Quarantined}");
                    return false;
                }
            }

            return await LoadAsync(SceneId.Menu);
        }

        public async Task QuitApplicationAsync()
        {
            Debug.Log("QuitApplicationAsync initiated");
            ShutdownResult result = await _composition.ShutdownAsync("APP_QUIT");
            Debug.Log($"Shutdown completed with outcome {result.Outcome}. Quitting application.");

#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        private void UpdateBackRouterForScene(SceneId sceneId)
        {
            switch (sceneId)
            {
                case SceneId.Boot:
                    _backRouter.SetSceneBackHandler(null, isBoot: true);
                    break;
                case SceneId.Menu:
                    _backRouter.SetSceneBackHandler(null, isBoot: false);
                    _backRouter.SetMenuQuitHandler(() => RequestQuit());
                    break;
                case SceneId.Lobby:
                    _backRouter.SetSceneBackHandler(() => BackToMenu(), isBoot: false);
                    break;
                default:
                    _backRouter.SetSceneBackHandler(() => BackToMenu(), isBoot: false);
                    break;
            }
        }
    }
}
