#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Unity.UI;
using Mahjong.Unity.UI.Session;

namespace Mahjong.Unity.Table
{
    /// <summary>
    /// Consolidated table presenter for Japanese Riichi Mahjong in Unity.
    /// Manages four-seat rendering, player hand/discard/meld interactions,
    /// three cheat disciplines (TileMark, SleeveBloom, BeamSwap), shuffle preparation,
    /// turn timer countdown, and confirmed animation dequeue.
    /// </summary>
    public sealed class TablePresenter : MonoBehaviour
    {
        [Header("Status & Session Info")]
        [SerializeField] private TextMeshProUGUI? _sessionInfoText;
        [SerializeField] private TextMeshProUGUI? _turnCountdownText;
        [SerializeField] private TextMeshProUGUI? _roundStatusText;
        [SerializeField] private TextMeshProUGUI? _wallRemainingText;
        [SerializeField] private TextMeshProUGUI? _doraIndicatorsText;
        [SerializeField] private TextMeshProUGUI? _lastDiscardText;
        [SerializeField] private GameObject? _pauseIndicator;
        [SerializeField] private TextMeshProUGUI? _statusBannerText;
        [SerializeField] private TextMeshProUGUI? _animationBannerText;

        [Header("Seats")]
        [SerializeField] private TableSeatView[] _seatViews = Array.Empty<TableSeatView>();

        [Header("Navigation & Controls")]
        [SerializeField] private Button? _backButton;
        [SerializeField] private Button? _viewSettlementButton;
        [SerializeField] private Button? _nextRoundButton;
        [SerializeField] private Button? _claimControlButton;
        [SerializeField] private Button? _releaseControlButton;
        [SerializeField] private TextMeshProUGUI? _controlStatusText;

        [Header("Own Hand Panel")]
        [SerializeField] private GameObject? _ownHandPanelRoot;
        [SerializeField] private Button[] _handTileButtons = Array.Empty<Button>();
        [SerializeField] private TextMeshProUGUI[] _handTileTexts = Array.Empty<TextMeshProUGUI>();
        [SerializeField] private GameObject[] _handTileMarkBadges = Array.Empty<GameObject>();
        [SerializeField] private Button? _drawnTileButton;
        [SerializeField] private TextMeshProUGUI? _drawnTileText;

        [Header("Action Candidates")]
        [SerializeField] private GameObject? _actionPanelRoot;
        [SerializeField] private Button? _discardButton;
        [SerializeField] private Button? _chiButton;
        [SerializeField] private Button? _ponButton;
        [SerializeField] private Button? _kanButton;
        [SerializeField] private Button? _riichiButton;
        [SerializeField] private Button? _tsumoButton;
        [SerializeField] private Button? _ronButton;
        [SerializeField] private Button? _passButton;
        [SerializeField] private Button? _cancelSelectionButton;

        [Header("Candidate Option Sub-panel")]
        [SerializeField] private GameObject? _candidateSelectionPanel;
        [SerializeField] private Button[] _candidateOptionButtons = Array.Empty<Button>();
        [SerializeField] private TextMeshProUGUI[] _candidateOptionTexts = Array.Empty<TextMeshProUGUI>();
        [SerializeField] private Button? _cancelCandidateOptionButton;

        [Header("Cheat Panel")]
        [SerializeField] private GameObject? _cheatPanelRoot;
        [SerializeField] private TextMeshProUGUI? _cheatPointsText;
        [SerializeField] private Button? _tileMarkButton;
        [SerializeField] private Button? _sleeveBloomButton;
        [SerializeField] private Button? _beamSwapButton;
        [SerializeField] private Button? _preparationButton;

        [Header("TileMark Sub-dialog")]
        [SerializeField] private GameObject? _tileMarkDialog;
        [SerializeField] private TextMeshProUGUI? _tileMarkStatusText;
        [SerializeField] private Button? _confirmTileMarkButton;
        [SerializeField] private Button? _cancelTileMarkButton;

        [Header("SleeveBloom Sub-dialog")]
        [SerializeField] private GameObject? _sleeveBloomDialog;
        [SerializeField] private TextMeshProUGUI? _sleeveBloomStatusText;
        [SerializeField] private TextMeshProUGUI? _sleeveTileDisplayText;
        [SerializeField] private Button? _sleeveStoreButton;
        [SerializeField] private Button? _sleeveSwapButton;
        [SerializeField] private Button? _sleevePassButton;
        [SerializeField] private Button? _cancelSleeveButton;

        [Header("BeamSwap Sub-dialog")]
        [SerializeField] private GameObject? _beamSwapDialog;
        [SerializeField] private TextMeshProUGUI? _beamSwapStatusText;
        [SerializeField] private Button[] _beamSlotButtons = Array.Empty<Button>();
        [SerializeField] private TextMeshProUGUI[] _beamSlotTexts = Array.Empty<TextMeshProUGUI>();
        [SerializeField] private Button? _confirmBeamSwapButton;
        [SerializeField] private Button? _keepBeamOriginalButton;
        [SerializeField] private Button? _cancelBeamButton;

        [Header("Preparation Sub-dialog")]
        [SerializeField] private GameObject? _preparationDialog;
        [SerializeField] private TextMeshProUGUI? _preparationInfoText;
        [SerializeField] private Button? _seeThroughButton;
        [SerializeField] private TextMeshProUGUI? _seeThroughButtonText;
        [SerializeField] private Button? _confirmPreparationButton;
        [SerializeField] private Button? _cancelPreparationButton;

        // Model & Interaction Pipeline
        private readonly ITableClientClock _clientClock;
        private readonly PlayerTableProjectionMapper _projectionMapper;
        private readonly TableCommandIntentFactory _intentFactory;
        private readonly TableInteractionState _interactionState;
        private readonly ConfirmedAnimationQueue _animationQueue;

        public TablePresenter()
        {
            _clientClock = new Mahjong.Client.SystemTableClientClock();
            _projectionMapper = new PlayerTableProjectionMapper(_clientClock);
            _intentFactory = new TableCommandIntentFactory(_clientClock);
            _interactionState = new TableInteractionState(_intentFactory);
            _animationQueue = new ConfirmedAnimationQueue();
        }

        private ITableCommandSink? _commandSink;
        private IAppNavigation? _navigation;
        private ISessionView? _sessionView;
        private Action? _onViewSettlement;
        private PlayerTableViewState? _latestView;

        // Selection buffers
        private readonly List<uint> _tileMarkSelectedTiles = new List<uint>();
        private uint? _selectedBeamHandTile;
        private PlayerBeamSwapCandidateState? _selectedBeamCandidate;
        private bool _seeThrough;
        private string? _currentSleeveOfferId;
        private string? _currentBeamOfferId;

        // Candidate options buffer
        private readonly List<PlayerLegalActionState> _presentedActionOptions = new List<PlayerLegalActionState>();

        // Animation dequeue pacing
        private float _animationBannerTimer;
        private const float AnimationDisplayDuration = 1.0f;

        public PlayerTableViewState? LatestView => _latestView;
        public TableInteractionState InteractionState => _interactionState;
        public ConfirmedAnimationQueue AnimationQueue => _animationQueue;

        private void Start()
        {
            WireButtons();
            WireControlButtonsGap();
            ResetAllViews();
        }

        private void Update()
        {
            UpdateCountdown();
            UpdateAnimationBanner();
        }

        public void Bind(
            ITableCommandSink? commandSink,
            IAppNavigation? navigation,
            ISessionView? sessionView,
            Action? onOpenSettlement = null)
        {
            if (_sessionView != null)
            {
                _sessionView.Changed -= OnSessionViewChanged;
            }

            _commandSink = commandSink;
            _navigation = navigation;
            _sessionView = sessionView;
            if (onOpenSettlement != null)
            {
                _onViewSettlement = onOpenSettlement;
            }

            if (_sessionView != null)
            {
                _sessionView.Changed += OnSessionViewChanged;
                OnSessionViewChanged(this, _sessionView.Current);
            }
        }

        public void Initialize(
            IAppNavigation navigation,
            ISessionView? sessionView,
            ITableCommandSink? commandSink,
            Action? onOpenSettlement = null)
        {
            _onViewSettlement = onOpenSettlement;
            Bind(commandSink, navigation, sessionView);
        }

        private void OnDestroy()
        {
            if (_sessionView != null)
            {
                _sessionView.Changed -= OnSessionViewChanged;
            }
        }

        public void OnSessionViewChanged(object? sender, SessionViewState state)
        {
            if (state == null) return;

            PlayerTableViewState nextView = _projectionMapper.Project(state);

            // Stale snapshot monotonicity fence:
            // A non-baseline snapshot with lower StateVersion for the same round/session must not overwrite newer confirmed state.
            if (_latestView != null && !state.IsFullBaseline)
            {
                if (string.Equals(_latestView.Identity.SessionId, nextView.Identity.SessionId, StringComparison.Ordinal) &&
                    string.Equals(_latestView.Identity.RoundId, nextView.Identity.RoundId, StringComparison.Ordinal) &&
                    nextView.Identity.StateVersion < _latestView.Identity.StateVersion)
                {
                    return; // Drop stale snapshot
                }
            }

            bool identityChanged = _latestView != null &&
                (_latestView.Identity.Role != nextView.Identity.Role ||
                 _latestView.Identity.Seat != nextView.Identity.Seat ||
                 _latestView.Identity.Generation != nextView.Identity.Generation ||
                 _latestView.Identity.BindingEpoch != nextView.Identity.BindingEpoch);

            bool roundChanged = _latestView != null &&
                !string.Equals(_latestView.Identity.RoundId, nextView.Identity.RoundId, StringComparison.Ordinal);

            if (state.IsFullBaseline || identityChanged || roundChanged)
            {
                _animationQueue.AcceptConfirmedBaseline(nextView);
                _animationBannerTimer = 0f;
                if (_animationBannerText != null)
                {
                    _animationBannerText.gameObject.SetActive(false);
                }
                _interactionState.ClearSelection();
                CloseAllCheatDialogs();
            }
            else if (_latestView != null)
            {
                _animationQueue.AcceptConfirmedDifference(_latestView, nextView, isFullBaseline: false);
            }

            // Private authorization fence: if private authorization is lost, close all cheat dialogs immediately
            if (!nextView.Identity.IsPrivateAuthorized)
            {
                CloseAllCheatDialogs();
                _interactionState.ClearSelection();
            }

            // Offer changed fence: if sleeve offer changed while dialog was open, close it
            var currentSleeve = nextView.Own?.Cheat?.ImmediateSleeveBloom ?? nextView.Own?.Cheat?.OffTurnSleeveBloom;
            string? newSleeveOfferId = currentSleeve?.OfferId;
            if (_currentSleeveOfferId != null && !string.Equals(_currentSleeveOfferId, newSleeveOfferId, StringComparison.Ordinal))
            {
                CloseSleeveBloomDialog();
            }
            _currentSleeveOfferId = newSleeveOfferId;

            // Reconcile pending commands against session receipts
            ReconcilePendingCommands(state);

            _interactionState.ApplyConfirmedView(nextView);
            _latestView = nextView;

            RenderView(nextView);
        }

        private void ReconcilePendingCommands(SessionViewState state)
        {
            if (_interactionState.Pending == null || state.PendingCommands == null) return;

            string pendingReqId = _interactionState.Pending.Intent.Command.RequestId.Value;
            for (int i = 0; i < state.PendingCommands.Count; i++)
            {
                var cmd = state.PendingCommands[i];
                if (string.Equals(cmd.RequestId, pendingReqId, StringComparison.Ordinal))
                {
                    if (cmd.LastReceipt.HasValue)
                    {
                        switch (cmd.LastReceipt.Value)
                        {
                            case Mahjong.Contracts.ReceiptStatus.Applied:
                                _interactionState.ApplyClientOutcome(TablePendingOutcome.Applied);
                                break;
                            case Mahjong.Contracts.ReceiptStatus.Recorded:
                                _interactionState.ApplyClientOutcome(TablePendingOutcome.Recorded);
                                break;
                            case Mahjong.Contracts.ReceiptStatus.Rejected:
                                _interactionState.ApplyClientOutcome(TablePendingOutcome.Rejected);
                                break;
                            case Mahjong.Contracts.ReceiptStatus.Unknown:
                                _interactionState.ApplyClientOutcome(TablePendingOutcome.Unknown);
                                break;
                        }
                    }
                    else if (cmd.NeedsReconciliation)
                    {
                        _interactionState.ApplyClientOutcome(TablePendingOutcome.Unknown);
                    }
                    break;
                }
            }
        }

        private void RenderView(PlayerTableViewState view)
        {
            RenderHeader(view);
            RenderSeats(view);
            RenderOwnHand(view);
            RenderActionButtons(view);
            RenderCheats(view);
            RenderSettlementNavigation(view);
        }

        private void RenderHeader(PlayerTableViewState view)
        {
            if (_sessionInfoText != null)
            {
                string roleStr = view.Identity.Role switch
                {
                    PlayerTableRole.Player => $"座位 {view.Identity.Seat} (本席)",
                    PlayerTableRole.Spectator => "观战视角",
                    _ => "大厅/未知"
                };
                string authStr = view.Identity.IsPrivateAuthorized ? "已授权" : "公开流";
                _sessionInfoText.text = $"{roleStr} | {authStr} | 代次:{view.Identity.Generation}";
            }

            if (_roundStatusText != null)
            {
                string wind = TileTextHelper.FormatWind((int)view.Board.RoundWind);
                _roundStatusText.text = $"{wind}{view.Board.RoundNumber}局 {view.Board.Honba}本场 (立直棒:{view.Board.RiichiSticks})";
            }

            if (_wallRemainingText != null)
            {
                _wallRemainingText.text = $"牌山余牌: {view.Board.WallRemaining}张";
            }

            if (_doraIndicatorsText != null)
            {
                _doraIndicatorsText.text = $"宝牌指示: {TileTextHelper.FormatTileList(view.Board.DoraIndicators)}";
            }

            if (_lastDiscardText != null)
            {
                if (view.Board.LastDiscard != null)
                {
                    _lastDiscardText.text = $"最新出牌: {TileTextHelper.FormatTile(view.Board.LastDiscard.TileId)}";
                }
                else
                {
                    _lastDiscardText.text = "最新出牌: 无";
                }
            }
        }

        private void RenderSeats(PlayerTableViewState view)
        {
            if (_seatViews == null) return;
            for (int i = 0; i < _seatViews.Length; i++)
            {
                if (_seatViews[i] == null) continue;
                if (i < view.Seats.Count)
                {
                    _seatViews[i].RenderSeat(view.Seats[i], view, view.Own != null && view.Identity.Seat == i);
                }
                else
                {
                    _seatViews[i].Clear();
                }
            }
        }

        private void RenderOwnHand(PlayerTableViewState view)
        {
            bool hasPrivateHand = view.Identity.IsPrivateAuthorized && view.Own != null;

            if (_ownHandPanelRoot != null)
            {
                _ownHandPanelRoot.SetActive(hasPrivateHand);
            }

            if (!hasPrivateHand)
            {
                if (_handTileButtons != null)
                {
                    for (int i = 0; i < _handTileButtons.Length; i++)
                    {
                        if (_handTileButtons[i] != null) _handTileButtons[i].gameObject.SetActive(false);
                    }
                }
                if (_drawnTileButton != null) _drawnTileButton.gameObject.SetActive(false);
                return;
            }

            var own = view.Own!;
            var closed = own.ClosedTiles;

            for (int i = 0; i < _handTileButtons.Length; i++)
            {
                var btn = _handTileButtons[i];
                var txt = i < _handTileTexts.Length ? _handTileTexts[i] : null;
                var badge = i < _handTileMarkBadges.Length ? _handTileMarkBadges[i] : null;

                if (i < closed.Count)
                {
                    uint tileId = closed[i];
                    if (btn != null) btn.gameObject.SetActive(true);
                    if (txt != null)
                    {
                        bool isSelected = _interactionState.Selection.HandTileId.HasValue && _interactionState.Selection.HandTileId.Value == tileId;
                        string markStr = (own.Cheat != null && TileTextHelper.IsTileMarked(own.Cheat.TileMark, tileId)) ? "★" : "";
                        txt.text = $"{markStr}{TileTextHelper.FormatTile(tileId)}";
                        txt.color = isSelected ? Color.yellow : Color.white;
                    }
                    if (badge != null)
                    {
                        badge.SetActive(own.Cheat != null && TileTextHelper.IsTileMarked(own.Cheat.TileMark, tileId));
                    }
                }
                else
                {
                    if (btn != null) btn.gameObject.SetActive(false);
                    if (badge != null) badge.SetActive(false);
                }
            }

            if (_drawnTileButton != null)
            {
                if (own.DrawnTileId.HasValue)
                {
                    _drawnTileButton.gameObject.SetActive(true);
                    if (_drawnTileText != null)
                    {
                        uint tileId = own.DrawnTileId.Value;
                        bool isSelected = _interactionState.Selection.HandTileId.HasValue && _interactionState.Selection.HandTileId.Value == tileId;
                        string markStr = (own.Cheat != null && TileTextHelper.IsTileMarked(own.Cheat.TileMark, tileId)) ? "★" : "";
                        _drawnTileText.text = $"摸牌:{markStr}{TileTextHelper.FormatTile(tileId)}";
                        _drawnTileText.color = isSelected ? Color.yellow : Color.white;
                    }
                }
                else
                {
                    _drawnTileButton.gameObject.SetActive(false);
                }
            }
        }

        private void RenderActionButtons(PlayerTableViewState view)
        {
            bool hasDecision = view.Identity.IsPrivateAuthorized && view.Own?.Decision != null;

            if (_actionPanelRoot != null)
            {
                _actionPanelRoot.SetActive(hasDecision);
            }

            if (!hasDecision || view.Own?.Decision == null)
            {
                SetButtonActive(_discardButton, false);
                SetButtonActive(_chiButton, false);
                SetButtonActive(_ponButton, false);
                SetButtonActive(_kanButton, false);
                SetButtonActive(_riichiButton, false);
                SetButtonActive(_tsumoButton, false);
                SetButtonActive(_ronButton, false);
                SetButtonActive(_passButton, false);
                SetButtonActive(_cancelSelectionButton, false);
                if (_candidateSelectionPanel != null) _candidateSelectionPanel.SetActive(false);
                return;
            }

            var decision = view.Own.Decision;
            bool canDiscard = false;
            bool canChi = false;
            bool canPon = false;
            bool canKan = false;
            bool canRiichi = false;
            bool canTsumo = false;
            bool canRon = false;
            bool canPass = false;

            for (int i = 0; i < decision.Candidates.Count; i++)
            {
                switch (decision.Candidates[i].Action)
                {
                    case PlayerActionKind.DiscardTile: canDiscard = true; break;
                    case PlayerActionKind.DeclareChi: canChi = true; break;
                    case PlayerActionKind.DeclarePon: canPon = true; break;
                    case PlayerActionKind.DeclareKan: canKan = true; break;
                    case PlayerActionKind.DeclareRiichi: canRiichi = true; break;
                    case PlayerActionKind.DeclareTsumo: canTsumo = true; break;
                    case PlayerActionKind.DeclareRon: canRon = true; break;
                    case PlayerActionKind.Pass: canPass = true; break;
                }
            }

            SetButtonActive(_discardButton, canDiscard && _interactionState.Selection.HandTileId.HasValue);
            SetButtonActive(_chiButton, canChi);
            SetButtonActive(_ponButton, canPon);
            SetButtonActive(_kanButton, canKan);
            SetButtonActive(_riichiButton, canRiichi);
            SetButtonActive(_tsumoButton, canTsumo);
            SetButtonActive(_ronButton, canRon);
            SetButtonActive(_passButton, canPass);
            SetButtonActive(_cancelSelectionButton, _interactionState.Selection.HandTileId.HasValue);
        }

        private void RenderCheats(PlayerTableViewState view)
        {
            bool canCheat = view.Identity.IsPrivateAuthorized && view.Own?.Cheat != null;

            if (_cheatPanelRoot != null)
            {
                _cheatPanelRoot.SetActive(canCheat);
            }

            if (!canCheat || view.Own?.Cheat == null)
            {
                if (_cheatPointsText != null) _cheatPointsText.text = string.Empty;
                SetButtonActive(_tileMarkButton, false);
                SetButtonActive(_sleeveBloomButton, false);
                SetButtonActive(_beamSwapButton, false);
                SetButtonActive(_preparationButton, false);
                CloseAllCheatDialogs();
                return;
            }

            var cheat = view.Own.Cheat;
            if (_cheatPointsText != null)
            {
                _cheatPointsText.text = $"出千点数: {cheat.CheatPoints}";
            }

            bool hasDecision = view.Own.Decision != null;
            var sleeveState = cheat.ImmediateSleeveBloom ?? cheat.OffTurnSleeveBloom;
            SetButtonActive(_tileMarkButton, cheat.TileMark != null && hasDecision);
            SetButtonActive(_sleeveBloomButton, sleeveState != null && (hasDecision || !string.IsNullOrEmpty(cheat.WindowId)));
            SetButtonActive(_beamSwapButton, cheat.BeamSwap != null && hasDecision);
            SetButtonActive(_preparationButton, cheat.Preparation != null);

            UpdateSleeveBloomDialog(sleeveState);
            UpdateBeamSwapDialog(cheat.BeamSwap);
            UpdatePreparationDialog(cheat.Preparation);
        }

        private void RenderSettlementNavigation(PlayerTableViewState view)
        {
            bool isRoundEnd = view.Board.Phase == PlayerGamePhase.RoundEnd;
            bool isMatchFinished = view.Board.Phase == PlayerGamePhase.MatchEnd;

            if (_nextRoundButton != null)
            {
                _nextRoundButton.gameObject.SetActive(isRoundEnd && !isMatchFinished);
            }

            if (_viewSettlementButton != null)
            {
                _viewSettlementButton.gameObject.SetActive(isRoundEnd || isMatchFinished);
            }
        }

        private void SetButtonActive(Button? button, bool active)
        {
            if (button != null)
            {
                button.gameObject.SetActive(active);
                button.interactable = active;
            }
        }

        private bool ConsumeInputLifecycle(GameObject? target)
        {
            if (UiInputLifecycle.Instance.IsPaused)
            {
                return false;
            }
            if (target != null && !UiInputLifecycle.Instance.ConsumeClickOrSubmit(target))
            {
                return false;
            }
            return true;
        }

        private void WireButtons()
        {
            if (_backButton != null)
                _backButton.onClick.AddListener(OnBackButtonClicked);

            if (_viewSettlementButton != null)
                _viewSettlementButton.onClick.AddListener(OnViewSettlementClicked);

            if (_nextRoundButton != null)
                _nextRoundButton.onClick.AddListener(OnNextRoundClicked);

            for (int i = 0; i < _handTileButtons.Length; i++)
            {
                int index = i;
                if (_handTileButtons[i] != null)
                    _handTileButtons[i].onClick.AddListener(() => OnHandTileClicked(index));
            }

            if (_drawnTileButton != null)
                _drawnTileButton.onClick.AddListener(OnDrawnTileClicked);

            if (_discardButton != null)
                _discardButton.onClick.AddListener(OnDiscardButtonClicked);

            if (_chiButton != null)
                _chiButton.onClick.AddListener(() => OnActionButtonClicked(PlayerActionKind.DeclareChi));

            if (_ponButton != null)
                _ponButton.onClick.AddListener(() => OnActionButtonClicked(PlayerActionKind.DeclarePon));

            if (_kanButton != null)
                _kanButton.onClick.AddListener(() => OnActionButtonClicked(PlayerActionKind.DeclareKan));

            if (_riichiButton != null)
                _riichiButton.onClick.AddListener(() => OnActionButtonClicked(PlayerActionKind.DeclareRiichi));

            if (_tsumoButton != null)
                _tsumoButton.onClick.AddListener(() => OnActionButtonClicked(PlayerActionKind.DeclareTsumo));

            if (_ronButton != null)
                _ronButton.onClick.AddListener(() => OnActionButtonClicked(PlayerActionKind.DeclareRon));

            if (_passButton != null)
                _passButton.onClick.AddListener(() => OnActionButtonClicked(PlayerActionKind.Pass));

            if (_cancelSelectionButton != null)
                _cancelSelectionButton.onClick.AddListener(OnCancelSelectionClicked);

            // Candidate sub-panel
            for (int i = 0; i < _candidateOptionButtons.Length; i++)
            {
                int optIdx = i;
                if (_candidateOptionButtons[i] != null)
                    _candidateOptionButtons[i].onClick.AddListener(() => OnCandidateOptionClicked(optIdx));
            }
            if (_cancelCandidateOptionButton != null)
                _cancelCandidateOptionButton.onClick.AddListener(CloseCandidateSelectionPanel);

            // Cheats
            if (_tileMarkButton != null)
                _tileMarkButton.onClick.AddListener(OpenTileMarkDialog);

            if (_sleeveBloomButton != null)
                _sleeveBloomButton.onClick.AddListener(OpenSleeveBloomDialog);

            if (_beamSwapButton != null)
                _beamSwapButton.onClick.AddListener(OpenBeamSwapDialog);

            if (_preparationButton != null)
                _preparationButton.onClick.AddListener(OpenPreparationDialog);

            // Sub-dialog confirmation buttons
            if (_confirmTileMarkButton != null)
                _confirmTileMarkButton.onClick.AddListener(OnConfirmTileMarkClicked);

            if (_cancelTileMarkButton != null)
                _cancelTileMarkButton.onClick.AddListener(CloseTileMarkDialog);

            if (_sleeveStoreButton != null)
                _sleeveStoreButton.onClick.AddListener(() => OnSleeveChoiceClicked(SleeveBloomChoice.First));

            if (_sleeveSwapButton != null)
                _sleeveSwapButton.onClick.AddListener(() => OnSleeveChoiceClicked(SleeveBloomChoice.Second));

            if (_sleevePassButton != null)
                _sleevePassButton.onClick.AddListener(CloseSleeveBloomDialog);

            if (_cancelSleeveButton != null)
                _cancelSleeveButton.onClick.AddListener(CloseSleeveBloomDialog);

            for (int b = 0; b < _beamSlotButtons.Length; b++)
            {
                int slotIndex = b;
                if (_beamSlotButtons[b] != null)
                    _beamSlotButtons[b].onClick.AddListener(() => OnBeamSlotClicked(slotIndex));
            }

            if (_confirmBeamSwapButton != null)
                _confirmBeamSwapButton.onClick.AddListener(OnConfirmBeamSwapClicked);

            if (_keepBeamOriginalButton != null)
                _keepBeamOriginalButton.onClick.AddListener(OnKeepBeamOriginalClicked);

            if (_cancelBeamButton != null)
                _cancelBeamButton.onClick.AddListener(CloseBeamSwapDialog);

            if (_seeThroughButton != null)
                _seeThroughButton.onClick.AddListener(OnToggleSeeThroughClicked);

            if (_confirmPreparationButton != null)
                _confirmPreparationButton.onClick.AddListener(OnConfirmPreparationClicked);

            if (_cancelPreparationButton != null)
                _cancelPreparationButton.onClick.AddListener(ClosePreparationDialog);
        }

        private void WireControlButtonsGap()
        {
            if (_claimControlButton != null)
            {
                _claimControlButton.gameObject.SetActive(false);
                _claimControlButton.interactable = false;
            }
            if (_releaseControlButton != null)
            {
                _releaseControlButton.gameObject.SetActive(false);
                _releaseControlButton.interactable = false;
            }
            if (_controlStatusText != null)
            {
                _controlStatusText.text = string.Empty;
            }
        }

        public void OnHandTileClicked(int index)
        {
            if (!ConsumeInputLifecycle(_handTileButtons != null && index < _handTileButtons.Length ? _handTileButtons[index]?.gameObject : null)) return;
            if (_latestView == null || _latestView.Own == null) return;

            if (_tileMarkDialog != null && _tileMarkDialog.activeSelf)
            {
                if (index < _latestView.Own.ClosedTiles.Count)
                {
                    uint tileId = _latestView.Own.ClosedTiles[index];
                    ToggleTileMarkTile(tileId);
                }
                return;
            }

            if (_beamSwapDialog != null && _beamSwapDialog.activeSelf)
            {
                if (index < _latestView.Own.ClosedTiles.Count)
                {
                    _selectedBeamHandTile = _latestView.Own.ClosedTiles[index];
                    UpdateBeamSwapStatus();
                }
                return;
            }

            if (index < _latestView.Own.ClosedTiles.Count)
            {
                uint tileId = _latestView.Own.ClosedTiles[index];
                _interactionState.SelectDiscardTile(tileId);
                RenderOwnHand(_latestView);
                RenderActionButtons(_latestView);
            }
        }

        public void OnDrawnTileClicked()
        {
            if (!ConsumeInputLifecycle(_drawnTileButton?.gameObject)) return;
            if (_latestView == null || _latestView.Own == null || !_latestView.Own.DrawnTileId.HasValue) return;

            uint tileId = _latestView.Own.DrawnTileId.Value;

            if (_tileMarkDialog != null && _tileMarkDialog.activeSelf)
            {
                ToggleTileMarkTile(tileId);
                return;
            }

            if (_beamSwapDialog != null && _beamSwapDialog.activeSelf)
            {
                _selectedBeamHandTile = tileId;
                UpdateBeamSwapStatus();
                return;
            }

            _interactionState.SelectDiscardTile(tileId);
            RenderOwnHand(_latestView);
            RenderActionButtons(_latestView);
        }

        public void OnCancelSelectionClicked()
        {
            if (!ConsumeInputLifecycle(_cancelSelectionButton?.gameObject)) return;
            _interactionState.ClearSelection();
            if (_latestView != null)
            {
                RenderOwnHand(_latestView);
                RenderActionButtons(_latestView);
            }
        }

        public void OnDiscardButtonClicked()
        {
            if (!ConsumeInputLifecycle(_discardButton?.gameObject)) return;
            if (_latestView == null) return;

            var ownerContext = BuildOwnerContext();
            if (ownerContext == null) return;

            var outcome = _interactionState.BuildIntent(TableIntentKind.SubmitAction, ownerContext);
            if (outcome.Accepted && outcome.Intent != null)
            {
                SubmitIntentAsync(outcome.Intent);
            }
            else
            {
                UpdateStatusText($"打牌无法提交: {FormatRejection(outcome.Code)}");
            }
        }

        public void OnActionButtonClicked(PlayerActionKind kind)
        {
            GameObject? btnTarget = kind switch
            {
                PlayerActionKind.DeclareChi => _chiButton?.gameObject,
                PlayerActionKind.DeclarePon => _ponButton?.gameObject,
                PlayerActionKind.DeclareKan => _kanButton?.gameObject,
                PlayerActionKind.DeclareRiichi => _riichiButton?.gameObject,
                PlayerActionKind.DeclareTsumo => _tsumoButton?.gameObject,
                PlayerActionKind.DeclareRon => _ronButton?.gameObject,
                PlayerActionKind.Pass => _passButton?.gameObject,
                _ => null
            };
            if (!ConsumeInputLifecycle(btnTarget)) return;
            if (_latestView == null || _latestView.Own?.Decision == null) return;

            var matching = new List<PlayerLegalActionState>();
            var candidates = _latestView.Own.Decision.Candidates;
            for (int i = 0; i < candidates.Count; i++)
            {
                var action = candidates[i];
                if (action.Action == kind)
                {
                    matching.Add(action);
                }
            }

            if (matching.Count == 0) return;

            // If multiple candidates exist for the same action type (e.g. 2 Chi options):
            // Present them for explicit player choice! Do not submit immediately!
            if (matching.Count > 1)
            {
                OpenCandidateSelectionPanel(matching);
                return;
            }

            // Exactly one candidate: select and submit
            SubmitSpecificAction(matching[0]);
        }

        private void OpenCandidateSelectionPanel(List<PlayerLegalActionState> options)
        {
            _presentedActionOptions.Clear();
            _presentedActionOptions.AddRange(options);

            if (_candidateSelectionPanel != null)
            {
                _candidateSelectionPanel.SetActive(true);
            }

            for (int i = 0; i < _candidateOptionButtons.Length; i++)
            {
                var btn = _candidateOptionButtons[i];
                var txt = i < _candidateOptionTexts.Length ? _candidateOptionTexts[i] : null;

                if (i < options.Count)
                {
                    if (btn != null) btn.gameObject.SetActive(true);
                    if (txt != null)
                    {
                        var opt = options[i];
                        string tiles = TileTextHelper.FormatTileList(opt.TileIds);
                        txt.text = $"{TileTextHelper.FormatActionKind(opt.Action)}: {tiles} ({opt.OptionId})";
                    }
                }
                else
                {
                    if (btn != null) btn.gameObject.SetActive(false);
                }
            }
        }

        public void CloseCandidateSelectionPanel()
        {
            if (_candidateSelectionPanel != null)
            {
                _candidateSelectionPanel.SetActive(false);
            }
            _presentedActionOptions.Clear();
        }

        public void OnCandidateOptionClicked(int index)
        {
            if (index < 0 || index >= _presentedActionOptions.Count) return;
            var action = _presentedActionOptions[index];
            CloseCandidateSelectionPanel();
            SubmitSpecificAction(action);
        }

        private void SubmitSpecificAction(PlayerLegalActionState action)
        {
            if (_latestView == null) return;
            var ownerContext = BuildOwnerContext();
            if (ownerContext == null) return;

            var selectOutcome = _interactionState.SelectAction(action);
            if (!selectOutcome.Accepted)
            {
                UpdateStatusText($"操作选择失败: {FormatRejection(selectOutcome.Code)}");
                return;
            }

            var outcome = _interactionState.BuildIntent(TableIntentKind.SubmitAction, ownerContext);
            if (outcome.Accepted && outcome.Intent != null)
            {
                SubmitIntentAsync(outcome.Intent);
            }
            else
            {
                UpdateStatusText($"操作无法提交: {FormatRejection(outcome.Code)}");
            }
        }

        public void OpenTileMarkDialog()
        {
            if (!ConsumeInputLifecycle(_tileMarkButton?.gameObject)) return;
            _tileMarkSelectedTiles.Clear();
            if (_tileMarkDialog != null) _tileMarkDialog.SetActive(true);
            UpdateTileMarkStatus();
        }

        public void CloseTileMarkDialog()
        {
            _tileMarkSelectedTiles.Clear();
            if (_tileMarkDialog != null) _tileMarkDialog.SetActive(false);
        }

        private void ToggleTileMarkTile(uint tileId)
        {
            if (_tileMarkSelectedTiles.Contains(tileId))
            {
                _tileMarkSelectedTiles.Remove(tileId);
            }
            else if (_tileMarkSelectedTiles.Count < 4)
            {
                _tileMarkSelectedTiles.Add(tileId);
            }
            UpdateTileMarkStatus();
        }

        private void UpdateTileMarkStatus()
        {
            if (_tileMarkStatusText != null)
            {
                var sb = new StringBuilder();
                sb.Append($"已选牌 ({_tileMarkSelectedTiles.Count}/4): ");
                for (int i = 0; i < _tileMarkSelectedTiles.Count; i++)
                {
                    sb.Append($"{TileTextHelper.FormatTile(_tileMarkSelectedTiles[i])} ");
                }
                _tileMarkStatusText.text = sb.ToString();
            }

            if (_confirmTileMarkButton != null)
            {
                _confirmTileMarkButton.interactable = _tileMarkSelectedTiles.Count == 4;
            }
        }

        public void OnConfirmTileMarkClicked()
        {
            if (!ConsumeInputLifecycle(_confirmTileMarkButton?.gameObject)) return;
            if (_tileMarkSelectedTiles.Count != 4 || _latestView == null) return;

            var ownerContext = BuildOwnerContext();
            if (ownerContext == null) return;

            var selectOutcome = _interactionState.SelectTileMark(_tileMarkSelectedTiles);
            if (!selectOutcome.Accepted)
            {
                UpdateStatusText($"落汗选择失败: {FormatRejection(selectOutcome.Code)}");
                return;
            }

            var outcome = _interactionState.BuildIntent(TableIntentKind.SubmitTileMark, ownerContext);
            if (outcome.Accepted && outcome.Intent != null)
            {
                SubmitIntentAsync(outcome.Intent);
                CloseTileMarkDialog();
            }
            else
            {
                UpdateStatusText($"落汗无法提交: {FormatRejection(outcome.Code)}");
            }
        }

        public void OpenSleeveBloomDialog()
        {
            if (!ConsumeInputLifecycle(_sleeveBloomButton?.gameObject)) return;
            var cheat = _latestView?.Own?.Cheat;
            var sleeve = cheat?.ImmediateSleeveBloom ?? cheat?.OffTurnSleeveBloom;
            if (sleeve == null) return;

            _currentSleeveOfferId = sleeve.OfferId;
            if (_sleeveBloomDialog != null) _sleeveBloomDialog.SetActive(true);

            if (_sleeveTileDisplayText != null)
            {
                string first = sleeve.FirstTileId.HasValue ? TileTextHelper.FormatTile(sleeve.FirstTileId.Value) : "无";
                string second = sleeve.SecondTileId.HasValue ? TileTextHelper.FormatTile(sleeve.SecondTileId.Value) : "无";
                _sleeveTileDisplayText.text = $"袖牌1: {first} | 袖牌2: {second}";
            }

            if (_sleeveBloomStatusText != null)
            {
                _sleeveBloomStatusText.text = $"模式: {sleeve.Mode} (类型:{sleeve.DrawKind})";
            }
        }

        public void CloseSleeveBloomDialog()
        {
            if (_sleeveBloomDialog != null) _sleeveBloomDialog.SetActive(false);
            _currentSleeveOfferId = null;
        }

        public void OnSleeveChoiceClicked(string choice)
        {
            var enumChoice = string.Equals(choice, "SWAP", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(choice, "SECOND", StringComparison.OrdinalIgnoreCase)
                ? SleeveBloomChoice.Second
                : SleeveBloomChoice.First;
            OnSleeveChoiceClicked(enumChoice);
        }

        public void OnSleeveChoiceClicked(SleeveBloomChoice choice)
        {
            GameObject? btnTarget = choice == SleeveBloomChoice.First ? _sleeveStoreButton?.gameObject : _sleeveSwapButton?.gameObject;
            if (!ConsumeInputLifecycle(btnTarget)) return;
            var cheat = _latestView?.Own?.Cheat;
            var sleeve = cheat?.ImmediateSleeveBloom ?? cheat?.OffTurnSleeveBloom;
            if (sleeve == null) return;
            if (_sleeveBloomDialog == null || !_sleeveBloomDialog.activeInHierarchy) return;

            var ownerContext = BuildOwnerContext();
            if (ownerContext == null) return;

            bool offTurn = cheat?.OffTurnSleeveBloom != null;
            var selectOutcome = _interactionState.SelectSleeveBloom(offTurn, choice);
            if (!selectOutcome.Accepted)
            {
                UpdateStatusText($"袖里藏花选择失败: {FormatRejection(selectOutcome.Code)}");
                return;
            }

            var outcome = _interactionState.BuildIntent(
                offTurn ? TableIntentKind.SubmitSleeveBloomOffTurnChoice : TableIntentKind.SubmitSleeveBloomChoice,
                ownerContext);

            if (outcome.Accepted && outcome.Intent != null)
            {
                SubmitIntentAsync(outcome.Intent);
                CloseSleeveBloomDialog();
            }
            else
            {
                UpdateStatusText($"袖里藏花无法提交: {FormatRejection(outcome.Code)}");
            }
        }

        private void UpdateSleeveBloomDialog(PlayerSleeveBloomState? sleeve)
        {
            if (sleeve == null)
            {
                CloseSleeveBloomDialog();
                return;
            }

            if (_sleeveTileDisplayText != null && _sleeveBloomDialog != null && _sleeveBloomDialog.activeSelf)
            {
                string first = sleeve.FirstTileId.HasValue ? TileTextHelper.FormatTile(sleeve.FirstTileId.Value) : "无";
                string second = sleeve.SecondTileId.HasValue ? TileTextHelper.FormatTile(sleeve.SecondTileId.Value) : "无";
                _sleeveTileDisplayText.text = $"袖牌1: {first} | 袖牌2: {second}";
            }
        }

        public void OpenBeamSwapDialog()
        {
            if (!ConsumeInputLifecycle(_beamSwapButton?.gameObject)) return;
            if (_latestView?.Own?.Cheat?.BeamSwap == null) return;

            var beam = _latestView.Own.Cheat.BeamSwap;
            _currentBeamOfferId = beam.OfferId;
            _selectedBeamHandTile = null;
            _selectedBeamCandidate = null;

            if (_beamSwapDialog != null) _beamSwapDialog.SetActive(true);
            UpdateBeamSwapDialog(beam);
        }

        public void CloseBeamSwapDialog()
        {
            if (_beamSwapDialog != null) _beamSwapDialog.SetActive(false);
            _currentBeamOfferId = null;
            _selectedBeamHandTile = null;
            _selectedBeamCandidate = null;
        }

        private void UpdateBeamSwapDialog(PlayerBeamSwapState? beam)
        {
            if (beam == null)
            {
                CloseBeamSwapDialog();
                return;
            }

            for (int i = 0; i < _beamSlotButtons.Length; i++)
            {
                var btn = _beamSlotButtons[i];
                var txt = i < _beamSlotTexts.Length ? _beamSlotTexts[i] : null;

                if (i < beam.Candidates.Count)
                {
                    if (btn != null) btn.gameObject.SetActive(true);
                    if (txt != null)
                    {
                        var cand = beam.Candidates[i];
                        string tileStr = cand.TileId.HasValue ? TileTextHelper.FormatTile(cand.TileId.Value) : "未知";
                        string kindStr = TileTextHelper.FormatBeamSwapSlotKind(cand.SlotKind, cand.SlotIndex);
                        txt.text = $"{kindStr}: [{tileStr}]";
                    }
                }
                else
                {
                    if (btn != null) btn.gameObject.SetActive(false);
                }
            }

            UpdateBeamSwapStatus();
        }

        public void OnBeamSlotClicked(int index)
        {
            if (!ConsumeInputLifecycle(_beamSlotButtons != null && index < _beamSlotButtons.Length ? _beamSlotButtons[index]?.gameObject : null)) return;
            if (_latestView?.Own?.Cheat?.BeamSwap == null) return;

            var candidates = _latestView.Own.Cheat.BeamSwap.Candidates;
            if (index < candidates.Count)
            {
                _selectedBeamCandidate = candidates[index];
                UpdateBeamSwapStatus();
            }
        }

        private void UpdateBeamSwapStatus()
        {
            if (_beamSwapStatusText != null)
            {
                string handStr = _selectedBeamHandTile.HasValue ? TileTextHelper.FormatTile(_selectedBeamHandTile.Value) : "未选";
                string slotStr = _selectedBeamCandidate != null ? $"槽位{_selectedBeamCandidate.SlotIndex}" : "未选";
                _beamSwapStatusText.text = $"手牌: {handStr} ◀ 交换 ▶ 目标: {slotStr}";
            }

            if (_confirmBeamSwapButton != null)
            {
                _confirmBeamSwapButton.interactable = _selectedBeamHandTile.HasValue && _selectedBeamCandidate != null;
            }
        }

        public void OnConfirmBeamSwapClicked()
        {
            if (!ConsumeInputLifecycle(_confirmBeamSwapButton?.gameObject)) return;
            if (_latestView == null || !_selectedBeamHandTile.HasValue || _selectedBeamCandidate == null) return;

            var ownerContext = BuildOwnerContext();
            if (ownerContext == null) return;

            var selectOutcome = _interactionState.SelectBeamSwap(BeamSwapChoice.Swap, _selectedBeamHandTile.Value, _selectedBeamCandidate);
            if (!selectOutcome.Accepted)
            {
                UpdateStatusText($"偷梁换柱选择失败: {FormatRejection(selectOutcome.Code)}");
                return;
            }

            var outcome = _interactionState.BuildIntent(TableIntentKind.SubmitBeamSwapChoice, ownerContext);
            if (outcome.Accepted && outcome.Intent != null)
            {
                SubmitIntentAsync(outcome.Intent);
                CloseBeamSwapDialog();
            }
            else
            {
                UpdateStatusText($"偷梁换柱无法提交: {FormatRejection(outcome.Code)}");
            }
        }

        public void OnKeepBeamOriginalClicked()
        {
            if (!ConsumeInputLifecycle(_keepBeamOriginalButton?.gameObject)) return;
            if (_latestView == null) return;

            var ownerContext = BuildOwnerContext();
            if (ownerContext == null) return;

            var selectOutcome = _interactionState.SelectBeamSwap(BeamSwapChoice.KeepOriginal, null, null);
            if (!selectOutcome.Accepted)
            {
                UpdateStatusText($"保留原牌选择失败: {FormatRejection(selectOutcome.Code)}");
                return;
            }

            var outcome = _interactionState.BuildIntent(TableIntentKind.SubmitBeamSwapChoice, ownerContext);
            if (outcome.Accepted && outcome.Intent != null)
            {
                SubmitIntentAsync(outcome.Intent);
                CloseBeamSwapDialog();
            }
            else
            {
                UpdateStatusText($"保留原牌无法提交: {FormatRejection(outcome.Code)}");
            }
        }

        public void OpenPreparationDialog()
        {
            if (!ConsumeInputLifecycle(_preparationButton?.gameObject)) return;
            if (_latestView?.Own?.Cheat?.Preparation == null) return;

            if (_preparationDialog != null) _preparationDialog.SetActive(true);
            UpdatePreparationDialog(_latestView.Own.Cheat.Preparation);
        }

        public void ClosePreparationDialog()
        {
            if (_preparationDialog != null) _preparationDialog.SetActive(false);
        }

        public void OnCancelPreparationClicked()
        {
            if (!ConsumeInputLifecycle(_cancelPreparationButton?.gameObject)) return;
            ClosePreparationDialog();
        }

        private void UpdatePreparationDialog(PlayerPreparationState? prep)
        {
            if (prep == null)
            {
                ClosePreparationDialog();
                return;
            }

            _seeThrough = prep.SeeThrough;

            if (_preparationInfoText != null)
            {
                _preparationInfoText.text = $"洗牌目标: {prep.Target} (耗费: {prep.ReservedPoints}点)";
            }

            UpdateSeeThroughDisplay(prep);
        }

        private void UpdateSeeThroughDisplay(PlayerPreparationState prep)
        {
            if (_seeThroughButton != null)
            {
                _seeThroughButton.interactable = prep.SeeThroughEnabled;
            }

            if (_seeThroughButtonText != null)
            {
                uint cost = prep.SeeThroughCost;
                string stateStr = _seeThrough ? "[开启]" : "[关闭]";
                _seeThroughButtonText.text = $"透视绝技{stateStr} (耗费: {cost}点)";
            }
        }

        public void OnToggleSeeThroughClicked()
        {
            if (!ConsumeInputLifecycle(_seeThroughButton?.gameObject)) return;
            if (_latestView?.Own?.Cheat?.Preparation == null) return;

            var prep = _latestView.Own.Cheat.Preparation;
            if (!prep.SeeThroughEnabled) return;

            _seeThrough = !_seeThrough;
            UpdateSeeThroughDisplay(prep);
        }

        public void OnConfirmPreparationClicked()
        {
            if (!ConsumeInputLifecycle(_confirmPreparationButton?.gameObject)) return;
            if (_latestView?.Own?.Cheat?.Preparation == null) return;

            var ownerContext = BuildOwnerContext();
            if (ownerContext == null) return;

            var selectOutcome = _interactionState.SelectPreparation(_seeThrough);
            if (!selectOutcome.Accepted)
            {
                UpdateStatusText($"码牌准备选择失败: {FormatRejection(selectOutcome.Code)}");
                return;
            }

            var outcome = _interactionState.BuildIntent(TableIntentKind.SubmitShufflePlan, ownerContext);
            if (outcome.Accepted && outcome.Intent != null)
            {
                SubmitIntentAsync(outcome.Intent);
                ClosePreparationDialog();
            }
            else
            {
                UpdateStatusText($"码牌准备无法提交: {FormatRejection(outcome.Code)}");
            }
        }

        public void OnNextRoundClicked()
        {
            if (!ConsumeInputLifecycle(_nextRoundButton?.gameObject)) return;
            if (_latestView == null) return;

            var ownerContext = BuildOwnerContext();
            if (ownerContext == null) return;

            var outcome = _interactionState.BuildIntent(TableIntentKind.NextRound, ownerContext);
            if (outcome.Accepted && outcome.Intent != null)
            {
                SubmitIntentAsync(outcome.Intent);
            }
            else
            {
                UpdateStatusText($"下一局无法提交: {FormatRejection(outcome.Code)}");
            }
        }

        public void OnBackButtonClicked()
        {
            if (!ConsumeInputLifecycle(_backButton?.gameObject)) return;
            _navigation?.BackToMenu();
        }

        public void OnViewSettlementClicked()
        {
            if (!ConsumeInputLifecycle(_viewSettlementButton?.gameObject)) return;
            _onViewSettlement?.Invoke();
        }

        private async void SubmitIntentAsync(TableCommandIntent intent)
        {
            if (intent == null) return;
            var sink = _commandSink;
            if (sink == null || !sink.CanSubmit)
            {
                UpdateStatusText("服务通道不可用，命令已拒绝");
                return;
            }

            if (_latestView == null) return;

            // Generation fence: sink generation must match view generation!
            if (sink.Generation != _latestView.Identity.Generation)
            {
                Debug.LogWarning($"[TablePresenter] Sink generation ({sink.Generation}) != view generation ({_latestView.Identity.Generation}). Submission dropped.");
                return;
            }

            var command = intent.Command;
            UpdateStatusText($"已提交: {intent.Kind} (等待权威确认)...");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            CommandSubmission submission;
            try
            {
                submission = await sink.SubmitAsync(command, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TablePresenter] SubmitAsync exception: {ex.Message}");
                return;
            }

            if (submission.Command.LastReceipt == Mahjong.Contracts.ReceiptStatus.Rejected)
            {
                _interactionState.ApplyClientOutcome(TablePendingOutcome.Rejected);
                UpdateStatusText($"命令被服务器拒绝: {intent.Kind}");
            }
            // For Sent and WasDuplicate, DO NOT mark Recorded here!
            // Transport Sent is not an authoritative business receipt.
            // The command remains pending until confirmed snapshot or receipt reconciliation.
        }

        private TableCommandOwnerContext? BuildOwnerContext()
        {
            if (_latestView == null) return null;
            return new TableCommandOwnerContext(
                requestId: new RequestId(Guid.NewGuid().ToString("N")),
                scopeId: new RequestScopeId(_latestView.Identity.ScopeId),
                expectedStateVersion: new StateVersion(_latestView.Identity.StateVersion),
                ownerEpoch: new OwnerEpoch(_latestView.Identity.OwnerEpoch));
        }

        private void UpdateCountdown()
        {
            var cd = _latestView?.Own?.Decision?.Countdown ?? (_latestView != null ? _latestView.Clock(_latestView.Identity.Seat)?.DecisionDeadline : null);
            if (cd != null && _turnCountdownText != null)
            {
                long totalMs = cd.RemainingMilliseconds(_clientClock.MonotonicMilliseconds);
                float sec = Mathf.Max(0f, totalMs / 1000f);
                _turnCountdownText.text = $"思考时间: {sec:F1}秒";
                _turnCountdownText.color = sec <= 3.0f ? Color.red : Color.green;
            }
            else if (_turnCountdownText != null)
            {
                _turnCountdownText.text = "思考时间: -";
                _turnCountdownText.color = Color.white;
            }
        }

        private void UpdateAnimationBanner()
        {
            if (_animationQueue.PendingCount > 0 && _animationBannerTimer <= 0f)
            {
                if (_animationQueue.TryDequeue(out var anim) && anim != null && _animationBannerText != null)
                {
                    string tileStr = anim.TileId.HasValue ? $": {TileTextHelper.FormatTile(anim.TileId.Value)}" : "";
                    _animationBannerText.text = $"[动画播报] 座位{anim.Seat} {FormatAnimationKind(anim.Kind)}{tileStr}";
                    _animationBannerText.gameObject.SetActive(true);
                    _animationBannerTimer = AnimationDisplayDuration;
                }
            }

            if (_animationBannerTimer > 0f)
            {
                _animationBannerTimer -= Time.deltaTime;
                if (_animationBannerTimer <= 0f && _animationBannerText != null)
                {
                    _animationBannerText.gameObject.SetActive(false);
                }
            }
        }

        private static string FormatAnimationKind(TableAnimationKind kind)
        {
            return kind switch
            {
                TableAnimationKind.OwnDraw => "摸牌",
                TableAnimationKind.Draw => "摸牌",
                TableAnimationKind.Discard => "打牌",
                TableAnimationKind.Meld => "副露鸣牌",
                TableAnimationKind.Riichi => "立直",
                TableAnimationKind.Win => "和牌",
                TableAnimationKind.Dora => "翻开宝牌指示牌",
                TableAnimationKind.CheatPoints => "出千点变动",
                TableAnimationKind.SleeveBloomResolved => "袖里藏花调换",
                TableAnimationKind.BeamSwapResolved => "偷梁换柱完成",
                TableAnimationKind.Deal => "发牌",
                TableAnimationKind.Settlement => "局末结算",
                TableAnimationKind.RoundEnd => "单局结束",
                TableAnimationKind.MatchEnd => "半庄结束",
                _ => "事件"
            };
        }

        private static string FormatRejection(string code)
        {
            return code switch
            {
                TableInteractionCodes.AlreadyPending => "操作正在处理中",
                TableInteractionCodes.PendingConflict => "存在冲突的操作正在处理中",
                TableInteractionCodes.NoPending => "无待处理操作",
                TableInteractionCodes.SelectionRequired => "请完成所有必要选择",
                TableInteractionCodes.SelectionExpired => "操作已过期",
                TableInteractionCodes.NotACandidate => "所选牌不在有效候选内",
                TableInteractionCodes.StaleSelection => "选择已过时",
                TableInteractionCodes.NotSubmittable => "当前状态不允许提交",
                TableInteractionCodes.NoConfirmedView => "尚未同步确认牌桌状态",
                TableInteractionCodes.NoPrivateView => "尚未获得私有授权状态",
                TableInteractionCodes.StaleGeneration => "会话代次已过时",
                TableInteractionCodes.StaleBinding => "会话绑定已过时",
                TableInteractionCodes.StaleState => "牌桌状态已推进",
                _ => code
            };
        }

        private void UpdateStatusText(string message)
        {
            if (_statusBannerText != null)
            {
                _statusBannerText.text = message;
                _statusBannerText.gameObject.SetActive(true);
            }
        }

        public void CloseAllCheatDialogs()
        {
            if (_tileMarkDialog != null) _tileMarkDialog.SetActive(false);
            if (_sleeveBloomDialog != null) _sleeveBloomDialog.SetActive(false);
            if (_beamSwapDialog != null) _beamSwapDialog.SetActive(false);
            if (_preparationDialog != null) _preparationDialog.SetActive(false);
            if (_candidateSelectionPanel != null) _candidateSelectionPanel.SetActive(false);
            _currentSleeveOfferId = null;
            _currentBeamOfferId = null;
        }

        public void ClearAll()
        {
            _latestView = null;
            _animationQueue?.Drain();
            _interactionState?.ClearSelection();
            _animationBannerTimer = 0f;
            if (_animationBannerText != null) _animationBannerText.gameObject.SetActive(false);
            if (_statusBannerText != null) _statusBannerText.gameObject.SetActive(false);
            CloseAllCheatDialogs();

            if (_handTileButtons != null)
            {
                for (int i = 0; i < _handTileButtons.Length; i++)
                {
                    if (_handTileButtons[i] != null) _handTileButtons[i].gameObject.SetActive(false);
                    if (_handTileMarkBadges != null && i < _handTileMarkBadges.Length && _handTileMarkBadges[i] != null)
                        _handTileMarkBadges[i].SetActive(false);
                }
            }
            if (_drawnTileButton != null) _drawnTileButton.gameObject.SetActive(false);

            ResetAllViews();
        }

        private void ResetAllViews()
        {
            if (_sessionInfoText != null) _sessionInfoText.text = "等待房间同步...";
            if (_roundStatusText != null) _roundStatusText.text = "-";
            if (_wallRemainingText != null) _wallRemainingText.text = "-";
            if (_doraIndicatorsText != null) _doraIndicatorsText.text = "-";
            if (_lastDiscardText != null) _lastDiscardText.text = "-";
            if (_turnCountdownText != null) _turnCountdownText.text = "-";
            if (_cheatPointsText != null) _cheatPointsText.text = "-";

            for (int i = 0; i < _seatViews.Length; i++)
            {
                _seatViews[i]?.Clear();
            }
        }
    }

    /// <summary>
    /// Presentation model and controls for a single seat on the Mahjong table (0..3).
    /// </summary>
    [Serializable]
    public sealed class TableSeatView
    {
        [SerializeField] private GameObject? _root;
        [SerializeField] private TextMeshProUGUI? _seatNameText;
        [SerializeField] private TextMeshProUGUI? _scoreText;
        [SerializeField] private TextMeshProUGUI? _handText;
        [SerializeField] private GameObject? _riichiBadge;
        [SerializeField] private TextMeshProUGUI? _discardsText;
        [SerializeField] private TextMeshProUGUI? _meldsText;
        [SerializeField] private TextMeshProUGUI? _ankanText;

        public GameObject? Root => _root;
        public TextMeshProUGUI? SeatNameText => _seatNameText;
        public TextMeshProUGUI? ScoreText => _scoreText;
        public TextMeshProUGUI? HandText => _handText;
        public GameObject? RiichiBadge => _riichiBadge;
        public TextMeshProUGUI? DiscardsText => _discardsText;
        public TextMeshProUGUI? MeldsText => _meldsText;
        public TextMeshProUGUI? AnkanText => _ankanText;

        public void BindControls(
            GameObject? root,
            TextMeshProUGUI? seatNameText,
            TextMeshProUGUI? scoreText,
            TextMeshProUGUI? handText,
            GameObject? riichiBadge,
            TextMeshProUGUI? discardsText,
            TextMeshProUGUI? meldsText,
            TextMeshProUGUI? ankanText)
        {
            _root = root;
            _seatNameText = seatNameText;
            _scoreText = scoreText;
            _handText = handText;
            _riichiBadge = riichiBadge;
            _discardsText = discardsText;
            _meldsText = meldsText;
            _ankanText = ankanText;
        }

        public void Clear()
        {
            if (_seatNameText != null) _seatNameText.text = string.Empty;
            if (_scoreText != null) _scoreText.text = string.Empty;
            if (_handText != null) _handText.text = string.Empty;
            if (_riichiBadge != null) _riichiBadge.SetActive(false);
            if (_discardsText != null) _discardsText.text = string.Empty;
            if (_meldsText != null) _meldsText.text = string.Empty;
            if (_ankanText != null) _ankanText.text = string.Empty;
        }

        public void RenderSeat(PlayerSeatState seat, PlayerTableViewState view, bool isOwnSeat)
        {
            // Dynamically calculate seat wind based on confirmed dealer seat
            int relativeWind = (seat.Seat - (int)view.Board.DealerSeat + 4) % 4;
            string seatLabel = $"{TileTextHelper.FormatWind(relativeWind)}家";
            bool isCurrentTurn = view.Board.CurrentSeat == seat.Seat;
            bool isDealer = view.Board.DealerSeat == seat.Seat;
            long scorePoints = 0;
            for (int i = 0; i < view.Board.Scores.Count; i++)
            {
                if (view.Board.Scores[i].Seat == seat.Seat)
                {
                    scorePoints = view.Board.Scores[i].Score;
                    break;
                }
            }

            if (isOwnSeat && view.Own != null)
            {
                UpdateOwnSeat(seat, view.Own, seatLabel, isCurrentTurn, isDealer, scorePoints);
            }
            else
            {
                UpdateOpponentSeat(seat, seatLabel, isCurrentTurn, isDealer, scorePoints);
            }
        }

        public void UpdateOpponentSeat(
            PlayerSeatState seat,
            string seatLabel,
            bool isCurrentTurn,
            bool isDealer = false,
            long score = 25000)
        {
            if (_seatNameText != null)
            {
                string dealer = isDealer ? " [庄]" : "";
                string turn = isCurrentTurn ? " ◀" : "";
                _seatNameText.text = $"{seatLabel}{dealer}{turn}";
            }

            if (_scoreText != null)
            {
                _scoreText.text = $"{score}点";
            }

            if (_handText != null)
            {
                if (seat.HandCount.HasValue)
                {
                    uint count = seat.HandCount.Value;
                    var sb = new StringBuilder();
                    sb.Append($"手牌: {count}张 ");
                    for (int i = 0; i < count && i < 14; i++)
                    {
                        sb.Append("[牌]");
                    }
                    _handText.text = sb.ToString();
                }
                else
                {
                    _handText.text = "手牌: ?张";
                }
            }

            if (_riichiBadge != null)
            {
                _riichiBadge.SetActive(seat.Riichi);
            }

            if (_discardsText != null)
            {
                _discardsText.text = FormatDiscards(seat.Discards);
            }

            if (_meldsText != null)
            {
                _meldsText.text = FormatMelds(seat.Melds, isOwnSeat: false);
            }

            if (_ankanText != null)
            {
                int ankanCount = 0;
                for (int i = 0; i < seat.Melds.Count; i++)
                {
                    if (seat.Melds[i].IsConcealedKan)
                    {
                        ankanCount++;
                    }
                }

                if (ankanCount > 0)
                {
                    _ankanText.text = $"暗杠: {ankanCount}组";
                }
                else
                {
                    _ankanText.text = string.Empty;
                }
            }
        }

        public void UpdateOwnSeat(
            PlayerSeatState seat,
            PlayerOwnState own,
            string seatLabel,
            bool isCurrentTurn,
            bool isDealer,
            long score)
        {
            if (_seatNameText != null)
            {
                string dealer = isDealer ? " [庄]" : "";
                string turn = isCurrentTurn ? " ◀" : "";
                string furiten = own.Furiten ? " [振听]" : "";
                _seatNameText.text = $"{seatLabel} (自家){dealer}{turn}{furiten}";
            }

            if (_scoreText != null)
            {
                _scoreText.text = $"{score}点";
            }

            if (_handText != null)
            {
                var sb = new StringBuilder();
                sb.Append($"手牌: {own.ClosedTiles.Count}张");
                if (own.DrawnTileId.HasValue)
                {
                    sb.Append($" 摸牌:[{TileTextHelper.FormatTile(own.DrawnTileId.Value)}]");
                }
                _handText.text = sb.ToString();
            }

            if (_riichiBadge != null)
            {
                _riichiBadge.SetActive(seat.Riichi);
            }

            if (_discardsText != null)
            {
                _discardsText.text = FormatDiscards(seat.Discards);
            }

            if (_meldsText != null)
            {
                _meldsText.text = FormatMelds(seat.Melds, isOwnSeat: true);
            }

            if (_ankanText != null)
            {
                _ankanText.text = string.Empty;
            }
        }

        private static string FormatDiscards(IReadOnlyList<PlayerDiscardState> discards)
        {
            if (discards == null || discards.Count == 0) return "牌河: 无";
            var sb = new StringBuilder("牌河: ");
            for (int i = 0; i < discards.Count; i++)
            {
                var d = discards[i];
                string r = d.RiichiDiscard ? "[立]" : "";
                string t = d.Tsumogiri ? "'" : "";
                sb.Append($"{TileTextHelper.FormatTile(d.TileId)}{r}{t} ");
            }
            return sb.ToString().TrimEnd();
        }

        private static string FormatMelds(IReadOnlyList<PlayerMeldState> melds, bool isOwnSeat)
        {
            if (melds == null || melds.Count == 0) return "副露: 门清";
            var sb = new StringBuilder("副露: ");
            for (int i = 0; i < melds.Count; i++)
            {
                var m = melds[i];
                if (m.IsPlaceholder)
                {
                    sb.Append("[暗杠:4张] ");
                    continue;
                }
                string kind = TileTextHelper.FormatMeldKind(m.Kind);
                var tiles = new StringBuilder();
                for (int t = 0; t < m.TileIds.Count; t++)
                {
                    if (t > 0) tiles.Append(',');
                    tiles.Append(TileTextHelper.FormatTile(m.TileIds[t]));
                }
                sb.Append($"[{kind}:{tiles}] ");
            }
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>
    /// Helper for formatting Japanese Riichi Mahjong tiles and seat names to player-facing Chinese.
    /// Distinguishes aka-dora (red fives 19, 55, 91).
    /// </summary>
    public static class TileTextHelper
    {
        private static readonly string[] Man = { "1万", "2万", "3万", "4万", "5万", "6万", "7万", "8万", "9万" };
        private static readonly string[] Sou = { "1索", "2索", "3索", "4索", "5索", "6索", "7索", "8索", "9索" };
        private static readonly string[] Pin = { "1筒", "2筒", "3筒", "4筒", "5筒", "6筒", "7筒", "8筒", "9筒" };
        private static readonly string[] Honors = { "东", "南", "西", "北", "白", "发", "中" };

        public static string FormatTile(uint tileId)
        {
            if (tileId > 135) return $"?[{tileId}]";

            // Distinguish aka-dora (red fives)
            if (tileId == 19) return "赤5万";
            if (tileId == 55) return "赤5索";
            if (tileId == 91) return "赤5筒";

            int kind = (int)(tileId / 4);
            if (kind < 9) return Man[kind];
            if (kind < 18) return Sou[kind - 9];
            if (kind < 27) return Pin[kind - 18];
            if (kind < 34) return Honors[kind - 27];

            return $"?[{tileId}]";
        }

        public static string FormatTileList(IReadOnlyList<uint>? tiles)
        {
            if (tiles == null || tiles.Count == 0) return "无";
            var sb = new StringBuilder();
            for (int i = 0; i < tiles.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(FormatTile(tiles[i]));
            }
            return sb.ToString();
        }

        public static string FormatMarkedTile(uint tileId, int remainingRounds)
        {
            return $"★{FormatTile(tileId)}(余{remainingRounds}局)";
        }

        public static bool IsTileMarked(PlayerTileMarkState? markState, uint tileId)
        {
            if (markState == null || markState.Groups == null) return false;
            for (int g = 0; g < markState.Groups.Count; g++)
            {
                var group = markState.Groups[g];
                if (group.Tiles == null) continue;
                for (int t = 0; t < group.Tiles.Count; t++)
                {
                    if (group.Tiles[t].TileId == tileId) return true;
                }
            }
            return false;
        }

        public static string FormatWind(int wind)
        {
            return wind switch
            {
                0 => "东",
                1 => "南",
                2 => "西",
                3 => "北",
                _ => "东"
            };
        }

        public static string FormatSeatName(int seat)
        {
            return seat switch
            {
                0 => "东家 (起庄)",
                1 => "南家",
                2 => "西家",
                3 => "北家",
                _ => $"席位{seat}"
            };
        }

        public static string FormatMeldKind(PlayerMeldKind kind)
        {
            return kind switch
            {
                PlayerMeldKind.Chi => "吃",
                PlayerMeldKind.Pon => "碰",
                PlayerMeldKind.OpenKan => "大明杠",
                PlayerMeldKind.ClosedKan => "暗杠",
                PlayerMeldKind.AddedKan => "加杠",
                _ => "未知副露"
            };
        }

        public static string FormatActionKind(PlayerActionKind kind)
        {
            return kind switch
            {
                PlayerActionKind.DiscardTile => "打牌",
                PlayerActionKind.DeclareChi => "吃",
                PlayerActionKind.DeclarePon => "碰",
                PlayerActionKind.DeclareKan => "杠",
                PlayerActionKind.DeclareRiichi => "立直",
                PlayerActionKind.DeclareTsumo => "自摸",
                PlayerActionKind.DeclareRon => "荣和",
                PlayerActionKind.Pass => "跳过",
                PlayerActionKind.DeclareAbortiveDraw => "流局申请",
                _ => "操作"
            };
        }

        public static string FormatBeamSwapSlotKind(PlayerBeamSwapSlotKind slotKind, uint slotIndex)
        {
            return slotKind switch
            {
                PlayerBeamSwapSlotKind.Wall => $"牌山 槽位{slotIndex}",
                PlayerBeamSwapSlotKind.DeadWall => $"王牌死山 槽位{slotIndex}",
                PlayerBeamSwapSlotKind.Replacement => $"岭上牌 槽位{slotIndex}",
                _ => $"槽位{slotIndex}"
            };
        }
    }
}
