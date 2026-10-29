#nullable enable
using System;
using UnityEngine;
using Mahjong.Unity.Spectator;
using Mahjong.Unity.UI;
using Mahjong.Unity.UI.Session;

namespace Mahjong.Unity.App
{
    /// <summary>
    /// Scene-level binding helper for read-only scenes (Spectator, Settlement).
    /// Binds presenters to the active AppComposition session view and navigation router
    /// without exposing internal session or channel mutation to UI components.
    /// </summary>
    public sealed class ReadOnlySceneBinding : MonoBehaviour
    {
        [SerializeField] private SpectatorPresenter? _spectatorPresenter;
        [SerializeField] private SettlementPresenter? _settlementPresenter;

        private void Start()
        {
            if (AppRoot.Instance == null)
            {
                return;
            }

            var router = AppRoot.Instance.Router;
            var sessionView = AppRoot.Instance.Composition?.SessionView;

            if (_spectatorPresenter == null)
            {
                _spectatorPresenter = FindFirstObjectByType<SpectatorPresenter>();
            }

            if (_spectatorPresenter != null && router != null)
            {
                _spectatorPresenter.Initialize(router, sessionView, () => router.OpenSettlement());
            }

            if (_settlementPresenter == null)
            {
                _settlementPresenter = FindFirstObjectByType<SettlementPresenter>();
            }

            if (_settlementPresenter != null && router != null)
            {
                _settlementPresenter.Initialize(router, sessionView);
            }
        }

        public static void BindSpectator(
            SpectatorPresenter presenter,
            IAppNavigation navigation,
            ISessionView? sessionView = null,
            Action? onOpenSettlement = null)
        {
            if (presenter == null) throw new ArgumentNullException(nameof(presenter));
            if (navigation == null) throw new ArgumentNullException(nameof(navigation));
            presenter.Initialize(navigation, sessionView, onOpenSettlement);
        }

        public static void BindSettlement(
            SettlementPresenter presenter,
            IAppNavigation navigation,
            ISessionView? sessionView = null)
        {
            if (presenter == null) throw new ArgumentNullException(nameof(presenter));
            if (navigation == null) throw new ArgumentNullException(nameof(navigation));
            presenter.Initialize(navigation, sessionView);
        }
    }
}
