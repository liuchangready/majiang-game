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
    public sealed class SettlementPresenter : MonoBehaviour
    {
        [SerializeField] private TextMeshProUGUI? _titleText;
        [SerializeField] private TextMeshProUGUI? _roundInfoText;
        [SerializeField] private TextMeshProUGUI? _winSummaryText;
        [SerializeField] private TextMeshProUGUI? _yakuListText;
        [SerializeField] private TextMeshProUGUI? _scoreBreakdownText;
        [SerializeField] private TextMeshProUGUI? _paymentsText;
        [SerializeField] private TextMeshProUGUI? _newScoresText;
        [SerializeField] private GameObject? _matchSummaryRoot;
        [SerializeField] private TextMeshProUGUI? _matchRankingsText;
        [SerializeField] private PrivateLedgerPresenter? _privateLedger;
        [SerializeField] private ConnectionBannerPresenter? _connectionBanner;
        [SerializeField] private GameObject? _incompatibleBannerRoot;
        [SerializeField] private TextMeshProUGUI? _incompatibleBannerText;
        [SerializeField] private Button? _backButton;

        private IAppNavigation? _navigation;
        private ISessionView? _sessionView;
        private long _lastGeneration = -1;
        private readonly FocusRouter _focusRouter = new FocusRouter();

        public TextMeshProUGUI? TitleText => _titleText;
        public TextMeshProUGUI? RoundInfoText => _roundInfoText;
        public TextMeshProUGUI? WinSummaryText => _winSummaryText;
        public TextMeshProUGUI? YakuListText => _yakuListText;
        public TextMeshProUGUI? ScoreBreakdownText => _scoreBreakdownText;
        public TextMeshProUGUI? PaymentsText => _paymentsText;
        public TextMeshProUGUI? NewScoresText => _newScoresText;
        public GameObject? MatchSummaryRoot => _matchSummaryRoot;
        public TextMeshProUGUI? MatchRankingsText => _matchRankingsText;
        public PrivateLedgerPresenter? PrivateLedger => _privateLedger;
        public ConnectionBannerPresenter? ConnectionBanner => _connectionBanner;
        public GameObject? IncompatibleBannerRoot => _incompatibleBannerRoot;
        public TextMeshProUGUI? IncompatibleBannerText => _incompatibleBannerText;
        public Button? BackButton => _backButton;

        public void Configure(
            TextMeshProUGUI? titleText = null,
            TextMeshProUGUI? roundInfoText = null,
            TextMeshProUGUI? winSummaryText = null,
            TextMeshProUGUI? yakuListText = null,
            TextMeshProUGUI? scoreBreakdownText = null,
            TextMeshProUGUI? paymentsText = null,
            TextMeshProUGUI? newScoresText = null,
            GameObject? matchSummaryRoot = null,
            TextMeshProUGUI? matchRankingsText = null,
            PrivateLedgerPresenter? privateLedger = null,
            ConnectionBannerPresenter? connectionBanner = null,
            GameObject? incompatibleBannerRoot = null,
            TextMeshProUGUI? incompatibleBannerText = null,
            Button? backButton = null)
        {
            if (titleText != null) _titleText = titleText;
            if (roundInfoText != null) _roundInfoText = roundInfoText;
            if (winSummaryText != null) _winSummaryText = winSummaryText;
            if (yakuListText != null) _yakuListText = yakuListText;
            if (scoreBreakdownText != null) _scoreBreakdownText = scoreBreakdownText;
            if (paymentsText != null) _paymentsText = paymentsText;
            if (newScoresText != null) _newScoresText = newScoresText;
            if (matchSummaryRoot != null) _matchSummaryRoot = matchSummaryRoot;
            if (matchRankingsText != null) _matchRankingsText = matchRankingsText;
            if (privateLedger != null) _privateLedger = privateLedger;
            if (connectionBanner != null) _connectionBanner = connectionBanner;
            if (incompatibleBannerRoot != null) _incompatibleBannerRoot = incompatibleBannerRoot;
            if (incompatibleBannerText != null) _incompatibleBannerText = incompatibleBannerText;
            if (backButton != null) _backButton = backButton;
        }

        public void Initialize(IAppNavigation navigation, ISessionView? sessionView = null)
        {
            _navigation = navigation;
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

            var selectables = new List<Selectable>();
            if (_backButton != null) selectables.Add(_backButton);
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
            if (_backButton != null)
            {
                _backButton.onClick.RemoveListener(OnBackClicked);
            }
            if (_sessionView != null)
            {
                _sessionView.Changed -= OnSessionChanged;
            }
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

            SettlementViewModel settlement = SettlementModelExtractor.Extract(state);
            UpdateDisplay(settlement);
        }

        private void UpdateDisplay(SettlementViewModel model)
        {
            var round = model.Round;
            var match = model.Match;
            var ledger = model.Ledger;

            if (_titleText != null)
            {
                _titleText.text = match.IsFinished ? "【终局对局结算】" : "【本局结算】";
            }

            // Round settlement
            if (round != null)
            {
                if (_roundInfoText != null)
                {
                    _roundInfoText.text = $"局: {round.RoundId} | 结果: {round.SummaryText}";
                }

                if (_winSummaryText != null)
                {
                    if (round.WinnerSeat >= 0)
                    {
                        string triggerInfo = round.LoserSeat >= 0 ? $" (放铳席位: {round.LoserSeat})" : "";
                        _winSummaryText.text = $"胜者席位: {round.WinnerSeat}{triggerInfo} | 得分: {round.Score}点 | {round.Han}番{round.Fu}符";
                    }
                    else if (!string.IsNullOrEmpty(round.DrawReason))
                    {
                        string reasonText = YakuTextMapper.GetDrawGameReasonLabel(round.DrawReason);
                        _winSummaryText.text = $"流局结算原因: {reasonText}";
                    }
                    else
                    {
                        _winSummaryText.text = "结算数据处理中...";
                    }
                }

                if (_yakuListText != null)
                {
                    if (round.Yaku.Count > 0)
                    {
                        var sb = new StringBuilder("【役种列表】\n");
                        sb.AppendLine($"席位{round.WinnerSeat}:");
                        foreach (var y in round.Yaku)
                        {
                            sb.AppendLine($"  - {y.Label} ({y.Han}番)");
                        }
                        if (round.Breakdown != null && round.Breakdown.DoraCount > 0)
                        {
                            sb.AppendLine($"  - 宝牌 ({round.Breakdown.DoraCount}番)");
                        }
                        _yakuListText.text = sb.ToString().TrimEnd();
                    }
                    else
                    {
                        _yakuListText.text = string.Empty;
                    }
                }

                if (_scoreBreakdownText != null)
                {
                    if (round.WinnerSeat >= 0)
                    {
                        _scoreBreakdownText.text = $"【番符得分明细】\n席位{round.WinnerSeat}: {round.Han}番{round.Fu}符 -> 算分总计: {round.Score}点";
                    }
                    else
                    {
                        _scoreBreakdownText.text = string.Empty;
                    }
                }

                if (_paymentsText != null)
                {
                    if (round.Payments.Count > 0)
                    {
                        var sb = new StringBuilder("【点棒收付变动】\n");
                        foreach (var p in round.Payments)
                        {
                            sb.AppendLine(p.FormatTransfer());
                        }
                        _paymentsText.text = sb.ToString().TrimEnd();
                    }
                    else
                    {
                        _paymentsText.text = string.Empty;
                    }
                }

                if (_newScoresText != null)
                {
                    if (round.NewScores.Count > 0)
                    {
                        var sb = new StringBuilder("【结算后点棒】: ");
                        for (int i = 0; i < round.NewScores.Count; i++)
                        {
                            if (i > 0) sb.Append(" | ");
                            sb.Append($"席位{round.NewScores[i].Seat}: {round.NewScores[i].Score}点");
                        }
                        _newScoresText.text = sb.ToString();
                    }
                    else
                    {
                        _newScoresText.text = string.Empty;
                    }
                }
            }
            else
            {
                if (_roundInfoText != null) _roundInfoText.text = "等待本局结算结果...";
                if (_winSummaryText != null) _winSummaryText.text = "暂无局结算数据";
                if (_yakuListText != null) _yakuListText.text = string.Empty;
                if (_scoreBreakdownText != null) _scoreBreakdownText.text = string.Empty;
                if (_paymentsText != null) _paymentsText.text = string.Empty;
                if (_newScoresText != null) _newScoresText.text = string.Empty;
            }

            // Match rankings
            if (_matchSummaryRoot != null)
            {
                _matchSummaryRoot.SetActive(match.IsFinished || match.Rankings.Count > 0);
            }

            if (_matchRankingsText != null)
            {
                if (match.Rankings.Count > 0)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("【整场终局名次】");
                    for (int i = 0; i < match.Rankings.Count; i++)
                    {
                        MatchRankingItem r = match.Rankings[i];
                        string scoreStr = r.FinalScore.HasValue ? $"{r.FinalScore.Value}点" : "点数未知";
                        sb.AppendLine($"第{r.Rank}位: 席位{r.Seat} ({scoreStr})");
                    }
                    _matchRankingsText.text = sb.ToString().TrimEnd();
                }
                else
                {
                    _matchRankingsText.text = string.Empty;
                }
            }

            // Private ledger
            if (_privateLedger != null)
            {
                _privateLedger.UpdateView(ledger);
            }
        }

        public void ClearDisplay()
        {
            if (_titleText != null) _titleText.text = "结算画面";
            if (_roundInfoText != null) _roundInfoText.text = string.Empty;
            if (_winSummaryText != null) _winSummaryText.text = string.Empty;
            if (_yakuListText != null) _yakuListText.text = string.Empty;
            if (_scoreBreakdownText != null) _scoreBreakdownText.text = string.Empty;
            if (_paymentsText != null) _paymentsText.text = string.Empty;
            if (_newScoresText != null) _newScoresText.text = string.Empty;
            if (_matchSummaryRoot != null) _matchSummaryRoot.SetActive(false);
            if (_matchRankingsText != null) _matchRankingsText.text = string.Empty;
            if (_incompatibleBannerRoot != null) _incompatibleBannerRoot.SetActive(false);
            if (_incompatibleBannerText != null) _incompatibleBannerText.text = string.Empty;

            if (_privateLedger != null)
            {
                _privateLedger.Clear();
            }
        }

        public void OnBackClicked()
        {
            if (!UiInputLifecycle.Instance.ConsumeClickOrSubmit(_backButton != null ? _backButton.gameObject : null))
            {
                return;
            }
            Debug.Log("SETTLEMENT_BACK_CLICKED: Returning to menu...");
            _navigation?.BackToMenu();
        }
    }
}
