#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Mahjong.Client;
using Mahjong.Contracts.Wire;
using Mahjong.Unity.UI;
using Mahjong.Unity.UI.Session;

namespace Mahjong.Unity.Spectator
{
    [Serializable]
    public sealed class SpectatorSeatPanel
    {
        public GameObject? root;
        public TextMeshProUGUI? seatNameText;
        public TextMeshProUGUI? scoreText;
        public TextMeshProUGUI? handCountText;
        public GameObject? riichiBadge;
        public TextMeshProUGUI? discardsText;
        public TextMeshProUGUI? meldsText;
        public TextMeshProUGUI? ankanPlaceholderText;

        public GameObject? Root => root;
        public TextMeshProUGUI? SeatNameText => seatNameText;
        public TextMeshProUGUI? ScoreText => scoreText;
        public TextMeshProUGUI? HandCountText => handCountText;
        public GameObject? RiichiBadge => riichiBadge;
        public TextMeshProUGUI? DiscardsText => discardsText;
        public TextMeshProUGUI? MeldsText => meldsText;
        public TextMeshProUGUI? AnkanPlaceholderText => ankanPlaceholderText;

        public void Clear()
        {
            if (seatNameText != null) seatNameText.text = string.Empty;
            if (scoreText != null) scoreText.text = string.Empty;
            if (handCountText != null) handCountText.text = string.Empty;
            if (riichiBadge != null) riichiBadge.SetActive(false);
            if (discardsText != null) discardsText.text = string.Empty;
            if (meldsText != null) meldsText.text = string.Empty;
            if (ankanPlaceholderText != null) ankanPlaceholderText.text = string.Empty;
        }

        public void UpdateSeat(SpectatorSeatModel seat)
        {
            if (seatNameText != null)
            {
                string dealerTag = seat.IsDealer ? " [庄]" : "";
                string currentTag = seat.IsCurrent ? " ◀" : "";
                seatNameText.text = $"{seat.SeatName}{dealerTag}{currentTag}";
            }

            if (scoreText != null)
            {
                if (seatNameText != null) seatNameText.color = Color.white;
                scoreText.text = seat.Score.HasValue ? $"{seat.Score.Value}点" : "点数: 未知";
            }

            if (handCountText != null)
            {
                // Pure hand count - zero tile identities exposed
                handCountText.text = $"手牌: {seat.HandCount}张";
            }

            if (riichiBadge != null)
            {
                riichiBadge.SetActive(seat.IsRiichi);
            }

            if (discardsText != null)
            {
                if (seat.Discards.Count == 0)
                {
                    discardsText.text = "牌河: 无";
                }
                else
                {
                    var sb = new StringBuilder("牌河: ");
                    for (int i = 0; i < seat.Discards.Count; i++)
                    {
                        DiscardView d = seat.Discards[i];
                        string r = d.RiichiDiscard ? "[立]" : "";
                        string t = d.Tsumogiri ? "'" : "";
                        sb.Append($"[{d.TileId}{r}{t}] ");
                    }
                    discardsText.text = sb.ToString();
                }
            }

            if (meldsText != null)
            {
                if (seat.Melds.Count == 0)
                {
                    meldsText.text = "副露: 门清";
                }
                else
                {
                    var sb = new StringBuilder("副露: ");
                    for (int i = 0; i < seat.Melds.Count; i++)
                    {
                        MeldView m = seat.Melds[i];
                        string typeLabel = GetMeldTypeLabel(m.Type);
                        sb.Append($"[{typeLabel}:{string.Join(",", m.TileIds)}] ");
                    }
                    meldsText.text = sb.ToString().TrimEnd();
                }
            }

            if (ankanPlaceholderText != null)
            {
                if (seat.ConcealedMeldCount > 0)
                {
                    // Proof of existence without carrying any tile identities
                    ankanPlaceholderText.text = $"暗杠占位: 暗杠x{seat.ConcealedMeldCount} (各4张无身份占位)";
                }
                else
                {
                    ankanPlaceholderText.text = string.Empty;
                }
            }
        }

        public static string GetMeldTypeLabel(MeldType type)
        {
            switch (type)
            {
                case MeldType.Chi: return "吃";
                case MeldType.Pon: return "碰";
                case MeldType.OpenKan: return "大明杠";
                case MeldType.AddedKan: return "加杠";
                case MeldType.ClosedKan: return "暗杠";
                default: return "副露";
            }
        }
    }

    public sealed class SpectatorPresenter : MonoBehaviour
    {
        [SerializeField] private TextMeshProUGUI? _sessionInfoText;
        [SerializeField] private TextMeshProUGUI? _roundStatusText;
        [SerializeField] private TextMeshProUGUI? _wallCountText;
        [SerializeField] private TextMeshProUGUI? _doraIndicatorsText;
        [SerializeField] private TextMeshProUGUI? _clocksText;
        [SerializeField] private GameObject? _pauseIndicator;
        [SerializeField] private SpectatorSeatPanel[] _seatPanels = new SpectatorSeatPanel[4];
        [SerializeField] private ConnectionBannerPresenter? _connectionBanner;
        [SerializeField] private GameObject? _incompatibleBannerRoot;
        [SerializeField] private TextMeshProUGUI? _incompatibleBannerText;
        [SerializeField] private Button? _backButton;
        [SerializeField] private Button? _viewSettlementButton;

        private IAppNavigation? _navigation;
        private ISessionView? _sessionView;
        private Action? _onViewSettlement;
        private long _lastGeneration = -1;
        private readonly FocusRouter _focusRouter = new FocusRouter();

        public TextMeshProUGUI? SessionInfoText => _sessionInfoText;
        public TextMeshProUGUI? RoundStatusText => _roundStatusText;
        public TextMeshProUGUI? WallCountText => _wallCountText;
        public TextMeshProUGUI? DoraIndicatorsText => _doraIndicatorsText;
        public TextMeshProUGUI? ClocksText => _clocksText;
        public GameObject? PauseIndicator => _pauseIndicator;
        public SpectatorSeatPanel[] SeatPanels => _seatPanels;
        public ConnectionBannerPresenter? ConnectionBanner => _connectionBanner;
        public GameObject? IncompatibleBannerRoot => _incompatibleBannerRoot;
        public TextMeshProUGUI? IncompatibleBannerText => _incompatibleBannerText;
        public Button? BackButton => _backButton;
        public Button? ViewSettlementButton => _viewSettlementButton;

        public void Configure(
            TextMeshProUGUI? sessionInfoText = null,
            TextMeshProUGUI? roundStatusText = null,
            TextMeshProUGUI? wallCountText = null,
            TextMeshProUGUI? doraIndicatorsText = null,
            TextMeshProUGUI? clocksText = null,
            GameObject? pauseIndicator = null,
            SpectatorSeatPanel[]? seatPanels = null,
            ConnectionBannerPresenter? connectionBanner = null,
            GameObject? incompatibleBannerRoot = null,
            TextMeshProUGUI? incompatibleBannerText = null,
            Button? backButton = null,
            Button? viewSettlementButton = null)
        {
            if (sessionInfoText != null) _sessionInfoText = sessionInfoText;
            if (roundStatusText != null) _roundStatusText = roundStatusText;
            if (wallCountText != null) _wallCountText = wallCountText;
            if (doraIndicatorsText != null) _doraIndicatorsText = doraIndicatorsText;
            if (clocksText != null) _clocksText = clocksText;
            if (pauseIndicator != null) _pauseIndicator = pauseIndicator;
            if (seatPanels != null) _seatPanels = seatPanels;
            if (connectionBanner != null) _connectionBanner = connectionBanner;
            if (incompatibleBannerRoot != null) _incompatibleBannerRoot = incompatibleBannerRoot;
            if (incompatibleBannerText != null) _incompatibleBannerText = incompatibleBannerText;
            if (backButton != null) _backButton = backButton;
            if (viewSettlementButton != null) _viewSettlementButton = viewSettlementButton;
        }

        public void Initialize(IAppNavigation navigation, ISessionView? sessionView = null, Action? onViewSettlement = null)
        {
            _navigation = navigation;
            _onViewSettlement = onViewSettlement;
            Rebind(sessionView);
        }

        public void Rebind(ISessionView? sessionView)
        {
            if (_sessionView != null)
            {
                _sessionView.Changed -= OnSessionChanged;
            }

            _sessionView = sessionView;
            _lastGeneration = -1;
            ClearDisplay();

            if (isActiveAndEnabled && _sessionView != null)
            {
                _sessionView.Changed += OnSessionChanged;
                UpdateSessionState(_sessionView.Current);
            }
        }

        private void OnEnable()
        {
            if (_sessionView != null)
            {
                _sessionView.Changed -= OnSessionChanged;
                _sessionView.Changed += OnSessionChanged;
                UpdateSessionState(_sessionView.Current);
            }
        }

        private void OnDisable()
        {
            if (_sessionView != null)
            {
                _sessionView.Changed -= OnSessionChanged;
            }
            ClearDisplay();
        }

        private void Start()
        {
            if (_backButton != null)
            {
                _backButton.onClick.AddListener(OnBackClicked);
            }

            if (_viewSettlementButton != null)
            {
                _viewSettlementButton.onClick.AddListener(OnViewSettlementClicked);
            }

            var selectables = new List<Selectable>();
            if (_backButton != null) selectables.Add(_backButton);
            if (_viewSettlementButton != null) selectables.Add(_viewSettlementButton);

            var canvas = GetComponentInParent<Canvas>();
            if (canvas != null)
            {
                foreach (var s in canvas.GetComponentsInChildren<Selectable>(true))
                {
                    if (!selectables.Contains(s)) selectables.Add(s);
                }
            }
            _focusRouter.RegisterSelectables(selectables);

            if (_backButton != null)
            {
                if (UnityEngine.EventSystems.EventSystem.current != null)
                {
                    UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(_backButton.gameObject);
                }
                else
                {
                    _backButton.Select();
                }
            }
        }

        private void OnDestroy()
        {
            if (_backButton != null) _backButton.onClick.RemoveListener(OnBackClicked);
            if (_viewSettlementButton != null) _viewSettlementButton.onClick.RemoveListener(OnViewSettlementClicked);
            if (_sessionView != null) _sessionView.Changed -= OnSessionChanged;
        }

        private void OnSessionChanged(object? sender, SessionViewState state)
        {
            if (!ReferenceEquals(sender, _sessionView))
            {
                return;
            }
            if (!isActiveAndEnabled)
            {
                return;
            }
            UpdateSessionState(state);
        }

        public void UpdateSessionState(SessionViewState? state)
        {
            if (state == null)
            {
                ClearDisplay();
                return;
            }

            if (state.Generation < _lastGeneration && _lastGeneration != -1)
            {
                return;
            }
            _lastGeneration = state.Generation;

            if (_connectionBanner != null)
            {
                _connectionBanner.UpdateState(state);
            }

            bool isIncompatible = state.State == TableConnectionState.Faulted &&
                !string.IsNullOrEmpty(state.LastError) &&
                (state.LastError.Contains("version", StringComparison.OrdinalIgnoreCase) ||
                 state.LastError.Contains("protocol", StringComparison.OrdinalIgnoreCase) ||
                 state.LastError.Contains("incompat", StringComparison.OrdinalIgnoreCase));

            if (isIncompatible)
            {
                if (_incompatibleBannerRoot != null) _incompatibleBannerRoot.SetActive(true);
                if (_incompatibleBannerText != null) _incompatibleBannerText.text = $"[严重警告] 服务端协议版本不兼容 ({state.LastError})";
            }
            else
            {
                if (_incompatibleBannerRoot != null) _incompatibleBannerRoot.SetActive(false);
            }

            if (state.PublicSnapshot == null)
            {
                ClearBoardDisplay();
                return;
            }

            SpectatorBoardModel? model = SpectatorWhitelistMapper.Map(state.PublicSnapshot, state.Binding, state.RoomSnapshot);
            if (model == null)
            {
                ClearBoardDisplay();
                return;
            }

            UpdateDisplay(model, state.Binding?.RoomId, state.Generation);
        }

        private void UpdateDisplay(SpectatorBoardModel model, string? roomId, long generation)
        {
            if (_sessionInfoText != null)
            {
                _sessionInfoText.text = $"观战公共流 | 房间: {roomId ?? "未知"} | 会话: {model.SessionId} | 基线代次: {generation}";
            }

            if (_roundStatusText != null)
            {
                _roundStatusText.text = $"{model.RoundText} | 供托: {model.RiichiSticks * 1000}点 | 状态: {model.PhaseText}";
            }

            if (_wallCountText != null)
            {
                _wallCountText.text = $"牌山余量: {model.WallRemaining}张";
            }

            if (_doraIndicatorsText != null)
            {
                string doraStr = model.DoraIndicators.Count > 0
                    ? string.Join(", ", model.DoraIndicators)
                    : "无";
                _doraIndicatorsText.text = $"宝牌指示牌: [{doraStr}]";
            }

            if (_clocksText != null)
            {
                if (model.Clocks.Count > 0)
                {
                    ClockView clk = model.Clocks[0];
                    long remainSec = Math.Max(0, clk.DeadlineRemainingMs / 1000);
                    long reserveSec = Math.Max(0, clk.ReserveRemainingMs / 1000);
                    _clocksText.text = $"行动倒计时: {remainSec}s (储备: {reserveSec}s)";
                }
                else
                {
                    _clocksText.text = string.Empty;
                }
            }

            if (_pauseIndicator != null)
            {
                _pauseIndicator.SetActive(model.IsPaused);
            }

            // Update 4 seats
            for (int i = 0; i < 4; i++)
            {
                if (_seatPanels != null && i < _seatPanels.Length && _seatPanels[i] != null)
                {
                    if (i < model.Seats.Count)
                    {
                        _seatPanels[i].UpdateSeat(model.Seats[i]);
                    }
                    else
                    {
                        _seatPanels[i].Clear();
                    }
                }
            }
        }

        public void ClearDisplay()
        {
            ClearBoardDisplay();
            if (_incompatibleBannerRoot != null) _incompatibleBannerRoot.SetActive(false);
            if (_incompatibleBannerText != null) _incompatibleBannerText.text = string.Empty;
        }

        public void ClearBoardDisplay()
        {
            if (_sessionInfoText != null) _sessionInfoText.text = "观战公共流 (等待就绪...)";
            if (_roundStatusText != null) _roundStatusText.text = string.Empty;
            if (_wallCountText != null) _wallCountText.text = string.Empty;
            if (_doraIndicatorsText != null) _doraIndicatorsText.text = string.Empty;
            if (_clocksText != null) _clocksText.text = string.Empty;
            if (_pauseIndicator != null) _pauseIndicator.SetActive(false);

            if (_seatPanels != null)
            {
                for (int i = 0; i < _seatPanels.Length; i++)
                {
                    _seatPanels[i]?.Clear();
                }
            }
        }

        public void OnBackClicked()
        {
            if (!UiInputLifecycle.Instance.ConsumeClickOrSubmit(_backButton != null ? _backButton.gameObject : null))
            {
                return;
            }
            Debug.Log("SPECTATOR_BACK_CLICKED: Returning to menu...");
            _navigation?.BackToMenu();
        }

        public void OnViewSettlementClicked()
        {
            if (!UiInputLifecycle.Instance.ConsumeClickOrSubmit(_viewSettlementButton != null ? _viewSettlementButton.gameObject : null))
            {
                return;
            }
            Debug.Log("SPECTATOR_VIEW_SETTLEMENT_CLICKED: Opening settlement...");
            _onViewSettlement?.Invoke();
        }
    }
}
