#nullable enable
using System;
using System.Collections.Generic;
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
            _clientClock = new SystemTableClientClock();
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

        // TileMark selection buffer: up to 4 tile IDs
        private readonly List<uint> _tileMarkSelectedTiles = new List<uint>();
        // BeamSwap selection buffer: hand tile ID and target candidate
        private uint? _selectedBeamHandTile;
        private PlayerBeamSwapCandidateState? _selectedBeamCandidate;
        private bool _seeThrough;

        // Animation dequeue pacing
        private float _animationBannerTimer;
        private const float AnimationDisplayDuration = 1.0f;

        public PlayerTableViewState? LatestView => _latestView;
        public TableInteractionState InteractionState => _interactionState;
        public ConfirmedAnimationQueue AnimationQueue => _animationQueue;

        private void Awake()
        {
        }

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
                _interactionState.ClearSelection();
                CloseAllCheatDialogs();
            }
            else if (_latestView != null)
            {
                _animationQueue.AcceptConfirmedDifference(_latestView, nextView, isFullBaseline: false);
            }

            _interactionState.ApplyConfirmedView(nextView);
            _latestView = nextView;

            RenderView(nextView);
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
                _sessionInfoText.text = $"对局: {view.Identity.SessionId} | 局: {view.Identity.RoundId} | 身份: {roleStr}";
            }

            if (_roundStatusText != null)
            {
                string windName = TileTextHelper.FormatWind((int)view.Board.RoundWind);
                _roundStatusText.text = $"{windName}{view.Board.RoundNumber}局 {view.Board.Honba}本场 | 供托: {view.Board.RiichiSticks}根";
            }

            if (_wallRemainingText != null)
            {
                _wallRemainingText.text = $"牌山余量: {view.Board.WallRemaining}张";
            }

            if (_doraIndicatorsText != null)
            {
                _doraIndicatorsText.text = $"宝牌指示牌: {TileTextHelper.FormatTileList(view.Board.DoraIndicators)}";
            }

            if (_lastDiscardText != null)
            {
                if (view.Board.LastDiscard != null)
                {
                    _lastDiscardText.text = $"上张舍牌: {TileTextHelper.FormatTile(view.Board.LastDiscard.TileId)}";
                }
                else
                {
                    _lastDiscardText.text = "上张舍牌: -";
                }
            }
            if (_pauseIndicator != null)
            {
                _pauseIndicator.SetActive(view.Board.Paused);
            }
        }

        private void RenderSeats(PlayerTableViewState view)
        {
            for (int i = 0; i < _seatViews.Length; i++)
            {
                if (i >= 4) break;
                var seatState = view.Seats[i];
                bool isOwnSeat = view.Identity.IsPlayer && view.Identity.Seat == i;
                _seatViews[i].RenderSeat(seatState, view, isOwnSeat);
            }
        }

        private void RenderOwnHand(PlayerTableViewState view)
        {
            if (!view.Identity.IsPrivateAuthorized || view.Own == null)
            {
                if (_ownHandPanelRoot != null) _ownHandPanelRoot.SetActive(false);
                return;
            }

            if (_ownHandPanelRoot != null) _ownHandPanelRoot.SetActive(true);

            var closedTiles = view.Own.ClosedTiles;
            uint? drawnTile = view.Own.DrawnTileId;

            // Render closed hand tiles
            for (int i = 0; i < _handTileButtons.Length; i++)
            {
                if (i < closedTiles.Count)
                {
                    uint tileId = closedTiles[i];
                    _handTileButtons[i].gameObject.SetActive(true);
                    _handTileButtons[i].interactable = true;

                    if (i < _handTileTexts.Length && _handTileTexts[i] != null)
                    {
                        _handTileTexts[i].text = TileTextHelper.FormatTile(tileId);
                    }

                    if (i < _handTileMarkBadges.Length && _handTileMarkBadges[i] != null)
                    {
                        bool isMarked = view.Own.Cheat?.TileMark != null &&
                            TileTextHelper.IsTileMarked(view.Own.Cheat.TileMark, tileId);
                        _handTileMarkBadges[i].SetActive(isMarked);
                    }
                }
                else
                {
                    _handTileButtons[i].gameObject.SetActive(false);
                }
            }

            // Render drawn tile
            if (_drawnTileButton != null)
            {
                if (drawnTile.HasValue)
                {
                    _drawnTileButton.gameObject.SetActive(true);
                    _drawnTileButton.interactable = true;
                    if (_drawnTileText != null)
                    {
                        _drawnTileText.text = TileTextHelper.FormatTile(drawnTile.Value);
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
            if (!view.Identity.IsPrivateAuthorized || view.Own?.Decision == null)
            {
                DisableAllActionButtons();
                return;
            }

            var candidates = view.Own.Decision.Candidates;
            bool hasDiscard = false;
            bool hasChi = false;
            bool hasPon = false;
            bool hasKan = false;
            bool hasRiichi = false;
            bool hasTsumo = false;
            bool hasRon = false;
            bool hasPass = false;

            for (int i = 0; i < candidates.Count; i++)
            {
                switch (candidates[i].Action)
                {
                    case PlayerActionKind.DiscardTile: hasDiscard = true; break;
                    case PlayerActionKind.DeclareChi: hasChi = true; break;
                    case PlayerActionKind.DeclarePon: hasPon = true; break;
                    case PlayerActionKind.DeclareKan: hasKan = true; break;
                    case PlayerActionKind.DeclareRiichi: hasRiichi = true; break;
                    case PlayerActionKind.DeclareTsumo: hasTsumo = true; break;
                    case PlayerActionKind.DeclareRon: hasRon = true; break;
                    case PlayerActionKind.Pass: hasPass = true; break;
                }
            }

            SetButtonState(_discardButton, hasDiscard);
            SetButtonState(_chiButton, hasChi);
            SetButtonState(_ponButton, hasPon);
            SetButtonState(_kanButton, hasKan);
            SetButtonState(_riichiButton, hasRiichi);
            SetButtonState(_tsumoButton, hasTsumo);
            SetButtonState(_ronButton, hasRon);
            SetButtonState(_passButton, hasPass);

            if (_cancelSelectionButton != null)
            {
                _cancelSelectionButton.gameObject.SetActive(_interactionState.Selection.HasSelection);
                _cancelSelectionButton.interactable = _interactionState.Selection.HasSelection;
            }
        }

        private void DisableAllActionButtons()
        {
            SetButtonState(_discardButton, false);
            SetButtonState(_chiButton, false);
            SetButtonState(_ponButton, false);
            SetButtonState(_kanButton, false);
            SetButtonState(_riichiButton, false);
            SetButtonState(_tsumoButton, false);
            SetButtonState(_ronButton, false);
            SetButtonState(_passButton, false);
            if (_cancelSelectionButton != null) _cancelSelectionButton.gameObject.SetActive(false);
        }

        private static void SetButtonState(Button? button, bool enabled)
        {
            if (button != null)
            {
                button.gameObject.SetActive(enabled);
                button.interactable = enabled;
            }
        }

        private void RenderCheats(PlayerTableViewState view)
        {
            if (!view.Identity.IsPrivateAuthorized || view.Own?.Cheat == null)
            {
                if (_cheatPanelRoot != null) _cheatPanelRoot.SetActive(false);
                return;
            }

            if (_cheatPanelRoot != null) _cheatPanelRoot.SetActive(true);

            PlayerCheatState cheat = view.Own.Cheat;
            if (_cheatPointsText != null)
            {
                _cheatPointsText.text = $"出千点数: {cheat.CheatPoints}";
            }

            // TileMark (落汗) button
            if (_tileMarkButton != null)
            {
                bool canMark = cheat.TileMark != null && cheat.TileMark.SelectableTileIds.Count >= 4 && view.Own.Decision != null;
                _tileMarkButton.interactable = canMark;
            }

            // SleeveBloom (袖里藏花) button
            if (_sleeveBloomButton != null)
            {
                bool canSleeve = view.Own.Decision != null || cheat.ImmediateSleeveBloom != null || cheat.OffTurnSleeveBloom != null;
                _sleeveBloomButton.interactable = canSleeve;
            }

            // BeamSwap (偷梁换柱) button
            if (_beamSwapButton != null)
            {
                bool canBeam = view.Own.Decision != null || cheat.BeamSwap != null;
                _beamSwapButton.interactable = canBeam;
            }

            // ShufflePreparation (码牌准备) button
            if (_preparationButton != null)
            {
                bool canPrep = cheat.Preparation != null && !cheat.Preparation.Confirmed;
                _preparationButton.interactable = canPrep;
            }
        }

        private void RenderSettlementNavigation(PlayerTableViewState view)
        {
            bool hasSettlement = !string.IsNullOrEmpty(view.Board.SettlementRoundId);
            if (_viewSettlementButton != null)
            {
                _viewSettlementButton.gameObject.SetActive(hasSettlement);
                _viewSettlementButton.interactable = hasSettlement;
            }

            bool roundEnded = view.Board.Phase == PlayerGamePhase.RoundEnd || hasSettlement;
            if (_nextRoundButton != null)
            {
                _nextRoundButton.gameObject.SetActive(roundEnded && view.Identity.IsPlayer);
                _nextRoundButton.interactable = roundEnded && view.Identity.IsPlayer;
            }
        }

        private void WireButtons()
        {
            if (_backButton != null)
            {
                _backButton.onClick.RemoveAllListeners();
                _backButton.onClick.AddListener(OnBackClicked);
            }

            if (_viewSettlementButton != null)
            {
                _viewSettlementButton.onClick.RemoveAllListeners();
                _viewSettlementButton.onClick.AddListener(OnViewSettlementClicked);
            }

            if (_nextRoundButton != null)
            {
                _nextRoundButton.onClick.RemoveAllListeners();
                _nextRoundButton.onClick.AddListener(OnNextRoundClicked);
            }

            if (_discardButton != null)
            {
                _discardButton.onClick.RemoveAllListeners();
                _discardButton.onClick.AddListener(OnDiscardButtonClicked);
            }

            if (_chiButton != null)
            {
                _chiButton.onClick.RemoveAllListeners();
                _chiButton.onClick.AddListener(() => OnActionButtonClicked(PlayerActionKind.DeclareChi));
            }

            if (_ponButton != null)
            {
                _ponButton.onClick.RemoveAllListeners();
                _ponButton.onClick.AddListener(() => OnActionButtonClicked(PlayerActionKind.DeclarePon));
            }

            if (_kanButton != null)
            {
                _kanButton.onClick.RemoveAllListeners();
                _kanButton.onClick.AddListener(() => OnActionButtonClicked(PlayerActionKind.DeclareKan));
            }

            if (_riichiButton != null)
            {
                _riichiButton.onClick.RemoveAllListeners();
                _riichiButton.onClick.AddListener(() => OnActionButtonClicked(PlayerActionKind.DeclareRiichi));
            }

            if (_tsumoButton != null)
            {
                _tsumoButton.onClick.RemoveAllListeners();
                _tsumoButton.onClick.AddListener(() => OnActionButtonClicked(PlayerActionKind.DeclareTsumo));
            }

            if (_ronButton != null)
            {
                _ronButton.onClick.RemoveAllListeners();
                _ronButton.onClick.AddListener(() => OnActionButtonClicked(PlayerActionKind.DeclareRon));
            }

            if (_passButton != null)
            {
                _passButton.onClick.RemoveAllListeners();
                _passButton.onClick.AddListener(() => OnActionButtonClicked(PlayerActionKind.Pass));
            }

            if (_cancelSelectionButton != null)
            {
                _cancelSelectionButton.onClick.RemoveAllListeners();
                _cancelSelectionButton.onClick.AddListener(OnCancelSelectionClicked);
            }

            // Hand tiles
            for (int i = 0; i < _handTileButtons.Length; i++)
            {
                int index = i;
                _handTileButtons[i].onClick.RemoveAllListeners();
                _handTileButtons[i].onClick.AddListener(() => OnHandTileClicked(index));
            }

            if (_drawnTileButton != null)
            {
                _drawnTileButton.onClick.RemoveAllListeners();
                _drawnTileButton.onClick.AddListener(OnDrawnTileClicked);
            }

            // Cheat launchers
            if (_tileMarkButton != null)
            {
                _tileMarkButton.onClick.RemoveAllListeners();
                _tileMarkButton.onClick.AddListener(OpenTileMarkDialog);
            }

            if (_sleeveBloomButton != null)
            {
                _sleeveBloomButton.onClick.RemoveAllListeners();
                _sleeveBloomButton.onClick.AddListener(OnSleeveBloomButtonClicked);
            }

            if (_beamSwapButton != null)
            {
                _beamSwapButton.onClick.RemoveAllListeners();
                _beamSwapButton.onClick.AddListener(OnBeamSwapButtonClicked);
            }

            if (_preparationButton != null)
            {
                _preparationButton.onClick.RemoveAllListeners();
                _preparationButton.onClick.AddListener(OpenPreparationDialog);
            }

            // TileMark dialog buttons
            if (_confirmTileMarkButton != null)
            {
                _confirmTileMarkButton.onClick.RemoveAllListeners();
                _confirmTileMarkButton.onClick.AddListener(OnConfirmTileMarkClicked);
            }
            if (_cancelTileMarkButton != null)
            {
                _cancelTileMarkButton.onClick.RemoveAllListeners();
                _cancelTileMarkButton.onClick.AddListener(CloseAllCheatDialogs);
            }

            // SleeveBloom dialog buttons
            if (_sleeveStoreButton != null)
            {
                _sleeveStoreButton.onClick.RemoveAllListeners();
                _sleeveStoreButton.onClick.AddListener(() => OnSleeveChoiceClicked(SleeveBloomChoice.First));
            }
            if (_sleeveSwapButton != null)
            {
                _sleeveSwapButton.onClick.RemoveAllListeners();
                _sleeveSwapButton.onClick.AddListener(() => OnSleeveChoiceClicked(SleeveBloomChoice.Second));
            }
            if (_cancelSleeveButton != null)
            {
                _cancelSleeveButton.onClick.RemoveAllListeners();
                _cancelSleeveButton.onClick.AddListener(CloseAllCheatDialogs);
            }

            // BeamSwap dialog buttons
            if (_confirmBeamSwapButton != null)
            {
                _confirmBeamSwapButton.onClick.RemoveAllListeners();
                _confirmBeamSwapButton.onClick.AddListener(OnConfirmBeamSwapClicked);
            }
            if (_keepBeamOriginalButton != null)
            {
                _keepBeamOriginalButton.onClick.RemoveAllListeners();
                _keepBeamOriginalButton.onClick.AddListener(OnKeepBeamOriginalClicked);
            }
            if (_cancelBeamButton != null)
            {
                _cancelBeamButton.onClick.RemoveAllListeners();
                _cancelBeamButton.onClick.AddListener(CloseAllCheatDialogs);
            }

            // Preparation dialog buttons
            if (_seeThroughButton != null)
            {
                _seeThroughButton.onClick.RemoveAllListeners();
                _seeThroughButton.onClick.AddListener(OnToggleSeeThroughClicked);
            }
            if (_confirmPreparationButton != null)
            {
                _confirmPreparationButton.onClick.RemoveAllListeners();
                _confirmPreparationButton.onClick.AddListener(OnConfirmPreparationClicked);
            }
            if (_cancelPreparationButton != null)
            {
                _cancelPreparationButton.onClick.RemoveAllListeners();
                _cancelPreparationButton.onClick.AddListener(CloseAllCheatDialogs);
            }
        }

        private void WireControlButtonsGap()
        {
            if (_claimControlButton != null)
            {
                _claimControlButton.interactable = false;
                _claimControlButton.onClick.RemoveAllListeners();
            }

            if (_releaseControlButton != null)
            {
                _releaseControlButton.interactable = false;
                _releaseControlButton.onClick.RemoveAllListeners();
            }

            if (_controlStatusText != null)
            {
                _controlStatusText.text = $"控制状态: 领域托管 (缺口: {TableCommandIntentFactory.ControlPayloadFieldCollision})";
            }
        }

        public void OnHandTileClicked(int index)
        {
            if (_latestView?.Own == null) return;
            var closed = _latestView.Own.ClosedTiles;
            if (index < 0 || index >= closed.Count) return;
            uint tileId = closed[index];

            // If TileMark dialog is open, add/remove from mark selection
            if (_tileMarkDialog != null && _tileMarkDialog.activeSelf)
            {
                ToggleTileMarkSelection(tileId);
                return;
            }

            // If BeamSwap dialog is open, set as selected hand tile
            if (_beamSwapDialog != null && _beamSwapDialog.activeSelf)
            {
                _selectedBeamHandTile = tileId;
                UpdateBeamSwapDialog();
                return;
            }

            // Normal play: select as discard candidate if DiscardTile is legal
            if (_latestView.Own.Decision != null)
            {
                var candidates = _latestView.Own.Decision.Candidates;
                PlayerLegalActionState? discardAction = null;
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (candidates[i].Action == PlayerActionKind.DiscardTile &&
                        candidates[i].TileIds.Count > 0 &&
                        candidates[i].TileIds[0] == tileId)
                    {
                        discardAction = candidates[i];
                        break;
                    }
                }

                if (discardAction != null)
                {
                    var outcome = _interactionState.SelectAction(discardAction);
                    if (outcome.Accepted)
                    {
                        UpdateStatusText($"已选手牌: {TileTextHelper.FormatTile(tileId)}");
                        if (_cancelSelectionButton != null)
                        {
                            _cancelSelectionButton.gameObject.SetActive(true);
                            _cancelSelectionButton.interactable = true;
                        }
                    }
                }
            }
        }

        public void OnDrawnTileClicked()
        {
            if (_latestView?.Own?.DrawnTileId == null) return;
            uint drawn = _latestView.Own.DrawnTileId.Value;

            if (_tileMarkDialog != null && _tileMarkDialog.activeSelf)
            {
                ToggleTileMarkSelection(drawn);
                return;
            }

            if (_beamSwapDialog != null && _beamSwapDialog.activeSelf)
            {
                _selectedBeamHandTile = drawn;
                UpdateBeamSwapDialog();
                return;
            }

            if (_latestView.Own.Decision != null)
            {
                var candidates = _latestView.Own.Decision.Candidates;
                PlayerLegalActionState? discardAction = null;
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (candidates[i].Action == PlayerActionKind.DiscardTile &&
                        candidates[i].TileIds.Count > 0 &&
                        candidates[i].TileIds[0] == drawn)
                    {
                        discardAction = candidates[i];
                        break;
                    }
                }

                if (discardAction != null)
                {
                    var outcome = _interactionState.SelectAction(discardAction);
                    if (outcome.Accepted)
                    {
                        UpdateStatusText($"已选摸牌: {TileTextHelper.FormatTile(drawn)}");
                        if (_cancelSelectionButton != null)
                        {
                            _cancelSelectionButton.gameObject.SetActive(true);
                            _cancelSelectionButton.interactable = true;
                        }
                    }
                }
            }
        }

        public void OnDiscardButtonClicked()
        {
            if (_latestView?.Own?.Decision == null || _commandSink == null) return;
            if (!_interactionState.Selection.HasSelection)
            {
                UpdateStatusText("请先点击手牌选择舍牌");
                return;
            }

            TableCommandOwnerContext owner = BuildOwnerContext();
            TableInteractionOutcome outcome = _interactionState.BuildIntent(TableIntentKind.SubmitAction, owner);
            HandleIntentOutcome(outcome);
        }

        public void OnActionButtonClicked(PlayerActionKind kind)
        {
            if (_latestView?.Own?.Decision == null || _commandSink == null) return;

            var candidates = _latestView.Own.Decision.Candidates;
            PlayerLegalActionState? match = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].Action == kind)
                {
                    match = candidates[i];
                    break;
                }
            }

            if (match == null)
            {
                UpdateStatusText($"无可用动作: {kind}");
                return;
            }

            var selectOutcome = _interactionState.SelectAction(match);
            if (!selectOutcome.Accepted)
            {
                UpdateStatusText($"动作选择失败: {selectOutcome.Code}");
                return;
            }

            TableCommandOwnerContext owner = BuildOwnerContext();
            TableInteractionOutcome outcome = _interactionState.BuildIntent(TableIntentKind.SubmitAction, owner);
            HandleIntentOutcome(outcome);
        }

        public void OnCancelSelectionClicked()
        {
            _interactionState.ClearSelection();
            UpdateStatusText("已取消选择");
            if (_latestView != null) RenderActionButtons(_latestView);
        }

        public void OnNextRoundClicked()
        {
            if (_latestView == null || _commandSink == null) return;
            TableCommandOwnerContext owner = BuildOwnerContext();
            var intent = _intentFactory.CreateNextRound(_latestView, owner);
            SubmitIntentAsync(intent);
        }

        public void OnBackClicked()
        {
            _navigation?.BackToMenu();
        }

        public void OnViewSettlementClicked()
        {
            _onViewSettlement?.Invoke();
        }

        // --- TileMark (落汗) Workflow ---
        public void OpenTileMarkDialog()
        {
            if (_tileMarkDialog == null || _latestView?.Own?.Cheat?.TileMark == null) return;
            CloseAllCheatDialogs();
            _tileMarkDialog.SetActive(true);
            _tileMarkSelectedTiles.Clear();
            UpdateTileMarkStatus();
        }

        private void ToggleTileMarkSelection(uint tileId)
        {
            if (_tileMarkSelectedTiles.Contains(tileId))
            {
                _tileMarkSelectedTiles.Remove(tileId);
            }
            else
            {
                if (_tileMarkSelectedTiles.Count < 4)
                {
                    _tileMarkSelectedTiles.Add(tileId);
                }
            }
            UpdateTileMarkStatus();
        }

        private void UpdateTileMarkStatus()
        {
            if (_tileMarkStatusText != null)
            {
                _tileMarkStatusText.text = $"已选 {_tileMarkSelectedTiles.Count}/4 张落汗牌: {TileTextHelper.FormatTileList(_tileMarkSelectedTiles)}";
            }
            if (_confirmTileMarkButton != null)
            {
                _confirmTileMarkButton.interactable = _tileMarkSelectedTiles.Count == 4;
            }
        }

        public void OnConfirmTileMarkClicked()
        {
            if (!UiInputLifecycle.Instance.ConsumeClickOrSubmit(_confirmTileMarkButton != null ? _confirmTileMarkButton.gameObject : null)) return;
            if (_tileMarkSelectedTiles.Count != 4 || _commandSink == null) return;

            var selOutcome = _interactionState.SelectTileMark(_tileMarkSelectedTiles);
            if (!selOutcome.Accepted)
            {
                UpdateStatusText($"落汗选择失败: {selOutcome.Code}");
                return;
            }

            TableCommandOwnerContext owner = BuildOwnerContext();
            TableInteractionOutcome outcome = _interactionState.BuildIntent(TableIntentKind.SubmitTileMark, owner);
            CloseAllCheatDialogs();
            HandleIntentOutcome(outcome);
        }

        // --- SleeveBloom (袖里藏花) Workflow ---
        private void OnSleeveBloomButtonClicked()
        {
            if (_latestView?.Own?.Cheat == null) return;
            PlayerCheatState cheat = _latestView.Own.Cheat;
            PlayerSleeveBloomState? sleeve = cheat.ImmediateSleeveBloom ?? cheat.OffTurnSleeveBloom;

            if (sleeve != null)
            {
                OpenSleeveBloomDialog();
            }
            else
            {
                // Direct activation intent
                TableCommandOwnerContext owner = BuildOwnerContext();
                TableInteractionOutcome outcome = _interactionState.BuildIntent(TableIntentKind.SubmitSleeveBloom, owner);
                HandleIntentOutcome(outcome);
            }
        }

        public void OpenSleeveBloomDialog()
        {
            if (_sleeveBloomDialog == null || _latestView?.Own?.Cheat == null) return;
            PlayerCheatState cheat = _latestView.Own.Cheat;
            PlayerSleeveBloomState? sleeve = cheat.ImmediateSleeveBloom ?? cheat.OffTurnSleeveBloom;
            if (sleeve == null) return;

            CloseAllCheatDialogs();
            _sleeveBloomDialog.SetActive(true);

            if (_sleeveTileDisplayText != null)
            {
                string first = sleeve.FirstTileId.HasValue ? TileTextHelper.FormatTile(sleeve.FirstTileId.Value) : "无";
                string second = sleeve.SecondTileId.HasValue ? TileTextHelper.FormatTile(sleeve.SecondTileId.Value) : "无";
                _sleeveTileDisplayText.text = $"首牌: {first} | 次牌: {second}";
            }

            if (_sleeveBloomStatusText != null)
            {
                _sleeveBloomStatusText.text = $"袖里藏花要约 ({sleeve.OfferId})，请选择保留牌:";
            }

            SetButtonState(_sleeveStoreButton, sleeve.FirstTileId.HasValue);
            SetButtonState(_sleeveSwapButton, sleeve.SecondTileId.HasValue);
            SetButtonState(_sleevePassButton, false);
        }

        public void OnSleeveChoiceClicked(SleeveBloomChoice choice)
        {
            if (_latestView?.Own?.Cheat == null || _commandSink == null) return;
            PlayerCheatState cheat = _latestView.Own.Cheat;
            bool offTurn = cheat.OffTurnSleeveBloom != null;

            var sel = _interactionState.SelectSleeveBloom(offTurn, choice);
            if (!sel.Accepted)
            {
                UpdateStatusText($"袖牌选择失败: {sel.Code}");
                return;
            }

            TableCommandOwnerContext owner = BuildOwnerContext();
            TableInteractionOutcome outcome = _interactionState.BuildIntent(
                offTurn ? TableIntentKind.SubmitSleeveBloomOffTurnChoice : TableIntentKind.SubmitSleeveBloomChoice,
                owner);

            CloseAllCheatDialogs();
            HandleIntentOutcome(outcome);
        }

        // --- BeamSwap (偷梁换柱) Workflow ---
        private void OnBeamSwapButtonClicked()
        {
            if (_latestView?.Own?.Cheat == null) return;
            PlayerCheatState cheat = _latestView.Own.Cheat;

            if (cheat.BeamSwap != null)
            {
                OpenBeamSwapDialog();
            }
            else
            {
                // Direct activation intent
                TableCommandOwnerContext owner = BuildOwnerContext();
                TableInteractionOutcome outcome = _interactionState.BuildIntent(TableIntentKind.SubmitBeamSwap, owner);
                HandleIntentOutcome(outcome);
            }
        }

        public void OpenBeamSwapDialog()
        {
            if (_beamSwapDialog == null || _latestView?.Own?.Cheat?.BeamSwap == null) return;
            CloseAllCheatDialogs();
            _beamSwapDialog.SetActive(true);
            _selectedBeamHandTile = null;
            _selectedBeamCandidate = null;
            UpdateBeamSwapDialog();
        }

        private void UpdateBeamSwapDialog()
        {
            if (_latestView?.Own?.Cheat?.BeamSwap == null) return;
            PlayerBeamSwapState beam = _latestView.Own.Cheat.BeamSwap;

            if (_beamSwapStatusText != null)
            {
                string handText = _selectedBeamHandTile.HasValue ? TileTextHelper.FormatTile(_selectedBeamHandTile.Value) : "未选";
                string slotText = _selectedBeamCandidate != null ? TileTextHelper.FormatBeamSwapSlotKind(_selectedBeamCandidate.SlotKind, _selectedBeamCandidate.SlotIndex) : "未选";
                _beamSwapStatusText.text = $"偷梁换柱要约中。已选手牌:[{handText}] 目标槽位:[{slotText}]";
            }

            // Populate slots buttons
            var candidates = beam.Candidates;
            for (int i = 0; i < _beamSlotButtons.Length; i++)
            {
                if (i < candidates.Count)
                {
                    var cand = candidates[i];
                    _beamSlotButtons[i].gameObject.SetActive(true);
                    _beamSlotButtons[i].interactable = true;
                    _beamSlotButtons[i].onClick.RemoveAllListeners();
                    _beamSlotButtons[i].onClick.AddListener(() =>
                    {
                        _selectedBeamCandidate = cand;
                        UpdateBeamSwapDialog();
                    });

                    if (i < _beamSlotTexts.Length && _beamSlotTexts[i] != null)
                    {
                        _beamSlotTexts[i].text = TileTextHelper.FormatBeamSwapSlotKind(cand.SlotKind, cand.SlotIndex);
                    }
                }
                else
                {
                    _beamSlotButtons[i].gameObject.SetActive(false);
                }
            }

            if (_confirmBeamSwapButton != null)
            {
                _confirmBeamSwapButton.interactable = _selectedBeamHandTile.HasValue && _selectedBeamCandidate != null;
            }

            if (_keepBeamOriginalButton != null)
            {
                _keepBeamOriginalButton.gameObject.SetActive(true);
                _keepBeamOriginalButton.interactable = true;
            }
        }

        public void OnConfirmBeamSwapClicked()
        {
            if (!UiInputLifecycle.Instance.ConsumeClickOrSubmit(_confirmBeamSwapButton != null ? _confirmBeamSwapButton.gameObject : null)) return;
            if (_latestView?.Own?.Cheat?.BeamSwap == null || _commandSink == null) return;
            if (!_selectedBeamHandTile.HasValue || _selectedBeamCandidate == null) return;

            var sel = _interactionState.SelectBeamSwap(BeamSwapChoice.Swap, _selectedBeamHandTile.Value, _selectedBeamCandidate);
            if (!sel.Accepted)
            {
                UpdateStatusText($"换牌选择失败: {sel.Code}");
                return;
            }

            TableCommandOwnerContext owner = BuildOwnerContext();
            TableInteractionOutcome outcome = _interactionState.BuildIntent(TableIntentKind.SubmitBeamSwapChoice, owner);
            CloseAllCheatDialogs();
            HandleIntentOutcome(outcome);
        }

        public void OnKeepBeamOriginalClicked()
        {
            if (!UiInputLifecycle.Instance.ConsumeClickOrSubmit(_keepBeamOriginalButton != null ? _keepBeamOriginalButton.gameObject : null)) return;
            if (_latestView?.Own?.Cheat?.BeamSwap == null || _commandSink == null) return;

            var sel = _interactionState.SelectBeamSwap(BeamSwapChoice.KeepOriginal, null, null);
            if (!sel.Accepted)
            {
                UpdateStatusText($"保留原牌选择失败: {sel.Code}");
                return;
            }

            TableCommandOwnerContext owner = BuildOwnerContext();
            TableInteractionOutcome outcome = _interactionState.BuildIntent(TableIntentKind.SubmitBeamSwapChoice, owner);
            CloseAllCheatDialogs();
            HandleIntentOutcome(outcome);
        }

        // --- Preparation (码牌准备) Workflow ---
        public void OpenPreparationDialog()
        {
            if (_preparationDialog == null || _latestView?.Own?.Cheat?.Preparation == null) return;
            CloseAllCheatDialogs();
            _preparationDialog.SetActive(true);

            PlayerPreparationState prep = _latestView.Own.Cheat.Preparation;
            if (_preparationInfoText != null)
            {
                _preparationInfoText.text = $"码牌准备 | 目标牌型: {prep.Target} | 透视消耗: {prep.SeeThroughCost}点";
            }

            _seeThrough = false;
            UpdateSeeThroughDisplay();
            if (_seeThroughButton != null)
            {
                _seeThroughButton.interactable = prep.SeeThroughEnabled;
            }
        }

        public void OnToggleSeeThroughClicked()
        {
            _seeThrough = !_seeThrough;
            UpdateSeeThroughDisplay();
        }

        private void UpdateSeeThroughDisplay()
        {
            if (_seeThroughButtonText != null)
            {
                string state = _seeThrough ? "开" : "关";
                _seeThroughButtonText.text = $"透视: [{state}] (10点)";
            }
        }

        public void OnConfirmPreparationClicked()
        {
            if (!UiInputLifecycle.Instance.ConsumeClickOrSubmit(_confirmPreparationButton != null ? _confirmPreparationButton.gameObject : null)) return;
            if (_latestView?.Own?.Cheat?.Preparation == null || _commandSink == null) return;

            bool seeThrough = _seeThrough;
            var sel = _interactionState.SelectPreparation(seeThrough);
            if (!sel.Accepted)
            {
                UpdateStatusText($"码牌准备选择失败: {sel.Code}");
                return;
            }

            TableCommandOwnerContext owner = BuildOwnerContext();
            TableInteractionOutcome outcome = _interactionState.BuildIntent(TableIntentKind.SubmitShufflePlan, owner);
            CloseAllCheatDialogs();
            HandleIntentOutcome(outcome);
        }

        public void OnCancelPreparationClicked()
        {
            CloseAllCheatDialogs();
        }

        private void CloseAllCheatDialogs()
        {
            if (_tileMarkDialog != null) _tileMarkDialog.SetActive(false);
            if (_sleeveBloomDialog != null) _sleeveBloomDialog.SetActive(false);
            if (_beamSwapDialog != null) _beamSwapDialog.SetActive(false);
            if (_preparationDialog != null) _preparationDialog.SetActive(false);
        }

        private void HandleIntentOutcome(TableInteractionOutcome outcome)
        {
            if (!outcome.Accepted || outcome.Intent == null)
            {
                UpdateStatusText($"操作未接受: {outcome.Code} ({outcome.Message})");
                return;
            }

            SubmitIntentAsync(outcome.Intent);
        }

        private async void SubmitIntentAsync(TableCommandIntent intent)
        {
            if (_commandSink == null || !_commandSink.CanSubmit)
            {
                UpdateStatusText("当前网络不可提交命令");
                return;
            }

            UpdateStatusText($"正在提交: {intent.Kind}...");

            try
            {
                var submission = await _commandSink.SubmitAsync(intent.Command, CancellationToken.None);
                if (submission.WasDuplicate)
                {
                    _interactionState.ApplyClientOutcome(TablePendingOutcome.Recorded);
                    UpdateStatusText($"命令为幂等重复提交 ({intent.Kind})");
                }
                else if (submission.Sent)
                {
                    _interactionState.ApplyClientOutcome(TablePendingOutcome.Recorded);
                    UpdateStatusText($"命令已确认入队发送 ({intent.Kind})");
                }
                else
                {
                    UpdateStatusText($"命令提交未发送 ({intent.Kind})");
                }
            }
            catch (Exception ex)
            {
                _interactionState.ApplyClientOutcome(TablePendingOutcome.Rejected);
                UpdateStatusText($"提交异常: {ex.Message}");
            }
        }

        private TableCommandOwnerContext BuildOwnerContext()
        {
            var id = _latestView?.Identity;
            string scopeIdStr = id?.ScopeId ?? "scope_default";
            ulong ownerEpoch = id?.OwnerEpoch ?? 1UL;
            ulong stateVer = id?.StateVersion ?? 1UL;

            return new TableCommandOwnerContext(
                new RequestId(Guid.NewGuid().ToString("N")),
                new RequestScopeId(scopeIdStr),
                new StateVersion(stateVer),
                new OwnerEpoch(ownerEpoch));
        }

        private void UpdateCountdown()
        {
            if (_latestView?.Own?.Decision?.Countdown != null && _turnCountdownText != null)
            {
                long remaining = _latestView.Own.Decision.Countdown.RemainingMilliseconds(_clientClock.MonotonicMilliseconds);
                float seconds = remaining / 1000f;
                _turnCountdownText.text = $"思考时间: {seconds:F1}s";
                _turnCountdownText.color = seconds < 3.0f ? Color.red : Color.white;
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
                    _animationBannerText.text = $"[动画播报] 座位{anim.Seat} {anim.Kind}{tileStr}";
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

        private void UpdateStatusText(string message)
        {
            if (_statusBannerText != null)
            {
                _statusBannerText.text = message;
            }
        }

        public void ClearAll()
        {
            _latestView = null;
            _animationQueue?.Drain();
            _interactionState?.ClearSelection();
            ResetAllViews();
        }

        private void ResetAllViews()
        {
            if (_sessionInfoText != null) _sessionInfoText.text = "等待房间同步...";
            if (_roundStatusText != null) _roundStatusText.text = "-";
            if (_wallRemainingText != null) _wallRemainingText.text = "牌山余量: -";
            if (_doraIndicatorsText != null) _doraIndicatorsText.text = "宝牌指示牌: -";
            if (_lastDiscardText != null) _lastDiscardText.text = "上张舍牌: -";
            if (_statusBannerText != null) _statusBannerText.text = "就绪";
            if (_animationBannerText != null) _animationBannerText.gameObject.SetActive(false);
            DisableAllActionButtons();
            CloseAllCheatDialogs();
        }
    }
}
