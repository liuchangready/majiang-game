#nullable enable
using System;
using UnityEngine;
using UnityEngine.EventSystems;
using Mahjong.Unity.Platform;
using Mahjong.Unity.UI;

namespace Mahjong.Unity.App
{
    public sealed class AppRoot : MonoBehaviour
    {
        private static AppRoot? _instance;
        public static AppRoot? Instance => _instance;

        private static int _mainThreadId;
        public static bool IsMainThread => Environment.CurrentManagedThreadId == _mainThreadId;

        private AppConfig _config = new AppConfig();
        private MainThreadDispatcher? _dispatcher;
        private AppLifecycle? _lifecycle;
        private BackRouter? _backRouter;
        private IBackRequestSource? _backSource;
        private AppComposition? _composition;
        private AppRouter? _router;
        private EventSystem? _persistentEventSystem;

        public MainThreadDispatcher? Dispatcher => _dispatcher;
        public AppLifecycle? Lifecycle => _lifecycle;
        public BackRouter? BackRouter => _backRouter;
        public AppComposition? Composition => _composition;
        public AppRouter? Router => _router;
        public AppConfig Config => _config;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            _instance = null;
            UiInputLifecycle.ResetForTest();
        }

        public static AppRoot EnsureInitialized(AppConfig config, UiTheme? theme = null)
        {
            if (_instance != null)
            {
                return _instance;
            }

            var existing = FindObjectsByType<AppRoot>(FindObjectsSortMode.None);
            if (existing.Length > 0)
            {
                _instance = existing[0];
                for (int i = 1; i < existing.Length; i++)
                {
                    Debug.LogWarning("APP_DUPLICATE_BOOTSTRAP: Destroying extra AppRoot instance.");
                    Destroy(existing[i].gameObject);
                }
                return _instance;
            }

            var go = new GameObject("AppRoot");
            DontDestroyOnLoad(go);
            var root = go.AddComponent<AppRoot>();
            root._config = config ?? new AppConfig();

            if (theme != null)
            {
                UiThemeProvider.Instance.SetTheme(theme);
            }

            root.Initialize();
            _instance = root;
            return root;
        }

        private void Initialize()
        {
            _mainThreadId = Environment.CurrentManagedThreadId;
            Application.targetFrameRate = _config.targetFrameRate;

            _dispatcher = new MainThreadDispatcher(_config.maxDispatchPerFrame * 8);
            _lifecycle = new AppLifecycle();
            _backRouter = new BackRouter();
            _backSource = new UnityBackRequestSource();

            _composition = new AppComposition(_config, _dispatcher, UiThemeProvider.Instance);
            _router = new AppRouter(_composition, _backRouter);
            _router.SceneLoaded += OnSceneLoaded;

            EnsurePersistentEventSystem();
        }

        private void EnsurePersistentEventSystem()
        {
            var eventSystems = FindObjectsByType<EventSystem>(FindObjectsSortMode.None);
            if (eventSystems.Length > 0)
            {
                _persistentEventSystem = eventSystems[0];
                DontDestroyOnLoad(_persistentEventSystem.gameObject);

                for (int i = 1; i < eventSystems.Length; i++)
                {
                    Debug.LogWarning("UI_DUPLICATE_EVENTSYSTEM: Destroying extra EventSystem.");
                    Destroy(eventSystems[i].gameObject);
                }
            }
            else
            {
                var esGo = new GameObject("EventSystem");
                DontDestroyOnLoad(esGo);
                _persistentEventSystem = esGo.AddComponent<EventSystem>();
                UnityBackRequestSource.EnsureInputModule(esGo);
            }
        }

        private void OnSceneLoaded(SceneId sceneId)
        {
            // Inject dependencies into scene presenters
            if (sceneId == SceneId.Menu)
            {
                var menuPresenter = FindFirstObjectByType<MenuPresenter>();
                if (menuPresenter != null && _router != null)
                {
                    menuPresenter.Initialize(_router);
                }
            }
            else if (sceneId == SceneId.Lobby)
            {
                var lobbyPresenter = FindFirstObjectByType<LobbyPresenter>();
                if (lobbyPresenter != null && _router != null)
                {
                    lobbyPresenter.Initialize(_router, _composition?.SessionView);
                }
            }
        }

        private void Update()
        {
            _dispatcher?.Update(_config.maxDispatchPerFrame, _config.maxDispatchMsPerFrame);

            // Back router polling
            if (_backRouter != null && _backSource != null && _backSource.TryConsumeBack())
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                _backRouter.HandleBack(BackEventSource.AndroidPlatformBack, isAndroidPlatform: true);
#else
                _backRouter.HandleBack(BackEventSource.DesktopEscape, isAndroidPlatform: false);
#endif
            }
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            _lifecycle?.HandleApplicationPause(pauseStatus);
            if (pauseStatus)
            {
                UiInputLifecycle.Instance.OnPause();
            }
            else
            {
                UiInputLifecycle.Instance.OnResume();
            }
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            _lifecycle?.HandleApplicationFocus(hasFocus);
            if (!hasFocus)
            {
                UiInputLifecycle.Instance.OnFocusLost();
            }
            else
            {
                UiInputLifecycle.Instance.OnFocusGained();
            }
        }

        private void OnApplicationQuit()
        {
            _lifecycle?.HandleApplicationQuit();
        }
    }
}
