#nullable enable
using System;
using Mahjong.Unity.Platform;
using Mahjong.Unity.UI;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Mahjong.Unity.App
{
    public sealed class AppRoot : MonoBehaviour
    {
        private static AppRoot? _instance;
        public static AppRoot? Instance => _instance;
        private static int _mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
        public static bool IsMainThread => System.Threading.Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        private AppConfig _config = new AppConfig();
        private AppComposition? _composition;
        private AppRouter? _router;
        private BackRouter? _backRouter;
        private IBackRequestSource? _backSource;
        private MainThreadDispatcher? _dispatcher;
        private AppLifecycle? _lifecycle;

        public AppComposition Composition => _composition!;
        public AppRouter Router => _router!;
        public BackRouter BackRouter => _backRouter!;
        public MainThreadDispatcher Dispatcher => _dispatcher!;
        public AppLifecycle Lifecycle => _lifecycle!;

        public static AppRoot EnsureInitialized(AppConfig? config = null, UiTheme? theme = null)
        {
            UiInputLifecycle.ResetForTest();
            if (_instance != null)
            {
                return _instance;
            }

            var existing = FindFirstObjectByType<AppRoot>();
            if (existing != null)
            {
                _instance = existing;
                return _instance;
            }

            var rootGo = new GameObject("AppRoot");
            DontDestroyOnLoad(rootGo);
            _instance = rootGo.AddComponent<AppRoot>();
            _instance.Initialize(config ?? new AppConfig(), theme);
            return _instance;
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);
            EnsureEventSystem();
        }

        public void Initialize(AppConfig config, UiTheme? theme = null)
        {
            _config = config ?? new AppConfig();
            EnsureEventSystem();

            _dispatcher = new MainThreadDispatcher(_config.maxDispatchPerFrame * 8);
            _lifecycle = new AppLifecycle();
            var themeProvider = theme != null ? new UiThemeProvider(theme) : UiThemeProvider.Instance;
            _composition = new AppComposition(_config, _dispatcher, themeProvider);
            _backRouter = new BackRouter();
            _backSource = new UnityBackRequestSource();

            // Bind pause gate to BackRouter
            _backRouter.SetPauseGate(() => (_lifecycle != null && _lifecycle.IsPaused) || UiInputLifecycle.Instance.IsPaused);

            _router = new AppRouter(_composition, _backRouter);
            _router.SceneLoaded += OnSceneLoaded;

            // Initialize UiThemeProvider line-breaking rules
            UiThemeProvider.EnsureLineBreakingRules();
        }

        private void EnsureEventSystem()
        {
            var eventSystem = FindFirstObjectByType<EventSystem>();
            if (eventSystem == null)
            {
                var eventSystemGo = new GameObject("EventSystem", typeof(EventSystem));
                DontDestroyOnLoad(eventSystemGo);
                UnityBackRequestSource.EnsureInputModule(eventSystemGo);
            }
            else
            {
                DontDestroyOnLoad(eventSystem.gameObject);
                UnityBackRequestSource.EnsureInputModule(eventSystem.gameObject);
            }
        }

        private void OnSceneLoaded(SceneId sceneId)
        {
            EnsureEventSystem();

            var modalPresenters = FindObjectsByType<ModalPresenter>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (_backRouter != null)
            {
                foreach (var modal in modalPresenters)
                {
                    modal.SetBackRouter(_backRouter);
                }
            }

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
            try
            {
                _composition?.ShutdownAsync("APP_QUIT").Wait(1000);
            }
            catch
            {
                // Bounded catch on quit
            }
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
            try
            {
                _composition?.ShutdownAsync("APP_DESTROY").Wait(1000);
            }
            catch
            {
                // Bounded catch on destroy
            }
        }
    }
}
