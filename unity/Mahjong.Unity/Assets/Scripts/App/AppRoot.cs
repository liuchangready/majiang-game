#nullable enable
using System;
using System.Threading.Tasks;
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
        public AppConfig Config => _config;
        public AppRouter Router => _router!;
        public BackRouter BackRouter => _backRouter!;
        public MainThreadDispatcher Dispatcher => _dispatcher!;
        public AppLifecycle Lifecycle => _lifecycle!;

        public static AppRoot EnsureInitialized(
            AppConfig? config = null,
            UiTheme? theme = null,
            string? persistentDataPath = null)
        {
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
            _instance.Initialize(config ?? new AppConfig(), theme, persistentDataPath);
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

        public void Initialize(
            AppConfig config,
            UiTheme? theme = null,
            string? persistentDataPath = null)
        {
            _config = config ?? new AppConfig();
            EnsureEventSystem();

            _dispatcher = new MainThreadDispatcher(_config.maxDispatchPerFrame * 8);
            _lifecycle = new AppLifecycle();
            var themeProvider = theme != null ? new UiThemeProvider(theme) : UiThemeProvider.Instance;
            // AppComposition captures the absolute persistent root and installs the
            // real local host factory on this main thread.
            _composition = new AppComposition(_config, _dispatcher, themeProvider, persistentDataPath: persistentDataPath);
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
            EventSystem[] systems = FindObjectsByType<EventSystem>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            EventSystem? eventSystem = EventSystem.current;
            if (eventSystem == null || !eventSystem.isActiveAndEnabled)
            {
                foreach (EventSystem candidate in systems)
                {
                    if (candidate != null && candidate.isActiveAndEnabled)
                    {
                        eventSystem = candidate;
                        break;
                    }
                }
            }

            if (eventSystem == null)
            {
                GameObject eventSystemGo = new GameObject("EventSystem", typeof(EventSystem));
                eventSystem = eventSystemGo.GetComponent<EventSystem>();
                eventSystem.enabled = true;
                systems = FindObjectsByType<EventSystem>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            }

            DontDestroyOnLoad(eventSystem.gameObject);
            UnityBackRequestSource.EnsureInputModule(eventSystem.gameObject);

            foreach (EventSystem duplicate in systems)
            {
                if (duplicate == null || duplicate == eventSystem) continue;
                duplicate.enabled = false;
                var duplicateModule = duplicate.GetComponent<BaseInputModule>();
                if (duplicateModule != null) duplicateModule.enabled = false;
                Destroy(duplicate.gameObject);
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
                    menuPresenter.Initialize(_router, _config.localLiteMode);
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
            else if (sceneId == SceneId.Spectator)
            {
                var spectatorPresenter = FindFirstObjectByType<Mahjong.Unity.Spectator.SpectatorPresenter>();
                if (spectatorPresenter != null && _router != null)
                {
                    spectatorPresenter.Initialize(_router, _composition?.SessionView, () => _router.OpenSettlement());
                }
            }
            else if (sceneId == SceneId.Settlement)
            {
                var settlementPresenter = FindFirstObjectByType<Mahjong.Unity.Spectator.SettlementPresenter>();
                if (settlementPresenter != null && _router != null)
                {
                    settlementPresenter.Initialize(_router, _composition?.SessionView);
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
            WaitForShutdown("APP_QUIT");
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
            WaitForShutdown("APP_DESTROY");
        }

        private void WaitForShutdown(string reason)
        {
            AppComposition? composition = _composition;
            if (composition == null) return;
            try
            {
                Task<ShutdownResult> shutdown = composition.ShutdownAsync(reason);
                if (!shutdown.Wait(composition.ShutdownWaitBudgetMs) || shutdown.Result.Outcome != ShutdownOutcome.Completed)
                    Debug.LogError("LOCAL_SHUTDOWN_INCOMPLETE: " + reason);
            }
            catch (Exception ex)
            {
                Debug.LogError("LOCAL_SHUTDOWN_FAILED: " + reason + " kind=" + ex.GetType().Name);
            }
        }
    }
}
