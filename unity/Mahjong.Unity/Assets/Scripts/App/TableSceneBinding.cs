#nullable enable
using System;
using UnityEngine;
using Mahjong.Unity.Table;
using Mahjong.Unity.UI;
using Mahjong.Unity.UI.Session;

namespace Mahjong.Unity.App
{
    /// <summary>
    /// Scene-level binding helper for the Table scene.
    /// Binds TablePresenter to the active AppComposition session view,
    /// command sink, and navigation router without exposing internal session
    /// or channel mutation to UI components.
    /// </summary>
    public sealed class TableSceneBinding : MonoBehaviour
    {
        [SerializeField] private TablePresenter? _tablePresenter;

        public TablePresenter? Presenter => _tablePresenter;

        private void Start()
        {
            if (AppRoot.Instance == null)
            {
                return;
            }

            var router = AppRoot.Instance.Router;
            var sessionCoordinator = AppRoot.Instance.Composition?.SessionCoordinator;
            var sessionView = AppRoot.Instance.Composition?.SessionView;

            if (_tablePresenter == null)
            {
                _tablePresenter = FindFirstObjectByType<TablePresenter>();
            }

            if (_tablePresenter != null && router != null)
            {
                _tablePresenter.Initialize(
                    router,
                    sessionView,
                    sessionCoordinator,
                    () => router.OpenSettlement());
            }
        }

        public static void BindTable(
            TablePresenter presenter,
            IAppNavigation navigation,
            ISessionView? sessionView = null,
            ITableCommandSink? commandSink = null,
            Action? onOpenSettlement = null)
        {
            if (presenter == null) throw new ArgumentNullException(nameof(presenter));
            if (navigation == null) throw new ArgumentNullException(nameof(navigation));
            presenter.Initialize(navigation, sessionView, commandSink, onOpenSettlement);
        }
    }
}
